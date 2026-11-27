using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Root inference (docs/04 §3.3, docs/02 §2.4): every reference has a
///     static root — the local, parameter, <c>static</c> or <c>heap</c> that
///     bounds its life — and no reference may outlive its root. Nothing is
///     written by hand: the roots are inferred, and a function's result
///     roots become part of its interface.
/// </summary>
/// <remarks>
///     <para>
///         <b>Anchors.</b> A reference — a SIL address that is a value of the
///         language — is given a set of anchors: the stack slot it points
///         into, a parameter it came in as, or <c>heap</c> for a field of an
///         object. A field's address has its base's anchors; a reference read
///         out of a ref variable has the anchors of every reference stored
///         there; a join has its incoming ones'; a call's result has the
///         anchors of the arguments its callee's summary says it may return.
///     </para>
///     <para>
///         <b>Summaries.</b> A function returning a reference has, as its
///         summary, the parameters its result may refer into. They are worked
///         out for the whole module to a fixed point, so recursion converges,
///         and recorded on the function — <c>roots(0, 1)</c> in SIL text.
///     </para>
///     <para>
///         <b>Checks.</b> A returned reference may be rooted in parameters,
///         <c>static</c> or <c>heap</c>, never in a local (rule 3). A
///         reference may not be used once its local's scope has ended or its
///         value been moved out: a forward dataflow over each function knows
///         which slots are dead at every point (rule 2, flow-sensitively).
///         A value that holds references — a struct with a reference
///         field, an optional reference — carries the anchors of what it
///         holds, through construction, extraction and the switch on a
///         case; returning one is held to rule 3 as a reference is. Rule 4:
///         such a value stored into a region's memory may only refer into
///         that region, and stored into an object's, only into the heap —
///         so a linked structure lives in one region, and nothing in it
///         points out. Rule 5's specialisation waits on the codegen that
///         would differ.
///     </para>
/// </remarks>
public static partial class RootChecker {
    abstract record Anchor;

    sealed record LocalAnchor(AllocStackInst Slot) : Anchor;

    sealed record ParameterAnchor(int Index) : Anchor;

    sealed record HeapAnchor : Anchor {
        public static readonly HeapAnchor Instance = new();
    }

    /// <summary>A value with no storage of its own to outlive: a temporary, or a parameter passed by value.</summary>
    sealed record ValueAnchor(SilValue Value, string Description) : Anchor;

    /// <summary>True for a type whose values may hold a reference: a reference itself, or an aggregate with one inside.</summary>
    static bool HoldsReference(SilType type, HashSet<SilType>? seen = null) {
        switch (type) {
            case AddressType or InlinePointerType:
                return true;
            case StructType { IsInline: false } or TupleType or EnumType:
                seen ??= [];
                if (!seen.Add(type)) {
                    return false;
                }

                return type switch {
                    StructType structType => structType.Fields.Any(f => HoldsReference(f.Type, seen)),
                    TupleType tuple => tuple.Elements.Any(e => HoldsReference(e, seen)),
                    EnumType enumType => enumType.Cases.Any(c => c.Payload is { } payload && HoldsReference(payload, seen)),
                    _ => false,
                };
            default:
                return false;
        }
    }

    /// <param name="reported">
    ///     Told, after each function is checked, how many diagnostics there
    ///     were before it and which source it is — so that what it reported
    ///     can be given that file.
    /// </param>
    public static void Check(SilModule module, IReadOnlyDictionary<SilFunction, SourceText> sources, DiagnosticBag diagnostics, Action<int, SourceText>? reported = null) {
        // Summaries to a fixed point: each pass may let a caller see more of its callee.
        var summaries = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var round = 0; round < module.Functions.Count + 2; round++) {
            var changed = false;
            foreach (var function in module.Functions) {
                if (!HoldsReference(function.Type.Result) || function.Blocks.Count == 0) {
                    continue;
                }

                var anchors = Anchors(function, summaries);
                var summary = new HashSet<int>();
                foreach (var returned in Returned(function)) {
                    foreach (var anchor in anchors.GetValueOrDefault(returned.Value) ?? []) {
                        if (anchor is ParameterAnchor parameter) {
                            summary.Add(parameter.Index);
                        }
                    }
                }

                if (!summaries.TryGetValue(function.Name, out var old) || !old.SetEquals(summary)) {
                    summaries[function.Name] = summary;
                    changed = true;
                }
            }

            if (!changed) {
                break;
            }
        }

