namespace Sable.Symbols.Tests;

/// <summary>
///     Aliases are transparent: a use of one is the type it names, with a
///     generic alias's parameters replaced by the arguments written at the use.
/// </summary>
public sealed class AliasTests {
    const string Preamble = """
                            module M
                            union Outcome<T, E> { Ok(value: T), Failed(error: E) }
                            union AppError { Broken }
                            alias Result<T> = Outcome<T, AppError>
                            alias Pairs<T> = (T, T)[]
                            alias Lookup<K, V> = (K) -> V?
                            alias Id<T> = U64
                            alias Count = Int
                            """;

    static Compilation Compile(string source) => Compilation.Create(Preamble + "\n" + source);

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compile(source)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    static string ParameterType(string signature) {
        var compilation = Compile($"fn probe(_ v: {signature}) {{ }}");
        var probe = compilation.GetModule("M")!.Members.OfType<FunctionSymbol>()
            .Single(static f => f.Name == "probe");
        return probe.Parameters[0].Type.ToDisplayString();
    }

    [Theory]
    [InlineData("Count", "Int")]
    [InlineData("Result<Int>", "Outcome<Int, AppError>")]
    [InlineData("Result<String>?", "Outcome<String, AppError>?")]
    [InlineData("Pairs<Int>", "(Int, Int)[]")]
    [InlineData("Pairs<Result<Int>>", "(Outcome<Int, AppError>, Outcome<Int, AppError>)[]")]
    [InlineData("Lookup<String, Int>", "(String) -> Int?")]
    [InlineData("Id<String>", "U64")]
    public void AnAliasIsTheTypeItNames(string written, string displayed) =>
        Assert.Equal(displayed, ParameterType(written));

    [Fact]
    public void AGenericAliasIsInterchangeableWithWhatItNames() =>
        AssertClean(
            """
            fn make() -> Result<Int> { return .Ok(value: 1) }
            fn take(_ r: Outcome<Int, AppError>) { }
            fn use() { take(make()) }
            """
        );

    [Fact]
    public void TwoUsesWithDifferentArgumentsAreDifferentTypes() =>
        Assert.NotEmpty(
            ErrorsIn(
                """
                fn make() -> Result<Int> { return .Ok(value: 1) }
                fn take(_ r: Result<String>) { }
                fn use() { take(make()) }
                """
            )
        );

    [Fact]
    public void AGenericAliasNeedsItsArguments() =>
        Assert.Contains(
            ErrorsIn("fn probe(_ r: Result) { }"),
            static e => e.Code == ErrorCode.ERR_WrongTypeArgumentCount
        );

    [Fact]
    public void TheArgumentCountMustMatch() =>
        Assert.Contains(
            ErrorsIn("fn probe(_ r: Result<Int, String>) { }"),
            static e => e.Code == ErrorCode.ERR_WrongTypeArgumentCount
        );

    [Fact]
    public void ANonGenericAliasTakesNoArguments() =>
        Assert.Contains(
            ErrorsIn("fn probe(_ c: Count<Int>) { }"),
            static e => e.Code == ErrorCode.ERR_TypeArgumentsOnNonGeneric
        );

    [Fact]
    public void AGenericAliasMayBeUsedInsideAGeneric() =>
        AssertClean(
            """
            fn first<T>(_ pairs: Pairs<T>) -> T? {
                if pairs.count == 0 { return none }
                return pairs[0].0
            }
            """
        );

    [Fact]
    public void AGenericTypeWithArgumentsMayBeNamedInAnExpression() =>
        AssertClean(
            """
            fn make() -> Outcome<Int, AppError> { return Outcome<Int, AppError>.Ok(value: 1) }
            fn viaAlias() -> Result<Int> { return Result<Int>.Ok(value: 2) }
            """
        );

