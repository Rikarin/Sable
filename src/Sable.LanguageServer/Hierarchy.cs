using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     Navigation through types and calls: a symbol's type, the types that
///     conform to a protocol or inherit from a class and the members that
///     implement a requirement, and the call and type hierarchies.
/// </summary>
public sealed partial class LanguageServer {
    // -----------------------------------------------------------------------
    // Type definition and implementation
    // -----------------------------------------------------------------------

    /// <summary><c>textDocument/typeDefinition</c>: where the type of what is at the position is declared.</summary>
    async Task<JsonNode?> TypeDefinitionAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var analysis = await AnalysisOfAsync(snapshot, document);
        cancellation.ThrowIfCancellationRequested();

        lock (analysis.Gate) {
            var model = analysis.Model(document.Tree);
            var token = SemanticModel.FindToken(document.Tree.Root, offset);
            if (token.IsNone) {
                return null;
            }

            TypeSymbol? type = model.GetSymbolOfToken(token) switch {
                TypeSymbol named => named,
                AliasSymbol => null,
                TypedSymbol typed => typed.Type,
                FunctionSymbol function => function.ReturnType,
                UnionCaseSymbol unionCase => unionCase.ContainingSymbol as TypeSymbol,
                EnumCaseSymbol enumCase => enumCase.ContainingSymbol as TypeSymbol,
                _ => null,
            };

            if (type is null
                && ExpressionAt(token) is { } expression
                && SemanticModel.FindMember(document.Tree.Root, offset) is { } member) {
                type = model.GetMemberModel(member).GetTypeOf(expression);
            }

            return NamedTypeIn(type) is { } named2 ? LocationOf(analysis, named2.OriginalDefinition) : null;
        }
    }

    /// <summary>The declared type a type is about: through optionals, references and arrays.</summary>
    static NamedTypeSymbol? NamedTypeIn(TypeSymbol? type) {
        for (var depth = 0; depth < 16; depth++) {
            switch (type) {
                case NamedTypeSymbol { IsError: false } named when !named.DeclaringSyntax.IsEmpty:
                    return named;
                case OptionalTypeSymbol optional:
                    type = optional.UnderlyingType;
                    continue;
                case ReferenceTypeSymbol reference:
                    type = reference.ReferencedType;
                    continue;
                case ArrayTypeSymbol array:
                    type = array.ElementType;
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    ///     <c>textDocument/implementation</c>: for a protocol or a class, the
    ///     types of the workspace that conform to it or inherit from it,
    ///     however indirectly; for a member of one, those types' members of
    ///     that name.
    /// </summary>
    async Task<JsonNode?> ImplementationAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var analysis = await AnalysisOfAsync(snapshot, document);
        cancellation.ThrowIfCancellationRequested();

        var locations = new JsonArray();
        lock (analysis.Gate) {
            var model = analysis.Model(document.Tree);
            var symbol = model.GetSymbolAt(offset);
            var (type, member) = symbol switch {
                NamedTypeSymbol named => (named.OriginalDefinition, null),
                FunctionSymbol or FieldSymbol when symbol.ContainingSymbol is NamedTypeSymbol owner => (owner.OriginalDefinition, symbol),
                _ => ((NamedTypeSymbol?)null, (Symbol?)null),
            };

            if (type is null) {
                return locations;
            }

            foreach (var subtype in Subtypes(analysis, type, transitively: true)) {
                cancellation.ThrowIfCancellationRequested();
                if (member is null) {
                    if (LocationOf(analysis, subtype) is { } location) {
                        locations.Add(location);
                    }

                    continue;
                }

                foreach (var candidate in model.GetMembersOf(subtype)) {
                    if (candidate.Name == member.Name
                        && candidate.Kind == member.Kind
                        && !SemanticModel.SameSymbol(candidate, member)
                        && !candidate.DeclaringSyntax.IsEmpty
                        && LocationOf(analysis, candidate) is { } location) {
                        locations.Add(location);
                    }
                }
            }
        }

        return locations;
    }

    /// <summary>The types a type conforms to or inherits from directly: its declaration's, its extensions' and <c>@derive</c>'s.</summary>
    static IEnumerable<NamedTypeSymbol> Supertypes(NamedTypeSymbol type) {
        var definition = type.OriginalDefinition;
        var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var all = definition.BaseTypes
            .Concat(definition.Extensions.SelectMany(static e => e.Conformances))
            .Concat(definition.DerivedConformances);
        foreach (var supertype in all) {
            if (supertype is NamedTypeSymbol { IsError: false } named && seen.Add(named.OriginalDefinition)) {
                yield return named.OriginalDefinition;
            }
        }
    }

    /// <summary>
    ///     The types declared in the analysis's documents that conform to or
    ///     inherit from a type — directly, or through one another. Hold the gate.
    /// </summary>
    static List<NamedTypeSymbol> Subtypes(Analysis analysis, NamedTypeSymbol type, bool transitively) {
        var types = DeclaredTypes(analysis);
        var found = new List<NamedTypeSymbol>();
        var wanted = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance) { type.OriginalDefinition };
        for (var grew = true; grew;) {
            grew = false;
            foreach (var candidate in types) {
                if (!wanted.Contains(candidate) && Supertypes(candidate).Any(wanted.Contains)) {
                    found.Add(candidate);
                    grew = transitively;
                    wanted.Add(candidate);
                }
            }
        }

        return found;
    }

    /// <summary>Every type declared in the analysis's documents, nested ones included. Hold the gate.</summary>
    static List<NamedTypeSymbol> DeclaredTypes(Analysis analysis) {
        var types = new List<NamedTypeSymbol>();
        foreach (var document in analysis.Documents) {
            foreach (var node in document.Tree.Root.DescendantNodes()) {
                if (node is StructDeclarationSyntax or ClassDeclarationSyntax or ActorDeclarationSyntax or ProtocolDeclarationSyntax
                        or UnionDeclarationSyntax or EnumDeclarationSyntax
                    && analysis.Compilation.GetDeclaredSymbol(node) is NamedTypeSymbol type) {
                    types.Add(type.OriginalDefinition);
                }
            }
        }

        return types;
    }

    // -----------------------------------------------------------------------
    // Hierarchy items
    // -----------------------------------------------------------------------

    /// <summary>
    ///     A symbol as a call or type hierarchy item. Its data says where its
    ///     name is, so that a later request can find it again.
    /// </summary>
    JsonObject? ItemOf(Analysis analysis, Symbol symbol) {
        if (DeclarationOf(analysis, symbol) is not var (uri, lines, declaration)) {
            return null;
        }

        var name = SemanticModel.NameTokenOf(symbol, declaration);
        var selection = name.IsNone ? declaration.Span : name.Span;
        return new JsonObject {
            ["name"] = symbol is FunctionSymbol { FunctionKind: FunctionKind.Initializer } ? "init" : symbol.Name,
            ["kind"] = ItemKind(symbol),
            ["detail"] = Container(symbol) ?? "",
            ["uri"] = uri,
            ["range"] = RangeNode(lines.RangeOf(declaration.Span)),
            ["selectionRange"] = RangeNode(lines.RangeOf(selection)),
            ["data"] = new JsonObject { ["uri"] = uri, ["offset"] = selection.Start }
        };
    }

    /// <summary>The protocol's <c>SymbolKind</c> for a symbol.</summary>
    static int ItemKind(Symbol symbol) =>
        symbol switch {
            NamedTypeSymbol { TypeKind: TypeKind.Protocol } => 11,
            NamedTypeSymbol { TypeKind: TypeKind.Struct } => 23,
            NamedTypeSymbol { TypeKind: TypeKind.Enum or TypeKind.Union } => 10,
            NamedTypeSymbol => 5,
            FunctionSymbol { FunctionKind: FunctionKind.Initializer } => 9,
            FunctionSymbol { ContainingSymbol: NamedTypeSymbol or ExtensionSymbol } => 6,
            FunctionSymbol => 12,
            FieldSymbol => 8,
            _ => 13,
        };

    /// <summary>
    ///     The symbol an item names, found again by its data in the snapshot,
    ///     with the analysis that has it; null for one that is no longer
    ///     there, or is not in a document of the workspace.
    /// </summary>
    async Task<(Analysis Analysis, Document Document, Symbol Symbol)?> ResolveItemAsync(WorkspaceSnapshot snapshot, JsonElement parameters) {
        var data = parameters.Get("item")?.Get("data");
        if (data?.GetString("uri") is not { } uri || data.Value.GetInt("offset") is not { } offset || snapshot.Find(uri) is not { } document) {
            return null;
        }

        var analysis = await AnalysisOfAsync(snapshot, document);
        lock (analysis.Gate) {
            return analysis.Model(document.Tree).GetSymbolAt(offset) is { } symbol ? (analysis, document, symbol) : null;
        }
    }

    // -----------------------------------------------------------------------
    // Type hierarchy
    // -----------------------------------------------------------------------

    async Task<JsonNode?> PrepareTypeHierarchyAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var analysis = await AnalysisOfAsync(snapshot, document);
        lock (analysis.Gate) {
            var symbol = analysis.Model(document.Tree).GetSymbolAt(offset);
            var type = symbol as NamedTypeSymbol ?? (symbol as ExtensionSymbol)?.ExtendedType;
            return type is not null && ItemOf(analysis, type.OriginalDefinition) is { } item ? new JsonArray(item) : null;
        }
    }

    async Task<JsonNode?> SupertypesAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        if (await ResolveItemAsync(snapshot, parameters) is not var (analysis, _, symbol) || symbol is not NamedTypeSymbol type) {
            return new JsonArray();
        }

        lock (analysis.Gate) {
            return new JsonArray([..Supertypes(type).Select(t => ItemOf(analysis, t)).OfType<JsonObject>()]);
        }
    }

    async Task<JsonNode?> SubtypesAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        if (await ResolveItemAsync(snapshot, parameters) is not var (analysis, _, symbol) || symbol is not NamedTypeSymbol type) {
            return new JsonArray();
        }

        lock (analysis.Gate) {
            return new JsonArray([..Subtypes(analysis, type, transitively: false).Select(t => ItemOf(analysis, t)).OfType<JsonObject>()]);
        }
    }

    // -----------------------------------------------------------------------
    // Call hierarchy
    // -----------------------------------------------------------------------

    async Task<JsonNode?> PrepareCallHierarchyAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var (document, offset) = Locate(snapshot, parameters);
        var analysis = await AnalysisOfAsync(snapshot, document);
        lock (analysis.Gate) {
            return analysis.Model(document.Tree).GetSymbolAt(offset) is FunctionSymbol function && ItemOf(analysis, function) is { } item
                ? new JsonArray(item)
                : null;
        }
    }

    /// <summary>
    ///     <c>callHierarchy/incomingCalls</c>: the declarations whose bodies
    ///     name the function, each with where they do.
    /// </summary>
    async Task<JsonNode?> IncomingCallsAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        if (await ResolveItemAsync(snapshot, parameters) is not var (analysis, document, symbol) || symbol is not FunctionSymbol function) {
            return new JsonArray();
        }

        var callers = new List<(Symbol Caller, Document Document, List<TextSpan> Spans)>();
        foreach (var (owner, span, isDeclaration) in References(analysis, function, document, cancellation)) {
            if (isDeclaration || SemanticModel.FindMember(owner.Tree.Root, span.Start) is not { } member) {
                continue;
            }

            Symbol? caller;
            lock (analysis.Gate) {
                caller = analysis.Compilation.GetDeclaredSymbol(member);
            }

            if (caller is null) {
                continue;
            }

            var known = callers.FindIndex(c => ReferenceEquals(c.Caller, caller));
            if (known < 0) {
                callers.Add((caller, owner, [span]));
            } else {
                callers[known].Spans.Add(span);
            }
        }

        var calls = new JsonArray();
        lock (analysis.Gate) {
            foreach (var (caller, owner, spans) in callers) {
                if (ItemOf(analysis, caller) is { } item) {
                    calls.Add(new JsonObject {
                        ["from"] = item,
                        ["fromRanges"] = new JsonArray([..spans.Select(s => RangeNode(owner.Lines.RangeOf(s)))])
                    });
                }
            }
        }

        return calls;
    }

    /// <summary>
    ///     <c>callHierarchy/outgoingCalls</c>: the functions the function's
    ///     body calls, each with where it does.
    /// </summary>
    async Task<JsonNode?> OutgoingCallsAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        if (await ResolveItemAsync(snapshot, parameters) is not var (analysis, _, symbol)
            || symbol is not FunctionSymbol function
            || function.DeclaringSyntax.IsEmpty
            || analysis.DocumentOf(function.DeclaringSyntax[0]) is not { } owner) {
            return new JsonArray();
        }

        var calls = new JsonArray();
        lock (analysis.Gate) {
            var declaration = function.DeclaringSyntax[0];
            var model = analysis.Model(owner.Tree).GetMemberModel(declaration);
            var callees = new List<(FunctionSymbol Callee, List<TextSpan> Spans)>();
            foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()) {
                cancellation.ThrowIfCancellationRequested();
                var callee = model.GetBoundNode(invocation) switch {
                    BoundCall call => call.Function,
                    BoundConversion { Operand: BoundCall call } => call.Function,
                    _ => null,
                };

                if (callee is null || callee.DeclaringSyntax.IsEmpty) {
                    continue;
                }

                var span = invocation.Expression is MemberAccessExpressionSyntax access ? access.Name.Span : invocation.Expression.Span;
                var known = callees.FindIndex(c => SemanticModel.SameSymbol(c.Callee, callee));
                if (known < 0) {
                    callees.Add((callee, [span]));
                } else {
                    callees[known].Spans.Add(span);
                }
            }

            foreach (var (callee, spans) in callees) {
                if (ItemOf(analysis, callee) is { } item) {
                    calls.Add(new JsonObject {
                        ["to"] = item,
                        ["fromRanges"] = new JsonArray([..spans.Select(s => RangeNode(owner.Lines.RangeOf(s)))])
                    });
                }
            }
        }

        return calls;
    }
}
