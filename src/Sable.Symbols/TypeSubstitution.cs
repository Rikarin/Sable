using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
///     Replaces type parameters with the types they stand for.
/// </summary>
/// <remarks>
///     Needed in two places: reading a member of a constructed type, where
///     <c>Box&lt;Int&gt;.item</c> must be <c>Int</c> rather than <c>T</c>, and
///     calling a generic function, where the arguments decide what its parameters
///     were.
/// </remarks>
public sealed class TypeSubstitution {
    public static readonly TypeSubstitution Empty = new([]);

    /// <summary>Replaces every <c>some P</c> result or local with the type it hides, wherever it occurs.</summary>
    static readonly TypeSubstitution Revealing = new([]) { _reveals = true };

    readonly Dictionary<TypeParameterSymbol, TypeSymbol> _map;
    bool _reveals;

    /// <summary>While revealing, what each <c>some P</c> parameter's type stands for, where known.</summary>
    Func<OpaqueTypeSymbol, TypeSymbol?>? _parameters;

    /// <summary>How many hidden types are being revealed, one inside another: a backstop for results that hide each other.</summary>
    [ThreadStatic] static int s_revealing;

    /// <summary>
    ///     The type with each <c>some P</c> result or local replaced by the
    ///     concrete type behind it, its associated types too: what code
    ///     generation, and only it, may look at. One whose origin hides
    ///     nothing concrete is left as it is.
    /// </summary>
    public static TypeSymbol Reveal(TypeSymbol type) => Revealing.Apply(type);

    /// <summary>
    ///     As <see cref="Reveal(TypeSymbol)" />, and with each <c>some P</c>
    ///     parameter's type replaced by what <paramref name="parameters" />
    ///     says it stands for: the element of a <c>some Sequence</c> parameter's
    ///     iterator is its argument's.
    /// </summary>
    public static TypeSymbol Reveal(TypeSymbol type, Func<OpaqueTypeSymbol, TypeSymbol?> parameters) =>
        new TypeSubstitution([]) { _reveals = true, _parameters = parameters }.Apply(type);

    public TypeSubstitution(Dictionary<TypeParameterSymbol, TypeSymbol> map) {
        _map = map;
    }

    public bool IsEmpty => _map.Count == 0;

    public IEnumerable<TypeParameterSymbol> Parameters => _map.Keys;

    public TypeSymbol? Lookup(TypeParameterSymbol parameter) => _map.TryGetValue(parameter, out var type) ? type : null;

    /// <summary>The substitution a constructed type implies for its members.</summary>
    public static TypeSubstitution For(TypeSymbol type) {
        if (type is not NamedTypeSymbol { IsConstructed: true } named) {
            return Empty;
        }

        var definition = named.OriginalDefinition;
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();

        for (var i = 0;
             i < definition.TypeParameters.Length
             && i < named.TypeArguments.Length;
             i++) {
            map[definition.TypeParameters[i]] = named.TypeArguments[i];
        }

        return new(map);
    }

