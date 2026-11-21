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
    [InlineData("Point.self", "Point.Type")]
    [InlineData("Int.self", "Int.Type")]
    [InlineData("Point?.self", "Point?.Type")]
    [InlineData("Point??.self", "Point??.Type")]
    [InlineData("Point[].self", "Point[].Type")]
    [InlineData("Point[>..].self", "Point[>..].Type")]
    [InlineData("Int[][].self", "Int[][].Type")]
    [InlineData("(Int, String).self", "(Int, String).Type")]
    [InlineData("(Int) -> Bool.self", "((Int) -> Bool).Type")]
    [InlineData("(Int) throws -> Bool.self", "((Int) throws(any Error) -> Bool).Type")]
    [InlineData("(Int | String).self", "(Int | String).Type")]
    [InlineData("any Named.self", "(any Named).Type")]
    public void NamesTheType(string body, string displayed) => AssertNamesType(body, displayed);

    static IReadOnlyList<Diagnostic> Errors(string source) =>
        Compilation.Create(source).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();

    /// <summary>A metatype is a type of its own: not the type it is of, and written 'T.Type'.</summary>
    [Fact]
    public void AMetatypeIsWrittenTDotTypeAndIsNotItsInstanceType() {
        Assert.Empty(Errors($$"""
                              {{Preamble}}
                              fn probe() { let t: Point.Type = Point.self }
                              """));

        var mismatch = Errors($$"""
                                {{Preamble}}
                                fn probe() { let p: Point = Point.self }
                                """);
        Assert.Contains(mismatch, static d => d.Message.Contains("Point.Type", StringComparison.Ordinal));
    }

    /// <summary>A generic parameter is inferred through 'T.Type', and its static members reached through the value.</summary>
    [Fact]
    public void AMetatypeParameterInfersItsTypeAndReachesStatics() {
        var compilation = Compilation.Create(
            $$"""
              {{Preamble}}
              protocol Zero { static fn zero() -> Self }
              struct Meters: Zero { var v: Int
                  static fn zero() -> Meters { return Meters(v: 0) } }
              fn make<T: Zero>(_ type: T.Type) -> T { return type.zero() }
              fn probe() { let m = make(Meters.self) }
              """
        );
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);

        var tree = compilation.SyntaxTrees[0];
        var initializer = tree.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Single().Declarator.Initializer!.Value;
        Assert.Equal("Meters", compilation.GetSemanticModel(tree).GetTypeOf(initializer)!.ToDisplayString());
    }

    /// <summary>
    ///     A metatype converts to a <c>Type</c>, the type known only at run
    ///     time, and not back; <c>type(of:)</c> gives one.
    /// </summary>
    [Fact]
    public void AMetatypeConvertsToATypeAndNotBack() {
        Assert.Empty(Errors($$"""
                              {{Preamble}}
                              fn probe(_ p: Point) -> Bool {
                                  let t: Type = Point.self
                                  let u: Type? = Int.self
                                  let keys: [Type: Int] = [Point.self: 1, type(of: p): 2]
                                  return t == type(of: p) && u != none && keys.count == 1
                              }
                              """));

        var narrowing = Errors($$"""
                                 {{Preamble}}
                                 fn probe(_ t: Type) { let p: Point.Type = t }
                                 """);
        Assert.Contains(narrowing, static d => d.Code == ErrorCode.ERR_TypeMismatch);
    }

    /// <summary>
    ///     A member the type has no static member of is the <c>Type</c>'s:
    ///     <c>Point.self.name</c>. A static member of the type stays first.
    /// </summary>
    [Fact]
    public void AMetatypeReachesTypesMembersWhereItsTypeHasNoStaticOne() {
        Assert.Empty(Errors($$"""
                              {{Preamble}}
                              struct Named2 { static var name: Int => 1 }
                              fn probe() -> Bool {
                                  let n: String = Point.self.name
                                  let s: Int = Point?.self.size
                                  let own: Int = Named2.self.name
                                  return n.count == s + own
                              }
                              """));
    }

    /// <summary>A cast is to a type written out; a value holding one is S3098.</summary>
    [Fact]
    public void ACastToATypeHeldInAValueIsAnError() {
        var errors = Errors($$"""
                              {{Preamble}}
                              fn probe(_ x: Int, _ t: Type) -> Bool { return x is t }
                              """);
        Assert.Equal(ErrorCode.ERR_RuntimeTypeInCast, Assert.Single(errors).Code);
    }

    /// <summary>A type with a nested type named 'Type' keeps it.</summary>
    [Fact]
    public void ANestedTypeNamedTypeIsStillReached() =>
        Assert.Empty(Errors("""
                            module M
                            struct Token { struct Type { var n: Int } }
                            fn probe() { let t: Token.Type = Token.Type(n: 1) }
                            """));

    [Fact]
    public void ACompoundMetatypeRoundTripsThroughTheTree() {
        const string source = "let a = Token[>..].self";
        var tree = SyntaxTree.Parse(new(source, "t.sb"));
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
