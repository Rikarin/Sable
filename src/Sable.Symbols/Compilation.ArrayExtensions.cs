using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     <c>extension&lt;Element&gt; Element[&gt;..] { … }</c>: members for every
///     array of a kind, whatever its element.
/// </summary>
/// <remarks>
///     <para>
///         An array type has no name to write parameters after, as
///         <c>extension Box&lt;T&gt;</c> does, so an array extension declares
///         them after the keyword, as a generic function does after
///         <c>fn</c>. Each kind of array is extended on its own —
///         <c>Element[]</c> (every fixed size), <c>Element[..]</c> (every
///         inline capacity), <c>Element[&gt;..]</c>, <c>Element[&gt;..&lt;]</c>
///         — except the slice: every array can be viewed as one, so what
///         <c>Element[:]</c> declares, every kind has. The element may also be
///         a type rather than a parameter (<c>extension U8[:]</c>), and the
///         parameters may be constrained (<c>extension&lt;Element: Equatable&gt;</c>,
///         or with <c>where</c>): the members are an array's only where its
///         element meets them.
///     </para>
///     <para>
///         The members belong to a type made for the extension: generic over
///         its parameters, unnamed, holding the array type it extends
///         (<see cref="NamedTypeSymbol.ExtendedArray" />). Inside, <c>self</c>
///         is that array; outside, looking a member up on an array matches the
///         array against each extension's and substitutes what it finds, so a
///         member is a generic one like any other, monomorphised per element.
///     </para>
/// </remarks>
public sealed partial class Compilation {
    /// <summary>The types the array extensions declare their members on, in declaration order.</summary>
    internal List<NamedTypeSymbol> ArrayExtensions { get; } = [];

    /// <summary>
    ///     The types array extensions declare their members on, one for each
    ///     <c>extension T[]</c>: no module lists them among its members, so
    ///     what walks the program's declarations by its symbols walks these too.
    /// </summary>
    public IReadOnlyList<NamedTypeSymbol> ArrayCarriers => ArrayExtensions;

    static TypeSyntax Unparenthesised(TypeSyntax syntax) {
        while (syntax is ParenthesizedTypeSyntax parenthesized) {
            syntax = parenthesized.Type;
        }

        return syntax;
    }

    /// <summary>A named type's name as written, without its arguments.</summary>
    static string WrittenName(TypeSyntax syntax) =>
        syntax switch {
            SimpleNameTypeSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => $"{qualified.Left.ToString().Trim()}.{qualified.Right.Identifier.Text}",
            _ => syntax.ToString().Trim(),
        };

    static bool IsArrayExtension(ExtensionDeclarationSyntax declaration) =>
        Unparenthesised(declaration.Type) is FixedSizeArrayTypeSyntax or InlineArrayTypeSyntax or GrowArrayTypeSyntax or SliceTypeSyntax;

