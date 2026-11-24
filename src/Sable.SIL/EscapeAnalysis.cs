namespace Sable.SIL;

/// <summary>
///     docs/04 §3.7: a class instance or an inline block that does not escape
///     the frame that made it is made on the stack — no allocation, no free.
/// </summary>
/// <remarks>
///     <para>
///         A value escapes when it, or a copy of it, may be reached once the
///         function has returned: returned or thrown; stored anywhere but a
///         local slot; put inside another value; captured; held weakly or
///         unowned; or passed to a parameter that escapes, or to a callee
///         not known here. Which parameters escape is a summary per function,
///         computed for the whole module to a fixed point — the same shape of
///         summary root inference keeps (docs/04 §3.3), so recursion converges.
///     </para>
///     <para>
///         The object keeps its header. One nothing copies is not counted
///         (<see cref="Uncount" />); one copied keeps its count, and its last
///         release runs the deinit and the fields' releases as before, but
///         frees nothing. Nor is one a weak reference could outlive promoted.
///     </para>
///     <para>
///         One stack slot must never hold two live objects, so an allocation
///         inside a loop is promoted only when the object is gone before the
///         loop can come round to it again: every value that holds it — each
///         copy, each borrow — is made in blocks the allocation's dominates,
///         and every local slot it is stored in is emptied of it (destroyed,
///         taken, or stored over) on every path back to the allocation, as
///         many blocks as the loop's body has.
///     </para>
///     <para>
///         A closure's context is promoted as any object is. Making the
///         closure does not let the context escape when the closure's code
///         keeps the context it is given no longer than the call, and
///         calling a closure hands its context only to that code; so the
///         closure is followed as the context is.
///     </para>
/// </remarks>
public sealed class PromoteToStack : ISilPass {
    public string Name => "promote-to-stack";

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<SilModule, Dictionary<string, bool[]>> _summaries = new();

    public void Run(SilFunction function) {
        if (function.Module is not { } module) {
            return;
        }

        var summaries = _summaries.GetValue(module, Summarise);
        InlineAllocators(function, module);

        var uses = new Uses(function);
        Dictionary<SilBlock, HashSet<SilBlock>>? dominators = null;
        Dictionary<SilBlock, HashSet<SilBlock>> Dominators() => dominators ??= SilVerifier.Dominators(function);
        var promoted = new List<(SilInstruction, HashSet<SilValue>)>();
        foreach (var block in function.Blocks) {
            var inLoop = InLoop(block);
            foreach (var instruction in block.Instructions) {
                switch (instruction) {
                    case AllocRefInst { OnStack: false, ClassType.IsCoroutineFrame: false } alloc when !(alloc.ClassType.IsGenericInstance && SilType.MentionsOpaque(alloc.ClassType)) && Promotable(alloc, inLoop, uses, summaries, Dominators, out var holders):
                        alloc.OnStack = true;
                        promoted.Add((alloc, holders));
                        break;

                    case BuiltinInst { Name: "inline_new" } made when Promotable(made, inLoop, uses, summaries, Dominators, out var holders):
                        made.Rename("inline_new_stack");
                        promoted.Add((made, holders));
                        break;

                    // An array literal: its header and its elements together.
                    case BuiltinInst { Name: "array_literal" } literal when !SilBuiltins.IsConstantArray(literal) && Promotable(literal, inLoop, uses, summaries, Dominators, out var holders):
                        literal.Rename("array_literal_stack");
                        promoted.Add((literal, holders));
                        break;
                }
            }
        }

        Uncount(promoted, uses);
    }

