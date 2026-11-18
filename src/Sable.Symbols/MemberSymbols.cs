using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>A symbol whose type is resolved the first time it is asked for.</summary>
/// <remarks>
///     Signatures are bound lazily so that a compilation can answer questions
///     about one declaration without binding the whole program, which is what the
///     language server needs.
/// </remarks>
public abstract class TypedSymbol : Symbol {
    TypeSymbol? _type;

    internal Func<TypeSymbol>? TypeResolver { get; set; }

    public TypeSymbol Type => _type ??= TypeResolver?.Invoke() ?? ErrorTypeSymbol.Instance;
}

/// <summary>A module: the unit of declaration scope, spanning its files.</summary>
public sealed class ModuleSymbol(string name) : Symbol {
    readonly List<Symbol> _members = [];

    /// <summary>
    ///     Members by name, built on demand and dropped when one is added.
    /// </summary>
    /// <remarks>
    ///     A module may hold thousands of declarations and every name lookup asks
    ///     it. Scanning the list made declaring a module quadratic in the number
    ///     of things it declares, which is the shape that only shows up on a real
    ///     program.
    /// </remarks>
    Dictionary<string, List<Symbol>>? _byName;

    public override SymbolKind Kind => SymbolKind.Module;
    public override string Name { get; } = name;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;

    public IReadOnlyList<Symbol> Members => _members;

    /// <summary>Modules this one imports, in declaration order.</summary>
    public List<ModuleSymbol> Imports { get; } = [];

    internal void AddMember(Symbol member) {
        _members.Add(member);

        if (_byName is null) {
            return;
        }

        if (!_byName.TryGetValue(member.Name, out var bucket)) {
            _byName[member.Name] = bucket = [];
        }

        bucket.Add(member);
    }

    public IEnumerable<Symbol> GetMembers(string name) {
        _byName ??= BuildIndex(_members);
        return _byName.TryGetValue(name, out var bucket)
            ? bucket
            : [];
    }

    internal static Dictionary<string, List<Symbol>> BuildIndex(List<Symbol> members) {
        var index = new Dictionary<string, List<Symbol>>(StringComparer.Ordinal);
        foreach (var member in members) {
            if (!index.TryGetValue(member.Name, out var bucket)) {
                index[member.Name] = bucket = [];
            }

            bucket.Add(member);
        }

        return index;
    }

    public NamedTypeSymbol? GetType(string name) {
        foreach (var member in GetMembers(name)) {
            if (member is NamedTypeSymbol type) {
                return type;
            }
        }

        return null;
    }

    public override string QualifiedName => Name;
}

