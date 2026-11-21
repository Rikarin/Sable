using System.Collections.Immutable;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>How generic functions are compiled (#297).</summary>
public enum GenericsMode {
    /// <summary>A copy of each generic function for each set of type arguments it is used with.</summary>
    Specialised,

    /// <summary>
    ///     One copy of each generic function that can be compiled once, taking
    ///     its type arguments' metadata and witness tables as hidden
    ///     parameters; the rest are still specialised.
    /// </summary>
    Unspecialised,
}

/// <summary>
///     Unspecialised generic code (#297, docs/06 §6.2): a generic function
///     compiled once for every type, as Swift compiles one. A value of a type
///     parameter's type is an <see cref="OpaqueType" />, held indirectly in
///     storage the type's metadata sizes at run time: a parameter is borrowed
///     where its caller holds it, a result written where its caller says;
///     the function takes each type parameter's metadata, and a witness table
///     for each protocol it is constrained to, after its own parameters. A
///     requirement is called through the table's entries in the generic
///     calling convention (<see cref="SilWitnessTable.GenericEntries" />),
///     which take the value where it is and Self's metadata.
/// </summary>
/// <remarks>
///     <para>
///         A function is compiled this way when the build asks for it
///         (<see cref="GenericsMode.Unspecialised" />) and every use it makes
///         of a type parameter is one it can serve (<see cref="ShapeChecker" />):
///         values of the type parameter itself, passed, stored, returned,
///         compared by metatype, written out, given to a requirement of its
///         protocols or to another such function, a closure over them among
///         them. Anything else — a slice of <c>T</c>, a method of a generic
///         class, an async function — is still specialised for its type
///         arguments, so a program builds either way.
///     </para>
///     <para>
///         Where the types are known, a call stores each argument of a type
///         parameter's type in a slot of its frame and passes its address,
///         and gives a slot for the result; inside unspecialised code, values
///         are passed on where they are.
///     </para>
/// </remarks>
public sealed partial class SilGen {
    static readonly AsyncLocal<GenericsMode?> AmbientGenerics = new();

    /// <summary>
    ///     How lowering compiles generics: what this code path has set
    ///     (<see cref="UseGenerics" />), else <c>SABLE_GENERICS</c>
    ///     (<c>unspecialised</c> or <c>specialised</c>), else specialised.
    /// </summary>
    public static GenericsMode Generics => AmbientGenerics.Value ?? GenericsFromEnvironment();

    static GenericsMode GenericsFromEnvironment() =>
        ParseGenerics(Environment.GetEnvironmentVariable("SABLE_GENERICS")) ?? GenericsMode.Specialised;

    /// <summary>The mode a <c>-generics=</c> flag or <c>SABLE_GENERICS</c> names, or null for neither.</summary>
    public static GenericsMode? ParseGenerics(string? text) =>
        text switch {
            "unspecialised" or "unspecialized" => GenericsMode.Unspecialised,
            "specialised" or "specialized" => GenericsMode.Specialised,
            _ => null,
        };

    /// <summary>Lowers with this mode until the handle is disposed, on this code path and what it calls.</summary>
    public static IDisposable UseGenerics(GenericsMode mode) {
        var before = AmbientGenerics.Value;
        AmbientGenerics.Value = mode;
        return new Restore(() => AmbientGenerics.Value = before);
    }

    sealed class Restore(Action undo) : IDisposable {
        public void Dispose() => undo();
    }

    readonly GenericsMode _generics = Generics;

    /// <summary>The type parameters of the unspecialised code being lowered: each lowers to an <see cref="OpaqueType" />.</summary>
    readonly List<TypeParameterSymbol> _opaque = [];

    IDisposable Opaque(GenericShape shape) {
        var count = _opaque.Count;
        _opaque.AddRange(shape.Parameters);
        return new Restore(() => _opaque.RemoveRange(count, _opaque.Count - count));
    }

    /// <summary>True for a type parameter of the unspecialised code being lowered, or an associated type read through one.</summary>
    internal bool IsOpaque(TypeSymbol type) => type is TypeParameterSymbol parameter && _opaque.Contains(parameter) || IsAssociatedOf(type, _opaque);

    /// <summary>
    ///     True for an associated type read through one of the type
    ///     parameters — <c>S.Element</c>, <c>C.Iter.Element</c> — or, inside a
    ///     protocol extension generic over its <c>Self</c>, named bare
    ///     (<c>Element</c>, Self's): what unspecialised code knows only by the
    ///     metadata the table of its base's conformance binds it to.
    /// </summary>
    internal static bool IsAssociatedOf(TypeSymbol type, IReadOnlyCollection<TypeParameterSymbol> parameters) =>
        parameters.Count > 0 && type switch {
            DependentMemberTypeSymbol { BaseType: var baseType } => baseType is TypeParameterSymbol root && parameters.Contains(root) || IsAssociatedOf(baseType, parameters),
            TypeParameterSymbol associated => AssociatedSelf(associated, parameters) is not null,
            _ => false,
        };

    /// <summary>The Self among the type parameters, of a protocol that has this associated type, for one named bare; null for any other type parameter.</summary>
    internal static TypeParameterSymbol? AssociatedSelf(TypeParameterSymbol associated, IReadOnlyCollection<TypeParameterSymbol> parameters) {
        if (associated.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } declaring || ReferenceEquals(declaring.OriginalDefinition.SelfTypeParameter, associated)
            || !declaring.OriginalDefinition.AssociatedTypes.Contains(associated)) {
            return null;
        }

        return parameters.LastOrDefault(p => p.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol } owner && ReferenceEquals(owner.OriginalDefinition.SelfTypeParameter, p)
                                             && ProtocolClosure([owner]).Any(c => ReferenceEquals(c, declaring.OriginalDefinition)));
    }

    /// <summary>
    ///     The name an opaque type has in SIL: a type parameter's own, an
    ///     associated type's its base's and its own, <c>S.Element</c>, and a
    ///     protocol extension's bare one Self's, <c>Self.Element</c>.
    /// </summary>
    internal string OpaqueName(TypeSymbol type) =>
        type switch {
            DependentMemberTypeSymbol member => $"{OpaqueName(member.BaseType)}.{member.Associated.Name}",
            TypeParameterSymbol parameter when !_opaque.Contains(parameter) && AssociatedSelf(parameter, _opaque) is { } self => $"{self.Name}.{parameter.Name}",
            _ => type.Name,
        };

    /// <summary>The function whose unspecialised body is being lowered: the same-type requirements its associated types are read with.</summary>
    Symbol? _associatedContext;

    /// <summary>Reads associated types with the requirements in force inside a declaration until the handle is disposed.</summary>
    internal IDisposable AssociatedContext(Symbol? context) {
        var before = _associatedContext;
        _associatedContext = context;
        return new Restore(() => _associatedContext = before);
    }

    /// <summary>
    ///     How an unspecialised function finds the metadata of each associated
    ///     type its values have (<see cref="SilFunction.AssociatedMetadata" />):
    ///     each opaque name it uses, <c>S.Iter.Element</c>, read one step at a
    ///     time from its root type parameter — each step an associated type of
    ///     a protocol the step before conforms to, found in the table of that
    ///     conformance: one the function is given, or the registry's.
    /// </summary>
    void AssociatedRecipes(SilFunction function, FunctionSymbol symbol, GenericShape shape) {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Collect(SilType? type) {
            switch (type) {
                case OpaqueType opaque when opaque.Name.Contains('.'):
                    names.Add(opaque.Name);
                    break;
                case AddressType address:
                    Collect(address.Pointee);
                    break;
                case ArrayType array:
                    Collect(array.Element);
                    break;
                case SliceType slice:
                    Collect(slice.Element);
                    break;
                case TupleType tuple:
                    foreach (var element in tuple.Elements) {
                        Collect(element);
                    }

                    break;
                case EnumType enumType:
                    foreach (var (_, payload) in enumType.Cases) {
                        Collect(payload);
                    }

                    foreach (var argument in enumType.TypeArguments) {
                        Collect(argument);
                    }

                    break;
                case StructType structType:
                    foreach (var field in structType.Fields) {
                        Collect(field.Type);
                    }

                    foreach (var argument in structType.TypeArguments) {
                        Collect(argument);
                    }

                    break;
            }
        }

        foreach (var block in function.Blocks) {
            foreach (var argument in block.Arguments) {
                Collect(argument.Type);
            }

            foreach (var instruction in block.Instructions) {
                if (instruction is BuiltinInst { Name: var builtin } && builtin.StartsWith("opaque_metadata:", StringComparison.Ordinal)) {
                    names.Add(builtin["opaque_metadata:".Length..]);
                }

                if (instruction.HasResult) {
                    Collect(instruction.Type);
                }
            }
        }

        var made = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names) {
            var steps = name.Split('.');
            if (shape.Parameters.FirstOrDefault(p => p.Name == steps[0]) is not { } root) {
                continue;
            }

            TypeSymbol current = root;
            var currentName = steps[0];
            for (var i = 1; i < steps.Length; i++) {
                var protocols = ProtocolClosure(ProtocolsKnown(symbol, shape, current));
                var associated = protocols.SelectMany(static p => p.AssociatedTypes).FirstOrDefault(a => a.Name == steps[i]);
                if (associated?.ContainingSymbol is not NamedTypeSymbol declaring) {
                    break;
                }

                var stepName = $"{currentName}.{steps[i]}";
                if (made.Add(stepName)) {
                    string? table = null;
                    var path = ImmutableArray<int>.Empty;
                    if (ReferenceEquals(current, root)) {
                        foreach (var (parameter, protocol) in shape.Requirements) {
                            if (ReferenceEquals(parameter, root) && FunctionLowering.BasePathOf(protocol, declaring.OriginalDefinition) is { } found) {
                                table = $"$witnesses.{parameter.Name}.{protocol.Name}";
                                path = [..found];
                                break;
                            }
                        }
                    }

                    function.AssociatedMetadata.Add(new(stepName, currentName, QualifiedName(declaring), declaring.OriginalDefinition.AssociatedTypes.IndexOf(associated), table, path));
                }

                current = AssociatedMember(current, associated);
                currentName = stepName;
            }
        }
    }

    /// <summary><c>Base.Associated</c> as the binder names it: bare inside its protocol, else a dependent member.</summary>
    static TypeSymbol AssociatedMember(TypeSymbol baseType, TypeParameterSymbol associated) =>
        baseType is TypeParameterSymbol { ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } owner } self && ReferenceEquals(owner.OriginalDefinition.SelfTypeParameter, self)
            ? associated
            : new DependentMemberTypeSymbol(baseType, associated);

    /// <summary>The protocols a type of unspecialised code is known to conform to: a type parameter's tables, and what its declaration's constraints and 'where' clauses say.</summary>
    internal IEnumerable<NamedTypeSymbol> ProtocolsKnown(Symbol context, GenericShape shape, TypeSymbol type) {
        var known = new List<NamedTypeSymbol>();
        if (type is TypeParameterSymbol parameter) {
            known.AddRange(shape.ProtocolsOf(parameter));
            if (parameter.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol } owner && ReferenceEquals(owner.OriginalDefinition.SelfTypeParameter, parameter)) {
                known.Add(owner.OriginalDefinition);
            }
        }

        foreach (var constraint in _compilation.ConstraintsIn(context, type)) {
            foreach (var part in CompositionTypeSymbol.PartsOf(constraint)) {
                if (part is NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol && !known.Any(k => ReferenceEquals(k, protocol.OriginalDefinition))) {
                    known.Add(protocol.OriginalDefinition);
                }
            }
        }

        return known;
    }

    /// <summary>A type as the same-type requirements in force where it is lowered make it: <c>S.Iter.Element</c> as <c>S.Element</c>.</summary>
    internal TypeSymbol Canonical(TypeSymbol type) => _associatedContext is { } context ? _compilation.CanonicalType(context, type) : type;

    /// <summary>
    ///     What an unspecialised function takes besides its own parameters: a
    ///     metadata for each type parameter, then a witness table for each
    ///     protocol one is constrained to, in this order.
    /// </summary>
    internal sealed record GenericShape(
        ImmutableArray<TypeParameterSymbol> Parameters,
        ImmutableArray<(TypeParameterSymbol Parameter, NamedTypeSymbol Protocol)> Requirements
    ) {
        public IEnumerable<NamedTypeSymbol> ProtocolsOf(TypeParameterSymbol parameter) =>
            Requirements.Where(r => ReferenceEquals(r.Parameter, parameter)).Select(static r => r.Protocol);
    }

    readonly Dictionary<FunctionSymbol, GenericShape?> _shapes = new(ReferenceEqualityComparer.Instance);

    /// <summary>For each unspecialised function, whether it, or unspecialised code it calls, writes a value of a type it is given out.</summary>
    readonly Dictionary<FunctionSymbol, bool> _describes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     Whether an unspecialised callee may write out a value of a type it
    ///     is given (<c>generic_describe</c>), and so needs that type's
    ///     describer: as its body says, or, for one still being decided — a
    ///     recursion — yes.
    /// </summary>
    internal bool DescribesValues(FunctionSymbol callee) => _describes.GetValueOrDefault(callee, true);
    readonly List<FunctionSymbol> _shapeOrder = [];
    readonly HashSet<FunctionSymbol> _shaping = new(ReferenceEqualityComparer.Instance);

    /// <summary>The unspecialised instances asked for, by name: the function and what it takes.</summary>
    readonly Dictionary<string, (FunctionSymbol Symbol, GenericShape Shape)> _unspecialised = new(StringComparer.Ordinal);

    /// <summary>
    ///     What a generic function takes compiled once for every type, or null
    ///     when it is specialised: when lowering is not unspecialised, or when
    ///     its signature or its body uses a type parameter in a way
    ///     unspecialised code cannot serve yet.
    /// </summary>
    internal GenericShape? UnspecialisedShape(FunctionSymbol symbol) {
        if (_generics != GenericsMode.Unspecialised) {
            return null;
        }

        if (_shapes.TryGetValue(symbol, out var known)) {
            return known;
        }

        if (SignatureShape(symbol, out var refused) is not { } shape) {
            if (refused is not null && Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 }) {
                Console.Error.WriteLine($"specialised {TraceName(symbol)}: {refused}");
            }

            _shapes[symbol] = null;
            _shapeOrder.Add(symbol);
            return null;
        }

        // A function its own body reaches is taken to be one until its body says otherwise.
        if (!_shaping.Add(symbol)) {
            return shape;
        }

        var before = _shapeOrder.Count;
        bool allowed;
        try {
            allowed = BodyAllows(symbol, shape);
        } finally {
            _shaping.Remove(symbol);
        }

        // What was decided while this one was taken to be unspecialised is decided again.
        if (!allowed) {
            foreach (var decided in _shapeOrder.Skip(before)) {
                _shapes.Remove(decided);
            }

            _shapeOrder.RemoveRange(before, _shapeOrder.Count - before);
        }

        _shapes[symbol] = allowed ? shape : null;
        _shapeOrder.Add(symbol);
        return allowed ? shape : null;
    }

    /// <summary>A protocol generic code reaches through a witness table: neither it nor one it refines generic.</summary>
    static bool IsTabledProtocol(NamedTypeSymbol protocol) =>
        protocol is { TypeKind: TypeKind.Protocol, IsConstructed: false } && protocol.Name != "AnyObject"
        && ProtocolClosure([protocol]).All(static p => p.TypeParameters.IsEmpty)
        && Bases(protocol).All(static b => !b.IsConstructed);

    static IEnumerable<NamedTypeSymbol> Bases(NamedTypeSymbol protocol) =>
        protocol.OriginalDefinition.BaseTypes.OfType<NamedTypeSymbol>().Where(static b => b.TypeKind == TypeKind.Protocol);

    /// <summary>The protocols given, and every one they refine, each once.</summary>
    internal static List<NamedTypeSymbol> ProtocolClosure(IEnumerable<NamedTypeSymbol> roots) {
        var found = new List<NamedTypeSymbol>();
        var pending = new Stack<NamedTypeSymbol>(roots.Select(static r => r.OriginalDefinition).Reverse());
        while (pending.TryPop(out var protocol)) {
            if (found.Any(p => ReferenceEquals(p, protocol))) {
                continue;
            }

            found.Add(protocol);
            foreach (var refined in Bases(protocol).Reverse()) {
                pending.Push(refined.OriginalDefinition);
            }
        }

        return found;
    }

    /// <summary>
    ///     True for an array — <c>T[]</c>, <c>T[&gt;..]</c> or <c>T[&gt;..&lt;]</c>, a
    ///     buffer whatever its elements — of one of the type parameters, or of
    ///     such arrays: what unspecialised code holds as an array of opaque
    ///     values, with the array's metadata made at run time from its element's.
    /// </summary>
    internal static bool IsArrayOf(TypeSymbol type, IReadOnlyCollection<TypeParameterSymbol> parameters) =>
        type is ArrayTypeSymbol { ArrayKind: ArrayKind.Grow or ArrayKind.GrowShrink or ArrayKind.Fixed or ArrayKind.Slice, ElementType: var element }
        && element is not MetatypeTypeSymbol && IsHeld(element, parameters);

    /// <summary>
    ///     True for a type unspecialised code over <paramref name="parameters" />
    ///     can hold a value of: one of them, its metatype, an array of them, or an
    ///     optional or a tuple made of such types (and of types that mention
    ///     none), laid out at run time from its parts' metadata.
    /// </summary>
    internal static bool IsHeld(TypeSymbol type, IReadOnlyCollection<TypeParameterSymbol> parameters) =>
        type switch {
            TypeParameterSymbol parameter => parameters.Contains(parameter) || IsAssociatedOf(parameter, parameters),
            DependentMemberTypeSymbol => IsAssociatedOf(type, parameters),
            MetatypeTypeSymbol { InstanceType: var instance } => instance is TypeParameterSymbol p && parameters.Contains(p) || IsAssociatedOf(instance, parameters),
            ArrayTypeSymbol => IsArrayOf(type, parameters),
            OptionalTypeSymbol optional => IsHeld(optional.UnderlyingType, parameters) && optional.UnderlyingType is not MetatypeTypeSymbol,
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(e => Mentions(e, parameters))
                                     && tuple.ElementTypes.All(e => !Mentions(e, parameters) || IsHeld(e, parameters) && e is not MetatypeTypeSymbol),
            NamedTypeSymbol { IsConstructed: true } generic => IsPlainGeneric(generic.OriginalDefinition) && generic.TypeArguments.Any(a => Mentions(a, parameters))
                                                             && generic.TypeArguments.All(a => !Mentions(a, parameters) || IsHeld(a, parameters) && a is not MetatypeTypeSymbol),
            _ => false,
        };

    /// <summary>
    ///     True for a function type of values unspecialised code over
    ///     <paramref name="parameters" /> holds — <c>(T) -> U</c>,
    ///     <c>(Element, Element) -> Bool</c>, <c>(inout Value) -> Void</c> —
    ///     called in the generic convention: each parameter of a type held
    ///     indirectly borrowed where its caller has it, a result of one
    ///     written where its caller says. A closure of a type known where it
    ///     is made is given to such code through a thunk that converts.
    /// </summary>
    internal static bool IsGenericFunction(TypeSymbol type, IReadOnlyCollection<TypeParameterSymbol> parameters) =>
        type is FunctionTypeSymbol { IsRethrows: false, IsAsync: false } function && Mentions(function, parameters)
        && function.ParameterTypes.All(p => !Mentions(p, parameters) || IsHeld(p, parameters) && p is not MetatypeTypeSymbol)
        && (!Mentions(function.ReturnType, parameters) || IsHeld(function.ReturnType, parameters) && function.ReturnType is not MetatypeTypeSymbol)
        && (function.ThrownType is not { } thrown || !Mentions(thrown, parameters));

    /// <summary>
    ///     A generic struct or union whose instance unspecialised code can lay
    ///     out at run time as TypeLayout lays out one it knows: its fields or
    ///     cases one after another — no deinit's live byte, no packing or
    ///     alignment of its own, nothing move-only, no case in a box of its own.
    /// </summary>
    static bool IsPlainGeneric(NamedTypeSymbol definition) {
        if (definition.IsNoncopyable || definition.TypeParameters.IsEmpty) {
            return false;
        }

        switch (definition.TypeKind) {
            case TypeKind.Struct:
                return !definition.IsInlineLayout && !definition.IsPacked && definition.Alignment == 0
                       && !definition.Members.OfType<FunctionSymbol>().Any(static f => f.FunctionKind == FunctionKind.Deinitializer)
                       && StoredFields(definition).All(f => f.ReferenceKind == default && Layable(f.Type, definition));
            case TypeKind.Union:
                return definition.Members.OfType<UnionCaseSymbol>().All(c => !c.HasAttribute("indirect")
                                                                             && c.Payload.All(p => Layable(p.Type, definition)));
            case TypeKind.Class:
                return IsPlainGenericClass(definition);
            default:
                return false;
        }
    }

    /// <summary>
    ///     A generic class whose instance is laid out alike whatever its type
    ///     arguments: no superclass, and no stored field that holds a value of
    ///     a type parameter by value — one of them only through a reference, an
    ///     array or a slice of them (<c>T[&gt;..]</c>, <c>T?[]</c>), or another
    ///     such class — so that unspecialised code reaches its fields where
    ///     code that knows the type does, and holds the reference as it is.
    /// </summary>
    static bool IsPlainGenericClass(NamedTypeSymbol definition) =>
        definition is { TypeKind: TypeKind.Class, IsNoncopyable: false, TypeParameters.IsEmpty: false }
        // A task is the runtime's, laid out for its result (TaskClass).
        && definition is not { Name: "Task", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }
        && !definition.TypeParameters.Any(static p => p.IsValue || p.AllowsNoncopyable)
        && definition.BaseTypes.OfType<NamedTypeSymbol>().All(static b => b.TypeKind != TypeKind.Class)
        && StoredFields(definition).All(f => f.ReferenceKind == default && !MentionsDefinition(f.Type, definition)
                                             && (!Mentions(f.Type, definition.TypeParameters) || ByReference(f.Type, definition.TypeParameters)));

    /// <summary>A type that mentions type parameters only behind a reference: an array or a slice of held values, or a plain generic class of them.</summary>
    static bool ByReference(TypeSymbol type, IReadOnlyCollection<TypeParameterSymbol> parameters) =>
        IsArrayOf(type, parameters)
        || type is NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Class } named && IsPlainGenericClass(named.OriginalDefinition)
           && named.TypeArguments.All(a => !Mentions(a, parameters) || IsHeld(a, parameters) && a is not MetatypeTypeSymbol)
        // A plain generic struct that holds its own type parameters only behind references: '[Key: Int]'.
        || type is NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Struct } structure && IsPlainGeneric(structure.OriginalDefinition)
           && StoredFields(structure.OriginalDefinition).All(f => !Mentions(f.Type, structure.OriginalDefinition.TypeParameters) || ByReference(f.Type, structure.OriginalDefinition.TypeParameters))
           && structure.TypeArguments.All(a => !Mentions(a, parameters) || IsHeld(a, parameters) && a is not MetatypeTypeSymbol);

    /// <summary>A field's or a payload's type: one of the definition's type parameters held, or one that mentions none, and no instance of the definition itself.</summary>
    static bool Layable(TypeSymbol type, NamedTypeSymbol definition) =>
        !MentionsDefinition(type, definition) && (!Mentions(type, definition.TypeParameters) || IsHeld(type, definition.TypeParameters) && type is not MetatypeTypeSymbol);

    /// <summary>What a member of a generic type, an optional or an array called on a receiver of this type binds the type's type parameters to; null for a receiver of another type.</summary>
    static Dictionary<TypeParameterSymbol, TypeSymbol>? OwnerBindings(NamedTypeSymbol owner, TypeSymbol? receiver) {
        if (receiver is null || owner.TypeParameters.IsEmpty) {
            return null;
        }

        if (owner.ExtendedOptional is not null) {
            return receiver is OptionalTypeSymbol optional ? new() { [owner.TypeParameters[0]] = optional.UnderlyingType } : null;
        }

        if (owner.ExtendedArray is not null) {
            return receiver is ArrayTypeSymbol array && owner.TypeParameters.Length == 1 ? new() { [owner.TypeParameters[0]] = array.ElementType } : null;
        }

        return receiver is NamedTypeSymbol named && ReferenceEquals(named.OriginalDefinition, owner) && named.TypeArguments.Length == owner.TypeParameters.Length
            ? owner.TypeParameters.Zip(named.TypeArguments).ToDictionary(static p => p.First, static p => p.Second)
            : null;
    }

    /// <summary>True when a type is, or is made of, an instance of the definition.</summary>
    static bool MentionsDefinition(TypeSymbol type, NamedTypeSymbol definition) =>
        type switch {
            NamedTypeSymbol named => ReferenceEquals(named.OriginalDefinition, definition) || named.TypeArguments.Any(a => MentionsDefinition(a, definition)),
            ArrayTypeSymbol array => MentionsDefinition(array.ElementType, definition),
            OptionalTypeSymbol optional => MentionsDefinition(optional.UnderlyingType, definition),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(e => MentionsDefinition(e, definition)),
            _ => false,
        };

    /// <summary>
    ///     A requirement's type made of Self as the generic calling convention
    ///     passes it — Self, an optional or a tuple of it — with Self opaque;
    ///     null for any other.
    /// </summary>
    SilType? HeldOfSelf(TypeSymbol type, TypeParameterSymbol self) {
        if (type is MetatypeTypeSymbol or ArrayTypeSymbol || !IsHeld(type, [self])) {
            return null;
        }

        using (Opaque(new GenericShape([self], [])))
        using (AssociatedContext(self.ContainingSymbol)) {
            return Lower(type) is { } lowered && SilType.IsIndirect(lowered) ? lowered : null;
        }
    }

    /// <summary>
    ///     A requirement's type read through the type it is called on: its
    ///     protocol's Self that type, and each associated type the one read
    ///     through it — bare, when that type is a Self whose protocol has it.
    /// </summary>
    internal static TypeSymbol Through(TypeSymbol declared, NamedTypeSymbol protocol, TypeSymbol on) {
        protocol = protocol.OriginalDefinition;
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        if (protocol.SelfTypeParameter is { } self) {
            map[self] = on;
        }

        foreach (var associated in ProtocolClosure([protocol]).SelectMany(static p => p.AssociatedTypes)) {
            map[associated] = on is TypeParameterSymbol parameter && AssociatedSelf(associated, [parameter]) is not null ? associated : new DependentMemberTypeSymbol(on, associated);
        }

        return new TypeSubstitution(map).Apply(declared);
    }

    /// <summary>Whether a requirement's type made of Self is the type given, with Self the type parameter it is called on.</summary>
    static bool SameHeld(TypeSymbol declared, TypeParameterSymbol self, TypeSymbol given, TypeParameterSymbol parameter) =>
        new TypeSubstitution(new Dictionary<TypeParameterSymbol, TypeSymbol> { [self] = parameter }).Apply(declared).Equals(given);

    /// <summary>True when the type is one of <paramref name="parameters" />, or is made of one.</summary>
    internal static bool Mentions(TypeSymbol type, IReadOnlyCollection<TypeParameterSymbol> parameters) =>
        parameters.Count > 0 && (type switch {
            TypeParameterSymbol parameter => parameters.Contains(parameter) || AssociatedSelf(parameter, parameters) is not null,
            NamedTypeSymbol named => named.TypeArguments.Any(a => Mentions(a, parameters)),
            ArrayTypeSymbol array => Mentions(array.ElementType, parameters),
            OptionalTypeSymbol optional => Mentions(optional.UnderlyingType, parameters),
            MetatypeTypeSymbol metatype => Mentions(metatype.InstanceType, parameters),
            ReferenceTypeSymbol reference => Mentions(reference.ReferencedType, parameters),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(e => Mentions(e, parameters)),
            FunctionTypeSymbol function => function.ParameterTypes.Any(p => Mentions(p, parameters)) || Mentions(function.ReturnType, parameters)
                                           || function.ThrownType is { } thrown && Mentions(thrown, parameters),
            StructuralUnionTypeSymbol union => union.Cases.Any(c => Mentions(c, parameters)),
            DependentMemberTypeSymbol dependent => Mentions(dependent.BaseType, parameters),
            ExistentialTypeSymbol existential => Mentions(existential.Constraint, parameters),
            CompositionTypeSymbol composition => CompositionTypeSymbol.PartsOf(composition).Any(p => Mentions(p, parameters)),
            OpaqueTypeSymbol => true,
            _ => false,
        });

    /// <summary>
    ///     What a generic function's signature lets it take compiled once:
    ///     null for one it does not, with why, for <c>SABLE_GENERICS_TRACE</c>
    ///     (null for a function that is not generic or has no body here).
    /// </summary>
    /// <remarks>
    ///     A protocol extension's member is generic over <c>Self</c>, which
    ///     conforms to the protocol: its shape is <c>Self</c> and the
    ///     protocol's table, then its own type parameters.
    /// </remarks>
    GenericShape? SignatureShape(FunctionSymbol symbol, out string? refused) {
        refused = null;
        var all = TypeParametersOf(symbol).ToList();
        if (all.Count == 0 || !_declarations.ContainsKey(symbol)) {
            return null;
        }

        refused = _localGenerics.ContainsKey(symbol) ? "a local generic function"
            : symbol.IsPolymorphic ? "polymorphic recursion"
            : symbol.IsAsync ? "async"
            : symbol.FunctionKind != FunctionKind.Ordinary && !IsGetter(symbol) && !IsSubscriptAccessor(symbol) && !IsValueInitializer(symbol) ? $"{(symbol.FunctionKind.ToString()[0] is 'A' or 'E' or 'I' or 'O' or 'U' ? "an" : "a")} {symbol.FunctionKind.ToString().ToLowerInvariant()}"
            : symbol.ForeignName is not null ? "foreign"
            : !symbol.OpaqueTypeParameters.IsEmpty ? "a 'some P' parameter"
            : _compilation.GetGenerator(symbol) is not null ? "a generator"
            : _compilation.GetDerivedBody(symbol) is not null || _compilation.GetLazyBody(symbol) is not null
              || _compilation.GetInheritedInitializerBody(symbol) is not null ? "a body the compiler writes"
            : null;
        if (refused is not null) {
            return null;
        }

        var requirements = new List<(TypeParameterSymbol, NamedTypeSymbol)>();
        var own = new List<TypeParameterSymbol>();
        var owner = Owner(symbol)?.OriginalDefinition;
        if (owner is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self }) {
            if (!IsTabledProtocol(owner)) {
                refused = "a member of a generic protocol";
                return null;
            }

            own.Add(self);
            requirements.Add((self, owner));
        } else if (owner is { TypeParameters.IsEmpty: false } || owner is { ExtendedArray: not null } or { ExtendedOptional: not null }) {
            // A member of an optional's or an array's extension, or of a generic
            // struct or union laid out at run time: generic over its type's
            // type parameters, then its own.
            if (owner is { ExtendedArray.ArrayKind: not (ArrayKind.Grow or ArrayKind.GrowShrink or ArrayKind.Fixed or ArrayKind.Slice) }
                || owner is { ExtendedArray: null, ExtendedOptional: null } && !IsPlainGeneric(owner)) {
                refused = "a member of a generic type";
                return null;
            }

            // A class's method a subclass may override is called through the
            // vtable, which holds its instances for each class's arguments.
            if (owner.TypeKind == TypeKind.Class && (IsInherited(owner) || owner.IsOpen)) {
                refused = "a member of a generic class a subclass may override";
                return null;
            }

            own.AddRange(owner.TypeParameters);
            if (ExtensionOf(symbol) is { SameTypeRequirements.IsEmpty: false }) {
                // 'extension Array where Element == Int' is another type's.
                refused = "a 'where' clause";
                return null;
            }

            if (SelfType(symbol, TypeSubstitution.Empty) is not { } selfType || !IsHeld(selfType, [..own, ..symbol.TypeParameters])) {
                refused = "a member of a generic type its code cannot hold";
                return null;
            }
        }

        own.AddRange(symbol.TypeParameters);
        if (!all.SequenceEqual(own)) {
            refused = "type parameters from around it";
            return null;
        }

        foreach (var parameter in own.Where(p => owner is not { TypeKind: TypeKind.Protocol } || !ReferenceEquals(p, owner.SelfTypeParameter))) {
            if (parameter is { IsValue: true } or { AllowsNoncopyable: true } || parameter.OpaqueParameter is not null) {
                refused = $"the type parameter '{parameter.Name}'";
                return null;
            }

            foreach (var part in parameter.Constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c))) {
                if (part is not NamedTypeSymbol protocol || !IsTabledProtocol(protocol)) {
                    refused = $"the constraint '{part.ToDisplayString()}'";
                    return null;
                }

                if (!requirements.Any(r => ReferenceEquals(r.Item1, parameter) && ReferenceEquals(r.Item2, protocol.OriginalDefinition))) {
                    requirements.Add((parameter, protocol.OriginalDefinition));
                }
            }
        }

        // 'where' clauses, the function's and its extension's: 'T: P' is a
        // table more; 'S.Element: P' is found in the registry, as the caller
        // makes sure it can be; 'S.Element == T' reads one as the other.
        var extension = ExtensionOf(symbol);
        foreach (var (subject, constraint) in symbol.ConformanceRequirements.Concat(extension?.ConformanceRequirements ?? [])) {
            foreach (var part in CompositionTypeSymbol.PartsOf(constraint)) {
                if (part is not NamedTypeSymbol protocol || !IsTabledProtocol(protocol)
                    || !(subject is TypeParameterSymbol direct && own.Contains(direct) || IsAssociatedOf(subject, own))) {
                    refused = "a 'where' clause";
                    return null;
                }

                if (subject is TypeParameterSymbol parameter && own.Contains(parameter)
                    && !requirements.Any(r => ReferenceEquals(r.Item1, parameter) && ReferenceEquals(r.Item2, protocol.OriginalDefinition))) {
                    requirements.Add((parameter, protocol.OriginalDefinition));
                }
            }
        }

        foreach (var (left, right) in symbol.SameTypeRequirements.Concat(extension?.SameTypeRequirements ?? [])) {
            if (!SameTypeSide(left, own) || !SameTypeSide(right, own)) {
                refused = "a 'where' clause";
                return null;
            }
        }

        bool Passable(TypeSymbol type) =>
            !Mentions(type, own) || IsHeld(type, own);

        foreach (var parameter in symbol.Parameters) {
            if (!Passable(parameter.Type) && !IsGenericFunction(parameter.Type, own)
                || Mentions(parameter.Type, own) && (parameter.IsVariadic || parameter.IsByReference || parameter.Modifier is not (ParameterModifier.None or ParameterModifier.In))) {
                refused = $"the parameter '{parameter.Name}: {parameter.Type.ToDisplayString()}'";
                return null;
            }
        }

        // An error of a type parameter's type, or an associated type's, 'throws(S.Failure)', is written where the caller says.
        if (!Passable(symbol.ReturnType) || symbol.ThrownType is { } thrown && _compilation.CanonicalType(symbol, thrown) is var canonicalThrown
            && Mentions(canonicalThrown, own) && (!IsHeld(canonicalThrown, own) || canonicalThrown is MetatypeTypeSymbol or ArrayTypeSymbol)) {
            refused = $"the result '{symbol.ReturnType.ToDisplayString()}'";
            return null;
        }

        return new([..own], [..requirements]);
    }

    /// <summary>A function as SABLE_GENERICS_TRACE names it: an accessor by its type and property, <c>Sable.Core.FloatingPoint.isNaN.get</c>.</summary>
    string TraceName(FunctionSymbol symbol) =>
        symbol is { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FieldSymbol property } && Owner(symbol) is { } owner
            ? $"{QualifiedName(owner.OriginalDefinition)}.{property.Name}.{(IsGetter(symbol) ? "get" : "set")}"
            : symbol is { FunctionKind: FunctionKind.Accessor or FunctionKind.Subscript } && Owner(symbol) is { } subscripted
                ? $"{QualifiedName(subscripted.OriginalDefinition)}.subscript.{(symbol.FunctionKind == FunctionKind.Subscript ? "get" : symbol.Name)}"
                : QualifiedName(symbol);

    /// <summary>
    ///     A generic struct's own initialiser — not a class's, whose object
    ///     unspecialised code cannot make yet: it builds self in a slot of
    ///     its own and returns it, as a static member that returns Self does.
    /// </summary>
    static bool IsValueInitializer(FunctionSymbol symbol) =>
        symbol is { FunctionKind: FunctionKind.Initializer, IsSynthesized: false } && Owner(symbol)?.OriginalDefinition is { TypeKind: TypeKind.Struct, ExtendedArray: null, ExtendedOptional: null } owner
        // Built in a slot of its layout, which does not depend on its arguments: what it holds of them is behind references.
        && StoredFields(owner).All(f => !Mentions(f.Type, owner.TypeParameters) || ByReference(f.Type, owner.TypeParameters));

    /// <summary>The extension a member is in: itself, or the property or subscript it is an accessor of.</summary>
    static ExtensionSymbol? ExtensionOf(FunctionSymbol symbol) =>
        symbol.ContainingSymbol switch {
            ExtensionSymbol extension => extension,
            FieldSymbol { ContainingSymbol: ExtensionSymbol extension } => extension,
            FunctionSymbol { FunctionKind: FunctionKind.Subscript, ContainingSymbol: ExtensionSymbol extension } => extension,
            _ => null,
        };

    /// <summary>
    ///     A subscript's getter — the subscript itself, when it has only a
    ///     body — or its setter: compiled once as a method is, its indices
    ///     its parameters (and the value stored, the setter's last).
    /// </summary>
    bool IsSubscriptAccessor(FunctionSymbol symbol) =>
        symbol is { FunctionKind: FunctionKind.Subscript } && ReferenceEquals(SubscriptGetter(symbol), symbol)
        || symbol is { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FunctionSymbol { FunctionKind: FunctionKind.Subscript } subscript }
        && (ReferenceEquals(SubscriptGetter(subscript), symbol) || ReferenceEquals(SubscriptSetter(subscript), symbol));

    /// <summary>A computed property's getter: compiled once as a method of no parameters is.</summary>
    bool IsGetter(FunctionSymbol symbol) =>
        symbol is { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FieldSymbol property } && ReferenceEquals(GetterOf(property), symbol);

    /// <summary>A side of a same-type requirement unspecialised code can read: a type parameter, an associated type read through one, or a type that mentions none.</summary>
    static bool SameTypeSide(TypeSymbol type, IReadOnlyCollection<TypeParameterSymbol> own) =>
        !Mentions(type, own) || type is TypeParameterSymbol parameter && own.Contains(parameter) || IsAssociatedOf(type, own);

    /// <summary>A body that is one call of a protocol's requirement, given the function's parameters as they are.</summary>
    static bool IsForwarder(BoundBlock body) {
        var statement = body.Statements is [var only] ? only : null;
        var value = statement switch {
            BoundReturn { Value: { } returned } => returned,
            BoundExpressionStatement { Expression: var expression } => expression,
            _ => null,
        };
        return value is BoundCall { Function.ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } } call
               && call.Receiver is null or BoundTypeExpression
               && call.Arguments.Length > 0 && call.Arguments.All(static a => a is BoundParameterAccess);
    }

    bool BodyAllows(FunctionSymbol symbol, GenericShape shape) {
        var (syntax, model) = _declarations[symbol];
        if (BodyModel(model, syntax).GetBoundNode(syntax) is not BoundBlock body) {
            return false;
        }

        // A function that only hands its parameters to a requirement —
        // 'sin(x) => T.sin(x)' — stays specialised: each copy is the
        // requirement's witness called directly, which becomes an intrinsic
        // or a library call where the type is known, and costs nothing.
        if (IsForwarder(body)) {
            if (Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 }) {
                Console.Error.WriteLine($"specialised {TraceName(symbol)}: a forwarder to a requirement");
            }

            return false;
        }

        var checker = new ShapeChecker(this, shape, symbol);
        var initialValues = IsValueInitializer(symbol) && Owner(symbol) is { } built
            ? built.OriginalDefinition.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false, HasInitializer: true })
                .Select(FieldInitializer).OfType<BoundExpression>().ToList()
            : [];
        if (initialValues.TrueForAll(v => checker.Allows(v, false)) && checker.Allows(body, false)) {
            _describes[symbol] = checker.Describes;
            if (Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 }) {
                Console.Error.WriteLine($"unspecialised {TraceName(symbol)}");
            }

            return true;
        }

        // Why each function that could be is not, for whoever is widening what can be.
        if (Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 } && checker.Refused is { } refused) {
            Console.Error.WriteLine($"specialised {TraceName(symbol)}: {refused.GetType().Name} '{refused.Syntax.ToString().Trim().ReplaceLineEndings(" ")}'{(Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") == "2" && refused is BoundExpression e ? $" : {e.Type.ToDisplayString()} ({e.Type.GetType().Name}){(e is BoundConversion c ? $" {c.ConversionKind} of {c.Operand.GetType().Name} {c.Operand.Type.ToDisplayString()}" : "")}" : "")}");
        }

        return false;
    }

    /// <summary>
    ///     Whether a body uses its type parameters only as unspecialised code can serve:
    ///     each expression of a type parameter's type (or its metatype's) is
    ///     a parameter, a local, a call, a metatype or a choice between two;
    ///     and what takes one is a declaration, an assignment to a local, a
    ///     return, a statement, an interpolation, a metatype comparison, a
    ///     requirement of the parameter's protocols, or a generic function
    ///     that is unspecialised too, given the parameter itself.
    /// </summary>
    sealed class ShapeChecker(SilGen generator, GenericShape shape, FunctionSymbol checkedSymbol) {
        readonly ImmutableArray<TypeParameterSymbol> _own = shape.Parameters;

        bool Mentions(TypeSymbol type) => SilGen.Mentions(type, _own);

        bool IsOwn(TypeSymbol type) => type is TypeParameterSymbol parameter && _own.Contains(parameter) || IsAssociatedOf(type, _own);

        /// <summary>The protocols, with those they refine, a type parameter or an associated type is known here to conform to.</summary>
        List<NamedTypeSymbol> Known(TypeSymbol type) => ProtocolClosure(generator.ProtocolsKnown(checkedSymbol, shape, type));

        TypeSymbol Canonical(TypeSymbol type) => generator._compilation.CanonicalType(checkedSymbol, type);

        /// <summary>Whether a requirement's type, read through the type it is called on, is the type given, as the requirements in force here read both.</summary>
        bool SameThrough(TypeSymbol declared, NamedTypeSymbol protocol, TypeSymbol on, TypeSymbol given) {
            var through = Through(declared, protocol, on);
            return through.Equals(given) || Canonical(through).Equals(Canonical(given));
        }

        /// <summary>
        ///     Whether a call of a requirement whose type mentions Self or its
        ///     associated types gives or takes a value this function holds:
        ///     each part one of its type parameters, an associated type, or a
        ///     type that mentions neither.
        /// </summary>
        bool HeldThrough(TypeSymbol declared, NamedTypeSymbol protocol, TypeSymbol on) => Simple(Canonical(Through(declared, protocol, on)));

        /// <summary>A type a value may have here: a type parameter, its metatype, or one that mentions none.</summary>
        bool Simple(TypeSymbol type) => !Mentions(type) || IsHeld(type, _own) || IsGenericFunction(type, _own);

        bool IsOwnArray(TypeSymbol type) => IsArrayOf(type, _own);

        /// <summary>The innermost node that kept the body from being unspecialised, for SABLE_GENERICS_TRACE.</summary>
        public BoundNode? Refused { get; private set; }

        /// <summary>Whether the body, or unspecialised code it calls, writes a value of a type parameter's type out: its callers make describers.</summary>
        public bool Describes { get; private set; }

        public bool Allows(BoundNode node, bool inClosure) {
            var allowed = AllowsHere(node, inClosure);
            if (!allowed) {
                Refused ??= node;
            }

            return allowed;
        }

        /// <summary>How many closures compiled with the function the node being checked is inside.</summary>
        int _closureDepth;

        /// <summary>Whether the code being checked may change the function's own self: not inside a closure, which has a copy.</summary>
        bool OwnMutating => (checkedSymbol.IsMutating || IsValueInitializer(checkedSymbol)) && _closureDepth == 0;

        /// <summary>Whether the code being checked may change its self's stored fields: a mutating method's, or any method's of a class.</summary>
        bool FieldsMutable => _closureDepth == 0 && (checkedSymbol.IsMutating || IsValueInitializer(checkedSymbol) || OwnerOf(checkedSymbol) is { TypeKind: TypeKind.Class });

        /// <summary>
        ///     Whether a closure can be compiled with this function, in the
        ///     generic convention, over its metadata and tables (#297): one
        ///     that captures what it captures of a type parameter's type by
        ///     value — a 'let', a parameter, not a 'var' it shares, nor self —
        ///     and is not inside another.
        /// </summary>
        bool ClosureAllowed(BoundClosure closure) {
            if (_closureDepth > 0 || closure.IsAutoclosure || generator._compilation.GetGenerator(closure.Symbol) is not null
                || Mentions(closure.Type) && !IsGenericFunction(closure.Type, _own)) {
                return false;
            }

            // What it captures by value is what lowering finds held by value, its type parameters opaque.
            List<Capture> captures;
            using (generator.Opaque(shape))
            using (generator.AssociatedContext(checkedSymbol)) {
                captures = generator.CapturesOf(closure);
            }

            return captures.All(c => !Mentions(c.Type) || c is { Boxed: false, Kind: CaptureKind.Default or CaptureKind.ByValue, Key: LocalSymbol or ParameterSymbol }
                && (IsHeld(c.Type, _own) && c.Type is not MetatypeTypeSymbol || IsGenericFunction(c.Type, _own)));
        }

        bool AllowsHere(BoundNode node, bool inClosure) {
            if (node is BoundClosure compiled && !inClosure && ClosureAllowed(compiled)) {
                _closureDepth++;
                try {
                    return compiled.Children.All(child => Allows(child, false));
                } finally {
                    _closureDepth--;
                }
            }

            if (node is BoundClosure or BoundLocalFunction or BoundUnboundClosure) {
                inClosure = true;
            }

            if (node is BoundExpression expression && Mentions(expression.Type) && (inClosure || !Simple(expression.Type) || !Produces(expression))) {
                return false;
            }

            switch (node) {
                case BoundLocalDeclaration declaration when Mentions(declaration.Local.Type) && (inClosure || !Simple(declaration.Local.Type)):
                case BoundPatternDeclaration pattern when Descendants(pattern).Any(n => n is BoundBindingPattern binding && Mentions(binding.Local.Type) && (inClosure || !Simple(binding.Local.Type))):
                case BoundBindingPattern binding when Mentions(binding.Local.Type) && (inClosure || !Simple(binding.Local.Type)):
                case BoundConditionBinding { Local: { } bound } when Mentions(bound.Type) && (inClosure || !Simple(bound.Type)):
                case BoundTypeTest test when Mentions(test.TestedType):
                case BoundForEach { Element: { } element } loop when Mentions(element.Type)
                                                                    && (inClosure || !IsOwnArray(loop.Sequence.Type) || !Simple(element.Type) || loop.Sequence is not (BoundLocalAccess or BoundParameterAccess or BoundSelfAccess or BoundFieldAccess { Receiver: BoundSelfAccess or BoundLocalAccess or BoundParameterAccess })):
                    return false;
                case BoundCall call when CallMentions(call) && (inClosure || !CallAllowed(call)):
                    return false;
                case BoundBinaryOperator binary when (Mentions(binary.Left.Type) || Mentions(binary.Right.Type)) && (inClosure || !BinaryAllowed(binary)):
                    return false;
                case BoundUnaryOperator unary when Mentions(unary.Operand.Type)
                                                   && (inClosure || unary.Method is not { } method || OwnerOf(method) is not { TypeKind: TypeKind.Protocol }
                                                       || !RequirementAllowed(new BoundCall(unary.Syntax, null, method, [unary.Operand], unary.Type))):
                    return false;
                case BoundFieldAccess access when access.Receiver is { } receiver && Mentions(receiver.Type) && (inClosure || !PropertyAllowed(access)):
                    return false;
                case BoundIndexAccess { Subscript: not null } indexed when IndexMentions(indexed) && (inClosure || !SubscriptAllowed(indexed, null)):
                    return false;
                case BoundDictionary dictionary when Mentions(dictionary.Type) && (inClosure || !DictionaryAllowed(dictionary)):
                    return false;
            }

            foreach (var child in node.Children) {
                if (child is BoundExpression value && Mentions(value.Type) && !Consumes(node, value)) {
                    return false;
                }

                // What a 'do' with catches or a 'try?' runs, it handles the errors of.
                var handles = node is BoundDo { Catches.IsEmpty: false } loop && ReferenceEquals(child, loop.Body)
                              || node is BoundTry { TryKind: not TryKind.Propagate } handled && ReferenceEquals(child, handled.Expression);
                _handlers += handles ? 1 : 0;
                var allowed = Allows(child, inClosure);
                _handlers -= handles ? 1 : 0;
                if (!allowed) {
                    return false;
                }
            }

            return true;
        }

        /// <summary>How many handlers of errors the node being checked is inside: a 'do' with catches, a 'try?'.</summary>
        int _handlers;

        /// <summary>
        ///     Whether a call may throw what it throws here: nothing, an error
        ///     of a type known here, or one of a type only its metadata knows
        ///     that goes straight out of this function, which throws the same
        ///     — written where this function's caller says, as the call is told to.
        /// </summary>
        bool ThrowsAllowed(TypeSymbol? thrown) {
            if (thrown is null || !Mentions(Canonical(thrown))) {
                return true;
            }

            if (_handlers > 0 || _closureDepth > 0 || checkedSymbol.ThrownType is not { } own) {
                return false;
            }

            // Thrown on as it is, written where this function's caller says;
            // or, of a type parameter's or an associated type's, boxed as the
            // 'any Error' this function throws, by its type's eraser.
            return Canonical(own).Equals(Canonical(thrown))
                   || IsOwn(Canonical(thrown)) && own is ExistentialTypeSymbol { Constraint: var constraint } && constraint.Equals(generator.Core.Error);
        }

        static IEnumerable<BoundNode> Descendants(BoundNode node) {
            foreach (var child in node.Children) {
                yield return child;
                foreach (var nested in Descendants(child)) {
                    yield return nested;
                }
            }
        }

        bool Produces(BoundExpression expression) =>
            expression switch {
                BoundParameterAccess { IsNarrowed: false } or BoundLocalAccess { IsNarrowed: false } or BoundSelfAccess => true,
                // A 'for' loop's iterator, named with the type it has.
                BoundLocalAccess local => local.Type.Equals(local.Local.Type),
                BoundCall or BoundMetatype or BoundTypeExpression or BoundConditional or BoundAssignment or BoundTry => true,
                // A call of a function of them, in the generic convention.
                BoundIndirectCall { Callee.Type: var calleeType } => IsGenericFunction(calleeType, _own),
                BoundBinaryOperator { Method: not null } or BoundUnaryOperator { Method: not null } => true,
                BoundFieldAccess access => PropertyAllowed(access),
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion => conversion.Type.Equals(conversion.Operand.Type),
                // A closure that throws nothing where one that may throw is taken.
                BoundConversion { ConversionKind: ConversionKind.Upcast, Operand: BoundClosure } upcast => IsGenericFunction(upcast.Type, _own),
                BoundArray array => IsOwnArray(array.Type),
                BoundIndexAccess { Subscript: null, Indices: [_] } index => IsOwnArray(index.Receiver.Type),
                BoundIndexAccess { Subscript: not null } subscripted => SubscriptAllowed(subscripted, null),
                // An optional or a tuple of them, made, unwrapped, taken apart and chosen between.
                BoundConversion { ConversionKind: ConversionKind.OptionalWrap } wrap => Simple(wrap.Operand.Type),
                BoundConversion { ConversionKind: ConversionKind.Explicit, Operand.Type: OptionalTypeSymbol { UnderlyingType: var unwrapped } } force => unwrapped.Equals(force.Type),
                BoundLiteral { Value: null, Type: OptionalTypeSymbol } => true,
                BoundBinaryOperator { OperatorKind: BinaryOperatorKind.Coalesce } => true,
                BoundTuple or BoundTupleElementAccess or BoundMatch or BoundIfExpression => true,
                // A generic struct or union of them, made where the metadata says.
                BoundRecordExpression or BoundUnionCaseConstruction => true,
                BoundDictionary => true,
                _ => false,
            };

        bool Consumes(BoundNode parent, BoundExpression child) =>
            parent switch {
                BoundLocalDeclaration or BoundPatternDeclaration or BoundExpressionStatement or BoundReturn => true,
                BoundAssignment assignment => ReferenceEquals(child, assignment.Value)
                                              || assignment.Target is BoundLocalAccess or BoundParameterAccess && ReferenceEquals(child, assignment.Target)
                                              || assignment.Target is BoundFieldAccess { Receiver: BoundSelfAccess or BoundLocalAccess { Local.IsMutable: true } } stored
                                              && ReferenceEquals(child, assignment.Target) && PropertyAllowed(stored)
                                              || assignment.Target is BoundSelfAccess && OwnMutating && ReferenceEquals(child, assignment.Target)
                                              || assignment.Target is BoundIndexAccess { Subscript: not null } stores && ReferenceEquals(child, assignment.Target) && SubscriptAllowed(stores, assignment.Value)
                                              || assignment.Target is BoundIndexAccess { Subscript: null, Indices: [_], Receiver: var array } && IsOwnArray(array.Type) && ReferenceEquals(child, assignment.Target)
                                              && (array is BoundLocalAccess { Local.IsMutable: true } || array is BoundFieldAccess { Receiver: BoundSelfAccess } && FieldsMutable
                                                  || array is BoundSelfAccess && OwnMutating),
                BoundCall or BoundBinaryOperator or BoundUnaryOperator or BoundTry => true,
                BoundIndirectCall indirect => IsGenericFunction(indirect.Callee.Type, _own) && (ReferenceEquals(child, indirect.Callee) ? indirect.Callee is BoundParameterAccess or BoundLocalAccess : true),
                BoundFieldAccess access => ReferenceEquals(child, access.Receiver) && PropertyAllowed(access),
                BoundConditional conditional => !ReferenceEquals(child, conditional.Condition),
                BoundConversion { ConversionKind: ConversionKind.Identity } => true,
                BoundConversion { ConversionKind: ConversionKind.Upcast, Operand: BoundClosure } upcast => IsGenericFunction(upcast.Type, _own),
                BoundArray or BoundTuple or BoundMatchArm or BoundIf or BoundRecordExpression or BoundUnionCaseConstruction or BoundDictionary => true,
                BoundTupleElementAccess element => ReferenceEquals(child, element.Receiver),
                BoundMatch match => ReferenceEquals(child, match.Subject),
                BoundConditionBinding binding => ReferenceEquals(child, binding.Source),
                BoundConversion { ConversionKind: ConversionKind.OptionalWrap } => true,
                BoundConversion { ConversionKind: ConversionKind.Explicit, Operand.Type: OptionalTypeSymbol } => true,
                BoundIndexAccess { Subscript: not null } => true,
                BoundIndexAccess index => ReferenceEquals(child, index.Receiver),
                BoundForEach loop => ReferenceEquals(child, loop.Sequence),
                // Not inside a protocol's own 'debugDescription' (Error's
                // '"\(self)"'): a describer would call it back.
                BoundInterpolatedString interpolated => interpolated.Parts.IndexOf(child) is var at and >= 0 && interpolated.Formats[at] is null && !DescribesSelf && (Describes = true),
                _ => false,
            };

        bool DescribesSelf => checkedSymbol is { Name: "debugDescription" } && OwnerOf(checkedSymbol) is { TypeKind: TypeKind.Protocol };

        /// <summary>A dictionary literal: a call of its type's 'init(dictionaryLiteral:)' with an array of its pairs, as lowering makes it.</summary>
        bool DictionaryAllowed(BoundDictionary dictionary) {
            if (dictionary.Type is not NamedTypeSymbol { TypeArguments: [var key, var value] } type
                || type.OriginalDefinition.GetMembers("init").OfType<FunctionSymbol>().FirstOrDefault(static f => f.Parameters is [{ Label: "dictionaryLiteral" }]) is not { } initializer) {
                return false;
            }

            var pair = new TupleTypeSymbol([key, value], [null, null]);
            var entries = new BoundArray(dictionary.Syntax, new ArrayTypeSymbol(pair, ArrayKind.Grow),
                [..dictionary.Entries.Select(e => (BoundExpression)new BoundTuple(e.Key.Syntax, pair, [e.Key, e.Value]))]);
            return Simple(pair) && CallAllowed(new BoundCall(dictionary.Syntax, null, initializer, [entries], type) { Substitution = TypeSubstitution.For(type) });
        }

        bool IndexMentions(BoundIndexAccess index) =>
            Mentions(index.Type) || Mentions(index.Receiver.Type) || index.Indices.Any(i => Mentions(i.Type))
            || index.Substitution.Parameters.Any(p => index.Substitution.Lookup(p) is { } bound && Mentions(bound));

        /// <summary>A subscript of a type this code holds: a call of its getter, or, given the value stored, of its setter.</summary>
        bool SubscriptAllowed(BoundIndexAccess index, BoundExpression? stored) {
            if (index.Subscript is not { } subscript || subscript.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol }
                || (stored is null ? generator.SubscriptGetter(subscript) : generator.SubscriptSetter(subscript)) is not { } accessor) {
                return false;
            }

            BoundExpression[] arguments = stored is null ? [..index.Indices] : [..index.Indices, stored];
            return CallAllowed(new BoundCall(index.Syntax, index.Receiver, accessor, [..arguments], stored is null ? index.Type : generator.Core.Void) { Substitution = index.Substitution });
        }

        bool CallMentions(BoundCall call) =>
            Mentions(call.Type) || call.Receiver is { } receiver && Mentions(receiver.Type) || call.Arguments.Any(a => Mentions(a.Type))
            || call.Substitution.Parameters.Any(p => call.Substitution.Lookup(p) is { } bound && Mentions(bound));

        bool CallAllowed(BoundCall call) {
            var callee = call.Function;

            // 'type(of: x)': the metadata, or an object's own class.
            if (callee is { Name: "type", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }) {
                return call.Arguments is [var argument] && IsOwn(argument.Type);
            }

            if (callee.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol }) {
                return RequirementAllowed(call);
            }

            // An array's own builtins, on an array of a type parameter's values held in a local.
            // One that only reads it: a slice of it, or a copy of a slice's run.
            if (callee.Builtin is "array_slice" or "array_of_slice" && call.Receiver is { } viewed && IsOwnArray(viewed.Type)) {
                return call.Arguments.All(a => !Mentions(a.Type));
            }

            if (callee.Builtin is "array_append" or "array_remove_last" or "array_rotate" or "array_reserve" or "array_clear" && call.Receiver is { } array && IsOwnArray(array.Type)) {
                return (call.Receiver is BoundLocalAccess { Local.IsMutable: true }
                        || call.Receiver is BoundSelfAccess && OwnMutating
                        || call.Receiver is BoundFieldAccess { Receiver: BoundSelfAccess } stored && FieldsMutable && PropertyAllowed(stored)
                        || call.Receiver is BoundFieldAccess { Receiver: BoundLocalAccess { Local.IsMutable: true }, Field: { IsComputed: false, IsStatic: false } } local && PropertyAllowed(local))
                       && call.Arguments.All(a => Simple(a.Type));
            }

            if (callee is { FunctionKind: FunctionKind.Initializer, IsSynthesized: true, Parameters.IsEmpty: false }
                && call.Type is NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Struct } made && IsHeld(made, _own) && ByReference(made, _own)) {
                return call.Arguments.All(a => Simple(a.Type)) && call.Arguments.Length == StoredFields(made.OriginalDefinition).Count();
            }

            if (!IsGeneric(callee) || generator.UnspecialisedShape(callee) is not { } calleeShape) {
                return false;
            }

            Describes |= generator.DescribesValues(callee);

            // A protocol extension's member: on a value of a type parameter of
            // this function, or on self, its Self that type parameter.
            var calleeSelf = OwnerOf(callee)?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } protocolSelf } ? protocolSelf : null;
            var receiverType = call.Receiver switch {
                null => calleeSelf is not null && IsOwn(calleeSelf) ? calleeSelf : null,
                BoundTypeExpression type => type.ReferencedType,
                { Type: MetatypeTypeSymbol metatype } => metatype.InstanceType,
                { } receiver => receiver.Type,
            };
            // A member of a generic type, an optional or an array: on a value this
            // function holds, or on its own self, its type arguments from the receiver's.
            var calleeOwner = calleeSelf is null ? OwnerOf(callee)?.OriginalDefinition : null;
            var ownerBound = calleeOwner is null ? null : OwnerBindings(calleeOwner, call.Receiver is null ? (Owner(checkedSymbol) is null ? null : SelfType(checkedSymbol, TypeSubstitution.Empty)) : receiverType);
            if (call.Receiver is { } onValue && Mentions(onValue.Type) && (calleeSelf is null || receiverType is null || !IsOwn(receiverType))
                && (ownerBound is null || !Simple(onValue.Type))) {
                return false;
            }

            if (calleeOwner is not null && callee.IsMutating && call.Receiver is not (BoundLocalAccess { Local.IsMutable: true } or null or BoundSelfAccess)
                && !(call.Receiver is BoundFieldAccess { Receiver: BoundSelfAccess, Field: { IsComputed: false, IsStatic: false } } changed && FieldsMutable && PropertyAllowed(changed))) {
                return false;
            }

            // One that changes self changes a local, or this function's own self.
            if (calleeSelf is not null && callee.IsMutating && receiverType is not null && IsOwn(receiverType)
                && call.Receiver is not (BoundLocalAccess { Local.IsMutable: true } or null or BoundSelfAccess) || callee.IsMutating && call.Receiver is null or BoundSelfAccess && !OwnMutating) {
                return false;
            }

            var boundMap = new Dictionary<TypeParameterSymbol, TypeSymbol>();
            foreach (var parameter in calleeShape.Parameters) {
                if ((ReferenceEquals(parameter, calleeSelf) ? call.Substitution.Lookup(parameter) ?? receiverType
                        : call.Substitution.Lookup(parameter) ?? ownerBound?.GetValueOrDefault(parameter)) is not { } bound) {
                    return false;
                }

                boundMap[parameter] = bound;

                if (!Mentions(bound)) {
                    continue;
                }

                // An array, an optional or a tuple of this function's type
                // parameter's values, its metadata made at run time —
                // polymorphic recursion — for a callee that asks nothing of it.
                if (bound is not TypeParameterSymbol && IsHeld(bound, _own) && bound is not MetatypeTypeSymbol) {
                    if (calleeShape.ProtocolsOf(parameter).Any()) {
                        return false;
                    }

                    continue;
                }

                // What the callee requires of it, this function has: its own tables, or what they refine.
                if (!IsOwn(bound)) {
                    return false;
                }

                var has = Known(bound);
                if (!calleeShape.ProtocolsOf(parameter).All(needed => has.Any(p => ReferenceEquals(p, needed)))) {
                    return false;
                }
            }

            var calleeSubstitution = WithAssociatedTypes(callee, new TypeSubstitution(boundMap));

            // A 'rethrows(E)' callee whose E is a type only metadata knows here,
            // and whose functions may throw something else too, throws them
            // together as 'any Error' (#297): only a function that throws one
            // may throw it on, as it is; it cannot be seen as E again yet.
            if (callee is { IsRethrows: true, RethrownAlso: { } also } && Canonical(calleeSubstitution.Apply(also)) is var rethrown && Mentions(rethrown) && !rethrown.Equals(generator.Core.Never)
                && (callee.ThrownType is not { } declared || !Canonical(calleeSubstitution.Apply(declared)).Equals(rethrown))
                && (_handlers > 0 || _closureDepth > 0 || checkedSymbol.ThrownType is not ExistentialTypeSymbol { Constraint: var erased } || !erased.Equals(generator.Core.Error))) {
                return false;
            }

            return callee.ThrownType is not { } calleeThrown || ThrowsAllowed(calleeSubstitution.Apply(calleeThrown));
        }

        /// <summary>A requirement of the protocols a type parameter has, called on one, as its generic witness takes it.</summary>
        bool RequirementAllowed(BoundCall call) {
            var callee = call.Function;
            if (callee.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } declared || generator.GenericEntryType(declared, callee) is null
                || !generator.GenericRequirements(declared).Exists(r => ReferenceEquals(r.Member, callee))) {
                return false;
            }

            var protocol = declared.OriginalDefinition;
            var self = protocol.SelfTypeParameter;
            TypeSymbol? selfType = call.Receiver switch {
                BoundTypeExpression type => type.ReferencedType,
                { Type: MetatypeTypeSymbol metatype } => metatype.InstanceType,
                { } receiver => receiver.Type,
                null => (self is null ? null : call.Substitution.Lookup(self)) ?? callee.Parameters.Zip(call.Arguments).FirstOrDefault(p => p.First.Type.Equals(self)).Second?.Type,
            };

            if (selfType is null || !IsOwn(selfType) || !Known(selfType).Any(p => ReferenceEquals(p, protocol))) {
                return false;
            }

            if (callee.ThrownType is { } thrown && !ThrowsAllowed(Through(thrown, protocol, selfType))) {
                return false;
            }

            // A method that changes its value changes it in a local, or a
            // protocol extension's own self, when it may change it.
            // Or a stored field of the self of a method that may change it: 'base.next()'.
            if (callee.IsMutating && call.Receiver is not BoundLocalAccess { Local.IsMutable: true }
                && !(call.Receiver is null or BoundSelfAccess && OwnMutating)
                && !(call.Receiver is BoundFieldAccess { Receiver: BoundSelfAccess, Field: { IsComputed: false, IsStatic: false } } stored && FieldsMutable && PropertyAllowed(stored))) {
                return false;
            }

            foreach (var (declaredParameter, argument) in callee.Parameters.Zip(call.Arguments)) {
                if (SilGen.Mentions(declaredParameter.Type, [self!]) ? !SameThrough(declaredParameter.Type, protocol, selfType, argument.Type) : Mentions(argument.Type)) {
                    return false;
                }
            }

            return SilGen.Mentions(callee.ReturnType, [self!]) ? SameThrough(callee.ReturnType, protocol, selfType, call.Type) : !Mentions(call.Type);
        }

        /// <summary>A property requirement of a type parameter's protocols, read on one: through its getter's generic entry.</summary>
        bool PropertyAllowed(BoundFieldAccess access) {
            var field = access.Field;

            // An array's own: 'items.count'.
            if (field.Builtin is "array_count" or "array_capacity" && access.Receiver is { } array && IsOwnArray(array.Type)) {
                return true;
            }

            // A stored field of a generic struct this function holds, where its metadata says.
            if (access.Receiver is { } holder && field is { IsStatic: false, IsComputed: false, Builtin: null }
                && holder.Type is NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Struct or TypeKind.Class } && Simple(holder.Type) && Simple(access.Type)) {
                return true;
            }

            // A computed property a protocol extension or a generic type
            // declares: a call of its getter, compiled once as any member is.
            if (field is { IsComputed: true, Builtin: null } && field.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } && generator.GetterOf(field) is { } getter) {
                return CallAllowed(new BoundCall(access.Syntax, access.Receiver, getter, [], access.Type));
            }

            if (field.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } declared || generator.GenericEntryType(declared, field) is null
                || !generator.GenericRequirements(declared).Exists(r => ReferenceEquals(r.Member, field))) {
                return false;
            }

            var protocol = declared.OriginalDefinition;
            var on = access.Receiver switch {
                BoundTypeExpression type => type.ReferencedType,
                { Type: MetatypeTypeSymbol metatype } => metatype.InstanceType,
                { } receiver => receiver.Type,
                null => null,
            };

            return on is not null && IsOwn(on) && field.IsStatic == access.Receiver is BoundTypeExpression or { Type: MetatypeTypeSymbol }
                   && Known(on).Any(p => ReferenceEquals(p, protocol))
                   && (SilGen.Mentions(field.Type, [protocol.SelfTypeParameter!]) ? SameThrough(field.Type, protocol, on, access.Type) : !Mentions(access.Type));
        }

        bool BinaryAllowed(BoundBinaryOperator binary) {
            // 'a ?? b', and 'x == none' — a test of the case.
            if (binary.OperatorKind == BinaryOperatorKind.Coalesce) {
                return Simple(binary.Left.Type) && Simple(binary.Right.Type);
            }

            if (binary is { OperatorKind: BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual, Method: null }
                && (binary.Left is BoundLiteral { Value: null } || binary.Right is BoundLiteral { Value: null })) {
                return Simple(binary.Left.Type) && Simple(binary.Right.Type);
            }

            if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
                && binary.Left.Type is MetatypeTypeSymbol && binary.Right.Type is MetatypeTypeSymbol) {
                return true;
            }

            return binary.Method is { } method && OwnerOf(method) is { TypeKind: TypeKind.Protocol }
                                               && RequirementAllowed(new BoundCall(binary.Syntax, null, method, [binary.Left, binary.Right], binary.Type));
        }
    }

    /// <summary>
    ///     The instance of a generic function that serves every type: its
    ///     own parameters, a type parameter's opaque, then where a result of one goes, each type
    ///     parameter's metadata and each of their witness tables.
    /// </summary>
    internal (string Name, FunctionType Type)? UnspecialisedInstance(FunctionSymbol symbol, GenericShape shape, SyntaxNode at) {
        var name = $"{_names[symbol]}$generic";
        if (_instances.TryGetValue(name, out var existing)) {
            return (name, existing);
        }

        FunctionType? signature;
        using (Opaque(shape))
        using (AssociatedContext(symbol)) {
            signature = SignatureOf(symbol, at, TypeSubstitution.Empty);
        }

        if (signature is null) {
            return null;
        }

        // A result of a type parameter's type is written where the caller says, before the hidden parameters.
        var hidden = Enumerable.Repeat(SilType.Metadata, shape.Parameters.Length).Concat(Enumerable.Repeat(SilType.WitnessTable, shape.Requirements.Length)).ToList();
        var result = signature.Result;
        if (SilType.IsIndirect(result)) {
            hidden.Insert(0, SilType.Address(result));
            result = SilType.Unit;
        }

        // So is an error of one, which is thrown as that address.
        var thrown = signature.Thrown;
        if (thrown is not null && SilType.IsIndirect(thrown)) {
            thrown = SilType.Address(thrown);
            hidden.Insert(SilType.IsIndirect(signature.Result) ? 1 : 0, thrown);
        }

        // An initialiser borrows what it is given, as any function compiled
        // once does: its callers pass values of a type parameter where they are.
        var owned = signature.Owned.IsEmpty || IsValueInitializer(symbol) ? default : signature.Owned.Concat(hidden.Select(static _ => false)).ToImmutableArray();
        var type = new FunctionType([..signature.Parameters, ..hidden], result, thrown, owned, signature.IsAsync);
        _instances[name] = type;
        _instanceOrigins[name] = (symbol, TypeSubstitution.Empty, _loweringInstance);
        _unspecialised[name] = (symbol, shape);
        _pending.Enqueue((symbol, name, type, TypeSubstitution.Empty));
        return (name, type);
    }

    // -----------------------------------------------------------------------
    // Existential witnesses that call unspecialised code
    // -----------------------------------------------------------------------

    /// <summary>
    ///     Witness thunks whose bodies are lowered once what is pending is:
    ///     a call into unspecialised code may ask for the very table the
    ///     thunk is being made for.
    /// </summary>
    readonly Queue<Action> _pendingThunks = new();

    /// <summary>
    ///     True when an existential's slot, in an unspecialised build, calls a
    ///     generic witness compiled once rather than an instance of it: a
    ///     method of the boxed value whose instance would take what the slot
    ///     is given as it is, and give what the slot gives. The instance is
    ///     then not made for the slot alone (#297).
    /// </summary>
    bool CallsGenericWitness(FunctionSymbol generic, TypeSubstitution bound, ThickFunctionType signature, bool givenBox, SyntaxNode at) {
        if (!HasSelf(generic) || generic.IsAsync || IsDynamic(generic) || !_declarations.ContainsKey(generic) || UnspecialisedShape(generic) is null) {
            return false;
        }

        var parameters = signature.Signature.Parameters;
        if (SignatureOf(generic, at, WithAssociatedTypes(generic, bound)) is not { } type || type.Parameters.Length != parameters.Length
            || type.Parameters[0] is AddressType && !givenBox
            || type.Result != signature.Signature.Result || type.Thrown != signature.Implementation.Thrown) {
            return false;
        }

        for (var i = 0; i + 1 < parameters.Length; i++) {
            if (type.Parameters[i + 1] != parameters[i]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The body of a witness thunk whose witness is generic code compiled
    ///     once: it opens the box, and calls the witness on the value as code
    ///     that knows the type does — with the type's metadata and tables.
    ///     Lowered from the call, so that it passes and takes everything as
    ///     any such call does; when that cannot be done, the thunk calls the
    ///     witness's instance instead.
    /// </summary>
    void GenericWitnessThunk(SilFunction thunk, NamedTypeSymbol concrete, SilType value, ClassType box, string slot, Symbol requirement, ThickFunctionType signature,
        FunctionSymbol callee, TypeSubstitution bound, SyntaxNode at) {
        var parameters = signature.Signature.Parameters;
        var entry = thunk.AddBlock();
        var arguments = parameters.Take(parameters.Length - 1).Select(p => (SilValue)entry.AddArgument(p)).ToList();
        var given = entry.AddArgument(parameters[^1]);
        entry.AddArgument(SilType.Object);

        var current = entry;
        var held = (SilValue)given;
        SilValue? loaded = null;
        if (given.Type is AddressType) {
            current = UniqueBox(thunk, entry, given, box);
            loaded = held = current.Append(new LoadInst(given));
        }

        var opened = current.Append(new RefElementAddrInst(current.Append(new UncheckedRefCastInst(held, box)), 0));
        var syntax = callee.DeclaringSyntax.FirstOrDefault() ?? at;
        var lowered = false;
        if (SourceOf(syntax) is { } source) {
            var substitution = WithAssociatedTypes(callee, bound);
            var call = new BoundCall(syntax, new BoundSelfAccess(syntax, concrete, callee.IsMutating), callee,
                [..callee.Parameters.Select(p => (BoundExpression)new BoundParameterAccess(syntax, p))], callee.ReturnType) { Substitution = substitution };
            var lowering = new FunctionLowering(this, callee, thunk, source, substitution);
            using (_diagnostics.Suppress()) {
                lowered = lowering.LowerWitnessCall(entry, current, opened, arguments, call, block => {
                    if (loaded is not null) {
                        block.Append(new DestroyValueInst(loaded));
                    }
                });
            }
        }

        if (lowered) {
            // The call is all it does: no line of its own, as the thunk it replaces had none.
            foreach (var instruction in thunk.Blocks.SelectMany(static b => b.Instructions)) {
                instruction.Location = null;
            }

            thunk.RemoveUnreachableBlocks();
            return;
        }

        thunk.Blocks.Clear();
        using (_diagnostics.Suppress()) {
            if (FillWitnessThunk(thunk, concrete, value, box, slot, requirement, signature, at, viaGeneric: false)) {
                return;
            }
        }

        // Neither: a slot no program can reach yet, as a table that could not be made would be.
        thunk.Blocks.Clear();
        var stub = thunk.AddBlock();
        foreach (var parameter in thunk.Type.Parameters) {
            stub.AddArgument(parameter);
        }

        stub.Append(new UnreachableInst());
    }

    // -----------------------------------------------------------------------
    // Witness tables in the generic calling convention
    // -----------------------------------------------------------------------

    readonly Dictionary<NamedTypeSymbol, List<(string Key, Symbol Member)>> _genericRequirements = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     The requirements a protocol itself declares that a witness table's
    ///     generic entries hold, in declaration order: its methods, static
    ///     ones and operators among them, and its properties' getters.
    /// </summary>
    internal List<(string Key, Symbol Member)> GenericRequirements(NamedTypeSymbol protocol) {
        protocol = protocol.OriginalDefinition;
        if (!_genericRequirements.TryGetValue(protocol, out var requirements)) {
            requirements = [];
            foreach (var member in protocol.Members) {
                if (!ReferenceEquals(member.ContainingSymbol, protocol)) {
                    continue;
                }

                switch (member) {
                    case FunctionSymbol { FunctionKind: FunctionKind.Ordinary or FunctionKind.Operator } function:
                        requirements.Add(((function.IsStatic || function.FunctionKind == FunctionKind.Operator ? "static$" : "") + SlotName(function), function));
                        break;
                    case FieldSymbol field:
                        requirements.Add(((field.IsStatic ? "static$" : "") + GetterSlot(field), field));
                        break;
                }
            }

            _genericRequirements[protocol] = requirements;
        }

        return requirements;
    }

    /// <summary>
    ///     A requirement's witness's type in the generic calling convention:
    ///     <c>self</c> first (its storage's address, for one that changes
    ///     it) unless it is static, then its parameters, a value of
    ///     <c>Self</c> opaque, where a result of Self goes, then Self's metadata. Null for a requirement
    ///     the convention cannot pass yet: one generic itself, or with
    ///     <c>Self</c> or an associated type inside another type.
    /// </summary>
    internal FunctionType? GenericEntryType(NamedTypeSymbol protocol, Symbol requirement) =>
        requirement switch {
            FunctionSymbol function => GenericEntryType(protocol, function),
            FieldSymbol field => GenericGetterType(protocol, field),
            _ => null,
        };

    /// <summary>A property's getter in the generic calling convention: <c>self</c> unless it is static, where a result of Self goes, then Self's metadata.</summary>
    FunctionType? GenericGetterType(NamedTypeSymbol protocol, FieldSymbol field) {
        protocol = protocol.OriginalDefinition;
        if (protocol.SelfTypeParameter is not { } self) {
            return null;
        }

        if (field.IsAsyncGetter) {
            return null;
        }

        var opaque = new OpaqueType(self.Name);
        ImmutableArray<SilType> taken = field.IsStatic ? [] : [opaque];
        if (HeldOfSelf(field.Type, self) is { } held) {
            return new FunctionType([..taken, SilType.Address(held), SilType.Metadata], SilType.Unit);
        }

        return Mentions(field.Type, [self, ..protocol.AssociatedTypes]) || Lower(field.Type) is not { } result ? null : new FunctionType([..taken, SilType.Metadata], result);
    }

    internal FunctionType? GenericEntryType(NamedTypeSymbol protocol, FunctionSymbol requirement) {
        protocol = protocol.OriginalDefinition;
        // An async one would keep the caller's storage across a suspension: not yet.
        if (protocol.SelfTypeParameter is not { } self || !requirement.TypeParameters.IsEmpty || requirement.IsRethrows || !requirement.OpaqueTypeParameters.IsEmpty
            || requirement.IsAsync) {
            return null;
        }

        var own = new List<TypeParameterSymbol>([self, ..protocol.AssociatedTypes]);
        var opaque = new OpaqueType(self.Name);
        SilType? Slot(TypeSymbol type) =>
            type.Equals(self) ? opaque : HeldOfSelf(type, self) is { } held ? held : Mentions(type, own) ? null : Lower(type);

        var parameters = ImmutableArray.CreateBuilder<SilType>();
        if (HasSelf(requirement)) {
            parameters.Add(requirement.IsMutating ? SilType.Address(opaque) : opaque);
        }

        foreach (var parameter in requirement.Parameters) {
            if (parameter.IsVariadic || parameter.IsByReference || Slot(parameter.Type) is not { } type) {
                return null;
            }

            switch (parameter.Modifier) {
                case ParameterModifier.None or ParameterModifier.In:
                    parameters.Add(type);
                    break;
                case ParameterModifier.InOut when !SilType.IsIndirect(type):
                    parameters.Add(SilType.Address(type));
                    break;
                default:
                    return null;
            }
        }

        if (Slot(requirement.ReturnType) is not { } result) {
            return null;
        }

        // A result of Self, or made of it, is written where the caller says.
        if (SilType.IsIndirect(result)) {
            parameters.Add(SilType.Address(result));
            result = SilType.Unit;
        }

        // So is an error of an associated type, 'throws(Failure)': the
        // caller says where, and is thrown that address.
        SilType? thrown = null;
        if (requirement.ThrownType is { } thrownType && !thrownType.Equals(Core.Never)) {
            if ((thrown = Slot(thrownType)) is null) {
                return null;
            }

            if (SilType.IsIndirect(thrown)) {
                thrown = SilType.Address(thrown);
                parameters.Add(thrown);
            }
        }

        parameters.Add(SilType.Metadata);
        return new FunctionType(parameters.ToImmutable(), result, thrown);
    }

    /// <summary>The generic entries made so far, by table.</summary>
    readonly HashSet<SilWitnessTable> _genericTabled = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     A type's witness tables for a protocol and every protocol it
    ///     refines, with their entries in the generic calling convention: what
    ///     a call from code that knows the type passes unspecialised code,
    ///     which finds the refined ones in the registry. False when the type
    ///     does not declare each conformance itself, or it cannot be lowered.
    /// </summary>
    internal bool GenericTable(TypeSymbol concrete, NamedTypeSymbol protocol, SyntaxNode at, bool describe = true) {
        if (Lower(concrete) is not { } value || concrete is not (NamedTypeSymbol or ArrayTypeSymbol)) {
            return false;
        }

        // An array conforms by its extensions, which say so for every array of its kind.
        var closure = ProtocolClosure([protocol]);
        if (concrete is NamedTypeSymbol named) {
            var declared = DeclaredProtocols(named.OriginalDefinition).ToHashSet(StringComparer.Ordinal);
            if (value is not NominalType) {
                declared.UnionWith(_module.PrimitiveConformances.GetValueOrDefault(value.ToString()) ?? []);
            }

            if (!closure.TrueForAll(p => declared.Contains(QualifiedName(p)))) {
                return false;
            }
        } else if (_generics != GenericsMode.Unspecialised || value is not ArrayType) {
            return false;
        }

        if (!closure.TrueForAll(concrete.Satisfies)) {
            return false;
        }

        foreach (var each in closure) {
            var name = QualifiedName(each);
            var table = _module.FindWitnessTable(value, name);
            if (table is null) {
                if (concrete is not NamedTypeSymbol boxed || !WitnessTable(boxed, value, each, at) || (table = _module.FindWitnessTable(value, name)) is null) {
                    table = new SilWitnessTable(value, name) { HasEntries = false };
                    AssociatedTypes(table, concrete, each);
                    _module.AddWitnessTable(table);
                }
            }

            GenericEntries(table, concrete, value, each, at);
        }

        // An error of the type, which unspecialised code may throw on as 'any Error'.
        if (_generics == GenericsMode.Unspecialised && closure.Exists(p => ReferenceEquals(p, Core.Error.OriginalDefinition))) {
            ErrorEraser(value);
        }

        // Unspecialised code reads the conformer's associated types through
        // the table, and finds what they conform to in the registry: those
        // tables are made too.
        // Written out by its describer, as a type parameter's value is — for
        // code that may write one out.
        var tabled = _associatedTabled.Add((concrete, protocol.OriginalDefinition));
        var described = describe && _associatedDescribed.Add((concrete, protocol.OriginalDefinition));
        if (_generics == GenericsMode.Unspecialised && (tabled || described)) {
            foreach (var each in closure) {
                foreach (var associated in each.AssociatedTypes) {
                    if (described && AssociatedBinding(concrete, associated) is { } read && Lower(read) is { } lowered && !SilType.MentionsOpaque(lowered)) {
                        Describer(read, lowered, at);
                    }

                    if (AssociatedBinding(concrete, associated) is not { } bound || bound is not (NamedTypeSymbol or ArrayTypeSymbol)) {
                        continue;
                    }

                    foreach (var constraint in associated.Constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c)).OfType<NamedTypeSymbol>()) {
                        if (constraint.TypeKind == TypeKind.Protocol && IsTabledProtocol(constraint) && bound.Satisfies(constraint)) {
                            GenericTable(bound, constraint.OriginalDefinition, at, describe);
                        }
                    }
                }
            }
        }

        return true;
    }

    readonly HashSet<(TypeSymbol, NamedTypeSymbol)> _associatedTabled = [];

    /// <summary>The conformances whose associated types have describers made, for code that may write their values out.</summary>
    readonly HashSet<(TypeSymbol, NamedTypeSymbol)> _associatedDescribed = [];

    /// <summary>What a conformer binds an associated type to, read with the conformer's own type arguments; null when it does not, or binds it to Never.</summary>
    TypeSymbol? AssociatedBinding(TypeSymbol concrete, TypeParameterSymbol associated) {
        TypeSymbol? read = null;
        if (concrete is NamedTypeSymbol named) {
            read = _compilation.AssociatedTypeOf(named, associated) is { } bound ? TypeSubstitution.For(named).Apply(bound) : null;
        } else if (associated.ContainingSymbol is NamedTypeSymbol { SelfTypeParameter: { } self }) {
            // An array's, as its conforming extension says: 'Self.Element' read with Self the array.
            read = new TypeSubstitution(new Dictionary<TypeParameterSymbol, TypeSymbol> { [self] = concrete }).Apply(new DependentMemberTypeSymbol(self, associated));
        }

        return read is null or DependentMemberTypeSymbol || read.Equals(Core.Never) ? null : read;
    }

    /// <summary>
    ///     Fills a table's entries in the generic calling convention, once, in
    ///     an unspecialised build: every table it makes has them, whatever
    ///     asked for the table first.
    /// </summary>
    void GenericEntries(SilWitnessTable table, TypeSymbol concrete, SilType value, NamedTypeSymbol protocol, SyntaxNode at) {
        // A value laid out as another type than its nominal one (an optional, whose members are Optional's) has none yet.
        if (_generics != GenericsMode.Unspecialised || !IsTabledProtocol(protocol) || Lower(concrete) != value || !_genericTabled.Add(table)) {
            return;
        }

        foreach (var refined in Bases(protocol.OriginalDefinition)) {
            table.Bases.Add(QualifiedName(refined.OriginalDefinition));
        }

        foreach (var (key, member) in GenericRequirements(protocol)) {
            string? witness = null;
            var type = GenericEntryType(protocol, member);
            if (type is not null) {
                using (_diagnostics.Suppress()) {
                    witness = GenericWitness(concrete, value, protocol, key, member, type, at);
                }
            }

            table.GenericEntries.Add((key, witness, witness is null ? null : type));
        }
    }

    /// <summary>A table's associated types, as the conformance binds them, and the protocols each must conform to.</summary>
    void AssociatedTypes(SilWitnessTable table, TypeSymbol concrete, NamedTypeSymbol protocol) {
        foreach (var associated in protocol.OriginalDefinition.AssociatedTypes) {
            // Each in its place, which unspecialised code reads it from: one
            // the conformance does not bind, or binds to Never, as '()'.
            var lowered = AssociatedBinding(concrete, associated) is { } bound ? Lower(bound) : null;
            var index = table.AssociatedTypes.Count;
            table.AssociatedTypes.Add((associated.Name, lowered ?? SilType.Unit));
            if (lowered is null) {
                continue;
            }

            foreach (var constraint in associated.Constraints.OfType<NamedTypeSymbol>().Where(static c => c.TypeKind == TypeKind.Protocol)) {
                table.AssociatedConformances.Add((index, QualifiedName(constraint)));
            }
        }
    }

    /// <summary>The source of the file a node is in, and the tree.</summary>
    SourceText? SourceOf(SyntaxNode syntax) {
        var root = syntax;
        while (root.Parent is { } parent) {
            root = parent;
        }

        return _compilation.SyntaxTrees.FirstOrDefault(t => ReferenceEquals(t.Root, root))?.Text;
    }

    /// <summary>
    ///     A requirement's witness for a type in the generic calling
    ///     convention: the requirement lowered as if its body called itself
    ///     on the type — so it is met as code that knows the type meets it,
    ///     the type's own member, a default, a builtin — with what arrives
    ///     opaque copied out of its caller's storage first, and a result of
    ///     <c>Self</c> written where the caller says. Null when it cannot be made.
    /// </summary>
    string? GenericWitness(TypeSymbol concrete, SilType value, NamedTypeSymbol protocol, string key, Symbol member, FunctionType type, SyntaxNode at) {
        if (member is FieldSymbol field) {
            return GenericGetter(concrete, value, protocol, field, type, at);
        }

        var requirement = (FunctionSymbol)member;
        // By its place in the table: operators sanitise alike (+ and - both to $).
        var index = GenericRequirements(protocol).FindIndex(r => ReferenceEquals(r.Member, requirement));
        var name = $"$generic$witness${Sanitise(value.ToString())}${Sanitise(QualifiedName(protocol))}${index}${(requirement.Name.All(char.IsLetterOrDigit) ? requirement.Name : "op")}";
        if (_module.Find(name) is not null) {
            return name;
        }

        var self = protocol.OriginalDefinition.SelfTypeParameter!;
        var syntax = requirement.DeclaringSyntax.FirstOrDefault() ?? at;
        if (SourceOf(syntax) is not { } source) {
            return null;
        }

        var substitution = WithAssociatedTypes(requirement, new TypeSubstitution(new Dictionary<TypeParameterSymbol, TypeSymbol> { [self] = concrete }));
        BoundExpression? receiver = HasSelf(requirement) ? new BoundSelfAccess(syntax, self, requirement.IsMutating)
            : requirement.IsStatic ? new BoundTypeExpression(syntax, self)
            : null;
        BoundExpression call = new BoundCall(syntax, receiver, requirement, [..requirement.Parameters.Select(p => (BoundExpression)new BoundParameterAccess(syntax, p))], requirement.ReturnType) {
            Substitution = substitution,
        };

        // An operator is applied as an operator: what code that knows the
        // type does with it — an enum's '==' compares cases — and no call of
        // a function the type may not have.
        if (requirement is { FunctionKind: FunctionKind.Operator, Parameters: [var left, var right] } && OperatorKindOf(requirement.Name) is { } kind) {
            call = new BoundBinaryOperator(syntax, requirement.ReturnType, kind, new BoundParameterAccess(syntax, left), new BoundParameterAccess(syntax, right), requirement);
        }
        BoundStatement[] statements = requirement.ReturnType.Equals(Core.Void)
            ? [new BoundExpressionStatement(syntax, call), new BoundReturn(syntax, null)]
            : [new BoundReturn(syntax, call)];

        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
        };
        var bridge = new Bridge(HasSelf(requirement), [..requirement.Parameters.Where(p => Mentions(p.Type, [self]))], Mentions(requirement.ReturnType, [self]));
        var lowering = new FunctionLowering(this, requirement, function, source, substitution) { Bridged = bridge with { Self = concrete } };
        lowering.Lower(new BoundBlock(syntax, [], [..statements]));
        if (lowering.Failed) {
            return null;
        }

        function.RemoveUnreachableBlocks();
        _module.Add(function);
        return name;
    }

    /// <summary>The binary operator a requirement's name writes, as the binder knows it; null for one it binds as a call.</summary>
    static BinaryOperatorKind? OperatorKindOf(string name) =>
        name switch {
            "+" => BinaryOperatorKind.Add,
            "-" => BinaryOperatorKind.Subtract,
            "*" => BinaryOperatorKind.Multiply,
            "/" => BinaryOperatorKind.Divide,
            "%" => BinaryOperatorKind.Remainder,
            "&" => BinaryOperatorKind.And,
            "|" => BinaryOperatorKind.Or,
            "^" => BinaryOperatorKind.ExclusiveOr,
            "<<" => BinaryOperatorKind.LeftShift,
            ">>" => BinaryOperatorKind.RightShift,
            "==" => BinaryOperatorKind.Equal,
            "!=" => BinaryOperatorKind.NotEqual,
            "<" => BinaryOperatorKind.LessThan,
            "<=" => BinaryOperatorKind.LessThanOrEqual,
            ">" => BinaryOperatorKind.GreaterThan,
            ">=" => BinaryOperatorKind.GreaterThanOrEqual,
            _ => null,
        };

    /// <summary>
    ///     A property requirement's getter for a type in the generic calling
    ///     convention: lowered as <c>print</c>'s instance would be if its body
    ///     read the property of its one parameter — the value, taken out of
    ///     the value it is given — or, for a static one, of the type.
    /// </summary>
    string? GenericGetter(TypeSymbol concrete, SilType value, NamedTypeSymbol protocol, FieldSymbol field, FunctionType type, SyntaxNode at) {
        var index = GenericRequirements(protocol).FindIndex(r => ReferenceEquals(r.Member, field));
        var name = $"$generic$witness${Sanitise(value.ToString())}${Sanitise(QualifiedName(protocol))}${index}${field.Name}";
        if (_module.Find(name) is not null) {
            return name;
        }

        if (PrintSymbol() is not { } print || print.DeclaringSyntax.FirstOrDefault() is not { } syntax || SourceOf(syntax) is not { } source) {
            return null;
        }

        var self = protocol.OriginalDefinition.SelfTypeParameter!;
        var substitution = WithAssociatedTypes(field, new TypeSubstitution(new Dictionary<TypeParameterSymbol, TypeSymbol> { [self] = concrete, [print.TypeParameters[0]] = concrete }));
        BoundExpression receiver = field.IsStatic ? new BoundTypeExpression(syntax, self) : new BoundParameterAccess(syntax, print.Parameters[0]);
        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
        };
        var bridge = new Bridge(false, field.IsStatic ? [] : [print.Parameters[0]], Mentions(field.Type, [self])) {
            Returns = new BoundFieldAccess(syntax, receiver, field, field.Type),
            TakesNoParameters = field.IsStatic,
            Self = concrete,
        };
        var lowering = new FunctionLowering(this, print, function, source, substitution) { Bridged = bridge };
        lowering.Lower(new BoundBlock(syntax, [], []));
        if (lowering.Failed) {
            return null;
        }

        function.RemoveUnreachableBlocks();
        _module.Add(function);
        return name;
    }

    /// <summary>Sable.Core's <c>print&lt;T&gt;(_ value: T)</c>: what bridged functions of one opaque value are lowered as.</summary>
    FunctionSymbol? PrintSymbol() =>
        Core.Module.GetMembers("print").OfType<FunctionSymbol>().FirstOrDefault(static f => f is { TypeParameters.Length: 1, Parameters.Length: 1 });

    /// <summary>A protocol's associated types, as a conformance binds them, for a property requirement as for a method.</summary>
    static TypeSubstitution WithAssociatedTypes(FieldSymbol field, TypeSubstitution substitution) {
        if (field.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self } protocol || substitution.Lookup(self) is null) {
            return substitution;
        }

        var map = substitution.Parameters.ToDictionary(p => p, p => substitution.Lookup(p)!);
        foreach (var associated in ProtocolClosure([protocol]).SelectMany(static p => p.AssociatedTypes)) {
            map.TryAdd(associated, substitution.Apply(new DependentMemberTypeSymbol(self, associated)));
        }

        return new(map);
    }

    /// <summary>
    ///     How a function generic code calls is given what it gets: <c>self</c>
    ///     opaque, the parameters given opaque, the result written where the caller says; for a
    ///     describer, the one parameter to write out as a string; for a
    ///     getter, the expression it returns, and whether it takes its
    ///     symbol's parameters at all.
    /// </summary>
    internal sealed record Bridge(bool OpaqueSelfValue, ImmutableHashSet<ParameterSymbol> OpaqueParameters, bool OpaqueResult, SyntaxNode? Describes = null) {
        public BoundExpression? Returns { get; init; }
        public bool TakesNoParameters { get; init; }

        /// <summary>The type the function is for: what Self is.</summary>
        public TypeSymbol? Self { get; init; }
    }

    // -----------------------------------------------------------------------
    // Reabstraction: a closure given where the generic convention is wanted
    // -----------------------------------------------------------------------

    readonly Dictionary<string, (string Thunk, ClassType Context)?> _reabstractions = new(StringComparer.Ordinal);

    /// <summary>
    ///     A thunk that calls a closure of one convention as one of another
    ///     wants to be called (#297): a function of a type parameter's
    ///     values, called in the generic convention — each such value the
    ///     address of its caller's storage, a result of one written where the
    ///     caller says — over a closure of the types the code that made it
    ///     knows, <c>(Int) -> Int</c>, or of another function's type parameters.
    ///     Its context holds the closure. Null for a pair it cannot convert.
    /// </summary>
    internal (string Thunk, ClassType Context)? Reabstraction(ThickFunctionType source, ThickFunctionType target) {
        var key = $"{source} as {target}";
        if (_reabstractions.TryGetValue(key, out var known)) {
            return known;
        }

        // Named for the pair, so that every unit that makes one makes the same.
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        var made = MakeReabstraction(source.Signature, target.Signature, $"$reabstract${hash}", source);
        _reabstractions[key] = made;
        return made;
    }

    (string Thunk, ClassType Context)? MakeReabstraction(FunctionType source, FunctionType target, string name, ThickFunctionType closure) {
        var outResult = target.Result == SilType.Unit && target.Parameters.Length == source.Parameters.Length + 1
                        && target.Parameters[^1] is AddressType { Pointee: var resultPointee } && SilType.IsIndirect(resultPointee);
        if (target.Parameters.Length != source.Parameters.Length + (outResult ? 1 : 0) || target.IsAsync != source.IsAsync
            || source.Thrown is not null && source.Thrown != target.Thrown) {
            return null;
        }

        var context = new ClassType($"{name}$context");
        context.Fields.Add(("function", closure));
        var thunk = new SilFunction(name, new ThickFunctionType(target).Implementation);
        var entry = thunk.AddBlock();
        var given = target.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();
        var contextArgument = entry.AddArgument(SilType.Object);
        var holder = entry.Append(new UncheckedRefCastInst(contextArgument, context));
        var function = entry.Append(new LoadBorrowInst(entry.Append(new RefElementAddrInst(holder, 0))));

        // Each argument as the closure takes it: where the caller holds it, seen as the type it is.
        var arguments = new List<SilValue>();
        var ends = new List<SilValue>();
        for (var i = 0; i < source.Parameters.Length; i++) {
            var (wanted, value) = (source.Parameters[i], given[i]);
            if (wanted == value.Type) {
                arguments.Add(value);
            } else if (value.Type is AddressType && wanted is AddressType && (SilType.MentionsOpaque(value.Type) || SilType.MentionsOpaque(wanted))) {
                arguments.Add(entry.Append(new BuiltinInst("opaque_cast", wanted, value)));
            } else if (SilType.SameErasingOpaque(value.Type, wanted) || SilType.IsIndirect(value.Type) && SilType.IsIndirect(wanted)) {
                arguments.Add(entry.Append(new BuiltinInst("opaque_borrow_as", wanted, value)));
            } else if (SilType.IsIndirect(value.Type) && wanted is not AddressType and not ThickFunctionType) {
                var address = entry.Append(new BuiltinInst("opaque_address", SilType.Address(wanted), value));
                if (wanted.IsTrivial) {
                    arguments.Add(entry.Append(new LoadInst(address, LoadKind.Trivial)));
                } else {
                    var borrowed = entry.Append(new LoadBorrowInst(address));
                    arguments.Add(borrowed);
                    ends.Add(borrowed);
                }
            } else {
                return null;
            }
        }

        var resultType = source.Result;
        void Finish(SilBlock block, SilValue? result) {
            for (var i = ends.Count - 1; i >= 0; i--) {
                block.Append(new EndBorrowInst(ends[i]));
            }

            block.Append(new EndBorrowInst(function));
            if (outResult && result is not null && resultType != SilType.Unit) {
                var into = given[^1];
                SilValue at = ((AddressType)into.Type).Pointee == resultType ? into : block.Append(new BuiltinInst("opaque_cast", SilType.Address(resultType), into));
                block.Append(new StoreInst(result, at, StoreKind.Init));
                result = null;
            }

            if (!outResult && result is not null && result.Type != target.Result) {
                result = block.Append(new BuiltinInst("opaque_take_as", target.Result, result));
            }

            block.Append(new ReturnInst(outResult || target.Result == SilType.Unit ? null : result));
        }

        if (!outResult && resultType != target.Result && !SilType.SameErasingOpaque(resultType, target.Result)) {
            return null;
        }

        if (outResult && SilType.IsIndirect(resultType)) {
            return null;
        }

        if (source.Thrown is null) {
            var result = entry.Append(new ApplyInst(function, resultType, [..arguments]));
            Finish(entry, resultType == SilType.Unit ? null : result);
        } else {
            var normal = thunk.AddBlock();
            var result = resultType == SilType.Unit ? null : normal.AddArgument(resultType);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(source.Thrown);
            entry.Append(new TryApplyInst(function, resultType, [..arguments], normal, failed));
            Finish(normal, result);
            for (var i = ends.Count - 1; i >= 0; i--) {
                failed.Append(new EndBorrowInst(ends[i]));
            }

            failed.Append(new EndBorrowInst(function));
            failed.Append(new ThrowInst(error));
        }

        _module.AddType(context);
        _module.Add(thunk);
        return (name, context);
    }

    // -----------------------------------------------------------------------
    // Error erasers: an error unspecialised code knows by its metadata, as 'any Error'
    // -----------------------------------------------------------------------

    /// <summary>
    ///     Boxes an error of a type unspecialised code is given as
    ///     <c>any Error</c>, as code that knows the type does
    ///     (<c>opaque_erase_error</c>): a function given where the value is
    ///     and its metadata, which copies it into its type's box. Made where
    ///     the type is known, with the type's table of its conformance to Error.
    /// </summary>
    internal void ErrorEraser(SilType value) {
        if (SilType.MentionsOpaque(value) || _module.ErrorErasers.ContainsKey(value.ToString())) {
            return;
        }

        var anyError = AnyErrorType();
        var name = $"$erase${Sanitise(value.ToString())}";
        var opaque = new OpaqueType("E");
        var function = new SilFunction(name, new FunctionType([opaque, SilType.Metadata], anyError));
        function.ParameterNames.AddRange(["$error", "$metadata.E"]);
        var block = function.AddBlock();
        var given = block.AddArgument(opaque);
        block.AddArgument(SilType.Metadata);
        var address = block.Append(new BuiltinInst("opaque_address", SilType.Address(value), given));
        var copy = block.Append(new LoadInst(address, value.IsTrivial ? LoadKind.Trivial : LoadKind.Copy));
        if (ConvertError(function, ref block, copy, anyError) is not { } boxed) {
            return;
        }

        block.Append(new ReturnInst(boxed));
        _module.Add(function);
        _module.ErrorErasers[value.ToString()] = (value, name);
    }

    // -----------------------------------------------------------------------
    // Describers: what '"\(x)"' writes for a value of a type parameter's type
    // -----------------------------------------------------------------------

    /// <summary>The types whose describer has been asked for.</summary>
    readonly HashSet<SilType> _described = [];

    /// <summary>
    ///     Writes out a value of a type unspecialised code is given, as an
    ///     interpolation in code that knows the type would
    ///     (<c>generic_describe</c>): a function given the value and its
    ///     metadata. Made the first time code that knows the type hands its
    ///     metadata to unspecialised code.
    /// </summary>
    internal void Describer(TypeSymbol type, SilType value, SyntaxNode at) {
        if (!_described.Add(value)) {
            return;
        }


        if (PrintSymbol() is not { } print || print.DeclaringSyntax.FirstOrDefault() is not { } syntax || SourceOf(syntax) is not { } source) {
            return;
        }

        var name = $"$describe${Sanitise(value.ToString())}";
        var function = new SilFunction(name, new FunctionType([new OpaqueType(print.TypeParameters[0].Name), SilType.Metadata], SilType.String)) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
        };
        var substitution = new TypeSubstitution(new Dictionary<TypeParameterSymbol, TypeSymbol> { [print.TypeParameters[0]] = type });
        var lowering = new FunctionLowering(this, print, function, source, substitution) { Bridged = new Bridge(false, [print.Parameters[0]], false, at) };
        using (_diagnostics.Suppress()) {
            lowering.Lower(new BoundBlock(syntax, [], []));
        }

        if (lowering.Failed) {
            return;
        }

        function.RemoveUnreachableBlocks();
        _module.Add(function);
        _module.Describers[value.ToString()] = (value, name);
    }

    sealed partial class FunctionLowering {
        /// <summary>
        ///     Lowers a witness thunk's call (<see cref="GenericWitnessThunk" />)
        ///     into the thunk, from <paramref name="current" /> on: <c>self</c>
        ///     the value in the box, at <paramref name="opened" />, and the
        ///     callee's parameters what the thunk is given. Each way out frees
        ///     what the call needed, then lets go of the box (<paramref name="release" />).
        ///     False when the call cannot be lowered.
        /// </summary>
        internal bool LowerWitnessCall(SilBlock entry, SilBlock current, SilValue opened, IReadOnlyList<SilValue> arguments, BoundCall call, Action<SilBlock> release) {
            _entry = entry;
            _current = current;
            _selfAddress = opened;
            for (var i = 0; i < symbol.Parameters.Length; i++) {
                _parameters[symbol.Parameters[i]] = arguments[i];
            }

            BoundStatement[] statements = symbol.ReturnType.Equals(Core.Void)
                ? [new BoundExpressionStatement(call.Syntax, call), new BoundReturn(call.Syntax, null)]
                : [new BoundReturn(call.Syntax, call)];
            _body = new BoundBlock(call.Syntax, [], [..statements]);
            Statement(_body);
            if (_failed || _current is not null) {
                return false;
            }

            if (_returnBlock is { } returnBlock) {
                _current = returnBlock;
                Deallocate();
                release(_current);
                Jump(new ReturnInst(returnBlock.Arguments.Count == 0 ? null : returnBlock.Arguments[0]));
            }

            if (_throwBlock is { } throwBlock) {
                _current = throwBlock;
                Deallocate();
                release(_current);
                Jump(new ThrowInst(throwBlock.Arguments[0]));
            }

            return !_failed;
        }

        /// <summary>For a function generic code calls (<see cref="Bridge" />), what arrives as a type parameter's value.</summary>
        internal Bridge? Bridged { get; init; }

        /// <summary>True when the body could not be lowered: the function is a stub.</summary>
        internal bool Failed => _failed;

        /// <summary>In unspecialised code, each type parameter's metadata, as the function is given it.</summary>
        readonly Dictionary<TypeParameterSymbol, SilValue> _genericMetadata = new(ReferenceEqualityComparer.Instance);

        /// <summary>In unspecialised code, the witness tables it is given, by type parameter and protocol.</summary>
        readonly Dictionary<(TypeParameterSymbol, NamedTypeSymbol), SilValue> _genericTables = [];

        /// <summary>Where a function whose result is a type parameter's value writes it: the address its caller passes.</summary>
        SilArgument? _indirectResult;

        /// <summary>Where a bridged function writes an error of an associated type before it throws the address.</summary>
        SilArgument? _errorOut;

        /// <summary>
        ///     An error on its way out of a bridged function whose error is
        ///     written where its caller says: written there, as the type it is,
        ///     and the address thrown. False for any other function.
        /// </summary>
        bool ErrorOut(ref SilValue error) {
            if (_errorOut is not { } into || _handlers.Count > 0 || _current is null) {
                return false;
            }

            // A callee's, written where it was told: this function's own place.
            if (error.Type is not AddressType) {
                Emit(new StoreInst(Owned(error), Emit(new BuiltinInst("opaque_cast", SilType.Address(error.Type), into)), StoreKind.Init));
            }

            error = into;
            return true;
        }

        /// <summary>
        ///     Where a callee that writes its error where it is told writes it,
        ///     given what the call throws here: none, a slot that is never
        ///     written; one this function throws too, its own caller's place;
        ///     one of a type known here, a slot it is taken from. Null for an
        ///     error this function cannot take.
        /// </summary>
        (SilValue Passed, AllocStackInst? Slot, bool CannotThrow)? GenericErrorOut(AddressType expected, TypeSymbol thrown) {
            var canonical = CanonicalHere(Sub(thrown));
            if (canonical.Equals(generator.Core.Never)) {
                return (Emit(new BuiltinInst("opaque_cast", expected, NewSlot(SilType.Int))), null, true);
            }

            if (IsOpaqueHere(canonical) || LowerType(canonical) is { } held && SilType.IsIndirect(held)) {
                if (_errorOut is { } own) {
                    return (own.Type == expected ? own : Emit(new BuiltinInst("opaque_cast", expected, own)), null, false);
                }

                // Thrown on as 'any Error': a slot of its own, the error taken
                // from there and boxed by its type's eraser where it goes.
                if (LowerType(canonical) is OpaqueType opaque) {
                    var errorSlot = NewSlot(opaque);
                    return (Emit(new BuiltinInst("opaque_cast", expected, errorSlot)), errorSlot, false);
                }

                return null;
            }

            if (LowerType(canonical) is not { } type) {
                return null;
            }

            var slot = NewSlot(type);
            return (Emit(new BuiltinInst("opaque_cast", expected, slot)), slot, false);
        }

        /// <summary>What a bridged function arrived with, waiting for the entry block: self, and the parameters of Self's type.</summary>
        SilArgument? _bridgeSelf;

        readonly List<(ParameterSymbol Parameter, SilArgument Value)> _bridgeParameters = [];

        /// <summary>True for a type parameter of this unspecialised function, or an associated type read through one, which no requirement makes another type.</summary>
        bool IsOpaqueHere(TypeSymbol type) =>
            type is TypeParameterSymbol parameter && _genericMetadata.ContainsKey(parameter)
            || _genericMetadata.Count > 0 && IsAssociatedOf(type, [.._genericMetadata.Keys]) && generator.Canonical(type) is var canonical
            && (canonical.Equals(type) ? true : IsOpaqueHere(canonical));

        /// <summary>Names the entry block's newest argument, for the debugger and for finding a type parameter's metadata (<c>$metadata.T</c>).</summary>
        void NameArgument(string name) {
            while (function.ParameterNames.Count < _entry.Arguments.Count - 1) {
                function.ParameterNames.Add($"$argument{function.ParameterNames.Count}");
            }

            function.ParameterNames.Add(name);
        }

        /// <summary>
        ///     Takes the hidden parameters of an unspecialised function, after
        ///     its own: where its result goes, when it is a type parameter's,
        ///     then the metadata and the witness tables; a bridged function's
        ///     result's address and Self's metadata.
        /// </summary>
        void GenericParameters(ImmutableArray<SilType> parameterTypes, ref int next) {
            if (generator._unspecialised.TryGetValue(function.Name, out var generic)) {
                if (function.Type.Result == SilType.Unit && next < parameterTypes.Length && parameterTypes[next] is AddressType { Pointee: var pointee } && SilType.IsIndirect(pointee)
                    && generator.Lower(symbol.ReturnType) is { } returned && SilType.IsIndirect(returned)) {
                    _indirectResult = _entry.AddArgument(parameterTypes[next++]);
                    NameArgument("$out");
                }

                if (function.Type.Thrown is AddressType genericErrorAddress && next < parameterTypes.Length && parameterTypes[next] == genericErrorAddress) {
                    _errorOut = _entry.AddArgument(parameterTypes[next++]);
                    NameArgument("$error");
                }

                foreach (var parameter in generic.Shape.Parameters) {
                    _genericMetadata[parameter] = _entry.AddArgument(parameterTypes[next++]);
                    NameArgument($"$metadata.{parameter.Name}");
                }

                foreach (var (parameter, protocol) in generic.Shape.Requirements) {
                    _genericTables[(parameter, protocol)] = _entry.AddArgument(parameterTypes[next++]);
                    NameArgument($"$witnesses.{parameter.Name}.{protocol.Name}");
                }
            }

            if (Bridged is null) {
                return;
            }

            if (Bridged.OpaqueResult && next < parameterTypes.Length && parameterTypes[next] is AddressType { Pointee: var bridgedPointee } && SilType.IsIndirect(bridgedPointee)
                && !(function.Type.Thrown is AddressType && next == parameterTypes.Length - 2)) {
                _indirectResult = _entry.AddArgument(parameterTypes[next++]);
                NameArgument("$out");
            }

            // Where an error of an associated type goes, which is thrown as that address.
            if (function.Type.Thrown is AddressType errorAddress && next < parameterTypes.Length && parameterTypes[next] == errorAddress) {
                _errorOut = _entry.AddArgument(parameterTypes[next++]);
                NameArgument("$error");
            }

            if (next < parameterTypes.Length && parameterTypes[next] == SilType.Metadata) {
                var opaque = parameterTypes.Select(static p => p is AddressType address ? address.Pointee : p).OfType<OpaqueType>().FirstOrDefault();
                _entry.AddArgument(parameterTypes[next++]);
                NameArgument($"$metadata.{opaque?.Name ?? "Self"}");
            }
        }

        /// <summary>
        ///     In a bridged function, what arrived as a type parameter's value
        ///     is seen as the type it is: a parameter, or a <c>self</c> only
        ///     read, copied out of the caller's storage into a slot of its own;
        ///     a <c>self</c> to change, changed where the caller has it.
        /// </summary>
        void BridgeArguments() {
            if (Bridged is null) {
                return;
            }

            if (_bridgeSelf is { } given && LowerType(SelfType(symbol, substitution)) is { } self) {
                if (given.Type is AddressType) {
                    _selfAddress = Emit(new BuiltinInst("opaque_cast", SilType.Address(self), given));
                } else {
                    var slot = NewSlot(self, "self");
                    Emit(new StoreInst(Emit(new LoadInst(Emit(new BuiltinInst("opaque_address", SilType.Address(self), given)))), slot, StoreKind.Init));
                    _selfAddress = slot;
                }
            }

            foreach (var (parameter, value) in _bridgeParameters) {
                if (LowerType(parameter.Type) is not { } type) {
                    _failed = true;
                    continue;
                }

                var slot = NewSlot(type, parameter.Name);
                Emit(new StoreInst(Emit(new LoadInst(Emit(new BuiltinInst("opaque_address", SilType.Address(type), value)))), slot, StoreKind.Init));
                _spilled[parameter] = slot;
                _spilledArguments.Add(value);
                _parameters.Remove(parameter);
            }
        }

        /// <summary>A bridged function's body: the describer's hole, written out and returned; a getter's read.</summary>
        bool BridgeBody() {
            if (Bridged?.Returns is { } returned) {
                if (Expression(returned) is { } read) {
                    Return(read);
                }

                return true;
            }

            if (Bridged?.Describes is not { } at) {
                return false;
            }

            if (Hole(new BoundParameterAccess(at, symbol.Parameters[0]), null) is { } text) {
                Return(text);
            }

            return true;
        }

        /// <summary>
        ///     A returned value, when the function writes its result where its
        ///     caller says: stored there — a type parameter's value as it is, or
        ///     in a bridged function the value of the type it is, through the
        ///     address seen as that type — and nothing left to return.
        /// </summary>
        SilValue? IndirectResult(SilValue? value) {
            if (_indirectResult is not { } result || value is null or UnitValue || _current is null) {
                return value;
            }

            var owned = Owned(value);
            SilValue into = value.Type == ((AddressType)result.Type).Pointee ? result : Emit(new BuiltinInst("opaque_cast", SilType.Address(value.Type), result));
            Emit(new StoreInst(owned, into, StoreKind.Init));
            return null;
        }

        /// <summary>
        ///     An opaque type's witness table for a protocol: a type parameter's
        ///     given, or the one a given one's refines through its bases; else,
        ///     as for an associated type, the registry's, kept at the call.
        /// </summary>
        SilValue TableFor(TypeSymbol subject, NamedTypeSymbol protocol) {
            protocol = protocol.OriginalDefinition;
            if (subject is TypeParameterSymbol parameter) {
                if (_genericTables.TryGetValue((parameter, protocol), out var table)) {
                    return table;
                }

                foreach (var ((given, held), value) in _genericTables) {
                    if (ReferenceEquals(given, parameter) && BasePath(held, protocol) is { } path) {
                        var at = value;
                        foreach (var index in path) {
                            at = Emit(new BuiltinInst($"witness_base:{index}", SilType.WitnessTable, at));
                        }

                        return at;
                    }
                }
            }

            return Emit(new BuiltinInst($"witness_table_known:{QualifiedName(protocol)}", SilType.WitnessTable, MetadataOf(subject)!));
        }

        internal static List<int>? BasePathOf(NamedTypeSymbol from, NamedTypeSymbol to) => BasePath(from.OriginalDefinition, to.OriginalDefinition);

        /// <summary>The indices of the bases to follow from one protocol's table to one it refines; null when it does not.</summary>
        static List<int>? BasePath(NamedTypeSymbol from, NamedTypeSymbol to) {
            if (ReferenceEquals(from, to)) {
                return [];
            }

            var bases = Bases(from).Select(static b => b.OriginalDefinition).ToList();
            for (var i = 0; i < bases.Count; i++) {
                if (BasePath(bases[i], to) is { } rest) {
                    return [i, ..rest];
                }
            }

            return null;
        }

        /// <summary>A type's metadata here: a type parameter's as given, any other's a constant.</summary>
        SilValue? MetadataOf(TypeSymbol type) {
            if (type is TypeParameterSymbol parameter && _genericMetadata.TryGetValue(parameter, out var given)) {
                return given;
            }

            // An associated type's: found where the function starts, from the table of its base's conformance.
            if (_genericMetadata.Count > 0 && IsAssociatedOf(type, [.._genericMetadata.Keys])) {
                return generator.Canonical(type) is var canonical && !canonical.Equals(type) ? MetadataOf(canonical)
                    : Emit(new BuiltinInst($"opaque_metadata:{generator.OpaqueName(type)}", SilType.Metadata));
            }

            // An array of a type parameter's values: made at run time from the element's.
            if (type is ArrayTypeSymbol { ElementType: var element } array && IsArrayOf(type, [.._genericMetadata.Keys])) {
                return MetadataOf(element) is { } of ? Emit(new BuiltinInst(array.ArrayKind == ArrayKind.Slice ? "metadata_slice" : "metadata_array", SilType.Metadata, of)) : null;
            }

            return LowerType(type) is { } lowered ? Emit(new MetadataInst(lowered)) : null;
        }

        /// <summary>The type parameter of this function a requirement's call is on, as Self, or an associated type read through one; null for a call on anything else.</summary>
        TypeSymbol? OpaqueSelf(BoundCall call) {
            if (_genericMetadata.Count == 0 || call.Function.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                return null;
            }

            var self = protocol.OriginalDefinition.SelfTypeParameter;
            var selfType = call.Receiver switch {
                BoundTypeExpression type => Sub(type.ReferencedType),
                { Type: MetatypeTypeSymbol metatype } => Sub(metatype.InstanceType),
                { } receiver => Sub(receiver.Type),
                null => (self is null ? null : call.Substitution.Lookup(self) is { } stated ? Sub(stated) : null)
                        ?? (call.Function.Parameters.Zip(call.Arguments).FirstOrDefault(p => p.First.Type.Equals(self)).Second is { } argument ? Sub(argument.Type) : null),
            };
            return selfType is not null && IsOpaqueHere(selfType) ? CanonicalHere(selfType) : null;
        }

        /// <summary>An opaque type here as the requirements in force read it.</summary>
        TypeSymbol CanonicalHere(TypeSymbol type) => type is TypeParameterSymbol parameter && _genericMetadata.ContainsKey(parameter) ? type : generator.Canonical(type);

        /// <summary>
        ///     Where a call writes a result of a type parameter's type: a slot
        ///     of the type it is here, which the call is given the address of —
        ///     seen as the callee's type parameter, when the type is known here.
        ///     Null when the result is not one.
        /// </summary>
        (SilValue Passed, AllocStackInst Slot)? GenericOut(SilType expected, TypeSymbol given) {
            if (expected is not AddressType { Pointee: var pointee } || !SilType.IsIndirect(pointee) || LowerType(given) is not { } type) {
                return null;
            }

            var slot = NewSlot(type);
            return (SilType.SameErasingOpaque(type, pointee) && SilType.IsIndirect(type) ? slot : (SilValue)Emit(new BuiltinInst("opaque_cast", expected, slot)), slot);
        }

        /// <summary>The result a call wrote where it was told: taken out of the slot.</summary>
        SilValue? TakeOut(SilValue? invoked, AllocStackInst slot) =>
            invoked is null || _current is null ? invoked : Emit(new LoadInst(slot, slot.ElementType.IsTrivial ? LoadKind.Trivial : LoadKind.Take));

        /// <summary>
        ///     A requirement called on a type parameter in unspecialised code:
        ///     its witness, from the type parameter's table, given the value
        ///     and the type's metadata, and for a result of Self, where to put it.
        /// </summary>
        SilValue? GenericRequirementCall(BoundCall call, TypeSymbol parameter) {
            var callee = call.Function;
            var protocol = ((NamedTypeSymbol)callee.ContainingSymbol!).OriginalDefinition;
            var index = generator.GenericRequirements(protocol).FindIndex(r => ReferenceEquals(r.Member, callee));
            if (index < 0 || generator.GenericEntryType(protocol, (Symbol)callee) is not { } type) {
                return Missing(call, $"'{callee.Name}' through a witness table");
            }

            var code = Emit(new BuiltinInst($"witness_generic:{index}", type, TableFor(parameter, protocol)));
            var arguments = new List<SilValue>();
            var borrows = new List<SilValue>();
            if (HasSelf(callee)) {
                // Implicitly, a protocol extension's own self.
                var self = callee.IsMutating
                    ? call.Receiver is { } receiver ? Address(receiver, borrows) : _selfAddress
                    : call.Receiver is { } value ? Borrow(value) : _selfValue ?? (_selfAddress is { } at ? Hold(Emit(new LoadInst(at))) : null);
                if (self is null) {
                    EndAll(borrows);
                    return Missing(call, "the receiver of this call");
                }

                if (!callee.IsMutating) {
                    borrows.Add(self);
                }

                arguments.Add(self);
            }

            for (var i = 0; i < call.Arguments.Length; i++) {
                SilValue? argument;
                if (callee.Parameters[i].Modifier == ParameterModifier.InOut) {
                    argument = Address(call.Arguments[i], borrows);
                } else if (SilType.IsIndirect(type.Parameters[arguments.Count])) {
                    // Where it is, or, of a type the requirements in force make known here, in a slot of its own.
                    argument = GenericArgument(call.Arguments[i], Sub(call.Arguments[i].Type), type.Parameters[arguments.Count], borrows);
                } else if ((argument = Borrow(call.Arguments[i])) is not null) {
                    borrows.Add(argument);
                }

                if (argument is null) {
                    EndAll(borrows);
                    return null;
                }

                arguments.Add(argument);
            }

            // Self's metadata: what a metatype receiver holds, or the type parameter's.
            SilValue? metadata = call.Receiver is { Type: MetatypeTypeSymbol } and not BoundTypeExpression ? Expression(call.Receiver) : MetadataOf(parameter);
            if (metadata is null) {
                EndAll(borrows);
                return null;
            }

            // Where the result goes, when there is a place for it before the error's and the metadata.
            var errorOut = type.Thrown as AddressType;
            var result = arguments.Count < type.Parameters.Length - 1 - (errorOut is null ? 0 : 1) ? GenericOut(type.Parameters[arguments.Count], Sub(call.Type)) : null;
            if (result is { } into) {
                arguments.Add(into.Passed);
            }

            // An error of an associated type: where it goes.
            (SilValue Passed, AllocStackInst? Slot, bool CannotThrow)? error = null;
            if (errorOut is not null) {
                if (callee.ThrownType is not { } thrown || GenericErrorOut(errorOut, Through(thrown, protocol, parameter)) is not { } place) {
                    EndAll(borrows);
                    return Missing(call, "where this call's error goes");
                }

                error = place;
                arguments.Add(place.Passed);
            }

            arguments.Add(metadata);
            var invoked = Invoke(code, type, arguments, borrows, call.Syntax, cannotThrow: error is { CannotThrow: true }, errorSlot: error?.Slot);
            return result is { } written ? TakeOut(invoked, written.Slot) : invoked;
        }

        /// <summary>The type parameter of this function a property requirement is read on, or null for any other read.</summary>
        internal TypeSymbol? GenericPropertyOf(BoundFieldAccess access) {
            if (_genericMetadata.Count == 0 || access.Field.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol }) {
                return null;
            }

            var on = access.Receiver switch {
                BoundTypeExpression type => Sub(type.ReferencedType),
                { Type: MetatypeTypeSymbol metatype } => Sub(metatype.InstanceType),
                { } receiver => Sub(receiver.Type),
                null => null,
            };
            return on is not null && IsOpaqueHere(on) ? CanonicalHere(on) : null;
        }

        /// <summary>A property requirement read on a type parameter: its getter, from the table, given the value and the metadata.</summary>
        SilValue? GenericPropertyRead(BoundFieldAccess access, TypeSymbol parameter) {
            var field = access.Field;
            var protocol = ((NamedTypeSymbol)field.ContainingSymbol!).OriginalDefinition;
            var index = generator.GenericRequirements(protocol).FindIndex(r => ReferenceEquals(r.Member, field));
            if (index < 0 || generator.GenericEntryType(protocol, field) is not { } type) {
                return Missing(access, $"'{field.Name}' through a witness table");
            }

            var code = Emit(new BuiltinInst($"witness_generic:{index}", type, TableFor(parameter, protocol)));
            var arguments = new List<SilValue>();
            var borrows = new List<SilValue>();
            if (!field.IsStatic) {
                if (access.Receiver is null || Borrow(access.Receiver) is not { } self) {
                    return Missing(access, "the receiver of this property");
                }

                arguments.Add(self);
                borrows.Add(self);
            }

            var result = GenericOut(type.Parameters[arguments.Count], Sub(access.Type));
            if (result is { } into) {
                arguments.Add(into.Passed);
            }

            arguments.Add(MetadataOf(parameter)!);
            var invoked = Invoke(code, type, arguments, borrows, access.Syntax);
            return result is { } written ? TakeOut(invoked, written.Slot) : invoked;
        }

        /// <summary>
        ///     Whether a call of a generic function goes to its unspecialised
        ///     instance: when it has one and what the call binds each type
        ///     parameter to can be passed — a type parameter here, as it is, or
        ///     a type whose metadata and witness tables are made for it.
        /// </summary>
        bool CallsUnspecialised(FunctionSymbol callee, TypeSubstitution bound, SyntaxNode at, out GenericShape? shape) {
            shape = null;
            if (generator.UnspecialisedShape(callee) is not { } found) {
                return false;
            }

            foreach (var parameter in found.Parameters) {
                if (bound.Lookup(parameter) is not { } given) {
                    return false;
                }

                if (IsOpaqueHere(given)) {
                    continue;
                }

                // An array, an optional or a tuple of this function's type parameter's values: its metadata made at run time.
                if (given is not TypeParameterSymbol and not MetatypeTypeSymbol && IsHeld(given, [.._genericMetadata.Keys])) {
                    if (found.ProtocolsOf(parameter).Any()) {
                        return false;
                    }

                    continue;
                }

                if (Mentions(given, [.._genericMetadata.Keys]) || LowerType(given) is not { } lowered || lowered == SilType.Unit
                    || lowered is NominalType { IsNoncopyable: true } || given.Equals(Core.Never)) {
                    return false;
                }

                foreach (var protocol in found.ProtocolsOf(parameter)) {
                    if (!generator.GenericTable(given, protocol, at, generator.DescribesValues(callee))) {
                        return false;
                    }
                }
            }

            // What its 'where' clauses ask of an associated type, the
            // callee finds in the registry: the tables are made where the type is known.
            var extension = callee.ContainingSymbol as ExtensionSymbol ?? (callee.ContainingSymbol as FieldSymbol)?.ContainingSymbol as ExtensionSymbol;
            foreach (var (subject, constraint) in callee.ConformanceRequirements.Concat(extension?.ConformanceRequirements ?? [])) {
                var named = Sub(WithAssociatedTypes(callee, bound).Apply(subject));
                if (subject is TypeParameterSymbol direct && found.Parameters.Contains(direct)
                    || named is not (NamedTypeSymbol or ArrayTypeSymbol) || Mentions(named, [.._genericMetadata.Keys])) {
                    continue;
                }

                foreach (var protocol in CompositionTypeSymbol.PartsOf(constraint).OfType<NamedTypeSymbol>()) {
                    if (!generator.GenericTable(named, protocol.OriginalDefinition, at, generator.DescribesValues(callee))) {
                        return false;
                    }
                }
            }

            shape = found;
            return true;
        }

        /// <summary>
        ///     An argument for a parameter of a type parameter's type, to an
        ///     unspecialised callee, which borrows it where it is: a value of a
        ///     type parameter here as it is, and any other in a slot of its
        ///     own, seen as the callee's type parameter, until the call is done.
        /// </summary>
        SilValue? GenericArgument(BoundExpression argument, TypeSymbol given, SilType expected, List<SilValue> borrows) {
            if (IsOpaqueHere(given) || LowerType(given) is { } held && SilType.IsIndirect(held)) {
                if (Borrow(argument) is not { } value) {
                    return null;
                }

                borrows.Add(value);
                return SilType.SameErasingOpaque(value.Type, expected) ? value : Emit(new BuiltinInst("opaque_borrow_as", expected, value));
            }

            if (LowerType(given) is not { } type || Expression(argument) is not { } evaluated) {
                return null;
            }

            var slot = NewSlot(type);
            Emit(new StoreInst(Owned(evaluated), slot, StoreKind.Init));
            var borrowed = Emit(new LoadBorrowInst(Emit(new BuiltinInst("opaque_cast", SilType.Address(expected), slot))));
            borrows.Add(new WriteBack(() => {
                Emit(new EndBorrowInst(borrowed));
                if (!type.IsTrivial) {
                    Emit(new DestroyAddrInst(slot));
                }
            }));
            return borrowed;
        }

        /// <summary>True when a closure in unspecialised code has a type parameter's values in its type, its captures or its body.</summary>
        bool MentionsOpaqueHere(BoundClosure closureExpression) {
            IReadOnlyCollection<TypeParameterSymbol> own = [.._genericMetadata.Keys];
            return Mentions(closureExpression.Type, own) || generator.CapturesOf(closureExpression).Any(c => Mentions(c.Type, own))
                   || Descendants(closureExpression.Body).Any(n => n is BoundExpression { Type: var type } && Mentions(type, own)
                                                                  || n is BoundLocalDeclaration { Local.Type: var local } && Mentions(local, own));
        }

        /// <summary>
        ///     A closure over a type parameter's values in unspecialised code
        ///     (#297): its code an unspecialised function of its own, given the
        ///     metadata and tables this function is, called by a thunk — the
        ///     closure's code — that finds them in the context. The context
        ///     holds them after what the closure captures, and each value it
        ///     captures of a type held indirectly in an array of one, which
        ///     sizes it at run time and destroys it through its witnesses.
        /// </summary>
        SilValue? GenericClosure(BoundClosure closureExpression, ThickFunctionType thick, List<Capture> captures, string name) {
            var (enclosing, shape) = generator._unspecialised[function.Name];
            if (ContextType(closureExpression, captures, name) is not { } contextType) {
                return null;
            }

            var indirect = new List<(Capture Capture, SilType Type)>();
            foreach (var capture in captures) {
                var (fieldName, fieldType) = contextType.Fields[capture.Field];
                if (SilType.IsIndirect(fieldType)) {
                    contextType.Fields[capture.Field] = (fieldName, new ArrayType(fieldType));
                    indirect.Add((capture, fieldType));
                }
            }

            var hiddenAt = contextType.Fields.Count;
            foreach (var parameter in shape.Parameters) {
                contextType.Fields.Add(($"$metadata.{parameter.Name}", SilType.Metadata));
            }

            foreach (var (parameter, protocol) in shape.Requirements) {
                contextType.Fields.Add(($"$witnesses.{parameter.Name}.{protocol.Name}", SilType.WitnessTable));
            }

            // The code, generic as this function is.
            var line = source.GetLinePosition(closureExpression.Syntax.Span.Start).Line;
            var hidden = contextType.Fields.Skip(hiddenAt).Select(static f => f.Type).ToList();
            var bodyType = new FunctionType([..thick.Signature.Parameters, ..hidden, SilType.Object], thick.Signature.Result, thick.Signature.Thrown);
            var bodyName = $"{name}$generic";
            generator._unspecialised[bodyName] = (enclosing, shape);
            var code = new SilFunction(bodyName, bodyType) { SourceFile = function.SourceFile, Line = line };
            code.ParameterNames.AddRange(closureExpression.Symbol.Parameters.Select(static p => p.Name));
            var info = new ClosureInfo(contextType, captures.Where(c => !indirect.Exists(i => ReferenceEquals(i.Capture, c))).ToDictionary(static c => c.Key, static c => c)) {
                Indirect = indirect,
            };
            var lowering = new FunctionLowering(generator, closureExpression.Symbol, code, source, substitution, info);
            lowering.Lower(closureExpression.Body);
            if (lowering.Failed) {
                _failed = true;
                return null;
            }

            code.RemoveUnreachableBlocks();
            generator._module.Add(code);
            generator.AssociatedRecipes(code, enclosing, shape);
            MoveChecker.Check(code, source, generator._diagnostics);

            // The closure's code: the generic one, given what the context holds.
            var thunk = new SilFunction(name, thick.Implementation) { SourceFile = function.SourceFile, Line = line };
            var entry = thunk.AddBlock();
            var given = thick.Signature.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();
            var contextArgument = entry.AddArgument(SilType.Object);
            var holder = entry.Append(new UncheckedRefCastInst(contextArgument, contextType));
            for (var i = hiddenAt; i < contextType.Fields.Count; i++) {
                given.Add(entry.Append(new LoadInst(entry.Append(new RefElementAddrInst(holder, i)), LoadKind.Trivial)));
            }

            given.Add(contextArgument);
            var reference = entry.Append(new FunctionRefInst(bodyName, bodyType));
            if (bodyType.Thrown is null) {
                var result = entry.Append(new ApplyInst(reference, bodyType.Result, [..given]));
                entry.Append(new ReturnInst(bodyType.Result == SilType.Unit ? null : result));
            } else {
                var normal = thunk.AddBlock();
                var result = bodyType.Result == SilType.Unit ? null : normal.AddArgument(bodyType.Result);
                var failed = thunk.AddBlock();
                var error = failed.AddArgument(bodyType.Thrown);
                entry.Append(new TryApplyInst(reference, bodyType.Result, [..given], normal, failed));
                normal.Append(new ReturnInst(result));
                failed.Append(new ThrowInst(error));
            }

            generator._module.Add(thunk);

            // The context: what it captures, then the metadata and the tables.
            Locate(closureExpression.Syntax);
            var context = Hold(Emit(new AllocRefInst(contextType)));
            foreach (var capture in captures) {
                if (CapturedValue(capture, closureExpression) is not { } value) {
                    return null;
                }

                var field = Emit(new RefElementAddrInst(context, capture.Field));
                switch (((AddressType)field.Type).Pointee) {
                    case ArrayType array when indirect.Exists(i => ReferenceEquals(i.Capture, capture)):
                        Emit(new StoreInst(Emit(new BuiltinInst("array_literal", array, Owned(value))), field, StoreKind.Init));
                        break;

                    case WeakStorageType:
                        Emit(new StoreWeakInst(value, field));
                        Destroy(value);
                        break;

                    case UnownedStorageType:
                        Emit(new StoreUnownedInst(value, field));
                        Destroy(value);
                        break;

                    default:
                        Emit(new StoreInst(Owned(value), field, StoreKind.Assign));
                        break;
                }
            }

            var at = hiddenAt;
            foreach (var parameter in shape.Parameters) {
                Emit(new StoreInst(_genericMetadata[parameter], Emit(new RefElementAddrInst(context, at++)), StoreKind.Init));
            }

            foreach (var (parameter, protocol) in shape.Requirements) {
                Emit(new StoreInst(_genericTables[(parameter, protocol)], Emit(new RefElementAddrInst(context, at++)), StoreKind.Init));
            }

            return Emit(new PartialApplyInst(Emit(new FunctionRefInst(name, thick.Implementation)), Unhold(context), thick));
        }

        /// <summary>
        ///     In a closure compiled with unspecialised code, what it captured
        ///     of a type parameter's type, out of the array of one its context
        ///     holds it in, into a slot of its own: a local's, a parameter's,
        ///     read as any other.
        /// </summary>
        void UnpackIndirectCaptures() {
            if (closure is not { Indirect.Count: > 0 } || _context is null) {
                return;
            }

            foreach (var (capture, type) in closure.Indirect) {
                var array = Emit(new LoadInst(Emit(new RefElementAddrInst(_context, capture.Field))));
                var element = Emit(new BuiltinInst("array_get", type, array, Emit(new IntegerLiteralInst(SilType.Int, 0))));
                Emit(new DestroyValueInst(array));
                var name = capture.Key is Symbol { Name: { Length: > 0 } named } ? named : null;
                var slot = NewSlot(type, name);
                Emit(new StoreInst(element, slot, StoreKind.Init));
                switch (capture.Key) {
                    case LocalSymbol local:
                        _slots[local] = slot;
                        break;
                    case ParameterSymbol parameter:
                        _spilled[parameter] = slot;
                        break;
                }
            }
        }

        /// <summary>
        ///     A function for a parameter of a function type of the callee's
        ///     type parameters' values, which it calls in the generic
        ///     convention: a closure already of that convention as it is, and
        ///     any other given through a thunk that converts, over a copy of
        ///     it, until the call is done.
        /// </summary>
        SilValue? GenericFunctionArgument(BoundExpression argument, ThickFunctionType expected, List<SilValue> borrows) {
            if (Borrow(argument) is not { } value) {
                return null;
            }

            borrows.Add(value);
            if (value.Type == expected) {
                return value;
            }

            if (value.Type is ThickFunctionType given && SilType.SameErasingOpaque(given, expected)) {
                return Emit(new BuiltinInst("opaque_borrow_as", expected, value));
            }

            if (value.Type is not ThickFunctionType source || generator.Reabstraction(source, expected) is not var (thunk, context)) {
                return Missing(argument, $"a function of type '{value.Type}' where unspecialised code takes '{expected}'");
            }

            var holder = Emit(new AllocRefInst(context));
            Emit(new StoreInst(Emit(new CopyValueInst(value)), Emit(new RefElementAddrInst(holder, 0)), StoreKind.Init));
            var made = Hold(Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk, expected.Implementation)), holder, expected)));
            borrows.Add(made);
            return made;
        }

        /// <summary>
        ///     What a call binds its callee's type parameters to, with a
        ///     protocol extension's Self, which a call on this function's own
        ///     self leaves unsaid, bound to this function's Self.
        /// </summary>
        TypeSubstitution OwnSelfBound(FunctionSymbol callee, TypeSubstitution bound) {
            // A member of this function's own generic type called on its own
            // self, which says nothing of its type parameters: this function's.
            var unbound = TypeParametersOf(callee).Where(p => bound.Lookup(p) is null && IsOpaqueHere(p)).ToList();
            if (unbound.Count == 0) {
                return bound;
            }

            var map = bound.Parameters.ToDictionary(p => p, p => bound.Lookup(p)!);
            foreach (var parameter in unbound) {
                map[parameter] = parameter;
            }

            return new TypeSubstitution(map);
        }

        /// <summary>
        ///     A receiver of a type known here for an unspecialised callee
        ///     that knows it only as Self: its storage's address seen as Self's,
        ///     for one that changes it; else a copy in a slot of its own,
        ///     borrowed until the call is done.
        /// </summary>
        SilValue GenericReceiver(SilValue self, SilType expected, List<SilValue> borrows) {
            if (expected is AddressType) {
                return Emit(new BuiltinInst("opaque_cast", expected, self));
            }

            var slot = NewSlot(self.Type);
            Emit(new StoreInst(self.Type.IsTrivial ? self : Emit(new CopyValueInst(self)), slot, StoreKind.Init));
            var borrowed = Emit(new LoadBorrowInst(Emit(new BuiltinInst("opaque_cast", SilType.Address(expected), slot))));
            borrows.Add(new WriteBack(() => {
                Emit(new EndBorrowInst(borrowed));
                if (!slot.ElementType.IsTrivial) {
                    Emit(new DestroyAddrInst(slot));
                }
            }));
            return borrowed;
        }

        /// <summary>
        ///     The metadata and witness tables an unspecialised callee takes
        ///     after its own parameters: a type parameter's here as given, and
        ///     where the type is known, its constant metadata and the tables
        ///     made for it, referred to as they are — and its describer made,
        ///     for the callee to write one of its values out with.
        /// </summary>
        IEnumerable<SilValue>? GenericHiddenArguments(GenericShape shape, TypeSubstitution bound, SyntaxNode at, FunctionSymbol? callee = null) {
            var hidden = new List<SilValue>();
            foreach (var parameter in shape.Parameters) {
                var given = bound.Lookup(parameter)!;
                if (MetadataOf(given) is not { } metadata) {
                    return null;
                }

                if (!IsOpaqueHere(given) && LowerType(given) is { } lowered && !SilType.MentionsOpaque(lowered) && (callee is null || generator.DescribesValues(callee))) {
                    generator.Describer(given, lowered, at);
                }

                hidden.Add(metadata);
            }

            foreach (var (parameter, protocol) in shape.Requirements) {
                var given = bound.Lookup(parameter)!;
                hidden.Add(IsOpaqueHere(given)
                    ? TableFor(CanonicalHere(given), protocol)
                    : Emit(new BuiltinInst($"witness_table_known:{QualifiedName(protocol)}", SilType.WitnessTable, MetadataOf(given)!)));
            }

            return hidden;
        }
    }
}
