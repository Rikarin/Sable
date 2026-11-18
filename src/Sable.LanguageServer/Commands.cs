using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     Code lenses (how many places use each type and function), document
///     links (an import to its module's file, a URL in a comment to the
///     page), and <c>workspace/executeCommand</c>'s one command:
///     <c>sable.fixAll</c>, every fix the file's diagnostics carry, applied
///     through <c>workspace/applyEdit</c>.
/// </summary>
public sealed partial class LanguageServer {
    /// <summary>The commands the server runs itself.</summary>
    internal const string FixAllCommand = "sable.fixAll";

    /// <summary>
    ///     What a references lens runs: a client command, since showing a
    ///     list of places is the editor's to do; the VS Code extension maps it
    ///     to its own references view.
    /// </summary>
    internal const string ShowReferencesCommand = "sable.showReferences";

    [GeneratedRegex(@"https?://[^\s<>""'`)\]}]+")]
    private static partial Regex Url();

    // -----------------------------------------------------------------------
    // Code lenses
    // -----------------------------------------------------------------------

    /// <summary>
    ///     <c>textDocument/codeLens</c>: a lens on the name of each type and
    ///     function the file declares; how many places use it is worked out
    ///     when the editor shows the lens (<c>codeLens/resolve</c>).
    /// </summary>
    Task<JsonNode?> CodeLensesAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        var lenses = new JsonArray();
        foreach (var node in document.Tree.Root.DescendantNodes()) {
            cancellation.ThrowIfCancellationRequested();
            var name = node switch {
                StructDeclarationSyntax s => s.Identifier,
                ClassDeclarationSyntax c => c.Identifier,
                ActorDeclarationSyntax a => a.Identifier,
                UnionDeclarationSyntax u => u.Identifier,
                EnumDeclarationSyntax e => e.Identifier,
                ProtocolDeclarationSyntax p => p.Identifier,
                FunctionDeclarationSyntax f when !InBody(f) => f.Identifier,
                _ => default,
            };

            if (name.IsNone || name.IsMissing) {
                continue;
            }

            var range = document.Lines.RangeOf(name.Span);
            lenses.Add(new JsonObject {
                ["range"] = RangeNode(range),
                ["data"] = new JsonObject { ["uri"] = document.Uri, ["position"] = PositionNode(range.Start) }
            });
        }

        return Task.FromResult<JsonNode?>(lenses);

