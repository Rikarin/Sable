using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

public sealed partial class LanguageServer {
    /// <summary>The analysis of a document's package in exactly a snapshot: what a request about declarations and references needs.</summary>
    async Task<Analysis> AnalysisOfAsync(WorkspaceSnapshot snapshot, Document document) {
        snapshot = await LoadedAsync(snapshot);
        return await _analyses.GetAsync(snapshot, snapshot.KeyOf(snapshot.Find(document.Uri) ?? document));
    }

    /// <summary>
    ///     The snapshot once the workspace's files are loaded: a request that
    ///     arrived while they were being read is about the documents it saw,
    ///     which the newer snapshot has unchanged, along with the rest.
    /// </summary>
    async Task<WorkspaceSnapshot> LoadedAsync(WorkspaceSnapshot snapshot) {
        await _loading;
        var current = _workspace.Current;
        return current.Generation > snapshot.Generation && snapshot.Documents.Values.All(d => ReferenceEquals(current.Find(d.Uri)?.Tree, d.Tree))
            ? current
            : snapshot;
    }

    // -----------------------------------------------------------------------
    // Hover
    // -----------------------------------------------------------------------

    /// <summary>
    ///     <c>textDocument/hover</c>. Like a completion, it does not wait for
    ///     a compilation of the text as it is now: the member under the
    ///     cursor is bound as it is, against the newest compilation there is.
    ///     Only what that cannot answer — a name declared since, say — waits
    ///     for this snapshot's compilation.
    /// </summary>
    async Task<JsonNode?> HoverAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var context = await EditContextAsync(snapshot, document, offset);
        cancellation.ThrowIfCancellationRequested();
        var quick = Hover(context.Analysis, context.Speculative ? context.Model : null, document, offset);
        if (quick is not null || !context.Speculative) {
            return quick;
        }

