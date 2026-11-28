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
///     <para>
///         A protocol's own requirements hold wherever it constrains a type:
///         <c>protocol Sequence { associatedtype Iter: Iterator where Iter.Element == Element }</c>
///         makes an <c>S.Iter.Element</c> an <c>S.Element</c> for every
///         <c>S: Sequence</c>. There may be no end to those (a protocol may
///         constrain an associated type by itself: <c>S.Slice.Slice…</c>), so
///         they are read in as types are met: the first time a type is read
///         through, its protocols' requirements are added, read through it.
///     </para>
/// </remarks>
internal sealed class TypeEquivalence {
    /// <summary>No requirements of a declaration's own: only what its types' protocols bring.</summary>
    /// <remarks>A new one each time, since what the protocols bring is read in as it is met.</remarks>
    public static TypeEquivalence Empty => new([], []);

    readonly Dictionary<TypeSymbol, TypeSymbol> _parent = [];
    readonly List<ConformanceRequirement> _conformances;

    // The generic types whose protocols' requirements have been read in.
    readonly HashSet<TypeSymbol> _expanded = [];

    // How deep reading in has gone: what one protocol brings in may be read
    // through in turn, and a recursive constraint would go on for ever.
    int _expanding;
    const int MaxExpansionDepth = 8;

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
    ) =>
        new(requirements, conformances.ToList());

    /// <summary>The requirements in force inside a symbol, of both kinds.</summary>
    public static TypeEquivalence ForSymbol(Symbol? symbol) =>
        For(RequirementsOf(symbol), ConformanceRequirementsOf(symbol));

    /// <summary>
    ///     What a <c>where</c> clause adds to a type's constraints:
    ///     <c>where C.Item: Comparable</c> makes a <c>C.Item</c> comparable.
    /// </summary>
    public IEnumerable<TypeSymbol> ExtraConstraints(TypeSymbol type) {
        // Reading it brings in what its base's protocols require of it.
        var canonical = Canonical(type);
        if (_conformances.Count == 0) {
            return [];
        }

        return _conformances
            .Where(r => Canonical(r.Subject).Equals(canonical))
            .Select(static r => r.Constraint);
    }

    public static IEnumerable<ConformanceRequirement> ConformanceRequirementsOf(Symbol? symbol) {
        for (; symbol is not null; symbol = symbol.ContainingSymbol) {
            var own = symbol switch {
                FunctionSymbol function => function.ConformanceRequirements,
                NamedTypeSymbol type => type.ConformanceRequirements,
                ExtensionSymbol extension => extension.ConformanceRequirements,
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
                ExtensionSymbol extension => extension.SameTypeRequirements,
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

        // A concrete type wins, so the class reads as that type; of two
        // generic ones the shorter, so an S.Iter.Element reads as S.Element.
        var aAbstract = AssociatedTypes.IsAbstract(a);
        var bAbstract = AssociatedTypes.IsAbstract(b);
        if (aAbstract && (!bAbstract || Depth(b) < Depth(a))) {
            _parent[a] = b;
        } else {
            _parent[b] = a;
        }
    }

    /// <summary>How many associated types a type is read through: S is 0, S.Iter.Element 2.</summary>
    static int Depth(TypeSymbol type) =>
        type is DependentMemberTypeSymbol member ? 1 + Depth(member.BaseType) : 0;

    /// <summary>
    ///     True when no requirement could make the type another: it mentions
    ///     no associated type, read through a parameter or named inside its
    ///     protocol.
    /// </summary>
    static bool MentionsNoAssociatedType(TypeSymbol type) =>
        type switch {
            DependentMemberTypeSymbol => false,
            TypeParameterSymbol parameter => parameter.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol },
            NamedTypeSymbol named => named.TypeArguments.All(MentionsNoAssociatedType),
            OptionalTypeSymbol optional => MentionsNoAssociatedType(optional.UnderlyingType),
            ArrayTypeSymbol array => MentionsNoAssociatedType(array.ElementType),
            TupleTypeSymbol tuple => tuple.ElementTypes.All(MentionsNoAssociatedType),
            FunctionTypeSymbol function => function.ParameterTypes.All(MentionsNoAssociatedType) && MentionsNoAssociatedType(function.ReturnType),
            ReferenceTypeSymbol reference => MentionsNoAssociatedType(reference.ReferencedType),
            MetatypeTypeSymbol metatype => MentionsNoAssociatedType(metatype.InstanceType),
            _ => true,
        };

    /// <summary>
    ///     Reads in the requirements of the protocols a generic type is
    ///     constrained to, read through it: for <c>S: Sequence</c>, that
    ///     <c>S.Iter.Element == S.Element</c>. Inside a protocol or an
    ///     extension of one, its associated types are its <c>Self</c>'s, so
    ///     its requirements hold of them as written.
    /// </summary>
    void Expand(TypeSymbol type) {
        if (_expanding >= MaxExpansionDepth || !_expanded.Add(type)) {
            return;
        }

        _expanding++;
        try {
            ImmutableArray<TypeSymbol> constraints;
            if (type is TypeParameterSymbol { ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } owner } parameter) {
                if (ReferenceEquals(owner.SelfTypeParameter, parameter)) {
                    // Self is whatever conforms to the protocol.
                    constraints = [owner];
                } else {
                    // 'Iter', inside Sequence, is Self.Iter.
                    if (owner.SelfTypeParameter is { } self) {
                        Expand(self);
                    }

                    constraints = parameter.Constraints;
                }
            } else {
                constraints = AssociatedTypes.ConstraintsOf(type);
            }

            // And what a 'where' clause around says of it: in 'extension
            // Sequence where Element: Sequence', an Element's Iter.Element
            // is its Element.Element.
            constraints = [..constraints, .._conformances.Where(r => r.Subject.Equals(type)).Select(static r => r.Constraint)];

            foreach (var constraint in constraints) {
                foreach (var part in CompositionTypeSymbol.PartsOf(constraint)) {
                    if (part is NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                        ReadIn(protocol, type);
                    }
                }
            }
        } finally {
            _expanding--;
        }
    }

    /// <summary>A protocol's requirements, and those of every protocol it refines, read through a type.</summary>
    void ReadIn(NamedTypeSymbol protocol, TypeSymbol through) {
        var seen = new HashSet<NamedTypeSymbol>();
        var pending = new Stack<NamedTypeSymbol>();
        pending.Push(protocol);

        while (pending.Count > 0) {
            var current = pending.Pop();
            var definition = current.OriginalDefinition;
            if (!seen.Add(definition)) {
                continue;
            }

            // 'Source<Int>' says what Source's own parameters are, too.
            var arguments = TypeSubstitution.For(current);
            var throughBase = AssociatedTypes.ThroughBase(definition, through);
            TypeSymbol Read(TypeSymbol written) => throughBase.Apply(arguments.Apply(written));

            foreach (var refined in definition.BaseTypes) {
                if (arguments.Apply(refined) is NamedTypeSymbol { TypeKind: TypeKind.Protocol } parent) {
                    pending.Push(parent);
                }
            }

            foreach (var (left, right) in definition.SameTypeRequirements) {
                Union(Read(left), Read(right));
            }

            foreach (var (subject, constraint) in definition.ConformanceRequirements) {
                _conformances.Add(new(Read(subject), Read(constraint)));
            }
        }
    }

    /// <summary>The type with every part replaced by its class's representative.</summary>
    public TypeSymbol Canonical(TypeSymbol type) {
        if (_parent.Count == 0 && MentionsNoAssociatedType(type)) {
            return type;
        }

        // What its protocols require of a generic type holds once it is met.
        switch (type) {
            case DependentMemberTypeSymbol member:
                Expand(member.BaseType);
                break;

            case TypeParameterSymbol:
                Expand(type);
                break;
        }

        var rewritten = type switch {
            // 'B.Item.Element' with B.Item == A.Item reads through A.Item.
            DependentMemberTypeSymbol member when Canonical(member.BaseType) is var baseType
                                                  && !baseType.Equals(member.BaseType) =>
                AssociatedTypes.Member(baseType, member.Associated),

            NamedTypeSymbol { IsConstructed: true } named =>
                named.OriginalDefinition.Construct([..named.TypeArguments.Select(Canonical)]),

            OptionalTypeSymbol optional => new OptionalTypeSymbol(Canonical(optional.UnderlyingType), optional.IsImplicitlyUnwrapped),

            MetatypeTypeSymbol metatype => new MetatypeTypeSymbol(Canonical(metatype.InstanceType)),

            ArrayTypeSymbol array => new ArrayTypeSymbol(Canonical(array.ElementType), array.ArrayKind, array.Capacity, array.CapacityParameter),

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
                ) { IsRethrows = function.IsRethrows, InOutParameters = function.InOutParameters, EscapingParameters = function.EscapingParameters },

            StructuralUnionTypeSymbol union => StructuralUnionTypeSymbol.Create(union.Cases.Select(Canonical)),

            _ => type,
        };

        return Find(rewritten);
    }

    /// <summary>True when the requirements make the two types the same.</summary>
    public bool AreSame(TypeSymbol left, TypeSymbol right) =>
        left.Equals(right) || Canonical(left).Equals(Canonical(right));
}
