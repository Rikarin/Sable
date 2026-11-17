using System.Collections.Immutable;
using System.Text;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>
///     A parsed source file: its text, its root node, and the diagnostics produced
///     while lexing and parsing it.
/// </summary>
/// <remarks>
///     <para>
///         The root plus <see cref="EndOfFileToken" /> account for every character
///         of <see cref="Text" />, so <see cref="ToFullString" /> reproduces the source
///         byte for byte. Trivia after the root — trailing whitespace, a final comment,
///         anything error recovery skipped — is attached to the end-of-file token.
///     </para>
///     <para>
///         Once declarations exist, the root will be a compilation unit that owns
///         the end-of-file token itself, as it does in Roslyn.
///     </para>
/// </remarks>
public sealed class SyntaxTree {
    readonly SyntaxToken _trailingEndOfFile;

    public SourceText Text { get; }
    public SyntaxNode Root { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    ///     The end-of-file token. A compilation unit owns it as a child; the
    ///     single-construct entry points carry it beside the root instead.
    /// </summary>
    public SyntaxToken EndOfFileToken =>
        _trailingEndOfFile.IsNone && Root is CompilationUnitSyntax unit
            ? unit.EndOfFileToken
            : _trailingEndOfFile;

    /// <summary>
    ///     The token stream this tree was built from, kept so that an edit can
    ///     splice rather than re-lex.
    /// </summary>
    internal ImmutableArray<GreenToken> Tokens { get; }

    /// <summary>
    ///     True when the file uses conditional compilation, which makes the lexer
    ///     stateful across tokens and so rules out splicing.
    /// </summary>
    internal bool HasDirectives { get; }

    internal SyntaxTree(
        SourceText text,
        SyntaxNode root,
        SyntaxToken endOfFileToken,
        IReadOnlyList<Diagnostic> diagnostics,
        ParseOptions options,
        ImmutableArray<GreenToken> tokens = default,
        bool hasDirectives = false,
        IReadOnlyList<Diagnostic>? lexicalDiagnostics = null
    ) {
        Text = text;
        Root = root;
        _trailingEndOfFile = endOfFileToken;
        Options = options;

        // Sorted so that a full parse and an incremental one, which collect
        // diagnostics in different orders, report the same sequence.
        Diagnostics = [
            ..diagnostics
                .OrderBy(static d => d.Span.Start)
                .ThenBy(static d => d.Span.Length)
                .ThenBy(static d => (int)d.Code)
        ];
        Tokens = tokens.IsDefault ? [] : tokens;
        HasDirectives = hasDirectives;
        LexicalDiagnostics = lexicalDiagnostics ?? [];
    }

    /// <summary>
    ///     The diagnostics the lexer of the whole file produced, kept apart
    ///     from the parser's.
    /// </summary>
    /// <remarks>
    ///     An edit splices old lexer diagnostics back in and regenerates the
    ///     parser's, so it must know which are which. The code range does not
    ///     say: an interpolation hole is lexed by the parser, and its lexer
    ///     errors are the parser's to regenerate.
    /// </remarks>
    internal IReadOnlyList<Diagnostic> LexicalDiagnostics { get; }

    /// <summary>The diagnostics the parser produced, holes included.</summary>
    internal IEnumerable<Diagnostic> ParserDiagnostics {
        get {
            var lexical = LexicalDiagnostics.ToHashSet();
            return Diagnostics.Where(d => !lexical.Contains(d));
        }
    }

    /// <summary>
    ///     Applies an edit, reusing everything the edit did not touch.
    /// </summary>
    /// <remarks>
    ///     Tokens on both sides of the edit are spliced from the old stream, and
    ///     whole members that survive are dropped in as the very same green nodes.
    ///     The result is the tree a full parse would have produced; only the work
    ///     differs.
    /// </remarks>
    public SyntaxTree WithChange(TextChange change) {
        var newText = Text.WithChange(change);

        // Only a compilation unit keeps the bookkeeping an edit needs.
        if (Tokens.IsEmpty || Root is not CompilationUnitSyntax) {
            return Parse(newText, Options);
        }

        var lexResult = IncrementalLexer.Lex(newText, Options, Tokens, LexicalDiagnostics, HasDirectives, change);

        var reuse = IncrementalReuse.Build(Root.Green, Tokens, [..ParserDiagnostics], lexResult);

        // Conservative: an added '#' may have introduced a directive.
        var hasDirectives = HasDirectives || change.NewText.Contains('#');

        return Parser.ParseCompilationUnitFrom(
            newText,
            Options,
            lexResult.Tokens,
            lexResult.Diagnostics,
            hasDirectives,
            reuse
        );
    }

    /// <summary>Applies several edits in order.</summary>
    public SyntaxTree WithChanges(IEnumerable<TextChange> changes) {
        var tree = this;
        foreach (var change in changes) {
            tree = tree.WithChange(change);
        }

        return tree;
    }

    public bool HasErrors {
        get {
            foreach (var diagnostic in Diagnostics) {
                if (diagnostic.Severity == DiagnosticSeverity.Error) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The configuration this tree was parsed under.</summary>
    public ParseOptions Options { get; }

    /// <summary>Parses a single expression.</summary>
    public static SyntaxTree ParseExpression(string text, string fileName = "<input>") =>
        Parser.ParseExpression(new(text, fileName), ParseOptions.Default);

    /// <summary>Parses a single type reference.</summary>
    public static SyntaxTree ParseType(string text, string fileName = "<input>") =>
        Parser.ParseType(new(text, fileName), ParseOptions.Default);

    /// <summary>Parses a single statement, which may be a block.</summary>
    public static SyntaxTree ParseStatement(string text, string fileName = "<input>") =>
        Parser.ParseStatement(new(text, fileName), ParseOptions.Default);

    /// <summary>Parses a whole source file.</summary>
    public static SyntaxTree Parse(string text, string fileName = "<input>") =>
        Parse(new(text, fileName), ParseOptions.Default);

    /// <summary>Parses a whole source file from already-loaded text.</summary>
    public static SyntaxTree Parse(SourceText text) => Parse(text, ParseOptions.Default);

    /// <summary>Parses a whole source file under the given options.</summary>
    public static SyntaxTree Parse(SourceText text, ParseOptions options) => Parser.ParseCompilationUnit(text, options);

    /// <summary>The whole file, reconstructed from the tree.</summary>
    public string ToFullString() {
        var builder = new StringBuilder(Text.Length);
        builder.Append(Root.ToFullString());
        builder.Append(_trailingEndOfFile.ToFullString());
        return builder.ToString();
    }

    /// <summary>Renders every diagnostic with its source line and caret.</summary>
    public string FormatDiagnostics() => string.Join("\n\n", Diagnostics.Select(d => d.Format(Text)));
}