    /// <summary>
    ///     An object made in the frame that nothing copies has one reference
    ///     all its life: no retain, so its count is one from start to end, and
    ///     whatever ends that reference is the last release. Each destroy of
    ///     it — of the value, of what was taken out of a slot it was put in,
    ///     or of such a slot — is marked <c>[uncounted]</c>: destroyed in
    ///     place, the count neither read nor written. A slot qualifies when
    ///     all that is ever stored in it is such objects, so that it cannot
    ///     hold anything counted instead. Nothing here may be held weakly or
    ///     unowned, or kept by a callee: it would have escaped.
    /// </summary>
    static void Uncount(List<(SilInstruction Made, HashSet<SilValue> Holders)> promoted, Uses uses) {
        var holders = new HashSet<SilValue>();
        var only = new HashSet<SilValue>();
        foreach (var (made, held) in promoted) {
            if (!held.Any(static h => h is CopyValueInst or LoadInst { Kind: LoadKind.Copy })) {
                only.Add(made);
                holders.UnionWith(held);
            }
        }

        // What holds such objects and nothing else: the objects; a slot
        // every store into which is of such a value; what is taken out of
        // one. A value merged with others at a join is not.
        var slots = new HashSet<AllocStackInst>();
        for (var changed = true; changed;) {
            changed = false;
            foreach (var holder in holders) {
                if (holder is AllocStackInst { Unmanaged: false } slot && !slots.Contains(slot)
                    && uses.Of(slot).All(u => u is not StoreInst store || ReferenceEquals(store.Address, slot) && only.Contains(store.Value))) {
                    slots.Add(slot);
                    only.Add(slot);
                    changed = true;
                }

                if (holder is LoadInst { Kind: LoadKind.Take, Address: AllocStackInst from } taken && slots.Contains(from) && only.Add(taken)) {
                    changed = true;
                }
            }
        }

        foreach (var holder in only) {
            foreach (var user in uses.Of(holder)) {
                switch (user) {
                    case DestroyAddrInst destroy when holder is AllocStackInst:
                        destroy.Uncounted = true;
                        break;

                    case DestroyValueInst destroy when holder is not AllocStackInst:
                        destroy.Uncounted = true;
                        break;
                }
            }
        }
    }

    /// <summary>Whether what an instruction makes may live in the frame: it does not escape, and in a loop, it is gone before the loop comes round.</summary>
    static bool Promotable(
        SilInstruction made,
        bool inLoop,
        Uses uses,
        Dictionary<string, bool[]> summaries,
        Func<Dictionary<SilBlock, HashSet<SilBlock>>> dominators,
        out HashSet<SilValue> holders
    ) {
        holders = [];
        return !Escapes(made, uses, summaries, holders) && (!inLoop || GoneBeforeItComesRound(made, holders, uses, dominators()));
    }

