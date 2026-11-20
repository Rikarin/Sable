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

    /// <summary>
    ///     The type as written before a suffix such as <c>?</c> or <c>[]</c>:
    ///     a union, a function or a composition takes parentheses, since
    ///     <c>Int | String?</c> reads as a union with an optional in it.
    /// </summary>
    public string ToSuffixedDisplayString() =>
        this is StructuralUnionTypeSymbol or FunctionTypeSymbol or CompositionTypeSymbol
            or ExistentialTypeSymbol or OpaqueTypeSymbol
            ? $"({ToDisplayString()})"
            : ToDisplayString();

    /// <summary>True when this type meets a constraint: conforms to a protocol, or is or inherits from a class.</summary>
    public bool Satisfies(TypeSymbol constraint) => Conversions.Satisfies(this, constraint);

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
    /// <remarks>A construction, <c>Source&lt;Int&gt;</c>, has its definition's.</remarks>
    public TypeParameterSymbol? SelfTypeParameter {
        get => ReferenceEquals(OriginalDefinition, this) ? _selfTypeParameter : OriginalDefinition.SelfTypeParameter;
        internal set => _selfTypeParameter = value;
    }

    TypeParameterSymbol? _selfTypeParameter;

    /// <summary>
    ///     A protocol's <c>associatedtype</c> declarations.
    /// </summary>
    /// <remarks>
    ///     Like <c>Self</c> these are type parameters, and like <c>Self</c> they
    ///     stay out of <see cref="TypeParameters" />: a conformer supplies them by
    ///     name rather than positionally, so they are not part of the protocol's
    ///     arity.
    /// </remarks>
    public ImmutableArray<TypeParameterSymbol> AssociatedTypes {
        get => ReferenceEquals(OriginalDefinition, this) ? _associatedTypes : OriginalDefinition.AssociatedTypes;
        internal set {
            _associatedTypes = value;
            Interlocked.Increment(ref s_declarationVersion);
        }
    }

    ImmutableArray<TypeParameterSymbol> _associatedTypes = [];

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

    /// <summary>
    ///     For a protocol, the array extensions that conform arrays to it,
    ///     directly or through a protocol that refines it:
    ///     <c>extension&lt;Element&gt; Element[:]: Sequence</c>.
    /// </summary>
    /// <remarks>
    ///     An array type has no declaration to list its conformances on, so
    ///     each protocol keeps those it is given; an array conforms where one
    ///     of them matches it (<see cref="Compilation.ArrayConformance" />).
    /// </remarks>
    public ImmutableArray<NamedTypeSymbol> ArrayConformers { get; internal set; } = [];

    /// <summary>
    ///     For a protocol, the extensions of Sable.Core's Optional that conform
    ///     optionals to it, directly or through a protocol that refines it:
    ///     <c>extension Optional: Equatable where Wrapped: Equatable</c>.
    /// </summary>
    /// <remarks>
    ///     <c>T?</c> is a type of its own, not the Optional it desugars to, so
    ///     as with arrays each protocol keeps the extensions that give it to
    ///     optionals; a <c>T?</c> conforms where one of them holds of its T.
    /// </remarks>
    public ImmutableArray<ExtensionSymbol> OptionalConformances { get; internal set; } = [];

    /// <summary>
    ///     For an array extension that declares conformances, what an array
    ///     of its kind has by a name, from every array extension: the
    ///     witnesses its conformances may be met by.
    /// </summary>
    internal Func<string, IEnumerable<Symbol>>? ArrayMembersNamed { get; set; }

    /// <summary>
    ///     For <c>Optional</c>, the shell <c>T?</c> desugars to: <c>Wrapped?</c>,
    ///     in terms of its own parameter. Null for every other type.
    /// </summary>
    /// <remarks>
    ///     As with <see cref="ExtendedArray" />, the shell is never a value's
    ///     type: <c>Optional&lt;Int&gt;</c> is written for <c>Int?</c> and binds
    ///     to it, inside <c>extension Optional</c> <c>self</c> is a
    ///     <c>Wrapped?</c>, and outside its members are looked up on the optional.
    /// </remarks>
    public OptionalTypeSymbol? ExtendedOptional { get; internal set; }

    /// <summary>What <c>self</c> is in a member of the type: the array or optional it stands for, or the type with its own parameters.</summary>
    internal TypeSymbol ExtendedSelf =>
        ExtendedArray ?? ExtendedOptional ?? (TypeSymbol)TypeBinder.OwnConstruction(this);

    /// <summary>The <c>where A == B</c> requirements on this type's parameters.</summary>
    public ImmutableArray<SameTypeRequirement> SameTypeRequirements {
        get => _sameTypeRequirements;
        internal set {
            _sameTypeRequirements = value;
            Interlocked.Increment(ref s_declarationVersion);
        }
    }

    ImmutableArray<SameTypeRequirement> _sameTypeRequirements = [];

    /// <summary>The <c>where C.Item: P</c> requirements on associated types.</summary>
    public ImmutableArray<ConformanceRequirement> ConformanceRequirements { get; internal set; } = [];

    /// <summary>
    ///     The <c>where N &gt; 0</c> predicates on this type's value generic
    ///     parameters, checked for each set of numbers the type is given.
    /// </summary>
    public ImmutableArray<ExpressionSyntax> ValuePredicates { get; internal set; } = [];

    /// <summary>
    ///     The protocols <c>@derive</c> conformed this generic type to. Each
    ///     holds of a construction only where its fields' types, substituted,
    ///     conform too (docs/06 §6.5); see <see cref="DerivedConformance" />.
    /// </summary>
    public ImmutableArray<NamedTypeSymbol> DerivedConformances { get; internal set; } = [];

    /// <summary>
    ///     The extensions that conform this type to a protocol. A constrained
    ///     one's conformance holds of a construction only where its
    ///     requirements do; see <see cref="Conversions.Satisfies" />.
    /// </summary>
    public ImmutableArray<ExtensionSymbol> Extensions { get; internal set; } = [];

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
    ///     <c>@packed</c> (SYNTAX.md §22): a struct whose fields follow one
    ///     another with no padding, aligned to a byte unless <c>@align</c>
    ///     says otherwise.
    /// </summary>
    public bool IsPacked { get; internal set; }

    /// <summary>
    ///     <c>@align(n)</c>: the least alignment of a value of the struct, a
    ///     power of two its size is rounded up to; 0 when none is written.
    /// </summary>
    public int Alignment { get; internal set; }

    /// <summary>
    ///     <c>@reflectable</c> (docs/06 §6.3): the type conforms to
    ///     <c>Reflectable</c>, so <c>TypeInfo.of</c> describes it.
    /// </summary>
    public bool IsReflectable { get; internal set; }

    /// <summary>
    ///     True for a <c>@flags</c> enum: its values are sets of its cases,
    ///     as the integer each is, combined with <c>|</c>.
    /// </summary>
    public bool IsFlags { get; internal set; }

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
                Interlocked.Increment(ref s_declarationVersion);
            }

            return _baseTypes;
        }
        internal set {
            _baseTypes = value;
            Interlocked.Increment(ref s_declarationVersion);
        }
    }

    /// <summary>
    ///     Counts the changes to any type's members and base types, which are
    ///     added to as declarations are read, so that what is worked out from
    ///     them (<see cref="Compilation.ResolveAssociated" />) is known to
    ///     still hold while the count has not moved.
    /// </summary>
    internal static long DeclarationVersion => Interlocked.Read(ref s_declarationVersion);

    static long s_declarationVersion;

    /// <summary>Binds the declaration's base types, its constraints and its 'where' clause; null once it has.</summary>
    internal Action? PendingBaseTypes { get; set; }

    /// <summary>Members by name, built on demand; see <see cref="ModuleSymbol" />.</summary>
    Dictionary<string, List<Symbol>>? _byName;

    internal void AddMember(Symbol member) {
        _members.Add(member);
        Interlocked.Increment(ref s_declarationVersion);

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

    /// <summary>
    ///     For the type parameter a function's <c>some P</c> parameter stands
    ///     for (<see cref="FunctionSymbol.OpaqueTypeParameters" />), that
    ///     <c>some P</c>; null for any other.
    /// </summary>
    public OpaqueTypeSymbol? OpaqueParameter { get; init; }

    /// <summary>Variance, from <c>out T</c> or <c>in T</c>.</summary>
    public VarianceKind Variance { get; internal set; } = VarianceKind.None;

    /// <summary>
    ///     True for <c>T: ~Copyable</c>, which may stand for a <c>@noncopy</c>
    ///     type; any other parameter is implicitly <c>Copyable</c> (docs/03 §3.3).
    /// </summary>
    public bool AllowsNoncopyable { get; internal set; }

    /// <summary>
    ///     What an <c>associatedtype</c> falls back to when a conformer does not
    ///     name it, as in <c>associatedtype Index: Comparable = Int</c>.
    /// </summary>
    public TypeSymbol? DefaultType {
        get {
            // Bound when first read, as the constraints are: 'some Sequence<Int>'
            // reads Failure's default where it is written, which may be before
            // the protocol's file has been through.
            if (DefaultTypeResolver is { } resolve) {
                DefaultTypeResolver = null;
                _defaultType = resolve();
            }

            return _defaultType;
        }
        internal set {
            DefaultTypeResolver = null;
            _defaultType = value;
        }
    }

    TypeSymbol? _defaultType;

    /// <summary>Binds <see cref="DefaultType" /> when it is first read; null once it is.</summary>
    internal Func<TypeSymbol?>? DefaultTypeResolver { get; set; }

    /// <summary>
    ///     For a value generic parameter, <c>N: Int</c>, the integer type of
    ///     its value: it stands for a number, given as <c>Buf&lt;Int, 4&gt;</c>,
    ///     rather than a type. Null for a type parameter.
    /// </summary>
    public TypeSymbol? ValueType { get; internal set; }

    /// <summary>True for a value generic parameter, <c>N: Int</c>.</summary>
    public bool IsValue => ValueType is not null;

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

public sealed class ArrayTypeSymbol(TypeSymbol elementType, ArrayKind arrayKind, int? capacity = null, TypeParameterSymbol? capacityParameter = null) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.ArrayType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;

    public TypeSymbol ElementType { get; } = elementType;
    public ArrayKind ArrayKind { get; } = arrayKind;

    /// <summary>The <c>k</c> of <c>T[..k]</c> or <c>T[k]</c>, when it was written.</summary>
    public int? Capacity { get; } = capacity;

    /// <summary>The value generic parameter <c>N</c> of <c>T[N]</c>, which an instance gives the count of.</summary>
    public TypeParameterSymbol? CapacityParameter { get; } = capacityParameter;

    public override string ToDisplayString() =>
        ArrayKind switch {
            ArrayKind.Fixed => $"{ElementType.ToSuffixedDisplayString()}[{CapacityParameter?.Name ?? Capacity?.ToString(System.Globalization.CultureInfo.InvariantCulture)}]",
            ArrayKind.Inline => $"{ElementType.ToSuffixedDisplayString()}[..{CapacityParameter?.Name ?? Capacity?.ToString(System.Globalization.CultureInfo.InvariantCulture)}]",
            ArrayKind.Grow => $"{ElementType.ToSuffixedDisplayString()}[>..]",
            ArrayKind.GrowShrink => $"{ElementType.ToSuffixedDisplayString()}[>..<]",
            _ => $"{ElementType.ToSuffixedDisplayString()}[:]",
        };

    public override bool Equals(TypeSymbol? other) =>
        other is ArrayTypeSymbol array
        && array.ArrayKind == ArrayKind
        && array.Capacity == Capacity
        && ReferenceEquals(array.CapacityParameter, CapacityParameter)
        && array.ElementType.Equals(ElementType);

    public override int GetHashCode() => HashCode.Combine(ElementType, ArrayKind, Capacity);
}

