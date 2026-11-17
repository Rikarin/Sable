using System.Text;

namespace Sable.Syntax.Tests;

/// <summary>
///     Conditional compilation, per spec/lexical.md §9. Inactive regions become
///     <see cref="SyntaxKind.DisabledTextTrivia" />: they are never parsed, but
///     they are still in the tree, so the file round-trips whichever branch is
///     taken.
/// </summary>
public sealed class DirectiveTests {
    static SyntaxTree Parse(string source, params string[] symbols) =>
        SyntaxTree.Parse(
            new(source, "<test>"),
            ParseOptions.Default.WithSymbols(symbols)
        );

    static IReadOnlyList<string> MemberNames(SyntaxTree tree) => [
        ..((CompilationUnitSyntax)tree.Root).Members
            .OfType<FunctionDeclarationSyntax>()
            .Select(static f => f.Identifier.Text)
    ];

    static string DisabledText(SyntaxTree tree) {
        var builder = new StringBuilder();
        foreach (var token in tree.Root.DescendantTokens().Append(tree.EndOfFileToken)) {
            foreach (var trivia in token.LeadingTrivia) {
                if (trivia.Kind == SyntaxKind.DisabledTextTrivia) {
                    builder.Append(trivia.Text);
                }
            }

            foreach (var trivia in token.TrailingTrivia) {
                if (trivia.Kind == SyntaxKind.DisabledTextTrivia) {
                    builder.Append(trivia.Text);
                }
            }
        }

        return builder.ToString();
    }

    const string Sample = """
                          #if DEBUG
                          fn debugOnly() { }
                          #else
                          fn releaseOnly() { }
                          #endif
                          fn always() { }
                          """;

    [Fact]
    public void TakesTheActiveBranch() {
        var tree = Parse(Sample, "DEBUG");
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(["debugOnly", "always"], MemberNames(tree));
    }

    [Fact]
    public void TakesTheElseBranchWhenTheSymbolIsUndefined() {
        var tree = Parse(Sample);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(["releaseOnly", "always"], MemberNames(tree));
    }

    [Fact]
    public void BothBranchesRoundTripExactly() {
        Assert.Equal(Sample, Parse(Sample, "DEBUG").ToFullString());
        Assert.Equal(Sample, Parse(Sample).ToFullString());
    }