    void AttachArrayExtension(ExtensionDeclarationSyntax declaration, FileScope? scope) {
        var syntax = Unparenthesised(declaration.Type);
        var module = (Symbol?)scope?.Module ?? Core.Module;

        // A capacity would make an extension of one size of array; the
        // members are every size's.
        var size = syntax switch {
            FixedSizeArrayTypeSyntax fixedSize => fixedSize.Size,
            InlineArrayTypeSyntax inline => inline.Capacity,
            _ => null,
        };

        if (size is not null) {
            ReportDeclaration(
                ErrorCode.ERR_MalformedArrayExtension,
                size.Span,
                "an array extension is for every size of the array: write 'T[]' or 'T[..]', without one"
            );
        }

        // Named for its kind, and numbered within its module: the name is what
        // its members' code is named for, never written or shown.
        var kind = syntax switch {
            FixedSizeArrayTypeSyntax => ArrayKind.Fixed,
            InlineArrayTypeSyntax => ArrayKind.Inline,
            GrowArrayTypeSyntax { RangeToken.Kind: SyntaxKind.DotDotLessToken } => ArrayKind.GrowShrink,
            GrowArrayTypeSyntax => ArrayKind.Grow,
            _ => ArrayKind.Slice,
        };
        var ordinal = ArrayExtensions.Count(c => ReferenceEquals(c.ContainingSymbol, module) && c.ExtendedArray?.ArrayKind == kind);
        var carrier = new NamedTypeSymbol(
            $"Array${kind}${ordinal}",
            TypeKind.Struct,
            module,
            declaration,
            Accessibility.Public
        );

        if (declaration.TypeParameters is { } typeParameters) {
            carrier.TypeParameters = [
                ..typeParameters.Parameters.Select((parameter, i) => new TypeParameterSymbol(parameter.Identifier.Text, carrier, parameter, i))
            ];
        }

        var pattern = TypeBinder.BindType(syntax, carrier, scope);
        if (pattern is not ArrayTypeSymbol array) {
            return;
        }

        carrier.ExtendedArray = new ArrayTypeSymbol(array.ElementType, array.ArrayKind);

        // Each parameter is the element, or in it: one the array does not
        // mention could never be inferred.
        foreach (var parameter in carrier.TypeParameters) {
            if (!Mentions(array.ElementType, parameter)) {
                ReportDeclaration(
                    ErrorCode.ERR_MalformedArrayExtension,
                    parameter.DeclaringSyntax[0].Span,
                    $"'{parameter.Name}' is not in '{syntax.ToString().Trim()}', so nothing could say what it is"
                );
            }
        }

        TypeBinder.BindArrayExtension(carrier, declaration, scope);

        if (declaration.BaseTypes is { } baseTypes) {
            DeclareArrayConformances(carrier, baseTypes, scope);
        }

        var extension = new ExtensionSymbol(carrier, declaration) { DeclaringModule = scope?.Module };
        RecordDeclaration(declaration, extension);
        ArrayExtensions.Add(carrier);

        // An initialiser, in an extension of every growable array of a
        // kind: such an array always has a value to begin from, '[]'.
        var growable = carrier.ExtendedArray.ArrayKind is ArrayKind.Grow or ArrayKind.GrowShrink;
        foreach (var member in declaration.Members) {
            if (!(growable && member is InitDeclarationSyntax) && RejectInExtension(member)) {
                continue;
            }

            DeclareMember(member, extension);
        }
    }

    /// <summary>
    ///     <c>extension&lt;Element&gt; Element[:]: Sequence</c>: every array of
    ///     the kind conforms, where its element meets the extension's
    ///     constraints. The protocols are bound now, as every type has been
    ///     declared, so that whether an array conforms can be asked as soon
    ///     as any signature is bound; each protocol, and each it refines,
    ///     keeps the extension, as an array has no declaration to keep them.
    /// </summary>
    void DeclareArrayConformances(NamedTypeSymbol carrier, BaseTypeListSyntax baseTypes, FileScope? scope) {
        var protocols = new List<TypeSymbol>();
        foreach (var syntax in baseTypes.Types) {
            var bound = TypeBinder.BindType(syntax, carrier, scope);
            if (bound.IsError) {
                continue;
            }

            if (bound is not NamedTypeSymbol { TypeKind: TypeKind.Protocol }) {
                ReportDeclaration(
                    ErrorCode.ERR_MalformedArrayExtension,
                    syntax.Span,
                    $"an array can conform to a protocol, and '{bound.ToDisplayString()}' is not one"
                );
                continue;
            }

            protocols.Add(bound);
        }

        carrier.BaseTypes = [..protocols];

        var registered = new HashSet<NamedTypeSymbol>();
        var pending = new Stack<NamedTypeSymbol>(protocols.OfType<NamedTypeSymbol>());
        while (pending.Count > 0) {
            var protocol = pending.Pop().OriginalDefinition;
            if (!registered.Add(protocol)) {
                continue;
            }

            protocol.ArrayConformers = protocol.ArrayConformers.Add(carrier);
            foreach (var refined in protocol.BaseTypes) {
                if (refined is NamedTypeSymbol { TypeKind: TypeKind.Protocol } parent) {
                    pending.Push(parent);
                }
            }
        }

        // A requirement may be met by what any array extension declares, as
        // 'count' is by the slice's. What the conformance asks is checked
        // once every signature is bound, as a named type's is.
        FindArrayMembers(carrier);
        TypeBinder.DeferConformanceCheck(carrier);
    }

