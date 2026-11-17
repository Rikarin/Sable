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
                    print(p.hash() == Point(x: 1, y: 2).hash())
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
                    let seen = HashSet<Point>()
                    seen.insert(Point(x: 1, y: 2))
                    seen.insert(Point(x: 1, y: 2))
                    seen.insert(Point(x: 1, y: 3))
                    print(seen.count)
                    print("\(seen.contains(Point(x: 1, y: 3))) \(seen.contains(Point(x: 3, y: 1)))")
                    let scores = HashMap<Point, Int>()
                    scores.set(Point(x: 4, y: 4), 7)
                    print(scores.get(Point(x: 4, y: 4)) ?? 0)
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
                    let m = HashMap<Pair<Int, String>, Int>()
                    m.set(a, 3)
                    print(m.get(Pair(first: 1, second: "a")) ?? 0)
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
                    let seen = HashSet<Shape>()
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
                    let seen = HashSet<Record>()
                    seen.insert(r)
                    seen.insert(Record(label: "a\tb \"q\"", weight: 1.5, flag: true, initial: 'z', outcome: bad))
                    print(seen.count)
                }
                """).Stdout
        );
}
