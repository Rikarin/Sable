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

        var items = new List<Offer>();
        var labels = new List<string>();
        var root = document.Tree.Root;
        var keywords = true;
        lock (context.Analysis.Gate) {
            if (start > 0 && SemanticModel.FindTokenAt(root, start - 1) is { Kind: SyntaxKind.DotToken or SyntaxKind.QuestionDotToken } dot) {
                keywords = false;
                switch (dot.Parent) {
                    case MemberAccessExpressionSyntax access:
                        AddMembers(context.Model, access, dot.Kind == SyntaxKind.QuestionDotToken, items);
                        break;

                    // '.Red' where a Color is wanted: the type's cases and statics.
                    case ImplicitMemberAccessExpressionSyntax implicitMember when ExpectedTypeOf(context.Model, implicitMember) is { } expected:
                        AddStatics(context.Model, expected, items);
                        break;
                }
            } else {
                labels.AddRange(ArgumentLabels(context.Model, root, start, offset));
                var rank = 0;
                var visible = new HashSet<string>(StringComparer.Ordinal);
                foreach (var symbol in context.Model.LookupSymbols(offset)) {
                    items.Add(new(symbol, rank++));
                    visible.Add(symbol.Name);
                }

                AddImportable(context.Analysis, _library.Ready, document, prefix, visible, rank, items);
            }

            var id = Interlocked.Increment(ref _completions);
            Volatile.Write(ref _lastCompletion, new CompletionMemory(id, context.Analysis, [..items.Select(static i => i.Symbol)]));
            var snippets = _snippets && !(offset < text.Length && text[offset] == '(');
            return CompletionList(document, start, offset, prefix, items, labels, keywords, snippets, id);
        }
    }

    /// <summary>
    ///     A symbol offered, ranked; <see cref="Import" /> names the module
    ///     whose import the item adds, for a symbol not yet in scope.
    /// </summary>
    internal sealed record Offer(Symbol Symbol, int Rank, string? Import = null);

    /// <summary>
    ///     The type a context wants of <c>.Name</c>: what a parameter, an
    ///     annotated declaration, an assignment, a comparison or a
    ///     <c>return</c> expects there. The binder knows it only while it
    ///     binds, so it is worked out again from the syntax around.
    /// </summary>
    static TypeSymbol? ExpectedTypeOf(SemanticModel model, ImplicitMemberAccessExpressionSyntax implicitMember) {
        SyntaxNode node = implicitMember;
        // Through parentheses, and a call of the member: '.circle(r)' is a
        // value of the type wanted too, and a dot typed at the end of a line
        // is read as calling what the next line starts with.
        while (node.Parent is ParenthesizedExpressionSyntax
               || node.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Expression, node)) {
            node = node.Parent;
        }

        var type = node.Parent switch {
            ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax invocation } argument => ParameterTypeAt(model, invocation, argument),
            EqualsValueClauseSyntax { Parent: { } declarator } => DeclaredTypeOf(model, declarator),
            AssignmentExpressionSyntax assignment when ReferenceEquals(assignment.Right, node) => model.GetTypeOf(assignment.Left),
            BinaryExpressionSyntax binary => model.GetTypeOf(ReferenceEquals(binary.Right, node) ? binary.Left : binary.Right),
            ReturnStatementSyntax ret => ReturnTypeAt(model, ret),
            _ => null,
        };

        type = type is OptionalTypeSymbol optional ? optional.UnderlyingType : type;
        return type is NamedTypeSymbol { IsError: false } ? type : null;
    }

    /// <summary>The type of the parameter an argument is for: by its label, or by its place among those without one.</summary>
    static TypeSymbol? ParameterTypeAt(SemanticModel model, InvocationExpressionSyntax invocation, ArgumentSyntax argument) {
        var arguments = invocation.ArgumentList!.Arguments.ToList();
        var index = arguments.IndexOf(argument);
        foreach (var function in CallCandidates(model, invocation).Candidates) {
            var parameter = argument.NameToken is { IsNone: false } label && label.Text.Length > 0
                ? function.Parameters.FirstOrDefault(p => LabelOf(p) == label.Text)
                : index >= 0 && index < function.Parameters.Length ? function.Parameters[index] : null;
            if (parameter is not null && !parameter.Type.IsError) {
                return parameter.Type;
            }
        }

        return null;
    }

    static TypeSymbol? DeclaredTypeOf(SemanticModel model, SyntaxNode declarator) {
        for (var node = declarator; node is not null; node = node.Parent) {
            switch (model.GetBoundNode(node)) {
                case BoundLocalDeclaration declaration:
                    return declaration.Local.Type;
            }

            if (node is StatementSyntax or DeclarationSyntax) {
                break;
            }
        }

        return null;
    }

    static TypeSymbol? ReturnTypeAt(SemanticModel model, SyntaxNode node) {
        for (var at = node.Parent; at is not null; at = at.Parent) {
            if (at is ClosureExpressionSyntax or BraceClosureExpressionSyntax) {
                return null;
            }

            if (at is FunctionDeclarationSyntax function) {
                return (model.GetSymbolOf(function) as FunctionSymbol)?.ReturnType;
            }
        }

        return null;
    }

    /// <summary>What a call's arguments write for a parameter: its label, or its name when it has none; null for one written without.</summary>
    static string? LabelOf(ParameterSymbol parameter) =>
        parameter.Label switch {
            "_" => null,
            { } label => label,
            null => parameter.Name,
        };

    /// <summary>The functions a call may be to: the one it binds to first, then the rest of its group or the type's initialisers.</summary>
    static (List<FunctionSymbol> Candidates, FunctionSymbol? Chosen) CallCandidates(SemanticModel model, InvocationExpressionSyntax invocation) {
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

        return (candidates, chosen);
    }

    /// <summary>
    ///     At the start of an argument, the labels the call's parameters take
    ///     that no other argument has written yet: <c>to:</c> in
    ///     <c>add(1, t|)</c>.
    /// </summary>
    static IEnumerable<string> ArgumentLabels(SemanticModel model, SyntaxNode root, int start, int offset) {
        var before = start > 0 ? SemanticModel.FindTokenAt(root, start - 1) : default;
        if (before.Kind is not (SyntaxKind.OpenParenToken or SyntaxKind.CommaToken) || before.Parent is not ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } list) {
            return [];
        }

        var written = list.Arguments
            .Where(a => a.NameToken is { IsNone: false } name && name.Text.Length > 0 && !(a.Span.Start <= offset && offset <= a.Span.End))
            .Select(static a => a.NameToken.Text)
            .ToHashSet(StringComparer.Ordinal);
        var labels = new List<string>();
        foreach (var function in CallCandidates(model, invocation).Candidates) {
            foreach (var parameter in function.Parameters) {
                if (LabelOf(parameter) is { } label && !written.Contains(label) && !labels.Contains(label)) {
                    labels.Add(label);
                }
            }
        }

        return labels;
    }

    /// <summary>
    ///     The public types and functions of the compilation's other modules
    ///     that the file does not import, for a name of two letters or more:
    ///     choosing one adds the import. So are those of the library modules
    ///     the compilation does not have, since nothing imports them yet,
    ///     once the <see cref="LibraryIndex" /> is built.
    /// </summary>
    static void AddImportable(Analysis analysis, IReadOnlyList<LibraryIndex.Entry>? library, Document document, string prefix, HashSet<string> visible, int rank, List<Offer> items) {
        if (prefix.Length < 2 || document.Tree.Root is not CompilationUnitSyntax unit) {
            return;
        }

        var own = unit.Module?.Name.ToString().Trim();
        var imported = unit.Members.OfType<ImportDeclarationSyntax>().Select(static i => i.Name.ToString().Trim()).ToHashSet(StringComparer.Ordinal);
        foreach (var module in analysis.Compilation.Modules.OrderBy(static m => m.Name, StringComparer.Ordinal)) {
            if (module.Name.Length == 0 || module.Name == own || module.Name == CoreLibrary.ModuleName || imported.Contains(module.Name)) {
                continue;
            }

            foreach (var member in module.Members) {
                if (member is NamedTypeSymbol or FunctionSymbol { FunctionKind: FunctionKind.Ordinary } or AliasSymbol
                    && member.DeclaredAccessibility == Accessibility.Public
                    && !visible.Contains(member.Name)
                    && Matches(prefix, member.Name)) {
                    items.Add(new(member, rank++, module.Name));
                }
            }
        }

        var compiled = analysis.Compilation.Modules.Select(static m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (module, member) in library ?? []) {
            if (!compiled.Contains(module) && module != own && !imported.Contains(module)
                && !visible.Contains(member.Name) && Matches(prefix, member.Name)) {
                items.Add(new(member, rank++, module));
            }
        }
    }

    /// <summary>The line a new import goes on: after the last import, or else after the module declaration.</summary>
    static int ImportLineOf(Document document) {
        if (document.Tree.Root is not CompilationUnitSyntax unit) {
            return 0;
        }

        var last = unit.Members.OfType<ImportDeclarationSyntax>().LastOrDefault() as SyntaxNode ?? unit.Module;
        return last is null ? 0 : document.Lines.PositionOf(last.Span.End).Line + 1;
    }

    /// <summary>A call written out as a snippet: each argument a placeholder, under its label if it has one.</summary>
    static string CallSnippet(FunctionSymbol function) {
        var arguments = function.Parameters.Select(static (p, i) =>
            (LabelOf(p) is { } label ? label + ": " : "") + "${" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + p.Name + "}");
        return $"{function.Name}({string.Join(", ", arguments)})$0";
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

    static void AddMembers(SemanticModel model, MemberAccessExpressionSyntax access, bool optionalChain, List<Offer> items) {
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

        if (isType) {
            AddStatics(model, type, items);
            return;
        }

        var rank = 0;
        foreach (var member in model.GetMembersOf(type)) {
            if (member is FunctionSymbol { IsStatic: false, FunctionKind: FunctionKind.Ordinary } or FieldSymbol { IsStatic: false }) {
                items.Add(new(member, rank++));
            }
        }
    }

    /// <summary>What a type offers after its name and a dot: its cases, statics and nested types.</summary>
    static void AddStatics(SemanticModel model, TypeSymbol type, List<Offer> items) {
        var rank = 0;
        foreach (var member in model.GetMembersOf(type)) {
            if (member is FunctionSymbol { IsStatic: true, FunctionKind: FunctionKind.Ordinary }
                or FieldSymbol { IsStatic: true }
                or UnionCaseSymbol or EnumCaseSymbol or NamedTypeSymbol or AliasSymbol) {
                items.Add(new(member, rank++));
            }
        }
    }

    static JsonNode CompletionList(
        Document document, int start, int offset, string prefix, List<Offer> symbols, List<string> labels, bool keywords, bool snippets, int id) {
        var range = RangeNode(document.Lines.RangeOf(new TextSpan(start, offset - start)));
        var items = new JsonArray();
        var incomplete = false;

        // An argument's label first: at the start of an argument, it is
        // what comes next.
        foreach (var label in labels) {
            if (Matches(prefix, label)) {
                items.Add(new JsonObject {
                    ["label"] = label + ":",
                    ["kind"] = 5,
                    ["detail"] = "argument label",
                    ["sortText"] = "!" + label,
                    ["textEdit"] = new JsonObject { ["range"] = range.DeepClone(), ["newText"] = label + ": " }
                });
            }
        }

        for (var index = 0; index < symbols.Count; index++) {
            var (symbol, rank, import) = symbols[index];
            if (!Matches(prefix, symbol.Name) || !IsIdentifier(symbol.Name)) {
                continue;
            }

            if (items.Count >= MaximumItems) {
                incomplete = true;
                break;
            }

            var item = new JsonObject {
                ["label"] = symbol.Name,
                ["kind"] = SymbolDisplay.CompletionKind(symbol),
                ["detail"] = SymbolDisplay.Signature(symbol),
                ["sortText"] = rank.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                ["textEdit"] = new JsonObject { ["range"] = range.DeepClone(), ["newText"] = symbol.Name },
                ["data"] = new JsonObject { ["completion"] = id, ["index"] = index }
            };

            // A function, written as a call with a placeholder per argument.
            if (snippets && symbol is FunctionSymbol { FunctionKind: FunctionKind.Ordinary } function) {
                item["textEdit"]!["newText"] = CallSnippet(function);
                item["insertTextFormat"] = 2;
            }

            // From a module the file does not import: the import comes with it.
            if (import is not null) {
                var line = ImportLineOf(document);
                item["labelDetails"] = new JsonObject { ["description"] = import };
                item["detail"] = $"import {import}\n{SymbolDisplay.Signature(symbol)}";
                item["additionalTextEdits"] = new JsonArray(new JsonObject {
                    ["range"] = RangeNode(new LspRange(new(line, 0), new(line, 0))),
                    ["newText"] = $"import {import}\n"
                });
            }

            items.Add(item);
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
            var (candidates, chosen) = CallCandidates(context.Model, invocation);
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
