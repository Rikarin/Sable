using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Every kind of array passed as a slice, and ranges of an array's
///     elements read out — natively and in the interpreter, which must agree.
/// </summary>
public sealed class SliceTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void ArraysAsSlicesAndRangesOfThem() =>
        Prints(
            "15\n30\n15\nann\n5\n12\n3\n9\n9\nbo\n",
            """
            fn sum(_ xs: Int[:]) -> Int {
                var total = 0
                for x in xs { total = total + x }
                return total
            }
            fn first<T>(_ xs: T[:]) -> T { return xs[0] }
            """,
            """
                let grow: Int[>..] = [1, 2, 3, 4, 5]
                let fixed: Int[] = [10, 20]
                var shrink: Int[>..<] = [7]
                shrink.append(8)
                print(sum(grow))
                print(sum(fixed))
                print(sum(shrink))
                let names: String[>..] = ["ann", "bo", "cy"]
                print(first(names))
                print(sum(grow[1..<3]))
                print(sum(grow[2...]))
                print(sum(grow[..<2]))
                print(sum(grow[1...3]))
                let r = 1..<4
                print(sum(grow[r]))
                print(first(names[1...]))
            """
        );
}
