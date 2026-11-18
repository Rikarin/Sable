using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Binds statements and expressions: resolves every name to a symbol and
///     every expression to a type.
/// </summary>
/// <remarks>
///     <para>
///         Scopes are a stack of dictionaries rather than a chain of binder
///         objects. The chain is Roslyn's shape and is better when binders carry
///         behaviour; here they only carry names, and a stack says that plainly.
///     </para>
///     <para>
///         Lookup order for a bare name: locals innermost first, then the
///         containing function's parameters, then the members of the enclosing type,
///         then the module, then imports, then <c>Sable.Core</c>.
///     </para>
/// </remarks>
public sealed partial class Binder {
    readonly Compilation _compilation;
    readonly FileScope _scope;
    readonly Conversions _conversions;
    readonly DiagnosticBag _diagnostics = new();
    readonly List<Dictionary<string, Symbol>> _locals = [];
    readonly Dictionary<SyntaxNode, BoundNode> _bound = [];

    /// <summary>
    ///     The declaration whose body is being bound. Setting it also brings
    ///     that declaration's same-type requirements into force.
    /// </summary>
    Symbol _containingSymbol {
        get => _containingSymbolField;
        set {
            _containingSymbolField = value;
            _conversions.Equivalence = TypeEquivalence.ForSymbol(value);
        }
    }

    Symbol _containingSymbolField = null!;
    TypeSymbol _returnType;
    int _loopDepth;

    /// <summary>
    ///     True inside a closure body whose return type was not written and could
    ///     not be read off the context: the first <c>return</c> with a value fixes
    ///     it, and the rest are checked against that.
    /// </summary>
    bool _inferringReturn;

    /// <summary>
    ///     Type parameters of the call being bound that inference has not settled
    ///     yet. A closure argument treats one of these as "not known", so its own
    ///     body decides, rather than being checked against a name that stands for
    ///     nothing.
    /// </summary>
    ImmutableArray<TypeParameterSymbol> _openTypeParameters = [];

    /// <summary>
    ///     Types a test in scope has established, keyed by the binding tested.
    /// </summary>
    /// <remarks>
    ///     docs/03 §3.5: <c>if x is Int { }</c> gives x the type Int inside the
    ///     branch, and <c>if x != none</c> unwraps it. The map is a stack of
    ///     frames, pushed for a branch and popped when it ends, so nothing a test
    ///     established outlives the test. Assigning to a binding drops what was
    ///     known about it.
    /// </remarks>
    readonly List<Dictionary<Symbol, TypeSymbol>> _narrowed = [];

    /// <summary>
    ///     The error type the enclosing function declared, or null when it does
    ///     not throw. An error has to reach one of these, or a <c>catch</c>.
    /// </summary>
    TypeSymbol? _declaredThrows;

    /// <summary>
    ///     True inside a <c>rethrows</c> body, where the only errors allowed out
    ///     are the ones the function's own closure parameters raise.
    /// </summary>
    bool _rethrows;

    bool _inAsyncContext;

    /// <summary>Depth of <c>try</c> and <c>await</c> operands being bound.</summary>
    int _tryDepth;

    int _awaitDepth;

    /// <summary>Depth of <c>do</c> bodies that have a <c>catch</c>.</summary>
    int _catchDepth;

    /// <summary>True when something under the innermost <c>try</c> can throw.</summary>
    bool _threwInTry;

    /// <summary>Expressions bound for the current statement, against the budget.</summary>
    int _steps;

    bool _budgetSpent;

    /// <summary>
    ///     What the innermost <c>do</c> body can throw, which is what its
    ///     <c>catch</c> clauses match against.
    /// </summary>
    TypeSymbol? _thrownInBody;

    /// <summary>
    ///     What the closure body just bound can let out, which is what its
    ///     function type claims. Written by <c>BindClosureBody</c>, read by the
    ///     caller that builds the type.
    /// </summary>
    TypeSymbol? _closureThrows;

