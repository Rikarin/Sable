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
        F32 = Primitive("F32");
        F64 = Primitive("F64");

        Char = Primitive("Char");
        String = Primitive("String");

        // A bump-allocated arena (docs/02 §2.1, Tier 1): what 'region arena { }'
        // makes and an 'in r: region' parameter borrows.
        Region = Primitive("Region");
        Void = Primitive("Void");
        Never = Primitive("Never");

        // Every thrown value conforms to Error; bare 'throws' means
        // 'throws(any Error)', per SYNTAX.md §18.
        Error = new("Error", TypeKind.Protocol, Module, accessibility: Accessibility.Public);
        Module.AddMember(Error);
        AnyError = new ExistentialTypeSymbol(Error);

        // What may cross an isolation boundary (SYNTAX.md §19, docs/02 §2.7):
        // a marker, which a type meets by what it is made of, not by saying so.
        Sendable = new("Sendable", TypeKind.Protocol, Module, accessibility: Accessibility.Public);
        Module.AddMember(Sendable);

        // 'extension AppError: From<IoError>' lets a 'try' in a function that
        // throws AppError propagate an IoError, converted (docs/03 §3.6).
        From = DeclareGeneric("From", TypeKind.Protocol, "Source");

        // 'Optional<T>' exists so that 'T?' has a nominal form to desugar to.
        Optional = DeclareGeneric("Optional", TypeKind.Union, "Wrapped");
        Dictionary = DeclareGeneric("Dictionary", TypeKind.Struct, "Key", "Value");

        Integers = [I8, I16, I32, I64, I128, Int, U8, U16, U32, U64, U128, UInt];
        Floats = [F16, F32, F64];
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

    /// <summary><c>InlineLayout</c>: what every <c>@inline_layout</c> struct conforms to (<c>InlineLayout.sb</c>).</summary>
    public NamedTypeSymbol? InlineLayout => Declared("InlineLayout", TypeKind.Protocol);

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
    public NamedTypeSymbol F32 { get; }
    public NamedTypeSymbol F64 { get; }
    public NamedTypeSymbol Char { get; }
    public NamedTypeSymbol String { get; }
    public NamedTypeSymbol Region { get; }
    public NamedTypeSymbol Void { get; }
    public NamedTypeSymbol Never { get; }
    public NamedTypeSymbol Error { get; }
    public NamedTypeSymbol Sendable { get; }

    /// <summary>What a bare <c>throws</c> means.</summary>
    public TypeSymbol AnyError { get; }

    /// <summary><c>From&lt;Source&gt;</c>: the conversion a <c>try</c> applies between error types.</summary>
    public NamedTypeSymbol From { get; }

    /// <summary>
    ///     <c>Range&lt;Bound&gt;</c>, what <c>a..&lt;b</c> makes, and
    ///     <c>ClosedRange&lt;Bound&gt;</c>, what <c>a...b</c> makes: declared in
    ///     <c>lib/Sable.Core/Range.sb</c>, so null only in a compilation that
    ///     brings a Sable.Core of its own without them.
    /// </summary>
    public NamedTypeSymbol? Range => _range ??= DeclaredStruct("Range");

    /// <inheritdoc cref="Range" />
    public NamedTypeSymbol? ClosedRange => _closedRange ??= DeclaredStruct("ClosedRange");

    NamedTypeSymbol? _range, _closedRange;

    NamedTypeSymbol? DeclaredStruct(string name) =>
        Module.GetType(name) is { TypeKind: TypeKind.Struct } type ? type : null;

    /// <summary>True for a <c>Range</c> or <c>ClosedRange</c> of some bound.</summary>
    public bool IsRange(TypeSymbol type) =>
        type is NamedTypeSymbol { TypeArguments.Length: 1 } named
        && (named.OriginalDefinition.Equals(Range) || named.OriginalDefinition.Equals(ClosedRange));

    public NamedTypeSymbol Optional { get; }
    public NamedTypeSymbol Dictionary { get; }

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

    NamedTypeSymbol Primitive(string name) {
        var type = new NamedTypeSymbol(name, TypeKind.Builtin, Module, accessibility: Accessibility.Public);
        Module.AddMember(type);
        return type;
    }

    NamedTypeSymbol DeclareGeneric(
        string name,
        TypeKind typeKind,
        params string[] typeParameterNames
    ) {
        var type = new NamedTypeSymbol(name, typeKind, Module, accessibility: Accessibility.Public);

        type.TypeParameters = [
            ..typeParameterNames.Select((parameterName, i) => new TypeParameterSymbol(parameterName, type, null, i))
        ];

        Module.AddMember(type);
        return type;
    }
}
