using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>@derive(Equatable, Hashable, Comparable, Debug)</c> (docs/06 §6.5):
///     members made from the fields, used directly, through generic code and
///     as Sable.Collections' keys and sort order. Native and interpreted.
/// </summary>
public sealed class DeriveTests {
    [Fact]
    public void AStructDerivesEveryProtocol() =>
        Assert.Equal(
            "false\ntrue\ntrue\ntrue\nfalse\ntrue\ntrue\nPoint(x: 1, y: 2)\nEmpty() true\n",
            AssertAgree("""
                module P

                @derive(Equatable, Hashable, Comparable, Debug)
                struct Point {
                    var x: Int
                    var y: Int
                }

                @derive(Equatable, Debug)
                struct Empty { }

                @main
                fn main() {
                    let p = Point(x: 1, y: 2)
                    let q = Point(x: 1, y: 3)
                    print(p == q)
                    print(p != q)
                    print(p < q)
                    print(q >= p)
                    print(Point(x: 2, y: 0) < p)
                    print(p <= Point(x: 1, y: 2))
                    print(p.hashValue == Point(x: 1, y: 2).hashValue)
                    print(p.debugDescription())
                    print("\(Empty().debugDescription()) \(Empty() == Empty())")
                }
                """).Stdout
        );

    [Fact]
    public void DerivedTypesAreKeysAndSortOrderForTheCollections() =>
        Assert.Equal(
            "2\ntrue false\n7\nPoint(x: 0, y: 9)\nPoint(x: 1, y: 2)\nPoint(x: 1, y: 3)\n",
            AssertAgree("""
                module P
                import Sable.Collections

                @derive(Hashable, Comparable, Debug)
                struct Point {
                    var x: Int
                    var y: Int
                }

                @main
                fn main() {
                    var seen = Set<Point>()
                    seen.insert(Point(x: 1, y: 2))
                    seen.insert(Point(x: 1, y: 2))
                    seen.insert(Point(x: 1, y: 3))
                    print(seen.count)
                    print("\(seen.contains(Point(x: 1, y: 3))) \(seen.contains(Point(x: 3, y: 1)))")
                    var scores: [Point: Int] = [:]
                    scores[Point(x: 4, y: 4)] = 7
                    print(scores[Point(x: 4, y: 4)] ?? 0)
                    for p in sorted([Point(x: 1, y: 3), Point(x: 1, y: 2), Point(x: 0, y: 9)]) {
                        print(p.debugDescription())
                    }
                }
                """).Stdout
        );

    [Fact]
    public void AGenericPairDerivesForWhatItHolds() =>
        Assert.Equal(
            "true\nfalse\ntrue\nPair(first: 1, second: \"a\")\ntrue\n3\nPair(first: Pair(first: 1, second: \"x\"), second: 'c')\nPair(first: 1, second: \"b\")\nPair(first: 2, second: \"a\")\n",
            AssertAgree("""
                module P
                import Sable.Collections

                @derive(Equatable, Hashable, Comparable, Debug)
                struct Pair<A, B> {
                    var first: A
                    var second: B
                }

                fn has<T: Equatable>(_ values: T[>..], _ wanted: T) -> Bool {
                    for v in values {
                        if v == wanted { return true }
                    }
                    return false
                }

                @main
                fn main() {
                    let a = Pair(first: 1, second: "a")
                    print(a == Pair(first: 1, second: "a"))
                    print(a == Pair(first: 1, second: "b"))
                    print(a < Pair(first: 1, second: "b"))
                    print(a.debugDescription())
                    print(has([Pair(first: 2, second: "z"), a], Pair(first: 1, second: "a")))
                    var m: [Pair<Int, String>: Int] = [:]
                    m[a] = 3
                    print(m[Pair(first: 1, second: "a")] ?? 0)
                    let nested = Pair(first: Pair(first: 1, second: "x"), second: 'c')
                    print(nested.debugDescription())
                    for p in sorted([Pair(first: 2, second: "a"), Pair(first: 1, second: "b")]) {
                        print(p.debugDescription())
                    }
                }
                """).Stdout
        );