    public TypeSymbol Apply(TypeSymbol type) {
        if (_map.Count == 0 && !_reveals) {
            return type;
        }

        switch (type) {
            case TypeParameterSymbol parameter:
                return _map.TryGetValue(parameter, out var replacement) ? replacement : type;

            case NamedTypeSymbol named when !named.TypeArguments.IsEmpty: {
                var arguments = named.TypeArguments.Select(Apply).ToImmutableArray();
                return Changed(named.TypeArguments, arguments)
                    ? named.OriginalDefinition.Construct(arguments)
                    : named;
            }

            case ArrayTypeSymbol array: {
                var element = Apply(array.ElementType);

                // 'T[N]': the count an instance gives N, or the parameter it passes on.
                var capacity = array.Capacity;
                var capacityParameter = array.CapacityParameter;
                if (capacityParameter is not null && _map.TryGetValue(capacityParameter, out var count)) {
                    capacity = count is ValueTypeArgumentSymbol value ? (int)value.Value : null;
                    capacityParameter = count as TypeParameterSymbol;
                }

                return element.Equals(array.ElementType) && capacity == array.Capacity && ReferenceEquals(capacityParameter, array.CapacityParameter)
                    ? array
                    : new ArrayTypeSymbol(element, array.ArrayKind, capacity, capacityParameter);
            }

            case OptionalTypeSymbol optional: {
                var underlying = Apply(optional.UnderlyingType);
                return underlying.Equals(optional.UnderlyingType)
                    ? optional
                    : new OptionalTypeSymbol(underlying, optional.IsImplicitlyUnwrapped);
            }

            case ReferenceTypeSymbol reference: {
                var referenced = Apply(reference.ReferencedType);
                return referenced.Equals(reference.ReferencedType)
                    ? reference
                    : new ReferenceTypeSymbol(referenced, reference.RelativeWidth, reference.Pool);
            }

            case TupleTypeSymbol tuple: {
                var elements = tuple.ElementTypes.Select(Apply).ToImmutableArray();
                return Changed(tuple.ElementTypes, elements)
                    ? new TupleTypeSymbol(elements, tuple.ElementNames)
                    : tuple;
            }

            case FunctionTypeSymbol function: {
                var parameters = function.ParameterTypes.Select(Apply).ToImmutableArray();
                var returnType = Apply(function.ReturnType);
                var thrown = function.ThrownType is null ? null : Apply(function.ThrownType);

                return Changed(function.ParameterTypes, parameters)
                    || !returnType.Equals(function.ReturnType)
                    || !Equals(thrown, function.ThrownType)
                        ? new FunctionTypeSymbol(parameters, returnType, function.IsAsync, thrown) {
                            IsRethrows = function.IsRethrows,
                            InOutParameters = function.InOutParameters,
                            EscapingParameters = function.EscapingParameters,
                        }
                        : function;
            }

            case StructuralUnionTypeSymbol union:
                return StructuralUnionTypeSymbol.Create(union.Cases.Select(Apply));

            case CompositionTypeSymbol composition:
                return new CompositionTypeSymbol(composition.Parts.Select(Apply));

            case ExistentialTypeSymbol existential:
                return new ExistentialTypeSymbol(Apply(existential.Constraint));

            case MetatypeTypeSymbol metatype: {
                var instance = Apply(metatype.InstanceType);
                return instance.Equals(metatype.InstanceType) ? metatype : new MetatypeTypeSymbol(instance);
            }

            // A result's opaque type keeps its origin, and learns what the
            // origin's type parameters are here.
            case OpaqueTypeSymbol { Origin: not null, HiddenType: { } hidden } when _reveals && s_revealing < 32:
                s_revealing++;
                try {
                    return Apply(hidden);
                } finally {
                    s_revealing--;
                }

            case OpaqueTypeSymbol { Origin: null } parameter when _reveals && _parameters?.Invoke(parameter) is { } argument:
                return argument;

            case OpaqueTypeSymbol when _reveals:
                return type;

            case OpaqueTypeSymbol opaque:
                return new OpaqueTypeSymbol(
                    Apply(opaque.Constraint),
                    opaque.Origin,
                    opaque.Origin is null ? null : Compose(opaque.Context),
                    [..opaque.Arguments.Select(Apply)]
                );

            case DependentMemberTypeSymbol member: {
                // 'C.Item' with C = WordBag is whatever WordBag says Item is;
                // with C = D, another parameter, it is D.Item.
                var baseType = Apply(member.BaseType);
                return baseType.Equals(member.BaseType)
                    ? member
                    : AssociatedTypes.Member(baseType, member.Associated);
            }

            default:
                return type;
        }
    }

    /// <summary>
    ///     <paramref name="inner" /> followed by this: what it maps is mapped on
    ///     through this, and what it leaves alone this maps itself.
    /// </summary>
    TypeSubstitution Compose(TypeSubstitution inner) {
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>(_map);
        foreach (var (parameter, type) in inner._map) {
            map[parameter] = Apply(type);
        }

        return new(map);
    }

    /// <summary>True when the two map the same parameters to the same types.</summary>
    public bool SameAs(TypeSubstitution other) =>
        ReferenceEquals(this, other)
        || (_map.Count == other._map.Count
            && _map.All(pair => other._map.TryGetValue(pair.Key, out var type) && type.Equals(pair.Value)));

