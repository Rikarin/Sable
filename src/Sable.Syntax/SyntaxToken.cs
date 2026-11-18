using System.Collections.Immutable;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>The kind of a segment inside an interpolated string literal.</summary>
public enum StringPartKind {
    Literal,
    Interpolation
}

/// <summary>
///     One piece of a string literal: either cooked literal text, or the source
///     span of an interpolation hole, which the parser re-enters recursively.
/// </summary>
/// <remarks>
///     <para>
///         Inside a green token the <see cref="Span" /> is relative to the start
///         of the token's text. <see cref="SyntaxToken.Parts" /> returns copies whose
///         spans have been offset to absolute positions.
///     </para>
///     <para>
///         The spans tile the token's text: a literal part spans its raw
///         characters, and an interpolation spans the whole hole including
///         <c>\(</c> and <c>)</c>, while its <see cref="Text" /> is just the
///         expression inside. That is what lets the parser rebuild the token as a
///         node without losing a character.
///     </para>
/// </remarks>
public sealed record StringPart(StringPartKind Kind, string Text, TextSpan Span) {
    public override string ToString() => Kind == StringPartKind.Literal ? Text : $"\\({Text})";
}

/// <summary>
///     A lexical token: a red facade over a green token, adding a parent pointer
///     and an absolute position.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="FullSpan" /> covers leading trivia, the token text and
///         trailing trivia. <see cref="Span" /> covers only the token text, and is what
///         diagnostics point at.
///     </para>
///     <para>
///         <see cref="TerminatesStatement" /> implements the newline rule of
///         spec/lexical.md §4: it is set on the last token of a statement, whether that
///         statement ended at a newline or at an explicit semicolon.
///     </para>
/// </remarks>
public readonly struct SyntaxToken : IEquatable<SyntaxToken> {
    readonly GreenToken? _green;

    internal SyntaxToken(GreenToken? green, SyntaxNode? parent, int position) {
        _green = green;
        Parent = parent;
        Position = position;
    }

    internal GreenToken? Green => _green;

    /// <summary>The node this token belongs to, or null for a free-standing token.</summary>
    public SyntaxNode? Parent { get; }

    /// <summary>Absolute start of the token including its leading trivia.</summary>
    public int Position { get; }

    public SyntaxKind Kind => _green?.Kind ?? SyntaxKind.None;

    /// <summary>The exact source text of the token, excluding trivia.</summary>
    public string Text => _green?.Text ?? string.Empty;

    /// <summary>
    ///     The decoded value: <c>ulong</c> for integers (<c>UInt128</c> for one
    ///     past 64 bits), <c>double</c> for floats,
    ///     <c>Rune</c> for chars, <c>string</c> for non-interpolated strings and
    ///     escaped identifiers, <c>bool</c> for <c>true</c>/<c>false</c>, otherwise
    ///     <see langword="null" />.
    /// </summary>
    public object? Value => _green?.Value;

    public bool TerminatesStatement => _green?.TerminatesStatement ?? false;

    /// <summary>True when this token was synthesised by error recovery.</summary>
    public bool IsMissing => _green?.IsMissing ?? false;

    /// <summary>True when there is no underlying token at all.</summary>
    public bool IsNone => _green is null;

    public int Width => _green?.Text.Length ?? 0;
    public int FullWidth => _green?.FullWidth ?? 0;

    /// <summary>Absolute start of the token text, after any leading trivia.</summary>
    public int SpanStart => Position + (_green?.LeadingTriviaWidth ?? 0);

    public TextSpan Span => new(SpanStart, Width);
    public TextSpan FullSpan => new(Position, FullWidth);

    public int FullStart => Position;
    public int FullEnd => Position + FullWidth;

    public SyntaxTriviaList LeadingTrivia => _green is null ? default : new SyntaxTriviaList(_green.Leading, Position);

    public SyntaxTriviaList TrailingTrivia =>
        _green is null ? default : new SyntaxTriviaList(_green.Trailing, SpanStart + Width);

    /// <summary>Segments of an interpolated string, with absolute spans.</summary>
    public ImmutableArray<StringPart> Parts {
        get {
            if (_green is null || _green.Parts.IsEmpty) {
                return [];
            }

            var start = SpanStart;
            var builder = ImmutableArray.CreateBuilder<StringPart>(_green.Parts.Length);
            foreach (var part in _green.Parts) {
                builder.Add(part with { Span = new(part.Span.Start + start, part.Span.Length) });
            }

            return builder.MoveToImmutable();
        }
    }

    public bool IsKeyword => SyntaxFacts.IsKeyword(Kind);
    public bool IsReservedKeyword => SyntaxFacts.IsReservedKeyword(Kind);
    public bool IsContextualKeyword => SyntaxFacts.IsContextualKeyword(Kind);
    public bool IsLiteral => SyntaxFacts.IsLiteral(Kind);

    /// <summary>True when a newline appears in this token's leading trivia.</summary>
    public bool HasLeadingNewLine => LeadingTrivia.Any(SyntaxKind.NewLineTrivia);

    /// <summary>True when a newline appears in this token's trailing trivia.</summary>
    public bool HasTrailingNewLine => TrailingTrivia.Any(SyntaxKind.NewLineTrivia);

    /// <summary>The token text alone.</summary>
    public override string ToString() => Text;

    /// <summary>Leading trivia, token text and trailing trivia, concatenated.</summary>
    public string ToFullString() => _green?.ToFullString() ?? string.Empty;

    public bool Equals(SyntaxToken other) =>
        ReferenceEquals(_green, other._green)
        && ReferenceEquals(Parent, other.Parent)
        && Position == other.Position;

    public override bool Equals(object? obj) => obj is SyntaxToken other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_green, Parent, Position);
    public static bool operator ==(SyntaxToken left, SyntaxToken right) => left.Equals(right);
    public static bool operator !=(SyntaxToken left, SyntaxToken right) => !left.Equals(right);
}
