using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>Any type.</summary>
/// <remarks>
///     Types compare by structure, so two independently constructed
///     <c>List&lt;Int&gt;</c> symbols are equal. Named types compare by definition
///     and type arguments; the structural forms compare by their parts.
/// </remarks>
public abstract class TypeSymbol : Symbol, IEquatable<TypeSymbol> {
    public abstract TypeKind TypeKind { get; }

    /// <summary>True for types with identity and a reference count.</summary>
    public virtual bool IsReferenceType => TypeKind is TypeKind.Class or TypeKind.Actor;

    /// <summary>True when this stands for a type that could not be resolved.</summary>
    public virtual bool IsError => false;

    /// <summary>The type as it would be written in source.</summary>
    public abstract string ToDisplayString();

    public override string ToString() => ToDisplayString();

    public virtual bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);
    public override bool Equals(object? obj) => Equals(obj as TypeSymbol);
    public override int GetHashCode() => ToDisplayString().GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(TypeSymbol? left, TypeSymbol? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(TypeSymbol? left, TypeSymbol? right) => !(left == right);
}

/// <summary>A type the source declared, or a built-in primitive.</summary>
public sealed class NamedTypeSymbol : TypeSymbol {
    readonly List<Symbol> _members = [];
    ImmutableArray<TypeSymbol> _baseTypes = [];

    internal NamedTypeSymbol(
        string name,
        TypeKind typeKind,
        Symbol? container,
        SyntaxNode? declaration = null,
        Accessibility accessibility = Accessibility.Internal,
        bool isVariableSize = false
    ) {
        Name = name;
        TypeKind = typeKind;
        ContainingSymbol = container;
        DeclaringSyntax = declaration is null ? [] : [declaration];
        DeclaredAccessibility = accessibility;
        IsVariableSize = isVariableSize;
        OriginalDefinition = this;
    }

    NamedTypeSymbol(NamedTypeSymbol definition, ImmutableArray<TypeSymbol> typeArguments) {
        Name = definition.Name;
        TypeKind = definition.TypeKind;
        ContainingSymbol = definition.ContainingSymbol;
        DeclaringSyntax = definition.DeclaringSyntax;
        DeclaredAccessibility = definition.DeclaredAccessibility;
        IsVariableSize = definition.IsVariableSize;
        OriginalDefinition = definition;
        TypeArguments = typeArguments;
        _members = definition._members;
        _baseTypes = definition._baseTypes;
        TypeParameters = definition.TypeParameters;
    }

