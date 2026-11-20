using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
///     Reading a protocol's associated types through a generic type: what
///     <c>C.Item</c> names, and what it becomes once <c>C</c> is known.
/// </summary>
internal static class AssociatedTypes {
    /// <summary>
    ///     A protocol's associated types together with those of every protocol it
    ///     refines, since <c>C: BidirectionalCollection</c> also has an Element.
    /// </summary>
    public static IEnumerable<TypeParameterSymbol> All(NamedTypeSymbol protocol) {
        var seen = new HashSet<NamedTypeSymbol>();
        var pending = new Stack<NamedTypeSymbol>();
        pending.Push(protocol);

        while (pending.Count > 0) {
            var current = pending.Pop();
            if (!seen.Add(current.OriginalDefinition)) {
                continue;
            }

            foreach (var associated in current.AssociatedTypes) {
                yield return associated;
            }

            foreach (var refined in current.BaseTypes) {
                if (refined is NamedTypeSymbol { TypeKind: TypeKind.Protocol } parent) {
                    pending.Push(parent);
                }
            }
        }
    }

    /// <summary>Every protocol a protocol refines, through every level, by its definition: not itself.</summary>
    public static IEnumerable<NamedTypeSymbol> RefinedProtocols(NamedTypeSymbol protocol) {
        var seen = new HashSet<NamedTypeSymbol> { protocol.OriginalDefinition };
        var pending = new Stack<NamedTypeSymbol>();
        pending.Push(protocol.OriginalDefinition);

        while (pending.Count > 0) {
            foreach (var refined in pending.Pop().BaseTypes) {
                if (refined is NamedTypeSymbol { TypeKind: TypeKind.Protocol } parent && seen.Add(parent.OriginalDefinition)) {
                    yield return parent.OriginalDefinition;
                    pending.Push(parent.OriginalDefinition);
                }
            }
        }
    }

