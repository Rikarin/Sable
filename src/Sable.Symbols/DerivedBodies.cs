using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     The bodies of the members <c>@derive</c> makes, as bound trees built
///     from the type's fields: what the binder would have made of the members
///     written out by hand.
/// </summary>
/// <remarks>
///     <para>
///         A field is compared, ordered, hashed or described through the core
///         protocol's requirement — <c>Equatable.==</c>, <c>Hashable.hash</c> —
///         so that SIL lowering finds the field type's own member once the type
///         is known, as it does for any call through a type parameter. That is
///         what lets one body serve every construction of a generic type. The
///         numbers, <c>Bool</c> and <c>Char</c> use the built-in operators
///         instead, which is what their conformances do anyway.
///     </para>
///     <para>
///         Every node points at the <c>@derive</c> attribute, so a failure
///         lowering one is reported where the derivation was asked for.
///     </para>
/// </remarks>
internal sealed class DerivedBodies(CoreTypes core, Func<string, string, FunctionSymbol?> requirement) {
    /// <summary>
    ///     What a hash is kept within, so that combining the fields' hashes
    ///     cannot overflow: below 2^31, times 31, plus another below it.
    /// </summary>
    const long HashModulus = 1_000_000_007;

    SyntaxNode _at = null!;
    FunctionSymbol _function = null!;

    public BoundBlock? Build(Derivation derivation) {
        _at = derivation.Attribute;
        _function = derivation.Function;
        var type = derivation.Type;
        var self = type.IsGenericDefinition ? type.Construct([..type.TypeParameters]) : type;

        var value = type.TypeKind == TypeKind.Enum ? Enum(derivation.Member, type, self) : derivation.Member switch {
            DerivedMember.Equals => type.TypeKind == TypeKind.Union ? UnionEquals(type, self) : StructEquals(type, self),
            DerivedMember.LessThan => type.TypeKind == TypeKind.Union ? UnionLessThan(type, self) : StructLessThan(type, self),
            DerivedMember.Hash => type.TypeKind == TypeKind.Union ? UnionHash(type, self) : StructHash(type, self),
            _ => type.TypeKind == TypeKind.Union ? UnionDebug(type, self) : StructDebug(type, self),
        };

        return value is null ? null : new BoundBlock(_at, [], [new BoundReturn(_at, value)]);
    }

    // ---- Structs ----------------------------------------------------------

    BoundExpression? StructEquals(NamedTypeSymbol type, NamedTypeSymbol self) {
        var (lhs, rhs) = Operands(self);
        return All(DerivedConformance.StoredFields(type).Select(f => Equal(f.Type, Field(lhs(), f), Field(rhs(), f))));
    }

    BoundExpression? StructLessThan(NamedTypeSymbol type, NamedTypeSymbol self) {
        var (lhs, rhs) = Operands(self);
        return Lexicographic([..DerivedConformance.StoredFields(type).Select(f => (f.Type, (Func<BoundExpression>)(() => Field(lhs(), f)), (Func<BoundExpression>)(() => Field(rhs(), f))))]);
    }

    BoundExpression? StructHash(NamedTypeSymbol type, NamedTypeSymbol self) {
        return Combine(17, DerivedConformance.StoredFields(type).Select(f => (f.Type, Field(new BoundSelfAccess(_at, self), f))));
    }

    /// <summary><c>Point(x: 1, y: 2)</c>: the name, and each field labelled.</summary>
    BoundExpression? StructDebug(NamedTypeSymbol type, NamedTypeSymbol self) {
        return Describe(type.Name, DerivedConformance.StoredFields(type).Select(f => ((string?)f.DisplayName, f.Type, Field(new BoundSelfAccess(_at, self), f))));
    }

    // ---- Unions -----------------------------------------------------------

    /// <summary>
    ///     <c>match lhs { case .A(a…) => match rhs { case .A(b…) => a… == b…; default => false } … }</c>
    /// </summary>
    BoundExpression? UnionEquals(NamedTypeSymbol type, NamedTypeSymbol self) {
        var (lhs, rhs) = Operands(self);
        var arms = new List<BoundMatchArm>();
        foreach (var unionCase in Cases(type)) {
            var (left, leftLocals) = Bind(self, unionCase, "a");
            var (right, rightLocals) = Bind(self, unionCase, "b");
            if (All(unionCase.Payload.Select((p, i) => Equal(p.Type, Local(leftLocals[i]), Local(rightLocals[i])))) is not { } same) {
                return null;
            }

            var inner = new BoundMatch(_at, core.Bool, rhs(), [Arm(right, same), new BoundMatchArm(_at, [], null, Literal(false))]);
            arms.Add(Arm(left, inner));
        }

        return new BoundMatch(_at, core.Bool, lhs(), [..arms]);
    }

