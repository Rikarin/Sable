using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Unspecialised generic code (#297 stage 4): each program is built with
///     its generic functions compiled once for every type — boxes, metadata
///     and witness tables — and again specialised, and both run natively and
///     in the interpreter, leak-checked; all four must print the same.
/// </summary>
[Native]
public sealed class UnspecialisedGenericTests {
    static void Prints(string expected, string declarations, string body) {
        var program = Program(declarations, body);
        string unspecialised;
        using (SilGen.UseGenerics(GenericsMode.Unspecialised)) {
            unspecialised = AssertAgree(program).Stdout;
        }

        string specialised;
        using (SilGen.UseGenerics(GenericsMode.Specialised)) {
            specialised = AssertAgree(program).Stdout;
        }

        Assert.Equal(expected, specialised);
        Assert.Equal(specialised, unspecialised);

        // And the program's own generic functions were compiled once, not specialised.
        Assert.Contains(Unspecialised(declarations, body), static f => f.StartsWith("Test.", StringComparison.Ordinal));
    }

    /// <summary>The functions a program's unspecialised build compiles once: their SIL names.</summary>
    static List<string> Unspecialised(string declarations, string body) {
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        var result = Pipeline.Compile(new SourceText(Program(declarations, body), "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        return [..result.Sil!.Functions.Select(static f => f.Name).Where(static n => n.EndsWith("$generic", StringComparison.Ordinal))];
    }

    const string Shapes = """
        protocol Shape {
            fn area() -> Int
            var name: String { get }
            static var sides: Int { get }
        }

        struct Square: Shape {
            var side: Int
            fn area() -> Int => side * side
            var name: String => "square"
            static var sides: Int => 4
        }

        final class Triangle: Shape {
            var base: Int
            var height: Int
            init(base: Int, height: Int) {
                self.base = base
                self.height = height
            }
            fn area() -> Int => base * height / 2
            var name: String => "triangle"
            static var sides: Int => 3
        }

        enum Dot: Shape {
            Small, Large

            fn area() -> Int => 1
            var name: String => "dot"
            static var sides: Int => 0
        }

        fn describe<S: Shape>(_ shape: S) -> String {
            return "\(shape.name) of \(S.sides) sides, area \(shape.area())"
        }

        fn larger<S: Shape>(_ a: S, _ b: S) -> S {
            return a.area() >= b.area() ? a : b
        }

        fn both<A: Shape, B: Shape>(_ a: A, _ b: B) -> String {
            var text = describe(a)
            text = text + "; " + describe(b)
            return text
        }
        """;

    [Fact]
    public void RequirementsAndPropertiesOfStructsClassesAndEnums() =>
        Prints(
            "square of 4 sides, area 9\ntriangle of 3 sides, area 6\ndot of 0 sides, area 1\n"
            + "square of 4 sides, area 25\ntriangle of 3 sides, area 10\nsquare of 4 sides, area 4; dot of 0 sides, area 1\n",
            Shapes,
            """
                print(describe(Square { side: 3 }))
                print(describe(Triangle(base: 4, height: 3)))
                print(describe(Dot.Large))
                print(describe(larger(Square { side: 2 }, Square { side: 5 })))
                print(describe(larger(Triangle(base: 4, height: 5), Triangle(base: 2, height: 2))))
                print(both(Square { side: 2 }, Dot.Small))
            """);

    [Fact]
    public void EachGenericFunctionIsCompiledOnce() {
        var functions = Unspecialised(Shapes, """
                print(describe(Square { side: 3 }))
                print(describe(Triangle(base: 4, height: 3)))
                print(both(Square { side: 2 }, Dot.Small))
            """);
        Assert.Contains("Test.describe$generic", functions);
        Assert.Contains("Test.both$generic", functions);
    }

    [Fact]
    public void StaticRequirementsOperatorsAndMetatypes() =>
        Prints(
            "0 5\ntrue false\nTest.Meters Int\n<7> <seven> <Meters(value: 7)>\n",
            """
            protocol Measure: Equatable {
                static fn zero() -> Self
                operator + (a: Self, b: Self) -> Self
                var value: Int { get }
            }

            struct Meters: Measure {
                var value: Int
                static fn zero() -> Meters => Meters { value: 0 }
                operator + (a: Meters, b: Meters) -> Meters => Meters { value: a.value + b.value }
                operator == (a: Meters, b: Meters) -> Bool => a.value == b.value
            }

            fn make<T: Measure>(_ type: T.Type) -> T { return type.zero() }

            fn total<T: Measure>(_ a: T, _ b: T, _ c: T) -> T { return a + b + c + T.zero() }

            fn same<T: Measure>(_ a: T, _ b: T) -> Bool { return a == b }

            fn isInt<T>(_ value: T) -> Bool { return T.self == Int.self }

            fn kind<T>(_ value: T) -> Type { return type(of: value) }

            fn show<T>(_ value: T) -> String { return "<\(value)>" }
            """,
            """
                print("\(make(Meters.self).value) \(total(Meters { value: 2 }, Meters { value: 3 }, Meters { value: 0 }).value)")
                print("\(same(Meters { value: 1 }, Meters { value: 1 })) \(isInt("one"))")
                print("\(kind(Meters { value: 1 })) \(kind(3))")
                print("\(show(7)) \(show("seven")) \(show(Meters { value: 7 }))")
            """);

    [Fact]
    public void AValueChangedInGenericCodeIsItsOwn() =>
        Prints(
            "1 3\n10 12\n",
            """
            protocol Counter {
                mutating fn bump()
                fn count() -> Int
            }

            struct Clicks: Counter {
                var n: Int
                mutating fn bump() { n = n + 1 }
                fn count() -> Int => n
            }

            final class Tally: Counter {
                var n: Int
                init(n: Int) { self.n = n }
                fn bump() { n = n + 1 }
                fn count() -> Int => n
            }

            fn bumped<C: Counter>(_ counter: C) -> C {
                var copy = counter
                copy.bump()
                copy.bump()
                return copy
            }
            """,
            """
                let start = Clicks { n: 1 }
                let after = bumped(start)
                print("\(start.count()) \(after.count())")
                let tally = Tally(n: 10)
                let same = bumped(tally)
                print("\(10) \(same.count())")
            """);

    [Fact]
    public void ARequirementThatThrowsThrowsThroughGenericCode() =>
        Prints(
            "4\nfailed: too big\n",
            """
            union Problem: Error {
                TooBig
            }

            protocol Checked {
                fn checked() throws(Problem) -> Int
            }

            struct Small: Checked {
                var n: Int
                fn checked() throws(Problem) -> Int {
                    if n > 9 { throw Problem.TooBig }
                    return n
                }
            }

            fn run<C: Checked>(_ value: C) throws(Problem) -> Int { return try value.checked() }
            """,
            """
                print(try! run(Small { n: 4 }))
                do {
                    print(try run(Small { n: 40 }))
                } catch {
                    print("failed: too big")
                }
            """);

    /// <summary>A generic function that recurses, and one refining protocol reached through another's table, by the registry.</summary>
    [Fact]
    public void RecursionAndRefinedProtocols() =>
        Prints(
            "6 6\nhello hello hello \n",
            """
            protocol Named {
                fn name() -> String
            }

            protocol Greeter: Named {
                fn greeting() -> String
            }

            struct English: Greeter {
                fn name() -> String => "hello"
                fn greeting() -> String => "hi"
            }

            fn nameOf<N: Named>(_ value: N) -> String { return value.name() }

            fn repeated<G: Greeter>(_ value: G, _ times: Int) -> String {
                if times == 0 { return "" }
                return nameOf(value) + " " + repeated(value, times - 1)
            }

            fn sum<T: AdditiveArithmetic>(_ a: T, _ n: Int) -> T {
                if n == 0 { return a }
                return a + sum(a, n - 1)
            }
            """,
            """
                print("\(sum(2, 2)) \(sum(1, 5))")
                print(repeated(English(), 3))
            """);

    const string Extended = """
        protocol Shape {
            fn area() -> Int
            mutating fn grow()
        }

        extension Shape {
            fn doubled() -> Int => area() * 2

            fn describe() -> String { return "area \(area()), doubled \(self.doubled())" }

            mutating fn growTwice() {
                grow()
                self.grow()
            }

            fn same(as other: Self) -> Bool { return area() == other.area() }

            fn bigger(_ other: Self) -> Self { return area() >= other.area() ? self : other }
        }

        struct Square: Shape {
            var side: Int
            fn area() -> Int => side * side
            mutating fn grow() { side = side + 1 }
        }

        final class Circle: Shape {
            var r: Int
            init(r: Int) { self.r = r }
            fn area() -> Int => 3 * r * r
            fn grow() { r = r + 1 }
        }

        fn report<S: Shape>(_ shape: S) -> String { return shape.describe() }
        """;

    const string UsesExtended = """
            var square = Square { side: 2 }
            print(square.describe())
            square.growTwice()
            print(square.doubled())
            print(report(Circle(r: 1)))
            print(square.same(as: Square { side: 4 }))
            print(square.bigger(Square { side: 9 }).area())
        """;

    /// <summary>
    ///     A protocol extension's members are generic over Self: compiled once,
    ///     given self where its caller holds it — its storage, for a mutating
    ///     one — and Self's metadata and table; one calls another on self.
    /// </summary>
    [Fact]
    public void ProtocolExtensionMembersAreCompiledOnce() {
        Prints("area 4, doubled 8\n32\narea 3, doubled 6\ntrue\n81\n", Extended, UsesExtended);
        var functions = Unspecialised(Extended, UsesExtended);
        foreach (var member in new[] { "doubled", "describe", "growTwice", "same", "bigger" }) {
            Assert.Contains($"Test.Shape.{member}$generic", functions);
        }
    }
}
