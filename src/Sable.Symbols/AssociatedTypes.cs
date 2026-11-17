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
    public static ImmutableArray<TypeSymbol> ConstraintsOf(TypeSymbol type) {
        switch (type) {
            case TypeParameterSymbol parameter:
                return parameter.Constraints;

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

        if (baseType is NamedTypeSymbol { TypeKind: not TypeKind.Protocol } conformer
            && ConformanceChecker.ResolveAssociated(conformer, associated) is { } bound) {
            // 'alias Item = T' names the conformer's own parameter, so
            // Tagged<String> binds Item to String.
            return TypeSubstitution.For(conformer).Apply(bound);
        }

        return new DependentMemberTypeSymbol(baseType, associated);
    }
}