    /// <summary>The associated type called <paramref name="name" /> that these constraints provide, or null.</summary>
    public static TypeParameterSymbol? Find(IEnumerable<TypeSymbol> constraints, string name) {
        foreach (var constraint in constraints) {
            var parts = constraint is CompositionTypeSymbol composition ? composition.Parts : [constraint];

            foreach (var part in parts) {
                if (part is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                    continue;
                }

                foreach (var associated in All(protocol)) {
                    if (string.Equals(associated.Name, name, StringComparison.Ordinal)) {
                        return associated;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>True for the implicit <c>Self</c> of a protocol.</summary>
    public static bool IsProtocolSelf(TypeSymbol type) =>
        type is TypeParameterSymbol parameter
        && parameter.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol
        && ReferenceEquals(protocol.SelfTypeParameter, parameter);

    /// <summary>True for a type whose associated types can only be named, not resolved.</summary>
    public static bool IsAbstract(TypeSymbol type) =>
        type is TypeParameterSymbol or DependentMemberTypeSymbol;

    /// <summary>
    ///     What a generic type is known to conform to: a parameter's declared
    ///     constraints, or an associated type's, read through its base.
    /// </summary>
    public static ImmutableArray<TypeSymbol> ConstraintsOf(TypeSymbol type, TypeEquivalence? context) {
        var own = ConstraintsOf(type);
        return context is null ? own : [..own, ..context.ExtraConstraints(type)];
    }

    public static ImmutableArray<TypeSymbol> ConstraintsOf(TypeSymbol type) {
        switch (type) {
            // A protocol's Self is whatever conforms to it.
            case TypeParameterSymbol parameter when IsProtocolSelf(parameter):
                return [(NamedTypeSymbol)parameter.ContainingSymbol!, ..parameter.Constraints];

            case TypeParameterSymbol parameter:
                return parameter.Constraints;

            // 'some Sequence<Int>' is a Sequence, and what its arguments say.
            case OpaqueTypeSymbol opaque:
                return [opaque.Constraint];

            case DependentMemberTypeSymbol member: {
                var constraints = member.Associated.Constraints;
                if (constraints.IsEmpty
                    || member.Associated.ContainingSymbol is not NamedTypeSymbol protocol) {
                    return constraints;
                }

                // 'associatedtype Slice: Collection<Element>' speaks of the
                // protocol's own Element; through C it is C.Element.
                var substitution = ThroughBase(protocol, member.BaseType);
                return [..constraints.Select(substitution.Apply)];
            }

            default:
                return [];
        }
    }

    /// <summary>
    ///     The substitution that reads a protocol's own <c>Self</c> and
    ///     associated types through a generic base.
    /// </summary>
    public static TypeSubstitution ThroughBase(NamedTypeSymbol protocol, TypeSymbol baseType) {
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        if (protocol.SelfTypeParameter is { } self) {
            map[self] = baseType;
        }

        foreach (var associated in All(protocol)) {
            map[associated] = Member(baseType, associated);
        }

        return new(map);
    }

    /// <summary>
    ///     <see cref="ThroughBase(NamedTypeSymbol, TypeSymbol)" /> for reading
    ///     one type: only the associated types it mentions are resolved. A
    ///     concrete conformer's associated type can be inferred from another
    ///     one, which resolving every one of them for each would ask again and
    ///     again, many times over for a type that does not say them.
    /// </summary>
    public static TypeSubstitution ThroughBase(NamedTypeSymbol protocol, TypeSymbol baseType, TypeSymbol reading) {
        var mentioned = new HashSet<TypeParameterSymbol>();
        if (!Mentioned(reading, mentioned)) {
            return ThroughBase(protocol, baseType);
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        if (protocol.SelfTypeParameter is { } self) {
            map[self] = baseType;
        }

        foreach (var associated in All(protocol)) {
            if (mentioned.Contains(associated) && !map.ContainsKey(associated)) {
                map[associated] = Member(baseType, associated);
            }
        }

        return new(map);

        // The type parameters a type is made of, or false for a kind of type
        // this does not take apart.
        static bool Mentioned(TypeSymbol type, HashSet<TypeParameterSymbol> found) =>
            type switch {
                TypeParameterSymbol parameter => found.Add(parameter) || true,
                DependentMemberTypeSymbol member => Mentioned(member.BaseType, found),
                NamedTypeSymbol named => named.TypeArguments.All(t => Mentioned(t, found)),
                ArrayTypeSymbol array => Mentioned(array.ElementType, found)
                                         && (array.CapacityParameter is not { } capacity || found.Add(capacity) || true),
                OptionalTypeSymbol optional => Mentioned(optional.UnderlyingType, found),
                ReferenceTypeSymbol reference => Mentioned(reference.ReferencedType, found),
                TupleTypeSymbol tuple => tuple.ElementTypes.All(t => Mentioned(t, found)),
                FunctionTypeSymbol function => function.ParameterTypes.All(t => Mentioned(t, found))
                                               && Mentioned(function.ReturnType, found)
                                               && (function.ThrownType is null || Mentioned(function.ThrownType, found)),
                MetatypeTypeSymbol metatype => Mentioned(metatype.InstanceType, found),
                _ => type.IsError,
            };
    }

    /// <summary>
    ///     <c>Base.Associated</c>: resolved when the base is a concrete conformer,
    ///     and kept as a dependent member while it is still generic.
    /// </summary>
    public static TypeSymbol Member(TypeSymbol baseType, TypeParameterSymbol associated) {
        if (baseType.IsError) {
            return baseType;
        }

        // Inside a protocol, 'Self.Item' is just 'Item'.
        if (IsProtocolSelf(baseType)) {
            return associated;
        }

        // 'some Sequence<Int>' says its Element is an Int.
        if (baseType is OpaqueTypeSymbol opaque && opaque.ArgumentFor(associated) is { } argument) {
            return argument;
        }

        if (baseType is NamedTypeSymbol { TypeKind: not TypeKind.Protocol } conformer
            && ConformanceChecker.ResolveAssociated(conformer, associated) is { } bound) {
            // 'alias Item = T' names the conformer's own parameter, so
            // Tagged<String> binds Item to String.
            return TypeSubstitution.For(conformer).Apply(bound);
        }

        // An array says what an associated type is in the extension that
        // conforms it: 'alias Iter = ArrayIterator<Element>', its element
        // read in.
        if (baseType is ArrayTypeSymbol array
            && associated.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol
            && Compilation.ArrayConformance(array, protocol) is var (carrier, substitution)
            && ConformanceChecker.ResolveAssociated(carrier, associated) is { } given) {
            return substitution.Apply(given);
        }

        return new DependentMemberTypeSymbol(baseType, associated);
    }
}