public sealed class OptionalTypeSymbol(TypeSymbol underlyingType, bool isImplicitlyUnwrapped = false) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.OptionalType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Union;

    public TypeSymbol UnderlyingType { get; } = underlyingType;

    /// <summary>
    ///     <c>T!</c>: an optional that is used as the <c>T</c> it holds
    ///     wherever a <c>T</c> is wanted, trapping when it holds none
    ///     (SYNTAX.md §4, for interop). It is the same type as <c>T?</c> —
    ///     equal to it, and stored the same — only read differently.
    /// </summary>
    public bool IsImplicitlyUnwrapped { get; } = isImplicitlyUnwrapped;

    public override string ToDisplayString() => $"{UnderlyingType.ToSuffixedDisplayString()}{(IsImplicitlyUnwrapped ? "!" : "?")}";

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

/// <summary>Which types are move-only, for the implicit <c>Copyable</c> constraint on a generic parameter.</summary>
internal static class Copyability {
    /// <summary>
    ///     True for a <c>@noncopy</c> type, something that holds one inline,
    ///     or a parameter that may stand for one (<c>T: ~Copyable</c>).
    /// </summary>
    public static bool IsNoncopyable(TypeSymbol type) =>
        type switch {
            NamedTypeSymbol named => named.IsNoncopyable || named.OriginalDefinition.IsNoncopyable,
            TypeParameterSymbol parameter => parameter.AllowsNoncopyable,
            OptionalTypeSymbol optional => IsNoncopyable(optional.UnderlyingType),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(IsNoncopyable),
            _ => false,
        };