    /// <summary>
    ///     True when nothing holds what <paramref name="made" /> made by the
    ///     time the loop comes back round to it, through however many blocks
    ///     the loop's body has. A local slot is followed from the allocation
    ///     along every path: it must be emptied of the object — destroyed,
    ///     taken, or stored over — before any path reaches the allocation's
    ///     block again. A value holding it is defined in a block the
    ///     allocation's dominates — not at a join the loop comes back through,
    ///     where it could carry this turn's object into the next — and, being
    ///     owned or borrowed, is gone before the allocation's block is entered
    ///     again, as every path into a block must bring the same values; or
    ///     it is made and gone in one block, as a slot's borrow often is; or
    ///     it is read out of a slot at a point the slot cannot hold the
    ///     object, and so holds something else.
    /// </summary>
    static bool GoneBeforeItComesRound(SilInstruction made, HashSet<SilValue> holders, Uses uses, Dictionary<SilBlock, HashSet<SilBlock>> dominators) {
        var home = made.Block;
        var slots = holders.OfType<AllocStackInst>().ToHashSet();
        bool Dominated(SilBlock block) => dominators.TryGetValue(block, out var set) && set.Contains(home);

        // Loads of a slot at a point it may hold the object.
        var holdingLoads = new HashSet<SilInstruction>();

        // Which slots may hold it at the end of each block reached from it.
        var holdingOut = new Dictionary<SilBlock, HashSet<AllocStackInst>>();
        var pending = new Queue<SilBlock>();
        HashSet<AllocStackInst>? Walk(SilBlock block, int from, HashSet<AllocStackInst> holding) {
            for (var i = from; i < block.Instructions.Count; i++) {
                switch (block.Instructions[i]) {
                    case StoreInst { Address: AllocStackInst slot } store when slots.Contains(slot):
                        if (holders.Contains(store.Value)) {
                            holding.Add(slot);
                        } else {
                            holding.Remove(slot);
                        }

                        break;

                    case DestroyAddrInst { Address: AllocStackInst slot }:
                        holding.Remove(slot);
                        break;

                    case LoadInst { Kind: LoadKind.Take, Address: AllocStackInst slot } load:
                        if (holding.Remove(slot)) {
                            holdingLoads.Add(load);
                        }

                        break;

                    case LoadInst or LoadBorrowInst when block.Instructions[i].Operands[0] is AllocStackInst slot:
                        if (holding.Contains(slot)) {
                            holdingLoads.Add(block.Instructions[i]);
                        }

                        break;

                    // Anything else given the address of a slot holding it may put it back.
                    case var other when other.Operands.Any(o => o is AllocStackInst slot && holding.Contains(slot))
                        && other is not (LoadInst or LoadBorrowInst or StoreInst or DeallocStackInst)
                        && !ChangesArrayInPlace(other, other.Operands[0]):
                        return null;
                }
            }

            return holding;
        }

        void Leave(SilBlock block, HashSet<AllocStackInst> holding) {
            if (holdingOut.TryGetValue(block, out var known) && holding.IsSubsetOf(known)) {
                return;
            }

            holdingOut[block] = known is null ? holding : [..known, ..holding];
            foreach (var successor in block.Successors) {
                pending.Enqueue(successor);
            }
        }

        if (Walk(home, home.Instructions.IndexOf(made) + 1, []) is not { } afterMade) {
            return false;
        }

        Leave(home, afterMade);
        while (pending.TryDequeue(out var block)) {
            var holding = new HashSet<AllocStackInst>();
            foreach (var (other, held) in holdingOut) {
                if (other.Successors.Contains(block)) {
                    holding.UnionWith(held);
                }
            }

            if (block == home) {
                if (holding.Count > 0) {
                    return false;
                }

                continue;
            }

            if (Walk(block, 0, holding) is not { } after) {
                return false;
            }

            Leave(block, after);
        }

        // A value holding it is defined where the allocation's block
        // dominates, so that it is made anew each turn — but a value read
        // out of a slot that cannot hold it then does not hold it at all.
        foreach (var holder in holders) {
            switch (holder) {
                case AllocStackInst:
                    continue;

                case SilArgument argument when argument.Block == home || !Dominated(argument.Block):
                    return false;

                case SilInstruction instruction when !Dominated(instruction.Block) && !ReadWhenEmpty(instruction)
                    && uses.Of(instruction).Any(u => u.Block != instruction.Block):
                    return false;
            }
        }

        return true;

        bool ReadWhenEmpty(SilInstruction value) {
            for (var depth = 0; depth < 16; depth++) {
                switch (value) {
                    case LoadInst { Address: AllocStackInst } or LoadBorrowInst { Address: AllocStackInst }:
                        return !holdingLoads.Contains(value);

                    case CopyValueInst or UpcastInst or UncheckedRefCastInst when value.Operands[0] is SilInstruction from:
                        value = from;
                        continue;

                    default:
                        return false;
                }
            }

            return false;
        }
    }

    /// <summary>
    ///     A class's allocating initialiser is an allocation and a call to its
    ///     <c>$initialize</c>: made where it is called, so that the caller
    ///     owns the allocation from the start and may keep it in its frame.
    /// </summary>
    static void InlineAllocators(SilFunction function, SilModule module) {
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is not ApplyInst { Callee: FunctionRefInst reference } apply
                    || module.Find(reference.Function) is not { } callee
                    || ReferenceEquals(callee, function)
                    || Allocator(callee) is not var (classType, initialize)) {
                    continue;
                }

