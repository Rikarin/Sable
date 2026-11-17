using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>A symbol whose type is resolved the first time it is asked for.</summary>
/// <remarks>
/// Signatures are bound lazily so that a compilation can answer questions
/// about one declaration without binding the whole program, which is what the
/// language server needs.
/// </remarks>
public abstract class TypedSymbol : Symbol
{
    private TypeSymbol? _type;

    internal Func<TypeSymbol>? TypeResolver { get; set; }

    public TypeSymbol Type =>
        _type ??= TypeResolver?.Invoke() ?? ErrorTypeSymbol.Instance;
}

/// <summary>A module: the unit of declaration scope, spanning its files.</summary>
public sealed class ModuleSymbol(string name) : Symbol
{
    private readonly List<Symbol> _members = [];

    public override SymbolKind Kind => SymbolKind.Module;
    public override string Name { get; } = name;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;

    public IReadOnlyList<Symbol> Members => _members;

    /// <summary>Modules this one imports, in declaration order.</summary>
    public List<ModuleSymbol> Imports { get; } = [];

    internal void AddMember(Symbol member) => _members.Add(member);

    public IEnumerable<Symbol> GetMembers(string name)
    {
        foreach (var member in _members)
            if (string.Equals(member.Name, name, StringComparison.Ordinal)) yield return member;
    }

    public NamedTypeSymbol? GetType(string name)
    {
        foreach (var member in _members)
        {
            if (member is NamedTypeSymbol type
                && string.Equals(type.Name, name, StringComparison.Ordinal))
            {
                return type;
            }
        }
        return null;
    }

    public override string QualifiedName => Name;
}

/// <summary>A function, method, initialiser, accessor or operator.</summary>
public sealed class FunctionSymbol : Symbol
{
    internal FunctionSymbol(
        string name,
        Symbol container,
        SyntaxNode? declaration,
        Accessibility accessibility,
        FunctionKind functionKind)
    {
        Name = name;
        ContainingSymbol = container;
        DeclaringSyntax = declaration is null ? [] : [declaration];
        DeclaredAccessibility = accessibility;
        FunctionKind = functionKind;
    }

    public override SymbolKind Kind => SymbolKind.Function;
    public override string Name { get; }
    public override Symbol? ContainingSymbol { get; }
    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; }
    public override Accessibility DeclaredAccessibility { get; }

    public FunctionKind FunctionKind { get; }

    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; internal set; } = [];
    public ImmutableArray<ParameterSymbol> Parameters { get; internal set; } = [];

    public bool IsStatic { get; internal set; }
    public bool IsAsync { get; internal set; }
    public bool IsMutating { get; internal set; }
    public bool IsOverride { get; internal set; }
    public bool IsOpen { get; internal set; }

    /// <summary>True when the declaration has no body, as in a protocol.</summary>
    public bool IsAbstract { get; internal set; }

    private TypeSymbol? _returnType;
    internal Func<TypeSymbol>? ReturnTypeResolver { get; set; }

    public TypeSymbol ReturnType =>
        _returnType ??= ReturnTypeResolver?.Invoke() ?? ErrorTypeSymbol.Instance;

    private TypeSymbol? _thrownType;
    internal Func<TypeSymbol?>? ThrownTypeResolver { get; set; }
    private bool _thrownResolved;

    /// <summary>The declared error type, or null when the function cannot throw.</summary>
    public TypeSymbol? ThrownType
    {
        get
        {
            if (_thrownResolved) return _thrownType;
            _thrownType = ThrownTypeResolver?.Invoke();
            _thrownResolved = true;
            return _thrownType;
        }
    }

    /// <summary>The function's type, as it would appear in a signature.</summary>
    public FunctionTypeSymbol FunctionType => new(
        [.. Parameters.Select(p => p.Type)], ReturnType, IsAsync, ThrownType);

    public override string ToString() => $"{QualifiedName}{SignatureString()}";

    private string SignatureString()
    {
        var parameters = string.Join(", ", Parameters.Select(p => p.Type.ToDisplayString()));
        return $"({parameters}) -> {ReturnType.ToDisplayString()}";
    }
}

