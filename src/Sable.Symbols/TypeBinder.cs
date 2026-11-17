using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
/// Resolves type syntax to type symbols, and binds the signatures of
/// declarations.
/// </summary>
/// <remarks>
/// <para>Signature binding is deferred: declaring a symbol records a closure,
/// and the work happens the first time someone asks for the type. Asking a
/// compilation for its diagnostics forces all of them.</para>
/// <para>Lookup order, from docs/03-type-system.md: type parameters and
/// members of the enclosing declarations, then the containing module, then
/// names imported individually, then imported modules, then the core module
/// that every file sees.</para>
/// </remarks>
internal sealed class TypeBinder(Compilation compilation)
{
    private readonly DiagnosticBag _diagnostics = new();
    private readonly List<Action> _forcers = [];
    private readonly List<Action> _late = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.ToList();

    private int _forced;
    private int _forcedLate;

    /// <summary>Binds every deferred signature, so diagnostics are complete.</summary>
    /// <remarks>
    /// Two queues, not one. Signatures come first, and the checks that compare
    /// whole types — conformance above all — come after every signature is
    /// bound, including the ones an extension declared somewhere else. Binding
    /// one signature can add more work, so both are drained by index, and the
    /// cursors persist so a second call does not repeat the diagnostics.
    /// </remarks>
    public void ForceAll()
    {
        while (_forced < _forcers.Count || _forcedLate < _late.Count)
        {
            while (_forced < _forcers.Count) _forcers[_forced++]();
            if (_forcedLate < _late.Count) _late[_forcedLate++]();
        }
    }

    internal void Defer(Action force) => _forcers.Add(force);

    /// <summary>Defers work that must see every signature, not just this one's.</summary>
    internal void DeferLate(Action force) => _late.Add(force);

    // -----------------------------------------------------------------------
    // Signature binding
    // -----------------------------------------------------------------------

    public void BindFunctionSignature(
        FunctionSymbol function, FunctionDeclarationSyntax declaration)
    {
        var scope = compilation.CurrentScope;

        function.ReturnTypeResolver = () => declaration.ReturnClause is { } clause
            ? BindType(clause.Type, function, scope)
            : compilation.Core.Void;

        function.ThrownTypeResolver = () =>
        {
            if (declaration.ThrowsSpec is not { } spec) return null;
            return spec.ErrorType is { } errorType
                ? BindType(errorType, function, scope)
                : compilation.Core.AnyError;   // bare 'throws' is 'throws(any Error)' 
        };

        Defer(() =>
        {
            // Constraints first: a signature may mention 'T.Associated',
            // which is only findable once T knows what it is constrained to.
            BindTypeParameterConstraints(function.TypeParameters, function, scope);
            BindWhereClause(declaration.WhereClause, function, scope);

            _ = function.ReturnType;
            _ = function.ThrownType;
            foreach (var parameter in function.Parameters) _ = parameter.Type;
        });
    }

    public void BindParameterType(ParameterSymbol parameter, ParameterSyntax syntax)
    {
        var scope = compilation.CurrentScope;

        parameter.TypeResolver = () =>
        {
            var written = BindType(syntax.Type, parameter.ContainingSymbol ?? parameter, scope);

            // 'values: Int...' is written as the element type and is a
            // sequence of them inside the body.
            return parameter.IsVariadic
                ? new ArrayTypeSymbol(written, ArrayKind.Slice)
                : written;
        };
    }

    public void BindUnionCaseFieldType(ParameterSymbol parameter, UnionCaseFieldSyntax syntax)
    {
        var scope = compilation.CurrentScope;
        parameter.TypeResolver = () =>
            BindType(syntax.Type, parameter.ContainingSymbol ?? parameter, scope);

        Defer(() => _ = parameter.Type);
    }

