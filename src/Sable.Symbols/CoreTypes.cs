using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
///     The built-in types, synthesised into a <c>Sable.Core</c> module that every
///     file sees without importing it.
/// </summary>
/// <remarks>
///     docs/06 has these written in Sable once the standard library exists. Until
///     then the compiler supplies them, so that type binding has something to bind
///     to; replacing this with a real <c>Sable.Core</c> changes nothing above it.
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
    ///     Gives the built-in types the small surface a real program needs.
    /// </summary>
    /// <remarks>
    ///     docs/06 §6.4 puts String, the array family and the core protocols in a
    ///     <c>Sable.Core</c> written in Sable. Until that exists the compiler
    ///     supplies the handful of members that nothing can be written without —
    ///     enough to write a parser against the type checker, which is the
    ///     go/no-go in docs/04 §1.4. Replacing this with the real module changes
    ///     nothing above it.
    /// </remarks>
    void DeclarePrimitiveMembers() {
        Property(String, "count", Int);
        Property(String, "isEmpty", Bool);
        Method(String, "at", [("index", Int)], Char);
        Method(String, "slice", [("from", Int), ("to", Int)], String);
        Method(String, "appending", [("other", String)], String);
        Method(String, "contains", [("character", Char)], Bool);
        Method(String, "indexOf", [("character", Char)], new OptionalTypeSymbol(Int));
        Method(String, "trimmed", [], String);
        Method(
            String,
            "split",
            [("separator", Char)],
            new ArrayTypeSymbol(String, ArrayKind.Grow)
        );
        Method(String, "fromChar", [("character", Char)], String, true);

        // 'arena.alloc(value)': the value, moved into the region; a reference
        // to it, rooted in the region, back.
        var alloc = new FunctionSymbol("alloc", Region, null, Accessibility.Public, FunctionKind.Ordinary);
        var element = new TypeParameterSymbol("T", alloc, null, 0);
        alloc.TypeParameters = [element];
        var allocated = new ParameterSymbol("value", "_", alloc, null, 0) { TypeResolver = () => element };
        alloc.Parameters = [allocated];
        alloc.ReturnTypeResolver = () => new ReferenceTypeSymbol(element);
        Region.AddMember(alloc);

        Property(Range, "lowerBound", Range.TypeParameters[0]);
        Property(Range, "upperBound", Range.TypeParameters[0]);
        Property(Range, "isEmpty", Bool);

        Property(Char, "isDigit", Bool);
        Property(Char, "isLetter", Bool);
        Property(Char, "isWhitespace", Bool);
        Property(Char, "code", U32);

        // The scalar as an Int, and back: what decoding and writing '\u' escapes
        // needs before the integer types other than Int lower.
        Property(Char, "codePoint", Int);
        Method(Char, "fromCodePoint", [("value", Int)], new OptionalTypeSymbol(Char), true);

        foreach (var numeric in Numerics) {
            Method(numeric, "toString", [], String);
            Method(numeric, "abs", [], numeric);
            Method(numeric, "min", [("other", numeric)], numeric);
            Method(numeric, "max", [("other", numeric)], numeric);
        }

        foreach (var integer in Integers) {
            Method(String, "to" + integer.Name, [], new OptionalTypeSymbol(integer));
        }

        Method(String, "toF64", [], new OptionalTypeSymbol(F64));
        Method(Bool, "toString", [], String);

        // Writing a line to standard output: what hello world needs, before
        // Sable.IO exists to own it (docs/06 §6.4).
        foreach (var printable in new[] { String, Int, F64, Bool }) {
            Print = Print.Add(Function("print", [("value", printable)], Void));
        }
    }

    /// <summary>The <c>print</c> overloads, which code generation recognises.</summary>
    public ImmutableArray<FunctionSymbol> Print { get; private set; } = [];

    /// <summary>A function the core module declares at its top level.</summary>
    FunctionSymbol Function(string name, (string Name, TypeSymbol Type)[] parameters, TypeSymbol returnType) {
        var function = new FunctionSymbol(name, Module, null, Accessibility.Public, FunctionKind.Ordinary);

        function.Parameters = [
            ..parameters.Select((parameter, i) => {
                    // Label '_': the argument is written bare, as in print(x).
                    var symbol = new ParameterSymbol(parameter.Name, "_", function, null, i);
                    symbol.TypeResolver = () => parameter.Type;
                    return symbol;
                }
            )
        ];

        function.ReturnTypeResolver = () => returnType;
        Module.AddMember(function);
        return function;
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

        if (array.ArrayKind is ArrayKind.Grow or ArrayKind.GrowShrink) {
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
