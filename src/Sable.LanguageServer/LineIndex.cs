using System.Runtime.CompilerServices;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>A protocol position: a zero-based line, and a zero-based column in UTF-16 code units.</summary>
internal readonly record struct LspPosition(int Line, int Character);

/// <summary>A protocol range: start inclusive, end exclusive.</summary>
internal readonly record struct LspRange(LspPosition Start, LspPosition End);

/// <summary>
///     Maps between offsets into a text and protocol positions.
/// </summary>
/// <remarks>
///     <para>
///         The protocol counts columns in UTF-16 code units unless the client
///         and server agree otherwise, and this server never does: it answers
///         <c>positionEncoding: "utf-16"</c>. A .NET string is UTF-16, so a
///         column is an offset from the line's start with no decoding — a
///         character outside the Basic Multilingual Plane is two units in
///         both. Lines end at <c>\n</c>, <c>\r\n</c> or a lone <c>\r</c>, as
///         the protocol says.
///     </para>
///     <para>
///         One index per text, built on first use and kept with it.
///     </para>
/// </remarks>
internal sealed class LineIndex {
    static readonly ConditionalWeakTable<SourceText, LineIndex> Cache = [];

    readonly string _text;
    readonly int[] _starts;

    LineIndex(string text) {
        _text = text;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) {
            switch (text[i]) {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') {
                        i++;
                    }

                    starts.Add(i + 1);
                    break;
                case '\n':
                    starts.Add(i + 1);
                    break;
            }
        }

        _starts = [..starts];
    }

    public static LineIndex Of(SourceText text) => Cache.GetValue(text, static t => new LineIndex(t.Text));

    public static LineIndex Of(string text) => new(text);

    public int LineCount => _starts.Length;

    /// <summary>The offset of a position; a column past its line's end is the line's end, a line past the last is the text's end.</summary>
    public int OffsetOf(LspPosition position) {
        if (position.Line < 0) {
            return 0;
        }

        if (position.Line >= _starts.Length) {
            return _text.Length;
        }

        var start = _starts[position.Line];
        return Math.Min(start + Math.Max(position.Character, 0), LineEnd(position.Line));
    }

    /// <summary>The position of an offset.</summary>
    public LspPosition PositionOf(int offset) {
        offset = Math.Clamp(offset, 0, _text.Length);
        var line = Array.BinarySearch(_starts, offset);
        if (line < 0) {
            line = ~line - 1;
        }

        return new(line, offset - _starts[line]);
    }

    public LspRange RangeOf(TextSpan span) => new(PositionOf(span.Start), PositionOf(span.End));

    /// <summary>Where a line's text ends, before its line break.</summary>
    int LineEnd(int line) {
        var end = line + 1 < _starts.Length ? _starts[line + 1] : _text.Length;
        if (end > _starts[line] && line + 1 < _starts.Length) {
            end--;
            if (_text[end] == '\n' && end > _starts[line] && _text[end - 1] == '\r') {
                end--;
            }
        }

        return end;
    }
}
