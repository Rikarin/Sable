namespace Sable.Symbols.Tests;

/// <summary>
///     <c>T.self</c>, including the compound types that do not parse as
///     expressions on their own and so need the metatype path.
/// </summary>
public sealed class MetatypeTests {
    const string Preamble = """
                            module M
                            struct Point { var x: Int, var y: Int }
                            protocol Named { fn name() -> String }
                            """;

    static Compilation Compile(string body) =>
        Compilation.Create(
            $$"""
              {{Preamble}}
              fn probe() { let t = {{body}} }
              """
        );

    static void AssertNamesType(string body, string displayed) {
        var compilation = Compile(body);

        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );

        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);

        var initializer = tree.Root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .Single()
            .Declarator.Initializer!.Value;

        Assert.Equal(displayed, model.GetTypeOf(initializer)!.ToDisplayString());
    }

    [Theory]
    [InlineData("Point.self", "Point")]
    [InlineData("Int.self", "Int")]
    [InlineData("Point?.self", "Point?")]
    [InlineData("Point??.self", "Point??")]
    [InlineData("Point[].self", "Point[]")]
    [InlineData("Point[>..].self", "Point[>..]")]
    [InlineData("Int[][].self", "Int[][]")]
    [InlineData("(Int, String).self", "(Int, String)")]
    [InlineData("(Int) -> Bool.self", "(Int) -> Bool")]
    [InlineData("(Int) throws -> Bool.self", "(Int) throws(any Error) -> Bool")]
    [InlineData("(Int | String).self", "Int | String")]
    [InlineData("any Named.self", "any Named")]
    public void NamesTheType(string body, string displayed) => AssertNamesType(body, displayed);

    [Fact]
    public void ACompoundMetatypeRoundTripsThroughTheTree() {
        const string source = "let a = Token[>..].self";
        var tree = SyntaxTree.Parse(new(source, "t.sab"));
        Assert.Equal(source, tree.Root.ToFullString());
    }

    [Fact]
    public void AnIndexIsStillAnIndex() {
        var compilation = Compilation.Create(
            $$"""
              {{Preamble}}
              fn probe(_ xs: Int[]) -> Int { return xs[0] }
              """
        );

        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static d => d.Severity == DiagnosticSeverity.Error
        );
    }

    [Fact]
    public void AnOptionalChainIsStillAnOptionalChain() {
        var compilation = Compilation.Create(
            $$"""
              {{Preamble}}
              fn probe(_ p: Point?) -> Int? { return p?.x }
              """
        );

        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static d => d.Severity == DiagnosticSeverity.Error
        );
    }

    [Fact]
    public void ATernaryIsStillATernary() {
        var compilation = Compilation.Create(
            $$"""
              {{Preamble}}
              fn probe(_ n: Int) -> Int { return n > 0 ? n : 0 }
              """
        );

        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static d => d.Severity == DiagnosticSeverity.Error
        );
    }
}
