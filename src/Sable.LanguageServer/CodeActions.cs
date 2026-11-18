using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     <c>textDocument/codeAction</c>: quick fixes for what the lints report
///     — the fix each one's message suggests, and turning the lint off for
///     the declaration with <c>@allow</c>. Those of the older lints are
///     worked out from the text alone; any other lint's are the fixes it
///     carried when it ran over the document's compilation. The compiler's
///     own errors get the fixes they carry (<c>try</c>, a match's missing
///     cases, a protocol's stubs), an import for a name no module in scope
///     declares, and the label a call's parameter has; and the whole file
///     gets <c>source.fixAll</c>, every first fix at once.
/// </summary>
public sealed partial class LanguageServer {
    [GeneratedRegex(@"\[([a-z_]+)\]$")]
    private static partial Regex LintName();

    [GeneratedRegex("'([A-Za-z_][A-Za-z0-9_]*)'")]
    private static partial Regex QuotedIdentifier();

    async Task<JsonNode?> CodeActionsAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        var actions = new JsonArray();
        var context = parameters.Get("context");
        bool Wanted(string kind) =>
            context?.Get("only") is not { ValueKind: JsonValueKind.Array } only
            || only.EnumerateArray().Any(k => k.GetString() is { } asked && (kind.StartsWith(asked, StringComparison.Ordinal) || asked.Length == 0));

        if (Wanted("source.fixAll.sable") && context?.Get("only") is { ValueKind: JsonValueKind.Array }) {
            var all = await FixAllAsync(snapshot, document, cancellation);
            if (all.Count > 0) {
                actions.Add(new JsonObject {
                    ["title"] = "Fix everything that has a fix",
                    ["kind"] = "source.fixAll.sable",
                    ["edit"] = new JsonObject { ["changes"] = new JsonObject { [document.Uri] = all } }
                });
            }
        }

        if (!Wanted("quickfix")) {
            return actions;
        }

        IReadOnlyList<Diagnostic>? lints = null;
        IReadOnlyList<Diagnostic>? errors = null;
        foreach (var diagnostic in context?.Get("diagnostics")?.EnumerateArray() ?? default) {
            cancellation.ThrowIfCancellationRequested();
            if (diagnostic.GetString("source") != "sable" || diagnostic.GetString("code") is not { } code || diagnostic.Get("range") is not { } range) {
                continue;
            }

            var r = ReadRange(range);
            var span = TextSpan.FromBounds(document.Lines.OffsetOf(r.Start), Math.Max(document.Lines.OffsetOf(r.Start), document.Lines.OffsetOf(r.End)));
            var message = diagnostic.GetString("message") ?? "";
            var lint = LintName().Match(message);
            if (!lint.Success) {
                var analysis = await AnalysisOfAsync(snapshot, document);
                errors ??= analysis.Snapshot.Find(document.Uri) is { } known && ReferenceEquals(known.Tree, document.Tree)
                    ? analysis.DiagnosticsOf(document.Tree, cancellation)
                    : [];
                var preferred = true;
                foreach (var fix in errors.Where(d => d.Id == code && d.Span == span && d.Message == message).SelectMany(static d => d.Fixes)) {
                    actions.Add(Action(document, fix.Title, diagnostic, [..fix.Changes.Select(c => TextEdit(document, c.Span, c.NewText))], preferred));
                    preferred = false;
                }

                foreach (var (fixTitle, fixEdits) in await ErrorFixesAsync(analysis, document, code, span, message)) {
                    actions.Add(Action(document, fixTitle, diagnostic, fixEdits, false));
                }

                continue;
            }

            if (FixOf(document, code, span, message) is var (title, edits) && edits.Count > 0) {
                actions.Add(Action(document, title, diagnostic, edits, true));
            } else {
                lints ??= (await AnalysisOfAsync(snapshot, document)).LintsOf(document.Tree, cancellation);
                foreach (var fix in lints.Where(d => d.Id == code && d.Span == span && d.Message == message).SelectMany(static d => d.Fixes).Take(1)) {
                    actions.Add(Action(document, fix.Title, diagnostic, [..fix.Changes.Select(c => TextEdit(document, c.Span, c.NewText))], true));
                }
            }

            if (AllowOf(document, span, lint.Groups[1].Value) is { } allow) {
                actions.Add(Action(document, $"Allow '{lint.Groups[1].Value}' in this declaration", diagnostic, [allow], false));
            }
        }

        return actions;
    }

    [GeneratedRegex(@"argument (\d+) is labelled '([A-Za-z_][A-Za-z0-9_]*)', and the parameter is '([A-Za-z_][A-Za-z0-9_]*)'")]
    private static partial Regex WrongLabel();

    /// <summary>
    ///     Fixes the compiler does not carry, worked out here: the import of
    ///     each module — the workspace's, or the library's, imported or not —
    ///     with a public name a file could not find, and the label a call's
    ///     parameter has where its argument says another.
    /// </summary>
    async Task<List<(string Title, List<JsonObject> Edits)>> ErrorFixesAsync(Analysis analysis, Document document, string code, TextSpan span, string message) {
        var fixes = new List<(string, List<JsonObject>)>();
        switch (code) {
            case "S2002" when QuotedIdentifier().Match(message) is { Success: true } quoted && document.Tree.Root is CompilationUnitSyntax unit: {
                var name = quoted.Groups[1].Value;
                var own = unit.Module?.Name.ToString().Trim();
                var imported = unit.Imports.Select(static i => i.Name.ToString().Trim()).ToHashSet(StringComparer.Ordinal);
                var modules = new SortedSet<string>(StringComparer.Ordinal);
                lock (analysis.Gate) {
                    foreach (var module in analysis.Compilation.Modules) {
                        if (module.Members.Any(m => m.Name == name && m.DeclaredAccessibility == Accessibility.Public)) {
                            modules.Add(module.Name);
                        }
                    }
                }

                foreach (var entry in await _library.GetAsync()) {
                    if (entry.Symbol.Name == name) {
                        modules.Add(entry.Module);
                    }
                }

                var line = ImportLineOf(document);
                foreach (var module in modules.Where(m => m.Length > 0 && m != own && m != CoreLibrary.ModuleName && !imported.Contains(m))) {
                    fixes.Add(($"Import {module}", [TextEdit(document, new(document.Lines.OffsetOf(new(line, 0)), 0), $"import {module}\n")]));
                }

                break;
            }

            case "S3007" when WrongLabel().Match(message) is { Success: true } wrong: {
                var index = int.Parse(wrong.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
                InvocationExpressionSyntax? call = null;
                for (var node = SemanticModel.FindToken(document.Tree.Root, span.Start).Parent; node is not null && call is null; node = node.Parent) {
                    call = node as InvocationExpressionSyntax;
                }

                if (call?.ArgumentList?.Arguments.ElementAtOrDefault(index) is { NameToken: { IsNone: false } label } && label.Text == wrong.Groups[2].Value) {
                    var expected = wrong.Groups[3].Value;
                    fixes.Add(($"Label it '{expected}'", [TextEdit(document, label.Span, expected)]));
                }

                break;
            }
        }

        return fixes;
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