    /// <summary>
    ///     What may meet a conformance's requirements: the conforming module's
    ///     own members before another's, which is an extension of arrays it
    ///     does not know, and does not change what its conformance means.
    /// </summary>
    void FindArrayMembers(NamedTypeSymbol carrier) =>
        carrier.ArrayMembersNamed = name =>
            carrier.ExtendedArray is { } extended && LookupArrayMember(extended, name) is { Owner: not null } found
                ? found.Members.OrderBy(member => ReferenceEquals(ArrayModuleOf(member), carrier.ContainingSymbol) ? 0 : 1)
                : [];

    static Symbol? ArrayModuleOf(Symbol member) =>
        member.ContainingSymbol switch {
            ExtensionSymbol extension => extension.ExtendedType.ContainingSymbol,
            var owner => owner?.ContainingSymbol,
        };

    /// <summary>
    ///     The array extension that conforms this array to the protocol, and
    ///     what its parameters stand for here; null when the array does not
    ///     conform, or its element does not meet the extension's constraints.
    /// </summary>
    internal static (NamedTypeSymbol Carrier, TypeSubstitution Substitution)? ArrayConformance(
        ArrayTypeSymbol array,
        NamedTypeSymbol protocol,
        TypeEquivalence? context = null
    ) {
        foreach (var carrier in protocol.OriginalDefinition.ArrayConformers) {
            if (MatchArray(carrier, array) is { } substitution && Unmet(carrier, substitution, context) is null) {
                return (carrier, substitution);
            }
        }

        return null;
    }

    static bool Mentions(TypeSymbol type, TypeParameterSymbol parameter) =>
        TypeSubstitution.Mentions(type, parameter);

    /// <summary>
    ///     The members an array has by this name, from its kind's extensions or
    ///     else the slice's: what implements a protocol's requirement on it.
    /// </summary>
    public IReadOnlyList<Symbol> ArrayMembersNamed(ArrayTypeSymbol array, string name) =>
        LookupArrayMember(array, name) is { Owner: not null } found ? found.Members : [];

    /// <summary>
    ///     The members of an array with this name: the array kind's own
    ///     extensions' if they have one, otherwise the slice's, which every
    ///     array has.
    /// </summary>
    /// <remarks>
    ///     <see cref="ArrayMemberLookup.Unmet" /> names the requirement a
    ///     member's element did not meet, when that is all that kept it from
    ///     being found.
    /// </remarks>
    internal ArrayMemberLookup? LookupArrayMember(ArrayTypeSymbol array, string name, TypeEquivalence? context = null) {
        string? unmet = null;
        foreach (var slices in (bool[])[false, true]) {
            var members = new List<Symbol>();
            NamedTypeSymbol? owner = null;
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();

            foreach (var carrier in ArrayExtensions) {
                if (carrier.ExtendedArray is not { } pattern
                    || (slices ? pattern.ArrayKind != ArrayKind.Slice || array.ArrayKind == ArrayKind.Slice : pattern.ArrayKind != array.ArrayKind)) {
                    continue;
                }

                var named = carrier.GetMembers(name).ToList();
                if (named.Count == 0 || MatchArray(carrier, array) is not { } substitution) {
                    continue;
                }

                if (Unmet(carrier, substitution, context) is { } requirement) {
                    unmet ??= requirement;
                    continue;
                }

                members.AddRange(named);
                owner ??= carrier;
                foreach (var parameter in substitution.Parameters) {
                    map[parameter] = substitution.Lookup(parameter)!;
                }
            }

            if (owner is not null) {
                return new(members, owner, new(map), null);
            }
        }

        return unmet is null ? null : new([], null, TypeSubstitution.Empty, unmet);
    }

