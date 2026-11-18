using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     What root inference concluded about the functions of a workspace:
///     for each one that returns a reference, the parameters its result may
///     refer into (docs/02 §2.4, docs/04 §3.3) — the <c>roots(0, 1)</c> of
///     its SIL, by SIL name, with the parameter names the indices count.
/// </summary>
internal sealed class RootSummaries(long generation, string key, IReadOnlyDictionary<string, (IReadOnlyList<int> Roots, IReadOnlyList<string> Parameters)> functions) {
    public long Generation { get; } = generation;

    /// <summary>The package whose compilation they were computed from.</summary>
    public string Key { get; } = key;

    /// <summary>The names a function's result is rooted in, or null when root inference said nothing about it.</summary>
    public IReadOnlyList<string>? RootsOf(FunctionSymbol function) {
        var owner = function.ContainingSymbol switch {
            NamedTypeSymbol type => type,
            ExtensionSymbol extension => extension.ExtendedType,
            _ => null,
        };
        var name = owner is not null
            ? $"{owner.ContainingSymbol?.Name}.{owner.Name}.{function.Name}"
            : $"{function.ContainingSymbol?.Name}.{function.Name}";

        var parameters = function.Parameters.Select(static p => p.Name).ToList();
        foreach (var (silName, summary) in functions) {
            if (silName != name && !silName.StartsWith(name + "$", StringComparison.Ordinal)) {
                continue;
            }

            // Overloads differ in their parameters; 'self' comes first in a method's.
            var own = summary.Parameters.Count > 0 && summary.Parameters[0] == "self" && parameters.FirstOrDefault() != "self"
                ? summary.Parameters.Skip(1).ToList()
                : summary.Parameters.ToList();
            if (!own.SequenceEqual(parameters)) {
                continue;
            }

            return [..summary.Roots.Where(i => i < summary.Parameters.Count).Select(i => summary.Parameters[i])];
        }

        return null;
    }

    /// <summary>
    ///     Lowers a separate compilation of the same trees to SIL — so the
    ///     server's own is not held while it does — and keeps the summaries
    ///     root inference recorded on its functions.
    /// </summary>
    public static RootSummaries Compute(Analysis analysis) {
        var functions = new Dictionary<string, (IReadOnlyList<int>, IReadOnlyList<string>)>(StringComparer.Ordinal);
        try {
            var compilation = Compilation.Create(analysis.Compilation.SyntaxTrees);
            var (module, _) = SilGen.Lower(compilation);
            foreach (var function in module.Functions) {
                if (function.ResultRoots is { } roots) {
                    functions[function.Name] = (roots, [..function.ParameterNames]);
                }
            }
        } catch (Exception e) when (e is not OutOfMemoryException) {
            // A program the lowering cannot take yet has no summaries to show.
        }

        return new(analysis.Snapshot.Generation, analysis.Key, functions);
    }
}

public sealed partial class LanguageServer {
    readonly object _rootsLock = new();
    Task<RootSummaries>? _roots;

    /// <summary>The root summaries of an analysis's snapshot, computed once per snapshot asked about.</summary>
    Task<RootSummaries> RootSummariesAsync(Analysis analysis) {
        lock (_rootsLock) {
            if (_roots is { IsCompletedSuccessfully: true } done && done.Result.Generation == analysis.Snapshot.Generation && done.Result.Key == analysis.Key) {
                return done;
            }

            if (_roots is { IsCompleted: false } running) {
                // One lowering at a time: the newer request waits for it,
                // then for its own.
                _roots = running.ContinueWith(_ => RootSummaries.Compute(analysis), TaskScheduler.Default);
            } else {
                _roots = Task.Run(() => RootSummaries.Compute(analysis));
            }

            return _roots;
        }
    }

    /// <summary>
    ///     Inlay hints over a range: the inferred type of each <c>let</c> and
    ///     <c>var</c> written without one, and the inferred roots — the
    ///     parameters a function's reference result may refer into, and what
    ///     a local reference refers into — where the borrow checker infers them.
    /// </summary>
    async Task<JsonNode?> InlayHintsAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var uri = parameters.Get("textDocument")?.GetString("uri") ?? "";
        var document = snapshot.Find(uri) ?? throw new LspException(LspException.InvalidParams, $"'{uri}' is not open");
        var range = parameters.Get("range") is { } r ? ReadRange(r) : new LspRange(default, new(int.MaxValue, 0));
        var start = document.Lines.OffsetOf(range.Start);
        var end = document.Lines.OffsetOf(range.End);

        var analysis = await AnalysisOfAsync(snapshot, document);
        var members = Analysis.MembersOf(document.Tree.Root)
            .Where(m => m.Span.End >= start && m.Span.Start <= end)
            .ToList();