    /// <summary>
    ///     By case, in declaration order, and then by payload: for each case
    ///     of <c>lhs</c>, the same case of <c>rhs</c> compares payloads, and any
    ///     other decides by which case comes first.
    /// </summary>
    BoundExpression? UnionLessThan(NamedTypeSymbol type, NamedTypeSymbol self) {
        var (lhs, rhs) = Operands(self);
        var cases = Cases(type);
        var arms = new List<BoundMatchArm>();
        for (var i = 0; i < cases.Count; i++) {
            var (left, leftLocals) = Bind(self, cases[i], "a");
            var (right, rightLocals) = Bind(self, cases[i], "b");
            if (Lexicographic([..cases[i].Payload.Select((p, k) => (p.Type, (Func<BoundExpression>)(() => Local(leftLocals[k])), (Func<BoundExpression>)(() => Local(rightLocals[k]))))]) is not { } less) {
                return null;
            }

            var inner = new List<BoundMatchArm> { Arm(right, less) };
            for (var j = 0; j < cases.Count; j++) {
                if (j != i) {
                    inner.Add(Arm(Ignore(self, cases[j]), Literal(i < j)));
                }
            }

            arms.Add(Arm(left, new BoundMatch(_at, core.Bool, rhs(), [..inner])));
        }

        return new BoundMatch(_at, core.Bool, lhs(), [..arms]);
    }

    /// <summary>The case's position, then its payload's hashes.</summary>
    BoundExpression? UnionHash(NamedTypeSymbol type, NamedTypeSymbol self) {
        var cases = Cases(type);
        var arms = new List<BoundMatchArm>();
        for (var i = 0; i < cases.Count; i++) {
            var (pattern, locals) = Bind(self, cases[i], "a");
            if (Combine(i + 1L, cases[i].Payload.Select((p, k) => (p.Type, Local(locals[k])))) is not { } hash) {
                return null;
            }

            arms.Add(Arm(pattern, hash));
        }

        return new BoundMatch(_at, core.Int, new BoundSelfAccess(_at, self), [..arms]);
    }

    /// <summary><c>Shape.Circle(radius: 1.5)</c>; <c>Shape.Empty</c> for a case that carries nothing.</summary>
    BoundExpression? UnionDebug(NamedTypeSymbol type, NamedTypeSymbol self) {
        var arms = new List<BoundMatchArm>();
        foreach (var unionCase in Cases(type)) {
            var (pattern, locals) = Bind(self, unionCase, "a");
            var name = $"{type.Name}.{unionCase.Name}";
            var description = unionCase.HasPayload
                ? Describe(name, unionCase.Payload.Select((p, k) => (p.Label, p.Type, Local(locals[k]))))
                : Literal(name);
            if (description is null) {
                return null;
            }

            arms.Add(Arm(pattern, description));
        }

        return new BoundMatch(_at, core.String, new BoundSelfAccess(_at, self), [..arms]);
    }

    static List<UnionCaseSymbol> Cases(NamedTypeSymbol type) => [..type.Members.OfType<UnionCaseSymbol>()];

    // ---- Enums ------------------------------------------------------------

    /// <summary>
    ///     An enum's members, by case, as a union's are with no payloads:
    ///     equal when the same case, ordered as the cases are declared,
    ///     hashed by position, described as <c>Direction.North</c>.
    /// </summary>
    BoundExpression? Enum(DerivedMember member, NamedTypeSymbol type, NamedTypeSymbol self) {
        var cases = type.Members.OfType<EnumCaseSymbol>().ToList();
        BoundPattern Case(int i) => new BoundEnumCasePattern(_at, self, cases[i]);
        var (lhs, rhs) = Operands(self);

        BoundExpression ByCase(BoundExpression subject, TypeSymbol result, Func<int, BoundExpression> arm) =>
            new BoundMatch(_at, result, subject, [..cases.Select((_, i) => Arm(Case(i), arm(i)))]);

        return member switch {
            DerivedMember.Equals => ByCase(lhs(), core.Bool, i => new BoundMatch(_at, core.Bool, rhs(), [Arm(Case(i), Literal(true)), new BoundMatchArm(_at, [], null, Literal(false))])),
            DerivedMember.LessThan => ByCase(lhs(), core.Bool, i => ByCase(rhs(), core.Bool, j => Literal(i < j))),
            DerivedMember.Hash => ByCase(new BoundSelfAccess(_at, self), core.Int, i => Literal(i + 1L)),
            _ => ByCase(new BoundSelfAccess(_at, self), core.String, i => Literal($"{type.Name}.{cases[i].Name}")),
        };
    }

