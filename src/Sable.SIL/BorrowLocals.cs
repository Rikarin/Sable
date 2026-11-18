namespace Sable.SIL;

/// <summary>
///     A binding that copies what another local holds — <c>if let n = p</c>,
///     <c>while let n = p</c> — borrows it instead, for as long as that local
///     is not changed: a retain and a release fewer per binding.
/// </summary>
/// <remarks>
///     <para>
///         The binding's slot holds a copy of the payload of a borrow of the
///         other slot. The copy is only needed if the binding is read after
///         the other slot is written — assigned, taken from, destroyed —
///         since until then the other slot keeps the object alive. The other
///         slot must be the function's own (its address used only to load,
///         store and destroy it), so that only those instructions change it;
///         and every read of the binding must be reached from the binding's
///         store along no path that writes it.
///     </para>
///     <para>
///         Then the slot holds the reference unretained, as
///         <see cref="BorrowFields" /> leaves one: <c>alloc_stack [unmanaged]</c>,
///         stored without the copy, never destroyed. A list walk —
///         <c>while let n = p { …; p = n.next }</c> — reads <c>n</c> only
///         before <c>p</c> moves on, so it retains a node once, not twice.
///     </para>
/// </remarks>
public sealed class BorrowLocals : ISilPass {
    public string Name => "borrow-locals";

    public void Run(SilFunction function) {
        if (function.Blocks.Count == 0) {
            return;
        }

        var uses = new Uses(function);
        foreach (var slot in function.Blocks.SelectMany(static b => b.Instructions).OfType<AllocStackInst>().ToList()) {
            if (slot.Unmanaged || !BorrowFields.Referential(slot.ElementType)) {
                continue;
            }

            StoreInst? store = null;
            var fits = true;
            foreach (var user in uses.Of(slot)) {
                switch (user) {
                    case StoreInst stored when ReferenceEquals(stored.Address, slot) && store is null:
                        store = stored;
                        break;
                    case LoadBorrowInst or DestroyAddrInst or DeallocStackInst:
                        break;
                    default:
                        fits = false;
                        break;
                }
            }

            if (!fits || store?.Value is not CopyValueInst copy || uses.Of(copy).Count != 1 || copy.Block != store.Block
                || BorrowedSlot(copy.Value) is not { } owner || owner == slot || !Local(owner, uses)
                || !Unchanged(function, slot, store, copy, owner)) {
                continue;
            }

            slot.Unmanaged = true;
            store.SetOperand(0, copy.Value);
            copy.Block.Instructions.Remove(copy);
            foreach (var destroy in uses.Of(slot).OfType<DestroyAddrInst>().ToList()) {
                destroy.Block.Instructions.Remove(destroy);
            }
        }
    }

    /// <summary>The slot a value is borrowed from: a <c>load_borrow</c> of it, or the payload a switch on one hands its case.</summary>
    static AllocStackInst? BorrowedSlot(SilValue value) =>
        value switch {
            LoadBorrowInst { Address: AllocStackInst slot } => slot,
            SilArgument { IsGuaranteed: true } payload when payload.Block.Arguments.Count == 1 => payload.Block.Function.Blocks
                .Select(static b => b.Terminator)
                .OfType<SwitchEnumInst>()
                .Where(s => s.Cases.Any(c => c.Target == payload.Block))
                .Select(static s => s.Value)
                .ToList() is [LoadBorrowInst { Address: AllocStackInst slot }] ? slot : null,
            _ => null,
        };

    /// <summary>A slot only the function's own instructions read and write: nothing else is given its address.</summary>
    static bool Local(AllocStackInst slot, Uses uses) =>
        uses.Of(slot).All(user => user switch {
            LoadInst or LoadBorrowInst or DestroyAddrInst or DeallocStackInst => true,
            StoreInst store => ReferenceEquals(store.Address, slot) && !ReferenceEquals(store.Value, slot),
            _ => false,
        });

    static bool Writes(SilInstruction instruction, AllocStackInst owner) =>
        instruction switch {
            StoreInst store => ReferenceEquals(store.Address, owner),
            DestroyAddrInst destroy => ReferenceEquals(destroy.Address, owner),
            LoadInst { Kind: LoadKind.Take } load => ReferenceEquals(load.Address, owner),
            _ => false,
        };

    /// <summary>
    ///     True when the owner is not written between the copy and the store,
    ///     nor on any path from the store to a read of the binding. A forward
    ///     analysis of "the owner may have changed since the binding was
    ///     stored", true at the entry, where nothing is stored yet.
    /// </summary>
    static bool Unchanged(SilFunction function, AllocStackInst slot, StoreInst store, CopyValueInst copy, AllocStackInst owner) {
        var instructions = store.Block.Instructions;
        for (var i = instructions.IndexOf(copy) + 1; i < instructions.IndexOf(store); i++) {
            if (Writes(instructions[i], owner)) {
                return false;
            }
        }

        var order = SilVerifier.ReversePostorder(function);
        var predecessors = order.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in order) {
            foreach (var successor in block.Successors) {
                predecessors[successor].Add(block);
            }
        }

        // Grows from false to a fixed point; then each read is checked.
        var failed = false;
        var changedOut = order.ToDictionary(static b => b, static _ => false);
        for (var again = true; again;) {
            again = false;
            foreach (var block in order) {
                var changed = Walk(block, check: false);
                if (changed != changedOut[block]) {
                    changedOut[block] = changed;
                    again = true;
                }
            }
        }

        foreach (var block in order) {
            Walk(block, check: true);
        }

        return !failed;

        bool Walk(SilBlock block, bool check) {
            var changed = block == function.Entry || predecessors[block].Any(p => changedOut[p]);
            foreach (var instruction in block.Instructions) {
                if (instruction == store) {
                    changed = false;
                } else if (Writes(instruction, owner)) {
                    changed = true;
                } else if (check && changed && instruction is LoadBorrowInst { Address: var address } && ReferenceEquals(address, slot)) {
                    failed = true;
                }
            }

            return changed;
        }
    }
}
