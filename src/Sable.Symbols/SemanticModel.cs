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
public sealed class SemanticModel {
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

    /// <summary>The symbol an expression refers to, if it refers to one.</summary>
    public Symbol? GetSymbolOf(SyntaxNode syntax) {
        if (_compilation.GetDeclaredSymbol(syntax) is { } declared) {
            return declared;
        }

        EnsureBound();
        if (!_bound.TryGetValue(syntax, out var node)) {
            return null;
        }

        return node switch {
            BoundLocalAccess local => local.Local,
            BoundParameterAccess parameter => parameter.Parameter,
            BoundFieldAccess field => field.Field,
            BoundCall call => call.Function,
            BoundUnionCaseConstruction construction => construction.UnionCase,
            BoundUnionCaseGroup group => group.UnionCase,
            BoundBinaryOperator { Method: { } method } => method,
            _ => null,
        };
    }

    /// <summary>
    ///     What a statement does with the names around it: what it declares,
    ///     reads, writes and captures, and what would have to cross the boundary
    ///     if it were pulled out into a function of its own.
    /// </summary>
    public DataFlowAnalysis AnalyzeDataFlow(StatementSyntax statement) {
        EnsureBound();

        if (GetBoundNode(statement) is not { } region) {
            return DataFlowAnalysis.Failed;
        }

        if (EnclosingBody(statement) is not { } body) {
            return DataFlowAnalysis.Failed;
        }

        return RegionFlow.Data(region, body);
    }

    /// <summary>How control enters and leaves a statement.</summary>
    public ControlFlowAnalysis AnalyzeControlFlow(StatementSyntax statement) {
        EnsureBound();

        return GetBoundNode(statement) is { } region
            ? RegionFlow.Control(region)
            : ControlFlowAnalysis.Failed;
    }

    /// <summary>The bound body of the declaration a statement sits in.</summary>
    BoundBlock? EnclosingBody(SyntaxNode statement) {
        for (var node = statement.Parent; node is not null; node = node.Parent) {
            if (_bound.TryGetValue(node, out var bound) && bound is BoundBlock body) {
                return body;
            }
        }

        return null;
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

        if (SyntaxTree.Root is not CompilationUnitSyntax unit) {
            return;
        }

        var scope = _compilation.GetFileScope(SyntaxTree);
        foreach (var member in unit.Members) {
            BindMember(member, scope);
        }

        // Types declared inside bodies exist once those bodies are bound, and
        // their own members can declare more. Document order visits an outer
        // one before anything inside it, so one pass reaches them all.
        foreach (var statement in unit.DescendantNodes().OfType<DeclarationStatementSyntax>()) {
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
    }

    void BindMember(SyntaxNode member, FileScope scope) {
        switch (member) {
            case FunctionDeclarationSyntax declaration:
                BindBody(declaration, declaration.Body, declaration.ExpressionBody, scope);
                return;

            case InitDeclarationSyntax declaration:
                BindBody(declaration, declaration.Body, null, scope);
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
        if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol function) {
            return;
        }

        BindParameterDefaults(function, scope);

        if (body is null && expressionBody is null) {
            return;
        }

        var binder = new Binder(_compilation, scope, function);

        _bound[declaration] = body is not null
            ? binder.BindFunctionBody(function, body)
            : binder.BindExpressionBody(function, expressionBody!);

        Collect(binder);
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

            var binder = new Binder(_compilation, scope, function);
            binder.BindInitializer(clause.Value, parameter.Type);
            Collect(binder);
        }
    }

    void BindSubscript(SubscriptDeclarationSyntax declaration, FileScope scope) {
        if (declaration.ExpressionBody is { } expression) {
            BindBody(declaration, null, expression, scope);
            return;
        }

        if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol subscript) {
            return;
        }

        BindAccessors(declaration.Accessors, subscript, subscript.ReturnType, scope);
    }

    void BindField(FieldDeclarationSyntax declaration, FileScope scope) {
        if (_compilation.GetDeclaredSymbol(declaration) is not FieldSymbol field) {
            return;
        }

        if (declaration.Declarator.Initializer is { } initializer) {
            BindInitializer(initializer.Value, field, field.Type, scope);
        }

        BindAccessors(declaration.Accessors, field, field.Type, scope);
    }

    void BindConstant(ConstDeclarationSyntax declaration, FileScope scope) {
        if (_compilation.GetDeclaredSymbol(declaration) is not FieldSymbol constant) {
            return;
        }

        BindInitializer(declaration.Initializer.Value, constant, constant.Type, scope);
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
                continue;
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
                IsMutating = !isGetter
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

            var binder = new Binder(_compilation, scope, function);

            _bound[accessor] = accessor.Body is { } body
                ? binder.BindFunctionBody(function, body)
                : binder.BindExpressionBody(function, accessor.ExpressionBody!);

            Collect(binder);
        }
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
        var binder = new Binder(_compilation, scope, owner);
        binder.BindInitializer(value, expected);
        Collect(binder);
    }

    void Collect(Binder binder) {
        foreach (var (syntax, node) in binder.BoundNodes) {
            _bound[syntax] = node;
        }

        _diagnostics.AddRange(binder.Diagnostics);
    }
}
