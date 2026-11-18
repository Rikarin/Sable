using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Resolves type syntax to type symbols, and binds the signatures of
///     declarations.
/// </summary>
/// <remarks>
///     <para>
///         Signature binding is deferred: declaring a symbol records a closure,
///         and the work happens the first time someone asks for the type. Asking a
///         compilation for its diagnostics forces all of them.
///     </para>
///     <para>
///         Lookup order, from docs/03-type-system.md: type parameters and
///         members of the enclosing declarations, then the containing module, then
///         names imported individually, then imported modules, then the core module
///         that every file sees.
///     </para>
/// </remarks>
internal sealed class TypeBinder(Compilation compilation) {
    readonly DiagnosticBag _diagnostics = new();
    readonly List<Action> _forcers = [];
    readonly List<Action> _late = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.ToList();

    /// <summary>
    ///     What each type name written in the source resolved to, by syntax:
    ///     the semantic model's answer for a name in a type's place.
    /// </summary>
    readonly Dictionary<SyntaxNode, Symbol> _names = [];

    /// <summary>The symbol a written type name resolved to, if it has been bound.</summary>
    internal Symbol? ResolvedName(SyntaxNode name) => _names.GetValueOrDefault(name);

    T Resolved<T>(SyntaxNode name, Symbol symbol, T type) where T : TypeSymbol {
        _names[name] = symbol;
        return type;
    }

    int _forced;
    int _forcedLate;

    /// <summary>Binds every deferred signature, so diagnostics are complete.</summary>
    /// <remarks>
    ///     Two queues, not one. Signatures come first, and the checks that compare
    ///     whole types — conformance above all — come after every signature is
    ///     bound, including the ones an extension declared somewhere else. Binding
    ///     one signature can add more work, so both are drained by index, and the
    ///     cursors persist so a second call does not repeat the diagnostics.
    /// </remarks>
    public void ForceAll() {
        while (_forced < _forcers.Count || _forcedLate < _late.Count) {
            while (_forced < _forcers.Count) {
                _forcers[_forced++]();
            }

            if (_forcedLate < _late.Count) {
                _late[_forcedLate++]();
            }
        }
    }

    internal void Defer(Action force) => _forcers.Add(force);

    /// <summary>
    ///     A generic type as its own declaration sees it: given its own
    ///     parameters, <c>Nest&lt;T&gt;</c>, which is what its members write. Any
    ///     other type is itself.
    /// </summary>
    internal static NamedTypeSymbol OwnConstruction(NamedTypeSymbol type) =>
        type is { TypeArguments.IsEmpty: true, TypeParameters.IsEmpty: false }
            ? type.Construct([..type.TypeParameters])
            : type;

    /// <summary>Defers work that must see every signature, not just this one's.</summary>
    internal void DeferLate(Action force) => _late.Add(force);

    // -----------------------------------------------------------------------
    // Signature binding
    // -----------------------------------------------------------------------

    /// <summary>
    ///     What a <c>rethrows</c> signature can let out: the union of what its
    ///     throwing function parameters throw, or null when it has none — which
    ///     is a signature that can never throw, and is reported as such.
    /// </summary>
    /// <remarks>
    ///     spec/SYNTAX.md §18: "<c>rethrows</c> means throws iff a closure
    ///     parameter throws". The error type is therefore not written anywhere;
    ///     it is read off the parameters, so that
    ///     <c>(_ body: () throws(IoError) -> T) rethrows</c> throws
    ///     <c>IoError</c> and nothing wider.
    /// </remarks>
    internal static TypeSymbol? RethrownType(IEnumerable<TypeSymbol> parameterTypes) {
        TypeSymbol? thrown = null;

        foreach (var parameterType in parameterTypes) {
            var type = parameterType is OptionalTypeSymbol optional
                ? optional.UnderlyingType
                : parameterType;

            if (type is not FunctionTypeSymbol { ThrownType: { } one }) {
                continue;
            }

            thrown = thrown is null
                ? one
                : StructuralUnionTypeSymbol.Create([thrown, one]);
        }

        return thrown;
    }

    /// <summary>
    ///     A function's result type, where a <c>some P</c> is the function's
    ///     own: the type its body returns, hidden.
    /// </summary>
    public TypeSymbol BindReturnType(TypeSyntax syntax, FunctionSymbol function, FileScope? scope) {
        var type = BindType(syntax, function, scope);
        return type is OpaqueTypeSymbol { Origin: null } opaque
            ? new OpaqueTypeSymbol(opaque.Constraint, function)
            : type;
    }

    public void BindFunctionSignature(
        FunctionSymbol function,
        FunctionDeclarationSyntax declaration
    ) {
        var scope = compilation.CurrentScope;

        function.ReturnTypeResolver = () => declaration.ReturnClause is { } clause
            ? BindReturnType(clause.Type, function, scope)
            : compilation.Core.Void;

        if (declaration.ExplicitProtocol is { } explicitProtocol) {
            function.ExplicitProtocolResolver = () => BindType(explicitProtocol, function, scope);
        }

        function.IsRethrows =
            declaration.ThrowsSpec?.ThrowsKeyword.Kind == SyntaxKind.RethrowsKeyword;

        function.ThrownTypeResolver = () => {
            if (declaration.ThrowsSpec is not { } spec) {
                return null;
            }

            if (function.IsRethrows) {
                return RethrownType(function.Parameters.Select(static p => p.Type));
            }

            return spec.ErrorType is { } errorType
                ? BindType(errorType, function, scope)
                : compilation.Core.AnyError; // bare 'throws' is 'throws(any Error)'
        };

        Defer(() => {
                // Constraints first: a signature may mention 'T.Associated',
                // which is only findable once T knows what it is constrained to.
                BindTypeParameterConstraints(function.TypeParameters, function, scope);
                BindWhereClause(declaration.WhereClause, function, scope);

                _ = function.ReturnType;
                _ = function.ExplicitProtocol;
                foreach (var parameter in function.Parameters) {
                    _ = parameter.Type;
                }

                if (function is { IsRethrows: true, ThrownType: null }) {
                    _diagnostics.Error(
                        ErrorCode.ERR_RethrowsWithoutThrowingParameter,
                        declaration.ThrowsSpec!.Span,
                        $"'{function.Name}' is 'rethrows', so it needs a parameter "
                        + "of throwing function type to rethrow from"
                    );
                }

                _ = function.ThrownType;
            }
        );

        // What a signature names, a caller has to be able to name too.
        DeferLate(() => {
                if (declaration.ReturnClause is { } clause) {
                    CheckExposure(function, function.ReturnType, clause.Type.Span, "its result type");
                }

                for (var i = 0; i < function.Parameters.Length && i < declaration.Parameters.Parameters.Count; i++) {
                    var parameter = function.Parameters[i];
                    CheckExposure(function, parameter.Type, declaration.Parameters.Parameters[i].Type.Span, $"its parameter '{parameter.Name}'");
                }

                if (declaration.ThrowsSpec?.ErrorType is { } errorType && function.ThrownType is { } thrown) {
                    CheckExposure(function, thrown, errorType.Span, "the error it throws");
                }
            }
        );
    }

