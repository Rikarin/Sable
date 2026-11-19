using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     An array literal of constants (#265): natively an immortal array in
///     read-only memory, not built by appends; copy on write copies it before
///     a change. Native and interpreted agree, leak-checked.
/// </summary>
public sealed class ConstantArrayTests {
    [Fact]
    public void ConstantArraysReadAndCopyBeforeAChange() =>
        Assert.Equal(
            "21\n-128\n99 0 5 4\n1 2 3 3\nfalse b -2.15\n4\n3\n2 true\n7\n18446744073709551615 1\n",
            AssertAgree(
                Program(
                    """
                    static let table: U32[] = [0, 7, 14, 21]
                    static let signs: I8[] = [-128, -1, 0, 127]

                    fn literal() -> Int[>..] {
                        return [3, 1, 2]
                    }
                    """,
                    """
                        print(table[3])
                        print(signs[0])
                        var copy: U32[>..] = []
                        copy = table.toArray()
                        var shared = table
                        shared[0] = 99
                        copy.append(5)
                        print("\(shared[0]) \(table[0]) \(copy.count) \(table.count)")
                        var xs = literal()
                        xs.sort()
                        print("\(xs[0]) \(xs[1]) \(xs[2]) \(literal()[0])")
                        let flags = [true, false, true]
                        let chars: Char[] = ['a', 'b']
                        let floats = [1.5, -2.25, 0.1]
                        print("\(flags[1]) \(chars[1]) \(floats[1] + floats[2])")
                        let x = 4
                        let mixed = [1, x, 3]
                        print(mixed[1])
                        var ys: Int[>..<] = [1, 2, 3]
                        print(ys.capacity)
                        ys.removeLast()
                        print("\(ys.count) \(ys.capacity >= 2)")
                        let s = table[1..<3]
                        print(s[0])
                        var big: U64[] = [18446744073709551615, 0]
                        big[1] = 1
                        print("\(big[0]) \(big[1])")
                    """
                )
            ).Stdout
        );

    /// <summary>A table of 4000 constants is one constant, and the code that reads it no larger for it.</summary>
    [Fact]
    public void ALargeTableIsOneConstantNotAppends() {
        var elements = string.Join(", ", Enumerable.Range(0, 4000).Select(static i => i * 7));
        var source = $"module Main\n\nstatic let table: U32[] = [{elements}]\n\nfn main() {{\n    print(table[3])\n}}\n";
        var result = Pipeline.Compile(new SourceText(source, "table.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("builtin \"array_literal\"", SilPrinter.Print(result.Sil!), StringComparison.Ordinal);
        Assert.Contains("[4000 x i32] [i32 0, i32 7,", result.LlvmIr!, StringComparison.Ordinal);
        Assert.True(result.LlvmIr!.Split('\n').Length < 2000, "the table is built by code, not kept as a constant");
    }
}