    /// <summary><c>case .A(let a0, let a1)</c>, with the locals it binds.</summary>
    (BoundPattern Pattern, ImmutableArray<LocalSymbol> Locals) Bind(NamedTypeSymbol self, UnionCaseSymbol unionCase, string prefix) {
        var substitution = TypeSubstitution.For(self);
        var locals = unionCase.Payload
            .Select(p => new LocalSymbol($"{prefix}{p.Ordinal}", _function, null, false) { TypeResolver = () => substitution.Apply(p.Type) })
            .ToImmutableArray();
        var payload = locals.Select(l => (BoundPattern)new BoundBindingPattern(_at, l.Type, l)).ToImmutableArray();
        return (new BoundUnionCasePattern(_at, self, unionCase, payload), locals);
    }

    /// <summary><c>case .B(_, _)</c>.</summary>
    BoundPattern Ignore(NamedTypeSymbol self, UnionCaseSymbol unionCase) {
        var substitution = TypeSubstitution.For(self);
        return new BoundUnionCasePattern(
            _at,
            self,
            unionCase,
            [..unionCase.Payload.Select(p => (BoundPattern)new BoundWildcardPattern(_at, substitution.Apply(p.Type)))]
        );
    }

    BoundMatchArm Arm(BoundPattern pattern, BoundExpression body) => new(_at, [pattern], null, body);

    // ---- Pieces -----------------------------------------------------------

    /// <summary>Makers of <c>lhs</c> and <c>rhs</c>: each use is a node of its own, as the binder's would be.</summary>
    (Func<BoundExpression> Lhs, Func<BoundExpression> Rhs) Operands(NamedTypeSymbol self) =>
        (() => new BoundParameterAccess(_at, _function.Parameters[0], self), () => new BoundParameterAccess(_at, _function.Parameters[1], self));

    BoundExpression Field(BoundExpression receiver, FieldSymbol field) =>
        new BoundFieldAccess(_at, receiver, field, TypeSubstitution.For(receiver.Type).Apply(field.Type));

    BoundExpression Local(LocalSymbol local) => new BoundLocalAccess(_at, local);

    BoundExpression Literal(object value) =>
        new BoundLiteral(_at, value switch { bool => core.Bool, string => core.String, _ => core.Int }, value);

    /// <summary>The built-in operators serve the numbers, <c>Bool</c> and <c>Char</c>; everything else conforms.</summary>
    bool IsPrimitive(TypeSymbol type) => core.IsNumeric(type) || type.Equals(core.Bool) || type.Equals(core.Char);

    BoundExpression? Equal(TypeSymbol type, BoundExpression left, BoundExpression right) =>
        IsPrimitive(type) ? new BoundBinaryOperator(_at, core.Bool, BinaryOperatorKind.Equal, left, right)
        : type is OptionalTypeSymbol { UnderlyingType: var payload } ? Structural(payload, left, right)
        : type is ArrayTypeSymbol { ElementType: var element } ? Structural(element, left, right)
        : requirement("Equatable", "==") is { } equals
            ? new BoundBinaryOperator(_at, core.Bool, BinaryOperatorKind.UserDefined, left, right, equals)
            : null;

    /// <summary>
    ///     Two optionals or two arrays compared by what they hold, as the
    ///     binder makes '==' of them: a comparison of two of the parts, bound
    ///     to locals that lowering gives each pair in turn.
    /// </summary>
    BoundExpression? Structural(TypeSymbol part, BoundExpression left, BoundExpression right) {
        var leftPart = new LocalSymbol("$left", _function, null, false) { TypeResolver = () => part };
        var rightPart = new LocalSymbol("$right", _function, null, false) { TypeResolver = () => part };
        if (Equal(part, new BoundLocalAccess(_at, leftPart), new BoundLocalAccess(_at, rightPart)) is not { } equal) {
            return null;
        }

        return new BoundBinaryOperator(_at, core.Bool, BinaryOperatorKind.Equal, left, right) {
            Structural = new StructuralComparison(leftPart, rightPart, equal),
        };
    }

