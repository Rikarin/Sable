namespace Sable.Symbols;

/// <summary>
///     Decides whether one type may be used where another is expected.
/// </summary>
/// <remarks>
///     The relation is deliberately small, per docs/03-type-system.md §3.8: no
///     implicit numeric widening, no array covariance, no structural record
///     subtyping. A numeric literal is the one thing that bends, because it has
///     no type of its own until the context gives it one.
/// </remarks>
internal sealed class Conversions(CoreTypes core) {
    /// <summary>
    ///     The same-type requirements in force where the conversion happens:
    ///     inside <c>where A.Item == B.Item</c> the two are one type.
    /// </summary>
    public TypeEquivalence Equivalence { get; set; } = TypeEquivalence.Empty;

    /// <summary>The conversion from source to target, or null when there is none.</summary>
    public ConversionKind? Classify(TypeSymbol source, TypeSymbol target, bool sourceIsLiteral) {
        // An error on either side converts, so one mistake stays one mistake.
        if (source.IsError || target.IsError) {
            return ConversionKind.Identity;
        }

        if (!Equivalence.IsEmpty) {
            source = Equivalence.Canonical(source);
            target = Equivalence.Canonical(target);
        }

        if (source.Equals(target)) {
            return ConversionKind.Identity;
        }

        // A reference stored as a relative one, or read back as absolute:
        // the same target, measured differently (docs/02 §2.5).
        if (SameReferent(source, target)) {
            return ConversionKind.Identity;
        }

        // A 'T!' is the T it holds wherever a T, or what a T converts to, is
        // wanted: unwrapped, as 'x!' would be, and the binder takes the step
        // from the T after.
        if (source is OptionalTypeSymbol { IsImplicitlyUnwrapped: true } unwrapped
            && target is not OptionalTypeSymbol
            && Classify(unwrapped.UnderlyingType, target, false) is not null) {
            return ConversionKind.Explicit;
        }

        // 'Never' is the bottom type: an expression that does not return fits
        // anywhere.
        if (source.Equals(core.Never)) {
            return ConversionKind.Bottom;
        }

        if (sourceIsLiteral && IsLiteralCompatible(source, target)) {
            return ConversionKind.LiteralTyping;
        }

        // A string literal as inline bytes: 'customer: "Ann"' for a 'U8[..32]'.
        if (sourceIsLiteral
            && source.Equals(core.String)
            && target is ArrayTypeSymbol { ElementType: var bytes, ArrayKind: ArrayKind.Inline or ArrayKind.Grow }
            && bytes.Equals(core.U8)) {
            return ConversionKind.LiteralTyping;
        }

        // An array literal has no storage kind of its own, so it fits any of
        // them as long as the elements match.
        if (sourceIsLiteral
            && source is ArrayTypeSymbol sourceArray
            && target is ArrayTypeSymbol targetArray
            && (sourceArray.ElementType.Equals(targetArray.ElementType)
                || IsLiteralCompatible(sourceArray.ElementType, targetArray.ElementType))) {
            return ConversionKind.LiteralTyping;
        }

        if (target is OptionalTypeSymbol optional) {
            if (source.Equals(optional.UnderlyingType) || SameReferent(source, optional.UnderlyingType)) {
                return ConversionKind.OptionalWrap;
            }

            if (sourceIsLiteral && IsLiteralCompatible(source, optional.UnderlyingType)) {
                return ConversionKind.OptionalWrap;
            }

            // Promotion composes with what gets a value to the underlying
            // type: '[9]' for an 'Int[>..]?' takes its kind and then wraps,
            // '"x"' for an '(Int | String)?' joins the union and then wraps.
            // The binder builds the two steps; an optional is never wrapped
            // again, so 'T?' does not drift into 'T??'.
            if (source is not OptionalTypeSymbol
                && Classify(source, optional.UnderlyingType, sourceIsLiteral) is not null) {
                return ConversionKind.OptionalWrap;
            }
        }

        if (StructuralUnionTypeSymbol.Of(target) is { } union) {
            foreach (var unionCase in union.Cases) {
                if (source.Equals(unionCase)) {
                    return ConversionKind.UnionInjection;
                }

                if (sourceIsLiteral && IsLiteralCompatible(source, unionCase)) {
                    return ConversionKind.UnionInjection;
                }
            }
        }

        // A function that throws less, or does not suspend, fits where one
        // that may is wanted. Parameters and result stay invariant: variance
        // there is a separate decision, and docs/03 §3.8 does not take it.
        if (source is FunctionTypeSymbol from
            && target is FunctionTypeSymbol to
            && from.ParameterTypes.Length == to.ParameterTypes.Length
            && from.ReturnType.Equals(to.ReturnType)
            && (!from.IsAsync || to.IsAsync)
            && (from.ThrownType is null || to.ThrownType is not null)) {
            var parametersMatch = true;
            for (var i = 0; i < from.ParameterTypes.Length; i++) {
                if (from.ParameterTypes[i].Equals(to.ParameterTypes[i])) {
                    continue;
                }

                parametersMatch = false;
                break;
            }

            if (parametersMatch) {
                return ConversionKind.Upcast;
            }
        }

        // 'any P' and 'some P' accept whatever conforms to P.
        if (target is ExistentialTypeSymbol existential) {
            return Satisfies(source, existential.Constraint, Equivalence)
                ? ConversionKind.Existential
                : null;
        }

        if (target is OpaqueTypeSymbol opaque) {
            return Satisfies(source, opaque.Constraint, Equivalence) ? ConversionKind.Existential : null;
        }

        if (target is CompositionTypeSymbol && Satisfies(source, target, Equivalence)) {
            return ConversionKind.Existential;
        }

        if (IsDerivedFrom(source, target)) {
            return ConversionKind.Upcast;
        }

        // A reference reads as what it refers to; a place, where a reference
        // is wanted, is referred to — the binder checks it is a place.
        if (source is ReferenceTypeSymbol { RelativeWidth: null } reference && reference.ReferencedType.Equals(target)) {
            return ConversionKind.Dereference;
        }

        if (target is ReferenceTypeSymbol { RelativeWidth: null } wanted && source.Equals(wanted.ReferencedType)) {
            return ConversionKind.Reference;
        }

        return null;
    }

