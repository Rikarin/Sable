namespace Sable.SIL;

/// <summary>
///     A copy of an owned object seen as its superclass, the object itself
///     destroyed after, is the object taken as the superclass:
///     <c>copy_value (upcast %0)</c> … <c>destroy_value %0</c> becomes
///     <c>upcast [owned] %0</c> — a retain and a release fewer. An object put
///     into an array of its base class, or boxed into an existential, just
///     after it is made, is one. So is the one field of an owned struct with
///     anything to destroy, copied or viewed whole and the struct destroyed:
///     it is taken out (<c>struct_extract [take]</c>), viewed by
///     <c>slice_of_take</c> — a digest's bytes kept from the digest an
///     inlined call gave.
/// </summary>
/// <remarks>
///     The destroy must be in the copy's block, after it, and nothing between
///     may use the object or anything borrowed from it. The pass runs last
///     of the ARC passes: those before see only the borrowing upcast.
/// </remarks>
public sealed class OwnUpcasts : ISilPass {
    public string Name => "own-upcasts";

    public void Run(SilFunction function) {
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is CopyValueInst { Value: UpcastInst { Value: var source } upcast } copy
                    && SilVerifier.OwnershipOf(source) == Ownership.Owned
                    && DestroyAfter(block, i, source) is { } destroy) {
                    var owned = new OwnedUpcastInst(source, (ClassType)upcast.Type) { Block = block, Location = copy.Location };
                    block.Instructions[i] = owned;
                    Uses.Replace(function, copy, owned);
                    block.Instructions.Remove(destroy);
                    if (!function.Blocks.Any(b => b.Instructions.Any(u => u.Operands.Contains(upcast)))) {
                        i -= Drop(upcast, block, i);
                    }

                    continue;
                }

                // The one field of an owned struct with anything to destroy,
                // copied — or viewed whole, which retains its buffer — and the
                // struct destroyed: the field taken out of it.
                var (extract, made) = block.Instructions[i] switch {
                    CopyValueInst { Value: StructExtractInst found } => (found, (Func<StructTakeInst, SilInstruction>?)(static taken => taken)),
                    BuiltinInst { Name: "slice_of", Operands: [StructExtractInst found] } slice =>
                        (found, taken => new BuiltinInst("slice_of_take", slice.Type, taken) { Location = slice.Location }),
                    _ => default,
                };
                if (extract is { Value: var whole } && made is not null
                    && SilVerifier.OwnershipOf(whole) == Ownership.Owned && StructTakeInst.CanTake(whole.Type, extract.Field)
                    && DestroyAfter(block, i, whole) is { } structDestroy) {
                    var replaced = block.Instructions[i];
                    var take = new StructTakeInst(whole, extract.Field) { Block = block, Location = replaced.Location };
                    block.Instructions[i] = take;
                    if (made(take) is var result && !ReferenceEquals(result, take)) {
                        result.Block = block;
                        block.Instructions.Insert(++i, result);
                    }

                    Uses.Replace(function, replaced, block.Instructions[i]);
                    block.Instructions.Remove(structDestroy);
                    if (!function.Blocks.Any(b => b.Instructions.Any(u => u.Operands.Contains(extract)))) {
                        i -= Drop(extract, block, i);
                    }
                }
            }
        }
    }

    /// <summary>Removes an instruction nothing uses any more; 1 when it came before <paramref name="at" /> in the block, which moves the instructions after it back.</summary>
    static int Drop(SilInstruction unused, SilBlock block, int at) {
        var index = unused.Block.Instructions.IndexOf(unused);
        unused.Block.Instructions.RemoveAt(index);
        return unused.Block == block && index < at ? 1 : 0;
    }

    /// <summary>The source's destroy after the copy at <paramref name="at" />, with no use between of it or of anything borrowed from it; and no use of it anywhere else after.</summary>
    static DestroyValueInst? DestroyAfter(SilBlock block, int at, SilValue source) {
        for (var k = at + 1; k < block.Instructions.Count; k++) {
            var instruction = block.Instructions[k];
            if (instruction is DestroyValueInst { Uncounted: false } destroy && ReferenceEquals(destroy.Value, source)) {
                return destroy;
            }

            if (instruction.Operands.Any(o => DependsOn(o, source))) {
                return null;
            }
        }

        return null;
    }

    /// <summary>True when the value is the source, or is borrowed from it through casts, fields and projections.</summary>
    static bool DependsOn(SilValue value, SilValue source) {
        for (var depth = 0; depth < 32; depth++) {
            if (ReferenceEquals(value, source)) {
                return true;
            }

            if (value is not SilInstruction { Operands.Count: > 0 } instruction || SilVerifier.OwnershipOf(value) == Ownership.Owned) {
                return false;
            }

            value = instruction.Operands[0];
        }

        return true;
    }
}