    internal Binder(Compilation compilation, FileScope scope, Symbol containingSymbol) {
        _compilation = compilation;
        _scope = scope;
        _conversions = new(compilation.Core);
        _containingSymbol = containingSymbol;
        _returnType = compilation.Core.Void;
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.ToList();

    CoreTypes Core => _compilation.Core;

    /// <summary>The bound node produced for a piece of syntax, if any.</summary>
    public BoundNode? GetBound(SyntaxNode syntax) => _bound.TryGetValue(syntax, out var node) ? node : null;

    /// <summary>Everything this binder bound, for the semantic model to keep.</summary>
    public IReadOnlyDictionary<SyntaxNode, BoundNode> BoundNodes => _bound;

    T Record<T>(SyntaxNode syntax, T node) where T : BoundNode {
        _bound[syntax] = node;
        return node;
    }

    // -----------------------------------------------------------------------
    // Entry points
    // -----------------------------------------------------------------------

    /// <summary>Binds a function body against its signature.</summary>
    public BoundBlock BindFunctionBody(FunctionSymbol function, BlockSyntax body) {
        var savedSymbol = _containingSymbol;
        var savedReturn = _returnType;

        var savedEffects = SaveEffects();

        _containingSymbol = function;
        _returnType = function.ReturnType;
        _declaredThrows = function.ThrownType;
        _rethrows = function.IsRethrows;
        _inAsyncContext = function.IsAsync;
        SeedRethrowSources(function);

        PushScope();
        foreach (var parameter in function.Parameters) {
            Declare(parameter.Name, parameter);
        }

        HoistLocalTypes(body);

        var bound = BindBlock(body);

        // One graph serves all three flow checks.
        var graph = ControlFlowGraph.Build(bound, function.Parameters);
        CheckFlow(function, body, graph);
        DefiniteAssignment.Analyze(graph, _diagnostics);

        PopScope();
        RestoreEffects(savedEffects);
        _containingSymbol = savedSymbol;
        _returnType = savedReturn;
        return bound;
    }

    /// <summary>Checks that a function returns on every path, and warns on dead code.</summary>
    void CheckFlow(FunctionSymbol function, BlockSyntax body, ControlFlowGraph graph) {
        if (FlowAnalysis.FirstUnreachable(graph) is { } unreachable) {
            _diagnostics.Add(
                Diagnostic.Warning(
                    ErrorCode.WRN_UnreachableCode,
                    unreachable.Syntax.Span,
                    "this statement can never be reached"
                )
            );
        }

        // An initialiser's "return type" is the type being built; it is not
        // something the body returns.
        if (function.FunctionKind
            is FunctionKind.Initializer or FunctionKind.Deinitializer) {
            return;
        }

        if (function.ReturnType.Equals(Core.Void) || function.ReturnType.IsError) {
            return;
        }

        if (!graph.EndIsReachable) {
            return;
        }

        // Point at the closing brace, which is where the path runs out.
        _diagnostics.Error(
            ErrorCode.ERR_NotAllPathsReturn,
            body.CloseBraceToken.Span,
            "not all paths return a value of type "
            + $"'{function.ReturnType.ToDisplayString()}'"
        );
    }

    /// <summary>Binds an expression-bodied function, as if it returned its value.</summary>
    public BoundBlock BindExpressionBody(FunctionSymbol function, ExpressionSyntax expression) {
        var savedSymbol = _containingSymbol;
        var savedReturn = _returnType;

        var savedEffects = SaveEffects();

        _containingSymbol = function;
        _returnType = function.ReturnType;
        _declaredThrows = function.ThrownType;
        _rethrows = function.IsRethrows;
        _inAsyncContext = function.IsAsync;
        SeedRethrowSources(function);

        PushScope();
        foreach (var parameter in function.Parameters) {
            Declare(parameter.Name, parameter);
        }

        var value = BindExpression(expression, _returnType);
        if (!_returnType.Equals(Core.Void)) {
            value = Convert(value, _returnType, expression);
        }

        PopScope();
        RestoreEffects(savedEffects);
        _containingSymbol = savedSymbol;
        _returnType = savedReturn;

        return new BoundBlock(expression, [], [new BoundReturn(expression, value)]);
    }

    /// <summary>Binds a stored member's initialiser against its declared type.</summary>
    public BoundExpression BindInitializer(ExpressionSyntax value, TypeSymbol expected) {
        PushScope();
        var bound = BindExpression(value, expected.IsError ? null : expected);
        if (!expected.IsError) {
            bound = Convert(bound, expected, value);
        }

        PopScope();
        return bound;
    }

    /// <summary>Binds a standalone statement, for tests and scripts.</summary>
    public BoundStatement BindStatementInNewScope(StatementSyntax statement) {
        PushScope();
        var bound = BindStatement(statement);
        PopScope();
        return bound;
    }

    /// <summary>Binds a standalone expression.</summary>
    public BoundExpression BindExpressionInNewScope(ExpressionSyntax expression) {
        PushScope();
        var bound = BindExpression(expression);
        PopScope();
        return bound;
    }

    // -----------------------------------------------------------------------
    // Scopes
    // -----------------------------------------------------------------------

    void PushScope() => _locals.Add(new(StringComparer.Ordinal));

    /// <summary>
    ///     A position to record the names in scope at, for the language
    ///     server's completion; negative for none. See <see cref="ScopeAtCapture" />.
    /// </summary>
    internal int CapturePosition { get; init; } = -1;

    internal TextSpan? CapturedFor => _capturedFor;

    /// <summary>The node the scope was last recorded for: the innermost one holding the position.</summary>
    TextSpan? _capturedFor;

    /// <summary>
    ///     The locals and parameters in scope at <see cref="CapturePosition" />,
    ///     innermost first and with shadowed names left out, or null when
    ///     binding never passed it.
    /// </summary>
    internal IReadOnlyList<Symbol>? ScopeAtCapture { get; private set; }

    /// <summary>Records the scope as it is on entering a node that holds the capture position.</summary>
    void CaptureScope(SyntaxNode node) {
        if (CapturePosition >= 0) {
            CaptureScopeAt(node.Span);
        }
    }

    /// <summary>Records the scope for a position between two statements of a block, or before the first, or after the last.</summary>
    void CaptureScopeInGap(BlockSyntax block, int start, int end) {
        if (CapturePosition >= 0 && CapturePosition >= start && CapturePosition <= end) {
            CaptureScopeAt(block.Span);
        }
    }

    void CaptureScopeAt(TextSpan span) {
        // Ends are inclusive: a name being typed ends at the caret.
        if (CapturePosition < span.Start || CapturePosition > span.End) {
            return;
        }

        // Nodes holding the position nest, so a deeper one is never longer;
        // one bound later that is longer is an outer one's gap, and loses.
        if (_capturedFor is { } previous && previous.Length < span.Length) {
            return;
        }

        _capturedFor = span;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new List<Symbol>();
        for (var i = _locals.Count - 1; i >= 0; i--) {
            foreach (var (name, symbol) in _locals[i]) {
                if (seen.Add(name)) {
                    symbols.Add(symbol);
                }
            }
        }

        ScopeAtCapture = symbols;
    }
    void PopScope() => _locals.RemoveAt(_locals.Count - 1);

    void Declare(string name, Symbol symbol) {
        if (name.Length == 0 || _locals.Count == 0) {
            return;
        }

        var scope = _locals[^1];
        if (scope.ContainsKey(name)) {
            _diagnostics.Error(
                ErrorCode.ERR_DuplicateDeclaration,
                symbol.Location ?? default,
                $"'{name}' is already declared in this scope"
            );
            return;
        }

        scope[name] = symbol;
    }

    void PushNarrowing(Dictionary<Symbol, TypeSymbol> frame) => _narrowed.Add(frame);

    void PopNarrowing() => _narrowed.RemoveAt(_narrowed.Count - 1);

    /// <summary>The narrowed type for a binding, innermost test first.</summary>
    TypeSymbol? NarrowedType(Symbol symbol) {
        for (var i = _narrowed.Count - 1; i >= 0; i--) {
            if (_narrowed[i].TryGetValue(symbol, out var type)) {
                return type;
            }
        }

        return null;
    }

    /// <summary>Forgets what was known about a binding that has been written.</summary>
    void DropNarrowing(Symbol symbol) {
        foreach (var frame in _narrowed) {
            frame.Remove(symbol);
        }
    }

    /// <summary>
    ///     Mutable bindings that a closure or local function writes from
    ///     outside its own body.
    /// </summary>
    /// <remarks>
    ///     Such a body can run during any call, so a call ends whatever a test
    ///     established about them: after <c>let clear = () => { p = none }</c>,
    ///     <c>if p != none { clear(); p.x }</c> must not read <c>p</c> as narrowed.
    /// </remarks>
    readonly HashSet<Symbol> _writtenByNestedBodies = [];

    /// <summary>
    ///     The expression read as its type's representative under the same-type
    ///     requirements in force, or unchanged when they say nothing about it.
    /// </summary>
    BoundExpression AsCanonical(BoundExpression expression) {
        var canonical = _conversions.Equivalence.Canonical(expression.Type);
        return canonical.Equals(expression.Type)
            ? expression
            : new BoundConversion(expression.Syntax, canonical, expression, ConversionKind.Identity);
    }

    /// <summary>A call may run a nested body that writes these, so forget them.</summary>
    BoundExpression AfterCall(BoundExpression call) {
        foreach (var symbol in _writtenByNestedBodies) {
            DropNarrowing(symbol);
        }

        return call;
    }

    /// <summary>
    ///     Enters a closure or local function body, which keeps only the
    ///     narrowings that cannot go stale.
    /// </summary>
    /// <remarks>
    ///     The body runs later, when a <c>var</c> may hold something else, so a
    ///     test outside it says nothing about the <c>var</c> inside. A
    ///     <c>let</c> cannot change, so what was learnt about it stays true.
    /// </remarks>
    List<Dictionary<Symbol, TypeSymbol>> EnterNestedBody() {
        var saved = _narrowed.ToList();
        var stable = new Dictionary<Symbol, TypeSymbol>();

        foreach (var frame in saved) {
            foreach (var (symbol, type) in frame) {
                if (IsStableBinding(symbol)) {
                    stable[symbol] = type;
                }
            }
        }

        _narrowed.Clear();
        _narrowed.Add(stable);
        return saved;
    }

    void ExitNestedBody(List<Dictionary<Symbol, TypeSymbol>> saved) {
        _narrowed.Clear();
        _narrowed.AddRange(saved);
    }

    static bool IsStableBinding(Symbol symbol) =>
        symbol switch {
            LocalSymbol local => !local.IsMutable,
            ParameterSymbol parameter => parameter.Modifier is not (ParameterModifier.InOut or ParameterModifier.Out),
            _ => false,
        };

    Symbol? LookupLocal(string name) {
        for (var i = _locals.Count - 1; i >= 0; i--) {
            if (_locals[i].TryGetValue(name, out var symbol)) {
                return symbol;
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether <c>self</c> can be written through here.
    /// </summary>
    /// <remarks>
    ///     A class or actor is a reference, so self is always writable through.
    ///     A struct is a value, so only a <c>mutating</c> member — or an
    ///     initialiser, which is building it — may change it.
    /// </remarks>
    bool SelfIsMutable(NamedTypeSymbol type) {
        if (type.IsReferenceType) {
            return true;
        }

        for (var symbol = _containingSymbol; symbol is not null; symbol = symbol.ContainingSymbol) {
            if (symbol is not FunctionSymbol function) {
                continue;
            }

            return function.IsMutating
                || function.FunctionKind
                    is FunctionKind.Initializer or FunctionKind.Deinitializer;
        }

        return false;
    }

    /// <summary>What <c>self</c> is in a member of the type: the type, or the array an array extension extends.</summary>
    static TypeSymbol SelfTypeOf(NamedTypeSymbol type) => type.ExtendedArray ?? (TypeSymbol)type;

    NamedTypeSymbol? EnclosingType() {
        for (var symbol = _containingSymbol; symbol is not null; symbol = symbol.ContainingSymbol) {
            if (symbol is NamedTypeSymbol type) {
                return type;
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Effects: 'throws' and 'async'
    // -----------------------------------------------------------------------

    private readonly record struct Effects(
        TypeSymbol? Throws,
        bool Rethrows,
        bool Async,
        int Try,
        int Await,
        int Catch,
        bool Threw);

    /// <summary>
    ///     The thrown types seen so far, reset around anything that has its own
    ///     handler.
    /// </summary>

    Effects SaveEffects() =>
        new(
            _declaredThrows,
            _rethrows,
            _inAsyncContext,
            _tryDepth,
            _awaitDepth,
            _catchDepth,
            _threwInTry
        );

    void RestoreEffects(Effects saved) {
        _declaredThrows = saved.Throws;
        _rethrows = saved.Rethrows;
        _inAsyncContext = saved.Async;
        _tryDepth = saved.Try;
        _awaitDepth = saved.Await;
        _catchDepth = saved.Catch;
        _threwInTry = saved.Threw;
    }

    /// <summary>
    ///     Records that the expression being bound can throw, and complains if
    ///     nothing marked it.
    /// </summary>
    /// <remarks>
    ///     The mark is lexical, as in Swift: <c>try</c> covers everything in the
    ///     expression it is written on, so one <c>try</c> can stand for several
    ///     calls. What it does not do is let the error leave silently — that is
    ///     checked where the <c>try</c> itself is bound.
    /// </remarks>
    /// <summary>The union of two error types, either of which may be absent.</summary>
    static TypeSymbol? Union(TypeSymbol? left, TypeSymbol? right) =>
        left is null ? right
        : right is null ? left
        : StructuralUnionTypeSymbol.Create([left, right]);

    void MarkThrows(
        TextSpan span,
        string what,
        TypeSymbol thrown,
        bool viaParameter = false
    ) {
        // In a 'rethrows' function the error has to come from one of the
        // closures the caller handed over. Anything else would let an error
        // out of a signature the caller reads as non-throwing.
        if (_rethrows && !viaParameter && _catchDepth == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_RethrowsThrowsItsOwn,
                span,
                $"{what} throws on its own, and a 'rethrows' function may only "
                + "throw what one of its own closure parameters throws"
            );

            // The 'try' around it was not pointless, whatever else is wrong.
            if (_tryDepth > 0) {
                _threwInTry = true;
            }

            return;
        }

        // One 'do' body may call several throwing functions. When they do not
        // agree, what reaches the catches is the union of them.
        _thrownInBody = Union(_thrownInBody, thrown);

        if (_tryDepth > 0) {
            _threwInTry = true;
            return;
        }

        _diagnostics.Error(
            ErrorCode.ERR_MustUseTry,
            span,
            $"{what} can throw, so the expression needs 'try'"
        );
    }

    /// <summary>
    ///     What a call to a <c>rethrows</c> function actually throws: whatever the
    ///     throwing closures among its arguments throw, and nothing when none of
    ///     them do — which is the whole point of the annotation.
    /// </summary>
    static TypeSymbol? RethrownBy(ImmutableArray<BoundExpression> arguments) =>
        TypeBinder.RethrownType(arguments.Select(static a => a.Type));

    /// <summary>
    ///     True when one of the throwing arguments is a parameter of the function
    ///     being bound, which is what lets one <c>rethrows</c> function pass its
    ///     closure on to another.
    /// </summary>
    bool PassesOwnParameter(ImmutableArray<BoundExpression> arguments) =>
        arguments.Any(a =>
            a.Type is FunctionTypeSymbol { ThrownType: not null } && IsRethrowSource(a)
        );

    /// <summary>
    ///     The throwing closures a <c>rethrows</c> body is allowed to throw
    ///     through: its own parameters, and the locals that hold one — which is
    ///     how <c>guard let body</c> on an optional parameter stays legal.
    /// </summary>
    readonly HashSet<Symbol> _rethrowSources = [];

    bool IsRethrowSource(BoundExpression expression) =>
        expression switch {
            BoundConversion conversion => IsRethrowSource(conversion.Operand),
            BoundParameterAccess access => _rethrowSources.Contains(access.Parameter),
            BoundLocalAccess access => _rethrowSources.Contains(access.Local),
            _ => false,
        };

    /// <summary>
    ///     Notes that a name now holds a closure the body may rethrow from, when
    ///     the value it was given was one.
    /// </summary>
    void TrackRethrowSource(Symbol name, BoundExpression? value) {
        if (!_rethrows || value is null) {
            return;
        }

        // A binding that can be reassigned may end up holding any closure at
        // all by the time it is called, so only one that cannot counts:
        // after 'var k = body; k = boom', calling 'k' throws on its own.
        if (!IsStableBinding(name)) {
            return;
        }

        if (IsRethrowSource(value)) {
            _rethrowSources.Add(name);
        }
    }

    /// <summary>
    ///     Seeds the sources with the parameters of a <c>rethrows</c> function:
    ///     every one whose type is a throwing function, behind an optional or not.
    /// </summary>
    void SeedRethrowSources(FunctionSymbol function) {
        if (!function.IsRethrows) {
            return;
        }

        foreach (var parameter in function.Parameters) {
            var type = parameter.Type is OptionalTypeSymbol optional
                ? optional.UnderlyingType
                : parameter.Type;

            if (type is FunctionTypeSymbol { ThrownType: not null } && IsStableBinding(parameter)) {
                _rethrowSources.Add(parameter);
            }
        }
    }

    /// <summary>Records that the expression being bound suspends.</summary>
    void MarkAsync(TextSpan span, string what) {
        if (!_inAsyncContext) {
            _diagnostics.Error(
                ErrorCode.ERR_AwaitOutsideAsync,
                span,
                $"{what} is async, and this is not an async context"
            );
            return;
        }

        if (_awaitDepth == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_MustUseAwait,
                span,
                $"{what} is async, so the expression needs 'await'"
            );
        }
    }

    /// <summary>True when an error thrown here has somewhere to go.</summary>
    bool CanThrowHere() => _catchDepth > 0 || _declaredThrows is not null;

    /// <summary>
    ///     Checks that an error leaving the function fits what it declares to
    ///     throw: as it is, or through a <c>From</c> conformance, which is the
    ///     one conversion applied implicitly between error types (docs/03 §3.6).
    /// </summary>
    /// <remarks>
    ///     Inside a <c>do</c> the error goes to the catches instead, and bare
    ///     <c>throws</c> erases every error to <c>any Error</c>, so neither is
    ///     checked here.
    /// </remarks>
    void CheckErrorFits(TypeSymbol thrown, TextSpan span, string what) {
        if (_catchDepth > 0
            || _declaredThrows is not { } declared
            || declared.IsError
            || declared.Equals(Core.AnyError)) {
            return;
        }

        var cases = thrown is StructuralUnionTypeSymbol union ? union.Cases : [thrown];

        foreach (var error in cases) {
            if (error.IsError
                || error.Equals(Core.Never)
                || _conversions.Classify(error, declared, false) is not null
                || ConvertsFrom(declared, error)) {
                continue;
            }

            _diagnostics.Error(
                ErrorCode.ERR_ErrorTypeDoesNotFit,
                span,
                $"{what} throws '{error.ToDisplayString()}', and this function throws "
                + $"'{declared.ToDisplayString()}'; add "
                + $"'extension {declared.ToDisplayString()}: From<{error.ToDisplayString()}>', "
                + "or catch it"
            );
            return;
        }
    }

    /// <summary>True when <paramref name="target" /> declares <c>From&lt;source&gt;</c>.</summary>
    bool ConvertsFrom(TypeSymbol target, TypeSymbol source) =>
        target is NamedTypeSymbol named
        && named.OriginalDefinition.BaseTypes.Any(b =>
            b is NamedTypeSymbol { IsConstructed: true } conformance
            && ReferenceEquals(conformance.OriginalDefinition, Core.From)
            && conformance.TypeArguments[0].Equals(source)
        );

    BoundExpression BindTry(TryExpressionSyntax syntax, TypeSymbol? expectedType) {
        var kind = syntax.QuestionOrBangToken.Kind switch {
            SyntaxKind.QuestionToken => TryKind.Optional,
            SyntaxKind.BangToken => TryKind.Force,
            _ => syntax.CatchBody is null ? TryKind.Propagate : TryKind.Fallback,
        };

        // 'try? e' is an optional, so the operand is not the context's type.
        var inner = kind == TryKind.Optional ? null : expectedType;

        var outerThrew = _threwInTry;
        var outerThrown = _thrownInBody;
        _threwInTry = false;
        _thrownInBody = null;
        _tryDepth++;

        var value = BindExpression(syntax.Expression, inner);

        _tryDepth--;
        var threw = _threwInTry;
        var operandThrown = _thrownInBody;
        _threwInTry = outerThrew || (threw && _tryDepth > 0);

        // What this body can throw is what can actually leave it. 'try?',
        // 'try!' and 'try e catch { }' all settle the error here, so only a
        // plain 'try' adds to it.
        _thrownInBody = kind == TryKind.Propagate
            ? Union(outerThrown, operandThrown)
            : outerThrown;

        if (!threw && !value.HasError) {
            _diagnostics.Add(
                Diagnostic.Warning(
                    ErrorCode.WRN_NothingThrowsHere,
                    syntax.TryKeyword.Span,
                    "nothing in this expression can throw"
                )
            );
        }

        switch (kind) {
            case TryKind.Optional:
                return Record(syntax, new BoundTry(syntax, new OptionalTypeSymbol(value.Type), value, kind, null));

            case TryKind.Force:
                return Record(syntax, new BoundTry(syntax, value.Type, value, kind, null));

            case TryKind.Fallback: {
                var fallback = BindCatchBody(syntax.CatchBody!, value.Type, operandThrown ?? _declaredThrows);

                return Record(
                    syntax,
                    new BoundTry(syntax, value.Type, value, kind, fallback) { ErrorLocal = _catchError }
                );
            }

            default:
                if (threw && !CanThrowHere()) {
                    _diagnostics.Error(
                        ErrorCode.ERR_ThrowsNotDeclared,
                        syntax.Span,
                        "this error has nowhere to go: declare 'throws', "
                        + "catch it, or use 'try?'"
                    );
                } else if (operandThrown is not null) {
                    CheckErrorFits(operandThrown, syntax.Span, "this expression");
                }

                return Record(syntax, new BoundTry(syntax, value.Type, value, kind, null));
        }
    }

    /// <summary>Binds the value an inline <c>catch</c> supplies.</summary>
    /// <summary>The <c>error</c> the catch being bound introduced.</summary>
    LocalSymbol? _catchError;

    BoundExpression BindCatchBody(
        SyntaxNode body,
        TypeSymbol expected,
        TypeSymbol? thrown
    ) {
        // 'error' is bound implicitly in a catch, per SYNTAX.md §18.
        PushScope();

        var errorType = thrown ?? Core.AnyError;
        var error = new LocalSymbol("error", _containingSymbol, body, false) { TypeResolver = () => errorType };
        Declare("error", error);
        _catchError = error;

        try {
            return BindCatchValue(body, expected);
        } finally {
            PopScope();
        }
    }

    BoundExpression BindCatchValue(SyntaxNode body, TypeSymbol expected) {
        switch (body) {
            case ExpressionSyntax expression:
                return Convert(BindExpression(expression, expected), expected, expression);

            case BlockSyntax block: {
                // The block's value is what its last expression statement
                // produces, which is how the spec writes it.
                var saved = SaveEffects();
                _catchDepth++;

                PushScope();
                BoundExpression? value = null;
                foreach (var statement in block.Statements) {
                    if (statement is ExpressionStatementSyntax last
                        && ReferenceEquals(last, block.Statements[^1])) {
                        value = Convert(BindExpression(last.Expression, expected), expected, last.Expression);
                        continue;
                    }

                    BindStatement(statement);
                }

                PopScope();

                RestoreEffects(saved);

                if (value is not null) {
                    return value;
                }

                _diagnostics.Error(
                    ErrorCode.ERR_CannotInferType,
                    block.Span,
                    "this 'catch' has to end in a value of type "
                    + $"'{expected.ToDisplayString()}'"
                );
                return new BoundError(block);
            }

            default:
                return new BoundError(body);
        }
    }

    BoundStatement BindDo(DoStatementSyntax statement) {
        var saved = SaveEffects();
        var savedThrown = _thrownInBody;
        if (statement.Catches.Count > 0) {
            _catchDepth++;
        }

        _thrownInBody = null;

        var body = BindBlock(statement.Body);

        // The catches match what the body can actually throw; failing that,
        // what the enclosing function declares; failing that, anything.
        var errorType = _thrownInBody ?? _declaredThrows ?? Core.AnyError;

        RestoreEffects(saved);
        _thrownInBody = savedThrown;
        var catches = ImmutableArray.CreateBuilder<BoundCatch>();

        foreach (var clause in statement.Catches) {
            PushScope();

            var pattern = clause.Pattern is null
                ? null
                : BindPattern(clause.Pattern, errorType);

            // A bare 'catch' binds the error as 'error', per SYNTAX.md §18.
            LocalSymbol? errorLocal = null;
            if (pattern is null) {
                errorLocal = new("error", _containingSymbol, clause, false);
                errorLocal.TypeResolver = () => errorType;
                Declare("error", errorLocal);
            }

            var guard = clause.WhereClause is { } where
                ? Convert(BindExpression(where.Condition, Core.Bool), Core.Bool, where.Condition)
                : null;

            var clauseBody = BindBlock(clause.Body);
            PopScope();

            catches.Add(new(clause, pattern, guard, clauseBody, errorLocal));
        }

        return Record(statement, new BoundDo(statement, body, catches.ToImmutable()));
    }

    // -----------------------------------------------------------------------
    // Statements
    // -----------------------------------------------------------------------

    public BoundStatement BindStatement(StatementSyntax statement) {
        CaptureScope(statement);

        // The budget is per statement, so a long body costs nothing extra and
        // the diagnostic points at the one expression that ran out.
        _steps = 0;
        _budgetSpent = false;
        return BindStatementCore(statement);
    }

    BoundStatement BindStatementCore(StatementSyntax statement) =>
        statement switch {
            BlockSyntax block => BindBlock(block),
            LocalDeclarationStatementSyntax declaration => BindLocalDeclaration(declaration),
            ExpressionStatementSyntax expression => Record(
                expression,
                new BoundExpressionStatement(expression, BindExpression(expression.Expression))
            ),
            IfStatementSyntax ifStatement => BindIf(ifStatement),
            WhileStatementSyntax whileStatement => BindWhile(whileStatement),
            RepeatStatementSyntax repeat => BindRepeat(repeat),
            ForStatementSyntax forStatement => BindForEach(forStatement),
            ReturnStatementSyntax returnStatement => BindReturn(returnStatement),
            ThrowStatementSyntax throwStatement => BindThrow(throwStatement),
            BreakStatementSyntax breakStatement => BindBreak(breakStatement),
            ContinueStatementSyntax continueStatement => BindContinue(continueStatement),
            DeferStatementSyntax defer => Record(
                defer,
                new BoundDefer(defer, BindStatement(defer.Body))
            ),
            GuardStatementSyntax guard => BindGuard(guard),
            LabeledStatementSyntax labeled => BindLabeled(labeled),
            DeclarationStatementSyntax declaration => BindDeclarationStatement(declaration),
            DoStatementSyntax doStatement => BindDo(doStatement),
            SwitchStatementSyntax switchStatement => BindSwitch(switchStatement),
            FallthroughStatementSyntax fallthrough =>
                Record(fallthrough, new BoundFallthrough(fallthrough)),
            UnsafeStatementSyntax unsafeBlock => BindScopedBlock(
                unsafeBlock,
                ScopeKind.Unsafe,
                default,
                null,
                unsafeBlock.Body
            ),
            ComptimeStatementSyntax comptime => BindScopedBlock(
                comptime,
                ScopeKind.Comptime,
                default,
                null,
                comptime.Body
            ),
            RegionStatementSyntax region => BindScopedBlock(
                region,
                ScopeKind.Region,
                region.Identifier,
                region.Source,
                region.Body
            ),
            EmptyStatementSyntax empty => new BoundBlock(empty, [], []),
            _ => new BoundErrorStatement(statement),
        };

    BoundBlock BindBlock(BlockSyntax block) {
        PushScope();

        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
        var gapStart = block.OpenBraceToken.Span.End;
        foreach (var statement in block.Statements) {
            CaptureScopeInGap(block, gapStart, statement.Span.Start);
            statements.Add(BindStatement(statement));
            gapStart = statement.Span.End;
        }

        CaptureScopeInGap(block, gapStart, block.CloseBraceToken.IsMissing ? block.Span.End : block.CloseBraceToken.Span.Start);

        var locals = _locals[^1].Values.OfType<LocalSymbol>().ToImmutableArray();
        PopScope();

        return Record(block, new BoundBlock(block, locals, statements.ToImmutable()));
    }

    /// <summary>
    ///     Binds <c>switch</c>, which dispatches on a value and binds nothing.
    /// </summary>
    BoundStatement BindSwitch(SwitchStatementSyntax statement) {
        var subject = BindExpression(statement.Expression);
        var cases = ImmutableArray.CreateBuilder<BoundSwitchCase>(statement.Cases.Count);

        foreach (var clause in statement.Cases) {
            var values = ImmutableArray.CreateBuilder<BoundExpression>();
            foreach (var value in clause.Values) {
                values.Add(Convert(BindExpression(value, subject.Type), subject.Type, value));
            }

            PushScope();
            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            foreach (var inner in clause.Statements) {
                statements.Add(BindStatement(inner));
            }

            PopScope();

            cases.Add(new(clause, values.ToImmutable(), statements.ToImmutable()));
        }

        return Record(statement, new BoundSwitch(statement, subject, cases.ToImmutable()));
    }

    /// <summary>
    ///     Binds a block that says something about itself: <c>unsafe</c>,
    ///     <c>comptime</c>, or a <c>region</c> that names an allocator.
    /// </summary>
    /// <remarks>
    ///     What those words mean to code generation is Phase 3's business. What
    ///     they mean here is a scope, so the body is checked like any other.
    /// </remarks>
    BoundStatement BindScopedBlock(
        StatementSyntax statement,
        ScopeKind kind,
        SyntaxToken name,
        ExpressionSyntax? sourceSyntax,
        BlockSyntax bodySyntax
    ) {
        var source = sourceSyntax is null ? null : BindExpression(sourceSyntax);

        PushScope();

        LocalSymbol? local = null;
        if (!name.IsNone && name.Text.Length > 0) {
            var regionType = source?.Type ?? (kind == ScopeKind.Region ? Core.Region : Core.Void);
            local = new(name.Text, _containingSymbol, statement, false) { TypeResolver = () => regionType };
            Declare(local.Name, local);
        }

        var body = BindBlock(bodySyntax);
        PopScope();

        return Record(statement, new BoundScopedBlock(statement, kind, local, source, body));
    }

    BoundStatement BindThrow(ThrowStatementSyntax statement) {
        var value = BindExpression(statement.Expression, _declaredThrows);
        _thrownInBody = Union(_thrownInBody, value.Type);

        if (!value.HasError && CanThrowHere() && !(_rethrows && _catchDepth == 0)) {
            CheckErrorFits(value.Type, statement.Expression.Span, "this");
        }

        if (_rethrows && _catchDepth == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_RethrowsThrowsItsOwn,
                statement.ThrowKeyword.Span,
                "a 'rethrows' function may only throw what one of its own "
                + "closure parameters throws"
            );
        } else if (!CanThrowHere()) {
            _diagnostics.Error(
                ErrorCode.ERR_ThrowsNotDeclared,
                statement.ThrowKeyword.Span,
                "this function does not declare 'throws', so it cannot throw"
            );
        }

        return Record(statement, new BoundThrow(statement, value));
    }

    /// <summary>Binds a declaration written inside a body.</summary>
    /// <remarks>
    ///     A local function is declared and bound here. A local type was already
    ///     declared when the body began (see <see cref="HoistLocalTypes" />), and
    ///     its members are bound by the semantic model like any other type's.
    /// </remarks>
    /// <summary>
    ///     Declares the types written in this body before any of it is bound, so
    ///     a local type can be used above the line that declares it, as a local
    ///     function can.
    /// </summary>
    /// <remarks>
    ///     A closure or local function hoists its own when its body is bound, so
    ///     the walk stops at both. It also stops at a local type: whatever that
    ///     declares belongs to its members, not to this body.
    /// </remarks>
    void HoistLocalTypes(SyntaxNode body) {
        if (_containingSymbol is not FunctionSymbol function) {
            return;
        }

        var declared = false;
        foreach (var statement in LocalTypeDeclarations(body)) {
            // Binding the same body again — the language server binds one
            // member at a time, and again to see what is in scope — finds
            // its types declared already.
            if (_compilation.GetDeclaredSymbol(statement.Declaration) is not null) {
                continue;
            }

            _compilation.DeclareLocalType(statement.Declaration, function, _scope);
            declared = true;
        }

        // The body is about to use them, so their signatures and conformances
        // are settled now rather than at the end with everything else. Every
        // file-scope signature was forced before any body was bound, so only
        // these are waiting.
        if (declared) {
            _compilation.TypeBinder.ForceAll();
        }
    }

    static IEnumerable<DeclarationStatementSyntax> LocalTypeDeclarations(SyntaxNode node) {
        foreach (var child in node.ChildNodes()) {
            switch (child) {
                case DeclarationStatementSyntax { Declaration: FunctionDeclarationSyntax }:
                    break;

                case DeclarationStatementSyntax statement:
                    yield return statement;
                    break;

                case ClosureExpressionSyntax or BraceClosureExpressionSyntax:
                    break;

                default:
                    foreach (var nested in LocalTypeDeclarations(child)) {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    BoundStatement BindDeclarationStatement(DeclarationStatementSyntax statement) {
        // A type was hoisted when the body began; its members are bound by
        // the semantic model, like any other type's. Here it is only a mark.
        if (statement.Declaration is not FunctionDeclarationSyntax declaration) {
            return Record(statement, new BoundBlock(statement, [], []));
        }

        var function = _compilation.DeclareLocalFunction(declaration, _containingSymbol, _scope);

        // Declared before the body is bound, so the function can call itself.
        Declare(function.Name, function);

        BoundBlock? body = null;
        var outerNarrowing = EnterNestedBody();
        if (declaration.Body is { } block) {
            body = BindFunctionBody(function, block);
        } else if (declaration.ExpressionBody is { } expression) {
            body = BindExpressionBody(function, expression);
        }

        ExitNestedBody(outerNarrowing);

        return Record(statement, new BoundLocalFunction(statement, function, body));
    }

    BoundStatement BindLocalDeclaration(LocalDeclarationStatementSyntax declaration) {
        var declarator = declaration.Declarator;

        var isMutable = declaration.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword;

        if (declarator.Pattern is not BindingPatternSyntax binding) {
            return BindPatternDeclaration(declaration, declarator, isMutable);
        }

        TypeSymbol? declaredType = declarator.TypeAnnotation is { } annotation
            ? _compilation.TypeBinder.BindType(annotation.Type, _containingSymbol, _scope)
            : null;

        // 'async let x = f()': the initial value is a child task's work, which
        // may await on its own; it is the reads of 'x' that await.
        var isAsyncLet = !declaration.AsyncKeyword.IsNone;
        if (isAsyncLet && !_inAsyncContext) {
            _diagnostics.Error(ErrorCode.ERR_AwaitOutsideAsync, declaration.AsyncKeyword.Span, "'async let' starts a child task, and this is not an async context");
        }

        BoundExpression? initializer = null;
        if (declarator.Initializer is { } initSyntax) {
            if (isAsyncLet) {
                _awaitDepth++;
            }

            initializer = BindExpression(initSyntax.Value, declaredType);
            if (declaredType is not null) {
                initializer = Convert(initializer, declaredType, initSyntax.Value);
            }

            if (isAsyncLet) {
                _awaitDepth--;
            }
        }

        // 'let f = someFunction' names the function rather than calling it.
        if (initializer is BoundFunctionGroup group) {
            initializer = Materialize(group, null);
            _bound[declarator.Initializer!.Value] = initializer;
        }

        var type = declaredType
            ?? initializer?.Type
            ?? ErrorTypeSymbol.Instance;

        if (declaredType is null && initializer is null) {
            _diagnostics.Error(
                ErrorCode.ERR_CannotInferType,
                binding.Identifier.Span,
                $"'{binding.Identifier.Text}' needs either a type annotation or an initialiser"
            );
        }

        var local = new LocalSymbol(binding.Identifier.Text, _containingSymbol, declaration, isMutable) {
            TypeResolver = () => type,
            IsAsyncLet = isAsyncLet,
        };

        Declare(local.Name, local);
        TrackRethrowSource(local, initializer);
        return Record(declaration, new BoundLocalDeclaration(declaration, local, initializer));
    }

    /// <summary>
    ///     <c>let (a, b) = pair</c>, <c>var { x, y } = point</c>: the value is
    ///     taken apart by a pattern, which has to match whatever the value is.
    /// </summary>
    /// <remarks>
    ///     The names inside take their mutability from the declaration's own
    ///     keyword, since a pattern written here has no <c>let</c> or <c>var</c>
    ///     of its own. A pattern that might not match — a literal, a union case
    ///     — belongs in <c>if case</c> or <c>guard case</c>, where there is
    ///     somewhere to go when it does not.
    /// </remarks>
    BoundStatement BindPatternDeclaration(
        LocalDeclarationStatementSyntax declaration,
        VariableDeclaratorSyntax declarator,
        bool isMutable
    ) {
        TypeSymbol? declaredType = declarator.TypeAnnotation is { } annotation
            ? _compilation.TypeBinder.BindType(annotation.Type, _containingSymbol, _scope)
            : null;

        BoundExpression initializer;
        if (declarator.Initializer is { } initSyntax) {
            initializer = BindExpression(initSyntax.Value, declaredType);
            if (declaredType is not null) {
                initializer = Convert(initializer, declaredType, initSyntax.Value);
            }
        } else {
            _diagnostics.Error(
                ErrorCode.ERR_CannotInferType,
                declarator.Pattern.Span,
                "a pattern takes a value apart, so it needs one: add '= ...'"
            );
            initializer = new BoundError(declarator);
        }

        var saved = _declaringMutable;
        _declaringMutable = isMutable;
        var pattern = BindPattern(declarator.Pattern, declaredType ?? initializer.Type);
        _declaringMutable = saved;

        if (!pattern.IsIrrefutable && !ContainsErrorPattern(pattern) && !initializer.HasError) {
            _diagnostics.Error(
                ErrorCode.ERR_RefutablePatternInDeclaration,
                declarator.Pattern.Span,
                "this pattern might not match, so it cannot declare names here; "
                + "use 'if case' or 'guard case'"
            );
        }

        return Record(declaration, new BoundPatternDeclaration(declaration, pattern, initializer));
    }

    static bool ContainsErrorPattern(BoundPattern pattern) =>
        pattern is BoundErrorPattern or BoundRecordPattern { IsIncomplete: true }
        || pattern.Children.OfType<BoundPattern>().Any(ContainsErrorPattern);

    /// <summary>
    ///     The keyword of the declaration a pattern is being bound for, which the
    ///     names inside it inherit; false outside a declaration.
    /// </summary>
    bool _declaringMutable;

    BoundStatement BindIf(IfStatementSyntax statement) {
        PushScope();
        var condition = BindConditionList(statement.Conditions, statement);

        PushNarrowing(NarrowingsWhen(condition, true));
        var consequence = BindStatement(statement.Body);
        PopNarrowing();

        PopScope();

        BoundStatement? alternative = null;
        if (statement.ElseClause is { } elseClause) {
            PushNarrowing(NarrowingsWhen(condition, false));
            alternative = BindStatement(elseClause.Body);
            PopNarrowing();
        }

        return Record(statement, new BoundIf(statement, condition, consequence, alternative));
    }

    BoundStatement BindGuard(GuardStatementSyntax statement) {
        // 'guard c else { ... }' is an 'if' whose bindings outlive it; the
        // else branch must not fall through, which the flow pass will check.
        var condition = BindConditionList(statement.Conditions, statement);

        PushNarrowing(NarrowingsWhen(condition, false));
        var body = BindStatement(statement.Body);
        PopNarrowing();

        // The point of 'guard' is that there is no way past it with the
        // condition false.
        if (!FlowAnalysis.AlwaysExits(body)) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAllPathsReturn,
                statement.Body.Span,
                "the body of a 'guard' must not fall through: return, throw, "
                + "break or continue"
            );
        }

        // What the guard established holds for the rest of the enclosing
        // block, because the only way past it is with the condition true.
        foreach (var (symbol, type) in NarrowingsWhen(condition, true)) {
            if (_narrowed.Count == 0) {
                PushNarrowing([]);
            }

            _narrowed[^1][symbol] = type;
        }

        return Record(statement, new BoundIf(statement, condition, body, null));
    }

    /// <summary>
    ///     What a condition establishes about the bindings it mentions.
    /// </summary>
    /// <remarks>
    ///     Only the shapes docs/03 §3.5 names: <c>x is T</c>, <c>x != none</c> and
    ///     <c>x == none</c>, combined with <c>&amp;&amp;</c>. Anything else
    ///     establishes nothing, which is always safe.
    /// </remarks>
    static Dictionary<Symbol, TypeSymbol> NarrowingsWhen(BoundExpression condition, bool holds) {
        var map = new Dictionary<Symbol, TypeSymbol>();
        Collect(condition, holds, map);
        return map;

        static void Collect(BoundExpression node, bool whenTrue, Dictionary<Symbol, TypeSymbol> into) {
            switch (node) {
                // 'a && b' establishes both when it holds, and neither when
                // it does not: either side could be the one that failed.
                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd } conjunction
                    when whenTrue:
                    Collect(conjunction.Left, true, into);
                    Collect(conjunction.Right, true, into);
                    return;

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalOr } disjunction
                    when !whenTrue:
                    Collect(disjunction.Left, false, into);
                    Collect(disjunction.Right, false, into);
                    return;

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not } negation:
                    Collect(negation.Operand, !whenTrue, into);
                    return;

                case BoundTypeTest test when whenTrue:
                    if (BindingOf(test.Operand) is { } tested) {
                        into[tested] = test.TestedType;
                    }

                    return;

                // 'x != none' unwraps when it holds; 'x == none' when it does
                // not.
                case BoundBinaryOperator {
                    OperatorKind: BinaryOperatorKind.NotEqual or BinaryOperatorKind.Equal,
                } comparison: {
                    var unwrapsWhenTrue =
                        comparison.OperatorKind == BinaryOperatorKind.NotEqual;
                    if (whenTrue != unwrapsWhenTrue) {
                        return;
                    }

                    var binding = IsNone(comparison.Right) ? comparison.Left
                        : IsNone(comparison.Left) ? comparison.Right
                        : null;

                    if (binding is null) {
                        return;
                    }

                    if (binding.Type is not OptionalTypeSymbol optional) {
                        return;
                    }

                    if (BindingOf(binding) is { } symbol) {
                        into[symbol] = optional.UnderlyingType;
                    }

                    return;
                }

                default:
                    return;
            }
        }
    }

    static Symbol? BindingOf(BoundExpression expression) =>
        expression switch {
            BoundLocalAccess local => local.Local,
            BoundParameterAccess parameter => parameter.Parameter,
            BoundConversion conversion => BindingOf(conversion.Operand),
            _ => null,
        };

    static bool IsNone(BoundExpression expression) =>
        expression switch {
            BoundConversion conversion => IsNone(conversion.Operand),
            _ => expression.Syntax is LiteralExpressionSyntax { Token.Kind: SyntaxKind.NoneKeyword, },
        };

    BoundExpression BindConditionList(
        SeparatedSyntaxList<ConditionSyntax> conditions,
        SyntaxNode syntax
    ) {
        BoundExpression? result = null;

        foreach (var condition in conditions) {
            var bound = BindCondition(condition);
            result = result is null
                ? bound
                : new BoundBinaryOperator(syntax, Core.Bool, BinaryOperatorKind.LogicalAnd, result, bound);
        }

        return result ?? new BoundLiteral(syntax, Core.Bool, true);
    }

    BoundExpression BindCondition(ConditionSyntax condition) {
        switch (condition) {
            case ExpressionConditionSyntax expression: {
                var bound = BindExpression(expression.Expression, Core.Bool);
                return Convert(bound, Core.Bool, expression.Expression);
            }

            case OptionalBindingConditionSyntax binding: {
                // 'if let x = e' binds x to the unwrapped value.
                var declarator = binding.Declarator;
                var name = declarator.Pattern is BindingPatternSyntax pattern
                    ? pattern.Identifier.Text
                    : "_";

                var source = declarator.Initializer is { } init
                    ? BindExpression(init.Value)
                    : BindNameAsValue(name, binding);

                var unwrapped = source.Type is OptionalTypeSymbol optional
                    ? optional.UnderlyingType
                    : source.Type;

                if (source.Type is not OptionalTypeSymbol && !source.Type.IsError) {
                    _diagnostics.Error(
                        ErrorCode.ERR_NotOptional,
                        binding.Span,
                        $"'{source.Type.ToDisplayString()}' is not optional, "
                        + "so there is nothing to unwrap"
                    );
                }

                var local = new LocalSymbol(
                    name,
                    _containingSymbol,
                    binding,
                    binding.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword
                ) { TypeResolver = () => unwrapped };

                Declare(name, local);
                TrackRethrowSource(local, source);
                return new BoundConditionBinding(binding, Core.Bool, source, null, local);
            }

            case CasePatternConditionSyntax casePattern: {
                var subject = BindExpression(casePattern.Expression);
                var pattern = BindPattern(casePattern.Pattern, subject.Type);
                return new BoundConditionBinding(casePattern, Core.Bool, subject, pattern, null);
            }

            default:
                return new BoundLiteral(condition, Core.Bool, true);
        }
    }

    BoundStatement BindWhile(WhileStatementSyntax statement) {
        PushScope();
        var condition = BindConditionList(statement.Conditions, statement);

        _loopDepth++;
        var body = BindStatement(statement.Body);
        _loopDepth--;

        PopScope();
        return Record(statement, new BoundWhile(statement, condition, body));
    }

    BoundStatement BindRepeat(RepeatStatementSyntax statement) {
        _loopDepth++;
        var body = BindStatement(statement.Body);
        _loopDepth--;

        var condition = Convert(BindExpression(statement.Condition, Core.Bool), Core.Bool, statement.Condition);

        return Record(statement, new BoundRepeat(statement, body, condition));
    }

    BoundStatement BindForEach(ForStatementSyntax statement) {
        var sequence = BindExpression(statement.Sequence);

        PushScope();

        LocalSymbol? element = null;
        if (statement.Pattern is BindingPatternSyntax binding) {
            var elementType = ElementTypeOf(sequence.Type, statement.Sequence);
            element = new(binding.Identifier.Text, _containingSymbol, binding, false) {
                TypeResolver = () => elementType
            };
            Declare(element.Name, element);
        }

        var filter = statement.WhereClause is { } where
            ? Convert(BindExpression(where.Condition, Core.Bool), Core.Bool, where.Condition)
            : null;

        _loopDepth++;
        var body = BindStatement(statement.Body);
        _loopDepth--;

        PopScope();
        return Record(
            statement,
            new BoundForEach(statement, element, sequence, filter, body)
        );
    }

    /// <summary>What a <c>for … in</c> yields, for the sequences understood so far.</summary>
    TypeSymbol ElementTypeOf(TypeSymbol sequence, SyntaxNode syntax) {
        if (sequence.IsError) {
            return ErrorTypeSymbol.Instance;
        }

        if (sequence is ArrayTypeSymbol array) {
            return array.ElementType;
        }

        if (sequence is NamedTypeSymbol named && Core.IsRange(named)) {
            // Only an integer range enumerates; a range of anything else is a
            // test of membership, not a sequence.
            if (Core.IsInteger(named.TypeArguments[0])) {
                return named.TypeArguments[0];
            }

            _diagnostics.Error(
                ErrorCode.ERR_NotIterable,
                syntax.Span,
                $"'{named.ToDisplayString()}' cannot be iterated: "
                + "only a range of integers enumerates"
            );
            return ErrorTypeSymbol.Instance;
        }

        // User sequences need the Iterable protocol, which arrives with the
        // standard library. A bare integer is not one: 'for i in 0..<n'.
        _diagnostics.Error(
            ErrorCode.ERR_NotIterable,
            syntax.Span,
            $"'{sequence.ToDisplayString()}' cannot be iterated"
        );
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>The function whose body is being bound, if any.</summary>
    FunctionSymbol? EnclosingFunction() {
        for (var symbol = _containingSymbol; symbol is not null; symbol = symbol.ContainingSymbol) {
            if (symbol is FunctionSymbol function) {
                return function;
            }
        }

        return null;
    }

    BoundStatement BindReturn(ReturnStatementSyntax statement) {
        // An initialiser's result is the value it is building, so 'return'
        // there means "stop", never "stop with this".
        if (statement.Expression is { } returned
            && EnclosingFunction() is { FunctionKind: FunctionKind.Initializer or FunctionKind.Deinitializer }) {
            _diagnostics.Error(
                ErrorCode.ERR_ReturnValueNotAllowed,
                returned.Span,
                "an initialiser returns nothing, so 'return' takes no value"
            );
            return Record(statement, new BoundReturn(statement, BindExpression(returned)));
        }

        if (statement.Expression is null) {
            if (!_returnType.Equals(Core.Void) && !_returnType.IsError) {
                _diagnostics.Error(
                    ErrorCode.ERR_ReturnValueRequired,
                    statement.Span,
                    $"this function returns '{_returnType.ToDisplayString()}', "
                    + "so 'return' needs a value"
                );
            }

            return Record(statement, new BoundReturn(statement, null));
        }

        if (_inferringReturn) {
            var inferred = BindExpression(statement.Expression);
            _returnType = inferred.Type;
            _inferringReturn = false;
            return Record(statement, new BoundReturn(statement, inferred));
        }

        var value = BindExpression(statement.Expression, _returnType);

        if (_returnType.Equals(Core.Void)) {
            _diagnostics.Error(
                ErrorCode.ERR_ReturnValueNotAllowed,
                statement.Expression.Span,
                "this function returns nothing, so 'return' takes no value"
            );
        } else {
            CheckOpaqueReturn(value, statement.Expression);
            value = Convert(value, _returnType, statement.Expression);
        }

        return Record(statement, new BoundReturn(statement, value));
    }

    /// <summary>
    ///     The concrete type each function returning <c>some P</c> has returned
    ///     so far.
    /// </summary>
    readonly Dictionary<Symbol, (TypeSymbol Type, SyntaxNode Where)> _opaqueReturns = [];

    /// <summary>
    ///     A <c>some P</c> result is one concrete type, fixed by the definition
    ///     and hidden from the caller (docs/03 §3.2), so every return must give
    ///     the same one.
    /// </summary>
    void CheckOpaqueReturn(BoundExpression value, SyntaxNode syntax) {
        if (_returnType is not OpaqueTypeSymbol opaque
            || value.HasError
            || value.Type.IsError
            || value.Type.Equals(Core.Never)) {
            return;
        }

        if (!_opaqueReturns.TryGetValue(_containingSymbol, out var first)) {
            _opaqueReturns[_containingSymbol] = (value.Type, syntax);
            return;
        }

        if (first.Type.Equals(value.Type)) {
            return;
        }

        _diagnostics.Error(
            ErrorCode.ERR_OpaqueReturnTypesDiffer,
            syntax.Span,
            $"'{opaque.ToDisplayString()}' stands for one concrete type, but this returns "
            + $"'{value.Type.ToDisplayString()}' and an earlier return gave "
            + $"'{first.Type.ToDisplayString()}'; return 'any {opaque.Constraint.ToDisplayString()}' "
            + "to allow both"
        );
    }

    /// <summary>Labels of the loops this statement sits inside, innermost last.</summary>
    readonly List<string> _loopLabels = [];

    BoundStatement BindLabeled(LabeledStatementSyntax statement) {
        var label = statement.Label.Text;
        var labelled = label.Length > 0;

        if (labelled) {
            _loopLabels.Add(label);
        }

        var bound = BindStatement(statement.Statement);
        if (labelled) {
            _loopLabels.RemoveAt(_loopLabels.Count - 1);
        }

        return bound;
    }

    BoundStatement BindBreak(BreakStatementSyntax statement) {
        CheckJump(statement.Span, statement.Label, "break");

        return Record(statement, new BoundBreak(statement, statement.Label.IsNone ? null : statement.Label.Text));
    }

    BoundStatement BindContinue(ContinueStatementSyntax statement) {
        CheckJump(statement.Span, statement.Label, "continue");

        return Record(statement, new BoundContinue(statement, statement.Label.IsNone ? null : statement.Label.Text));
    }

    /// <summary>A jump needs a loop, and a labelled one needs that label.</summary>
    void CheckJump(TextSpan span, SyntaxToken label, string keyword) {
        if (_loopDepth == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_NoEnclosingLoop,
                span,
                $"'{keyword}' is only allowed inside a loop"
            );
            return;
        }

        if (label.IsNone || label.Text.Length == 0) {
            return;
        }

        if (_loopLabels.Contains(label.Text)) {
            return;
        }

        _diagnostics.Error(
            ErrorCode.ERR_NameNotFound,
            label.Span,
            $"no enclosing loop is labelled '{label.Text}'"
        );
    }

    // -----------------------------------------------------------------------
    // Expressions
    // -----------------------------------------------------------------------

    public BoundExpression BindExpression(
        ExpressionSyntax expression,
        TypeSymbol? expectedType = null
    ) {
        CaptureScope(expression);

        if (++_steps > _compilation.BindOptions.StepBudget) {
            if (!_budgetSpent) {
                _budgetSpent = true;
                _diagnostics.Error(
                    ErrorCode.ERR_ExpressionTooComplex,
                    expression.Span,
                    "this expression is too complex to type-check in one piece: "
                    + "split it up, or annotate a binding inside it"
                );
            }

            return new BoundError(expression);
        }

        var bound = BindExpressionCore(expression, expectedType);
        _bound[expression] = bound;
        return bound;
    }

    BoundExpression BindExpressionCore(
        ExpressionSyntax expression,
        TypeSymbol? expectedType
    ) =>
        expression switch {
            LiteralExpressionSyntax literal => BindLiteral(literal, expectedType),
            InterpolatedStringExpressionSyntax interpolated => BindInterpolatedString(interpolated),
            IdentifierNameSyntax name => BindName(name, name.Identifier.Text),
            GenericNameSyntax name => BindGenericName(name),
            SelfExpressionSyntax self => BindSelf(self),
            SuperExpressionSyntax super => BindSuper(super),
            ParenthesizedExpressionSyntax parenthesized =>
                BindExpression(parenthesized.Expression, expectedType),
            MemberAccessExpressionSyntax member => BindMemberAccess(member),
            ImplicitMemberAccessExpressionSyntax implicitMember =>
                BindImplicitMemberAccess(implicitMember, expectedType),
            InvocationExpressionSyntax invocation => AfterCall(BindInvocation(invocation, expectedType)),
            ElementAccessExpressionSyntax element => BindElementAccess(element),
            BinaryExpressionSyntax binary => BindBinary(binary),
            PrefixUnaryExpressionSyntax unary => BindPrefixUnary(unary),
            PostfixUnaryExpressionSyntax unary => BindPostfixUnary(unary),
            AssignmentExpressionSyntax assignment => BindAssignment(assignment),
            ConditionalExpressionSyntax conditional => BindConditionalExpression(conditional, expectedType),
            TupleExpressionSyntax tuple => BindTuple(tuple, expectedType),
            ArrayExpressionSyntax array => BindArray(array, expectedType),
            RecordExpressionSyntax record => BindRecordExpression(record),
            CastExpressionSyntax cast => BindCast(cast),
            TypeTestExpressionSyntax test => BindTypeTest(test),
            MatchExpressionSyntax match => BindMatch(match, expectedType),
            RangeExpressionSyntax range => BindRange(range, expectedType),
            DictionaryExpressionSyntax dictionary => BindDictionary(dictionary, expectedType),
            BuiltinExpressionSyntax builtin => BindBuiltin(builtin),

            // A compound type written where an expression goes, which only
            // '.self' follows: the member access turns it into the metatype.
            TypeExpressionSyntax written => Record(
                written,
                new BoundTypeExpression(
                    written,
                    _compilation.TypeBinder.BindType(written.Type, _containingSymbol, _scope)
                )
            ),

            ClosureExpressionSyntax closure => BindClosure(
                closure,
                closure.CaptureList,
                closure.Signature.Parameters,
                closure.Signature.ReturnType,
                closure.Body,
                expectedType,
                !closure.Signature.OpenParenToken.IsNone
            ),
            BraceClosureExpressionSyntax brace => BindBraceClosure(brace, expectedType),
            ImplicitParameterExpressionSyntax implicitParameter =>
                BindImplicitParameter(implicitParameter),
            TryExpressionSyntax tryExpression => BindTry(tryExpression, expectedType),
            _ => Unsupported(expression),
        };

    BoundExpression Unsupported(ExpressionSyntax expression) {
        _diagnostics.Error(
            ErrorCode.ERR_NotSupportedYet,
            expression.Span,
            $"'{expression.Kind}' is not bound yet"
        );
        return new BoundError(expression);
    }

    BoundExpression BindLiteral(
        LiteralExpressionSyntax literal,
        TypeSymbol? expectedType
    ) {
        var token = literal.Token;

        var type = token.Kind switch {
            SyntaxKind.IntegerLiteralToken => Core.Int,
            SyntaxKind.FloatLiteralToken => Core.F64,
            SyntaxKind.CharLiteralToken => Core.Char,
            SyntaxKind.StringLiteralToken => Core.String,
            SyntaxKind.MultilineStringLiteralToken => Core.String,
            SyntaxKind.RawStringLiteralToken => Core.String,
            SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => Core.Bool,
            SyntaxKind.NoneKeyword => expectedType as OptionalTypeSymbol
                ?? (TypeSymbol)new OptionalTypeSymbol(ErrorTypeSymbol.Instance),
            _ => ErrorTypeSymbol.Instance,
        };

        // A numeric literal takes the type the context asked for.
        if (expectedType is not null
            && token.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken) {
            var target = expectedType is OptionalTypeSymbol optional
                ? optional.UnderlyingType
                : expectedType;

            if (_conversions.Classify(type, target, true)
                is ConversionKind.LiteralTyping) {
                type = target;
            }
        }

        return new BoundLiteral(literal, type, token.Value);
    }

    BoundExpression BindInterpolatedString(InterpolatedStringExpressionSyntax syntax) {
        var parts = ImmutableArray.CreateBuilder<BoundExpression>();

        foreach (var content in syntax.Contents) {
            if (content is InterpolationSyntax hole) {
                parts.Add(BindExpression(hole.Expression));
            }
        }

        return new BoundInterpolatedString(syntax, Core.String, parts.ToImmutable());
    }

    BoundExpression BindSelf(SelfExpressionSyntax syntax) {
        if (EnclosingType() is { } type) {
            return new BoundSelfAccess(syntax, SelfTypeOf(type), SelfIsMutable(type));
        }

        _diagnostics.Error(
            ErrorCode.ERR_SelfOutsideType,
            syntax.Span,
            "'self' is only available inside a type"
        );
        return new BoundError(syntax);
    }

    BoundExpression BindSuper(SuperExpressionSyntax syntax) {
        if (EnclosingType() is { } type) {
            foreach (var baseType in type.BaseTypes) {
                if (baseType is NamedTypeSymbol { TypeKind: TypeKind.Class } baseClass) {
                    return new BoundSuperAccess(syntax, baseClass);
                }
            }

            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Span,
                $"'{type.Name}' has no base class, so there is no 'super'"
            );
            return new BoundError(syntax);
        }

        _diagnostics.Error(
            ErrorCode.ERR_SelfOutsideType,
            syntax.Span,
            "'super' is only available inside a type"
        );
        return new BoundError(syntax);
    }

    /// <summary>
    ///     Types named <paramref name="name" /> declared in the bodies around this
    ///     one: those inside the nearest enclosing type, or with
    ///     <paramref name="beyondType" />, those outside it.
    /// </summary>
    List<Symbol> LocalTypes(string name, bool beyondType) {
        var pastType = false;
        for (var owner = _containingSymbol; owner is not null; owner = owner.ContainingSymbol) {
            if (owner is NamedTypeSymbol) {
                if (!beyondType) {
                    break;
                }

                pastType = true;
                continue;
            }

            if (beyondType && !pastType) {
                continue;
            }

            if (owner is not FunctionSymbol function || function.LocalTypes.Count == 0) {
                continue;
            }

            var found = function.GetLocalTypes(name).ToList();
            if (found.Count > 0) {
                return found;
            }
        }

        return [];
    }

    /// <summary>
    ///     <c>Outcome&lt;Int, AppError&gt;</c> in an expression: a generic type,
    ///     or an alias of one, with its arguments applied, so that a case or a
    ///     static member reached through it is of the constructed type.
    /// </summary>
    BoundExpression BindGenericName(GenericNameSyntax syntax) {
        var name = syntax.Identifier.Text;

        if (LookupLocal(name) is null
            && _compilation.TypeBinder.Lookup(name, _containingSymbol, _scope, syntax.Span)
                is NamedTypeSymbol or AliasSymbol) {
            var type = _compilation.TypeBinder.BindGenericName(syntax, _containingSymbol, _scope);
            return type.IsError
                ? new BoundError(syntax)
                : new BoundTypeExpression(syntax, type);
        }

        return BindName(syntax, name);
    }

    BoundExpression BindName(ExpressionSyntax syntax, string name) => BindNameAsValue(name, syntax);

    /// <summary>An 'async let': its child task's result, so reading it awaits.</summary>
    BoundExpression AwaitedLocal(SyntaxNode syntax, LocalSymbol local) {
        MarkAsync(syntax.Span, $"'{local.Name}', an 'async let',");
        return new BoundLocalAccess(syntax, local, NarrowedType(local));
    }

    /// <summary>
    ///     A call into an actor from outside it: the actor runs one call at a
    ///     time, so its caller may have to wait its turn (docs/02 §2.7).
    /// </summary>
    bool CrossesIntoActor(FunctionSymbol callee) {
        if (callee is not { IsStatic: false, FunctionKind: FunctionKind.Ordinary }
            || (callee.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : callee.ContainingSymbol) is not NamedTypeSymbol { TypeKind: TypeKind.Actor } actor) {
            return false;
        }

        for (var here = _containingSymbol; here is not null; here = here.ContainingSymbol) {
            if (ReferenceEquals(here, actor.OriginalDefinition) || here is ExtensionSymbol { ExtendedType: var extended } && ReferenceEquals(extended, actor.OriginalDefinition)) {
                return false;
            }
        }

        return true;
    }

    BoundExpression BindNameAsValue(string name, SyntaxNode syntax) {
        if (LookupLocal(name) is { } local) {
            return local switch {
                LocalSymbol { IsAsyncLet: true } asyncLet => AwaitedLocal(syntax, asyncLet),
                LocalSymbol localSymbol =>
                    new BoundLocalAccess(syntax, localSymbol, NarrowedType(localSymbol)),
                ParameterSymbol parameter =>
                    new BoundParameterAccess(syntax, parameter, NarrowedType(parameter)),
                FunctionSymbol function => new BoundFunctionGroup(syntax, null, [function]),
                _ => new BoundError(syntax),
            };
        }

        // A type declared in this body, or in a closure's enclosing body,
        // before the enclosing type's members: the nearer declaration wins.
        if (LocalTypes(name, false) is { Count: > 0 } nearer) {
            return BindMemberGroup(syntax, null, nearer, null);
        }

        // Members of the enclosing type, reached implicitly through self —
        // including the ones it inherits, which is the whole point of a base
        // class.
        if (EnclosingType() is { } enclosing
            && LookupMember(SelfTypeOf(enclosing), name) is { } inherited) {
            return BindMemberGroup(syntax, null, inherited.Members, inherited.Owner, inherited.Substitution);
        }

        // Then the bodies the enclosing type itself was declared in.
        if (LocalTypes(name, true) is { Count: > 0 } farther) {
            return BindMemberGroup(syntax, null, farther, null);
        }

        var moduleMembers = _scope.Module.GetMembers(name).ToList();
        if (moduleMembers.Count > 0) {
            return BindMemberGroup(syntax, null, moduleMembers, null);
        }

        if (_scope.ImportedNames.TryGetValue(name, out var imported)) {
            return BindMemberGroup(syntax, null, [imported], null);
        }

        foreach (var module in _scope.Imports) {
            var candidates = module.GetMembers(name)
                .Where(static m => m.DeclaredAccessibility == Accessibility.Public)
                .ToList();
            if (candidates.Count > 0) {
                return BindMemberGroup(syntax, null, candidates, null);
            }
        }

        var coreMembers = Core.Module.GetMembers(name).ToList();
        if (coreMembers.Count > 0) {
            return BindMemberGroup(syntax, null, coreMembers, null);
        }

        // A type parameter can be named, to reach a static member through
        // what it is constrained to: 'T.decode(from: reader)'.
        if (_compilation.TypeBinder.Lookup(name, _containingSymbol, _scope, syntax.Span)
            is TypeParameterSymbol named) {
            return new BoundTypeExpression(syntax, named);
        }

        _diagnostics.Error(
            ErrorCode.ERR_NameNotFound,
            syntax.Span,
            $"no symbol named '{name}' is in scope"
        );
        return new BoundError(syntax);
    }

    BoundExpression BindMemberGroup(
        SyntaxNode syntax,
        BoundExpression? receiver,
        IReadOnlyList<Symbol> members,
        NamedTypeSymbol? containingType,
        TypeSubstitution? substitution = null
    ) {
        substitution ??= TypeSubstitution.Empty;

        var functions = members.OfType<FunctionSymbol>().ToImmutableArray();
        if (!functions.IsEmpty) {
            return new BoundFunctionGroup(syntax, receiver, functions) { Substitution = substitution };
        }

        var member = members[0];
        switch (member) {
            case FieldSymbol field:
                if (receiver is null && !field.IsStatic && containingType is not null) {
                    // An array extension's member, reached from another's: self is this one's array.
                    var self = containingType.ExtendedArray is not null && EnclosingType() is { } enclosing ? SelfTypeOf(enclosing) : containingType;
                    receiver = new BoundSelfAccess(syntax, self, SelfIsMutable(containingType));
                }

                // 'task.value' waits for the task.
                if (field.IsAsyncGetter) {
                    MarkAsync(syntax.Span, $"reading '{field.Name}'");
                }

                return new BoundFieldAccess(syntax, receiver, field, substitution.Apply(field.Type));

            case NamedTypeSymbol type:
                return new BoundTypeExpression(syntax, type);

            case AliasSymbol alias:
                return new BoundTypeExpression(syntax, alias.AliasedType);

            case UnionCaseSymbol unionCase: {
                var union = (NamedTypeSymbol)unionCase.ContainingSymbol!;
                return new BoundUnionCaseGroup(
                    syntax,
                    containingType is not null
                    && containingType.OriginalDefinition.Equals(union)
                        ? containingType
                        : union,
                    unionCase
                );
            }

            case EnumCaseSymbol enumCase:
                return new BoundLiteral(syntax, (NamedTypeSymbol)enumCase.ContainingSymbol!, enumCase.Name);

            default:
                _diagnostics.Error(
                    ErrorCode.ERR_NotAValue,
                    syntax.Span,
                    $"'{member.Name}' cannot be used as a value"
                );
                return new BoundError(syntax);
        }
    }

    /// <summary>True for an expression that names storage: something a reference can refer to.</summary>
    static bool IsPlace(BoundExpression expression) =>
        expression switch {
            BoundLocalAccess or BoundParameterAccess or BoundSelfAccess => true,
            BoundFieldAccess { Field.IsComputed: false, Field.IsStatic: false, Receiver: { } receiver } =>
                receiver.Type.TypeKind == TypeKind.Struct && IsPlace(receiver),
            BoundTupleElementAccess element => IsPlace(element.Receiver),
            BoundConversion { ConversionKind: ConversionKind.Dereference } => true,
            BoundConversion { ConversionKind: ConversionKind.Identity } conversion => IsPlace(conversion.Operand),
            _ => false,
        };

    BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax) {
        var receiver = BindExpression(syntax.Expression);

        // A member of a reference is the member of what it refers to.
        if (receiver.Type is ReferenceTypeSymbol reference) {
            receiver = new BoundConversion(syntax.Expression, reference.ReferencedType, receiver, ConversionKind.Dereference);
        }
        var name = syntax.Name switch {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => string.Empty,
        };

        if (receiver.HasError) {
            return new BoundError(syntax, receiver);
        }

        // 'T.self' names the type itself.
        if (receiver is BoundTypeExpression typeExpression) {
            if (name == "self") {
                return new BoundTypeExpression(syntax, typeExpression.Type);
            }

            // A type parameter reaches the static members of its constraints,
            // so the lookup is the same one a value uses.
            if (LookupMember(typeExpression.ReferencedType, name) is { } onType) {
                var statics = onType.Members.Where(IsStaticMember).ToList();

                if (statics.Count > 0) {
                    return CheckAccessible(statics[0], syntax.Name)
                        ? BindMemberGroup(syntax, null, statics, onType.Owner, onType.Substitution)
                        : new BoundError(syntax, receiver);
                }

                _diagnostics.Error(
                    ErrorCode.ERR_MemberAccessMismatch,
                    syntax.Name.Span,
                    $"'{name}' is an instance member of "
                    + $"'{typeExpression.ReferencedType.ToDisplayString()}', "
                    + "so it needs a value to read it from"
                );
                return new BoundError(syntax, receiver);
            }

            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Name.Span,
                $"'{typeExpression.ReferencedType.ToDisplayString()}' has no member named '{name}'"
            );
            return new BoundError(syntax, receiver);
        }

        if (receiver is BoundSuperAccess super) {
            var baseMembers = super.BaseType.GetMembers(name).ToList();
            if (baseMembers.Count > 0) {
                return BindMemberGroup(
                    syntax,
                    receiver,
                    baseMembers,
                    super.BaseType,
                    TypeSubstitution.For(super.BaseType)
                );
            }

            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Name.Span,
                $"'{super.BaseType.ToDisplayString()}' has no member named '{name}'"
            );
            return new BoundError(syntax, receiver);
        }