    public void BindFieldType(FieldSymbol field, FieldDeclarationSyntax declaration)
    {
        var scope = compilation.CurrentScope;

        field.TypeResolver = () => declaration.Declarator.TypeAnnotation is { } annotation
            ? BindType(annotation.Type, field, scope)
            // Without an annotation the type comes from the initialiser, which
            // needs expression binding; until then it is unresolved.
            : ErrorTypeSymbol.Instance;

        if (declaration.Declarator.TypeAnnotation is not null) Defer(() => _ = field.Type);
    }

    public void BindConstantType(FieldSymbol field, ConstDeclarationSyntax declaration)
    {
        var scope = compilation.CurrentScope;

        field.TypeResolver = () => declaration.TypeAnnotation is { } annotation
            ? BindType(annotation.Type, field, scope)
            : ErrorTypeSymbol.Instance;

        if (declaration.TypeAnnotation is not null) Defer(() => _ = field.Type);
    }

    /// <summary>Aliases being resolved right now, to catch a cycle.</summary>
    private readonly HashSet<AliasSymbol> _resolvingAliases = [];

    public void BindAliasTarget(AliasSymbol alias, TypeSyntax target)
    {
        var scope = compilation.CurrentScope;

        alias.TypeResolver = () =>
        {
            // 'alias A = A', or a longer ring, would otherwise resolve forever.
            if (!_resolvingAliases.Add(alias))
            {
                return Error(ErrorCode.ERR_CircularAlias, target.Span,
                    $"'{alias.Name}' is defined in terms of itself");
            }

            try
            {
                return BindType(target, alias, scope);
            }
            finally
            {
                _resolvingAliases.Remove(alias);
            }
        };

        Defer(() => _ = alias.Type);
    }

    public void BindTypeDeclaration(NamedTypeSymbol type, SyntaxNode declaration)
    {
        var scope = compilation.CurrentScope;

        Defer(() =>
        {
            BindTypeParameterConstraints(type.TypeParameters, type, scope);

            var baseTypes = BaseTypeListOf(declaration);
            if (baseTypes is not null)
            {
                type.BaseTypes =
                    [.. baseTypes.Types.Select(syntax => BindType(syntax, type, scope))];
            }

            BindWhereClause(WhereClauseOf(declaration), type, scope);
        });

        // Conformance is checked after every signature is bound, so that the
        // comparison sees real types rather than unresolved ones — and so that
        // a conformance an extension added is checked along with the rest.
        DeferLate(() => ConformanceChecker.Check(type, _diagnostics, compilation.Core));
        DeferLate(() => VarianceChecker.Check(type, _diagnostics));
    }

    private static BaseTypeListSyntax? BaseTypeListOf(SyntaxNode declaration) => declaration switch
    {
        StructDeclarationSyntax s => s.BaseTypes,
        ClassDeclarationSyntax s => s.BaseTypes,
        ActorDeclarationSyntax s => s.BaseTypes,
        ProtocolDeclarationSyntax s => s.BaseTypes,
        UnionDeclarationSyntax s => s.BaseTypes,
        EnumDeclarationSyntax s => s.BaseTypes,
        ExtensionDeclarationSyntax s => s.BaseTypes,
        _ => null,
    };

    private static WhereClauseSyntax? WhereClauseOf(SyntaxNode declaration) => declaration switch
    {
        StructDeclarationSyntax s => s.WhereClause,
        ClassDeclarationSyntax s => s.WhereClause,
        ActorDeclarationSyntax s => s.WhereClause,
        ProtocolDeclarationSyntax s => s.WhereClause,
        UnionDeclarationSyntax s => s.WhereClause,
        ExtensionDeclarationSyntax s => s.WhereClause,
        _ => null,
    };

    private void BindTypeParameterConstraints(
        ImmutableArray<TypeParameterSymbol> typeParameters, Symbol context, FileScope? scope)
    {
        foreach (var parameter in typeParameters)
        {
            if (parameter.DeclaringSyntax.IsEmpty) continue;
            if (parameter.DeclaringSyntax[0] is not GenericParameterSyntax syntax) continue;
            if (syntax.Constraint is null) continue;

            parameter.Constraints = [BindType(syntax.Constraint, context, scope)];
        }
    }

