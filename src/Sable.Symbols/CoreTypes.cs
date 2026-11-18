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
///     <c>lib/Sable.Core</c> (<see cref="CoreLibrary" />), apart from the few
///     <see cref="DeclarePrimitiveMembers" /> explains.
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

        // 'extension AppError: From<IoError>' lets a 'try' in a function that
        // throws AppError propagate an IoError, converted (docs/03 §3.6).
        From = DeclareGeneric("From", TypeKind.Protocol, "Source");

        // A range has a nominal form too, so that 'for i in 0..<n' iterates
        // something rather than a shape the binder knows by sight.
        Range = DeclareGeneric("Range", TypeKind.Struct, "Bound");

        // 'Optional<T>' exists so that 'T?' has a nominal form to desugar to.
        Optional = DeclareGeneric("Optional", TypeKind.Union, "Wrapped");
        Dictionary = DeclareGeneric("Dictionary", TypeKind.Struct, "Key", "Value");

        Integers = [I8, I16, I32, I64, I128, Int, U8, U16, U32, U64, U128, UInt];
        Floats = [F16, F32, F64];
        Numerics = [..Integers, ..Floats];

        DeclarePrimitiveMembers();
    }

    // -----------------------------------------------------------------------
    // The members a program cannot do without
    // -----------------------------------------------------------------------

    /// <summary>
    ///     The members of the core types that Sable cannot yet declare.
    /// </summary>
    /// <remarks>
    ///     Everything a primitive does that Sable can say is in
    ///     <c>lib/Sable.Core</c> — String's, Char's and the numbers' members,
    ///     <c>print</c>, <c>Runtime</c> — written in Sable, the floor of it as
    ///     <c>@builtin</c>s (Compilation.Builtins.cs). What is left here is
    ///     generic in a way a declaration cannot write: <c>Range</c>'s
    ///     bounds, and the array members, built per element type in
    ///     <see cref="GetArrayMembers" />. A region's <c>alloc</c>,
    ///     <c>Task</c>, <c>Decoded</c>, <c>Bytes.of</c> and the
    ///     <c>InlineLayout</c> protocol are Sable too, their bodies the
    ///     compiler's (<c>@intrinsic</c>, and <c>fromBytes</c> synthesised
    ///     per type).
    /// </remarks>
    void DeclarePrimitiveMembers() {
        Property(Range, "lowerBound", Range.TypeParameters[0]);
        Property(Range, "upperBound", Range.TypeParameters[0]);
        Property(Range, "isEmpty", Bool);
    }

    /// <summary>The members every array has, built for one element type.</summary>
    /// <remarks>
    ///     An array is not a named type, so its members cannot live in a member
    ///     list; they are built on demand and cached per array type.
    /// </remarks>
    public IReadOnlyList<Symbol> GetArrayMembers(ArrayTypeSymbol array) {
        if (_arrayMembers.TryGetValue(array, out var cached)) {
            return cached;
        }

        var owner = new NamedTypeSymbol(
            array.ToDisplayString(),
            TypeKind.Struct,
            Module,
            accessibility: Accessibility.Public
        );

        Property(owner, "count", Int);
        Property(owner, "isEmpty", Bool);
        Method(owner, "contains", [("element", array.ElementType)], Bool);
        Method(
            owner,
            "slice",
            [("from", Int), ("to", Int)],
            new ArrayTypeSymbol(array.ElementType, ArrayKind.Slice)
        );

        // Bytes read as text: UTF-8, decoded.
        if (array.ElementType.Equals(U8)) {
            Method(owner, "toString", [], String);
        }

        // A bounded inline array grows within its bound.
        if (array.ArrayKind is ArrayKind.Grow or ArrayKind.GrowShrink or ArrayKind.Inline) {
            Method(owner, "append", [("element", array.ElementType)], Void);
            Method(owner, "removeLast", [], array.ElementType);
            Method(owner, "clear", [], Void);
        }

        var members = owner.Members;
        _arrayMembers[array] = members;
        return members;
    }

    readonly Dictionary<ArrayTypeSymbol, IReadOnlyList<Symbol>> _arrayMembers = [];

    static void Property(NamedTypeSymbol owner, string name, TypeSymbol type) {
        var field = new FieldSymbol(name, owner, null, Accessibility.Public, false) {
            IsComputed = true, TypeResolver = () => type
        };
        owner.AddMember(field);
    }

    static void Method(
        NamedTypeSymbol owner,
        string name,
        (string Name, TypeSymbol Type)[] parameters,
        TypeSymbol returnType,
        bool isStatic = false
    ) {
        var function = new FunctionSymbol(name, owner, null, Accessibility.Public, FunctionKind.Ordinary) {
            IsStatic = isStatic
        };

        function.Parameters = [
            ..parameters.Select((parameter, i) => {
                    // No declared label, so the name may be written or left out.
                    var symbol = new ParameterSymbol(parameter.Name, null, function, null, i);
                    symbol.TypeResolver = () => parameter.Type;
                    return symbol;
                }
            )
        ];

        function.ReturnTypeResolver = () => returnType;
        owner.AddMember(function);
    }

    public ModuleSymbol Module { get; }

    // Declared in Sable, in lib/Sable.Core, and known here by name, since
    // lowering gives their '@intrinsic' members bodies. Null before the core
    // is declared, or when a Sable.Core read from disk lacks them.

    /// <summary><c>Task&lt;Success&gt;</c>: a task and, once it finishes, its result (<c>Task.sab</c>).</summary>
    public NamedTypeSymbol? Task => Declared("Task", TypeKind.Class);

    /// <summary><c>Decoded&lt;Value&gt;</c>: what <c>fromBytes</c> gives (<c>InlineLayout.sab</c>).</summary>
    public NamedTypeSymbol? Decoded => Declared("Decoded", TypeKind.Union);

    /// <summary><c>Bytes</c>: where <c>Bytes.of</c> lives (<c>InlineLayout.sab</c>).</summary>
    public NamedTypeSymbol? Bytes => Declared("Bytes", TypeKind.Struct);

    /// <summary><c>InlineLayout</c>: what every <c>@inline_layout</c> struct conforms to (<c>InlineLayout.sab</c>).</summary>
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

    /// <summary>What a bare <c>throws</c> means.</summary>
    public TypeSymbol AnyError { get; }

    /// <summary><c>From&lt;Source&gt;</c>: the conversion a <c>try</c> applies between error types.</summary>
    public NamedTypeSymbol From { get; }

    public NamedTypeSymbol Range { get; }

    public NamedTypeSymbol Optional { get; }
    public NamedTypeSymbol Dictionary { get; }

    public ImmutableArray<NamedTypeSymbol> Integers { get; }
    public ImmutableArray<NamedTypeSymbol> Floats { get; }
    public ImmutableArray<NamedTypeSymbol> Numerics { get; }

    public bool IsNumeric(TypeSymbol type) => Numerics.Any(n => n.Equals(type));
    public bool IsInteger(TypeSymbol type) => Integers.Any(n => n.Equals(type));

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
