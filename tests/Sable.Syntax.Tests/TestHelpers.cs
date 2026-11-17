using System.Collections.Immutable;

namespace Sable.Syntax.Tests;

internal static class TestHelpers {
    /// <summary>Lexes and returns every token except end-of-file.</summary>
    public static ImmutableArray<SyntaxToken> Lex(string text) {
        var tokens = Lexer.Tokenize(new(text), out _);
        return tokens.RemoveAt(tokens.Length - 1);
    }

    public static ImmutableArray<SyntaxToken> LexAll(string text) => Lexer.Tokenize(new(text), out _);

    public static (ImmutableArray<SyntaxToken> Tokens, IReadOnlyList<Diagnostic> Diagnostics)
        LexWithDiagnostics(string text) {
        var tokens = Lexer.Tokenize(new(text), out var diagnostics);
        return (tokens, diagnostics);
    }

    public static SyntaxToken LexSingle(string text) {
        var tokens = Lex(text);
        Assert.Single(tokens);
        return tokens[0];
    }

    public static IReadOnlyList<SyntaxKind> Kinds(string text) => [..Lex(text).Select(static t => t.Kind)];

    /// <summary>Walks up from the test assembly to the directory holding Sable.slnx.</summary>
    public static string RepositoryRoot {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sable.slnx"))) {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
