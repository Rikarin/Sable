using System.Collections.Immutable;

namespace Sable.Syntax;

/// <summary>
/// A span of non-token source text attached to an adjacent token. Trivia is
/// retained rather than discarded so that the full-fidelity round-trip
/// invariant holds: concatenating <see cref="SyntaxToken.ToFullString"/> over
/// every token of a file reproduces that file byte for byte.
/// </summary>
public sealed class SyntaxTrivia(SyntaxKind kind, string text, int position)
{
    public SyntaxKind Kind { get; } = kind;
    public string Text { get; } = text;
    public int Position { get; } = position;

    public int Width => Text.Length;
    public TextSpan Span => new(Position, Width);

    public bool IsComment => Kind is SyntaxKind.LineCommentTrivia
        or SyntaxKind.BlockCommentTrivia
        or SyntaxKind.DocCommentTrivia
        or SyntaxKind.ModuleDocCommentTrivia;

    public bool IsDocumentation => Kind is SyntaxKind.DocCommentTrivia
        or SyntaxKind.ModuleDocCommentTrivia;

    public override string ToString() => Text;
}

/// <summary>The kind of a segment inside an interpolated string literal.</summary>
public enum StringPartKind { Literal, Interpolation }

/// <summary>
/// One piece of a string literal: either cooked literal text, or the source
/// span of an interpolation hole, which the parser re-enters recursively.
/// </summary>
public sealed record StringPart(StringPartKind Kind, string Text, TextSpan Span)
{
    public override string ToString() =>
        Kind == StringPartKind.Literal ? Text : $"\\({Text})";
}

/// <summary>
/// A lexical token together with its attached trivia.
/// </summary>
/// <remarks>
/// <para>Positions: <see cref="FullSpan"/> covers leading trivia, the token text
/// and trailing trivia. <see cref="Span"/> covers only the token text, and is
/// what diagnostics point at.</para>
/// <para><see cref="TerminatesStatement"/> implements the newline rule of
/// spec/lexical.md §4: it is set on the last token of a statement, whether that
/// statement ended at a newline or at an explicit semicolon.</para>
/// </remarks>
public sealed class SyntaxToken
{
    public SyntaxKind Kind { get; }

    /// <summary>The exact source text of the token, excluding trivia.</summary>
    public string Text { get; }

    /// <summary>
    /// The decoded value: <c>ulong</c> for integers, <c>double</c> for floats,
    /// <c>Rune</c> for chars, <c>string</c> for non-interpolated strings,
    /// <c>bool</c> for <c>true</c>/<c>false</c>, otherwise <see langword="null"/>.
    /// </summary>
    public object? Value { get; }

    /// <summary>Segments of an interpolated string; empty for every other token.</summary>
    public ImmutableArray<StringPart> Parts { get; }

    public ImmutableArray<SyntaxTrivia> LeadingTrivia { get; }
    public ImmutableArray<SyntaxTrivia> TrailingTrivia { get; }

    /// <summary>Start of the token text itself (after leading trivia).</summary>
    public int Position { get; }

    /// <summary>True when a statement break occurs immediately after this token.</summary>
    public bool TerminatesStatement { get; internal set; }

    /// <summary>True when this token is synthesised by error recovery.</summary>
    public bool IsMissing { get; init; }

    public SyntaxToken(
        SyntaxKind kind,
        string text,
        int position,
        ImmutableArray<SyntaxTrivia> leadingTrivia,
        ImmutableArray<SyntaxTrivia> trailingTrivia,
        object? value = null,
        ImmutableArray<StringPart> parts = default)
    {
        Kind = kind;
        Text = text;
        Position = position;
        LeadingTrivia = leadingTrivia;
        TrailingTrivia = trailingTrivia;
        Value = value;
        Parts = parts.IsDefault ? [] : parts;
    }

    public int Width => Text.Length;
    public TextSpan Span => new(Position, Width);

    public int FullStart =>
        LeadingTrivia.Length > 0 ? LeadingTrivia[0].Position : Position;

    public int FullEnd =>
        TrailingTrivia.Length > 0
            ? TrailingTrivia[^1].Position + TrailingTrivia[^1].Width
            : Position + Width;

    public TextSpan FullSpan => TextSpan.FromBounds(FullStart, FullEnd);

    public bool IsKeyword => SyntaxFacts.IsKeyword(Kind);
    public bool IsReservedKeyword => SyntaxFacts.IsReservedKeyword(Kind);
    public bool IsContextualKeyword => SyntaxFacts.IsContextualKeyword(Kind);
    public bool IsLiteral => SyntaxFacts.IsLiteral(Kind);

    /// <summary>True when a newline appears in this token's leading trivia.</summary>
    public bool HasLeadingNewLine
    {
        get
        {
            foreach (var t in LeadingTrivia)
                if (t.Kind == SyntaxKind.NewLineTrivia) return true;
            return false;
        }
    }

    /// <summary>The token text alone.</summary>
    public override string ToString() => Text;

    /// <summary>Leading trivia, token text and trailing trivia, concatenated.</summary>
    public string ToFullString()
    {
        if (LeadingTrivia.IsEmpty && TrailingTrivia.IsEmpty) return Text;

        var builder = new System.Text.StringBuilder(Text.Length + 16);
        foreach (var t in LeadingTrivia) builder.Append(t.Text);
        builder.Append(Text);
        foreach (var t in TrailingTrivia) builder.Append(t.Text);
        return builder.ToString();
    }
}
