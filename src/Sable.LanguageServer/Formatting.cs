using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Syntax;

namespace Sable.LanguageServer;

public sealed partial class LanguageServer {
    /// <summary>
    ///     <c>textDocument/formatting</c>: what <c>sable fmt</c> would write,
    ///     as one edit over the lines that change, so that the editor keeps
    ///     the cursor and the folds of the lines that do not. A document with
    ///     a syntax error is left as it is, as <c>sable fmt</c> leaves it; the
    ///     options (tab size, spaces) are ignored — there is one style.
    /// </summary>
    Task<JsonNode?> FormattingAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var uri = parameters.Get("textDocument")?.GetString("uri") ?? "";
        var document = snapshot.Find(uri) ?? throw new LspException(LspException.InvalidParams, $"'{uri}' is not open");
        if (document.Tree.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)) {
            return Task.FromResult<JsonNode?>(new JsonArray());
        }

        var text = document.Tree.Text.Text;
        var formatted = SyntaxFormatter.Format(document.Tree);
        return Task.FromResult<JsonNode?>(Edits(document, text, formatted));
    }

    /// <summary>The edit turning <paramref name="text" /> into <paramref name="formatted" />: whole lines, from the first that differs to the last.</summary>
    static JsonArray Edits(Document document, string text, string formatted) {
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

        var span = TextSpan.FromBounds(start, text.Length - end);
        return [
            new JsonObject {
                ["range"] = RangeNode(document.Lines.RangeOf(span)),
                ["newText"] = formatted[start..^end]
            },
        ];
    }
}