                var alloc = new AllocRefInst(classType) { Block = block, Location = apply.Location };
                var target = new FunctionRefInst(initialize.Function, (FunctionType)initialize.Type) { Block = block, Location = apply.Location };
                var call = new ApplyInst(target, SilType.Unit, [alloc, ..apply.Arguments]) { Block = block, Location = apply.Location };
                block.Instructions[i] = call;
                block.Instructions.Insert(i, target);
                block.Instructions.Insert(i, alloc);
                Uses.Replace(function, apply, alloc);
                i += 2;
            }
        }
    }

    /// <summary>The class and the <c>$initialize</c> of a function that is only an allocating initialiser.</summary>
    static (ClassType, FunctionRefInst)? Allocator(SilFunction callee) {
        if (callee.Blocks is not [var body]
            || body.Instructions is not [AllocRefInst alloc, FunctionRefInst initialize, ApplyInst call, ReturnInst returned]
            || !ReferenceEquals(call.Callee, initialize)
            || !ReferenceEquals(returned.Value, alloc)
            || initialize.Type is not FunctionType initializeType
            // One compiled once makes its object from metadata it is given (#297): it stays a call.
            || callee.IsUnspecialised) {
            return null;
        }

        var arguments = call.Arguments.ToList();
        if (arguments.Count != body.Arguments.Count + 1 || !ReferenceEquals(arguments[0], alloc)) {
            return null;
        }

        for (var i = 0; i < body.Arguments.Count; i++) {
            if (!ReferenceEquals(arguments[i + 1], body.Arguments[i]) || callee.Type.IsOwned(i) != initializeType.IsOwned(i + 1)) {
                return null;
            }
        }

        return (alloc.ClassType, initialize);
    }

    /// <summary>For every function, which of its parameters may escape it.</summary>
    static Dictionary<string, bool[]> Summarise(SilModule module) {
        var summaries = module.Functions.ToDictionary(static f => f.Name, static f => new bool[f.Type.Parameters.Length]);
        var uses = module.Functions.ToDictionary(static f => f, static f => new Uses(f));
        for (var changed = true; changed;) {
            changed = false;
            foreach (var function in module.Functions) {
                if (function.Blocks.Count == 0) {
                    continue;
                }

                var summary = summaries[function.Name];
                for (var i = 0; i < summary.Length && i < function.Entry.Arguments.Count; i++) {
                    if (!summary[i] && Escapes(function.Entry.Arguments[i], uses[function], summaries)) {
                        summary[i] = true;
                        changed = true;
                    }
                }
            }
        }

        return summaries;
    }

    /// <summary>True when the value, or a copy of it, may outlive the function.</summary>
    static bool Escapes(SilValue value, Uses uses, Dictionary<string, bool[]> summaries, HashSet<SilValue>? holders = null) {
        var seen = new HashSet<SilValue>();
        var pending = new Stack<SilValue>();
        pending.Push(value);
        while (pending.Count > 0) {
            var current = pending.Pop();
            if (!seen.Add(current) || current.Type.IsTrivial && current is not AllocStackInst) {
                continue;
            }

            holders?.Add(current);

            foreach (var user in uses.Of(current)) {
                if (Escapes(user, current, pending, uses, summaries)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when this use lets the value escape; values it flows on into are pushed to be followed.</summary>
    static bool Escapes(SilInstruction user, SilValue value, Stack<SilValue> pending, Uses uses, Dictionary<string, bool[]> summaries) {
        switch (user) {
            case DestroyValueInst or DestroyAddrInst or DeallocStackInst or EndBorrowInst:
                return false;

            // Its fields and methods are reached through it; it does not go anywhere.
            case RefElementAddrInst or ClassMethodInst:
                return false;

            case CopyValueInst or UpcastInst or UncheckedRefCastInst or LoadBorrowInst:
                pending.Push(user);
                return false;

            // A local slot: what is loaded from it is followed; anything else
            // given the slot's address may keep what it holds.
            case LoadInst load when value is AllocStackInst:
                pending.Push(load);
                return false;

            case StoreInst store when ReferenceEquals(store.Value, value):
                if (store.Address is AllocStackInst slot) {
                    pending.Push(slot);
                    return false;
                }

                return true;

            case StoreInst when value is AllocStackInst:
                return false;

            case BranchInst branch:
                for (var i = 0; i < branch.Operands.Count; i++) {
                    if (ReferenceEquals(branch.Operands[i], value)) {
                        pending.Push(branch.Target.Arguments[i]);
                    }
                }

                return false;

            case CondBranchInst condition: {
                var trueArguments = condition.TrueArguments.ToList();
                var falseArguments = condition.FalseArguments.ToList();
                for (var i = 0; i < trueArguments.Count; i++) {
                    if (ReferenceEquals(trueArguments[i], value)) {
                        pending.Push(condition.WhenTrue.Arguments[i]);
                    }
                }

                for (var i = 0; i < falseArguments.Count; i++) {
                    if (ReferenceEquals(falseArguments[i], value)) {
                        pending.Push(condition.WhenFalse.Arguments[i]);
                    }
                }

                return false;
            }

            // A closure made with it as its context: followed as it is, when
            // the closure's code keeps the context no longer than a call.
            case PartialApplyInst closure when ReferenceEquals(closure.Context, value):
                if (closure.Function is not FunctionRefInst code || !summaries.TryGetValue(code.Function, out var codeSummary)
                    || codeSummary.Length == 0 || codeSummary[^1]) {
                    return true;
                }

                pending.Push(closure);
                return false;

            case ApplyInst apply:
                return ArgumentEscapes(apply.Callee, [..apply.Arguments], value, summaries);

            case TryApplyInst apply:
                return ArgumentEscapes(apply.Callee, [..apply.Arguments], value, summaries);

            case BuiltinInst builtin when ChangesArrayInPlace(builtin, value):
                return false;

            case BuiltinInst builtin:
                return !BorrowsOnly(builtin, value);

            default:
                return true;
        }
    }

    /// <summary>A known callee keeps an argument only where its summary says the parameter escapes.</summary>
    /// <remarks>
    ///     A closure called is not kept by the call: its context goes to its
    ///     code alone, and a context is followed only into code that does not
    ///     keep it (the closure's own arguments are another matter).
    /// </remarks>
    static bool ArgumentEscapes(SilValue callee, List<SilValue> arguments, SilValue value, Dictionary<string, bool[]> summaries) {
        if (ReferenceEquals(callee, value) && value.Type is ThickFunctionType) {
            return arguments.Any(a => ReferenceEquals(a, value));
        }

        if (ReferenceEquals(callee, value) || callee is not FunctionRefInst reference || !summaries.TryGetValue(reference.Function, out var summary)) {
            return true;
        }

        for (var i = 0; i < arguments.Count; i++) {
            if (ReferenceEquals(arguments[i], value) && (i >= summary.Length || summary[i])) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     An array in a local slot, changed where it is — an element
    ///     appended, set or removed, room made — by an operation that keeps
    ///     neither the array nor the slot. The array stays the slot's,
    ///     unless a copy of a shared one takes its place.
    /// </summary>
    static bool ChangesArrayInPlace(SilInstruction instruction, SilValue value) =>
        instruction is BuiltinInst
        {
            Name: "array_append" or "array_set" or "array_set_unique" or "array_make_unique" or "array_remove_last" or "array_clear" or "array_truncate" or "array_reserve"
                or "array_rotate" or "array_append_utf8" or "array_append_repeating",
        } builtin
        && value is AllocStackInst
        && ReferenceEquals(builtin.Operands[0], value)
        && !builtin.Operands.Skip(1).Any(o => ReferenceEquals(o, value));

    /// <summary>The builtins that read a value, or the slot it is in, and keep neither.</summary>
    static bool BorrowsOnly(BuiltinInst builtin, SilValue value) {
        if (builtin.Name.StartsWith("inline_", StringComparison.Ordinal)) {
            // Every inline operation reads the block or changes it where it is;
            // only the element and value operands are copied, as bytes.
            return true;
        }

        // A slice ('array_slice', 'slice_of') keeps the buffer it views, so
        // it is not one of these.
        return builtin.Name switch {
            // The runtime keeps the hook it is given, for as long as the program runs.
            "trap_hook_set" or "trap_hook_take" => false,
            "string_count" or "string_at" or "string_slice" or "string_contains" or "string_index_of" or "string_trimmed"
                or "string_split" or "string_concat" or "string_concat_many" or "array_count" or "array_get" or "array_get_borrowed" or "print_String" => true,
            _ when builtin.Name.StartsWith("cmp_", StringComparison.Ordinal) => true,
            _ => SilBuiltins.Resolve(builtin.Name, [..builtin.Operands.Select(static o => o.Type)], builtin.Type) is { } signature
                && signature.Consumed.IsEmpty
                && builtin.Type.IsTrivial
                && !builtin.Operands.Any(static o => o.Type is AddressType),
        };
    }

    static bool InLoop(SilBlock block) {
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>(block.Successors);
        while (pending.Count > 0) {
            var next = pending.Pop();
            if (next == block) {
                return true;
            }

            if (seen.Add(next)) {
                foreach (var successor in next.Successors) {
                    pending.Push(successor);
                }
            }
        }

        return false;
    }
}
