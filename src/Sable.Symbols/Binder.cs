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
    ///     Integer literals bound as a call's arguments before the call's
    ///     parameter types are known, whose range is checked when each is
    ///     converted to its parameter's type: 'take(0x8000_0000_0000_0000)'
    ///     is a U64, though as an Int it would not fit.
    /// </summary>
    readonly HashSet<BoundLiteral> _uncheckedArguments = [];

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
    ///     The <c>switch</c> cases being bound, innermost last: true for one
    ///     with a case after it, which a <c>fallthrough</c> can go on to.
    /// </summary>
    List<bool> _switchCases = [];

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

    /// <summary>Depth of <c>unsafe { }</c> blocks being bound, inside which an <c>unsafe fn</c> may be called.</summary>
    int _unsafeDepth;

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
    ///     True when the innermost <c>do</c> body throws something that did not
    ///     come from a closure parameter — which a <c>rethrows</c> function may
    ///     not let out, should its catches not take it.
    /// </summary>
    bool _threwOwnError;

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
        var savedGenerator = (_generator, _notGenerator, _deferDepth);
        (_generator, _notGenerator) = StartGenerator(function, function.ReturnType, body);
        _deferDepth = 0;

        // A generator's body throws what its 'next()' does.
        if (_generator is { } generating) {
            _declaredThrows = generating.BodyThrown;
        }

        PushScope();
        foreach (var parameter in function.Parameters) {
            Declare(parameter.Name, parameter);
        }

        HoistLocalTypes(body);

        var bound = BindBlock(body);

        // One graph serves all three flow checks.
        // An 'out' parameter comes in with no value: the body gives it one.
        var self = InitializedSelfOf(function);
        var graph = ControlFlowGraph.Build(bound, function.Parameters.Where(static p => p.Modifier != ParameterModifier.Out), self);
        CheckFlow(function, body, graph);
        DefiniteAssignment.Analyze(
            graph,
            _diagnostics,
            function,
            self,
            function.DeclaringSyntax.FirstOrDefault() is InitDeclarationSyntax declaration ? declaration.InitKeyword.Span : body.Span
        );
        CheckEscapes(bound, function);

        PopScope();
        RestoreEffects(savedEffects);
        (_generator, _notGenerator, _deferDepth) = savedGenerator;
        _containingSymbol = savedSymbol;
        _returnType = savedReturn;
        return bound;
    }

    /// <summary>
    ///     For an initialiser of a struct, class or actor, the stored fields its
    ///     body has to set: those without an initial value, as a record literal
    ///     has to give them (S3028). A value with one unset would hold whatever
    ///     bytes were there, and a reference field would be followed to nothing.
    ///     A subclass's has to call <c>super.init(…)</c> too (S3143), for the
    ///     superclass's fields.
    /// </summary>
    InitializedSelf? InitializedSelfOf(FunctionSymbol function) {
        if (function is not { FunctionKind: FunctionKind.Initializer, ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Class or TypeKind.Actor } type }) {
            return null;
        }

        var fields = type.Members
            .OfType<FieldSymbol>()
            .Where(static f => f is { IsStatic: false, IsComputed: false, HasInitializer: false })
            .ToList();

        // A subclass's object is the superclass's too, which only its own
        // initialiser, called as 'super.init(…)', builds.
        var superclass = type.TypeKind == TypeKind.Class
            ? type.BaseTypes.OfType<NamedTypeSymbol>().FirstOrDefault(static b => b.TypeKind == TypeKind.Class)
            : null;
        if (fields.Count == 0 && superclass is null) {
            return null;
        }

        var implicitInit = superclass?.OriginalDefinition.GetMembers("init").OfType<FunctionSymbol>()
            .Any(i => i is { DeclaringSyntax.IsEmpty: true, Parameters.IsEmpty: true } && !_compilation.InheritedInitializers.ContainsKey(i))
            ?? false;
        return new InitializedSelf(type, fields, superclass, implicitInit);
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

        // A generator ends when its body does: it returns nothing.
        if (function.ReturnType.Equals(Core.Void) || function.ReturnType.IsError || _generator is not null || _notGenerator is not null) {
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
        var savedGenerator = (_generator, _notGenerator, _deferDepth);
        (_generator, _notGenerator, _deferDepth) = (null, null, 0);

        PushScope();
        foreach (var parameter in function.Parameters) {
            Declare(parameter.Name, parameter);
        }

        var value = BindExpression(expression, _returnType);
        if (!_returnType.Equals(Core.Void)) {
            CheckOpaqueReturn(value, expression);
            value = ConvertResult(value, expression);
        }

        PopScope();
        RestoreEffects(savedEffects);
        (_generator, _notGenerator, _deferDepth) = savedGenerator;
        _containingSymbol = savedSymbol;
        _returnType = savedReturn;

        var block = new BoundBlock(expression, [], [new BoundReturn(expression, value)]);
        CheckEscapes(block, function);
        return block;
    }

    /// <summary>
    ///     What may not outlive the call, does not. A local function's body is
    ///     checked with the function around it, which sees what it captures.
    /// </summary>
    void CheckEscapes(BoundBlock body, FunctionSymbol function) {
        if (function.ContainingSymbol is not FunctionSymbol) {
            EscapeChecker.Check(body, function, _diagnostics);
        }
    }

    /// <summary>Binds a stored member's initialiser against its declared type.</summary>
    public BoundExpression BindInitializer(ExpressionSyntax value, TypeSymbol expected) {
        PushScope();
        var bound = BindExpression(value, expected.IsError ? null : expected);
        if (!expected.IsError) {
            bound = Convert(bound, expected, value);
        }

        // A stored field's initial value is stored before the initialiser's
        // body runs, in declaration order, when the other fields may hold
        // nothing yet: 'var x: Int = y' would read a 'y' not yet there. A
        // lazy one is computed on its first read, from a finished instance.
        // One written 'lazy' that cannot be (S3113) has said enough.
        if (_containingSymbol is FieldSymbol { IsStatic: false, IsLazy: false } field
            && !(field.DeclaringSyntax.FirstOrDefault() is FieldDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.LazyKeyword))
            && FirstSelfUse(bound) is var (use, what)) {
            _diagnostics.Error(
                ErrorCode.ERR_UseBeforeAssignment,
                use.Syntax.Span,
                $"{what} is used here before the instance exists: a field's initial value cannot use 'self'"
            );
        }

        PopScope();
        return bound;
    }

    /// <summary>The first place a bound tree uses self, explicitly or not, and how to name it.</summary>
    static (BoundNode Node, string What)? FirstSelfUse(BoundNode node) {
        switch (node) {
            case BoundFieldAccess { Receiver: BoundSelfAccess, Field: var field }:
                return (node, $"'{field.Name}'");

            case BoundCall { Receiver: null, Function: { IsStatic: false, FunctionKind: FunctionKind.Ordinary, ContainingSymbol: NamedTypeSymbol or ExtensionSymbol } function }:
                return (node, $"'{function.Name}'");

            case BoundSelfAccess:
                return (node, "'self'");

            case BoundSuperAccess:
                return (node, "'super'");
        }

        foreach (var child in node.Children) {
            if (FirstSelfUse(child) is { } found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    ///     A user-defined attribute, <c>@Route("/users", method: "GET")</c>:
    ///     its arguments as a call to one of the struct's initialisers, as
    ///     <c>Route("/users", method: "GET")</c> would be — a value of the
    ///     struct, which reflection gives back.
    /// </summary>
    public BoundExpression BindAttribute(AttributeSyntax attribute, NamedTypeSymbol type) {
        PushScope();
        var syntaxes = ImmutableArray.CreateBuilder<ExpressionSyntax>();
        var labels = ImmutableArray.CreateBuilder<string?>();
        foreach (var argument in attribute.ArgumentList?.Arguments ?? default) {
            syntaxes.Add(argument.Expression);
            labels.Add(argument.NameToken.IsNone ? null : argument.NameToken.Text);
        }

        var arguments = syntaxes.Select(e => BindExpression(e)).ToImmutableArray();
        var initializers = type.GetMembers("init").OfType<FunctionSymbol>().Where(i => i.DeclaringSyntax.IsEmpty || IsAccessible(i)).ToImmutableArray();
        BoundExpression bound;
        if (initializers.IsEmpty) {
            _diagnostics.Error(
                ErrorCode.ERR_MalformedAttribute,
                attribute.Span,
                $"'{type.ToDisplayString()}' has no initialiser here, so '@{type.Name}' cannot make one"
            );
            bound = new BoundError(attribute, arguments.ToArray());
        } else {
            var group = new BoundFunctionGroup(attribute, new BoundTypeExpression(attribute.Name, type), initializers);
            bound = BindCallToGroup(attribute, group, arguments, labels.ToImmutable(), syntaxes.ToImmutable(), type);
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
        if (scope.TryGetValue(name, out var earlier)) {
            _diagnostics.Add(Diagnostic.Error(
                ErrorCode.ERR_DuplicateDeclaration,
                symbol.Location ?? default,
                $"'{name}' is already declared in this scope"
            ).WithRelated(earlier.DeclaredHere("declared first here")));
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
    /// <summary>
    ///     What <c>self</c> is inside a type: the type — a generic one with
    ///     its own parameters as its arguments, <c>Nest&lt;T&gt;</c>, so that it is
    ///     the same type its signatures write — or, in an array extension, the array.
    ///     In a protocol and its extensions it is Self, the conforming type,
    ///     constrained to the protocol: it has the protocol's operators and
    ///     static members, as a T constrained to it has.
    /// </summary>
    static TypeSymbol SelfTypeOf(NamedTypeSymbol type) => type.SelfTypeParameter ?? type.ExtendedSelf;

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
        // What the requirements in force make Never cannot throw: a loop
        // over S, where S.Failure == Never, needs no 'try'.
        if (ThrowsNothing(thrown)) {
            return;
        }

        // Said as what it is: over S, an 'S.Iter.Failure' is the S.Failure it must be.
        thrown = _conversions.Equivalence.Canonical(thrown);

        // 'rethrows(E)' lets E out too, from wherever it comes.
        if (_rethrows && !viaParameter && FitsRethrownAlso(thrown)) {
            viaParameter = true;
        }

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
        _threwOwnError |= !viaParameter;

        if (_tryDepth > 0) {
            _threwInTry = true;
            return;
        }

        // A loop's 'next()' is marked by 'for try', which goes after 'for'.
        if (_iterating is { } loop) {
            var at = loop.AwaitKeyword.IsNone ? loop.Pattern.Span.Start : loop.AwaitKeyword.Span.Start;
            _diagnostics.Add(QuickFixes.MissingTry(Diagnostic.Error(
                ErrorCode.ERR_MustUseTry,
                loop.Sequence.Span,
                $"'{loop.Sequence.ToString().Trim()}' can throw '{thrown.ToDisplayString()}' as it is gone through, so the loop needs 'for try'"
            ), new TextSpan(at, 0)));
            return;
        }

        _diagnostics.Add(QuickFixes.MissingTry(Diagnostic.Error(
            ErrorCode.ERR_MustUseTry,
            span,
            $"{what} can throw, so the expression needs 'try'"
        ), span));
    }

    /// <summary>The loop whose <c>next()</c> is being bound without <c>for try</c>; null elsewhere.</summary>
    ForStatementSyntax? _iterating;

    /// <summary>True for an error type that has no values once the requirements in force are read: Never.</summary>
    bool ThrowsNothing(TypeSymbol thrown) {
        if (thrown.Equals(Core.Never)) {
            return true;
        }

        var canonical = _conversions.Equivalence.Canonical(thrown);
        return canonical.Equals(Core.Never)
               || canonical is StructuralUnionTypeSymbol union && union.Cases.All(c => _conversions.Equivalence.Canonical(c).Equals(Core.Never));
    }

    /// <summary>
    ///     True when what is thrown is all the enclosing <c>rethrows(E)</c>
    ///     function's E — or nothing — so it may leave it.
    /// </summary>
    bool FitsRethrownAlso(TypeSymbol thrown) {
        if (EnclosingFunction() is not { RethrownAlso: { } also }) {
            return false;
        }

        var allowed = _conversions.Equivalence.Canonical(also);
        var allowedCases = allowed is StructuralUnionTypeSymbol allowedUnion ? allowedUnion.Cases : [allowed];
        var canonical = _conversions.Equivalence.Canonical(thrown);
        var cases = canonical is StructuralUnionTypeSymbol union ? union.Cases : [canonical];
        return cases.All(c => {
                var one = _conversions.Equivalence.Canonical(c);
                return one.Equals(Core.Never) || allowedCases.Any(a => a.Equals(one));
            }
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
                var (body, fallback) = BindCatchBody(syntax.CatchBody!, value.Type, operandThrown ?? _declaredThrows);

                return Record(
                    syntax,
                    new BoundTry(syntax, value.Type, value, kind, fallback) { ErrorLocal = _catchError, FallbackBody = body }
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

    (BoundBlock? Body, BoundExpression Value) BindCatchBody(
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

    (BoundBlock? Body, BoundExpression Value) BindCatchValue(SyntaxNode body, TypeSymbol expected) {
        switch (body) {
            case ExpressionSyntax expression:
                return (null, Convert(BindExpression(expression, expected), expected, expression));

            case BlockSyntax block: {
                // The block's value is what its last expression statement
                // produces, which is how the spec writes it. The statements
                // before it run first, so they are kept as the catch's body.
                var saved = SaveEffects();
                _catchDepth++;

                PushScope();
                var statements = ImmutableArray.CreateBuilder<BoundStatement>();
                BoundExpression? value = null;
                foreach (var statement in block.Statements) {
                    if (statement is ExpressionStatementSyntax last
                        && ReferenceEquals(last, block.Statements[^1])) {
                        value = Convert(BindExpression(last.Expression, expected), expected, last.Expression);
                        continue;
                    }

                    statements.Add(BindStatement(statement));
                }

                var locals = _locals[^1].Values.OfType<LocalSymbol>().ToImmutableArray();
                PopScope();

                RestoreEffects(saved);

                var prelude = new BoundBlock(block, locals, statements.ToImmutable());
                if (value is not null) {
                    return (prelude, value);
                }

                _diagnostics.Error(
                    ErrorCode.ERR_CannotInferType,
                    block.Span,
                    "this 'catch' has to end in a value of type "
                    + $"'{expected.ToDisplayString()}'"
                );
                return (prelude, new BoundError(block));
            }

            default:
                return (null, new BoundError(body));
        }
    }

    BoundStatement BindDo(DoStatementSyntax statement) {
        var saved = SaveEffects();
        var savedThrown = _thrownInBody;
        var savedOwn = _threwOwnError;
        if (statement.Catches.Count > 0) {
            _catchDepth++;
        }

        _thrownInBody = null;
        _threwOwnError = false;

        var body = BindBlock(statement.Body);

        // The catches match what the body can actually throw; failing that,
        // what the enclosing function declares; failing that, anything.
        var thrownByBody = _thrownInBody;
        var threwOwn = _threwOwnError;
        var errorType = thrownByBody ?? _declaredThrows ?? Core.AnyError;

        RestoreEffects(saved);
        _thrownInBody = savedThrown;
        _threwOwnError = savedOwn;
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

        if (thrownByBody is not null && statement.Catches.Count > 0) {
            CheckUncaught(statement, catches, thrownByBody, threwOwn);
        }

        return Record(statement, new BoundDo(statement, body, catches.ToImmutable()));
    }

    /// <summary>
    ///     Checks where an error goes when the catches of a <c>do</c> do not
    ///     take every error its body throws.
    /// </summary>
    /// <remarks>
    ///     The catches are tried in order and an error none of them matches
    ///     leaves the <c>do</c>, as in Swift. So what they miss is thrown again
    ///     from the <c>do</c> itself, and has to have somewhere to go like any
    ///     other throw: an enclosing <c>do</c>, or the function's
    ///     <c>throws</c>. Whether they miss anything is the same usefulness
    ///     question a <c>match</c> asks of its arms — a guarded catch can fail,
    ///     so it covers nothing.
    /// </remarks>
    void CheckUncaught(
        DoStatementSyntax statement,
        IReadOnlyList<BoundCatch> catches,
        TypeSymbol thrown,
        bool threwOwn
    ) {
        if (thrown.IsError) {
            return;
        }

        var checker = new Exhaustiveness(Core);
        var rows = new List<Exhaustiveness.Pat>();
        foreach (var clause in catches) {
            if (clause.Guard is not null) {
                continue;
            }

            if (clause.Pattern is null) {
                return;
            }

            rows.Add(checker.Lower(clause.Pattern));
        }

        var missing = checker.Missing(rows, thrown);
        if (missing.Count == 0 || checker.GaveUp || checker.HasErrors) {
            return;
        }

        // A wildcard as the witness means no unguarded catch matched anything
        // in particular, which reads better said plainly.
        var witness = missing is ["_"] ? "every error"
            : missing.Count == 1 ? $"'{missing[0]}'"
            : $"'{string.Join("', '", missing.Take(3))}'" + (missing.Count > 3 ? " and more" : string.Empty);
        var span = statement.DoKeyword.Span;

        // What escapes is part of what this body throws, for an enclosing
        // 'do' to match and a closure's type to claim.
        _thrownInBody = Union(_thrownInBody, thrown);
        _threwOwnError |= threwOwn;

        if (_rethrows && _catchDepth == 0) {
            if (threwOwn) {
                _diagnostics.Error(
                    ErrorCode.ERR_RethrowsThrowsItsOwn,
                    span,
                    $"the catches do not cover {witness}, so that error leaves this 'do', and a "
                    + "'rethrows' function may only throw what one of its own closure parameters throws"
                );
            }

            return;
        }

        if (!CanThrowHere()) {
            _diagnostics.Error(
                ErrorCode.ERR_ThrowsNotDeclared,
                span,
                $"the catches do not cover {witness}, so that error has nowhere to go: "
                + "add a catch for it, or declare 'throws'"
            );
            return;
        }

        CheckErrorFits(thrown, span, $"this 'do', whose catches do not cover {witness},");
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
            YieldStatementSyntax yieldStatement => BindYield(yieldStatement),
            BreakStatementSyntax breakStatement => BindBreak(breakStatement),
            ContinueStatementSyntax continueStatement => BindContinue(continueStatement),
            DeferStatementSyntax defer => BindDefer(defer),
            GuardStatementSyntax guard => BindGuard(guard),
            LabeledStatementSyntax labeled => BindLabeled(labeled),
            DeclarationStatementSyntax declaration => BindDeclarationStatement(declaration),
            DoStatementSyntax doStatement => BindDo(doStatement),
            SwitchStatementSyntax switchStatement => BindSwitch(switchStatement),
            FallthroughStatementSyntax fallthrough => BindFallthrough(fallthrough),
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

        // What a 'guard', or an 'if' that leaves, establishes for the rest
        // of a block ends with it: a 'break' out of a loop's body is a way
        // past the test with it false.
        PushNarrowing([]);

        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
        var gapStart = block.OpenBraceToken.Span.End;
        foreach (var statement in block.Statements) {
            CaptureScopeInGap(block, gapStart, statement.Span.Start);
            statements.Add(BindStatement(statement));
            gapStart = statement.Span.End;
        }

        CaptureScopeInGap(block, gapStart, block.CloseBraceToken.IsMissing ? block.Span.End : block.CloseBraceToken.Span.Start);

        var locals = _locals[^1].Values.OfType<LocalSymbol>().ToImmutableArray();
        PopNarrowing();
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
            _switchCases.Add(cases.Count + 1 < statement.Cases.Count);
            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            foreach (var inner in clause.Statements) {
                statements.Add(BindStatement(inner));
            }

            _switchCases.RemoveAt(_switchCases.Count - 1);
            PopScope();

            cases.Add(new(clause, values.ToImmutable(), statements.ToImmutable()));
        }

        return Record(statement, new BoundSwitch(statement, subject, cases.ToImmutable()));
    }

    /// <summary><c>fallthrough</c> goes on into the next case, so there has to be one.</summary>
    BoundStatement BindFallthrough(FallthroughStatementSyntax statement) {
        if (_switchCases.Count == 0 || !_switchCases[^1]) {
            _diagnostics.Error(
                ErrorCode.ERR_FallthroughOutOfPlace,
                statement.Span,
                _switchCases.Count == 0
                    ? "'fallthrough' is only allowed in a switch case"
                    : "'fallthrough' in the last case of a switch has no case to go on to"
            );
        }

        return Record(statement, new BoundFallthrough(statement));
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

        if (kind == ScopeKind.Unsafe) {
            _unsafeDepth++;
        }

        var body = BindBlock(bodySyntax);
        if (kind == ScopeKind.Unsafe) {
            _unsafeDepth--;
        }

        PopScope();

        return Record(statement, new BoundScopedBlock(statement, kind, local, source, body));
    }

    /// <summary>
    ///     An <c>unsafe fn</c> is used only where its caller vouches for the
    ///     promise it asks: inside an <c>unsafe</c> block, or in the body of
    ///     another <c>unsafe fn</c> — a closure's in either included (S3470).
    ///     Taken as a value, it could be called anywhere, so taking it is held
    ///     to the same.
    /// </summary>
    void CheckUnsafeUse(FunctionSymbol function, SyntaxNode at, string use) {
        if (!function.IsUnsafe || _unsafeDepth > 0) {
            return;
        }

        for (var container = _containingSymbol; container is not null; container = container.ContainingSymbol) {
            if (container is FunctionSymbol { IsUnsafe: true }) {
                return;
            }
        }

        _diagnostics.Error(
            ErrorCode.ERR_UnsafeCallOutsideUnsafe,
            at.Span,
            $"'{function.Name}' is an 'unsafe fn', safe only when its caller keeps the promise it documents; "
            + $"{use} inside an 'unsafe' block, or in another 'unsafe fn'"
        );
    }

    BoundStatement BindThrow(ThrowStatementSyntax statement) {
        var value = BindExpression(statement.Expression, _declaredThrows);
        _thrownInBody = Union(_thrownInBody, value.Type);
        _threwOwnError = true;

        var rethrownAlso = _rethrows && !value.HasError && FitsRethrownAlso(value.Type);
        if (!value.HasError && CanThrowHere() && !(_rethrows && _catchDepth == 0)) {
            CheckErrorFits(value.Type, statement.Expression.Span, "this");
        }

        if (_rethrows && _catchDepth == 0 && !rethrownAlso) {
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
                initializer = BindAsyncLetChild(initSyntax.Value, declaredType);
                if (initializer is BoundClosure childBody) {
                    CheckTaskCaptures(childBody, "'async let'");
                }
            } else {
                initializer = BindExpression(initSyntax.Value, declaredType);
                if (declaredType is not null) {
                    initializer = Convert(initializer, declaredType, initSyntax.Value);
                }
            }
        }

        // 'let f = someFunction' names the function rather than calling it.
        if (initializer is BoundFunctionGroup group) {
            initializer = Materialize(group, null);
            _bound[declarator.Initializer!.Value] = initializer;
        }

        var type = declaredType
            ?? (initializer is BoundClosure { Type: FunctionTypeSymbol child } && isAsyncLet ? child.ReturnType : initializer?.Type)
            ?? ErrorTypeSymbol.Instance;

        if (declaredType is null && initializer is null) {
            _diagnostics.Error(
                ErrorCode.ERR_CannotInferType,
                binding.Identifier.Span,
                $"'{binding.Identifier.Text}' needs either a type annotation or an initialiser"
            );
        } else if (declaredType is null && initializer is not null && IsNone(initializer)
                   && initializer.Type is OptionalTypeSymbol { UnderlyingType.IsError: true }) {
            // 'none' is no particular optional, so it cannot give the binding
            // a type; left alone, the binding's type would be '??'.
            _diagnostics.Error(
                ErrorCode.ERR_CannotInferType,
                binding.Identifier.Span,
                $"'{binding.Identifier.Text}' needs a type annotation: 'none' alone does not say what it is optional of"
            );
            type = ErrorTypeSymbol.Instance;
        }

        var local = new LocalSymbol(binding.Identifier.Text, _containingSymbol, declaration, isMutable) {
            IsAsyncLet = isAsyncLet,
            AsyncLetThrownType = isAsyncLet ? (initializer?.Type as FunctionTypeSymbol)?.ThrownType : null,
        };

        // 'let s: some Shape = Circle()' hides the initialiser's type behind
        // one of the local's own: later code sees only a Shape, and a 'var'
        // takes only values of that same hidden type.
        if (type is OpaqueTypeSymbol { Origin: null } opaque && initializer is { HasError: false }) {
            local.HiddenType = initializer is BoundConversion { ConversionKind: ConversionKind.Existential } opened
                ? opened.Operand.Type
                : initializer.Type;
            type = opaque.WithOrigin(local);
        }

        local.TypeResolver = () => type;

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

        // 'if v is Int { return … }': the only way on is with the test
        // false, so what that establishes holds for the rest of the block,
        // as after a 'guard'. Likewise the other way round, past an 'else'
        // that leaves.
        var consequenceExits = FlowAnalysis.AlwaysExits(consequence);
        var alternativeExits = alternative is not null && FlowAnalysis.AlwaysExits(alternative);
        if (consequenceExits != alternativeExits) {
            KeepNarrowings(NarrowingsWhen(condition, !consequenceExits));
        }

        return Record(statement, new BoundIf(statement, condition, consequence, alternative));
    }

    /// <summary>Narrowings that hold from here to the end of the enclosing block.</summary>
    void KeepNarrowings(Dictionary<Symbol, TypeSymbol> narrowings) {
        foreach (var (symbol, type) in narrowings) {
            if (_narrowed.Count == 0) {
                PushNarrowing([]);
            }

            _narrowed[^1][symbol] = type;
        }
    }

    /// <summary>
    ///     <c>if c { a } else { b }</c> where a value is wanted: each branch's
    ///     last line is its value, and they all have one type — the one the
    ///     context asks for, or else the first branch's.
    /// </summary>
    BoundExpression BindIfExpression(IfExpressionSyntax syntax, TypeSymbol? expectedType) {
        var type = expectedType;
        var body = BindIfValue(syntax.Statement, ref type);
        return new BoundIfExpression(syntax, type ?? Core.Never, body);
    }

    BoundIf BindIfValue(IfStatementSyntax statement, ref TypeSymbol? type) {
        PushScope();
        var condition = BindConditionList(statement.Conditions, statement);

        PushNarrowing(NarrowingsWhen(condition, true));
        var consequence = BindValueBlock(statement.Body, ref type);
        PopNarrowing();

        PopScope();

        BoundStatement alternative;
        PushNarrowing(NarrowingsWhen(condition, false));
        switch (statement.ElseClause?.Body) {
            case IfStatementSyntax elseIf:
                alternative = BindIfValue(elseIf, ref type);
                break;

            case BlockSyntax block:
                alternative = BindValueBlock(block, ref type);
                break;

            default:
                // Without an 'else' there is no value when the condition fails.
                _diagnostics.Error(
                    ErrorCode.ERR_IfValueNeedsElse,
                    statement.IfKeyword.Span,
                    "an 'if' that gives a value needs an 'else', for when its condition is false"
                );
                alternative = new BoundBlock(statement, [], []);
                break;
        }

        PopNarrowing();
        return Record(statement, new BoundIf(statement, condition, consequence, alternative));
    }

    /// <summary>
    ///     A branch of an <c>if</c> that gives a value: its statements, the last
    ///     of which is the value, converted to <paramref name="type" /> — or, if
    ///     no type is known yet, setting it.
    /// </summary>
    BoundBlock BindValueBlock(BlockSyntax block, ref TypeSymbol? type) {
        PushScope();

        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
        var gapStart = block.OpenBraceToken.Span.End;
        for (var i = 0; i < block.Statements.Count; i++) {
            var statement = block.Statements[i];
            CaptureScopeInGap(block, gapStart, statement.Span.Start);
            gapStart = statement.Span.End;

            if (i < block.Statements.Count - 1 || statement is not ExpressionStatementSyntax last) {
                statements.Add(BindStatement(statement));
                continue;
            }

            // A branch that never comes back, 'fatal("…")', gives no type.
            var value = BindExpression(last.Expression, type);
            if (value.Type.Equals(Core.Never)) {
                statements.Add(Record(last, new BoundExpressionStatement(last, value)));
                continue;
            }

            if (type is null) {
                type = value.Type;
            } else if (!value.Type.Equals(type) && !value.HasError && !type.IsError) {
                if (_conversions.HasImplicitConversion(value.Type, type, IsLiteral(value))) {
                    value = Convert(value, type, last.Expression);
                } else {
                    _diagnostics.Error(
                        ErrorCode.ERR_BranchTypesDiffer,
                        last.Expression.Span,
                        $"this branch gives '{value.Type.ToDisplayString()}' but the one before gave '{type.ToDisplayString()}'"
                    );
                    type = ErrorTypeSymbol.Instance;
                }
            }

            statements.Add(Record(last, new BoundExpressionStatement(last, value)));
        }

        CaptureScopeInGap(block, gapStart, block.CloseBraceToken.IsMissing ? block.Span.End : block.CloseBraceToken.Span.Start);

        var locals = _locals[^1].Values.OfType<LocalSymbol>().ToImmutableArray();
        PopScope();

        var bound = Record(block, new BoundBlock(block, locals, statements.ToImmutable()));
        if (statements is not [.., BoundExpressionStatement] && !FlowAnalysis.AlwaysExits(bound)) {
            _diagnostics.Error(
                ErrorCode.ERR_BranchTypesDiffer,
                block.Statements.Count > 0 ? block.Statements[^1].Span : block.Span,
                "this branch gives no value: an 'if' that gives one ends each branch with it"
            );
        }

        return bound;
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
        KeepNarrowings(NarrowingsWhen(condition, true));

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

                // 'v is Int' failing on an 'Int | String' leaves a String:
                // a union's other cases are what it can still hold.
                case BoundTypeTest test
                    when StructuralUnionTypeSymbol.Of(test.Operand.Type) is { } union && union.Cases.Contains(test.TestedType):
                    if (BindingOf(test.Operand) is { } excluded) {
                        into[excluded] = StructuralUnionTypeSymbol.Create(union.Cases.Where(c => !c.Equals(test.TestedType)));
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

        // A condition is tested only once those before it held, so what
        // they say about a binding holds in it: 'if x != none, x > 3'.
        var pushed = 0;
        foreach (var condition in conditions) {
            if (result is not null) {
                PushNarrowing(NarrowingsWhen(result, true));
                pushed++;
            }

            var bound = BindCondition(condition);
            result = result is null
                ? bound
                : new BoundBinaryOperator(syntax, Core.Bool, BinaryOperatorKind.LogicalAnd, result, bound);
        }

        for (; pushed > 0; pushed--) {
            PopNarrowing();
        }

        return result ?? new BoundLiteral(syntax, Core.Bool, true);
    }

    BoundExpression BindCondition(ConditionSyntax condition) {
        switch (condition) {
            case ExpressionConditionSyntax expression: {
                var bound = BindExpression(expression.Expression, Core.Bool);
                return Convert(bound, Core.Bool, expression.Expression);
            }

            case OptionalBindingConditionSyntax { Declarator: { Pattern: not (BindingPatternSyntax or WildcardPatternSyntax), Initializer: { } taken } } destructuring:
                return BindDestructuringCondition(destructuring, taken.Value);

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

    /// <summary>
    ///     'if let (a, _) = e': the unwrapped value taken apart, as
    ///     'let (a, _) =' would, binding each name the pattern declares —
    ///     'if case (let a, _)? = e', in effect.
    /// </summary>
    BoundExpression BindDestructuringCondition(OptionalBindingConditionSyntax binding, ExpressionSyntax value) {
        var source = BindExpression(value);
        if (source.Type is not OptionalTypeSymbol optional) {
            if (!source.Type.IsError) {
                _diagnostics.Error(
                    ErrorCode.ERR_NotOptional,
                    binding.Span,
                    $"'{source.Type.ToDisplayString()}' is not optional, so there is nothing to unwrap"
                );
            }

            // Taken apart as it is, so that what it names is still bound.
            var saved = _declaringMutable;
            _declaringMutable = binding.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword;
            var plain = BindPattern(binding.Declarator.Pattern, source.Type);
            _declaringMutable = saved;
            return new BoundConditionBinding(binding, Core.Bool, source, plain, null);
        }

        var savedMutable = _declaringMutable;
        _declaringMutable = binding.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword;
        var pattern = BindPattern(binding.Declarator.Pattern, optional.UnderlyingType);
        _declaringMutable = savedMutable;

        return new BoundConditionBinding(binding, Core.Bool, source, new BoundSomePattern(binding, optional, pattern), null);
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
        var isAwait = !statement.AwaitKeyword.IsNone;

        PushScope();

        // A sequence the compiler does not know by sight goes through its
        // iterator: 'var $iterator = s.makeIterator()', then 'next()' until
        // it gives none. 'for await' always does, awaiting each 'next()'.
        var throughIterator = isAwait || !IsKnownSequence(sequence.Type);
        var iteration = throughIterator ? BindIteration(statement, sequence, isAwait) : null;

        // An array or a range is gone through without an iterator, and
        // throws nothing.
        if (!throughIterator && !statement.TryKeyword.IsNone && !sequence.Type.IsError) {
            _diagnostics.Add(
                Diagnostic.Warning(
                    ErrorCode.WRN_NothingThrowsHere,
                    statement.TryKeyword.Span,
                    $"going through '{sequence.Type.ToDisplayString()}' cannot throw, so the loop needs no 'try'"
                )
            );
        }

        // A sequence that cannot be gone through has been reported; its
        // elements are of no type.
        TypeSymbol ElementType() =>
            iteration is { } known ? known.Element
            : throughIterator ? ErrorTypeSymbol.Instance
            : ElementTypeOf(sequence.Type, statement.Sequence);

        LocalSymbol? element = null;
        BoundStatement? deconstruction = null;
        if (statement.Pattern is BindingPatternSyntax binding) {
            var elementType = ElementType();
            element = new(binding.Identifier.Text, _containingSymbol, binding, false) {
                TypeResolver = () => elementType
            };
            Declare(element.Name, element);
        } else if (statement.Pattern is not WildcardPatternSyntax) {
            // 'for (key, value) in d': each element in a variable of the
            // loop's own, nameless, taken apart as 'let (key, value) =' would.
            var elementType = ElementType();
            element = new("$element", _containingSymbol, statement.Pattern, false) {
                TypeResolver = () => elementType
            };
            var pattern = BindPattern(statement.Pattern, elementType);
            if (!pattern.IsIrrefutable && !ContainsErrorPattern(pattern) && !elementType.IsError) {
                _diagnostics.Error(
                    ErrorCode.ERR_RefutablePatternInDeclaration,
                    statement.Pattern.Span,
                    "this pattern might not match every element, so it cannot declare names here; "
                    + "bind the element and use 'if case' in the loop"
                );
            }

            deconstruction = new BoundPatternDeclaration(statement.Pattern, pattern, new BoundLocalAccess(statement.Pattern, element));
        }

        var filter = statement.WhereClause is { } where
            ? Convert(BindExpression(where.Condition, Core.Bool), Core.Bool, where.Condition)
            : null;

        _loopDepth++;
        var body = BindStatement(statement.Body);
        _loopDepth--;

        // The names come first, so the filter sees them too: '{ let (k, v) =
        // element; if filter { body } }'.
        if (deconstruction is not null) {
            body = filter is null
                ? new BoundBlock(statement.Body, [], [deconstruction, body])
                : new BoundBlock(statement.Body, [], [deconstruction, new BoundIf(statement.Body, filter, body, null)]);
            filter = null;
        }

        PopScope();

        if (iteration is { } through) {
            // 'for _ in s' still takes each value, in a variable no one names.
            element ??= new("$element", _containingSymbol, statement.Pattern, false) {
                TypeResolver = () => through.Element
            };
            var loopBody = filter is null ? body : new BoundIf(statement.Body, filter, body, null);
            var next = new BoundConditionBinding(statement.Sequence, Core.Bool, through.Next, null, element);
            return Record(
                statement,
                new BoundBlock(
                    statement,
                    [through.Iterator],
                    [
                        new BoundLocalDeclaration(statement.Sequence, through.Iterator, through.MakeIterator),
                        new BoundWhile(statement, next, loopBody),
                    ]
                )
            );
        }

        // 'for _ in xs' still takes each element, in a variable no one names,
        // as a loop through an iterator does.
        if (element is null && !sequence.Type.IsError) {
            var elementType = ElementType();
            element = new("$element", _containingSymbol, statement.Pattern, false) {
                TypeResolver = () => elementType
            };
        }

        return Record(
            statement,
            new BoundForEach(statement, element, sequence, filter, body)
        );
    }

    /// <summary>
    ///     A type 'for' goes through directly, with no iterator: an array, a
    ///     range — and an error, already reported. A dictionary is a
    ///     Sequence of its entries, like any other.
    /// </summary>
    bool IsKnownSequence(TypeSymbol type) =>
        type.IsError || type is ArrayTypeSymbol
        || type is NamedTypeSymbol named && Core.IsAnyRange(named);

    /// <summary>How a loop goes through a sequence by its iterator.</summary>
    /// <param name="Iterator">The loop's own variable holding the iterator.</param>
    /// <param name="MakeIterator">The call that makes it.</param>
    /// <param name="Next">The call that gives each value, as an optional.</param>
    /// <param name="Element">What each value is.</param>
    sealed record Iteration(LocalSymbol Iterator, BoundExpression MakeIterator, BoundExpression Next, TypeSymbol Element);

    /// <summary>
    ///     Binds the calls a loop over a <c>Sequence</c> (or, for
    ///     <c>for await</c>, an <c>AsyncSequence</c>) makes, as if written:
    ///     <c>makeIterator()</c> on the sequence and <c>next()</c> on the
    ///     iterator, which the loop's variable holds and can change.
    /// </summary>
    Iteration? BindIteration(ForStatementSyntax statement, BoundExpression sequence, bool isAwait) {
        var protocol = isAwait ? Core.AsyncSequence : Core.Sequence;
        var make = isAwait ? "makeAsyncIterator" : "makeIterator";
        var type = sequence.Type is OptionalTypeSymbol ? null : sequence.Type;

        if (protocol is null || type is null || !Conversions.Satisfies(type, protocol, _conversions.Equivalence)) {
            var description = sequence.Type.ToDisplayString();
            _diagnostics.Error(
                ErrorCode.ERR_NotIterable,
                statement.Sequence.Span,
                isAwait
                    ? IsKnownSequence(sequence.Type) || type is not null && Core.Sequence is { } plain && Conversions.Satisfies(type, plain, _conversions.Equivalence)
                        ? $"'{description}' is not an async sequence, so there is nothing to await: write 'for' without 'await'"
                        : $"'{description}' cannot be iterated with 'for await': it does not conform to 'AsyncSequence'"
                    : sequence.Type is OptionalTypeSymbol
                        ? $"'{description}' may hold nothing, so unwrap it before iterating"
                        : $"'{description}' cannot be iterated: it does not conform to 'Sequence'"
            );
            return null;
        }

        var makeIterator = BindIterationCall(statement.Sequence, sequence, make);
        if (makeIterator.HasError) {
            return null;
        }

        var iterator = new LocalSymbol("$iterator", _containingSymbol, statement.Sequence, true) {
            TypeResolver = () => makeIterator.Type
        };

        // 'for await' awaits each value, as 'await iterator.next()' would,
        // and 'for try' marks it as 'try iterator.next()' would: a sequence
        // whose Failure is not Never throws as it is gone through.
        if (isAwait) {
            _awaitDepth++;
        }

        var isTry = !statement.TryKeyword.IsNone;
        var outerThrew = _threwInTry;
        var outerThrown = _thrownInBody;
        var outerIterating = _iterating;
        if (isTry) {
            _threwInTry = false;
            _thrownInBody = null;
            _tryDepth++;
            _iterating = null;
        } else {
            _iterating = statement;
        }

        var next = BindIterationCall(statement.Sequence, new BoundLocalAccess(statement.Sequence, iterator, makeIterator.Type), "next");

        _iterating = outerIterating;
        if (isTry) {
            _tryDepth--;
            var threw = _threwInTry;
            var thrown = _thrownInBody;
            _threwInTry = outerThrew || (threw && _tryDepth > 0);
            _thrownInBody = Union(outerThrown, thrown);

            if (!threw && !next.HasError) {
                _diagnostics.Add(
                    Diagnostic.Warning(
                        ErrorCode.WRN_NothingThrowsHere,
                        statement.TryKeyword.Span,
                        $"going through '{sequence.Type.ToDisplayString()}' cannot throw, so the loop needs no 'try'"
                    )
                );
            } else if (threw && !CanThrowHere()) {
                _diagnostics.Error(
                    ErrorCode.ERR_ThrowsNotDeclared,
                    statement.TryKeyword.Span,
                    "this loop's errors have nowhere to go: declare 'throws', or catch them"
                );
            } else if (thrown is not null) {
                CheckErrorFits(thrown, statement.Sequence.Span, "this loop");
            }

            // As 'try iterator.next()': where an error leaves the loop.
            if (threw) {
                next = new BoundTry(statement.Sequence, next.Type, next, TryKind.Propagate, null);
            }
        }

        if (isAwait) {
            _awaitDepth--;
        }

        if (next.HasError) {
            return null;
        }

        if (next.Type is not OptionalTypeSymbol optional) {
            _diagnostics.Error(
                ErrorCode.ERR_NotIterable,
                statement.Sequence.Span,
                $"'{makeIterator.Type.ToDisplayString()}.next()' gives '{next.Type.ToDisplayString()}', "
                + "and an iterator gives an optional, none when it is done"
            );
            return null;
        }

        // Read through the requirements in force: over S, where S.Element ==
        // (K, V), an element is the tuple 'for (k, v) in s' takes apart.
        return new(iterator, makeIterator, next, _conversions.Equivalence.Canonical(optional.UnderlyingType));
    }

    /// <summary>A call, with no arguments, to the instance member named on the receiver, as 'receiver.name()' binds.</summary>
    BoundExpression BindIterationCall(SyntaxNode syntax, BoundExpression receiver, string name) {
        var found = LookupMember(receiver.Type, name);
        ImmutableArray<FunctionSymbol> functions = found is null
            ? []
            : [..found.Value.Members.OfType<FunctionSymbol>().Where(static f => !f.IsStatic && f.Parameters.IsEmpty)];

        if (functions.IsEmpty) {
            _diagnostics.Error(
                ErrorCode.ERR_NotIterable,
                syntax.Span,
                $"'{receiver.Type.ToDisplayString()}' has no '{name}()' for a loop to call"
            );
            return new BoundError(syntax, receiver);
        }

        if (!CheckAccessible(functions[0], syntax)) {
            return new BoundError(syntax, receiver);
        }

        var group = new BoundFunctionGroup(syntax, receiver, functions) { Substitution = found!.Value.Substitution };
        return BindCallToGroup(syntax, group, [], [], []);
    }

    /// <summary>What a <c>for … in</c> yields, for the sequences understood so far.</summary>
    TypeSymbol ElementTypeOf(TypeSymbol sequence, SyntaxNode syntax) {
        if (sequence.IsError) {
            return ErrorTypeSymbol.Instance;
        }

        if (sequence is ArrayTypeSymbol array) {
            return array.ElementType;
        }

        if (sequence is NamedTypeSymbol named && Core.IsAnyRange(named)) {
            // A range open at the start has no first value to count from.
            if (!Core.IsCountableRange(named)) {
                _diagnostics.Error(
                    ErrorCode.ERR_NotIterable,
                    syntax.Span,
                    $"'{named.ToDisplayString()}' cannot be iterated: it has no lower bound to start from"
                );
                return ErrorTypeSymbol.Instance;
            }

            // A range of integers or characters counts through them; a range
            // of anything else is a test of membership, not a sequence.
            if (Core.IsInteger(named.TypeArguments[0]) || named.TypeArguments[0].Equals(Core.Char)) {
                return named.TypeArguments[0];
            }

            _diagnostics.Error(
                ErrorCode.ERR_NotIterable,
                syntax.Span,
                $"'{named.ToDisplayString()}' cannot be iterated: "
                + "only a range of integers or characters enumerates"
            );
            return ErrorTypeSymbol.Instance;
        }

        // Any other type goes through its iterator (BindIteration), so is not
        // asked about here.
        _diagnostics.Error(
            ErrorCode.ERR_NotIterable,
            syntax.Span,
            $"'{sequence.ToDisplayString()}' cannot be iterated"
        );
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>The property whose accessor is being bound, closures within it included; or null.</summary>
    FieldSymbol? EnclosingProperty() {
        for (var symbol = _containingSymbol; symbol is not null; symbol = symbol.ContainingSymbol) {
            if (symbol is FunctionSymbol { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FieldSymbol property }) {
                return property;
            }
        }

        return null;
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

        // A generator's 'return' ends it; what it gives, it yields.
        if (_generator is not null) {
            if (statement.Expression is { } given) {
                _diagnostics.Error(
                    ErrorCode.ERR_ReturnValueInGenerator,
                    given.Span,
                    "a generator gives its elements by 'yield', and 'return' only ends it: write 'yield' for the value, then 'return'"
                );
                BindExpression(given);
            }

            return Record(statement, new BoundReturn(statement, null));
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
            var inferred = AsValue(BindExpression(statement.Expression));
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
            value = ConvertResult(value, statement.Expression);
        }

        return Record(statement, new BoundReturn(statement, value));
    }

    /// <summary>Converts what a <c>return</c> gives to the function's result type.</summary>
    /// <remarks>
    ///     A <c>some P</c> result is a type only this function's returns make:
    ///     a value converts to it here as it would to an anonymous <c>some P</c>,
    ///     and nowhere else (see <see cref="Conversions.Classify" />).
    /// </remarks>
    BoundExpression ConvertResult(BoundExpression value, SyntaxNode syntax) {
        if (_returnType is not OpaqueTypeSymbol { Origin: { } origin } opaque || !IsOwnResult(origin)) {
            return Convert(value, _returnType, syntax);
        }

        var converted = Convert(value, opaque.WithOrigin(null), syntax);
        return converted switch {
            { HasError: true } => converted,
            BoundConversion conversion => new BoundConversion(conversion.Syntax, opaque, conversion.Operand, conversion.ConversionKind),
            _ => new BoundConversion(syntax, opaque, converted, ConversionKind.Existential),
        };
    }

    /// <summary>
    ///     True when the body being bound gives the <c>some P</c> of this origin:
    ///     its function's own result, or, in a getter, its property's type.
    /// </summary>
    bool IsOwnResult(Symbol origin) =>
        ReferenceEquals(origin, _containingSymbol)
        || origin is FieldSymbol && _containingSymbol is FunctionSymbol { FunctionKind: FunctionKind.Accessor, Name: "get" } getter
        && ReferenceEquals(getter.ContainingSymbol, origin);

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
        // A return of the function's own result, recursively, is that
        // result whatever it hides; it says nothing of what that is.
        if (_returnType is not OpaqueTypeSymbol opaque
            || value.HasError
            || value.Type.IsError
            || value.Type.Equals(Core.Never)
            || value.Type is OpaqueTypeSymbol { Origin: { } returned } && ReferenceEquals(returned, _containingSymbol)) {
            return;
        }

        if (!_opaqueReturns.TryGetValue(_containingSymbol, out var first)) {
            _opaqueReturns[_containingSymbol] = (value.Type, syntax);

            // What the first return gives is the type the result hides.
            if (opaque.Origin is FunctionSymbol origin && ReferenceEquals(origin, _containingSymbol)) {
                origin.HiddenResultType = value.Type;
            } else if (opaque.Origin is FieldSymbol property && IsOwnResult(property)) {
                property.HiddenType = value.Type;
            }

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

    /// <summary>A jump needs a loop, and a labelled one needs that label. A plain 'break' may leave a switch instead.</summary>
    void CheckJump(TextSpan span, SyntaxToken label, string keyword) {
        if (keyword == "break" && label.IsNone && _switchCases.Count > 0) {
            return;
        }

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

        // 'E.Bad' names a case that carries a value. Called, it makes one;
        // anywhere else ('let x = E.Bad', 'throw .Bad', '(E.Bad, 1)') it is
        // not a value of the union, whatever the context expects.
        if (bound is BoundUnionCaseGroup { UnionCase.HasPayload: true } unionCase && !IsCallee(expression)) {
            _diagnostics.Error(
                ErrorCode.ERR_WrongPayloadCount,
                expression.Span,
                $"'{unionCase.UnionCase.Name}' carries "
                + $"{unionCase.UnionCase.Payload.Length} value(s), so it needs them here"
            );
            bound = new BoundError(expression);
        }

        _bound[expression] = bound;
        return bound;
    }

    /// <summary>True when <paramref name="expression"/> is what a call calls: the 'E.Bad' of 'E.Bad(at: 1)', parentheses and all.</summary>
    static bool IsCallee(ExpressionSyntax expression) {
        SyntaxNode node = expression;
        while (node.Parent is ParenthesizedExpressionSyntax parenthesized && ReferenceEquals(parenthesized.Expression, node)) {
            node = parenthesized;
        }

        return node.Parent is InvocationExpressionSyntax invocation && ReferenceEquals(invocation.Expression, node);
    }

    BoundExpression BindExpressionCore(
        ExpressionSyntax expression,
        TypeSymbol? expectedType
    ) =>
        expression switch {
            LiteralExpressionSyntax literal => BindLiteral(literal, expectedType),
            InterpolatedStringExpressionSyntax interpolated => BindInterpolatedString(interpolated),
            // A name the parser had to make up stands for an expression it
            // has already reported missing; looking it up would say so again.
            IdentifierNameSyntax { Identifier.IsMissing: true } missing => new BoundError(missing),
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
            PrefixUnaryExpressionSyntax unary => BindPrefixUnary(unary, expectedType),
            PostfixUnaryExpressionSyntax unary => BindPostfixUnary(unary),
            AssignmentExpressionSyntax assignment => BindAssignment(assignment),
            ConditionalExpressionSyntax conditional => BindConditionalExpression(conditional, expectedType),
            TupleExpressionSyntax tuple => BindTuple(tuple, expectedType),
            ArrayExpressionSyntax array => BindArray(array, expectedType),
            RecordExpressionSyntax record => BindRecordExpression(record),
            CastExpressionSyntax cast => BindCast(cast),
            TypeTestExpressionSyntax test => BindTypeTest(test),
            MatchExpressionSyntax match => BindMatch(match, expectedType),
            IfExpressionSyntax ifExpression => BindIfExpression(ifExpression, expectedType),
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
                closure.Signature.ThrowsSpec,
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

        var bound = new BoundLiteral(literal, type, token.Value);

        // '-128' is checked as a whole, where it is negated.
        // An argument with no type asked of it yet is checked against its
        // parameter's, once the call has chosen one (Convert).
        if (expectedType is null && literal.Parent is ArgumentSyntax) {
            _uncheckedArguments.Add(bound);
        } else if (literal.Parent is not PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.MinusToken }) {
            CheckIntegerFits(bound, type);
        }

        return bound;
    }

    /// <summary>
    ///     Reports an integer literal that its type cannot hold — 'let x: I8 =
    ///     200' — where the type is given to it, rather than leaving lowering
    ///     to find out.
    /// </summary>
    void CheckIntegerFits(BoundLiteral literal, TypeSymbol type) {
        if (Core.IntegerRange(type) is not var (low, high)) {
            return;
        }

        System.Numerics.BigInteger? value = literal.Value switch {
            long l => l,
            int i => i,
            ulong u => u,
            Int128 i => i,
            UInt128 u => u,
            _ => null,
        };

        if (value is { } number && (number < low || number > high)) {
            _diagnostics.Error(
                ErrorCode.ERR_IntegerDoesNotFit,
                literal.Syntax.Span,
                $"'{number}' does not fit in '{type.ToDisplayString()}', which holds {low} through {high}"
            );
        }
    }

    BoundExpression BindInterpolatedString(InterpolatedStringExpressionSyntax syntax) {
        var parts = ImmutableArray.CreateBuilder<BoundExpression>();
        var formats = ImmutableArray.CreateBuilder<BoundFormatSpec?>();

        foreach (var content in syntax.Contents) {
            if (content is InterpolationSyntax hole) {
                var part = BindExpression(hole.Expression);
                parts.Add(part);
                formats.Add(hole.FormatToken.IsNone ? null : BindFormatSpec(hole, part));
            }
        }

        return new BoundInterpolatedString(syntax, Core.String, parts.ToImmutable(), formats.ToImmutable());
    }

    BoundExpression BindSelf(SelfExpressionSyntax syntax) {
        // 'Self' is the type, as it is in a signature: 'Self(x: 3)' makes
        // one, 'Self.origin' reads a static member.
        if (syntax.SelfKeyword.Kind == SyntaxKind.CapitalSelfKeyword) {
            if (EnclosingType() is { } enclosing) {
                return new BoundTypeExpression(syntax, enclosing.SelfTypeParameter ?? SelfTypeOf(enclosing));
            }

            _diagnostics.Error(ErrorCode.ERR_SelfOutsideType, syntax.Span, "'Self' is only available inside a type");
            return new BoundError(syntax);
        }

        // In a closure that captured it '[weak self]' or '[unowned self]',
        // 'self' is the closure's own name for the object — or what
        // 'guard let self' unwrapped it to.
        if (LookupLocal("self") is LocalSymbol captured) {
            return new BoundLocalAccess(syntax, captured, NarrowedType(captured));
        }

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
            && _compilation.TypeBinder.Lookup(name, _containingSymbol, _scope, syntax.Span, report: false)
                is NamedTypeSymbol or AliasSymbol) {
            var type = _compilation.TypeBinder.BindGenericName(syntax, _containingSymbol, _scope);
            return type.IsError
                ? new BoundError(syntax)
                : new BoundTypeExpression(syntax, type);
        }

        return BindName(syntax, name);
    }

    BoundExpression BindName(ExpressionSyntax syntax, string name) => BindNameAsValue(name, syntax);

    /// <summary>An 'async let': its child task's result, so reading it awaits, and tries when the child may throw.</summary>
    BoundExpression AwaitedLocal(SyntaxNode syntax, LocalSymbol local) {
        // Its child belongs to the scope that declared it, which waits for
        // it: a closure, which may run anywhere and later, cannot.
        if (!ReferenceEquals(local.ContainingSymbol, _containingSymbol)) {
            _diagnostics.Error(
                ErrorCode.ERR_AsyncLetCaptured,
                syntax.Span,
                $"'{local.Name}' is an 'async let', read only where it is declared, not from a closure: read it first, and capture the value"
            );
        }

        MarkAsync(syntax.Span, $"'{local.Name}', an 'async let',");
        if (local.AsyncLetThrownType is { } thrown) {
            MarkThrows(syntax.Span, $"'{local.Name}', an 'async let' whose child", thrown);
        }

        return new BoundLocalAccess(syntax, local, NarrowedType(local));
    }

    /// <summary>
    ///     'async let x = value': the value is the work of a child task, run
    ///     alongside the code that follows — an async closure taking nothing,
    ///     its body the value, which may await and throw without saying so;
    ///     what it throws comes out where 'x' is read.
    /// </summary>
    BoundClosure BindAsyncLetChild(ExpressionSyntax value, TypeSymbol? declaredType) {
        var symbol = new FunctionSymbol(string.Empty, _containingSymbol, value, Accessibility.Private, FunctionKind.Ordinary) {
            IsAsync = true,
            Parameters = [],
        };
        symbol.ThrownTypeResolver = () => Core.AnyError;
        symbol.ReturnTypeResolver = () => declaredType ?? Core.Void;

        var outerNarrowing = EnterNestedBody();
        var body = BindClosureBody(symbol, value, declaredType ?? Core.Void, declaredType is null, [], tryAndAwait: true);
        ExitNestedBody(outerNarrowing);

        var type = new FunctionTypeSymbol([], symbol.ReturnType, true, _closureThrows);
        return new BoundClosure(value, type, symbol, [], body);
    }

    /// <summary>
    ///     A call into an actor from outside it: the actor runs one call at a
    ///     time, so its caller may have to wait its turn (docs/02 §2.7).
    /// </summary>
    bool CrossesIntoActor(FunctionSymbol callee) =>
        callee is { IsStatic: false, FunctionKind: FunctionKind.Ordinary } && IsolatedFromHere(callee);

    /// <summary>
    ///     A member of an actor reached from outside it: anything but what is
    ///     static or declared 'nonisolated'.
    /// </summary>
    bool IsolatedFromHere(Symbol member) {
        if (IsNonisolated(member)) {
            return false;
        }

        // '@MainActor' pins a declaration, or everything in a type, to the
        // main executor: code elsewhere reaches it as it reaches an actor.
        if (OnMainActor(member)) {
            return _containingSymbol is not { } here || !OnMainActor(here) && !IsEntryPoint(here);
        }

        if ((member.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : member.ContainingSymbol) is not NamedTypeSymbol { TypeKind: TypeKind.Actor } actor) {
            return false;
        }

        for (var here = _containingSymbol; here is not null; here = here.ContainingSymbol) {
            if (ReferenceEquals(here, actor.OriginalDefinition) || here is ExtensionSymbol { ExtendedType: var extended } && ReferenceEquals(extended, actor.OriginalDefinition)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Declared '@MainActor', or inside a declaration that is — a member
    ///     of such a type, a closure in such a function.
    /// </summary>
    static bool OnMainActor(Symbol symbol) {
        for (var at = symbol; at is not null and not ModuleSymbol; at = at.ContainingSymbol) {
            foreach (var syntax in at.DeclaringSyntax) {
                for (SyntaxNode? node = syntax; node is not null; node = node.Parent) {
                    if (node is DeclarationSyntax && node.ChildNodes().OfType<AttributeSyntax>().Any(static a => a.Name.ToString().Trim() == "MainActor")) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>What a member is isolated to, as a diagnostic says it.</summary>
    static string Isolation(Symbol member) =>
        OnMainActor(member)
            ? "the main actor"
            : $"the actor '{(member.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : member.ContainingSymbol)?.Name}'";

    /// <summary>The program's entry point, or code inside it: it runs on the main executor.</summary>
    static bool IsEntryPoint(Symbol symbol) {
        for (var at = symbol; at is not null and not ModuleSymbol; at = at.ContainingSymbol) {
            if (at is FunctionSymbol { ContainingSymbol: ModuleSymbol } function
                && (function.Name == "main" || function.DeclaringSyntax.Any(static s => s is FunctionDeclarationSyntax declaration
                    && declaration.Attributes.Any(static a => a.Name.ToString().Trim() == "main")))) {
                return true;
            }
        }

        return false;
    }

    static bool IsNonisolated(Symbol member) =>
        member.DeclaringSyntax.Any(static syntax => {
            for (SyntaxNode? node = syntax; node is not null; node = node.Parent) {
                switch (node) {
                    case FunctionDeclarationSyntax function:
                        return function.Modifiers.Any(static m => m.Kind == SyntaxKind.NonisolatedKeyword);
                    case FieldDeclarationSyntax field:
                        return field.Modifiers.Any(static m => m.Kind == SyntaxKind.NonisolatedKeyword);
                }
            }

            return false;
        });

    /// <summary>
    ///     An actor's stored property reached from outside it (SYNTAX.md §19):
    ///     read only by waiting for the actor, as a call is; never changed
    ///     but by the actor itself. A 'let' cannot change, so reading one does
    ///     not wait.
    /// </summary>
    void CheckIsolatedField(SyntaxNode syntax, FieldSymbol field) {
        if (field.IsStatic || !IsolatedFromHere(field)) {
            return;
        }

        if (_assignedSpine.Contains(syntax)) {
            _diagnostics.Error(
                ErrorCode.ERR_ActorStateFromOutside,
                syntax.Span,
                $"'{field.Name}' is the state of {Isolation(field)}, which only it changes; give it a method that changes it, and call that"
            );
            return;
        }

        if (field is { IsMutable: false, IsComputed: false }) {
            return;
        }

        MarkAsync(syntax.Span, $"reading '{field.Name}', isolated to {Isolation(field)},");
        CheckSendable(field.Type, syntax.Span, $"'{field.Name}'", field);
    }

    /// <summary>The expressions an assignment being bound writes through: its target, and what that is a part of.</summary>
    readonly HashSet<SyntaxNode> _assignedSpine = new(ReferenceEqualityComparer.Instance);

    static IEnumerable<SyntaxNode> Spine(ExpressionSyntax target) {
        for (SyntaxNode? node = target; node is not null;) {
            yield return node;
            node = node switch {
                MemberAccessExpressionSyntax member => member.Expression,
                ElementAccessExpressionSyntax element => element.Expression,
                ParenthesizedExpressionSyntax parenthesized => parenthesized.Expression,
                _ => null,
            };
        }
    }

    static BoundExpression Unconverted(BoundExpression expression) =>
        expression is BoundConversion conversion ? Unconverted(conversion.Operand) : expression;

    /// <summary>
    ///     What is passed to a <c>@Sendable</c> parameter: a closure written
    ///     there, whose captures are checked; a named function, which
    ///     captures nothing; or a <c>@Sendable</c> parameter, checked where
    ///     its closure was written. Any other closure's captures cannot be seen.
    /// </summary>
    void CheckSendableClosure(BoundExpression argument, string spawner) {
        switch (Unconverted(argument)) {
            case BoundClosure closure:
                CheckTaskCaptures(closure, spawner);
                return;

            case BoundParameterAccess { Parameter.IsSendable: true }:
            case BoundFunctionReference:
            case BoundFunctionGroup:
            case BoundError:
                return;

            case var other when other.Type is FunctionTypeSymbol or OptionalTypeSymbol { UnderlyingType: FunctionTypeSymbol }:
                _diagnostics.Error(
                    ErrorCode.ERR_TaskCaptureNotSendable,
                    argument.Syntax.Span,
                    $"the closure {spawner} runs alongside the code around it must be written here, or be a named function or a '@Sendable' parameter, "
                    + "so that what it captures can be seen to be Sendable"
                );
                return;
        }
    }

    /// <summary>
    ///     What a task's closure captures (SYNTAX.md §19): it runs alongside
    ///     the code that spawned it, maybe on another thread, so everything it
    ///     captures crosses a boundary as an actor's arguments do, and must be
    ///     Sendable. A 'var' would be shared between the two, each changing
    ///     what the other reads: it is captured by value, '[x = x]', or not at all.
    /// </summary>
    void CheckTaskCaptures(BoundClosure closure, string spawner) {
        var inner = new HashSet<Symbol>(ReferenceEqualityComparer.Instance) { closure.Symbol };
        var listed = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        foreach (var capture in closure.Captures) {
            listed.Add(capture.Symbol);
            if (capture.Initializer is { } value && Sendability.Why(value.Type, []) is { } listedReason) {
                ReportTaskCapture(capture.Syntax, capture.Symbol.Name, value.Type, listedReason, spawner);
            }
        }

        var reported = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<BoundNode>([closure.Body]);
        var nodes = new List<BoundNode>();
        while (pending.TryPop(out var node)) {
            nodes.Add(node);
            switch (node) {
                case BoundClosure nested:
                    inner.Add(nested.Symbol);
                    break;
                case BoundLocalFunction local:
                    inner.Add(local.Symbol);
                    break;
            }

            foreach (var child in node.Children) {
                pending.Push(child);
            }
        }

        // A closure that cannot wait runs on another thread with no way to
        // wait for an actor's turn: it may not touch an actor's state.
        var synchronous = closure.Type is FunctionTypeSymbol { IsAsync: false };
        foreach (var node in nodes) {
            if (synchronous && !InUnsafe(node.Syntax) && IsolatedMemberIn(node) is { } isolated) {
                _diagnostics.Error(
                    ErrorCode.ERR_IsolatedStateInSendableClosure,
                    node.Syntax.Span,
                    $"'{isolated.Name}' is isolated to {Isolation(isolated)}, and the closure {spawner} runs on another thread cannot wait for its turn; "
                    + "read what it needs before, or make the closure async, so that it hops"
                );
            }

            switch (node) {
                // In an 'unsafe' block the programmer vouches for what is shared.
                case BoundNode vouched when InUnsafe(vouched.Syntax):
                    break;

                case BoundLocalAccess access when access.Local.ContainingSymbol is { } owner && !inner.Contains(owner)
                    && !listed.Contains(access.Local) && reported.Add(access.Local):
                    if (access.Local.IsMutable) {
                        _diagnostics.Error(
                            ErrorCode.ERR_TaskCaptureNotSendable,
                            access.Syntax.Span,
                            $"'{access.Local.Name}' is a 'var' the task made by {spawner} would share with the code around it, each changing what the other reads; "
                            + $"copy it into a 'let' to capture, or capture its value in a capture list, '[{access.Local.Name} = {access.Local.Name}]'"
                        );
                    } else if (Sendability.Why(access.Local.Type, []) is { } reason) {
                        ReportTaskCapture(access.Syntax, access.Local.Name, access.Local.Type, reason, spawner);
                    }

                    break;

                case BoundParameterAccess access when access.Parameter.ContainingSymbol is { } owner && !inner.Contains(owner) && reported.Add(access.Parameter):
                    // A '@Sendable' closure parameter was checked where it was written.
                    if (!access.Parameter.IsSendable && Sendability.Why(access.Parameter.Type, []) is { } parameterReason) {
                        ReportTaskCapture(access.Syntax, access.Parameter.Name, access.Parameter.Type, parameterReason, spawner);
                    }

                    break;

                case BoundSelfAccess self when reported.Add(SelfCaptureKey):
                    if (Sendability.Why(self.Type, []) is { } selfReason) {
                        ReportTaskCapture(self.Syntax, "self", self.Type, selfReason, spawner);
                    }

                    break;
            }
        }
    }

    static readonly object SelfCaptureKey = new();

    /// <summary>The actor-isolated member a node reaches: a 'var' of an actor, or a method of one; '@MainActor' state too.</summary>
    static Symbol? IsolatedMemberIn(BoundNode node) {
        Symbol? member = node switch {
            BoundFieldAccess { Field: { IsStatic: false } field } when field.IsMutable || field.IsComputed => field,
            BoundCall { Function: { IsStatic: false, FunctionKind: FunctionKind.Ordinary } function } => function,
            _ => null,
        };

        return member is not null && IsActorIsolated(member) ? member : null;
    }

    static bool IsActorIsolated(Symbol member) {
        if (IsNonisolated(member)) {
            return false;
        }

        var owner = member.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : member.ContainingSymbol;
        return owner is NamedTypeSymbol { TypeKind: TypeKind.Actor } || OnMainActor(member);
    }

    static bool InUnsafe(SyntaxNode node) {
        for (SyntaxNode? at = node; at is not null; at = at.Parent) {
            if (at is UnsafeStatementSyntax) {
                return true;
            }
        }

        return false;
    }

    void ReportTaskCapture(SyntaxNode at, string name, TypeSymbol type, string reason, string spawner) =>
        _diagnostics.Error(
            ErrorCode.ERR_TaskCaptureNotSendable,
            at.Span,
            $"'{name}' is captured by the task {spawner} makes, which runs alongside the code around it, "
            + $"so it must be Sendable, and '{type.ToDisplayString()}' is not: {reason}"
        );

    /// <summary>Reports a value crossing into or out of an actor whose type is not Sendable.</summary>
    void CheckSendable(TypeSymbol type, TextSpan span, string what, Symbol member) {
        if (Sendability.Why(type, []) is { } reason) {
            _diagnostics.Error(
                ErrorCode.ERR_NotSendable,
                span,
                $"{what} crosses the boundary of {Isolation(member)}, "
                + $"so it must be Sendable, and '{type.ToDisplayString()}' is not: {reason}"
            );
        }
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

        // 'field', in the accessors of a property kept in it: its storage.
        if (name == "field" && EnclosingProperty() is { } property) {
            // In an extension it has none, and that has been said.
            if (property.BackingField is not { } backing) {
                if (property.ContainingSymbol is ExtensionSymbol) {
                    return new BoundError(syntax);
                }
            } else {
                name = backing.Name;
            }
        }

        // Members of the enclosing type, reached implicitly through self —
        // including the ones it inherits, which is the whole point of a base
        // class.
        _unmetRequirement = null;
        if (EnclosingType() is { } enclosing
            && LookupMember(SelfTypeOf(enclosing), name) is { } inherited) {
            // Reached through 'self' or not, a member is only as visible as
            // it is: an extension in another module does not see the type's
            // private ones.
            if (inherited.Members.Count > 0 && !inherited.Members.Any(IsAccessible)) {
                ReportInaccessible(inherited.Members[0], syntax);
                return new BoundError(syntax);
            }

            return BindMemberGroup(syntax, null, inherited.Members, inherited.Owner, inherited.Substitution);
        }

        // One a constrained extension declares, where its requirements do not hold.
        var unmetOnSelf = EnclosingType() is { } outer && _unmetRequirement is { } unmet
            ? $"'{SelfTypeOf(outer).ToDisplayString()}' has '{name}' only where {unmet}"
            : null;

        // Then the bodies the enclosing type itself was declared in.
        if (LocalTypes(name, true) is { Count: > 0 } farther) {
            return BindMemberGroup(syntax, null, farther, null);
        }

        // A name that is there but hidden from here is reported as hidden,
        // not as missing, unless something visible answers to it after all.
        Symbol? hidden = null;

        var moduleMembers = Visible(_scope.Module.GetMembers(name), ref hidden);
        if (moduleMembers.Count > 0) {
            return BindMemberGroup(syntax, null, moduleMembers, null);
        }

        if (_scope.ImportedNames.TryGetValue(name, out var imported)) {
            return BindMemberGroup(syntax, null, [imported], null);
        }

        foreach (var module in _scope.Imports) {
            var candidates = Visible(module.GetMembers(name), ref hidden);
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
        if (_compilation.TypeBinder.Lookup(name, _containingSymbol, _scope, syntax.Span, report: false)
            is TypeParameterSymbol named) {
            // A value generic parameter, 'N: Int', is the number it stands for.
            return named.ValueType is { } valueType
                ? new BoundValueParameter(syntax, named, valueType)
                : new BoundTypeExpression(syntax, named);
        }

        if (unmetOnSelf is not null) {
            _diagnostics.Error(ErrorCode.ERR_DoesNotConform, syntax.Span, unmetOnSelf);
            return new BoundError(syntax);
        }

        if (hidden is not null) {
            ReportInaccessible(hidden, syntax);
            return new BoundError(syntax);
        }

        // A module is reached through, never held: 'M.gcd(a, b)', not 'M'.
        if (_compilation.ResolveModule(name, _scope) is { } namedModule) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAValue,
                syntax.Span,
                $"'{name}' names the module '{namedModule.Name}', which is not a value; reach its members with '.'"
            );
            return new BoundError(syntax);
        }

        _diagnostics.Error(
            ErrorCode.ERR_NameNotFound,
            syntax.Span,
            $"no symbol named '{name}' is in scope"
        );
        return new BoundError(syntax);
    }

    /// <summary>
    ///     Whether a name means something here other than a module: a local,
    ///     a member, or a declaration in this module, an import or the core.
    ///     Such a name hides a module of the same name, as it would hide a
    ///     declaration further out.
    /// </summary>
    bool NamesAValue(string name) {
        Symbol? hidden = null;
        return LookupLocal(name) is not null
            || LocalTypes(name, false).Count > 0
            || LocalTypes(name, true).Count > 0
            || EnclosingType() is { } enclosing && LookupMember(SelfTypeOf(enclosing), name) is not null
            || Visible(_scope.Module.GetMembers(name), ref hidden).Count > 0
            || _scope.ImportedNames.ContainsKey(name)
            || _scope.Imports.Any(module => Visible(module.GetMembers(name), ref hidden).Count > 0)
            || Core.Module.GetMembers(name).Any()
            || _compilation.TypeBinder.Lookup(name, _containingSymbol, _scope, default, report: false) is TypeParameterSymbol;
    }

    /// <summary>
    ///     The module an expression names, when it is a dotted name that does:
    ///     <c>Sable.Math</c>, or an alias imported for one.
    /// </summary>
    ModuleSymbol? ModuleNamedBy(ExpressionSyntax syntax) {
        var path = new List<string>();
        var here = syntax;
        while (here is MemberAccessExpressionSyntax { OperatorToken.Kind: SyntaxKind.DotToken, Name: IdentifierNameSyntax name } member) {
            path.Insert(0, name.Identifier.Text);
            here = member.Expression;
        }

        if (here is not IdentifierNameSyntax first || NamesAValue(first.Identifier.Text)) {
            return null;
        }

        path.Insert(0, first.Identifier.Text);
        return _compilation.ResolveModule(string.Join('.', path), _scope);
    }

    /// <summary>
    ///     <c>M.gcd</c> or <c>Sable.Collections.Deque&lt;Int&gt;</c>: a member of
    ///     a module named in full or through its alias, as it would be found
    ///     unqualified after importing the module — and only as visible as it is.
    /// </summary>
    BoundExpression BindModuleMember(MemberAccessExpressionSyntax syntax, ModuleSymbol module, string name) {
        Symbol? hidden = null;
        var members = Visible(module.GetMembers(name), ref hidden);
        if (members.Count == 0) {
            if (hidden is not null) {
                ReportInaccessible(hidden, syntax.Name);
            } else {
                _diagnostics.Error(
                    ErrorCode.ERR_NameNotFound,
                    syntax.Name.Span,
                    $"'{module.Name}' has no member named '{name}'"
                );
            }

            return new BoundError(syntax);
        }

        // 'Sable.Collections.Deque<Int>' names the type with its arguments.
        if (syntax.Name is GenericNameSyntax generic && members[0] is NamedTypeSymbol or AliasSymbol) {
            var type = _compilation.TypeBinder.BindGenericName(members[0], generic, _containingSymbol, _scope);
            return type.IsError ? new BoundError(syntax) : new BoundTypeExpression(syntax, type);
        }

        return BindMemberGroup(syntax, null, members, null);
    }

    /// <summary>A use of a declaration, which '@deprecated' warns of and '@available' may refuse.</summary>
    void ReportUse(Symbol used, SyntaxNode at) =>
        _compilation.ReportUse(_diagnostics, used, at.Span, _containingSymbol, _scope);

    BoundExpression BindMemberGroup(
        SyntaxNode syntax,
        BoundExpression? receiver,
        IReadOnlyList<Symbol> members,
        NamedTypeSymbol? containingType,
        TypeSubstitution? substitution = null
    ) {
        substitution ??= TypeSubstitution.Empty;

        // A property and methods of one name, as a collection's 'first' and
        // 'first(where:)': called, it is the methods; read, the property.
        if (members.Any(static m => m is FieldSymbol) && members.Any(static m => m is FunctionSymbol)) {
            var called = syntax.Parent is InvocationExpressionSyntax invocation && ReferenceEquals(invocation.Expression, syntax);
            members = called ? [..members.OfType<FunctionSymbol>()] : [..members.OfType<FieldSymbol>()];
        }

        // A function is reported once its overload is chosen.
        if (members is [not FunctionSymbol and var used, ..]) {
            ReportUse(used, syntax is MemberAccessExpressionSyntax { Name: var name } ? name : syntax);
        }

        var functions = members.OfType<FunctionSymbol>().ToImmutableArray();
        if (!functions.IsEmpty) {
            if (receiver is null && containingType is not null
                && functions.Any(static f => f is { IsStatic: false, FunctionKind: FunctionKind.Ordinary })
                && CapturedSelf(syntax, functions[0].Name) is { } captured) {
                receiver = captured;
            }

            return new BoundFunctionGroup(syntax, receiver, functions) { Substitution = substitution };
        }

        var member = members[0];
        switch (member) {
            case FieldSymbol field:
                if (receiver is null && !field.IsStatic && containingType is not null && CapturedSelf(syntax, field.Name) is { } captured) {
                    receiver = captured;
                }

                // A protocol's static property — a requirement, or one an
                // extension of it gives — belongs to the type it is read
                // through: 'T.zero' is the zero of whatever T stands for, and
                // only that type says which, so it is kept as the receiver.
                if (receiver is null && field.IsStatic
                    && containingType?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } protocolSelf }) {
                    receiver = new BoundTypeExpression(syntax, substitution.Lookup(protocolSelf) ?? protocolSelf);
                }

                if (receiver is null && !field.IsStatic && containingType is not null) {
                    // An array extension's member, reached from another's: self is this one's array.
                    // A protocol's, reached from one refining it: self is the refining one's Self.
                    var self = (containingType.ExtendedArray is not null || containingType.TypeKind == TypeKind.Protocol) && EnclosingType() is { } enclosing
                        ? SelfTypeOf(enclosing)
                        : SelfTypeOf(containingType);
                    receiver = new BoundSelfAccess(syntax, self, SelfIsMutable(containingType));
                }

                // A lazy property of a value is computed by its first read,
                // which writes it: the value must be one that can change.
                if (field.IsLazy && receiver is { IsAssignable: false } held && !held.Type.IsReferenceType) {
                    _diagnostics.Error(
                        ErrorCode.ERR_NotMutable,
                        syntax.Span,
                        $"lazy '{field.Name}' is computed the first time it is read, which changes the value, so it needs one that can change"
                    );
                }

                // An observed property, written in its own observers or in
                // its type's initialisers, is its storage: the observers run
                // for changes made once the value is made, and not again for
                // the changes they make themselves.
                if (field is { BackingField: { } storage }
                    && receiver is BoundSelfAccess or null
                    && _compilation.ObservedProperties.ContainsKey(field)
                    && EnclosingFunction() is { } within
                    && (within is { FunctionKind: FunctionKind.Accessor } && ReferenceEquals(within.ContainingSymbol, field)
                        || within is { FunctionKind: FunctionKind.Initializer } && ReferenceEquals(within.ContainingSymbol, field.ContainingSymbol))) {
                    field = storage;
                }

                // 'task.value' waits for the task.
                if (field.IsAsyncGetter) {
                    MarkAsync(syntax.Span, $"reading '{field.Name}'");
                }

                CheckIsolatedField(syntax, field);

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
    /// <summary>What a reference refers to, read: '-x' and 'x + 1' with 'x: Int&amp;'.</summary>
    static BoundExpression Dereferenced(BoundExpression expression) =>
        expression.Type is ReferenceTypeSymbol { RelativeWidth: null } reference
            ? new BoundConversion(expression.Syntax, reference.ReferencedType, expression, ConversionKind.Dereference)
            : expression;

    static bool IsPlace(BoundExpression expression) =>
        expression switch {
            BoundLocalAccess or BoundParameterAccess or BoundSelfAccess => true,
            BoundFieldAccess { Field.IsComputed: false, Field.IsStatic: false, Receiver: { } receiver } =>
                receiver.Type.TypeKind == TypeKind.Struct && IsPlace(receiver),
            BoundTupleElementAccess element => element.Receiver.Type is not OptionalTypeSymbol && IsPlace(element.Receiver),
            BoundConversion { ConversionKind: ConversionKind.Dereference } => true,
            BoundConversion { ConversionKind: ConversionKind.Identity } conversion => IsPlace(conversion.Operand),
            _ => false,
        };

    BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax) {
        // 'Sable.Math.gcd' and 'M.gcd': the left names a module, not a value.
        if (syntax.OperatorToken.Kind == SyntaxKind.DotToken && ModuleNamedBy(syntax.Expression) is { } module) {
            return BindModuleMember(
                syntax,
                module,
                syntax.Name switch {
                    IdentifierNameSyntax identifier => identifier.Identifier.Text,
                    GenericNameSyntax generic => generic.Identifier.Text,
                    _ => string.Empty,
                }
            );
        }

        var receiver = BindExpression(syntax.Expression);

        // A member of a reference is the member of what it refers to.
        if (receiver.Type is ReferenceTypeSymbol reference) {
            receiver = new BoundConversion(syntax.Expression, reference.ReferencedType, receiver, ConversionKind.Dereference);
        }
        // 's.`lazy`' names what 's.lazy' does: the member called 'lazy'.
        var name = syntax.Name switch {
            IdentifierNameSyntax identifier => identifier.Identifier.Value as string ?? identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Value as string ?? generic.Identifier.Text,
            _ => string.Empty,
        };

        // 'p.' with the name missing: the parser has said so.
        if (receiver.HasError || syntax.Name is IdentifierNameSyntax { Identifier.IsMissing: true }) {
            return new BoundError(syntax, receiver);
        }

        // A metatype value reaches its type's static members, as the type
        // does: 't.zero()' with 't: T.Type'. Its one value is the type, so
        // reading it has nothing to keep but what evaluating it does, which
        // a name or 'X.self' does not.
        if (receiver is BoundMetatype or BoundLocalAccess or BoundParameterAccess
            && receiver.Type is MetatypeTypeSymbol { InstanceType: var instanceType } && name != "self") {
            receiver = new BoundTypeExpression(syntax.Expression, instanceType);
        }

        // 'T.self' is the type itself, as a value of type 'T.Type'.
        if (receiver is BoundTypeExpression typeExpression) {
            if (name == "self") {
                return new BoundMetatype(syntax, typeExpression.ReferencedType);
            }

            // A type parameter reaches the static members of its constraints,
            // so the lookup is the same one a value uses.
            if (LookupMemberOfKind(typeExpression.ReferencedType, name, instance: false) is { } onType) {
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

        // 'self.init(…)', in an initialiser: the work handed to another of
        // the type's own initialisers, which builds this value in its place.
        if (name == "init" && receiver is BoundSelfAccess
            && EnclosingFunction() is { FunctionKind: FunctionKind.Initializer } initializer
            && initializer.ContainingSymbol is NamedTypeSymbol owner) {
            var initializers = owner.GetMembers("init").OfType<FunctionSymbol>().Where(i => !ReferenceEquals(i, initializer)).ToList();
            if (initializers.Count > 0) {
                return BindMemberGroup(syntax, receiver, initializers, owner, TypeSubstitution.For(receiver.Type));
            }
        }

        // 'x.count' of an 'x: String!' reaches the String it holds.
        if (syntax.OperatorToken.Kind != SyntaxKind.QuestionDotToken) {
            receiver = ImplicitlyUnwrapped(receiver);
        }

        var receiverType = receiver.Type;
        var optionalChain = false;

        if (receiverType is OptionalTypeSymbol optional) {
            // The optional's own members, lib/Sable.Core/Optional.sb's, are
            // reached with '.': 'p.map(f)' maps the optional, where 'p?.map'
            // would map what it holds.
            if (syntax.OperatorToken.Kind != SyntaxKind.QuestionDotToken
                && LookupMember(optional, name) is { } own) {
                var ownMembers = own.Members.Where(static m => !IsStaticMember(m)).ToList();
                if (ownMembers.Count > 0) {
                    return CheckAccessible(ownMembers[0], syntax.Name)
                        ? BindMemberGroup(syntax, receiver, ownMembers, own.Owner, own.Substitution)
                        : new BoundError(syntax, receiver);
                }
            }

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
            var element = BindTupleElementAccess(syntax, receiver, tuple, name);

            // 'pair?.0' is optional, as any member reached through '?.' is;
            // an element optional already is given as it is.
            return optionalChain && element is BoundTupleElementAccess { Type: not OptionalTypeSymbol } chained
                ? new BoundTupleElementAccess(syntax, new OptionalTypeSymbol(chained.Type), receiver, chained.Index)
                : element;
        }

        _unmetRequirement = null;
        if (LookupMemberOfKind(receiverType, name, instance: true) is not { } found) {
            if (_unmetRequirement is { } unmet) {
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

        // A property and methods may share a name, as String's 'count' and
        // 'count(of:)' do: called, the name is the methods; otherwise, the
        // property. Left together, every plain 'text.count' would be an
        // overload group with no call to choose from it.
        if (instanceMembers.Any(static m => m is FunctionSymbol) && instanceMembers.Any(static m => m is not FunctionSymbol)) {
            var called = syntax.Parent is InvocationExpressionSyntax invocation && invocation.Expression == syntax;
            instanceMembers = [..instanceMembers.Where(m => m is FunctionSymbol == called)];
        }

        // What this code cannot see is not a candidate: another module's
        // internal 'count(of:)' on every array is no reason Sable.Core's
        // 'names.count' should fail. Only when nothing of the name is
        // visible is that the error.
        if (instanceMembers.Where(IsAccessible).ToList() is { Count: > 0 } accessible) {
            instanceMembers = accessible;
        } else if (!CheckAccessible(instanceMembers[0], syntax.Name)) {
            return new BoundError(syntax, receiver);
        }

        // Through 'any P', Self and P's associated types are some conformer's,
        // and which one is not known: 'a.same(b)' could compare two different
        // types. A requirement that speaks of them is reached only through a
        // generic parameter, which names one conformer.
        if (_conversions.Equivalence.Canonical(receiverType) is ExistentialTypeSymbol
            && instanceMembers.Where(m => !SpeaksOfSelf(m)).ToList() is var callable
            && callable.Count < instanceMembers.Count) {
            if (callable.Count == 0) {
                _diagnostics.Error(
                    ErrorCode.ERR_SelfRequirementOnExistential,
                    syntax.Name.Span,
                    $"'{name}' takes or gives 'Self' or an associated type of '{found.Owner.ToDisplayString()}', "
                    + $"which '{receiverType.ToDisplayString()}' does not know; reach it through a generic parameter "
                    + $"'T: {found.Owner.ToDisplayString()}' instead"
                );
                return new BoundError(syntax, receiver);
            }

            instanceMembers = callable;
        }

        var member = BindMemberGroup(syntax, receiver, instanceMembers, found.Owner, found.Substitution);

        if (!optionalChain) {
            return member;
        }

        // Reaching through an optional gives an optional: '?.' can produce
        // nothing, and the type says so. A member that is optional already
        // says so itself, and the chain gives it as it is.
        return member switch {
            BoundFieldAccess field => new BoundFieldAccess(
                syntax,
                field.Receiver,
                field.Field,
                field.Type is OptionalTypeSymbol ? field.Type : new OptionalTypeSymbol(field.Type)
            ) {
                FlattensChain = field.Type is OptionalTypeSymbol,
            },

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

        ReportInaccessible(member, syntax);
        return false;
    }

    /// <summary>The members a file can see at the top level, noting the first it cannot.</summary>
    List<Symbol> Visible(IEnumerable<Symbol> members, ref Symbol? hidden) {
        var visible = new List<Symbol>();
        foreach (var member in members) {
            if (Visibility.IsVisible(member, _scope, _compilation)) {
                visible.Add(member);
            } else {
                hidden ??= member;
            }
        }

        return visible;
    }

    void ReportInaccessible(Symbol member, SyntaxNode syntax) =>
        _diagnostics.Error(
            ErrorCode.ERR_NotAccessible,
            syntax.Span,
            $"'{member.Name}' is {Visibility.Describe(member, member.DeclaredAccessibility)}"
        );

    /// <summary>
    ///     Of two methods that fit a call equally well, the one declared
    ///     closer to the type: on the type (or the array) itself rather than in
    ///     a protocol's extension, or in a protocol refining the other's.
    ///     Null when neither is.
    /// </summary>
    static FunctionSymbol? MoreSpecific(FunctionSymbol first, FunctionSymbol second) {
        static NamedTypeSymbol? OwnerOf(FunctionSymbol function) =>
            function.ContainingSymbol switch {
                ExtensionSymbol extension => extension.ExtendedType.OriginalDefinition,
                NamedTypeSymbol type => type.OriginalDefinition,
                _ => null,
            };

        if (OwnerOf(first) is not { } a || OwnerOf(second) is not { } b || ReferenceEquals(a, b)) {
            return null;
        }

        var aIsProtocol = a.TypeKind == TypeKind.Protocol;
        var bIsProtocol = b.TypeKind == TypeKind.Protocol;
        if (aIsProtocol != bIsProtocol) {
            return aIsProtocol ? second : first;
        }

        if (!aIsProtocol) {
            return null;
        }

        return Refines(a, b) ? first : Refines(b, a) ? second : null;

        static bool Refines(NamedTypeSymbol protocol, NamedTypeSymbol other) =>
            AssociatedTypes.RefinedProtocols(protocol).Any(p => ReferenceEquals(p, other));
    }

    /// <summary>What an overload is resolved for: a call, or a subscript read or stored through.</summary>
    enum SubscriptUse {
        None,
        Read,
        Store,
    }

    /// <summary>Added to a read-only subscript's cost where it is stored through, so that any that fits and can store wins.</summary>
    const int ReadOnlySubscriptCost = 1 << 20;

    /// <summary>Of two subscripts, the one with no setter, when the other has one; null when both or neither do.</summary>
    static FunctionSymbol? ReadOnlyOfTwo(FunctionSymbol first, FunctionSymbol second) =>
        (BoundIndexAccess.HasSetter(first), BoundIndexAccess.HasSetter(second)) switch {
            (false, true) => first,
            (true, false) => second,
            _ => null,
        };

    static FunctionSymbol? RequirementAndDefault(FunctionSymbol first, FunctionSymbol second) {
        static Symbol? Owner(FunctionSymbol f) => f.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : f.ContainingSymbol;
        static bool InExtension(FunctionSymbol f) => f.ContainingSymbol is ExtensionSymbol;

        if (Owner(first) is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol
            || !ReferenceEquals(Owner(second), protocol)
            || first.Parameters.Length != second.Parameters.Length
            || first.TypeParameters.Length != second.TypeParameters.Length
            || InExtension(first) == InExtension(second)) {
            return null;
        }

        // A generic requirement and its default each have parameters of
        // their own: 'replaceSubrange<C>' is the same method under either's C.
        var renamed = new TypeSubstitution(
            second.TypeParameters.Zip(first.TypeParameters)
                .ToDictionary(static p => p.First, static p => (TypeSymbol)p.Second)
        );
        if (!first.Parameters.Zip(second.Parameters).All(p => p.First.Type.Equals(renamed.Apply(p.Second.Type)))) {
            return null;
        }

        return InExtension(first) ? second : first;
    }

    bool IsAccessible(Symbol member) => IsAccessible(member, member.DeclaredAccessibility);

    bool IsAccessible(Symbol member, Accessibility accessibility) {
        if (accessibility != Accessibility.Private || member.ContainingSymbol is null or ModuleSymbol) {
            return Visibility.IsVisible(member, accessibility, _scope, _compilation);
        }

        // Private: somewhere inside the declaration that holds it. One an
        // extension declares is the extended type's, like the rest of what
        // the extension adds; and the type's extensions in the same module
        // see it as the type does — but not ones in another module, nor the
        // type itself when the extension that declared it is elsewhere.
        var owner = member.ContainingSymbol is ExtensionSymbol declaredIn
            ? declaredIn.ExtendedType
            : member.ContainingSymbol;
        var module = Visibility.ModuleOf(member);

        for (var here = _containingSymbol; here is not null; here = here.ContainingSymbol) {
            var type = here is ExtensionSymbol extension ? extension.ExtendedType : here;
            if (ReferenceEquals(type, owner)) {
                return ReferenceEquals(Visibility.ModuleOf(here), module);
            }
        }

        return false;
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
    ///     Set when a member was not found only because the type does not meet
    ///     the requirements of the extension that declares it — an array's
    ///     element its constraint, or a type's arguments a <c>where</c>
    ///     clause: what the requirement is.
    /// </summary>
    string? _unmetRequirement;

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
    /// <summary>
    ///     True for a protocol's requirement whose signature mentions the
    ///     protocol's <c>Self</c> or one of its associated types.
    /// </summary>
    static bool SpeaksOfSelf(Symbol member) {
        if (member.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
            return false;
        }

        var own = new HashSet<TypeParameterSymbol>(AssociatedTypes.All(protocol));
        if (protocol.SelfTypeParameter is { } self) {
            own.Add(self);
        }

        return member switch {
            FunctionSymbol function => function.Parameters.Any(p => Mentions(p.Type)) || Mentions(function.ReturnType),
            FieldSymbol field => Mentions(field.Type),
            _ => false,
        };

        bool Mentions(TypeSymbol type) =>
            type switch {
                TypeParameterSymbol parameter => own.Contains(parameter),
                DependentMemberTypeSymbol dependent => Mentions(dependent.BaseType) || own.Contains(dependent.Associated),
                NamedTypeSymbol named => named.TypeArguments.Any(Mentions),
                ArrayTypeSymbol array => Mentions(array.ElementType),
                OptionalTypeSymbol optional => Mentions(optional.UnderlyingType),
                MetatypeTypeSymbol metatype => Mentions(metatype.InstanceType),
                TupleTypeSymbol tuple => tuple.ElementTypes.Any(Mentions),
                FunctionTypeSymbol function => function.ParameterTypes.Any(Mentions) || Mentions(function.ReturnType),
                ReferenceTypeSymbol reference => Mentions(reference.ReferencedType),
                _ => false,
            };
    }

    /// <summary>
    ///     A lookup's members, with the same-named ones its protocols give that
    ///     none of them hides: overloads, told apart by their labels. A
    ///     sequence's 'count' property and its 'count(where:)' are both
    ///     there, as are a lazy view's 'prefix(while:)' and every sequence's
    ///     'prefix(_:)'; a member of the type's own, or of a more refined
    ///     protocol, hides one with the same labels.
    /// </summary>
    MemberLookup WithProtocolOverloads(
        MemberLookup found,
        IEnumerable<TypeSymbol> bases,
        string name,
        TypeSubstitution outer,
        HashSet<TypeSymbol> visited,
        TypeSymbol selfType,
        Func<Symbol, bool>? wanted
    ) {
        var members = found.Members.ToList();
        var substitution = found.Substitution;
        foreach (var baseType in bases) {
            if (baseType is not NamedTypeSymbol { TypeKind: TypeKind.Protocol }
                || LookupMemberCore(baseType, name, outer, visited, selfType, wanted) is not { } inherited) {
                continue;
            }

            var added = false;
            foreach (var member in inherited.Members) {
                if (!members.Any(own => Hides(own, member))) {
                    members.Add(member);
                    added = true;
                }
            }

            if (added) {
                substitution = Combine(substitution, inherited.Substitution);
            }
        }

        return members.Count == found.Members.Count ? found : found with { Members = members, Substitution = substitution };

        static bool Hides(Symbol own, Symbol other) =>
            (own, other) switch {
                // A subscript that only reads does not hide one that stores as
                // well: an array's 'subscript(fromEnd:)' is RandomAccessCollection's
                // to read and MutableCollection's to store, and a List's
                // 'subscript(range:)' its own to read and RangeReplaceableCollection's
                // to store. Which is meant is the overload's choice (ResolveOverload).
                (FunctionSymbol { FunctionKind: FunctionKind.Subscript } a, FunctionSymbol { FunctionKind: FunctionKind.Subscript } b)
                    when !BoundIndexAccess.HasSetter(a) && BoundIndexAccess.HasSetter(b) => false,

                // A concrete method does not hide a generic one of the same labels:
                // an array's 'append(contentsOf:)' of a slice, and every growable
                // collection's of any sequence, are both there.
                (FunctionSymbol a, FunctionSymbol b) => a.IsStatic == b.IsStatic && SameLabels(a, b) && a.TypeParameters.IsEmpty == b.TypeParameters.IsEmpty,
                (FieldSymbol a, FieldSymbol b) => a.IsStatic == b.IsStatic,
                _ => false,
            };

        static bool SameLabels(FunctionSymbol a, FunctionSymbol b) =>
            a.Parameters.Length == b.Parameters.Length
            && a.Parameters.Zip(b.Parameters).All(static p => (p.First.Label ?? p.First.Name) == (p.Second.Label ?? p.Second.Name));
    }

    MemberLookup? LookupMember(TypeSymbol type, string name, Func<Symbol, bool>? wanted = null) {
        // After 'where T == Int' a T has Int's members.
        type = _conversions.Equivalence.Canonical(type);
        var visited = new HashSet<TypeSymbol>();
        return LookupMemberCore(type, name, TypeSubstitution.Empty, visited, type, wanted);
    }

    /// <summary>
    ///     The members of the name of the kind the access wants — instance
    ///     members through a value, static ones through a type — looked for
    ///     past a type whose members of that name are all of the other kind:
    ///     'x.max(other: y)' is the instance method a protocol's extension
    ///     gives Int, though Int has a static 'max' of its own. Failing
    ///     that, the members of the name whatever their kind, for the
    ///     diagnostic that says which kind they are.
    /// </summary>
    MemberLookup? LookupMemberOfKind(TypeSymbol type, string name, bool instance) =>
        LookupMember(type, name, instance ? static m => !IsStaticMember(m) : IsStaticMember) ?? LookupMember(type, name);

    MemberLookup? LookupMemberCore(
        TypeSymbol type,
        string name,
        TypeSubstitution outer,
        HashSet<TypeSymbol> visited,
        TypeSymbol selfType,
        Func<Symbol, bool>? wanted = null
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
                        && !AssociatedTypes.IsProtocolSelf(selfType)
                        || selfType is ArrayTypeSymbol or OpaqueTypeSymbol) {
                        foreach (var associated in AssociatedTypes.All(named)) {
                            map[associated] = AssociatedTypes.Member(selfType, associated);
                        }
                    } else if (Unwrap(selfType) is NamedTypeSymbol conformer) {
                        // 'alias Item = T' names the conformer's own
                        // parameter, so Tagged<String> binds Item to String.
                        var own = TypeSubstitution.For(conformer);

                        foreach (var associated in AssociatedTypes.All(named)) {
                            if (ConformanceChecker.ResolveAssociated(conformer, associated)
                                is { } bound) {
                                map[associated] = own.Apply(bound);
                            }
                        }
                    }

                    substitution = Combine(substitution, new(map));
                }

                var members = named.GetMembers(name).Where(m => wanted is null || wanted(m)).ToList();

                if (members.Count > 0) {
                    // A constrained extension's member is there only where its
                    // requirements hold: Wrapper<T>'s 'doubled', from 'extension
                    // Wrapper where T == Int', is a Wrapper<Int>'s alone.
                    string? unmet = null;
                    var available = new List<Symbol>(members.Count);
                    foreach (var member in members) {
                        if (member.ContainingSymbol is ExtensionSymbol { IsConstrained: true } extension
                            && extension.Unmet(substitution, _conversions.Equivalence) is { } missing) {
                            unmet ??= missing;
                            continue;
                        }

                        available.Add(member);
                    }

                    if (available.Count > 0) {
                        return WithProtocolOverloads(
                            new MemberLookup(available, named, substitution),
                            named.BaseTypes.Select(substitution.Apply),
                            name,
                            substitution,
                            visited,
                            selfType,
                            wanted
                        );
                    }

                    _unmetRequirement = unmet;
                    return null;
                }

                // A base is read with this type's arguments first: through
                // 'Counted<String>', 'protocol Counted<U>: Source<U>' is a
                // Source<String>, whose T is then String.
                foreach (var baseType in named.BaseTypes) {
                    if (LookupMemberCore(substitution.Apply(baseType), name, substitution, visited, selfType, wanted)
                        is { } inherited) {
                        return inherited;
                    }
                }

                return null;
            }

            // An array's members are its extensions' (Compilation.ArrayExtensions.cs).
            case ArrayTypeSymbol array: {
                var found = _compilation.LookupArrayMember(array, name, _conversions.Equivalence);

                // What the protocols it conforms to give it as well: '[1,
                // 2].map(…)' is Sequence's, with Self the array.
                var protocols = _compilation.ArrayExtensions
                    .Where(carrier => !carrier.BaseTypes.IsEmpty && Compilation.MatchArray(carrier, array) is not null)
                    .SelectMany(static carrier => carrier.BaseTypes)
                    .Where(protocol => protocol is NamedTypeSymbol wanted && Compilation.ArrayConformance(array, wanted, _conversions.Equivalence) is not null)
                    .ToList();

                if (found is { Owner: { } owner } lookup && lookup.Members.Where(m => wanted is null || wanted(m)).ToList() is { Count: > 0 } kept) {
                    return WithProtocolOverloads(new MemberLookup(kept, owner, Combine(outer, lookup.Substitution)), protocols, name, outer, visited, selfType, wanted);
                }

                foreach (var protocol in protocols) {
                    if (LookupMemberCore(protocol, name, outer, visited, selfType, wanted) is { } inherited) {
                        return WithProtocolOverloads(inherited, protocols, name, outer, visited, selfType, wanted);
                    }
                }

                if (found is { Unmet: { } unmet }) {
                    _unmetRequirement = unmet;
                }

                return null;
            }

            case TypeParameterSymbol or DependentMemberTypeSymbol:
                foreach (var constraint in AssociatedTypes.ConstraintsOf(type, _conversions.Equivalence)) {
                    if (LookupMemberCore(constraint, name, outer, visited, selfType, wanted) is { } found) {
                        return found;
                    }
                }

                return null;

            // An optional's members are Optional's, its Wrapped what it holds.
            case OptionalTypeSymbol optional:
                return LookupMemberCore(Core.Optional.Construct([optional.UnderlyingType]), name, outer, visited, selfType);

            case OpaqueTypeSymbol opaque:
                return LookupMemberCore(opaque.Constraint, name, outer, visited, selfType, wanted);

            case ExistentialTypeSymbol existential:
                return LookupMemberCore(existential.Constraint, name, outer, visited, selfType, wanted);

            case CompositionTypeSymbol composition:
                foreach (var part in composition.Parts) {
                    if (LookupMemberCore(part, name, outer, visited, selfType, wanted) is { } found) {
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
        // 's.`lazy`' names what 's.lazy' does: the member called 'lazy'.
        var name = syntax.Name switch {
            IdentifierNameSyntax identifier => identifier.Identifier.Value as string ?? identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Value as string ?? generic.Identifier.Text,
            _ => string.Empty,
        };

        // The context may ask for an optional, and '.Some' is rarely what is
        // meant; the case belongs to what it wraps.
        var wanted = expectedType is OptionalTypeSymbol optional
            ? optional.UnderlyingType
            : expectedType;

        // ... unless what it wraps has no such case: then '.None' is 'none'.
        // ('.Some(x)' is a call, bound by BindOptionalSome.)
        if (expectedType is OptionalTypeSymbol noneOf && name == "None" && !HasMember(wanted, name)) {
            return new BoundLiteral(syntax, noneOf, null);
        }

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

    static bool HasMember(TypeSymbol? type, string name) =>
        type is NamedTypeSymbol named && named.GetMembers(name).Any();

    /// <summary>
    ///     <c>.Some(x)</c> where an optional is wanted, and what it wraps has no
    ///     <c>Some</c> of its own: <c>x</c>, wrapped. Null for any other call.
    /// </summary>
    BoundExpression? BindOptionalSome(InvocationExpressionSyntax syntax, TypeSymbol? expectedType) {
        if (syntax.Expression is not ImplicitMemberAccessExpressionSyntax { Name: IdentifierNameSyntax { Identifier.Text: "Some" } }
            || expectedType is not OptionalTypeSymbol optional
            || HasMember(optional.UnderlyingType, "Some")) {
            return null;
        }

        if (syntax.ArgumentList is not { Arguments: [{ NameToken.IsNone: true } argument] } || syntax.TrailingClosure is not null
            || syntax.LabeledClosures.Count > 0) {
            _diagnostics.Error(
                ErrorCode.ERR_NoMatchingOverload,
                syntax.Span,
                "'.Some' takes one value, the one the optional holds, with no label"
            );
            return new BoundError(syntax);
        }

        var value = BindExpression(argument.Expression, optional.UnderlyingType);
        return Convert(Convert(value, optional.UnderlyingType, argument.Expression), optional, syntax);
    }

    BoundExpression BindInvocation(
        InvocationExpressionSyntax syntax,
        TypeSymbol? expectedType = null
    ) {
        if (BindOptionalSome(syntax, expectedType) is { } wrapped) {
            return wrapped;
        }

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

        // 'both { 1 } onFail: { 2 }': the closures after the first carry
        // their labels, as arguments in parentheses would.
        foreach (var labeled in syntax.LabeledClosures) {
            argumentSyntax.Add(labeled.Closure);
            labelBuilder.Add(labeled.NameToken.Text);
        }

        var syntaxes = argumentSyntax.ToImmutable();
        var labels = labelBuilder.ToImmutable();

        var arguments = syntaxes
            .Select(e => IsClosure(e) ? new BoundUnboundClosure(e) : BindExpression(e))
            .ToImmutableArray();

        var callee = BindCallee(syntax.Expression, expectedType);
        if (syntax.QuestionToken.IsNone) {
            callee = ImplicitlyUnwrapped(callee);
        }

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
                return call.HasError || call.Type is OptionalTypeSymbol
                    ? call
                    : new BoundConversion(
                        syntax,
                        new OptionalTypeSymbol(call.Type),
                        call,
                        ConversionKind.OptionalWrap
                    );
            }

            // 'T(seed: 4)' for T: Makeable: the protocol's initialiser
            // requirement, met by whatever T turns out to be. 'Int[>..]()' is
            // RangeReplaceableCollection's 'init()', as a growable array meets it.
            case BoundTypeExpression typeExpression
                when AssociatedTypes.IsAbstract(typeExpression.ReferencedType)
                     || typeExpression.ReferencedType is ArrayTypeSymbol: {
                var found = LookupMember(typeExpression.ReferencedType, "init");
                var requirements = found is { } lookup
                    ? lookup.Members.OfType<FunctionSymbol>().Where(static f => f.FunctionKind == FunctionKind.Initializer).ToImmutableArray()
                    : [];

                if (found is null || requirements.IsEmpty) {
                    _diagnostics.Error(
                        ErrorCode.ERR_NotCallable,
                        syntax.Expression.Span,
                        $"'{typeExpression.ReferencedType.ToDisplayString()}' is not callable: "
                        + "nothing it is constrained to requires an initialiser"
                    );
                    return new BoundError(syntax, arguments.ToArray());
                }

                var group = new BoundFunctionGroup(syntax, typeExpression, requirements) { Substitution = found.Value.Substitution };
                return BindCallToGroup(syntax, group, arguments, labels, syntaxes, expectedType);
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

                // Its initialiser is a requirement, made by a conformer's.
                if (namedType.TypeKind == TypeKind.Protocol) {
                    _diagnostics.Error(
                        ErrorCode.ERR_CannotInstantiate,
                        syntax.Expression.Span,
                        $"'{namedType.Name}' is a protocol, so it has no values of its own; "
                        + "a type that conforms to it makes them"
                    );
                    return new BoundError(syntax, arguments.ToArray());
                }

                // A generic type's initialiser sees the type's parameters as the
                // type was written — 'Box<String>(…)' — or as its arguments say
                // they must be — 'Box("z")'.
                var constructed = namedType;
                if (namedType is { TypeArguments.IsEmpty: true, TypeParameters.IsEmpty: false }) {
                    var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
                    var fromContext = false;
                    if (Expand(expectedType) is NamedTypeSymbol { IsConstructed: true } wanted
                        && ReferenceEquals(wanted.OriginalDefinition, namedType.OriginalDefinition)) {
                        fromContext = true;
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

                        // Arguments written in a type were checked where the
                        // type was bound; ones inferred from the initialiser's
                        // arguments have to meet the same constraints and
                        // 'where' clause, or 'Box(t: notAShape)' makes a Box
                        // whose methods call what its T does not have.
                        if (!fromContext) {
                            _compilation.TypeBinder.CheckTypeArguments(
                                namedType,
                                constructed.TypeArguments,
                                syntax.Span,
                                _containingSymbol
                            );
                        }
                    }
                }

                var group = new BoundFunctionGroup(syntax, null, initializers) {
                    Substitution = constructed.TypeArguments.IsEmpty ? null : TypeSubstitution.For(constructed),
                };
                return BindCallToGroup(syntax, group, arguments, labels, syntaxes, expectedType);
            }

            // 'm(3)' on a value whose type declares 'operator ()'.
            case BoundExpression value when !value.HasError
                && BindOperatorCall(syntax, "()", value, arguments, labels, syntaxes) is { } called:
                return called;

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

            // What waited for a context — '[.Circle(radius: 1)]', '.None' —
            // has one now: the payload's type.
            var argument = arguments[i] is BoundUnboundClosure unbound
                ? BindExpression((ExpressionSyntax)unbound.Syntax, substitution.Apply(field.Type))
                : arguments[i];
            converted.Add(Convert(argument, substitution.Apply(field.Type), argument.Syntax));
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

            // Nor is '[.Circle(radius: 1), .Square]' until the parameter
            // says what its elements' leading dots name.
            ArrayExpressionSyntax array => array.Elements.Any(IsClosure),

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

            // '(inout Int) -> ()' called with '&n': n is changed where it is.
            if (type.IsInOut(i)) {
                CheckWritableArgument(bound[i], "inout");
            }

            CheckArgumentMarker(syntaxes[i], type.IsInOut(i) ? ParameterModifier.InOut : ParameterModifier.None, $"argument {i + 1}");

            converted.Add(Convert(bound[i], type.ParameterTypes[i], bound[i].Syntax));
        }

        // Before conversion, so that a closure that cannot throw is still
        // seen as one that cannot: converting it to the parameter's throwing
        // type would hide exactly the thing 'rethrows' is asking about.
        arguments = bound.ToImmutable();

        if (type.IsRethrows) {
            if (RethrownBy(arguments) is { } rethrown && !ThrowsNothing(rethrown)) {
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
        SyntaxNode syntax,
        BoundFunctionGroup group,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        ImmutableArray<ExpressionSyntax> syntaxes,
        TypeSymbol? expectedType = null
    ) {
        var outer = group.Substitution ?? TypeSubstitution.Empty;

        // 'Box.make(4)': a static member through the type named bare, whose
        // parameters the call infers. Inside Box, T is Box's own, and
        // inferring it from a T gives it back unchanged.
        var savedOwner = _ownerTypeParameters;
        _ownerTypeParameters = syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var typeSyntax } }
                               && _bound.TryGetValue(typeSyntax, out var named)
                               && named is BoundTypeExpression { ReferencedType: NamedTypeSymbol { IsConstructed: false, TypeParameters.IsEmpty: false } bare }
                               && outer.IsEmpty
                               && group.Candidates.All(c => c.IsStatic && ReferenceEquals(c.ContainingSymbol is ExtensionSymbol e ? e.ExtendedType : c.ContainingSymbol, bare))
            ? bare.TypeParameters
            : InitializerOwnerTypeParameters(group, outer);

        try {
            return BindResolvedCall(syntax, group, arguments, labels, syntaxes, expectedType, outer);
        } finally {
            _ownerTypeParameters = savedOwner;
        }
    }

    /// <summary>
    ///     The parameters of a generic type made bare by an initialiser with
    ///     type parameters of its own: <c>Groups(grouping: xs, by: f)</c>
    ///     infers Groups' K and V along with the initialiser's E, through its
    ///     arguments and its <c>where</c> clause, as a generic function's are.
    /// </summary>
    static ImmutableArray<TypeParameterSymbol> InitializerOwnerTypeParameters(BoundFunctionGroup group, TypeSubstitution outer) =>
        outer.IsEmpty
        && group.Receiver is null
        && group.Candidates.Any(static c => !c.TypeParameters.IsEmpty)
        && group.Candidates.All(static c => c.FunctionKind == FunctionKind.Initializer)
        && group.Candidates[0].ContainingSymbol is NamedTypeSymbol { IsConstructed: false, TypeParameters.IsEmpty: false, TypeKind: not TypeKind.Protocol } type
        && group.Candidates.All(c => ReferenceEquals(c.ContainingSymbol, type))
            ? type.TypeParameters
            : [];

    BoundExpression BindResolvedCall(
        SyntaxNode syntax,
        BoundFunctionGroup group,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        ImmutableArray<ExpressionSyntax> syntaxes,
        TypeSymbol? expectedType,
        TypeSubstitution outer
    ) {
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
        var substitution = Combine(outer, Infer(resolved, arguments, labels, outer, expectedType));
        var inferrable = resolved.TypeParameters.AddRange(_ownerTypeParameters);
        var slots = Align(resolved, labels, arguments.Length);

        // Now that a signature is chosen, the closures have parameter types.
        if (arguments.Any(static a => a is BoundUnboundClosure)) {
            var saved = _openTypeParameters;
            _openTypeParameters = [
                ..inferrable.Where(p => substitution.Lookup(p) is null)
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
            substitution = Combine(outer, Infer(resolved, arguments, labels, outer, expectedType));
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

            if (parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Out) {
                CheckWritableArgument(arguments[i], parameter);
            }

            if (i < syntaxes.Length) {
                CheckArgumentMarker(syntaxes[i], parameter.Modifier, $"'{parameter.Name}'");
            }

            // A parameter type inference could not settle is reported below,
            // once, rather than again as every argument that does not fit it.
            if (ContainsUninferred(wanted, resolved)) {
                converted.Add(arguments[i]);
                continue;
            }

            converted.Add(
                parameter is { IsByReference: true, IsVariadic: false } && wanted is ReferenceTypeSymbol shared
                    ? SharedArgument(arguments[i], shared)
                    : Convert(arguments[i], wanted, arguments[i].Syntax)
            );

            // A 'some P' parameter is a generic one, bound to its argument's
            // type: a generator's sequence holds what it was given, so its
            // result is known at the call as a generic one's would be.
            if (parameter.Type is OpaqueTypeSymbol { Origin: null } opaque
                && resolved.OpaqueTypeParameters.FirstOrDefault(t => opaque.Equals(t.OpaqueParameter)) is { } standsFor
                && substitution.Lookup(standsFor) is null
                && !arguments[i].Type.IsError) {
                substitution = Combine(substitution, new(new Dictionary<TypeParameterSymbol, TypeSymbol> { [standsFor] = Decayed(parameter.Type, arguments[i].Type) }));
            }
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
            } else if (DefaultArgument(syntax, resolved.Parameters[p], substitution) is { } value) {
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
            && inferrable.Any(p => substitution.Lookup(p) is null)) {
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
            foreach (var parameter in inferrable) {
                if (substitution.Lookup(parameter) is { } bound) {
                    map[parameter] = bound;
                }
            }

            TypeSubstitution.Unify(resolved.ReturnType, expectedType, inferrable, map);

            substitution = Combine(outer, new(map));
        }

        // A parameter left uninferred because an argument was an error has
        // had its error reported. One left so by 'none' has not: 'id(none)'
        // says nothing about what it is optional of. A bare generic type's
        // parameter nothing mentions stays open: 'Task.yield()' says nothing
        // of Task's Success, and needs nothing.
        var uninferred = false;
        if (!resolved.TypeParameters.IsEmpty) {
            var argumentError = arguments.Any(static a =>
                a is not BoundUnboundClosure && TypeSubstitution.ContainsError(a.Type) && !IsNone(a)
            );
            foreach (var typeParameter in resolved.TypeParameters) {
                if (substitution.Lookup(typeParameter) is not null) {
                    continue;
                }

                uninferred = true;
                if (argumentError) {
                    continue;
                }

                _diagnostics.Error(
                    ErrorCode.ERR_CannotInferTypeArgument,
                    syntax.Span,
                    arguments.Any(IsNone)
                        ? $"cannot infer '{typeParameter.Name}' from these arguments: 'none' alone does not say "
                        + "what it is optional of, so give the call a type to produce, as in 'let x: Int? = ...'"
                        : $"cannot infer '{typeParameter.Name}' from these arguments"
                );
            }
        }

        // A type parameter is Copyable unless it says '~Copyable': a move-only
        // argument is refused here, at the call, not later inside the body
        // that copies it. An initialiser's are its type's.
        var parametersOfCall = resolved.FunctionKind == FunctionKind.Initializer && resolved.ContainingSymbol is NamedTypeSymbol { TypeArguments.IsEmpty: true } constructedType
            ? [..resolved.TypeParameters, ..constructedType.TypeParameters]
            : resolved.TypeParameters;
        foreach (var typeParameter in parametersOfCall) {
            if (substitution.Lookup(typeParameter) is { } argument
                && Copyability.Refusal(typeParameter, argument, resolved.FunctionKind == FunctionKind.Initializer ? resolved.ContainingSymbol!.Name : resolved.Name) is { } refusal) {
                _diagnostics.Error(ErrorCode.ERR_NoncopyableTypeArgument, syntax.Span, refusal);
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

        if (IsGenerator(resolved)) {
            // A generator's call only captures its arguments: what throws is 'next()'.
        } else if (resolved.IsRethrows) {
            // Not what the signature says it may throw, but what these
            // arguments actually can: 'withLock(m) { 1 }' does not throw.
            // A 'rethrows(E)' throws E as well, here what E is for this
            // call: 'xs.map { … }' over an array throws nothing.
            var also = resolved.RethrownAlso is { } own ? substitution.Apply(own) : null;
            var rethrown = RethrownBy(arguments);
            if (TypeBinder.ErrorUnion(also, rethrown is null ? null : substitution.Apply(rethrown)) is { } thrownHere && !ThrowsNothing(thrownHere)) {
                MarkThrows(
                    syntax.Span,
                    $"'{resolved.Name}'",
                    thrownHere,
                    (rethrown is null || ThrowsNothing(rethrown) || PassesOwnParameter(arguments))
                    && (also is null || ThrowsNothing(also) || FitsRethrownAlso(also))
                );
            }
        } else if (resolved.ThrownType is { } thrown && !ThrowsNothing(substitution.Apply(thrown))) {
            // 'throws(E)' with E inferred as Never, from a closure that
            // cannot throw, cannot throw either.
            MarkThrows(syntax.Span, $"'{resolved.Name}'", substitution.Apply(thrown));
        }

        // An async generator's call only captures its arguments: what waits is 'next()'.
        if (resolved.IsAsync && !IsGenerator(resolved)) {
            MarkAsync(syntax.Span, $"'{resolved.Name}'");
        } else if (CrossesIntoActor(resolved)) {
            MarkAsync(syntax.Span, $"'{resolved.Name}', isolated to {(OnMainActor(resolved) ? "the main actor" : "its actor")},");
        }

        // A task's body runs alongside the code that made it: what it captures crosses.
        if (converted.Count == resolved.Parameters.Length) {
            for (var i = 0; i < converted.Count; i++) {
                if (resolved.Parameters[i].IsSendable) {
                    CheckSendableClosure(converted[i], $"'{resolved.Name}'");
                }
            }
        }

        // What goes into the actor, and what comes back out, crosses.
        if (CrossesIntoActor(resolved)) {
            for (var i = 0; i < converted.Count; i++) {
                CheckSendable(converted[i].Type, converted[i].Syntax.Span, $"argument {i + 1} of '{resolved.Name}'", resolved);
            }

            CheckSendable(substitution.Apply(resolved.ReturnType), syntax.Span, $"the result of '{resolved.Name}'", resolved);
        }

        var result = substitution.Apply(resolved.ReturnType);

        // What an uninferred parameter would have made the result is unknown,
        // and saying so once is enough.
        if (uninferred && ContainsUninferred(result, resolved)) {
            result = ErrorTypeSymbol.Instance;
        }

        // An initialiser of a generic type gives the type its arguments made:
        // 'Box<String>(…)' is a Box<String>, not the definition.
        if (resolved.FunctionKind == FunctionKind.Initializer
            && result is NamedTypeSymbol { TypeArguments.IsEmpty: true, TypeParameters.IsEmpty: false } definition
            && definition.TypeParameters.All(p => substitution.Lookup(p) is not null)) {
            result = definition.Construct([..definition.TypeParameters.Select(p => substitution.Lookup(p)!)]);

            // Inferred with the initialiser's own parameters, as in
            // 'Groups(grouping: xs, by: f)': they meet the type's
            // constraints as ones inferred before the call do.
            if (!_ownerTypeParameters.IsEmpty) {
                _compilation.TypeBinder.CheckTypeArguments(definition, ((NamedTypeSymbol)result).TypeArguments, syntax.Span, _containingSymbol);
            }
        }

        ReportUnmetDerivation(resolved, group.Receiver?.Type, syntax);
        ReportUse(resolved, group.Syntax is MemberAccessExpressionSyntax { Name: var name } ? name : group.Syntax);
        CheckUnsafeUse(resolved, syntax, "call it");

        var call = new BoundCall(syntax, group.Receiver, resolved, converted.ToImmutable(), result) {
            Substitution = substitution,
        };

        // A call through '?.' that gives an optional already gives that one.
        return group.IsOptionalChain && result is not OptionalTypeSymbol
            ? new BoundConversion(syntax, new OptionalTypeSymbol(result), call, ConversionKind.OptionalWrap)
            : call;
    }

    /// <summary>
    ///     The defaults being put into a call right now, outermost first, each
    ///     known by the parameter's or field's declaration — the same whatever
    ///     a generic is instantiated with. A default is bound afresh at every
    ///     call that leaves it off, so one whose value makes such a call again
    ///     would be put in without end.
    /// </summary>
    ImmutableArray<(SyntaxNode Key, string Name)> _defaultsBeingBound = [];

    /// <summary>The first of <see cref="_defaultsBeingBound" /> is the declaration this binder binds, not a call that left it off.</summary>
    bool _bindsDeclaredDefault;

    /// <summary>The binder whose call started <see cref="_defaultsBeingBound" />, and that call: where a cycle is reported.</summary>
    (Binder Binder, SyntaxNode Call)? _defaultOrigin;

    /// <summary>
    ///     Makes this the binder of a parameter's default value or a stored
    ///     field's initial value, as declared: a default that needs itself is
    ///     reported here, once, rather than at every call that leaves it off.
    /// </summary>
    internal Binder ForDeclaredDefault(Symbol owner) {
        if (owner.DeclaringSyntax is [var key]) {
            _defaultsBeingBound = [(key, owner.Name)];
            _bindsDeclaredDefault = true;
        }

        return this;
    }

    /// <summary>
    ///     A left-off parameter's default value, bound afresh for this call in
    ///     the file and the function that declare it — the default names what
    ///     is visible there, not at the call. Its diagnostics were reported
    ///     once, with the declaration, so they are not reported again here.
    /// </summary>
    /// <remarks>
    ///     A default that is a place in the source alone — <c>#file</c>,
    ///     <c>#line</c>, <c>#column</c>, <c>#function</c> — is the call's
    ///     place instead, as Swift has it: that is how <c>precondition</c>
    ///     and <c>fatalError</c> say where they were called from.
    /// </remarks>
    BoundExpression? DefaultArgument(SyntaxNode call, ParameterSymbol parameter, TypeSubstitution substitution) {
        if (MemberwiseField(parameter) is { } field) {
            return FieldDefault(call, field, substitution.Apply(parameter.Type));
        }

        if (parameter.DeclaringSyntax is not [ParameterSyntax { DefaultValue: { } clause } syntax]
            || _compilation.FileScopeOf(syntax) is not { } scope
            || parameter.ContainingSymbol is not { } container) {
            return null;
        }

        if (clause.Value is BuiltinExpressionSyntax { NameToken.Text: var name } && name is "file" or "line" or "column" or "function") {
            var type = name is "line" or "column" ? Core.Int : Core.String;
            return Convert(new BoundBuiltin(call, type, name), substitution.Apply(parameter.Type), call);
        }

        if (AlreadyDefaulting(call, parameter.Name, syntax)) {
            return new BoundError(call);
        }

        return Within(new Binder(_compilation, scope, container), call, syntax, parameter.Name).BindInitializer(clause.Value, substitution.Apply(parameter.Type));
    }

    /// <summary>
    ///     True when the default declared at <paramref name="key" /> is already
    ///     being put in further out: a call in it leaves it off again —
    ///     <c>var b: Int = S(a: 2).b</c> in S, or two functions whose defaults
    ///     call each other — so it has no value to put in (S3412). The
    ///     declaration's own binder reports it; a call elsewhere just gets an
    ///     error in its place.
    /// </summary>
    bool AlreadyDefaulting(SyntaxNode call, string name, SyntaxNode key) {
        var at = _defaultsBeingBound.Select(static d => d.Key).ToList().IndexOf(key);
        if (at < 0) {
            return false;
        }

        var (origin, originCall) = _defaultOrigin ?? (this, call);
        if (at == 0 && origin._bindsDeclaredDefault
            && !origin._diagnostics.Any(d => d.Code == ErrorCode.ERR_DefaultValueCycle && d.Span == originCall.Span)) {
            var path = string.Join(" → ", _defaultsBeingBound.Select(static d => d.Name).Append(name).Select(static n => $"'{n}'"));
            origin._diagnostics.Error(
                ErrorCode.ERR_DefaultValueCycle,
                originCall.Span,
                $"the default value of '{name}' needs itself ({path}): a default is put into each call that leaves it off, so this one would be put in without end"
            );
        }

        return true;
    }

    /// <summary>A binder for the default declared at <paramref name="key" />, put in at <paramref name="call" />: it knows every default being put in around it.</summary>
    Binder Within(Binder inner, SyntaxNode call, SyntaxNode key, string name) {
        inner._defaultsBeingBound = _defaultsBeingBound.Add((key, name));
        inner._defaultOrigin = _defaultOrigin ?? (this, call);
        return inner;
    }

    /// <summary>
    ///     The field a synthesised memberwise initialiser's parameter stands
    ///     for, when that field has an initial value to default to.
    /// </summary>
    static FieldSymbol? MemberwiseField(ParameterSymbol parameter) =>
        parameter is { HasDefaultValue: true, DeclaringSyntax.IsEmpty: true, ContainingSymbol: FunctionSymbol { FunctionKind: FunctionKind.Initializer, DeclaringSyntax.IsEmpty: true, ContainingSymbol: NamedTypeSymbol type } }
            ? type.GetMembers(parameter.Name).OfType<FieldSymbol>().FirstOrDefault(static f => f.HasInitializer)
            : null;

    /// <summary>
    ///     A field's initial value, as a memberwise initialiser left it to:
    ///     bound afresh in the file that declares the field, as a parameter's
    ///     default is. A lazy property's storage starts as none.
    /// </summary>
    BoundExpression? FieldDefault(SyntaxNode call, FieldSymbol field, TypeSymbol type) {
        if (field.DeclaringSyntax is not [FieldDeclarationSyntax declaration]
            || _compilation.FileScopeOf(declaration) is not { } scope) {
            return null;
        }

        if (field.BackedProperty is { IsLazy: true }) {
            return new BoundLiteral(declaration.Declarator, type, null);
        }

        if (declaration.Declarator.Initializer is not { } initializer) {
            return null;
        }

        return AlreadyDefaulting(call, field.Name, declaration)
            ? new BoundError(call)
            : Within(new Binder(_compilation, scope, field), call, declaration, field.Name).BindInitializer(initializer.Value, type);
    }

    /// <summary>
    ///     The parameters of the generic type a static member is called
    ///     through, when the call names the type bare: in <c>Box.make(4)</c> the
    ///     call infers Box's T, as it does a generic function's own.
    /// </summary>
    ImmutableArray<TypeParameterSymbol> _ownerTypeParameters = [];

    /// <summary>Works out a generic function's type arguments from the call.</summary>
    /// <remarks>
    ///     What a numeric literal says is weakest: it fits many types, so it
    ///     only decides a parameter nothing else did. First the other
    ///     arguments, then the type the context wants back —
    ///     <c>let r: I8[&gt;..] = wrap(3)</c> makes T an I8 — and only then the
    ///     literals, where a float among them wins over an integer:
    ///     <c>pick(1, 2.5)</c> is a pick of F64s.
    /// </remarks>
    TypeSubstitution Infer(
        FunctionSymbol function,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        TypeSubstitution outer,
        TypeSymbol? expectedType = null
    ) {
        var inferrable = function.TypeParameters.AddRange(_ownerTypeParameters);
        if (inferrable.IsEmpty) {
            return TypeSubstitution.Empty;
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        var slots = Align(function, labels, arguments.Length);
        var literals = new List<(TypeSymbol Wanted, BoundExpression Argument)>();
        for (var i = 0; i < arguments.Length; i++) {
            if (slots[i] >= function.Parameters.Length) {
                break;
            }

            var wanted = outer.Apply(function.Parameters[slots[i]].Type);
            if (IsNumericLiteral(arguments[i])) {
                literals.Add((wanted, arguments[i]));
                continue;
            }

            TypeSubstitution.Unify(wanted, AdaptForInference(wanted, arguments[i]), inferrable, map);
        }

        if (expectedType is not null && inferrable.Any(p => !map.ContainsKey(p))) {
            InferFromContext(outer.Apply(function.ReturnType), expectedType, inferrable, map);
        }

        InferFromSameTypeRequirements(function, outer, inferrable, map);

        foreach (var (wanted, argument) in literals) {
            // A float literal where an integer one decided: both are floats.
            if (wanted is TypeParameterSymbol parameter && map.TryGetValue(parameter, out var earlier)
                && earlier.Equals(Core.Int) && argument.Type.Equals(Core.F64)
                && literals.Any(l => ReferenceEquals(l.Wanted, wanted) && !ReferenceEquals(l.Argument, argument))) {
                map[parameter] = Core.F64;
                continue;
            }

            TypeSubstitution.Unify(wanted, argument.Type, inferrable, map);
        }

        // 'apply { 7 }' for 'apply<T, E>(_ f: () throws(E) -> T)': a closure
        // that cannot throw throws nothing, so E is Never.
        for (var i = 0; i < arguments.Length && i < slots.Length; i++) {
            if (slots[i] < function.Parameters.Length
                && outer.Apply(function.Parameters[slots[i]].Type) is FunctionTypeSymbol { ThrownType: TypeParameterSymbol thrown }
                && inferrable.Contains(thrown)
                && !map.ContainsKey(thrown)
                && AdaptForInference(function.Parameters[slots[i]].Type, arguments[i]) is FunctionTypeSymbol { ThrownType: null }) {
                map[thrown] = Core.Never;
            }
        }

        return new(map);
    }

    /// <summary>
    ///     What a <c>where A == B</c> says of a parameter the arguments left
    ///     open, once the other side is known: <c>init&lt;E&gt;(grouping: E[:])
    ///     where Value == E[&gt;..]</c> makes Value from E.
    /// </summary>
    static void InferFromSameTypeRequirements(
        FunctionSymbol function,
        TypeSubstitution outer,
        ImmutableArray<TypeParameterSymbol> inferrable,
        Dictionary<TypeParameterSymbol, TypeSymbol> map
    ) {
        if (function.SameTypeRequirements.IsEmpty) {
            return;
        }

        // Each pass may settle what the next needs: 'where A == B, B == E[>..]'.
        for (var pass = 0; pass < function.SameTypeRequirements.Length; pass++) {
            var before = map.Count;
            foreach (var (left, right) in function.SameTypeRequirements) {
                var known = Combine(outer, new(new Dictionary<TypeParameterSymbol, TypeSymbol>(map)));
                var (l, r) = (known.Apply(left), known.Apply(right));
                if (l is TypeParameterSymbol lp && inferrable.Contains(lp) && !map.ContainsKey(lp)) {
                    TypeSubstitution.Unify(l, r, inferrable, map);
                } else if (r is TypeParameterSymbol rp && inferrable.Contains(rp) && !map.ContainsKey(rp)) {
                    TypeSubstitution.Unify(r, l, inferrable, map);
                }
            }

            if (map.Count == before) {
                return;
            }
        }
    }

    /// <summary>
    ///     What the type the context wants back says of the parameters the
    ///     arguments left open — kept only where it meets their constraints,
    ///     since a context that converts, as an optional does, may ask for a
    ///     type the parameter could never be.
    /// </summary>
    void InferFromContext(
        TypeSymbol returnType,
        TypeSymbol expectedType,
        ImmutableArray<TypeParameterSymbol> inferrable,
        Dictionary<TypeParameterSymbol, TypeSymbol> map
    ) {
        var fromContext = new Dictionary<TypeParameterSymbol, TypeSymbol>(map);
        if (!TypeSubstitution.Unify(returnType, expectedType, inferrable, fromContext)) {
            return;
        }

        foreach (var (parameter, type) in fromContext) {
            if (map.ContainsKey(parameter)
                || type.IsError
                || !parameter.Constraints.All(c => Conversions.Satisfies(type, c, _conversions.Equivalence))) {
                continue;
            }

            map[parameter] = type;
        }
    }

    /// <summary>An integer or float literal: a value that could be several types.</summary>
    bool IsNumericLiteral(BoundExpression expression) =>
        expression is BoundLiteral { Type: var type } && (type.Equals(Core.Int) || type.Equals(Core.F64));

    /// <summary>
    ///     An argument's type as inference should see it.
    /// </summary>
    /// <remarks>
    ///     An array literal's storage kind comes from the context, so it must not
    ///     be the thing that stops a parameter being inferred. A named function
    ///     passed where a function is wanted, <c>map(xs, dbl)</c>, is the one
    ///     overload of that shape, as a function type.
    /// </remarks>
    static TypeSymbol AdaptForInference(TypeSymbol parameter, BoundExpression argument) {
        if (parameter is ArrayTypeSymbol wanted
            && argument is BoundArray { Type: ArrayTypeSymbol actual }
            && actual.ArrayKind != wanted.ArrayKind) {
            return new ArrayTypeSymbol(actual.ElementType, wanted.ArrayKind);
        }

        if (parameter is FunctionTypeSymbol function && argument is BoundFunctionGroup group) {
            var fitting = group.Candidates
                .Where(c => c.TypeParameters.IsEmpty && c.Parameters.Length == function.ParameterTypes.Length)
                .ToList();
            if (fitting.Count == 1) {
                return Apply(fitting[0].FunctionType, group.Substitution ?? TypeSubstitution.Empty);
            }
        }

        return Decayed(parameter, argument.Type);
    }

    /// <summary>
    ///     A reference passed where a value is wanted is the value it refers
    ///     to: 'show(choose(s))', with 'choose' giving a 'String&amp;', makes
    ///     'show&lt;T&gt;' a 'show&lt;String&gt;', as passing to a 'String'
    ///     parameter would read the string. Only a 'T&amp;' parameter takes the
    ///     reference itself.
    /// </summary>
    static TypeSymbol Decayed(TypeSymbol parameter, TypeSymbol argument) =>
        argument is ReferenceTypeSymbol { RelativeWidth: null } reference && parameter is not ReferenceTypeSymbol
            ? reference.ReferencedType
            : argument;

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
        TypeSubstitution outer,
        SubscriptUse use = SubscriptUse.None
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

            // A subscript stored through must be one that can store: one
            // with no 'set' is chosen only when none that fits has one, and
            // is then refused as read-only (BindAssignment).
            if (use == SubscriptUse.Store && !BoundIndexAccess.HasSetter(candidate)) {
                cost += ReadOnlySubscriptCost;
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

            // The type's own method beats what a protocol's extension gives
            // every conformer, and a more refined protocol's beats the one
            // it refines: an array's 'contains(element:)' is the one for it.
            if (MoreSpecific(best, candidate) is { } specific) {
                best = specific;
                continue;
            }

            // Two subscripts of unrelated protocols' extensions, one of which
            // can store as well: 'xs[^1]' on an array is RandomAccessCollection's
            // and MutableCollection's. Either reads the same element; a read
            // takes the one that only reads, which is written for reading.
            if (use == SubscriptUse.Read && ReadOnlyOfTwo(best, candidate) is { } reader) {
                best = reader;
                continue;
            }

            ambiguous = true;
            tied.Add(candidate);
        }

        // An argument that is already an error fits every candidate or none,
        // and either way its own error has been reported: 'print(missing())'
        // is not also ambiguous between every 'print'.
        // A closure, 'none' or '[]' not bound yet stands in with an error
        // type, and is no error.
        var argumentError = arguments.Any(static a => a is not BoundUnboundClosure && TypeSubstitution.ContainsError(a.Type));

        // Each rejected candidate keeps its reason, so the error can say what
        // was wrong rather than only that something was (docs/04 §1.2).
        if (best is null) {
            if (argumentError) {
                return null;
            }

            var name = candidates.IsEmpty ? "?" : candidates[0].Name;
            var message = candidates.Length == 1 && rejected.Count == 1
                ? $"'{name}' cannot be called with these arguments: {rejected[0].Reason}"
                : $"no overload of '{name}' takes these arguments"
                + string.Concat(rejected.Take(3).Select(static r => $"; '{Signature(r.Candidate)}': {r.Reason}"))
                + (rejected.Count > 3 ? $"; and {rejected.Count - 3} more" : string.Empty);

            _diagnostics.Error(ErrorCode.ERR_NoMatchingOverload, syntax.Span, message);
            return null;
        }

        if (ambiguous && !argumentError) {
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

            // A variadic parameter's label goes on the first of its elements
            // only, as in 'sum(of: 1, 2, 3)'; the rest follow unlabelled.
            if (parameter.IsVariadic && i > 0 && slots[i - 1] == slots[i]) {
                if (i < labels.Length && labels[i] is { } repeated) {
                    return Reject(
                        $"argument {i + 1} is labelled '{repeated}', and it is another element "
                        + $"of '{parameter.Name}', which takes its label only on the first"
                    );
                }
            } else if (i < labels.Length && labels[i] is { } label) {
                // A written label must match the parameter's.
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

            if (ContainsUninferred(leftType, candidate) || ContainsUninferred(rightType, candidate)
                || ContainsOwnerParameter(leftType) || ContainsOwnerParameter(rightType)) {
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
            if (ContainsUninferred(subjectType, candidate) || ContainsOwnerParameter(subjectType)) {
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
    ///     A memberwise initialiser's parameter for a field with an initial
    ///     value is passed over the same way: <c>D(b: 2)</c> leaves <c>a</c>
    ///     its initial value.
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
                       && (parameters[reach].DeclaringSyntax is [ParameterSyntax { DefaultValue: not null, EllipsisToken.IsNone: true }]
                           || MemberwiseField(parameters[reach]) is not null)
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
    /// <summary>Whether a type still names a parameter of the type a bare call infers (<see cref="_ownerTypeParameters" />).</summary>
    bool ContainsOwnerParameter(TypeSymbol type) =>
        !_ownerTypeParameters.IsEmpty && _ownerTypeParameters.Any(p => TypeSubstitution.Mentions(type, p));

    static bool ContainsUninferred(TypeSymbol type, FunctionSymbol candidate) =>
        type switch {
            TypeParameterSymbol parameter => candidate.TypeParameters.Contains(parameter),
            DependentMemberTypeSymbol member => ContainsUninferred(member.BaseType, candidate),
            NamedTypeSymbol named => named.TypeArguments.Any(a => ContainsUninferred(a, candidate)),
            OptionalTypeSymbol optional => ContainsUninferred(optional.UnderlyingType, candidate),
            MetatypeTypeSymbol metatype => ContainsUninferred(metatype.InstanceType, candidate),
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
    static bool IsLiteral(BoundExpression expression) =>
        expression is BoundLiteral or BoundArray or BoundDictionary
        || expression is BoundRange range && (range.Lower is null or BoundLiteral) && (range.Upper is null or BoundLiteral);

    /// <summary>The message for a value of one type where another is wanted.</summary>
    /// <remarks>
    ///     Two functions' <c>some Shape</c> results are two types that print
    ///     alike, so naming them alone reads "expected 'some Shape' but found
    ///     'some Shape'". Each is told apart by what chose its hidden type.
    /// </remarks>
    static string DescribeMismatch(TypeSymbol target, TypeSymbol found) {
        var wanted = target.ToDisplayString();
        var actual = found.ToDisplayString();
        if (wanted != actual) {
            return $"expected '{wanted}' but found '{actual}'";
        }

        if (target is OpaqueTypeSymbol wantedOpaque && found is OpaqueTypeSymbol foundOpaque) {
            var why = ReferenceEquals(wantedOpaque.Origin, foundOpaque.Origin)
                ? "each choice of its type parameters gives a type of its own"
                : $"each function's or variable's '{wanted}' is a type of its own";
            return $"expected the '{wanted}' {DescribeOpaqueOrigin(wantedOpaque)} but found the one "
                   + $"{DescribeOpaqueOrigin(foundOpaque)}; {why}";
        }

        return $"expected '{wanted}' but found a different type also written '{actual}'";
    }

    static string DescribeOpaqueOrigin(OpaqueTypeSymbol opaque) {
        // 'make(1)' and 'make("x")' return two types from one function.
        var context = opaque.Context.IsEmpty
            ? ""
            : " with "
              + string.Join(
                  ", ",
                  opaque.Context.Parameters.Select(p => $"{p.Name} = {opaque.Context.Lookup(p)!.ToDisplayString()}")
              );
        return opaque.Origin switch {
            FunctionSymbol { ContainingSymbol: TypeSymbol owner } method =>
                $"returned by '{owner.Name}.{method.Name}()'{context}",
            FunctionSymbol function => $"returned by '{function.Name}()'{context}",
            LocalSymbol local => $"of '{local.Name}'",
            _ => "of a parameter",
        };
    }

    BoundExpression BindElementAccess(ElementAccessExpressionSyntax syntax) {
        var receiver = BindExpression(syntax.Expression);

        // '[]' and 'none' are no particular type until the subscript chosen
        // says which: 'groups[key, default: []]'. They are bound once it is.
        var indices = syntax.ArgumentList.Arguments
            .Select(a => IsClosure(a.Expression) && receiver.Type is not ArrayTypeSymbol ? new BoundUnboundClosure(a.Expression) : BindExpression(a.Expression))
            .ToImmutableArray();

        if (receiver.HasError) {
            return new BoundError(syntax, [receiver, ..BindUnbound(indices)]);
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
        CheckEndAsElement(syntax);

        // 'xs[^1]' and 'xs[1..<^1]' are the collection protocols' subscripts,
        // an array's as any collection's, written over its own indexing.
        if (receiver.Type is ArrayTypeSymbol array && !IndexesThroughSubscript(indices)) {
            indices = BindUnbound(indices);

            // 'xs[1..<3]', 'xs[2...]', 'xs[..<2]': the elements in the range,
            // as a slice (SYNTAX.md §11).
            if (indices is [{ Type: NamedTypeSymbol { TypeArguments: [var bound] } range }] && Core.IsAnyRange(range) && bound.Equals(Core.Int)) {
                return new BoundIndexAccess(syntax, new ArrayTypeSymbol(array.ElementType, ArrayKind.Slice), receiver, indices);
            }

            indices = CheckIndices(indices, Core.Int, syntax);
            return new BoundIndexAccess(syntax, array.ElementType, receiver, indices);
        }

        // A type may declare its own indexing with 'subscript'.
        if (LookupMember(receiver.Type, "subscript") is { } found) {
            var group = new BoundFunctionGroup(syntax, receiver, [..found.Members.OfType<FunctionSymbol>()]) {
                Substitution = found.Substitution
            };

            if (!group.Candidates.IsEmpty) {
                // Stored through, 'xs[^1] = v' and 'xs[^1].x = v' want a
                // subscript with a setter, of those whose labels fit.
                var use = _assignedSpine.Contains(syntax) ? SubscriptUse.Store : SubscriptUse.Read;
                var resolved = ResolveOverload(group.Candidates, indices, [], syntax, found.Substitution, use);

                if (resolved is null) {
                    return new BoundError(syntax, [receiver, ..BindUnbound(indices)]);
                }

                if (MismatchedRange(syntax, receiver, indices, resolved)) {
                    return new BoundError(syntax, [receiver, ..indices]);
                }

                var converted = ImmutableArray.CreateBuilder<BoundExpression>(indices.Length);
                for (var i = 0; i < indices.Length && i < resolved.Parameters.Length; i++) {
                    var wanted = found.Substitution.Apply(resolved.Parameters[i].Type);
                    var index = indices[i] is BoundUnboundClosure unbound
                        ? BindExpression(unbound.Syntax as ExpressionSyntax ?? throw new InvalidOperationException(), wanted)
                        : indices[i];
                    converted.Add(Convert(index, wanted, index.Syntax));
                }

                return new BoundIndexAccess(
                    syntax,
                    found.Substitution.Apply(resolved.ReturnType),
                    receiver,
                    converted.ToImmutable()
                ) { Subscript = resolved, Substitution = found.Substitution };
            }
        }

        // Or with 'operator []', the alternative form (SYNTAX.md §13).
        if (BindOperatorCall(syntax, "[]", receiver, indices, [], [..syntax.ArgumentList.Arguments.Select(static a => a.Expression)]) is { } indexed) {
            return indexed;
        }

        _diagnostics.Error(
            ErrorCode.ERR_NotIndexable,
            syntax.Span,
            $"'{receiver.Type.ToDisplayString()}' cannot be indexed"
        );
        return new BoundError(syntax, [receiver, ..BindUnbound(indices)]);
    }

    /// <summary>
    ///     A range of one kind of index given to a slicing subscript, which
    ///     takes any RangeExpression, of a collection with another: a range
    ///     of Ints slicing a String, whose positions are String.Index. The
    ///     range's bounds are the collection's positions as they are, so
    ///     the two must be the same type.
    /// </summary>
    bool MismatchedRange(ElementAccessExpressionSyntax syntax, BoundExpression receiver, ImmutableArray<BoundExpression> indices, FunctionSymbol subscript) {
        if (indices is not [{ Type: NamedTypeSymbol { TypeArguments: [var bound] } range } index]
            || !Core.IsAnyRange(range)
            || subscript.Parameters is not [{ Type: var parameter }]
            || parameter is NamedTypeSymbol named && Core.IsAnyRange(named)
            || Core.Collection is not { } collection
            || AssociatedTypes.All(collection).FirstOrDefault(static a => a.Name == "Index") is not { } associated
            || !Conversions.Satisfies(receiver.Type, collection, _conversions.Equivalence)) {
            return false;
        }

        var position = AssociatedTypes.Member(receiver.Type, associated);
        if (position.IsError || bound.IsError || position is DependentMemberTypeSymbol || _conversions.Equivalence.AreSame(position, bound)) {
            return false;
        }

        _diagnostics.Error(
            ErrorCode.ERR_TypeMismatch,
            index.Syntax.Span,
            $"a range of '{bound.ToDisplayString()}' cannot slice '{receiver.Type.ToDisplayString()}', "
            + $"whose positions are '{Qualified(position)}'"
        );
        return true;

        // 'String.Index', not 'Index'.
        static string Qualified(TypeSymbol type) =>
            type switch {
                NamedTypeSymbol { ContainingSymbol: NamedTypeSymbol outer } => $"{Qualified(outer)}.{type.ToDisplayString()}",
                NamedTypeSymbol { ContainingSymbol: ExtensionSymbol extension } => $"{Qualified(extension.ExtendedType)}.{type.ToDisplayString()}",
                _ => type.ToDisplayString(),
            };
    }

    /// <summary>
    ///     An index an array does not take itself: a position from the end,
    ///     or a range that is not one of integers, which the subscripts every
    ///     collection has (Collection+FromEnd.sb, Array+Collection.sb) take.
    /// </summary>
    bool IndexesThroughSubscript(ImmutableArray<BoundExpression> indices) =>
        indices is [{ Type: var type }]
        && (Core.IsFromEnd(type)
            || !Core.IsAnyRange(type) && Core.RangeExpression is { } rangeExpression && type is NamedTypeSymbol
            && Conversions.Satisfies(type, rangeExpression, _conversions.Equivalence));

    /// <summary>
    ///     <c>xs[^0]</c>: the end, which is no element, so reading there
    ///     always traps. Written so, it is refused (S3090).
    /// </summary>
    void CheckEndAsElement(ElementAccessExpressionSyntax syntax) {
        if (syntax.ArgumentList.Arguments is not [{ Expression: var index }]) {
            return;
        }

        while (index is ParenthesizedExpressionSyntax parenthesized) {
            index = parenthesized.Expression;
        }

        if (index is PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.CaretToken, Operand: LiteralExpressionSyntax { Token: { Kind: SyntaxKind.IntegerLiteralToken } digits } }
            && digits.Value is 0L or 0 or 0UL) {
            _diagnostics.Error(
                ErrorCode.ERR_EndIsNoElement,
                index.Span,
                "'^0' is the end, past the last element, so there is no element to read there; "
                + "the last is '^1'"
            );
        }
    }

    /// <summary>Indices left unbound for a subscript's parameters to type, bound with none, where there is no subscript.</summary>
    ImmutableArray<BoundExpression> BindUnbound(ImmutableArray<BoundExpression> indices) =>
        [..indices.Select(i => i is BoundUnboundClosure { Syntax: ExpressionSyntax unbound } ? BindExpression(unbound) : i)];

    /// <summary>
    ///     <c>m["x"]</c> and <c>m(3)</c> on a type that declares
    ///     <c>operator []</c> or <c>operator ()</c>: a call to it, with the
    ///     value first, as a binary operator's left operand is. Null when the
    ///     type declares no such operator.
    /// </summary>
    BoundExpression? BindOperatorCall(
        SyntaxNode syntax,
        string symbol,
        BoundExpression receiver,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        ImmutableArray<ExpressionSyntax> syntaxes
    ) {
        if (receiver.Type is not NamedTypeSymbol named) {
            return null;
        }

        ImmutableArray<FunctionSymbol> candidates = [
            ..named.OriginalDefinition.Members.OfType<FunctionSymbol>()
                .Where(f => f.FunctionKind == FunctionKind.Operator && f.Name == symbol && f.Parameters.Length >= 1)
        ];
        if (candidates.IsEmpty) {
            return null;
        }

        var substitution = TypeSubstitution.For(named);
        ImmutableArray<BoundExpression> all = [receiver, ..arguments];
        ImmutableArray<string?> allLabels = labels.IsEmpty ? [] : [null, ..labels];
        if (ResolveOverload(candidates, all, allLabels, syntax, substitution) is not { } resolved) {
            return new BoundError(syntax, all.ToArray());
        }

        var converted = ImmutableArray.CreateBuilder<BoundExpression>(all.Length);
        for (var i = 0; i < all.Length && i < resolved.Parameters.Length; i++) {
            var wanted = substitution.Apply(resolved.Parameters[i].Type);
            var argument = all[i] is BoundUnboundClosure && !syntaxes.IsDefault
                ? BindExpression(syntaxes[i - 1], wanted)
                : all[i];
            converted.Add(Convert(argument, wanted, argument.Syntax));
        }

        return new BoundCall(syntax, null, resolved, converted.ToImmutable(), substitution.Apply(resolved.ReturnType)) {
            Substitution = substitution,
        };
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

        // '()' is the unit value, of the unit type 'Void' (TypeBinder).
        TypeSymbol type = elements.IsEmpty ? Core.Void : new TupleTypeSymbol([..elements.Select(static e => e.Type)], names);
        return new BoundTuple(syntax, type, elements);
    }

    BoundExpression BindArray(ArrayExpressionSyntax syntax, TypeSymbol? expectedType) {
        // '[.Read, .Write]' where a '@flags' enum is wanted: the flags, together.
        if (Expand(expectedType) is NamedTypeSymbol { IsFlags: true } flags) {
            return BindFlags(syntax, flags);
        }

        expectedType = TheCaseOf(expectedType, static t => t is ArrayTypeSymbol);

        // A parameter a 'where Value == E[>..]' makes an array is that array.
        if (expectedType is TypeParameterSymbol or DependentMemberTypeSymbol
            && _conversions.Equivalence.Canonical(expectedType) is ArrayTypeSymbol same) {
            expectedType = same;
        }

        var expectedElement = expectedType is ArrayTypeSymbol expectedArray
            ? expectedArray.ElementType
            : null;

        var elements = syntax.Elements
            .Select(e => BindExpression(e, expectedElement))
            .ToImmutableArray();

        // 'T[..k]' holds at most k.
        if (expectedType is ArrayTypeSymbol { ArrayKind: ArrayKind.Inline, Capacity: { } capacity } bounded && elements.Length > capacity) {
            _diagnostics.Error(
                ErrorCode.ERR_TypeMismatch,
                syntax.Span,
                $"'{bounded.ToDisplayString()}' holds at most {capacity} elements, and this literal has {elements.Length}"
            );
        }

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

            // The first element said what the array holds; the others that
            // differ get there by a conversion — '[j, true]' of a 'Json'
            // puts the Bool in the union — and the array holds what it made.
            if (!elementType.IsError) {
                elements = [..elements.Select(e => Convert(e, elementType, e.Syntax))];
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
            syntax.Header?.ThrowsSpec,
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
        ThrowsSpecSyntax? throwsSpec,
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

        // A closure passed where '() throws -> T' is wanted may throw. Where
        // the error type is the call's to infer, '() throws(E) -> T', it may
        // throw anything, and what it does throw is what E becomes.
        // One that says what it throws, 'throws(ParseError)', throws that,
        // whatever its body does; bare 'throws' is 'throws(any Error)'.
        var declaredThrown = BindClosureThrows(throwsSpec, symbol);
        var thrown = declaredThrown
            ?? (expected?.ThrownType is { } open && IsOpen(open) ? Core.AnyError : expected?.ThrownType);
        symbol.ThrownTypeResolver = () => thrown;

        // Captures are bound in the enclosing scope, before the closure's own
        // scope exists, because that is where their names are written.
        var captures = BindCaptures(captureList);

        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var count = declared.Count;

        for (var i = 0; i < count; i++) {
            var parameterSyntax = declared[i];

            // '(v: inout Int) in' changes the caller's Int where it is; so
            // does a 'v' whose type the context gives as '(inout Int) -> ()'.
            var inOut = !parameterSyntax.ModifierToken.IsNone
                || parameterSyntax.Type is null && expected is not null && expected.IsInOut(i);
            var parameter = new ParameterSymbol(parameterSyntax.Identifier.Text, null, symbol, parameterSyntax, i) {
                Modifier = inOut ? ParameterModifier.InOut : ParameterModifier.None,
            };

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
                var parameter = new ParameterSymbol($"${i}", null, symbol, null, i) {
                    Modifier = expected.IsInOut(i) ? ParameterModifier.InOut : ParameterModifier.None,
                };
                parameter.TypeResolver = () => implicitType;
                parameters.Add(parameter);
            }
        }

        symbol.Parameters = parameters.ToImmutable();

        var returnType =
            returnTypeSyntax is { } declaredReturn
                ? _compilation.TypeBinder.BindType(declaredReturn, symbol, _scope)
                : expected is not null && !MentionsOpen(expected.ReturnType)
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
            declaredThrown ?? _closureThrows
        ) { InOutParameters = FunctionTypeSymbol.InOutOf(symbol.Parameters.Select(static p => p.Modifier == ParameterModifier.InOut)) };

        return Record(syntax, new BoundClosure(syntax, type, symbol, captures, bound));
    }

    /// <summary>
    ///     What a closure's header says it throws, or null when it says
    ///     nothing. A closure does not rethrow: what it throws is in its type.
    /// </summary>
    TypeSymbol? BindClosureThrows(ThrowsSpecSyntax? spec, FunctionSymbol closure) {
        if (spec is null) {
            return null;
        }

        if (spec.ThrowsKeyword.Kind == SyntaxKind.RethrowsKeyword) {
            _diagnostics.Error(
                ErrorCode.ERR_ClosureRethrows,
                spec.Span,
                "a closure does not rethrow: write 'throws(E)' for the error it throws, "
                + "or leave it out and let its body say"
            );
            return null;
        }

        return spec.ErrorType is { } errorType
            ? _compilation.TypeBinder.BindType(errorType, closure, _scope)
            : Core.AnyError;
    }

    /// <summary>Binds a closure's body with its parameters in scope.</summary>
    BoundBlock BindClosureBody(
        FunctionSymbol symbol,
        SyntaxNode body,
        TypeSymbol returnType,
        bool inferring,
        ImmutableArray<BoundCapture> captures,
        bool tryAndAwait = false
    ) {
        var savedSymbol = _containingSymbol;
        var savedReturn = _returnType;
        var savedInferring = _inferringReturn;
        var savedLoopDepth = _loopDepth;
        var savedSwitchCases = _switchCases;
        var savedOpen = _openTypeParameters;
        var savedEffects = SaveEffects();

        _containingSymbol = symbol;
        _returnType = returnType;
        _inferringReturn = inferring;
        var savedGenerator = (_generator, _notGenerator, _deferDepth);
        (_generator, _notGenerator, _deferDepth) = (null, null, 0);

        // A closure whose result is 'some Sequence<T>' and whose body yields
        // is a generator, its result its own.
        if (Yields(body)) {
            if (!inferring && returnType is OpaqueTypeSymbol { Origin: null } closureOpaque) {
                _returnType = returnType = closureOpaque.WithOrigin(symbol);
            }

            (_generator, _notGenerator) = StartGenerator(symbol, inferring ? Core.Void : returnType, body);
        }

        _loopDepth = 0;
        _switchCases = [];
        _openTypeParameters = [];
        _declaredThrows = _generator is { } generating ? generating.BodyThrown : symbol.ThrownType;
        _rethrows = symbol.IsRethrows;
        _inAsyncContext = symbol.IsAsync;
        SeedRethrowSources(symbol);
        // An 'async let''s child is its initialiser, which needs neither
        // 'try' nor 'await': the reads of the 'async let' are marked instead.
        _tryDepth = tryAndAwait ? 1 : 0;
        _awaitDepth = tryAndAwait ? 1 : 0;
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
                    value = AsValue(value);
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
                        value = AsValue(value);
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
        _switchCases = savedSwitchCases;
        (_generator, _notGenerator, _deferDepth) = savedGenerator;
        _inferringReturn = savedInferring;
        _returnType = savedReturn;
        _containingSymbol = savedSymbol;
        return bound;
    }

    /// <summary>A closure with a result returns on every path, like anything else.</summary>
    void CheckClosureFlow(SyntaxNode body, BoundBlock bound) {
        if (_returnType.Equals(Core.Void) || _returnType.IsError || _generator is not null || _notGenerator is not null) {
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

    /// <summary>
    ///     A member reached implicitly through <c>self</c>, in a closure that
    ///     captured <c>self</c> weakly or unowned: through the closure's own
    ///     name for it, which a weak capture must first unwrap. Null where
    ///     <c>self</c> is the ordinary one.
    /// </summary>
    BoundExpression? CapturedSelf(SyntaxNode syntax, string member) {
        if (LookupLocal("self") is not LocalSymbol captured) {
            return null;
        }

        if ((NarrowedType(captured) ?? captured.Type) is OptionalTypeSymbol optional) {
            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Span,
                $"'{member}' is reached through 'self', which this closure holds weakly, as '{optional.ToDisplayString()}': "
                + "unwrap it first, with 'guard let self'"
            );
        }

        return new BoundLocalAccess(syntax, captured, NarrowedType(captured));
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

            if (kind is CaptureKind.Weak or CaptureKind.Unowned) {
                if (BindHeldCapture(item, name, kind) is { } held) {
                    captures.Add(held);
                }

                continue;
            }

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

    /// <summary>
    ///     <c>[weak x]</c>, <c>[unowned self]</c>, <c>[weak x = e]</c>: an
    ///     object the closure holds without keeping it alive, under a name of
    ///     the closure's own — optional for a weak one, which reads as none
    ///     once the object is gone, so the body unwraps it:
    ///     <c>guard let self else { … }</c>.
    /// </summary>
    BoundCapture? BindHeldCapture(CaptureItemSyntax item, string name, CaptureKind kind) {
        BoundExpression value;
        if (item.Expression is { } expression) {
            value = BindExpression(expression);
        } else if (item.Identifier.Kind == SyntaxKind.SelfKeyword || name == "self") {
            if (LookupLocal("self") is LocalSymbol outer) {
                value = new BoundLocalAccess(item, outer, NarrowedType(outer));
            } else if (EnclosingType() is { } enclosing) {
                value = new BoundSelfAccess(item, SelfTypeOf(enclosing), SelfIsMutable(enclosing));
            } else {
                _diagnostics.Error(ErrorCode.ERR_SelfOutsideType, item.Identifier.Span, "'self' is only available inside a type");
                return null;
            }
        } else {
            value = BindNameAsValue(name, item);
        }

        if (value.HasError || value.Type.IsError) {
            return null;
        }

        var weak = kind == CaptureKind.Weak;
        var objectType = weak && value.Type is OptionalTypeSymbol optional ? optional.UnderlyingType : value.Type;
        if (objectType.TypeKind is not (TypeKind.Class or TypeKind.Actor)) {
            _diagnostics.Error(
                weak ? ErrorCode.ERR_WeakNeedsOptionalClass : ErrorCode.ERR_UnownedNeedsClass,
                item.Span,
                $"'{item.SpecifierToken.Text}' holds an object without keeping it alive, so it captures a class instance; "
                + $"'{name}' is '{value.Type.ToDisplayString()}'"
            );
            return null;
        }

        var type = weak ? (TypeSymbol)new OptionalTypeSymbol(objectType) : objectType;
        var local = new LocalSymbol(name, _containingSymbol, item, false) { TypeResolver = () => type };
        return new(item, local, kind, Convert(value, type, item));
    }

    /// <summary>
    ///     <c>[.Read, .Write]</c> as a <c>@flags</c> enum: its elements
    ///     combined with <c>|</c>; <c>[]</c> is none of them, the integer 0.
    /// </summary>
    BoundExpression BindFlags(ArrayExpressionSyntax syntax, NamedTypeSymbol flags) {
        BoundExpression? combined = null;
        foreach (var element in syntax.Elements) {
            var value = Convert(BindExpression(element, flags), flags, element);
            combined = combined is null ? value : new BoundBinaryOperator(syntax, flags, BinaryOperatorKind.Or, combined, value);
        }

        return combined is null
            ? new BoundConversion(syntax, flags, new BoundLiteral(syntax, _compilation.EnumRawType(flags), 0L), ConversionKind.Explicit)
            : combined;
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

    /// <summary>
    ///     True for a type with one inference has not settled in it: a
    ///     closure for <c>(T) -&gt; U?</c> gives what its body gives, from which
    ///     U is inferred, as one for <c>(T) -&gt; U</c> does.
    /// </summary>
    bool MentionsOpen(TypeSymbol type) =>
        type switch {
            TypeParameterSymbol => IsOpen(type),
            NamedTypeSymbol named => named.TypeArguments.Any(MentionsOpen),
            OptionalTypeSymbol optional => MentionsOpen(optional.UnderlyingType),
            ArrayTypeSymbol array => MentionsOpen(array.ElementType),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(MentionsOpen),
            FunctionTypeSymbol function => function.ParameterTypes.Any(MentionsOpen) || MentionsOpen(function.ReturnType),
            _ => false,
        };

    /// <summary>Looks through an alias or an optional for a function type.</summary>
    static TypeSymbol? Expand(TypeSymbol? type) =>
        type switch {
            OptionalTypeSymbol optional => optional.UnderlyingType,
            _ => type,
        };

    /// <summary>
    ///     Where a union is expected, the one case of it a literal could be:
    ///     '["a", 1.5]' for a 'Json' is its 'Json[]', so the elements are
    ///     bound expecting a Json each. The expected type itself otherwise.
    /// </summary>
    static TypeSymbol? TheCaseOf(TypeSymbol? expected, Func<TypeSymbol, bool> fits) {
        if (StructuralUnionTypeSymbol.Of(expected) is not { } union) {
            return expected;
        }

        var candidates = union.Cases.Where(fits).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0] : expected;
    }

    /// <summary>Binds <c>["a": 1]</c> and the empty <c>[:]</c>.</summary>
    BoundExpression BindDictionary(
        DictionaryExpressionSyntax syntax,
        TypeSymbol? expectedType
    ) {
        TypeSymbol? expectedKey = null;
        TypeSymbol? expectedValue = null;

        expectedType = TheCaseOf(expectedType, Core.IsDictionary);
        if (expectedType is NamedTypeSymbol wanted
            && Core.IsDictionary(wanted)
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

        if (Core.Dictionary is not { } dictionary) {
            _diagnostics.Error(ErrorCode.ERR_NotAType, syntax.Span, "a dictionary needs Sable.Core's 'Dictionary', which is not here");
            return new BoundError(syntax);
        }

        // Its key must be Hashable, as '[Key: Value]' written out asks: a
        // key type the context gave was checked where it was written.
        if (expectedKey is null) {
            _compilation.TypeBinder.CheckTypeArguments(dictionary, [keyType, valueType], syntax.Span, _containingSymbol);
        }
        return Record(
            syntax,
            new BoundDictionary(syntax, dictionary.Construct([keyType, valueType]), entries.ToImmutable())
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

            // An observed property is initialised in its storage: a value
            // being made has no old value to observe.
            if (field is { BackingField: { } storage } && _compilation.ObservedProperties.ContainsKey(field)) {
                field = storage;
            }

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
            .Where(f => !initialized.Contains(f.DisplayName))
            .Select(static f => f.DisplayName)
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
        var target = _compilation.TypeBinder.BindType(syntax.Type, _containingSymbol, _scope);

        // A plain 'as' says what type a value is to have, so the operand is
        // bound expecting it: 'none as Int?' is that type's none, and
        // '[1] as Int[>..]?' an array of that kind. A number literal is
        // given the type so, as 'let x: T = literal' gives it: '255 as U8'
        // and '18446744073709551615 as U128' are that type's values, held to
        // its range where they are written. Any other number is bound as it
        // is, so that 'as' to another number stays the checked conversion
        // below; and 'unchecked as' wraps a literal as it does any number.
        var isPlain = syntax.QuestionOrBangToken.IsNone;
        var isUnchecked = !syntax.UncheckedKeyword.IsNone;
        var expected = !isPlain ? null
            : !Core.IsNumeric(target) ? target
            : !isUnchecked && IsNumberLiteral(syntax.Expression) ? target
            : null;
        var operand = BindExpression(syntax.Expression, expected);

        // 'as?' yields an optional; 'as' and 'as!' yield the type itself.
        var isOptional = syntax.QuestionOrBangToken.Kind == SyntaxKind.QuestionToken;
        var type = isOptional ? new OptionalTypeSymbol(target) : target;

        if (operand.HasError || target.IsError) {
            return new BoundConversion(syntax, type, operand, ConversionKind.Explicit);
        }

        var source = operand.Type;

        // Between two numeric types (spec/SYNTAX.md §16): 'as' traps on a
        // value the target cannot hold, 'unchecked as' wraps or truncates.
        // Either always gives a number, so neither has a '?' or '!' form.
        if (Core.IsNumeric(source) && Core.IsNumeric(target)) {
            if (!isPlain) {
                return InvalidCast(
                    syntax,
                    operand,
                    $"'{syntax.AsKeyword.Text}{syntax.QuestionOrBangToken.Text}' tests what a value is, and "
                    + $"'{source.ToDisplayString()}' is always a number: convert it with 'as' or 'unchecked as'"
                );
            }

            if (source.Equals(target)) {
                return new BoundConversion(syntax, type, operand, ConversionKind.Identity);
            }

            return new BoundConversion(syntax, type, operand, ConversionKind.Explicit) { IsUnchecked = isUnchecked };
        }

        // A character and an integer convert through its code point: 'c as
        // U32', and 'n as Char', which traps on a number that is no Unicode
        // scalar. Nothing wraps to a character, so 'unchecked' goes only the
        // other way, keeping the code point's low bits: 'c unchecked as U8'.
        if (source.Equals(Core.Char) && Core.IsInteger(target) || Core.IsInteger(source) && target.Equals(Core.Char)) {
            if (!isPlain) {
                return InvalidCast(
                    syntax,
                    operand,
                    $"'{syntax.AsKeyword.Text}{syntax.QuestionOrBangToken.Text}' tests what a value is, and "
                    + $"'{source.ToDisplayString()}' is always one: convert it with 'as'"
                );
            }

            if (isUnchecked && target.Equals(Core.Char)) {
                return InvalidCast(
                    syntax,
                    operand,
                    "a number that is not a Unicode scalar has no character to wrap to: "
                    + "convert it with 'as', which traps on one, or 'Char.fromCodePoint', which gives none"
                );
            }

            return new BoundConversion(syntax, type, operand, ConversionKind.Explicit) { IsUnchecked = isUnchecked };
        }

        // Between an enum and an integer (spec/SYNTAX.md §10): a case is the
        // integer it names, and an integer the case that names it, or a trap.
        if (isPlain && !isUnchecked
            && (source.TypeKind == TypeKind.Enum && Core.IsInteger(target) || Core.IsInteger(source) && target.TypeKind == TypeKind.Enum)) {
            return new BoundConversion(syntax, type, operand, ConversionKind.Explicit);
        }

        if (isUnchecked) {
            return InvalidCast(
                syntax,
                operand,
                $"'unchecked as' converts one numeric type to another, and '{source.ToDisplayString()}' "
                + $"to '{target.ToDisplayString()}' is not that"
            );
        }

        // Between classes, a cast goes up or down one line of inheritance; a
        // downcast is checked as the program runs. Classes on different lines
        // have no object in common, so the cast could never succeed.
        var sourceClass = source is OptionalTypeSymbol optionalSource ? optionalSource.UnderlyingType : source;
        if (sourceClass is NamedTypeSymbol { TypeKind: TypeKind.Class } && target is NamedTypeSymbol { TypeKind: TypeKind.Class }) {
            var isUpcast = _conversions.Classify(sourceClass, target, false) is not null;
            if (!isUpcast && _conversions.Classify(target, sourceClass, false) is null) {
                return InvalidCast(
                    syntax,
                    operand,
                    $"'{sourceClass.ToDisplayString()}' and '{target.ToDisplayString()}' are unrelated classes, "
                    + "so no object is both and the cast can never succeed"
                );
            }

            // Up the line, nothing needs checking: 'dog as Animal' is an upcast.
            if (isUpcast && ReferenceEquals(sourceClass, source)) {
                var upcast = Convert(operand, target, syntax);
                return isOptional ? new BoundConversion(syntax, type, upcast, ConversionKind.OptionalWrap) : upcast;
            }

            return new BoundConversion(syntax, type, operand, ConversionKind.Explicit);
        }

        // A cast that always succeeds is the conversion the context would
        // make anyway: 'dog as Animal', 'point as any Shape'.
        if (isPlain && _conversions.Classify(source, target, IsLiteral(operand)) is not null) {
            return Convert(operand, target, syntax);
        }

        // What a value is may be known only as the program runs: an
        // existential, a type parameter, a union of types, an optional.
        if (IsDynamicallyTyped(source) || IsDynamicallyTyped(target)) {
            return new BoundConversion(syntax, type, operand, ConversionKind.Explicit);
        }

        return InvalidCast(
            syntax,
            operand,
            $"'{source.ToDisplayString()}' cannot be cast to '{target.ToDisplayString()}'"
        );
    }

    /// <summary>An integer or float literal as written, negated or in parentheses or not: '5', '-2.5', '(-1)'.</summary>
    static bool IsNumberLiteral(ExpressionSyntax syntax) =>
        syntax switch {
            LiteralExpressionSyntax { Token.Kind: SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken } => true,
            PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.MinusToken, Operand: var operand } => IsNumberLiteral(operand),
            ParenthesizedExpressionSyntax { Expression: var inner } => IsNumberLiteral(inner),
            _ => false,
        };

    /// <summary>A type whose values may turn out, as the program runs, to be some other type.</summary>
    static bool IsDynamicallyTyped(TypeSymbol type) =>
        type is ExistentialTypeSymbol or OpaqueTypeSymbol or CompositionTypeSymbol or StructuralUnionTypeSymbol
            or RecursiveUnionTypeSymbol or TypeParameterSymbol or OptionalTypeSymbol or NamedTypeSymbol { TypeKind: TypeKind.Protocol };

    BoundExpression InvalidCast(CastExpressionSyntax syntax, BoundExpression operand, string message) {
        _diagnostics.Error(ErrorCode.ERR_InvalidCast, syntax.Span, message);
        return new BoundError(syntax, operand);
    }

    /// <summary>
    ///     Binds <c>a..&lt;b</c> to a <c>Range&lt;T&gt;</c> and <c>a...b</c> to a
    ///     <c>ClosedRange&lt;T&gt;</c>; the one-sided forms to
    ///     <c>PartialRangeUpTo</c> (<c>..&lt;b</c>), <c>PartialRangeThrough</c>
    ///     (<c>...b</c>) and <c>PartialRangeFrom</c> (<c>a...</c>) — all
    ///     declared in Sable.Core.
    /// </summary>
    BoundExpression BindRange(RangeExpressionSyntax syntax, TypeSymbol? expectedType) {
        var isInclusive = syntax.OperatorToken.Kind == SyntaxKind.DotDotDotToken;
        var (definition, name) = (syntax.Left, syntax.Right) switch {
            (not null, not null) => isInclusive ? (Core.ClosedRange, "ClosedRange") : (Core.Range, "Range"),
            (null, not null) => isInclusive ? (Core.PartialRangeThrough, "PartialRangeThrough") : (Core.PartialRangeUpTo, "PartialRangeUpTo"),
            _ => (Core.PartialRangeFrom, "PartialRangeFrom"),
        };

        // 'a..<' stops before an end it does not name: there is no such range.
        if (syntax.Left is not null && syntax.Right is null && !isInclusive) {
            _diagnostics.Error(
                ErrorCode.ERR_HalfOpenRangeWithoutEnd,
                syntax.OperatorToken.Span,
                "'..<' stops before its upper bound, and this range has none; "
                + "write the bound, or 'a...' for the values from 'a' on"
            );
            BindExpression(syntax.Left);
            return new BoundError(syntax);
        }

        var bound = expectedType is NamedTypeSymbol { TypeArguments.Length: 1 } expected
            && Core.IsAnyRange(expected)
                ? expected.TypeArguments[0]
                : null;

        var lower = syntax.Left is { } left ? BindExpression(left, bound) : null;
        var upper = syntax.Right is { } right ? BindExpression(right, bound ?? lower?.Type) : null;

        // '1..<^1', '^3...': a bound counted from the end makes a range a
        // collection resolves against its own end.
        if (lower is { Type: var lowerType } && Core.IsFromEnd(lowerType) || upper is { Type: var upperType } && Core.IsFromEnd(upperType)) {
            return BindRelativeRange(syntax, lower, upper, isInclusive);
        }

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
                $"no struct '{name}<Bound>' is declared in Sable.Core, so there is no range for this to make"
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

    /// <summary>
    ///     A range with a bound written <c>^n</c> — <c>1..&lt;^1</c>,
    ///     <c>^3...</c>, <c>..&lt;^1</c>, <c>^2...^1</c> — as the Sable.Core
    ///     struct <c>RelativeRange</c>: each bound an offset and whether it
    ///     counts from the end. A range with no upper bound runs to
    ///     <c>^0</c>, the end; one with no lower bound starts at 0.
    /// </summary>
    BoundExpression BindRelativeRange(RangeExpressionSyntax syntax, BoundExpression? lower, BoundExpression? upper, bool isInclusive) {
        if (Core.RelativeRange is not { } relative || Core.FromEnd is not { } fromEnd) {
            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Span,
                "no struct 'RelativeRange' is declared in Sable.Core, so there is no range counted from the end for this to make"
            );
            return new BoundError(syntax, [..new[] { lower, upper }.OfType<BoundExpression>()]);
        }

        var count = fromEnd.Members.OfType<FieldSymbol>().First(static f => f is { Name: "n", IsStatic: false });
        var (lowerOffset, lowerFromEnd) = Side(lower, false);
        var (upperOffset, upperFromEnd) = Side(upper, true);
        if (lowerOffset.HasError || upperOffset.HasError) {
            return new BoundError(syntax, lowerOffset, upperOffset);
        }

        var values = new Dictionary<string, BoundExpression> {
            ["lower"] = lowerOffset,
            ["lowerFromEnd"] = new BoundLiteral(syntax, Core.Bool, lowerFromEnd),
            ["upper"] = upperOffset,
            ["upperFromEnd"] = new BoundLiteral(syntax, Core.Bool, upperFromEnd),
            ["closed"] = new BoundLiteral(syntax, Core.Bool, isInclusive && upper is not null),
        };

        var fields = relative.Members.OfType<FieldSymbol>().Where(static f => !f.IsStatic && !f.IsComputed).ToList();
        if (fields.Count != values.Count || fields.Any(f => !values.ContainsKey(f.Name))) {
            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Span,
                "Sable.Core's 'RelativeRange' is not the struct of 'lower', 'lowerFromEnd', 'upper', 'upperFromEnd' and 'closed' a range counted from the end is made of"
            );
            return new BoundError(syntax, lowerOffset, upperOffset);
        }

        return Record(syntax, new BoundRecordExpression(syntax, relative, [..fields.Select(f => (f, values[f.Name]))]));

        // A bound counted from the end gives its count; one counted from the
        // start, itself; a missing one, the start or the end.
        (BoundExpression Offset, bool FromEnd) Side(BoundExpression? side, bool isUpper) =>
            side switch {
                null => (new BoundLiteral(syntax, Core.Int, 0L), isUpper),
                BoundRecordExpression { Fields: [var (_, n)] } written when Core.IsFromEnd(written.Type) => (n, true),
                _ when Core.IsFromEnd(side.Type) => (new BoundFieldAccess(side.Syntax, side, count), true),
                _ => (Convert(side, Core.Int, side.Syntax), false),
            };
    }

    BoundExpression BindTypeTest(TypeTestExpressionSyntax syntax) {
        var operand = BindExpression(syntax.Expression);
        var target = _compilation.TypeBinder.BindType(syntax.Type, _containingSymbol, _scope);
        return new BoundTypeTest(syntax, Core.Bool, operand, target);
    }

    /// <summary>
    ///     <c>self.ttl = ttl</c> in an initialiser: a stored <c>let</c> of the
    ///     type being initialised gets its value there, as it has none before.
    /// </summary>
    bool InitialisesLetField(BoundExpression target) =>
        target is BoundFieldAccess { Receiver: BoundSelfAccess, Field: { IsStatic: false, IsComputed: false } field }
        && _containingSymbol is FunctionSymbol { FunctionKind: FunctionKind.Initializer } initializer
        && initializer.ContainingSymbol is NamedTypeSymbol owner
        && ReferenceEquals(field.ContainingSymbol, owner.OriginalDefinition);

    /// <summary>
    ///     An <c>inout</c> or <c>out</c> argument is written by the callee, so it
    ///     must be a place an assignment could write: <c>inc(&amp;n)</c> changes
    ///     <c>n</c> as <c>n = n + 1</c> would, and is refused where that is.
    /// </summary>
    void CheckWritableArgument(BoundExpression argument, ParameterSymbol parameter) =>
        CheckWritableArgument(argument, parameter.Modifier == ParameterModifier.Out ? "out" : "inout");

    /// <summary>
    ///     <c>&amp;</c> at a call marks an argument the call may write: an
    ///     <c>inout</c> or <c>out</c> one has it (or, for <c>out</c>, the word),
    ///     and nothing else does. A <c>T&amp;</c> parameter only reads, so its
    ///     argument is written plainly.
    /// </summary>
    void CheckArgumentMarker(ExpressionSyntax argument, ParameterModifier modifier, string what) {
        if (argument.Parent is not ArgumentSyntax written) {
            return;
        }

        var marker = written.ModifierToken;
        var writes = modifier is ParameterModifier.InOut or ParameterModifier.Out;
        if (writes && marker.IsNone) {
            var keyword = modifier == ParameterModifier.Out ? "out" : "inout";
            _diagnostics.Add(QuickFixes.With(
                Diagnostic.Error(
                    ErrorCode.ERR_ArgumentMarker,
                    argument.Span,
                    $"{what} is '{keyword}': the call may write this argument, so it is marked '&{argument}'"
                ),
                "Add '&'",
                new TextChange(new(argument.Span.Start, 0), "&")
            ));
        } else if (marker.Kind == SyntaxKind.AmpersandToken && !writes
                   || marker.Kind == SyntaxKind.OutKeyword && modifier != ParameterModifier.Out) {
            var word = marker.Kind == SyntaxKind.AmpersandToken ? "&" : "out";
            _diagnostics.Add(QuickFixes.With(
                Diagnostic.Error(
                    ErrorCode.ERR_ArgumentMarker,
                    marker.Span,
                    $"'{word}' marks an argument the call may write, and {what} is not "
                    + (word == "&" ? "'inout' or 'out'" : "'out'")
                    + (word == "&" && !writes ? ": pass it as it is, '" + argument + "'" : "")
                ),
                $"Remove '{word}'",
                new TextChange(new(marker.Span.Start, argument.Span.Start - marker.Span.Start), "")
            ));
        }
    }

    /// <summary>
    ///     An argument for a <c>T&amp;</c> parameter: the place it names, or a
    ///     value with no place, which the call holds in a temporary of its
    ///     own for the length of the call.
    /// </summary>
    BoundExpression SharedArgument(BoundExpression argument, ReferenceTypeSymbol parameterType) {
        if (argument.HasError || argument.Type is ReferenceTypeSymbol || IsPlace(argument) || argument is BoundConditional) {
            return Convert(argument, parameterType, argument.Syntax);
        }

        var value = Convert(argument, parameterType.ReferencedType, argument.Syntax);
        if (value.HasError) {
            return value;
        }

        var shared = new BoundConversion(argument.Syntax, parameterType, value, ConversionKind.Reference);
        _bound[argument.Syntax] = shared;
        return shared;
    }

    void CheckWritableArgument(BoundExpression argument, string keyword) {
        var place = argument is BoundUnaryOperator { OperatorKind: UnaryOperatorKind.AddressOf, Operand: var operand }
            ? operand
            : argument;

        if (place.HasError) {
            return;
        }

        if (!place.IsAssignable && !InitialisesLetField(place)) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAssignable,
                argument.Syntax.Span,
                $"an '{keyword}' argument is written by the call, so it must be a mutable binding"
            );
        } else if (place is BoundFieldAccess { Field.SetterAccessibility: { } setter } field && !IsAccessible(field.Field, setter)) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAssignable,
                argument.Syntax.Span,
                $"'{field.Field.Name}' can be read here but not written: its setter is {setter.ToString().ToLowerInvariant()}"
            );
        }
    }

    BoundExpression BindAssignment(AssignmentExpressionSyntax syntax) {
        var spine = Spine(syntax.Left).ToList();
        _assignedSpine.UnionWith(spine);
        var target = BindExpression(syntax.Left);
        _assignedSpine.ExceptWith(spine);

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

        // 'a.next?.v = 9' stores a 'v' when there is a next, and nothing
        // when there is not: the value is the field's own type, not the
        // chain's optional one.
        var stored = target.Type;
        var throughChain = target is BoundFieldAccess { Receiver.Type: OptionalTypeSymbol } chainTarget
            && syntax.Left is MemberAccessExpressionSyntax { OperatorToken.Kind: SyntaxKind.QuestionDotToken };
        if (throughChain && !((BoundFieldAccess)target).FlattensChain && target.Type is OptionalTypeSymbol reached) {
            stored = reached.UnderlyingType;
        }

        var value = BindExpression(syntax.Right, stored);

        if (throughChain && syntax.OperatorToken.Kind != SyntaxKind.EqualsToken) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAssignable,
                syntax.OperatorToken.Span,
                $"'{syntax.OperatorToken.Text}' through '?.' is not supported; unwrap with 'if let' first"
            );
            return new BoundError(syntax, target, value);
        }

        // 'xs[1..<3]' is the elements in the range, read out: there is no
        // place in the array that is all of them to store to.
        if (target is BoundIndexAccess { Subscript: null, Indices: [var rangeIndex] } && Core.IsAnyRange(rangeIndex.Type)) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAssignable,
                syntax.Left.Span,
                "a range of an array's elements is read as a slice, and cannot be stored to; store each element"
            );
            return new BoundError(syntax, target, value);
        }

        // 'text[^1] = c': a subscript with no 'set' only reads.
        if (target is BoundIndexAccess { Subscript: { } readOnly } readOnlyTarget && !BoundIndexAccess.HasSetter(readOnly)) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAssignable,
                syntax.Left.Span,
                $"'{Signature(readOnly)}' of '{readOnlyTarget.Receiver.Type.ToDisplayString()}' only reads: it has no 'set' to store through"
            );
            return new BoundError(syntax, target, value);
        }

        if (!target.HasError && !IsAssignableThroughChain(target) && !InitialisesLetField(target)) {
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
            // The operator's result need not be the target's type: 'x ??= 5'
            // on an Int? computes 'x ?? 5', an Int, which is stored as an Int?.
            var kind = CompoundOperatorKind(syntax.OperatorToken.Kind);
            if (kind is not null) {
                var combined = BindBinaryOperator(syntax, kind.Value, target, value, syntax.OperatorToken);
                return new BoundAssignment(syntax, target, Convert(combined, target.Type, syntax));
            }
        }

        return new BoundAssignment(syntax, target, Convert(value, stored, syntax.Right));
    }

    /// <summary>
    ///     Assignable as a place is, or — reached through '?.' — a mutable
    ///     field of an object, which is written where it is, or of a value
    ///     in a place the optional can be written back to.
    /// </summary>
    static bool IsAssignableThroughChain(BoundExpression target) =>
        target.IsAssignable
        || target is BoundFieldAccess {
                Field: { IsMutable: true, IsStatic: false } field,
                Receiver: { Type: OptionalTypeSymbol { UnderlyingType: var underlying } } receiver,
            }
            && (!field.IsComputed || field.HasSetter)
            && (underlying.IsReferenceType || receiver.IsAssignable);

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
            SyntaxKind.AmpersandPlusEqualsToken
                or SyntaxKind.AmpersandMinusEqualsToken
                or SyntaxKind.AmpersandStarEqualsToken => BinaryOperatorKind.UserDefined,
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

    BoundExpression BindPrefixUnary(PrefixUnaryExpressionSyntax syntax, TypeSymbol? expectedType = null) {
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

        if (syntax.OperatorToken.Kind == SyntaxKind.CaretToken) {
            return BindFromEnd(syntax);
        }

        var operand = BindExpression(syntax.Operand);
        if (syntax.OperatorToken.Kind is not (SyntaxKind.AmpersandToken or SyntaxKind.MoveKeyword)) {
            operand = ImplicitlyUnwrapped(Dereferenced(operand));
        }

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
                Core.IsInteger(operand.Type) || operand.Type is NamedTypeSymbol { IsFlags: true } ? operand.Type : null,
            UnaryOperatorKind.AddressOf => new ReferenceTypeSymbol(operand.Type),


            _ => operand.Type,
        };

        if (type is null) {
            if (FindUserUnaryOperator(syntax.OperatorToken.Text, operand.Type, OperatorFixity.Prefix) is { } method) {
                return new BoundUnaryOperator(
                    syntax,
                    UserResultType(method, operand.Type),
                    UnaryOperatorKind.UserDefined,
                    UserOperand(method, operand),
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
                ulong u => -(Int128)u,
                UInt128 u when u <= UInt128.One << 127 => unchecked(-(Int128)u),
                double d => -d,
                _ => null,
            };

            if (negated is not null) {
                // It takes the type its context asks for, as '5' does; an
                // argument's is checked against its parameter's (Convert).
                if (expectedType is not null
                    && _conversions.Classify(type, expectedType is OptionalTypeSymbol optional ? optional.UnderlyingType : expectedType, true)
                        is ConversionKind.LiteralTyping) {
                    type = expectedType is OptionalTypeSymbol wrapped ? wrapped.UnderlyingType : expectedType;
                }

                var negative = new BoundLiteral(syntax, type, negated);
                if (expectedType is null && syntax.Parent is ArgumentSyntax) {
                    _uncheckedArguments.Add(negative);
                } else {
                    CheckIntegerFits(negative, type);
                }

                return negative;
            }

            // Past -2^63, there is no integer type it fits.
            if (literal.Value is ulong digits) {
                _diagnostics.Error(
                    ErrorCode.ERR_IntegerDoesNotFit,
                    syntax.Span,
                    $"'-{digits}' does not fit in any integer type"
                );
                return new BoundError(syntax, operand);
            }
        }

        return new BoundUnaryOperator(syntax, type, kind, operand);
    }

    /// <summary>
    ///     <c>^n</c>: a position counted back from a collection's end, the
    ///     Sable.Core struct <c>FromEnd</c> holding the integer (<c>xs[^1]</c>,
    ///     <c>xs[1..&lt;^1]</c>). Prefix <c>^</c> means nothing else.
    /// </summary>
    BoundExpression BindFromEnd(PrefixUnaryExpressionSyntax syntax) {
        var operand = ImplicitlyUnwrapped(BindExpression(syntax.Operand, Core.Int));
        if (operand.HasError) {
            return new BoundError(syntax, operand);
        }

        if (Core.FromEnd is not { } fromEnd) {
            _diagnostics.Error(
                ErrorCode.ERR_NameNotFound,
                syntax.Span,
                "no struct 'FromEnd' with an 'n: Int' is declared in Sable.Core, so there is nothing for '^' to make"
            );
            return new BoundError(syntax, operand);
        }

        if (!operand.Type.Equals(Core.Int) && !_conversions.HasImplicitConversion(operand.Type, Core.Int, IsLiteral(operand))) {
            _diagnostics.Error(
                ErrorCode.ERR_OperatorNotDefined,
                syntax.Span,
                $"'^' counts an 'Int' of positions back from the end, not a '{operand.Type.ToDisplayString()}'"
            );
            return new BoundError(syntax, operand);
        }

        var count = fromEnd.Members.OfType<FieldSymbol>().First(static f => f is { Name: "n", IsStatic: false });
        return new BoundRecordExpression(syntax, fromEnd, [(count, Convert(operand, Core.Int, syntax.Operand))]);
    }

    BoundExpression BindPostfixUnary(PostfixUnaryExpressionSyntax syntax) {
        var operand = AsValue(BindExpression(syntax.Operand));

        // The only postfix operator is '!', which unwraps an optional.
        if (operand.Type is OptionalTypeSymbol optional) {
            return new BoundConversion(syntax, optional.UnderlyingType, operand, ConversionKind.Explicit);
        }

        if (FindUserUnaryOperator(syntax.OperatorToken.Text, operand.Type, OperatorFixity.Postfix) is { } method) {
            return new BoundUnaryOperator(syntax, UserResultType(method, operand.Type), UnaryOperatorKind.UserDefined, UserOperand(method, operand), method);
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

        // The right side of 'a && b' is evaluated only when 'a' holds, and
        // of 'a || b' only when it does not: what that says about a binding
        // holds there — 'i != none && i > 3' reads 'i' unwrapped.
        var shortCircuit = syntax.OperatorToken.Kind switch {
            SyntaxKind.AmpersandAmpersandToken => NarrowingsWhen(left, true),
            SyntaxKind.PipePipeToken => NarrowingsWhen(left, false),
            _ => null,
        };

        if (shortCircuit is not null) {
            PushNarrowing(shortCircuit);
        }

        var right = BindExpression(syntax.Right, expected);
        if (shortCircuit is not null) {
            PopNarrowing();
        }

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

            // '&+', '&-' and '&*' are FixedWidthInteger's, declared in Sable.Core.
            SyntaxKind.AmpersandPlusToken
                or SyntaxKind.AmpersandMinusToken
                or SyntaxKind.AmpersandStarToken => BinaryOperatorKind.UserDefined,
            _ => null,
        };

    BoundExpression BindBinaryOperator(
        SyntaxNode syntax,
        BinaryOperatorKind kind,
        BoundExpression left,
        BoundExpression right,
        SyntaxToken operatorToken,
        string? symbolOverride = null
    ) {
        if (left.HasError || right.HasError) {
            return new BoundError(syntax, left, right);
        }

        // An operator reads what a reference refers to: 'x + 1' with 'x: Int&'.
        // An assignment's target stays the place it names.
        if (CompoundOperatorKind(operatorToken.Kind) is null) {
            left = Dereferenced(left);
        }

        right = Dereferenced(right);

        // 'm += n' is 'm = m + n': the operator it looks for is '+'
        // (SYNTAX.md §13 — compound assignment derives from the binary one).
        var symbol = symbolOverride
            ?? (CompoundOperatorKind(operatorToken.Kind) is null ? operatorToken.Text : operatorToken.Text[..^1]);

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

        // 'x + 1' of an 'x: Int!' adds the Int it holds. '==' and '!=' keep
        // the optional, so 'x == none' still asks whether there is one.
        if (kind is not (BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual)) {
            left = ImplicitlyUnwrapped(left);
            right = ImplicitlyUnwrapped(right);
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
        } else if (IsNumericLiteral(left) && IsNumericLiteral(right) && !left.Type.Equals(right.Type)) {
            // '1 + 2.0': two literals, one a float, are two floats.
            if (left.Type.Equals(Core.Int)) {
                left = Convert(left, Core.F64, left.Syntax);
            } else {
                right = Convert(right, Core.F64, right.Syntax);
            }
        }

        // 'T.self == Int.self': two metatypes are equal when they are the
        // type of the same type. A metatype's one value is its type, so the
        // answer is known in each instance of the code (SYNTAX.md §16).
        if (kind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
            && left.Type is MetatypeTypeSymbol && right.Type is MetatypeTypeSymbol) {
            return new BoundBinaryOperator(syntax, Core.Bool, kind, left, right);
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
            if (FindUserOperator(symbol, left.Type, right.Type) is { } own) {
                (left, right) = UserOperands(own, left, right);
                return new BoundBinaryOperator(syntax, UserResultType(own.Method, left.Type), BinaryOperatorKind.UserDefined, left, right, own.Method);
            }

            var derivedFrom = kind switch {
                BinaryOperatorKind.NotEqual => "==",
                BinaryOperatorKind.GreaterThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThanOrEqual => "<",
                _ => null,
            };

            // '>' and '<=' pass their operands to '<' the other way round.
            var swapped = kind is BinaryOperatorKind.GreaterThan or BinaryOperatorKind.LessThanOrEqual;
            if (derivedFrom is not null && FindUserOperator(derivedFrom, left.Type, right.Type, swapped) is { } basis) {
                (left, right) = UserOperands(basis, left, right);
                return new BoundBinaryOperator(syntax, Core.Bool, kind, left, right, basis.Method);
            }

            if (ReportUnmetDerivedOperator(derivedFrom ?? symbol, left.Type, syntax)) {
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
            && FindUserOperator("<", left.Type, right.Type, kind is BinaryOperatorKind.GreaterThan or BinaryOperatorKind.LessThanOrEqual) is { } order) {
            (left, right) = UserOperands(order, left, right);
            return kind == BinaryOperatorKind.LessThan
                ? new BoundBinaryOperator(syntax, Core.Bool, BinaryOperatorKind.UserDefined, left, right, order.Method)
                : new BoundBinaryOperator(syntax, Core.Bool, kind, left, right, order.Method);
        }

        // 'x == none' asks only which case x is, so any optional compares.
        if (kind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
            && (IsNone(left) && right.Type is OptionalTypeSymbol || IsNone(right) && left.Type is OptionalTypeSymbol)) {
            return new BoundBinaryOperator(syntax, Core.Bool, kind, left, right);
        }

        if (kind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
            && StructuralEquality(syntax, kind, left, right, operatorToken) is { } structural) {
            return structural;
        }

        var type = ResultTypeOf(kind, left.Type, right.Type);
        if (type is not null) {
            return new BoundBinaryOperator(syntax, type, kind, left, right);
        }

        if (FindUserOperator(symbol, left.Type, right.Type) is { } method) {
            (left, right) = UserOperands(method, left, right);
            return new BoundBinaryOperator(
                syntax,
                UserResultType(method.Method, left.Type),
                BinaryOperatorKind.UserDefined,
                left,
                right,
                method.Method
            );
        }

        _diagnostics.Error(
            ErrorCode.ERR_OperatorNotDefined,
            syntax.Span,
            $"'{symbol}' is not defined for "
            + $"'{left.Type.ToDisplayString()}' and '{right.Type.ToDisplayString()}'"
        );
        return new BoundError(syntax, left, right);
    }

    /// <summary>
    ///     <c>==</c> and <c>!=</c> on what has no comparison of its own but
    ///     is made of parts that do: two tuples, element by element; two
    ///     arrays, as long as each other and equal element by element; and two
    ///     optionals — or an optional and a value — of a type whose '==' is
    ///     not built in, by what they hold. Null when none applies.
    /// </summary>
    BoundExpression? StructuralEquality(
        SyntaxNode syntax,
        BinaryOperatorKind kind,
        BoundExpression left,
        BoundExpression right,
        SyntaxToken operatorToken
    ) {
        // 'x == none' asks only whether there is a value.
        if (IsNone(left) || IsNone(right)) {
            return null;
        }

        TypeSymbol compared;
        if (left.Type is TupleTypeSymbol && right.Type is TupleTypeSymbol) {
            // Tuples of different types do not compare: '==' is not defined
            // for them, as for any two different types.
            if (!_conversions.HasImplicitConversion(right.Type, left.Type, IsLiteral(right))) {
                return null;
            }

            compared = left.Type;
            right = Convert(right, compared, right.Syntax);
        } else if (left.Type is ArrayTypeSymbol leftArray && right.Type is ArrayTypeSymbol rightArray) {
            // The comparison is of one element of each, bound once and
            // made for each index in turn.
            if (!leftArray.ElementType.Equals(rightArray.ElementType)) {
                return null;
            }

            compared = leftArray.ElementType;
        } else {
            var payload = (left.Type as OptionalTypeSymbol)?.UnderlyingType ?? (right.Type as OptionalTypeSymbol)?.UnderlyingType;
            if (payload is null || IsBuiltinOperand(payload)) {
                return null;
            }

            bool Holds(TypeSymbol type) => type.Equals(payload) || type is OptionalTypeSymbol optional && optional.UnderlyingType.Equals(payload);
            if (!Holds(left.Type) || !Holds(right.Type)) {
                return null;
            }

            compared = payload;
        }

        if (right.HasError) {
            return new BoundError(syntax, left, right);
        }

        var leftPart = new LocalSymbol("$left", _containingSymbol, null, false) { TypeResolver = () => compared };
        var rightPart = new LocalSymbol("$right", _containingSymbol, null, false) { TypeResolver = () => compared };
        var leftAccess = new BoundLocalAccess(syntax, leftPart);
        var rightAccess = new BoundLocalAccess(syntax, rightPart);

        BoundExpression equal;
        if (compared is TupleTypeSymbol tuple) {
            BoundExpression? all = null;
            for (var i = 0; i < tuple.ElementTypes.Length; i++) {
                var element = BindBinaryOperator(
                    syntax,
                    BinaryOperatorKind.Equal,
                    new BoundTupleElementAccess(syntax, tuple.ElementTypes[i], leftAccess, i),
                    new BoundTupleElementAccess(syntax, tuple.ElementTypes[i], rightAccess, i),
                    operatorToken,
                    "=="
                );
                all = all is null ? element : new BoundBinaryOperator(syntax, Core.Bool, BinaryOperatorKind.LogicalAnd, all, element);
            }

            equal = all ?? new BoundLiteral(syntax, Core.Bool, true);
        } else {
            equal = BindBinaryOperator(syntax, BinaryOperatorKind.Equal, leftAccess, rightAccess, operatorToken, "==");
        }

        if (equal.HasError) {
            return new BoundError(syntax, left, right);
        }

        return new BoundBinaryOperator(syntax, Core.Bool, kind, left, right) {
            Structural = new StructuralComparison(leftPart, rightPart, equal),
        };
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
                left.Equals(right) && (Core.IsInteger(left) || left is NamedTypeSymbol { IsFlags: true }) ? left
                : left.Equals(Core.Bool) && right.Equals(Core.Bool) ? Core.Bool
                : null,

            BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift =>
                Core.IsInteger(left) && Core.IsInteger(right) ? left : null,

            BinaryOperatorKind.LessThan
                or BinaryOperatorKind.LessThanOrEqual
                or BinaryOperatorKind.GreaterThan
                or BinaryOperatorKind.GreaterThanOrEqual =>
                // Characters order by scalar value, an instruction like the
                // integers'; without it Sable.Core's 'operator <' on Char,
                // written 'lhs < rhs', would bind to itself and never return.
                left.Equals(right) && (Core.IsNumeric(left) || left.Equals(Core.String) || left.Equals(Core.Char))
                    ? Core.Bool
                    : null,

            // 'T → T?' is implicit (docs/03 §3.7), so comparing an optional with
            // a value of its type is comparing two optionals.
            // Only what has an equality of its own compares without an '==':
            // an unconstrained 'T' does not, whatever it is made at the call.
            BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual =>
                (left.Equals(right)
                 || (left is OptionalTypeSymbol leftOptional
                     && leftOptional.UnderlyingType.Equals(right))
                 || (right is OptionalTypeSymbol rightOptional
                     && rightOptional.UnderlyingType.Equals(left)))
                && HasBuiltinEquality(left is OptionalTypeSymbol { UnderlyingType: var underlying } ? underlying : left)
                    ? Core.Bool
                    : null,

            BinaryOperatorKind.ReferenceEqual or BinaryOperatorKind.ReferenceNotEqual =>
                left.IsReferenceType && right.IsReferenceType ? Core.Bool : null,

            BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr =>
                left.Equals(Core.Bool) && right.Equals(Core.Bool) ? Core.Bool : null,

            _ => null,
        };

    /// <summary>Looks for a one-operand <c>operator</c> member on the operand's type.</summary>
    /// <summary>
    ///     The operand of a prefix or postfix operator of the program's own,
    ///     converted to the parameter it is passed to — a reference to it,
    ///     where the operator takes a reference. A protocol's requirement
    ///     takes it as it is: the type that meets it says what it takes.
    /// </summary>
    BoundExpression UserOperand(FunctionSymbol method, BoundExpression operand) =>
        method.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol } || operand.Type is not NamedTypeSymbol named
            ? operand
            : Convert(operand, TypeSubstitution.For(named).Apply(method.Parameters[0].Type), operand.Syntax);

    FunctionSymbol? FindUserUnaryOperator(
        string symbol,
        TypeSymbol operand,
        OperatorFixity fixity
    ) {
        // '-x' on an x: T where T: SignedNumeric — the protocol's
        // requirement, met by whatever T turns out to be, as a binary
        // operator's is.
        if (AssociatedTypes.IsAbstract(operand)) {
            return ConstraintProtocols(operand)
                .SelectMany(static p => p.Members.OfType<FunctionSymbol>())
                .FirstOrDefault(f => f is { FunctionKind: FunctionKind.Operator, Parameters.Length: 1 }
                    && f.Fixity == fixity && string.Equals(f.Name, symbol, StringComparison.Ordinal));
        }

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
    /// <summary>
    ///     True for a type '==' compares without an operator of its own: the
    ///     scalars, and an enum or union whose cases carry nothing, which are
    ///     equal when they are the same case.
    /// </summary>
    bool HasBuiltinEquality(TypeSymbol type) =>
        type.IsError
        || Core.IsNumeric(type) || type.Equals(Core.String) || type.Equals(Core.Bool) || type.Equals(Core.Char)
        || type is NamedTypeSymbol { TypeKind: TypeKind.Enum }
        || type is NamedTypeSymbol { TypeKind: TypeKind.Union } union
        && union.Members.OfType<UnionCaseSymbol>().All(static c => !c.HasPayload)
        || type is TupleTypeSymbol tuple && tuple.ElementTypes.All(HasBuiltinEquality);

    bool IsBuiltinOperand(TypeSymbol type) =>
        Core.IsNumeric(type) || type.Equals(Core.String) || type.Equals(Core.Bool) || type.Equals(Core.Char)
        || type is OptionalTypeSymbol || type.IsError;

    /// <summary>What a user operator gives: 'Self' in a protocol's requirement is the operand's type.</summary>
    static TypeSymbol UserResultType(FunctionSymbol method, TypeSymbol operand) =>
        method.ReturnType is TypeParameterSymbol { ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } } ? operand : method.ReturnType;

    /// <summary>
    ///     The protocols a type parameter, or an associated type read through
    ///     one, is constrained to, and those they refine.
    /// </summary>
    IEnumerable<NamedTypeSymbol> ConstraintProtocols(TypeSymbol type) {
        var seen = new HashSet<NamedTypeSymbol>();
        var pending = new Stack<NamedTypeSymbol>(
            AssociatedTypes.ConstraintsOf(type, _conversions.Equivalence)
                .SelectMany(static c => CompositionTypeSymbol.PartsOf(c))
                .OfType<NamedTypeSymbol>()
                .Where(static c => c.TypeKind == TypeKind.Protocol)
        );
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

    /// <summary>
    ///     An infix operator found for two operands: the operator, and the
    ///     type each operand is passed as — its parameter's, seen through the
    ///     operand's type arguments — or null where it is passed as it is.
    /// </summary>
    readonly record struct UserOperator(FunctionSymbol Method, TypeSymbol? LeftParameter, TypeSymbol? RightParameter);

    /// <summary>
    ///     The operands of a user operator, each converted to the parameter
    ///     it is passed to: 'a + b' to an 'operator + (a: Money, b: Money&)'
    ///     passes b's address, and so needs b to have one.
    /// </summary>
    (BoundExpression Left, BoundExpression Right) UserOperands(UserOperator found, BoundExpression left, BoundExpression right) {
        if (found.LeftParameter is { } leftParameter) {
            left = Convert(left, leftParameter, left.Syntax);
        }

        if (found.RightParameter is { } rightParameter) {
            right = Convert(right, rightParameter, right.Syntax);
        }

        return (left, right);
    }

    /// <summary>
    ///     The infix operator <paramref name="symbol" /> that either operand's
    ///     type declares for the two; <paramref name="swapped" /> when the
    ///     left operand is passed second, as '>' passes its operands to '<'.
    /// </summary>
    UserOperator? FindUserOperator(string symbol, TypeSymbol left, TypeSymbol right, bool swapped = false) {
        // 'a < b' on two T: Comparable — the protocol's requirement, met by
        // whatever T turns out to be. 'C.Item' with 'associatedtype Item:
        // Comparable' is the same: its constraints are as good as T's.
        if (AssociatedTypes.IsAbstract(left) && left.Equals(right)) {
            foreach (var protocol in ConstraintProtocols(left)) {
                if (protocol.Members.OfType<FunctionSymbol>().FirstOrDefault(f =>
                        f is { FunctionKind: FunctionKind.Operator, Fixity: OperatorFixity.Infix, Parameters.Length: 2 }
                        && string.Equals(f.Name, symbol, StringComparison.Ordinal)) is { } requirement) {
                    return new UserOperator(requirement, null, null);
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
                var first = members.Apply(function.Parameters[0].Type);
                var second = members.Apply(function.Parameters[1].Type);
                var (leftParameter, rightParameter) = swapped ? (second, first) : (first, second);
                if (_conversions.HasImplicitConversion(left, leftParameter)
                    && _conversions.HasImplicitConversion(right, rightParameter)
                    && (_compilation.GetDerivation(function) is not { } derivation || Conversions.Satisfies(named, derivation.Protocol))) {
                    return new UserOperator(function, leftParameter, rightParameter);
                }
            }
        }

        // An operator a protocol's extension gives every conformer, on two
        // of one conforming type: 'a &+ b' on two Ints is FixedWidthInteger's.
        if (left is NamedTypeSymbol { TypeKind: not TypeKind.Protocol } conformer && left.Equals(right)) {
            foreach (var protocol in ProtocolsConformedToBy(conformer)) {
                if (protocol.Members.OfType<FunctionSymbol>().FirstOrDefault(f =>
                        f is { FunctionKind: FunctionKind.Operator, Fixity: OperatorFixity.Infix, Parameters.Length: 2, IsAbstract: false }
                        && string.Equals(f.Name, symbol, StringComparison.Ordinal)
                        && f.Parameters.All(p => p.Type is TypeParameterSymbol self && ReferenceEquals(self, protocol.SelfTypeParameter))) is { } given) {
                    return new UserOperator(given, conformer, conformer);
                }
            }
        }

        return null;
    }

    /// <summary>Every protocol the type conforms to, and every one those refine.</summary>
    static IEnumerable<NamedTypeSymbol> ProtocolsConformedToBy(NamedTypeSymbol type) {
        var seen = new HashSet<NamedTypeSymbol>();
        var pending = new Stack<NamedTypeSymbol>(type.OriginalDefinition.BaseTypes.OfType<NamedTypeSymbol>().Where(static b => b.TypeKind == TypeKind.Protocol));
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

        // An argument's literal, given the type of the parameter it is for:
        // an integer type's range, or, where it stays an Int (an 'any P'
        // parameter), Int's.
        var checkedArgument = expression is BoundLiteral argument && _uncheckedArguments.Remove(argument);
        if (checkedArgument) {
            var parameterType = target is OptionalTypeSymbol optionalTarget ? optionalTarget.UnderlyingType : target;
            CheckIntegerFits((BoundLiteral)expression, Core.IsNumeric(parameterType) ? parameterType : expression.Type);
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

        // '-1.0...1.0' where a 'ClosedRange<F32>' is wanted: each bound
        // takes the bound type, as an array literal's elements do.
        if (expression is BoundRange { Type: NamedTypeSymbol literalRange } rangeLiteral
            && IsLiteral(rangeLiteral)
            && target is NamedTypeSymbol { TypeArguments: [var wantedBound] } wantedRange
            && ReferenceEquals(literalRange.OriginalDefinition, wantedRange.OriginalDefinition)
            && !literalRange.Equals(wantedRange)) {
            expression = new BoundRange(
                rangeLiteral.Syntax,
                wantedRange,
                rangeLiteral.Lower is { } lowerBound ? Convert(lowerBound, wantedBound, lowerBound.Syntax) : null,
                rangeLiteral.Upper is { } upperBound ? Convert(upperBound, wantedBound, upperBound.Syntax) : null,
                rangeLiteral.IsInclusive
            );
            return expression;
        }

        var conversion = _conversions.Classify(expression.Type, target, IsLiteral(expression));

        // 'let o: (any Shape)? = Square()': into what the optional wraps,
        // boxed or upcast, and then wrapped.
        if (conversion is null
            && target is OptionalTypeSymbol { UnderlyingType: var wrapped }
            && expression.Type is not OptionalTypeSymbol
            && _conversions.Classify(expression.Type, wrapped, IsLiteral(expression)) is ConversionKind.Existential or ConversionKind.Upcast) {
            return new BoundConversion(syntax, target, Convert(expression, wrapped, syntax), ConversionKind.OptionalWrap);
        }

        if (conversion is null) {
            if (!expression.HasError && !target.IsError) {
                _diagnostics.Error(ErrorCode.ERR_TypeMismatch, syntax.Span, DescribeMismatch(target, expression.Type));
            }

            return new BoundError(syntax, expression);
        }

        // '-1' bound first, then given the 'U8' its place wants.
        if (conversion == ConversionKind.LiteralTyping && !checkedArgument && expression is BoundLiteral literal) {
            CheckIntegerFits(literal, target is OptionalTypeSymbol optional ? optional.UnderlyingType : target);
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

        // An array as a slice of it: the same value, with the slice's type.
        if (conversion == ConversionKind.ArrayView) {
            var viewed = new BoundConversion(syntax, target, expression, ConversionKind.Identity);
            if (syntax == expression.Syntax) {
                _bound[syntax] = viewed;
            }

            return viewed;
        }

        // A 'T!' where a T is wanted: unwrapped, then whatever takes it on.
        if (conversion == ConversionKind.Explicit && expression.Type is OptionalTypeSymbol { IsImplicitlyUnwrapped: true }) {
            return Convert(ImplicitlyUnwrapped(expression), target, syntax);
        }

        // A promotion that needs a step first — an array literal taking a
        // kind, a value joining a union — takes that step, then wraps.
        if (conversion == ConversionKind.OptionalWrap
            && target is OptionalTypeSymbol promoted
            && _conversions.Classify(expression.Type, promoted.UnderlyingType, IsLiteral(expression))
                is not (null or ConversionKind.Identity)) {
            expression = Convert(expression, promoted.UnderlyingType, syntax);
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
    ///     A <c>T!</c> read as the <c>T</c> it holds, trapping when it holds
    ///     none, as <c>x!</c> does; anything else as it is.
    /// </summary>
    static BoundExpression ImplicitlyUnwrapped(BoundExpression expression) =>
        expression.Type is OptionalTypeSymbol { IsImplicitlyUnwrapped: true } optional
            ? new BoundConversion(expression.Syntax, optional.UnderlyingType, expression, ConversionKind.Explicit)
            : expression;

    /// <summary>
    ///     An expression used where nothing says what type it should be: a
    ///     named function is the one function it names, as a value.
    /// </summary>
    BoundExpression AsValue(BoundExpression expression) =>
        expression is BoundFunctionGroup group ? Record(group.Syntax, Materialize(group, null)) : expression;

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
                // A generic function's own type parameters are what the type
                // wanted makes them: 'let f: (Int) -> Int = id' is id<Int>.
                var inferred = InferFromFunctionType(candidate, substitution, wanted);
                if (inferred is null) {
                    continue;
                }

                var type = Apply(candidate.FunctionType, inferred);
                if (!_conversions.HasImplicitConversion(type, wanted)) {
                    continue;
                }

                chosen = candidate;
                substitution = inferred;
                break;
            }
        } else if (group.Candidates.Length == 1) {
            chosen = group.Candidates[0];

            // Nothing says what a generic function's own type parameters are.
            if (!chosen.TypeParameters.IsEmpty) {
                _diagnostics.Error(
                    ErrorCode.ERR_CannotInferTypeArgument,
                    group.Syntax.Span,
                    $"cannot infer '{chosen.TypeParameters[0].Name}' of '{chosen.Name}' taken as a value; "
                    + "say which function type is wanted, as in 'let f: (Int) -> Int = ...'"
                );
                return new BoundError(group.Syntax);
            }
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

        // 'counter.bump' would change a copy the closure keeps, not counter.
        if (group.Receiver is { } receiver && chosen.IsMutating && !receiver.Type.IsReferenceType) {
            _diagnostics.Error(
                ErrorCode.ERR_NotAValue,
                group.Syntax.Span,
                $"'{chosen.Name}' changes the value it is called on, so it cannot be taken with it as a value; "
                + "call it in a closure instead"
            );
            return new BoundError(group.Syntax);
        }

        // 'counter.bump' of an actor: a closure called later, anywhere,
        // would call into the actor without waiting its turn (Swift's rule).
        if (group.Receiver is not null && !chosen.IsStatic && chosen.FunctionKind == FunctionKind.Ordinary && !OnMainActor(chosen) && IsActorIsolated(chosen) && IsolatedFromHere(chosen)) {
            _diagnostics.Error(
                ErrorCode.ERR_IsolatedWitness,
                group.Syntax.Span,
                $"'{chosen.Name}' is isolated to {Isolation(chosen)}, so it cannot be taken as a value: a call through it would not wait for the actor; "
                + "call it in an async closure instead, which waits"
            );
            return new BoundError(group.Syntax);
        }

        CheckUnsafeUse(chosen, group.Syntax, "take it as a value");
        return new BoundFunctionReference(
            group.Syntax,
            Apply(chosen.FunctionType, substitution),
            group.Receiver,
            chosen
        ) { Substitution = substitution };
    }

    /// <summary>
    ///     What a function's own type parameters are when it is taken as a
    ///     value of the function type wanted, added to what the group already
    ///     knows (its receiver's type arguments); null when the type cannot
    ///     settle them all, or settles one to a type its constraints refuse.
    /// </summary>
    TypeSubstitution? InferFromFunctionType(FunctionSymbol candidate, TypeSubstitution known, FunctionTypeSymbol wanted) {
        if (candidate.TypeParameters.IsEmpty) {
            return known;
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        if (!TypeSubstitution.Unify(Apply(candidate.FunctionType, known), wanted, candidate.TypeParameters, map)
            || candidate.TypeParameters.Any(p => !map.ContainsKey(p))) {
            return null;
        }

        var inferred = Combine(known, new(map));
        foreach (var typeParameter in candidate.TypeParameters) {
            if (typeParameter.Constraints.Any(c => !Conversions.Satisfies(map[typeParameter], inferred.Apply(c), _conversions.Equivalence))) {
                return null;
            }
        }

        return inferred;
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
            ) { InOutParameters = type.InOutParameters };

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

            case OptionalPatternSyntax optional:
                if (inputType is OptionalTypeSymbol optionalType) {
                    return new BoundSomePattern(optional, optionalType, BindPattern(optional.Pattern, optionalType.UnderlyingType));
                }

                if (!inputType.IsError) {
                    _diagnostics.Error(
                        ErrorCode.ERR_PatternTypeMismatch,
                        optional.QuestionToken.Span,
                        $"'{inputType.ToDisplayString()}' is not optional, so '?' has nothing to unwrap"
                    );
                }

                BindPattern(optional.Pattern, inputType);
                return new BoundErrorPattern(optional);

            default:
                return new BoundErrorPattern(pattern);
        }
    }

    BoundPattern BindLiteralPattern(
        LiteralPatternSyntax pattern,
        TypeSymbol inputType
    ) {
        var token = pattern.Token;

        // 'none' is the optional with no value, and nothing else: on a value
        // that is not optional it would match nothing, and where the type has
        // a case of that name ('`none`'), that case is '.none'.
        if (token.Kind == SyntaxKind.NoneKeyword && inputType is not OptionalTypeSymbol && !inputType.IsError) {
            var hint = inputType is NamedTypeSymbol named && named.GetMembers("none").Any(m => m is EnumCaseSymbol or UnionCaseSymbol)
                ? "; its case 'none' is '.none'"
                : "";
            _diagnostics.Error(
                ErrorCode.ERR_PatternTypeMismatch,
                pattern.Span,
                $"'{inputType.ToDisplayString()}' is not optional, so 'none' cannot match it{hint}"
            );
            return new BoundErrorPattern(pattern);
        }

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

        // An optional is 'union { Some(T), None }' (SYNTAX.md §4), so its two
        // cases can be spelled out as well as written 'let v?' and 'none'.
        if (owner is OptionalTypeSymbol optional) {
            return BindOptionalCasePattern(pattern, optional);
        }

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

        // A qualifier names the input's own type or nothing it can match: a
        // case of another union shares no tag with the input, and one of the
        // same name and a different payload would be taken for the input's.
        if (pattern.Qualifier is not null && !named.IsError
            && (inputType is not NamedTypeSymbol input || !input.OriginalDefinition.Equals(named.OriginalDefinition))) {
            if (!inputType.IsError) {
                _diagnostics.Error(
                    ErrorCode.ERR_PatternTypeMismatch,
                    pattern.Span,
                    $"'{named.ToDisplayString()}.{name}' is a case of '{named.ToDisplayString()}', not of '{inputType.ToDisplayString()}'"
                );
            }

            // Its bindings are still declared, so the arm does not report them unknown too.
            return new BoundErrorPattern(
                pattern,
                pattern.ArgumentList is { } mismatched
                    ? [..mismatched.Arguments.Select(a => BindPattern(a.Pattern, ErrorTypeSymbol.Instance))]
                    : []
            );
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

    /// <summary><c>.Some(p)</c> and <c>.None</c>, against an optional.</summary>
    BoundPattern BindOptionalCasePattern(UnionCasePatternSyntax pattern, OptionalTypeSymbol optional) {
        var name = pattern.Identifier.Text;
        var arguments = pattern.ArgumentList?.Arguments;
        switch (name) {
            case "None":
                if (arguments is { } extra) {
                    _diagnostics.Error(
                        ErrorCode.ERR_WrongPayloadCount,
                        pattern.Span,
                        $"'None' carries no values, but the pattern names {extra.Count}"
                    );
                }

                return new BoundLiteralPattern(pattern, optional, null, false);

            case "Some":
                if (arguments is not { Count: 1 } one) {
                    _diagnostics.Error(
                        ErrorCode.ERR_WrongPayloadCount,
                        pattern.Span,
                        $"'Some' carries 1 value, but the pattern names {arguments?.Count ?? 0}"
                    );

                    foreach (var argument in arguments ?? default) {
                        BindPattern(argument.Pattern, ErrorTypeSymbol.Instance);
                    }

                    return new BoundErrorPattern(pattern);
                }

                return new BoundSomePattern(pattern, optional, BindPattern(one[0].Pattern, optional.UnderlyingType));

            default: {
                // The optional's cases are never looked through: '.none' on a
                // 'Mode?' is not the 'Mode' case, which is '.Some(.none)'.
                var wrapped = optional.UnderlyingType is NamedTypeSymbol underlying
                              && underlying.GetMembers(name).Any(m => m is EnumCaseSymbol or UnionCaseSymbol)
                    ? $"; the '{optional.UnderlyingType.ToDisplayString()}' case is '.Some(.{name})'"
                    : "";
                _diagnostics.Error(
                    ErrorCode.ERR_UnknownUnionCase,
                    pattern.Identifier.Span,
                    $"'{optional.ToDisplayString()}' has no case named '{name}': an optional is '.Some' or '.None'{wrapped}"
                );
                return new BoundErrorPattern(pattern);
            }
        }
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

        // What the arms so far have left of the subject: a bare binding
        // binds that, so 'case let v' after 'case none' is the value.
        var remaining = subject.Type;

        foreach (var armSyntax in syntax.Arms) {
            PushScope();

            var patterns = ImmutableArray.CreateBuilder<BoundPattern>();
            BoundExpression? guard = null;
            BoundNode body;

            switch (armSyntax) {
                case MatchArmSyntax arm: {
                    foreach (var pattern in arm.Patterns) {
                        patterns.Add(BindArmPattern(pattern, subject.Type, remaining));
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
                        patterns.Add(BindArmPattern(pattern, subject.Type, remaining));
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

            if (guard is null) {
                remaining = Remaining(remaining, patterns);
            }
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
    ///     One of an arm's patterns. A bare <c>let v</c>, after arms that took
    ///     some of the subject's values, binds what is left: the value after
    ///     <c>none</c>, as <c>.Some(let v)</c> would; the last case of a union
    ///     after the others, as <c>let v as T</c> would.
    /// </summary>
    BoundPattern BindArmPattern(PatternSyntax pattern, TypeSymbol subject, TypeSymbol remaining) {
        // Only a single type is narrowed to: a binding of it reads it
        // straight out of the subject, where several left would be a union
        // of their own, which the subject would have to be converted to.
        // '(let v)' is 'let v'.
        var inner = pattern;
        while (inner is ParenthesizedPatternSyntax parenthesized) {
            inner = parenthesized.Pattern;
        }

        if (inner is not BindingPatternSyntax || remaining.Equals(subject) || remaining.IsError
            || remaining is StructuralUnionTypeSymbol) {
            return BindPattern(pattern, subject);
        }

        var binding = (BoundBindingPattern)BindPattern(pattern, remaining);
        return subject switch {
            OptionalTypeSymbol optional when remaining.Equals(optional.UnderlyingType) =>
                new BoundSomePattern(pattern, optional, binding),
            _ => new BoundTypeTestPattern(pattern, subject, remaining, binding.Local),
        };
    }

    /// <summary>
    ///     What of <paramref name="type" /> an unguarded arm leaves for the
    ///     arms after it: an optional without <c>none</c>, a union without
    ///     the cases it tested for.
    /// </summary>
    static TypeSymbol Remaining(TypeSymbol type, IEnumerable<BoundPattern> patterns) {
        foreach (var pattern in patterns.SelectMany(Alternatives)) {
            type = (type, pattern) switch {
                (OptionalTypeSymbol optional, BoundLiteralPattern { Value: null }) => optional.UnderlyingType,
                (_, BoundTypeTestPattern test) when StructuralUnionTypeSymbol.Of(type) is { } union && union.Cases.Any(c => c.Equals(test.TestedType)) =>
                    StructuralUnionTypeSymbol.Create(union.Cases.Where(c => !c.Equals(test.TestedType))),
                _ => type,
            };
        }

        return type;

        static IEnumerable<BoundPattern> Alternatives(BoundPattern pattern) =>
            pattern is BoundOrPattern or ? [..Alternatives(or.Left), ..Alternatives(or.Right)] : [pattern];
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

        var checker = new Exhaustiveness(Core) { Module = _scope.Module };
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

        // Every case named, of a union that may gain more: only 'default' covers those.
        // The fix covers every case left out, not just those the message names.
        Diagnostic Fixed(Diagnostic diagnostic) {
            if (match.Syntax is not MatchExpressionSyntax syntax) {
                return diagnostic;
            }

            var all = checker.Missing(rows, subject, int.MaxValue);
            return QuickFixes.MissingArms(diagnostic, syntax, [..all.Where(static m => m != Exhaustiveness.LaterCase)], all.Contains(Exhaustiveness.LaterCase));
        }

        if (missing is [Exhaustiveness.LaterCase]) {
            _diagnostics.Add(Fixed(Diagnostic.Error(
                ErrorCode.ERR_MatchNotExhaustive,
                match.Syntax.Span,
                $"match is not exhaustive: '{subject.ToDisplayString()}' is another module's, and not '@frozen', "
                + "so it may gain cases; add 'default => ...' for them"
            )));
            return;
        }

        var witness = missing.Count == 1
            ? $"'{missing[0]}'"
            : $"'{string.Join("', '", missing.Take(3))}'"
            + (missing.Count > 3 ? " and more" : string.Empty);

        _diagnostics.Add(Fixed(Diagnostic.Error(
            ErrorCode.ERR_MatchNotExhaustive,
            match.Syntax.Span,
            $"match is not exhaustive: {witness} not covered"
        )));
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
