namespace Sable.SIL;

/// <summary>
///     docs/01's promise — "every reference the checker proves is
///     non-escaping is a retain/release pair the optimiser deletes" — for
///     the commonest case left: <c>if let left = node.left</c>, where the
///     binding is a copy of a field of a borrowed parameter, kept in a slot.
/// </summary>
/// <remarks>
///     The copy is only needed if the field could change, or its object go,
///     while the binding lives. The object is a borrowed parameter, which
///     the caller keeps; so it is enough that nothing the function does can
///     write any object's field — no store outside its own slots, no call to
///     a function that might (a summary per function, to a fixed point), no
///     release that could run a deinit. Then the slot holds the reference
///     unretained: <c>alloc_stack [unmanaged]</c>, stored without a copy,
///     never destroyed. A retain and a release per binding are gone.
/// </remarks>
public sealed class BorrowFields : ISilPass {
    public string Name => "borrow-fields";

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<SilModule, Dictionary<string, bool>> _writes = new();

    public void Run(SilFunction function) {
        if (function.Module is not { } module || function.Blocks.Count == 0) {
            return;
        }

        var writes = _writes.GetValue(module, Summarise);
        if (MayWrite(function, writes, ignoring: null)) {
            return;
        }

        var uses = new Uses(function);
        foreach (var slot in function.Entry.Instructions.OfType<AllocStackInst>().ToList()) {
            if (slot.Unmanaged || !Referential(slot.ElementType)) {
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

            if (!fits || store?.Value is not CopyValueInst copy || uses.Of(copy).Count != 1 || !FieldOfParameter(copy.Value, function)
                || EndsBeforeStore(copy.Value, store, uses) is not { } ends) {
                continue;
            }

            EndAfter(ends, store);
            slot.Unmanaged = true;
            store.SetOperand(0, copy.Value);
            copy.Block.Instructions.Remove(copy);
            foreach (var destroy in uses.Of(slot).OfType<DestroyAddrInst>().ToList()) {
                destroy.Block.Instructions.Remove(destroy);
            }
        }
    }

    /// <summary>
    ///     Whether the borrows a copied value comes from can cover the store
    ///     that will now take the value itself: each <c>load_borrow</c> it is
    ///     borrowed from, through payloads, fields and casts, either ends
    ///     after the store, or ends in the store's block before it — where the
    ///     copy outlived the borrow, as the value now will not — and is then
    ///     to be moved after the store (the ends given); no end on a way to
    ///     the store from another block. Null when that cannot be so.
    /// </summary>
    internal static List<EndBorrowInst>? EndsBeforeStore(SilValue value, StoreInst store, Uses uses) {
        var block = store.Block;
        var at = block.Instructions.IndexOf(store);
        var early = new List<EndBorrowInst>();
        for (var depth = 0; depth < 16; depth++) {
            switch (value) {
                case SilArgument argument when argument.Block == argument.Block.Function.Entry:
                    return early;
                case SilArgument { IsGuaranteed: true } payload:
                    if (payload.Block.Function.Blocks.Select(static b => b.Terminator).OfType<SwitchEnumInst>()
                            .Where(s => s.Cases.Any(c => c.Target == payload.Block)).ToList() is not [var switched]) {
                        return null;
                    }

                    value = switched.Value;
                    continue;
                case StructExtractInst or TupleExtractInst or UpcastInst or UncheckedRefCastInst:
                    value = ((SilInstruction)value).Operands[0];
                    continue;
                case LoadBorrowInst borrow:
                    foreach (var end in uses.Of(borrow).OfType<EndBorrowInst>()) {
                        if (end.Block == block) {
                            if (block.Instructions.IndexOf(end) < at) {
                                early.Add(end);
                            }
                        } else if (Reaches(end.Block, block)) {
                            return null;
                        }
                    }

                    // What the slot borrows from stays alive: a lent parameter's field, another slot.
                    return early;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>Moves borrows' ends that came before a store to just after it.</summary>
    internal static void EndAfter(List<EndBorrowInst> ends, StoreInst store) {
        foreach (var end in ends) {
            end.Block.Instructions.Remove(end);
            store.Block.Instructions.Insert(store.Block.Instructions.IndexOf(store) + 1, end);
        }
    }

    /// <summary>True when <paramref name="to" /> can be reached from <paramref name="from" />.</summary>
    static bool Reaches(SilBlock from, SilBlock to) {
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>([from]);
        while (pending.TryPop(out var next)) {
            if (next == to) {
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

    /// <summary>A class reference, or an optional one: one pointer, kept alive by whatever holds it.</summary>
    internal static bool Referential(SilType type) =>
        type is ClassType || type is EnumType { IsNoncopyable: false } optional && optional.Cases.All(static c => c.Payload is null or ClassType);

    /// <summary>A value borrowed from a field of an object the caller lends: kept alive by it while the field is unchanged.</summary>
    static bool FieldOfParameter(SilValue value, SilFunction function) =>
        value switch {
            LoadBorrowInst { Address: RefElementAddrInst { Reference: SilArgument owner } } =>
                owner.Block == function.Entry && !function.Type.IsOwned(function.Entry.Arguments.IndexOf(owner)),
            SilArgument { IsGuaranteed: true } payload => payload.Block.Function.Blocks
                .Select(static b => b.Terminator)
                .OfType<SwitchEnumInst>()
                .Where(s => s.Cases.Any(c => c.Target == payload.Block) && payload.Block.Arguments.Count == 1)
                .Select(static s => s.Value)
                .ToList() is [var switched] && FieldOfParameter(switched, function),
            _ => false,
        };

    /// <summary>For every function, whether it may write an object's field.</summary>
    internal static Dictionary<string, bool> Summarise(SilModule module) {
        var writes = module.Functions.ToDictionary(static f => f.Name, static _ => false);
        for (var changed = true; changed;) {
            changed = false;
            foreach (var function in module.Functions) {
                if (!writes[function.Name] && MayWrite(function, writes, ignoring: null)) {
                    writes[function.Name] = true;
                    changed = true;
                }
            }
        }

        return writes;
    }

    static bool LocalSlot(SilValue address) =>
        address switch {
            AllocStackInst => true,
            StructElementAddrInst element => LocalSlot(element.Address),
            TupleElementAddrInst element => LocalSlot(element.Address),
            EnumPayloadAddrInst element => LocalSlot(element.Address),
            _ => false,
        };

    /// <summary>
    ///     True when the function may write an object's field or run a deinit:
    ///     a store outside its own slots, a release of what could have one, a
    ///     call to a function that may. The stores to and destroys of the
    ///     slots <paramref name="ignoring" /> names are left out: slots that
    ///     will hold their objects unretained, so that neither releases.
    /// </summary>
    internal static bool MayWrite(SilFunction function, Dictionary<string, bool> writes, IReadOnlySet<AllocStackInst>? ignoring) {
        if (function.Blocks.Count == 0) {
            return true; // declared, not defined: anything
        }

        return MayWrite(function.Module!, function.Blocks.SelectMany(static b => b.Instructions), writes, ignoring);
    }

    /// <summary>The same, of some of a function's instructions: a loop's body.</summary>
    internal static bool MayWrite(SilModule module, IEnumerable<SilInstruction> instructions, Dictionary<string, bool> writes, IReadOnlySet<AllocStackInst>? ignoring) {
        foreach (var instruction in instructions) {
            switch (instruction) {
                case StoreInst or DestroyAddrInst when ignoring is not null && instruction.Operands[^1] is AllocStackInst slot && ignoring.Contains(slot):
                    break;

                // Moving a value out of a field leaves the field without it.
                case LoadInst { Kind: LoadKind.Take } take when !LocalSlot(take.Address):
                case StoreInst store when !LocalSlot(store.Address):
                case StoreWeakInst or StoreUnownedInst:
                    return true;

                case DestroyValueInst destroy when module.MayRunDeinit(destroy.Value.Type):
                case DestroyAddrInst destroyAddr when destroyAddr.Address is not AllocStackInst { Unmanaged: true }
                    && module.MayRunDeinit(((AddressType)destroyAddr.Address.Type).Pointee):
                    return true;

                // An assignment into a slot releases what was there.
                case StoreInst { Kind: StoreKind.Assign } assigned when module.MayRunDeinit(assigned.Value.Type):
                    return true;

                // An exclusivity marker or check writes nothing.
                case BuiltinInst builtin when SharedStorage.IsMarker(builtin.Name):
                    break;

                case BuiltinInst builtin when SilBuiltins.RunsWork(builtin.Name) || builtin.Operands.Any(static o => o.Type is AddressType && !LocalSlot(o)):
                    return true;

                case BuiltinInst builtin when SilVerifier.Consumed(builtin).Any(i => module.MayRunDeinit(builtin.Operands[i].Type)):
                    return true;

                case ApplyInst { Callee: var callee }:
                case TryApplyInst { Callee: var tryCallee } when (callee = tryCallee) is not null:
                    if (callee is not FunctionRefInst reference || writes.GetValueOrDefault(reference.Function, true)) {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }
}
