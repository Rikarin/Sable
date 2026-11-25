namespace Sable.SIL;

/// <summary>
///     The first store to each field of an object just made initialises it:
///     there is no value there to destroy, and the object, which nothing else
///     has seen, is not shared with another task.
/// </summary>
/// <remarks>
///     <para>
///         An initialiser's stores are lowered as assignments, which share
///         the value if the object is shared (<see cref="AsyncLowering" />): it
///         cannot tell who else has the object. Once it is inlined after the
///         <c>alloc_ref</c> it can be told. From the allocation to the end of
///         its block, for as long as the object is used only for its fields'
///         addresses, a store is <see cref="StoreInst.IntoFresh" />, and a
///         field's first a <c>store [init]</c>.
///     </para>
///     <para>
///         A value already stored there may be read, and the rest of the
///         object still holds nothing (it is made empty), so what runs if the
///         object is dropped part-way is unchanged.
///     </para>
/// </remarks>
public sealed class InitFields : ISilPass {
    public string Name => "init-fields";

    public void Run(SilFunction function) {
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is AllocRefInst { ClassType.IsCoroutineFrame: false } alloc && !SilType.MentionsOpaque(alloc.ClassType)) {
                    Initialise(block, i, alloc);
                }
            }
        }
    }

    static void Initialise(SilBlock block, int made, AllocRefInst alloc) {
        // The object, seen as one of its superclasses too, and the fields
        // whose addresses have been taken.
        var objects = new HashSet<SilValue>(ReferenceEqualityComparer.Instance) { alloc };
        var addresses = new Dictionary<SilValue, int>(ReferenceEqualityComparer.Instance);
        var written = new HashSet<int>();
        for (var i = made + 1; i < block.Instructions.Count; i++) {
            var instruction = block.Instructions[i];
            switch (instruction) {
                case UpcastInst upcast when objects.Contains(upcast.Value):
                    objects.Add(upcast);
                    break;

                case RefElementAddrInst element when objects.Contains(element.Reference):
                    addresses[element] = element.Field;
                    break;

                case StoreInst store when addresses.TryGetValue(store.Address, out var field) && !objects.Contains(store.Value):
                    var kind = written.Add(field) ? StoreKind.Init : store.Kind;
                    block.Instructions[i] = new StoreInst(store.Value, store.Address, kind) { Block = block, Location = store.Location, IntoFresh = true };

                    break;

                case LoadInst { Kind: not LoadKind.Take } load when addresses.ContainsKey(load.Address):
                    break;

                default:
                    foreach (var operand in instruction.Operands) {
                        if (objects.Contains(operand)) {
                            return;
                        }

                        // Given to something that may write it.
                        if (addresses.TryGetValue(operand, out var given)) {
                            written.Add(given);
                        }
                    }

                    break;
            }
        }
    }
}
