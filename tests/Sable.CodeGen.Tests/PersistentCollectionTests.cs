using Sable.SIL;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The persistent and the frozen collections (docs/06, "Frozen and
///     persistent collections") the same natively and interpreted, with
///     generics compiled each way, nothing left; and the frozen ones a
///     constant holds. The library's own tests
///     (tests/lib/Sable.Collections) say more, natively.
/// </summary>
[Native]
public sealed class PersistentCollectionTests {
    const string Persistent = """
        module Test

        import Sable.Collections

        struct Hostile: Hashable {
            let value: Int
            operator == (lhs: Hostile, rhs: Hostile) -> Bool => lhs.value == rhs.value
            fn hash(into hasher: inout Hasher) { hasher.combine(value % 3) }
        }

        final class Item {
            let name: String
            init(_ name: String) { self.name = name }
        }

        fn follow<K: Hashable>(_ key: (Int) -> K) -> Int {
            var map = ImmutableDictionary<K, Item>()
            for i in 0..<60 {
                map[key(i)] = Item("\(i)")
            }
            let before = map
            for i in 0..<30 {
                map.remove(key(i * 2))
                map[key(i + 100)] = Item("new")
            }
            let merged = before.merging(map) { $1 }
            assert(map.structuralFault() == none && merged.structuralFault() == none)
            assert(before.count == 60 && before[key(4)]!.name == "4")
            return map.count * 1000 + merged.count
        }

        @main
        fn main() {
            print("\(follow((i: Int) => i)) \(follow((i: Int) => "\(i)")) \(follow((i: Int) => Hostile { value: i }))")

            var list = ImmutableList<Item>()
            for i in 0..<200 {
                list.append(Item("\(i)"))
            }
            let older = list
            list = list[20..<150] + list
            list.insert(Item("in"), at: 40)
            _ = list.remove(at: 5)
            list[9] = Item("set")
            assert(list.structuralFault() == none)
            print("\(list.count) \(list[39].name) \(list[9].name) \(list[200].name) \(older.count) \(older[9].name)")

            let a = ImmutableSet<Int>(0..<50)
            let b = ImmutableSet<Int>(30..<80)
            print("\(a.union(b).count) \(a.intersection(b).count) \(a.subtracting(b).count) \(a.symmetricDifference(b).count) \(a == b) \(a.inserting(7) == a)")
            let sorted = ImmutableSortedDictionary<String, Int>([("b", 2), ("a", 1), ("c", 3)])
            print("\(sorted.setting("bb", to: 9).keys) \(sorted.range("b"...).values) \(ImmutableSortedSet<Int>([3, 1]))")
        }
        """;

    const string PersistentOutput = "60090 60090 60090\n330 in set 70 200 9\n80 20 30 60 false true\n[\"a\", \"b\", \"bb\", \"c\"] [2, 3] ImmutableSortedSet([1, 3])\n";

    const string Frozen = """
        module Test

        import Sable.Collections

        struct Point { let x: Int; let name: String }

        const Words = FrozenDictionary<String, Int>([("get", 1), ("put", 2), ("post", 3), ("delete", 4), ("head", 5), ("patch", 6)])
        const Numbers = FrozenDictionary<Int, Point>([(100, Point { x: 1, name: "one" }), (20000, Point { x: 2, name: "two" })])
        const Reserved = FrozenSet<String>(ignoringAsciiCase: ["con", "prn", "aux", "nul", "lpt", "com"])
        const Squares: Int[>..] = squares(400)
        const Names: String[>..] = ["x", "y"]
        const Nothing: Int[>..] = []

        const fn squares(_ count: Int) -> Int[>..] {
            var all: Int[>..] = []
            var i = 0
            while i < count {
                all.append(i * i)
                i += 1
            }
            return all
        }

        fn hits<K: Hashable>(_ frozen: FrozenDictionary<K, Int>, _ keys: K[>..]) -> Int {
            var found = 0
            for key in keys {
                found = found * 10 + (frozen[key] ?? 0)
            }
            return found
        }

        @main
        fn main() {
            print("\(Words["head"] ?? 0) \(Words["hea"] ?? 0) \(Numbers[20000]!.name) \(Numbers[1] == none) \(Reserved.contains("AUX")) \(Reserved.contains("au"))")
            var mine = Squares
            mine[0] = 7
            mine.append(8)
            var names = Names
            names[1] = "z\(names.count)"
            names.append("w")
            print("\(mine[0]) \(mine.count) \(Squares[0]) \(Squares[399]) \(names) \(Names) \(Nothing.count)")
            let words = Words.mapValues { $0 + 1 }
            print("\(hits(Words, ["patch", "x", "get"])) \(hits(words, ["patch", "x", "get"])) \(hits(FrozenDictionary<Char, Int>([('a', 1), ('z', 2)]), ['z', 'a', 'b']))")
            let odd = FrozenDictionary<F64, Int>([(1.5, 1), (2.5, 2), (3.5, 3), (4.5, 4), (5.5, 5)])
            print("\(hits(odd, [5.5, 0.0, 1.5]))")
        }
        """;

    const string FrozenOutput = "5 0 two true true false\n7 401 0 159201 [\"x\", \"z2\", \"w\"] [\"x\", \"y\"] 0\n601 702 210\n501\n";

    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void PersistentCollectionsAgreeEachWay(GenericsMode mode) {
        using var _ = SilGen.UseGenerics(mode);
        Assert.Equal(PersistentOutput, AssertAgree(Persistent).Stdout);
    }

    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void FrozenCollectionsAndConstantsAgreeEachWay(GenericsMode mode) {
        using var _ = SilGen.UseGenerics(mode);
        Assert.Equal(FrozenOutput, AssertAgree(Frozen).Stdout);
    }
}
