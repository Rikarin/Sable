using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Warns of classes that hold one another strongly (docs/02 §2.2): a
///     type-level strong-reference graph, whose cycles through two or more
///     classes are reported, each once, with the way round printed.
/// </summary>
/// <remarks>
///     <para>
///         An edge runs from a class to each class one of its stored fields
///         holds strongly — directly, or inside an optional, a tuple, an array,
///         a struct (field by field) or a union's payload. A <c>weak</c> or
///         <c>unowned</c> field is no edge, which is how such a cycle is
///         broken. A protocol, a closure or a type parameter could hold
///         anything, and is no edge either: the check is not sound — the shape
///         objects are linked in at run time is what decides — but it catches
///         the parent that holds its children and the child that holds its
///         parent strongly every time.
///     </para>
///     <para>
///         A class that holds its own kind — a list's <c>next</c>, a tree's
///         children — is not reported: such links usually run one way, and
///         the objects they make form no cycle.
///     </para>
/// </remarks>
internal static class CycleChecker {
    public static void Check(NamedTypeSymbol type, DiagnosticBag diagnostics) {
        if (!IsClass(type) || !ReferenceEquals(type, type.OriginalDefinition)) {
            return;
        }

        // The classes that reach this one and that it reaches: the strongly
        // connected set it is in, which is reported once, by its first member.
        var reached = Reachable(type);
        if (!reached.Contains(type)) {
            return;
        }

        var members = reached.Where(other => !ReferenceEquals(other, type) && Reachable(other).Contains(type)).ToList();
        if (members.Count == 0 || members.Any(other => Order(other, type) < 0)) {
            return;
        }

        if (ShortestWayRound(type) is not { } cycle) {
            return;
        }

        var path = string.Join(" → ", cycle.Select(static step => $"{step.From.Name}.{step.Field.Name}")) + $" → {type.Name}";
        // At the field, unless the class inherits it: then at the class.
        var at = ReferenceEquals(cycle[0].Field.ContainingSymbol, type) && cycle[0].Field.Location is not null ? cycle[0].Field : (Symbol)type;
        diagnostics.Add(Diagnostic.Warning(
            ErrorCode.WRN_StrongReferenceCycle,
            at.Location ?? default,
            $"'{type.Name}' is in a strong reference cycle ({path}): objects linked this way keep one another alive "
            + "and are never freed; make one of these references 'weak' or 'unowned'"
        ).AnchoredAt(at.DeclaringSyntax.FirstOrDefault()));
    }

    static bool IsClass(TypeSymbol type) => type.TypeKind is TypeKind.Class or TypeKind.Actor;

    /// <summary>Which of two classes a cycle is reported at: the one declared first (in a file of their own, the place is all a symbol knows), then by name.</summary>
    static int Order(NamedTypeSymbol a, NamedTypeSymbol b) {
        var byPlace = (a.Location?.Start ?? 0).CompareTo(b.Location?.Start ?? 0);
        return byPlace != 0 ? byPlace : string.CompareOrdinal($"{a.ContainingSymbol?.Name}.{a.Name}", $"{b.ContainingSymbol?.Name}.{b.Name}");
    }

    /// <summary>The classes a class's objects hold strongly, step by step: every class reachable, itself included only by a way through another.</summary>
    static HashSet<NamedTypeSymbol> Reachable(NamedTypeSymbol start) {
        var reached = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<NamedTypeSymbol>();
        foreach (var (_, target) in Edges(start).Where(edge => !ReferenceEquals(edge.Target, start))) {
            if (reached.Add(target)) {
                pending.Enqueue(target);
            }
        }

        while (pending.TryDequeue(out var next)) {
            foreach (var (_, target) in Edges(next)) {
                if (reached.Add(target)) {
                    pending.Enqueue(target);
                }
            }
        }

        return reached;
    }

    /// <summary>The fewest fields that lead from a class back to itself through another class; null when none do.</summary>
    static List<(NamedTypeSymbol From, FieldSymbol Field)>? ShortestWayRound(NamedTypeSymbol start) {
        var cameFrom = new Dictionary<NamedTypeSymbol, (NamedTypeSymbol From, FieldSymbol Field)>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<NamedTypeSymbol>();
        foreach (var (field, target) in Edges(start)) {
            if (!ReferenceEquals(target, start) && cameFrom.TryAdd(target, (start, field))) {
                pending.Enqueue(target);
            }
        }

        while (pending.TryDequeue(out var next)) {
            foreach (var (field, target) in Edges(next)) {
                if (ReferenceEquals(target, start)) {
                    var cycle = new List<(NamedTypeSymbol From, FieldSymbol Field)> { (next, field) };
                    for (var at = next; !ReferenceEquals(at, start);) {
                        var step = cameFrom[at];
                        cycle.Insert(0, step);
                        at = step.From;
                    }

                    return cycle;
                }

                if (cameFrom.TryAdd(target, (next, field))) {
                    pending.Enqueue(target);
                }
            }
        }

        return null;
    }

    /// <summary>The classes a class's stored fields, its superclasses' included, hold strongly, each with the field.</summary>
    static IEnumerable<(FieldSymbol Field, NamedTypeSymbol Target)> Edges(NamedTypeSymbol type) {
        var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var current = type;
        while (current is not null && seen.Add(current.OriginalDefinition)) {
            var named = current;
            current = named.BaseTypes.OfType<NamedTypeSymbol>().FirstOrDefault(IsClass);
            var substitution = TypeSubstitution.For(named);
            foreach (var field in StoredFields(named.OriginalDefinition)) {
                if (field.ReferenceKind != ReferenceKind.Strong) {
                    continue;
                }

                var targets = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
                Held(substitution.Apply(field.Type), targets, []);
                foreach (var target in targets) {
                    yield return (field, target);
                }
            }
        }
    }

    static IEnumerable<FieldSymbol> StoredFields(NamedTypeSymbol type) =>
        type.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false });

    /// <summary>The classes a value of a type holds strongly, without going through another object.</summary>
    static void Held(TypeSymbol type, HashSet<NamedTypeSymbol> classes, HashSet<NamedTypeSymbol> visiting) {
        switch (type) {
            case OptionalTypeSymbol optional:
                Held(optional.UnderlyingType, classes, visiting);
                break;

            case TupleTypeSymbol tuple:
                foreach (var element in tuple.ElementTypes) {
                    Held(element, classes, visiting);
                }

                break;

            case ArrayTypeSymbol array:
                Held(array.ElementType, classes, visiting);
                break;

            case StructuralUnionTypeSymbol union:
                foreach (var @case in union.Cases) {
                    Held(@case, classes, visiting);
                }

                break;

            case NamedTypeSymbol named when IsClass(named):
                classes.Add(named.OriginalDefinition);
                break;

            case NamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Union } named when visiting.Add(named.OriginalDefinition): {
                var substitution = TypeSubstitution.For(named);
                foreach (var field in StoredFields(named.OriginalDefinition).Where(static f => f.ReferenceKind == ReferenceKind.Strong)) {
                    Held(substitution.Apply(field.Type), classes, visiting);
                }

                foreach (var @case in named.OriginalDefinition.Members.OfType<UnionCaseSymbol>()) {
                    foreach (var payload in @case.Payload) {
                        Held(substitution.Apply(payload.Type), classes, visiting);
                    }
                }

                visiting.Remove(named.OriginalDefinition);
                break;
            }
        }
    }
}
