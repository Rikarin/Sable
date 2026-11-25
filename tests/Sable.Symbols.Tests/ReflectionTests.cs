namespace Sable.Symbols.Tests;

/// <summary>
///     <c>@reflectable</c> (docs/06 §6.3): a conformance to Sable.Core's
///     <c>Reflectable</c>, which <c>TypeInfo.of</c> asks of its argument.
///     The library's reflection file is part of every compilation, as the
///     rest of Sable.Core is.
/// </summary>
public sealed class ReflectionTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void TheLibraryBindsCleanly() => AssertClean("");

    [Fact]
    public void AReflectableTypeHasTypeInfo() =>
        AssertClean("""
                    @reflectable
                    struct Point { var x: Int }
                    fn probe() -> String { return TypeInfo.of(Point.self).name }
                    """);

    [Fact]
    public void ATypeThatIsNotReflectableHasNone() {
        var errors = ErrorsIn("""
                              struct Point { var x: Int }
                              fn probe() -> String { return TypeInfo.of(Point.self).name }
                              """);
        var error = Assert.Single(errors, static e => e.Code == ErrorCode.ERR_NoMatchingOverload);
        Assert.Contains("'Point', which is not 'Reflectable'", error.Message);
    }

    [Fact]
    public void ConformingByHandIsTheSameAsTheAttribute() =>
        AssertClean("""
                    struct Point: Reflectable { var x: Int }
                    @reflectable
                    struct Both: Reflectable { var y: Int }
                    fn probe() -> String { return TypeInfo.of(Point.self).name + TypeInfo.of(Both.self).name }
                    """);

    [Fact]
    public void GenericCodeAsksForReflectableToo() {
        AssertClean("""
                    fn name<T: Reflectable>(_ value: T) -> String { return TypeInfo.of(value).name }
                    @reflectable
                    class Model { var id: Int = 0 }
                    fn probe() -> String { return name(Model()) }
                    """);

        Assert.Contains(
            ErrorsIn("fn name<T>(_ value: T) -> String { return TypeInfo.of(value).name }"),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );
    }

    [Fact]
    public void AMirrorNeedsNothing() =>
        AssertClean("""
                    struct Point { var x: Int }
                    fn probe() -> String { return Mirror(of: Point(x: 1)).description + Reflect.describe(3) }
                    """);

    [Theory]
    [InlineData("@reflectable\nprotocol Named { }", "'Named' is a protocol")]
    public void OnlyATypeWithValuesCanBeReflectable(string source, string message) {
        var error = Assert.Single(ErrorsIn(source));
        Assert.Equal(ErrorCode.ERR_NotReflectableKind, error.Code);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void ACallableTypeConformsToCallable() =>
        AssertClean("""
                    @reflectable(callable)
                    public actor Tally {
                        public init() { }
                        public fn add(_ n: Int) -> Int => n
                    }
                    @reflectable(callable)
                    union Shape {
                        Dot
                        public fn area() -> F64 => 0
                    }
                    @reflectable(callable)
                    struct Box<T> {
                        var item: T
                        public fn get() -> T => item
                    }
                    fn first<T: Callable>(_ type: T.Type) -> String => T.callableMembers()[0].name
                    fn probe() -> String => first(Tally.self) + first(Shape.self) + first(Box<Int>.self) + TypeInfo.of(Shape.self).name
                    """);

    [Fact]
    public void AReflectableTypeIsCallableOnlyIfItAsks() {
        var errors = ErrorsIn("""
                              @reflectable
                              struct Point { var x: Int }
                              fn probe() -> Int => Point.callableMembers().count
                              """);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void AMovedTypeIsNotCallable() {
        var error = Assert.Single(ErrorsIn("""
                                           @noncopy
                                           @reflectable(callable)
                                           struct Handle { public fn close() { } }
                                           """));
        Assert.Equal(ErrorCode.ERR_NotReflectableKind, error.Code);
    }

    [Theory]
    [InlineData("@reflectable(invocable)")]
    [InlineData("@reflectable()")]
    [InlineData("@reflectable(callable, 1)")]
    public void ReflectableTakesNothingOrCallable(string attribute) {
        var error = Assert.Single(ErrorsIn($"{attribute}\nstruct Point {{ var x: Int }}"));
        Assert.Contains("'@reflectable(callable)'", error.Message);
    }
}