        var analysis = await AnalysisOfAsync(snapshot, document);
        cancellation.ThrowIfCancellationRequested();
        return Hover(analysis, null, document, offset);
    }

    /// <summary>
    ///     What to show over a position: the symbol a name there names, or
    ///     an expression's type: by the speculative model of the member
    ///     there, if there is one, else by the analysis's own model.
    /// </summary>
    static JsonNode? Hover(Analysis analysis, SemanticModel? speculative, Document document, int offset) {
        lock (analysis.Gate) {
            var model = speculative ?? analysis.Model(document.Tree);
            var token = SemanticModel.FindToken(document.Tree.Root, offset);
            if (token.IsNone) {
                return null;
            }

            var markdown = new StringBuilder();
            if (model.GetSymbolOfToken(token) is { } symbol) {
                markdown.Append("```sable\n").Append(SymbolDisplay.Signature(symbol)).Append("\n```");
                if (Container(symbol) is { } container) {
                    markdown.Append("\n\nin `").Append(container).Append('`');
                }

                if (SymbolDisplay.Documentation(symbol) is { } documentation) {
                    markdown.Append("\n\n---\n\n").Append(documentation);
                }
            } else if (ExpressionAt(token) is { } expression
                       && (speculative is not null ? model : SemanticModel.FindMember(document.Tree.Root, offset) is { } member ? model.GetMemberModel(member) : null) is { } memberModel
                       && memberModel.GetTypeOf(expression) is { IsError: false } type) {
                markdown.Append("```sable\n").Append(SymbolDisplay.Type(type)).Append("\n```");
            } else {
                return null;
            }

            return new JsonObject {
                ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown.ToString() },
                ["range"] = RangeNode(document.Lines.RangeOf(token.Span))
            };
        }
    }

    static string? Container(Symbol symbol) =>
        symbol switch {
            LocalSymbol or ParameterSymbol or ModuleSymbol => null,
            _ => symbol.ContainingSymbol switch {
                ModuleSymbol { Name.Length: > 0 } module => module.Name,
                NamedTypeSymbol type => type.QualifiedName,

                // An array extension's member: the array, which has no name of its own.
                ExtensionSymbol { ExtendedType: { ExtendedArray: { } array } extended } =>
                    $"{extended.ContainingSymbol?.Name}.{array.ToDisplayString()}",
                ExtensionSymbol extension => extension.ExtendedType.QualifiedName,
                _ => null,
            },
        };

    static ExpressionSyntax? ExpressionAt(SyntaxToken token) {
        for (var node = token.Parent; node is not null; node = node.Parent) {
            if (node is ExpressionSyntax expression) {
                return expression;
            }

            if (node is StatementSyntax) {
                return null;
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Definition
    // -----------------------------------------------------------------------

    async Task<JsonNode?> DefinitionAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var analysis = await AnalysisOfAsync(snapshot, document);
        document = analysis.Snapshot.Find(document.Uri) ?? document;
        cancellation.ThrowIfCancellationRequested();

        lock (analysis.Gate) {
            var symbol = analysis.Model(document.Tree).GetSymbolAt(offset);
            if (symbol is null || symbol.DeclaringSyntax.IsEmpty) {
                return null;
            }

            return LocationOf(analysis, symbol);
        }
    }

    /// <summary>Where a symbol is declared: its name in its declaration, in a workspace file or a library one.</summary>
    JsonNode? LocationOf(Analysis analysis, Symbol symbol) {
        if (DeclarationOf(analysis, symbol) is not var (uri, lines, declaration)) {
            return null;
        }

        var name = SemanticModel.NameTokenOf(symbol, declaration);
        var span = name.IsNone ? declaration.Span : name.Span;
        return new JsonObject { ["uri"] = uri, ["range"] = RangeNode(lines.RangeOf(span)) };
    }

    /// <summary>
    ///     A symbol's declaration, and the file the editor can open it in: a
    ///     document, a dependency's file on disk, or a copy of the library's.
    /// </summary>
    (string Uri, LineIndex Lines, SyntaxNode Declaration)? DeclarationOf(Analysis analysis, Symbol symbol) {
        if (symbol.DeclaringSyntax.IsEmpty) {
            return null;
        }

        var declaration = symbol.DeclaringSyntax[0];
        if (analysis.DocumentOf(declaration) is { } owner) {
            return (owner.Uri, owner.Lines, declaration);
        }

        if (analysis.TreeOf(declaration) is not { } tree) {
            return null;
        }

        // A dependency's file is on disk, where the editor can open it.
        if (analysis.IsDependencyFile(tree)) {
            return (Workspace.UriOf(tree.Text.FileName), LineIndex.Of(tree.Text), declaration);
        }

        // A library file lives in the compiler; the editor is shown a copy.
        var path = Path.Combine(_options.LibraryCacheDirectory, tree.Text.FileName);
        try {
            if (!File.Exists(path) || File.ReadAllText(path) != tree.Text.Text) {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, tree.Text.Text);
            }
        } catch (IOException e) {
            Log($"cannot write {path}: {e.Message}");
            return null;
        }

        return (Workspace.UriOf(path), LineIndex.Of(tree.Text), declaration);
    }

    // -----------------------------------------------------------------------
    // References and rename
    // -----------------------------------------------------------------------

    async Task<JsonNode?> ReferencesAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var analysis = await AnalysisOfAsync(snapshot, document);
        document = analysis.Snapshot.Find(document.Uri) ?? document;
        var includeDeclaration = parameters.Get("context")?.GetBool("includeDeclaration") ?? true;

        Symbol? symbol;
        lock (analysis.Gate) {
            symbol = analysis.Model(document.Tree).GetSymbolAt(offset);
        }

        if (symbol is null) {
            return null;
        }

        var locations = new JsonArray();
        foreach (var (owner, span, isDeclaration) in References(analysis, symbol, document, cancellation)) {
            if (includeDeclaration || !isDeclaration) {
                locations.Add(new JsonObject { ["uri"] = owner.Uri, ["range"] = RangeNode(owner.Lines.RangeOf(span)) });
            }
        }

        return locations;
    }

    /// <summary>
    ///     Every reference to a symbol in the workspace's documents, its
    ///     declaration included. A local can only be named in its own file,
    ///     and so can a parameter — but one whose label is its name is also
    ///     named by the label of each argument for it, in any file. Anything
    ///     else is looked for in each file whose text mentions it, binding
    ///     only the members that do.
    /// </summary>
    static List<(Document Document, TextSpan Span, bool IsDeclaration)> References(Analysis analysis, Symbol symbol, Document origin, CancellationToken cancellation) {
        var labelled = symbol is ParameterSymbol { Label: null, ContainingSymbol: FunctionSymbol { FunctionKind: not FunctionKind.Accessor } } parameter
            ? parameter
            : null;
        IReadOnlyList<Document> documents = symbol is LocalSymbol || symbol is ParameterSymbol && labelled is null
            ? [origin]
            : [..analysis.Documents];

        var declaration = !symbol.DeclaringSyntax.IsEmpty ? SemanticModel.NameTokenOf(symbol, symbol.DeclaringSyntax[0]) : default;
        var declaredIn = !symbol.DeclaringSyntax.IsEmpty ? analysis.DocumentOf(symbol.DeclaringSyntax[0]) : null;

        var found = new List<(Document, TextSpan, bool)>();
        foreach (var document in documents) {
            cancellation.ThrowIfCancellationRequested();
            if (!document.Tree.Text.Text.Contains(symbol.Name, StringComparison.Ordinal)) {
                continue;
            }

            IReadOnlyList<TextSpan> spans;
            lock (analysis.Gate) {
                spans = analysis.Model(document.Tree).FindReferences(symbol);
            }

            foreach (var span in spans) {
                var isDeclaration = ReferenceEquals(declaredIn, document) && !declaration.IsNone && declaration.Span == span;
                found.Add((document, span, isDeclaration));
            }

            if (labelled is not null) {
                foreach (var span in ArgumentLabels(analysis, document, labelled, cancellation)) {
                    found.Add((document, span, false));
                }
            }
        }

        return found;
    }

    /// <summary>The labels of the arguments for a parameter in calls to its function: <c>x</c> in <c>f(x: 1)</c>.</summary>
    static IEnumerable<TextSpan> ArgumentLabels(Analysis analysis, Document document, ParameterSymbol parameter, CancellationToken cancellation) {
        var function = (FunctionSymbol)parameter.ContainingSymbol!;
        var found = new List<TextSpan>();
        foreach (var argument in document.Tree.Root.DescendantNodes().OfType<ArgumentSyntax>()) {
            cancellation.ThrowIfCancellationRequested();
            if (argument.NameToken is not { IsNone: false } label
                || label.Text != parameter.Name
                || argument.Parent?.Parent is not InvocationExpressionSyntax invocation
                || SemanticModel.FindMember(document.Tree.Root, invocation.Span.Start) is not { } member) {
                continue;
            }

            lock (analysis.Gate) {
                var bound = analysis.Model(document.Tree).GetMemberModel(member).GetBoundNode(invocation);
                while (bound is BoundConversion conversion) {
                    bound = conversion.Operand;
                }

                if (bound is BoundCall call && SemanticModel.SameSymbol(call.Function, function)) {
                    found.Add(label.Span);
                }
            }
        }

        return found;
    }

    async Task<JsonNode?> PrepareRenameAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var analysis = await AnalysisOfAsync(snapshot, document);
        lock (analysis.Gate) {
            var token = SemanticModel.FindToken(document.Tree.Root, offset);
            var symbol = token.IsNone ? null : analysis.Model(document.Tree).GetSymbolOfToken(token);
            CheckRenamable(analysis, symbol);
            return new JsonObject {
                ["range"] = RangeNode(document.Lines.RangeOf(token.Span)),
                ["placeholder"] = token.Text
            };
        }
    }

    /// <summary>
    ///     Refuses what cannot be renamed: nothing, what the compiler made up,
    ///     and anything the library declares — renaming <c>print</c> would
    ///     rename it for every program.
    /// </summary>
    static void CheckRenamable(Analysis analysis, Symbol? symbol) {
        if (symbol is null) {
            throw new LspException(LspException.RequestFailed, "there is nothing here to rename");
        }

        if (symbol.DeclaringSyntax.IsEmpty || analysis.TreeOf(symbol.DeclaringSyntax[0]) is not { } tree) {
            throw new LspException(LspException.RequestFailed, $"'{symbol.Name}' is built into the compiler and cannot be renamed");
        }

        if (analysis.IsLibrary(tree) || analysis.DocumentOf(symbol.DeclaringSyntax[0]) is null) {
            throw new LspException(LspException.RequestFailed, $"'{symbol.Name}' is declared in the standard library and cannot be renamed");
        }

        if (SemanticModel.NameTokenOf(symbol, symbol.DeclaringSyntax[0]).IsNone) {
            throw new LspException(LspException.RequestFailed, $"'{symbol.Name}' cannot be renamed");
        }
    }

    async Task<JsonNode?> RenameAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var newName = parameters.GetString("newName") ?? "";
        if (!IsIdentifier(newName) || SyntaxFacts.KeywordKind(newName) != SyntaxKind.IdentifierToken) {
            throw new LspException(LspException.InvalidParams, $"'{newName}' is not a name");
        }

        var analysis = await AnalysisOfAsync(snapshot, document);
        Symbol? symbol;
        lock (analysis.Gate) {
            symbol = analysis.Model(document.Tree).GetSymbolAt(offset);
            CheckRenamable(analysis, symbol);
        }

        var changes = new JsonObject();
        foreach (var group in References(analysis, symbol!, document, cancellation).GroupBy(static r => r.Document.Uri)) {
            var edits = new JsonArray();
            foreach (var (owner, span, _) in group) {
                edits.Add(new JsonObject { ["range"] = RangeNode(owner.Lines.RangeOf(span)), ["newText"] = newName });
            }

            changes[group.Key] = edits;
        }

        return new JsonObject { ["changes"] = changes };
    }

    // -----------------------------------------------------------------------
    // Document symbols
    // -----------------------------------------------------------------------

    /// <summary>The file's declarations as an outline: syntax alone, so it needs no compilation.</summary>
    Task<JsonNode?> DocumentSymbolsAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var uri = parameters.Get("textDocument")?.GetString("uri") ?? "";
        var document = snapshot.Find(uri) ?? throw new LspException(LspException.InvalidParams, $"'{uri}' is not open");
        var root = document.Tree.Root is CompilationUnitSyntax unit ? unit.Members : default;
        return Task.FromResult<JsonNode?>(Outline(document, root));
    }

    static JsonArray Outline(Document document, IEnumerable<SyntaxNode> members) {
        var symbols = new JsonArray();
        foreach (var member in members) {
            var (name, children) = OutlineOf(member);
            if (name.Length == 0) {
                continue;
            }

            var selection = member.DescendantTokens().FirstOrDefault(t => t.Text == name || name.EndsWith(t.Text, StringComparison.Ordinal) && t.Text.Length > 0);
            var node = new JsonObject {
                ["name"] = name,
                ["detail"] = Detail(member),
                ["kind"] = SymbolDisplay.SymbolKindOf(member),
                ["range"] = RangeNode(document.Lines.RangeOf(member.Span)),
                ["selectionRange"] = RangeNode(document.Lines.RangeOf(selection.IsNone ? member.Span : selection.Span))
            };

            var nested = Outline(document, children);
            if (nested.Count > 0) {
                node["children"] = nested;
            }

            symbols.Add(node);
        }

        return symbols;
    }

    /// <summary>What a declaration is called in an outline, and the declarations inside it; no name for what is not one.</summary>
    static (string Name, IEnumerable<SyntaxNode> Children) OutlineOf(SyntaxNode member) =>
        member switch {
            StructDeclarationSyntax type => (type.Identifier.Text, (IEnumerable<SyntaxNode>)type.Members),
            ClassDeclarationSyntax type => (type.Identifier.Text, type.Members),
            ActorDeclarationSyntax type => (type.Identifier.Text, type.Members),
            ProtocolDeclarationSyntax type => (type.Identifier.Text, type.Members),
            UnionDeclarationSyntax type => (type.Identifier.Text, type.Members),
            EnumDeclarationSyntax type => (type.Identifier.Text, type.Members),
            ExtensionDeclarationSyntax extension => ("extension " + extension.Type.ToString().Trim(), extension.Members),
            FunctionDeclarationSyntax function => (function.Identifier.Text, []),
            InitDeclarationSyntax => ("init", []),
            DeinitDeclarationSyntax => ("deinit", []),
            SubscriptDeclarationSyntax => ("subscript", []),
            OperatorDeclarationSyntax op => ("operator " + op.OperatorToken.Text, []),
            FieldDeclarationSyntax field => (NameOf(field.Declarator), []),
            ConstDeclarationSyntax constant => (constant.Identifier.Text, []),
            AliasDeclarationSyntax alias => (alias.Identifier.Text, []),
            UnionCaseDeclarationSyntax unionCase => (unionCase.Identifier.Text, []),
            EnumCaseDeclarationSyntax enumCase => (enumCase.Identifier.Text, []),
            _ => ("", Array.Empty<SyntaxNode>()),
        };

    static string Detail(SyntaxNode member) =>
        member switch {
            FunctionDeclarationSyntax function => function.Parameters.ToString().Trim() + (function.ReturnClause is { } result ? " " + result.ToString().Trim() : ""),
            InitDeclarationSyntax init => init.Parameters.ToString().Trim(),
            FieldDeclarationSyntax field => field.Declarator.TypeAnnotation?.Type.ToString().Trim() ?? "",
            _ => "",
        };
}
