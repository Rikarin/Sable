using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Rejects a value type that contains itself, which would need infinite
///     storage.
/// </summary>
/// <remarks>
///     A struct is laid out inline (docs/02 §2.1), and so is everything it
///     holds by value: other structs, tuples, optionals. A path through those
///     back to the struct it started from is an infinite size. A class
///     reference, an array or a union breaks the chain — a class and an
///     array's storage are elsewhere, and a union boxes a recursive case
///     automatically (docs/03 §3.3).
/// </remarks>
internal static class LayoutChecker {
    public static void Check(NamedTypeSymbol type, DiagnosticBag diagnostics) {
        if (type.TypeKind != TypeKind.Struct) {
            return;
        }

        var path = new List<string>();
        var visiting = new HashSet<NamedTypeSymbol>();

        foreach (var field in StoredFields(type)) {
            path.Add($"{type.Name}.{field.Name}");
            if (Reaches(field.Type, type.OriginalDefinition, path, visiting)) {
                diagnostics.Add(Diagnostic.Error(
                    ErrorCode.ERR_InfiniteSize,
                    field.Location ?? type.Location ?? default,
                    $"'{type.Name}' contains itself by value ({string.Join(" → ", path)} → {type.Name}), "
                    + "so it would have no finite size; hold it through a class, an array, "
                    + "or a union case instead"
                ).AnchoredAt(field.DeclaringSyntax.FirstOrDefault() ?? type.DeclaringSyntax.FirstOrDefault()));
                return;
            }

            path.RemoveAt(path.Count - 1);
        }
    }

    static IEnumerable<FieldSymbol> StoredFields(NamedTypeSymbol type) =>
        type.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false });

    /// <summary>True when a value of <paramref name="type" /> holds a <paramref name="target" /> inline.</summary>
    static bool Reaches(
        TypeSymbol type,
        NamedTypeSymbol target,
        List<string> path,
        HashSet<NamedTypeSymbol> visiting
    ) {
        switch (type) {
            case OptionalTypeSymbol optional:
                return Reaches(optional.UnderlyingType, target, path, visiting);

            case TupleTypeSymbol tuple:
                return tuple.ElementTypes.Any(e => Reaches(e, target, path, visiting));

            case NamedTypeSymbol { TypeKind: TypeKind.Struct } named: {
                if (ReferenceEquals(named.OriginalDefinition, target)) {
                    return true;
                }

                // A cycle that does not pass through the target is some other
                // struct's problem, and is reported there.
                if (!visiting.Add(named.OriginalDefinition)) {
                    return false;
                }

                var substitution = TypeSubstitution.For(named);
                foreach (var field in StoredFields(named.OriginalDefinition)) {
                    path.Add($"{named.Name}.{field.Name}");
                    if (Reaches(substitution.Apply(field.Type), target, path, visiting)) {
                        return true;
                    }

                    path.RemoveAt(path.Count - 1);
                }

                visiting.Remove(named.OriginalDefinition);
                return false;
            }

            default:
                return false;
        }
    }
}
