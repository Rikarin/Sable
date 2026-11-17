using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
/// Answers questions about one file: what a name refers to, what an
/// expression's type is, what is wrong with it.
/// </summary>
/// <remarks>
/// This is the public face of the semantic layer, and docs/04 §1.1 is explicit
/// that it is a product rather than an internal detail: the language server,
/// the formatter's semantic rules, the linter and the documentation generator
/// all read it.
/// </remarks>
public sealed class SemanticModel
{
    private readonly Compilation _compilation;
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly Dictionary<SyntaxNode, BoundNode> _bound = [];
    private bool _bodiesBound;

    internal SemanticModel(Compilation compilation, SyntaxTree tree)
    {
        _compilation = compilation;
        SyntaxTree = tree;
    }

    public SyntaxTree SyntaxTree { get; }
    public Compilation Compilation => _compilation;

    /// <summary>Diagnostics from binding this file's bodies.</summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics()
    {
        EnsureBound();
        return _diagnostics;
    }

    /// <summary>The type an expression was bound to, or null if it was not bound.</summary>
    public TypeSymbol? GetTypeOf(ExpressionSyntax expression)
    {
        EnsureBound();
        return _bound.TryGetValue(expression, out var node) && node is BoundExpression bound
            ? bound.Type
            : null;
    }

    /// <summary>
    /// The type an expression has before the surrounding context converts it.
    /// </summary>
    public TypeSymbol? GetDeclaredTypeOf(ExpressionSyntax expression)
    {
        EnsureBound();
        if (!_bound.TryGetValue(expression, out var node)) return null;

        return node switch
        {
            BoundConversion conversion => conversion.Operand.Type,
            BoundExpression bound => bound.Type,
            _ => null,
        };
    }

    /// <summary>The bound node for a piece of syntax, if there is one.</summary>
    public BoundNode? GetBoundNode(SyntaxNode syntax)
    {
        EnsureBound();
        return _bound.TryGetValue(syntax, out var node) ? node : null;
    }

    /// <summary>The symbol an expression refers to, if it refers to one.</summary>
    public Symbol? GetSymbolOf(SyntaxNode syntax)
    {
        if (_compilation.GetDeclaredSymbol(syntax) is { } declared) return declared;

        EnsureBound();
        if (!_bound.TryGetValue(syntax, out var node)) return null;

        return node switch
        {
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

    /// <summary>The bound body of a function declaration, binding it if needed.</summary>
    public BoundBlock? GetBoundBody(FunctionDeclarationSyntax declaration)
    {
        EnsureBound();
        return _bound.TryGetValue(declaration, out var node) ? node as BoundBlock : null;
    }

    private void EnsureBound()
    {
        if (_bodiesBound) return;
        _bodiesBound = true;

        if (SyntaxTree.Root is not CompilationUnitSyntax unit) return;

        var scope = _compilation.GetFileScope(SyntaxTree);
        foreach (var member in unit.Members) BindMember(member, scope);
    }

    private void BindMember(SyntaxNode member, FileScope scope)
    {
        switch (member)
        {
            case FunctionDeclarationSyntax declaration:
                BindFunction(declaration, scope);
                return;

            case StructDeclarationSyntax type: BindAll(type.Members, scope); return;
            case ClassDeclarationSyntax type: BindAll(type.Members, scope); return;
            case ActorDeclarationSyntax type: BindAll(type.Members, scope); return;
            case ProtocolDeclarationSyntax type: BindAll(type.Members, scope); return;
            case UnionDeclarationSyntax type: BindAll(type.Members, scope); return;
            case EnumDeclarationSyntax type: BindAll(type.Members, scope); return;
            case ExtensionDeclarationSyntax type: BindAll(type.Members, scope); return;

            default:
                return;
        }
    }

    private void BindAll(SyntaxList<SyntaxNode> members, FileScope scope)
    {
        foreach (var member in members) BindMember(member, scope);
    }

    private void BindFunction(FunctionDeclarationSyntax declaration, FileScope scope)
    {
        if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol function) return;
        if (declaration.Body is null && declaration.ExpressionBody is null) return;

        var binder = new Binder(_compilation, scope, function);

        if (declaration.Body is { } body)
        {
            var bound = binder.BindFunctionBody(function, body);
            _bound[declaration] = bound;
        }
        else if (declaration.ExpressionBody is { } expression)
        {
            var bound = binder.BindExpressionBody(function, expression);
            _bound[declaration] = bound;
        }

        foreach (var (syntax, node) in binder.BoundNodes) _bound[syntax] = node;
        _diagnostics.AddRange(binder.Diagnostics);
    }
}
