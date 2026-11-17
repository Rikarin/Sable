using System.Text.Json;
using Sable.Visualiser;

namespace Sable.Symbols.Tests;

/// <summary>
///     The syntax-tree visualiser of docs/04 §0.5: it shows every node, token
///     and trivia, broken input included, and what the semantic model says.
/// </summary>
public sealed class VisualiserTests {
    static VisualDocument Build(string text, bool semantics = true) => VisualTree.Build(new(text, "t.sab"), semantics);

    static IEnumerable<VisualItem> All(VisualItem item) => item.Children.SelectMany(All).Prepend(item);

    const string Program = """
                           module Demo
                           // a comment
                           fn add(_ a: Int, _ b: Int) -> Int { return a + b }
                           """;

    [Fact]
    public void TheTokensAndTriviaReproduceTheSourceExactly() {
        var document = Build(Program);

        // Every character belongs to exactly one token or one trivia.
        var pieces = All(document.Root)
            .Where(static i => i.Category == VisualCategory.Trivia
                || (i.Category == VisualCategory.Token && i.Text is not null)
            )
            .SelectMany(static i => i.Category == VisualCategory.Token
                    ? [(i.Span.Start, i.Text!)]
                    : new[] { (i.Span.Start, i.Text!) }
            )
            .OrderBy(static p => p.Item1)
            .Select(static p => p.Item2);

        Assert.Equal(Program, string.Concat(pieces));
    }

    [Fact]
    public void ANameCarriesItsTypeAndSymbol() {
        var a = All(Build(Program).Root)
            .First(static i =>
                i.Kind == SyntaxKind.IdentifierName && i.Span.Start == Program.IndexOf("a + b")
            );

        Assert.Equal("Int", a.Type);
        Assert.Equal("Parameter", a.SymbolKind);
    }

    [Fact]
    public void ADeclarationCarriesItsSymbol() {
        var function = All(Build(Program).Root)
            .Single(static i => i.Kind == SyntaxKind.FunctionDeclaration);
        Assert.Equal("Function", function.SymbolKind);
    }

    [Fact]
    public void SyntaxOnlyLeavesTheSemanticsOut() {
        var document = Build(Program, false);
        Assert.False(document.HasSemantics);
        Assert.All(All(document.Root), static i => Assert.Null(i.Type));
    }

    [Fact]
    public void BrokenSourceShowsItsMissingTokensAndDiagnostics() {
        var document = Build("module Demo\nfn broken( -> Int { return }");

        Assert.Contains(All(document.Root), static i => i.IsMissing);
        Assert.Contains(document.Diagnostics, static d => d.Id == "S1001");
        Assert.Contains(document.Diagnostics, static d => d.Id == "S3016");
    }

    [Fact]
    public void TheTextDumpIndentsAndAnnotates() {
        var text = TextRenderer.Render(Build(Program));
        Assert.Contains("CompilationUnit [0..", text, StringComparison.Ordinal);
        Assert.Contains("    FnKeyword", text, StringComparison.Ordinal);
        Assert.Contains("BinaryExpression", text, StringComparison.Ordinal);
        Assert.Contains(": Int", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LineCommentTrivia", text, StringComparison.Ordinal);
        Assert.Contains(
            "LineCommentTrivia",
            TextRenderer.Render(Build(Program), true),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ThePageEmbedsTheTreeAsJson() {
        var html = HtmlRenderer.Render(Build(Program));

        const string marker = "const DATA = ";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = html.IndexOf(";\n", start, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(html[start..end]);

        Assert.Equal(Program, json.RootElement.GetProperty("source").GetString());
        Assert.Equal(
            "CompilationUnit",
            json.RootElement.GetProperty("root")[1].GetString()
        );
    }

    [Fact]
    public void SourceTextCannotCloseThePagesScript() {
        var html = HtmlRenderer.Render(Build("module M\n// </script><b>x</b>\n"));
        var script = html[html.IndexOf("const DATA", StringComparison.Ordinal)..];
        Assert.Equal(1, CountOf(script, "</script>"));
    }

    static int CountOf(string text, string what) {
        var count = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(what, i + 1, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }
}
