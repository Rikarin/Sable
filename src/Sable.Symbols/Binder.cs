using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
/// Binds statements and expressions: resolves every name to a symbol and
/// every expression to a type.
/// </summary>
/// <remarks>
/// <para>Scopes are a stack of dictionaries rather than a chain of binder
/// objects. The chain is Roslyn's shape and is better when binders carry
/// behaviour; here they only carry names, and a stack says that plainly.</para>
/// <para>Lookup order for a bare name: locals innermost first, then the
/// containing function's parameters, then the members of the enclosing type,
/// then the module, then imports, then <c>Sable.Core</c>.</para>
/// </remarks>
public sealed class Binder
{
    private readonly Compilation _compilation;
    private readonly FileScope _scope;
    private readonly Conversions _conversions;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly List<Dictionary<string, Symbol>> _locals = [];
    private readonly Dictionary<SyntaxNode, BoundNode> _bound = [];

    private Symbol _containingSymbol;
    private TypeSymbol _returnType;
    private int _loopDepth;

    internal Binder(Compilation compilation, FileScope scope, Symbol containingSymbol)
    {
        _compilation = compilation;
        _scope = scope;
        _containingSymbol = containingSymbol;
        _conversions = new Conversions(compilation.Core);
        _returnType = compilation.Core.Void;
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.ToList();

    private CoreTypes Core => _compilation.Core;

    /// <summary>The bound node produced for a piece of syntax, if any.</summary>
    public BoundNode? GetBound(SyntaxNode syntax) =>
        _bound.TryGetValue(syntax, out var node) ? node : null;

    /// <summary>Everything this binder bound, for the semantic model to keep.</summary>
    public IReadOnlyDictionary<SyntaxNode, BoundNode> BoundNodes => _bound;

    private T Record<T>(SyntaxNode syntax, T node) where T : BoundNode
    {
        _bound[syntax] = node;
        return node;
    }

    // -----------------------------------------------------------------------
    // Entry points
    // -----------------------------------------------------------------------

    /// <summary>Binds a function body against its signature.</summary>
    public BoundBlock BindFunctionBody(FunctionSymbol function, BlockSyntax body)
    {
        var savedSymbol = _containingSymbol;
        var savedReturn = _returnType;

        _containingSymbol = function;
        _returnType = function.ReturnType;

        PushScope();
        foreach (var parameter in function.Parameters) Declare(parameter.Name, parameter);

        var bound = BindBlock(body);
        CheckFlow(function, body, bound);

        PopScope();
        _containingSymbol = savedSymbol;
        _returnType = savedReturn;
        return bound;
    }

    /// <summary>Checks that a function returns on every path, and warns on dead code.</summary>
    private void CheckFlow(FunctionSymbol function, BlockSyntax body, BoundBlock bound)
    {
        if (FlowAnalysis.FirstUnreachable(bound) is { } unreachable)
        {
            _diagnostics.Add(Diagnostic.Warning(
                ErrorCode.WRN_UnreachableCode, unreachable.Syntax.Span,
                "this statement can never be reached"));
        }

        if (function.ReturnType.Equals(Core.Void) || function.ReturnType.IsError) return;
        if (FlowAnalysis.AlwaysExits(bound)) return;

        // Point at the closing brace, which is where the path runs out.
        _diagnostics.Error(ErrorCode.ERR_NotAllPathsReturn, body.CloseBraceToken.Span,
            $"not all paths return a value of type "
            + $"'{function.ReturnType.ToDisplayString()}'");
    }

    /// <summary>Binds an expression-bodied function, as if it returned its value.</summary>
    public BoundBlock BindExpressionBody(FunctionSymbol function, ExpressionSyntax expression)
    {
        var savedSymbol = _containingSymbol;
        var savedReturn = _returnType;

        _containingSymbol = function;
        _returnType = function.ReturnType;

        PushScope();
        foreach (var parameter in function.Parameters) Declare(parameter.Name, parameter);

        var value = BindExpression(expression, _returnType);
        if (!_returnType.Equals(Core.Void)) value = Convert(value, _returnType, expression);

        PopScope();
        _containingSymbol = savedSymbol;
        _returnType = savedReturn;

        return new BoundBlock(expression, [], [new BoundReturn(expression, value)]);
    }

    /// <summary>Binds a standalone statement, for tests and scripts.</summary>
    public BoundStatement BindStatementInNewScope(StatementSyntax statement)
    {
        PushScope();
        var bound = BindStatement(statement);
        PopScope();
        return bound;
    }

    /// <summary>Binds a standalone expression.</summary>
    public BoundExpression BindExpressionInNewScope(ExpressionSyntax expression)
    {
        PushScope();
        var bound = BindExpression(expression);
        PopScope();
        return bound;
    }

    // -----------------------------------------------------------------------
    // Scopes
    // -----------------------------------------------------------------------

    private void PushScope() => _locals.Add(new Dictionary<string, Symbol>(StringComparer.Ordinal));
    private void PopScope() => _locals.RemoveAt(_locals.Count - 1);

    private void Declare(string name, Symbol symbol)
    {
        if (name.Length == 0 || _locals.Count == 0) return;

        var scope = _locals[^1];
        if (scope.ContainsKey(name))
        {
            _diagnostics.Error(ErrorCode.ERR_DuplicateDeclaration,
                symbol.Location ?? default,
                $"'{name}' is already declared in this scope");
            return;
        }

        scope[name] = symbol;
    }

    private Symbol? LookupLocal(string name)
    {
        for (var i = _locals.Count - 1; i >= 0; i--)
            if (_locals[i].TryGetValue(name, out var symbol)) return symbol;
        return null;
    }

    private NamedTypeSymbol? EnclosingType()
    {
        for (var symbol = _containingSymbol; symbol is not null; symbol = symbol.ContainingSymbol)
            if (symbol is NamedTypeSymbol type) return type;
        return null;
    }

    // -----------------------------------------------------------------------
    // Statements
    // -----------------------------------------------------------------------

    public BoundStatement BindStatement(StatementSyntax statement) => statement switch
    {
        BlockSyntax block => BindBlock(block),
        LocalDeclarationStatementSyntax declaration => BindLocalDeclaration(declaration),
        ExpressionStatementSyntax expression => Record(expression,
            new BoundExpressionStatement(expression, BindExpression(expression.Expression))),
        IfStatementSyntax ifStatement => BindIf(ifStatement),
        WhileStatementSyntax whileStatement => BindWhile(whileStatement),
        RepeatStatementSyntax repeat => BindRepeat(repeat),
        ForStatementSyntax forStatement => BindForEach(forStatement),
        ReturnStatementSyntax returnStatement => BindReturn(returnStatement),
        ThrowStatementSyntax throwStatement => Record(throwStatement,
            new BoundThrow(throwStatement, BindExpression(throwStatement.Expression))),
        BreakStatementSyntax breakStatement => BindBreak(breakStatement),
        ContinueStatementSyntax continueStatement => BindContinue(continueStatement),
        DeferStatementSyntax defer => Record(defer,
            new BoundDefer(defer, BindStatement(defer.Body))),
        GuardStatementSyntax guard => BindGuard(guard),
        LabeledStatementSyntax labeled => BindStatement(labeled.Statement),
        EmptyStatementSyntax empty => new BoundBlock(empty, [], []),
        _ => new BoundErrorStatement(statement),
    };

    private BoundBlock BindBlock(BlockSyntax block)
    {
        PushScope();

        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
        foreach (var statement in block.Statements) statements.Add(BindStatement(statement));

        var locals = _locals[^1].Values.OfType<LocalSymbol>().ToImmutableArray();
        PopScope();

        return Record(block, new BoundBlock(block, locals, statements.ToImmutable()));
    }

    private BoundStatement BindLocalDeclaration(LocalDeclarationStatementSyntax declaration)
    {
        var declarator = declaration.Declarator;

        if (declarator.Pattern is not BindingPatternSyntax binding)
        {
            // Destructuring needs pattern binding, which comes with match.
            var value = declarator.Initializer is { } init
                ? BindExpression(init.Value)
                : null;
            return new BoundExpressionStatement(declaration, value ?? new BoundError(declaration));
        }

        var isMutable = declaration.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword;

        TypeSymbol? declaredType = declarator.TypeAnnotation is { } annotation
            ? _compilation.TypeBinder.BindType(annotation.Type, _containingSymbol, _scope)
            : null;

        BoundExpression? initializer = null;
        if (declarator.Initializer is { } initSyntax)
        {
            initializer = BindExpression(initSyntax.Value, declaredType);
            if (declaredType is not null)
                initializer = Convert(initializer, declaredType, initSyntax.Value);
        }

        var type = declaredType
            ?? initializer?.Type
            ?? ErrorTypeSymbol.Instance;

        if (declaredType is null && initializer is null)
        {
            _diagnostics.Error(ErrorCode.ERR_CannotInferType, binding.Identifier.Span,
                $"'{binding.Identifier.Text}' needs either a type annotation or an initialiser");
        }

        var local = new LocalSymbol(
            binding.Identifier.Text, _containingSymbol, declaration, isMutable)
        {
            TypeResolver = () => type,
        };

        Declare(local.Name, local);
        return Record(declaration, new BoundLocalDeclaration(declaration, local, initializer));
    }

    private BoundStatement BindIf(IfStatementSyntax statement)
    {
        PushScope();
        var condition = BindConditionList(statement.Conditions, statement);
        var consequence = BindStatement(statement.Body);
        PopScope();

        var alternative = statement.ElseClause is { } elseClause
            ? BindStatement(elseClause.Body)
            : null;

        return Record(statement, new BoundIf(statement, condition, consequence, alternative));
    }

    private BoundStatement BindGuard(GuardStatementSyntax statement)
    {
        // 'guard c else { ... }' is an 'if' whose bindings outlive it; the
        // else branch must not fall through, which the flow pass will check.
        var condition = BindConditionList(statement.Conditions, statement);
        var body = BindStatement(statement.Body);
        return Record(statement, new BoundIf(statement, condition, body, null));
    }

    private BoundExpression BindConditionList(
        SeparatedSyntaxList<ConditionSyntax> conditions, SyntaxNode syntax)
    {
        BoundExpression? result = null;

        foreach (var condition in conditions)
        {
            var bound = BindCondition(condition);
            result = result is null
                ? bound
                : new BoundBinaryOperator(
                    syntax, Core.Bool, BinaryOperatorKind.LogicalAnd, result, bound);
        }

        return result ?? new BoundLiteral(syntax, Core.Bool, true);
    }

    private BoundExpression BindCondition(ConditionSyntax condition)
    {
        switch (condition)
        {
            case ExpressionConditionSyntax expression:
            {
                var bound = BindExpression(expression.Expression, Core.Bool);
                return Convert(bound, Core.Bool, expression.Expression);
            }

            case OptionalBindingConditionSyntax binding:
            {
                // 'if let x = e' binds x to the unwrapped value.
                var declarator = binding.Declarator;
                var name = declarator.Pattern is BindingPatternSyntax pattern
                    ? pattern.Identifier.Text
                    : "_";

                BoundExpression? source = declarator.Initializer is { } init
                    ? BindExpression(init.Value)
                    : BindNameAsValue(name, binding);

                var unwrapped = source.Type is OptionalTypeSymbol optional
                    ? optional.UnderlyingType
                    : source.Type;

                if (source.Type is not OptionalTypeSymbol && !source.Type.IsError)
                {
                    _diagnostics.Error(ErrorCode.ERR_NotOptional, binding.Span,
                        $"'{source.Type.ToDisplayString()}' is not optional, "
                        + "so there is nothing to unwrap");
                }

                var local = new LocalSymbol(
                    name, _containingSymbol, binding,
                    binding.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword)
                {
                    TypeResolver = () => unwrapped,
                };

                Declare(name, local);
                return new BoundTypeTest(binding, Core.Bool, source, unwrapped);
            }

            case CasePatternConditionSyntax casePattern:
            {
                var subject = BindExpression(casePattern.Expression);
                BindPattern(casePattern.Pattern, subject.Type);
                return new BoundTypeTest(casePattern, Core.Bool, subject, subject.Type);
            }

            default:
                return new BoundLiteral(condition, Core.Bool, true);
        }
    }

    private BoundStatement BindWhile(WhileStatementSyntax statement)
    {
        PushScope();
        var condition = BindConditionList(statement.Conditions, statement);

        _loopDepth++;
        var body = BindStatement(statement.Body);
        _loopDepth--;

        PopScope();
        return Record(statement, new BoundWhile(statement, condition, body));
    }

    private BoundStatement BindRepeat(RepeatStatementSyntax statement)
    {
        _loopDepth++;
        var body = BindStatement(statement.Body);
        _loopDepth--;

        var condition = Convert(
            BindExpression(statement.Condition, Core.Bool), Core.Bool, statement.Condition);

        return Record(statement, new BoundRepeat(statement, body, condition));
    }

    private BoundStatement BindForEach(ForStatementSyntax statement)
    {
        var sequence = BindExpression(statement.Sequence);

        PushScope();

        LocalSymbol? element = null;
        if (statement.Pattern is BindingPatternSyntax binding)
        {
            var elementType = ElementTypeOf(sequence.Type, statement.Sequence);
            element = new LocalSymbol(binding.Identifier.Text, _containingSymbol, binding, false)
            {
                TypeResolver = () => elementType,
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
        return Record(statement,
            new BoundForEach(statement, element, sequence, filter, body));
    }

    /// <summary>What a <c>for … in</c> yields, for the sequences understood so far.</summary>
    private TypeSymbol ElementTypeOf(TypeSymbol sequence, SyntaxNode syntax)
    {
        if (sequence.IsError) return ErrorTypeSymbol.Instance;
        if (sequence is ArrayTypeSymbol array) return array.ElementType;

        // Ranges and user sequences need the Iterable protocol, which arrives
        // with the standard library.
        if (Core.IsInteger(sequence)) return sequence;

        _diagnostics.Error(ErrorCode.ERR_NotIterable, syntax.Span,
            $"'{sequence.ToDisplayString()}' cannot be iterated");
        return ErrorTypeSymbol.Instance;
    }

    private BoundStatement BindReturn(ReturnStatementSyntax statement)
    {
        if (statement.Expression is null)
        {
            if (!_returnType.Equals(Core.Void) && !_returnType.IsError)
            {
                _diagnostics.Error(ErrorCode.ERR_ReturnValueRequired, statement.Span,
                    $"this function returns '{_returnType.ToDisplayString()}', "
                    + "so 'return' needs a value");
            }
            return Record(statement, new BoundReturn(statement, null));
        }

        var value = BindExpression(statement.Expression, _returnType);

        if (_returnType.Equals(Core.Void))
        {
            _diagnostics.Error(ErrorCode.ERR_ReturnValueNotAllowed, statement.Expression.Span,
                "this function returns nothing, so 'return' takes no value");
        }
        else
        {
            value = Convert(value, _returnType, statement.Expression);
        }

        return Record(statement, new BoundReturn(statement, value));
    }

    private BoundStatement BindBreak(BreakStatementSyntax statement)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Error(ErrorCode.ERR_NoEnclosingLoop, statement.Span,
                "'break' is only allowed inside a loop");
        }

        return Record(statement, new BoundBreak(
            statement, statement.Label.IsNone ? null : statement.Label.Text));
    }

    private BoundStatement BindContinue(ContinueStatementSyntax statement)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Error(ErrorCode.ERR_NoEnclosingLoop, statement.Span,
                "'continue' is only allowed inside a loop");
        }

