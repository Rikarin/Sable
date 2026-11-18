using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     What the editor asks of a file's shape: the uses of a name to
///     highlight, the declarations of the workspace by name, the regions to
///     fold, and the ranges a selection grows through.
/// </summary>
public sealed partial class LanguageServer {
    /// <summary><c>textDocument/documentHighlight</c>: every place in the file that names what the position names.</summary>
    async Task<JsonNode?> DocumentHighlightAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var analysis = await AnalysisOfAsync(snapshot, document);
        cancellation.ThrowIfCancellationRequested();

        var highlights = new JsonArray();
        lock (analysis.Gate) {
            var model = analysis.Model(document.Tree);
            if (model.GetSymbolAt(offset) is not { } symbol) {
                return highlights;
            }

            var declaration = !symbol.DeclaringSyntax.IsEmpty && analysis.DocumentOf(symbol.DeclaringSyntax[0]) == document
                ? SemanticModel.NameTokenOf(symbol, symbol.DeclaringSyntax[0])
                : default;
            foreach (var span in model.FindReferences(symbol)) {
                // The declaration is where the name is written, as a write
                // is; every other place reads it, as far as the editor shows.
                highlights.Add(new JsonObject {
                    ["range"] = RangeNode(document.Lines.RangeOf(span)),
                    ["kind"] = !declaration.IsNone && declaration.Span == span ? 3 : 2
                });
            }
        }

