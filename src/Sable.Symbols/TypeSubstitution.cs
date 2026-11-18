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

    readonly Dictionary<TypeParameterSymbol, TypeSymbol> _map;

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
        if (_map.Count == 0) {
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
                return element.Equals(array.ElementType)
                    ? array
                    : new ArrayTypeSymbol(element, array.ArrayKind);
            }

            case OptionalTypeSymbol optional: {
                var underlying = Apply(optional.UnderlyingType);
                return underlying.Equals(optional.UnderlyingType)
                    ? optional
                    : new OptionalTypeSymbol(underlying);
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
                            IsRethrows = function.IsRethrows
                        }
                        : function;
            }

            case StructuralUnionTypeSymbol union:
                return new StructuralUnionTypeSymbol(union.Cases.Select(Apply));

            case CompositionTypeSymbol composition:
                return new CompositionTypeSymbol(composition.Parts.Select(Apply));

            case ExistentialTypeSymbol existential:
                return new ExistentialTypeSymbol(Apply(existential.Constraint));

            case OpaqueTypeSymbol opaque:
                return new OpaqueTypeSymbol(Apply(opaque.Constraint));

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
                // one would turn a single mistake into a cascade.
                if (argument.IsError) {
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

            case ArrayTypeSymbol array when argument is ArrayTypeSymbol otherArray:
                return array.ArrayKind == otherArray.ArrayKind
                    && Unify(array.ElementType, otherArray.ElementType, inferrable, into);

            case OptionalTypeSymbol optional when argument is OptionalTypeSymbol otherOptional:
                return Unify(optional.UnderlyingType, otherOptional.UnderlyingType, inferrable, into);

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
}
