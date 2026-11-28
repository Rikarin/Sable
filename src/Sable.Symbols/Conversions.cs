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

        source = Equivalence.Canonical(source);
        target = Equivalence.Canonical(target);

        if (source.Equals(target)) {
            return ConversionKind.Identity;
        }

        // Inside a generic type, 'self' is of the definition, 'Channel',
        // which is the type over its own parameters, 'Channel<Element>'.
        if (IsOwnConstruction(source, target) || IsOwnConstruction(target, source)) {
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

        // Every kind of array can be viewed as a slice of its elements.
        if (source is ArrayTypeSymbol { ArrayKind: not ArrayKind.Slice } array
            && target is ArrayTypeSymbol { ArrayKind: ArrayKind.Slice } slice
            && array.ElementType.Equals(slice.ElementType)) {
            return ConversionKind.ArrayView;
        }

        // 'Int.self' where a 'Type' is wanted: the one value of 'Int.Type' is
        // Int, as its metadata, which is what a Type holds (SYNTAX.md
        // "Metatypes"). The other way needs the type known statically.
        if (source is MetatypeTypeSymbol && target.Equals(core.Type)) {
            return ConversionKind.MetatypeErasure;
        }

        // 'any P.Type': a type that conforms to P as one, and as a 'Type' any.
        if (target is ExistentialTypeSymbol { MetatypeOf: { } conformed }) {
            return source switch {
                MetatypeTypeSymbol { InstanceType: not ExistentialTypeSymbol and var known } when Satisfies(known, conformed) => ConversionKind.MetatypeErasure,
                ExistentialTypeSymbol { MetatypeOf: { } held } when Satisfies(held, conformed) => ConversionKind.MetatypeErasure,
                _ => null,
            };
        }

        if (source is ExistentialTypeSymbol { MetatypeOf: not null } && target.Equals(core.Type)) {
            return ConversionKind.MetatypeErasure;
        }

        // A 'Type' given for a generic callee's 'T.Type', which inference
        // bound to the type it holds at run time: opened (SYNTAX.md "Opening
        // a type").
        if ((source.Equals(core.Type) || source is ExistentialTypeSymbol { MetatypeOf: not null }) && target is MetatypeTypeSymbol { InstanceType: TypeParameterSymbol { OpenedFrom: not null } }) {
            return ConversionKind.Open;
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
                || IsLiteralCompatible(sourceArray.ElementType, targetArray.ElementType)
                // '[Int.self]' where Types are wanted.
                || sourceArray.ElementType is MetatypeTypeSymbol && targetArray.ElementType.Equals(core.Type))) {
            return ConversionKind.LiteralTyping;
        }

        // A range of literals, '0.0..<1.0', has bounds of no type of their
        // own yet: it fits a range of the same kind whose bounds they fit.
        if (sourceIsLiteral
            && source is NamedTypeSymbol { TypeArguments: [var sourceBound] } sourceRange
            && target is NamedTypeSymbol { TypeArguments: [var targetBound] } targetRange
            && core.IsAnyRange(sourceRange)
            && ReferenceEquals(sourceRange.OriginalDefinition, targetRange.OriginalDefinition)
            && IsLiteralCompatible(sourceBound, targetBound)) {
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
                // 'inout' is how the argument is passed: '(Int) -> ()' does
                // not take an Int where it is.
                // '@escaping' is what the function may do with a closure it is
                // given: one that keeps it does not fit where callers lend
                // theirs only for the call, but one that does not keep it
                // fits anywhere.
                if (from.ParameterTypes[i].Equals(to.ParameterTypes[i]) && from.IsInOut(i) == to.IsInOut(i) && (!from.IsEscaping(i) || to.IsEscaping(i))) {
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
            return Satisfies(source, existential.Constraint, Equivalence) || ConformsVariantly(source, existential.Constraint)
                ? ConversionKind.Existential
                : null;
        }

        // A result's or a local's 'some P' is a type of its own, which only
        // the origin's own definition gives values of: the binder converts
        // those itself. Nothing else converts to it, not even the type it hides.
        if (target is OpaqueTypeSymbol { Origin: not null }) {
            return null;
        }

        if (target is OpaqueTypeSymbol opaque) {
            return Satisfies(source, opaque.Constraint, Equivalence) && MeetsArguments(source, opaque, Equivalence) ? ConversionKind.Existential : null;
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

    /// <summary>
    ///     True when what <c>some Sequence&lt;Int&gt;</c> says of the protocol's
    ///     associated types holds of a type: its Element is an Int.
    /// </summary>
    public static bool MeetsArguments(TypeSymbol source, OpaqueTypeSymbol opaque, TypeEquivalence context) {
        if (opaque.Arguments.IsEmpty || opaque.Constraint is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
            return true;
        }

        var declared = protocol.OriginalDefinition.AssociatedTypes;
        for (var i = 0; i < opaque.Arguments.Length && i < declared.Length; i++) {
            var wanted = opaque.Arguments[i];
            var given = context.Canonical(AssociatedTypes.Member(source, declared[i]));
            if (wanted.IsError || given.IsError) {
                continue;
            }

            if (!context.Canonical(wanted).Equals(given)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The questions being answered through a type's own constraints, and what they were asked of.</summary>
    [ThreadStatic]
    static HashSet<(TypeSymbol, TypeSymbol)>? _asking;

    /// <summary>
    ///     Whether what an abstract type is constrained to meets a constraint.
    ///     A question that comes back to itself — <c>T: Box&lt;T&gt;</c> where a
    ///     Box is a P if its T is — is not answered by that.
    /// </summary>
    static bool ConstraintsSatisfy(TypeSymbol source, TypeSymbol constraint, TypeEquivalence? context) {
        var constraints = AssociatedTypes.ConstraintsOf(source, context);
        var asking = _asking ??= [];
        if (!asking.Add((source, constraint))) {
            return false;
        }

        try {
            return constraints.Any(c => Satisfies(c, constraint, context));
        } finally {
            asking.Remove((source, constraint));
        }
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

        // In a generic protocol's own extension, Self is 'Validator', which
        // is the 'Validator<T>' of the T in scope there.
        if (source is NamedTypeSymbol { IsGenericDefinition: true, TypeKind: TypeKind.Protocol } definition
            && constraint is NamedTypeSymbol { IsConstructed: true } own
            && ReferenceEquals(own.OriginalDefinition, definition)
            && own.TypeArguments.SequenceEqual(definition.TypeParameters)) {
            return true;
        }

        // Sendable is met by what a type is made of, not by declaring it.
        // A type parameter is Sendable too where the code around requires it
        // to be: 'extension AsyncSequence where Self: Sendable'.
        if (Sendability.IsSendableProtocol(constraint)) {
            return Sendability.IsSendable(source)
                || source is TypeParameterSymbol or DependentMemberTypeSymbol
                && ConstraintsSatisfy(source, constraint, context);
        }

        // PlainData too: a type is its bytes alone, or it is not.
        if (PlainData.IsPlainDataProtocol(constraint)) {
            return source is TypeParameterSymbol or DependentMemberTypeSymbol
                ? ConstraintsSatisfy(source, constraint, context)
                : PlainData.Why(source) is null;
        }

        // CSignature is met by a function type C can have, by what it takes
        // and gives (Compilation.Interop.cs).
        if (source is FunctionTypeSymbol && Compilation.IsCSignatureProtocol(constraint)) {
            return Compilation.WhyNotCSignature(source) is null;
        }

        // A type parameter satisfies only what it was constrained to: an
        // unconstrained one conforms to nothing.
        if (source is TypeParameterSymbol or DependentMemberTypeSymbol) {
            return ConstraintsSatisfy(source, constraint, context);
        }

        // Never has no values, so it is an Error (and what Error refines): a
        // sequence that cannot fail has Never for its Failure. Only those:
        // a type found conforming to any other would be one generic code
        // and existentials are made for.
        if (source is NamedTypeSymbol { TypeKind: TypeKind.Builtin, Name: "Never", TypeArguments.IsEmpty: true }
            && constraint is NamedTypeSymbol { TypeKind: TypeKind.Protocol, Name: "Error" or "Debug" or "Sendable", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }) {
            return true;
        }

        if (source is OpaqueTypeSymbol sourceOpaque) {
            return Satisfies(sourceOpaque.Constraint, constraint, context);
        }

        if (source is ExistentialTypeSymbol sourceExistential) {
            return Satisfies(sourceExistential.Constraint, constraint, context);
        }

        // 'any A & B' is an A, and a B.
        if (source is CompositionTypeSymbol sourceComposition) {
            return sourceComposition.Parts.Any(part => Satisfies(part, constraint, context));
        }

        // A conformance '@derive' gave a generic type is conditional: it
        // holds where the construction's fields conform.
        if (source is NamedTypeSymbol { IsConstructed: true } derived && constraint is NamedTypeSymbol protocol
            && derived.OriginalDefinition.DerivedConformances.Contains(protocol)) {
            return DerivedConformance.Holds(derived, protocol, context);
        }

        // 'extension<Element> Element[:]: Sequence' conforms every array;
        // 'extension<Element: Equatable> …', those of an equatable element.
        if (source is ArrayTypeSymbol array && constraint is NamedTypeSymbol { TypeKind: TypeKind.Protocol } wanted) {
            return Compilation.ArrayConformance(array, wanted, context) is not null;
        }

        // 'extension Optional: Equatable where Wrapped: Equatable' conforms
        // an 'Int?', whose Int is.
        if (source is OptionalTypeSymbol optional && constraint is NamedTypeSymbol { TypeKind: TypeKind.Protocol } conformed) {
            return OptionalConformance(optional, conformed, context) is not null;
        }

        // 'extension Box: P where T: P' conforms a Box<T> only where its T
        // does; inside the extension, its own requirements say so.
        if (source is NamedTypeSymbol named
            && ConditionalConformance(named, constraint) is { } extension) {
            return extension.Unmet(TypeSubstitution.For(named), context) is null;
        }

        return IsDerivedFrom(source, constraint);
    }

    /// <summary>
    ///     The extension of Optional that conforms this optional to the
    ///     protocol, and the substitution its members are seen through; null
    ///     when none does, or none holds of what the optional holds.
    /// </summary>
    internal static (ExtensionSymbol Extension, TypeSubstitution Substitution)? OptionalConformance(
        OptionalTypeSymbol optional,
        NamedTypeSymbol protocol,
        TypeEquivalence? context = null
    ) {
        foreach (var extension in protocol.OriginalDefinition.OptionalConformances) {
            var substitution = TypeSubstitution.For(extension.ExtendedType.Construct([optional.UnderlyingType]));
            if (extension.Unmet(substitution, context) is null) {
                return (extension, substitution);
            }
        }

        return null;
    }

    /// <summary>
    ///     The constrained extension a type's conformance to a protocol comes
    ///     from, directly or through a protocol that refines it; null when the
    ///     conformance, if there is one, holds unconditionally.
    /// </summary>
    static ExtensionSymbol? ConditionalConformance(NamedTypeSymbol type, TypeSymbol protocol) {
        foreach (var extension in type.OriginalDefinition.Extensions) {
            if (!extension.IsConstrained) {
                continue;
            }

            foreach (var conformance in extension.Conformances) {
                if (conformance.Equals(protocol) || IsDerivedFrom(conformance, protocol)) {
                    return extension;
                }
            }
        }

        return null;
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
            ConversionKind.ArrayView => 1,
            ConversionKind.MetatypeErasure => 2,
            ConversionKind.Open => 6,
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

    static bool IsDerivedFrom(TypeSymbol source, TypeSymbol target) =>
        Supertypes(source).Any(target.Equals);

    /// <summary>
    ///     Everything a type inherits from or conforms to, through every
    ///     level, with a construction's arguments read into them: a
    ///     <c>Rep&lt;Int&gt;</c> declared <c>Rep&lt;E&gt;: Source&lt;E&gt;</c> is a
    ///     <c>Source&lt;Int&gt;</c>.
    /// </summary>
    public static IEnumerable<TypeSymbol> Supertypes(TypeSymbol source) {
        if (source is not NamedTypeSymbol named) {
            yield break;
        }

        var visited = new HashSet<NamedTypeSymbol>();
        var queue = new Queue<NamedTypeSymbol>();
        queue.Enqueue(named);

        while (queue.Count > 0) {
            var current = queue.Dequeue();
            if (!visited.Add(current)) {
                continue;
            }

            var arguments = TypeSubstitution.For(current);
            foreach (var written in current.OriginalDefinition.BaseTypes) {
                var baseType = arguments.Apply(written);

                // An enum's base list names its underlying representation as
                // well as its protocols, and that is not a supertype: going
                // between an enum and its representation is written out.
                if (current.TypeKind == TypeKind.Enum
                    && baseType is NamedTypeSymbol { TypeKind: TypeKind.Builtin }) {
                    continue;
                }

                yield return baseType;

                if (baseType is NamedTypeSymbol baseNamed) {
                    queue.Enqueue(baseNamed);
                }
            }
        }
    }

    /// <summary>
    ///     True when a type conforms to a protocol its existential may be used
    ///     variantly for (docs/03 §3.4): a DogProducer, a
    ///     <c>Producer&lt;Dog&gt;</c>, is an <c>any Producer&lt;Animal&gt;</c>
    ///     when T is declared <c>out</c>, and an AnimalConsumer an
    ///     <c>any Consumer&lt;Dog&gt;</c> when it is <c>in</c>.
    /// </summary>
    /// <remarks>
    ///     Only an existential is variant: a generic parameter constrained to
    ///     <c>Producer&lt;Animal&gt;</c> is invariant, as every generic type is.
    /// </remarks>
    static bool ConformsVariantly(TypeSymbol source, TypeSymbol constraint) {
        if (constraint is not NamedTypeSymbol { TypeKind: TypeKind.Protocol, IsConstructed: true } wanted
            || !wanted.OriginalDefinition.TypeParameters.Any(static p => p.Variance != VarianceKind.None)) {
            return false;
        }

        foreach (var supertype in Supertypes(source)) {
            if (supertype is not NamedTypeSymbol { IsConstructed: true } candidate
                || !ReferenceEquals(candidate.OriginalDefinition, wanted.OriginalDefinition)) {
                continue;
            }

            var parameters = wanted.OriginalDefinition.TypeParameters;
            var fits = true;
            for (var i = 0; i < parameters.Length && fits; i++) {
                var had = candidate.TypeArguments[i];
                var asked = wanted.TypeArguments[i];
                fits = had.Equals(asked) || parameters[i].Variance switch {
                    VarianceKind.Out => IsDerivedFrom(had, asked),
                    VarianceKind.In => IsDerivedFrom(asked, had),
                    _ => false,
                };
            }

            if (fits) {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="constructed" /> is <paramref name="definition" /> applied to its own type parameters.</summary>
    static bool IsOwnConstruction(TypeSymbol definition, TypeSymbol constructed) =>
        definition is NamedTypeSymbol { TypeArguments.IsEmpty: true, TypeParameters.IsEmpty: false } generic
        && constructed is NamedTypeSymbol named
        && ReferenceEquals(named.OriginalDefinition, generic)
        && named.TypeArguments.SequenceEqual(generic.TypeParameters);

    static bool SameReferent(TypeSymbol source, TypeSymbol target) =>
        source is ReferenceTypeSymbol from
        && target is ReferenceTypeSymbol to
        && (from.RelativeWidth is not null || to.RelativeWidth is not null)
        && from.ReferencedType.Equals(to.ReferencedType);
}
