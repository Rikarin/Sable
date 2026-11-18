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
///     And, from the same lowering, what the checks that run on SIL said of
///     each file — moves, exclusivity, roots — for its diagnostics.
/// </summary>
internal sealed class RootSummaries(
    long generation,
    string key,
    IReadOnlyDictionary<string, (IReadOnlyList<int> Roots, IReadOnlyList<string> Parameters)> functions,
    IReadOnlyDictionary<SyntaxTree, IReadOnlyList<Diagnostic>>? checks = null) {
    public long Generation { get; } = generation;

    /// <summary>The package whose compilation they were computed from.</summary>
    public string Key { get; } = key;

    /// <summary>What the SIL checks reported in a file: none for a workspace with type errors, which is not lowered for them.</summary>
    public IReadOnlyList<Diagnostic> ChecksOf(SyntaxTree tree) => checks?.GetValueOrDefault(tree) ?? [];

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
        var checks = new Dictionary<SyntaxTree, IReadOnlyList<Diagnostic>>(ReferenceEqualityComparer.Instance);
        try {
            var compilation = Compilation.Create(analysis.Compilation.SyntaxTrees);
            var (module, lowering) = SilGen.LowerByFile(compilation);
            foreach (var function in module.Functions) {
                if (function.ResultRoots is { } roots) {
                    functions[function.Name] = (roots, [..function.ParameterNames]);
                }
            }

            // The SIL checks are only the program's problems once its types
            // are right: a broken tree lowers to whatever it can.
            if (!compilation.GetAllDiagnosticsByFile().Any(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)) {
                foreach (var group in lowering.Where(static d => d.Diagnostic.Code != ErrorCode.ERR_NotLoweredYet && d.Tree is not null).GroupBy(static d => d.Tree!)) {
                    checks[group.Key] = [..group.Select(static d => d.Diagnostic)];
                }
            }
        } catch (Exception e) when (e is not OutOfMemoryException) {
            // A program the lowering cannot take yet has no summaries to show.
        }

        return new(analysis.Snapshot.Generation, analysis.Key, functions, checks);
    }
}

public sealed partial class LanguageServer {
    readonly object _rootsLock = new();
    readonly Dictionary<string, (Analysis Analysis, Task<RootSummaries> Task)> _roots = new(StringComparer.Ordinal);
    Task _lowering = Task.CompletedTask;

    /// <summary>
    ///     The root summaries of an analysis, computed once per analysis
    ///     asked about, and kept — the latest — for each package.
    /// </summary>
    Task<RootSummaries> RootSummariesAsync(Analysis analysis) {
        lock (_rootsLock) {
            if (_roots.TryGetValue(analysis.Key, out var known) && ReferenceEquals(known.Analysis, analysis)) {
                return known.Task;
            }

            // One lowering at a time: a lowering takes memory, and a newer
            // request waits for the one under way, then for its own.
            var task = _lowering.ContinueWith(_ => RootSummaries.Compute(analysis), TaskScheduler.Default);
            _lowering = task;
            _roots[analysis.Key] = (analysis, task);
            return task;
        }
    }

    /// <summary>The root summaries of an analysis if they are computed, without waiting for them.</summary>
    RootSummaries? RootSummariesIfDone(Analysis analysis) {
        lock (_rootsLock) {
            return _roots.TryGetValue(analysis.Key, out var known) && ReferenceEquals(known.Analysis, analysis) && known.Task.IsCompletedSuccessfully
                ? known.Task.Result
                : null;
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
                AddCallAndClosureHints(document, model, member, start, end, hints);
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

    /// <summary>
    ///     The parameter an argument written without a label is for, before
    ///     it — where the call takes more than one and the argument does not
    ///     already say it; and a closure's parameter and result types, where
    ///     they are inferred.
    /// </summary>
    static void AddCallAndClosureHints(Document document, SemanticModel model, SyntaxNode member, int start, int end, JsonArray hints) {
        foreach (var node in member.DescendantNodes()) {
            if (node.Span.End < start || node.Span.Start > end) {
                continue;
            }

            switch (node) {
                case InvocationExpressionSyntax { ArgumentList: { } list } invocation: {
                    var function = Unconverted(model.GetBoundNode(invocation)) switch {
                        BoundCall call => call.Function,
                        _ => null,
                    };

                    if (function is null || function.Parameters.Length < 2) {
                        break;
                    }

                    var arguments = list.Arguments.ToList();
                    for (var i = 0; i < arguments.Count && i < function.Parameters.Length; i++) {
                        var (argument, parameter) = (arguments[i], function.Parameters[i]);

                        // A label written puts the arguments after it by name, not place.
                        if (argument.NameToken is { IsNone: false } label && label.Text.Length > 0) {
                            break;
                        }

                        var written = argument.Expression.ToString().Trim();
                        if (parameter.Label != "_" || parameter.IsVariadic || parameter.Name.StartsWith('_') || argument.Span.Length == 0
                            || written == parameter.Name || written.EndsWith("." + parameter.Name, StringComparison.Ordinal)) {
                            continue;
                        }

                        hints.Add(new JsonObject {
                            ["position"] = PositionNode(document.Lines.PositionOf(argument.Span.Start)),
                            ["label"] = parameter.Name + ":",
                            ["kind"] = 2,
                            ["paddingRight"] = true
                        });
                    }

                    break;
                }

                case ClosureExpressionSyntax closure:
                    AddClosureHints(document, model, closure, closure.Signature.Parameters, closure.Signature.CloseParenToken, closure.Signature.ReturnType, hints);
                    break;

                case BraceClosureExpressionSyntax { Header: { } header } closure:
                    AddClosureHints(document, model, closure, header.Parameters, header.CloseParenToken, null, hints);
                    break;
            }
        }
    }

    static void AddClosureHints(
        Document document, SemanticModel model, ExpressionSyntax closure, SeparatedSyntaxList<ClosureParameterSyntax> parameters,
        SyntaxToken closeParen, TypeSyntax? returnType, JsonArray hints) {
        if (Unconverted(model.GetBoundNode(closure)) is not BoundClosure { Symbol: var function }) {
            return;
        }

        var written = parameters.ToList();
        for (var i = 0; i < written.Count && i < function.Parameters.Length; i++) {
            if (written[i].Type is null && !written[i].Identifier.IsMissing && function.Parameters[i].Type is { IsError: false } type) {
                hints.Add(new JsonObject {
                    ["position"] = PositionNode(document.Lines.PositionOf(written[i].Identifier.Span.End)),
                    ["label"] = ": " + SymbolDisplay.Type(type),
                    ["kind"] = 1
                });
            }
        }

        // The result, after the parameters' closing parenthesis, or the last parameter.
        var after = !closeParen.IsNone && !closeParen.IsMissing ? closeParen.Span.End : written.Count > 0 ? written[^1].Span.End : -1;
        if (returnType is null && after >= 0 && function.ReturnType is { IsError: false } result && result.ToDisplayString() != "Void") {
            hints.Add(new JsonObject {
                ["position"] = PositionNode(document.Lines.PositionOf(after)),
                ["label"] = "-> " + SymbolDisplay.Type(result),
                ["kind"] = 1,
                ["paddingLeft"] = true
            });
        }
    }

    static BoundNode? Unconverted(BoundNode? node) {
        while (node is BoundConversion conversion) {
            node = conversion.Operand;
        }

        return node;
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