    /// <summary>Every member an array has, for completion.</summary>
    /// <remarks>The kind's own first, as lookup finds them before the slice's.</remarks>
    internal IEnumerable<Symbol> ArrayMembers(ArrayTypeSymbol array) {
        foreach (var carrier in ArrayExtensions.OrderBy(c => c.ExtendedArray?.ArrayKind == array.ArrayKind ? 0 : 1)) {
            if (MatchArray(carrier, array) is not { } substitution
                || Unmet(carrier, substitution, null) is not null) {
                continue;
            }

            foreach (var member in carrier.Members) {
                yield return member;
            }
        }
    }

    /// <summary>The first constraint of the extension the element does not meet, said as the extension says it.</summary>
    static string? Unmet(NamedTypeSymbol carrier, TypeSubstitution substitution, TypeEquivalence? context) {
        foreach (var parameter in carrier.TypeParameters) {
            if (substitution.Lookup(parameter) is not { } argument) {
                continue;
            }

            foreach (var constraint in parameter.Constraints) {
                if (!Conversions.Satisfies(argument, substitution.Apply(constraint), context)) {
                    return $"'{parameter.Name}: {constraint.ToDisplayString()}', and '{argument.ToDisplayString()}' does not conform{PlainData.Because(argument, constraint)}";
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     What an array extension's parameters stand for on this array, or
    ///     null when the extension is not for it: the kinds must agree — a
    ///     slice's extension is every kind's — and the elements match.
    /// </summary>
    public static TypeSubstitution? MatchArray(NamedTypeSymbol carrier, TypeSymbol type) {
        if (carrier.OriginalDefinition.ExtendedArray is not { } pattern || type is not ArrayTypeSymbol array
            || pattern.ArrayKind != array.ArrayKind && pattern.ArrayKind != ArrayKind.Slice) {
            return null;
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        return Unify(pattern.ElementType, array.ElementType, carrier.OriginalDefinition, map) ? new(map) : null;
    }

    static bool Unify(TypeSymbol pattern, TypeSymbol actual, NamedTypeSymbol carrier, Dictionary<TypeParameterSymbol, TypeSymbol> map) {
        switch (pattern) {
            case TypeParameterSymbol parameter when ReferenceEquals(parameter.ContainingSymbol, carrier):
                if (map.TryGetValue(parameter, out var bound)) {
                    return bound.Equals(actual);
                }

                map[parameter] = actual;
                return true;

            case ArrayTypeSymbol array when actual is ArrayTypeSymbol other && other.ArrayKind == array.ArrayKind:
                return Unify(array.ElementType, other.ElementType, carrier, map);

            case OptionalTypeSymbol optional when actual is OptionalTypeSymbol other:
                return Unify(optional.UnderlyingType, other.UnderlyingType, carrier, map);

            case TupleTypeSymbol tuple when actual is TupleTypeSymbol other && other.ElementTypes.Length == tuple.ElementTypes.Length:
                for (var i = 0; i < tuple.ElementTypes.Length; i++) {
                    if (!Unify(tuple.ElementTypes[i], other.ElementTypes[i], carrier, map)) {
                        return false;
                    }
                }

                return true;

            case NamedTypeSymbol { IsConstructed: true } named when actual is NamedTypeSymbol { IsConstructed: true } other
                && ReferenceEquals(named.OriginalDefinition, other.OriginalDefinition):
                for (var i = 0; i < named.TypeArguments.Length; i++) {
                    if (!Unify(named.TypeArguments[i], other.TypeArguments[i], carrier, map)) {
                        return false;
                    }
                }

                return true;

            default:
                return pattern.Equals(actual);
        }
    }
}

/// <summary>What looking a member up on an array found, or the requirement that kept it from being found.</summary>
internal readonly record struct ArrayMemberLookup(
    IReadOnlyList<Symbol> Members,
    NamedTypeSymbol? Owner,
    TypeSubstitution Substitution,
    string? Unmet);
