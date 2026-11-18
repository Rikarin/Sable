using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Answers questions about one file: what a name refers to, what an
///     expression's type is, what is wrong with it.
/// </summary>
/// <remarks>
///     This is the public face of the semantic layer, and docs/04 §1.1 is explicit
///     that it is a product rather than an internal detail: the language server,
///     the formatter's semantic rules, the linter and the documentation generator
///     all read it.
/// </remarks>
public sealed partial class SemanticModel {
    readonly Compilation _compilation;
    readonly List<Diagnostic> _diagnostics = [];
    readonly Dictionary<SyntaxNode, BoundNode> _bound = [];
    bool _bodiesBound;

    internal SemanticModel(Compilation compilation, SyntaxTree tree) {
        _compilation = compilation;
        SyntaxTree = tree;
    }

    public SyntaxTree SyntaxTree { get; }
    public Compilation Compilation => _compilation;

    /// <summary>Diagnostics from binding this file's bodies.</summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics() {
        EnsureBound();
        return _diagnostics;
    }

    /// <summary>The type an expression was bound to, or null if it was not bound.</summary>
    public TypeSymbol? GetTypeOf(ExpressionSyntax expression) {
        EnsureBound();
        return _bound.TryGetValue(expression, out var node) && node is BoundExpression bound
            ? bound.Type
            : null;
    }

    /// <summary>
    ///     The type an expression has before the surrounding context converts it.
    /// </summary>
    public TypeSymbol? GetDeclaredTypeOf(ExpressionSyntax expression) {
        EnsureBound();
        if (!_bound.TryGetValue(expression, out var node)) {
            return null;
        }

        return node switch {
            BoundConversion conversion => conversion.Operand.Type,
            BoundExpression bound => bound.Type,
            _ => null,
        };
    }

    /// <summary>The bound node for a piece of syntax, if there is one.</summary>
    public BoundNode? GetBoundNode(SyntaxNode syntax) {
        EnsureBound();
        return _bound.TryGetValue(syntax, out var node) ? node : null;
    }

    readonly Dictionary<AccessorSyntax, FunctionSymbol> _accessors = [];

    /// <summary>
    ///     The function an accessor's body was bound as: its parameters, and
    ///     the member it belongs to as its container. Code generation lowers
    ///     a getter as a method, with this as its symbol.
    /// </summary>
    public FunctionSymbol? GetAccessorSymbol(AccessorSyntax accessor) {
        EnsureBound();
        return _accessors.GetValueOrDefault(accessor);
    }

    /// <summary>
    ///     The same symbol, without binding any body: what a unit of a
    ///     separately compiled program needs of another unit's accessors — to
    ///     name them and call them. Binding later gives the same symbol.
    /// </summary>
    public FunctionSymbol? GetAccessorDeclaration(AccessorSyntax accessor) {
        if (_accessors.TryGetValue(accessor, out var known) || _bodiesBound) {
            return known;
        }

        if (accessor.Body is null && accessor.ExpressionBody is null
            && !(accessor.Parent?.Parent is FieldDeclarationSyntax owner && _compilation.GetDeclaredSymbol(owner) is FieldSymbol property && IsPlainAccessor(accessor, property))) {
            return null;
        }

        return accessor.Parent?.Parent switch {
            FieldDeclarationSyntax declaration when _compilation.GetDeclaredSymbol(declaration) is FieldSymbol field => AccessorFunction(accessor, field, field.Type),
            SubscriptDeclarationSyntax { ExpressionBody: null } declaration when _compilation.GetDeclaredSymbol(declaration) is FunctionSymbol subscript =>
                AccessorFunction(accessor, subscript, subscript.ReturnType),
            _ => null,
        };
    }

    /// <summary>The symbol an expression refers to, if it refers to one.</summary>
    /// <remarks>
    ///     A declaration answers with what it declares; a name in a type's
    ///     place with the type it resolved to; a call's callee with the
    ///     function overload resolution chose.
    /// </remarks>
    public Symbol? GetSymbolOf(SyntaxNode syntax) {
        if (DeclaredSymbol(syntax) is { } declared) {
            return declared;
        }

        EnsureBound();
        if (_compilation.TypeBinder.ResolvedName(syntax) is { } named) {
            return named;
        }

        if (!_bound.TryGetValue(syntax, out var node)) {
            return null;
        }

        return SymbolOf(node, syntax);
    }

    Symbol? SymbolOf(BoundNode node, SyntaxNode syntax) =>
        node switch {
            BoundLocalAccess local => local.Local,
            BoundParameterAccess parameter => parameter.Parameter,
            BoundFieldAccess field => field.Field,
            BoundCall call => call.Function,
            BoundUnionCaseConstruction construction => construction.UnionCase,
            BoundUnionCaseGroup group => group.UnionCase,
            BoundBinaryOperator { Method: { } method } => method,
            BoundTypeExpression { ReferencedType: NamedTypeSymbol type } => type.OriginalDefinition,
            BoundTypeExpression { ReferencedType: var type } => type,
            BoundConversion conversion when ReferenceEquals(conversion.Operand.Syntax, syntax) => SymbolOf(conversion.Operand, syntax),
            BoundFunctionGroup group => CalleeOf(group, syntax) ?? (group.Candidates.IsEmpty ? null : group.Candidates[0]),
            _ => null,
        };

    /// <summary>The overload a call through a function group chose, when the group is a call's callee.</summary>
    FunctionSymbol? CalleeOf(BoundFunctionGroup group, SyntaxNode syntax) {
        // 'f(x)' and 'a.f(x)': the call is bound on the invocation, with the
        // group's syntax as its callee.
        var callee = syntax.Parent is MemberAccessExpressionSyntax access && ReferenceEquals(access.Name, syntax) ? access : syntax;
        return callee.Parent is InvocationExpressionSyntax invocation
               && ReferenceEquals(invocation.Expression, callee)
               && _bound.TryGetValue(invocation, out var call)
               && Unconverted(call) is BoundCall { Function: var function }
               && group.Candidates.Contains(function)
            ? function
            : null;
    }

    static BoundNode Unconverted(BoundNode node) =>
        node is BoundConversion conversion ? Unconverted(conversion.Operand) : node;

    /// <summary>
    ///     What a statement does with the names around it: what it declares,
    ///     reads, writes and captures, and what would have to cross the boundary
    ///     if it were pulled out into a function of its own.
    /// </summary>
    public DataFlowAnalysis AnalyzeDataFlow(StatementSyntax statement) => AnalyzeDataFlow(statement, statement);

    /// <summary>
    ///     The same for a run of statements in one block, from
    ///     <paramref name="first" /> to <paramref name="last" /> inclusive —
    ///     the selection an "extract method" refactoring works on.
    /// </summary>
    public DataFlowAnalysis AnalyzeDataFlow(StatementSyntax first, StatementSyntax last) {
        EnsureBound();

        if (Region(first, last) is not { } region || FunctionBody(first) is not { } body) {
            return DataFlowAnalysis.Failed;
        }

        return RegionFlow.Data(region, body);
    }

    /// <summary>How control enters and leaves a statement.</summary>
    public ControlFlowAnalysis AnalyzeControlFlow(StatementSyntax statement) => AnalyzeControlFlow(statement, statement);

    /// <summary>How control enters and leaves a run of statements in one block.</summary>
    public ControlFlowAnalysis AnalyzeControlFlow(StatementSyntax first, StatementSyntax last) {
        EnsureBound();

        return Region(first, last) is { } region
            ? RegionFlow.Control(region)
            : ControlFlowAnalysis.Failed;
    }

    /// <summary>
    ///     The bound statements from <paramref name="first" /> to
    ///     <paramref name="last" />, or null unless both sit in the same block
    ///     in that order.
    /// </summary>
    List<BoundStatement>? Region(StatementSyntax first, StatementSyntax last) {
        if (ReferenceEquals(first, last)) {
            return _bound.TryGetValue(first, out var single) && single is BoundStatement statement
                ? [statement]
                : null;
        }

        if (first.Parent is not BlockSyntax block || !ReferenceEquals(first.Parent, last.Parent)) {
            return null;
        }

        var region = new List<BoundStatement>();
        var inside = false;
        foreach (var statement in block.Statements) {
            inside |= ReferenceEquals(statement, first);
            if (inside) {
                if (!_bound.TryGetValue(statement, out var bound) || bound is not BoundStatement boundStatement) {
                    return null;
                }

                region.Add(boundStatement);
            }

            if (ReferenceEquals(statement, last)) {
                return inside ? region : null;
            }
        }

        return null;
    }

    /// <summary>
    ///     The bound body of the function a statement sits in: the outermost
    ///     bound block above it, so that a closure's statements are analysed
    ///     against the whole function around them.
    /// </summary>
    BoundBlock? FunctionBody(SyntaxNode statement) {
        BoundBlock? outermost = null;
        for (var node = statement.Parent; node is not null; node = node.Parent) {
            if (_bound.TryGetValue(node, out var bound) && bound is BoundBlock body) {
                outermost = body;
            }

            // A local function's statements belong to the function around it;
            // any other declaration — a type member, a method of a local type —
            // is a body of its own.
            if (node is DeclarationSyntax && node.Parent is not DeclarationStatementSyntax) {
                break;
            }
        }

        return outermost;
    }

    /// <summary>The bound body of a function declaration, binding it if needed.</summary>
    public BoundBlock? GetBoundBody(FunctionDeclarationSyntax declaration) {
        EnsureBound();
        return _bound.TryGetValue(declaration, out var node) ? node as BoundBlock : null;
    }

    void EnsureBound() {
        if (_bodiesBound) {
            return;
        }

        _bodiesBound = true;

        // A body cannot be bound before the signatures it mentions, including
        // base types and whatever extensions added elsewhere.
        _compilation.TypeBinder.ForceAll();

        SyntaxNode root;
        var scope = _compilation.GetFileScope(SyntaxTree);
        if (_member is not null) {
            // A member model binds its one declaration (Tooling).
            root = _member;
            BindMember(_member, scope);
        } else if (SyntaxTree.Root is CompilationUnitSyntax unit) {
            root = unit;
            foreach (var member in unit.Members) {
                BindMember(member, scope);
            }
        } else {
            return;
        }

        // Types declared inside bodies exist once those bodies are bound, and
        // their own members can declare more. Document order visits an outer
        // one before anything inside it, so one pass reaches them all.
        foreach (var statement in root.DescendantNodes().OfType<DeclarationStatementSyntax>()) {
            if (statement.Declaration is FunctionDeclarationSyntax) {
                continue;
            }

            if (_compilation.GetDeclaredSymbol(statement.Declaration) is null) {
                continue;
            }

            BindMember(statement.Declaration, scope);
        }

        // Binding those may have bound signatures of their own.
        _compilation.TypeBinder.ForceAll();

        // '@test' and '@bench' functions the harness could not call.
        _diagnostics.AddRange(_compilation.CheckTestFunctions(SyntaxTree));
    }

    void BindMember(SyntaxNode member, FileScope scope) {
        switch (member) {
            case FunctionDeclarationSyntax declaration:
                BindBody(declaration, declaration.Body, declaration.ExpressionBody, scope);
                return;

            case InitDeclarationSyntax declaration:
                BindBody(declaration, declaration.Body, null, scope);
                CheckConvenience(declaration);
                return;

            case DeinitDeclarationSyntax declaration:
                BindBody(declaration, declaration.Body, null, scope);
                return;

            case OperatorDeclarationSyntax declaration:
                BindBody(declaration, declaration.Body, declaration.ExpressionBody, scope);
                return;

            case SubscriptDeclarationSyntax declaration:
                BindSubscript(declaration, scope);
                return;

            case FieldDeclarationSyntax declaration:
                BindField(declaration, scope);
                return;

            case ConstDeclarationSyntax declaration:
                BindConstant(declaration, scope);
                return;

            case ComptimeDeclarationSyntax declaration:
                BindComptime(declaration, scope);
                return;

            case StructDeclarationSyntax type:
                BindAll(type.Members, scope);
                return;
            case ClassDeclarationSyntax type:
                BindAll(type.Members, scope);
                return;
            case ActorDeclarationSyntax type:
                BindAll(type.Members, scope);
                return;
            case ProtocolDeclarationSyntax type:
                BindAll(type.Members, scope);
                return;
            case UnionDeclarationSyntax type:
                BindAll(type.Members, scope);
                return;
            case EnumDeclarationSyntax type:
                BindAll(type.Members, scope);
                return;
            case ExtensionDeclarationSyntax type:
                BindAll(type.Members, scope);
                return;

            default:
                return;
        }
    }

    void BindAll(SyntaxList<SyntaxNode> members, FileScope scope) {
        foreach (var member in members) {
            BindMember(member, scope);
        }
    }

    /// <summary>Binds whichever body a member declaration carries.</summary>
    void BindBody(
        SyntaxNode declaration,
        BlockSyntax? body,
        ExpressionSyntax? expressionBody,
        FileScope scope
    ) {
        if (DeclaredSymbol(declaration) is not FunctionSymbol function) {
            return;
        }

        BindParameterDefaults(function, scope);

        if (declaration is FunctionDeclarationSyntax { Attributes.Count: > 0 } written) {
            _diagnostics.AddRange(_compilation.CheckForeignSignature(function, written));
        }

        if (body is null && expressionBody is null) {
            return;
        }

        var binder = NewBinder(scope, function);

        var bound = _bound[declaration] = body is not null
            ? binder.BindFunctionBody(function, body)
            : binder.BindExpressionBody(function, expressionBody!);

        Collect(binder);

        // What runs in the compiler: a const fn's body, a comptime block.
        if (function.IsConst || CompileTimeRules.MightNeedChecking(SyntaxTree, (SyntaxNode?)body ?? expressionBody!)) {
            CompileTimeRules.CheckBody(bound, function, _diagnostics);
            RecordComptime(bound, function);
        }
    }

    /// <summary>
    ///     Each <c>comptime</c> block in a body, as a function of its own: it
    ///     runs as the program is compiled, apart from the function it is
    ///     written in.
    /// </summary>
    void RecordComptime(BoundNode node, FunctionSymbol function) {
        if (node is BoundScopedBlock { ScopeKind: ScopeKind.Comptime } comptime) {
            _comptime[comptime.Syntax] = (ComptimeFunction(comptime.Syntax, function.ContainingSymbol!), comptime.Body);
            return;
        }

        foreach (var child in node.Children) {
            RecordComptime(child, function);
        }
    }

    FunctionSymbol ComptimeFunction(SyntaxNode syntax, Symbol owner) =>
        new("comptime", owner, syntax, Accessibility.Private, FunctionKind.Ordinary) {
            IsStatic = owner is NamedTypeSymbol,
            IsConst = true,
            ReturnTypeResolver = () => _compilation.Core.Void,
        };

    /// <summary>
    ///     A <c>convenience init</c> hands the work to another initialiser of
    ///     its type, <c>self.init(…)</c>, which initialises the fields: one
    ///     that does not is an ordinary initialiser, so it says so.
    /// </summary>
    void CheckConvenience(InitDeclarationSyntax declaration) {
        if (!declaration.Modifiers.Any(SyntaxKind.ConvenienceKeyword) || _bound.GetValueOrDefault(declaration) is not BoundBlock body) {
            return;
        }

        if (!Delegates(body)) {
            _diagnostics.Add(Diagnostic.Error(
                ErrorCode.ERR_ConvenienceMustDelegate,
                declaration.InitKeyword.Span,
                "a 'convenience init' hands the work to another initialiser, 'self.init(…)'; without one, drop 'convenience'"
            ));
        }

        static bool Delegates(BoundNode node) =>
            node is BoundCall { Function.FunctionKind: FunctionKind.Initializer, Receiver: BoundSelfAccess }
            || node.Children.Any(Delegates);
    }

    /// <summary>
    ///     Binds a parameter's default value against the parameter's type.
    /// </summary>
    /// <remarks>
    ///     It is part of the signature rather than of the body, so it is bound
    ///     even for a declaration that has none.
    /// </remarks>
    void BindParameterDefaults(FunctionSymbol function, FileScope scope) {
        foreach (var parameter in function.Parameters) {
            if (parameter.DeclaringSyntax.IsEmpty) {
                continue;
            }

            if (parameter.DeclaringSyntax[0] is not ParameterSyntax syntax) {
                continue;
            }

            if (syntax.DefaultValue is not { } clause) {
                continue;
            }

            var binder = NewBinder(scope, function);
            binder.BindInitializer(clause.Value, parameter.Type);
            Collect(binder);
        }
    }

    void BindSubscript(SubscriptDeclarationSyntax declaration, FileScope scope) {
        if (declaration.ExpressionBody is { } expression) {
            BindBody(declaration, null, expression, scope);
            return;
        }

        if (DeclaredSymbol(declaration) is not FunctionSymbol subscript) {
            return;
        }

        BindAccessors(declaration.Accessors, subscript, subscript.ReturnType, scope);
    }

    void BindField(FieldDeclarationSyntax declaration, FileScope scope) {
        if (DeclaredSymbol(declaration) is not FieldSymbol field) {
            return;
        }

        _diagnostics.AddRange(_compilation.CheckForeignVariable(field, declaration));

        if (field.IsLazy && _compilation.LazyProperties.TryGetValue(field, out var lazy) && declaration.Declarator.Initializer is { } computed) {
            BindLazy(declaration, field, lazy, computed.Value, scope);
            CheckReferenceKind(declaration, field);
            return;
        }

        // A property kept in 'field', or observed, starts with its initial
        // value in its storage.
        if (declaration.Declarator.Initializer is { } initializer) {
            BindInitializer(initializer.Value, field.BackingField ?? field, field.Type, scope);
        }

        BindAccessors(declaration.Accessors, field, field.Type, scope);
        CheckReferenceKind(declaration, field);

        if (_compilation.ObservedProperties.TryGetValue(field, out var observed)) {
            BindObserved(declaration, field, observed);
        }
    }

    /// <summary>
    ///     An observed property's getter, <c>return self.storage</c>, and its
    ///     setter, written here:
    ///     <code>
    ///     self.willSet(newValue)
    ///     let oldValue = self.storage
    ///     self.storage = newValue
    ///     self.didSet(oldValue)
    ///     </code>
    ///     with only the observers it has. The observers are the accessors
    ///     written in source, bound with the others.
    /// </summary>
    void BindObserved(FieldDeclarationSyntax declaration, FieldSymbol field, Compilation.LazyAccessors accessors) {
        var backing = field.BackingField!;
        var at = declaration.Declarator;
        var type = field.ContainingSymbol as NamedTypeSymbol;

        BoundSelfAccess? Self(bool mutable) => field.IsStatic || type is null ? null : new BoundSelfAccess(at, type, mutable);
        BoundFieldAccess Storage(bool mutable) => new(at, Self(mutable), backing);

        _lazyBodies[accessors.Getter] = new BoundBlock(at, [], [new BoundReturn(at, Storage(false))]);

        AccessorSyntax? willSet = null, didSet = null;
        foreach (var accessor in declaration.Accessors!.Accessors) {
            if (accessor.Keyword.Kind == SyntaxKind.WillSetKeyword) {
                willSet = accessor;
            } else if (accessor.Keyword.Kind == SyntaxKind.DidSetKeyword) {
                didSet = accessor;
            }
        }

        BoundStatement Observe(AccessorSyntax observer, BoundExpression value) {
            var function = AccessorFunction(observer, field, field.Type);
            return new BoundExpressionStatement(at, new BoundCall(at, Self(true), function, [value], _compilation.Core.Void));
        }

        var setter = accessors.Setter;
        BoundExpression NewValue() => new BoundParameterAccess(at, setter.Parameters[0]);

        var locals = new List<LocalSymbol>();
        var statements = new List<BoundStatement>();
        if (willSet is not null) {
            statements.Add(Observe(willSet, NewValue()));
        }

        LocalSymbol? oldValue = null;
        if (didSet is not null) {
            oldValue = new LocalSymbol("oldValue", setter, at, false) { TypeResolver = () => field.Type };
            locals.Add(oldValue);
            statements.Add(new BoundLocalDeclaration(at, oldValue, Storage(false)));
        }

        statements.Add(new BoundExpressionStatement(at, new BoundAssignment(at, Storage(true), NewValue())));
        if (didSet is not null) {
            statements.Add(Observe(didSet, new BoundLocalAccess(at, oldValue!)));
        }

        _lazyBodies[setter] = new BoundBlock(at, [..locals], [..statements]);
    }

    readonly Dictionary<FunctionSymbol, BoundBlock> _lazyBodies = [];

    /// <summary>The body the compiler wrote for a lazy or observed property's getter or setter.</summary>
    public BoundBlock? GetLazyBody(FunctionSymbol accessor) {
        EnsureBound();
        return _lazyBodies.GetValueOrDefault(accessor);
    }

    /// <summary>
    ///     A lazy property's accessors, written here: the getter
    ///     <code>
    ///     if let stored = self.storage { return stored }
    ///     let made = initialiser
    ///     self.storage = made
    ///     return made
    ///     </code>
    ///     the initialiser bound in it, so that it sees <c>self</c>; and the
    ///     setter, <c>self.storage = newValue</c>. The storage starts as none.
    /// </summary>
    void BindLazy(FieldDeclarationSyntax declaration, FieldSymbol field, Compilation.LazyAccessors lazy, ExpressionSyntax initializer, FileScope scope) {
        var backing = field.BackingField!;
        var type = (NamedTypeSymbol)field.ContainingSymbol!;
        var at = declaration.Declarator;

        var binder = NewBinder(scope, lazy.Getter);
        var value = binder.BindInitializer(initializer, field.Type);
        Collect(binder);

        _initializers[backing] = new BoundLiteral(at, backing.Type, null);

        BoundFieldAccess Storage(bool mutable) => new(at, new BoundSelfAccess(at, type, mutable), backing);
        BoundExpression Wrapped(BoundExpression made) => new BoundConversion(at, backing.Type, made, ConversionKind.OptionalWrap);

        var stored = new LocalSymbol("stored", lazy.Getter, at, false) { TypeResolver = () => field.Type };
        var made = new LocalSymbol("made", lazy.Getter, at, false) { TypeResolver = () => field.Type };
        _lazyBodies[lazy.Getter] = new BoundBlock(at, [stored, made], [
            new BoundIf(at,
                new BoundConditionBinding(at, _compilation.Core.Bool, Storage(false), null, stored),
                new BoundBlock(at, [], [new BoundReturn(at, new BoundLocalAccess(at, stored))]),
                null),
            new BoundLocalDeclaration(at, made, value),
            new BoundExpressionStatement(at, new BoundAssignment(at, Storage(true), Wrapped(new BoundLocalAccess(at, made)))),
            new BoundReturn(at, new BoundLocalAccess(at, made)),
        ]);

        _lazyBodies[lazy.Setter] = new BoundBlock(at, [], [
            new BoundExpressionStatement(at, new BoundAssignment(at, Storage(true), Wrapped(new BoundParameterAccess(at, lazy.Setter.Parameters[0])))),
        ]);
    }

    /// <summary>
    ///     <c>weak</c> holds an object that may go, so it is a <c>var</c> of an
    ///     optional class type, which becomes none when it does; <c>unowned</c>
    ///     holds an object it assumes outlives it, of a class type.
    /// </summary>
    void CheckReferenceKind(FieldDeclarationSyntax declaration, FieldSymbol field) {
        var span = declaration.Declarator.Span;
        switch (field.ReferenceKind) {
            case ReferenceKind.Weak when !field.IsMutable || field.Type is not OptionalTypeSymbol { UnderlyingType.TypeKind: TypeKind.Class }:
                _diagnostics.Add(Diagnostic.Error(
                    ErrorCode.ERR_WeakNeedsOptionalClass,
                    span,
                    $"'weak' needs a 'var' of an optional class type, since it becomes none when its object goes; '{field.Name}' is "
                    + (field.IsMutable ? $"'{field.Type.ToDisplayString()}'" : "a 'let'")
                ));
                break;

            case ReferenceKind.Unowned when field.Type.TypeKind != TypeKind.Class:
                _diagnostics.Add(Diagnostic.Error(
                    ErrorCode.ERR_UnownedNeedsClass,
                    span,
                    $"'unowned' needs a class type; '{field.Name}' is '{field.Type.ToDisplayString()}'"
                ));
                break;
        }
    }

    readonly Dictionary<SyntaxNode, (FunctionSymbol Function, BoundBlock Body)> _comptime = [];
    readonly Dictionary<FieldSymbol, FunctionSymbol> _constantFunctions = [];

    /// <summary>
    ///     The file's <c>comptime</c> blocks, among its declarations and in
    ///     its bodies: each a function of no parameters, with its body, that
    ///     lowering runs while the program is compiled.
    /// </summary>
    public IReadOnlyList<(SyntaxNode Syntax, FunctionSymbol Function, BoundBlock Body)> GetComptimeBlocks() {
        EnsureBound();
        return [.._comptime.OrderBy(static c => c.Key.Span.Start).Select(static c => (c.Key, c.Value.Function, c.Value.Body))];
    }

    /// <summary>
    ///     A constant's value as a function of no parameters, for lowering to
    ///     compute it with while the program is compiled.
    /// </summary>
    public FunctionSymbol? GetConstantFunction(FieldSymbol constant) {
        EnsureBound();
        return _constantFunctions.GetValueOrDefault(constant);
    }

    void BindComptime(ComptimeDeclarationSyntax declaration, FileScope scope) {
        // In a type, it sees the type's static members, as a static method would.
        var owner = declaration.Parent is { } parent && DeclaredSymbol(parent) is NamedTypeSymbol type ? (Symbol)type : scope.Module;
        var function = ComptimeFunction(declaration, owner);
        var binder = NewBinder(scope, function);
        var body = binder.BindFunctionBody(function, declaration.Body);
        _bound[declaration] = body;
        _comptime[declaration] = (function, body);
        Collect(binder);
        CompileTimeRules.CheckComptime(body, _diagnostics);
    }

    void BindConstant(ConstDeclarationSyntax declaration, FileScope scope) {
        if (DeclaredSymbol(declaration) is not FieldSymbol constant) {
            return;
        }

        BindInitializer(declaration.Initializer.Value, constant, constant.Type, scope);
        if (_initializers.GetValueOrDefault(constant) is { } value) {
            CompileTimeRules.CheckConstant(value, constant, _diagnostics);

            // Plain integer arithmetic is checked as it is bound, so that the
            // editor reports its overflow (S3201); the value itself, and
            // anything that calls, is computed by lowering, in the
            // interpreter (SilGen.Constants.cs), which reports the same way.
            if (!value.HasError) {
                var folding = new DiagnosticBag();
                ConstantFolding.Fold(value, _compilation.Core, folding);
                _diagnostics.AddRange(folding);
            }
        }

        _constantFunctions[constant] = new(constant.Name, constant.ContainingSymbol!, declaration, Accessibility.Private, FunctionKind.Ordinary) {
            IsStatic = true,
            IsConst = true,
            ReturnTypeResolver = () => _compilation.Core.Void,
        };
    }

    /// <summary>
    ///     Binds the accessors of a computed member, each as a little function.
    /// </summary>
    /// <remarks>
    ///     Accessors have no symbols of their own: a getter is the member's type
    ///     returned, and a setter takes it as <c>newValue</c> and returns nothing.
    ///     Synthesising that here is enough for the body to be checked, and keeps
    ///     the declaration table free of symbols nothing can name.
    /// </remarks>
    void BindAccessors(
        AccessorListSyntax? accessors,
        Symbol owner,
        TypeSymbol valueType,
        FileScope scope
    ) {
        if (accessors is null) {
            return;
        }

        foreach (var accessor in accessors.Accessors) {
            if (accessor.Body is null && accessor.ExpressionBody is null) {
                // A property kept in 'field': 'get' and 'set' written alone
                // are the plain ones, reading and writing it.
                if (IsPlainAccessor(accessor, owner) && owner is FieldSymbol { BackingField: { } backing } property) {
                    _bound[accessor] = PlainAccessor(accessor, AccessorFunction(accessor, owner, valueType), property, backing);
                }

                continue;
            }

            var function = AccessorFunction(accessor, owner, valueType);
            var binder = NewBinder(scope, function);

            _bound[accessor] = accessor.Body is { } body
                ? binder.BindFunctionBody(function, body)
                : binder.BindExpressionBody(function, accessor.ExpressionBody!);

            Collect(binder);
        }
    }

    /// <summary>A bodiless <c>get</c> or <c>set</c> of a property kept in <c>field</c>.</summary>
    static bool IsPlainAccessor(AccessorSyntax accessor, Symbol owner) =>
        accessor is { Body: null, ExpressionBody: null }
        && accessor.Keyword.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
        && owner is FieldSymbol { BackingField: not null };

    /// <summary>The body a plain accessor has: <c>return field</c>, or <c>field = newValue</c>.</summary>
    static BoundBlock PlainAccessor(AccessorSyntax accessor, FunctionSymbol function, FieldSymbol property, FieldSymbol backing) {
        var self = property.IsStatic || property.ContainingSymbol is not NamedTypeSymbol type
            ? null
            : new BoundSelfAccess(accessor, type, function.IsMutating);
        var storage = new BoundFieldAccess(accessor, self, backing);
        BoundStatement statement = accessor.Keyword.Kind == SyntaxKind.GetKeyword
            ? new BoundReturn(accessor, storage)
            : new BoundExpressionStatement(accessor, new BoundAssignment(accessor, storage, new BoundParameterAccess(accessor, function.Parameters[^1])));
        return new BoundBlock(accessor, [], [statement]);
    }

    /// <summary>An accessor's function, made the first time it is asked for: binding its body and declaring it share one.</summary>
    FunctionSymbol AccessorFunction(AccessorSyntax accessor, Symbol owner, TypeSymbol valueType) {
        if (_accessors.TryGetValue(accessor, out var existing)) {
            return existing;
        }

        var isGetter = accessor.Keyword.Kind == SyntaxKind.GetKeyword;
        var name = accessor.Keyword.Text;

        var function = new FunctionSymbol(
            name,
            owner,
            accessor,
            owner.DeclaredAccessibility,
            FunctionKind.Accessor
        ) {
            // A setter writes the value it belongs to; a getter does not.
            IsMutating = !isGetter,
            // 'async get' may await, as its readers do.
            IsAsync = isGetter && accessor.Modifiers.Any(SyntaxKind.AsyncKeyword),

            // A static property's accessors have no value to belong to.
            IsStatic = owner is FieldSymbol { IsStatic: true },
        };

        // A subscript's accessors see its indices.
        var parameters = owner is FunctionSymbol { FunctionKind: FunctionKind.Subscript } sub
            ? sub.Parameters
            : [];

        if (!isGetter) {
            var parameterName = accessor.ParameterName.IsNone
                ? DefaultParameterName(accessor.Keyword.Kind)
                : accessor.ParameterName.Text;

            var parameter = new ParameterSymbol(parameterName, null, function, accessor, parameters.Length);
            parameter.TypeResolver = () => valueType;
            parameters = parameters.Add(parameter);
        }

        function.Parameters = parameters;

        var returnType = isGetter ? valueType : _compilation.Core.Void;
        function.ReturnTypeResolver = () => returnType;
        _accessors[accessor] = function;
        return function;
    }

    /// <summary>What a setter-like accessor calls its incoming value.</summary>
    static string DefaultParameterName(SyntaxKind keyword) =>
        keyword switch {
            SyntaxKind.DidSetKeyword => "oldValue",
            _ => "newValue",
        };

    void BindInitializer(
        ExpressionSyntax value,
        Symbol owner,
        TypeSymbol expected,
        FileScope scope
    ) {
        var binder = NewBinder(scope, owner);
        var bound = binder.BindInitializer(value, expected);
        if (owner is FieldSymbol field) {
            _initializers[field] = bound;
        }

        Collect(binder);
    }

    readonly Dictionary<FieldSymbol, BoundExpression> _initializers = [];

    /// <summary>A stored property's initial value, bound and converted to its type: what each initialiser stores first.</summary>
    public BoundExpression? GetFieldInitializer(FieldSymbol field) {
        EnsureBound();
        return _initializers.GetValueOrDefault(field);
    }

    void Collect(Binder binder) {
        foreach (var (syntax, node) in binder.BoundNodes) {
            _bound[syntax] = node;
        }

        CollectScope(binder);

        _diagnostics.AddRange(binder.Diagnostics);
    }
}