    BoundExpression? Less(TypeSymbol type, BoundExpression left, BoundExpression right) =>
        IsPrimitive(type)
            ? new BoundBinaryOperator(_at, core.Bool, BinaryOperatorKind.LessThan, left, right)
            : requirement("Comparable", "<") is { } less
                ? new BoundBinaryOperator(_at, core.Bool, BinaryOperatorKind.UserDefined, left, right, less)
                : null;

    /// <summary><c>a && b && …</c>; <c>true</c> for nothing.</summary>
    BoundExpression? All(IEnumerable<BoundExpression?> parts) {
        BoundExpression? result = null;
        foreach (var part in parts) {
            if (part is null) {
                return null;
            }

            result = result is null ? part : new BoundBinaryOperator(_at, core.Bool, BinaryOperatorKind.LogicalAnd, result, part);
        }

        return result ?? Literal(true);
    }

    /// <summary>
    ///     <c>a0 &lt; b0 || !(b0 &lt; a0) && (…the rest…)</c>, the last pair
    ///     plain <c>a &lt; b</c>; <c>false</c> for nothing, which no value precedes.
    /// </summary>
    BoundExpression? Lexicographic(ImmutableArray<(TypeSymbol Type, Func<BoundExpression> Left, Func<BoundExpression> Right)> pairs) {
        if (pairs.IsEmpty) {
            return Literal(false);
        }

        var (type, left, right) = pairs[^1];
        var result = Less(type, left(), right());
        for (var i = pairs.Length - 2; i >= 0 && result is not null; i--) {
            (type, left, right) = pairs[i];
            if (Less(type, left(), right()) is not { } before || Less(type, right(), left()) is not { } after) {
                return null;
            }

            var notAfter = new BoundUnaryOperator(_at, core.Bool, UnaryOperatorKind.Not, after);
            var rest = new BoundBinaryOperator(_at, core.Bool, BinaryOperatorKind.LogicalAnd, notAfter, result);
            result = new BoundBinaryOperator(_at, core.Bool, BinaryOperatorKind.LogicalOr, before, rest);
        }

        return result;
    }

    /// <summary><c>(h * 31 + value.hash() % M) % M</c> over each value in turn, from a seed.</summary>
    BoundExpression? Combine(long seed, IEnumerable<(TypeSymbol Type, BoundExpression Value)> values) {
        if (requirement("Hashable", "hash") is not { } hash) {
            return null;
        }

        BoundExpression result = Literal(seed);
        foreach (var (_, value) in values) {
            var hashed = new BoundCall(_at, value, hash, [], core.Int);
            var bounded = Arithmetic(BinaryOperatorKind.Remainder, hashed, Literal(HashModulus));
            var mixed = Arithmetic(BinaryOperatorKind.Add, Arithmetic(BinaryOperatorKind.Multiply, result, Literal(31L)), bounded);
            result = Arithmetic(BinaryOperatorKind.Remainder, mixed, Literal(HashModulus));
        }

        return result;
    }

    BoundExpression Arithmetic(BinaryOperatorKind kind, BoundExpression left, BoundExpression right) =>
        new BoundBinaryOperator(_at, core.Int, kind, left, right);

    /// <summary><c>"Name(" + "label: " + value.debugDescription() + ", " + … + ")"</c>.</summary>
    BoundExpression? Describe(string name, IEnumerable<(string? Label, TypeSymbol Type, BoundExpression Value)> values) {
        if (requirement("Debug", "debugDescription") is not { } debug) {
            return null;
        }

        var parts = new List<BoundExpression>();
        var text = name + "(";
        var first = true;
        foreach (var (label, _, value) in values) {
            text += (first ? "" : ", ") + (label is null ? "" : label + ": ");
            parts.Add(Literal(text));
            parts.Add(new BoundCall(_at, value, debug, [], core.String));
            text = "";
            first = false;
        }

        parts.Add(Literal(text + ")"));
        return parts.Aggregate((left, right) => new BoundBinaryOperator(_at, core.String, BinaryOperatorKind.Add, left, right));
    }
}
