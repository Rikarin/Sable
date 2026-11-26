namespace Sable.Symbols;

/// <summary>
///     Which values are their bytes and nothing else (SYNTAX.md "Views of a
///     slice's bytes", docs/02 §2.5): the types a slice of one may be seen
///     as a slice of another as, with nothing copied.
/// </summary>
/// <remarks>
///     <para>
///         <c>PlainData</c> is met by what a type is made of, as Sendable is:
///         the integers and the floats, and a struct of them laid out with no
///         padding — every byte of it a byte of a field — with no deinit,
///         that is not <c>@noncopy</c> or <c>@inline_layout</c>; and an inline
///         array, <c>T[k]</c>, of one. Every pattern of bits is a value of
///         each, and a value holds nothing but its bits, so a run of one type's
///         values may be read and written as a run of another's.
///     </para>
///     <para>
///         Not a Bool or a Char, which not every pattern of bits is; not an
///         enum, a union or an optional, whose tag is not; not a class, an
///         array with a buffer, a closure or a reference, which hold an
///         address the bytes would let a program forge; not a tuple, whose
///         layout is not promised; and not a struct with padding, whose bytes
///         there are no one's. A type parameter is PlainData when it is
///         constrained to be.
///     </para>
///     <para>
///         The layout is C's (LlvmEmitter.Layout.cs): each field at the next
///         offset its alignment allows, the whole rounded up to its largest
///         alignment; a <c>@packed</c> struct's fields one after another, an
///         <c>@align(n)</c> one's size rounded up to n.
///     </para>
/// </remarks>
internal static class PlainData {
    /// <summary>True for the core's 'PlainData' protocol.</summary>
    public static bool IsPlainDataProtocol(TypeSymbol type) =>
        type is NamedTypeSymbol { Name: "PlainData", TypeKind: TypeKind.Protocol, ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } };

    /// <summary>A protocol that is PlainData, or refines it.</summary>
    public static bool Refines(TypeSymbol constraint) => Refines(constraint, []);

    static bool Refines(TypeSymbol constraint, HashSet<TypeSymbol> seen) =>
        constraint switch {
            _ when IsPlainDataProtocol(constraint) => true,
            CompositionTypeSymbol composition => composition.Parts.Any(p => Refines(p, seen)),
            NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol when seen.Add(protocol) => protocol.BaseTypes.Any(b => Refines(b, seen)),
            _ => false,
        };

    /// <summary>Why a type is not PlainData, or null when it is.</summary>
    public static string? Why(TypeSymbol type) => Layout(type, []).Why;

    /// <summary>A value's size and alignment, when it is PlainData; else why it is not.</summary>
    readonly record struct Shape(int Size, int Align, string? Why) {
        public static Shape Not(string why) => new(0, 0, why);
    }

    static Shape Layout(TypeSymbol type, HashSet<TypeSymbol> seen) {
        switch (type) {
            case ErrorTypeSymbol:
                return new(1, 1, null);

            case TypeParameterSymbol parameter:
                return parameter.Constraints.Any(Refines)
                    ? new(0, 0, null)
                    : Shape.Not($"'{parameter.Name}' is not constrained to be PlainData");

            case ArrayTypeSymbol { ArrayKind: ArrayKind.Fixed, Capacity: { } count } inline: {
                var element = Layout(inline.ElementType, seen);
                return element.Why is { } why
                    ? Shape.Not($"its elements are '{inline.ElementType.ToDisplayString()}': {why}")
                    : new(element.Size * count, element.Align, null);
            }

            case ArrayTypeSymbol array:
                return Shape.Not($"'{array.ToDisplayString()}' holds its elements in a buffer it refers to");

            case ReferenceTypeSymbol:
                return Shape.Not("a reference is an address");

            case FunctionTypeSymbol:
                return Shape.Not("a closure refers to what it captures");

            case OptionalTypeSymbol:
                return Shape.Not("an optional's tag is not any pattern of bits");

            case TupleTypeSymbol:
                return Shape.Not("a tuple's layout is not promised; declare a struct");

            case NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol:
                return Refines(protocol) ? new(0, 0, null) : Shape.Not($"'{protocol.ToDisplayString()}' does not refine PlainData");

            case NamedTypeSymbol named:
                return Named(named, seen);

            default:
                return Shape.Not($"'{type.ToDisplayString()}' is not made of plain bytes");
        }
    }

    static Shape Named(NamedTypeSymbol type, HashSet<TypeSymbol> seen) {
        if (type.TypeKind == TypeKind.Builtin && type.TypeArguments.IsEmpty) {
            return type.Name switch {
                "I8" or "U8" => new(1, 1, null),
                "I16" or "U16" or "F16" or "BF16" => new(2, 2, null),
                "I32" or "U32" or "F32" => new(4, 4, null),
                "I64" or "U64" or "F64" or "Int" or "UInt" => new(8, 8, null),
                "I128" or "U128" => new(16, 16, null),
                "Bool" => Shape.Not("not every byte is a Bool"),
                "Char" => Shape.Not("not every pattern of bits is a Char"),
                _ => Shape.Not($"'{type.Name}' is not made of plain bytes"),
            };
        }

        switch (type.TypeKind) {
            case TypeKind.Class or TypeKind.Actor:
                return Shape.Not($"'{type.ToDisplayString()}' is a class, which a value refers to");
            case TypeKind.Enum or TypeKind.Union:
                return Shape.Not($"'{type.ToDisplayString()}' is a choice, whose tag is not any pattern of bits");
            case not TypeKind.Struct:
                return Shape.Not($"'{type.ToDisplayString()}' is not made of plain bytes");
        }

        var definition = type.OriginalDefinition;
        if (definition.IsNoncopyable) {
            return Shape.Not($"'{type.ToDisplayString()}' is '@noncopy': a copy of its bytes would be a second one");
        }

        if (definition.IsInlineLayout) {
            return Shape.Not($"'{type.ToDisplayString()}' is '@inline_layout', its bytes laid out for its block");
        }

        if (definition.Members.OfType<FunctionSymbol>().Any(static f => f.FunctionKind == FunctionKind.Deinitializer)) {
            return Shape.Not($"'{type.ToDisplayString()}' has a deinit, which its bytes do not");
        }

        if (!seen.Add(definition)) {
            return Shape.Not($"'{type.ToDisplayString()}' holds itself");
        }

        try {
            var substitution = type.IsConstructed ? TypeSubstitution.For(type) : TypeSubstitution.Empty;
            var fields = definition.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false }).ToList();
            if (fields.Count == 0) {
                return Shape.Not($"'{type.ToDisplayString()}' has no bytes");
            }

            var offset = 0;
            var align = definition.IsPacked ? 1 : 0;
            var generic = false;
            foreach (var field in fields) {
                var fieldType = substitution.Apply(field.Type);
                var part = Layout(fieldType, seen);
                if (part.Why is { } why) {
                    return Shape.Not($"its field '{field.Name}' is '{fieldType.ToDisplayString()}': {why}");
                }

                // A type parameter's size is known only where it is given:
                // fields of the one type parameter alone have nothing between them.
                if (part.Size == 0) {
                    generic = true;
                    continue;
                }

                if (!definition.IsPacked) {
                    if (offset % part.Align != 0) {
                        return Shape.Not($"'{type.ToDisplayString()}' has padding before '{field.Name}', bytes that are no field's");
                    }

                    align = Math.Max(align, part.Align);
                }

                offset += part.Size;
            }

            if (generic) {
                var parameters = fields.Select(f => substitution.Apply(f.Type)).Distinct().ToList();
                return parameters.Count == 1 && parameters[0] is TypeParameterSymbol
                    ? new(0, 0, null)
                    : Shape.Not($"'{type.ToDisplayString()}' mixes fields whose sizes are known only where it is used, so whether it has padding is not known");
            }

            align = Math.Max(align, Math.Max(definition.Alignment, 1));
            if (offset % align != 0) {
                return Shape.Not($"'{type.ToDisplayString()}' has padding at its end, bytes that are no field's");
            }

            return new(offset, align, null);
        } finally {
            seen.Remove(definition);
        }
    }

    /// <summary>For a message that a type does not meet a constraint: why, where the constraint is PlainData.</summary>
    public static string Because(TypeSymbol type, TypeSymbol constraint) =>
        IsPlainDataProtocol(constraint) && Why(type) is { } why ? $": {why}" : "";

    /// <summary>
    ///     Why a type that declares <c>: PlainData</c>, or a protocol that
    ///     refines it, is not; null when it is.
    /// </summary>
    public static string? WhyNotAsDeclared(NamedTypeSymbol type) =>
        Why(type) is { } reason ? $"'{type.Name}' cannot be PlainData: {reason}" : null;
}
