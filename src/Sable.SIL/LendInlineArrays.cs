namespace Sable.SIL;

/// <summary>
///     An inline array (<c>T[k]</c>, <c>T[..k]</c>, #304) viewed as a slice for
///     uses that keep nothing lends itself: the slice is a view of the array's
///     own storage, nothing copied (docs/02 §2.5). Lowering writes every such
///     view as a copy — <c>slice_of</c> or <c>array_slice</c> of the array, its
///     elements copied into a buffer of their own, and <c>fixed_lend_mut_copy</c>
///     for the place a mutating member of <c>Element[:]</c> changes — which is
///     always right; this pass makes one a lend where it proves the lend is.
/// </summary>
/// <remarks>
///     <para>
///         <b>What holds the view.</b> The view, and every value that comes to
///         hold it — a copy, a slot it is stored in and what is loaded from
///         that, a slice of it, an argument a branch hands it to — may be
///         used only where none of them can outlive the function: given to a
///         call that keeps nothing of that argument (the escape summary
///         <see cref="PromoteToStack" /> computes, to a fixed point over the
///         module), read by a builtin that keeps nothing, destroyed. Anything
///         else — a store into memory that is not a local slot, a return, a
///         capture, a value made of it, <c>toArray()</c> of it — and the view
///         stays a copy.
///     </para>
///     <para>
///         <b>A read</b> (<c>fixed_lend</c>, <c>fixed_lend_slice</c>, a
///         guaranteed value, never destroyed) also needs what it views to
///         live, unchanged, through every use of every holder: a parameter,
///         a borrow that ends after them, a value that is consumed after them
///         (<see cref="BorrowHeld" />'s keepers). Its header's count starts at
///         two, so that a write through any holder copies the run first, as
///         a write to a shared buffer does: the array lent is never changed.
///     </para>
///     <para>
///         <b>A change.</b> The place a mutating member changes is under an
///         access for the call, so nothing else reaches it while the view is
///         lent (<c>fixed_lend_mut</c>, its count one): a write through the
///         view changes the array where it is, and <c>fixed_restore</c> has
///         nothing to do but for what the callee put in the view's place.
///     </para>
/// </remarks>
public sealed class LendInlineArrays : ISilPass {
    public string Name => "lend-inline-arrays";

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<SilModule, Dictionary<string, bool[]>> _summaries = new();

    public void Run(SilFunction function) {
        if (function.Module is not { } module || function.Type.IsAsync
            || !function.Blocks.Any(static b => b.Instructions.Any(static i => i is BuiltinInst view && Viewed(view) is not null))) {
            return;
        }

        var summaries = _summaries.GetValue(module, PromoteToStack.Summarise);
        var switches = new Dictionary<SilBlock, SwitchEnumInst?>(ReferenceEqualityComparer.Instance);
        foreach (var block in function.Blocks) {
            if (block.Terminator is SwitchEnumInst switched) {
                foreach (var (_, target) in switched.Cases) {
                    switches[target] = switches.ContainsKey(target) ? null : switched;
                }
            }
        }

        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is not BuiltinInst view || Viewed(view) is null) {
                    continue;
                }

                var uses = new Uses(function);
                var change = view.Name == "fixed_lend_mut_copy";
                if (Holders(view, uses, summaries, change) is not { } used) {
                    continue;
                }

                if (change) {
                    view.Rename("fixed_lend_mut");
                    continue;
                }

                if (BorrowHeld.Keepers(view.Operands[0], function, switches) is not { } keepers || !Alive(view, used, keepers)) {
                    continue;
                }

                // A guaranteed value now: never destroyed, and copied where it was consumed.
                view.Rename(view.Name == "slice_of" ? "fixed_lend" : "fixed_lend_slice");
                foreach (var user in uses.Of(view).ToList()) {
                    if (user is DestroyValueInst destroy) {
                        destroy.Block.Instructions.Remove(destroy);
                    } else if (Uses.Consumes(user, view)) {
                        var copy = new CopyValueInst(view) { Block = user.Block, Location = user.Location };
                        user.Block.Instructions.Insert(user.Block.Instructions.IndexOf(user), copy);
                        for (var k = 0; k < user.Operands.Count; k++) {
                            if (ReferenceEquals(user.Operands[k], view)) {
                                user.SetOperand(k, copy);
                            }
                        }
                    }
                }