    /// <summary>
    ///     The message for a move-only type given to a parameter that copies,
    ///     or null when it may have it. The compiler's own generics — the
    ///     'Wrapped' of 'T?' — are declared nowhere, and say for themselves.
    /// </summary>
    public static string? Refusal(TypeParameterSymbol parameter, TypeSymbol argument, string owner) =>
        parameter.AllowsNoncopyable || parameter.DeclaringSyntax.IsEmpty || !IsNoncopyable(argument)
            ? null
            : $"'{argument.ToDisplayString()}' {(argument is TypeParameterSymbol ? "may be @noncopy ('~Copyable')" : "is @noncopy")}, "
              + $"so it cannot be '{parameter.Name}' of '{owner}', which is implicitly Copyable; "
              + $"declare it '{parameter.Name}: ~Copyable' to take a move-only type";
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

    /// <summary>
    ///     Which parameters are <c>inout</c>: <c>(inout Int) -&gt; ()</c> is
    ///     given the caller's Int where it is, to change. Empty when none is.
    /// </summary>
    public ImmutableArray<bool> InOutParameters { get; init; } = [];

    /// <summary>Whether parameter <paramref name="index" /> is <c>inout</c>.</summary>
    public bool IsInOut(int index) => index < InOutParameters.Length && InOutParameters[index];

