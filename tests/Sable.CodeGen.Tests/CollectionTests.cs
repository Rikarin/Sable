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
    public void DictionariesHaveMembersAndIterate() =>
        Prints(
            "2 false\n3 true false\n36 none 2\n2 126\n141 alangrace\n120 2\n1x 2y\n0 true\nv 0\n",
            """
            fn total(_ d: [String: Int]) -> Int {
                var sum = 0
                for (_, value) in d {
                    sum = sum + value
                }
                return sum
            }
            """,
            """
                var ages: [String: Int] = ["ada": 36, "alan": 41]
                print("\(ages.count) \(ages.isEmpty)")
                ages["grace"] = 85
                print("\(ages.count) \(ages.contains("grace")) \(ages.contains("bob"))")
                let removed = ages.remove("ada")
                let again = ages.remove("ada")
                print("\(removed ?? -1) \(again == none ? "none" : "some") \(ages.count)")
                var sum = 0
                for v in ages.values() { sum = sum + v }
                print("\(ages.keys().count) \(sum)")
                var names = ""
                for key in ages.keys() where key != "nobody" { names = names + key }
                print("\(total(ages) + 15) \(names.count == 9 ? "alangrace" : names)")
                let scores: [String: Int] = ["a": 10, "b": 50, "c": 70]
                var kept = 0
                var count = 0
                for (name, score) in scores where score > 20 && name != "z" {
                    kept = kept + score
                    count = count + 1
                }
                print("\(kept) \(count)")
                let pairs: (Int, String)[>..] = [(1, "x"), (2, "y")]
                var line = ""
                for (n, s) in pairs { line = line + (line.isEmpty ? "" : " ") + "\(n)\(s)" }
                print(line)
                ages.removeAll()
                print("\(ages.count) \(ages.isEmpty)")
                var words: [String: String] = ["k": "v"]
                let gone = words.remove("k")
                print("\(words["k"] ?? gone ?? "none") \(words.values().count)")
            """);

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
    public void SlicesViewARangeAndTrapOutsideIt() {
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
        Assert.Equal("fatal error: array slice out of range\n  at test.sb:11:15\n", execution.Stderr);
    }

    [Fact]
    public void RangesAreValuesStoredPassedReturnedAndIterated() =>
        Prints(
            "5\n10\nfalse true\ntrue false\n12\ntrue false\nfalse true\ntrue false\n0 2 4\n",
            """
            struct Window {
                var span: Range<Int>
                var name: String
            }

            fn make(_ n: Int) -> Range<Int> {
                return 0..<n
            }

            fn sum(_ r: Range<Int>) -> Int {
                var total = 0
                for i in r {
                    total = total + i
                }
                return total
            }

            fn closed(_ n: Int) -> ClosedRange<Int> {
                return 1...n
            }
            """,
            """
                let r = make(5)
                print(r.lowerBound + r.upperBound)
                print(sum(r))
                print("\(r.isEmpty) \(make(0).isEmpty)")
                print("\(r.contains(4)) \(r.contains(5))")
                var t = 0
                for i in closed(3) where i != 2 { t = t + i * 3 }
                print(t)
                let c = closed(4)
                print("\(c.contains(4)) \(c.isEmpty)")
                let s = "a"..<"c"
                print("\(s.contains("c")) \(s.contains("b"))")
                let f = 2.5...3.5
                print("\(f.contains(3.0)) \(f.contains(3.75))")
                let w = Window(span: 0..<6, name: "w")
                var picked = ""
                for i in w.span where i % 2 == 0 {
                    picked = picked + (picked.isEmpty ? "" : " ") + "\(i)"
                }
                print(picked)
            """
        );
}
