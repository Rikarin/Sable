namespace Sable.Symbols.Tests;

/// <summary>Key paths (SYNTAX.md §16): what they are, and what is refused.</summary>
public sealed class KeyPathTests {
    const string Customer = "struct Address { var city: String }\nstruct Customer { var name: String, var address: Address, var nick: String?, fn shout() -> String { return name } }\n";

    static IReadOnlyList<Diagnostic> ErrorsIn(string body) => [
        ..Compilation.Create($"module M\n{Customer}{body}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string body) {
        var errors = ErrorsIn(body);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static void AssertError(ErrorCode code, string body) {
        var errors = ErrorsIn(body);
        Assert.True(errors.Count > 0 && errors.All(e => e.Code == code), string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    [Theory]
    [InlineData("let p: KeyPath<Customer, String> = \\.address.city")]
    [InlineData("let p: KeyPath<Customer, String?> = \\.address.city")]
    [InlineData("let p: KeyPath<Customer, String>? = \\.name")]
    [InlineData("fn f() -> String { let p = \\Customer.address.city\n return p.path }")]
    [InlineData("fn f(_ all: Customer[]) -> String[>..] { return all.map(\\.name) }")]
    [InlineData("fn g<T, V>(_ t: T, _ p: KeyPath<T, V>) -> V { return p.value(of: t) }\nfn f(_ c: Customer) -> Int { return g(c, \\.name.count) }")]
    [InlineData("fn g<V>(_ p: KeyPath<Customer, V?>) -> V? { return none }\nfn f() -> String? { return g(\\.nick) }")]
    public void AKeyPathIsOfItsRootAndWhatItLeadsTo(string body) => AssertClean(body);

    [Fact]
    public void ARootMustBeKnown() {
        AssertError(ErrorCode.ERR_CannotInferType, "fn f() { let p = \\.name }");
        AssertError(ErrorCode.ERR_CannotInferType, "fn g<T, V>(_ p: KeyPath<T, V>) -> Int { return 1 }\nfn f() -> Int { return g(\\.name) }");
    }

    [Fact]
    public void AMemberMustBeThereAndBeAValue() {
        AssertError(ErrorCode.ERR_NameNotFound, "let p: KeyPath<Customer, String> = \\.nothing");
        AssertError(ErrorCode.ERR_NameNotFound, "fn f() { let p = \\Customer.address.nothing }");
        Assert.NotEmpty(ErrorsIn("let p: KeyPath<Customer, String> = \\.shout"));
        AssertError(ErrorCode.ERR_TypeMismatch, "let p: KeyPath<Customer, Int> = \\.name");
        AssertError(ErrorCode.ERR_NotOptional, "fn f() { let p = \\Customer.nick.count }");
    }

    [Fact]
    public void AKeyPathIsNoOtherTypesValue() =>
        AssertError(ErrorCode.ERR_NoMatchingOverload, "fn g(_ n: Int) { }\nfn f() { g(\\Customer.name) }");

    [Fact]
    public void AKeyPathIsPassedToTheOverloadItFits() {
        var tree = SyntaxTree.Parse(new SourceText(
            $"module M\n{Customer}struct R<T> {{\n    fn f<V>(_ p: KeyPath<T, V>) -> Int {{ return 1 }}\n    fn f<V>(_ p: KeyPath<T, V?>) -> String {{ return \"\" }}\n}}\n"
            + "fn a() -> Int { return R<Customer>().f(\\.name) }\nfn b() -> String { return R<Customer>().f(\\.nick) }\n", "test.sb"));
        Assert.DoesNotContain(Compilation.Create([tree]).GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
    }
}