    /// <summary>
    /// Applies a <c>where</c> clause to the parameters it names.
    /// </summary>
    /// <remarks>
    /// <c>fn f&lt;T&gt;(x: T) where T: Named</c> and
    /// <c>fn f&lt;T: Named&gt;(x: T)</c> say the same thing, so they end in
    /// the same place: the parameter's constraint list.
    /// </remarks>
    private void BindWhereClause(WhereClauseSyntax? clause, Symbol context, FileScope? scope)
    {
        if (clause is null) return;

        foreach (var constraint in clause.Constraints)
        {
            var left = BindType(constraint.Left, context, scope);
            var right = BindType(constraint.Right, context, scope);

            if (left is TypeParameterSymbol parameter && !right.IsError)
                parameter.Constraints = parameter.Constraints.Add(right);
        }
    }

    /// <summary>
    /// Checks that a constructed type's arguments meet the declaration's
    /// constraints.
    /// </summary>
    private void CheckTypeArguments(
        NamedTypeSymbol definition, ImmutableArray<TypeSymbol> arguments, TextSpan span)
    {
        for (var i = 0; i < arguments.Length && i < definition.TypeParameters.Length; i++)
        {
            foreach (var constraint in definition.TypeParameters[i].Constraints)
            {
                if (Conversions.Satisfies(arguments[i], constraint)) continue;

                _diagnostics.Error(ErrorCode.ERR_DoesNotConform, span,
                    $"'{arguments[i].ToDisplayString()}' does not conform to "
                    + $"'{constraint.ToDisplayString()}', which "
                    + $"'{definition.Name}' requires of "
                    + $"'{definition.TypeParameters[i].Name}'");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Type syntax to type symbol
    // -----------------------------------------------------------------------

    public TypeSymbol BindType(TypeSyntax syntax, Symbol context, FileScope? scope)
    {
        switch (syntax)
        {
            case SimpleNameTypeSyntax simple:
                return BindSimpleName(simple, context, scope);

            case QualifiedNameSyntax qualified:
                return BindQualifiedName(qualified, context, scope);

            case OptionalTypeSyntax optional:
                return new OptionalTypeSymbol(BindType(optional.ElementType, context, scope));

            case ImplicitlyUnwrappedTypeSyntax unwrapped:
                return new OptionalTypeSymbol(BindType(unwrapped.ElementType, context, scope));

            case ReferenceTypeSyntax reference:
            {
                var referenced = BindType(reference.ElementType, context, scope);
                var spec = reference.RelativeSpec;
                var result = new ReferenceTypeSymbol(
                    referenced,
                    spec is null ? null : spec.WidthToken.Text,
                    spec is null || spec.PoolToken.IsNone ? null : spec.PoolToken.Text);

                return reference.QuestionToken.IsNone
                    ? result
                    : new OptionalTypeSymbol(result);
            }

            case RefTypeSyntax borrowed:
                return new ReferenceTypeSymbol(BindType(borrowed.ElementType, context, scope));

            case FixedSizeArrayTypeSyntax array:
                return new ArrayTypeSymbol(
                    BindType(array.ElementType, context, scope), ArrayKind.Fixed);

            case InlineArrayTypeSyntax array:
                return new ArrayTypeSymbol(
                    BindType(array.ElementType, context, scope), ArrayKind.Inline);

            case GrowArrayTypeSyntax array:
                return new ArrayTypeSymbol(
                    BindType(array.ElementType, context, scope),
                    array.RangeToken.Kind == SyntaxKind.DotDotLessToken
                        ? ArrayKind.GrowShrink
                        : ArrayKind.Grow);

            case SliceTypeSyntax slice:
                return new ArrayTypeSymbol(
                    BindType(slice.ElementType, context, scope), ArrayKind.Slice);

            case DictionaryTypeSyntax dictionary:
                return compilation.Core.Dictionary.Construct(
                [
                    BindType(dictionary.KeyType, context, scope),
                    BindType(dictionary.ValueType, context, scope),
                ]);

            case ParenthesizedTypeSyntax parenthesized:
                return BindType(parenthesized.Type, context, scope);

            case TupleTypeSyntax tuple:
                return new TupleTypeSymbol(
                    [.. tuple.Elements.Select(e => BindType(e.Type, context, scope))],
                    [.. tuple.Elements.Select(e =>
                        e.NameToken.IsNone ? null : e.NameToken.Text)]);

            case FunctionTypeSyntax function:
            {
                var parameters = function.Parameters.Elements
                    .Select(e => BindType(e.Type, context, scope));

                TypeSymbol? thrown = null;
                if (function.ThrowsSpec is { } spec)
                {
                    thrown = spec.ErrorType is { } errorType
                        ? BindType(errorType, context, scope)
                        : compilation.Core.AnyError;
                }

                return new FunctionTypeSymbol(
                    [.. parameters],
                    BindType(function.ReturnType, context, scope),
                    !function.AsyncKeyword.IsNone,
                    thrown);
            }

            case OpaqueTypeSyntax opaque:
            {
                var constraint = BindType(opaque.ConstraintType, context, scope);
                return RequireProtocol(constraint, "some", opaque.ConstraintType.Span)
                    ? new OpaqueTypeSymbol(constraint)
                    : ErrorTypeSymbol.Instance;
            }

            case ExistentialTypeSyntax existential:
            {
                var constraint = BindType(existential.ConstraintType, context, scope);
                return RequireProtocol(constraint, "any", existential.ConstraintType.Span)
                    ? new ExistentialTypeSymbol(constraint)
                    : ErrorTypeSymbol.Instance;
            }

            case CompositionTypeSyntax composition:
                return new CompositionTypeSymbol(
                [
                    BindType(composition.Left, context, scope),
                    BindType(composition.Right, context, scope),
                ]);

            case UnionTypeSyntax union:
                return new StructuralUnionTypeSymbol(
                [
                    BindType(union.Left, context, scope),
                    BindType(union.Right, context, scope),
                ]);

            case SelfTypeSyntax self:
            {
                if (EnclosingType(context) is not { } enclosing)
                {
                    return Error(ErrorCode.ERR_NotAType, self.Span,
                        "'Self' is only meaningful inside a type or an extension");
                }

                // Inside a protocol 'Self' is the conforming type, which is
                // not known yet; everywhere else it is the type itself.
                return enclosing.SelfTypeParameter ?? (TypeSymbol)enclosing;
            }

            default:
                return ErrorTypeSymbol.Instance;
        }
    }

    private static NamedTypeSymbol? EnclosingType(Symbol context)
    {
        for (var symbol = context; symbol is not null; symbol = symbol.ContainingSymbol)
            if (symbol is NamedTypeSymbol type) return type;
        return null;
    }

    private TypeSymbol BindSimpleName(
        SimpleNameTypeSyntax syntax, Symbol context, FileScope? scope)
    {
        var name = syntax.Identifier.Text;
        if (name.Length == 0) return ErrorTypeSymbol.Instance;

        var found = Lookup(name, context, scope, syntax.Identifier.Span);
        if (found is null)
        {
            return Error(ErrorCode.ERR_TypeNotFound, syntax.Identifier.Span,
                $"no type named '{name}' is in scope");
        }

        return ApplyTypeArguments(found, syntax.TypeArgumentList, context, scope, syntax.Span);
    }

    private TypeSymbol BindQualifiedName(
        QualifiedNameSyntax syntax, Symbol context, FileScope? scope)
    {
        // A dotted name may be a module path, so try the longest module first.
        var full = Compilation.NameOf(syntax.Left);
        if (compilation.GetModule(full) is { } module)
        {
            var member = module.GetMembers(syntax.Right.Identifier.Text).FirstOrDefault();
            if (member is null)
            {
                return Error(ErrorCode.ERR_TypeNotFound, syntax.Right.Span,
                    $"'{full}' has no type named '{syntax.Right.Identifier.Text}'");
            }

            return ApplyTypeArguments(
                member, syntax.Right.TypeArgumentList, context, scope, syntax.Span);
        }

        // Otherwise the left side names a type, and the right a nested one.
        var left = BindType(syntax.Left, context, scope);
        if (left.IsError) return left;

        // 'C.Item' where C is constrained: the associated type of whatever
        // protocol declares it.
        if (left is TypeParameterSymbol parameter)
        {
            var wanted = syntax.Right.Identifier.Text;

            foreach (var constraint in parameter.Constraints)
            {
                if (constraint is not NamedTypeSymbol protocol) continue;

                foreach (var associated in protocol.AssociatedTypes)
                {
                    if (string.Equals(associated.Name, wanted, StringComparison.Ordinal))
                        return associated;
                }
            }

            return Error(ErrorCode.ERR_TypeNotFound, syntax.Right.Span,
                $"'{parameter.Name}' has no associated type named '{wanted}'");
        }

        if (left is not NamedTypeSymbol named)
        {
            return Error(ErrorCode.ERR_NotAType, syntax.Right.Span,
                $"'{left.ToDisplayString()}' has no nested types");
        }

        var nested = named.GetMembers(syntax.Right.Identifier.Text).FirstOrDefault();
        if (nested is null)
        {
            return Error(ErrorCode.ERR_TypeNotFound, syntax.Right.Span,
                $"'{named.ToDisplayString()}' has no type named "
                + $"'{syntax.Right.Identifier.Text}'");
        }

        return ApplyTypeArguments(
            nested, syntax.Right.TypeArgumentList, context, scope, syntax.Span);
    }

    private TypeSymbol ApplyTypeArguments(
        Symbol symbol,
        TypeArgumentListSyntax? argumentList,
        Symbol context,
        FileScope? scope,
        TextSpan span)
    {
        var arguments = argumentList is null
            ? ImmutableArray<TypeSymbol>.Empty
            : [.. argumentList.Arguments.Select(a => BindType(a, context, scope))];

        switch (symbol)
        {
            case TypeParameterSymbol typeParameter:
                if (!arguments.IsEmpty)
                {
                    return Error(ErrorCode.ERR_TypeArgumentsOnNonGeneric, span,
                        $"'{typeParameter.Name}' is a type parameter and takes no type arguments");
                }
                return typeParameter;

            case AliasSymbol alias:
                // Aliases are transparent; a generic alias is not yet substituted.
                if (!arguments.IsEmpty && alias.TypeParameters.Length != arguments.Length)
                {
                    return Error(ErrorCode.ERR_WrongTypeArgumentCount, span,
                        $"'{alias.Name}' takes {alias.TypeParameters.Length} type argument(s), "
                        + $"but {arguments.Length} were given");
                }
                return alias.AliasedType;

            case NamedTypeSymbol named:
            {
                var expected = named.TypeParameters.Length;

                if (arguments.IsEmpty)
                {
                    if (expected == 0) return named;

                    return Error(ErrorCode.ERR_MissingTypeArguments, span,
                        $"'{named.Name}' takes {expected} type argument(s)");
                }

                if (arguments.Length != expected)
                {
                    return Error(ErrorCode.ERR_WrongTypeArgumentCount, span,
                        $"'{named.Name}' takes {expected} type argument(s), "
                        + $"but {arguments.Length} were given");
                }

                // Constraints bind later than this, so the check waits.
                DeferLate(() => CheckTypeArguments(named, arguments, span));
                return named.Construct(arguments);
            }

            default:
                return Error(ErrorCode.ERR_NotAType, span,
                    $"'{symbol.Name}' is not a type");
        }
    }

    // -----------------------------------------------------------------------
    // Lookup
    // -----------------------------------------------------------------------

    /// <summary>Finds the symbol a type name refers to, or null.</summary>
    public Symbol? Lookup(string name, Symbol context, FileScope? scope, TextSpan span)
    {
        // Enclosing declarations: their type parameters, then their members.
        for (var symbol = context; symbol is not null; symbol = symbol.ContainingSymbol)
        {
            switch (symbol)
            {
                case NamedTypeSymbol type:
                    if (Match(type.TypeParameters, name) is { } typeParameter)
                        return typeParameter;
                    if (Match(type.AssociatedTypes, name) is { } associated)
                        return associated;
                    if (type.SelfTypeParameter is { } self
                        && string.Equals(self.Name, name, StringComparison.Ordinal))
                    {
                        return self;
                    }
                    if (FindTypeMember(type.GetMembers(name)) is { } nested) return nested;
                    break;

                case FunctionSymbol function:
                    if (Match(function.TypeParameters, name) is { } functionParameter)
                        return functionParameter;
                    break;

                case AliasSymbol alias:
                    if (Match(alias.TypeParameters, name) is { } aliasParameter)
                        return aliasParameter;
                    break;

                case ExtensionSymbol extension:
                    foreach (var written in extension.TypeParameterAliases)
                    {
                        if (string.Equals(written.Name, name, StringComparison.Ordinal))
                            return written;
                    }
                    break;
            }
        }

        if (scope is not null)
        {
            if (FindTypeMember(scope.Module.GetMembers(name)) is { } moduleMember)
                return moduleMember;

            if (scope.ImportedNames.TryGetValue(name, out var imported)) return imported;

            Symbol? fromImports = null;
            foreach (var module in scope.Imports)
            {
                var candidate = FindTypeMember(module.GetMembers(name), publicOnly: true);
                if (candidate is null) continue;

                if (fromImports is not null && !ReferenceEquals(fromImports, candidate))
                {
                    _diagnostics.Error(ErrorCode.ERR_AmbiguousName, span,
                        $"'{name}' is ambiguous between "
                        + $"'{fromImports.QualifiedName}' and '{candidate.QualifiedName}'");
                    return fromImports;
                }

                fromImports = candidate;
            }

            if (fromImports is not null) return fromImports;
        }

        // The core module is in scope everywhere.
        return FindTypeMember(compilation.Core.Module.GetMembers(name), publicOnly: true);
    }

    private static TypeParameterSymbol? Match(
        ImmutableArray<TypeParameterSymbol> parameters, string name)
    {
        foreach (var parameter in parameters)
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal)) return parameter;
        return null;
    }

    private static Symbol? FindTypeMember(
        IEnumerable<Symbol> named, bool publicOnly = false)
    {
        foreach (var member in named)
        {
            if (member is not (NamedTypeSymbol or AliasSymbol)) continue;
            if (publicOnly && member.DeclaredAccessibility != Accessibility.Public) continue;
            return member;
        }
        return null;
    }

    /// <summary>
    /// <c>some</c> and <c>any</c> take a protocol, or several composed.
    /// </summary>
    /// <remarks>
    /// They also take the whole type after them, so <c>any P[]</c> is an
    /// existential over an array rather than an array of existentials. Saying
    /// the constraint is not a protocol is how that reads back to whoever
    /// wrote it.
    /// </remarks>
    private bool RequireProtocol(TypeSymbol constraint, string keyword, TextSpan span)
    {
        if (constraint.IsError) return false;

        if (constraint is CompositionTypeSymbol composition)
        {
            var ok = true;
            foreach (var part in composition.Parts)
                ok &= RequireProtocol(part, keyword, span);
            return ok;
        }

        if (constraint is NamedTypeSymbol { TypeKind: TypeKind.Protocol }) return true;
        if (constraint is TypeParameterSymbol) return true;

        _diagnostics.Error(ErrorCode.ERR_NotAProtocol, span,
            $"'{keyword}' takes a protocol, and '{constraint.ToDisplayString()}' is not one");
        return false;
    }

    private ErrorTypeSymbol Error(ErrorCode code, TextSpan span, string message)
    {
        _diagnostics.Error(code, span, message);
        return ErrorTypeSymbol.Instance;
    }
}
