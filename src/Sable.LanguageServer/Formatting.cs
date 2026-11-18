using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

public sealed partial class LanguageServer {
    /// <summary>
    ///     <c>textDocument/formatting</c>: what <c>sable fmt</c> would write,
    ///     as one edit over the lines that change, so that the editor keeps
    ///     the cursor and the folds of the lines that do not. The options
    ///     (tab size, spaces) are ignored — there is one style. A document
    ///     with a syntax error has only its whole declarations without one
    ///     formatted (<see cref="FormatRegions" />).
    /// </summary>
    Task<JsonNode?> FormattingAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        if (!HasSyntaxErrors(document)) {
            var text = document.Tree.Text.Text;
            return Task.FromResult<JsonNode?>(Edits(text, SyntaxFormatter.Format(document.Tree)));
        }

        return Task.FromResult<JsonNode?>(FormatRegions(document, 0, document.Tree.Text.Length));
    }

    /// <summary>
    ///     <c>textDocument/rangeFormatting</c>: the lines the range touches,
    ///     as formatting the document would leave them — and any lines the
    ///     formatter would join to them, since a line cannot be half-formatted.
    /// </summary>
    Task<JsonNode?> RangeFormattingAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        var range = parameters.Get("range") is { } r ? ReadRange(r) : new LspRange(default, new(int.MaxValue, 0));
        var start = document.Lines.OffsetOf(range.Start);
        var end = Math.Max(start, document.Lines.OffsetOf(range.End));
        return Task.FromResult<JsonNode?>(FormatRegions(document, start, end));
    }

    /// <summary>
    ///     <c>textDocument/onTypeFormatting</c> on a closing brace: the
    ///     declaration or statement it closes, formatted as a range.
    /// </summary>
    Task<JsonNode?> OnTypeFormattingAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        var offset = parameters.Get("position") is { } p ? document.Lines.OffsetOf(ReadPosition(p)) : 0;
        var brace = SemanticModel.FindTokenAt(document.Tree.Root, Math.Max(offset - 1, 0));
        if (brace.Kind != SyntaxKind.CloseBraceToken || brace.Parent is not { } closed) {
            return Task.FromResult<JsonNode?>(new JsonArray());
        }

        return Task.FromResult<JsonNode?>(FormatRegions(document, closed.Span.Start, closed.Span.End));
    }

    static Document DocumentOf(WorkspaceSnapshot snapshot, JsonElement parameters) {
        var uri = parameters.Get("textDocument")?.GetString("uri") ?? "";
        return snapshot.Find(uri) ?? throw new LspException(LspException.InvalidParams, $"'{uri}' is not open");
    }

    static bool HasSyntaxErrors(Document document) => document.Tree.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>
    ///     The edits that format the lines between two offsets as formatting
    ///     the whole document would. In a document with a syntax error, only
    ///     whole top-level declarations without one are touched: the
    ///     formatter keeps a tree's tokens, but what it makes of a broken
    ///     tree's is no one's intent.
    /// </summary>
    /// <remarks>
    ///     Formatting changes only the whitespace between tokens, so a place
    ///     in the text and one in the formatted text are matched by how many
    ///     characters that are not whitespace come before each. A region is
    ///     grown, both sides at once, until it is whole lines in both and
    ///     holds the same of those characters — which it is then checked to,
    ///     so a formatter that ever changed more would only format less.
    /// </remarks>
    static JsonArray FormatRegions(Document document, int start, int end) {
        var tree = document.Tree;
        var text = tree.Text.Text;
        string formatted;
        try {
            formatted = SyntaxFormatter.Format(tree);
        } catch (Exception e) when (e is not OutOfMemoryException) {
            // A tree too broken to write out has nothing to format.
            return [];
        }

        if (formatted == text) {
            return [];
        }

        var regions = new List<(int Start, int End)>();
        if (!HasSyntaxErrors(document)) {
            regions.Add((start, end));
        } else if (tree.Root is CompilationUnitSyntax unit) {
            var errors = tree.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.Span).ToList();
            foreach (var member in unit.Members) {
                var span = member.Span;
                if (span.End < start || span.Start > end || errors.Any(e => e.Start <= span.End && span.Start <= e.End)) {
                    continue;
                }

                regions.Add((Math.Max(span.Start, start), Math.Min(span.End, end)));
            }
        }

        var original = new SolidIndex(text);
        var target = new SolidIndex(formatted);
        var edits = new JsonArray();
        var last = -1;
        foreach (var (from, to) in regions) {
            if (Align(original, target, from, to) is not var (a, b, fa, fb) || a < last) {
                continue;
            }

            if (Edit(document, text, a, b, formatted[fa..fb]) is { } edit) {
                edits.Add(edit);
            }

            last = b;
        }

        return edits;
    }

    /// <summary>
    ///     The region of the text, from <paramref name="start" /> to
    ///     <paramref name="end" /> grown to whole lines, and the region of
    ///     the formatted text that holds the same tokens; null when the two
    ///     cannot be matched.
    /// </summary>
    static (int Start, int End, int FormattedStart, int FormattedEnd)? Align(SolidIndex text, SolidIndex formatted, int start, int end) {
        if (text.Total != formatted.Total) {
            return null;
        }

        var a = text.LineStart(start);
        var b = text.LineEnd(Math.Max(start, end - 1));
        for (var rounds = 0; rounds < 1000; rounds++) {
            var (from, to) = (text.Before(a), text.Before(b));
            if (from == to) {
                return null;
            }

            var fa = formatted.LineStart(formatted.At(from));
            var fb = formatted.LineEnd(formatted.At(to - 1));
            var (ffrom, fto) = (formatted.Before(fa), formatted.Before(fb));
            if (ffrom == from && fto == to) {
                return text.Solid(a, b) == formatted.Solid(fa, fb) ? (a, b, fa, fb) : null;
            }

            a = Math.Min(a, text.LineStart(text.At(ffrom)));
            b = Math.Max(b, text.LineEnd(text.At(fto - 1)));
        }

        return null;
    }

    /// <summary>Replacing one region of whole lines with another, as an edit over just the lines that differ.</summary>
    static JsonObject? Edit(Document document, string text, int start, int end, string replacement) {
        var old = text[start..end];
        if (old == replacement) {
            return null;
        }

        var edits = Edits(old, replacement);
        var edit = (JsonObject)edits[0]!;
        edits.Clear();
        var range = edit["range"]!;
        var first = document.Lines.PositionOf(start).Line;
        foreach (var side in new[] { "start", "end" }) {
            range[side]!["line"] = range[side]!["line"]!.GetValue<int>() + first;
        }

        // The region starts a line, so a column in it is the document's.
        return edit;
    }

    /// <summary>The edit turning <paramref name="text" /> into <paramref name="formatted" />: whole lines, from the first that differs to the last.</summary>
    static JsonArray Edits(string text, string formatted) {
        if (text == formatted) {
            return [];
        }

        var start = 0;
        var limit = Math.Min(text.Length, formatted.Length);
        while (start < limit && text[start] == formatted[start]) {
            start++;
        }

        var end = 0;
        while (end < limit - start && text[^(end + 1)] == formatted[^(end + 1)]) {
            end++;
        }

        // Out to whole lines, in both texts at once: what precedes start and
        // follows the end is the same in both.
        while (start > 0 && text[start - 1] != '\n') {
            start--;
        }

        while (end > 0 && end < text.Length && text[^(end + 1)] != '\n') {
            end--;
        }

        var lines = LineIndex.Of(text);
        var span = TextSpan.FromBounds(start, text.Length - end);
        return [
            new JsonObject {
                ["range"] = RangeNode(lines.RangeOf(span)),
                ["newText"] = formatted[start..^end]
            },
        ];
    }

    /// <summary>A text's characters that are not whitespace, counted: how a place in it is matched to one in the same text formatted.</summary>
    sealed class SolidIndex {
        readonly string _text;
        readonly int[] _before;
        readonly List<int> _positions = [];

        public SolidIndex(string text) {
            _text = text;
            _before = new int[text.Length + 1];
            for (var i = 0; i < text.Length; i++) {
                _before[i + 1] = _before[i];
                if (!char.IsWhiteSpace(text[i])) {
                    _before[i + 1]++;
                    _positions.Add(i);
                }
            }
        }

        public int Total => _positions.Count;

        /// <summary>How many come before an offset.</summary>
        public int Before(int offset) => _before[offset];

        /// <summary>Where the one at an index is; the end of the text past the last.</summary>
        public int At(int index) => index < _positions.Count ? _positions[index] : _text.Length;

        public int LineStart(int offset) => offset <= 0 ? 0 : _text.LastIndexOf('\n', Math.Min(offset, _text.Length) - 1) + 1;

        /// <summary>Past the line break that ends the line of an offset, or the end of the text.</summary>
        public int LineEnd(int offset) {
            var newline = offset >= _text.Length ? -1 : _text.IndexOf('\n', offset);
            return newline < 0 ? _text.Length : newline + 1;
        }

        public string Solid(int start, int end) => string.Concat(_text[start..end].Where(static c => !char.IsWhiteSpace(c)));
    }
}
