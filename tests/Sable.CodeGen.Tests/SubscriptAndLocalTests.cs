using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Subscripts a type declares, functions declared inside functions,
///     inline arrays as locals, the compile-time builtins and recursive
///     generic unions — each as a program whose output is checked, natively
///     and in the interpreter, which must agree.
/// </summary>
public sealed class SubscriptAndLocalTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    // ---- Subscripts -------------------------------------------------------------

    const string Grid = """
                        struct Grid {
                            var cells: Int[>..]
                            var width: Int

                            subscript(row: Int, column: Int) -> Int {
                                get => cells[row * width + column]
                                set { cells[row * width + column] = newValue }
                            }

                            subscript(row: Int) -> Int[>..] {
                                get {
                                    var picked: Int[>..] = []
                                    var i = 0
                                    while i < width {
                                        picked.append(cells[row * width + i])
                                        i = i + 1
                                    }
                                    return picked
                                }
                            }
                        }
                        """;

    [Fact]
    public void ASubscriptReadsAndWrites() =>
        Prints(
            "5\n40\n41\n3\n5\n",
            Grid,
            """
                var grid = Grid(cells: [0, 1, 2, 3, 4, 5], width: 3)
                print(grid[1, 2])
                grid[1, 2] = 40
                print(grid[1, 2])
                grid[1, 2] += 1
                print(grid[1, 2])
                print(grid[1].count)
                grid[0, 0] = grid[0, 1] + grid[1, 1]
                print(grid[0, 0])
            """
        );

    [Fact]
    public void ASubscriptWithAnExpressionBodyAndAGenericOwner() =>
        Prints(
            "b\nc\nzz\n",
            """
            struct Row<Value> {
                var values: Value[>..]

                subscript(index: Int) -> Value => values[index]

                subscript(from index: Int, or fallback: Value) -> Value {
                    get {
                        if index < values.count {
                            return values[index]
                        }
                        return fallback
                    }
                    set { values[index] = newValue }
                }
            }
            """,
            """
                var row = Row<String>(values: ["a", "b", "c"])
                print(row[1])
                print(row[from: 2, or: "x"])
                row[from: 0, or: ""] = "zz"
                print(row[0])
            """
        );

    [Fact]
    public void ASubscriptOfAClassWritesThroughTheReference() =>
        Prints(
            "14\n",
            """
            class Box {
                var items: Int[>..] = []
                init() {}
                subscript(i: Int) -> Int {
                    get => items[i]
                    set { items[i] = newValue * 2 }
                }
            }
            """,
            """
                let box = Box()
                box.items.append(0)
                box[0] = 7
                print(box[0])
            """
        );
}