    [Fact]
    public void AUnionDerivesCaseWise() =>
        Assert.Equal(
            "true\nfalse\nfalse\ntrue\ntrue\nShape.Rect(w: 2, h: 3)\nShape.Circle(radius: 1.5)\nShape.Empty\n"
            + "Shape.Circle(radius: 1.5)\nShape.Circle(radius: 4.0)\nShape.Rect(w: 1, h: 9)\nShape.Rect(w: 2, h: 3)\nShape.Empty\n3\n",
            AssertAgree("""
                module P
                import Sable.Collections

                @derive(Equatable, Hashable, Comparable, Debug)
                union Shape {
                    Circle(radius: F64)
                    Rect(w: Int, h: Int)
                    Empty
                }

                @main
                fn main() {
                    let s = Shape.Rect(w: 2, h: 3)
                    print(s == Shape.Rect(w: 2, h: 3))
                    print(s == Shape.Rect(w: 2, h: 4))
                    print(s == Shape.Empty)
                    print(Shape.Circle(radius: 5.0) < s)
                    print(s < Shape.Empty)
                    print(s.debugDescription())
                    print(Shape.Circle(radius: 1.5).debugDescription())
                    print(Shape.Empty.debugDescription())
                    let shapes: Shape[>..] = [Shape.Empty, Shape.Rect(w: 2, h: 3), Shape.Circle(radius: 4.0), Shape.Rect(w: 1, h: 9), Shape.Circle(radius: 1.5)]
                    for shape in sorted(shapes) {
                        print(shape.debugDescription())
                    }
                    var seen = Set<Shape>()
                    for shape in shapes {
                        seen.insert(shape)
                    }
                    seen.insert(Shape.Rect(w: 2, h: 3))
                    seen.insert(Shape.Empty)
                    print(seen.count - 2)
                }
                """).Stdout
        );

    [Fact]
    public void AGenericUnionAndNestedFieldsDerive() =>
        Assert.Equal(
            "Outcome.Ok(1)\nOutcome.Ok(9)\nOutcome.Err(\"a\")\nOutcome.Err(\"b\")\n"
            + "Record(label: \"a\\tb \\\"q\\\"\", weight: 1.5, flag: true, initial: 'z', outcome: Outcome.Err(\"bad\"))\ntrue\nfalse\n1\n",
            AssertAgree("""
                module P
                import Sable.Collections

                @derive(Equatable, Hashable, Comparable, Debug)
                union Outcome<T, E> {
                    Ok(T)
                    Err(E)
                }

                @derive(Equatable, Hashable, Debug)
                struct Record {
                    var label: String
                    var weight: F64
                    var flag: Bool
                    var initial: Char
                    var outcome: Outcome<Int, String>
                }

                @main
                fn main() {
                    let xs: Outcome<Int, String>[>..] = [Outcome.Err("b"), Outcome.Ok(9), Outcome.Err("a"), Outcome.Ok(1)]
                    for x in sorted(xs) {
                        print(x.debugDescription())
                    }
                    let bad: Outcome<Int, String> = Outcome.Err("bad")
                    let r = Record(label: "a\tb \"q\"", weight: 1.5, flag: true, initial: 'z', outcome: bad)
                    print(r.debugDescription())
                    print(r == Record(label: "a\tb \"q\"", weight: 1.5, flag: true, initial: 'z', outcome: bad))
                    print(r == Record(label: "a\tb \"q\"", weight: 1.5, flag: false, initial: 'z', outcome: bad))
                    var seen = Set<Record>()
                    seen.insert(r)
                    seen.insert(Record(label: "a\tb \"q\"", weight: 1.5, flag: true, initial: 'z', outcome: bad))
                    print(seen.count)
                }
                """).Stdout
        );
    /// <summary>
    ///     A field that is an optional or an array of what compares is
    ///     compared by what it holds: '==' on them is the language's own.
    /// </summary>
    [Fact]
    public void OptionalAndArrayFieldsAreComparedByWhatTheyHold() =>
        Assert.Equal(
            "true\nfalse\nfalse\nfalse\ntrue\nfalse\n",
            AssertAgree("""
                module P

                @derive(Equatable)
                struct Point {
                    var x: Int
                    var y: Int
                }

                @derive(Equatable)
                struct Record {
                    var count: Int?
                    var tags: String[>..]
                    var path: Point[>..]?
                    var label: String?
                }

                @main
                fn main() {
                    let a = Record(count: 3, tags: ["x", "y"], path: [Point(x: 1, y: 2)], label: none)
                    print(a == Record(count: 3, tags: ["x", "y"], path: [Point(x: 1, y: 2)], label: none))
                    print(a == Record(count: none, tags: ["x", "y"], path: [Point(x: 1, y: 2)], label: none))
                    print(a == Record(count: 3, tags: ["x"], path: [Point(x: 1, y: 2)], label: none))
                    print(a == Record(count: 3, tags: ["x", "y"], path: [Point(x: 2, y: 2)], label: none))
                    print(a != Record(count: 3, tags: ["x", "y"], path: none, label: none))
                    print(a == Record(count: 3, tags: ["x", "y"], path: [Point(x: 1, y: 2)], label: "l"))
                }
                """).Stdout
        );

