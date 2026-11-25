using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
///     The built-in types, synthesised into a <c>Sable.Core</c> module that every
///     file sees without importing it.
/// </summary>
/// <remarks>
///     The types are the compiler's, because the binder, SIL lowering and code
///     generation all know them by sight: a literal's type, an integer's width,
///     a string's representation. Their members are not: those are Sable, in
///     <c>lib/Sable.Core</c> (<see cref="CoreLibrary" />): every one of them,
///     the array family's included (<c>Array.sb</c>, through array
///     extensions), with <c>@builtin</c> and <c>@intrinsic</c> bodies where
///     only the compiler can write one. What is declared here is the types
///     themselves, and the shells the binder desugars to.
/// </remarks>
public sealed class CoreTypes {
    internal CoreTypes(ModuleSymbol module) {
        Module = module;

        Bool = Primitive("Bool");

        I8 = Primitive("I8");
        I16 = Primitive("I16");
        I32 = Primitive("I32");
        I64 = Primitive("I64");
        I128 = Primitive("I128");
        Int = Primitive("Int");

        U8 = Primitive("U8");
        U16 = Primitive("U16");
        U32 = Primitive("U32");
        U64 = Primitive("U64");
        U128 = Primitive("U128");
        UInt = Primitive("UInt");

        F16 = Primitive("F16");
        BF16 = Primitive("BF16");
        F32 = Primitive("F32");
        F64 = Primitive("F64");

        Char = Primitive("Char");
        String = Primitive("String");

        // A bump-allocated arena (docs/02 §2.1, Tier 1): what 'region arena { }'
        // makes and an 'in r: region' parameter borrows.
        Region = Primitive("Region");

        // A type as a value (SYNTAX.md "Metatypes", docs/06 §6.2): what every
        // 'T.Type' converts to, and what 'type(of: x)' gives — a pointer at
        // the type's metadata, which says its kind, layout, value witnesses,
        // fields and conformances.
        Type = Primitive("Type");
        Void = Primitive("Void");
        Never = Primitive("Never");

        // What may cross an isolation boundary (SYNTAX.md §19, docs/02 §2.7):
        // a marker, which a type meets by what it is made of, not by saying so.
        Sendable = Known("Sendable") ?? new("Sendable", TypeKind.Protocol, Module, accessibility: Accessibility.Public);
        if (Known("Sendable") is null) {
            Module.AddMember(Sendable);
        }

        // 'extension AppError: From<IoError>' lets a 'try' in a function that
        // throws AppError propagate an IoError, converted (docs/03 §3.6).
        From = DeclareGeneric("From", TypeKind.Protocol, "Source");

        // 'Optional<T>' exists so that 'T?' has a nominal form to desugar to.
        // Its members, though, are lib/Sable.Core/Optional.sb's, on 'T?' itself.
        // ('[K: V]' desugars to Dictionary, which is Sable: see Dictionary below.)
        Optional = DeclareGeneric("Optional", TypeKind.Union, "Wrapped");
        Optional.ExtendedOptional ??= new OptionalTypeSymbol(Optional.TypeParameters[0]);

        Integers = [I8, I16, I32, I64, I128, Int, U8, U16, U32, U64, U128, UInt];
        Floats = [F16, BF16, F32, F64];
        Numerics = [..Integers, ..Floats];
    }

    public ModuleSymbol Module { get; }

    // Declared in Sable, in lib/Sable.Core, and known here by name, since
    // lowering gives their '@intrinsic' members bodies. Null before the core
    // is declared, or when a Sable.Core read from disk lacks them.

    /// <summary><c>Task&lt;Success&gt;</c>: a task and, once it finishes, its result (<c>Task.sb</c>).</summary>
    public NamedTypeSymbol? Task => Declared("Task", TypeKind.Class);

    /// <summary><c>Decoded&lt;Value&gt;</c>: what <c>fromBytes</c> gives (<c>InlineLayout.sb</c>).</summary>
    public NamedTypeSymbol? Decoded => Declared("Decoded", TypeKind.Union);