/// <summary>A function, method, initialiser, accessor or operator.</summary>
public sealed class FunctionSymbol : Symbol {
    internal FunctionSymbol(
        string name,
        Symbol container,
        SyntaxNode? declaration,
        Accessibility accessibility,
        FunctionKind functionKind
    ) {
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

    /// <summary>
    ///     For an <c>override</c>, the member it finally overrides, in the
    ///     class at the top of the chain; null for any other function.
    /// </summary>
    public FunctionSymbol? RootOverridden => ConformanceChecker.RootOverridden(this);
    public bool IsOpen { get; internal set; }

    /// <summary><c>final</c>: no subclass may override it (spec/SYNTAX.md §7).</summary>
    public bool IsFinal { get; internal set; }

    /// <summary>
    ///     Declared <c>rethrows</c>: it throws exactly when one of the closures
    ///     handed to it does, so <see cref="ThrownType" /> is what those closures
    ///     throw rather than anything written at the declaration.
    /// </summary>
    public bool IsRethrows { get; internal set; }

    /// <summary>The <c>where A == B</c> requirements on this function's parameters.</summary>
    public ImmutableArray<SameTypeRequirement> SameTypeRequirements { get; internal set; } = [];

    /// <summary>The <c>where C.Item: P</c> requirements on associated types.</summary>
    public ImmutableArray<ConformanceRequirement> ConformanceRequirements { get; internal set; } = [];

    readonly List<Symbol> _localTypes = [];

    /// <summary>
    ///     Types and aliases declared inside the body. They are hoisted: visible
    ///     from anywhere in the function, including before the line that declares
    ///     them, the way a local function is.
    /// </summary>
    public IReadOnlyList<Symbol> LocalTypes => _localTypes;

    internal void AddLocalType(Symbol type) => _localTypes.Add(type);

    internal IEnumerable<Symbol> GetLocalTypes(string name) =>
        _localTypes.Where(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>
    ///     For an <c>operator</c> member, where it sits relative to its operand.
    ///     Arity alone cannot tell a prefix operator from a postfix one.
    /// </summary>
    public OperatorFixity Fixity { get; internal set; } = OperatorFixity.Infix;

    /// <summary>True when the declaration has no body, as in a protocol.</summary>
    public bool IsAbstract { get; internal set; }

    /// <summary>
    ///     The SIL builtin a <c>Sable.Core</c> declaration written
    ///     <c>@builtin("name")</c> lowers to, or null. Such a function has no
    ///     body: each call is the builtin, applied to the receiver and then the
    ///     arguments, in place — no function is made for it, so it costs what
    ///     the operation costs and nothing more.
    /// </summary>
    public string? Builtin { get; internal set; }

    TypeSymbol? _explicitProtocol;
    internal Func<TypeSymbol?>? ExplicitProtocolResolver { get; set; }

    /// <summary>
    ///     The protocol named before the function's name in an explicit
    ///     implementation, <c>fn Drawable.render()</c> (SYNTAX.md §7): the
    ///     function implements that protocol's requirement, and no other's.
    ///     Null for an ordinary function.
    /// </summary>
    public TypeSymbol? ExplicitProtocol {
        get {
            if (ExplicitProtocolResolver is { } resolve) {
                _explicitProtocol = resolve();
                ExplicitProtocolResolver = null;
            }

            return _explicitProtocol;
        }
    }

    TypeSymbol? _returnType;
    internal Func<TypeSymbol>? ReturnTypeResolver { get; set; }

    public TypeSymbol ReturnType => _returnType ??= ReturnTypeResolver?.Invoke() ?? ErrorTypeSymbol.Instance;

    TypeSymbol? _thrownType;
    internal Func<TypeSymbol?>? ThrownTypeResolver { get; set; }
    bool _thrownResolved;

    /// <summary>The declared error type, or null when the function cannot throw.</summary>
    public TypeSymbol? ThrownType {
        get {
            if (_thrownResolved) {
                return _thrownType;
            }

            _thrownType = ThrownTypeResolver?.Invoke();
            _thrownResolved = true;
            return _thrownType;
        }
    }

    /// <summary>The function's type, as it would appear in a signature.</summary>
    public FunctionTypeSymbol FunctionType =>
        new([..Parameters.Select(static p => p.Type)], ReturnType, IsAsync, ThrownType);

    public override string ToString() => $"{QualifiedName}{SignatureString()}";

    string SignatureString() {
        var parameters = string.Join(", ", Parameters.Select(static p => p.Type.ToDisplayString()));
        return $"({parameters}) -> {ReturnType.ToDisplayString()}";
    }
}

/// <summary>Where an <c>operator</c> member sits relative to its operands.</summary>
public enum OperatorFixity {
    Infix,
    Prefix,
    Postfix
}

public enum FunctionKind {
    Ordinary,
    Initializer,
    Deinitializer,
    Operator,
    Subscript,
    Accessor
}

public sealed class ParameterSymbol(
    string name,
    string? label,
    Symbol container,
    SyntaxNode? declaration,
    int ordinal) : TypedSymbol {
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

    /// <summary>
    ///     <c>@escaping</c>: a closure passed here may outlive the call — be
    ///     stored, returned, or captured by a closure that does. Without it, a
    ///     closure parameter may only be called, or passed on to another that
    ///     does not escape (SYNTAX.md §17).
    /// </summary>
    public bool IsEscaping { get; internal set; }
}

public enum ParameterModifier {
    None,
    InOut,
    Out,
    Consuming,
    Borrowing,
    Ref,
    In
}

/// <summary>A stored or computed member, or a module-level binding.</summary>
/// <summary>How a stored reference holds its object.</summary>
public enum ReferenceKind {
    /// <summary>It keeps the object alive.</summary>
    Strong,

    /// <summary><c>weak</c>: it does not, and reads as none once the object is gone.</summary>
    Weak,

    /// <summary><c>unowned</c>: it does not, and reading it once the object is gone traps.</summary>
    Unowned,
}

public sealed class FieldSymbol(
    string name,
    Symbol container,
    SyntaxNode? declaration,
    Accessibility accessibility,
    bool isMutable) : TypedSymbol {
    public override SymbolKind Kind => SymbolKind.Field;
    public override string Name { get; } = name;
    public override Symbol? ContainingSymbol { get; } = container;

    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];

    /// <summary><c>private(set)</c> and the like: who may write it, when narrower than who may read it.</summary>
    /// <summary>A property whose read may suspend: <c>task.value</c>.</summary>
    public bool IsAsyncGetter { get; internal set; }

    public Accessibility? SetterAccessibility { get; internal set; }

    public override Accessibility DeclaredAccessibility { get; } = accessibility;

    /// <summary>True for <c>var</c>; false for <c>let</c> and the terse form.</summary>
    public bool IsMutable { get; } = isMutable;

    public bool IsStatic { get; internal set; }
    public bool IsLazy { get; internal set; }

    /// <summary><c>override var</c>: it replaces a property of a base class, whose type it keeps.</summary>
    public bool IsOverride { get; internal set; }

    /// <summary><c>final</c>: no subclass may override it (spec/SYNTAX.md §7).</summary>
    public bool IsFinal { get; internal set; }

    /// <summary>How a field of a class type holds its object (docs/02 §2.2): strongly, unless declared otherwise.</summary>
    public ReferenceKind ReferenceKind { get; internal set; }

    /// <summary>True when the member has accessors rather than storage.</summary>
    public bool IsComputed { get; internal set; }

    /// <summary>
    ///     A property whose accessors say <c>field</c>: the storage they read
    ///     and write, a private stored field of the type that nothing else
    ///     can name. Its initial value is the property's.
    /// </summary>
    public FieldSymbol? BackingField { get; internal set; }

    /// <summary>For a backing field, the property it is the storage of.</summary>
    public FieldSymbol? BackedProperty { get; internal set; }

    /// <summary>What it is called where a person reads it — a derived description, reflection: a backing field by its property's name.</summary>
    public string DisplayName => BackedProperty?.Name ?? Name;

    /// <summary>True when a computed member declares a <c>set</c>.</summary>
    public bool HasSetter { get; internal set; }

    /// <summary>
    ///     True for a protocol requirement: accessors are declared but none has a
    ///     body, so a conforming type has to supply one.
    /// </summary>
    public bool IsAbstract { get; internal set; }

    /// <summary>
    ///     For a computed property <c>Sable.Core</c> declares
    ///     <c>@builtin("name")</c>, with a bodiless <c>get</c>: the SIL builtin
    ///     a read lowers to, applied to the receiver.
    /// </summary>
    public string? Builtin { get; internal set; }

    public bool HasInitializer { get; internal set; }
}

/// <summary>A name introduced by <c>alias</c>.</summary>
public sealed class AliasSymbol(
    string name,
    Symbol container,
    SyntaxNode? declaration,
    Accessibility accessibility) : TypedSymbol {
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
    SyntaxNode? declaration) : Symbol {
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
    SyntaxNode? declaration) : Symbol {
    public override SymbolKind Kind => SymbolKind.EnumCase;
    public override string Name { get; } = name;
    public override Symbol? ContainingSymbol { get; } = container;

    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];

    public override Accessibility DeclaredAccessibility => container.DeclaredAccessibility;

    /// <summary>True when the case declared an explicit value.</summary>
    public bool HasExplicitValue { get; internal set; }

    /// <summary>The integer the case is: the one it writes, or the one it follows on to.</summary>
    public long RawValue { get; internal set; }
}

/// <summary>A local binding introduced by a statement.</summary>
public sealed class LocalSymbol(
    string name,
    Symbol container,
    SyntaxNode? declaration,
    bool isMutable) : TypedSymbol {
    public override SymbolKind Kind => SymbolKind.Local;
    public override string Name { get; } = name;
    public override Symbol? ContainingSymbol { get; } = container;

    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];

    /// <summary><c>async let</c>: its value comes from a child task, so reading it awaits.</summary>
    public bool IsAsyncLet { get; internal set; }

    public bool IsMutable { get; } = isMutable;
}
