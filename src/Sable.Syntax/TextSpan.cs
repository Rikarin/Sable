namespace Sable.Syntax;

/// <summary>An immutable half-open range of characters in a source file.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
    public bool IsEmpty => Length == 0;

    public static TextSpan FromBounds(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, end);
        return new TextSpan(start, end - start);
    }

    public bool Contains(int position) => position >= Start && position < End;

    public bool OverlapsWith(TextSpan other) =>
        Math.Max(Start, other.Start) < Math.Min(End, other.End);

    public override string ToString() => $"[{Start}..{End})";
}

/// <summary>A one-based line/column pair, for diagnostics.</summary>
public readonly record struct LinePosition(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

/// <summary>
/// Source text plus a lazily built line index. Line terminators recognised are
/// LF, CRLF and bare CR, per spec/lexical.md §1.
/// </summary>
public sealed class SourceText
{
    private readonly int[] _lineStarts;

    public string Text { get; }
    public string FileName { get; }

    public SourceText(string text, string fileName = "<unknown>")
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        FileName = fileName;
        _lineStarts = ComputeLineStarts(text);
    }

    public int Length => Text.Length;
    public char this[int index] => Text[index];
    public int LineCount => _lineStarts.Length;

    public ReadOnlySpan<char> Slice(TextSpan span) => Text.AsSpan(span.Start, span.Length);
    public string ToString(TextSpan span) => Text.Substring(span.Start, span.Length);

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return [.. starts];
    }

    /// <summary>Maps an absolute character offset to a one-based line/column.</summary>
    public LinePosition GetLinePosition(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        var index = Array.BinarySearch(_lineStarts, position);
        if (index < 0) index = ~index - 1;
        if (index < 0) index = 0;
        return new LinePosition(index + 1, position - _lineStarts[index] + 1);
    }

    /// <summary>Returns the text of a one-based line, without its terminator.</summary>
    public string GetLineText(int line)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(line, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(line, LineCount);
        var start = _lineStarts[line - 1];
        var end = line < LineCount ? _lineStarts[line] : Text.Length;
        while (end > start && (Text[end - 1] == '\n' || Text[end - 1] == '\r')) end--;
        return Text[start..end];
    }
}
