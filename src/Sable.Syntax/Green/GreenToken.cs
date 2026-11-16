using System.Collections.Immutable;
using System.Text;

namespace Sable.Syntax.Green;

/// <summary>A green trivia node: width and text only.</summary>
internal sealed class GreenTrivia(SyntaxKind kind, string text)
    : GreenNode(kind, text.Length)
{
    public string Text { get; } = text;

    public override bool IsTrivia => true;
    public override int LeadingTriviaWidth => 0;
    public override int TrailingTriviaWidth => 0;

    public override void WriteTo(StringBuilder builder, bool leading, bool trailing) =>
        builder.Append(Text);
}

/// <summary>
/// A green token: its text, decoded value, and attached trivia, all without
/// absolute positions.
/// </summary>
internal sealed class GreenToken : GreenNode
{
    public string Text { get; }
    public object? Value { get; }

    /// <summary>
    /// Segments of an interpolated string. Spans are relative to the start of
    /// the token's text; the red facade offsets them to absolute positions.
    /// </summary>
    public ImmutableArray<StringPart> Parts { get; }

    public ImmutableArray<GreenTrivia> Leading { get; }
    public ImmutableArray<GreenTrivia> Trailing { get; }

    /// <summary>True when a statement break occurs immediately after this token.</summary>
    public bool TerminatesStatement { get; }

    /// <summary>True when this token was synthesised by error recovery.</summary>
    public bool IsMissing { get; }

    public GreenToken(
        SyntaxKind kind,
        string text,
        ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing,
        object? value = null,
        ImmutableArray<StringPart> parts = default,
        bool terminatesStatement = false,
        bool isMissing = false)
        : base(kind, 0)
    {
        Text = text;
        Value = value;
        Parts = parts.IsDefault ? [] : parts;
        Leading = leading;
        Trailing = trailing;
        TerminatesStatement = terminatesStatement;
        IsMissing = isMissing;

        var width = text.Length;
        foreach (var t in leading) width += t.FullWidth;
        foreach (var t in trailing) width += t.FullWidth;
        FullWidth = width;
    }

    public override bool IsToken => true;

    public override int LeadingTriviaWidth
    {
        get
        {
            var width = 0;
            foreach (var t in Leading) width += t.FullWidth;
            return width;
        }
    }

    public override int TrailingTriviaWidth
    {
        get
        {
            var width = 0;
            foreach (var t in Trailing) width += t.FullWidth;
            return width;
        }
    }

    public GreenToken WithTerminatesStatement(bool value) =>
        value == TerminatesStatement
            ? this
            : new GreenToken(Kind, Text, Leading, Trailing, Value, Parts, value, IsMissing);

    public GreenToken WithTrivia(
        ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing) =>
        new(Kind, Text, leading, trailing, Value, Parts, TerminatesStatement, IsMissing);

    public override void WriteTo(StringBuilder builder, bool leading, bool trailing)
    {
        if (leading)
            foreach (var t in Leading) builder.Append(t.Text);

        builder.Append(Text);

        if (trailing)
            foreach (var t in Trailing) builder.Append(t.Text);
    }

    public override string ToString() => Text;
}
