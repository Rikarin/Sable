using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     <c>textDocument/codeAction</c>: quick fixes for what the lints report
///     — the fix each one's message suggests, and turning the lint off for
///     the declaration with <c>@allow</c>. Syntax alone: they need the text,
///     not a compilation.
/// </summary>
public sealed partial class LanguageServer {
    [GeneratedRegex(@"\[([a-z_]+)\]$")]
    private static partial Regex LintName();

    [GeneratedRegex("'([A-Za-z_][A-Za-z0-9_]*)'")]
    private static partial Regex QuotedIdentifier();

    Task<JsonNode?> CodeActionsAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        var actions = new JsonArray();
        var context = parameters.Get("context");
        if (context?.Get("only") is { ValueKind: JsonValueKind.Array } only
            && !only.EnumerateArray().Any(static k => k.GetString() is { } kind && ("quickfix".StartsWith(kind, StringComparison.Ordinal) || kind.Length == 0))) {
            return Task.FromResult<JsonNode?>(actions);
        }

        foreach (var diagnostic in context?.Get("diagnostics")?.EnumerateArray() ?? default) {
            cancellation.ThrowIfCancellationRequested();
            if (diagnostic.GetString("source") != "sable" || diagnostic.GetString("code") is not { } code || diagnostic.Get("range") is not { } range) {
                continue;
            }

            var lint = LintName().Match(diagnostic.GetString("message") ?? "");
            if (!lint.Success) {
                continue;
            }

            var r = ReadRange(range);
            var span = TextSpan.FromBounds(document.Lines.OffsetOf(r.Start), Math.Max(document.Lines.OffsetOf(r.Start), document.Lines.OffsetOf(r.End)));
            var message = diagnostic.GetString("message") ?? "";
            if (FixOf(document, code, span, message) is var (title, edits) && edits.Count > 0) {
                actions.Add(Action(document, title, diagnostic, edits, true));
            }

            if (AllowOf(document, span, lint.Groups[1].Value) is { } allow) {
                actions.Add(Action(document, $"Allow '{lint.Groups[1].Value}' in this declaration", diagnostic, [allow], false));
            }
        }

        return Task.FromResult<JsonNode?>(actions);
    }

    static JsonObject Action(Document document, string title, JsonElement diagnostic, IReadOnlyList<JsonObject> edits, bool preferred) =>
        new() {
            ["title"] = title,
            ["kind"] = "quickfix",
            ["diagnostics"] = new JsonArray(JsonNode.Parse(diagnostic.GetRawText())),
            ["isPreferred"] = preferred,
            ["edit"] = new JsonObject { ["changes"] = new JsonObject { [document.Uri] = new JsonArray([..edits]) } }
        };

    static JsonObject TextEdit(Document document, TextSpan span, string text) =>
        new() { ["range"] = RangeNode(document.Lines.RangeOf(span)), ["newText"] = text };

    /// <summary>What a lint's message says to do, as edits; null for a lint whose fix is not mechanical.</summary>
    static (string Title, List<JsonObject> Edits)? FixOf(Document document, string code, TextSpan span, string message) {
        var tokens = TokensIn(document, span).ToList();
        var name = QuotedIdentifier().Match(message) is { Success: true } quoted ? quoted.Groups[1].Value : null;
        switch (code) {
            // unused_local: '_' before the name says nobody reads it.
            case "S3300" when name is not null && tokens.FirstOrDefault(t => t.Kind == SyntaxKind.IdentifierToken && t.Text == name) is { IsNone: false } local:
                return ($"Prefix '{name}' with '_'", [TextEdit(document, new TextSpan(local.Span.Start, 0), "_")]);

            // unused_parameter: the name, not the label callers write.
            case "S3301" when name is not null && tokens.Select(static t => t.Parent).OfType<ParameterSyntax>().FirstOrDefault() is { } parameter:
                var renamed = "_" + parameter.Identifier.Text;
                return parameter.Label.IsNone || parameter.Label.IsMissing
                    ? ($"Name the parameter '{renamed}', keeping its label", [TextEdit(document, parameter.Identifier.Span, $"{parameter.Identifier.Text} {renamed}")])
                    : ($"Name the parameter '{renamed}'", [TextEdit(document, parameter.Identifier.Span, renamed)]);

            // unused_import: the whole line goes.
            case "S3302" when tokens.Select(static t => t.Parent).OfType<ImportDeclarationSyntax>().FirstOrDefault() is { } import:
                var lines = document.Lines;
                var first = lines.PositionOf(import.Span.Start).Line;
                var last = lines.PositionOf(import.Span.End).Line;
                var whole = TextSpan.FromBounds(lines.OffsetOf(new(first, 0)), lines.OffsetOf(new(last + 1, 0)));
                return ("Remove the import", [TextEdit(document, whole, "")]);

            // var_never_mutated: 'let'.
            case "S3303" when tokens.FirstOrDefault(static t => t.Kind == SyntaxKind.VarKeyword) is { IsNone: false } keyword:
                return ("Declare it with 'let'", [TextEdit(document, keyword.Span, "let")]);

            default:
                return null;
        }
    }

    /// <summary>The tokens a span covers, or the one it starts at.</summary>
    static IEnumerable<SyntaxToken> TokensIn(Document document, TextSpan span) {
        var any = false;
        foreach (var token in document.Tree.Root.DescendantTokens()) {
            if (token.Span.Start >= span.Start && token.Span.End <= span.End && token.Span.Length > 0) {
                any = true;
                yield return token;
            } else if (token.Span.Start > span.End) {
                break;
            }
        }

        if (!any && SemanticModel.FindToken(document.Tree.Root, span.Start) is { IsNone: false } at) {
            yield return at;
        }
    }

    /// <summary><c>@allow(lint)</c> on its own line before the declaration around a span.</summary>
    static JsonObject? AllowOf(Document document, TextSpan span, string lint) {
        if (SemanticModel.FindMember(document.Tree.Root, span.Start) is not { } member) {
            return null;
        }

        var start = member.Span.Start;
        var column = document.Lines.PositionOf(start).Character;
        var indent = document.Tree.Text.Text.Substring(start - column, column);
        if (indent.Any(static c => !char.IsWhiteSpace(c))) {
            indent = new string(' ', column);
        }

        return TextEdit(document, new TextSpan(start, 0), $"@allow({lint})\n{indent}");
    }
}