    /// <summary>
    ///     Reports a declaration whose signature names a type less visible
    ///     than the declaration itself: a public function returning an
    ///     internal type could be called from where its result cannot be
    ///     named, or its members reached.
    /// </summary>
    void CheckExposure(Symbol member, TypeSymbol type, TextSpan span, string what) {
        var access = EffectiveAccessibility(member);
        if (access <= Accessibility.Internal) {
            return;
        }

        if (LeastVisible(type) is not var (hidden, hiddenAccess) || hiddenAccess >= access) {
            return;
        }

        _diagnostics.Error(
            ErrorCode.ERR_ExposesHiddenType,
            span,
            $"'{member.Name}' is {Word(access)}, and {what} names '{hidden.Name}', which is {Word(hiddenAccess)}: "
            + $"make '{hidden.Name}' {Word(access)}, or '{member.Name}' less visible"
        );

        static string Word(Accessibility accessibility) => accessibility.ToString().ToLowerInvariant();
    }

    /// <summary>
    ///     How far a declaration is visible: no farther than what holds it, so
    ///     a public member of an internal type is internal.
    /// </summary>
    static Accessibility EffectiveAccessibility(Symbol symbol) {
        var access = symbol.DeclaredAccessibility;
        for (var here = symbol.ContainingSymbol; here is not null and not ModuleSymbol; here = here.ContainingSymbol) {
            var holder = here is ExtensionSymbol extension ? extension.ExtendedType : here;
            if (holder is NamedTypeSymbol or FunctionSymbol && holder.DeclaredAccessibility < access) {
                access = holder.DeclaredAccessibility;
            }

            // A body's local declarations are its own.
            if (here is FunctionSymbol) {
                return Accessibility.Private;
            }
        }

        return access;
    }

    /// <summary>The least visible named type a type is built from, if any.</summary>
    static (NamedTypeSymbol Type, Accessibility Access)? LeastVisible(TypeSymbol type) {
        (NamedTypeSymbol, Accessibility)? least = null;
        Visit(type);
        return least;

        void Visit(TypeSymbol part) {
            switch (part) {
                case NamedTypeSymbol named: {
                    var definition = named.OriginalDefinition;
                    var access = EffectiveAccessibility(definition);
                    if (least is not { Item2: var current } || access < current) {
                        least = (definition, access);
                    }

                    foreach (var argument in named.TypeArguments) {
                        Visit(argument);
                    }

                    break;
                }

                case ArrayTypeSymbol array:
                    Visit(array.ElementType);
                    break;

                case OptionalTypeSymbol optional:
                    Visit(optional.UnderlyingType);
                    break;

                case ReferenceTypeSymbol reference:
                    Visit(reference.ReferencedType);
                    break;

                case TupleTypeSymbol tuple:
                    foreach (var element in tuple.ElementTypes) {
                        Visit(element);
                    }

                    break;

                case FunctionTypeSymbol function:
                    foreach (var parameter in function.ParameterTypes) {
                        Visit(parameter);
                    }

                    Visit(function.ReturnType);
                    break;

                case ExistentialTypeSymbol existential:
                    Visit(existential.Constraint);
                    break;

                case OpaqueTypeSymbol opaque:
                    Visit(opaque.Constraint);
                    break;

                case CompositionTypeSymbol composition:
                    foreach (var element in composition.Parts) {
                        Visit(element);
                    }

                    break;

                case StructuralUnionTypeSymbol union:
                    foreach (var element in union.Cases) {
                        Visit(element);
                    }

                    break;
            }
        }
    }

    public void BindParameterType(ParameterSymbol parameter, ParameterSyntax syntax) {
        var scope = compilation.CurrentScope;

        parameter.TypeResolver = () => {
            var written = BindType(syntax.Type, parameter.ContainingSymbol ?? parameter, scope);

            // 'values: Int...' is written as the element type and is a
            // sequence of them inside the body.
            return parameter.IsVariadic
                ? new ArrayTypeSymbol(written, ArrayKind.Slice)
                : written;
        };
    }

    public void BindUnionCaseFieldType(ParameterSymbol parameter, UnionCaseFieldSyntax syntax) {
        var scope = compilation.CurrentScope;
        parameter.TypeResolver = () =>
            BindType(syntax.Type, parameter.ContainingSymbol ?? parameter, scope);

        Defer(() => _ = parameter.Type);
    }

    public void BindFieldType(FieldSymbol field, FieldDeclarationSyntax declaration) {
        var scope = compilation.CurrentScope;

        field.TypeResolver = () => declaration.Declarator.TypeAnnotation is { } annotation
            ? BindType(annotation.Type, field, scope)
            // Without an annotation the type comes from the initial value, read
            // off its form: members are declared before expressions are bound.
            : declaration.Declarator.Initializer is { } initializer && InferFromSyntax(initializer.Value, field, scope) is { } inferred
                ? inferred
                : Error(
                    ErrorCode.ERR_TypeNotFound,
                    declaration.Declarator.Span,
                    $"'{field.Name}' needs a type annotation: its type can be left out only when its initial value is a literal, "
                    + "an array of literals, or a call to a type's initialiser"
                );

        Defer(() => _ = field.Type);

        if (declaration.Declarator.TypeAnnotation is { } written) {
            DeferLate(() => CheckExposure(field, field.Type, written.Type.Span, "its type"));
        }
    }

    /// <summary>A stored property's type, from the form of its initial value: '0' is Int, 'Point(…)' a Point.</summary>
    TypeSymbol? InferFromSyntax(ExpressionSyntax value, Symbol context, FileScope? scope) {
        var core = compilation.Core;
        switch (value) {
            case ParenthesizedExpressionSyntax parenthesized:
                return InferFromSyntax(parenthesized.Expression, context, scope);

            case PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.MinusToken } negated:
                return InferFromSyntax(negated.Operand, context, scope) is { } number && core.IsNumeric(number) ? number : null;

            case LiteralExpressionSyntax literal:
                return literal.Token.Kind switch {
                    SyntaxKind.IntegerLiteralToken => core.Int,
                    SyntaxKind.FloatLiteralToken => core.F64,
                    SyntaxKind.StringLiteralToken or SyntaxKind.MultilineStringLiteralToken or SyntaxKind.RawStringLiteralToken => core.String,
                    SyntaxKind.CharLiteralToken => core.Char,
                    SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => core.Bool,
                    _ => null,
                };

            case InterpolatedStringExpressionSyntax:
                return core.String;

            case ArrayExpressionSyntax { Elements.Count: > 0 } array
                when InferFromSyntax(array.Elements[0], context, scope) is { } element:
                return new ArrayTypeSymbol(element, ArrayKind.Grow);

            // 'Point { x: 0, y: 0 }': a record of the type it names.
            case RecordExpressionSyntax record:
                return BindType(record.Type, context, scope);

            // 'Counter()': a call to a type's initialiser.
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax callee }
                when Lookup(callee.Identifier.Text, context, scope, callee.Span) is NamedTypeSymbol { IsGenericDefinition: false } type:
                return type;