    public override SymbolKind Kind => SymbolKind.NamedType;
    public override string Name { get; }
    public override TypeKind TypeKind { get; }
    public override Symbol? ContainingSymbol { get; }
    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; }
    public override Accessibility DeclaredAccessibility { get; }

    /// <summary>True for a <c>union X..</c>, whose values are each their own size.</summary>
    public bool IsVariableSize { get; }

    /// <summary>The generic definition; itself when this is not constructed.</summary>
    public NamedTypeSymbol OriginalDefinition { get; }

    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; internal set; } = [];
    public ImmutableArray<TypeSymbol> TypeArguments { get; } = [];

    public bool IsGenericDefinition => !TypeParameters.IsEmpty && TypeArguments.IsEmpty;
    public bool IsConstructed => !TypeArguments.IsEmpty;

    public IReadOnlyList<Symbol> Members => _members;

    /// <summary>
    ///     A protocol's implicit <c>Self</c>: the conforming type, as a parameter
    ///     so that every substitution mechanism already handles it.
    /// </summary>
    /// <remarks>
    ///     It is deliberately not in <see cref="TypeParameters" />, because a
    ///     protocol with one associated type is written <c>P</c> and not
    ///     <c>P&lt;Self&gt;</c> — its arity is what the source says.
    /// </remarks>
    public TypeParameterSymbol? SelfTypeParameter { get; internal set; }

    /// <summary>
    ///     A protocol's <c>associatedtype</c> declarations.
    /// </summary>
    /// <remarks>
    ///     Like <c>Self</c> these are type parameters, and like <c>Self</c> they
    ///     stay out of <see cref="TypeParameters" />: a conformer supplies them by
    ///     name rather than positionally, so they are not part of the protocol's
    ///     arity.
    /// </remarks>
    public ImmutableArray<TypeParameterSymbol> AssociatedTypes { get; internal set; } = [];

    /// <summary>
    ///     For the type an array extension declares its members on, the array
    ///     it extends, in terms of the extension's parameters:
    ///     <c>Element[&gt;..]</c>. Null for every other type.
    /// </summary>
    /// <remarks>
    ///     It is never a value's type: inside the extension <c>self</c> is
    ///     this array, and outside a member is looked up on the array
    ///     (Compilation.ArrayExtensions.cs).
    /// </remarks>
    public ArrayTypeSymbol? ExtendedArray { get; internal set; }

    /// <summary>The <c>where A == B</c> requirements on this type's parameters.</summary>
    public ImmutableArray<SameTypeRequirement> SameTypeRequirements { get; internal set; } = [];

    /// <summary>The <c>where C.Item: P</c> requirements on associated types.</summary>
    public ImmutableArray<ConformanceRequirement> ConformanceRequirements { get; internal set; } = [];

    /// <summary>
    ///     The protocols <c>@derive</c> conformed this generic type to. Each
    ///     holds of a construction only where its fields' types, substituted,
    ///     conform too (docs/06 §6.5); see <see cref="DerivedConformance" />.
    /// </summary>
    public ImmutableArray<NamedTypeSymbol> DerivedConformances { get; internal set; } = [];

    /// <summary>Inheritance control, per spec/SYNTAX.md §2.</summary>
    public bool IsOpen { get; internal set; }

    /// <summary>
    ///     <c>@noncopy</c>: a move-only struct or union (docs/02 §2.3). A
    ///     value of it is moved, never copied; a use after the move is an error.
    /// </summary>
    public bool IsNoncopyable { get; internal set; }

    /// <summary>
    ///     <c>@inline_layout</c> (docs/02 §2.5): a struct laid out as one
    ///     contiguous, pointer-free block, variable-size fields in place.
    /// </summary>
    public bool IsInlineLayout { get; internal set; }

    /// <summary>
    ///     <c>@reflectable</c> (docs/06 §6.3): the type conforms to
    ///     <c>Reflectable</c>, so <c>TypeInfo.of</c> describes it.
    /// </summary>
    public bool IsReflectable { get; internal set; }

    /// <summary>True for <c>sealed class</c>: it may not be subclassed.</summary>
    public bool IsSealed { get; internal set; }

    /// <summary>True for <c>final class</c>: as with <c>sealed</c>, it may not be subclassed.</summary>
    public bool IsFinal { get; internal set; }

    /// <summary>True for <c>abstract class</c>: it cannot be instantiated.</summary>
    public bool IsAbstract { get; internal set; }

    public ImmutableArray<TypeSymbol> BaseTypes {
        get {
            // A construction's are its definition's, whenever they were bound:
            // 'Channel<Int>' may be made before 'Channel: AsyncSequence' is.
            if (!ReferenceEquals(OriginalDefinition, this)) {
                return OriginalDefinition.BaseTypes;
            }

            // Bound the first time they are asked for, as the constraints of
            // an associated type are: a body in a file read before this
            // type's may ask whether it conforms to something.
            if (PendingBaseTypes is { } bind) {
                PendingBaseTypes = null;
                bind();
            }

            return _baseTypes;
        }
        internal set => _baseTypes = value;
    }

    /// <summary>Binds the declaration's base types, its constraints and its 'where' clause; null once it has.</summary>
    internal Action? PendingBaseTypes { get; set; }

    /// <summary>Members by name, built on demand; see <see cref="ModuleSymbol" />.</summary>
    Dictionary<string, List<Symbol>>? _byName;

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

    /// <summary>Members declared with a given name, in declaration order.</summary>
    public IEnumerable<Symbol> GetMembers(string name) {
        _byName ??= ModuleSymbol.BuildIndex(_members);
        return _byName.TryGetValue(name, out var bucket) ? bucket : [];
    }

    public NamedTypeSymbol Construct(ImmutableArray<TypeSymbol> typeArguments) =>
        typeArguments.IsEmpty ? this : new NamedTypeSymbol(OriginalDefinition, typeArguments);

    public override string ToDisplayString() =>
        OriginalDefinition.ExtendedArray is { } array
            ? array.ToDisplayString()
            : TypeArguments.IsEmpty
                ? Name
                : $"{Name}<{string.Join(", ", TypeArguments.Select(static t => t.ToDisplayString()))}>";

    public override bool Equals(TypeSymbol? other) {
        if (other is not NamedTypeSymbol named) {
            return false;
        }

        if (!ReferenceEquals(OriginalDefinition, named.OriginalDefinition)) {
            return false;
        }

        if (TypeArguments.Length != named.TypeArguments.Length) {
            return false;
        }

        for (var i = 0; i < TypeArguments.Length; i++) {
            if (!TypeArguments[i].Equals(named.TypeArguments[i])) {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() {
        var hash = OriginalDefinition.Name.GetHashCode(StringComparison.Ordinal);
        foreach (var argument in TypeArguments) {
            hash = (hash * 31) ^ argument.GetHashCode();
        }

        return hash;
    }
}

/// <summary>A generic parameter, such as the <c>T</c> of <c>List&lt;T&gt;</c>.</summary>
public sealed class TypeParameterSymbol(
    string name,
    Symbol container,
    SyntaxNode? declaration,
    int ordinal) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.TypeParameter;
    public override string Name { get; } = name;
    public override TypeKind TypeKind => TypeKind.Struct;
    public override Symbol? ContainingSymbol { get; } = container;

    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } =
        declaration is null ? [] : [declaration];

    public int Ordinal { get; } = ordinal;

    /// <summary>The types this parameter must conform to or inherit from.</summary>
    public ImmutableArray<TypeSymbol> Constraints {
        get {
            // Bound the first time they are asked for: an associated type's
            // constraints may be wanted by a signature in a file read before
            // the protocol's own, as 'S.Iter.Element' wants 'Iter: Iterator'.
            if (ConstraintsResolver is { } resolve) {
                ConstraintsResolver = null;
                _constraints = resolve();
            }

            return _constraints;
        }
        internal set {
            ConstraintsResolver = null;
            _constraints = value;
        }
    }

    ImmutableArray<TypeSymbol> _constraints = [];

    /// <summary>Binds <see cref="Constraints" /> when they are first read; null once they are.</summary>
    internal Func<ImmutableArray<TypeSymbol>>? ConstraintsResolver { get; set; }

    /// <summary>Variance, from <c>out T</c> or <c>in T</c>.</summary>
    public VarianceKind Variance { get; internal set; } = VarianceKind.None;

    /// <summary>
    ///     What an <c>associatedtype</c> falls back to when a conformer does not
    ///     name it, as in <c>associatedtype Index: Comparable = Int</c>.
    /// </summary>
    public TypeSymbol? DefaultType { get; internal set; }

    public override string ToDisplayString() => Name;

    public override bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);

    public override int GetHashCode() =>
        System.Runtime.CompilerServices
            .RuntimeHelpers.GetHashCode(this);
}

