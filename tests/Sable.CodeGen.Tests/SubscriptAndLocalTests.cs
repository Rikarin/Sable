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

    // ---- Local functions --------------------------------------------------------

    [Fact]
    public void ALocalFunctionThatCapturesNothingIsAPlainFunction() =>
        Prints(
            "4\n120\n",
            """
            fn nested(_ n: Int) -> Int {
                fn helper(_ x: Int) -> Int {
                    return x + 1
                }
                return helper(n) + helper(n)
            }

            fn factorial(_ n: Int) -> Int {
                fn go(_ k: Int, _ product: Int) -> Int {
                    if k <= 1 {
                        return product
                    }
                    return go(k - 1, product * k)
                }
                return go(n, 1)
            }
            """,
            """
                print(nested(1))
                print(factorial(5))
            """
        );

    [Fact]
    public void ALocalFunctionCapturesWhatIsAroundIt() =>
        // Locals by reference, as a closure does: 'bump' changes what 'helper' reads.
        Prints(
            "181\n15\n3\n",
            """
            fn outer(_ n: Int) -> Int {
                var offset = 10
                fn helper(_ x: Int) -> Int {
                    return x + offset
                }
                fn bump() {
                    offset = offset + 1
                }
                fn sum(_ k: Int) -> Int {
                    if k <= 0 {
                        return 0
                    }
                    return helper(k) + sum(k - 1)
                }
                fn fact(_ k: Int) -> Int {
                    if k <= 1 {
                        return 1
                    }
                    return k * fact(k - 1)
                }
                bump()
                let f = (y: Int) => helper(y) * 2
                return helper(n) + fact(5) + sum(2) + f(1)
            }

            struct S {
                var x: Int
                fn m(_ by: Int) -> Int {
                    fn h() -> Int { return x + by }
                    fn g() -> Int { return self.x * h() }
                    return g()
                }
            }

            fn repeated<T>(_ value: T, _ count: Int) -> T[>..] {
                var items: T[>..] = []
                fn add() {
                    items.append(value)
                }
                var i = 0
                while i < count {
                    add()
                    i = i + 1
                }
                return items
            }
            """,
            """
                print(outer(1))
                print(S(x: 3).m(2))
                print(repeated("a", 3).count)
            """
        );

    [Fact]
    public void ALocalFunctionThatCapturesCanThrowAndBePassedOn() =>
        Prints(
            "6\nnegative\n",
            """
            union Problem { Negative }

            fn apply(_ f: (Int) -> Int, _ x: Int) -> Int { return f(x) }

            fn checked(_ limit: Int, _ x: Int) throws(Problem) -> Int {
                fn check(_ v: Int) throws(Problem) -> Int {
                    if v < 0 {
                        throw Problem.Negative
                    }
                    return v + limit
                }
                fn twice(_ v: Int) -> Int { return v * limit }
                return try check(x) + apply(twice, 1) - limit
            }
            """,
            """
                do {
                    print(try checked(2, 4))
                    print(try checked(2, -1))
                } catch {
                    print("negative")
                }
            """
        );

    // ---- Bounded arrays outside an inline struct --------------------------------

    [Fact]
    public void ABoundedArrayGrowsWithinItsBoundAndIsAValue() =>
        Prints(
            "3\n6\n1\n2\n0\n",
            """
            struct Small {
                var items: Int[..4]
            }

            fn total(_ values: Int[..4]) -> Int {
                var sum = 0
                for value in values {
                    sum = sum + value
                }
                return sum
            }
            """,
            """
                var inline: Int[..8] = []
                inline.append(1)
                inline.append(2)
                inline.append(3)
                print(inline.count)
                var small = Small(items: [1, 2])
                let copy = small
                small.items.append(3)
                print(total(small.items))
                print(small.items[0])
                print(copy.items.count)
                inline.clear()
                print(inline.count)
            """
        );

    [Fact]
    public void ABoundedArrayTrapsWhenItIsFull() {
        var run = AssertAgree(Program("", "    var pair: Int[..2] = [1, 2]\n    print(pair.count)\n    pair.append(3)\n    print(pair.count)"));
        Assert.Equal("2\n", run.Stdout);
        Assert.Equal("fatal error: inline array is full\n", run.Stderr);
    }

    // ---- Compiler builtins ------------------------------------------------------

    [Fact]
    public void TheCompilerBuiltinsSayWhereTheyAre() =>
        Prints(
            "test.sab:5:50 in main of Test\nwhere\n",
            "fn whereAmI() -> String { return #function }",
            """
                print(#file + ":" + #line.toString() + ":" + #column.toString() + " in " + #function + " of " + #module)
                print(whereAmI().count > 0 ? "where" : "nowhere")
            """
        );
}