    /// <summary><c>Bytes</c>: where <c>Bytes.of</c> lives (<c>InlineLayout.sb</c>).</summary>
    public NamedTypeSymbol? Bytes => Declared("Bytes", TypeKind.Struct);

    /// <summary><c>Attribute</c>: what a user-defined attribute's struct conforms to (<c>Attribute.sb</c>).</summary>
    public NamedTypeSymbol? Attribute => Declared("Attribute", TypeKind.Protocol);

    /// <summary><c>KeyPath&lt;Root, Value&gt;</c>: what <c>\.name</c> is (<c>KeyPath.sb</c>).</summary>
    public NamedTypeSymbol? KeyPath => Declared("KeyPath", TypeKind.Struct);

    /// <summary><c>InlineLayout</c>: what every <c>@inline_layout</c> struct conforms to (<c>InlineLayout.sb</c>).</summary>
    public NamedTypeSymbol? InlineLayout => Declared("InlineLayout", TypeKind.Protocol);

    /// <summary><c>Formattable</c>: what an interpolation writes a value through (<c>Formattable.sb</c>).</summary>
    public NamedTypeSymbol? Formattable => Declared("Formattable", TypeKind.Protocol);

    /// <summary><c>FormatSpec</c>: what an interpolation's format spec becomes (<c>FormatSpec.sb</c>).</summary>
    public NamedTypeSymbol? FormatSpec => Declared("FormatSpec", TypeKind.Struct);

    NamedTypeSymbol? Declared(string name, TypeKind kind) =>
        Module.GetType(name) is { } type && type.TypeKind == kind ? type : null;

    public NamedTypeSymbol Bool { get; }
    public NamedTypeSymbol I8 { get; }
    public NamedTypeSymbol I16 { get; }
    public NamedTypeSymbol I32 { get; }
    public NamedTypeSymbol I64 { get; }
    public NamedTypeSymbol I128 { get; }
    public NamedTypeSymbol Int { get; }
    public NamedTypeSymbol U8 { get; }
    public NamedTypeSymbol U16 { get; }
    public NamedTypeSymbol U32 { get; }
    public NamedTypeSymbol U64 { get; }
    public NamedTypeSymbol U128 { get; }
    public NamedTypeSymbol UInt { get; }
    public NamedTypeSymbol F16 { get; }
    public NamedTypeSymbol BF16 { get; }
    public NamedTypeSymbol F32 { get; }
    public NamedTypeSymbol F64 { get; }
    public NamedTypeSymbol Char { get; }
    public NamedTypeSymbol String { get; }
    public NamedTypeSymbol Region { get; }
    /// <summary><c>Type</c>: the existential metatype, a type known only at run time, as its metadata.</summary>
    public NamedTypeSymbol Type { get; }
    public NamedTypeSymbol Void { get; }
    public NamedTypeSymbol Never { get; }
    public NamedTypeSymbol Sendable { get; }

    /// <summary>
    ///     <c>Error</c>, what is thrown: declared in Sable, in
    ///     <c>lib/Sable.Core/Error.sb</c>, with its requirements; bare
    ///     <c>throws</c> means <c>throws(any Error)</c>, per SYNTAX.md §18.
    /// </summary>
    /// <remarks>
    ///     Found once the core is declared, as <see cref="Sequence" /> is. A
    ///     Sable.Core brought from disk without one gets a marker protocol in
    ///     its place, so that 'throws' still means something.
    /// </remarks>
    public NamedTypeSymbol Error => _error ??= DeclaredProtocol("Error") ?? MarkerError();

    /// <summary>What a bare <c>throws</c> means.</summary>
    public TypeSymbol AnyError => _anyError ??= new ExistentialTypeSymbol(Error);

    NamedTypeSymbol? _error;
    TypeSymbol? _anyError;

    NamedTypeSymbol MarkerError() {
        var marker = new NamedTypeSymbol("Error", TypeKind.Protocol, Module, accessibility: Accessibility.Public);
        Module.AddMember(marker);
        return marker;
    }

