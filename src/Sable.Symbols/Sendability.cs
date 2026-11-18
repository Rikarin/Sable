namespace Sable.Symbols;

/// <summary>
///     Which values may cross an isolation boundary — into an actor, or out
///     of one — without two tasks coming to share something either could
///     change (SYNTAX.md §19, docs/02 §2.7).
/// </summary>
/// <remarks>
///     <c>Sendable</c> is met by what a type is made of: a value whose parts
///     are all Sendable, an actor, a class declared <c>@immutable</c>. A class
///     otherwise is shared mutable state; a closure may capture some; a
///     reference or a region is tied to where it was made. A type parameter
///     is Sendable when it is constrained to be.
/// </remarks>
internal static class Sendability {
    /// <summary>True for the core's 'Sendable' protocol.</summary>
    public static bool IsSendableProtocol(TypeSymbol type) =>
        type is NamedTypeSymbol { Name: "Sendable", TypeKind: TypeKind.Protocol, ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } };

    public static bool IsSendable(TypeSymbol type) => Why(type, []) is null;

    /// <summary>Why a type is not Sendable — the part of it that is not — or null when it is.</summary>
    public static string? Why(TypeSymbol type, HashSet<TypeSymbol> seen) {
        switch (type) {
            case ErrorTypeSymbol:
                return null;

            case FunctionTypeSymbol:
                return "a closure may capture state that is not";

            case ReferenceTypeSymbol:
                return "a reference refers into storage where it was made";

            case ArrayTypeSymbol array:
                return Why(array.ElementType, seen);

            case OptionalTypeSymbol optional:
                return Why(optional.UnderlyingType, seen);

            case TupleTypeSymbol tuple:
                return tuple.ElementTypes.Select(e => Why(e, seen)).FirstOrDefault(static w => w is not null);

            case StructuralUnionTypeSymbol union:
                return union.Cases.Select(c => Why(c, seen)).FirstOrDefault(static w => w is not null);

            case TypeParameterSymbol parameter:
                return parameter.Constraints.Any(c => IsSendableProtocol(c) || c is not TypeParameterSymbol && Why(c, seen) is null)
                    ? null
                    : $"'{parameter.Name}' is not constrained to be Sendable";

            case ExistentialTypeSymbol or OpaqueTypeSymbol: {
                var constraint = type is ExistentialTypeSymbol existential ? existential.Constraint : ((OpaqueTypeSymbol)type).Constraint;
                return Refines(constraint, []) ? null : $"'{constraint.ToDisplayString()}' does not refine Sendable";
            }

            case NamedTypeSymbol named:
                return Named(named, seen);

            default:
                return null;
        }
    }

    static string? Named(NamedTypeSymbol type, HashSet<TypeSymbol> seen) {
        switch (type.TypeKind) {
            case TypeKind.Actor:
                return null;

            case TypeKind.Builtin:
                return type.Name == "Region" ? "a region's allocations are tied to the task that made it" : null;

            case TypeKind.Protocol:
                return Refines(type, []) ? null : $"'{type.ToDisplayString()}' does not refine Sendable";

            case TypeKind.Class:
                return Has(type.OriginalDefinition, "immutable") || Has(type.OriginalDefinition, "MainActor")
                    ? null
                    : $"'{type.ToDisplayString()}' is a class, whose state is shared; make it '@immutable', '@MainActor', or an actor";
        }

        // A value: Sendable when what it holds is. A type that holds itself,
        // through an indirect case, is as Sendable as the rest of it.
        if (!seen.Add(type)) {
            return null;
        }

        var substitution = type.IsConstructed ? TypeSubstitution.For(type) : TypeSubstitution.Empty;
        foreach (var member in type.OriginalDefinition.Members) {
            var held = member switch {
                FieldSymbol { IsStatic: false, IsComputed: false } field => [field.Type],
                UnionCaseSymbol unionCase => unionCase.Payload.Select(static p => p.Type),
                _ => [],
            };

            foreach (var part in held) {
                if (Why(substitution.Apply(part), seen) is { } reason) {
                    return $"'{type.ToDisplayString()}' holds '{member.Name}', and {reason}";
                }
            }
        }

        // The core's generic values hold their arguments.
        if (type.IsConstructed && type.OriginalDefinition.Members.Count == 0) {
            return type.TypeArguments.Select(a => Why(a, seen)).FirstOrDefault(static w => w is not null);
        }

        return null;
    }

    /// <summary>A protocol that is Sendable, or refines it.</summary>
    static bool Refines(TypeSymbol constraint, HashSet<TypeSymbol> seen) =>
        constraint switch {
            _ when IsSendableProtocol(constraint) => true,
            CompositionTypeSymbol composition => composition.Parts.Any(p => Refines(p, seen)),
            NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol when seen.Add(protocol) => protocol.BaseTypes.Any(b => Refines(b, seen)),
            _ => false,
        };

    /// <summary>
    ///     A class declared '@immutable' has no state to race on; one declared
    ///     '@MainActor' keeps its state where only the main actor reaches it.
    /// </summary>
    static bool Has(NamedTypeSymbol type, string attribute) =>
        type.DeclaringSyntax.Any(s => s is Sable.Syntax.ClassDeclarationSyntax { Attributes: var attributes }
            && attributes.Any(a => a.Name.ToString().Trim() == attribute));
}