        return highlights;
    }

    // -----------------------------------------------------------------------
    // Workspace symbols
    // -----------------------------------------------------------------------

    const int MaximumSymbols = 500;

    /// <summary>
    ///     <c>workspace/symbol</c>: the declarations of every file of the
    ///     workspace whose name has the query's letters in order. Syntax
    ///     alone: it needs no compilation, and finds what does not compile.
    /// </summary>
    async Task<JsonNode?> WorkspaceSymbolsAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        snapshot = await LoadedAsync(snapshot);
        var query = parameters.GetString("query") ?? "";
        var found = new List<(int Rank, JsonObject Symbol)>();
        foreach (var document in snapshot.Documents.Values.OrderBy(static d => d.Uri, StringComparer.Ordinal)) {
            cancellation.ThrowIfCancellationRequested();
            var members = document.Tree.Root is CompilationUnitSyntax unit ? unit.Members : default;
            foreach (var (name, kind, container, selection) in Declarations(members, null)) {
                if (!Matches(query, name)) {
                    continue;
                }

                var symbol = new JsonObject {
                    ["name"] = name,
                    ["kind"] = kind,
                    ["location"] = new JsonObject { ["uri"] = document.Uri, ["range"] = RangeNode(document.Lines.RangeOf(selection)) }
                };
                if (container is not null) {
                    symbol["containerName"] = container;
                }

                // A name that starts as typed comes first, then one that has it whole.
                var rank = name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
                    : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1
                    : 2;
                found.Add((rank, symbol));
            }
        }

        return new JsonArray([..found.OrderBy(static f => f.Rank).Take(MaximumSymbols).Select(static f => f.Symbol)]);
    }

    /// <summary>The declarations among members and those inside them: name, protocol kind, container, and the name's span.</summary>
    static IEnumerable<(string Name, int Kind, string? Container, TextSpan Selection)> Declarations(IEnumerable<SyntaxNode> members, string? container) {
        foreach (var member in members) {
            var (name, children) = OutlineOf(member);
            if (name.Length == 0) {
                continue;
            }

            var selection = member.DescendantTokens().FirstOrDefault(t => t.Text == name);
            yield return (name, SymbolDisplay.SymbolKindOf(member), container, selection.IsNone ? member.Span : selection.Span);
            foreach (var nested in Declarations(children, container is null ? name : container + "." + name)) {
                yield return nested;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Folding and selection
    // -----------------------------------------------------------------------

    /// <summary>
    ///     <c>textDocument/foldingRange</c>: what a pair of braces, brackets
    ///     or parentheses spans over several lines; a run of imports; a block
    ///     comment, or a run of line comments.
    /// </summary>
    Task<JsonNode?> FoldingRangesAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        var lines = document.Lines;
        var ranges = new JsonArray();
        var seen = new HashSet<(int, int)>();

        void Add(int startLine, int endLine, string? kind) {
            if (endLine > startLine && seen.Add((startLine, endLine))) {
                var range = new JsonObject { ["startLine"] = startLine, ["endLine"] = endLine };
                if (kind is not null) {
                    range["kind"] = kind;
                }

                ranges.Add(range);
            }
        }

        // Each closing token folds back to its opener in the same node;
        // the closing line stays visible.
        var open = new Stack<SyntaxToken>();
        var comments = new List<(int First, int Last)>();
        int? importStart = null, importEnd = null;
        foreach (var token in document.Tree.Root.DescendantTokens()) {
            cancellation.ThrowIfCancellationRequested();
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia)) {
                if (trivia.IsComment) {
                    comments.Add((lines.PositionOf(trivia.Span.Start).Line, lines.PositionOf(Math.Max(trivia.Span.Start, trivia.Span.End - 1)).Line));
                }
            }

            switch (token.Kind) {
                case SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenParenToken:
                    open.Push(token);
                    break;
                case SyntaxKind.CloseBraceToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseParenToken when open.Count > 0 && !token.IsMissing:
                    var opener = open.Pop();
                    Add(lines.PositionOf(opener.Span.Start).Line, lines.PositionOf(token.Span.Start).Line - 1, null);
                    break;
            }

            if (token.Kind == SyntaxKind.ImportKeyword && token.Parent is ImportDeclarationSyntax import) {
                var startLine = lines.PositionOf(import.Span.Start).Line;
                var endLine = lines.PositionOf(import.Span.End).Line;
                if (importEnd is { } previous && startLine <= previous + 1) {
                    importEnd = endLine;
                } else {
                    if (importStart is { } s && importEnd is { } e) {
                        Add(s, e, "imports");
                    }

                    (importStart, importEnd) = (startLine, endLine);
                }
            }
        }

        if (importStart is { } a && importEnd is { } b) {
            Add(a, b, "imports");
        }

        // A comment over several lines folds; so does a run of comments on
        // lines one after another, a line comment each.
        for (var i = 0; i < comments.Count;) {
            var (first, last) = comments[i++];
            while (i < comments.Count && comments[i].First == last + 1 && comments[i].First == comments[i].Last) {
                last = comments[i++].Last;
            }

            Add(first, last, "comment");
        }

        return Task.FromResult<JsonNode?>(ranges);
    }

    /// <summary>
    ///     <c>textDocument/selectionRange</c>: for each position, the name or
    ///     token there, then each node around it — each range larger than
    ///     the one before.
    /// </summary>
    Task<JsonNode?> SelectionRangesAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        var result = new JsonArray();
        foreach (var position in parameters.Get("positions")?.EnumerateArray() ?? default) {
            var offset = document.Lines.OffsetOf(ReadPosition(position));
            var token = SemanticModel.FindToken(document.Tree.Root, offset);
            var spans = new List<TextSpan>();
            if (!token.IsNone) {
                spans.Add(token.Span);
            }

            for (var node = token.IsNone ? document.Tree.Root : token.Parent; node is not null; node = node.Parent) {
                if (spans.Count == 0 || node.Span.Length > spans[^1].Length && node.Span.Start <= spans[^1].Start && node.Span.End >= spans[^1].End) {
                    spans.Add(node.Span);
                }
            }

            JsonObject? range = null;
            for (var i = spans.Count - 1; i >= 0; i--) {
                var next = new JsonObject { ["range"] = RangeNode(document.Lines.RangeOf(spans[i])) };
                if (range is not null) {
                    next["parent"] = range;
                }

                range = next;
            }

            result.Add(range ?? new JsonObject { ["range"] = RangeNode(document.Lines.RangeOf(new TextSpan(offset, 0))) });
        }

        return Task.FromResult<JsonNode?>(result);
    }
}