    /// <summary>True when a type meets a constraint: a protocol, a class, or several.</summary>
    public static bool Satisfies(TypeSymbol source, TypeSymbol constraint, TypeEquivalence? context = null) {
        if (constraint.IsError || source.IsError) {
            return true;
        }

        if (constraint is CompositionTypeSymbol composition) {
            return composition.Parts.All(part => Satisfies(source, part, context));
        }

        // A constraint that is itself a parameter stands for a type nothing
        // is known about yet, so anything may satisfy it.
        if (constraint is TypeParameterSymbol) {
            return true;
        }

        if (source.Equals(constraint)) {
            return true;
        }

        // A type parameter satisfies only what it was constrained to: an
        // unconstrained one conforms to nothing.
        if (source is TypeParameterSymbol or DependentMemberTypeSymbol) {
            return AssociatedTypes.ConstraintsOf(source, context).Any(c => Satisfies(c, constraint, context));
        }

        if (source is OpaqueTypeSymbol sourceOpaque) {
            return Satisfies(sourceOpaque.Constraint, constraint, context);
        }

        if (source is ExistentialTypeSymbol sourceExistential) {
            return Satisfies(sourceExistential.Constraint, constraint, context);
        }

        // A conformance '@derive' gave a generic type is conditional: it
        // holds where the construction's fields conform.
        if (source is NamedTypeSymbol { IsConstructed: true } derived && constraint is NamedTypeSymbol protocol
            && derived.OriginalDefinition.DerivedConformances.Contains(protocol)) {
            return DerivedConformance.Holds(derived, protocol, context);
        }

        return IsDerivedFrom(source, constraint);
    }

    public bool HasImplicitConversion(
        TypeSymbol source,
        TypeSymbol target,
        bool sourceIsLiteral = false
    ) =>
        Classify(source, target, sourceIsLiteral) is not null;

    /// <summary>How much a conversion costs, for choosing between overloads.</summary>
    public static int Cost(ConversionKind kind) =>
        kind switch {
            ConversionKind.Identity => 0,
            ConversionKind.LiteralTyping => 1,
            ConversionKind.Upcast => 2,
            ConversionKind.OptionalWrap => 3,
            ConversionKind.UnionInjection => 4,
            ConversionKind.Existential => 5,
            ConversionKind.Bottom => 0,
            _ => 10,
        };

    /// <summary>
    ///     A literal has no type of its own; an integer literal fits any integer
    ///     type and a floating one any float type, provided the value fits.
    /// </summary>
    bool IsLiteralCompatible(TypeSymbol source, TypeSymbol target) {
        if (source.Equals(core.Int)) {
            return core.IsInteger(target) || core.Floats.Contains(target);
        }

        if (source.Equals(core.F64)) {
            return core.Floats.Contains(target);
        }

        return false;
    }

    static bool IsDerivedFrom(TypeSymbol source, TypeSymbol target) {
        if (source is not NamedTypeSymbol named) {
            return false;
        }

        var visited = new HashSet<NamedTypeSymbol>();
        var queue = new Queue<NamedTypeSymbol>();
        queue.Enqueue(named);

        while (queue.Count > 0) {
            var current = queue.Dequeue();
            if (!visited.Add(current)) {
                continue;
            }

            foreach (var baseType in current.BaseTypes) {
                // An enum's base list names its underlying representation as
                // well as its protocols, and that is not a supertype: going
                // between an enum and its representation is written out.
                if (current.TypeKind == TypeKind.Enum
                    && baseType is NamedTypeSymbol { TypeKind: TypeKind.Builtin }) {
                    continue;
                }

                if (baseType.Equals(target)) {
                    return true;
                }

                if (baseType is NamedTypeSymbol baseNamed) {
                    queue.Enqueue(baseNamed);
                }
            }
        }

        return false;
    }

    static bool SameReferent(TypeSymbol source, TypeSymbol target) =>
        source is ReferenceTypeSymbol from
        && target is ReferenceTypeSymbol to
        && (from.RelativeWidth is not null || to.RelativeWidth is not null)
        && from.ReferencedType.Equals(to.ReferencedType);
}