public enum FunctionKind
{
    Ordinary,
    Initializer,
    Deinitializer,
    Operator,
    Subscript,
    Accessor,
}

public sealed class ParameterSymbol(
    string name,
    string? label,
    Symbol container,
    SyntaxNode? declaration,
    int ordinal) : TypedSymbol
{
    public override SymbolKind Kind => SymbolKind.Parameter;
    public override string Name { get; } = name;
    public override Symbol? ContainingSymbol { get; } = container;
    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];

    /// <summary>The external label, when it differs from the name.</summary>
    public string? Label { get; } = label;

    public int Ordinal { get; } = ordinal;

    public bool HasDefaultValue { get; internal set; }
    public bool IsVariadic { get; internal set; }
    public ParameterModifier Modifier { get; internal set; } = ParameterModifier.None;
}

public enum ParameterModifier { None, InOut, Out, Consuming, Borrowing, Ref, In }

/// <summary>A stored or computed member, or a module-level binding.</summary>
public sealed class FieldSymbol(
    string name,
    Symbol container,
    SyntaxNode? declaration,
    Accessibility accessibility,
    bool isMutable) : TypedSymbol
{
    public override SymbolKind Kind => SymbolKind.Field;
    public override string Name { get; } = name;
    public override Symbol? ContainingSymbol { get; } = container;
    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];
    public override Accessibility DeclaredAccessibility { get; } = accessibility;

    /// <summary>True for <c>var</c>; false for <c>let</c> and the terse form.</summary>
    public bool IsMutable { get; } = isMutable;

    public bool IsStatic { get; internal set; }
    public bool IsLazy { get; internal set; }

    /// <summary>True when the member has accessors rather than storage.</summary>
    public bool IsComputed { get; internal set; }

    public bool HasInitializer { get; internal set; }
}

/// <summary>A name introduced by <c>alias</c> or <c>typealias</c>.</summary>
public sealed class AliasSymbol(
    string name,
    Symbol container,
    SyntaxNode? declaration,
    Accessibility accessibility) : TypedSymbol
{
    public override SymbolKind Kind => SymbolKind.Alias;
    public override string Name { get; } = name;
    public override Symbol? ContainingSymbol { get; } = container;
    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];
    public override Accessibility DeclaredAccessibility { get; } = accessibility;

    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; internal set; } = [];

    /// <summary>The type this name stands for.</summary>
    public TypeSymbol AliasedType => Type;
}

/// <summary>One case of a <c>union</c>, with its payload fields.</summary>
public sealed class UnionCaseSymbol(
    string name,
    NamedTypeSymbol container,
    SyntaxNode? declaration) : Symbol
{
    public override SymbolKind Kind => SymbolKind.UnionCase;
    public override string Name { get; } = name;
    public override Symbol? ContainingSymbol { get; } = container;
    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];
    public override Accessibility DeclaredAccessibility => container.DeclaredAccessibility;

    public ImmutableArray<ParameterSymbol> Payload { get; internal set; } = [];

    public bool HasPayload => !Payload.IsEmpty;
}

/// <summary>One case of an <c>enum</c>.</summary>
public sealed class EnumCaseSymbol(
    string name,
    NamedTypeSymbol container,
    SyntaxNode? declaration) : Symbol
{
    public override SymbolKind Kind => SymbolKind.EnumCase;
    public override string Name { get; } = name;
    public override Symbol? ContainingSymbol { get; } = container;
    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];
    public override Accessibility DeclaredAccessibility => container.DeclaredAccessibility;

    /// <summary>True when the case declared an explicit value.</summary>
    public bool HasExplicitValue { get; internal set; }
}

/// <summary>A local binding introduced by a statement.</summary>
public sealed class LocalSymbol(
    string name,
    Symbol container,
    SyntaxNode? declaration,
    bool isMutable) : TypedSymbol
{
    public override SymbolKind Kind => SymbolKind.Local;
    public override string Name { get; } = name;
    public override Symbol? ContainingSymbol { get; } = container;
    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];

    public bool IsMutable { get; } = isMutable;
}
