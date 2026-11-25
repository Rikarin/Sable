namespace Sable.SIL;

/// <summary>
///     An array read through a view made only for that, read itself.
/// </summary>
/// <remarks>
///     A member of <c>Element[:]</c> called on an array — <c>bytes.readU32(at: 4)</c>
///     — is given a view of the array, which holds its buffer: a retain and a
///     release a call. Once the member is inlined and what is left of the view
///     is a few reads in one block, with only arithmetic between them, while
///     the array is borrowed, the reads are of the array, and there is no view.
/// </remarks>
public sealed class ReadInPlace : ISilPass {
    public string Name => "read-in-place";

    public void Run(SilFunction function) {
        Uses? uses = null;
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is BuiltinInst { Name: "slice_of", Operands: [LoadBorrowInst array] } view
                    && Forward(block, i, view, array, uses ??= new Uses(function))) {
                    uses = null;
                    i--;
                }
            }
        }
    }

    static bool Forward(SilBlock block, int start, BuiltinInst view, LoadBorrowInst array, Uses uses) {
        var reads = new List<BuiltinInst>();
        DestroyValueInst? destroyed = null;
        foreach (var user in uses.Of(view)) {
            switch (user) {
                case BuiltinInst read when IsRead(read):
                    reads.Add(read);
                    break;

                case DestroyValueInst destroy when destroyed is null:
                    destroyed = destroy;
                    break;

                default:
                    return false;
            }
        }

        if (destroyed is null) {
            return false;
        }

        // From the view to its end: the reads, and nothing that could change
        // the array or end its borrow.
        for (var i = start + 1; i < block.Instructions.Count; i++) {
            var instruction = block.Instructions[i];
            if (ReferenceEquals(instruction, destroyed)) {
                foreach (var read in reads) {
                    read.SetOperand(0, array);
                    if (read.Name.StartsWith("bytes_load_", StringComparison.Ordinal)) {
                        read.Rename("bytes_load_array_" + read.Name["bytes_load_".Length..]);
                    }
                }

                block.Instructions.RemoveAt(i);
                block.Instructions.RemoveAt(start);
                return true;
            }

            if (!(instruction is BuiltinInst read2 && reads.Contains(read2)) && !IsArithmetic(instruction)) {
                return false;
            }
        }

        return false;
    }

    static bool IsRead(BuiltinInst read) =>
        read.Name.StartsWith("bytes_load_U", StringComparison.Ordinal) || read.Name == "array_count" || read.Name == "array_get" && read.Type.IsTrivial;

    static bool IsArithmetic(SilInstruction instruction) =>
        instruction is IntegerLiteralInst
        || instruction is BuiltinInst builtin
        && builtin.Operands.All(static o => o.Type.IsTrivial)
        && builtin.Type is ScalarType
        && (Simplify.Pure(builtin.Name) || _arithmetic.Any(p => builtin.Name.StartsWith(p, StringComparison.Ordinal)));

    static readonly string[] _arithmetic = ["add_", "sub_", "mul_", "div_", "rem_", "neg_", "convert_", "byte_swap_"];
}
