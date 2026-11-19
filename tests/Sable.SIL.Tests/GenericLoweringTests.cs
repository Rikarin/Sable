namespace Sable.SIL.Tests;

/// <summary>
///     Generics by monomorphisation (docs/04 §2.2, item 6): each generic
///     function and type is lowered once per set of type arguments it is used
///     with, and every instance is ordinary SIL.
/// </summary>
public sealed class GenericLoweringTests {
    static SilModule Lower(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));

        var problems = SilVerifier.Verify(module);
        Assert.True(problems.Count == 0, string.Join("\n", problems) + "\n\n" + SilPrinter.Print(module));

        var text = SilPrinter.Print(module);
        var reparsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(reparsed));

        SilPassManager.Run(reparsed, SilPassManager.Default);
        return reparsed;
    }

    static object? Run(SilModule module, string function, params object[] arguments) {
        var interpreter = new SilInterpreter(module);
        var result = interpreter.Run($"M.{function}", arguments);
        Assert.True(interpreter.LiveObjects == 0, $"{interpreter.LiveObjects} object(s) leaked");
        return result;
    }

    /// <summary>
    ///     Where a protocol's extension overloads a requirement's name, the
    ///     requirement's default is the overload with its labels, not the
    ///     first of the name: 'make(seed:)', not the generic 'make(_:)'.
    /// </summary>
    [Fact]
    public void ARequirementsDefaultIsTheOverloadWithItsLabels() {
        var module = Lower(
            """
            protocol Seeded {
                static var origin: Self { get }
                static fn make(seed: Int) -> Self
            }
            extension Seeded {
                static fn make<T: Seeded>(_ other: T) -> Self { return Self.origin }
                static fn make(seed: Int) -> Self { return Self.origin }
            }
            struct Box: Seeded {
                var n: Int
                static var origin: Box => Box { n: 3 }
            }
            fn build<T: Seeded>(_ type: T.Type) -> T { return T.make(seed: 5) }
            fn test() -> Int { return build(Box.self).n }
            """
        );
        Assert.Equal(3L, Run(module, "test"));
    }

    /// <summary>
    ///     A protocol extension's property read on a value of another
    ///     conformer, inside an instance where Self is the first: the
    ///     value's own type is its Self, not the instance's.
    /// </summary>
    [Fact]
    public void AnExtensionsPropertyIsReadAsTheReceiversOwn() {
        var module = Lower(
            """
            protocol Named {
                static var name: String { get }
            }
            extension Named {
                var label: String => Self.name
                fn both(_ other: B) -> String { return self.label + other.label }
            }
            struct A: Named {
                static var name: String => "A"
            }
            struct B: Named {
                static var name: String => "B"
            }
            fn test() -> String { return A().both(B()) }
            """
        );
        Assert.Equal("AB", Run(module, "test"));
    }

    [Fact]
    public void AGenericFunctionIsLoweredOncePerTypeArgument() {
        var module = Lower(
            """
            fn identity<T>(_ value: T) -> T {
                return value
            }

            fn test() -> String {
                let n = identity(41) + identity(1)
                return identity("n=") + "\(n)"
            }
            """
        );

        Assert.Equal("n=42", Run(module, "test"));
        Assert.NotNull(module.Find("M.identity$of$Int"));
        Assert.NotNull(module.Find("M.identity$of$String"));
        Assert.Null(module.Find("M.identity"));
    }

    [Fact]
    public void GenericStructsUnionsAndTheirMethods() {
        var module = Lower(
            """
            struct Box<T> {
                var item: T

                fn get() -> T {
                    return item
                }

                fn replaced(with value: T) -> Box<T> {
                    return Box<T> { item: value }
                }
            }

            union Outcome<T, E> {
                Ok(T)
                Err(E)
            }

            fn unwrapOr<T, E>(_ outcome: Outcome<T, E>, _ fallback: T) -> T {
                return match outcome {
                    case .Ok(let value) => value
                    case .Err(_) => fallback
                }
            }

            fn test() -> String {
                let b = Box<Int> { item: 7 }
                let s = Box<String> { item: "seven" }
                let ok: Outcome<String, Int> = Outcome.Ok("fine")
                let bad: Outcome<String, Int> = Outcome.Err(3)
                return "\(b.replaced(with: b.get() * 6).item) \(s.get()) \(unwrapOr(ok, "?")) \(unwrapOr(bad, "?"))"
            }
            """
        );

        Assert.Equal("42 seven fine ?", Run(module, "test"));
    }

    [Fact]
    public void GenericsOverArraysAndOptionals() {
        var module = Lower(
            """
            fn firstOr<T>(_ items: T[>..], _ fallback: T) -> T {
                for item in items {
                    return item
                }
                return fallback
            }

            fn lastOf<T>(_ items: T[>..]) -> T? {
                if items.count == 0 {
                    return none
                }
                return items[items.count - 1]
            }

            fn reversed<T>(_ items: T[>..]) -> T[>..] {
                var result: T[>..] = []
                var i = items.count - 1
                while i >= 0 {
                    result.append(items[i])
                    i = i - 1
                }
                return result
            }

            fn test() -> String {
                let names: String[>..] = ["a", "b", "c"]
                let empty: Int[>..] = []
                let back = reversed(names)
                return firstOr(names, "none") + "\(firstOr(empty, 9))" + (lastOf(names) ?? "?") + back[0] + back[2]
            }
            """
        );

        Assert.Equal("a9cca", Run(module, "test"));
    }

    [Fact]
    public void AProtocolRequirementCalledOnATypeParameterReachesTheConcreteMember() {
        var module = Lower(
            """
            protocol Shape {
                fn area() -> Int
                fn name() -> String
            }

            struct Square: Shape {
                var side: Int
                fn area() -> Int { return side * side }
                fn name() -> String { return "square" }
            }

            struct Rect: Shape {
                var w: Int
                var h: Int
                fn area() -> Int { return w * h }
                fn name() -> String { return "rect" }
            }

            fn describe<S: Shape>(_ shape: S) -> String {
                return shape.name() + " \(shape.area())"
            }

            fn total<S: Shape>(_ shapes: S[>..]) -> Int {
                var sum = 0
                for s in shapes {
                    sum = sum + s.area()
                }
                return sum
            }

            fn test() -> String {
                let squares: Square[>..] = [Square { side: 2 }, Square { side: 3 }]
                return describe(Square { side: 4 }) + ", " + describe(Rect { w: 2, h: 5 }) + ", \(total(squares))"
            }
            """
        );

        Assert.Equal("square 16, rect 10, 13", Run(module, "test"));
    }

    [Fact]
    public void ExtensionMembersLower() {
        var module = Lower(
            """
            struct Point {
                var x: Int
                var y: Int
            }

            struct Box<Item> {
                var item: Item
            }

            extension Point {
                fn magnitudeSquared() -> Int {
                    return x * x + y * y
                }

                static fn origin() -> Point {
                    return Point { x: 0, y: 0 }
                }
            }

            extension Box<T> {
                fn held() -> T {
                    return item
                }
            }

            fn test() -> Int {
                let p = Point { x: 3, y: 4 }
                let b = Box<Int> { item: 100 }
                return p.magnitudeSquared() + Point.origin().x + b.held()
            }
            """
        );

        Assert.Equal(125L, Run(module, "test"));
    }

    // ---- Polymorphic recursion ---------------------------------------------

    /// <summary>The lowering diagnostics of a module the front end accepts.</summary>
    static List<Diagnostic> LoweringErrors(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        return [..SilGen.Lower(compilation).Diagnostics];
    }

    [Theory]
    [InlineData(
        // Each call doubles the type: Int, (Int, Int), ((Int, Int), (Int, Int))…
        """
        fn deep<T>(_ x: T, _ n: Int) -> Int {
            if n == 0 { return 0 }
            return 1 + deep((x, x), n - 1)
        }
        fn test() -> Int { return deep(1, 3) }
        """
    )]
    [InlineData(
        // The same, through a second function and an array.
        """
        fn there<T>(_ x: T, _ n: Int) -> Int {
            if n == 0 { return 0 }
            return 1 + back([x], n - 1)
        }
        fn back<T>(_ x: T, _ n: Int) -> Int { return there(x, n) }
        fn test() -> Int { return there(1, 3) }
        """
    )]
    [InlineData(
        // A result of the type parameter's, taken back out of its box as
        // the type each call gives it.
        """
        fn wrap<T>(_ x: T, _ n: Int) -> T {
            if n == 0 { return x }
            let y = wrap((x, n), n - 1)
            return y.0
        }
        fn test() -> Int { return wrap(3, 5) }
        """
    )]
    public void PolymorphicRecursionIsCompiledOnceForEveryType(string source) {
        // The copies for Int and for (Int, Int) are made as usual; the one
        // that would ask for an endless series instead calls the instance
        // that serves every type, which then calls itself.
        Assert.Empty(LoweringErrors(source));
        var module = Lower(source);
        Assert.Contains(module.Functions, static f => f.Name.EndsWith("$polymorphic", StringComparison.Ordinal));
        Assert.Equal(3L, Run(module, "test"));
    }

    [Theory]
    [InlineData(
        // A parameter that holds the type parameter without being it: each
        // element would need a box of its own.
        """
        fn spread<T>(_ xs: T[], _ n: Int) -> Int {
            if n == 0 { return 0 }
            return spread([xs], n - 1)
        }
        fn test() -> Int { return spread([1], 3) }
        """
    )]
    [InlineData(
        // A generic type's method that asks for a copy of itself one level deeper.
        """
        struct Nest<T> {
            let inner: T
            fn depth(_ n: Int) -> Int {
                if n == 0 { return 0 }
                let next = Nest<Nest<T>> { inner: Nest<T> { inner: inner } }
                return 1 + next.depth(n - 1)
            }
        }
        fn test() -> Int { return Nest<Int> { inner: 1 }.depth(3) }
        """
    )]
    public void PolymorphicRecursionThatCannotBeBoxedIsReportedNotInstantiatedForever(string source) {
        var errors = LoweringErrors(source);
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_EndlessInstantiation);
    }

    [Fact]
    public void ARecursionThatSettlesOnOneTypeIsNotPolymorphicRecursion() {
        // 'show<String>' asks for 'show<Int>', which asks only for itself.
        var module = Lower(
            """
            fn show<T>(_ x: T, _ n: Int) -> Int {
                if n == 0 { return 0 }
                return 1 + show(n, n - 1)
            }
            fn test() -> Int { return show("start", 4) }
            """
        );

        Assert.Equal(4L, Run(module, "test"));
    }
}