        // Root inference runs on SIL, and lowering the workspace is not
        // free: only when something here returns a reference.
        RootSummaries? roots = null;
        if (members.Any(static m => m is FunctionDeclarationSyntax { ReturnClause.Type: RefTypeSyntax or ReferenceTypeSyntax or OptionalTypeSyntax { ElementType: ReferenceTypeSyntax } })
            || members.Any(static m => m.DescendantNodes().Any(static n => n is PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.AmpersandToken }))) {
            roots = await RootSummariesAsync(analysis);
        }

        var hints = new JsonArray();
        foreach (var member in members) {
            cancellation.ThrowIfCancellationRequested();
            lock (analysis.Gate) {
                var model = analysis.Model(document.Tree).GetMemberModel(member);
                AddTypeHints(document, model, member, start, end, roots, hints);
                if (member is FunctionDeclarationSyntax { ReturnClause: { } result } function
                    && roots is not null
                    && model.GetSymbolOf(function) is FunctionSymbol symbol
                    && roots.RootsOf(symbol) is { } names) {
                    hints.Add(RootHint(document, result.Span.End, names, $"'{symbol.Name}' returns a reference that may refer into "
                        + (names.Count == 0 ? "nothing it is given: it is rooted in the heap or in static storage" : string.Join(" and ", names.Select(static n => $"'{n}'")) + "; the result must not outlive " + (names.Count == 1 ? "it" : "them"))));
                }
            }
        }

        return hints;
    }

    static void AddTypeHints(Document document, SemanticModel model, SyntaxNode member, int start, int end, RootSummaries? roots, JsonArray hints) {
        var localRoots = new Dictionary<Symbol, IReadOnlySet<string>>();
        foreach (var statement in member.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()) {
            if (model.GetBoundNode(statement) is not BoundLocalDeclaration { Local: var local, Initializer: var initializer }) {
                continue;
            }

            var holdsReference = HoldsReference(local.Type);
            if (holdsReference && initializer is not null) {
                localRoots[local] = RootsOf(initializer, localRoots, roots);
            }

            if (statement.Span.End < start || statement.Span.Start > end || statement.Declarator.Pattern is not BindingPatternSyntax binding) {
                continue;
            }

            if (statement.Declarator.TypeAnnotation is null && !local.Type.IsError) {
                hints.Add(new JsonObject {
                    ["position"] = PositionNode(document.Lines.PositionOf(binding.Identifier.Span.End)),
                    ["label"] = ": " + SymbolDisplay.Type(local.Type),
                    ["kind"] = 1
                });
            }

            if (holdsReference && localRoots.TryGetValue(local, out var rootedIn) && rootedIn.Count > 0) {
                var names = rootedIn.Order(StringComparer.Ordinal).ToList();
                hints.Add(RootHint(document, binding.Identifier.Span.End, names,
                    $"'{local.Name}' refers into {string.Join(" and ", names.Select(static n => $"'{n}'"))}, and may not be used once "
                    + (names.Count == 1 ? "it is" : "they are") + " gone"));
            }
        }
    }

    static JsonObject RootHint(Document document, int offset, IReadOnlyList<string> names, string tooltip) =>
        new() {
            ["position"] = PositionNode(document.Lines.PositionOf(offset)),
            ["label"] = $"roots({string.Join(", ", names)})",
            ["paddingLeft"] = true,
            ["tooltip"] = new JsonObject { ["kind"] = "markdown", ["value"] = tooltip + "  \n*inferred by root inference*" }
        };

    static bool HoldsReference(TypeSymbol type) =>
        type is ReferenceTypeSymbol || type is OptionalTypeSymbol { UnderlyingType: ReferenceTypeSymbol };

    /// <summary>
    ///     What a reference-valued expression refers into, as root inference
    ///     would see it in the front end: the variable a place starts from,
    ///     through fields, joins, other references and — by their summaries —
    ///     calls.
    /// </summary>
    static IReadOnlySet<string> RootsOf(BoundExpression expression, Dictionary<Symbol, IReadOnlySet<string>> locals, RootSummaries? summaries) {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        Walk(expression);
        return roots;

        void Walk(BoundExpression? node) {
            switch (node) {
                case null:
                    return;
                case BoundConversion conversion:
                    Walk(conversion.Operand);
                    return;
                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.AddressOf } address:
                    Walk(address.Operand);
                    return;
                case BoundLocalAccess access:
                    if (locals.TryGetValue(access.Local, out var through)) {
                        roots.UnionWith(through);
                    } else {
                        roots.Add(access.Local.Name);
                    }

                    return;
                case BoundParameterAccess parameter:
                    roots.Add(parameter.Parameter.Name);
                    return;
                case BoundSelfAccess:
                    roots.Add("self");
                    return;
                case BoundFieldAccess field:
                    if (field.Receiver?.Type.IsReferenceType == true) {
                        roots.Add("heap");
                    } else if (field.Receiver is null) {
                        roots.Add("self");
                    } else {
                        Walk(field.Receiver);
                    }

                    return;
                case BoundConditional conditional:
                    Walk(conditional.WhenTrue);
                    Walk(conditional.WhenFalse);
                    return;
                case BoundCall call:
                    var arguments = call.Receiver is not null ? [call.Receiver, ..call.Arguments] : call.Arguments.ToList();
                    var parameters = call.Receiver is not null ? ["self", ..call.Function.Parameters.Select(static p => p.Name)] : call.Function.Parameters.Select(static p => p.Name).ToList();
                    if (summaries?.RootsOf(call.Function) is { } named) {
                        foreach (var name in named) {
                            var index = parameters.IndexOf(name);
                            if (index >= 0 && index < arguments.Count) {
                                Walk(arguments[index]);
                            }
                        }
                    } else {
                        foreach (var argument in arguments) {
                            if (HoldsReference(argument.Type)) {
                                Walk(argument);
                            }
                        }
                    }

                    return;
            }
        }
    }
}