        foreach (var function in module.Functions) {
            if (summaries.TryGetValue(function.Name, out var summary)) {
                function.ResultRoots = [..summary.Order()];
            }

            if (function.Blocks.Count == 0 || !sources.TryGetValue(function, out var source)) {
                continue;
            }

            var before = diagnostics.Count;
            var anchors = Anchors(function, summaries);
            CheckReturns(function, anchors, source, diagnostics);
            CheckStores(function, anchors, source, diagnostics);
            CheckLiveness(function, anchors, source, diagnostics);
            CheckExclusive(function, anchors, summaries, source, diagnostics);
            reported?.Invoke(before, source);
        }
    }

    // =======================================================================
    // Anchors
    // =======================================================================

    static Dictionary<SilValue, HashSet<Anchor>> Anchors(SilFunction function, Dictionary<string, HashSet<int>> summaries) {
        var anchors = new Dictionary<SilValue, HashSet<Anchor>>();
        var stored = new Dictionary<AllocStackInst, HashSet<Anchor>>();

        bool Add(SilValue value, IEnumerable<Anchor> more) {
            if (!anchors.TryGetValue(value, out var set)) {
                anchors[value] = set = [];
            }

            var before = set.Count;
            set.UnionWith(more);
            return set.Count != before;
        }

        IEnumerable<Anchor> Of(SilValue value) => anchors.GetValueOrDefault(value) ?? [];

        // A reference parameter, and a region the caller lends, are rooted in the caller.
        for (var i = 0; i < function.Entry.Arguments.Count; i++) {
            if (function.Entry.Arguments[i].Type is AddressType or InlinePointerType || function.Entry.Arguments[i].Type == SilType.Region) {
                Add(function.Entry.Arguments[i], [new ParameterAnchor(i)]);
            }
        }

        // To a fixed point: a loop can carry a reference round to where it came from.
        var changed = true;
        while (changed) {
            changed = false;
            foreach (var block in function.Blocks) {
                foreach (var instruction in block.Instructions) {
                    switch (instruction) {
                        case AllocStackInst slot:
                            changed |= Add(slot, [new LocalAnchor(slot)]);
                            break;

                        case StructElementAddrInst element:
                            changed |= Add(element, Of(element.Address));
                            break;

                        case TupleElementAddrInst element:
                            changed |= Add(element, Of(element.Address));
                            break;

                        case RefElementAddrInst element:
                            changed |= Add(element, [HeapAnchor.Instance]);
                            break;

                        // An element where it is, in the buffer of the array at the place: rooted where the array is.
                        case BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique" } element:
                            changed |= Add(element, Of(element.Operands[0]));
                            break;

                        // A reference kept in a variable: whatever was put there.
                        case StoreInst store when HoldsReference(store.Value.Type) && store.Address is AllocStackInst slot: {
                            if (!stored.TryGetValue(slot, out var set)) {
                                stored[slot] = set = [];
                            }

                            var before = set.Count;
                            set.UnionWith(Of(store.Value));
                            changed |= set.Count != before;
                            break;
                        }

                        // From a variable, what was put there; from a region's or an
                        // object's memory, where that memory is — rule 4 keeps it so.
                        case LoadInst load when HoldsReference(load.Type):
                            changed |= load.Address is AllocStackInst from
                                ? Add(load, stored.GetValueOrDefault(from) ?? [])
                                : Add(load, Of(load.Address));
                            break;

                        case LoadBorrowInst borrowed when HoldsReference(borrowed.Type):
                            changed |= borrowed.Address is AllocStackInst borrowedFrom
                                ? Add(borrowed, stored.GetValueOrDefault(borrowedFrom) ?? [])
                                : Add(borrowed, Of(borrowed.Address));
                            break;

                        // A value holding references holds their anchors.
                        case StructInst or TupleInst or EnumInst when HoldsReference(instruction.Type):
                            changed |= Add(instruction, instruction.Operands.Where(static o => HoldsReference(o.Type)).SelectMany(Of).ToList());
                            break;

                        case StructExtractInst extract when HoldsReference(extract.Type):
                            changed |= Add(extract, Of(extract.Value));
                            break;

                        case TupleExtractInst extract when HoldsReference(extract.Type):
                            changed |= Add(extract, Of(extract.Value));
                            break;

                        case CopyValueInst copied when HoldsReference(copied.Type):
                            changed |= Add(copied, Of(copied.Value));
                            break;

                        case SwitchEnumInst switched when HoldsReference(switched.Value.Type):
                            foreach (var (_, target) in switched.Cases) {
                                if (target.Arguments is [var payload] && HoldsReference(payload.Type)) {
                                    changed |= Add(payload, Of(switched.Value));
                                }
                            }

                            break;

                        // A region read out of its variable is rooted there: it ends with that scope.
                        case LoadInst { Address: AllocStackInst regionSlot } regionLoad when regionLoad.Type == SilType.Region:
                            changed |= Add(regionLoad, [new LocalAnchor(regionSlot)]);
                            break;

                        case LoadBorrowInst { Address: AllocStackInst regionSlot } regionBorrow when regionBorrow.Type == SilType.Region:
                            changed |= Add(regionBorrow, [new LocalAnchor(regionSlot)]);
                            break;

                        case CopyValueInst { Type: var copied } copy when copied == SilType.Region:
                            changed |= Add(copy, Of(copy.Value));
                            break;

                        // What a region allocates is rooted where the region is.
                        case BuiltinInst { Name: "region_alloc" } allocation:
                            changed |= Add(allocation, Of(allocation.Operands[0]));
                            break;

                        // A view into inline bytes is rooted where the block is held:
                        // the variable it was read from, the parameter it came in as.
                        case BuiltinInst { Name: "inline_base" } based:
                            changed |= Add(based, BlockAnchors(based.Operands[0], function, Of));
                            break;

                        case BuiltinInst { Type: InlinePointerType, Name: var viewName } view when viewName.StartsWith("inline_", StringComparison.Ordinal):
                            changed |= Add(view, Of(viewName == "inline_next" ? view.Operands[1] : view.Operands[0]));
                            break;

                        case ApplyInst apply when HoldsReference(apply.Type):
                            changed |= Add(apply, CallResult(apply.Callee, [..apply.Arguments], summaries, Of));
                            break;

                        case BranchInst branch:
                            for (var i = 0; i < branch.Operands.Count; i++) {
                                if (HoldsReference(branch.Operands[i].Type)) {
                                    changed |= Add(branch.Target.Arguments[i], Of(branch.Operands[i]));
                                }
                            }

                            break;

                        case CondBranchInst conditional: {
                            var i = 0;
                            foreach (var argument in conditional.TrueArguments) {
                                if (HoldsReference(argument.Type)) {
                                    changed |= Add(conditional.WhenTrue.Arguments[i], Of(argument));
                                }

                                i++;
                            }

                            i = 0;
                            foreach (var argument in conditional.FalseArguments) {
                                if (HoldsReference(argument.Type)) {
                                    changed |= Add(conditional.WhenFalse.Arguments[i], Of(argument));
                                }

                                i++;
                            }

                            break;
                        }

                        case TryApplyInst tryApply when tryApply.Normal.Arguments is [var result] && HoldsReference(result.Type):
                            changed |= Add(result, CallResult(tryApply.Callee, [..tryApply.Arguments], summaries, Of));
                            break;
                    }
                }
            }
        }

        return anchors;
    }

    /// <summary>Where a block is held, for the views into it: its variable, or the value it is.</summary>
    static IEnumerable<Anchor> BlockAnchors(SilValue block, SilFunction function, Func<SilValue, IEnumerable<Anchor>> of) =>
        block switch {
            LoadInst { Address: AllocStackInst slot } => [new LocalAnchor(slot)],
            LoadBorrowInst { Address: AllocStackInst slot } => [new LocalAnchor(slot)],
            LoadInst load => of(load.Address),
            LoadBorrowInst borrowed => of(borrowed.Address),
            CopyValueInst copy => BlockAnchors(copy.Value, function, of),
            SilArgument argument when argument.Block == function.Entry => [
                new ValueAnchor(
                    argument,
                    $"parameter '{(function.Entry.Arguments.IndexOf(argument) is var i && i < function.ParameterNames.Count ? function.ParameterNames[i] : "?")}', which is passed by value"
                ),
            ],
            _ => [new ValueAnchor(block, "a temporary value")],
        };

    /// <summary>
    ///     What a call's reference result may be rooted in: the anchors of the
    ///     arguments its callee's summary names; for a callee not known here,
    ///     every reference passed to it.
    /// </summary>
    static IEnumerable<Anchor> CallResult(SilValue callee, List<SilValue> arguments, Dictionary<string, HashSet<int>> summaries, Func<SilValue, IEnumerable<Anchor>> of) {
        if (callee is FunctionRefInst reference && summaries.TryGetValue(reference.Function, out var summary)) {
            return summary.Where(i => i < arguments.Count).SelectMany(i => of(arguments[i]));
        }

        return arguments.Where(static a => HoldsReference(a.Type)).SelectMany(of);
    }

    /// <summary>
    ///     Each returned reference, with where it was returned from: the
    ///     <c>return</c>, or — since every return goes through one block —
    ///     each branch that hands the exit its value.
    /// </summary>
    static IEnumerable<(SilValue Value, SilInstruction At)> Returned(SilFunction function) {
        foreach (var block in function.Blocks) {
            if (block.Terminator is not ReturnInst { Value: { } value } returnInst) {
                continue;
            }

            if (value is SilArgument argument && argument.Block == block) {
                var index = block.Arguments.IndexOf(argument);
                foreach (var predecessor in function.Blocks) {
                    if (predecessor.Terminator is BranchInst branch && branch.Target == block) {
                        yield return (branch.Operands[index], branch);
                    }
                }

                continue;
            }

            yield return (value, returnInst);
        }
    }

    // =======================================================================
    // Checks
    // =======================================================================

    static TextSpan SpanOf(SilInstruction at, SourceText source) =>
        at.Location is { } location ? new TextSpan(source.GetPosition(new LinePosition(location.Line, location.Column)), 0) : default;

    static string Describe(AllocStackInst slot, SilFunction function) =>
        slot.VariableName is { } name
            ? slot.ElementType == SilType.Region ? $"region '{name}'"
            : function.ParameterNames.Contains(name) ? $"parameter '{name}', which is passed by value"
            : $"local '{name}'"
            : "a temporary value";

    /// <summary>Rule 3: a returned reference is rooted in parameters, static or heap — not in a local, which dies as the function returns.</summary>
    static void CheckReturns(SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors, SourceText source, DiagnosticBag diagnostics) {
        foreach (var (value, at) in Returned(function)) {
            var anchor = (anchors.GetValueOrDefault(value) ?? []).FirstOrDefault(static a => a is LocalAnchor or ValueAnchor);
            if (anchor is null) {
                continue;
            }

            var root = anchor is LocalAnchor local ? Describe(local.Slot, function) : ((ValueAnchor)anchor).Description;
            var fix = root.StartsWith("parameter", StringComparison.Ordinal)
                ? "take the parameter as 'ref', so the reference is rooted in the caller's value; or return an owned copy"
                : root.StartsWith("region", StringComparison.Ordinal)
                    ? "return a copy of the value, out of the region (r.toOwned()); or allocate it in a region the caller passes (fn f(in r: Region) -> T&)"
                    : "return an owned value instead (copy what it refers to); allocate in the caller's region (fn f(in r: Region) -> T&); or return the whole value";
            diagnostics.Error(
                ErrorCode.ERR_ReferenceOutlivesRoot,
                SpanOf(at, source),
                $"cannot return a reference rooted in {root}: it dies when the function returns. To fix it: {fix}"
            );
        }
    }

    /// <summary>
    ///     Rule 4: references stored into a region's memory refer only into
    ///     that region, which they then live as long as; into an object's,
    ///     only into the heap. A local, a parameter, another region would
    ///     each be gone while the stored reference still could be read.
    /// </summary>
    static void CheckStores(SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors, SourceText source, DiagnosticBag diagnostics) {
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                if (instruction is not StoreInst store || store.Address is AllocStackInst || !HoldsReference(store.Value.Type)) {
                    continue;
                }

                var destination = anchors.GetValueOrDefault(store.Address) ?? [];
                var heap = destination.Contains(HeapAnchor.Instance);
                foreach (var anchor in anchors.GetValueOrDefault(store.Value) ?? []) {
                    if (destination.Contains(anchor) && !(heap && anchor is not HeapAnchor)) {
                        continue;
                    }

                    var into = heap
                        ? "an object's field, which may outlive it"
                        : destination.OfType<LocalAnchor>().FirstOrDefault() is { } region
                            ? Describe(region.Slot, function)
                            : destination.OfType<ParameterAnchor>().FirstOrDefault() is { } lent
                                ? $"region '{(lent.Index < function.ParameterNames.Count ? function.ParameterNames[lent.Index] : "?")}', passed in"
                                : "storage that outlives it";
                    var root = anchor switch {
                        LocalAnchor local => Describe(local.Slot, function),
                        ParameterAnchor parameter => $"parameter '{(parameter.Index < function.ParameterNames.Count ? function.ParameterNames[parameter.Index] : "?")}'",
                        _ => "the heap",
                    };
                    diagnostics.Error(
                        ErrorCode.ERR_ReferenceOutlivesRoot,
                        SpanOf(store, source),
                        $"cannot store a reference rooted in {root} into {into}: what is stored there may refer only into the same region. "
                        + "To fix it: allocate what it refers to in that region too (r.alloc(…)); or store a copy of the value instead of a reference to it"
                    );
                    break;
                }
            }
        }
    }

    /// <summary>
    ///     Rule 2, flow-sensitively: a reference is not used where a local it
    ///     may be rooted in is dead — its scope ended, or its value moved out —
    ///     until something is stored there again.
    /// </summary>
    static void CheckLiveness(SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors, SourceText source, DiagnosticBag diagnostics) {
        // Only references that came from elsewhere are checked: the lowering's
        // own reads of a local are inside its scope by construction.
        bool Indirect(SilValue value) =>
            value switch {
                LoadInst or ApplyInst => true,
                StructExtractInst extract => Indirect(extract.Value),
                TupleExtractInst extract => Indirect(extract.Value),
                SilArgument argument => argument.Block != function.Entry,
                StructElementAddrInst element => Indirect(element.Address),
                TupleElementAddrInst element => Indirect(element.Address),
                EnumPayloadAddrInst element => Indirect(element.Address),
                _ => false,
            };

        var blocks = SilVerifier.ReversePostorder(function);
        var predecessors = blocks.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in blocks) {
            foreach (var successor in block.Successors) {
                if (predecessors.TryGetValue(successor, out var list)) {
                    list.Add(block);
                }
            }
        }

        // A region reset leaves its slot alive but what it allocated gone. A
        // value made since — by the region, or derived from one — is fresh;
        // a variable is stale from a reset until something fresh is stored in
        // it. A reference rooted in a region reset on some path to its use,
        // and not fresh, is dead.
        var holderAnchors = new Dictionary<AllocStackInst, HashSet<Anchor>>();
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                if (instruction is StoreInst { Address: AllocStackInst holder } stored && HoldsReference(stored.Value.Type)) {
                    if (!holderAnchors.TryGetValue(holder, out var set)) {
                        holderAnchors[holder] = set = [];
                    }

                    set.UnionWith(RegionsOf(stored.Value));
                }
            }
        }

        // What can be invalidated under a reference: a region's slot, a block's variable or parameter.
        IEnumerable<Anchor> RegionsOf(SilValue value) =>
            (anchors.GetValueOrDefault(value) ?? []).Where(static a => a is LocalAnchor or ParameterAnchor);

        // A region reset, or an inline block changed — copied on write, or
        // grown and moved — leaves what was taken from it before stale.
        static bool Invalidates(SilInstruction instruction) =>
            instruction is BuiltinInst { Name: "region_reset" or "inline_unique" or "inline_append" or "inline_assign" or "inline_assign_string" };

        // For each slot that is dead, where it died; the regions reset, where;
        // the variables that may hold what a reset took; the values made since.
        var exit = new Dictionary<SilBlock, Dictionary<AllocStackInst, SilInstruction>>();
        var exitReset = new Dictionary<SilBlock, Dictionary<Anchor, SilInstruction>>();
        var exitStale = new Dictionary<SilBlock, HashSet<AllocStackInst>>();
        var exitFresh = new Dictionary<SilBlock, HashSet<SilValue>>();
        var reported = new HashSet<SilValue>();
        var reportedAt = new HashSet<(SilInstruction, int)>();
        var reportedUses = new HashSet<(AllocStackInst, SilLocation?)>();
        for (var round = 0; round < 2 * blocks.Count + 2; round++) {
            var changed = false;
            foreach (var block in blocks) {
                var dead = new Dictionary<AllocStackInst, SilInstruction>();
                var resets = new Dictionary<Anchor, SilInstruction>();
                var stale = new HashSet<AllocStackInst>();
                HashSet<SilValue>? fresh = null;
                foreach (var predecessor in predecessors[block]) {
                    foreach (var (slot, at) in exit.GetValueOrDefault(predecessor) ?? []) {
                        dead.TryAdd(slot, at);
                    }

                    foreach (var (slot, at) in exitReset.GetValueOrDefault(predecessor) ?? []) {
                        resets.TryAdd(slot, at);
                    }

                    stale.UnionWith(exitStale.GetValueOrDefault(predecessor) ?? []);
                    if (exitFresh.TryGetValue(predecessor, out var incoming)) {
                        if (fresh is null) {
                            fresh = [..incoming];
                        } else {
                            fresh.IntersectWith(incoming);
                        }
                    }
                }

                fresh ??= [];

                // A block argument is fresh when every edge into it passes a fresh value.
                SilValue? PassedOn(SilBlock from, int index) =>
                    from.Terminator switch {
                        BranchInst branch when branch.Target == block => branch.Operands[index],
                        CondBranchInst conditional when conditional.WhenTrue == block => conditional.TrueArguments.ElementAt(index),
                        CondBranchInst conditional when conditional.WhenFalse == block => conditional.FalseArguments.ElementAt(index),
                        SwitchEnumInst switched => switched.Value,
                        _ => null,
                    };

                for (var a = 0; a < block.Arguments.Count; a++) {
                    var index = a;
                    if (predecessors[block].Count > 0 && predecessors[block].All(p =>
                            PassedOn(p, index) is { } passedOn && exitFresh.TryGetValue(p, out var passed) && passed.Contains(passedOn))) {
                        fresh.Add(block.Arguments[a]);
                    }
                }

                foreach (var instruction in block.Instructions) {
                    // What the instruction uses is checked before what it changes.
                    if (round > 0) {
                        foreach (var operand in instruction.Operands) {
                            if (operand.Type is not (AddressType or InlinePointerType) || fresh.Contains(operand)
                                || operand.Type is AddressType && !Indirect(operand)
                                || instruction is StoreInst store && ReferenceEquals(store.Value, operand)
                                || instruction is BranchInst or CondBranchInst or ReturnInst) {
                                continue;
                            }

                            foreach (var changedAnchor in RegionsOf(operand)) {
                                if (resets.TryGetValue(changedAnchor, out var reset)
                                    && !(changedAnchor is LocalAnchor { Slot: var changedSlot } && dead.ContainsKey(changedSlot))
                                    && reported.Add(operand)
                                    && reportedAt.Add((reset, instruction.Location?.Line ?? -1))) {
                                    var when = reset.Location is { } resetAt ? $"line {resetAt.Line}" : "earlier";
                                    var what = changedAnchor is LocalAnchor { Slot: var named }
                                        ? Describe(named, function)
                                        : $"parameter '{(((ParameterAnchor)changedAnchor).Index is var p && p < function.ParameterNames.Count ? function.ParameterNames[p] : "?")}'";
                                    diagnostics.Error(
                                        ErrorCode.ERR_ReferenceOutlivesRoot,
                                        SpanOf(instruction, source),
                                        reset is BuiltinInst { Name: "region_reset" }
                                            ? $"this reference refers to {what}, which is gone — the region was reset at {when}; take the reference again after the reset"
                                            : $"this reference into {what} is stale — the value was changed at {when}, which may have moved its bytes; take the reference again after the change"
                                    );
                                }
                            }
                        }
                    }

                    switch (instruction) {
                        case DestroyAddrInst { Address: AllocStackInst slot }:
                            dead[slot] = instruction;
                            continue;

                        case LoadInst { Kind: LoadKind.Take, Address: AllocStackInst slot }:
                            dead[slot] = instruction;
                            continue;

                        case StoreInst { Address: AllocStackInst slot } stored:
                            dead.Remove(slot);
                            if (fresh.Contains(stored.Value) || !RegionsOf(stored.Value).Any(resets.ContainsKey)) {
                                stale.Remove(slot);
                            } else {
                                stale.Add(slot);
                            }

                            break;

                        // 'arena.reset()', or a block changed: what was taken from it is
                        // gone; it is not. A view the change itself gives is fresh.
                        case BuiltinInst when Invalidates(instruction): {
                            var changedAnchors = instruction is BuiltinInst { Name: "region_reset" } || instruction.Operands[0] is not AllocStackInst
                                ? RegionsOf(instruction.Operands[0]).ToList()
                                : [new LocalAnchor((AllocStackInst)instruction.Operands[0])];
                            foreach (var changedAnchor in changedAnchors) {
                                resets[changedAnchor] = instruction;
                                fresh.RemoveWhere(v => RegionsOf(v).Contains(changedAnchor));
                                foreach (var (holder, held) in holderAnchors) {
                                    if (held.Contains(changedAnchor)) {
                                        stale.Add(holder);
                                    }
                                }
                            }

                            if (HoldsReference(instruction.Type)) {
                                fresh.Add(instruction);
                            }

                            break;
                        }

                        // Made by the region, or taken from a block: fresh, whatever changed before.
                        case BuiltinInst { Name: "region_alloc" or "inline_base" } or ApplyInst when HoldsReference(instruction.Type):
                            fresh.Add(instruction);
                            break;

                        case LoadInst { Address: AllocStackInst holder } load when HoldsReference(load.Type) && !stale.Contains(holder):
                            fresh.Add(load);
                            break;

                        case StructElementAddrInst or TupleElementAddrInst or EnumPayloadAddrInst or StructExtractInst or TupleExtractInst or CopyValueInst or LoadBorrowInst
                            when instruction.Operands.Count > 0 && fresh.Contains(instruction.Operands[0]):
                            fresh.Add(instruction);
                            break;

                        case BuiltinInst { Type: InlinePointerType, Name: var viewName } view when viewName.StartsWith("inline_", StringComparison.Ordinal)
                            && fresh.Contains(viewName == "inline_next" ? view.Operands[1] : view.Operands[0]):
                            fresh.Add(instruction);
                            break;

                        case StructInst or TupleInst or EnumInst when HoldsReference(instruction.Type)
                            && instruction.Operands.Where(static o => HoldsReference(o.Type)).All(fresh.Contains):
                            fresh.Add(instruction);
                            break;
                    }

                    if (round == 0) {
                        continue;
                    }

                    foreach (var operand in instruction.Operands) {
                        if (operand.Type is not (AddressType or InlinePointerType) || !Indirect(operand) || instruction is StoreInst store && ReferenceEquals(store.Value, operand)
                            || instruction is BranchInst or CondBranchInst or ReturnInst) {
                            continue;
                        }

                        foreach (var anchor in anchors.GetValueOrDefault(operand) ?? []) {
                            // 'r.v' reads through 'r' and then through the field's
                            // address: one use, said once.
                            if (anchor is LocalAnchor { Slot: var slot } && dead.TryGetValue(slot, out var ended) && reported.Add(operand)
                                && reportedUses.Add((slot, instruction.Location))) {
                                var where = ended.Location is { } location ? $"line {location.Line}" : "earlier";
                                diagnostics.Error(
                                    ErrorCode.ERR_ReferenceOutlivesRoot,
                                    SpanOf(instruction, source),
                                    $"this reference refers to {Describe(slot, function)}, which is gone — "
                                    + (ended is LoadInst ? "its value was moved out" : slot.ElementType == SilType.Region ? "the region ended" : "its scope ended")
                                    + $" at {where}"
                                );
                            }
                        }
                    }
                }

                var previous = exit.GetValueOrDefault(block);
                if (previous is null || previous.Count != dead.Count || !previous.Keys.All(dead.ContainsKey)) {
                    exit[block] = dead;
                    changed = true;
                }

                var previousFresh = exitFresh.GetValueOrDefault(block);
                if (previousFresh is null || !previousFresh.SetEquals(fresh)) {
                    exitFresh[block] = fresh;
                    changed = true;
                }

                var previousResets = exitReset.GetValueOrDefault(block);
                if (previousResets is null || previousResets.Count != resets.Count) {
                    exitReset[block] = resets;
                    changed = true;
                }

                var previousStale = exitStale.GetValueOrDefault(block);
                if (previousStale is null || !previousStale.SetEquals(stale)) {
                    exitStale[block] = stale;
                    changed = true;
                }
            }

            if (!changed && round > 0) {
                break;
            }
        }
    }

}