                i = block.Instructions.IndexOf(view);
            }
        }
    }

    /// <summary>The inline array a view is of: its value's type, or its place's.</summary>
    static FixedArrayType? Viewed(BuiltinInst view) =>
        view.Name switch {
            "slice_of" or "array_slice" when view.Operands.Count > 0 => view.Operands[0].Type as FixedArrayType,
            "fixed_lend_mut_copy" => (view.Operands[0].Type as AddressType)?.Pointee as FixedArrayType,
            _ => null,
        };

    /// <summary>Whether a call may keep the value it is given: a callee not known here, or one whose summary says so.</summary>
    static bool KeptBy(SilInstruction call, SilValue value, Dictionary<string, bool[]> summaries) =>
        call switch {
            ApplyInst apply => ReferenceEquals(apply.Callee, value) || PromoteToStack.ArgumentEscapes(apply.Callee, [..apply.Arguments], value, summaries),
            TryApplyInst apply => ReferenceEquals(apply.Callee, value) || PromoteToStack.ArgumentEscapes(apply.Callee, [..apply.Arguments], value, summaries),
            _ => true,
        };

    /// <summary>
    ///     Every instruction that uses a value holding the view, when none of
    ///     them lets it outlive the function; null when one may. With
    ///     <paramref name="change" />, the view is a place's, which the
    ///     builtins of a slice's place may change where it is.
    /// </summary>
    static HashSet<SilInstruction>? Holders(BuiltinInst view, Uses uses, Dictionary<string, bool[]> summaries, bool change) {
        var used = new HashSet<SilInstruction>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<SilValue>(ReferenceEqualityComparer.Instance) { view };
        var pending = new Stack<SilValue>([view]);
        void Hold(SilValue value) {
            if (seen.Add(value)) {
                pending.Push(value);
            }
        }

        while (pending.TryPop(out var holder)) {
            foreach (var user in uses.Of(holder)) {
                used.Add(user);
                switch (user) {
                    case DestroyValueInst or EndBorrowInst or DestroyAddrInst or DeallocStackInst:
                        break;

                    case CopyValueInst or LoadBorrowInst:
                        Hold(user);
                        break;

                    case LoadInst load when holder is AllocStackInst:
                        Hold(load);
                        break;

                    // Into a local slot, which then holds it; over what a slot held, nothing of it.
                    case StoreInst store when ReferenceEquals(store.Value, holder):
                        if (store.Address is not AllocStackInst slot) {
                            return null;
                        }

                        Hold(slot);
                        break;

                    case StoreInst when holder is AllocStackInst:
                        break;

                    case ApplyInst or TryApplyInst when !KeptBy(user, holder, summaries):
                        break;

                    case BranchInst branch:
                        for (var k = 0; k < branch.Operands.Count; k++) {
                            if (ReferenceEquals(branch.Operands[k], holder)) {
                                Hold(branch.Target.Arguments[k]);
                            }
                        }

                        break;

                    case BuiltinInst { Name: "fixed_restore" } when change:
                        break;

                    // A slice of it, or an element borrowed from it, holds it too.
                    case BuiltinInst { Name: "array_slice" or "array_get_borrowed" } part when ReferenceEquals(part.Operands[0], holder) && !part.Type.IsTrivial:
                        Hold(part);
                        break;

                    // A slot's slice changed where it is (a place's view alone may be).
                    case BuiltinInst { Name: var name } inPlace when holder is AllocStackInst && ReferenceEquals(inPlace.Operands[0], holder)
                                                                    && inPlace.Operands.Skip(1).All(o => !ReferenceEquals(o, holder))
                                                                    && (name is "array_count" or "array_get" or "array_get_borrowed" or "array_make_unique"
                                                                        || change && name is "array_set" or "array_set_unique" or "array_element_addr" or "array_element_addr_unique"
                                                                            or "array_rotate" or "array_store_slice"
                                                                        || change && name.StartsWith("bytes_store_", StringComparison.Ordinal)
                                                                        || change && SimdBuiltins.Is(name) && SimdBuiltins.Op(name) is "store" or "store_unique"):
                        if (inPlace.Name == "array_get_borrowed" && !inPlace.Type.IsTrivial) {
                            Hold(inPlace);
                        }

                        break;

                    case BuiltinInst reader when !Uses.Consumes(reader, holder) && holder is not AllocStackInst && PromoteToStack.BorrowsOnly(reader, holder):
                        break;

                    default:
                        return null;
                }
            }
        }

        // In a loop, a header made each time round is the same storage: no
        // holder may carry one round to the next.
        if (!change && InLoop(view.Block) && seen.Any(static h => h is SilArgument or AllocStackInst)) {
            return null;
        }

        return used;
    }

    static bool InLoop(SilBlock block) {
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>(block.Successors);
        while (pending.TryPop(out var next)) {
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

    /// <summary>
    ///     Whether what the view is of lives through every use of what holds
    ///     it: along every path from the view, none of those uses comes after
    ///     one of its keepers ends — a borrow's end, a consuming use.
    /// </summary>
    static bool Alive(BuiltinInst view, HashSet<SilInstruction> used, List<SilValue> keepers) {
        bool Ends(SilInstruction instruction) =>
            keepers.Exists(keeper => instruction is EndBorrowInst end ? ReferenceEquals(end.Borrowed, keeper) : instruction.Operands.Contains(keeper) && Uses.Consumes(instruction, keeper));

        // Each block, with whether a keeper has ended on the way into it.
        var seen = new HashSet<(SilBlock, bool)>();
        var pending = new Stack<(SilBlock Block, int Start, bool Ended)>();
        pending.Push((view.Block, view.Block.Instructions.IndexOf(view) + 1, false));
        while (pending.TryPop(out var next)) {
            var ended = next.Ended;
            var through = true;
            for (var i = next.Start; i < next.Block.Instructions.Count; i++) {
                var instruction = next.Block.Instructions[i];
                // Back round to the view: a new one, which is checked as this is.
                if (ReferenceEquals(instruction, view)) {
                    through = false;
                    break;
                }

                if (ended && used.Contains(instruction)) {
                    return false;
                }

                ended |= Ends(instruction);
            }

            if (!through) {
                continue;
            }

            foreach (var successor in next.Block.Successors) {
                if (seen.Add((successor, ended))) {
                    pending.Push((successor, 0, ended));
                }
            }
        }

        return true;
    }
}
