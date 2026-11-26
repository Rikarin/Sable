namespace Sable.SIL;

/// <summary>
///     A view of a local array — not an inline one, which a view copies out —
///     (<c>slice_of</c> of a <c>load_borrow</c> of
///     its slot, the borrow ended at once) lasts, borrowed, as long as the
///     view does, where nothing writes the slot meanwhile: the borrow's end
///     sinks to the view's destroys, wherever they are, and the view is
///     <c>slice_of_borrowed</c> — no retain of the buffer, no release.
///     A loop that changes an array, then passes views of it to calls,
///     <c>dot(x, y)</c> each turn, makes one each turn.
/// </summary>
/// <remarks>
///     Every path from the view must reach one of its destroys without
///     coming back to the view, and on the way nothing may be given the
///     slot's address but to read it: a slot on the stack is written by
///     nothing else. An async function is left alone: what it holds across
///     a suspension its frame keeps.
/// </remarks>
public sealed class BorrowViews : ISilPass {
    public string Name => "borrow-views";

    public void Run(SilFunction function) {
        if (function.Type.IsAsync) {
            return;
        }

        Uses? uses = null;
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is not BuiltinInst { Name: "slice_of", Operands: [LoadBorrowInst { Address: AllocStackInst slot } borrow] } view
                    || view.Type.IsTrivial || borrow.Type is not ArrayType) {
                    continue;
                }

                uses ??= new Uses(function);
                if (Ends(function, block, i, view, borrow, slot, uses) is not var (end, destroys)) {
                    continue;
                }

                view.Rename("slice_of_borrowed");
                block.Instructions.Remove(end);
                foreach (var destroy in destroys) {
                    var at = destroy.Block.Instructions.IndexOf(destroy);
                    destroy.Block.Instructions[at] = new EndBorrowInst(borrow) { Block = destroy.Block, Location = destroy.Location };
                }

                uses = null;
            }
        }
    }

    /// <summary>The borrow's end, just after the view, and the view's destroys; null when the borrow cannot last to them.</summary>
    static (EndBorrowInst End, List<DestroyValueInst> Destroys)? Ends(SilFunction function, SilBlock block, int at, BuiltinInst view, LoadBorrowInst borrow, AllocStackInst slot, Uses uses) {
        // The borrow is used by the view alone after it, and ends in the view's block.
        EndBorrowInst? end = null;
        foreach (var user in uses.Of(borrow)) {
            if (user == view) {
                continue;
            }

            if (user is EndBorrowInst ended && ended.Block == block && end is null && block.Instructions.IndexOf(ended) > at) {
                end = ended;
                continue;
            }

            if (user.Block != block || block.Instructions.IndexOf(user) > at) {
                return null;
            }
        }

        if (end is null) {
            return null;
        }

        var destroys = new List<DestroyValueInst>();
        foreach (var user in uses.Of(view)) {
            if (user is DestroyValueInst { Uncounted: false } destroy) {
                destroys.Add(destroy);
            } else if (Uses.Consumes(user, view)) {
                return null;
            }
        }

        // A view gone in its own block is left as it is: around a call LLVM
        // then inlines, its retain and release cost less than what borrowing
        // changes in the inlined code (frustum-cull's culling loop, 2% slower).
        if (destroys.Count == 0 || destroys.TrueForAll(d => d.Block == block)) {
            return null;
        }

        // Every path on from the view, to a destroy: nothing on it writes the slot.
        var ends = destroys.ToHashSet<SilInstruction>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<(SilBlock Block, int From)>([(block, at + 1)]);
        while (pending.Count > 0) {
            var (current, from) = pending.Pop();
            var reached = false;
            for (var k = from; k < current.Instructions.Count; k++) {
                var instruction = current.Instructions[k];
                if (ends.Contains(instruction)) {
                    reached = true;
                    break;
                }

                if (instruction != end && Writes(instruction, slot)) {
                    return null;
                }

                if (instruction is ReturnInst or ThrowInst or UnreachableInst) {
                    return null;
                }
            }

            if (reached) {
                continue;
            }

            foreach (var successor in current.Successors) {
                if (successor == block) {
                    return null;
                }

                if (seen.Add(successor)) {
                    pending.Push((successor, 0));
                }
            }
        }

        // A destroy in the view's own block comes after the borrow's end, which it then takes the place of.
        return (end, destroys);
    }

    /// <summary>Whether the instruction may write the slot: given its address, or one inside it, other than to read.</summary>
    static bool Writes(SilInstruction instruction, AllocStackInst slot) =>
        instruction is not (LoadInst { Kind: not LoadKind.Take } or LoadBorrowInst or EndBorrowInst or StructElementAddrInst or TupleElementAddrInst)
        && instruction.Operands.Any(o => o.Type is AddressType && InSlot(o, slot));

    static bool InSlot(SilValue address, AllocStackInst slot) {
        for (var depth = 0; depth < 32; depth++) {
            switch (address) {
                case AllocStackInst found:
                    return ReferenceEquals(found, slot);
                case StructElementAddrInst element:
                    address = element.Address;
                    break;
                case TupleElementAddrInst element:
                    address = element.Address;
                    break;
                case EnumPayloadAddrInst element:
                    address = element.Address;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }
}