        var receiverType = receiver.Type;
        var optionalChain = false;

        if (receiverType is OptionalTypeSymbol optional) {
            // Optionality is in the type system, so reaching through it is
            // written down: 'p?.x', not 'p.x'. docs/03 §3.7.
            if (syntax.OperatorToken.Kind != SyntaxKind.QuestionDotToken) {
                _diagnostics.Error(
                    ErrorCode.ERR_NotOptional,
                    syntax.OperatorToken.Span,
                    $"'{receiverType.ToDisplayString()}' may hold nothing, so reach "
                    + "through it with '?.' or unwrap it first"
                );
                return new BoundError(syntax, receiver);
            }

            receiverType = optional.UnderlyingType;
            optionalChain = true;
        }

        if (receiverType is TupleTypeSymbol tuple) {
            return BindTupleElementAccess(syntax, receiver, tuple, name);
        }

        _unmetArrayRequirement = null;
        if (LookupMember(receiverType, name) is not { } found) {
            if (_unmetArrayRequirement is { } unmet) {
                _diagnostics.Error(
                    ErrorCode.ERR_DoesNotConform,
                    syntax.Name.Span,
                    $"'{receiverType.ToDisplayString()}' has '{name}' only where {unmet}"
                );
                return new BoundError(syntax, receiver);
            }

            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Name.Span,
                $"'{receiverType.ToDisplayString()}' has no member named '{name}'"
            );
            return new BoundError(syntax, receiver);
        }

        // A subscript is reached by indexing, never by its name, which is a
        // keyword anyway.
        var instanceMembers = found.Members
            .Where(static m => m is not FunctionSymbol { FunctionKind: FunctionKind.Subscript })
            .Where(static m => !IsStaticMember(m))
            .ToList();

        if (instanceMembers.Count == 0
            && found.Members.All(static m => m is FunctionSymbol { FunctionKind: FunctionKind.Subscript })) {
            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Name.Span,
                $"'{found.Owner.ToDisplayString()}' has no member named '{name}'; "
                + "a subscript is reached by indexing"
            );
            return new BoundError(syntax, receiver);
        }

        if (instanceMembers.Count == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_MemberAccessMismatch,
                syntax.Name.Span,
                $"'{name}' is a static member of '{found.Owner.ToDisplayString()}', "
                + "so it is reached through the type rather than a value"
            );
            return new BoundError(syntax, receiver);
        }

        if (!CheckAccessible(instanceMembers[0], syntax.Name)) {
            return new BoundError(syntax, receiver);
        }

        var member = BindMemberGroup(syntax, receiver, instanceMembers, found.Owner, found.Substitution);

        if (!optionalChain) {
            return member;
        }

        // Reaching through an optional gives an optional: '?.' can produce
        // nothing, and the type says so.
        return member switch {
            BoundFieldAccess field => new BoundFieldAccess(
                syntax,
                field.Receiver,
                field.Field,
                new OptionalTypeSymbol(field.Type)
            ),

            // A call through the chain is optional too, which the group
            // carries until the call is bound.
            BoundFunctionGroup group => new BoundFunctionGroup(syntax, group.Receiver, group.Candidates) {
                Substitution = group.Substitution, IsOptionalChain = true
            },

            _ => member,
        };
    }

    /// <summary>
    ///     Reports a member the code here is not allowed to see.
    /// </summary>
    /// <remarks>
    ///     Per spec/SYNTAX.md §2: public everywhere, package across the
    ///     workspace, internal within the module, private within the declaration
    ///     that holds it — and, since an extension's members belong to the type
    ///     they extend, within that type's extensions in the same module too.
    /// </remarks>
    bool CheckAccessible(Symbol member, SyntaxNode syntax) {
        if (IsAccessible(member)) {
            return true;
        }

        _diagnostics.Error(
            ErrorCode.ERR_NotAccessible,
            syntax.Span,
            $"'{member.Name}' is {Describe(member.DeclaredAccessibility)} to "
            + $"'{member.ContainingSymbol?.Name ?? "its module"}'"
        );
        return false;
    }

    static string Describe(Accessibility accessibility) =>
        accessibility switch {
            Accessibility.Private => "private",
            Accessibility.Internal => "internal",
            Accessibility.Package => "visible only",
            _ => "visible",
        };

    static FunctionSymbol? RequirementAndDefault(FunctionSymbol first, FunctionSymbol second) {
        static Symbol? Owner(FunctionSymbol f) => f.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : f.ContainingSymbol;
        static bool InExtension(FunctionSymbol f) => f.ContainingSymbol is ExtensionSymbol;

        if (Owner(first) is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol
            || !ReferenceEquals(Owner(second), protocol)
            || first.Parameters.Length != second.Parameters.Length
            || !first.Parameters.Zip(second.Parameters).All(static p => p.First.Type.Equals(p.Second.Type))
            || InExtension(first) == InExtension(second)) {
            return null;
        }

        return InExtension(first) ? second : first;
    }

    bool IsAccessible(Symbol member) => IsAccessible(member, member.DeclaredAccessibility);

    bool IsAccessible(Symbol member, Accessibility accessibility) {
        switch (accessibility) {
            case Accessibility.Public or Accessibility.Package:
                return true;

            case Accessibility.Internal:
                return ReferenceEquals(member.ContainingModule, _scope.Module)
                    || member.ContainingModule is null;

            default: {
                // Private: somewhere inside the declaration that holds it.
                var owner = member.ContainingSymbol;
                if (owner is null) {
                    return true;
                }

                for (var here = _containingSymbol; here is not null; here = here.ContainingSymbol) {
                    if (ReferenceEquals(here, owner)) {
                        return true;
                    }

                    // An extension's members are the extended type's, and a
                    // private member is visible to them in the same module.
                    if (here is ExtensionSymbol extension
                        && ReferenceEquals(extension.ExtendedType, owner)) {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    ///     True for a member reached through the type rather than a value.
    /// </summary>
    /// <remarks>
    ///     Initialisers, nested types, aliases and the cases of a union or enum
    ///     are all named through the type, so they count as static here even
    ///     though none of them is written <c>static</c>.
    /// </remarks>
    static bool IsStaticMember(Symbol member) =>
        member switch {
            FunctionSymbol function =>
                function.IsStatic || function.FunctionKind is FunctionKind.Initializer,
            FieldSymbol field => field.IsStatic,
            NamedTypeSymbol or AliasSymbol or UnionCaseSymbol or EnumCaseSymbol => true,
            _ => false,
        };

    /// <summary>
    ///     Set when an array member was not found only because the element
    ///     does not meet its extension's constraint: what the constraint is.
    /// </summary>
    string? _unmetArrayRequirement;

    /// <summary>What a member lookup found, and what it has to be read through.</summary>
    private readonly record struct MemberLookup(
        IReadOnlyList<Symbol> Members,
        NamedTypeSymbol Owner,
        TypeSubstitution Substitution);

    /// <summary>
    ///     Finds a named member on a type, reaching through everything that can
    ///     stand in for one.
    /// </summary>
    /// <remarks>
    ///     A class inherits its base's members; a type parameter has the members
    ///     of everything it is constrained to; <c>some P</c> and <c>any P</c> have
    ///     P's; and a composition has all of its parts'. Each step composes its
    ///     substitution, so a member of <c>Base&lt;Int&gt;</c> reached through
    ///     <c>Derived : Base&lt;Int&gt;</c> still reports Int.
    /// </remarks>
    MemberLookup? LookupMember(TypeSymbol type, string name) {
        // After 'where T == Int' a T has Int's members.
        type = _conversions.Equivalence.Canonical(type);
        var visited = new HashSet<TypeSymbol>();
        return LookupMemberCore(type, name, TypeSubstitution.Empty, visited, type);
    }

    MemberLookup? LookupMemberCore(
        TypeSymbol type,
        string name,
        TypeSubstitution outer,
        HashSet<TypeSymbol> visited,
        TypeSymbol selfType
    ) {
        if (!visited.Add(type)) {
            return null;
        }

        switch (type) {
            case NamedTypeSymbol named: {
                var substitution = Combine(outer, TypeSubstitution.For(named));

                // A protocol's members speak of 'Self'; through a conformer,
                // a constrained parameter or an existential, Self is whatever
                // the lookup started from.
                if (named.SelfTypeParameter is { } self) {
                    var map = new Dictionary<TypeParameterSymbol, TypeSymbol> { [self] = selfType };

                    // The conformer also says what the associated types are.
                    // Through a generic parameter they are read through it:
                    // 'b.first()' for B: Bag is a B.Item, not any bag's Item.
                    if (AssociatedTypes.IsAbstract(selfType)
                        && !AssociatedTypes.IsProtocolSelf(selfType)) {
                        foreach (var associated in AssociatedTypes.All(named)) {
                            map[associated] = AssociatedTypes.Member(selfType, associated);
                        }
                    } else if (Unwrap(selfType) is NamedTypeSymbol conformer) {
                        // 'alias Item = T' names the conformer's own
                        // parameter, so Tagged<String> binds Item to String.
                        var own = TypeSubstitution.For(conformer);

                        foreach (var associated in named.AssociatedTypes) {
                            if (ConformanceChecker.ResolveAssociated(conformer, associated)
                                is { } bound) {
                                map[associated] = own.Apply(bound);
                            }
                        }
                    }

                    substitution = Combine(substitution, new(map));
                }

                var members = named.GetMembers(name).ToList();
                if (members.Count > 0) {
                    return new MemberLookup(members, named, substitution);
                }

                foreach (var baseType in named.BaseTypes) {
                    if (LookupMemberCore(baseType, name, substitution, visited, selfType)
                        is { } inherited) {
                        return inherited;
                    }
                }

                return null;
            }

            // An array's members are its extensions' (Compilation.ArrayExtensions.cs).
            case ArrayTypeSymbol array: {
                if (_compilation.LookupArrayMember(array, name, _conversions.Equivalence) is not { } found) {
                    return null;
                }

                if (found.Owner is null) {
                    _unmetArrayRequirement = found.Unmet;
                    return null;
                }

                return new MemberLookup(found.Members, found.Owner, Combine(outer, found.Substitution));
            }

            case TypeParameterSymbol or DependentMemberTypeSymbol:
                foreach (var constraint in AssociatedTypes.ConstraintsOf(type, _conversions.Equivalence)) {
                    if (LookupMemberCore(constraint, name, outer, visited, selfType) is { } found) {
                        return found;
                    }
                }

                return null;

            case OpaqueTypeSymbol opaque:
                return LookupMemberCore(opaque.Constraint, name, outer, visited, selfType);

            case ExistentialTypeSymbol existential:
                return LookupMemberCore(existential.Constraint, name, outer, visited, selfType);

            case CompositionTypeSymbol composition:
                foreach (var part in composition.Parts) {
                    if (LookupMemberCore(part, name, outer, visited, selfType) is { } found) {
                        return found;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    BoundExpression BindTupleElementAccess(
        MemberAccessExpressionSyntax syntax,
        BoundExpression receiver,
        TupleTypeSymbol tuple,
        string name
    ) {
        var index = -1;

        for (var i = 0; i < tuple.ElementNames.Length; i++) {
            if (string.Equals(tuple.ElementNames[i], name, StringComparison.Ordinal)) {
                index = i;
                break;
            }
        }

        if (index < 0 && int.TryParse(name, out var position)) {
            index = position;
        }

        if (index < 0 || index >= tuple.ElementTypes.Length) {
            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Name.Span,
                $"'{tuple.ToDisplayString()}' has no element named '{name}'"
            );
            return new BoundError(syntax, receiver);
        }

        return new BoundTupleElementAccess(syntax, tuple.ElementTypes[index], receiver, index);
    }

    BoundExpression BindImplicitMemberAccess(
        ImplicitMemberAccessExpressionSyntax syntax,
        TypeSymbol? expectedType
    ) {
        var name = syntax.Name switch {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => string.Empty,
        };

        // The context may ask for an optional, and '.Some' is rarely what is
        // meant; the case belongs to what it wraps.
        var wanted = expectedType is OptionalTypeSymbol optional
            ? optional.UnderlyingType
            : expectedType;

        if (wanted is NamedTypeSymbol named) {
            var members = named.GetMembers(name).ToList();
            if (members.Count > 0) {
                return BindMemberGroup(syntax, null, members, named);
            }

            // The context does name a type; it just has no such member.
            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Name.Span,
                $"'{named.ToDisplayString()}' has no member named '{name}'"
            );
            return new BoundError(syntax);
        }

        _diagnostics.Error(
            ErrorCode.ERR_NoContextualType,
            syntax.Span,
            $"cannot infer what '.{name}' refers to here"
        );
        return new BoundError(syntax);
    }

    BoundExpression BindInvocation(
        InvocationExpressionSyntax syntax,
        TypeSymbol? expectedType = null
    ) {
        // A closure argument is left unbound: its parameter types come from
        // the signature that is chosen, which is not known yet.
        var argumentSyntax = ImmutableArray.CreateBuilder<ExpressionSyntax>();
        var labelBuilder = ImmutableArray.CreateBuilder<string?>();

        if (syntax.ArgumentList is { } list) {
            foreach (var argument in list.Arguments) {
                argumentSyntax.Add(argument.Expression);
                labelBuilder.Add(argument.NameToken.IsNone ? null : argument.NameToken.Text);
            }
        }

        if (syntax.TrailingClosure is { } trailing) {
            argumentSyntax.Add(trailing);
            labelBuilder.Add(null);
        }

        var syntaxes = argumentSyntax.ToImmutable();
        var labels = labelBuilder.ToImmutable();

        var arguments = syntaxes
            .Select(e => IsClosure(e) ? new BoundUnboundClosure(e) : BindExpression(e))
            .ToImmutableArray();

        var callee = BindCallee(syntax.Expression, expectedType);

        switch (callee) {
            case BoundFunctionGroup group:
                return BindCallToGroup(syntax, group, arguments, labels, syntaxes, expectedType);

            case BoundUnionCaseGroup unionCase:
                return BindUnionCaseConstruction(syntax, unionCase, arguments, labels, expectedType);

            case BoundExpression value when value.Type is FunctionTypeSymbol functionType:
                return BindCallToValue(syntax, value, functionType, arguments, syntaxes);

            // 'f?()' calls through an optional and gives an optional back.
            case BoundExpression value
                when value.Type is OptionalTypeSymbol { UnderlyingType: FunctionTypeSymbol inner }: {
                if (syntax.QuestionToken.IsNone) {
                    _diagnostics.Error(
                        ErrorCode.ERR_NotCallable,
                        syntax.Expression.Span,
                        $"'{value.Type.ToDisplayString()}' may hold nothing, so call it "
                        + "with '?()' or unwrap it first"
                    );
                    return new BoundError(syntax, [value, ..arguments]);
                }

                var call = BindCallToValue(syntax, value, inner, arguments, syntaxes);
                return call.HasError
                    ? call
                    : new BoundConversion(
                        syntax,
                        new OptionalTypeSymbol(call.Type),
                        call,
                        ConversionKind.OptionalWrap
                    );
            }

            case BoundTypeExpression typeExpression
                when typeExpression.ReferencedType is NamedTypeSymbol namedType: {
                // 'U64(x)': a numeric conversion, as 'x as U64'; 'U64(1)': a literal of that type.
                if (Core.IsNumeric(namedType)
                    && arguments is [var converted]
                    && labels is [null]
                    && Core.IsNumeric(converted.Type)) {
                    return converted is BoundLiteral literalArgument && _conversions.HasImplicitConversion(literalArgument.Type, namedType, true)
                        ? new BoundConversion(syntax, namedType, literalArgument, ConversionKind.LiteralTyping)
                        : new BoundConversion(syntax, namedType, converted, ConversionKind.Explicit);
                }

                var initializers = namedType.GetMembers("init")
                    .OfType<FunctionSymbol>()
                    .ToImmutableArray();

                if (initializers.IsEmpty) {
                    _diagnostics.Error(
                        ErrorCode.ERR_NotAValue,
                        syntax.Expression.Span,
                        $"'{namedType.ToDisplayString()}' declares no initialiser, "
                        + "so it cannot be used as a value this way"
                    );
                    return new BoundError(syntax, arguments.ToArray());
                }

                // An initialiser written 'private' makes the type's values its
                // own to make: 'Task' is made by 'spawn' alone.
                var accessible = initializers.Where(i => i.DeclaringSyntax.IsEmpty || IsAccessible(i)).ToImmutableArray();
                if (accessible.IsEmpty) {
                    CheckAccessible(initializers[0], syntax.Expression);
                    return new BoundError(syntax, arguments.ToArray());
                }

                initializers = accessible;

                if (namedType.IsAbstract) {
                    _diagnostics.Error(
                        ErrorCode.ERR_CannotInstantiate,
                        syntax.Expression.Span,
                        $"'{namedType.Name}' is abstract, so it has no values of its own"
                    );
                    return new BoundError(syntax, arguments.ToArray());
                }

                // A generic type's initialiser sees the type's parameters as the
                // type was written — 'Box<String>(…)' — or as its arguments say
                // they must be — 'Box("z")'.
                var constructed = namedType;
                if (namedType is { TypeArguments.IsEmpty: true, TypeParameters.IsEmpty: false }) {
                    var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
                    if (Expand(expectedType) is NamedTypeSymbol { IsConstructed: true } wanted
                        && ReferenceEquals(wanted.OriginalDefinition, namedType.OriginalDefinition)) {
                        for (var i = 0; i < namedType.TypeParameters.Length; i++) {
                            map[namedType.TypeParameters[i]] = wanted.TypeArguments[i];
                        }
                    }

                    foreach (var initializer in initializers.Where(i => i.Parameters.Length == arguments.Length)) {
                        for (var i = 0; i < arguments.Length; i++) {
                            if (arguments[i] is not BoundUnboundClosure) {
                                TypeSubstitution.Unify(initializer.Parameters[i].Type, arguments[i].Type, namedType.TypeParameters, map);
                            }
                        }
                    }

                    if (map.Count == namedType.TypeParameters.Length) {
                        constructed = namedType.Construct([..namedType.TypeParameters.Select(p => map[p])]);
                    }
                }

                var group = new BoundFunctionGroup(syntax, null, initializers) {
                    Substitution = constructed.TypeArguments.IsEmpty ? null : TypeSubstitution.For(constructed),
                };
                return BindCallToGroup(syntax, group, arguments, labels, syntaxes, expectedType);
            }

            default:
                if (!callee.HasError) {
                    _diagnostics.Error(
                        ErrorCode.ERR_NotCallable,
                        syntax.Expression.Span,
                        $"'{callee.Type.ToDisplayString()}' is not callable"
                    );
                }

                return new BoundError(syntax, [callee, ..arguments]);
        }
    }

    /// <summary>
    ///     Builds a union value, checking the payload against what the case
    ///     declared.
    /// </summary>
    BoundExpression BindUnionCaseConstruction(
        InvocationExpressionSyntax syntax,
        BoundUnionCaseGroup group,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        TypeSymbol? expectedType = null
    ) {
        var unionCase = group.UnionCase;
        var union = group.Type as NamedTypeSymbol;
        var substitution = union is not null
            ? TypeSubstitution.For(union)
            : TypeSubstitution.Empty;

        // 'Tree.Node(value: 1, ...)' names the definition, so the arguments
        // say what its parameters are, exactly as they do for a call.
        if (union is { TypeArguments.IsEmpty: true, TypeParameters.IsEmpty: false }
            && arguments.Length == unionCase.Payload.Length) {
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();

            // What the context wants settles the parameters the payload does
            // not mention: 'Outcome.Ok(value: 1)' where 'Outcome<Int, AppError>'
            // is expected has nothing else to say what 'E' is.
            var wanted = expectedType is OptionalTypeSymbol optional
                ? optional.UnderlyingType
                : expectedType;

            if (Expand(wanted) is NamedTypeSymbol { IsConstructed: true } expected
                && ReferenceEquals(expected.OriginalDefinition, union.OriginalDefinition)) {
                for (var i = 0; i < union.TypeParameters.Length; i++) {
                    map[union.TypeParameters[i]] = expected.TypeArguments[i];
                }
            }

            for (var i = 0; i < arguments.Length; i++) {
                TypeSubstitution.Unify(unionCase.Payload[i].Type, arguments[i].Type, union.TypeParameters, map);
            }

            if (map.Count == union.TypeParameters.Length) {
                substitution = new(map);
                union = union.Construct([..union.TypeParameters.Select(substitution.Apply)]);
            }
        }

        if (arguments.Length != unionCase.Payload.Length) {
            _diagnostics.Error(
                ErrorCode.ERR_WrongPayloadCount,
                syntax.Span,
                $"'{unionCase.Name}' takes {unionCase.Payload.Length} value(s), "
                + $"not {arguments.Length}"
            );
            return new BoundError(syntax, arguments.ToArray());
        }

        var converted = ImmutableArray.CreateBuilder<BoundExpression>(arguments.Length);
        for (var i = 0; i < arguments.Length; i++) {
            var field = unionCase.Payload[i];

            if (i < labels.Length
                && labels[i] is { } label
                && !string.Equals(label, field.Name, StringComparison.Ordinal)) {
                _diagnostics.Error(
                    ErrorCode.ERR_NameNotFound,
                    arguments[i].Syntax.Span,
                    $"'{unionCase.Name}' has no field named '{label}'"
                );
            }

            converted.Add(Convert(arguments[i], substitution.Apply(field.Type), arguments[i].Syntax));
        }

        return new BoundUnionCaseConstruction(syntax, union ?? group.Type, unionCase, converted.ToImmutable());
    }

    /// <summary>
    ///     True for an argument whose type the signature has to supply.
    /// </summary>
    /// <remarks>
    ///     A closure's parameter types and a leading dot's owner both come from
    ///     the parameter they are passed to, so neither can be bound until the
    ///     overload is chosen.
    /// </remarks>
    static bool IsClosure(ExpressionSyntax expression) =>
        expression switch {
            ClosureExpressionSyntax or BraceClosureExpressionSyntax => true,
            ImplicitMemberAccessExpressionSyntax => true,

            // '.Full(value: 1)' as an argument: the dot still needs the parameter.
            InvocationExpressionSyntax { Expression: ImplicitMemberAccessExpressionSyntax } => true,

            // 'none' is no particular optional until the parameter says which:
            // 'tripled(none)' with tripled taking Int?.
            LiteralExpressionSyntax { Token.Kind: SyntaxKind.NoneKeyword } => true,

            // Nor is '[]' any particular array: 'run(name, [])' takes the
            // element type and the kind from the parameter.
            ArrayExpressionSyntax { Elements.Count: 0 } => true,

            ParenthesizedExpressionSyntax parenthesized => IsClosure(parenthesized.Expression),
            _ => false,
        };

    /// <summary>Calls a value of function type, which has no overloads to pick between.</summary>
    BoundExpression BindCallToValue(
        InvocationExpressionSyntax syntax,
        BoundExpression callee,
        FunctionTypeSymbol type,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> syntaxes
    ) {
        if (arguments.Length != type.ParameterTypes.Length) {
            _diagnostics.Error(
                ErrorCode.ERR_NoMatchingOverload,
                syntax.Span,
                $"this takes {type.ParameterTypes.Length} argument(s), "
                + $"not {arguments.Length}"
            );
            return new BoundError(syntax, [callee, ..arguments]);
        }

        var bound = arguments.ToBuilder();
        var converted = ImmutableArray.CreateBuilder<BoundExpression>(arguments.Length);
        for (var i = 0; i < arguments.Length; i++) {
            if (bound[i] is BoundUnboundClosure) {
                bound[i] = BindExpression(syntaxes[i], type.ParameterTypes[i]);
            }

            converted.Add(Convert(bound[i], type.ParameterTypes[i], bound[i].Syntax));
        }

        // Before conversion, so that a closure that cannot throw is still
        // seen as one that cannot: converting it to the parameter's throwing
        // type would hide exactly the thing 'rethrows' is asking about.
        arguments = bound.ToImmutable();

        if (type.IsRethrows) {
            if (RethrownBy(arguments) is { } rethrown) {
                MarkThrows(
                    syntax.Span,
                    "this call",
                    rethrown,
                    IsRethrowSource(callee) || PassesOwnParameter(arguments)
                );
            }
        } else if (type.ThrownType is { } thrown) {
            MarkThrows(syntax.Span, "this call", thrown, IsRethrowSource(callee));
        }

        if (type.IsAsync) {
            MarkAsync(syntax.Span, "this call");
        }

        return new BoundIndirectCall(syntax, type.ReturnType, callee, converted.ToImmutable());
    }

    /// <summary>
    ///     Binds what is being called. A leading-dot callee, <c>.Ok(value: 1)</c>,
    ///     names a member of the type the call's context wants, so it is the one
    ///     callee that needs to see that type.
    /// </summary>
    BoundExpression BindCallee(ExpressionSyntax syntax, TypeSymbol? expectedType) =>
        syntax is ImplicitMemberAccessExpressionSyntax implicitMember
            ? Record(implicitMember, BindImplicitMemberAccess(implicitMember, expectedType))
            : BindExpression(syntax);

    BoundExpression BindCallToGroup(
        InvocationExpressionSyntax syntax,
        BoundFunctionGroup group,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        ImmutableArray<ExpressionSyntax> syntaxes,
        TypeSymbol? expectedType = null
    ) {
        var outer = group.Substitution ?? TypeSubstitution.Empty;
        var resolved = ResolveOverload(group.Candidates, arguments, labels, syntax, outer);

        if (resolved is null) {
            // Bind the closures anyway, so the body is still checked and the
            // semantic model has something to say about it.
            foreach (var argumentSyntax in syntaxes) {
                if (IsClosure(argumentSyntax)) {
                    BindExpression(argumentSyntax);
                }
            }

            return new BoundError(syntax, arguments.ToArray());
        }

        // The receiver's substitution, plus whatever the arguments imply for
        // the function's own type parameters.
        var substitution = Combine(outer, Infer(resolved, arguments, labels, outer));
        var slots = Align(resolved, labels, arguments.Length);

        // Now that a signature is chosen, the closures have parameter types.
        if (arguments.Any(static a => a is BoundUnboundClosure)) {
            var saved = _openTypeParameters;
            _openTypeParameters = [
                ..resolved.TypeParameters.Where(p => substitution.Lookup(p) is null)
            ];

            var rebound = arguments.ToBuilder();
            for (var i = 0; i < rebound.Count; i++) {
                if (rebound[i] is not BoundUnboundClosure) {
                    continue;
                }

                var expected = slots[i] < resolved.Parameters.Length
                    ? substitution.Apply(resolved.Parameters[slots[i]].Type)
                    : null;
                rebound[i] = BindExpression(syntaxes[i], expected);
            }

            _openTypeParameters = saved;
            arguments = rebound.ToImmutable();

            // A closure's return type can settle a type parameter nothing else
            // mentions, so inference runs again with them bound.
            substitution = Combine(outer, Infer(resolved, arguments, labels, outer));
        }

        var converted = ImmutableArray.CreateBuilder<BoundExpression>(arguments.Length);
        for (var i = 0; i < arguments.Length; i++) {
            var parameter = slots[i] < resolved.Parameters.Length
                ? resolved.Parameters[slots[i]]
                : resolved.Parameters.Length > 0 && resolved.Parameters[^1].IsVariadic
                    ? resolved.Parameters[^1]
                    : null;

            if (parameter is null) {
                converted.Add(arguments[i]);
                continue;
            }

            var wanted = substitution.Apply(parameter.Type);
            if (parameter.IsVariadic && wanted is ArrayTypeSymbol variadic) {
                wanted = variadic.ElementType;
            }

            converted.Add(Convert(arguments[i], wanted, arguments[i].Syntax));
        }

        // The parameters the call leaves off take their defaults, so every
        // call passes every argument, in the parameters' order, and nothing
        // past the binder need know defaults exist. Arguments only ever skip
        // forward, so they are still evaluated as written.
        var passed = ImmutableArray.CreateBuilder<BoundExpression>(resolved.Parameters.Length);
        var next = 0;
        for (var p = 0; p < resolved.Parameters.Length; p++) {
            if (resolved.Parameters[p].IsVariadic) {
                break;
            }

            if (next < converted.Count && slots[next] == p) {
                passed.Add(converted[next++]);
            } else if (DefaultArgument(resolved.Parameters[p], substitution) is { } value) {
                passed.Add(value);
            }
        }

        while (next < converted.Count) {
            passed.Add(converted[next++]);
        }

        converted = passed;

        // A parameter that appears only in the result is settled by what the
        // context asked for: 'let p: Point? = decodeOne(bytes)'.
        if (expectedType is not null
            && resolved.TypeParameters.Any(p => substitution.Lookup(p) is null)) {
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
            foreach (var parameter in resolved.TypeParameters) {
                if (substitution.Lookup(parameter) is { } bound) {
                    map[parameter] = bound;
                }
            }

            TypeSubstitution.Unify(resolved.ReturnType, expectedType, resolved.TypeParameters, map);

            substitution = Combine(outer, new(map));
        }

        if (!resolved.TypeParameters.IsEmpty) {
            foreach (var typeParameter in resolved.TypeParameters) {
                if (substitution.Lookup(typeParameter) is not null) {
                    continue;
                }

                _diagnostics.Error(
                    ErrorCode.ERR_CannotInferTypeArgument,
                    syntax.Span,
                    $"cannot infer '{typeParameter.Name}' from these arguments"
                );
            }
        }

        if (resolved.IsMutating
            && group.Receiver is { } mutated
            && !mutated.Type.IsReferenceType
            && !mutated.IsAssignable) {
            _diagnostics.Error(
                ErrorCode.ERR_NotMutable,
                syntax.Span,
                $"'{resolved.Name}' is mutating, so it needs a value it can change"
            );
        }

        if (resolved.IsRethrows) {
            // Not what the signature says it may throw, but what these
            // arguments actually can: 'withLock(m) { 1 }' does not throw.
            if (RethrownBy(arguments) is { } rethrown) {
                MarkThrows(
                    syntax.Span,
                    $"'{resolved.Name}'",
                    substitution.Apply(rethrown),
                    PassesOwnParameter(arguments)
                );
            }
        } else if (resolved.ThrownType is { } thrown) {
            MarkThrows(syntax.Span, $"'{resolved.Name}'", substitution.Apply(thrown));
        }

        if (resolved.IsAsync) {
            MarkAsync(syntax.Span, $"'{resolved.Name}'");
        } else if (CrossesIntoActor(resolved)) {
            MarkAsync(syntax.Span, $"'{resolved.Name}', isolated to its actor,");
        }

        var result = substitution.Apply(resolved.ReturnType);

        // An initialiser of a generic type gives the type its arguments made:
        // 'Box<String>(…)' is a Box<String>, not the definition.
        if (resolved.FunctionKind == FunctionKind.Initializer
            && result is NamedTypeSymbol { TypeArguments.IsEmpty: true, TypeParameters.IsEmpty: false } definition
            && definition.TypeParameters.All(p => substitution.Lookup(p) is not null)) {
            result = definition.Construct([..definition.TypeParameters.Select(p => substitution.Lookup(p)!)]);
        }

        ReportUnmetDerivation(resolved, group.Receiver?.Type, syntax);

        var call = new BoundCall(syntax, group.Receiver, resolved, converted.ToImmutable(), result) {
            Substitution = substitution,
        };

        return group.IsOptionalChain
            ? new BoundConversion(syntax, new OptionalTypeSymbol(result), call, ConversionKind.OptionalWrap)
            : call;
    }

    /// <summary>
    ///     A left-off parameter's default value, bound afresh for this call in
    ///     the file and the function that declare it — the default names what
    ///     is visible there, not at the call. Its diagnostics were reported
    ///     once, with the declaration, so they are not reported again here.
    /// </summary>
    BoundExpression? DefaultArgument(ParameterSymbol parameter, TypeSubstitution substitution) {
        if (parameter.DeclaringSyntax is not [ParameterSyntax { DefaultValue: { } clause } syntax]
            || _compilation.FileScopeOf(syntax) is not { } scope
            || parameter.ContainingSymbol is not { } container) {
            return null;
        }

        return new Binder(_compilation, scope, container).BindInitializer(clause.Value, substitution.Apply(parameter.Type));
    }

    /// <summary>Works out a generic function's type arguments from the call.</summary>
    static TypeSubstitution Infer(
        FunctionSymbol function,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        TypeSubstitution outer
    ) {
        if (function.TypeParameters.IsEmpty) {
            return TypeSubstitution.Empty;
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        var slots = Align(function, labels, arguments.Length);
        for (var i = 0; i < arguments.Length; i++) {
            if (slots[i] >= function.Parameters.Length) {
                break;
            }

            var wanted = outer.Apply(function.Parameters[slots[i]].Type);

            TypeSubstitution.Unify(wanted, AdaptForInference(wanted, arguments[i]), function.TypeParameters, map);
        }

        return new(map);
    }

    /// <summary>
    ///     An argument's type as inference should see it.
    /// </summary>
    /// <remarks>
    ///     An array literal's storage kind comes from the context, so it must not
    ///     be the thing that stops a parameter being inferred.
    /// </remarks>
    static TypeSymbol AdaptForInference(TypeSymbol parameter, BoundExpression argument) =>
        parameter is ArrayTypeSymbol wanted
        && argument is BoundArray { Type: ArrayTypeSymbol actual }
        && actual.ArrayKind != wanted.ArrayKind
            ? new ArrayTypeSymbol(actual.ElementType, wanted.ArrayKind)
            : argument.Type;

    static TypeSubstitution Combine(TypeSubstitution outer, TypeSubstitution inner) {
        if (outer.IsEmpty) {
            return inner;
        }

        if (inner.IsEmpty) {
            return outer;
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        foreach (var parameter in inner.Parameters) {
            map[parameter] = inner.Apply(parameter);
        }

        foreach (var parameter in outer.Parameters) {
            map[parameter] = outer.Apply(parameter);
        }

        return new(map);
    }

    /// <summary>
    ///     Picks the overload that fits, preferring the one needing the fewest
    ///     conversions.
    /// </summary>
    FunctionSymbol? ResolveOverload(
        ImmutableArray<FunctionSymbol> candidates,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        SyntaxNode syntax,
        TypeSubstitution outer
    ) {
        FunctionSymbol? best = null;
        var bestCost = int.MaxValue;
        var ambiguous = false;
        var rejected = new List<(FunctionSymbol Candidate, string Reason)>();
        var tied = new List<FunctionSymbol>();

        foreach (var candidate in candidates) {
            var cost = Score(candidate, arguments, labels, outer);
            if (cost is null) {
                rejected.Add((candidate, _rejection ?? "it does not fit"));
                continue;
            }

            if (cost < bestCost) {
                best = candidate;
                bestCost = cost.Value;
                ambiguous = false;
                tied.Clear();
                continue;
            }

            if (cost != bestCost || best is null) {
                continue;
            }

            // A concrete overload beats one that had to infer a parameter:
            // 'both(aPoint)' means the one written for Point.
            if (candidate.TypeParameters.Length < best.TypeParameters.Length) {
                best = candidate;
                ambiguous = false;
                tied.Clear();
                continue;
            }

            if (candidate.TypeParameters.Length > best.TypeParameters.Length) {
                continue;
            }

            // A protocol's requirement and the default an extension of it
            // gives are one method: the requirement, which dispatch resolves
            // to the conforming type's own, or to the default.
            if (RequirementAndDefault(best, candidate) is { } requirement) {
                best = requirement;
                continue;
            }

            ambiguous = true;
            tied.Add(candidate);
        }

        // Each rejected candidate keeps its reason, so the error can say what
        // was wrong rather than only that something was (docs/04 §1.2).
        if (best is null) {
            var name = candidates.IsEmpty ? "?" : candidates[0].Name;
            var message = candidates.Length == 1 && rejected.Count == 1
                ? $"'{name}' cannot be called with these arguments: {rejected[0].Reason}"
                : $"no overload of '{name}' takes these arguments"
                + string.Concat(rejected.Take(3).Select(static r => $"; '{Signature(r.Candidate)}': {r.Reason}"))
                + (rejected.Count > 3 ? $"; and {rejected.Count - 3} more" : string.Empty);

            _diagnostics.Error(ErrorCode.ERR_NoMatchingOverload, syntax.Span, message);
            return null;
        }

        if (ambiguous) {
            var contenders = new[] { best }.Concat(tied).Distinct().Take(3).Select(static c => $"'{Signature(c)}'");
            _diagnostics.Error(
                ErrorCode.ERR_AmbiguousOverload,
                syntax.Span,
                $"the call to '{best.Name}' is ambiguous between {string.Join(" and ", contenders)}"
            );
        }

        return best;
    }

    /// <summary>Why the last candidate <see cref="Score" /> looked at was rejected.</summary>
    string? _rejection;

    int? Reject(string reason) {
        _rejection = reason;
        return null;
    }

    static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>A candidate as the source would declare it: <c>insert(_: T, at: Int)</c>.</summary>
    static string Signature(FunctionSymbol function) {
        var parameters = function.Parameters.Select(static p =>
            $"{p.Label ?? p.Name}: {p.Type.ToDisplayString()}"
        );
        return $"{function.Name}({string.Join(", ", parameters)})";
    }

    int? Score(
        FunctionSymbol candidate,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        TypeSubstitution outer
    ) {
        _rejection = null;
        var substitution = Combine(outer, Infer(candidate, arguments, labels, outer));
        var parameters = candidate.Parameters;

        var required = parameters.Count(static p => p is { HasDefaultValue: false, IsVariadic: false });
        var variadic = parameters.Any(static p => p.IsVariadic);
        var slots = Align(candidate, labels, arguments.Length);

        if (arguments.Length < required) {
            return Reject($"it needs {Plural(required, "argument")}, and this passes {arguments.Length}");
        }

        if (!variadic && arguments.Length > 0 && slots[^1] >= parameters.Length) {
            return Reject($"it takes {Plural(parameters.Length, "argument")}, and this passes {arguments.Length}");
        }

        // A parameter a label passed over must have a default to take.
        for (var p = 0; p < parameters.Length; p++) {
            if (parameters[p] is { HasDefaultValue: false, IsVariadic: false } && Array.IndexOf(slots, p) < 0) {
                return Reject($"'{parameters[p].Name}' has no default, and this passes nothing for it");
            }
        }

        var cost = 0;
        for (var i = 0; i < arguments.Length; i++) {
            // Everything from a variadic parameter on is one of its elements.
            var parameter = slots[i] < parameters.Length
                ? parameters[slots[i]]
                : parameters.Length > 0 && parameters[^1].IsVariadic
                    ? parameters[^1]
                    : null;

            if (parameter is null) {
                break;
            }

            // A written label must match the parameter's.
            if (i < labels.Length && labels[i] is { } label) {
                var expected = parameter.Label ?? parameter.Name;
                if (!string.Equals(expected, label, StringComparison.Ordinal)) {
                    return Reject($"argument {i + 1} is labelled '{label}', and the parameter is '{expected}'");
                }
            } else if (i < labels.Length
                       && parameter.Label is { } requiredLabel
                       && requiredLabel != "_"
                       && !string.Equals(requiredLabel, parameter.Name, StringComparison.Ordinal)) {
                // The parameter declares a distinct external label, so one is
                // required at the call site.
                return Reject($"argument {i + 1} needs the label '{requiredLabel}:'");
            }

            var wanted = substitution.Apply(parameter.Type);
            if (parameter.IsVariadic && wanted is ArrayTypeSymbol elements) {
                wanted = elements.ElementType;
            }

            var conversion = _conversions.Classify(arguments[i].Type, wanted, IsLiteral(arguments[i]));

            if (conversion is null) {
                return Reject(
                    $"argument {i + 1} is '{arguments[i].Type.ToDisplayString()}', "
                    + $"and '{parameter.Name}' takes '{wanted.ToDisplayString()}'"
                );
            }

            cost += Conversions.Cost(conversion.Value);
        }

        // A candidate whose constraints the inferred arguments do not meet is
        // not a candidate: 'needsNamed(aPoint)' has no overload, rather than
        // an overload that then complains.
        foreach (var typeParameter in candidate.TypeParameters) {
            if (substitution.Lookup(typeParameter) is not { } argument) {
                continue;
            }

            foreach (var constraint in typeParameter.Constraints) {
                if (!Conversions.Satisfies(argument, substitution.Apply(constraint), _conversions.Equivalence)) {
                    return Reject(
                        $"'{typeParameter.Name}' would be '{argument.ToDisplayString()}', "
                        + $"which is not '{substitution.Apply(constraint).ToDisplayString()}'"
                    );
                }
            }
        }

        // 'where A.Item == B.Item' holds only when the arguments make the two
        // the same, as the caller's own requirements understand sameness.
        foreach (var (left, right) in candidate.SameTypeRequirements) {
            var leftType = substitution.Apply(left);
            var rightType = substitution.Apply(right);

            if (ContainsUninferred(leftType, candidate) || ContainsUninferred(rightType, candidate)) {
                continue;
            }

            if (!_conversions.Equivalence.AreSame(leftType, rightType)) {
                return Reject(
                    $"it requires '{left.ToDisplayString()} == {right.ToDisplayString()}', "
                    + $"and here they are '{leftType.ToDisplayString()}' and '{rightType.ToDisplayString()}'"
                );
            }
        }

        // 'where C.Item: Comparable' holds only when what C became says so.
        foreach (var (subject, constraint) in candidate.ConformanceRequirements) {
            var subjectType = substitution.Apply(subject);
            if (ContainsUninferred(subjectType, candidate)) {
                continue;
            }

            if (!Conversions.Satisfies(subjectType, substitution.Apply(constraint), _conversions.Equivalence)) {
                return Reject(
                    $"it requires '{subject.ToDisplayString()}: {constraint.ToDisplayString()}', "
                    + $"and '{subjectType.ToDisplayString()}' is not"
                );
            }
        }

        return cost;
    }

    /// <summary>
    ///     The parameter each argument is passed to, in order. Arguments fill
    ///     the parameters left to right, but a labelled one may pass over
    ///     parameters with defaults to reach the one its label names:
    ///     <c>connect(host: h, timeout: t)</c> leaves <c>port</c> its default.
    ///     Only a default written in a declaration is passed over: a memberwise
    ///     initialiser's defaults are the fields', which lowering fills in
    ///     only at the end of the list.
    ///     An index past the last parameter is an argument with nowhere to go;
    ///     from a variadic parameter on, every argument is one of its elements.
    /// </summary>
    static int[] Align(FunctionSymbol function, ImmutableArray<string?> labels, int count) {
        var parameters = function.Parameters;
        var slots = new int[count];
        var next = 0;
        for (var i = 0; i < count; i++) {
            if (i < labels.Length && labels[i] is { } label) {
                var reach = next;
                while (reach < parameters.Length
                       && parameters[reach].DeclaringSyntax is [ParameterSyntax { DefaultValue: not null, EllipsisToken.IsNone: true }]
                       && !Names(parameters[reach], label)) {
                    reach++;
                }

                if (reach < parameters.Length && Names(parameters[reach], label)) {
                    next = reach;
                }
            }

            slots[i] = next < parameters.Length && parameters[next].IsVariadic ? next : next++;
        }

        return slots;

        static bool Names(ParameterSymbol parameter, string label) =>
            string.Equals(parameter.Label ?? parameter.Name, label, StringComparison.Ordinal);
    }

    /// <summary>True when a type still names one of the candidate's own, uninferred, parameters.</summary>
    static bool ContainsUninferred(TypeSymbol type, FunctionSymbol candidate) =>
        type switch {
            TypeParameterSymbol parameter => candidate.TypeParameters.Contains(parameter),
            DependentMemberTypeSymbol member => ContainsUninferred(member.BaseType, candidate),
            NamedTypeSymbol named => named.TypeArguments.Any(a => ContainsUninferred(a, candidate)),
            OptionalTypeSymbol optional => ContainsUninferred(optional.UnderlyingType, candidate),
            ArrayTypeSymbol array => ContainsUninferred(array.ElementType, candidate),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(e => ContainsUninferred(e, candidate)),
            FunctionTypeSymbol function =>
                function.ParameterTypes.Any(p => ContainsUninferred(p, candidate))
                || ContainsUninferred(function.ReturnType, candidate),
            _ => false,
        };

    /// <summary>
    ///     True for an expression whose type the context may still decide.
    /// </summary>
    /// <remarks>
    ///     A numeric literal has no type until something asks for one, and an
    ///     array literal has no storage kind; both bend to fit.
    /// </remarks>
    static bool IsLiteral(BoundExpression expression) => expression is BoundLiteral or BoundArray or BoundDictionary;

    BoundExpression BindElementAccess(ElementAccessExpressionSyntax syntax) {
        var receiver = BindExpression(syntax.Expression);
        var indices = syntax.ArgumentList.Arguments
            .Select(a => BindExpression(a.Expression))
            .ToImmutableArray();

        if (receiver.HasError) {
            return new BoundError(syntax, [receiver, ..indices]);
        }

        // 'd?[k]' indexes through an optional and gives an optional back.
        if (receiver.Type is OptionalTypeSymbol optional) {
            if (syntax.QuestionToken.IsNone) {
                _diagnostics.Error(
                    ErrorCode.ERR_NotOptional,
                    syntax.Span,
                    $"'{receiver.Type.ToDisplayString()}' may hold nothing, so index it "
                    + "with '?[...]' or unwrap it first"
                );
                return new BoundError(syntax, [receiver, ..indices]);
            }

            var unwrapped = new BoundConversion(
                syntax.Expression,
                optional.UnderlyingType,
                receiver,
                ConversionKind.Explicit
            );

            var inner = BindElementAccessCore(syntax, unwrapped, indices);

            return inner.HasError || inner.Type is OptionalTypeSymbol
                ? inner
                : new BoundConversion(
                    syntax,
                    new OptionalTypeSymbol(inner.Type),
                    inner,
                    ConversionKind.OptionalWrap
                );
        }

        return BindElementAccessCore(syntax, receiver, indices);
    }

    BoundExpression BindElementAccessCore(
        ElementAccessExpressionSyntax syntax,
        BoundExpression receiver,
        ImmutableArray<BoundExpression> indices
    ) {
        if (receiver.Type is ArrayTypeSymbol array) {
            indices = CheckIndices(indices, Core.Int, syntax);
            return new BoundIndexAccess(syntax, array.ElementType, receiver, indices);
        }

        if (receiver.Type is NamedTypeSymbol named
            && named.OriginalDefinition.Equals(Core.Dictionary)
            && named.TypeArguments.Length == 2) {
            indices = CheckIndices(indices, named.TypeArguments[0], syntax);
            return new BoundIndexAccess(syntax, new OptionalTypeSymbol(named.TypeArguments[1]), receiver, indices);
        }

        // A type may declare its own indexing with 'subscript'.
        if (LookupMember(receiver.Type, "subscript") is { } found) {
            var group = new BoundFunctionGroup(syntax, receiver, [..found.Members.OfType<FunctionSymbol>()]) {
                Substitution = found.Substitution
            };

            if (!group.Candidates.IsEmpty) {
                var resolved = ResolveOverload(group.Candidates, indices, [], syntax, found.Substitution);

                if (resolved is null) {
                    return new BoundError(syntax, [receiver, ..indices]);
                }

                var converted = ImmutableArray.CreateBuilder<BoundExpression>(indices.Length);
                for (var i = 0; i < indices.Length && i < resolved.Parameters.Length; i++) {
                    converted.Add(
                        Convert(
                            indices[i],
                            found.Substitution.Apply(resolved.Parameters[i].Type),
                            indices[i].Syntax
                        )
                    );
                }

                return new BoundIndexAccess(
                    syntax,
                    found.Substitution.Apply(resolved.ReturnType),
                    receiver,
                    converted.ToImmutable()
                );
            }
        }

        _diagnostics.Error(
            ErrorCode.ERR_NotIndexable,
            syntax.Span,
            $"'{receiver.Type.ToDisplayString()}' cannot be indexed"
        );
        return new BoundError(syntax, [receiver, ..indices]);
    }

    /// <summary>An index has a type too, and it is the one the receiver wants.</summary>
    ImmutableArray<BoundExpression> CheckIndices(
        ImmutableArray<BoundExpression> indices,
        TypeSymbol expected,
        SyntaxNode syntax
    ) {
        if (indices.Length != 1) {
            _diagnostics.Error(
                ErrorCode.ERR_NotIndexable,
                syntax.Span,
                $"this takes one index, not {indices.Length}"
            );
            return indices;
        }

        return [Convert(indices[0], expected, indices[0].Syntax)];
    }

    BoundExpression BindTuple(TupleExpressionSyntax syntax, TypeSymbol? expectedType) {
        // '(1, 2)' where '(I64, I64)' is wanted: each element is read against
        // its own expected type, as an array literal's are, so a literal can
        // take the type the context asks for.
        var expected = Expand(expectedType) is TupleTypeSymbol wanted
            && wanted.ElementTypes.Length == syntax.Elements.Count
                ? wanted.ElementTypes
                : default;

        var elements = syntax.Elements
            .Select((e, i) => {
                    if (expected.IsDefault) {
                        return BindExpression(e.Expression);
                    }

                    var element = BindExpression(e.Expression, expected[i]);
                    return _conversions.HasImplicitConversion(element.Type, expected[i], IsLiteral(element))
                        ? Convert(element, expected[i], e.Expression)
                        : element;
                }
            )
            .ToImmutableArray();

        var names = syntax.Elements
            .Select(static e => e.NameToken.IsNone ? null : e.NameToken.Text)
            .ToImmutableArray();

        var type = new TupleTypeSymbol([..elements.Select(static e => e.Type)], names);
        return new BoundTuple(syntax, type, elements);
    }

    BoundExpression BindArray(ArrayExpressionSyntax syntax, TypeSymbol? expectedType) {
        var expectedElement = expectedType is ArrayTypeSymbol expectedArray
            ? expectedArray.ElementType
            : null;

        var elements = syntax.Elements
            .Select(e => BindExpression(e, expectedElement))
            .ToImmutableArray();

        TypeSymbol elementType;
        if (expectedElement is not null) {
            // The context says what the elements are, so each one is checked
            // against that rather than taken on trust.
            elements = [
                ..elements.Select(e => Convert(e, expectedElement, e.Syntax))
            ];
            elementType = expectedElement;
        } else if (elements.IsEmpty) {
            elementType = ErrorTypeSymbol.Instance;
        } else {
            elementType = elements[0].Type;
            foreach (var element in elements) {
                if (element.Type.Equals(elementType)) {
                    continue;
                }

                if (_conversions.HasImplicitConversion(element.Type, elementType, IsLiteral(element))) {
                    continue;
                }

                _diagnostics.Error(
                    ErrorCode.ERR_InconsistentArrayElements,
                    element.Syntax.Span,
                    $"this element is '{element.Type.ToDisplayString()}' but the array holds "
                    + $"'{elementType.ToDisplayString()}'"
                );
                elementType = ErrorTypeSymbol.Instance;
                break;
            }
        }

        if (elements.IsEmpty && expectedElement is null) {
            _diagnostics.Error(
                ErrorCode.ERR_CannotInferType,
                syntax.Span,
                "an empty array needs a type annotation"
            );
        }

        // An array literal has no storage kind of its own; like a numeric
        // literal, it takes the one the context asks for.
        var arrayKind = expectedType is ArrayTypeSymbol expected
            ? expected.ArrayKind
            : ArrayKind.Fixed;

        return new BoundArray(syntax, new ArrayTypeSymbol(elementType, arrayKind), elements);
    }

    // -----------------------------------------------------------------------
    // Closures
    // -----------------------------------------------------------------------

    BoundExpression BindBraceClosure(
        BraceClosureExpressionSyntax syntax,
        TypeSymbol? expectedType
    ) =>
        BindClosure(
            syntax,
            syntax.CaptureList,
            syntax.Header?.Parameters ?? default,
            syntax.Header?.ReturnType,
            syntax,
            expectedType,
            syntax.Header is not null
        );

    /// <summary>
    ///     Binds a closure in either spelling, against the type the context wants.
    /// </summary>
    /// <remarks>
    ///     A closure parameter may leave its type out, in which case the expected
    ///     function type supplies it. When the closure declares no parameters at
    ///     all the expected type supplies them whole, under the names
    ///     <c>$0</c>, <c>$1</c> and so on, which is what the shorthand refers to.
    /// </remarks>
    BoundExpression BindClosure(
        ExpressionSyntax syntax,
        CaptureListSyntax? captureList,
        SeparatedSyntaxList<ClosureParameterSyntax> declared,
        TypeSyntax? returnTypeSyntax,
        SyntaxNode body,
        TypeSymbol? expectedType,
        bool hasParameterList
    ) {
        var expected = Expand(expectedType) as FunctionTypeSymbol;

        var symbol = new FunctionSymbol(
            string.Empty,
            _containingSymbol,
            syntax,
            Accessibility.Private,
            FunctionKind.Ordinary
        ) { IsAsync = expected?.IsAsync ?? false };

        // A closure passed where '() throws -> T' is wanted may throw.
        var thrown = expected?.ThrownType;
        symbol.ThrownTypeResolver = () => thrown;

        // Captures are bound in the enclosing scope, before the closure's own
        // scope exists, because that is where their names are written.
        var captures = BindCaptures(captureList);

        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var count = declared.Count;

        for (var i = 0; i < count; i++) {
            var parameterSyntax = declared[i];
            var parameter = new ParameterSymbol(parameterSyntax.Identifier.Text, null, symbol, parameterSyntax, i);

            TypeSymbol parameterType;
            if (parameterSyntax.Type is { } annotation) {
                parameterType = _compilation.TypeBinder.BindType(annotation, symbol, _scope);
            } else if (expected is not null
                       && i < expected.ParameterTypes.Length
                       && !IsOpen(expected.ParameterTypes[i])) {
                parameterType = expected.ParameterTypes[i];
            } else {
                _diagnostics.Error(
                    ErrorCode.ERR_CannotInferType,
                    parameterSyntax.Span,
                    $"the type of '{parameterSyntax.Identifier.Text}' cannot be inferred here, "
                    + "so write it"
                );
                parameterType = ErrorTypeSymbol.Instance;
            }

            parameter.TypeResolver = () => parameterType;
            parameters.Add(parameter);
        }

        // No parameter list at all: the expected type names them, and '$0'
        // finds them. An empty one that was written means no parameters.
        if (count == 0
            && !hasParameterList
            && expected is not null
            && !expected.ParameterTypes.Any(IsOpen)) {
            for (var i = 0; i < expected.ParameterTypes.Length; i++) {
                var implicitType = expected.ParameterTypes[i];
                var parameter = new ParameterSymbol($"${i}", null, symbol, null, i);
                parameter.TypeResolver = () => implicitType;
                parameters.Add(parameter);
            }
        }

        symbol.Parameters = parameters.ToImmutable();

        var returnType =
            returnTypeSyntax is { } declaredReturn
                ? _compilation.TypeBinder.BindType(declaredReturn, symbol, _scope)
                : expected is not null && !IsOpen(expected.ReturnType)
                    ? expected.ReturnType
                    : null;

        var inferring = returnType is null;
        symbol.ReturnTypeResolver = () => returnType ?? Core.Void;

        var outerNarrowing = EnterNestedBody();
        var bound = BindClosureBody(symbol, body, returnType ?? Core.Void, inferring, captures);
        ExitNestedBody(outerNarrowing);

        var type = new FunctionTypeSymbol(
            [..symbol.Parameters.Select(static p => p.Type)],
            symbol.ReturnType,
            symbol.IsAsync,
            _closureThrows
        );

        return Record(syntax, new BoundClosure(syntax, type, symbol, captures, bound));
    }

    /// <summary>Binds a closure's body with its parameters in scope.</summary>
    BoundBlock BindClosureBody(
        FunctionSymbol symbol,
        SyntaxNode body,
        TypeSymbol returnType,
        bool inferring,
        ImmutableArray<BoundCapture> captures
    ) {
        var savedSymbol = _containingSymbol;
        var savedReturn = _returnType;
        var savedInferring = _inferringReturn;
        var savedLoopDepth = _loopDepth;
        var savedOpen = _openTypeParameters;
        var savedEffects = SaveEffects();

        _containingSymbol = symbol;
        _returnType = returnType;
        _inferringReturn = inferring;
        _loopDepth = 0;
        _openTypeParameters = [];
        _declaredThrows = symbol.ThrownType;
        _rethrows = symbol.IsRethrows;
        _inAsyncContext = symbol.IsAsync;
        SeedRethrowSources(symbol);
        _tryDepth = 0;
        _awaitDepth = 0;
        _catchDepth = 0;
        _threwInTry = false;
        _thrownInBody = null;

        PushScope();
        foreach (var parameter in symbol.Parameters) {
            Declare(parameter.Name, parameter);
        }

        // '[name = value]' introduces a name the body sees and the scope around
        // it does not.
        foreach (var capture in captures) {
            if (capture.Symbol is LocalSymbol local) {
                Declare(local.Name, local);
            }
        }

        HoistLocalTypes(body);

        BoundBlock bound;
        switch (body) {
            case ExpressionSyntax expression and not BraceClosureExpressionSyntax: {
                var value = BindExpression(expression, inferring ? null : _returnType);
                if (inferring) {
                    _returnType = value.Type.Equals(Core.Never) ? Core.Void : value.Type;
                    _inferringReturn = false;
                } else if (!_returnType.Equals(Core.Void)) {
                    value = Convert(value, _returnType, expression);
                }

                bound = new BoundBlock(
                    expression,
                    [],
                    [
                        _returnType.Equals(Core.Void)
                            ? new BoundExpressionStatement(expression, value)
                            : new BoundReturn(expression, value)
                    ]
                );
                break;
            }

            case BlockSyntax block:
                bound = BindBlock(block);
                break;

            case BraceClosureExpressionSyntax brace: {
                // '{ $0 * 2 }' is a value, not a statement that throws one
                // away: a brace closure whose body is one expression returns
                // it, which is what makes the shorthand mean anything.
                if (brace.Statements.Count == 1
                    && brace.Statements[0] is ExpressionStatementSyntax only
                    && only.Expression is not (MatchExpressionSyntax or AssignmentExpressionSyntax)) {
                    var value = BindExpression(only.Expression, inferring ? null : _returnType);

                    if (inferring) {
                        _returnType = value.Type.Equals(Core.Never) ? Core.Void : value.Type;
                        _inferringReturn = false;
                    } else if (!_returnType.Equals(Core.Void)) {
                        value = Convert(value, _returnType, only.Expression);
                    }

                    bound = new BoundBlock(
                        brace,
                        [],
                        [
                            _returnType.Equals(Core.Void)
                                ? new BoundExpressionStatement(only, value)
                                : new BoundReturn(only, value)
                        ]
                    );
                    break;
                }

                var statements = ImmutableArray.CreateBuilder<BoundStatement>();
                foreach (var statement in brace.Statements) {
                    statements.Add(BindStatement(statement));
                }

                var locals = _locals[^1].Values.OfType<LocalSymbol>().ToImmutableArray();
                bound = new(brace, locals, statements.ToImmutable());
                break;
            }

            default:
                bound = new BoundBlock(body, [], []);
                break;
        }

        // Nothing returned a value, so the closure returns nothing.
        if (_inferringReturn) {
            _returnType = Core.Void;
        }

        CheckClosureFlow(body, bound);

        var resolved = _returnType;
        symbol.ReturnTypeResolver = () => resolved;

        // What the closure's type claims is what its body can actually let
        // out, not what the context would have allowed: a closure that does
        // not throw is not a throwing closure, which is what 'rethrows' reads.
        _closureThrows = _thrownInBody;

        PopScope();
        RestoreEffects(savedEffects);
        _openTypeParameters = savedOpen;
        _loopDepth = savedLoopDepth;
        _inferringReturn = savedInferring;
        _returnType = savedReturn;
        _containingSymbol = savedSymbol;
        return bound;
    }

    /// <summary>A closure with a result returns on every path, like anything else.</summary>
    void CheckClosureFlow(SyntaxNode body, BoundBlock bound) {
        if (_returnType.Equals(Core.Void) || _returnType.IsError) {
            return;
        }

        if (FlowAnalysis.AlwaysExits(bound)) {
            return;
        }

        var span = body switch {
            BraceClosureExpressionSyntax brace => brace.CloseBraceToken.Span,
            BlockSyntax block => block.CloseBraceToken.Span,
            _ => body.Span,
        };

        _diagnostics.Error(
            ErrorCode.ERR_NotAllPathsReturn,
            span,
            $"this closure returns '{_returnType.ToDisplayString()}', "
            + "so every path through it needs a value"
        );
    }

    ImmutableArray<BoundCapture> BindCaptures(CaptureListSyntax? captureList) {
        if (captureList is null) {
            return [];
        }

        var captures = ImmutableArray.CreateBuilder<BoundCapture>();
        foreach (var item in captureList.Items) {
            var kind = item.SpecifierToken.Kind switch {
                SyntaxKind.WeakKeyword => CaptureKind.Weak,
                SyntaxKind.UnownedKeyword => CaptureKind.Unowned,
                _ => item.Expression is null ? CaptureKind.Default : CaptureKind.ByValue,
            };

            var name = item.Identifier.Text;

            if (item.Expression is { } expression) {
                var value = BindExpression(expression);
                var local = new LocalSymbol(name, _containingSymbol, item, false);
                local.TypeResolver = () => value.Type;
                captures.Add(new(item, local, kind, value));
                continue;
            }

            // A bare name in a capture list has to name something already in
            // scope; 'self' is the one that needs no local.
            if (name == "self" || item.Identifier.Kind == SyntaxKind.SelfKeyword) {
                if (EnclosingType() is { } enclosing) {
                    captures.Add(
                        new(
                            item,
                            enclosing,
                            kind,
                            new BoundSelfAccess(item, SelfTypeOf(enclosing), SelfIsMutable(enclosing))
                        )
                    );
                }

                continue;
            }

            var captured = BindNameAsValue(name, item);
            var symbol = captured switch {
                BoundLocalAccess local => (Symbol)local.Local,
                BoundParameterAccess parameter => parameter.Parameter,
                BoundFieldAccess field => field.Field,
                _ => null,
            };

            if (symbol is not null) {
                captures.Add(new(item, symbol, kind, captured));
            }
        }

        return captures.ToImmutable();
    }

    BoundExpression BindImplicitParameter(ImplicitParameterExpressionSyntax syntax) {
        var name = "$" + syntax.NumberToken.Text;

        if (LookupLocal(name) is ParameterSymbol parameter) {
            return new BoundParameterAccess(syntax, parameter);
        }

        _diagnostics.Error(
            ErrorCode.ERR_NameNotFound,
            syntax.Span,
            $"'{name}' is only available inside a closure whose parameter types are known"
        );
        return new BoundError(syntax);
    }

    /// <summary>The concrete type behind an opaque or existential wrapper.</summary>
    static TypeSymbol Unwrap(TypeSymbol type) =>
        type switch {
            OpaqueTypeSymbol opaque => Unwrap(opaque.Constraint),
            ExistentialTypeSymbol existential => Unwrap(existential.Constraint),
            _ => type,
        };

    /// <summary>True for a type inference has not settled yet.</summary>
    bool IsOpen(TypeSymbol type) => type is TypeParameterSymbol parameter && _openTypeParameters.Contains(parameter);

    /// <summary>Looks through an alias or an optional for a function type.</summary>
    static TypeSymbol? Expand(TypeSymbol? type) =>
        type switch {
            OptionalTypeSymbol optional => optional.UnderlyingType,
            _ => type,
        };

    /// <summary>Binds <c>["a": 1]</c> and the empty <c>[:]</c>.</summary>
    BoundExpression BindDictionary(
        DictionaryExpressionSyntax syntax,
        TypeSymbol? expectedType
    ) {
        TypeSymbol? expectedKey = null;
        TypeSymbol? expectedValue = null;

        if (expectedType is NamedTypeSymbol wanted
            && wanted.OriginalDefinition.Equals(Core.Dictionary)
            && wanted.TypeArguments.Length == 2) {
            expectedKey = wanted.TypeArguments[0];
            expectedValue = wanted.TypeArguments[1];
        }

        var entries =
            ImmutableArray.CreateBuilder<(BoundExpression, BoundExpression)>(syntax.Entries.Count);

        TypeSymbol? keyType = expectedKey;
        TypeSymbol? valueType = expectedValue;

        foreach (var entry in syntax.Entries) {
            var key = BindExpression(entry.Key, keyType);
            var value = BindExpression(entry.Value, valueType);

            keyType ??= key.Type;
            valueType ??= value.Type;

            entries.Add(
                (
                    Convert(key, keyType, entry.Key),
                    Convert(value, valueType, entry.Value))
            );
        }

        if (keyType is null || valueType is null) {
            _diagnostics.Error(
                ErrorCode.ERR_CannotInferType,
                syntax.Span,
                "an empty dictionary needs a type annotation"
            );
            return new BoundError(syntax);
        }

        return Record(
            syntax,
            new BoundDictionary(syntax, Core.Dictionary.Construct([keyType, valueType]), entries.ToImmutable())
        );
    }

    /// <summary>Binds <c>#file</c> and the rest, per spec/lexical.md §9.</summary>
    BoundExpression BindBuiltin(BuiltinExpressionSyntax syntax) {
        var name = syntax.NameToken.Text;

        TypeSymbol type = name switch {
            "file" or "function" or "module" => Core.String,
            "line" or "column" => Core.Int,
            _ => ErrorTypeSymbol.Instance,
        };

        if (type.IsError) {
            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Span,
                $"there is no compiler builtin called '#{name}'"
            );
            return new BoundError(syntax);
        }

        return Record(syntax, new BoundBuiltin(syntax, type, name));
    }

    BoundExpression BindRecordExpression(RecordExpressionSyntax syntax) {
        // 'Pair { first: 1, second: "a" }' names a generic type without its
        // arguments, which the field values settle — so the name is resolved
        // first and the arguments inferred, rather than demanded up front.
        var type = InferredRecordType(syntax)
            ?? _compilation.TypeBinder.BindType(syntax.Type, _containingSymbol, _scope);

        if (type is not NamedTypeSymbol named) {
            if (!type.IsError) {
                _diagnostics.Error(
                    ErrorCode.ERR_NotAValue,
                    syntax.Type.Span,
                    $"'{type.ToDisplayString()}' has no fields to initialise"
                );
            }

            return new BoundError(syntax);
        }

        var substitution = TypeSubstitution.For(named);
        var fields = ImmutableArray.CreateBuilder<(FieldSymbol, BoundExpression)>();
        var initialized = new HashSet<string>(StringComparer.Ordinal);

        foreach (var initializer in syntax.Fields) {
            var name = initializer.NameToken.Text;
            var field = named.GetMembers(name).OfType<FieldSymbol>().FirstOrDefault();

            if (field is null) {
                _diagnostics.Error(
                    ErrorCode.ERR_NameNotFound,
                    initializer.NameToken.Span,
                    $"'{named.ToDisplayString()}' has no field named '{name}'"
                );
                continue;
            }

            if (!initialized.Add(name)) {
                _diagnostics.Error(
                    ErrorCode.ERR_DuplicateDeclaration,
                    initializer.NameToken.Span,
                    $"'{name}' is initialised more than once"
                );
            }

            var fieldType = substitution.Apply(field.Type);

            var value = initializer.Expression is { } expression
                ? Convert(BindExpression(expression, fieldType), fieldType, expression)
                : new BoundError(initializer);

            fields.Add((field, value));
        }

        var missing = named.Members
            .OfType<FieldSymbol>()
            .Where(static f => f is { IsStatic: false, IsComputed: false, HasInitializer: false })
            .Where(f => !initialized.Contains(f.Name))
            .Select(static f => f.Name)
            .ToList();

        if (missing.Count > 0) {
            _diagnostics.Error(
                ErrorCode.ERR_MissingFieldInitializer,
                syntax.Span,
                missing.Count == 1
                    ? $"'{missing[0]}' is not initialised"
                    : $"'{string.Join("', '", missing)}' are not initialised"
            );
        }

        return new BoundRecordExpression(syntax, named, fields.ToImmutable());
    }

    /// <summary>
    ///     A generic type written without its arguments in a record literal, with
    ///     the arguments read off the field values.
    /// </summary>
    NamedTypeSymbol? InferredRecordType(RecordExpressionSyntax syntax) {
        if (syntax.Type is not SimpleNameTypeSyntax { TypeArgumentList: null } name) {
            return null;
        }

        var found = _compilation.TypeBinder.Lookup(name.Identifier.Text, _containingSymbol, _scope, name.Span);

        if (found is not NamedTypeSymbol { TypeArguments.IsEmpty: true } definition) {
            return null;
        }

        if (definition.TypeParameters.IsEmpty) {
            return null;
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();

        // The field values are bound again below, in the position they turn
        // out to have, so this pass asks rather than tells.
        using (_diagnostics.Suppress()) {
            foreach (var initializer in syntax.Fields) {
                if (initializer.Expression is not { } expression) {
                    continue;
                }

                var field = definition
                    .GetMembers(initializer.NameToken.Text)
                    .OfType<FieldSymbol>()
                    .FirstOrDefault();

                if (field is null) {
                    continue;
                }

                TypeSubstitution.Unify(field.Type, BindExpression(expression).Type, definition.TypeParameters, map);
            }
        }

        if (map.Count != definition.TypeParameters.Length) {
            return null;
        }

        var substitution = new TypeSubstitution(map);
        return definition.Construct([..definition.TypeParameters.Select(substitution.Apply)]);
    }

    BoundExpression BindCast(CastExpressionSyntax syntax) {
        var operand = BindExpression(syntax.Expression);
        var target = _compilation.TypeBinder.BindType(syntax.Type, _containingSymbol, _scope);

        // 'as?' yields an optional; 'as' and 'as!' yield the type itself.
        var isOptional = syntax.QuestionOrBangToken.Kind == SyntaxKind.QuestionToken;
        var type = isOptional ? new OptionalTypeSymbol(target) : target;

        return new BoundConversion(syntax, type, operand, ConversionKind.Explicit);
    }

    /// <summary>
    ///     Binds <c>a..&lt;b</c> to a <c>Range&lt;T&gt;</c> and <c>a...b</c> to a
    ///     <c>ClosedRange&lt;T&gt;</c>, both declared in Sable.Core; the
    ///     one-sided forms are a <c>Range</c> with a bound missing.
    /// </summary>
    BoundExpression BindRange(RangeExpressionSyntax syntax, TypeSymbol? expectedType) {
        var isInclusive = syntax.OperatorToken.Kind == SyntaxKind.DotDotDotToken;
        var definition = isInclusive && syntax.Left is not null && syntax.Right is not null
            ? Core.ClosedRange
            : Core.Range;

        var bound = expectedType is NamedTypeSymbol { TypeArguments.Length: 1 } expected
            && Core.IsRange(expected)
                ? expected.TypeArguments[0]
                : null;

        var lower = syntax.Left is { } left ? BindExpression(left, bound) : null;
        var upper = syntax.Right is { } right ? BindExpression(right, bound ?? lower?.Type) : null;

        var element = bound ?? lower?.Type ?? upper?.Type;

        if (element is null) {
            _diagnostics.Error(
                ErrorCode.ERR_CannotInferType,
                syntax.Span,
                "a range with no bounds has nothing to say what it ranges over"
            );
            return new BoundError(syntax);
        }

        if (lower is not null) {
            lower = Convert(lower, element, lower.Syntax);
        }

        if (upper is not null) {
            upper = Convert(upper, element, upper.Syntax);
        }

        if (definition is null) {
            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Span,
                $"no type named '{(isInclusive ? "ClosedRange" : "Range")}' is declared in Sable.Core"
            );
            return new BoundError(syntax);
        }

        // A range's bounds are ordered: 'Range<Bound: Comparable>'.
        if (!element.IsError) {
            foreach (var constraint in definition.TypeParameters[0].Constraints) {
                if (!Conversions.Satisfies(element, constraint, _conversions.Equivalence)) {
                    _diagnostics.Error(
                        ErrorCode.ERR_DoesNotConform,
                        syntax.Span,
                        $"'{element.ToDisplayString()}' does not conform to '{constraint.ToDisplayString()}', "
                        + "which a range requires of its bounds"
                    );
                }
            }
        }

        return Record(
            syntax,
            new BoundRange(syntax, definition.Construct([element]), lower, upper, isInclusive)
        );
    }

    BoundExpression BindTypeTest(TypeTestExpressionSyntax syntax) {
        var operand = BindExpression(syntax.Expression);
        var target = _compilation.TypeBinder.BindType(syntax.Type, _containingSymbol, _scope);
        return new BoundTypeTest(syntax, Core.Bool, operand, target);
    }

    BoundExpression BindAssignment(AssignmentExpressionSyntax syntax) {
        var target = BindExpression(syntax.Left);

        // A plain store replaces the value, so it may store anything the
        // binding's declared type holds: inside 'if p != none', 'p = none' is
        // fine. What a test narrowed applies to reads, not to the slot.
        if (syntax.OperatorToken.Kind == SyntaxKind.EqualsToken) {
            target = target switch {
                BoundLocalAccess { IsNarrowed: true } local =>
                    Record(syntax.Left, new BoundLocalAccess(syntax.Left, local.Local)),
                BoundParameterAccess { IsNarrowed: true } parameter =>
                    Record(syntax.Left, new BoundParameterAccess(syntax.Left, parameter.Parameter)),
                _ => target,
            };
        }

        var value = BindExpression(syntax.Right, target.Type);

        if (!target.HasError && !target.IsAssignable) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAssignable,
                syntax.Left.Span,
                "the left side of an assignment must be a mutable binding"
            );
        }

        // 'private(set) var count': readable here, but not writable.
        if (target is BoundFieldAccess { Field.SetterAccessibility: { } setter } setField && !IsAccessible(setField.Field, setter)) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAssignable,
                syntax.Left.Span,
                $"'{setField.Field.Name}' can be read here but not written: its setter is {setter.ToString().ToLowerInvariant()}"
            );
        }

        // Whatever a test established about this binding, the write ends it.
        Symbol? written = target switch {
            BoundLocalAccess local => local.Local,
            BoundParameterAccess parameter => parameter.Parameter,
            _ => null,
        };

        if (written is not null) {
            DropNarrowing(written);

            // Written from inside a closure or local function: that body can
            // run during any later call.
            if (!ReferenceEquals(written.ContainingSymbol, _containingSymbol)) {
                _writtenByNestedBodies.Add(written);
            }
        }

        if (syntax.OperatorToken.Kind != SyntaxKind.EqualsToken) {
            // A compound assignment is the binary operator then the store.
            var kind = CompoundOperatorKind(syntax.OperatorToken.Kind);
            if (kind is not null) {
                var combined = BindBinaryOperator(syntax, kind.Value, target, value, syntax.OperatorToken);
                return new BoundAssignment(syntax, target, combined);
            }
        }

        return new BoundAssignment(syntax, target, Convert(value, target.Type, syntax.Right));
    }

    static BinaryOperatorKind? CompoundOperatorKind(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.PlusEqualsToken => BinaryOperatorKind.Add,
            SyntaxKind.MinusEqualsToken => BinaryOperatorKind.Subtract,
            SyntaxKind.StarEqualsToken => BinaryOperatorKind.Multiply,
            SyntaxKind.SlashEqualsToken => BinaryOperatorKind.Divide,
            SyntaxKind.PercentEqualsToken => BinaryOperatorKind.Remainder,
            SyntaxKind.AmpersandEqualsToken => BinaryOperatorKind.And,
            SyntaxKind.PipeEqualsToken => BinaryOperatorKind.Or,
            SyntaxKind.CaretEqualsToken => BinaryOperatorKind.ExclusiveOr,
            SyntaxKind.LessLessEqualsToken => BinaryOperatorKind.LeftShift,
            SyntaxKind.GreaterGreaterEqualsToken => BinaryOperatorKind.RightShift,
            SyntaxKind.QuestionQuestionEqualsToken => BinaryOperatorKind.Coalesce,
            _ => null,
        };

    BoundExpression BindConditionalExpression(ConditionalExpressionSyntax syntax, TypeSymbol? expectedType = null) {
        var condition = Convert(BindExpression(syntax.Condition, Core.Bool), Core.Bool, syntax.Condition);

        // 'c ? 5 : none' is an Int?: 'none' is no particular optional until
        // the other branch, or the context, says which.
        BoundExpression whenTrue, whenFalse;
        if (IsNoneLiteral(syntax.WhenTrue) && !IsNoneLiteral(syntax.WhenFalse)) {
            whenFalse = BindExpression(syntax.WhenFalse, expectedType);
            whenTrue = BindExpression(syntax.WhenTrue, expectedType ?? Optional(whenFalse.Type));
        } else {
            whenTrue = BindExpression(syntax.WhenTrue, expectedType);
            whenFalse = BindExpression(
                syntax.WhenFalse,
                expectedType ?? (IsNoneLiteral(syntax.WhenFalse) ? Optional(whenTrue.Type) : whenTrue.Type)
            );
        }

        var type = whenTrue.Type;
        if (!whenFalse.Type.Equals(type)) {
            if (_conversions.HasImplicitConversion(whenFalse.Type, type, IsLiteral(whenFalse))) {
                whenFalse = Convert(whenFalse, type, syntax.WhenFalse);
            } else if (_conversions.HasImplicitConversion(type, whenFalse.Type, IsLiteral(whenTrue))) {
                type = whenFalse.Type;
                whenTrue = Convert(whenTrue, type, syntax.WhenTrue);
            } else {
                _diagnostics.Error(
                    ErrorCode.ERR_BranchTypesDiffer,
                    syntax.Span,
                    "the branches have different types, "
                    + $"'{type.ToDisplayString()}' and '{whenFalse.Type.ToDisplayString()}'"
                );
                type = ErrorTypeSymbol.Instance;
            }
        }

        return new BoundConditional(syntax, type, condition, whenTrue, whenFalse);
    }

    static bool IsNoneLiteral(ExpressionSyntax expression) =>
        expression switch {
            LiteralExpressionSyntax { Token.Kind: SyntaxKind.NoneKeyword } => true,
            ParenthesizedExpressionSyntax parenthesized => IsNoneLiteral(parenthesized.Expression),
            _ => false,
        };

    /// <summary>The type as an optional, unless it is one already (or an error).</summary>
    static TypeSymbol Optional(TypeSymbol type) =>
        type is OptionalTypeSymbol || type.IsError ? type : new OptionalTypeSymbol(type);

    BoundExpression BindPrefixUnary(PrefixUnaryExpressionSyntax syntax) {
        if (syntax.OperatorToken.Kind == SyntaxKind.AwaitKeyword) {
            if (!_inAsyncContext) {
                _diagnostics.Error(
                    ErrorCode.ERR_AwaitOutsideAsync,
                    syntax.OperatorToken.Span,
                    "'await' is only available in an async context"
                );
            }

            _awaitDepth++;
            var awaited = BindExpression(syntax.Operand);
            _awaitDepth--;

            return new BoundUnaryOperator(syntax, awaited.Type, UnaryOperatorKind.Await, awaited);
        }

        var operand = BindExpression(syntax.Operand);

        var kind = syntax.OperatorToken.Kind switch {
            SyntaxKind.MinusToken => UnaryOperatorKind.Negate,
            SyntaxKind.PlusToken => UnaryOperatorKind.Plus,
            SyntaxKind.BangToken => UnaryOperatorKind.Not,
            SyntaxKind.TildeToken => UnaryOperatorKind.Complement,
            SyntaxKind.AmpersandToken => UnaryOperatorKind.AddressOf,
            SyntaxKind.AwaitKeyword => UnaryOperatorKind.Await,
            SyntaxKind.MoveKeyword => UnaryOperatorKind.Move,
            _ => UnaryOperatorKind.Plus,
        };

        if (operand.HasError) {
            return new BoundError(syntax, operand);
        }

        var type = kind switch {
            UnaryOperatorKind.Negate or UnaryOperatorKind.Plus =>
                Core.IsNumeric(operand.Type) ? operand.Type : null,
            UnaryOperatorKind.Not =>
                operand.Type.Equals(Core.Bool) ? Core.Bool : null,
            UnaryOperatorKind.Complement =>
                Core.IsInteger(operand.Type) ? operand.Type : null,
            UnaryOperatorKind.AddressOf => new ReferenceTypeSymbol(operand.Type),


            _ => operand.Type,
        };

        if (type is null) {
            if (FindUserUnaryOperator(syntax.OperatorToken.Text, operand.Type, OperatorFixity.Prefix) is { } method) {
                return new BoundUnaryOperator(
                    syntax,
                    method.ReturnType,
                    UnaryOperatorKind.UserDefined,
                    operand,
                    method
                );
            }

            _diagnostics.Error(
                ErrorCode.ERR_OperatorNotDefined,
                syntax.Span,
                $"'{syntax.OperatorToken.Text}' is not defined for "
                + $"'{operand.Type.ToDisplayString()}'"
            );
            return new BoundError(syntax, operand);
        }

        // '-5' is a literal, so that it takes the type its context wants,
        // 'let c: I16 = -5', as '5' does.
        if (kind == UnaryOperatorKind.Negate && operand is BoundLiteral literal) {
            object? negated = literal.Value switch {
                long l => -l,
                int i => -(long)i,
                ulong u when u <= 1UL << 63 => unchecked(-(long)u),
                double d => -d,
                _ => null,
            };

            if (negated is not null) {
                return new BoundLiteral(syntax, type, negated);
            }
        }

        return new BoundUnaryOperator(syntax, type, kind, operand);
    }

    BoundExpression BindPostfixUnary(PostfixUnaryExpressionSyntax syntax) {
        var operand = BindExpression(syntax.Operand);

        // The only postfix operator is '!', which unwraps an optional.
        if (operand.Type is OptionalTypeSymbol optional) {
            return new BoundConversion(syntax, optional.UnderlyingType, operand, ConversionKind.Explicit);
        }

        if (FindUserUnaryOperator(syntax.OperatorToken.Text, operand.Type, OperatorFixity.Postfix) is { } method) {
            return new BoundUnaryOperator(syntax, method.ReturnType, UnaryOperatorKind.UserDefined, operand, method);
        }

        if (!operand.HasError) {
            _diagnostics.Error(
                ErrorCode.ERR_NotOptional,
                syntax.Span,
                $"'{operand.Type.ToDisplayString()}' is not optional, "
                + "so there is nothing to unwrap"
            );
        }

        return new BoundError(syntax, operand);
    }

    BoundExpression BindBinary(BinaryExpressionSyntax syntax) {
        var left = BindExpression(syntax.Left);

        // The right side of '??' is typed by the left's unwrapped type.
        TypeSymbol? expected = syntax.OperatorToken.Kind == SyntaxKind.QuestionQuestionToken
            ? (left.Type as OptionalTypeSymbol)?.UnderlyingType
            : left.Type;

        var right = BindExpression(syntax.Right, expected);

        var kind = BinaryKindOf(syntax.OperatorToken.Kind);
        if (kind is null) {
            return new BoundError(syntax, left, right);
        }

        return BindBinaryOperator(syntax, kind.Value, left, right, syntax.OperatorToken);
    }

    static BinaryOperatorKind? BinaryKindOf(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.PlusToken => BinaryOperatorKind.Add,
            SyntaxKind.MinusToken => BinaryOperatorKind.Subtract,
            SyntaxKind.StarToken => BinaryOperatorKind.Multiply,
            SyntaxKind.SlashToken => BinaryOperatorKind.Divide,
            SyntaxKind.PercentToken => BinaryOperatorKind.Remainder,
            SyntaxKind.AmpersandToken => BinaryOperatorKind.And,
            SyntaxKind.PipeToken => BinaryOperatorKind.Or,
            SyntaxKind.CaretToken => BinaryOperatorKind.ExclusiveOr,
            SyntaxKind.LessLessToken => BinaryOperatorKind.LeftShift,
            SyntaxKind.GreaterGreaterToken => BinaryOperatorKind.RightShift,
            SyntaxKind.EqualsEqualsToken => BinaryOperatorKind.Equal,
            SyntaxKind.BangEqualsToken => BinaryOperatorKind.NotEqual,
            SyntaxKind.EqualsEqualsEqualsToken => BinaryOperatorKind.ReferenceEqual,
            SyntaxKind.BangEqualsEqualsToken => BinaryOperatorKind.ReferenceNotEqual,
            SyntaxKind.LessToken => BinaryOperatorKind.LessThan,
            SyntaxKind.LessEqualsToken => BinaryOperatorKind.LessThanOrEqual,
            SyntaxKind.GreaterToken => BinaryOperatorKind.GreaterThan,
            SyntaxKind.GreaterEqualsToken => BinaryOperatorKind.GreaterThanOrEqual,
            SyntaxKind.AmpersandAmpersandToken => BinaryOperatorKind.LogicalAnd,
            SyntaxKind.PipePipeToken => BinaryOperatorKind.LogicalOr,
            SyntaxKind.QuestionQuestionToken => BinaryOperatorKind.Coalesce,
            _ => null,
        };

    BoundExpression BindBinaryOperator(
        SyntaxNode syntax,
        BinaryOperatorKind kind,
        BoundExpression left,
        BoundExpression right,
        SyntaxToken operatorToken
    ) {
        if (left.HasError || right.HasError) {
            return new BoundError(syntax, left, right);
        }

        // After 'where T == Int' a T is an Int, operators and all.
        left = AsCanonical(left);
        right = AsCanonical(right);

        if (kind == BinaryOperatorKind.Coalesce) {
            if (left.Type is not OptionalTypeSymbol optional) {
                _diagnostics.Error(
                    ErrorCode.ERR_NotOptional,
                    syntax.Span,
                    "'??' needs an optional on the left, not "
                    + $"'{left.Type.ToDisplayString()}'"
                );
                return new BoundError(syntax, left, right);
            }

            var result = Convert(right, optional.UnderlyingType, right.Syntax);
            return new BoundBinaryOperator(syntax, optional.UnderlyingType, kind, left, result);
        }

        // A numeric literal on one side takes the other side's type.
        if (IsLiteral(left)
            && !IsLiteral(right)
            && _conversions.Classify(left.Type, right.Type, true) is ConversionKind.LiteralTyping) {
            left = Convert(left, right.Type, left.Syntax);
        } else if (IsLiteral(right)
                   && !IsLiteral(left)
                   && _conversions.Classify(right.Type, left.Type, true)
                       is ConversionKind.LiteralTyping) {
            right = Convert(right, left.Type, right.Syntax);
        }

        // A type of the program's own, or a type parameter, uses the operators
        // it declares, or its constraints require: 'a == b' on two Moneys is
        // Money's '=='. '!=', '>', '<=' and '>=' follow from '==' and '<'.
        // Strings are ordered the same way: there is no ordering builtin for
        // them, only Sable.Core's '<'.
        var stringOrdering = kind is BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
                or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual
            && left.Type.Equals(Core.String) && right.Type.Equals(Core.String);
        if (!IsBuiltinOperand(left.Type) || !IsBuiltinOperand(right.Type) || stringOrdering) {
            if (FindUserOperator(operatorToken.Text, left.Type, right.Type) is { } own) {
                return new BoundBinaryOperator(syntax, UserResultType(own, left.Type), BinaryOperatorKind.UserDefined, left, right, own);
            }

            var derivedFrom = kind switch {
                BinaryOperatorKind.NotEqual => "==",
                BinaryOperatorKind.GreaterThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThanOrEqual => "<",
                _ => null,
            };

            if (derivedFrom is not null && FindUserOperator(derivedFrom, left.Type, right.Type) is { } basis) {
                return new BoundBinaryOperator(syntax, Core.Bool, kind, left, right, basis);
            }

            if (ReportUnmetDerivedOperator(derivedFrom ?? operatorToken.Text, left.Type, syntax)) {
                return new BoundError(syntax, left, right);
            }
        }

        // Strings have no ordering instruction: they order by Sable.Core's
        // '<', written in Sable, and the other three follow from it.
        if (kind is BinaryOperatorKind.LessThan
                or BinaryOperatorKind.GreaterThan
                or BinaryOperatorKind.LessThanOrEqual
                or BinaryOperatorKind.GreaterThanOrEqual
            && left.Type.Equals(Core.String)
            && right.Type.Equals(Core.String)
            && FindUserOperator("<", left.Type, right.Type) is { } order) {
            return kind == BinaryOperatorKind.LessThan
                ? new BoundBinaryOperator(syntax, Core.Bool, BinaryOperatorKind.UserDefined, left, right, order)
                : new BoundBinaryOperator(syntax, Core.Bool, kind, left, right, order);
        }

        var type = ResultTypeOf(kind, left.Type, right.Type);
        if (type is not null) {
            return new BoundBinaryOperator(syntax, type, kind, left, right);
        }

        if (FindUserOperator(operatorToken.Text, left.Type, right.Type) is { } method) {
            return new BoundBinaryOperator(
                syntax,
                method.ReturnType,
                BinaryOperatorKind.UserDefined,
                left,
                right,
                method
            );
        }

        _diagnostics.Error(
            ErrorCode.ERR_OperatorNotDefined,
            syntax.Span,
            $"'{operatorToken.Text}' is not defined for "
            + $"'{left.Type.ToDisplayString()}' and '{right.Type.ToDisplayString()}'"
        );
        return new BoundError(syntax, left, right);
    }

    TypeSymbol? ResultTypeOf(
        BinaryOperatorKind kind,
        TypeSymbol left,
        TypeSymbol right
    ) =>
        kind switch {
            BinaryOperatorKind.Add when left.Equals(Core.String) && right.Equals(Core.String) =>
                Core.String,

            BinaryOperatorKind.Add
                or BinaryOperatorKind.Subtract
                or BinaryOperatorKind.Multiply
                or BinaryOperatorKind.Divide
                or BinaryOperatorKind.Remainder =>
                left.Equals(right) && Core.IsNumeric(left) ? left : null,

            BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr =>
                left.Equals(right) && Core.IsInteger(left) ? left
                : left.Equals(Core.Bool) && right.Equals(Core.Bool) ? Core.Bool
                : null,

            BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift =>
                Core.IsInteger(left) && Core.IsInteger(right) ? left : null,

            BinaryOperatorKind.LessThan
                or BinaryOperatorKind.LessThanOrEqual
                or BinaryOperatorKind.GreaterThan
                or BinaryOperatorKind.GreaterThanOrEqual =>
                left.Equals(right) && (Core.IsNumeric(left) || left.Equals(Core.String))
                    ? Core.Bool
                    : null,

            // 'T → T?' is implicit (docs/03 §3.7), so comparing an optional with
            // a value of its type is comparing two optionals.
            BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual =>
                left.Equals(right)
                || (left is OptionalTypeSymbol leftOptional
                    && leftOptional.UnderlyingType.Equals(right))
                || (right is OptionalTypeSymbol rightOptional
                    && rightOptional.UnderlyingType.Equals(left))
                    ? Core.Bool
                    : null,

            BinaryOperatorKind.ReferenceEqual or BinaryOperatorKind.ReferenceNotEqual =>
                left.IsReferenceType && right.IsReferenceType ? Core.Bool : null,

            BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr =>
                left.Equals(Core.Bool) && right.Equals(Core.Bool) ? Core.Bool : null,

            _ => null,
        };

    /// <summary>Looks for a one-operand <c>operator</c> member on the operand's type.</summary>
    FunctionSymbol? FindUserUnaryOperator(
        string symbol,
        TypeSymbol operand,
        OperatorFixity fixity
    ) {
        if (operand is not NamedTypeSymbol named) {
            return null;
        }

        foreach (var member in named.Members) {
            if (member is not FunctionSymbol { FunctionKind: FunctionKind.Operator } function) {
                continue;
            }

            if (function.Fixity != fixity) {
                continue;
            }

            if (!string.Equals(function.Name, symbol, StringComparison.Ordinal)) {
                continue;
            }

            if (function.Parameters.Length != 1) {
                continue;
            }

            if (_conversions.HasImplicitConversion(operand, function.Parameters[0].Type)) {
                return function;
            }
        }

        return null;
    }

    /// <summary>Looks for an <c>operator</c> member on either operand's type.</summary>
    /// <summary>A type the built-in operators are defined on: what never looks for one of its own.</summary>
    bool IsBuiltinOperand(TypeSymbol type) =>
        Core.IsNumeric(type) || type.Equals(Core.String) || type.Equals(Core.Bool) || type.Equals(Core.Char)
        || type is OptionalTypeSymbol || type.IsError;

    /// <summary>What a user operator gives: 'Self' in a protocol's requirement is the operand's type.</summary>
    static TypeSymbol UserResultType(FunctionSymbol method, TypeSymbol operand) =>
        method.ReturnType is TypeParameterSymbol { ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } } ? operand : method.ReturnType;

    /// <summary>The protocols a type parameter is constrained to, and those they refine.</summary>
    static IEnumerable<NamedTypeSymbol> ConstraintProtocols(TypeParameterSymbol parameter) {
        var seen = new HashSet<NamedTypeSymbol>();
        var pending = new Stack<NamedTypeSymbol>(parameter.Constraints.OfType<NamedTypeSymbol>().Where(static c => c.TypeKind == TypeKind.Protocol));
        while (pending.Count > 0) {
            var protocol = pending.Pop();
            if (!seen.Add(protocol)) {
                continue;
            }

            yield return protocol;
            foreach (var refined in protocol.BaseTypes.OfType<NamedTypeSymbol>().Where(static b => b.TypeKind == TypeKind.Protocol)) {
                pending.Push(refined);
            }
        }
    }

    FunctionSymbol? FindUserOperator(string symbol, TypeSymbol left, TypeSymbol right) {
        // 'a < b' on two T: Comparable — the protocol's requirement, met by
        // whatever T turns out to be.
        if (left is TypeParameterSymbol parameter && left.Equals(right)) {
            foreach (var protocol in ConstraintProtocols(parameter)) {
                if (protocol.Members.OfType<FunctionSymbol>().FirstOrDefault(f =>
                        f is { FunctionKind: FunctionKind.Operator, Fixity: OperatorFixity.Infix, Parameters.Length: 2 }
                        && string.Equals(f.Name, symbol, StringComparison.Ordinal)) is { } requirement) {
                    return requirement;
                }
            }

            return null;
        }

        foreach (var owner in new[] { left, right }) {
            if (owner is not NamedTypeSymbol named) {
                continue;
            }

            foreach (var member in named.OriginalDefinition.Members) {
                if (member is not FunctionSymbol { FunctionKind: FunctionKind.Operator } function) {
                    continue;
                }

                if (function.Fixity != OperatorFixity.Infix) {
                    continue;
                }

                if (!string.Equals(function.Name, symbol, StringComparison.Ordinal)) {
                    continue;
                }

                if (function.Parameters.Length != 2) {
                    continue;
                }

                // A generic type's operator, seen through the operand's type
                // arguments; a derived one only where its conformance holds.
                var members = TypeSubstitution.For(named);
                if (_conversions.HasImplicitConversion(left, members.Apply(function.Parameters[0].Type))
                    && _conversions.HasImplicitConversion(right, members.Apply(function.Parameters[1].Type))
                    && (_compilation.GetDerivation(function) is not { } derivation || Conversions.Satisfies(named, derivation.Protocol))) {
                    return function;
                }
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Conversions
    // -----------------------------------------------------------------------

    BoundExpression Convert(
        BoundExpression expression,
        TypeSymbol target,
        SyntaxNode syntax
    ) {
        if (expression is BoundFunctionGroup group) {
            expression = Materialize(group, target);
        }

        // 'Tree.Leaf' carries nothing to infer from, so the context says
        // which Tree it is.
        if (expression is BoundUnionCaseGroup { UnionCase.HasPayload: false } empty
            && empty.Type is NamedTypeSymbol written
            && target is NamedTypeSymbol wanted
            && ReferenceEquals(written.OriginalDefinition, wanted.OriginalDefinition)) {
            return new BoundUnionCaseConstruction(expression.Syntax, wanted, empty.UnionCase, []);
        }

        // '.Circle' names a case that carries a radius; without one it is not
        // a value of the union.
        if (expression is BoundUnionCaseGroup { UnionCase.HasPayload: true } unionCase) {
            _diagnostics.Error(
                ErrorCode.ERR_WrongPayloadCount,
                expression.Syntax.Span,
                $"'{unionCase.UnionCase.Name}' carries "
                + $"{unionCase.UnionCase.Payload.Length} value(s), so it needs them here"
            );
            return new BoundError(expression.Syntax);
        }

        if (expression.Type.Equals(target)) {
            return expression;
        }

        // '[1, 2, 3]' where a 'U16[]' is wanted: each element takes the element type.
        if (expression is BoundArray { Type: ArrayTypeSymbol literalArray } arrayLiteral
            && target is ArrayTypeSymbol wantedArray
            && !literalArray.ElementType.Equals(wantedArray.ElementType)
            && !arrayLiteral.Elements.IsEmpty
            && arrayLiteral.Elements.All(static e => e is BoundLiteral)) {
            expression = new BoundArray(
                arrayLiteral.Syntax,
                new ArrayTypeSymbol(wantedArray.ElementType, literalArray.ArrayKind),
                [..arrayLiteral.Elements.Select(e => Convert(e, wantedArray.ElementType, e.Syntax))]
            );
            if (expression.Type.Equals(target)) {
                return expression;
            }
        }

        var conversion = _conversions.Classify(expression.Type, target, IsLiteral(expression));
        if (conversion is null) {
            if (!expression.HasError && !target.IsError) {
                _diagnostics.Error(
                    ErrorCode.ERR_TypeMismatch,
                    syntax.Span,
                    $"expected '{target.ToDisplayString()}' but found "
                    + $"'{expression.Type.ToDisplayString()}'"
                );
            }

            return new BoundError(syntax, expression);
        }

        // docs/02 §2.5: a variable-mode value is exactly its variant's size,
        // so nothing can box it behind a fixed-size existential.
        if (conversion == ConversionKind.Existential
            && target is ExistentialTypeSymbol
            && expression.Type is NamedTypeSymbol { IsVariableSize: true } variable) {
            _diagnostics.Error(
                ErrorCode.ERR_VariableSizeNotAllowed,
                syntax.Span,
                $"'{variable.ToDisplayString()}' is variable-size, so it cannot be held "
                + $"as '{target.ToDisplayString()}'"
            );
            return new BoundError(syntax, expression);
        }

        if (conversion == ConversionKind.Identity) {
            return expression;
        }

        // 'c ? a : b' refers to whichever place it picks: each arm becomes a reference.
        if (conversion == ConversionKind.Reference && expression is BoundConditional choice) {
            var whenTrue = Convert(choice.WhenTrue, target, choice.WhenTrue.Syntax);
            var whenFalse = Convert(choice.WhenFalse, target, choice.WhenFalse.Syntax);
            return new BoundConditional(choice.Syntax, target, choice.Condition, whenTrue, whenFalse);
        }

        // A reference refers to somewhere: a variable, a parameter, a field
        // of one — not a value that exists only for the moment.
        if (conversion == ConversionKind.Reference && !IsPlace(expression)) {
            _diagnostics.Error(
                ErrorCode.ERR_ReferenceNeedsPlace,
                syntax.Span,
                $"a reference needs something with an address — a variable, a parameter, or a field of one — "
                + $"and this '{expression.Type.ToDisplayString()}' is a value that exists only here"
            );
            return new BoundError(syntax, expression);
        }

        var converted = new BoundConversion(syntax, target, expression, conversion.Value);

        // Record against the syntax, so asking the model for an expression's
        // type gives the type it actually has in this position.
        if (syntax == expression.Syntax) {
            _bound[syntax] = converted;
        }

        return converted;
    }

    /// <summary>
    ///     Turns a named function into a value of function type.
    /// </summary>
    /// <remarks>
    ///     A bare name that resolves to functions is a group until something says
    ///     which one is meant. A call says it by its arguments; everywhere else
    ///     the expected type says it, and with no expected type the group has to
    ///     hold exactly one function.
    /// </remarks>
    BoundExpression Materialize(BoundFunctionGroup group, TypeSymbol? expected) {
        var substitution = group.Substitution ?? TypeSubstitution.Empty;

        FunctionSymbol? chosen = null;

        if (Expand(expected) is FunctionTypeSymbol wanted) {
            foreach (var candidate in group.Candidates) {
                var type = Apply(candidate.FunctionType, substitution);
                if (!_conversions.HasImplicitConversion(type, wanted)) {
                    continue;
                }

                chosen = candidate;
                break;
            }
        } else if (group.Candidates.Length == 1) {
            chosen = group.Candidates[0];
        }

        if (chosen is null) {
            _diagnostics.Error(
                group.Candidates.Length > 1
                    ? ErrorCode.ERR_AmbiguousOverload
                    : ErrorCode.ERR_TypeMismatch,
                group.Syntax.Span,
                group.Candidates.Length > 1
                    ? $"several overloads of '{group.Candidates[0].Name}' could be meant here; "
                    + "say which with a type annotation"
                    : "this function does not have the type wanted here"
            );
            return new BoundError(group.Syntax);
        }

        return new BoundFunctionReference(
            group.Syntax,
            Apply(chosen.FunctionType, substitution),
            group.Receiver,
            chosen
        );
    }

    static FunctionTypeSymbol Apply(
        FunctionTypeSymbol type,
        TypeSubstitution substitution
    ) =>
        substitution.IsEmpty
            ? type
            : new FunctionTypeSymbol(
                [..type.ParameterTypes.Select(substitution.Apply)],
                substitution.Apply(type.ReturnType),
                type.IsAsync,
                type.ThrownType is null ? null : substitution.Apply(type.ThrownType)
            );

    // =======================================================================
    // Patterns
    // =======================================================================

    /// <summary>Binds a pattern against the type of the value it will match.</summary>
    /// <remarks>
    ///     Binding a pattern declares its bindings in the current scope, so the
    ///     caller pushes a scope for each arm.
    /// </remarks>
    public BoundPattern BindPattern(PatternSyntax pattern, TypeSymbol inputType) {
        var bound = BindPatternCore(pattern, inputType);
        _bound[pattern] = bound;
        return bound;
    }

    BoundPattern BindPatternCore(PatternSyntax pattern, TypeSymbol inputType) {
        switch (pattern) {
            case WildcardPatternSyntax wildcard:
                return new BoundWildcardPattern(wildcard, inputType);

            case ParenthesizedPatternSyntax parenthesized:
                return BindPattern(parenthesized.Pattern, inputType);

            case BindingPatternSyntax binding: {
                var mutable = binding.LetOrVarKeyword.IsNone
                    ? _declaringMutable
                    : binding.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword;

                var local = new LocalSymbol(binding.Identifier.Text, _containingSymbol, binding, mutable) {
                    TypeResolver = () => inputType
                };

                Declare(local.Name, local);
                return new BoundBindingPattern(binding, inputType, local);
            }

            case LiteralPatternSyntax literal:
                return BindLiteralPattern(literal, inputType);

            case RangePatternSyntax range: {
                var lower = range.Left is null ? null : BindPattern(range.Left, inputType);
                var upper = range.Right is null ? null : BindPattern(range.Right, inputType);
                return new BoundRangePattern(range, inputType, lower, upper);
            }

            case TuplePatternSyntax tuple:
                return BindTuplePattern(tuple, inputType);

            case UnionCasePatternSyntax unionCase:
                return BindUnionCasePattern(unionCase, inputType);

            case RecordPatternSyntax record:
                return BindRecordPattern(record, inputType);

            case ArrayPatternSyntax array:
                return BindArrayPattern(array, inputType);

            case TypeTestPatternSyntax test: {
                var tested = _compilation.TypeBinder.BindType(test.Type, _containingSymbol, _scope);
                return new BoundTypeTestPattern(test, inputType, tested, null);
            }

            case BindingTypeTestPatternSyntax test: {
                var tested = _compilation.TypeBinder.BindType(test.Type, _containingSymbol, _scope);

                var local = new LocalSymbol(
                    test.Identifier.Text,
                    _containingSymbol,
                    test,
                    test.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword
                ) { TypeResolver = () => tested };

                Declare(local.Name, local);
                return new BoundTypeTestPattern(test, inputType, tested, local);
            }

            case OrPatternSyntax or: {
                var left = BindPattern(or.Left, inputType);
                var right = BindPattern(or.Right, inputType);
                return new BoundOrPattern(or, inputType, left, right);
            }

            case RestPatternSyntax rest:
                return new BoundWildcardPattern(rest, inputType);

            default:
                return new BoundErrorPattern(pattern);
        }
    }

    BoundPattern BindLiteralPattern(
        LiteralPatternSyntax pattern,
        TypeSymbol inputType
    ) {
        var token = pattern.Token;

        var literalType = token.Kind switch {
            SyntaxKind.IntegerLiteralToken => Core.Int,
            SyntaxKind.FloatLiteralToken => Core.F64,
            SyntaxKind.CharLiteralToken => Core.Char,
            SyntaxKind.StringLiteralToken
                or SyntaxKind.MultilineStringLiteralToken
                or SyntaxKind.RawStringLiteralToken => Core.String,
            SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => Core.Bool,
            SyntaxKind.NoneKeyword => inputType,
            _ => ErrorTypeSymbol.Instance,
        };

        if (!inputType.IsError
            && !literalType.IsError
            && !_conversions.HasImplicitConversion(literalType, inputType, true)) {
            _diagnostics.Error(
                ErrorCode.ERR_PatternTypeMismatch,
                pattern.Span,
                $"a '{literalType.ToDisplayString()}' pattern cannot match "
                + $"'{inputType.ToDisplayString()}'"
            );
        }

        return new BoundLiteralPattern(pattern, inputType, token.Value, !pattern.MinusToken.IsNone);
    }

    BoundPattern BindTuplePattern(TuplePatternSyntax pattern, TypeSymbol inputType) {
        var elementTypes = inputType is TupleTypeSymbol tuple
            ? tuple.ElementTypes
            : ImmutableArray<TypeSymbol>.Empty;

        if (inputType is not TupleTypeSymbol && !inputType.IsError) {
            _diagnostics.Error(
                ErrorCode.ERR_PatternTypeMismatch,
                pattern.Span,
                $"'{inputType.ToDisplayString()}' is not a tuple"
            );
        } else if (!elementTypes.IsEmpty && elementTypes.Length != pattern.Elements.Count) {
            _diagnostics.Error(
                ErrorCode.ERR_PatternTypeMismatch,
                pattern.Span,
                $"this tuple has {elementTypes.Length} element(s), "
                + $"but the pattern has {pattern.Elements.Count}"
            );
        }

        var elements = ImmutableArray.CreateBuilder<BoundPattern>(pattern.Elements.Count);
        for (var i = 0; i < pattern.Elements.Count; i++) {
            var elementType = i < elementTypes.Length
                ? elementTypes[i]
                : ErrorTypeSymbol.Instance;
            elements.Add(BindPattern(pattern.Elements[i], elementType));
        }

        return new BoundTuplePattern(pattern, inputType, elements.ToImmutable());
    }

    BoundPattern BindUnionCasePattern(
        UnionCasePatternSyntax pattern,
        TypeSymbol inputType
    ) {
        var name = pattern.Identifier.Text;

        // A qualifier names the union explicitly; otherwise the input type does.
        var owner = pattern.Qualifier is { } qualifier
            ? _compilation.TypeBinder.BindType(qualifier, _containingSymbol, _scope)
            : inputType;

        if (owner is not NamedTypeSymbol named) {
            if (!owner.IsError) {
                _diagnostics.Error(
                    ErrorCode.ERR_PatternTypeMismatch,
                    pattern.Span,
                    $"'{owner.ToDisplayString()}' has no cases to match"
                );
            }

            return new BoundErrorPattern(pattern);
        }

        var member = named.GetMembers(name).FirstOrDefault();

        if (member is EnumCaseSymbol enumCase) {
            // An enum case carries nothing, so there is nothing to name.
            if (pattern.ArgumentList is { } enumArguments) {
                _diagnostics.Error(
                    ErrorCode.ERR_WrongPayloadCount,
                    pattern.Span,
                    $"'{name}' is an enum case and carries no values, "
                    + $"but the pattern names {enumArguments.Arguments.Count}"
                );
            }

            return new BoundEnumCasePattern(pattern, inputType, enumCase);
        }

        if (member is not UnionCaseSymbol unionCase) {
            _diagnostics.Error(
                ErrorCode.ERR_UnknownUnionCase,
                pattern.Identifier.Span,
                $"'{named.ToDisplayString()}' has no case named '{name}'"
            );
            return new BoundErrorPattern(pattern);
        }

        var substitution = TypeSubstitution.For(inputType);
        var payload = ImmutableArray.CreateBuilder<BoundPattern>();
        if (pattern.ArgumentList is { } arguments) {
            if (arguments.Arguments.Count != unionCase.Payload.Length) {
                _diagnostics.Error(
                    ErrorCode.ERR_WrongPayloadCount,
                    pattern.Span,
                    $"'{name}' carries {unionCase.Payload.Length} value(s), "
                    + $"but the pattern names {arguments.Arguments.Count}"
                );
            }

            for (var i = 0; i < arguments.Arguments.Count; i++) {
                var fieldType = i < unionCase.Payload.Length
                    ? substitution.Apply(unionCase.Payload[i].Type)
                    : ErrorTypeSymbol.Instance;
                payload.Add(BindPattern(arguments.Arguments[i].Pattern, fieldType));
            }
        } else if (unionCase.HasPayload) {
            _diagnostics.Error(
                ErrorCode.ERR_WrongPayloadCount,
                pattern.Span,
                $"'{name}' carries {unionCase.Payload.Length} value(s), "
                + "but the pattern names none"
            );
        }

        return new BoundUnionCasePattern(pattern, inputType, unionCase, payload.ToImmutable());
    }

    BoundPattern BindRecordPattern(RecordPatternSyntax pattern, TypeSymbol inputType) {
        var owner = pattern.Type is { } typeSyntax
            ? _compilation.TypeBinder.BindType(typeSyntax, _containingSymbol, _scope)
            : inputType;

        var fields = ImmutableArray.CreateBuilder<(FieldSymbol, BoundPattern)>();
        var incomplete = false;

        if (owner is not NamedTypeSymbol named) {
            if (!owner.IsError) {
                _diagnostics.Error(
                    ErrorCode.ERR_PatternTypeMismatch,
                    pattern.Span,
                    $"'{owner.ToDisplayString()}' has no fields to match"
                );
            }

            return new BoundErrorPattern(pattern);
        }

        foreach (var field in pattern.Fields) {
            var name = field.NameToken.Text;
            var member = named.GetMembers(name).OfType<FieldSymbol>().FirstOrDefault();

            if (member is null) {
                _diagnostics.Error(
                    ErrorCode.ERR_NameNotFound,
                    field.NameToken.Span,
                    $"'{named.ToDisplayString()}' has no field named '{name}'"
                );
                incomplete = true;
                continue;
            }

            if (field.Pattern is { } sub) {
                fields.Add((member, BindPattern(sub, member.Type)));
                continue;
            }

            // '{ name }' binds the field to a local of the same name.
            var local = new LocalSymbol(name, _containingSymbol, field, _declaringMutable) {
                TypeResolver = () => member.Type
            };
            Declare(name, local);
            fields.Add((member, new BoundBindingPattern(field, member.Type, local)));
        }

        // Naming the input's own type, or none, tests nothing; naming a
        // narrower one is a type test and can fail.
        return new BoundRecordPattern(pattern, inputType, fields.ToImmutable()) {
            TestsType = !owner.Equals(inputType), IsIncomplete = incomplete
        };
    }

    BoundPattern BindArrayPattern(ArrayPatternSyntax pattern, TypeSymbol inputType) {
        var elementType = inputType is ArrayTypeSymbol array
            ? array.ElementType
            : ErrorTypeSymbol.Instance;

        if (inputType is not ArrayTypeSymbol && !inputType.IsError) {
            _diagnostics.Error(
                ErrorCode.ERR_PatternTypeMismatch,
                pattern.Span,
                $"'{inputType.ToDisplayString()}' is not an array"
            );
        }

        var elements = ImmutableArray.CreateBuilder<BoundPattern>(pattern.Elements.Count);
        var restIndex = -1;

        for (var i = 0; i < pattern.Elements.Count; i++) {
            var element = pattern.Elements[i];

            if (element is RestPatternSyntax rest) {
                restIndex = i;
                if (!rest.Identifier.IsNone) {
                    var restType = new ArrayTypeSymbol(elementType, ArrayKind.Slice);
                    var local = new LocalSymbol(rest.Identifier.Text, _containingSymbol, rest, false) {
                        TypeResolver = () => restType
                    };
                    Declare(local.Name, local);
                    elements.Add(new BoundBindingPattern(rest, restType, local));
                    continue;
                }

                elements.Add(new BoundWildcardPattern(rest, elementType));
                continue;
            }

            elements.Add(BindPattern(element, elementType));
        }

        return new BoundArrayPattern(pattern, inputType, elements.ToImmutable(), restIndex);
    }

    // =======================================================================
    // match
    // =======================================================================

    BoundExpression BindMatch(MatchExpressionSyntax syntax, TypeSymbol? expectedType) {
        var subject = BindExpression(syntax.Expression);
        var arms = ImmutableArray.CreateBuilder<BoundMatchArm>();

        TypeSymbol? resultType = expectedType;
        var sawValue = false;

        foreach (var armSyntax in syntax.Arms) {
            PushScope();

            var patterns = ImmutableArray.CreateBuilder<BoundPattern>();
            BoundExpression? guard = null;
            BoundNode body;

            switch (armSyntax) {
                case MatchArmSyntax arm: {
                    foreach (var pattern in arm.Patterns) {
                        patterns.Add(BindPattern(pattern, subject.Type));
                    }

                    if (arm.WhereClause is { } where) {
                        guard = Convert(
                            BindExpression(where.Condition, Core.Bool),
                            Core.Bool,
                            where.Condition
                        );
                    }

                    if (arm.Body is BlockSyntax block) {
                        body = BindBlock(block);
                    } else if (arm.Body is ExpressionSyntax expression) {
                        var value = BindExpression(expression, resultType);
                        resultType ??= value.Type;
                        sawValue = true;

                        if (!value.Type.Equals(resultType)) {
                            if (_conversions.HasImplicitConversion(value.Type, resultType, value is BoundLiteral)) {
                                value = Convert(value, resultType, expression);
                            } else {
                                _diagnostics.Error(
                                    ErrorCode.ERR_MatchArmTypesDiffer,
                                    expression.Span,
                                    $"this arm gives '{value.Type.ToDisplayString()}' but "
                                    + $"earlier arms gave '{resultType.ToDisplayString()}'"
                                );
                                resultType = ErrorTypeSymbol.Instance;
                            }
                        }

                        body = value;
                    } else {
                        body = new BoundError(arm);
                    }

                    arms.Add(new(arm, patterns.ToImmutable(), guard, body));
                    break;
                }

                case MatchCaseBlockSyntax caseBlock: {
                    foreach (var pattern in caseBlock.Patterns) {
                        patterns.Add(BindPattern(pattern, subject.Type));
                    }

                    if (caseBlock.WhereClause is { } where) {
                        guard = Convert(
                            BindExpression(where.Condition, Core.Bool),
                            Core.Bool,
                            where.Condition
                        );
                    }

                    var statements = ImmutableArray.CreateBuilder<BoundStatement>();
                    foreach (var statement in caseBlock.Statements) {
                        statements.Add(BindStatement(statement));
                    }

                    body = new BoundBlock(caseBlock, [], statements.ToImmutable());
                    arms.Add(new(caseBlock, patterns.ToImmutable(), guard, body));
                    break;
                }
            }

            PopScope();
        }

        var type = sawValue ? resultType ?? Core.Void : Core.Void;
        var bound = new BoundMatch(syntax, type, subject, arms.ToImmutable());

        // When the match is a value, an arm whose body is a block has to
        // produce one too — or leave, which a 'throw' or 'return' does.
        if (sawValue && !type.Equals(Core.Void) && !type.IsError) {
            foreach (var arm in bound.Arms) {
                if (arm.Body is not BoundStatement statement) {
                    continue;
                }

                if (FlowAnalysis.AlwaysExits(statement)) {
                    continue;
                }

                _diagnostics.Error(
                    ErrorCode.ERR_MatchArmTypesDiffer,
                    arm.Syntax.Span,
                    "this arm gives nothing, but earlier arms gave "
                    + $"'{type.ToDisplayString()}'"
                );
            }
        }

        CheckExhaustive(bound);
        return bound;
    }

    /// <summary>
    ///     Reports a match that does not cover its subject, with values it
    ///     misses, and an arm that earlier arms already cover.
    /// </summary>
    /// <remarks>
    ///     Both are Maranget's usefulness question, asked by
    ///     <see cref="Exhaustiveness" />: an arm is redundant when none of its
    ///     patterns is useful after the arms before it, and the match is
    ///     exhaustive when a wildcard is not useful after all of them. A guard
    ///     can fail, so a guarded arm is checked for redundancy but covers
    ///     nothing.
    /// </remarks>
    void CheckExhaustive(BoundMatch match) {
        var subject = match.Subject.Type;
        if (subject.IsError) {
            return;
        }

        var checker = new Exhaustiveness(Core);
        var rows = new List<Exhaustiveness.Pat>();

        foreach (var arm in match.Arms) {
            var patterns = arm.IsDefault
                ? [checker.Lower(new BoundWildcardPattern(arm.Syntax, subject))]
                : arm.Patterns.Select(checker.Lower).ToList();

            if (!patterns.Any(p => checker.IsUseful(rows, p, subject)) && !checker.GaveUp) {
                ReportCovered(arm);
            }

            if (arm.Guard is null) {
                rows.AddRange(patterns);
            }
        }

        var missing = checker.Missing(rows, subject);
        if (missing.Count == 0 || checker.GaveUp || checker.HasErrors) {
            return;
        }

        var witness = missing.Count == 1
            ? $"'{missing[0]}'"
            : $"'{string.Join("', '", missing.Take(3))}'"
            + (missing.Count > 3 ? " and more" : string.Empty);

        _diagnostics.Error(
            ErrorCode.ERR_MatchNotExhaustive,
            match.Syntax.Span,
            $"match is not exhaustive: {witness} not covered"
        );
    }

    void ReportCovered(BoundMatchArm arm) =>
        _diagnostics.Add(
            Diagnostic.Warning(
                ErrorCode.WRN_PatternAlreadyCovered,
                arm.Syntax.Span,
                "earlier arms already cover this one, so it can never run"
            )
        );
}