        return Record(statement, new BoundContinue(
            statement, statement.Label.IsNone ? null : statement.Label.Text));
    }

    // -----------------------------------------------------------------------
    // Expressions
    // -----------------------------------------------------------------------

    public BoundExpression BindExpression(
        ExpressionSyntax expression, TypeSymbol? expectedType = null)
    {
        var bound = BindExpressionCore(expression, expectedType);
        _bound[expression] = bound;
        return bound;
    }

    private BoundExpression BindExpressionCore(
        ExpressionSyntax expression, TypeSymbol? expectedType) => expression switch
    {
        LiteralExpressionSyntax literal => BindLiteral(literal, expectedType),
        InterpolatedStringExpressionSyntax interpolated => BindInterpolatedString(interpolated),
        IdentifierNameSyntax name => BindName(name, name.Identifier.Text),
        GenericNameSyntax name => BindName(name, name.Identifier.Text),
        SelfExpressionSyntax self => BindSelf(self),
        ParenthesizedExpressionSyntax parenthesized =>
            BindExpression(parenthesized.Expression, expectedType),
        MemberAccessExpressionSyntax member => BindMemberAccess(member),
        ImplicitMemberAccessExpressionSyntax implicitMember =>
            BindImplicitMemberAccess(implicitMember, expectedType),
        InvocationExpressionSyntax invocation => BindInvocation(invocation),
        ElementAccessExpressionSyntax element => BindElementAccess(element),
        BinaryExpressionSyntax binary => BindBinary(binary),
        PrefixUnaryExpressionSyntax unary => BindPrefixUnary(unary),
        PostfixUnaryExpressionSyntax unary => BindPostfixUnary(unary),
        AssignmentExpressionSyntax assignment => BindAssignment(assignment),
        ConditionalExpressionSyntax conditional => BindConditionalExpression(conditional),
        TupleExpressionSyntax tuple => BindTuple(tuple),
        ArrayExpressionSyntax array => BindArray(array, expectedType),
        CastExpressionSyntax cast => BindCast(cast),
        TypeTestExpressionSyntax test => BindTypeTest(test),
        MatchExpressionSyntax match => BindMatch(match, expectedType),
        TryExpressionSyntax tryExpression => BindExpression(tryExpression.Expression, expectedType),
        _ => Unsupported(expression),
    };

    private BoundExpression Unsupported(ExpressionSyntax expression)
    {
        _diagnostics.Error(ErrorCode.ERR_NotSupportedYet, expression.Span,
            $"'{expression.Kind}' is not bound yet");
        return new BoundError(expression);
    }

    private BoundExpression BindLiteral(
        LiteralExpressionSyntax literal, TypeSymbol? expectedType)
    {
        var token = literal.Token;

        TypeSymbol type = token.Kind switch
        {
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
            && token.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken)
        {
            var target = expectedType is OptionalTypeSymbol optional
                ? optional.UnderlyingType
                : expectedType;

            if (_conversions.Classify(type, target, sourceIsLiteral: true)
                is ConversionKind.LiteralTyping)
            {
                type = target;
            }
        }

        return new BoundLiteral(literal, type, token.Value);
    }

    private BoundExpression BindInterpolatedString(InterpolatedStringExpressionSyntax syntax)
    {
        var parts = ImmutableArray.CreateBuilder<BoundExpression>();

        foreach (var content in syntax.Contents)
        {
            if (content is InterpolationSyntax hole) parts.Add(BindExpression(hole.Expression));
        }

        return new BoundInterpolatedString(syntax, Core.String, parts.ToImmutable());
    }

    private BoundExpression BindSelf(SelfExpressionSyntax syntax)
    {
        if (EnclosingType() is { } type) return new BoundSelfAccess(syntax, type);

        _diagnostics.Error(ErrorCode.ERR_SelfOutsideType, syntax.Span,
            "'self' is only available inside a type");
        return new BoundError(syntax);
    }

    private BoundExpression BindName(ExpressionSyntax syntax, string name) =>
        BindNameAsValue(name, syntax);

    private BoundExpression BindNameAsValue(string name, SyntaxNode syntax)
    {
        if (LookupLocal(name) is { } local)
        {
            return local switch
            {
                LocalSymbol localSymbol => new BoundLocalAccess(syntax, localSymbol),
                ParameterSymbol parameter => new BoundParameterAccess(syntax, parameter),
                _ => new BoundError(syntax),
            };
        }

        // Members of the enclosing type, reached implicitly through self.
        if (EnclosingType() is { } enclosing)
        {
            var members = enclosing.GetMembers(name).ToList();
            if (members.Count > 0) return BindMemberGroup(syntax, null, members, enclosing);
        }

        var moduleMembers = _scope.Module.GetMembers(name).ToList();
        if (moduleMembers.Count > 0) return BindMemberGroup(syntax, null, moduleMembers, null);

        if (_scope.ImportedNames.TryGetValue(name, out var imported))
            return BindMemberGroup(syntax, null, [imported], null);

        foreach (var module in _scope.Imports)
        {
            var candidates = module.GetMembers(name)
                .Where(m => m.DeclaredAccessibility == Accessibility.Public).ToList();
            if (candidates.Count > 0) return BindMemberGroup(syntax, null, candidates, null);
        }

        var coreMembers = Core.Module.GetMembers(name).ToList();
        if (coreMembers.Count > 0) return BindMemberGroup(syntax, null, coreMembers, null);

        _diagnostics.Error(ErrorCode.ERR_NameNotFound, syntax.Span,
            $"no symbol named '{name}' is in scope");
        return new BoundError(syntax);
    }

    private BoundExpression BindMemberGroup(
        SyntaxNode syntax,
        BoundExpression? receiver,
        IReadOnlyList<Symbol> members,
        NamedTypeSymbol? containingType)
    {
        var functions = members.OfType<FunctionSymbol>().ToImmutableArray();
        if (!functions.IsEmpty)
            return new BoundFunctionGroup(syntax, receiver, functions);

        var member = members[0];
        switch (member)
        {
            case FieldSymbol field:
                if (receiver is null && !field.IsStatic && containingType is not null)
                    receiver = new BoundSelfAccess(syntax, containingType);
                return new BoundFieldAccess(syntax, receiver, field);

            case NamedTypeSymbol type:
                return new BoundTypeExpression(syntax, type);

            case AliasSymbol alias:
                return new BoundTypeExpression(syntax, alias.AliasedType);

            case UnionCaseSymbol unionCase:
                return new BoundUnionCaseGroup(
                    syntax, (NamedTypeSymbol)unionCase.ContainingSymbol!, unionCase);

            case EnumCaseSymbol enumCase:
                return new BoundLiteral(
                    syntax, (NamedTypeSymbol)enumCase.ContainingSymbol!, enumCase.Name);

            default:
                _diagnostics.Error(ErrorCode.ERR_NotAValue, syntax.Span,
                    $"'{member.Name}' cannot be used as a value");
                return new BoundError(syntax);
        }
    }

    private BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax)
    {
        var receiver = BindExpression(syntax.Expression);
        var name = syntax.Name switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => "",
        };

        if (receiver.HasError) return new BoundError(syntax, receiver);

        // 'T.self' names the type itself.
        if (receiver is BoundTypeExpression typeExpression)
        {
            if (name == "self") return new BoundTypeExpression(syntax, typeExpression.Type);

            if (typeExpression.ReferencedType is NamedTypeSymbol namedType)
            {
                var staticMembers = namedType.GetMembers(name).ToList();
                if (staticMembers.Count > 0)
                    return BindMemberGroup(syntax, null, staticMembers, namedType);
            }

            _diagnostics.Error(ErrorCode.ERR_NameNotFound, syntax.Name.Span,
                $"'{typeExpression.ReferencedType.ToDisplayString()}' has no member named '{name}'");
            return new BoundError(syntax, receiver);
        }

        var receiverType = receiver.Type is OptionalTypeSymbol optional
            ? optional.UnderlyingType
            : receiver.Type;

        if (receiverType is not NamedTypeSymbol owner)
        {
            _diagnostics.Error(ErrorCode.ERR_NameNotFound, syntax.Name.Span,
                $"'{receiverType.ToDisplayString()}' has no member named '{name}'");
            return new BoundError(syntax, receiver);
        }

        var members = owner.GetMembers(name).ToList();
        if (members.Count == 0)
        {
            _diagnostics.Error(ErrorCode.ERR_NameNotFound, syntax.Name.Span,
                $"'{owner.ToDisplayString()}' has no member named '{name}'");
            return new BoundError(syntax, receiver);
        }

        return BindMemberGroup(syntax, receiver, members, owner);
    }

    private BoundExpression BindImplicitMemberAccess(
        ImplicitMemberAccessExpressionSyntax syntax, TypeSymbol? expectedType)
    {
        var name = syntax.Name switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => "",
        };

        if (expectedType is NamedTypeSymbol named)
        {
            var members = named.GetMembers(name).ToList();
            if (members.Count > 0) return BindMemberGroup(syntax, null, members, named);
        }

        _diagnostics.Error(ErrorCode.ERR_NoContextualType, syntax.Span,
            $"cannot infer what '.{name}' refers to here");
        return new BoundError(syntax);
    }

    private BoundExpression BindInvocation(InvocationExpressionSyntax syntax)
    {
        var arguments = syntax.ArgumentList is { } list
            ? list.Arguments.Select(a => BindExpression(a.Expression)).ToImmutableArray()
            : [];

        var labels = syntax.ArgumentList is { } labelled
            ? labelled.Arguments
                .Select(a => a.NameToken.IsNone ? null : a.NameToken.Text)
                .ToImmutableArray()
            : ImmutableArray<string?>.Empty;

        var callee = BindCallee(syntax.Expression, arguments);

        switch (callee)
        {
            case BoundFunctionGroup group:
                return BindCallToGroup(syntax, group, arguments, labels);

            case BoundUnionCaseGroup unionCase:
                return new BoundUnionCaseConstruction(
                    syntax, unionCase.Type, unionCase.UnionCase, arguments);

            case BoundTypeExpression typeExpression
                when typeExpression.ReferencedType is NamedTypeSymbol namedType:
            {
                var initializers = namedType.GetMembers("init")
                    .OfType<FunctionSymbol>().ToImmutableArray();

                if (initializers.IsEmpty)
                {
                    // Every struct has a memberwise initialiser; synthesising
                    // it needs field ordering, which belongs with lowering.
                    return new BoundTypeExpression(syntax, namedType);
                }

                var group = new BoundFunctionGroup(syntax, null, initializers);
                return BindCallToGroup(syntax, group, arguments, labels);
            }

            default:
                if (!callee.HasError)
                {
                    _diagnostics.Error(ErrorCode.ERR_NotCallable, syntax.Expression.Span,
                        $"'{callee.Type.ToDisplayString()}' is not callable");
                }
                return new BoundError(syntax, [callee, .. arguments]);
        }
    }

    private BoundExpression BindCallee(
        ExpressionSyntax syntax, ImmutableArray<BoundExpression> arguments)
    {
        _ = arguments;
        return BindExpression(syntax);
    }

    private BoundExpression BindCallToGroup(
        InvocationExpressionSyntax syntax,
        BoundFunctionGroup group,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels)
    {
        var resolved = ResolveOverload(group.Candidates, arguments, labels, syntax);

        if (resolved is null)
        {
            return new BoundError(syntax, arguments.ToArray());
        }

        var converted = ImmutableArray.CreateBuilder<BoundExpression>(arguments.Length);
        for (var i = 0; i < arguments.Length; i++)
        {
            var parameter = i < resolved.Parameters.Length ? resolved.Parameters[i] : null;
            converted.Add(parameter is null
                ? arguments[i]
                : Convert(arguments[i], parameter.Type, arguments[i].Syntax));
        }

        return new BoundCall(syntax, group.Receiver, resolved, converted.ToImmutable());
    }

    /// <summary>
    /// Picks the overload that fits, preferring the one needing the fewest
    /// conversions.
    /// </summary>
    private FunctionSymbol? ResolveOverload(
        ImmutableArray<FunctionSymbol> candidates,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        SyntaxNode syntax)
    {
        FunctionSymbol? best = null;
        var bestCost = int.MaxValue;
        var ambiguous = false;

        foreach (var candidate in candidates)
        {
            var cost = Score(candidate, arguments, labels);
            if (cost is null) continue;

            if (cost < bestCost) { best = candidate; bestCost = cost.Value; ambiguous = false; }
            else if (cost == bestCost && best is not null) ambiguous = true;
        }

        if (best is null)
        {
            var name = candidates.IsEmpty ? "?" : candidates[0].Name;
            _diagnostics.Error(ErrorCode.ERR_NoMatchingOverload, syntax.Span,
                candidates.Length == 1
                    ? $"'{name}' cannot be called with these arguments"
                    : $"no overload of '{name}' takes these arguments");
            return null;
        }

        if (ambiguous)
        {
            _diagnostics.Error(ErrorCode.ERR_AmbiguousOverload, syntax.Span,
                $"the call to '{best.Name}' is ambiguous between several overloads");
        }

        return best;
    }

    private int? Score(
        FunctionSymbol candidate,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels)
    {
        var parameters = candidate.Parameters;

        var required = parameters.Count(p => !p.HasDefaultValue && !p.IsVariadic);
        var variadic = parameters.Any(p => p.IsVariadic);

        if (arguments.Length < required) return null;
        if (!variadic && arguments.Length > parameters.Length) return null;

        var cost = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            if (i >= parameters.Length) break;
            var parameter = parameters[i];

            // A written label must match the parameter's.
            if (i < labels.Length && labels[i] is { } label)
            {
                var expected = parameter.Label ?? parameter.Name;
                if (!string.Equals(expected, label, StringComparison.Ordinal)) return null;
            }
            else if (i < labels.Length
                     && parameter.Label is { } requiredLabel
                     && requiredLabel != "_"
                     && !string.Equals(requiredLabel, parameter.Name, StringComparison.Ordinal))
            {
                // The parameter declares a distinct external label, so one is
                // required at the call site.
                return null;
            }

            var conversion = _conversions.Classify(
                arguments[i].Type, parameter.Type, IsLiteral(arguments[i]));

            if (conversion is null) return null;
            cost += Conversions.Cost(conversion.Value);
        }

        return cost;
    }

    private static bool IsLiteral(BoundExpression expression) =>
        expression is BoundLiteral;

    private BoundExpression BindElementAccess(ElementAccessExpressionSyntax syntax)
    {
        var receiver = BindExpression(syntax.Expression);
        var indices = syntax.ArgumentList.Arguments
            .Select(a => BindExpression(a.Expression)).ToImmutableArray();

        if (receiver.HasError) return new BoundError(syntax, [receiver, .. indices]);

        if (receiver.Type is ArrayTypeSymbol array)
            return new BoundIndexAccess(syntax, array.ElementType, receiver, indices);

        if (receiver.Type is NamedTypeSymbol named
            && named.OriginalDefinition.Equals(Core.Dictionary)
            && named.TypeArguments.Length == 2)
        {
            return new BoundIndexAccess(
                syntax, new OptionalTypeSymbol(named.TypeArguments[1]), receiver, indices);
        }

        _diagnostics.Error(ErrorCode.ERR_NotIndexable, syntax.Span,
            $"'{receiver.Type.ToDisplayString()}' cannot be indexed");
        return new BoundError(syntax, [receiver, .. indices]);
    }

    private BoundExpression BindTuple(TupleExpressionSyntax syntax)
    {
        var elements = syntax.Elements
            .Select(e => BindExpression(e.Expression)).ToImmutableArray();

        var names = syntax.Elements
            .Select(e => e.NameToken.IsNone ? null : e.NameToken.Text).ToImmutableArray();

        var type = new TupleTypeSymbol([.. elements.Select(e => e.Type)], names);
        return new BoundTuple(syntax, type, elements);
    }

    private BoundExpression BindArray(ArrayExpressionSyntax syntax, TypeSymbol? expectedType)
    {
        var expectedElement = expectedType is ArrayTypeSymbol expectedArray
            ? expectedArray.ElementType
            : null;

        var elements = syntax.Elements
            .Select(e => BindExpression(e, expectedElement)).ToImmutableArray();

        TypeSymbol elementType;
        if (expectedElement is not null) elementType = expectedElement;
        else if (elements.IsEmpty) elementType = ErrorTypeSymbol.Instance;
        else
        {
            elementType = elements[0].Type;
            foreach (var element in elements)
            {
                if (element.Type.Equals(elementType)) continue;
                if (_conversions.HasImplicitConversion(
                        element.Type, elementType, IsLiteral(element)))
                {
                    continue;
                }

                _diagnostics.Error(ErrorCode.ERR_InconsistentArrayElements, element.Syntax.Span,
                    $"this element is '{element.Type.ToDisplayString()}' but the array holds "
                    + $"'{elementType.ToDisplayString()}'");
                elementType = ErrorTypeSymbol.Instance;
                break;
            }
        }

        if (elements.IsEmpty && expectedElement is null)
        {
            _diagnostics.Error(ErrorCode.ERR_CannotInferType, syntax.Span,
                "an empty array needs a type annotation");
        }

        return new BoundArray(
            syntax, new ArrayTypeSymbol(elementType, ArrayKind.Fixed), elements);
    }

    private BoundExpression BindCast(CastExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Expression);
        var target = _compilation.TypeBinder.BindType(syntax.Type, _containingSymbol, _scope);

        // 'as?' yields an optional; 'as' and 'as!' yield the type itself.
        var isOptional = syntax.QuestionOrBangToken.Kind == SyntaxKind.QuestionToken;
        var type = isOptional ? new OptionalTypeSymbol(target) : target;

        return new BoundConversion(syntax, type, operand, ConversionKind.Explicit);
    }

    private BoundExpression BindTypeTest(TypeTestExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Expression);
        var target = _compilation.TypeBinder.BindType(syntax.Type, _containingSymbol, _scope);
        return new BoundTypeTest(syntax, Core.Bool, operand, target);
    }

    private BoundExpression BindAssignment(AssignmentExpressionSyntax syntax)
    {
        var target = BindExpression(syntax.Left);
        var value = BindExpression(syntax.Right, target.Type);

        if (!target.HasError && !target.IsAssignable)
        {
            _diagnostics.Error(ErrorCode.ERR_NotAssignable, syntax.Left.Span,
                "the left side of an assignment must be a mutable binding");
        }

        if (syntax.OperatorToken.Kind != SyntaxKind.EqualsToken)
        {
            // A compound assignment is the binary operator then the store.
            var kind = CompoundOperatorKind(syntax.OperatorToken.Kind);
            if (kind is not null)
            {
                var combined = BindBinaryOperator(
                    syntax, kind.Value, target, value, syntax.OperatorToken);
                return new BoundAssignment(syntax, target, combined);
            }
        }

        return new BoundAssignment(syntax, target, Convert(value, target.Type, syntax.Right));
    }

    private static BinaryOperatorKind? CompoundOperatorKind(SyntaxKind kind) => kind switch
    {
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

    private BoundExpression BindConditionalExpression(ConditionalExpressionSyntax syntax)
    {
        var condition = Convert(
            BindExpression(syntax.Condition, Core.Bool), Core.Bool, syntax.Condition);

        var whenTrue = BindExpression(syntax.WhenTrue);
        var whenFalse = BindExpression(syntax.WhenFalse, whenTrue.Type);

        var type = whenTrue.Type;
        if (!whenFalse.Type.Equals(type))
        {
            if (_conversions.HasImplicitConversion(whenFalse.Type, type, IsLiteral(whenFalse)))
            {
                whenFalse = Convert(whenFalse, type, syntax.WhenFalse);
            }
            else if (_conversions.HasImplicitConversion(type, whenFalse.Type, IsLiteral(whenTrue)))
            {
                type = whenFalse.Type;
                whenTrue = Convert(whenTrue, type, syntax.WhenTrue);
            }
            else
            {
                _diagnostics.Error(ErrorCode.ERR_BranchTypesDiffer, syntax.Span,
                    $"the branches have different types, "
                    + $"'{type.ToDisplayString()}' and '{whenFalse.Type.ToDisplayString()}'");
                type = ErrorTypeSymbol.Instance;
            }
        }

        return new BoundConditional(syntax, type, condition, whenTrue, whenFalse);
    }

    private BoundExpression BindPrefixUnary(PrefixUnaryExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);

        var kind = syntax.OperatorToken.Kind switch
        {
            SyntaxKind.MinusToken => UnaryOperatorKind.Negate,
            SyntaxKind.PlusToken => UnaryOperatorKind.Plus,
            SyntaxKind.BangToken => UnaryOperatorKind.Not,
            SyntaxKind.TildeToken => UnaryOperatorKind.Complement,
            SyntaxKind.AmpersandToken => UnaryOperatorKind.AddressOf,
            SyntaxKind.AwaitKeyword => UnaryOperatorKind.Await,
            SyntaxKind.MoveKeyword => UnaryOperatorKind.Move,
            _ => UnaryOperatorKind.Plus,
        };

        if (operand.HasError) return new BoundError(syntax, operand);

        var type = kind switch
        {
            UnaryOperatorKind.Negate or UnaryOperatorKind.Plus =>
                Core.IsNumeric(operand.Type) ? operand.Type : null,
            UnaryOperatorKind.Not =>
                operand.Type.Equals(Core.Bool) ? Core.Bool : null,
            UnaryOperatorKind.Complement =>
                Core.IsInteger(operand.Type) ? operand.Type : null,
            UnaryOperatorKind.AddressOf => new ReferenceTypeSymbol(operand.Type),
            UnaryOperatorKind.Await => operand.Type,
            UnaryOperatorKind.Move => operand.Type,
            _ => operand.Type,
        };

        if (type is null)
        {
            _diagnostics.Error(ErrorCode.ERR_OperatorNotDefined, syntax.Span,
                $"'{syntax.OperatorToken.Text}' is not defined for "
                + $"'{operand.Type.ToDisplayString()}'");
            return new BoundError(syntax, operand);
        }

        return new BoundUnaryOperator(syntax, type, kind, operand);
    }

    private BoundExpression BindPostfixUnary(PostfixUnaryExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);

        // The only postfix operator is '!', which unwraps an optional.
        if (operand.Type is OptionalTypeSymbol optional)
            return new BoundConversion(
                syntax, optional.UnderlyingType, operand, ConversionKind.Explicit);

        if (!operand.HasError)
        {
            _diagnostics.Error(ErrorCode.ERR_NotOptional, syntax.Span,
                $"'{operand.Type.ToDisplayString()}' is not optional, "
                + "so there is nothing to unwrap");
        }

        return new BoundError(syntax, operand);
    }

    private BoundExpression BindBinary(BinaryExpressionSyntax syntax)
    {
        var left = BindExpression(syntax.Left);

        // The right side of '??' is typed by the left's unwrapped type.
        TypeSymbol? expected = syntax.OperatorToken.Kind == SyntaxKind.QuestionQuestionToken
            ? (left.Type as OptionalTypeSymbol)?.UnderlyingType
            : left.Type;

        var right = BindExpression(syntax.Right, expected);

        var kind = BinaryKindOf(syntax.OperatorToken.Kind);
        if (kind is null) return new BoundError(syntax, left, right);

        return BindBinaryOperator(syntax, kind.Value, left, right, syntax.OperatorToken);
    }

    private static BinaryOperatorKind? BinaryKindOf(SyntaxKind kind) => kind switch
    {
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

    private BoundExpression BindBinaryOperator(
        SyntaxNode syntax,
        BinaryOperatorKind kind,
        BoundExpression left,
        BoundExpression right,
        SyntaxToken operatorToken)
    {
        if (left.HasError || right.HasError) return new BoundError(syntax, left, right);

        if (kind == BinaryOperatorKind.Coalesce)
        {
            if (left.Type is not OptionalTypeSymbol optional)
            {
                _diagnostics.Error(ErrorCode.ERR_NotOptional, syntax.Span,
                    $"'??' needs an optional on the left, not "
                    + $"'{left.Type.ToDisplayString()}'");
                return new BoundError(syntax, left, right);
            }

            var result = Convert(right, optional.UnderlyingType, right.Syntax);
            return new BoundBinaryOperator(
                syntax, optional.UnderlyingType, kind, left, result);
        }

        // A numeric literal on one side takes the other side's type.
        if (IsLiteral(left) && !IsLiteral(right)
            && _conversions.Classify(left.Type, right.Type, true) is ConversionKind.LiteralTyping)
        {
            left = Convert(left, right.Type, left.Syntax);
        }
        else if (IsLiteral(right) && !IsLiteral(left)
                 && _conversions.Classify(right.Type, left.Type, true)
                     is ConversionKind.LiteralTyping)
        {
            right = Convert(right, left.Type, right.Syntax);
        }

        var type = ResultTypeOf(kind, left.Type, right.Type);
        if (type is not null) return new BoundBinaryOperator(syntax, type, kind, left, right);

        if (FindUserOperator(operatorToken.Text, left.Type, right.Type) is { } method)
        {
            return new BoundBinaryOperator(
                syntax, method.ReturnType, BinaryOperatorKind.UserDefined, left, right, method);
        }

        _diagnostics.Error(ErrorCode.ERR_OperatorNotDefined, syntax.Span,
            $"'{operatorToken.Text}' is not defined for "
            + $"'{left.Type.ToDisplayString()}' and '{right.Type.ToDisplayString()}'");
        return new BoundError(syntax, left, right);
    }

    private TypeSymbol? ResultTypeOf(
        BinaryOperatorKind kind, TypeSymbol left, TypeSymbol right) => kind switch
    {
        BinaryOperatorKind.Add when left.Equals(Core.String) && right.Equals(Core.String) =>
            Core.String,

        BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply
            or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder =>
            left.Equals(right) && Core.IsNumeric(left) ? left : null,

        BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr =>
            left.Equals(right) && Core.IsInteger(left) ? left
            : left.Equals(Core.Bool) && right.Equals(Core.Bool) ? Core.Bool
            : null,

        BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift =>
            Core.IsInteger(left) && Core.IsInteger(right) ? left : null,

        BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
            or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual =>
            left.Equals(right) && (Core.IsNumeric(left) || left.Equals(Core.String))
                ? Core.Bool
                : null,

        BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual =>
            left.Equals(right) ? Core.Bool : null,

        BinaryOperatorKind.ReferenceEqual or BinaryOperatorKind.ReferenceNotEqual =>
            left.IsReferenceType && right.IsReferenceType ? Core.Bool : null,

        BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr =>
            left.Equals(Core.Bool) && right.Equals(Core.Bool) ? Core.Bool : null,

        _ => null,
    };

    /// <summary>Looks for an <c>operator</c> member on either operand's type.</summary>
    private FunctionSymbol? FindUserOperator(string symbol, TypeSymbol left, TypeSymbol right)
    {
        foreach (var owner in new[] { left, right })
        {
            if (owner is not NamedTypeSymbol named) continue;

            foreach (var member in named.Members)
            {
                if (member is not FunctionSymbol { FunctionKind: FunctionKind.Operator } function)
                    continue;
                if (!string.Equals(function.Name, symbol, StringComparison.Ordinal)) continue;
                if (function.Parameters.Length != 2) continue;

                if (_conversions.HasImplicitConversion(left, function.Parameters[0].Type)
                    && _conversions.HasImplicitConversion(right, function.Parameters[1].Type))
                {
                    return function;
                }
            }
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Conversions
    // -----------------------------------------------------------------------

    private BoundExpression Convert(
        BoundExpression expression, TypeSymbol target, SyntaxNode syntax)
    {
        if (expression.Type.Equals(target)) return expression;

        var conversion = _conversions.Classify(expression.Type, target, IsLiteral(expression));
        if (conversion is null)
        {
            if (!expression.HasError && !target.IsError)
            {
                _diagnostics.Error(ErrorCode.ERR_TypeMismatch, syntax.Span,
                    $"expected '{target.ToDisplayString()}' but found "
                    + $"'{expression.Type.ToDisplayString()}'");
            }
            return new BoundError(syntax, expression);
        }

        if (conversion == ConversionKind.Identity) return expression;

        var converted = new BoundConversion(syntax, target, expression, conversion.Value);

        // Record against the syntax, so asking the model for an expression's
        // type gives the type it actually has in this position.
        if (syntax == expression.Syntax) _bound[syntax] = converted;

        return converted;
    }

    // =======================================================================
    // Patterns
    // =======================================================================

    /// <summary>Binds a pattern against the type of the value it will match.</summary>
    /// <remarks>
    /// Binding a pattern declares its bindings in the current scope, so the
    /// caller pushes a scope for each arm.
    /// </remarks>
    public BoundPattern BindPattern(PatternSyntax pattern, TypeSymbol inputType)
    {
        var bound = BindPatternCore(pattern, inputType);
        _bound[pattern] = bound;
        return bound;
    }

    private BoundPattern BindPatternCore(PatternSyntax pattern, TypeSymbol inputType)
    {
        switch (pattern)
        {
            case WildcardPatternSyntax wildcard:
                return new BoundWildcardPattern(wildcard, inputType);

            case ParenthesizedPatternSyntax parenthesized:
                return BindPattern(parenthesized.Pattern, inputType);

            case BindingPatternSyntax binding:
            {
                var local = new LocalSymbol(
                    binding.Identifier.Text, _containingSymbol, binding,
                    binding.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword)
                {
                    TypeResolver = () => inputType,
                };

                Declare(local.Name, local);
                return new BoundBindingPattern(binding, inputType, local);
            }

            case LiteralPatternSyntax literal:
                return BindLiteralPattern(literal, inputType);

            case RangePatternSyntax range:
            {
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

            case TypeTestPatternSyntax test:
            {
                var tested = _compilation.TypeBinder.BindType(
                    test.Type, _containingSymbol, _scope);
                return new BoundTypeTestPattern(test, inputType, tested, null);
            }

            case BindingTypeTestPatternSyntax test:
            {
                var tested = _compilation.TypeBinder.BindType(
                    test.Type, _containingSymbol, _scope);

                var local = new LocalSymbol(
                    test.Identifier.Text, _containingSymbol, test,
                    test.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword)
                {
                    TypeResolver = () => tested,
                };

                Declare(local.Name, local);
                return new BoundTypeTestPattern(test, inputType, tested, local);
            }

            case OrPatternSyntax or:
            {
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

    private BoundPattern BindLiteralPattern(
        LiteralPatternSyntax pattern, TypeSymbol inputType)
    {
        var token = pattern.Token;

        TypeSymbol literalType = token.Kind switch
        {
            SyntaxKind.IntegerLiteralToken => Core.Int,
            SyntaxKind.FloatLiteralToken => Core.F64,
            SyntaxKind.CharLiteralToken => Core.Char,
            SyntaxKind.StringLiteralToken or SyntaxKind.MultilineStringLiteralToken
                or SyntaxKind.RawStringLiteralToken => Core.String,
            SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => Core.Bool,
            SyntaxKind.NoneKeyword => inputType,
            _ => ErrorTypeSymbol.Instance,
        };

        if (!inputType.IsError && !literalType.IsError
            && !_conversions.HasImplicitConversion(literalType, inputType, true))
        {
            _diagnostics.Error(ErrorCode.ERR_PatternTypeMismatch, pattern.Span,
                $"a '{literalType.ToDisplayString()}' pattern cannot match "
                + $"'{inputType.ToDisplayString()}'");
        }

        return new BoundLiteralPattern(
            pattern, inputType, token.Value, !pattern.MinusToken.IsNone);
    }

    private BoundPattern BindTuplePattern(TuplePatternSyntax pattern, TypeSymbol inputType)
    {
        var elementTypes = inputType is TupleTypeSymbol tuple
            ? tuple.ElementTypes
            : ImmutableArray<TypeSymbol>.Empty;

        if (inputType is not TupleTypeSymbol && !inputType.IsError)
        {
            _diagnostics.Error(ErrorCode.ERR_PatternTypeMismatch, pattern.Span,
                $"'{inputType.ToDisplayString()}' is not a tuple");
        }
        else if (!elementTypes.IsEmpty && elementTypes.Length != pattern.Elements.Count)
        {
            _diagnostics.Error(ErrorCode.ERR_PatternTypeMismatch, pattern.Span,
                $"this tuple has {elementTypes.Length} element(s), "
                + $"but the pattern has {pattern.Elements.Count}");
        }

        var elements = ImmutableArray.CreateBuilder<BoundPattern>(pattern.Elements.Count);
        for (var i = 0; i < pattern.Elements.Count; i++)
        {
            var elementType = i < elementTypes.Length
                ? elementTypes[i]
                : ErrorTypeSymbol.Instance;
            elements.Add(BindPattern(pattern.Elements[i], elementType));
        }

        return new BoundTuplePattern(pattern, inputType, elements.ToImmutable());
    }

    private BoundPattern BindUnionCasePattern(
        UnionCasePatternSyntax pattern, TypeSymbol inputType)
    {
        var name = pattern.Identifier.Text;

        // A qualifier names the union explicitly; otherwise the input type does.
        TypeSymbol owner = pattern.Qualifier is { } qualifier
            ? _compilation.TypeBinder.BindType(qualifier, _containingSymbol, _scope)
            : inputType;

        if (owner is not NamedTypeSymbol named)
        {
            if (!owner.IsError)
            {
                _diagnostics.Error(ErrorCode.ERR_PatternTypeMismatch, pattern.Span,
                    $"'{owner.ToDisplayString()}' has no cases to match");
            }
            return new BoundErrorPattern(pattern);
        }

        var member = named.GetMembers(name).FirstOrDefault();

        if (member is EnumCaseSymbol enumCase)
            return new BoundEnumCasePattern(pattern, inputType, enumCase);

        if (member is not UnionCaseSymbol unionCase)
        {
            _diagnostics.Error(ErrorCode.ERR_UnknownUnionCase, pattern.Identifier.Span,
                $"'{named.ToDisplayString()}' has no case named '{name}'");
            return new BoundErrorPattern(pattern);
        }

        var payload = ImmutableArray.CreateBuilder<BoundPattern>();
        if (pattern.ArgumentList is { } arguments)
        {
            if (arguments.Arguments.Count != unionCase.Payload.Length)
            {
                _diagnostics.Error(ErrorCode.ERR_WrongPayloadCount, pattern.Span,
                    $"'{name}' carries {unionCase.Payload.Length} value(s), "
                    + $"but the pattern names {arguments.Arguments.Count}");
            }

            for (var i = 0; i < arguments.Arguments.Count; i++)
            {
                var fieldType = i < unionCase.Payload.Length
                    ? unionCase.Payload[i].Type
                    : ErrorTypeSymbol.Instance;
                payload.Add(BindPattern(arguments.Arguments[i].Pattern, fieldType));
            }
        }
        else if (unionCase.HasPayload)
        {
            _diagnostics.Error(ErrorCode.ERR_WrongPayloadCount, pattern.Span,
                $"'{name}' carries {unionCase.Payload.Length} value(s), "
                + "but the pattern names none");
        }

        return new BoundUnionCasePattern(pattern, inputType, unionCase, payload.ToImmutable());
    }

    private BoundPattern BindRecordPattern(RecordPatternSyntax pattern, TypeSymbol inputType)
    {
        var owner = pattern.Type is { } typeSyntax
            ? _compilation.TypeBinder.BindType(typeSyntax, _containingSymbol, _scope)
            : inputType;

        var fields = ImmutableArray.CreateBuilder<(FieldSymbol, BoundPattern)>();

        if (owner is not NamedTypeSymbol named)
        {
            if (!owner.IsError)
            {
                _diagnostics.Error(ErrorCode.ERR_PatternTypeMismatch, pattern.Span,
                    $"'{owner.ToDisplayString()}' has no fields to match");
            }
            return new BoundErrorPattern(pattern);
        }

        foreach (var field in pattern.Fields)
        {
            var name = field.NameToken.Text;
            var member = named.GetMembers(name).OfType<FieldSymbol>().FirstOrDefault();

            if (member is null)
            {
                _diagnostics.Error(ErrorCode.ERR_NameNotFound, field.NameToken.Span,
                    $"'{named.ToDisplayString()}' has no field named '{name}'");
                continue;
            }

            if (field.Pattern is { } sub)
            {
                fields.Add((member, BindPattern(sub, member.Type)));
                continue;
            }

            // '{ name }' binds the field to a local of the same name.
            var local = new LocalSymbol(name, _containingSymbol, field, false)
            {
                TypeResolver = () => member.Type,
            };
            Declare(name, local);
            fields.Add((member, new BoundBindingPattern(field, member.Type, local)));
        }

        return new BoundRecordPattern(pattern, inputType, fields.ToImmutable());
    }

    private BoundPattern BindArrayPattern(ArrayPatternSyntax pattern, TypeSymbol inputType)
    {
        var elementType = inputType is ArrayTypeSymbol array
            ? array.ElementType
            : ErrorTypeSymbol.Instance;

        if (inputType is not ArrayTypeSymbol && !inputType.IsError)
        {
            _diagnostics.Error(ErrorCode.ERR_PatternTypeMismatch, pattern.Span,
                $"'{inputType.ToDisplayString()}' is not an array");
        }

        var elements = ImmutableArray.CreateBuilder<BoundPattern>(pattern.Elements.Count);
        var restIndex = -1;

        for (var i = 0; i < pattern.Elements.Count; i++)
        {
            var element = pattern.Elements[i];

            if (element is RestPatternSyntax rest)
            {
                restIndex = i;
                if (!rest.Identifier.IsNone)
                {
                    var restType = new ArrayTypeSymbol(elementType, ArrayKind.Slice);
                    var local = new LocalSymbol(
                        rest.Identifier.Text, _containingSymbol, rest, false)
                    {
                        TypeResolver = () => restType,
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

        return new BoundArrayPattern(
            pattern, inputType, elements.ToImmutable(), restIndex);
    }

    // =======================================================================
    // match
    // =======================================================================

    private BoundExpression BindMatch(MatchExpressionSyntax syntax, TypeSymbol? expectedType)
    {
        var subject = BindExpression(syntax.Expression);
        var arms = ImmutableArray.CreateBuilder<BoundMatchArm>();

        TypeSymbol? resultType = expectedType;
        var sawValue = false;

        foreach (var armSyntax in syntax.Arms)
        {
            PushScope();

            var patterns = ImmutableArray.CreateBuilder<BoundPattern>();
            BoundExpression? guard = null;
            BoundNode body;

            switch (armSyntax)
            {
                case MatchArmSyntax arm:
                {
                    foreach (var pattern in arm.Patterns)
                        patterns.Add(BindPattern(pattern, subject.Type));

                    if (arm.WhereClause is { } where)
                    {
                        guard = Convert(
                            BindExpression(where.Condition, Core.Bool),
                            Core.Bool, where.Condition);
                    }

                    if (arm.Body is BlockSyntax block)
                    {
                        body = BindBlock(block);
                    }
                    else if (arm.Body is ExpressionSyntax expression)
                    {
                        var value = BindExpression(expression, resultType);
                        resultType ??= value.Type;
                        sawValue = true;

                        if (!value.Type.Equals(resultType))
                        {
                            if (_conversions.HasImplicitConversion(
                                    value.Type, resultType, value is BoundLiteral))
                            {
                                value = Convert(value, resultType, expression);
                            }
                            else
                            {
                                _diagnostics.Error(ErrorCode.ERR_MatchArmTypesDiffer,
                                    expression.Span,
                                    $"this arm gives '{value.Type.ToDisplayString()}' but "
                                    + $"earlier arms gave '{resultType.ToDisplayString()}'");
                                resultType = ErrorTypeSymbol.Instance;
                            }
                        }

                        body = value;
                    }
                    else
                    {
                        body = new BoundError(arm);
                    }

                    arms.Add(new BoundMatchArm(
                        arm, patterns.ToImmutable(), guard, body));
                    break;
                }

                case MatchCaseBlockSyntax caseBlock:
                {
                    foreach (var pattern in caseBlock.Patterns)
                        patterns.Add(BindPattern(pattern, subject.Type));

                    if (caseBlock.WhereClause is { } where)
                    {
                        guard = Convert(
                            BindExpression(where.Condition, Core.Bool),
                            Core.Bool, where.Condition);
                    }

                    var statements = ImmutableArray.CreateBuilder<BoundStatement>();
                    foreach (var statement in caseBlock.Statements)
                        statements.Add(BindStatement(statement));

                    body = new BoundBlock(caseBlock, [], statements.ToImmutable());
                    arms.Add(new BoundMatchArm(
                        caseBlock, patterns.ToImmutable(), guard, body));
                    break;
                }
            }

            PopScope();
        }

        var type = sawValue ? resultType ?? Core.Void : Core.Void;
        var bound = new BoundMatch(syntax, type, subject, arms.ToImmutable());

        CheckExhaustive(bound);
        return bound;
    }

    /// <summary>
    /// Reports a match that does not cover its subject, naming a case it
    /// misses.
    /// </summary>
    /// <remarks>
    /// This covers unions, enums and booleans, which is where exhaustiveness
    /// actually bites. The general algorithm — Maranget's usefulness, with a
    /// witness built from the uncovered constructors — replaces it when
    /// nested patterns need checking too.
    /// </remarks>
    private void CheckExhaustive(BoundMatch match)
    {
        if (match.Subject.Type.IsError) return;

        foreach (var arm in match.Arms)
        {
            if (arm.Guard is not null) continue;
            if (arm.IsDefault) return;
            if (arm.Patterns.Any(p => p.IsIrrefutable)) return;
        }

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arm in match.Arms)
        {
            if (arm.Guard is not null) continue;
            foreach (var pattern in arm.Patterns) CollectCovered(pattern, covered);
        }

        var missing = MissingCases(match.Subject.Type, covered);
        if (missing is null || missing.Count == 0) return;

        var witness = missing.Count == 1
            ? $"'{missing[0]}'"
            : $"'{string.Join("', '", missing.Take(3))}'"
              + (missing.Count > 3 ? $" and {missing.Count - 3} more" : "");

        _diagnostics.Error(ErrorCode.ERR_MatchNotExhaustive, match.Syntax.Span,
            $"match is not exhaustive: {witness} not covered");
    }

    private static void CollectCovered(BoundPattern pattern, HashSet<string> covered)
    {
        switch (pattern)
        {
            case BoundUnionCasePattern unionCase:
                if (unionCase.Payload.All(p => p.IsIrrefutable))
                    covered.Add(unionCase.UnionCase.Name);
                return;

            case BoundEnumCasePattern enumCase:
                covered.Add(enumCase.EnumCase.Name);
                return;

            case BoundLiteralPattern literal when literal.Value is bool value:
                covered.Add(value ? "true" : "false");
                return;

            case BoundOrPattern or:
                CollectCovered(or.Left, covered);
                CollectCovered(or.Right, covered);
                return;
        }
    }

    private List<string>? MissingCases(TypeSymbol subject, HashSet<string> covered)
    {
        if (subject.Equals(Core.Bool))
        {
            var missing = new List<string>();
            if (!covered.Contains("true")) missing.Add("true");
            if (!covered.Contains("false")) missing.Add("false");
            return missing;
        }

        if (subject is not NamedTypeSymbol named) return null;

        if (named.TypeKind == TypeKind.Union)
        {
            return [.. named.Members.OfType<UnionCaseSymbol>()
                .Select(c => c.Name)
                .Where(name => !covered.Contains(name))];
        }

        if (named.TypeKind == TypeKind.Enum)
        {
            return [.. named.Members.OfType<EnumCaseSymbol>()
                .Select(c => c.Name)
                .Where(name => !covered.Contains(name))];
        }

        // Any other type has too many values to enumerate, so a match on one
        // needs a wildcard or a default, which the caller already checked for.
        return ["_"];
    }
}
