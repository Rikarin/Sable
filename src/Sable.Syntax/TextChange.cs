namespace Sable.Syntax;

/// <summary>An edit: the span of old text to replace, and what replaces it.</summary>
public readonly record struct TextChange(TextSpan Span, string NewText) {
    /// <summary>How much longer the text becomes. Negative for a deletion.</summary>
    public int Delta => NewText.Length - Span.Length;

    /// <summary>The span the new text occupies after the edit.</summary>
    public TextSpan NewSpan => new(Span.Start, NewText.Length);

    public static TextChange Insert(int position, string text) => new(new TextSpan(position, 0), text);

    public static TextChange Delete(TextSpan span) => new(span, string.Empty);

    public static TextChange Replace(TextSpan span, string text) => new(span, text);

    public override string ToString() =>
        NewText.Length == 0
            ? $"delete {Span}"
            : Span.IsEmpty
                ? $"insert {NewText.Length} chars at {Span.Start}"
                : $"replace {Span} with {NewText.Length} chars";
}
