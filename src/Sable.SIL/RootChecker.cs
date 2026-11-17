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
///         References cannot be stored in fields yet, so rule 4's regions,
///         and rule 5's specialisation, wait for region values (§3.5).
///     </para>
/// </remarks>
public static class RootChecker {
    abstract record Anchor;

    sealed record LocalAnchor(AllocStackInst Slot) : Anchor;

    sealed record ParameterAnchor(int Index) : Anchor;

    sealed record HeapAnchor : Anchor {
        public static readonly HeapAnchor Instance = new();
    }

    public static void Check(SilModule module, IReadOnlyDictionary<SilFunction, SourceText> sources, DiagnosticBag diagnostics) {
        // Summaries to a fixed point: each pass may let a caller see more of its callee.
        var summaries = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var round = 0; round < module.Functions.Count + 2; round++) {
            var changed = false;
            foreach (var function in module.Functions) {
                if (function.Type.Result is not AddressType || function.Blocks.Count == 0) {
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

            var anchors = Anchors(function, summaries);
            CheckReturns(function, anchors, source, diagnostics);
            CheckLiveness(function, anchors, source, diagnostics);
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
            if (function.Entry.Arguments[i].Type is AddressType || function.Entry.Arguments[i].Type == SilType.Region) {
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

                        // A reference kept in a variable: whatever was put there.
                        case StoreInst { Value.Type: AddressType } store when store.Address is AllocStackInst slot: {
                            if (!stored.TryGetValue(slot, out var set)) {
                                stored[slot] = set = [];
                            }

                            var before = set.Count;
                            set.UnionWith(Of(store.Value));
                            changed |= set.Count != before;
                            break;
                        }

                        case LoadInst { Type: AddressType } load:
                            changed |= load.Address is AllocStackInst from
                                ? Add(load, stored.GetValueOrDefault(from) ?? [])
                                : Add(load, Of(load.Address));
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

                        case ApplyInst { Type: AddressType } apply:
                            changed |= Add(apply, CallResult(apply.Callee, [..apply.Arguments], summaries, Of));
                            break;

                        case BranchInst branch:
                            for (var i = 0; i < branch.Operands.Count; i++) {
                                if (branch.Operands[i].Type is AddressType) {
                                    changed |= Add(branch.Target.Arguments[i], Of(branch.Operands[i]));
                                }
                            }

                            break;

                        case CondBranchInst conditional: {
                            var i = 0;
                            foreach (var argument in conditional.TrueArguments) {
                                if (argument.Type is AddressType) {
                                    changed |= Add(conditional.WhenTrue.Arguments[i], Of(argument));
                                }

                                i++;
                            }

                            i = 0;
                            foreach (var argument in conditional.FalseArguments) {
                                if (argument.Type is AddressType) {
                                    changed |= Add(conditional.WhenFalse.Arguments[i], Of(argument));
                                }

                                i++;
                            }

                            break;
                        }

                        case TryApplyInst tryApply when tryApply.Normal.Arguments is [{ Type: AddressType } result]:
                            changed |= Add(result, CallResult(tryApply.Callee, [..tryApply.Arguments], summaries, Of));
                            break;
                    }
                }
            }
        }

        return anchors;
    }

    /// <summary>
    ///     What a call's reference result may be rooted in: the anchors of the
    ///     arguments its callee's summary names; for a callee not known here,
    ///     every reference passed to it.
    /// </summary>
    static IEnumerable<Anchor> CallResult(SilValue callee, List<SilValue> arguments, Dictionary<string, HashSet<int>> summaries, Func<SilValue, IEnumerable<Anchor>> of) {
        if (callee is FunctionRefInst reference && summaries.TryGetValue(reference.Function, out var summary)) {
            return summary.Where(i => i < arguments.Count).SelectMany(i => of(arguments[i]));
        }

        return arguments.Where(static a => a.Type is AddressType).SelectMany(of);
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
            var local = (anchors.GetValueOrDefault(value) ?? []).OfType<LocalAnchor>().FirstOrDefault();
            if (local is null) {
                continue;
            }

            var root = Describe(local.Slot, function);
            var fix = root.StartsWith("parameter", StringComparison.Ordinal)
                ? "take the parameter as 'ref', so the reference is rooted in the caller's value; or return an owned copy"
                : root.StartsWith("region", StringComparison.Ordinal)
                    ? "return a copy of the value, out of the region; or allocate it in a region the caller passes (fn f(in r: Region) -> T&)"
                    : "return an owned value instead (copy what it refers to); allocate in the caller's region (fn f(in r: Region) -> T&); or return the whole value";
            diagnostics.Error(
                ErrorCode.ERR_ReferenceOutlivesRoot,
                SpanOf(at, source),
                $"cannot return a reference rooted in {root}: it dies when the function returns. To fix it: {fix}"
            );
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
                SilArgument argument => argument.Block != function.Entry,
                StructElementAddrInst element => Indirect(element.Address),
                TupleElementAddrInst element => Indirect(element.Address),
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

        // For each slot that is dead, where it died.
        var exit = new Dictionary<SilBlock, Dictionary<AllocStackInst, SilInstruction>>();
        var reported = new HashSet<SilValue>();
        for (var round = 0; round < 2 * blocks.Count + 2; round++) {
            var changed = false;
            foreach (var block in blocks) {
                var dead = new Dictionary<AllocStackInst, SilInstruction>();
                foreach (var predecessor in predecessors[block]) {
                    foreach (var (slot, at) in exit.GetValueOrDefault(predecessor) ?? []) {
                        dead.TryAdd(slot, at);
                    }
                }

                foreach (var instruction in block.Instructions) {
                    switch (instruction) {
                        case DestroyAddrInst { Address: AllocStackInst slot }:
                            dead[slot] = instruction;
                            continue;

                        case LoadInst { Kind: LoadKind.Take, Address: AllocStackInst slot }:
                            dead[slot] = instruction;
                            continue;

                        case StoreInst { Address: AllocStackInst slot }:
                            dead.Remove(slot);
                            break;
                    }

                    if (round == 0) {
                        continue;
                    }

                    foreach (var operand in instruction.Operands) {
                        if (operand.Type is not AddressType || !Indirect(operand) || instruction is StoreInst store && ReferenceEquals(store.Value, operand)
                            || instruction is BranchInst or CondBranchInst or ReturnInst) {
                            continue;
                        }

                        foreach (var anchor in anchors.GetValueOrDefault(operand) ?? []) {
                            if (anchor is LocalAnchor { Slot: var slot } && dead.TryGetValue(slot, out var ended) && reported.Add(operand)) {
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
            }

            if (!changed && round > 0) {
                break;
            }
        }
    }
}
