namespace Sable.Symbols;

/// <summary>
///     A derived Arbitrary conformance's <c>static arbitrary(from:)</c>, as a
///     bound tree: a struct made of its fields' own arbitrary values, an enum's
///     case by its number, a union's case of its payload's — each drawn from
///     the choices by a call to the requirement, found for each field's type
///     when SIL lowering knows it.
/// </summary>
/// <remarks>
///     The cases of a union that can hold another of the union are numbered
///     after those that cannot, in the order declared otherwise: the first are
///     the simplest, which a failing case shrinks to, and all that are drawn
///     once a value has grown large (<c>Choices.chooseCase</c>).
/// </remarks>
internal sealed partial class DerivedBodies {
    List<BoundStatement>? Arbitrary(NamedTypeSymbol type, NamedTypeSymbol self) {
        var choices = compilation.TestingType("Choices");
        if (choices is null || ArbitraryRequirement is null) {
            return null;
        }

        var substitution = TypeSubstitution.For(self);
        if (type.TypeKind == TypeKind.Struct) {
            return [new BoundReturn(_at, new BoundRecordExpression(_at, self, [
                ..DerivedConformance.StoredFields(type).Select(f => (f, ArbitraryValue(substitution.Apply(f.Type)))),
            ]))];
        }

        var arms = new List<BoundMatchArm>();
        BoundExpression number;
        List<BoundExpression> values;
        if (type.TypeKind == TypeKind.Enum) {
            values = [..type.Members.OfType<EnumCaseSymbol>().Select(BoundExpression (c) => new BoundLiteral(_at, self, c.Name))];
            number = CallChoices(choices, "below", Literal((long)values.Count));
        } else {
            var cases = Cases(type);
            var simple = cases.Where(c => !c.Payload.Any(p => Reaches(p.Type, type, []))).ToList();
            values = [
                ..simple.Concat(cases.Except(simple)).Select(BoundExpression (c) =>
                    new BoundUnionCaseConstruction(_at, self, c, [..c.Payload.Select(p => ArbitraryValue(substitution.Apply(p.Type)))])),
            ];
            number = CallChoices(choices, "chooseCase", Literal((long)simple.Count), Literal((long)values.Count));
        }

        for (var i = 0; i < values.Count; i++) {
            // The last is what is left, so that the match has an arm for every number.
            arms.Add(i == values.Count - 1
                ? new BoundMatchArm(_at, [], null, values[i])
                : Arm(new BoundLiteralPattern(_at, core.Int, (long)i, false), values[i]));
        }

        return arms.Count == 0 ? null : [new BoundReturn(_at, new BoundMatch(_at, self, number, [..arms]))];
    }

    FunctionSymbol? ArbitraryRequirement => compilation.TestingType("Arbitrary")?.GetMembers("arbitrary").OfType<FunctionSymbol>().FirstOrDefault();

    /// <summary><c>T.arbitrary(from: choices)</c>, through the requirement.</summary>
    BoundExpression ArbitraryValue(TypeSymbol type) {
        var substitution = new TypeSubstitution(new() { [compilation.TestingType("Arbitrary")!.SelfTypeParameter!] = type });
        return new BoundCall(_at, new BoundTypeExpression(_at, type), ArbitraryRequirement!, [new BoundParameterAccess(_at, _function.Parameters[0])], type) { Substitution = substitution };
    }

    BoundExpression CallChoices(NamedTypeSymbol choices, string name, params BoundExpression[] arguments) =>
        new BoundCall(_at, new BoundParameterAccess(_at, _function.Parameters[0]), MemberOf(choices, name), [..arguments], core.Int);

    /// <summary>Whether a value of the one type can hold a value of the other, at any depth.</summary>
    static bool Reaches(TypeSymbol type, NamedTypeSymbol sought, HashSet<TypeSymbol> seen) {
        switch (type) {
            case OptionalTypeSymbol optional:
                return Reaches(optional.UnderlyingType, sought, seen);
            case ArrayTypeSymbol array:
                return Reaches(array.ElementType, sought, seen);
            case TupleTypeSymbol tuple:
                return tuple.ElementTypes.Any(t => Reaches(t, sought, seen));
            case NamedTypeSymbol named:
                if (ReferenceEquals(named.OriginalDefinition, sought.OriginalDefinition)) {
                    return true;
                }

                return named.TypeArguments.Any(t => Reaches(t, sought, seen))
                    || seen.Add(named) && named.TypeKind is TypeKind.Struct or TypeKind.Union
                    && DerivedConformance.Fields(named.OriginalDefinition).Any(f => Reaches(f.Type, sought, seen));
            default:
                return false;
        }
    }
}
