using System.Collections.Immutable;
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

    /// <summary>
    ///     In a closure's body, what it captured: reached through its context,
    ///     a variable it shares in a box among them. Rooted where the closure
    ///     was made, which is checked there: a body that keeps any of it in
    ///     memory that outlives the call keeps its captures
    ///     (<see cref="KeepsCaptures" />), and the closure may then capture
    ///     nothing that dies before the heap does.
    /// </summary>
    sealed record CapturesAnchor : Anchor {
        public static readonly CapturesAnchor Instance = new();
    }

    /// <summary>In <see cref="Escaping" />'s sets, a closure's body that keeps what it captured past its call.</summary>
    const int KeepsCaptures = -1;

    /// <summary>A closure's body: given its context, an object of no type of its own (<c>$Object</c>), as an argument.</summary>
    static bool IsClosureBody(SilFunction function) =>
        function.Blocks.Count > 0 && function.Entry.Arguments.Any(static a => a.Type == SilType.Object);

    /// <summary>
    ///     In a closure's body, an address of what it captured: a field of its
    ///     context, or the value of a box one holds.
    /// </summary>
    static bool IsCaptured(RefElementAddrInst element, SilFunction function) =>
        element.Reference switch {
            UncheckedRefCastInst { Value: SilArgument context } => context.Block == function.Entry && context.Type == SilType.Object,
            LoadInst { Type: ClassType { Boxed: not null }, Address: RefElementAddrInst field } => IsCaptured(field, function),
            LoadBorrowInst { Type: ClassType { Boxed: not null }, Address: RefElementAddrInst field } => IsCaptured(field, function),
            CopyValueInst { Type: ClassType { Boxed: not null }, Value: LoadInst or LoadBorrowInst } copied => copied.Value switch {
                LoadInst { Address: RefElementAddrInst field } => IsCaptured(field, function),
                LoadBorrowInst { Address: RefElementAddrInst field } => IsCaptured(field, function),
                _ => false,
            },
            _ => false,
        };

    /// <summary>
    ///     True for a type whose values may hold a reference: a reference
    ///     itself, an aggregate with one inside, or a closure, whose context
    ///     may have captured one.
    /// </summary>
    /// <param name="bare">Only a reference itself, in an aggregate or not: what a function's result roots are recorded for.</param>
    /// <param name="closures">Whether a closure counts: not for what a closure called may hand back of its arguments.</param>
    static bool HoldsReference(SilType type, HashSet<SilType>? seen = null, bool bare = false, bool closures = true) {
        switch (type) {
            case AddressType or InlinePointerType:
                return true;
            case ThickFunctionType:
                return !bare && closures;
            case ArrayType or SliceType or FixedArrayType:
                return !bare && HoldsReference(ArrayType.ElementOf(type)!, seen, bare, closures);
            case StructType { IsInline: false } or TupleType or EnumType:
                seen ??= [];
                if (!seen.Add(type)) {
                    return false;
                }

                return type switch {
                    StructType structType => structType.Fields.Any(f => HoldsReference(f.Type, seen, bare, closures)),
                    TupleType tuple => tuple.Elements.Any(e => HoldsReference(e, seen, bare, closures)),
                    EnumType enumType => enumType.Cases.Any(c => c.Payload is { } payload && HoldsReference(payload, seen, bare, closures)),
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

        // What generic code compiled once may give back rooted in a region or
        // local of its own — passed on by a generic caller that gives it its
        // own metadata, to a fixed point.
        var localResults = new Dictionary<string, string>(StringComparer.Ordinal);
        var unspecialised = module.Functions.Where(static f => f.IsUnspecialised && f.Blocks.Count > 0).ToList();
        for (var round = 0; round < unspecialised.Count + 2; round++) {
            var changed = false;
            foreach (var function in unspecialised) {
                if (!localResults.ContainsKey(function.Name) && LocalGenericResult(function, Anchors(function, summaries, localResults)) is { } root) {
                    localResults[function.Name] = root;
                    changed = true;
                }
            }

            if (!changed) {
                break;
            }
        }

        // The closure parameters each function keeps past its call, to a fixed point.
        var escaping = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var takingClosures = module.Functions.Where(static f => f.Blocks.Count > 0 && (f.Entry.Arguments.Any(static a => IsClosureArgument(a.Type)) || IsClosureBody(f))).ToList();
        for (var round = 0; round < takingClosures.Count + 2; round++) {
            var changed = false;
            foreach (var function in takingClosures) {
                var kept = Escaping(function, Anchors(function, summaries, localResults), escaping);
                if (kept.Count > 0 && (!escaping.TryGetValue(function.Name, out var old) || !old.SetEquals(kept))) {
                    escaping[function.Name] = kept;
                    changed = true;
                }
            }

            if (!changed) {
                break;
            }
        }

        foreach (var function in module.Functions) {
            if (summaries.TryGetValue(function.Name, out var summary) && HoldsReference(function.Type.Result, bare: true)) {
                function.ResultRoots = [..summary.Order()];
            }

            if (function.Blocks.Count == 0 || !sources.TryGetValue(function, out var source)) {
                continue;
            }

            var before = diagnostics.Count;
            var contents = new Dictionary<AllocStackInst, HashSet<Anchor>>();
            var anchors = Anchors(function, summaries, localResults, contents);
            CheckReturns(function, anchors, source, diagnostics);
            CheckGenericResults(function, localResults, source, diagnostics);
            CheckEscapingClosures(function, anchors, contents, escaping, source, diagnostics);
            CheckStores(function, anchors, source, diagnostics);
            // A closure that captured a region, not what it held, is not stale after a reset.
            var resetAnchors = function.Blocks.Any(static b => b.Instructions.Any(static i => i is BuiltinInst { Name: "region_reset" }))
                ? Anchors(function, summaries, localResults, regionCaptures: false)
                : anchors;
            CheckLiveness(function, anchors, resetAnchors, source, diagnostics);
            CheckExclusive(function, anchors, summaries, source, diagnostics);
            reported?.Invoke(before, source);
        }
    }

    // =======================================================================
    // Anchors
    // =======================================================================

    static Dictionary<SilValue, HashSet<Anchor>> Anchors(SilFunction function, Dictionary<string, HashSet<int>> summaries,
        Dictionary<string, string>? localResults = null, Dictionary<AllocStackInst, HashSet<Anchor>>? contents = null, bool regionCaptures = true) {
        var anchors = new Dictionary<SilValue, HashSet<Anchor>>();
        var stored = contents ?? new Dictionary<AllocStackInst, HashSet<Anchor>>();

        bool Add(SilValue value, IEnumerable<Anchor> more) {
            if (!anchors.TryGetValue(value, out var set)) {
                anchors[value] = set = [];
            }

            var before = set.Count;
            set.UnionWith(more);
            return set.Count != before;
        }

        IEnumerable<Anchor> Of(SilValue value) => anchors.GetValueOrDefault(value) ?? [];

        // What is at an address: what was put in its variable; for memory not a
        // variable's, where it is (rule 4), or for a parameter's, the caller's.
        IEnumerable<Anchor> Contents(SilValue address) =>
            RootSlot(address) is { } slot ? stored.GetValueOrDefault(slot) ?? [] : Of(address);

        var contexts = Contexts(function);

        bool Hold(List<Anchor> held, IEnumerable<AllocStackInst> slots) {
            var grew = false;
            foreach (var slot in slots) {
                if (!stored.TryGetValue(slot, out var set)) {
                    stored[slot] = set = [];
                }

                var before = set.Count;
                set.UnionWith(held);
                grew |= set.Count != before;
            }

            return grew;
        }

        // A variable lent to a call ('inout', '&') may be given any reference the call is given.
        bool Lent(IEnumerable<SilValue> arguments) {
            List<SilValue> list = [..arguments];
            var parts = list.Where(static a => a.Type is AddressType { Pointee: var p } && HoldsReference(p) && RootSlot(a) is not null).ToList();
            var slots = parts.Select(static a => RootSlot(a)!).Concat(list.OfType<AllocStackInst>().Where(static s => HoldsReference(s.ElementType))).Distinct().ToList();
            return slots.Count > 0 && Hold(
                [
                    ..list.Where(a => a is not AllocStackInst && !parts.Contains(a) && (HoldsReference(a.Type) || a.Type == SilType.Region)).SelectMany(Of),
                    ..slots.SelectMany(s => stored.GetValueOrDefault(s) ?? []),
                ],
                slots
            );
        }

        // What a call gives back through an address, in the generic convention.
        bool GenericResults(SilInstruction call, SilValue callee, List<SilValue> arguments) {
            var grew = false;

            // Compiled once, a closure gives a generic result through an
            // address: it may be a reference into a region it is given.
            if (callee is not FunctionRefInst && IndirectResults(arguments).Any()) {
                grew |= Hold([..arguments.Where(static a => a.Type == SilType.Region).SelectMany(Of).Concat(Of(callee))], IndirectResults(arguments));
            }

            // Generic code compiled once that may give back what is rooted in a
            // region of its own, given metadata not yet known: so may this call.
            if (callee is FunctionRefInst { Function: var generic } && localResults?.GetValueOrDefault(generic) is { } genericRoot
                && arguments.Any(static a => a.Type == SilType.Metadata && a is not MetadataInst)) {
                grew |= Hold([new ValueAnchor(call, $"{genericRoot} (in '{GenericName(function, generic)}')")], IndirectResults(arguments));
            }

            return grew;
        }

        // A reference parameter, a region the caller lends and a closure it gives are rooted in the caller.
        for (var i = 0; i < function.Entry.Arguments.Count; i++) {
            if (function.Entry.Arguments[i].Type is AddressType or InlinePointerType || IsClosureArgument(function.Entry.Arguments[i].Type) || function.Entry.Arguments[i].Type == SilType.Region) {
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

                        // A closure's context holds what it captured: rooted where that is.
                        case RefElementAddrInst element when contexts.Of(element.Reference) is { } context:
                            changed |= Add(element, Of(context));
                            break;

                        // A region captured is as a reference into it: it ends with its block. A
                        // box captured — an 'inout' parameter's, a 'var's — holds what is put in it.
                        case StoreInst { Address: RefElementAddrInst capturedAt } captured
                            when contexts.Of(capturedAt.Reference) is { } context && (regionCaptures || captured.Value.Type != SilType.Region):
                            changed |= Add(context, Of(captured.Value).Concat(contexts.Of(captured.Value) is { } inner ? Of(inner) : []).ToList());
                            break;

                        case PartialApplyInst { Context: { } context } closure:
                            changed |= Add(closure, Of(context));
                            break;

                        case RefElementAddrInst element when IsCaptured(element, function):
                            changed |= Add(element, [CapturesAnchor.Instance]);
                            break;

                        case RefElementAddrInst element:
                            changed |= Add(element, [HeapAnchor.Instance]);
                            break;

                        // An element where it is, in the buffer of the array at the place: rooted where the array is.
                        case BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique" } element:
                            changed |= Add(element, Of(element.Operands[0]));
                            break;

                        // A closure kept in a part of a variable: what was put there. A closure
                        // does not refer to where it is kept, only to what it captured.
                        case StoreInst { Address: not AllocStackInst } intoPart when ClosuresOnly(intoPart.Value.Type) && RootSlot(intoPart.Address) is { } partSlot:
                            changed |= Hold([..Of(intoPart.Value)], [partSlot]);
                            break;

                        // Read from a parameter's storage, it is what the caller put there.
                        case LoadInst { Address: not AllocStackInst } fromPart when ClosuresOnly(fromPart.Type):
                            changed |= Add(fromPart, RootSlot(fromPart.Address) is { } fromSlot ? stored.GetValueOrDefault(fromSlot) ?? [] : Of(fromPart.Address).Where(a => HoldsOnlyItsOwn(a, function) || IsStorageParameter(a, function)));
                            break;

                        case LoadBorrowInst { Address: not AllocStackInst } borrowedPart when ClosuresOnly(borrowedPart.Type):
                            changed |= Add(borrowedPart, RootSlot(borrowedPart.Address) is { } borrowedSlot ? stored.GetValueOrDefault(borrowedSlot) ?? [] : Of(borrowedPart.Address).Where(a => HoldsOnlyItsOwn(a, function) || IsStorageParameter(a, function)));
                            break;

                        // A reference put in a part of a variable is kept in the variable.
                        case StoreInst { Address: not AllocStackInst } intoRefPart when HoldsReference(intoRefPart.Value.Type) && RootSlot(intoRefPart.Address) is { } refSlot:
                            changed |= Hold([..Of(intoRefPart.Value)], [refSlot]);
                            break;

                        // A reference put in a variable's array is kept there, as one put in the variable.
                        case StoreInst { Address: BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique", Operands: [AllocStackInst arraySlot, ..] } } intoElement
                            when HoldsReference(intoElement.Value.Type):
                            changed |= Hold([..Of(intoElement.Value)], [arraySlot]);
                            break;

                        case LoadInst { Address: BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique", Operands: [AllocStackInst arraySlot, ..] } } fromElement
                            when HoldsReference(fromElement.Type):
                            changed |= Add(fromElement, stored.GetValueOrDefault(arraySlot) ?? []);
                            break;

                        case LoadBorrowInst { Address: BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique", Operands: [AllocStackInst arraySlot, ..] } } borrowedElement
                            when HoldsReference(borrowedElement.Type):
                            changed |= Add(borrowedElement, stored.GetValueOrDefault(arraySlot) ?? []);
                            break;

                        // An array of references holds what is put in it — appended, set,
                        // inserted — and gives back what it holds.
                        case BuiltinInst { Name: var arrayName } arrayBuiltin when arrayName.StartsWith("array_", StringComparison.Ordinal)
                            && arrayBuiltin.Operands.Any(static o => HoldsReference(o.Type) || o is AllocStackInst { ElementType: var e } && HoldsReference(e)): {
                            // An array in a variable, or in a part of one, holds what was put in the
                            // variable. One reached through another address holds what was put in it
                            // there, which is not known here: of references, it is taken to be rooted
                            // where the array is; of closures, in a parameter's storage, the caller's.
                            List<AllocStackInst> arraySlots = [
                                ..arrayBuiltin.Operands.Where(static o => o.Type is AddressType { Pointee: var p } && HoldsReference(p)).Select(RootSlot).OfType<AllocStackInst>().Distinct(),
                            ];
                            var held = arrayBuiltin.Operands.Where(o => o.Type is not AddressType || RootSlot(o) is null && HoldsReference(o.Type, closures: false))
                                .Where(static o => HoldsReference(o.Type)).SelectMany(Of)
                                .Concat(arrayBuiltin.Operands.Where(o => o.Type is AddressType && RootSlot(o) is null && !HoldsReference(o.Type, closures: false)).SelectMany(Of).Where(a => IsStorageParameter(a, function) || HoldsOnlyItsOwn(a, function)))
                                .Concat(arraySlots.SelectMany(o => stored.GetValueOrDefault(o) ?? [])).ToList();
                            changed |= Hold(held, arraySlots);
                            if (HoldsReference(arrayBuiltin.Type)) {
                                changed |= Add(arrayBuiltin, held);
                            }

                            break;
                        }

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
                        // From a part of a variable, also what was put in the variable.
                        case LoadInst load when HoldsReference(load.Type):
                            changed |= load.Address is AllocStackInst from
                                ? Add(load, stored.GetValueOrDefault(from) ?? [])
                                : Add(load, Of(load.Address).Concat(RootSlot(load.Address) is { } loadSlot ? stored.GetValueOrDefault(loadSlot) ?? [] : []));
                            break;

                        case LoadBorrowInst borrowed when HoldsReference(borrowed.Type):
                            changed |= borrowed.Address is AllocStackInst borrowedFrom
                                ? Add(borrowed, stored.GetValueOrDefault(borrowedFrom) ?? [])
                                : Add(borrowed, Of(borrowed.Address).Concat(RootSlot(borrowed.Address) is { } borrowSlot ? stored.GetValueOrDefault(borrowSlot) ?? [] : []));
                            break;

                        // A value holding references holds their anchors; so does
                        // one holding a generic value, which may be one ('Box<T>').
                        case StructInst or TupleInst or EnumInst when HoldsReference(instruction.Type) || function.IsUnspecialised && SilType.MentionsOpaque(instruction.Type):
                            changed |= Add(instruction, instruction.Operands.Where(o => HoldsReference(o.Type) || function.IsUnspecialised && SilType.MentionsOpaque(o.Type)).SelectMany(Of).ToList());
                            break;

                        case StructExtractInst extract when HoldsReference(extract.Type):
                            changed |= Add(extract, Of(extract.Value));
                            break;

                        case TupleExtractInst extract when HoldsReference(extract.Type):
                            changed |= Add(extract, Of(extract.Value));
                            break;

                        case CopyValueInst copied when HoldsReference(copied.Type) || copied.Value is AllocRefInst box && contexts.Contains(box):
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

                        case ApplyInst apply:
                            if (HoldsReference(apply.Type)) {
                                changed |= Add(apply, CallResult(apply.Callee, [..apply.Arguments], apply.Type, summaries, Of, Contents));
                            }

                            changed |= Lent(apply.Arguments);
                            changed |= GenericResults(apply, apply.Callee, [..apply.Arguments]);
                            break;

                        // A generic value kept in a variable: what was put there, as it may be a reference.
                        case StoreInst { Value.Type: OpaqueType, Address: AllocStackInst opaqueInto } opaqueStore:
                            changed |= Hold([..Of(opaqueStore.Value)], [opaqueInto]);
                            break;

                        case LoadInst { Type: OpaqueType, Address: AllocStackInst opaqueSlot } opaqueLoad:
                            changed |= Add(opaqueLoad, stored.GetValueOrDefault(opaqueSlot) ?? []);
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

                        // As an apply: its result, on to the normal block.
                        case TryApplyInst tryApply:
                            if (tryApply.Normal.Arguments is [var result] && HoldsReference(result.Type)) {
                                changed |= Add(result, CallResult(tryApply.Callee, [..tryApply.Arguments], result.Type, summaries, Of, Contents));
                            }

                            changed |= Lent(tryApply.Arguments);
                            changed |= GenericResults(tryApply, tryApply.Callee, [..tryApply.Arguments]);
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

    /// <summary>The variables a call's generic results are given back in: its arguments that are the address of an opaque value.</summary>
    static IEnumerable<AllocStackInst> IndirectResults(IEnumerable<SilValue> arguments) =>
        arguments.Select(static a => a is BuiltinInst { Name: "opaque_cast" } cast ? cast.Operands[0] : a).OfType<AllocStackInst>().Where(static s => s.ElementType is OpaqueType);

    /// <summary>A type that holds closures but no reference of its own: where it is kept says nothing of what it captured.</summary>
    static bool ClosuresOnly(SilType type) => HoldsReference(type) && !HoldsReference(type, closures: false);

    /// <summary>The variable a part's address is in: through fields, tuple elements and payloads.</summary>
    static AllocStackInst? RootSlot(SilValue address) =>
        address switch {
            AllocStackInst slot => slot,
            StructElementAddrInst element => RootSlot(element.Address),
            TupleElementAddrInst element => RootSlot(element.Address),
            EnumPayloadAddrInst element => RootSlot(element.Address),
            BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique", Operands: [var array, ..] } when array.Type is AddressType => RootSlot(array),
            _ => null,
        };

    /// <summary>
    ///     Memory whose contents are rooted where it is (rule 4): an object's,
    ///     a region's. Not a variable's or what a reference parameter points to,
    ///     which may hold anything.
    /// </summary>
    static bool HoldsOnlyItsOwn(Anchor anchor, SilFunction function) =>
        anchor switch {
            HeapAnchor or CapturesAnchor => true,
            LocalAnchor { Slot.ElementType: var element } => element == SilType.Region,
            ParameterAnchor { Index: var i } => i < function.Entry.Arguments.Count && function.Entry.Arguments[i].Type == SilType.Region,
            _ => false,
        };

    /// <summary>A closure the caller gave, which the callee may keep only as the caller says (<see cref="Escaping" />).</summary>
    static bool IsClosureParameter(Anchor anchor, SilFunction function) =>
        anchor is ParameterAnchor { Index: var i } && i < function.Entry.Arguments.Count && IsClosureArgument(function.Entry.Arguments[i].Type);

    /// <summary>
    ///     A closure given as an argument: one, '(() -> Int)?' (which is
    ///     always '@escaping'), or a variable of closures given by address.
    /// </summary>
    static bool IsClosureArgument(SilType type) =>
        type is ThickFunctionType
        || TypeMetadata.IsOptional(type) && ((EnumType)type).Cases[0].Payload is ThickFunctionType
        || type is AddressType { Pointee: var p } && ClosuresOnly(p);

    /// <summary>The caller's storage, given by address ('inout', '&amp;'): what is in it, the caller put there.</summary>
    static bool IsStorageParameter(Anchor anchor, SilFunction function) =>
        anchor is ParameterAnchor { Index: var i } && i < function.Entry.Arguments.Count && function.Entry.Arguments[i].Type is AddressType;

    /// <summary>Whether a store is into a variable's own storage, whose anchors follow what is stored, rather than memory that outlives the function.</summary>
    static bool IntoVariable(StoreInst store, ContextSet contexts) =>
        store.Address is AllocStackInst
        || store.Address is RefElementAddrInst element && contexts.Of(element.Reference) is not null
        || store.Address is BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique", Operands: [AllocStackInst, ..] }
        || ClosuresOnly(store.Value.Type) && RootSlot(store.Address) is not null;

    /// <summary>
    ///     What an instruction puts into memory that outlives the function:
    ///     a store, and what is appended to, set in or stored into an array
    ///     reached through an address that is not a variable's (an object's
    ///     field, a region's memory, the caller's storage). Into a variable
    ///     the anchors follow what is put there instead.
    /// </summary>
    static IEnumerable<(SilInstruction At, SilValue Address, SilValue Value)> Puts(SilInstruction instruction, ContextSet contexts) {
        switch (instruction) {
            case StoreInst store when HoldsReference(store.Value.Type) && !IntoVariable(store, contexts):
                yield return (store, store.Address, store.Value);
                break;
            // An array changed where it is (what SharedStorage says of the builtin): what it is given goes in.
            case BuiltinInst builtin when SharedStorage.AccessOf(builtin) is ({ Type: AddressType { Pointee: var array } } at, true)
                                          && HoldsReference(array) && RootSlot(at) is null
                                          && !(at is RefElementAddrInst boxed && contexts.Of(boxed.Reference) is not null):
                foreach (var value in builtin.Operands.Skip(1).Where(static o => o.Type is not AddressType && HoldsReference(o.Type))) {
                    yield return (instruction, at, value);
                }

                break;
        }
    }

    /// <summary>
    ///     The closure parameters a function keeps past its call: stores into
    ///     an object, a region or a global, or gives to a function that keeps
    ///     them. What such a closure captured must outlive any call. Of a
    ///     closure's body, <see cref="KeepsCaptures" /> too when it keeps what
    ///     it captured so.
    /// </summary>
    static HashSet<int> Escaping(SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors, Dictionary<string, HashSet<int>> escaping) {
        var kept = new HashSet<int>();
        var contexts = Contexts(function);
        void Keep(SilValue value, bool captures = true) {
            foreach (var anchor in anchors.GetValueOrDefault(value) ?? []) {
                if (IsClosureParameter(anchor, function)) {
                    kept.Add(((ParameterAnchor)anchor).Index);
                } else if (anchor is CapturesAnchor && captures) {
                    kept.Add(KeepsCaptures);
                }
            }
        }

        foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
            // What came from the caller's storage, put back there ('inout' written
            // back), is not kept: the caller's variable holds it as it did. What
            // is put in a variable the closure captured lives as its captures do.
            foreach (var (_, address, value) in Puts(instruction, contexts)) {
                var into = anchors.GetValueOrDefault(address);
                if (into is not { Count: > 0 } || !into.All(a => IsStorageParameter(a, function))
                    || !(anchors.GetValueOrDefault(value) ?? []).All(into.Contains)) {
                    Keep(value, captures: into is not { Count: > 0 } || !into.All(static a => a is CapturesAnchor));
                }
            }

            if (instruction is not StoreInst) {
                foreach (var argument in KeptArguments(instruction, function, escaping).Arguments) {
                    Keep(argument);
                }
            }
        }

        return kept;
    }

    /// <summary>
    ///     The arguments a call may keep past it, and who keeps them: for a
    ///     function of the module, the closure parameters it keeps
    ///     (<see cref="Escaping" />); for any other callee — a closure, a
    ///     method or requirement dispatched to, a function without a body —
    ///     every closure its type does not say it only borrows
    ///     (<see cref="ApplyInst.Lent" />, the parameters not '@escaping'):
    ///     the type holds of every closure of it, and an override or a
    ///     conformance may not add '@escaping' (S3210).
    /// </summary>
    static (string? Keeper, List<SilValue> Arguments) KeptArguments(SilInstruction instruction, SilFunction function, Dictionary<string, HashSet<int>> escaping) {
        // A closure whose body keeps what it captured: its context is kept, as an argument would be.
        if (instruction is PartialApplyInst { Function: FunctionRefInst { Function: var body }, Context: { } context }
            && escaping.TryGetValue(body, out var bodyKeeps) && bodyKeeps.Contains(KeepsCaptures)) {
            return ("its body keeps what it captured past its call", [context]);
        }

        (SilValue? callee, List<SilValue> arguments, ImmutableArray<int> lent) = instruction switch {
            ApplyInst apply => (apply.Callee, apply.Arguments.ToList(), apply.Lent),
            TryApplyInst tryApply => (tryApply.Callee, tryApply.Arguments.ToList(), tryApply.Lent),
            _ => ((SilValue?)null, new List<SilValue>(), ImmutableArray<int>.Empty),
        };
        if (callee is null) {
            return (null, []);
        }

        if (callee is FunctionRefInst { Function: var name } && function.Module?.Find(name) is { Blocks.Count: > 0 } known) {
            return escaping.TryGetValue(name, out var keeps)
                ? ($"'{known.DisplayName ?? known.Name}' keeps it past the call", [..keeps.Order().Where(j => j >= 0 && j < arguments.Count).Select(j => arguments[j])])
                : (null, []);
        }

        List<SilValue> closures = [..arguments.Where((a, i) => !lent.Contains(i) && IsClosureArgument(a.Type))];
        return closures.Count == 0
            ? (null, [])
            : (callee switch {
                FunctionRefInst reference => $"'{reference.Function}', whose body is not seen, may keep it past the call (its parameter is '@escaping')",
                ClassMethodInst method => $"the method '{method.Method}' may keep it past the call (its parameter is '@escaping')",
                BuiltinInst { Name: var witness } when witness.StartsWith("witness", StringComparison.Ordinal) => "the protocol requirement called here may keep it past the call",
                _ => "the closure called here may keep it past the call (its type's parameter is '@escaping')",
            }, closures);
    }

    /// <summary>A closure given to a function that keeps it may capture nothing that dies before the heap does.</summary>
    /// <remarks>A variable given by address is kept as what it holds.</remarks>
    static void CheckEscapingClosures(SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors, Dictionary<AllocStackInst, HashSet<Anchor>> contents,
        Dictionary<string, HashSet<int>> escaping, SourceText source, DiagnosticBag diagnostics) {
        foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
            if (KeptArguments(instruction, function, escaping) is not ({ } keeper, var arguments)) {
                continue;
            }

            foreach (var argument in arguments) {
                var given = argument.Type is AddressType && RootSlot(argument) is { } slot ? contents.GetValueOrDefault(slot) : anchors.GetValueOrDefault(argument);
                var root = (given ?? []).Where(a => !IsClosureParameter(a, function)).Select(a => a switch {
                    LocalAnchor local => Describe(local.Slot, function),
                    ValueAnchor temporary => temporary.Description,
                    ParameterAnchor parameter => $"parameter '{(parameter.Index < function.ParameterNames.Count ? function.ParameterNames[parameter.Index] : "?")}'",
                    _ => null,
                }).FirstOrDefault(static r => r is not null);
                if (root is null) {
                    continue;
                }

                diagnostics.Error(
                    ErrorCode.ERR_ReferenceOutlivesRoot,
                    SpanOf(instruction, source),
                    $"this closure captured a reference into {root}, and {keeper}, where it would outlive it. "
                    + "To fix it: capture a copy of the value instead of the reference (r.toOwned()); or allocate what it refers to on the heap"
                );
                break;
            }
        }
    }

    /// <summary>The objects made in a function as a closure's context: what they hold, the closure captured.</summary>
    /// <remarks>
    ///     A box a context captures — an 'inout' parameter's copy, a 'var'
    ///     closures share — is one too: it is reached only through them.
    /// </remarks>
    static ContextSet Contexts(SilFunction function) {
        var instructions = function.Blocks.SelectMany(static b => b.Instructions).ToList();
        var contexts = new ContextSet();
        contexts.UnionWith(instructions.OfType<PartialApplyInst>().Select(static p => p.Context).OfType<AllocRefInst>());

        // A 'var' closures share is a box kept in a variable of its own until
        // a closure captures it: the variable holds only that box.
        var boxed = new Dictionary<AllocStackInst, AllocRefInst?>();
        foreach (var store in instructions.OfType<StoreInst>()) {
            if (store.Address is AllocStackInst { ElementType: ClassType { Boxed: not null } } slot) {
                boxed[slot] = (store.Value is CopyValueInst copied ? copied.Value : store.Value) is AllocRefInst made
                              && (!boxed.TryGetValue(slot, out var earlier) || ReferenceEquals(earlier, made))
                    ? made
                    : null;
            }
        }

        foreach (var (slot, box) in boxed) {
            if (box is not null) {
                contexts.Slots[slot] = box;
            }
        }

        foreach (var store in instructions.OfType<StoreInst>()) {
            if (store.Address is RefElementAddrInst { Reference: AllocRefInst context } && contexts.Contains(context)
                && contexts.Box(store.Value) is { Type: ClassType { Boxed: not null } } box) {
                contexts.Add(box);
            }
        }

        return contexts;
    }

    /// <summary>
    ///     The objects made as closures' contexts, and the boxes they capture,
    ///     with the variables a box is kept in until it is captured.
    /// </summary>
    sealed class ContextSet : HashSet<AllocRefInst> {
        public Dictionary<AllocStackInst, AllocRefInst> Slots { get; } = [];

        /// <summary>The object made here that a reference is: itself, a copy, or read from the variable it is kept in.</summary>
        public AllocRefInst? Box(SilValue reference) =>
            reference switch {
                AllocRefInst made => made,
                CopyValueInst copy => Box(copy.Value),
                LoadInst { Address: AllocStackInst slot } when Slots.TryGetValue(slot, out var box) => box,
                LoadBorrowInst { Address: AllocStackInst slot } when Slots.TryGetValue(slot, out var box) => box,
                _ => null,
            };

        /// <summary>The context or box a reference is, if it is one.</summary>
        public AllocRefInst? Of(SilValue reference) => Box(reference) is { } box && Contains(box) ? box : null;
    }

    /// <summary>
    ///     What a call's reference result may be rooted in: the anchors of the
    ///     arguments its callee's summary names; for a callee not known here —
    ///     a closure — every reference and region passed to it, and whatever
    ///     the closure itself captured.
    /// </summary>
    /// <remarks>
    ///     A result that holds closures, not references, is not an address
    ///     it was given — a closure cannot capture one — but may hold what is
    ///     there: an 'inout' parameter's value is captured as a copy, in a box.
    /// </remarks>
    static IEnumerable<Anchor> CallResult(SilValue callee, List<SilValue> arguments, SilType result, Dictionary<string, HashSet<int>> summaries,
        Func<SilValue, IEnumerable<Anchor>> of, Func<SilValue, IEnumerable<Anchor>> contents) {
        var lent = HoldsReference(result, closures: false);
        IEnumerable<Anchor> Given(SilValue argument) =>
            argument.Type is not AddressType ? of(argument)
            : lent ? of(argument).Concat(contents(argument))
            : contents(argument);

        if (callee is FunctionRefInst reference && summaries.TryGetValue(reference.Function, out var summary)) {
            return summary.Where(i => i < arguments.Count).SelectMany(i => Given(arguments[i]));
        }

        // Of a callee not known here, a result that holds closures, not
        // references, is taken to hold no reference it was lent — only what a
        // region given to it, a closure, or a value given by address holds.
        return arguments.Where(a => (lent ? HoldsReference(a.Type) : a.Type is ThickFunctionType || a.Type is AddressType { Pointee: var p } && HoldsReference(p)) || a.Type == SilType.Region)
            .SelectMany(Given).Concat(of(callee));
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
            if (LocalRoot(value, function, anchors) is not { } root) {
                continue;
            }

            diagnostics.Error(
                ErrorCode.ERR_ReferenceOutlivesRoot,
                SpanOf(at, source),
                $"cannot return a reference rooted in {root}{(function.DisplayName is { } instance ? $" (as {instance})" : "")}: it dies when the function returns. To fix it: {Fix(root)}"
            );
        }
    }

    /// <summary>The local, region or temporary a value may be rooted in, which dies with the function, described.</summary>
    static string? LocalRoot(SilValue value, SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors) =>
        (anchors.GetValueOrDefault(value) ?? []).FirstOrDefault(static a => a is LocalAnchor or ValueAnchor) switch {
            LocalAnchor local => Describe(local.Slot, function),
            ValueAnchor temporary => temporary.Description,
            _ => null,
        };

    static string Fix(string root) =>
        root.StartsWith("parameter", StringComparison.Ordinal)
            ? "take the parameter as 'ref', so the reference is rooted in the caller's value; or return an owned copy"
            : root.StartsWith("region", StringComparison.Ordinal)
                ? "return a copy of the value, out of the region (r.toOwned()); or allocate it in a region the caller passes (fn f(in r: Region) -> T&)"
                : "return an owned value instead (copy what it refers to); allocate in the caller's region (fn f(in r: Region) -> T&); or return the whole value";

    /// <summary>
    ///     A generic function compiled once gives its result through
    ///     <c>$out</c>, not knowing whether it is a reference: what it may give
    ///     that is rooted in its own local or region, described — for its
    ///     callers to refuse where the result is one (<see cref="CheckGenericResults" />).
    /// </summary>
    static string? LocalGenericResult(SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors) {
        var outIndex = function.ParameterNames.IndexOf("$out");
        if (!function.IsUnspecialised || outIndex < 0 || outIndex >= function.Entry.Arguments.Count) {
            return null;
        }

        var output = function.Entry.Arguments[outIndex];
        return function.Blocks.SelectMany(static b => b.Instructions).OfType<StoreInst>()
            .Where(s => ReferenceEquals(s.Address, output) && SilType.MentionsOpaque(s.Value.Type))
            .Select(s => LocalRoot(s.Value, function, anchors)).FirstOrDefault(static r => r is not null);
    }

    /// <summary>
    ///     A call of a generic function compiled once whose result may be
    ///     rooted in a region or local of its own (<see cref="LocalGenericResult" />),
    ///     made with a reference among its types: the reference would outlive its root.
    /// </summary>
    static void CheckGenericResults(SilFunction function, Dictionary<string, string> localResults, SourceText source, DiagnosticBag diagnostics) {
        if (localResults.Count == 0) {
            return;
        }

        foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
            (SilValue? callee, IEnumerable<SilValue> arguments) = instruction switch {
                ApplyInst apply => (apply.Callee, apply.Arguments),
                TryApplyInst tryApply => (tryApply.Callee, tryApply.Arguments),
                _ => (null, Enumerable.Empty<SilValue>()),
            };
            if (callee is not FunctionRefInst { Function: var name } || !localResults.TryGetValue(name, out var root)
                || arguments.OfType<MetadataInst>().FirstOrDefault(static m => HoldsReference(m.Described)) is not { } reference) {
                continue;
            }

            var generic = GenericName(function, name);
            diagnostics.Error(
                ErrorCode.ERR_ReferenceOutlivesRoot,
                SpanOf(instruction, source),
                $"'{generic}' would return a reference rooted in {root}, which dies when it returns, as its result here is a reference ({(reference.Described is AddressType { Pointee: var pointee } ? TypeMetadata.DisplayName(pointee) + "&" : TypeMetadata.DisplayName(reference.Described))}). To fix it: {Fix(root)}"
            );
        }
    }

    static string GenericName(SilFunction caller, string name) =>
        caller.Module?.Find(name) is { } found ? found.DisplayName ?? found.Name.Replace("$generic", "", StringComparison.Ordinal) : name;

    /// <summary>
    ///     Rule 4: references stored into a region's memory refer only into
    ///     that region, which they then live as long as; into an object's,
    ///     only into the heap. A local, a parameter, another region would
    ///     each be gone while the stored reference still could be read.
    /// </summary>
    static void CheckStores(SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors, SourceText source, DiagnosticBag diagnostics) {
        // What a closure captures is held to where the closure goes, not to the heap its context is in.
        var contexts = Contexts(function);
        foreach (var block in function.Blocks) {
            foreach (var (instruction, address, value) in block.Instructions.SelectMany(i => Puts(i, contexts))) {
                var destination = anchors.GetValueOrDefault(address) ?? [];
                var heap = destination.Contains(HeapAnchor.Instance);
                var captured = destination.Count > 0 && destination.All(static a => a is CapturesAnchor);
                foreach (var anchor in anchors.GetValueOrDefault(value) ?? []) {
                    // A closure parameter kept is the caller's to check, and what a
                    // closure captured is checked where it is made (CheckEscapingClosures).
                    // A variable it captured outlives its call, as the heap does.
                    if (destination.Contains(anchor) && !(heap && anchor is not HeapAnchor) || IsClosureParameter(anchor, function)
                        || anchor is CapturesAnchor && heap || captured && anchor is HeapAnchor) {
                        continue;
                    }

                    var into = heap
                        ? "an object's field, which may outlive it"
                        : captured
                        ? "a variable the closure captured, which outlives its call"
                        : destination.OfType<LocalAnchor>().FirstOrDefault() is { } region
                            ? Describe(region.Slot, function)
                            : destination.OfType<ParameterAnchor>().FirstOrDefault() is { } lent
                                ? $"region '{(lent.Index < function.ParameterNames.Count ? function.ParameterNames[lent.Index] : "?")}', passed in"
                                : "storage that outlives it";
                    var root = anchor switch {
                        LocalAnchor local => Describe(local.Slot, function),
                        ParameterAnchor parameter => $"parameter '{(parameter.Index < function.ParameterNames.Count ? function.ParameterNames[parameter.Index] : "?")}'",
                        CapturesAnchor => "what the closure captured",
                        _ => "the heap",
                    };
                    diagnostics.Error(
                        ErrorCode.ERR_ReferenceOutlivesRoot,
                        SpanOf(instruction, source),
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
    /// <param name="resetAnchors">The same, but a closure without what it captured of a region itself: what a reset leaves it calling into.</param>
    static void CheckLiveness(SilFunction function, Dictionary<SilValue, HashSet<Anchor>> anchors, Dictionary<SilValue, HashSet<Anchor>> resetAnchors,
        SourceText source, DiagnosticBag diagnostics) {
        // Only references that came from elsewhere are checked: the lowering's
        // own reads of a local are inside its scope by construction.
        bool Indirect(SilValue value) =>
            value switch {
                LoadInst or ApplyInst => true,
                LoadBorrowInst { Type: ThickFunctionType } => true,
                BuiltinInst { Name: "array_get" or "array_get_borrowed" or "array_remove_last" or "array_take" } => true,
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
        // What each closure's context was given, by the context.
        var captures = new Dictionary<AllocRefInst, List<SilValue>>();
        foreach (var store in function.Blocks.SelectMany(static b => b.Instructions).OfType<StoreInst>()) {
            if (store.Address is RefElementAddrInst { Reference: AllocRefInst context } && HoldsReference(store.Value.Type)) {
                if (!captures.TryGetValue(context, out var list)) {
                    captures[context] = list = [];
                }

                list.Add(store.Value);
            }
        }

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

        // The read of a variable a value is, through copies.
        static SilValue? Read(SilValue value) =>
            value switch {
                LoadInst { Address: AllocStackInst } or LoadBorrowInst { Address: AllocStackInst } => value,
                CopyValueInst copy => Read(copy.Value),
                _ => null,
            };

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

        // A variable assigned after a slot died holds what it was given, not
        // what it held: for each variable, the dead slots what it may hold
        // was taken from before they died; and what each read of a variable
        // may hold of them.
        var exitUnclean = new Dictionary<SilBlock, HashSet<(AllocStackInst Holder, AllocStackInst Dead)>>();
        var readUnclean = new Dictionary<SilValue, HashSet<AllocStackInst>>();
        var reported = new HashSet<SilValue>();
        var reportedAt = new HashSet<(SilInstruction, int)>();
        var reportedUses = new HashSet<(AllocStackInst, SilLocation?)>();
        for (var round = 0; round < 2 * blocks.Count + 2; round++) {
            var changed = false;
            foreach (var block in blocks) {
                var dead = new Dictionary<AllocStackInst, SilInstruction>();
                var resets = new Dictionary<Anchor, SilInstruction>();
                var stale = new HashSet<AllocStackInst>();
                var unclean = new HashSet<(AllocStackInst Holder, AllocStackInst Dead)>();
                HashSet<SilValue>? fresh = null;
                foreach (var predecessor in predecessors[block]) {
                    unclean.UnionWith(exitUnclean.GetValueOrDefault(predecessor) ?? []);
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

                // What a variable held of a slot that dies, it holds of a dead one.
                void Died(AllocStackInst slot) {
                    var anchor = new LocalAnchor(slot);
                    foreach (var (holder, held) in holderAnchors) {
                        if (held.Contains(anchor)) {
                            unclean.Add((holder, slot));
                        }
                    }
                }

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
                            if (operand.Type is not (AddressType or InlinePointerType or ThickFunctionType) || fresh.Contains(operand)
                                || operand.Type is AddressType or ThickFunctionType && !Indirect(operand)
                                || instruction is StoreInst store && ReferenceEquals(store.Value, operand)
                                || instruction is BranchInst or CondBranchInst or ReturnInst
                                || operand.Type is ThickFunctionType && instruction is DestroyValueInst or CopyValueInst) {
                                continue;
                            }

                            // A closure called or passed on after a reset of a region it took a reference from.
                            var under = operand.Type is ThickFunctionType
                                ? (resetAnchors.GetValueOrDefault(operand) ?? []).Where(static a => a is LocalAnchor or ParameterAnchor)
                                : RegionsOf(operand);
                            foreach (var changedAnchor in under) {
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
                                        reset is BuiltinInst { Name: "region_reset" } && operand.Type is ThickFunctionType
                                            ? $"this closure captured a reference into {what}, which is gone — the region was reset at {when}; make the closure again after the reset"
                                        : reset is BuiltinInst { Name: "region_reset" }
                                            ? $"this reference refers to {what}, which is gone — the region was reset at {when}; take the reference again after the reset"
                                            : $"this reference into {what} is stale — the value was changed at {when}, which may have moved its bytes; take the reference again after the change"
                                    );
                                }
                            }
                        }
                    }

                    if (((instruction as LoadInst)?.Address ?? (instruction as LoadBorrowInst)?.Address) is AllocStackInst readFrom && HoldsReference(instruction.Type)) {
                        readUnclean[instruction] = [..unclean.Where(p => ReferenceEquals(p.Holder, readFrom)).Select(static p => p.Dead)];
                    }

                    switch (instruction) {
                        case DestroyAddrInst { Address: AllocStackInst slot }:
                            dead[slot] = instruction;
                            Died(slot);
                            continue;

                        case LoadInst { Kind: LoadKind.Take, Address: AllocStackInst slot }:
                            dead[slot] = instruction;
                            Died(slot);
                            continue;

                        case StoreInst { Address: AllocStackInst slot } stored:
                            dead.Remove(slot);
                            var given = RegionsOf(stored.Value).OfType<LocalAnchor>().Select(static a => a.Slot).ToHashSet();
                            unclean.RemoveWhere(p => ReferenceEquals(p.Holder, slot) && !given.Contains(p.Dead));
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

                        // A closure is fresh when what it captured is.
                        case PartialApplyInst { Context: var context } made
                            when context is not AllocRefInst made2 || (captures.GetValueOrDefault(made2) ?? []).All(fresh.Contains):
                            fresh.Add(made);
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

                    // A closure is used where it is called or passed on: what it captured must be alive.
                    foreach (var operand in instruction.Operands) {
                        if (operand.Type is not (AddressType or InlinePointerType or ThickFunctionType) || !Indirect(operand)
                            || instruction is StoreInst store && ReferenceEquals(store.Value, operand)
                            || instruction is BranchInst or CondBranchInst or ReturnInst
                            || operand.Type is ThickFunctionType && instruction is DestroyValueInst or CopyValueInst) {
                            continue;
                        }

                        foreach (var anchor in anchors.GetValueOrDefault(operand) ?? []) {
                            // 'r.v' reads through 'r' and then through the field's
                            // address: one use, said once. A variable read after it
                            // was given something else holds none of what it had.
                            if (anchor is LocalAnchor { Slot: var slot } && dead.TryGetValue(slot, out var ended)
                                && !(Read(operand) is { } read && readUnclean.TryGetValue(read, out var mayHold) && !mayHold.Contains(slot))
                                && reported.Add(operand)
                                && reportedUses.Add((slot, instruction.Location))) {
                                var where = ended.Location is { } location ? $"line {location.Line}" : "earlier";
                                diagnostics.Error(
                                    ErrorCode.ERR_ReferenceOutlivesRoot,
                                    SpanOf(instruction, source),
                                    (operand.Type is ThickFunctionType ? $"this closure captured a reference into {Describe(slot, function)}, which is gone — " : $"this reference refers to {Describe(slot, function)}, which is gone — ")
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

                var previousUnclean = exitUnclean.GetValueOrDefault(block);
                if (previousUnclean is null || !previousUnclean.SetEquals(unclean)) {
                    exitUnclean[block] = unclean;
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