    /// <summary><c>From&lt;Source&gt;</c>: the conversion a <c>try</c> applies between error types.</summary>
    public NamedTypeSymbol From { get; }

    /// <summary>
    ///     <c>Range&lt;Bound&gt;</c>, what <c>a..&lt;b</c> makes, and
    ///     <c>ClosedRange&lt;Bound&gt;</c>, what <c>a...b</c> makes: declared in
    ///     <c>lib/Sable.Core/Range.sb</c> and <c>ClosedRange.sb</c>, so null only in a compilation that
    ///     brings a Sable.Core of its own without them, or with one not
    ///     generic over a single bound.
    /// </summary>
    public NamedTypeSymbol? Range => _range ??= DeclaredRange("Range");

    /// <inheritdoc cref="Range" />
    public NamedTypeSymbol? ClosedRange => _closedRange ??= DeclaredRange("ClosedRange");

    /// <summary>
    ///     The one-sided ranges: <c>PartialRangeUpTo</c> for <c>..&lt;b</c>,
    ///     <c>PartialRangeThrough</c> for <c>...b</c> and
    ///     <c>PartialRangeFrom</c> for <c>a...</c>, declared beside the others.
    /// </summary>
    public NamedTypeSymbol? PartialRangeUpTo => _upTo ??= DeclaredRange("PartialRangeUpTo");

    /// <inheritdoc cref="PartialRangeUpTo" />
    public NamedTypeSymbol? PartialRangeThrough => _through ??= DeclaredRange("PartialRangeThrough");

    /// <inheritdoc cref="PartialRangeUpTo" />
    public NamedTypeSymbol? PartialRangeFrom => _from ??= DeclaredRange("PartialRangeFrom");

    NamedTypeSymbol? _range, _closedRange, _upTo, _through, _from;

    /// <summary>
    ///     <c>StrideTo</c> and <c>StrideThrough</c>, what <c>stride(from:to:by:)</c>
    ///     and <c>stride(from:through:by:)</c> make: declared in
    ///     <c>lib/Sable.Core/Stride.sb</c> as structs of Int fields <c>start</c>,
    ///     <c>end</c> and <c>step</c>. A <c>for</c> over one counts, as over a
    ///     range; null where Sable.Core declares them of some other shape.
    /// </summary>
    public NamedTypeSymbol? StrideTo => _strideTo ??= DeclaredStride("StrideTo");

    /// <inheritdoc cref="StrideTo" />
    public NamedTypeSymbol? StrideThrough => _strideThrough ??= DeclaredStride("StrideThrough");

    NamedTypeSymbol? _strideTo, _strideThrough;

    NamedTypeSymbol? DeclaredStride(string name) =>
        DeclaredStruct(name) is { TypeParameters.IsEmpty: true } type
        && ((string[])["start", "end", "step"]).All(field => type.Members.OfType<FieldSymbol>().Any(f => f.Name == field && !f.IsStatic && f.Type.Equals(Int)))
            ? type
            : null;

    /// <summary>True for a <c>StrideTo</c> or a <c>StrideThrough</c>.</summary>
    public bool IsStride(TypeSymbol type) =>
        type is NamedTypeSymbol { TypeArguments.IsEmpty: true } named
        && (named.Equals(StrideTo) || named.Equals(StrideThrough));

    /// <summary>
    ///     <c>FromEnd</c>, what <c>^n</c> makes: a position counted back from
    ///     a collection's end, declared in <c>lib/Sable.Core/FromEnd.sb</c>.
    ///     Null only in a compilation that brings a Sable.Core of its own
    ///     without it, or with one not a struct of one <c>n: Int</c>.
    /// </summary>
    public NamedTypeSymbol? FromEnd => _fromEnd ??= DeclaredStruct("FromEnd") is { TypeParameters.IsEmpty: true } type
        && type.Members.OfType<FieldSymbol>().Any(static f => f is { Name: "n", IsStatic: false })
            ? type
            : null;

