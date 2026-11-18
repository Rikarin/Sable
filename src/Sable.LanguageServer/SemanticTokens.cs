using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     <c>textDocument/semanticTokens</c>: each name classified by what it
///     names — a type, a parameter, a field, a method — which the grammar
///     alone cannot tell.
/// </summary>
public sealed partial class LanguageServer {
    /// <summary>The token types, by index, as the legend tells the client.</summary>
    static readonly string[] TokenTypes = [
        "namespace", "type", "struct", "class", "enum", "interface", "typeParameter",
        "parameter", "variable", "property", "enumMember", "function", "method",
    ];

    /// <summary>The token modifiers, by bit, as the legend tells the client.</summary>
    static readonly string[] TokenModifiers = ["declaration", "readonly", "static", "defaultLibrary"];

    const int Declaration = 1, Readonly = 2, Static = 4, DefaultLibrary = 8;

    static JsonObject SemanticTokensLegend() =>
        new() {
            ["tokenTypes"] = new JsonArray([..TokenTypes.Select(static t => (JsonNode)t)]),
            ["tokenModifiers"] = new JsonArray([..TokenModifiers.Select(static t => (JsonNode)t)])
        };

    Task<JsonNode?> SemanticTokensFullAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) =>
        SemanticTokensAsync(snapshot, parameters, null, cancellation);

    Task<JsonNode?> SemanticTokensRangeAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) =>
        SemanticTokensAsync(snapshot, parameters, parameters.Get("range") is { } range ? ReadRange(range) : null, cancellation);

    async Task<JsonNode?> SemanticTokensAsync(WorkspaceSnapshot snapshot, JsonElement parameters, LspRange? range, CancellationToken cancellation) {
        var document = DocumentOf(snapshot, parameters);
        var analysis = await AnalysisOfAsync(snapshot, document);
        var start = range is { } r ? document.Lines.OffsetOf(r.Start) : 0;
        var end = range is { } q ? document.Lines.OffsetOf(q.End) : document.Tree.Text.Length;

        var data = new JsonArray();
        var (line, character) = (0, 0);
        SemanticModel model;
        lock (analysis.Gate) {
            model = analysis.Model(document.Tree);
        }

        foreach (var token in document.Tree.Root.DescendantTokens()) {
            if (token.Span.End < start || token.Span.Start > end || token.Kind != SyntaxKind.IdentifierToken || token.Span.Length == 0) {
                continue;
            }

            cancellation.ThrowIfCancellationRequested();
            (int Type, int Modifiers)? classified;
            lock (analysis.Gate) {
                classified = model.GetSymbolOfToken(token) is { } symbol ? Classify(analysis, symbol, token) : null;
            }

            if (classified is not var (type, modifiers)) {
                continue;
            }

            // Each token is placed relative to the one before it.
            var position = document.Lines.PositionOf(token.Span.Start);
            var deltaLine = position.Line - line;
            var deltaCharacter = deltaLine == 0 ? position.Character - character : position.Character;
            data.Add(deltaLine);
            data.Add(deltaCharacter);
            data.Add(token.Span.Length);
            data.Add(type);
            data.Add(modifiers);
            (line, character) = (position.Line, position.Character);
        }

        return new JsonObject { ["data"] = data };
    }

    /// <summary>A symbol's token type and modifiers, where the token is a use of it or its declaration.</summary>
    static (int Type, int Modifiers)? Classify(Analysis analysis, Symbol symbol, SyntaxToken token) {
        int? type = symbol switch {
            ModuleSymbol => 0,
            NamedTypeSymbol { TypeKind: TypeKind.Protocol } => 5,
            NamedTypeSymbol { TypeKind: TypeKind.Struct } => 2,
            NamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Actor } => 3,
            NamedTypeSymbol { TypeKind: TypeKind.Enum or TypeKind.Union } => 4,
            NamedTypeSymbol or AliasSymbol => 1,
            TypeParameterSymbol => 6,
            ParameterSymbol => 7,
            LocalSymbol => 8,
            FieldSymbol => 9,
            UnionCaseSymbol or EnumCaseSymbol => 10,
            FunctionSymbol { ContainingSymbol: NamedTypeSymbol or ExtensionSymbol } => 12,
            FunctionSymbol => 11,
            _ => null,
        };

        if (type is null) {
            return null;
        }

        var modifiers = symbol switch {
            LocalSymbol { IsMutable: false } => Readonly,
            FieldSymbol field => (field.IsMutable ? 0 : Readonly) | (field.IsStatic ? Static : 0),
            FunctionSymbol { IsStatic: true } => Static,
            _ => 0,
        };

        if (symbol.DeclaringSyntax.IsEmpty) {
            modifiers |= DefaultLibrary;
        } else {
            var declaration = symbol.DeclaringSyntax[0];
            if (analysis.TreeOf(declaration) is { } tree && analysis.IsLibrary(tree)) {
                modifiers |= DefaultLibrary;
            }

            if (SemanticModel.NameTokenOf(symbol, declaration) == token) {
                modifiers |= Declaration;
            }
        }

        return (type.Value, modifiers);
    }
}