    /// <summary>Which of these parameters are <c>inout</c>, empty when none is.</summary>
    public static ImmutableArray<bool> InOutOf(IEnumerable<bool> inOut) {
        var all = inOut.ToImmutableArray();
        return all.Contains(true) ? all : [];
    }

    public override string ToDisplayString() {
        var prefix = IsAsync ? "async " : string.Empty;
        var parameters = string.Join(", ", ParameterTypes.Select((t, i) => (IsInOut(i) ? "inout " : string.Empty) + t.ToDisplayString()));
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
            if (!ParameterTypes[i].Equals(function.ParameterTypes[i]) || IsInOut(i) != function.IsInOut(i)) {
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
    ///     Builds the union of these cases, normalised as docs/03 §3.1 says:
    ///     flattened, without duplicates, and with what another case subsumes
    ///     collapsed — so it is one of them when only one is left.
    /// </summary>
    /// <remarks>
    ///     <c>Never</c> has no values, so it adds no case: <c>Int | Never</c>
    ///     is <c>Int</c>. An optional case makes the whole union optional,
    ///     since an optional is itself a union with none: <c>T | T?</c> is
    ///     <c>T?</c>, and <c>Int | String?</c> is <c>(Int | String)?</c> — one
    ///     none, outside, rather than one hidden in a case.
    /// </remarks>
    public static TypeSymbol Create(IEnumerable<TypeSymbol> cases) {
        var flattened = new List<TypeSymbol>();
        Flatten(cases, flattened);

        var live = flattened.Where(static t => !IsNever(t)).ToList();
        if (live.Count == 0) {
            return flattened[0];
        }

        if (live.Exists(static t => t is OptionalTypeSymbol)) {
            return new OptionalTypeSymbol(
                Create(live.Select(static t => t is OptionalTypeSymbol optional ? optional.UnderlyingType : t))
            );
        }

        var union = new StructuralUnionTypeSymbol(live);
        return union.Cases.Length == 1 ? union.Cases[0] : union;
    }

    /// <summary>
    ///     The union a type is, seeing through an alias that defines one in
    ///     terms of itself; null when it is not a structural union.
    /// </summary>
    public static StructuralUnionTypeSymbol? Of(TypeSymbol? type) =>
        type switch {
            StructuralUnionTypeSymbol union => union,
            RecursiveUnionTypeSymbol recursive => recursive.Union,
            _ => null,
        };

    static bool IsNever(TypeSymbol type) =>
        type is NamedTypeSymbol { TypeKind: TypeKind.Builtin, Name: "Never", TypeArguments.IsEmpty: true };

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

/// <summary>
///     A structural union defined in terms of itself, through an alias:
///     <c>alias Json = Bool | String | Json[] | [String: Json]</c>.
/// </summary>
/// <remarks>
///     The type is infinite when written out, so it is known by its alias,
///     as a nominal type is by its declaration: each mention of <c>Json</c>,
///     inside the union or out, is this one symbol, and it is equal only to
///     itself. What it holds is <see cref="Union" />, whose cases mention it
///     again — so walking a type's parts stops here rather than going round.
///     Everything that asks a union for its cases asks
///     <see cref="StructuralUnionTypeSymbol.Of" />, which sees through it.
/// </remarks>
public sealed class RecursiveUnionTypeSymbol : TypeSymbol {
    internal RecursiveUnionTypeSymbol(AliasSymbol alias) => Alias = alias;

    public AliasSymbol Alias { get; }

    /// <summary>The union the alias stands for; null while it is being bound.</summary>
    public StructuralUnionTypeSymbol? Union { get; internal set; }

    public override SymbolKind Kind => SymbolKind.StructuralUnionType;
    public override string Name => Alias.Name;
    public override TypeKind TypeKind => TypeKind.Union;

    public override string ToDisplayString() => Alias.Name;

    public override bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
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
/// <remarks>
///     A <c>some P</c> parameter has no <see cref="Origin" />: it is an
///     anonymous generic parameter, and the caller chooses its type. A result
///     or a local's has one — the function or local whose definition chooses
///     the type — and is a type of its own: two functions returning
///     <c>some Shape</c> return two types, which may not mix, while two calls
///     of one function return the same. A generic origin's hidden type may
///     mention its type parameters, so the opaque type carries what they stand
///     for where it is used, in <see cref="Context" />.
/// </remarks>
public sealed class OpaqueTypeSymbol(
    TypeSymbol constraint,
    Symbol? origin = null,
    TypeSubstitution? context = null,
    ImmutableArray<TypeSymbol> arguments = default
) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.OpaqueType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;

    public TypeSymbol Constraint { get; } = constraint;

    /// <summary>
    ///     What <c>some Sequence&lt;Int&gt;</c> says of the protocol's associated
    ///     types, in the order the protocol declares them: Element is Int.
    ///     Empty for <c>some P</c> alone.
    /// </summary>
    public ImmutableArray<TypeSymbol> Arguments { get; } = arguments.IsDefault ? [] : arguments;

    /// <summary>The same opaque type, of another origin: what its arguments say carries over.</summary>
    public OpaqueTypeSymbol WithOrigin(Symbol? origin, TypeSubstitution? context = null) =>
        new(Constraint, origin, context, Arguments);

    /// <summary>What the arguments make an associated type of the constraint, or null where they say nothing.</summary>
    public TypeSymbol? ArgumentFor(TypeParameterSymbol associated) {
        if (Arguments.IsEmpty || Constraint is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
            return null;
        }

        var declared = protocol.OriginalDefinition.AssociatedTypes;
        for (var i = 0; i < Arguments.Length && i < declared.Length; i++) {
            if (ReferenceEquals(declared[i], associated)) {
                return Arguments[i];
            }
        }

        return null;
    }

    /// <summary>The function whose result, or the local whose value, this is the type of; null for a parameter.</summary>
    public Symbol? Origin { get; } = origin;

    /// <summary>What the origin's type parameters stand for where this type is used.</summary>
    public TypeSubstitution Context { get; } = context ?? TypeSubstitution.Empty;

    /// <summary>
    ///     The concrete type behind it, which only code generation may look at;
    ///     null for a parameter, or before the origin's body is bound.
    /// </summary>
    public TypeSymbol? HiddenType =>
        (Origin switch {
            FunctionSymbol function => function.HiddenResultType,
            LocalSymbol local => local.HiddenType,
            FieldSymbol property => property.HiddenType,
            _ => null,
        }) is { } hidden
            ? Context.Apply(hidden)
            : null;

    public override string ToDisplayString() =>
        Arguments.IsEmpty
            ? $"some {Constraint.ToDisplayString()}"
            : $"some {Constraint.ToDisplayString()}<{string.Join(", ", Arguments.Select(static a => a.ToDisplayString()))}>";

    public override bool Equals(TypeSymbol? other) =>
        other is OpaqueTypeSymbol opaque
        && ReferenceEquals(opaque.Origin, Origin)
        && opaque.Constraint.Equals(Constraint)
        && opaque.Arguments.Length == Arguments.Length
        && opaque.Arguments.Zip(Arguments).All(static p => p.First.Equals(p.Second))
        && opaque.Context.SameAs(Context);

    public override int GetHashCode() =>
        HashCode.Combine(Constraint, Origin is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Origin), 0x50E);
}

/// <summary>
///     <c>T.Type</c>: the type of <c>T.self</c>, a type used as a value
///     (SYNTAX.md §16). It has one value, the type itself, so it holds nothing
///     at run time: code is generated for each type it is used with, and the
///     value says nothing the type does not.
/// </summary>
public sealed class MetatypeTypeSymbol(TypeSymbol instanceType) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.MetatypeType;
    public override string Name => string.Empty;
    public override TypeKind TypeKind => TypeKind.Struct;

    /// <summary>The type this is the type of: <c>Int</c> for <c>Int.Type</c>.</summary>
    public TypeSymbol InstanceType { get; } = instanceType;

    public override bool IsError => InstanceType.IsError;

    public override string ToDisplayString() => $"{InstanceType.ToSuffixedDisplayString()}.Type";

    public override bool Equals(TypeSymbol? other) =>
        other is MetatypeTypeSymbol metatype && metatype.InstanceType.Equals(InstanceType);

    public override int GetHashCode() => HashCode.Combine(InstanceType, 0x7E7A);
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

/// <summary>
///     <c>4</c> in <c>Buf&lt;Int, 4&gt;</c>: a value given to a value generic
///     parameter, which stands in the type arguments where a type would.
/// </summary>
public sealed class ValueTypeArgumentSymbol(long value) : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.ValueTypeArgument;
    public override string Name => ToDisplayString();
    public override TypeKind TypeKind => TypeKind.Builtin;

    public long Value { get; } = value;

    public override string ToDisplayString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override bool Equals(TypeSymbol? other) => other is ValueTypeArgumentSymbol argument && argument.Value == Value;

    public override int GetHashCode() => HashCode.Combine(Value, 0x7A1E);
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

    /// <summary>
    ///     The separate constraints a constraint stands for: <c>T: P &amp; Q</c>
    ///     says what <c>T: P, T: Q</c> says, so a parameter keeps the parts.
    /// </summary>
    public static ImmutableArray<TypeSymbol> PartsOf(TypeSymbol constraint) =>
        constraint is CompositionTypeSymbol composition ? composition.Parts : [constraint];

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
