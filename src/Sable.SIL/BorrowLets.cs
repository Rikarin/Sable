namespace Sable.SIL;

/// <summary>
///     A <c>let</c> that copies what stays put while the binding lives —
///     <c>let label = node.name</c> in a loop over a borrowed node, or
///     <c>let x = parameter</c> — borrows it instead: the retain at the top
///     of each turn and the release at the bottom pair across the turn and
///     go (docs/02 §2.2's loop-wide ARC motion).
/// </summary>
/// <remarks>
///     <para>
///         The binding's slot is stored once, with a copy loaded from storage
///         or of a borrowed parameter, and otherwise only read and destroyed.
///         A parameter the caller lends outlives the call, and nothing the
///         function does can release it. Storage must stay as it is wherever
///         the slot may hold the copy — followed from the store along every
///         path to the slot's destroys, a loop's turn however many blocks
///         long: for a stack slot or an inout parameter, nothing is given its
///         address but to read it; for a field of an object a parameter
///         lends, nothing calls out, writes outside the function's own slots,
///         or releases what could run a deinit — the rule
///         <see cref="BorrowLoads" /> holds a borrow to. A copy that publishes
///         to another task is a copy made from the binding, not the binding's
///         own, and is untouched.
///     </para>
///     <para>
///         Then the slot holds the value unretained, as
///         <see cref="BorrowFields" /> leaves one: <c>alloc_stack [unmanaged]</c>,
///         stored with a borrow, never destroyed.
///     </para>
/// </remarks>
public sealed class BorrowLets : ISilPass {
    public string Name => "borrow-lets";

    public void Run(SilFunction function) {
        if (function.Blocks.Count == 0) {
            return;
        }

        var uses = new Uses(function);
        foreach (var slot in function.Blocks.SelectMany(static b => b.Instructions).OfType<AllocStackInst>().ToList()) {
            if (slot.Unmanaged || !OnePointer(slot.ElementType)) {
                continue;
            }

            StoreInst? store = null;
            var fits = true;
            foreach (var user in uses.Of(slot)) {
                switch (user) {
                    case StoreInst stored when ReferenceEquals(stored.Address, slot) && store is null:
                        store = stored;
                        break;
                    case LoadBorrowInst or LoadInst { Kind: LoadKind.Copy } or DestroyAddrInst or DeallocStackInst:
                        break;
                    default:
                        fits = false;
                        break;
                }
            }

            if (!fits || store is null || uses.Of(store.Value).Count != 1 || store.Value is not SilInstruction copied || copied.Block != store.Block) {
                continue;
            }

            switch (copied) {
                // A parameter the caller lends, or part of one: alive, and the
                // same, for the whole call.
                case CopyValueInst { Value: var parameter } copy
                    when SilVerifier.OwnershipOf(parameter) == Ownership.Guaranteed && ForwardCopies.IsParameterBorrow(parameter):
                    Unmanage(slot, uses);
                    store.SetOperand(0, parameter);
                    copy.Block.Instructions.Remove(copy);
                    break;

                case LoadInst { Kind: LoadKind.Copy } load when Steady(load, store, slot, function):
                    Unmanage(slot, uses);
                    var borrow = new LoadBorrowInst(load.Address) { Block = load.Block, Location = load.Location };
                    var instructions = load.Block.Instructions;
                    instructions[instructions.IndexOf(load)] = borrow;
                    store.SetOperand(0, borrow);
                    instructions.Insert(instructions.IndexOf(store) + 1, new EndBorrowInst(borrow) { Block = load.Block, Location = store.Location });
                    break;
            }
        }
    }

    /// <summary>One reference, which whatever holds it keeps alive: an object, an optional one, a string or an array.</summary>
    static bool OnePointer(SilType type) => BorrowFields.Referential(type) || type == SilType.String || type is ArrayType;

    static void Unmanage(AllocStackInst slot, Uses uses) {
        slot.Unmanaged = true;
        foreach (var destroy in uses.Of(slot).OfType<DestroyAddrInst>().ToList()) {
            destroy.Block.Instructions.Remove(destroy);
        }
    }

    /// <summary>
    ///     True when the storage a copy is loaded from is not changed, nor
    ///     its object released, between the load and the store and wherever
    ///     after the slot may hold the copy. The storage is a stack slot, an
    ///     inout parameter, or a field of an object a parameter lends.
    /// </summary>
    static bool Steady(LoadInst load, StoreInst store, AllocStackInst slot, SilFunction function) {
        var exclusive = BorrowLoads.ExclusiveOf(load.Address);
        if (exclusive is null && !OfParameter(load.Address, function)) {
            return false;
        }

        // A 'let' field of what the caller lends never changes.
        if (BorrowHeld.LetField(load.Address) is not null) {
            return true;
        }

        // A branch changes nothing; a call that can throw is a call.
        bool Changes(SilInstruction instruction) =>
            instruction switch {
                DestroyAddrInst { Address: var destroyed } when ReferenceEquals(destroyed, slot) => false,
                TryApplyInst => true,
                { IsTerminator: true } => false,
                _ => BorrowLoads.MayWrite(instruction, exclusive, null),
            };

        var block = store.Block;
        for (var i = block.Instructions.IndexOf(load) + 1; i < block.Instructions.IndexOf(store); i++) {
            if (Changes(block.Instructions[i])) {
                return false;
            }
        }

        // Forward from the store: where the slot may hold the copy.
        var holdingIn = new HashSet<SilBlock>();
        var pending = new Queue<SilBlock>();
        bool Walk(SilBlock from, int start) {
            var holding = true;
            for (var i = start; i < from.Instructions.Count && holding; i++) {
                var instruction = from.Instructions[i];
                if (instruction is DestroyAddrInst { Address: var destroyed } && ReferenceEquals(destroyed, slot)) {
                    holding = false;
                } else if (instruction == store || Changes(instruction)) {
                    return false;
                }
            }

            if (holding) {
                foreach (var successor in from.Successors) {
                    if (holdingIn.Add(successor)) {
                        pending.Enqueue(successor);
                    }
                }
            }

            return true;
        }

        if (!Walk(block, block.Instructions.IndexOf(store) + 1)) {
            return false;
        }

        while (pending.TryDequeue(out var next)) {
            if (!Walk(next, 0)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>A field of an object a parameter lends, reached through fields and casts.</summary>
    static bool OfParameter(SilValue address, SilFunction function) =>
        address switch {
            RefElementAddrInst field => Lent(field.Reference, function),
            StructElementAddrInst element => OfParameter(element.Address, function),
            TupleElementAddrInst element => OfParameter(element.Address, function),
            _ => false,
        };

    static bool Lent(SilValue reference, SilFunction function) =>
        reference switch {
            SilArgument argument => argument.Block == function.Entry && SilVerifier.OwnershipOf(argument) == Ownership.Guaranteed,
            UpcastInst upcast => Lent(upcast.Value, function),
            UncheckedRefCastInst cast => Lent(cast.Value, function),
            _ => false,
        };
}