    static bool Changed(
        ImmutableArray<TypeSymbol> before,
        ImmutableArray<TypeSymbol> after
    ) {
        for (var i = 0; i < before.Length; i++) {
            if (!before[i].Equals(after[i])) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Matches a parameter type against an argument type, recording what each
    ///     type parameter must be.
    /// </summary>
    /// <returns>False when the shapes cannot match at all.</returns>
    public static bool Unify(
        TypeSymbol parameter,
        TypeSymbol argument,
        ImmutableArray<TypeParameterSymbol> inferrable,
        Dictionary<TypeParameterSymbol, TypeSymbol> into
    ) {
        switch (parameter) {
            case TypeParameterSymbol typeParameter when inferrable.Contains(typeParameter):
                // An error says nothing about the parameter, and binding it to
                // one would turn a single mistake into a cascade. Nor does a
                // type with an error inside: 'none' with nothing to say what
                // it is optional of is '?', and 'T' is not that.
                if (ContainsError(argument)) {
                    return true;
                }

                if (into.TryGetValue(typeParameter, out var existing)) {
                    return existing.Equals(argument);
                }

                into[typeParameter] = argument;
                return true;

            case NamedTypeSymbol named when argument is NamedTypeSymbol other: {
                if (!ReferenceEquals(named.OriginalDefinition, other.OriginalDefinition)) {
                    return false;
                }

                if (named.TypeArguments.Length != other.TypeArguments.Length) {
                    return false;
                }

                for (var i = 0; i < named.TypeArguments.Length; i++) {
                    if (!Unify(named.TypeArguments[i], other.TypeArguments[i], inferrable, into)) {
                        return false;
                    }
                }

                return true;
            }

            // A slice takes any kind of array, so only the elements say what it is of.
            case ArrayTypeSymbol array when argument is ArrayTypeSymbol otherArray:
                return (array.ArrayKind == otherArray.ArrayKind || array.ArrayKind == ArrayKind.Slice)
                    && Unify(array.ElementType, otherArray.ElementType, inferrable, into);

            case OptionalTypeSymbol optional when argument is OptionalTypeSymbol otherOptional:
                return Unify(optional.UnderlyingType, otherOptional.UnderlyingType, inferrable, into);

            // 'TypeInfo.of(Point.self)': T.Type against Point.Type makes T Point.
            case MetatypeTypeSymbol metatype when argument is MetatypeTypeSymbol otherMetatype:
                return Unify(metatype.InstanceType, otherMetatype.InstanceType, inferrable, into);

            case ReferenceTypeSymbol reference when argument is ReferenceTypeSymbol otherReference:
                return Unify(reference.ReferencedType, otherReference.ReferencedType, inferrable, into);

            case TupleTypeSymbol tuple when argument is TupleTypeSymbol otherTuple: {
                if (tuple.ElementTypes.Length != otherTuple.ElementTypes.Length) {
                    return false;
                }

                for (var i = 0; i < tuple.ElementTypes.Length; i++) {
                    if (!Unify(
                            tuple.ElementTypes[i],
                            otherTuple.ElementTypes[i],
                            inferrable,
                            into
                        )) {
                        return false;
                    }
                }

                return true;
            }

            case FunctionTypeSymbol function when argument is FunctionTypeSymbol otherFunction: {
                if (function.ParameterTypes.Length != otherFunction.ParameterTypes.Length) {
                    return false;
                }

                for (var i = 0; i < function.ParameterTypes.Length; i++) {
                    if (!Unify(
                            function.ParameterTypes[i],
                            otherFunction.ParameterTypes[i],
                            inferrable,
                            into
                        )) {
                        return false;
                    }
                }

                // 'throws(E)' against a closure that throws IoError: E is IoError.
                if (function.ThrownType is { } thrown && otherFunction.ThrownType is { } otherThrown
                    && !Unify(thrown, otherThrown, inferrable, into)) {
                    return false;
                }

                return Unify(function.ReturnType, otherFunction.ReturnType, inferrable, into);
            }

            default:
                // A concrete parameter type matches whatever converts to it;
                // the conversion check that follows decides.
                return true;
        }
    }

    /// <summary>True when a type is, or is built from, <paramref name="parameter" />.</summary>
    public static bool Mentions(TypeSymbol type, TypeParameterSymbol parameter) =>
        type switch {
            TypeParameterSymbol p => ReferenceEquals(p, parameter),
            DependentMemberTypeSymbol member => Mentions(member.BaseType, parameter),
            ArrayTypeSymbol array => Mentions(array.ElementType, parameter),
            OptionalTypeSymbol optional => Mentions(optional.UnderlyingType, parameter),
            ReferenceTypeSymbol reference => Mentions(reference.ReferencedType, parameter),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(e => Mentions(e, parameter)),
            NamedTypeSymbol named => named.TypeArguments.Any(a => Mentions(a, parameter)),
            FunctionTypeSymbol function => function.ParameterTypes.Any(p => Mentions(p, parameter)) || Mentions(function.ReturnType, parameter),
            MetatypeTypeSymbol metatype => Mentions(metatype.InstanceType, parameter),
            _ => false,
        };

    /// <summary>True when a type is, or is built from, an error type.</summary>
    public static bool ContainsError(TypeSymbol type) =>
        type switch {
            { IsError: true } => true,
            NamedTypeSymbol named => named.TypeArguments.Any(ContainsError),
            ArrayTypeSymbol array => ContainsError(array.ElementType),
            OptionalTypeSymbol optional => ContainsError(optional.UnderlyingType),
            MetatypeTypeSymbol metatype => ContainsError(metatype.InstanceType),
            ReferenceTypeSymbol reference => ContainsError(reference.ReferencedType),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(ContainsError),
            FunctionTypeSymbol function => function.ParameterTypes.Any(ContainsError) || ContainsError(function.ReturnType),
            _ => false,
        };
}
