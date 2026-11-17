using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>Dictionaries and array slices: native and interpreted alike, leak-checked.</summary>
public sealed class CollectionTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void DictionariesAreReadWrittenCountedAndRemovedFrom() =>
        Prints(
            "3 2 1 0\n2\nnone\nchanged 3\nremoved: true\n7 9\n",
            """
            fn counts(_ words: String[>..]) -> [String: Int] {
                var result: [String: Int] = [:]
                for word in words {
                    result[word] = (result[word] ?? 0) + 1
                }
                return result
            }
            """,
            """
                let c = counts(["a", "b", "a", "c", "a", "b"])
                print("\(c["a"] ?? 0) \(c["b"] ?? 0) \(c["c"] ?? 0) \(c["z"] ?? 0)")
                let literal = ["one": 1, "two": 2, "three": 3]
                print(literal["two"] ?? -1)
                if literal["four"] == none {
                    print("none")
                }
                var copy = literal
                copy["three"] = 33
                copy["one"] = none
                print("changed \(literal["three"] ?? 0)")
                print("removed: \(copy["one"] == none && copy["three"] == 33 && copy["two"] == 2)")
                var byChar: [Char: Int] = [:]
                byChar['x'] = 7
                byChar['y'] = 9
                print("\(byChar['x'] ?? 0) \(byChar['y'] ?? 0)")
            """
        );

    [Fact]
    public void ADictionaryOfStringsAndStructs() =>
        Prints(
            "Ada Lovelace (36)\nmissing\n",
            """
            struct Person {
                var name: String
                var age: Int
            }
            """,
            """
                var people: [String: Person] = [:]
                people["ada"] = Person { name: "Ada Lovelace", age: 36 }
                people["grace"] = Person { name: "Grace Hopper", age: 85 }
                people["grace"] = none
                if let ada = people["ada"] {
                    print("\(ada.name) (\(ada.age))")
                }
                if people["grace"] == none {
                    print("missing")
                }
            """
        );

    [Fact]
    public void SlicesCopyARangeAndTrapOutsideIt() {
        var execution = AssertAgree(
            Program(
                "",
                """
                    let xs: String[>..] = ["a", "b", "c", "d"]
                    let middle = xs.slice(from: 1, to: 3)
                    print(middle.count)
                    print(middle[0] + middle[1])
                    let empty = xs.slice(from: 2, to: 2)
                    print(empty.count)
                    let bad = xs.slice(from: 3, to: 9)
                    print(bad.count)
                """
            )
        );
        Assert.Equal("2\nbc\n0\n", execution.Stdout);
        Assert.Equal("fatal error: array slice out of range\n", execution.Stderr);
    }
}
