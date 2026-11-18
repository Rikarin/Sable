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

        // 'Optional<T>' exists so that 'T?' has a nominal form to desugar to.
        Optional = DeclareGeneric("Optional", TypeKind.Union, "Wrapped");
        Dictionary = DeclareGeneric("Dictionary", TypeKind.Struct, "Key", "Value");

        // What 'fromBytes' gives (docs/02 §2.5): the value, or why the bytes were refused.
        Decoded = DeclareGeneric("Decoded", TypeKind.Union, "Value");

        // Concurrency's one core type, until Sable.Concurrency (docs/06): a task
        // running concurrently, whose result is awaited.
        Task = DeclareGeneric("Task", TypeKind.Class, "Success");
        Bytes = new NamedTypeSymbol("Bytes", TypeKind.Struct, Module, accessibility: Accessibility.Public);
        Module.AddMember(Bytes);

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
    ///     generic in a way a declaration cannot write, or lowered by more
    ///     than one builtin: a region's <c>alloc</c> (its result is a
    ///     reference to whatever it is given), <c>Task</c>'s members (a spawn
    ///     builds a closure's task object), <c>Bytes.of</c> and
    ///     <c>Decoded</c> (inline layout's), and the array members, built per element type in
    ///     <see cref="GetArrayMembers" />.
    /// </remarks>
    void DeclarePrimitiveMembers() {
        // 'arena.alloc(value)': the value, moved into the region; a reference
        // to it, rooted in the region, back.
        var alloc = new FunctionSymbol("alloc", Region, null, Accessibility.Public, FunctionKind.Ordinary);
        var element = new TypeParameterSymbol("T", alloc, null, 0);
        alloc.TypeParameters = [element];
        var allocated = new ParameterSymbol("value", "_", alloc, null, 0) { TypeResolver = () => element };
        alloc.Parameters = [allocated];
        alloc.ReturnTypeResolver = () => new ReferenceTypeSymbol(element);
        Region.AddMember(alloc);

        // 'Decoded<T>': '.Ok(value)' or '.Invalid(why)'.
        var ok = new UnionCaseSymbol("Ok", Decoded, null);
        ok.Payload = [new ParameterSymbol("value", null, ok, null, 0) { TypeResolver = () => Decoded.TypeParameters[0] }];
        Decoded.AddMember(ok);
        var invalid = new UnionCaseSymbol("Invalid", Decoded, null);
        invalid.Payload = [new ParameterSymbol("why", null, invalid, null, 0) { TypeResolver = () => String }];
        Decoded.AddMember(invalid);

        // 'Task.spawn { … }': a task, queued; 'await task.value': its result;
        // 'await Task.yield()': every task queued runs first.
        var spawn = new FunctionSymbol("spawn", Task, null, Accessibility.Public, FunctionKind.Ordinary) { IsStatic = true };
        var success = new TypeParameterSymbol("T", spawn, null, 0);
        spawn.TypeParameters = [success];
        spawn.Parameters = [
            new ParameterSymbol("body", "_", spawn, null, 0) { TypeResolver = () => new FunctionTypeSymbol([], success, isAsync: true) },
        ];
        spawn.ReturnTypeResolver = () => Task.Construct([success]);
        Task.AddMember(spawn);

        var value = new FieldSymbol("value", Task, null, Accessibility.Public, false) {
            IsComputed = true, IsAsyncGetter = true, TypeResolver = () => Task.TypeParameters[0],
        };
        Task.AddMember(value);

        var yielding = new FunctionSymbol("yield", Task, null, Accessibility.Public, FunctionKind.Ordinary) { IsStatic = true, IsAsync = true };
        yielding.Parameters = [];
        yielding.ReturnTypeResolver = () => Void;
        Task.AddMember(yielding);

        // 'Bytes.of(value)': an @inline_layout value's bytes, as they are — no serialising pass.
        var of = new FunctionSymbol("of", Bytes, null, Accessibility.Public, FunctionKind.Ordinary) { IsStatic = true };
        var serialised = new TypeParameterSymbol("T", of, null, 0);
        of.TypeParameters = [serialised];
        of.Parameters = [new ParameterSymbol("value", "_", of, null, 0) { TypeResolver = () => serialised }];
        of.ReturnTypeResolver = () => new ArrayTypeSymbol(U8, ArrayKind.Grow);
        Bytes.AddMember(of);
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

    /// <summary><c>Task&lt;Success&gt;</c>: a task and, once it finishes, its result.</summary>
    public NamedTypeSymbol Task { get; }

    /// <summary><c>Decoded&lt;T&gt;</c>: what <c>fromBytes</c> gives.</summary>
    public NamedTypeSymbol Decoded { get; }

    /// <summary><c>Bytes</c>: where <c>Bytes.of</c> lives.</summary>
    public NamedTypeSymbol Bytes { get; }

    /// <summary>
    ///     <c>T.fromBytes(bytes)</c> for an <c>@inline_layout</c> struct: the
    ///     bytes, validated — framing, lengths, tags and every relative link.
    /// </summary>
    public FunctionSymbol FromBytes(NamedTypeSymbol type) {
        var function = new FunctionSymbol("fromBytes", type, null, Accessibility.Public, FunctionKind.Ordinary) { IsStatic = true };
        function.Parameters = [new ParameterSymbol("bytes", "_", function, null, 0) { TypeResolver = () => new ArrayTypeSymbol(U8, ArrayKind.Grow) }];
        function.ReturnTypeResolver = () => Decoded.Construct([type]);
        return function;
    }

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

    /// <summary>
    ///     <c>Range&lt;Bound&gt;</c>, what <c>a..&lt;b</c> makes, and
    ///     <c>ClosedRange&lt;Bound&gt;</c>, what <c>a...b</c> makes: declared in
    ///     <c>lib/Sable.Core/Range.sab</c>, so null only in a compilation that
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