    /// <summary>Arrays compare: as long as each other, and equal element by element.</summary>
    [Fact]
    public void ArraysCompareElementByElement() =>
        Assert.Equal(
            "true\nfalse\ntrue\ntrue\nfalse\ntrue\ntrue\n",
            AssertAgree("""
                module P

                @derive(Equatable)
                struct Point {
                    var x: Int
                    var y: Int
                }

                @main
                fn main() {
                    print([1, 2] == [1, 2])
                    print([1, 2] == [1, 2, 3])
                    let a: Int[>..] = [1, 2]
                    var b: Int[>..] = [1]
                    b.append(2)
                    print(a == b)
                    print(["x", "y"] != ["x", "z"])
                    print([Point(x: 1, y: 2)] == [Point(x: 2, y: 1)])
                    let empty: String[>..] = []
                    print(empty == [])
                    let nested: Int[>..][>..] = [[1], [2, 3]]
                    print(nested == [[1], [2, 3]])
                }
                """).Stdout
        );

    /// <summary>
    ///     The built-in dictionary takes any key with an '==': a derived one,
    ///     or the program's own, as well as a builtin's.
    /// </summary>
    [Fact]
    public void ADictionaryTakesKeysWithAHashOfTheirOwn() =>
        Assert.Equal(
            "a\nb\nnone\n2\nc\n1\n",
            AssertAgree("""
                module P

                @derive(Hashable)
                struct Point {
                    var x: Int
                    var y: Int
                }

                struct Name: Hashable {
                    var text: String
                    operator == (lhs: Name, rhs: Name) -> Bool {
                        return lhs.text.count == rhs.text.count
                    }
                    fn hash(into hasher: inout Hasher) {
                        hasher.combine(text.count)
                    }
                }

                @main
                fn main() {
                    var d: [Point: String] = [:]
                    d[Point(x: 1, y: 2)] = "a"
                    d[Point(x: 2, y: 1)] = "b"
                    print(d[Point(x: 1, y: 2)] ?? "none")
                    print(d[Point(x: 2, y: 1)] ?? "none")
                    print(d[Point(x: 9, y: 9)] ?? "none")
                    print(d.count)
                    d[Point(x: 1, y: 2)] = "c"
                    print(d[Point(x: 1, y: 2)] ?? "none")
                    var byLength: [Name: Int] = [:]
                    byLength[Name(text: "ab")] = 1
                    print(byLength[Name(text: "xy")] ?? 0)
                }
                """).Stdout
        );
}
