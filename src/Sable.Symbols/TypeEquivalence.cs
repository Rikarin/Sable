using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>A <c>where A == B</c> requirement on a generic declaration.</summary>
public readonly record struct SameTypeRequirement(TypeSymbol Left, TypeSymbol Right);

/// <summary>
///     A <c>where C.Item: P</c> requirement on something that is not a plain
///     type parameter, so cannot live in that parameter's constraint list.
/// </summary>
public readonly record struct ConformanceRequirement(TypeSymbol Subject, TypeSymbol Constraint);

/// <summary>
///     Which generic types a declaration's same-type requirements make one and
///     the same, per docs/03 §3.4: a union-find over type parameters and the
///     associated types read through them.
/// </summary>
/// <remarks>
///     Inside <c>fn f&lt;A: Bag, B: Bag&gt;(…) where A.Item == B.Item</c> an
///     <c>A.Item</c> may go wherever a <c>B.Item</c> is wanted. Each class has
///     one representative, a concrete type when the class has one — so after
///     <c>where T == Int</c> a <c>T</c> is an Int — and otherwise whichever
///     member was named first. <see cref="Canonical" /> rewrites a type to use
///     representatives throughout, so two types are interchangeable exactly
///     when their canonical forms are equal.
/// </remarks>
internal sealed class TypeEquivalence {
    public static readonly TypeEquivalence Empty = new([], []);

    readonly Dictionary<TypeSymbol, TypeSymbol> _parent = [];
    readonly List<ConformanceRequirement> _conformances;

    TypeEquivalence(IEnumerable<SameTypeRequirement> requirements, List<ConformanceRequirement> conformances) {
        foreach (var (left, right) in requirements) {
            Union(left, right);
        }

        _conformances = conformances;
    }

    public static TypeEquivalence For(IEnumerable<SameTypeRequirement> requirements) =>
        For(requirements, []);

    public static TypeEquivalence For(
        IEnumerable<SameTypeRequirement> requirements,
        IEnumerable<ConformanceRequirement> conformances
    ) {
        var sameTypes = requirements.ToList();
        var extra = conformances.ToList();
        return sameTypes.Count == 0 && extra.Count == 0 ? Empty : new TypeEquivalence(sameTypes, extra);
    }

    /// <summary>The requirements in force inside a symbol, of both kinds.</summary>
    public static TypeEquivalence ForSymbol(Symbol? symbol) =>
        For(RequirementsOf(symbol), ConformanceRequirementsOf(symbol));

    /// <summary>True when the same-type requirements say nothing.</summary>
    public bool IsEmpty => _parent.Count == 0;

    /// <summary>
    ///     What a <c>where</c> clause adds to a type's constraints:
    ///     <c>where C.Item: Comparable</c> makes a <c>C.Item</c> comparable.
    /// </summary>
    public IEnumerable<TypeSymbol> ExtraConstraints(TypeSymbol type) {
        if (_conformances.Count == 0) {
            return [];
        }

        var canonical = Canonical(type);
        return _conformances
            .Where(r => Canonical(r.Subject).Equals(canonical))
            .Select(static r => r.Constraint);
    }

    public static IEnumerable<ConformanceRequirement> ConformanceRequirementsOf(Symbol? symbol) {
        for (; symbol is not null; symbol = symbol.ContainingSymbol) {
            var own = symbol switch {
                FunctionSymbol function => function.ConformanceRequirements,
                NamedTypeSymbol type => type.ConformanceRequirements,
                _ => [],
            };

            foreach (var requirement in own) {
                yield return requirement;
            }
        }
    }

    /// <summary>The requirements in force inside a symbol: its own and every enclosing declaration's.</summary>
    public static IEnumerable<SameTypeRequirement> RequirementsOf(Symbol? symbol) {
        for (; symbol is not null; symbol = symbol.ContainingSymbol) {
            var own = symbol switch {
                FunctionSymbol function => function.SameTypeRequirements,
                NamedTypeSymbol type => type.SameTypeRequirements,
                _ => [],
            };

            foreach (var requirement in own) {
                yield return requirement;
            }
        }
    }

    TypeSymbol Find(TypeSymbol type) {
        if (!_parent.TryGetValue(type, out var parent)) {
            return type;
        }

        if (parent.Equals(type)) {
            return type;
        }

        var root = Find(parent);
        _parent[type] = root;
        return root;
    }

    void Union(TypeSymbol left, TypeSymbol right) {
        if (left.IsError || right.IsError) {
            return;
        }

        var a = Find(left);
        var b = Find(right);
        _parent.TryAdd(a, a);
        _parent.TryAdd(b, b);

        if (a.Equals(b)) {
            return;
        }

        // A concrete type wins, so the class reads as that type.
        if (AssociatedTypes.IsAbstract(a) && !AssociatedTypes.IsAbstract(b)) {
            _parent[a] = b;
        } else {
            _parent[b] = a;
        }
    }

    /// <summary>The type with every part replaced by its class's representative.</summary>
    public TypeSymbol Canonical(TypeSymbol type) {
        if (IsEmpty) {
            return type;
        }

        var rewritten = type switch {
            // 'B.Item.Element' with B.Item == A.Item reads through A.Item.
            DependentMemberTypeSymbol member when Canonical(member.BaseType) is var baseType
                                                  && !baseType.Equals(member.BaseType) =>
                AssociatedTypes.Member(baseType, member.Associated),

            NamedTypeSymbol { IsConstructed: true } named =>
                named.OriginalDefinition.Construct([..named.TypeArguments.Select(Canonical)]),

            OptionalTypeSymbol optional => new OptionalTypeSymbol(Canonical(optional.UnderlyingType), optional.IsImplicitlyUnwrapped),

            ArrayTypeSymbol array => new ArrayTypeSymbol(Canonical(array.ElementType), array.ArrayKind),

            ReferenceTypeSymbol reference =>
                new ReferenceTypeSymbol(Canonical(reference.ReferencedType), reference.RelativeWidth, reference.Pool),

            TupleTypeSymbol tuple =>
                new TupleTypeSymbol([..tuple.ElementTypes.Select(Canonical)], tuple.ElementNames),

            FunctionTypeSymbol function =>
                new FunctionTypeSymbol(
                    [..function.ParameterTypes.Select(Canonical)],
                    Canonical(function.ReturnType),
                    function.IsAsync,
                    function.ThrownType is null ? null : Canonical(function.ThrownType)
                ) { IsRethrows = function.IsRethrows },

            StructuralUnionTypeSymbol union => StructuralUnionTypeSymbol.Create(union.Cases.Select(Canonical)),

            _ => type,
        };

        return Find(rewritten);
    }

    /// <summary>True when the requirements make the two types the same.</summary>
    public bool AreSame(TypeSymbol left, TypeSymbol right) =>
        left.Equals(right) || Canonical(left).Equals(Canonical(right));
}