    /// <summary>
    ///     <c>RelativeRange</c>, what a range makes when a bound is counted
    ///     from the end, <c>1..&lt;^1</c>: declared in <c>lib/Sable.Core/RelativeRange.sb</c>.
    /// </summary>
    public NamedTypeSymbol? RelativeRange => _relativeRange ??= DeclaredStruct("RelativeRange") is { TypeParameters.IsEmpty: true } type ? type : null;

    /// <summary>
    ///     <c>RangeExpression</c>, what a collection's one slicing subscript
    ///     takes: every range, and a range counted from the end.
    /// </summary>
    public NamedTypeSymbol? RangeExpression => _rangeExpression ??= DeclaredProtocol("RangeExpression");

    /// <summary><c>Collection</c>, whose <c>Index</c> a range of indices must be to slice one.</summary>
    public NamedTypeSymbol? Collection => _collection ??= DeclaredProtocol("Collection");

    NamedTypeSymbol? _fromEnd, _relativeRange, _rangeExpression, _collection;

    /// <summary>True for <c>FromEnd</c>, what <c>^n</c> makes.</summary>
    public bool IsFromEnd(TypeSymbol type) => FromEnd is { } fromEnd && type.Equals(fromEnd);

    /// <summary>True for a range a loop can count through: one with a lower bound, <c>Range</c>, <c>ClosedRange</c> or <c>PartialRangeFrom</c>.</summary>
    public bool IsCountableRange(TypeSymbol type) =>
        IsRange(type) || type is NamedTypeSymbol { TypeArguments.Length: 1 } named && named.OriginalDefinition.Equals(PartialRangeFrom);

    /// <summary>True for a range of any shape, one-sided ones included.</summary>
    public bool IsAnyRange(TypeSymbol type) =>
        IsCountableRange(type)
        || type is NamedTypeSymbol { TypeArguments.Length: 1 } named
        && (named.OriginalDefinition.Equals(PartialRangeUpTo) || named.OriginalDefinition.Equals(PartialRangeThrough));

    /// <summary>
    ///     <c>Sequence</c> and <c>AsyncSequence</c>, what <c>for</c> and
    ///     <c>for await</c> go through when the compiler does not know the
    ///     type by sight: declared in <c>lib/Sable.Core/Sequence.sb</c>.
    /// </summary>
    public NamedTypeSymbol? Sequence => _sequence ??= DeclaredProtocol("Sequence");

    /// <inheritdoc cref="Sequence" />
    public NamedTypeSymbol? AsyncSequence => _asyncSequence ??= DeclaredProtocol("AsyncSequence");

    NamedTypeSymbol? _sequence, _asyncSequence;

    NamedTypeSymbol? DeclaredStruct(string name) =>
        Module.GetType(name) is { TypeKind: TypeKind.Struct } type ? type : null;

    /// <summary>
    ///     A range type is a struct of one bound, <c>Range&lt;Bound&gt;</c>: a
    ///     Sable.Core that declares a <c>Range</c> of some other shape has no
    ///     range the operators can make, just as one declaring none has not.
    /// </summary>
    NamedTypeSymbol? DeclaredRange(string name) =>
        DeclaredStruct(name) is { TypeParameters.Length: 1 } type ? type : null;

    NamedTypeSymbol? DeclaredProtocol(string name) =>
        Module.GetType(name) is { TypeKind: TypeKind.Protocol } type ? type : null;

    /// <summary>True for a <c>Range</c> or <c>ClosedRange</c> of some bound.</summary>
    public bool IsRange(TypeSymbol type) =>
        type is NamedTypeSymbol { TypeArguments.Length: 1 } named
        && (named.OriginalDefinition.Equals(Range) || named.OriginalDefinition.Equals(ClosedRange));

    public NamedTypeSymbol Optional { get; }

    /// <summary>
    ///     <c>Dictionary&lt;Key, Value&gt;</c>, what <c>[Key: Value]</c> names:
    ///     a hash table declared in <c>lib/Sable.Core/Dictionary.sb</c>, null
    ///     only in a compilation that brings a Sable.Core of its own without it.
    /// </summary>
    public NamedTypeSymbol? Dictionary => _dictionary ??= DeclaredStruct("Dictionary");

