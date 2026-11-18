using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     Where a position is, semantically, while the user types: a model
///     that has bound the member around it as it is now.
/// </summary>
/// <param name="Model">
///     A member model of the document's own tree, a speculative model of the
///     member's new text over an older compilation, or a file's model when
///     the position is in no member.
/// </param>
internal sealed record EditContext(Analysis Analysis, SemanticModel Model, bool Speculative);

/// <summary>The symbols of the latest completion list, by index, which <c>completionItem/resolve</c> documents.</summary>
internal sealed record CompletionMemory(int Id, Analysis Analysis, IReadOnlyList<Symbol> Symbols);

public sealed partial class LanguageServer {
    /// <summary>
    ///     A model for a position in the document as it is, as cheaply as
    ///     there is one: the newest analysis, if it has this very tree; else
    ///     the member's new text bound speculatively against the newest
    ///     analysis's version of it; else — a new member, or none — an
    ///     analysis of this snapshot, built now.
    /// </summary>
    async Task<EditContext> EditContextAsync(WorkspaceSnapshot snapshot, Document document, int offset) {
        snapshot = await LoadedAsync(snapshot);
        var tree = document.Tree;
        var member = SemanticModel.FindMember(tree.Root, offset);

        var key = snapshot.KeyOf(document);
        var latest = _analyses.Latest(key);
        if (latest is not null && member is not null && !latest.Contains(tree)
            && latest.Snapshot.Find(document.Uri) is { } older
            && latest.Contains(older.Tree)
            && MapMember(older.Tree.Root, member) is { } original) {
            lock (latest.Gate) {
                return new(latest, latest.Model(older.Tree).GetSpeculativeModel(original, member, offset), true);
            }
        }

        var analysis = latest is not null && latest.Contains(tree) ? latest : await _analyses.GetAsync(snapshot, key);
        lock (analysis.Gate) {
            var model = analysis.Model(tree);
            return new(analysis, member is null ? model : model.GetMemberModel(member), false);
        }
    }

    /// <summary>
    ///     The declaration in an older tree that a member of a newer one is
    ///     an edit of: the one at the same place among the same declarations,
    ///     of the same kind and name.
    /// </summary>
    internal static SyntaxNode? MapMember(SyntaxNode olderRoot, SyntaxNode member) {
        var path = new List<(int Index, SyntaxKind Kind, string Name)>();
        for (var node = member; node.Parent is { } parent; node = parent) {
            var index = 0;
            foreach (var child in parent.ChildNodes()) {
                if (ReferenceEquals(child, node)) {
                    break;
                }

                index++;
            }

            path.Add((index, node.Kind, NameOf(node)));
        }

        path.Reverse();
        var current = olderRoot;
        foreach (var (index, kind, name) in path) {
            var child = current.ChildNodes().ElementAtOrDefault(index);
            if (child is null || child.Kind != kind || NameOf(child) != name) {
                return null;
            }

            current = child;
        }

        return current;
    }

    static string NameOf(SyntaxNode node) {
        foreach (var token in node.DescendantTokens()) {
            if (token.Kind == SyntaxKind.IdentifierToken) {
                return token.Text;
            }

            if (token.Kind is SyntaxKind.OpenBraceToken or SyntaxKind.OpenParenToken or SyntaxKind.EqualsToken) {
                break;
            }
        }

        return "";
    }

    // -----------------------------------------------------------------------
    // Completion
    // -----------------------------------------------------------------------

    const int MaximumItems = 400;

    CompletionMemory? _lastCompletion;
    int _completions;

    static readonly string[] Keywords = [
        "let", "var", "fn", "if", "else", "guard", "while", "repeat", "for", "in", "match", "case", "return",
        "throw", "try", "await", "break", "continue", "defer", "do", "catch", "struct", "class", "actor",
        "union", "enum", "protocol", "extension", "import", "module", "public", "private", "internal",
        "static", "mutating", "init", "self", "Self", "true", "false", "none", "region", "unsafe", "async",
        "throws", "is", "as", "where", "move", "ref", "inout", "consuming", "borrowing", "alias", "const",
    ];

    [GeneratedRegex(@"^\s*(public\s+)?import\s+([A-Za-z0-9_.]*)$")]
    private static partial Regex ImportLine();

    async Task<JsonNode?> CompletionAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var text = document.Tree.Text.Text;

        var start = offset;
        while (start > 0 && IsNameCharacter(text[start - 1])) {
            start--;
        }

        var prefix = text[start..offset];

        // 'import Sable.Co|': module names, the whole dotted name replaced.
        var lineStart = text.LastIndexOfAny(['\n', '\r'], Math.Max(offset - 1, 0)) + 1;
        if (lineStart <= offset && ImportLine().Match(text[lineStart..offset]) is { Success: true } import) {
            return await ModuleCompletionAsync(snapshot, document, offset, import.Groups[2].Value);
        }

        var context = await EditContextAsync(snapshot, document, offset);
        cancellation.ThrowIfCancellationRequested();

