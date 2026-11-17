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

            if (!fits || store?.Value is not CopyValueInst copy || uses.Of(copy).Count != 1 || !FieldOfParameter(copy.Value, function)) {
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

    /// <summary>A class reference, or an optional one: one pointer, kept alive by whatever holds it.</summary>
    static bool Referential(SilType type) =>
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
    static Dictionary<string, bool> Summarise(SilModule module) {
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
            _ => false,
        };

    static bool MayWrite(SilFunction function, Dictionary<string, bool> writes, AllocStackInst? ignoring) {
        if (function.Blocks.Count == 0) {
            return true; // declared, not defined: anything
        }

        var module = function.Module!;
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                switch (instruction) {
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

                    case BuiltinInst builtin when builtin.Operands.Any(static o => o.Type is AddressType && !LocalSlot(o)):
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
        }

        return false;
    }
}