/// <summary>
///     An associated type reached through a generic type, such as <c>C.Item</c>
///     where <c>C: Container</c>.
/// </summary>
/// <remarks>
///     The base is part of the type: for <c>A: Bag</c> and <c>B: Bag</c>,
///     <c>A.Item</c> and <c>B.Item</c> are different types, because nothing says
///     two bags hold the same thing. Substituting a concrete conformer for the
///     base resolves it to whatever that conformer binds the associated type to;
///     substituting another type parameter keeps it dependent.
/// </remarks>
public sealed class DependentMemberTypeSymbol(TypeSymbol baseType, TypeParameterSymbol associated) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.DependentMemberType;
    public override string Name => Associated.Name;
    public override TypeKind TypeKind => TypeKind.Struct;
    public override Symbol? ContainingSymbol => Associated.ContainingSymbol;

    /// <summary>The type the associated type is read through: a type parameter, or another dependent member.</summary>
    public TypeSymbol BaseType { get; } = baseType;

    /// <summary>The protocol's <c>associatedtype</c> declaration.</summary>
    public TypeParameterSymbol Associated { get; } = associated;

    public override string ToDisplayString() => $"{BaseType.ToDisplayString()}.{Associated.Name}";

    public override bool Equals(TypeSymbol? other) =>
        other is DependentMemberTypeSymbol member
        && ReferenceEquals(member.Associated, Associated)
        && member.BaseType.Equals(BaseType);

    public override int GetHashCode() => HashCode.Combine(BaseType, Associated, 0x0DE9);
}

public enum VarianceKind {
    None,
    Out,
    In
}

