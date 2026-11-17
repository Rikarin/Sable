using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
/// The built-in types, synthesised into a <c>Sable.Core</c> module that every
/// file sees without importing it.
/// </summary>
/// <remarks>
/// docs/06 has these written in Sable once the standard library exists. Until
/// then the compiler supplies them, so that type binding has something to bind
/// to; replacing this with a real <c>Sable.Core</c> changes nothing above it.
/// </remarks>
public sealed class CoreTypes
{
    internal CoreTypes(ModuleSymbol module)
    {
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
        Void = Primitive("Void");
        Never = Primitive("Never");

        // Every thrown value conforms to Error; bare 'throws' means
        // 'throws(any Error)', per SYNTAX.md §18.
        Error = new NamedTypeSymbol(
            "Error", TypeKind.Protocol, Module, accessibility: Accessibility.Public);
        Module.AddMember(Error);
        AnyError = new ExistentialTypeSymbol(Error);

        // 'Optional<T>' exists so that 'T?' has a nominal form to desugar to.
        Optional = DeclareGeneric("Optional", TypeKind.Union, "Wrapped");
        Dictionary = DeclareGeneric("Dictionary", TypeKind.Struct, "Key", "Value");

        Integers = [I8, I16, I32, I64, I128, Int, U8, U16, U32, U64, U128, UInt];
        Floats = [F16, F32, F64];
        Numerics = [.. Integers, .. Floats];
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
    public NamedTypeSymbol Void { get; }
    public NamedTypeSymbol Never { get; }
    public NamedTypeSymbol Error { get; }

    /// <summary>What a bare <c>throws</c> means.</summary>
    public TypeSymbol AnyError { get; }

    public NamedTypeSymbol Optional { get; }
    public NamedTypeSymbol Dictionary { get; }

    public ImmutableArray<NamedTypeSymbol> Integers { get; }
    public ImmutableArray<NamedTypeSymbol> Floats { get; }
    public ImmutableArray<NamedTypeSymbol> Numerics { get; }

    public bool IsNumeric(TypeSymbol type) => Numerics.Any(n => n.Equals(type));
    public bool IsInteger(TypeSymbol type) => Integers.Any(n => n.Equals(type));

    private NamedTypeSymbol Primitive(string name)
    {
        var type = new NamedTypeSymbol(
            name, TypeKind.Builtin, Module, accessibility: Accessibility.Public);
        Module.AddMember(type);
        return type;
    }

    private NamedTypeSymbol DeclareGeneric(
        string name, TypeKind typeKind, params string[] typeParameterNames)
    {
        var type = new NamedTypeSymbol(
            name, typeKind, Module, accessibility: Accessibility.Public);

        type.TypeParameters =
        [
            .. typeParameterNames.Select(
                (parameterName, i) => new TypeParameterSymbol(parameterName, type, null, i)),
        ];

        Module.AddMember(type);
        return type;
    }
}
