using Sable.Symbols;

namespace Sable.Symbols.Tests;

/// <summary>
/// Aliases are transparent: a use of one is the type it names, with a
/// generic alias's parameters replaced by the arguments written at the use.
/// </summary>
public sealed class AliasTests
{
    private const string Preamble = """
        module M
        union Outcome<T, E> { Ok(value: T), Failed(error: E) }
        union AppError { Broken }
        alias Result<T> = Outcome<T, AppError>
        alias Pairs<T> = (T, T)[]
        alias Lookup<K, V> = (K) -> V?
        alias Id<T> = U64
        alias Count = Int
        """;

    private static Compilation Compile(string source) =>
        Compilation.Create(Preamble + "\n" + source);

    private static IReadOnlyList<Diagnostic> ErrorsIn(string source) =>
        [.. Compile(source).GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)];

    private static void AssertClean(string source)
    {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message}")));
    }

    private static string ParameterType(string signature)
    {
        var compilation = Compile($"fn probe(_ v: {signature}) {{ }}");
        var probe = compilation.GetModule("M")!.Members.OfType<FunctionSymbol>()
            .Single(f => f.Name == "probe");
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
    public void AGenericAliasIsInterchangeableWithWhatItNames() => AssertClean("""
        fn make() -> Result<Int> { return .Ok(value: 1) }
        fn take(_ r: Outcome<Int, AppError>) { }
        fn use() { take(make()) }
        """);

    [Fact]
    public void TwoUsesWithDifferentArgumentsAreDifferentTypes() =>
        Assert.NotEmpty(ErrorsIn("""
            fn make() -> Result<Int> { return .Ok(value: 1) }
            fn take(_ r: Result<String>) { }
            fn use() { take(make()) }
            """));

    [Fact]
    public void AGenericAliasNeedsItsArguments() =>
        Assert.Contains(ErrorsIn("fn probe(_ r: Result) { }"),
            e => e.Code == ErrorCode.ERR_WrongTypeArgumentCount);

    [Fact]
    public void TheArgumentCountMustMatch() =>
        Assert.Contains(ErrorsIn("fn probe(_ r: Result<Int, String>) { }"),
            e => e.Code == ErrorCode.ERR_WrongTypeArgumentCount);

    [Fact]
    public void ANonGenericAliasTakesNoArguments() =>
        Assert.Contains(ErrorsIn("fn probe(_ c: Count<Int>) { }"),
            e => e.Code == ErrorCode.ERR_TypeArgumentsOnNonGeneric);

    [Fact]
    public void AGenericAliasMayBeUsedInsideAGeneric() => AssertClean("""
        fn first<T>(_ pairs: Pairs<T>) -> T? {
            if pairs.count == 0 { return none }
            return pairs[0].0
        }
        """);

    [Fact]
    public void AGenericTypeWithArgumentsMayBeNamedInAnExpression() => AssertClean("""
        fn make() -> Outcome<Int, AppError> { return Outcome<Int, AppError>.Ok(value: 1) }
        fn viaAlias() -> Result<Int> { return Result<Int>.Ok(value: 2) }
        """);

    // ---- Building a generic union's cases -------------------------------

    private const string Box = "union Box<T> { Full(value: T), Empty }";

    [Theory]
    [InlineData("fn make() -> Outcome<Int, AppError> { return .Ok(value: 1) }")]
    [InlineData("fn make() -> Outcome<Int, AppError> { return Outcome.Ok(value: 1) }")]
    [InlineData("fn make() -> Outcome<Int, AppError> { return Outcome<Int, AppError>.Ok(value: 1) }")]
    [InlineData("fn make() -> Box<Int> { return .Empty }")]
    [InlineData("fn make() -> Box<Int>? { return .Full(value: 1) }")]
    [InlineData("fn take(_ b: Box<Int>) { }\nfn use() { take(.Full(value: 1)) }")]
    [InlineData("fn make() -> Result<Int> { return Result<Int>.Failed(error: .Broken) }")]
    public void AGenericUnionCaseTakesItsArgumentsFromTheContext(string source) =>
        AssertClean(Box + "\n" + source);
}