        var items = new List<(Symbol Symbol, int Rank)>();
        var root = document.Tree.Root;
        var keywords = true;
        lock (context.Analysis.Gate) {
            if (start > 0 && SemanticModel.FindTokenAt(root, start - 1) is { Kind: SyntaxKind.DotToken or SyntaxKind.QuestionDotToken } dot) {
                keywords = false;
                if (dot.Parent is MemberAccessExpressionSyntax access) {
                    AddMembers(context.Model, access, dot.Kind == SyntaxKind.QuestionDotToken, items);
                }
            } else {
                var rank = 0;
                foreach (var symbol in context.Model.LookupSymbols(offset)) {
                    items.Add((symbol, rank++));
                }
            }

            var id = Interlocked.Increment(ref _completions);
            Volatile.Write(ref _lastCompletion, new CompletionMemory(id, context.Analysis, [..items.Select(static i => i.Symbol)]));
            return CompletionList(document, start, offset, prefix, items, keywords, id);
        }
    }

    /// <summary>
    ///     <c>completionItem/resolve</c>: the item, with the documentation of
    ///     the symbol it offers — read only for the item the editor shows, not
    ///     for each of hundreds offered.
    /// </summary>
    Task<JsonNode?> CompletionResolveAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var item = JsonNode.Parse(parameters.GetRawText())!.AsObject();
        if (parameters.Get("data") is { } data
            && data.GetInt("completion") is { } id
            && data.GetInt("index") is { } index
            && Volatile.Read(ref _lastCompletion) is { } last
            && last.Id == id
            && index >= 0 && index < last.Symbols.Count) {
            lock (last.Analysis.Gate) {
                if (SymbolDisplay.Documentation(last.Symbols[index]) is { } documentation) {
                    item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
                }
            }
        }

        return Task.FromResult<JsonNode?>(item);
    }

    static void AddMembers(SemanticModel model, MemberAccessExpressionSyntax access, bool optionalChain, List<(Symbol, int)> items) {
        var receiver = access.Expression;
        var bound = model.GetBoundNode(receiver);
        var isType = bound is BoundTypeExpression;
        var type = isType ? ((BoundTypeExpression)bound!).ReferencedType : model.GetDeclaredTypeOf(receiver) ?? model.GetTypeOf(receiver);
        if (type is null || type.IsError) {
            return;
        }

        if (optionalChain && type is OptionalTypeSymbol optional) {
            type = optional.UnderlyingType;
        }

        var rank = 0;
        foreach (var member in model.GetMembersOf(type)) {
            var offered = isType
                ? member is FunctionSymbol { IsStatic: true, FunctionKind: FunctionKind.Ordinary }
                    or FieldSymbol { IsStatic: true }
                    or UnionCaseSymbol or EnumCaseSymbol or NamedTypeSymbol or AliasSymbol
                : member is FunctionSymbol { IsStatic: false, FunctionKind: FunctionKind.Ordinary }
                    or FieldSymbol { IsStatic: false };
            if (offered) {
                items.Add((member, rank++));
            }
        }
    }

    static JsonNode CompletionList(Document document, int start, int offset, string prefix, List<(Symbol Symbol, int Rank)> symbols, bool keywords, int id) {
        var range = RangeNode(document.Lines.RangeOf(new TextSpan(start, offset - start)));
        var items = new JsonArray();
        var incomplete = false;

        for (var index = 0; index < symbols.Count; index++) {
            var (symbol, rank) = symbols[index];
            if (!Matches(prefix, symbol.Name) || !IsIdentifier(symbol.Name)) {
                continue;
            }

            if (items.Count >= MaximumItems) {
                incomplete = true;
                break;
            }

            items.Add(new JsonObject {
                ["label"] = symbol.Name,
                ["kind"] = SymbolDisplay.CompletionKind(symbol),
                ["detail"] = SymbolDisplay.Signature(symbol),
                ["sortText"] = rank.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                ["textEdit"] = new JsonObject { ["range"] = range.DeepClone(), ["newText"] = symbol.Name },
                ["data"] = new JsonObject { ["completion"] = id, ["index"] = index }
            });
        }

        if (keywords) {
            foreach (var keyword in Keywords) {
                if (Matches(prefix, keyword) && items.Count < MaximumItems) {
                    items.Add(new JsonObject {
                        ["label"] = keyword,
                        ["kind"] = 14,
                        ["sortText"] = "9" + keyword,
                        ["textEdit"] = new JsonObject { ["range"] = range.DeepClone(), ["newText"] = keyword }
                    });
                }
            }
        }

        return new JsonObject { ["isIncomplete"] = incomplete, ["items"] = items };
    }

    async Task<JsonNode?> ModuleCompletionAsync(WorkspaceSnapshot snapshot, Document document, int offset, string typed) {
        var names = new SortedSet<string>(_options.LibraryModules, StringComparer.Ordinal) { Symbols.CoreLibrary.ModuleName };
        var analysis = _analyses.Latest(snapshot.KeyOf(document)) ?? await _analyses.GetAsync(snapshot, snapshot.KeyOf(document));
        lock (analysis.Gate) {
            foreach (var module in analysis.Compilation.Modules) {
                if (module.Name.Length > 0) {
                    names.Add(module.Name);
                }
            }
        }

        var range = RangeNode(document.Lines.RangeOf(new TextSpan(offset - typed.Length, typed.Length)));
        var items = new JsonArray();
        foreach (var name in names) {
            if (name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) || Matches(typed, name)) {
                items.Add(new JsonObject {
                    ["label"] = name,
                    ["kind"] = 9,
                    ["textEdit"] = new JsonObject { ["range"] = range.DeepClone(), ["newText"] = name }
                });
            }
        }

        return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
    }

    static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(IsNameCharacter);

    /// <summary>
    ///     Whether a name is offered for what has been typed: its letters in
    ///     order, ignoring case, the first one first — <c>lS</c> finds
    ///     <c>lengthSquared</c>.
    /// </summary>
    internal static bool Matches(string typed, string name) {
        if (typed.Length == 0) {
            return true;
        }

        if (name.Length == 0 || char.ToLowerInvariant(typed[0]) != char.ToLowerInvariant(name[0])) {
            return false;
        }

        var at = 1;
        for (var i = 1; i < typed.Length; i++) {
            var wanted = char.ToLowerInvariant(typed[i]);
            while (at < name.Length && char.ToLowerInvariant(name[at]) != wanted) {
                at++;
            }

            if (at == name.Length) {
                return false;
            }

            at++;
        }

        return true;
    }

    // -----------------------------------------------------------------------
    // Signature help
    // -----------------------------------------------------------------------

    async Task<JsonNode?> SignatureHelpAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var token = SemanticModel.FindTokenAt(document.Tree.Root, Math.Max(offset - 1, 0));
        InvocationExpressionSyntax? invocation = null;
        for (var node = token.Parent; node is not null; node = node.Parent) {
            if (node is InvocationExpressionSyntax { ArgumentList: { } arguments } candidate
                && arguments.OpenToken.Span.End <= offset
                && (arguments.CloseToken.IsMissing || offset <= arguments.CloseToken.Span.Start)) {
                invocation = candidate;
                break;
            }
        }

        if (invocation is null) {
            return null;
        }

        var context = await EditContextAsync(snapshot, document, offset);
        cancellation.ThrowIfCancellationRequested();

        lock (context.Analysis.Gate) {
            var model = context.Model;
            var candidates = new List<FunctionSymbol>();
            FunctionSymbol? chosen = null;
            switch (model.GetBoundNode(invocation)) {
                case BoundCall call:
                    chosen = call.Function;
                    break;
                case BoundConversion { Operand: BoundCall call }:
                    chosen = call.Function;
                    break;
            }

            switch (model.GetBoundNode(invocation.Expression)) {
                case BoundFunctionGroup group:
                    candidates.AddRange(group.Candidates);
                    break;
                case BoundTypeExpression { ReferencedType: var type }:
                    candidates.AddRange(model.GetMembersOf(type).OfType<FunctionSymbol>().Where(static f => f.FunctionKind == FunctionKind.Initializer));
                    break;
            }

            if (chosen is not null && !candidates.Contains(chosen)) {
                candidates.Insert(0, chosen);
            }

            if (candidates.Count == 0 && model.GetSymbolOf(invocation.Expression) is FunctionSymbol named) {
                candidates.Add(named);
            }

            if (candidates.Count == 0) {
                return null;
            }

            // The argument being typed: one for each that ends before the
            // caret, since a comma follows each of those.
            var active = 0;
            foreach (var argument in invocation.ArgumentList!.Arguments) {
                if (argument.Span.Length > 0 && argument.Span.End < offset) {
                    active++;
                }
            }

            var signatures = new JsonArray();
            foreach (var function in candidates) {
                signatures.Add(SignatureNode(function));
            }

            var index = chosen is null ? 0 : candidates.IndexOf(chosen);
            return new JsonObject {
                ["signatures"] = signatures,
                ["activeSignature"] = Math.Max(index, 0),
                ["activeParameter"] = active
            };
        }
    }

    static JsonNode SignatureNode(FunctionSymbol function) {
        var label = new System.Text.StringBuilder();
        label.Append(function.FunctionKind == FunctionKind.Initializer ? "init" : function.Name).Append('(');
        var parameters = new JsonArray();
        for (var i = 0; i < function.Parameters.Length; i++) {
            if (i > 0) {
                label.Append(", ");
            }

            var start = label.Length;
            label.Append(SymbolDisplay.Parameter(function.Parameters[i]));
            parameters.Add(new JsonObject { ["label"] = new JsonArray(start, label.Length) });
        }

        label.Append(')');
        if (function.FunctionKind != FunctionKind.Initializer && function.ReturnType.ToDisplayString() is var result && result != "Void" && !function.ReturnType.IsError) {
            label.Append(" -> ").Append(result);
        }

        var node = new JsonObject { ["label"] = label.ToString(), ["parameters"] = parameters };
        if (SymbolDisplay.Documentation(function) is { } documentation) {
            node["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
        }

        return node;
    }
}
