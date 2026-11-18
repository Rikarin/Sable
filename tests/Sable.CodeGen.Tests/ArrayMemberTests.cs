using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The array family's members, declared in Sable (lib/Sable.Core/Array.sb)
///     and an array extension of the program's own: native and interpreted
///     alike, leak-checked.
/// </summary>
public sealed class ArrayMemberTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void TheCoreMembersOfEachKind() =>
        Prints(
            "3 false\ntrue false\ntrue false\n2 1\n9 2\n0 true\n2 true\n",
            "",
            """
                var xs: Int[>..] = []
                xs.append(4)
                xs.append(element: 9)
                xs.append(1)
                print("\(xs.count) \(xs.isEmpty)")
                print("\(xs.contains(9)) \(xs.contains(element: 5))")
                var names: String[>..<] = ["ada", "grace"]
                print("\(names.contains("grace")) \(names.contains("alan"))")
                let part = xs.slice(from: 1, to: 3)
                print("\(part.count) \(part[1])")
                names.removeLast()
                print("\(xs[1]) \(names.count + 1)")
                xs.clear()
                print("\(xs.count) \(xs.isEmpty)")
                let fixed: String[] = ["a", "b"]
                print("\(fixed.count) \(fixed.contains("b"))")
            """
        );

    [Fact]
    public void BytesReadAsUtf8() =>
        Prints(
            "hé€!\n4\n",
            "",
            """
                let bytes: U8[>..] = [104, 195, 169, 226, 130, 172, 33]
                let text = bytes.toString()
                print(text)
                print(text.count)
            """
        );

    [Fact]
    public void AnArrayExtensionOfTheProgramsOwn() =>
        Prints(
            "9 pear\n9\n3\n",
            """
            struct Item { var name: String, var weight: Int }

            extension<T: Comparable> T[:] {
                fn largest() -> T {
                    var best = self[0]
                    for x in self {
                        if best < x {
                            best = x
                        }
                    }
                    return best
                }
            }

            extension<T> T[>..] {
                mutating fn pushTwice(_ item: T) {
                    append(item)
                    self.append(item)
                }
            }

            extension Item[>..] {
                fn totalWeight() -> Int {
                    var total = 0
                    for item in self {
                        total = total + item.weight
                    }
                    return total
                }
            }
            """,
            """
                var xs: Int[>..] = [3, 9, 4]
                let words: String[>..] = ["apple", "pear", "fig"]
                print("\(xs.largest()) \(words.largest())")
                var items: Item[>..] = [Item { name: "a", weight: 5 }]
                items.pushTwice(Item { name: "b", weight: 1 })
                xs.pushTwice(0)
                print(items.totalWeight() + items.count * 2 - xs.count + 1)
                print(items.count)
            """
        );
}