    const string Pair = """
                        struct Pair<A, B> {
                            var first: A
                            var second: B
                            init(_ first: A, _ second: B, count: Int = 0) { self.first = first; self.second = second }
                        }
                        alias Named<T> = Pair<String, T>
                        alias Both<T: Equatable> = Pair<T, T>
                        """;

    /// <summary>An initialiser called through a generic alias named bare infers the alias's parameters.</summary>
    [Theory]
    [InlineData("fn use() -> Pair<String, Int> { return Named(\"a\", 4) }")]
    [InlineData("fn use() -> Pair<Int, Int> { let b = Both(4, 5); return b }")]
    [InlineData("fn use() -> Pair<String, F64> { return Named(\"a\", 4.5, count: 3) }")]
    [InlineData("fn use() { let n: Named<I8> = Named(\"a\", 4) }")]
    public void AGenericAliasIsMadeWithoutItsArguments(string source) => AssertClean(Pair + "\n" + source);

    [Theory]
    [InlineData("struct Plain { }\nfn use() { let b = Both(Plain(), Plain()) }", ErrorCode.ERR_DoesNotConform)]
    [InlineData("fn use() { let b = Both() }", ErrorCode.ERR_CannotInferTypeArgument)]
    public void AGenericAliasMadeWithoutItsArgumentsIsChecked(string source, ErrorCode code) =>
        Assert.Contains(ErrorsIn(Pair + "\n" + source), e => e.Code == code);

    // ---- Building a generic union's cases -------------------------------

    const string Box = "union Box<T> { Full(value: T), Empty }";

    [Theory]
    [InlineData("fn make() -> Outcome<Int, AppError> { return .Ok(value: 1) }")]
    [InlineData("fn make() -> Outcome<Int, AppError> { return Outcome.Ok(value: 1) }")]
    [InlineData("fn make() -> Outcome<Int, AppError> { return Outcome<Int, AppError>.Ok(value: 1) }")]
    [InlineData("fn make() -> Box<Int> { return .Empty }")]
    [InlineData("fn make() -> Box<Int>? { return .Full(value: 1) }")]
    [InlineData("fn take(_ b: Box<Int>) { }\nfn use() { take(.Full(value: 1)) }")]
    [InlineData("fn make() -> Result<Int> { return Result<Int>.Failed(error: .Broken) }")]
    public void AGenericUnionCaseTakesItsArgumentsFromTheContext(string source) => AssertClean(Box + "\n" + source);

    // ---- A generic alias's constraints ----------------------------------

    /// <summary>
    ///     'alias Channel<T: Sendable> = Queue<T, Never>': the constraint
    ///     written on the alias's parameter holds in what it names, which
    ///     requires it — and a use with a type that does not meet it is
    ///     refused, as a use of the named type would be.
    /// </summary>
    [Fact]
    public void AGenericAliasParametersConstraintHoldsInWhatItNames() {
        const string declarations = """
                                    final class Queue<T: Sendable, E: Error> { init() { } }
                                    alias Channel<T: Sendable> = Queue<T, Never>
                                    """;
        AssertClean(declarations + "\nfn make() -> Channel<Int> { return Channel<Int>() }");
        Assert.Contains(
            ErrorsIn(declarations + "\nclass Shared { var n = 0 }\nfn make() -> Channel<Shared> { return Channel<Shared>() }"),
            static e => e.Message.Contains("Sendable")
        );
    }

    /// <summary>
    ///     An extension's 'where Self: Sendable, Element: Sendable' makes a
    ///     protocol's Self and its associated types Sendable in its members,
    ///     as a type parameter's own constraint would.
    /// </summary>
    [Fact]
    public void AnExtensionsWhereClauseMakesItsTypesSendable() =>
        AssertClean(
            """
            struct Pump<S: Sequence> where S: Sendable, S.Element: Sendable { let base: S }
            extension Sequence where Self: Sendable, Element: Sendable {
                fn pumped() -> Pump<Self> { return Pump<Self> { base: self } }
            }
            """
        );
}
