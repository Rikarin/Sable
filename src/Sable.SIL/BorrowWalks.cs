namespace Sable.SIL;

/// <summary>
///     Loop ARC for a read-only walk: a local that only ever holds objects
///     reachable from what the caller lends — <c>var p = list</c>, then
///     <c>p = n.next</c> each time round — holds them unretained.
/// </summary>
/// <remarks>
///     <para>
///         A walk retains each object it steps onto and releases the one it
///         steps off, though nothing it does could free either: every object
///         it visits is held by a field of the one before, back to a borrowed
///         parameter the caller keeps. So long as no field anywhere changes
///         and no deinit runs while the function does — the same whole
///         function rule as <see cref="BorrowFields" />, apart from the walking
///         locals' own stores, which stop releasing — that chain holds.
///     </para>
///     <para>
///         A local qualifies when every value stored to it is "rooted": a
///         copy of a borrowed parameter, a copy of a field of a rooted object,
///         a payload or projection of a rooted value, a borrow of another
///         qualifying local, or an empty case such as <c>none</c>. The set is
///         worked out optimistically — every candidate assumed rooted, those
///         with a store that is not dropped, until none change — since a walk
///         reads its own local to find the next object. Each one then becomes
///         <c>alloc_stack [unmanaged]</c>: stored without a copy (a field's
///         copy becomes a borrow), never destroyed. A list walk does no ARC at
///         all; before, it retained and released every node.
///     </para>
/// </remarks>
public sealed class BorrowWalks : ISilPass {
    public string Name => "borrow-walks";

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<SilModule, Dictionary<string, bool>> _writes = new();

    public void Run(SilFunction function) {
        if (function.Module is not { } module || function.Blocks.Count == 0) {
            return;
        }

        var uses = new Uses(function);
        var slots = function.Blocks.SelectMany(static b => b.Instructions)
            .OfType<AllocStackInst>()
            .Where(s => BorrowFields.Referential(s.ElementType) && Walkable(s, uses))
            .ToHashSet();
        if (slots.Count == 0) {
            return;
        }

        // Drop each slot a store of which is not rooted, until none is dropped.
        for (var changed = true; changed;) {
            changed = false;
            foreach (var slot in slots.ToList()) {
                if (!uses.Of(slot).OfType<StoreInst>().All(s => Stored(s, slots, uses, function))) {
                    slots.Remove(slot);
                    changed = true;
                }
            }
        }

        // Only those still owning their objects change; if none does, nothing to gain.
        if (!slots.Any(static s => !s.Unmanaged)) {
            return;
        }

        var writes = _writes.GetValue(module, BorrowFields.Summarise);
        if (BorrowFields.MayWrite(function, writes, ignoring: slots)) {
            return;
        }

        foreach (var slot in slots.Where(static s => !s.Unmanaged)) {
            slot.Unmanaged = true;
            foreach (var user in uses.Of(slot).ToList()) {
                switch (user) {
                    case StoreInst { Value: CopyValueInst copy } store:
                        store.SetOperand(0, copy.Value);
                        copy.Block.Instructions.Remove(copy);
                        break;

                    // A field's copy becomes a borrow, ended once it is stored.
                    case StoreInst { Value: LoadInst { Kind: LoadKind.Copy } load } store:
                        var borrow = new LoadBorrowInst(load.Address) { Block = load.Block, Location = load.Location };
                        load.Block.Instructions[load.Block.Instructions.IndexOf(load)] = borrow;
                        store.SetOperand(0, borrow);
                        store.Block.Instructions.Insert(
                            store.Block.Instructions.IndexOf(store) + 1,
                            new EndBorrowInst(borrow) { Block = store.Block, Location = store.Location }
                        );
                        break;

                    case DestroyAddrInst destroy:
                        destroy.Block.Instructions.Remove(destroy);
                        break;
                }
            }
        }
    }

    /// <summary>
    ///     A slot only the function's own loads, stores and destroys touch,
    ///     never moved out of: so its stores are all that put a value in it,
    ///     and nothing takes ownership of what it holds.
    /// </summary>
    static bool Walkable(AllocStackInst slot, Uses uses) =>
        uses.Of(slot).All(user => user switch {
            LoadInst load => load.Kind != LoadKind.Take,
            LoadBorrowInst or DestroyAddrInst or DeallocStackInst => true,
            StoreInst store => ReferenceEquals(store.Address, slot) && !ReferenceEquals(store.Value, slot),
            _ => false,
        });

    /// <summary>
    ///     True when the store puts a rooted value in the slot in a form this
    ///     pass can stop owning: the value itself for a slot already
    ///     unmanaged; else a copy, or a field's copy, used for nothing else.
    /// </summary>
    static bool Stored(StoreInst store, HashSet<AllocStackInst> slots, Uses uses, SilFunction function) {
        var slot = (AllocStackInst)store.Address;
        return store.Value switch {
            // An empty case holds no object.
            EnumInst { Payload: null } empty => uses.Of(empty).Count == 1,
            _ when slot.Unmanaged => Rooted(store.Value, slots, function),
            CopyValueInst copy => uses.Of(copy).Count == 1 && copy.Block == store.Block && Rooted(copy.Value, slots, function),
            LoadInst { Kind: LoadKind.Copy, Address: RefElementAddrInst field } load =>
                uses.Of(load).Count == 1 && load.Block == store.Block && Rooted(field.Reference, slots, function),
            _ => false,
        };
    }

    /// <summary>A value kept alive, for the whole call, by a chain of fields from a borrowed parameter.</summary>
    static bool Rooted(SilValue value, HashSet<AllocStackInst> slots, SilFunction function) =>
        value switch {
            SilArgument argument when argument.Block == function.Entry =>
                !function.Type.IsOwned(function.Entry.Arguments.IndexOf(argument)) && argument.Type is not AddressType,
            SilArgument { IsGuaranteed: true } payload => payload.Block.Arguments.Count == 1 && function.Blocks
                .Select(static b => b.Terminator)
                .OfType<SwitchEnumInst>()
                .Where(s => s.Cases.Any(c => c.Target == payload.Block))
                .Select(static s => s.Value)
                .ToList() is [var switched] && Rooted(switched, slots, function),
            LoadBorrowInst { Address: AllocStackInst slot } => slots.Contains(slot),
            LoadBorrowInst { Address: RefElementAddrInst field } => Rooted(field.Reference, slots, function),
            StructExtractInst extract => Rooted(extract.Operands[0], slots, function),
            TupleExtractInst extract => Rooted(extract.Operands[0], slots, function),
            UpcastInst or UncheckedRefCastInst => Rooted(value is SilInstruction cast ? cast.Operands[0] : value, slots, function),
            _ => false,
        };
}