    [Fact]
    public void TheInactiveBranchBecomesDisabledText() {
        var tree = Parse(Sample, "DEBUG");
        Assert.Contains("releaseOnly", DisabledText(tree), StringComparison.Ordinal);
        Assert.DoesNotContain("debugOnly", DisabledText(tree), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledTextIsNeverParsed() {
        // Syntactic nonsense in the inactive branch must not produce errors.
        const string source = """
                              #if NOPE
                              this is ( not /// valid "sable at all
                              #endif
                              fn ok() { }
                              """;

        var tree = Parse(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(["ok"], MemberNames(tree));
        Assert.Equal(source, tree.ToFullString());
    }

    [Fact]
    public void ElseIfChainTakesTheFirstMatch() {
        const string source = """
                              #if A
                              fn a() { }
                              #elseif B
                              fn b() { }
                              #elseif C
                              fn c() { }
                              #else
                              fn d() { }
                              #endif
                              """;

        Assert.Equal(["a"], MemberNames(Parse(source, "A", "B")));
        Assert.Equal(["b"], MemberNames(Parse(source, "B", "C")));
        Assert.Equal(["c"], MemberNames(Parse(source, "C")));
        Assert.Equal(["d"], MemberNames(Parse(source)));
        Assert.Equal(source, Parse(source, "B").ToFullString());
    }

    [Fact]
    public void NestedConditionalsNestCorrectly() {
        const string source = """
                              #if OUTER
                              fn outer() { }
                              #if INNER
                              fn both() { }
                              #else
                              fn outerOnly() { }
                              #endif
                              #else
                              #if INNER
                              fn innerOnly() { }
                              #endif
                              #endif
                              """;

        Assert.Equal(["outer", "both"], MemberNames(Parse(source, "OUTER", "INNER")));
        Assert.Equal(["outer", "outerOnly"], MemberNames(Parse(source, "OUTER")));
        Assert.Equal(["innerOnly"], MemberNames(Parse(source, "INNER")));
        Assert.Empty(MemberNames(Parse(source)));
        Assert.Equal(source, Parse(source, "OUTER").ToFullString());
    }

    [Fact]
    public void ADisabledOuterRegionDisablesEveryInnerBranch() {
        const string source = """
                              #if NOPE
                              #if YES
                              fn a() { }
                              #else
                              fn b() { }
                              #endif
                              #endif
                              fn c() { }
                              """;

        Assert.Equal(["c"], MemberNames(Parse(source, "YES")));
        Assert.Equal(source, Parse(source, "YES").ToFullString());
    }

    // ---- Condition expressions --------------------------------------------

    [Theory]
    [InlineData("A", true)]
    [InlineData("B", false)]
    [InlineData("!A", false)]
    [InlineData("!B", true)]
    [InlineData("A && B", false)]
    [InlineData("A || B", true)]
    [InlineData("A && !B", true)]
    [InlineData("(A || B) && A", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("os(macos)", true)]
    [InlineData("os(linux)", false)]
    [InlineData("arch(arm64)", true)]
    [InlineData("arch(x86_64)", false)]
    [InlineData("os(macos) && arch(arm64)", true)]
    [InlineData("feature(vulkan)", true)]
    [InlineData("feature(metal)", false)]
    public void EvaluatesConditions(string condition, bool expected) {
        var options = ParseOptions.Default
            .WithSymbols("A")
            .WithFeatures("vulkan") with {
            TargetOs = "macos", TargetArch = "arm64"
        };

        var source = $"#if {condition}\nfn taken() {{ }}\n#endif\n";
        var tree = SyntaxTree.Parse(new(source, "<test>"), options);

        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(expected ? ["taken"] : (string[])[], MemberNames(tree));
    }

    // ---- Diagnostics --------------------------------------------------------

    [Fact]
    public void UnmatchedEndIfIsReported() {
        var tree = Parse("#endif\nfn f() { }");
        Assert.Contains(tree.Diagnostics, static d => d.Code == ErrorCode.ERR_UnmatchedDirective);
    }

    [Fact]
    public void UnterminatedConditionalIsReported() {
        var tree = Parse("#if A\nfn f() { }");
        Assert.Contains(tree.Diagnostics, static d => d.Code == ErrorCode.ERR_UnterminatedConditional);
    }

    [Fact]
    public void MalformedConditionIsReportedAndDisablesTheRegion() {
        var tree = Parse("#if A +++ B\nfn f() { }\n#endif");
        Assert.Contains(tree.Diagnostics, static d => d.Code == ErrorCode.ERR_BadDirectiveCondition);
        Assert.Empty(MemberNames(tree));
    }

    [Fact]
    public void UserErrorAndWarningFireOnlyWhenActive() {
        var active = Parse("#if A\n#error(\"nope\")\n#endif", "A");
        Assert.Contains(
            active.Diagnostics,
            static d => d.Code == ErrorCode.ERR_UserError && d.Message == "nope"
        );

        var inactive = Parse("#if A\n#error(\"nope\")\n#endif");
        Assert.DoesNotContain(inactive.Diagnostics, static d => d.Code == ErrorCode.ERR_UserError);

        var warned = Parse("""#warning("careful")""");
        Assert.Contains(
            warned.Diagnostics,
            static d => d.Code == ErrorCode.WRN_UserWarning && d.Message == "careful"
        );
    }

    // ---- Interaction with the rest of the lexer -----------------------------

    [Fact]
    public void DirectivesInsideFunctionBodiesWork() {
        const string source = """
                              fn f() {
                                  let a = 1
                              #if DEBUG
                                  log(a)
                              #endif
                                  return a
                              }
                              """;

        var withDebug = Parse(source, "DEBUG");
        Assert.False(withDebug.HasErrors, withDebug.FormatDiagnostics());
        Assert.Equal(source, withDebug.ToFullString());

        var without = Parse(source);
        Assert.False(without.HasErrors, without.FormatDiagnostics());
        Assert.Equal(source, without.ToFullString());

        var fn = (FunctionDeclarationSyntax)((CompilationUnitSyntax)withDebug.Root).Members[0];
        Assert.Equal(3, fn.Body!.Statements.Count);

        var fnWithout = (FunctionDeclarationSyntax)((CompilationUnitSyntax)without.Root).Members[0];
        Assert.Equal(2, fnWithout.Body!.Statements.Count);
    }

    [Fact]
    public void AHashInExpressionPositionIsStillABuiltin() {
        var tree = SyntaxTree.ParseExpression("#file");
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(SyntaxKind.BuiltinExpression, tree.Root.Kind);
    }

    [Fact]
    public void ADirectiveMustBeFirstOnItsLine() {
        // 'let x = #line' is a builtin, not a directive.
        var tree = SyntaxTree.ParseStatement("let x = #line");
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
    }

    [Fact]
    public void DirectivesAreTrivia() {
        var tree = Parse("#if A\n#endif\nfn f() { }", "A");
        var first = tree.Root.GetFirstToken();
        Assert.Equal(2, first.LeadingTrivia.Count(static t => t.Kind == SyntaxKind.DirectiveTrivia));
    }
}