        // A local function is not worth a lens: its uses are a few lines away.
        static bool InBody(SyntaxNode node) {
            for (var parent = node.Parent; parent is not null; parent = parent.Parent) {
                if (parent is BlockSyntax) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary><c>codeLens/resolve</c>: how many places name the declaration, as a command that lists them.</summary>
    async Task<JsonNode?> CodeLensResolveAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var lens = JsonNode.Parse(parameters.GetRawText())!.AsObject();
        if (parameters.Get("data") is not { } data || data.GetString("uri") is not { } uri || data.Get("position") is not { } position
            || snapshot.Find(uri) is not { } document) {
            return lens;
        }

        var offset = document.Lines.OffsetOf(ReadPosition(position));
        var (analysis, edits) = await ReferenceAnalysisAsync(snapshot, document);
        Symbol? symbol;
        lock (analysis.Gate) {
            symbol = SymbolAt(analysis, edits, document, offset).Symbol;
        }

        var locations = new JsonArray();
        if (symbol is not null) {
            var references = References(analysis, symbol, document, cancellation, edits);
            _analyses.Bound(analysis);
            foreach (var (owner, span, isDeclaration) in references) {
                if (!isDeclaration) {
                    locations.Add(new JsonObject { ["uri"] = owner.Uri, ["range"] = RangeNode(owner.Lines.RangeOf(span)) });
                }
            }
        }

        lens["command"] = new JsonObject {
            ["title"] = locations.Count == 1 ? "1 reference" : $"{locations.Count} references",
            ["command"] = ShowReferencesCommand,
            ["arguments"] = new JsonArray(uri, JsonNode.Parse(position.GetRawText()), locations)
        };
        return lens;
    }

    // -----------------------------------------------------------------------
    // Document links
    // -----------------------------------------------------------------------

    /// <summary>
    ///     <c>textDocument/documentLink</c>: each import's module name, to the
    ///     first file that declares the module — a document, a dependency's,
    ///     or the copy of the library's — and each URL in a comment.
    /// </summary>
    async Task<JsonNode?> DocumentLinksAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        var links = new JsonArray();
        foreach (var token in document.Tree.Root.DescendantTokens()) {
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia)) {
                if (trivia.Kind is not (SyntaxKind.LineCommentTrivia or SyntaxKind.BlockCommentTrivia or SyntaxKind.DocCommentTrivia or SyntaxKind.ModuleDocCommentTrivia)) {
                    continue;
                }

                foreach (Match url in Url().Matches(trivia.Text)) {
                    var span = new TextSpan(trivia.Span.Start + url.Index, url.Length);
                    links.Add(new JsonObject { ["range"] = RangeNode(document.Lines.RangeOf(span)), ["target"] = url.Value.TrimEnd('.', ',', ';', ':') });
                }
            }
        }

        if (document.Tree.Root is not CompilationUnitSyntax unit || !unit.Imports.Any()) {
            return links;
        }

        var analysis = await AnalysisOfAsync(snapshot, document);
        cancellation.ThrowIfCancellationRequested();
        foreach (var import in unit.Imports) {
            var name = import.Name.ToString().Trim();
            var declaring = analysis.Compilation.SyntaxTrees
                .Where(t => CoreLibrary.ModuleOf(t) == name)
                .OrderBy(static t => t.Text.FileName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (declaring is not null && FileOf(analysis, document, declaring.Root) is var (target, _)) {
                links.Add(new JsonObject {
                    ["range"] = RangeNode(document.Lines.RangeOf(import.Name.Span)),
                    ["target"] = target,
                    ["tooltip"] = $"module {name}"
                });
            }
        }

        return links;
    }

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    /// <summary><c>workspace/executeCommand</c>: <c>sable.fixAll</c> takes a document's URI and applies its fixes.</summary>
    async Task<JsonNode?> ExecuteCommandAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var command = parameters.GetString("command");
        if (command != FixAllCommand) {
            throw new LspException(LspException.InvalidParams, $"no command '{command}'");
        }

        var uri = parameters.Get("arguments") is { ValueKind: JsonValueKind.Array } arguments && arguments.GetArrayLength() > 0
            ? arguments[0].GetString()
            : null;
        var document = (uri is null ? null : snapshot.Find(uri)) ?? throw new LspException(LspException.InvalidParams, $"'{uri}' is not open");
        var edits = await FixAllAsync(snapshot, document, cancellation);
        if (edits.Count == 0) {
            return null;
        }

        var answer = await SendRequestAsync("workspace/applyEdit", new JsonObject {
            ["label"] = "Fix all",
            ["edit"] = new JsonObject { ["changes"] = new JsonObject { [document.Uri] = edits } }
        });
        return answer.Get("result")?.GetBool("applied") is true ? null : throw new LspException(LspException.RequestFailed, "the editor did not apply the fixes");
    }

    /// <summary>
    ///     The first fix of each diagnostic of a document — its type errors'
    ///     and its lints' — leaving out any that would overlap one already
    ///     taken: the edits of <c>sable.fixAll</c> and the <c>source.fixAll</c>
    ///     code action.
    /// </summary>
    async Task<JsonArray> FixAllAsync(WorkspaceSnapshot snapshot, Document document, CancellationToken cancellation) {
        var analysis = await AnalysisOfAsync(snapshot, document);
        document = analysis.Snapshot.Find(document.Uri) ?? document;
        var diagnostics = analysis.DiagnosticsOf(document.Tree, cancellation).Concat(analysis.LintsOf(document.Tree, cancellation));
        var taken = new List<TextSpan>();
        var edits = new JsonArray();
        foreach (var fix in diagnostics.SelectMany(static d => d.Fixes.Take(1))) {
            if (fix.Changes.Any(c => taken.Any(t => t.OverlapsWith(c.Span) || t.Start == c.Span.Start))) {
                continue;
            }

            foreach (var change in fix.Changes) {
                taken.Add(change.Span);
                edits.Add(TextEdit(document, change.Span, change.NewText));
            }
        }

        return edits;
    }
}
