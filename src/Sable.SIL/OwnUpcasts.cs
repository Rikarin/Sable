namespace Sable.SIL;

/// <summary>
///     A copy of an owned object seen as its superclass, the object itself
///     destroyed after, is the object taken as the superclass:
///     <c>copy_value (upcast %0)</c> … <c>destroy_value %0</c> becomes
///     <c>upcast [owned] %0</c> — a retain and a release fewer. An object put
///     into an array of its base class, or boxed into an existential, just
///     after it is made, is one.
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
                        upcast.Block.Instructions.Remove(upcast);
                    }
                }
            }
        }
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