            default:
                return null;
        }
    }

    public void BindConstantType(FieldSymbol field, ConstDeclarationSyntax declaration) {
        var scope = compilation.CurrentScope;

        field.TypeResolver = () => {
            if (declaration.TypeAnnotation is { } annotation) {
                return BindType(annotation.Type, field, scope);
            }

            // 'const Pi = 3.14', 'const Table = factorial(5)': the type is read
            // off the value's form, as a stored property's is, with what a
            // constant is usually written as besides — a call, another
            // constant, arithmetic on them. A ring of constants, each the
            // next's, has no type to find.
            if (!_inferringConstants.Add(field)) {
                return Error(
                    ErrorCode.ERR_TypeNotFound,
                    declaration.Identifier.Span,
                    $"'{field.Name}' is defined in terms of itself, so its type cannot be inferred; write it"
                );
            }

            try {
                return InferConstant(declaration.Initializer.Value, field, scope) is { IsError: false } inferred
                    ? inferred
                    : Error(
                        ErrorCode.ERR_TypeNotFound,
                        declaration.Identifier.Span,
                        $"'{field.Name}' needs a type annotation: its type can be left out only when its value is a literal, "
                        + "another constant, a call to a function or a type's initialiser, or arithmetic on those"
                    );
            } finally {
                _inferringConstants.Remove(field);
            }
        };

        Defer(() => _ = field.Type);
    }

    readonly HashSet<FieldSymbol> _inferringConstants = [];

    /// <summary>
    ///     A constant's type, from the form of its value: what
    ///     <see cref="InferFromSyntax" /> reads, and calls, names and
    ///     operators over them.
    /// </summary>
    TypeSymbol? InferConstant(ExpressionSyntax value, Symbol context, FileScope? scope) {
        var core = compilation.Core;
        switch (value) {
            case ParenthesizedExpressionSyntax parenthesized:
                return InferConstant(parenthesized.Expression, context, scope);

            case PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.MinusToken or SyntaxKind.TildeToken } negated:
                return InferConstant(negated.Operand, context, scope) is { } number && core.IsNumeric(number) ? number : null;

            case PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.BangToken }:
                return core.Bool;

            case BinaryExpressionSyntax binary:
                switch (binary.OperatorToken.Kind) {
                    case SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or SyntaxKind.LessToken
                        or SyntaxKind.LessEqualsToken or SyntaxKind.GreaterToken or SyntaxKind.GreaterEqualsToken
                        or SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken:
                        return core.Bool;
                    case SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken
                        or SyntaxKind.PercentToken or SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.CaretToken
                        or SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken: {
                        // A literal takes the other side's type: 'Width * 2' is what Width is.
                        var left = InferConstant(binary.Left, context, scope);
                        var right = InferConstant(binary.Right, context, scope);
                        return (left, right) switch {
                            ({ } l, { } r) when ReferenceEquals(l, r) => l,
                            ({ } l, _) when binary.Right is LiteralExpressionSyntax => l,
                            (_, { } r) when binary.Left is LiteralExpressionSyntax => r,
                            _ => null,
                        };
                    }
                    default:
                        return null;
                }

            case ConditionalExpressionSyntax conditional:
                return InferConstant(conditional.WhenTrue, context, scope) ?? InferConstant(conditional.WhenFalse, context, scope);

            case IdentifierNameSyntax name when LookupValue(name.Identifier.Text, context, scope) is FieldSymbol other:
                return other.Type is { IsError: false } type ? type : null;

            // 'Platform.pointerWidth': a type's static member.
            case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax owner, Name: IdentifierNameSyntax memberName }
                when Lookup(owner.Identifier.Text, context, scope, owner.Span) is NamedTypeSymbol ownerType
                && ownerType.GetMembers(memberName.Identifier.Text).OfType<FieldSymbol>().FirstOrDefault(static f => f.IsStatic) is { } member:
                return member.Type is { IsError: false } memberType ? memberType : null;

            // 'factorial(5)': what the function it calls returns.
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax callee }
                when LookupValue(callee.Identifier.Text, context, scope) is FunctionSymbol { TypeParameters.IsEmpty: true } function:
                return function.ReturnType is { IsError: false } returned ? returned : null;

            default:
                return InferFromSyntax(value, context, scope);
        }
    }

    /// <summary>
    ///     The function or stored value a name means where a constant's value
    ///     is written: a member of an enclosing type, the module's, an
    ///     import's, the core's.
    /// </summary>
    Symbol? LookupValue(string name, Symbol context, FileScope? scope) {
        static Symbol? Value(IEnumerable<Symbol> members, bool publicOnly) =>
            members.FirstOrDefault(m => m is FunctionSymbol or FieldSymbol && (!publicOnly || m.DeclaredAccessibility == Accessibility.Public));

        for (var symbol = context.ContainingSymbol; symbol is not null; symbol = symbol.ContainingSymbol) {
            if (symbol is NamedTypeSymbol type && Value(type.GetMembers(name), false) is { } member) {
                return member;
            }
        }

        if (scope is not null) {
            if (Value(scope.Module.GetMembers(name), false) is { } own) {
                return own;
            }

            foreach (var module in scope.Imports) {
                if (Value(module.GetMembers(name), true) is { } imported) {
                    return imported;
                }
            }
        }

        return Value(compilation.Core.Module.GetMembers(name), true);
    }

    /// <summary>Aliases being resolved right now, to catch a cycle.</summary>
    readonly HashSet<AliasSymbol> _resolvingAliases = [];

    public void BindAliasTarget(AliasSymbol alias, TypeSyntax target) {
        var scope = compilation.CurrentScope;

        // A union may mention itself inside its cases — 'Json[]' in
        // 'alias Json = … | Json[]' — and is then known by its name
        // (RecursiveUnionTypeSymbol). Made on the first mention of itself.
        RecursiveUnionTypeSymbol? recursive = null;

        alias.TypeResolver = () => {
            if (!_resolvingAliases.Add(alias)) {
                if (target is UnionTypeSyntax && alias.TypeParameters.IsEmpty) {
                    return recursive ??= new RecursiveUnionTypeSymbol(alias);
                }

                // 'alias A = A', or a longer ring, would otherwise resolve forever.
                return Error(
                    ErrorCode.ERR_CircularAlias,
                    target.Span,
                    $"'{alias.Name}' is defined in terms of itself"
                );
            }

            try {
                var type = BindType(target, alias, scope);
                if (recursive is null) {
                    return type;
                }

                // Itself as one of its own cases, or behind '?', would be a
                // value that holds itself: nothing to stop at, and no size.
                if (type is not StructuralUnionTypeSymbol union || union.Cases.Contains(recursive)) {
                    return Error(
                        ErrorCode.ERR_CircularAlias,
                        target.Span,
                        $"'{alias.Name}' is defined in terms of itself: a union can mention itself only "
                        + "inside one of its cases, as in an array or a dictionary of it"
                    );
                }

                recursive.Union = union;
                return recursive;
            } finally {
                _resolvingAliases.Remove(alias);
            }
        };

        Defer(() => _ = alias.Type);
    }

    public void BindTypeDeclaration(NamedTypeSymbol type, SyntaxNode declaration) {
        var scope = compilation.CurrentScope;

        // Bound when the base types are first asked for — whether the type
        // conforms to something may be asked before this deferred work runs —
        // and in any case with the rest of the signatures. What an extension
        // adds is added to what this binds, never replaced by it.
        type.PendingBaseTypes = () => {
            BindTypeParameterConstraints(type.TypeParameters, type, scope);

            var baseTypes = BaseTypeListOf(declaration);
            if (baseTypes is not null) {
                type.BaseTypes =
                    [..baseTypes.Types.Select(syntax => BindType(syntax, type, scope)), ..type.BaseTypes];
            }

            // '@reflectable' is a conformance to Sable.Core's Reflectable,
            // which 'TypeInfo.of' asks of its argument.
            if (type.IsReflectable && compilation.Core.Module.GetType("Reflectable") is { TypeKind: TypeKind.Protocol } reflectable
                && !type.BaseTypes.Contains(reflectable)) {
                type.BaseTypes = type.BaseTypes.Add(reflectable);
            }

            BindWhereClause(WhereClauseOf(declaration), type, scope);
        };

        Defer(() => _ = type.BaseTypes);

        // Conformance is checked after every signature is bound, so that the
        // comparison sees real types rather than unresolved ones — and so that
        // a conformance an extension added is checked along with the rest.
        DeferLate(() => ConformanceChecker.Check(type, _diagnostics, compilation.Core));
        DeferLate(() => VarianceChecker.Check(type, _diagnostics));
        DeferLate(() => LayoutChecker.Check(type, _diagnostics));
        DeferLate(() => CycleChecker.Check(type, _diagnostics));
    }

    /// <summary>An array extension's constraints: on its parameters, and in its <c>where</c> clause.</summary>
    public void BindArrayExtension(NamedTypeSymbol carrier, ExtensionDeclarationSyntax declaration, FileScope? scope) =>
        Defer(() => {
                BindTypeParameterConstraints(carrier.TypeParameters, carrier, scope);
                BindWhereClause(declaration.WhereClause, carrier, scope);
            }
        );

    /// <summary>A named type's extension's <c>where</c> clause, kept on the extension.</summary>
    public void BindExtensionWhereClause(ExtensionSymbol extension, ExtensionDeclarationSyntax declaration, FileScope? scope) =>
        Defer(() => BindWhereClause(declaration.WhereClause, extension, scope));

    static BaseTypeListSyntax? BaseTypeListOf(SyntaxNode declaration) =>
        declaration switch {
            StructDeclarationSyntax s => s.BaseTypes,
            ClassDeclarationSyntax s => s.BaseTypes,
            ActorDeclarationSyntax s => s.BaseTypes,
            ProtocolDeclarationSyntax s => s.BaseTypes,
            UnionDeclarationSyntax s => s.BaseTypes,
            EnumDeclarationSyntax s => s.BaseTypes,
            ExtensionDeclarationSyntax s => s.BaseTypes,
            _ => null,
        };

    static WhereClauseSyntax? WhereClauseOf(SyntaxNode declaration) =>
        declaration switch {
            StructDeclarationSyntax s => s.WhereClause,
            ClassDeclarationSyntax s => s.WhereClause,
            ActorDeclarationSyntax s => s.WhereClause,
            ProtocolDeclarationSyntax s => s.WhereClause,
            UnionDeclarationSyntax s => s.WhereClause,
            ExtensionDeclarationSyntax s => s.WhereClause,
            _ => null,
        };

    void BindTypeParameterConstraints(
        ImmutableArray<TypeParameterSymbol> typeParameters,
        Symbol context,
        FileScope? scope
    ) {
        foreach (var parameter in typeParameters) {
            if (parameter.DeclaringSyntax.IsEmpty) {
                continue;
            }

            if (parameter.DeclaringSyntax[0] is not GenericParameterSyntax syntax) {
                continue;
            }

            if (syntax.Constraint is null) {
                continue;
            }

            // 'T: P & Q' is kept as two constraints, as 'where T: P, T: Q' is,
            // so everything that walks a parameter's protocols sees both.
            var constraints = Constraints(syntax.Constraint, parameter);
            parameter.Constraints = [..constraints.SelectMany(c => CompositionTypeSymbol.PartsOf(BindType(c, context, scope)))];
        }
    }

    /// <summary>
    ///     A constraint's parts, without the <c>~Copyable</c> among them, which
    ///     instead lets the parameter stand for a move-only type (docs/03 §3.3).
    /// </summary>
    static List<TypeSyntax> Constraints(TypeSyntax constraint, TypeParameterSymbol parameter) {
        switch (constraint) {
            case SuppressedTypeSyntax { ConstraintType: SimpleNameTypeSyntax { Identifier.Text: "Copyable", TypeArgumentList: null } }:
                parameter.AllowsNoncopyable = true;
                return [];

            case CompositionTypeSyntax composition when ContainsSuppression(composition):
                return [..Constraints(composition.Left, parameter), ..Constraints(composition.Right, parameter)];

            default:
                return [constraint];
        }

        static bool ContainsSuppression(TypeSyntax type) =>
            type is SuppressedTypeSyntax || type is CompositionTypeSyntax composition && (ContainsSuppression(composition.Left) || ContainsSuppression(composition.Right));
    }

    /// <summary>
    ///     Applies a <c>where</c> clause to the parameters it names.
    /// </summary>
    /// <remarks>
    ///     <c>fn f&lt;T&gt;(x: T) where T: Named</c> and
    ///     <c>fn f&lt;T: Named&gt;(x: T)</c> say the same thing, so they end in
    ///     the same place: the parameter's constraint list.
    /// </remarks>
    void BindWhereClause(WhereClauseSyntax? clause, Symbol context, FileScope? scope) {
        if (clause is null) {
            return;
        }

        foreach (var item in clause.Constraints) {
            // A const-generic predicate tests a value parameter, and generic
            // parameters are types only so far: there is nothing for 'N > 0'
            // to be about.
            if (item is not WhereConstraintSyntax constraint) {
                _diagnostics.Error(
                    ErrorCode.ERR_ConstGenericPredicate,
                    item.Span,
                    "a const-generic predicate needs a value generic parameter, and generic parameters are types only so far"
                );
                continue;
            }

            var left = BindType(constraint.Left, context, scope);

            // 'where T: ~Copyable' says what 'T: ~Copyable' does.
            if (left is TypeParameterSymbol lifted && constraint.OperatorToken.Kind != SyntaxKind.EqualsEqualsToken
                && constraint.Right is SuppressedTypeSyntax or CompositionTypeSyntax) {
                var parts = Constraints(constraint.Right, lifted);
                if (parts is not [var whole] || !ReferenceEquals(whole, constraint.Right)) {
                    lifted.Constraints = lifted.Constraints.AddRange(parts.Select(p => BindType(p, context, scope)).Where(static t => !t.IsError));
                    continue;
                }
            }

            var right = BindType(constraint.Right, context, scope);

            if (left.IsError || right.IsError) {
                continue;
            }

            // 'S.Element == T' makes two types one; it is not a conformance.
            if (constraint.OperatorToken.Kind == SyntaxKind.EqualsEqualsToken) {
                var requirement = new SameTypeRequirement(left, right);
                switch (context) {
                    case FunctionSymbol function:
                        function.SameTypeRequirements = function.SameTypeRequirements.Add(requirement);
                        break;

                    case NamedTypeSymbol type:
                        type.SameTypeRequirements = type.SameTypeRequirements.Add(requirement);
                        break;

                    case ExtensionSymbol extension:
                        extension.SameTypeRequirements = extension.SameTypeRequirements.Add(requirement);
                        break;
                }

                continue;
            }

            // An extension's 'where T: P' is the extension's alone: T is the
            // type's parameter, and the type itself asks nothing of it.
            if (context is ExtensionSymbol constrained) {
                constrained.ConformanceRequirements = constrained.ConformanceRequirements.AddRange(
                    CompositionTypeSymbol.PartsOf(right).Select(part => new ConformanceRequirement(left, part))
                );
                continue;
            }

            if (left is TypeParameterSymbol parameter) {
                parameter.Constraints = parameter.Constraints.AddRange(CompositionTypeSymbol.PartsOf(right));
                continue;
            }

            // 'C.Item: Comparable' constrains a type no single parameter owns.
            if (left is DependentMemberTypeSymbol) {
                var requirement = new ConformanceRequirement(left, right);
                switch (context) {
                    case FunctionSymbol function:
                        function.ConformanceRequirements = function.ConformanceRequirements.Add(requirement);
                        break;

                    case NamedTypeSymbol type:
                        type.ConformanceRequirements = type.ConformanceRequirements.Add(requirement);
                        break;
                }
            }
        }
    }

    /// <summary>
    ///     Checks that a constructed type's arguments meet the declaration's
    ///     constraints and its <c>where</c> clause, as understood inside
    ///     <paramref name="context" />.
    /// </summary>
    /// <remarks>
    ///     The same check serves arguments written, <c>Box&lt;Circle&gt;</c>,
    ///     and arguments inferred from an initialiser's, <c>Box(t: circle)</c>.
    ///     A requirement is read with the arguments in place of the
    ///     parameters, so <c>where A == B</c> on <c>Pair&lt;Int, String&gt;</c>
    ///     asks whether Int is String; and as the context's own requirements
    ///     have it, so <c>Pair&lt;X, Y&gt;</c> is fine inside a function that
    ///     says <c>where X == Y</c>.
    /// </remarks>
    internal void CheckTypeArguments(
        NamedTypeSymbol definition,
        ImmutableArray<TypeSymbol> arguments,
        TextSpan span,
        Symbol? context = null
    ) {
        if (arguments.Any(TypeSubstitution.ContainsError)) {
            return;
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        for (var i = 0; i < arguments.Length && i < definition.TypeParameters.Length; i++) {
            map[definition.TypeParameters[i]] = arguments[i];
        }

        var substitution = new TypeSubstitution(map);
        var equivalence = TypeEquivalence.ForSymbol(context);

        for (var i = 0; i < arguments.Length && i < definition.TypeParameters.Length; i++) {
            if (Copyability.Refusal(definition.TypeParameters[i], arguments[i], definition.Name) is { } refusal) {
                _diagnostics.Error(ErrorCode.ERR_NoncopyableTypeArgument, span, refusal);
            }

            foreach (var constraint in definition.TypeParameters[i].Constraints) {
                if (Conversions.Satisfies(arguments[i], substitution.Apply(constraint), equivalence)) {
                    continue;
                }

                _diagnostics.Error(
                    ErrorCode.ERR_DoesNotConform,
                    span,
                    $"'{arguments[i].ToDisplayString()}' does not conform to "
                    + $"'{constraint.ToDisplayString()}', which "
                    + $"'{definition.Name}' requires of "
                    + $"'{definition.TypeParameters[i].Name}'"
                );
            }
        }

        foreach (var (left, right) in definition.SameTypeRequirements) {
            var leftType = substitution.Apply(left);
            var rightType = substitution.Apply(right);
            if (equivalence.AreSame(leftType, rightType)) {
                continue;
            }

            _diagnostics.Error(
                ErrorCode.ERR_DoesNotConform,
                span,
                $"'{definition.Name}' requires '{left.ToDisplayString()} == {right.ToDisplayString()}', "
                + $"and here they are '{leftType.ToDisplayString()}' and '{rightType.ToDisplayString()}'"
            );
        }

        foreach (var (subject, constraint) in definition.ConformanceRequirements) {
            var subjectType = substitution.Apply(subject);
            if (Conversions.Satisfies(subjectType, substitution.Apply(constraint), equivalence)) {
                continue;
            }

            _diagnostics.Error(
                ErrorCode.ERR_DoesNotConform,
                span,
                $"'{subjectType.ToDisplayString()}' does not conform to '{constraint.ToDisplayString()}', "
                + $"which '{definition.Name}' requires of '{subject.ToDisplayString()}'"
            );
        }
    }

    // -----------------------------------------------------------------------
    // Type syntax to type symbol
    // -----------------------------------------------------------------------

    public TypeSymbol BindType(TypeSyntax syntax, Symbol context, FileScope? scope) {
        switch (syntax) {
            case SimpleNameTypeSyntax simple:
                return BindSimpleName(simple, context, scope);

            case QualifiedNameSyntax qualified:
                return BindQualifiedName(qualified, context, scope);

            case OptionalTypeSyntax optional:
                return new OptionalTypeSymbol(BindType(optional.ElementType, context, scope));

            case ImplicitlyUnwrappedTypeSyntax unwrapped:
                return new OptionalTypeSymbol(BindType(unwrapped.ElementType, context, scope), isImplicitlyUnwrapped: true);

            case ReferenceTypeSyntax reference: {
                var referenced = BindType(reference.ElementType, context, scope);
                var spec = reference.RelativeSpec;
                var result = new ReferenceTypeSymbol(
                    referenced,
                    spec?.WidthToken.Text,
                    spec is null || spec.PoolToken.IsNone ? null : spec.PoolToken.Text
                );

                return reference.QuestionToken.IsNone
                    ? result
                    : new OptionalTypeSymbol(result);
            }

            case RefTypeSyntax borrowed:
                return new ReferenceTypeSymbol(BindType(borrowed.ElementType, context, scope));

            case FixedSizeArrayTypeSyntax array:
                return new ArrayTypeSymbol(BindType(array.ElementType, context, scope), ArrayKind.Fixed, CapacityOf(array.Size));

            case InlineArrayTypeSyntax array:
                return new ArrayTypeSymbol(BindType(array.ElementType, context, scope), ArrayKind.Inline, CapacityOf(array.Capacity));

            case GrowArrayTypeSyntax array:
                return new ArrayTypeSymbol(
                    BindType(array.ElementType, context, scope),
                    array.RangeToken.Kind == SyntaxKind.DotDotLessToken
                        ? ArrayKind.GrowShrink
                        : ArrayKind.Grow
                );

            case SliceTypeSyntax slice:
                return new ArrayTypeSymbol(BindType(slice.ElementType, context, scope), ArrayKind.Slice);

            case DictionaryTypeSyntax dictionary:
                return compilation.Core.Dictionary.Construct(
                    [
                        BindType(dictionary.KeyType, context, scope),
                        BindType(dictionary.ValueType, context, scope)
                    ]
                );

            case ParenthesizedTypeSyntax parenthesized:
                return BindType(parenthesized.Type, context, scope);

            case TupleTypeSyntax tuple:
                return new TupleTypeSymbol(
                    [..tuple.Elements.Select(e => BindType(e.Type, context, scope))],
                    [
                        ..tuple.Elements.Select(static e =>
                            e.NameToken.IsNone ? null : e.NameToken.Text
                        )
                    ]
                );

            case FunctionTypeSyntax function: {
                var parameters = function.Parameters.Elements
                    .Select(e => BindType(e.Type, context, scope));

                var parameterTypes = parameters.ToImmutableArray();
                var rethrows =
                    function.ThrowsSpec?.ThrowsKeyword.Kind == SyntaxKind.RethrowsKeyword;

                TypeSymbol? thrown = null;
                if (rethrows) {
                    thrown = RethrownType(parameterTypes);
                    if (thrown is null) {
                        _diagnostics.Error(
                            ErrorCode.ERR_RethrowsWithoutThrowingParameter,
                            function.ThrowsSpec!.Span,
                            "'rethrows' needs a parameter of throwing function "
                            + "type to rethrow from"
                        );
                    }
                } else if (function.ThrowsSpec is { } spec) {
                    thrown = spec.ErrorType is { } errorType
                        ? BindType(errorType, context, scope)
                        : compilation.Core.AnyError;
                }

                return new FunctionTypeSymbol(
                    parameterTypes,
                    BindType(function.ReturnType, context, scope),
                    !function.AsyncKeyword.IsNone,
                    thrown
                ) { IsRethrows = rethrows };
            }

            case OpaqueTypeSyntax opaque: {
                var constraint = BindType(opaque.ConstraintType, context, scope);
                return RequireProtocol(constraint, "some", opaque.ConstraintType.Span)
                    ? new OpaqueTypeSymbol(constraint)
                    : ErrorTypeSymbol.Instance;
            }

            case ExistentialTypeSyntax existential: {
                var constraint = BindType(existential.ConstraintType, context, scope);
                return RequireProtocol(constraint, "any", existential.ConstraintType.Span)
                    ? new ExistentialTypeSymbol(constraint)
                    : ErrorTypeSymbol.Instance;
            }

            // '@escaping': the parameter's, read where it is declared.
            case AttributedTypeSyntax attributed:
                return BindType(attributed.Type, context, scope);

            // Anywhere but a generic parameter's constraint, which takes it apart first.
            case SuppressedTypeSyntax suppressed:
                return Error(
                    ErrorCode.ERR_MisplacedSuppression,
                    suppressed.Span,
                    $"'{suppressed.ToString().Trim()}' is not a type: '~Copyable' is written only as a generic parameter's constraint, to let it stand for a @noncopy type"
                );

            case CompositionTypeSyntax composition:
                return new CompositionTypeSymbol(
                    [
                        BindType(composition.Left, context, scope),
                        BindType(composition.Right, context, scope)
                    ]
                );

            case UnionTypeSyntax union:
                return BindUnion(union, context, scope);

            case SelfTypeSyntax self: {
                if (EnclosingType(context) is not { } enclosing) {
                    return Error(
                        ErrorCode.ERR_NotAType,
                        self.Span,
                        "'Self' is only meaningful inside a type or an extension"
                    );
                }

                // Inside a protocol 'Self' is the conforming type, which is
                // not known yet; everywhere else it is the type itself.
                return enclosing.SelfTypeParameter ?? (TypeSymbol)OwnConstruction(enclosing);
            }

            default:
                return ErrorTypeSymbol.Instance;
        }
    }

    static NamedTypeSymbol? EnclosingType(Symbol context) {
        for (var symbol = context; symbol is not null; symbol = symbol.ContainingSymbol) {
            if (symbol is NamedTypeSymbol type) {
                return type;
            }
        }

        return null;
    }

    TypeSymbol BindSimpleName(
        SimpleNameTypeSyntax syntax,
        Symbol context,
        FileScope? scope
    ) {
        var name = syntax.Identifier.Text;
        if (name.Length == 0) {
            return ErrorTypeSymbol.Instance;
        }

        // The keyword 'region' in a type's place is the region type.
        if (syntax.Identifier.Kind == SyntaxKind.RegionKeyword) {
            return compilation.Core.Region;
        }

        var found = Lookup(name, context, scope, syntax.Identifier.Span);
        if (found is null) {
            return Error(
                ErrorCode.ERR_TypeNotFound,
                syntax.Identifier.Span,
                $"no type named '{name}' is in scope"
            );
        }

        compilation.ReportUse(_diagnostics, found, syntax.Identifier.Span, context, scope);

        return Resolved(syntax, found, ApplyTypeArguments(found, syntax.TypeArgumentList, context, scope, syntax.Span));
    }

    TypeSymbol BindQualifiedName(
        QualifiedNameSyntax syntax,
        Symbol context,
        FileScope? scope
    ) {
        // A dotted name may be a module path, so try the longest module first.
        // An alias the file imported stands for the module it names:
        // 'M.Vector' after 'import Sable.Math as M'.
        var full = Compilation.NameOf(syntax.Left);
        // 'T.Type' where a module is also called T, and declares no 'Type':
        // the metatype of the type, below.
        if (compilation.ResolveModule(full, scope) is { } module
            && !(syntax.Right is { Identifier.Text: "Type", TypeArgumentList: null } && !module.GetMembers("Type").Any())) {
            full = module.Name;
            var member = module.GetMembers(syntax.Right.Identifier.Text).FirstOrDefault();
            if (member is null) {
                return Error(
                    ErrorCode.ERR_TypeNotFound,
                    syntax.Right.Span,
                    $"'{full}' has no type named '{syntax.Right.Identifier.Text}'"
                );
            }

            // Naming the module does not get past what it keeps to itself.
            if (!Visibility.IsVisible(member, scope, compilation)) {
                ReportHidden(member, syntax.Right.Span);
            }

            compilation.ReportUse(_diagnostics, member, syntax.Right.Span, context, scope);

            return Resolved(syntax.Right, member, ApplyTypeArguments(member, syntax.Right.TypeArgumentList, context, scope, syntax.Span));
        }

        // Otherwise the left side names a type, and the right a nested one.
        var left = BindType(syntax.Left, context, scope);
        if (left.IsError) {
            return left;
        }

        // 'T.Type': the type of 'T.self' — unless the type has a nested type
        // of that name, which its own name reaches first.
        if (syntax.Right is { Identifier.Text: "Type", TypeArgumentList: null }
            && (left is not NamedTypeSymbol owner || !owner.GetMembers("Type").Any())) {
            return new MetatypeTypeSymbol(left);
        }

        // 'C.Item' where C is constrained: the associated type of whatever
        // protocol declares it, read through C — so that A.Item and B.Item
        // stay different types. 'C.Slice.Element' reads through C.Slice.
        if (AssociatedTypes.IsAbstract(left)) {
            var wanted = syntax.Right.Identifier.Text;

            var constraints = AssociatedTypes.IsProtocolSelf(left)
                ? [(TypeSymbol)left.ContainingSymbol!]
                : AssociatedTypes.ConstraintsOf(left);

            if (AssociatedTypes.Find(constraints, wanted) is { } associated) {
                return AssociatedTypes.Member(left, associated);
            }

            return Error(
                ErrorCode.ERR_TypeNotFound,
                syntax.Right.Span,
                $"'{left.ToDisplayString()}' has no associated type named '{wanted}'"
            );
        }

        if (left is not NamedTypeSymbol named) {
            return Error(
                ErrorCode.ERR_NotAType,
                syntax.Right.Span,
                $"'{left.ToDisplayString()}' has no nested types"
            );
        }

        var nested = named.GetMembers(syntax.Right.Identifier.Text).FirstOrDefault();
        if (nested is null) {
            return Error(
                ErrorCode.ERR_TypeNotFound,
                syntax.Right.Span,
                $"'{named.ToDisplayString()}' has no type named "
                + $"'{syntax.Right.Identifier.Text}'"
            );
        }

        return Resolved(syntax.Right, nested, ApplyTypeArguments(nested, syntax.Right.TypeArgumentList, context, scope, syntax.Span));
    }

    /// <summary>
    ///     A generic name written in an expression, <c>Outcome&lt;Int, E&gt;</c>,
    ///     read as the type it names.
    /// </summary>
    internal TypeSymbol BindGenericName(GenericNameSyntax syntax, Symbol context, FileScope? scope) {
        var symbol = Lookup(syntax.Identifier.Text, context, scope, syntax.Identifier.Span);
        return symbol is null
            ? ErrorTypeSymbol.Instance
            : Resolved(syntax, symbol, ApplyTypeArguments(symbol, syntax.TypeArgumentList, context, scope, syntax.Span));
    }

    /// <summary>
    ///     A generic name already found, <c>Deque&lt;Int&gt;</c> in
    ///     <c>Sable.Collections.Deque&lt;Int&gt;()</c>, read as the type it names.
    /// </summary>
    internal TypeSymbol BindGenericName(Symbol found, GenericNameSyntax syntax, Symbol context, FileScope? scope) =>
        Resolved(syntax, found, ApplyTypeArguments(found, syntax.TypeArgumentList, context, scope, syntax.Span));

    TypeSymbol ApplyTypeArguments(
        Symbol symbol,
        TypeArgumentListSyntax? argumentList,
        Symbol context,
        FileScope? scope,
        TextSpan span
    ) {
        var arguments = argumentList is null
            ? ImmutableArray<TypeSymbol>.Empty
            : [..argumentList.Arguments.Select(a => BindType(a, context, scope))];

        switch (symbol) {
            case TypeParameterSymbol typeParameter:
                if (!arguments.IsEmpty) {
                    return Error(
                        ErrorCode.ERR_TypeArgumentsOnNonGeneric,
                        span,
                        $"'{typeParameter.Name}' is a type parameter and takes no type arguments"
                    );
                }

                return typeParameter;

            case AliasSymbol alias: {
                // An alias is transparent: it is the type it names, with its
                // own parameters replaced by the arguments written here.
                // 'alias Result<T> = Outcome<T, AppError>' makes
                // 'Result<Int>' exactly 'Outcome<Int, AppError>'.
                var expected = alias.TypeParameters.Length;

                if (expected == 0) {
                    if (arguments.IsEmpty) {
                        return alias.AliasedType;
                    }

                    return Error(
                        ErrorCode.ERR_TypeArgumentsOnNonGeneric,
                        span,
                        $"'{alias.Name}' is not generic and takes no type arguments"
                    );
                }

                if (arguments.Length != expected) {
                    return Error(
                        ErrorCode.ERR_WrongTypeArgumentCount,
                        span,
                        $"'{alias.Name}' takes {expected} type argument(s), "
                        + $"but {arguments.Length} were given"
                    );
                }

                var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
                for (var i = 0; i < expected; i++) {
                    map[alias.TypeParameters[i]] = arguments[i];
                }

                return new TypeSubstitution(map).Apply(alias.AliasedType);
            }

            case NamedTypeSymbol named: {
                var expected = named.TypeParameters.Length;

                if (arguments.IsEmpty) {
                    if (expected == 0) {
                        return named;
                    }

                    return Error(
                        ErrorCode.ERR_MissingTypeArguments,
                        span,
                        $"'{named.Name}' takes {expected} type argument(s)"
                    );
                }

                if (arguments.Length != expected) {
                    return Error(
                        ErrorCode.ERR_WrongTypeArgumentCount,
                        span,
                        $"'{named.Name}' takes {expected} type argument(s), "
                        + $"but {arguments.Length} were given"
                    );
                }

                // Constraints bind later than this, so the check waits.
                DeferLate(() => CheckTypeArguments(named, arguments, span, context));
                return named.Construct(arguments);
            }

            default:
                return Error(
                    ErrorCode.ERR_NotAType,
                    span,
                    $"'{symbol.Name}' is not a type"
                );
        }
    }

    // -----------------------------------------------------------------------
    // Lookup
    // -----------------------------------------------------------------------

    /// <summary>Finds the symbol a type name refers to, or null.</summary>
    /// <remarks>
    ///     A type hidden from the file — private to another file, internal to
    ///     another module — is not found, but when nothing else answers to the
    ///     name it is reported as hidden (unless <paramref name="report" /> is
    ///     false, for a caller only asking) and returned, so that one mistake
    ///     is one error.
    /// </remarks>
    public Symbol? Lookup(string name, Symbol context, FileScope? scope, TextSpan span, bool report = true) {
        // Enclosing declarations: their type parameters, then their members.
        for (var symbol = context; symbol is not null; symbol = symbol.ContainingSymbol) {
            switch (symbol) {
                case NamedTypeSymbol type:
                    if (Match(type.TypeParameters, name) is { } typeParameter) {
                        return typeParameter;
                    }

                    if (Match(type.AssociatedTypes, name) is { } associated) {
                        return associated;
                    }

                    if (type.SelfTypeParameter is { } self
                        && string.Equals(self.Name, name, StringComparison.Ordinal)) {
                        return self;
                    }

                    if (FindTypeMember(type.GetMembers(name)) is { } nested) {
                        return nested;
                    }

                    break;

                case FunctionSymbol function:
                    if (Match(function.TypeParameters, name) is { } functionParameter) {
                        return functionParameter;
                    }

                    if (FindTypeMember(function.GetLocalTypes(name)) is { } local) {
                        return local;
                    }

                    break;

                case AliasSymbol alias:
                    if (Match(alias.TypeParameters, name) is { } aliasParameter) {
                        return aliasParameter;
                    }

                    break;

                case ExtensionSymbol extension:
                    foreach (var written in extension.TypeParameterAliases) {
                        if (string.Equals(written.Name, name, StringComparison.Ordinal)) {
                            return written;
                        }
                    }

                    break;
            }
        }

        Symbol? hidden = null;
        if (scope is not null) {
            if (FindTypeMember(VisibleFrom(scope, scope.Module.GetMembers(name), ref hidden)) is { } moduleMember) {
                return moduleMember;
            }

            if (scope.ImportedNames.TryGetValue(name, out var imported)) {
                return imported;
            }

            Symbol? fromImports = null;
            foreach (var module in scope.Imports) {
                var candidate = FindTypeMember(VisibleFrom(scope, module.GetMembers(name), ref hidden));
                if (candidate is null) {
                    continue;
                }

                if (fromImports is not null && !ReferenceEquals(fromImports, candidate)) {
                    _diagnostics.Error(
                        ErrorCode.ERR_AmbiguousName,
                        span,
                        $"'{name}' is ambiguous between "
                        + $"'{fromImports.QualifiedName}' and '{candidate.QualifiedName}'"
                    );
                    return fromImports;
                }

                fromImports = candidate;
            }

            if (fromImports is not null) {
                return fromImports;
            }
        }

        // The core module is in scope everywhere.
        if (FindTypeMember(compilation.Core.Module.GetMembers(name), true) is { } core) {
            return core;
        }

        if (hidden is not null && report) {
            ReportHidden(hidden, span);
        }

        return hidden;
    }

    /// <summary>The types and aliases among <paramref name="members" /> a file can see, noting the first it cannot.</summary>
    IEnumerable<Symbol> VisibleFrom(FileScope scope, IEnumerable<Symbol> members, ref Symbol? hidden) {
        var visible = new List<Symbol>();
        foreach (var member in members) {
            if (member is not (NamedTypeSymbol or AliasSymbol)) {
                continue;
            }

            if (Visibility.IsVisible(member, scope, compilation)) {
                visible.Add(member);
            } else {
                hidden ??= member;
            }
        }

        return visible;
    }

    void ReportHidden(Symbol symbol, TextSpan span) =>
        _diagnostics.Error(
            ErrorCode.ERR_NotAccessible,
            span,
            $"'{symbol.Name}' is {Visibility.Describe(symbol, symbol.DeclaredAccessibility)}"
        );

    static TypeParameterSymbol? Match(
        ImmutableArray<TypeParameterSymbol> parameters,
        string name
    ) {
        foreach (var parameter in parameters) {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal)) {
                return parameter;
            }
        }

        return null;
    }

    static Symbol? FindTypeMember(
        IEnumerable<Symbol> named,
        bool publicOnly = false
    ) {
        foreach (var member in named) {
            if (member is not (NamedTypeSymbol or AliasSymbol)) {
                continue;
            }

            if (publicOnly && member.DeclaredAccessibility != Accessibility.Public) {
                continue;
            }

            return member;
        }

        return null;
    }

    /// <summary>
    ///     <c>some</c> and <c>any</c> take a protocol, or several composed.
    /// </summary>
    /// <remarks>
    ///     They also take the whole type after them, so <c>any P[]</c> is an
    ///     existential over an array rather than an array of existentials. Saying
    ///     the constraint is not a protocol is how that reads back to whoever
    ///     wrote it.
    /// </remarks>
    bool RequireProtocol(TypeSymbol constraint, string keyword, TextSpan span) {
        if (constraint.IsError) {
            return false;
        }

        if (constraint is CompositionTypeSymbol composition) {
            var ok = true;
            foreach (var part in composition.Parts) {
                ok &= RequireProtocol(part, keyword, span);
            }

            return ok;
        }

        if (constraint is NamedTypeSymbol { TypeKind: TypeKind.Protocol }) {
            return true;
        }

        if (constraint is TypeParameterSymbol) {
            return true;
        }

        _diagnostics.Error(
            ErrorCode.ERR_NotAProtocol,
            span,
            $"'{keyword}' takes a protocol, and '{constraint.ToDisplayString()}' is not one"
        );
        return false;
    }

    /// <summary>
    ///     <c>A | B | C</c>: the union of its members, normalised
    ///     (<see cref="StructuralUnionTypeSymbol.Create" />).
    /// </summary>
    /// <remarks>
    ///     <c>None</c> as a member is the absence of a value, as
    ///     <c>Optional</c>'s case of that name is: <c>Int | String | None</c>
    ///     is <c>(Int | String)?</c> (docs/03 §3.5), unless a type of that
    ///     name is in scope. A union is closed, so it may not hold
    ///     <c>any P</c>, whose conformers anyone can add to (docs/03 §3.1);
    ///     <c>any Error</c> is the exception, as what a throw of several
    ///     error types widens to.
    /// </remarks>
    TypeSymbol BindUnion(UnionTypeSyntax union, Symbol context, FileScope? scope) {
        var members = new List<TypeSyntax>();
        Collect(union);

        var cases = new List<TypeSymbol>();
        var hasNone = false;
        foreach (var member in members) {
            if (member is SimpleNameTypeSyntax { Identifier.Text: "None", TypeArgumentList: null } none
                && Lookup("None", context, scope, none.Span) is null) {
                hasNone = true;
                continue;
            }

            var type = BindType(member, context, scope);
            if (type is ExistentialTypeSymbol { Constraint: var constraint } && !constraint.Equals(compilation.Core.Error)) {
                type = Error(
                    ErrorCode.ERR_OpenExistentialInUnion,
                    member.Span,
                    $"a union's cases are closed, and '{type.ToDisplayString()}' is not: anything can conform to "
                    + $"'{constraint.ToDisplayString()}'; name the conforming types instead"
                );
            }

            cases.Add(type);
        }

        if (cases.Count == 0) {
            return Error(ErrorCode.ERR_NotAType, union.Span, "a union of only 'None' has no values to hold");
        }

        var result = StructuralUnionTypeSymbol.Create(cases);
        return hasNone && result is not OptionalTypeSymbol ? new OptionalTypeSymbol(result) : result;

        void Collect(TypeSyntax syntax) {
            if (syntax is UnionTypeSyntax pair) {
                Collect(pair.Left);
                Collect(pair.Right);
            } else {
                members.Add(syntax);
            }
        }
    }

    ErrorTypeSymbol Error(ErrorCode code, TextSpan span, string message) {
        _diagnostics.Error(code, span, message);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>The <c>k</c> of <c>T[..k]</c>: a literal count, or none.</summary>
    static int? CapacityOf(ExpressionSyntax? size) =>
        size is LiteralExpressionSyntax literal && int.TryParse(literal.Token.Text.Replace("_", ""), out var count) && count > 0
            ? count
            : null;
}
