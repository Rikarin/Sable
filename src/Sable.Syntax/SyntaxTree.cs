using System.Text;

namespace Sable.Syntax;

/// <summary>
/// A parsed source file: its text, its root node, and the diagnostics produced
/// while lexing and parsing it.
/// </summary>
/// <remarks>
/// <para>The root plus <see cref="EndOfFileToken"/> account for every character
/// of <see cref="Text"/>, so <see cref="ToFullString"/> reproduces the source
/// byte for byte. Trivia after the root — trailing whitespace, a final comment,
/// anything error recovery skipped — is attached to the end-of-file token.</para>
/// <para>Once declarations exist, the root will be a compilation unit that owns
/// the end-of-file token itself, as it does in Roslyn.</para>
/// </remarks>
public sealed class SyntaxTree
{
    private readonly SyntaxToken _trailingEndOfFile;

    public SourceText Text { get; }
    public SyntaxNode Root { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// The end-of-file token. A compilation unit owns it as a child; the
    /// single-construct entry points carry it beside the root instead.
    /// </summary>
    public SyntaxToken EndOfFileToken =>
        _trailingEndOfFile.IsNone && Root is CompilationUnitSyntax unit
            ? unit.EndOfFileToken
            : _trailingEndOfFile;

    internal SyntaxTree(
        SourceText text,
        SyntaxNode root,
        SyntaxToken endOfFileToken,
        IReadOnlyList<Diagnostic> diagnostics,
        ParseOptions options)
    {
        Text = text;
        Root = root;
        _trailingEndOfFile = endOfFileToken;
        Diagnostics = diagnostics;
        Options = options;
    }

    public bool HasErrors
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
                if (diagnostic.Severity == DiagnosticSeverity.Error) return true;
            return false;
        }
    }

    /// <summary>The configuration this tree was parsed under.</summary>
    public ParseOptions Options { get; }

    /// <summary>Parses a single expression.</summary>
    public static SyntaxTree ParseExpression(string text, string fileName = "<input>") =>
        Parser.ParseExpression(new SourceText(text, fileName), ParseOptions.Default);

    /// <summary>Parses a single type reference.</summary>
    public static SyntaxTree ParseType(string text, string fileName = "<input>") =>
        Parser.ParseType(new SourceText(text, fileName), ParseOptions.Default);

    /// <summary>Parses a single statement, which may be a block.</summary>
    public static SyntaxTree ParseStatement(string text, string fileName = "<input>") =>
        Parser.ParseStatement(new SourceText(text, fileName), ParseOptions.Default);

    /// <summary>Parses a whole source file.</summary>
    public static SyntaxTree Parse(string text, string fileName = "<input>") =>
        Parse(new SourceText(text, fileName), ParseOptions.Default);

    /// <summary>Parses a whole source file from already-loaded text.</summary>
    public static SyntaxTree Parse(SourceText text) => Parse(text, ParseOptions.Default);

    /// <summary>Parses a whole source file under the given options.</summary>
    public static SyntaxTree Parse(SourceText text, ParseOptions options) =>
        Parser.ParseCompilationUnit(text, options);

    /// <summary>The whole file, reconstructed from the tree.</summary>
    public string ToFullString()
    {
        var builder = new StringBuilder(Text.Length);
        builder.Append(Root.ToFullString());
        builder.Append(_trailingEndOfFile.ToFullString());
        return builder.ToString();
    }

    /// <summary>Renders every diagnostic with its source line and caret.</summary>
    public string FormatDiagnostics() =>
        string.Join("\n\n", Diagnostics.Select(d => d.Format(Text)));
}