/// <summary>The kinds in the array family, per docs/02-memory-model.md §2.5.</summary>
public enum ArrayKind {
    /// <summary><c>T[]</c> or <c>T[k]</c>: size fixed at construction.</summary>
    Fixed,

    /// <summary><c>T[..]</c> or <c>T[..k]</c>: inline capacity.</summary>
    Inline,

    /// <summary><c>T[&gt;..]</c>: grow-only, with stable references.</summary>
    Grow,

    /// <summary><c>T[&gt;..&lt;]</c>: grows and shrinks.</summary>
    GrowShrink,

    /// <summary><c>T[:]</c>: a non-copying slice view.</summary>
    Slice
}

public sealed class ArrayTypeSymbol(TypeSymbol elementType, ArrayKind arrayKind, int? capacity = null) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.ArrayType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;

    public TypeSymbol ElementType { get; } = elementType;
    public ArrayKind ArrayKind { get; } = arrayKind;

    /// <summary>The <c>k</c> of <c>T[..k]</c> or <c>T[k]</c>, when it was written.</summary>
    public int? Capacity { get; } = capacity;

    public override string ToDisplayString() =>
        ArrayKind switch {
            ArrayKind.Fixed => $"{ElementType.ToDisplayString()}[{Capacity}]",
            ArrayKind.Inline => $"{ElementType.ToDisplayString()}[..{Capacity}]",
            ArrayKind.Grow => $"{ElementType.ToDisplayString()}[>..]",
            ArrayKind.GrowShrink => $"{ElementType.ToDisplayString()}[>..<]",
            _ => $"{ElementType.ToDisplayString()}[:]",
        };

    public override bool Equals(TypeSymbol? other) =>
        other is ArrayTypeSymbol array
        && array.ArrayKind == ArrayKind
        && array.Capacity == Capacity
        && array.ElementType.Equals(ElementType);

    public override int GetHashCode() => HashCode.Combine(ElementType, ArrayKind, Capacity);
}

public sealed class OptionalTypeSymbol(TypeSymbol underlyingType) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.OptionalType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Union;

    public TypeSymbol UnderlyingType { get; } = underlyingType;

    public override string ToDisplayString() => $"{UnderlyingType.ToDisplayString()}?";

    public override bool Equals(TypeSymbol? other) =>
        other is OptionalTypeSymbol optional && optional.UnderlyingType.Equals(UnderlyingType);

    public override int GetHashCode() => HashCode.Combine(UnderlyingType, 0x0B7);
}

public sealed class ReferenceTypeSymbol(
    TypeSymbol referencedType,
    string? relativeWidth = null,
    string? pool = null) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.ReferenceType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;

    public TypeSymbol ReferencedType { get; } = referencedType;

    /// <summary>The width of a relative reference, such as <c>u32</c>, or null.</summary>
    public string? RelativeWidth { get; } = relativeWidth;

    /// <summary>The pool a relative reference is measured from, or null.</summary>
    public string? Pool { get; } = pool;

    public override string ToDisplayString() {
        var suffix = RelativeWidth is null
            ? string.Empty
            : Pool is null
                ? $"<{RelativeWidth}>"
                : $"<{RelativeWidth} in {Pool}>";
        return $"{ReferencedType.ToDisplayString()}&{suffix}";
    }

    public override bool Equals(TypeSymbol? other) =>
        other is ReferenceTypeSymbol reference
        && reference.ReferencedType.Equals(ReferencedType)
        && reference.RelativeWidth == RelativeWidth
        && reference.Pool == Pool;

    public override int GetHashCode() => HashCode.Combine(ReferencedType, RelativeWidth, Pool);
}