    NamedTypeSymbol? _dictionary;

    /// <summary>True for a <c>[Key: Value]</c> of some key and value.</summary>
    public bool IsDictionary(TypeSymbol type) =>
        type is NamedTypeSymbol named && Dictionary is { } dictionary && named.OriginalDefinition.Equals(dictionary);

    /// <summary>
    ///     <c>SIMD&lt;Scalar, N&gt;</c> and <c>SIMDMask&lt;N&gt;</c>, declared in
    ///     <c>lib/Sable.Core/SIMD.sb</c> and lowered to vectors of N lanes:
    ///     null in a compilation whose Sable.Core has none.
    /// </summary>
    public NamedTypeSymbol? Simd => _simd ??= DeclaredStruct("SIMD");

    public NamedTypeSymbol? SimdMask => _simdMask ??= DeclaredStruct("SIMDMask");

    NamedTypeSymbol? _simd;
    NamedTypeSymbol? _simdMask;

    /// <summary>The lane type and the number of lanes of a <c>SIMD&lt;T, N&gt;</c> (a mask's lanes are Bools), or null for any other type.</summary>
    public (TypeSymbol Lane, long Count)? SimdLanes(TypeSymbol type) {
        if (type is not NamedTypeSymbol { IsConstructed: true } named) {
            return null;
        }

        if (Simd is { } simd && named.OriginalDefinition.Equals(simd) && named.TypeArguments is [var lane, ValueTypeArgumentSymbol { Value: var count }]) {
            return (lane, count);
        }

        if (SimdMask is { } mask && named.OriginalDefinition.Equals(mask) && named.TypeArguments is [ValueTypeArgumentSymbol { Value: var lanes }]) {
            return (Bool, lanes);
        }

        return null;
    }

    public ImmutableArray<NamedTypeSymbol> Integers { get; }
    public ImmutableArray<NamedTypeSymbol> Floats { get; }
    public ImmutableArray<NamedTypeSymbol> Numerics { get; }

    public bool IsNumeric(TypeSymbol type) => Numerics.Any(n => n.Equals(type));
    public bool IsInteger(TypeSymbol type) => Integers.Any(n => n.Equals(type));

    /// <summary>The values an integer type holds, least and greatest; null for a type that is not one.</summary>
    public (System.Numerics.BigInteger Low, System.Numerics.BigInteger High)? IntegerRange(TypeSymbol type) {
        var index = type is NamedTypeSymbol named ? Integers.IndexOf(named) : -1;
        if (index < 0) {
            return null;
        }

        // I8 … I128, Int; then U8 … U128, UInt: 'Int' and 'UInt' are 64 bits.
        int[] bits = [8, 16, 32, 64, 128, 64];
        var width = bits[index % 6];
        var one = System.Numerics.BigInteger.One;
        return index < 6 ? (-(one << (width - 1)), (one << (width - 1)) - 1) : (0, (one << width) - 1);
    }

    /// <summary>A type the module has already, its symbols read from its interface.</summary>
    NamedTypeSymbol? Known(string name) => Module.GetType(name);

    NamedTypeSymbol Primitive(string name) {
        if (Known(name) is { } known) {
            return known;
        }

        var type = new NamedTypeSymbol(name, TypeKind.Builtin, Module, accessibility: Accessibility.Public);
        Module.AddMember(type);
        return type;
    }

    NamedTypeSymbol DeclareGeneric(
        string name,
        TypeKind typeKind,
        params string[] typeParameterNames
    ) {
        if (Known(name) is { } known) {
            return known;
        }

        var type = new NamedTypeSymbol(name, typeKind, Module, accessibility: Accessibility.Public);

        type.TypeParameters = [
            ..typeParameterNames.Select((parameterName, i) => new TypeParameterSymbol(parameterName, type, null, i))
        ];

        Module.AddMember(type);
        return type;
    }
}