public sealed class TupleTypeSymbol(
    ImmutableArray<TypeSymbol> elementTypes,
    ImmutableArray<string?> elementNames) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.TupleType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;

    public ImmutableArray<TypeSymbol> ElementTypes { get; } = elementTypes;
    public ImmutableArray<string?> ElementNames { get; } = elementNames;

    public override string ToDisplayString() {
        var parts = ElementTypes.Select((type, i) =>
            ElementNames.Length > i && ElementNames[i] is { } name
                ? $"{name}: {type.ToDisplayString()}"
                : type.ToDisplayString()
        );
        return $"({string.Join(", ", parts)})";
    }

    public override bool Equals(TypeSymbol? other) {
        if (other is not TupleTypeSymbol tuple) {
            return false;
        }

        if (tuple.ElementTypes.Length != ElementTypes.Length) {
            return false;
        }

        for (var i = 0; i < ElementTypes.Length; i++) {
            if (!ElementTypes[i].Equals(tuple.ElementTypes[i])) {
                return false;
            }

            var left = i < ElementNames.Length ? ElementNames[i] : null;
            var right = i < tuple.ElementNames.Length ? tuple.ElementNames[i] : null;
            if (left != right) {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() {
        var hash = ElementTypes.Length;
        foreach (var type in ElementTypes) {
            hash = (hash * 31) ^ type.GetHashCode();
        }

        return hash;
    }
}

public sealed class FunctionTypeSymbol(
    ImmutableArray<TypeSymbol> parameterTypes,
    TypeSymbol returnType,
    bool isAsync = false,
    TypeSymbol? thrownType = null) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.FunctionType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;

    public ImmutableArray<TypeSymbol> ParameterTypes { get; } = parameterTypes;
    public TypeSymbol ReturnType { get; } = returnType;
    public bool IsAsync { get; } = isAsync;

    /// <summary>The declared error type, or null when the function cannot throw.</summary>
    public TypeSymbol? ThrownType { get; } = thrownType;

    /// <summary>
    ///     Written <c>rethrows</c>: <see cref="ThrownType" /> is then what the
    ///     throwing function parameters throw, and a call only throws when the
    ///     arguments it is given actually do.
    /// </summary>
    public bool IsRethrows { get; init; }

    public override string ToDisplayString() {
        var prefix = IsAsync ? "async " : string.Empty;
        var parameters = string.Join(", ", ParameterTypes.Select(static t => t.ToDisplayString()));
        var throws = IsRethrows
            ? " rethrows"
            : ThrownType is null
                ? string.Empty
                : $" throws({ThrownType.ToDisplayString()})";
        return $"{prefix}({parameters}){throws} -> {ReturnType.ToDisplayString()}";
    }

    public override bool Equals(TypeSymbol? other) {
        if (other is not FunctionTypeSymbol function) {
            return false;
        }

        if (function.IsAsync != IsAsync) {
            return false;
        }

        if (function.IsRethrows != IsRethrows) {
            return false;
        }

        if (!function.ReturnType.Equals(ReturnType)) {
            return false;
        }

        if (function.ParameterTypes.Length != ParameterTypes.Length) {
            return false;
        }

        if (function.ThrownType is null != ThrownType is null) {
            return false;
        }

        if (ThrownType is not null && !ThrownType.Equals(function.ThrownType)) {
            return false;
        }

        for (var i = 0; i < ParameterTypes.Length; i++) {
            if (!ParameterTypes[i].Equals(function.ParameterTypes[i])) {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() {
        var hash = ReturnType.GetHashCode();
        foreach (var type in ParameterTypes) {
            hash = (hash * 31) ^ type.GetHashCode();
        }

        return hash;
    }
}

/// <summary>An anonymous sum type, such as <c>Int | String</c>.</summary>
/// <remarks>
///     Cases are normalised on construction, per docs/03-type-system.md §3.1:
///     nested unions are flattened, duplicates removed, and the result sorted, so
///     that two unions with the same case set are the same type.
/// </remarks>
public sealed class StructuralUnionTypeSymbol : TypeSymbol {
    /// <summary>
    ///     Builds the union of these cases, which is one of them when, after
    ///     flattening and removing duplicates, only one is left.
    /// </summary>
    public static TypeSymbol Create(IEnumerable<TypeSymbol> cases) {
        var union = new StructuralUnionTypeSymbol(cases);
        return union.Cases.Length == 1 ? union.Cases[0] : union;
    }

    public StructuralUnionTypeSymbol(IEnumerable<TypeSymbol> cases) {
        var flattened = new List<TypeSymbol>();
        Flatten(cases, flattened);

        var distinct = new List<TypeSymbol>();
        foreach (var type in flattened) {
            if (!distinct.Exists(existing => existing.Equals(type))) {
                distinct.Add(type);
            }
        }

        distinct.Sort(static (a, b) =>
            string.CompareOrdinal(a.ToDisplayString(), b.ToDisplayString())
        );

        Cases = [..distinct];
    }

    static void Flatten(IEnumerable<TypeSymbol> types, List<TypeSymbol> into) {
        foreach (var type in types) {
            if (type is StructuralUnionTypeSymbol nested) {
                Flatten(nested.Cases, into);
            } else {
                into.Add(type);
            }
        }
    }

    public override SymbolKind Kind => SymbolKind.StructuralUnionType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Union;

    public ImmutableArray<TypeSymbol> Cases { get; }

    public override string ToDisplayString() => string.Join(" | ", Cases.Select(static c => c.ToDisplayString()));

    public override bool Equals(TypeSymbol? other) {
        if (other is not StructuralUnionTypeSymbol union) {
            return false;
        }

        if (union.Cases.Length != Cases.Length) {
            return false;
        }

        for (var i = 0; i < Cases.Length; i++) {
            if (!Cases[i].Equals(union.Cases[i])) {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() {
        var hash = Cases.Length;
        foreach (var type in Cases) {
            hash = (hash * 31) ^ type.GetHashCode();
        }

        return hash;
    }
}

/// <summary><c>any P</c>: a boxed value dispatched through a witness table.</summary>
public sealed class ExistentialTypeSymbol(TypeSymbol constraint) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.ExistentialType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;
    public override bool IsReferenceType => true;

    public TypeSymbol Constraint { get; } = constraint;

    public override string ToDisplayString() => $"any {Constraint.ToDisplayString()}";

    public override bool Equals(TypeSymbol? other) =>
        other is ExistentialTypeSymbol existential && existential.Constraint.Equals(Constraint);

    public override int GetHashCode() => HashCode.Combine(Constraint, 0x0A17);
}

/// <summary><c>some P</c>: one concrete type, hidden from the caller.</summary>
public sealed class OpaqueTypeSymbol(TypeSymbol constraint) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.OpaqueType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;

    public TypeSymbol Constraint { get; } = constraint;

    public override string ToDisplayString() => $"some {Constraint.ToDisplayString()}";

    public override bool Equals(TypeSymbol? other) =>
        other is OpaqueTypeSymbol opaque && opaque.Constraint.Equals(Constraint);

    public override int GetHashCode() => HashCode.Combine(Constraint, 0x50E);
}

/// <summary>
///     Stands in for a type that could not be resolved, so that binding continues
///     and one mistake does not cascade into a page of diagnostics.
/// </summary>
public sealed class ErrorTypeSymbol(string name = "?") : TypeSymbol {
    public static readonly ErrorTypeSymbol Instance = new();

    public override SymbolKind Kind => SymbolKind.ErrorType;
    public override string Name { get; } = name;
    public override TypeKind TypeKind => TypeKind.Error;
    public override bool IsError => true;

    public override string ToDisplayString() => Name;

    /// <summary>Every error type is equal, so errors do not cascade.</summary>
    public override bool Equals(TypeSymbol? other) => other is ErrorTypeSymbol;

    public override int GetHashCode() => 0x0E22;
}

/// <summary><c>P &amp; Q</c>: a value conforming to every part.</summary>
public sealed class CompositionTypeSymbol : TypeSymbol {
    public CompositionTypeSymbol(IEnumerable<TypeSymbol> parts) {
        var flattened = new List<TypeSymbol>();
        Flatten(parts, flattened);

        var distinct = new List<TypeSymbol>();
        foreach (var type in flattened) {
            if (!distinct.Exists(existing => existing.Equals(type))) {
                distinct.Add(type);
            }
        }

        Parts = [..distinct];
    }

    static void Flatten(IEnumerable<TypeSymbol> types, List<TypeSymbol> into) {
        foreach (var type in types) {
            if (type is CompositionTypeSymbol nested) {
                Flatten(nested.Parts, into);
            } else {
                into.Add(type);
            }
        }
    }

    public override SymbolKind Kind => SymbolKind.NamedType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Protocol;

    public ImmutableArray<TypeSymbol> Parts { get; }

    public override string ToDisplayString() => string.Join(" & ", Parts.Select(static p => p.ToDisplayString()));

    public override bool Equals(TypeSymbol? other) {
        if (other is not CompositionTypeSymbol composition) {
            return false;
        }

        if (composition.Parts.Length != Parts.Length) {
            return false;
        }

        for (var i = 0; i < Parts.Length; i++) {
            if (!Parts[i].Equals(composition.Parts[i])) {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() {
        var hash = Parts.Length;
        foreach (var type in Parts) {
            hash = (hash * 31) ^ type.GetHashCode();
        }

        return hash;
    }
}
