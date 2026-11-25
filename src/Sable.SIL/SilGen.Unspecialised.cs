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
///         them, an async function's awaits among them (its frame keeps the
///         metadata, <see cref="AsyncLowering" />). Anything else — a slice of
///         <c>T</c>, a method of a generic class — is still specialised for its type
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
    public static GenericsMode Generics => AmbientGenerics.Value ?? GenericsFromEnvironment;

    static readonly GenericsMode GenericsFromEnvironment =
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

    static readonly AsyncLocal<bool> AmbientOptimised = new();

    /// <summary>
    ///     Lowers for an optimised build, or not, until the handle is
    ///     disposed: one keeps the functions its generic types store in the
    ///     convention of their types (<see cref="StoredFunctionType" />), as
    ///     the specialisation pass would otherwise convert them at each use.
    /// </summary>
    public static IDisposable UseOptimised(bool optimised) {
        var before = AmbientOptimised.Value;
        AmbientOptimised.Value = optimised;
        return new Restore(() => AmbientOptimised.Value = before);
    }

    sealed class Restore(Action undo) : IDisposable {
        public void Dispose() => undo();
    }

    readonly GenericsMode _generics = Generics;

    /// <summary>
    ///     Whether a call has opened a <c>Type</c> (SYNTAX.md "Opening a type"):
    ///     from then on, in a specialised build too, generic functions are
    ///     compiled once where code that knows a type only by its metadata
    ///     calls them, and the tables made have their generic entries.
    /// </summary>
    bool _opening;

    /// <summary>Whether generic code compiled once is made: in an unspecialised build, or once a call has opened a Type.</summary>
    internal bool CompilesOnce => _generics == GenericsMode.Unspecialised || _opening;

    /// <summary>Notes that a call opens a Type: from now on, generic code is compiled once where it is called so.</summary>
    internal void Opening() => _opening = true;

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
            TypeParameterSymbol parameter => ParameterName(parameter),
            _ => type.Name,
        };

    /// <summary>
    ///     A type parameter's name in SIL: its own, or, for the one a
    ///     <c>some P</c> parameter stands for, <c>some1</c>, <c>some2</c>, …
    ///     in the order the function's parameters have them.
    /// </summary>
    internal static string ParameterName(TypeParameterSymbol parameter) =>
        parameter.OpaqueParameter is null ? parameter.Name : $"some{-parameter.Ordinal}";

    /// <summary>
    ///     A function's types as its code compiled once reads them: each
    ///     <c>some P</c> parameter's as the type parameter standing for it
    ///     (<see cref="FunctionSymbol.OpaqueTypeParameters" />), which the
    ///     substitution it is lowered with maps to itself.
    /// </summary>
    internal static TypeSubstitution OpaqueParametersAsThemselves(FunctionSymbol symbol) =>
        symbol.OpaqueTypeParameters.IsEmpty
            ? TypeSubstitution.Empty
            : new(symbol.OpaqueTypeParameters.ToDictionary(static p => p, static p => (TypeSymbol)p));

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
    void AssociatedRecipes(SilFunction function, Symbol symbol, GenericShape shape) {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        // A union may hold itself, in an array or a box of a case: each
        // nominal type is looked through once.
        var seen = new HashSet<NominalType>(ReferenceEqualityComparer.Instance);
        void Collect(SilType? type) {
            if (type is NominalType nominal && !seen.Add(nominal)) {
                return;
            }

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

                if (instruction is MetadataInst { Described: var described }) {
                    Collect(described);
                }
            }
        }

        var made = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names) {
            var steps = name.Split('.');
            if (shape.Parameters.FirstOrDefault(p => ParameterName(p) == steps[0]) is not { } root) {
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
                                table = $"$witnesses.{ParameterName(parameter)}.{protocol.Name}";
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
        /// <summary>
        ///     The tables, in order. Not one of Sendable's, which asks nothing
        ///     of a type at run time, and is met by what one is made of: no
        ///     record says which types do.
        /// </summary>
        public ImmutableArray<(TypeParameterSymbol Parameter, NamedTypeSymbol Protocol)> Requirements { get; init; } = [..Requirements.Where(static r => !IsMarker(r.Protocol))];

        public IEnumerable<NamedTypeSymbol> ProtocolsOf(TypeParameterSymbol parameter) =>
            Requirements.Where(r => ReferenceEquals(r.Parameter, parameter)).Select(static r => r.Protocol);
    }

    readonly Dictionary<FunctionSymbol, GenericShape?> _shapes = new(ReferenceEqualityComparer.Instance);

    /// <summary>Why each generic function that is not compiled once is not: what a call that opens a Type says of it (S3164).</summary>
    readonly Dictionary<FunctionSymbol, string> _refusals = new(ReferenceEqualityComparer.Instance);

    /// <summary>For each unspecialised function, whether it, or unspecialised code it calls, writes a value of a type it is given out.</summary>
    readonly Dictionary<FunctionSymbol, bool> _describes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     Whether an unspecialised callee may write out a value of a type it
    ///     is given (<c>generic_describe</c>), and so needs that type's
    ///     describer: as its body says, or, for one still being decided — a
    ///     recursion — yes.
    /// </summary>
    internal bool DescribesValues(FunctionSymbol callee) => _describes.GetValueOrDefault(callee, true);

    /// <summary>
    ///     For each unspecialised function, whether it, or unspecialised code
    ///     it calls, writes a value of a type it is given out with <c>?</c>
    ///     (<c>generic_debug_describe</c>), and so needs that type's debug
    ///     describer — which writes a value as <c>Debug</c> or
    ///     <c>Formattable</c> say, and so reaches code a describer does not.
    ///     A library's interface records it (<see cref="SilUnitInfo.DebugDescribing" />).
    /// </summary>
    readonly Dictionary<FunctionSymbol, bool> _debugDescribes = new(ReferenceEqualityComparer.Instance);

    internal bool DebugDescribesValues(FunctionSymbol callee) => _debugDescribes.GetValueOrDefault(callee, false);
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
        if (!CompilesOnce) {
            return null;
        }

        if (_shapes.TryGetValue(symbol, out var known)) {
            return known;
        }

        // A library's generic function: as the library compiled it, without
        // binding its body — compiled once in its archive, or not at all. A
        // specialised archive has none: what an opened call needs is made here.
        if (_generics == GenericsMode.Unspecialised && CompiledBy(symbol) is { } library) {
            var compiled = library.Functions.TryGetValue(_names[symbol], out var describes) ? SignatureShape(symbol, out _) : null;
            if (compiled is not null) {
                _describes[symbol] = describes;
                _debugDescribes[symbol] = library.DebugDescribing?.Contains(_names[symbol]) == true;
            }

            _shapes[symbol] = compiled;
            _shapeOrder.Add(symbol);
            return compiled;
        }

        if (SignatureShape(symbol, out var refused) is not { } shape) {
            if (refused is not null) {
                _refusals[symbol] = refused;
            }

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

    /// <summary>
    ///     The library whose archive compiled the generic functions of the
    ///     file declaring <paramref name="symbol" />, when that is not this
    ///     unit's (<see cref="SilUnit.Libraries" />); null otherwise.
    /// </summary>
    CompiledGenerics? CompiledBy(FunctionSymbol symbol) {
        if (_unit is not { Libraries.Count: > 0 } unit || !_declarations.TryGetValue(symbol, out var declared) || unit.Owns(declared.Model.SyntaxTree)
            || !_names.ContainsKey(symbol)) {
            return null;
        }

        foreach (var library in unit.Libraries) {
            if (library.Covers(declared.Model.SyntaxTree)) {
                return library;
            }
        }

        return null;
    }

    /// <summary>
    ///     The largest unspecialised function, in SIL instructions, whose body
    ///     a library's interface offers other units to copy and specialise
    ///     without its being marked <c>@inline</c> or <c>@inlinable</c>
    ///     (#297 stage 7).
    /// </summary>
    public const int InlinableSize = 120;

    /// <summary>
    ///     Whether other units may copy a library's generic function compiled
    ///     once, to specialise it for the types they know: it says so
    ///     (<c>@inlinable</c>, <c>@inline(always)</c>), or it is small and
    ///     not <c>@inline(never)</c>.
    /// </summary>
    static bool IsInlinable(FunctionSymbol symbol, SilFunction function) =>
        symbol.GetAttribute("inlinable") is not null
        || (symbol.GetAttribute("inline")?.Arguments is not { } inline
            ? function.Blocks.Sum(static b => b.Instructions.Count) <= InlinableSize
            : inline.Contains("always"));

    /// <summary>A protocol generic code reaches through a witness table: neither it nor one it refines generic.</summary>
    static bool IsTabledProtocol(NamedTypeSymbol protocol) =>
        protocol is { TypeKind: TypeKind.Protocol, IsConstructed: false } && protocol.Name != "AnyObject"
        && ProtocolClosure([protocol]).All(static p => p.TypeParameters.IsEmpty)
        && Bases(protocol).All(static b => !b.IsConstructed);

    /// <summary>Sendable, which has no witnesses.</summary>
    static bool IsMarker(NamedTypeSymbol protocol) => protocol is { Name: "Sendable", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } };

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
        && (function.ThrownType is not { } thrown || !Mentions(thrown, parameters)
            // An error of one is written where the caller says, after a result, and thrown as that address.
            || IsHeld(thrown, parameters) && thrown is TypeParameterSymbol or DependentMemberTypeSymbol);

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
    static bool IsPlainGenericClass(NamedTypeSymbol definition) {
        // A class asked about already, from one its fields refer to: 'Log<Repo<T>>', in Repo<T>.
        _plainClassesAsked ??= new(ReferenceEqualityComparer.Instance);
        if (!_plainClassesAsked.Add(definition)) {
            return true;
        }

        try {
            return HasPlainFields(definition);
        } finally {
            _plainClassesAsked.Remove(definition);
        }
    }

    [ThreadStatic] static HashSet<NamedTypeSymbol>? _plainClassesAsked;

    static bool HasPlainFields(NamedTypeSymbol definition) =>
        definition is { TypeKind: TypeKind.Class, IsNoncopyable: false, TypeParameters.IsEmpty: false }
        // A task is the runtime's, laid out for its result (TaskClass).
        && definition is not { Name: "Task", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }
        && !definition.TypeParameters.Any(static p => p.IsValue || p.AllowsNoncopyable)
        && definition.BaseTypes.OfType<NamedTypeSymbol>().All(static b => b.TypeKind != TypeKind.Class)
        && StoredFields(definition).All(f => f.ReferenceKind == default
                                             && (!MentionsDefinition(f.Type, definition) || f.Type is NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Class } other && !ReferenceEquals(other.OriginalDefinition, definition))
                                             && (IsStoredFunction(f.Type, definition) || !Mentions(f.Type, definition.TypeParameters) || ByReference(f.Type, definition.TypeParameters)
                                                 // One held by value: the object laid out at run time, from its arguments' metadata.
                                                 || IsHeld(f.Type, definition.TypeParameters) && f.Type is not MetatypeTypeSymbol));

    /// <summary>
    ///     A generic class's own initialiser, compiled once: its object made
    ///     from its class's metadata for the arguments, made at run time
    ///     (<c>sable_class_type</c>); that of a class with a deinit, which is
    ///     compiled once too, finds it by the class's name (<see cref="GenericDeinitThunk" />).
    /// </summary>
    bool IsClassInitializer(FunctionSymbol symbol) =>
        symbol is { FunctionKind: FunctionKind.Initializer, IsSynthesized: false } && Owner(symbol)?.OriginalDefinition is { TypeKind: TypeKind.Class } owner
        && IsPlainGenericClass(owner) && (DeinitOf(owner) is not { } deinit || UnspecialisedShape(deinit) is not null);

    static FunctionSymbol? DeinitOf(NamedTypeSymbol definition) => definition.Members.OfType<FunctionSymbol>().FirstOrDefault(static f => f.FunctionKind == FunctionKind.Deinitializer);

    /// <summary>A generic class's deinit: compiled once as a method is.</summary>
    static bool IsClassDeinit(FunctionSymbol symbol) =>
        symbol.FunctionKind == FunctionKind.Deinitializer && Owner(symbol)?.OriginalDefinition is { TypeKind: TypeKind.Class } owner && IsPlainGenericClass(owner);

    /// <summary>
    ///     What runs the deinit of a generic class's object whose class the
    ///     runtime made, when its arguments are known only there
    ///     (<see cref="SilModule.GenericDeinits" />): a function given the
    ///     object that reads the type arguments from its class's metadata, as
    ///     a template table's entry does, and calls the deinit compiled once.
    ///     Made with the class's initialiser, which makes such objects.
    /// </summary>
    void GenericDeinitThunk(NamedTypeSymbol owner, SyntaxNode at) {
        if (DeinitOf(owner) is not { } deinit || _module.GenericDeinits.ContainsKey(QualifiedName(owner)) || UnspecialisedShape(deinit) is not { } shape
            || UnspecialisedInstance(deinit, shape, at) is not var (name, type)) {
            return;
        }

        var stem = $"$template$deinit${Sanitise(QualifiedName(owner))}";
        _module.GenericDeinits[QualifiedName(owner)] = stem;
        var thunk = new SilFunction(stem, new FunctionType([type.Parameters[0]], SilType.Unit));
        var entry = thunk.AddBlock();
        var self = entry.AddArgument(type.Parameters[0]);
        var metadata = entry.Append(new BuiltinInst("object_metadata", SilType.Metadata, self));
        entry.Append(new ApplyInst(entry.Append(new FunctionRefInst(name, type)), SilType.Unit, [self, ..TemplateArguments(entry, metadata, shape)]));
        entry.Append(new ReturnInst(null));
        _module.Add(thunk);
    }

    /// <summary>A type that mentions type parameters only behind a reference: an array or a slice of held values, or a plain generic class of them.</summary>
    static bool ByReference(TypeSymbol type, IReadOnlyCollection<TypeParameterSymbol> parameters) =>
        IsArrayOf(type, parameters)
        || type is NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Class } named && IsPlainGenericClass(named.OriginalDefinition)
           && named.TypeArguments.All(a => !Mentions(a, parameters) || IsHeld(a, parameters) && a is not MetatypeTypeSymbol)
        // A plain generic struct that holds its own type parameters only behind references: '[Key: Int]'.
        || type is NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Struct } structure && IsPlainGeneric(structure.OriginalDefinition)
           && StoredFields(structure.OriginalDefinition).All(f => !Mentions(f.Type, structure.OriginalDefinition.TypeParameters) || IsStoredFunction(f.Type, structure.OriginalDefinition) || ByReference(f.Type, structure.OriginalDefinition.TypeParameters))
           && structure.TypeArguments.All(a => !Mentions(a, parameters) || IsHeld(a, parameters) && a is not MetatypeTypeSymbol);

    /// <summary>A field's or a payload's type: one of the definition's type parameters held, or one that mentions none, and no instance of the definition itself.</summary>
    static bool Layable(TypeSymbol type, NamedTypeSymbol definition) =>
        !MentionsDefinition(type, definition) && (!Mentions(type, definition.TypeParameters) || IsHeld(type, definition.TypeParameters) && type is not MetatypeTypeSymbol
                                                  || IsStoredFunction(type, definition));

    /// <summary>
    ///     A function type of a generic type's type parameters' values that a
    ///     field of it has: in an unoptimised build that compiles generics once, every
    ///     instance keeps it in the generic convention, of the type's own
    ///     parameters (<see cref="StoredFunctionType" />), as code that knows
    ///     the instance's arguments and code that does not both use it.
    /// </summary>
    static bool IsStoredFunction(TypeSymbol type, NamedTypeSymbol definition) =>
        type is FunctionTypeSymbol && Generics == GenericsMode.Unspecialised && !AmbientOptimised.Value && Mentions(type, definition.TypeParameters) && IsGenericFunction(type, definition.TypeParameters);

    readonly Dictionary<FieldSymbol, ThickFunctionType?> _storedFunctions = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     The type a field of a generic type keeps a function in, whatever
    ///     the instance's arguments (<see cref="IsStoredFunction" />): what
    ///     reads the field converts it to the type the function has there,
    ///     and what writes it converts one to this. Null for any other field,
    ///     and of a type no code compiled once holds.
    /// </summary>
    internal ThickFunctionType? StoredFunctionType(FieldSymbol field) {
        if (_storedFunctions.TryGetValue(field, out var known)) {
            return known;
        }

        ThickFunctionType? stored = null;
        if (_generics == GenericsMode.Unspecialised && field is { IsStatic: false, IsComputed: false, ReferenceKind: 0 } && field.Type is FunctionTypeSymbol
            && (field.ContainingSymbol is NamedTypeSymbol { TypeParameters.IsEmpty: false } owner ? owner.OriginalDefinition : null) is { } definition
            && IsStoredFunction(field.Type, definition) && IsPlainGeneric(definition)) {
            var own = definition.TypeParameters.ToDictionary(static p => p, TypeParameterSymbol.Stored);
            var map = new TypeSubstitution(own.ToDictionary(static p => p.Key, static p => (TypeSymbol)p.Value));
            using (Opaque(new GenericShape([..own.Values], []))) {
                stored = Lower(map.Apply(_compilation.CanonicalType(definition, field.Type))) as ThickFunctionType;
            }
        }

        _storedFunctions[field] = stored;
        return stored;
    }

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
    SilType? HeldOfSelf(TypeSymbol type, TypeParameterSymbol self, params ImmutableArray<TypeParameterSymbol> own) {
        if (type is MetatypeTypeSymbol or ArrayTypeSymbol || !IsHeld(type, [self, ..own])) {
            return null;
        }

        using (Opaque(new GenericShape([self, ..own], [])))
        using (AssociatedContext(self.ContainingSymbol)) {
            return Lower(type) is { } lowered && SilType.IsIndirect(lowered) ? lowered : null;
        }
    }

    /// <summary>An array of Self's values, or of its associated types', as a requirement gives one back: the array, whatever they are.</summary>
    SilType? ArrayOfSelf(TypeSymbol type, TypeParameterSymbol self, params ImmutableArray<TypeParameterSymbol> own) {
        if (type is not ArrayTypeSymbol { ArrayKind: not ArrayKind.Slice } || !IsArrayOf(type, [self, ..own])) {
            return null;
        }

        using (Opaque(new GenericShape([self, ..own], [])))
        using (AssociatedContext(self.ContainingSymbol)) {
            return Lower(type);
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
            : symbol.FunctionKind != FunctionKind.Ordinary && !IsOwnOperator(symbol) && !IsGetter(symbol) && !IsSubscriptAccessor(symbol) && !IsValueInitializer(symbol) && !IsClassInitializer(symbol) && !IsClassDeinit(symbol) ? $"{(symbol.FunctionKind.ToString()[0] is 'A' or 'E' or 'I' or 'O' or 'U' ? "an" : "a")} {symbol.FunctionKind.ToString().ToLowerInvariant()}"
            : symbol.ForeignName is not null ? "foreign"
            : symbol.OpaqueTypeParameters.Any(static p => !p.OpaqueParameter!.Arguments.IsEmpty) ? "a 'some P<…>' parameter"
            : _compilation.GetGenerator(symbol) is not null ? "a generator"
            : _compilation.GetLazyBody(symbol) is not null
              || _compilation.GetInheritedInitializerBody(symbol) is not null ? "a body the compiler writes"
            : null;
        if (refused is not null) {
            return null;
        }

        var requirements = new List<(TypeParameterSymbol, NamedTypeSymbol)>();
        var own = new List<TypeParameterSymbol>();
        var owner = Owner(symbol)?.OriginalDefinition;
        // One whose body names them ('static fn kind() => "box of \(T.kind())"') is generic over them, given their metadata by its callers.
        var independent = owner is not null && IsIndependentStatic(symbol, owner)
            && !(_declarations.TryGetValue(symbol, out var declaredStatic) && BodyOf(symbol, declaredStatic.Syntax, declaredStatic.Model) is { } staticBody
                && (NamesOwnerParameters(staticBody, owner) || NamesOwnerParameterAsType(staticBody, owner)));
        if (independent) {
            // A static member of a generic type that names none of its type
            // parameters — 'Task.sleep', 'Task.isCancelled' — is generic over
            // its own alone, whatever its type's are (its body is checked to
            // name none either).
        } else if (owner is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self }) {
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

        // A 'some P' parameter is an anonymous type parameter, after the named ones.
        own.AddRange(symbol.TypeParameters);
        own.AddRange(symbol.OpaqueTypeParameters);
        if (!(independent ? all.Skip(owner!.TypeParameters.Length) : all).SequenceEqual(own)) {
            refused = "type parameters from around it";
            return null;
        }

        foreach (var parameter in own.Where(p => owner is not { TypeKind: TypeKind.Protocol } || !ReferenceEquals(p, owner.SelfTypeParameter))) {
            if (parameter is { IsValue: true } or { AllowsNoncopyable: true }) {
                refused = $"the type parameter '{parameter.Name}'";
                return null;
            }

            // A 'some P''s protocols are what it says.
            IEnumerable<TypeSymbol> constraints = parameter.OpaqueParameter is { } opaque ? [opaque.Constraint] : parameter.Constraints;
            foreach (var part in constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c))) {
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

        var asThemselves = OpaqueParametersAsThemselves(symbol);
        foreach (var parameter in symbol.Parameters) {
            var parameterType = asThemselves.IsEmpty ? parameter.Type : Substitute(asThemselves, parameter.Type);

            // 'using generator: inout G' is the address of the caller's storage, as any inout parameter is.
            // So is an array of them: the buffer's reference, the same whatever they are.
            var inoutOwn = parameter.Modifier == ParameterModifier.InOut && (parameterType is TypeParameterSymbol changed && own.Contains(changed) || IsArrayOf(parameterType, own));
            if (!Passable(parameterType) && !IsGenericFunction(parameterType, own)
                || Mentions(parameterType, own) && !inoutOwn && (parameter.IsVariadic || parameter.IsByReference || parameter.Modifier is not (ParameterModifier.None or ParameterModifier.In))) {
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

    /// <summary>
    ///     A static member of a generic class, struct or union — a method or
    ///     a property's accessor — whose signature names none of its type's
    ///     type parameters: compiled once without their metadata, which
    ///     nothing it does needs (<see cref="BodyAllows" /> checks its body).
    /// </summary>
    static bool IsIndependentStatic(FunctionSymbol symbol, NamedTypeSymbol owner) =>
        owner is { TypeKind: TypeKind.Class or TypeKind.Struct or TypeKind.Union, TypeParameters.IsEmpty: false, ExtendedArray: null, ExtendedOptional: null }
        && (symbol is { IsStatic: true, FunctionKind: FunctionKind.Ordinary } || symbol is { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FieldSymbol { IsStatic: true } })
        && !symbol.Parameters.Any(p => Mentions(p.Type, owner.TypeParameters)) && !Mentions(symbol.ReturnType, owner.TypeParameters)
        && (symbol.ThrownType is not { } thrown || !Mentions(thrown, owner.TypeParameters));

    /// <summary>Whether a body names any of its type's type parameters but as the type a static member is called on (<c>Task.yield()</c>).</summary>
    static bool NamesOwnerParameters(BoundNode node, NamedTypeSymbol owner) =>
        node is not BoundTypeExpression
        && (node is BoundExpression { Type: var type } && Mentions(type, owner.TypeParameters)
            || node is BoundLocalDeclaration { Local.Type: var local } && Mentions(local, owner.TypeParameters)
            || node.Children.Any(child => NamesOwnerParameters(child, owner)));

    /// <summary>Whether a body names one of its type's type parameters itself as a type — <c>T.kind()</c>, <c>T.self</c> — which only its metadata can answer.</summary>
    static bool NamesOwnerParameterAsType(BoundNode node, NamedTypeSymbol owner) =>
        node is BoundTypeExpression { ReferencedType: TypeParameterSymbol parameter } && owner.TypeParameters.Contains(parameter)
        || node is BoundCall { Receiver: null, Substitution: var substitution } && substitution.Parameters.Any(p => substitution.Apply(p) is TypeParameterSymbol bound && owner.TypeParameters.Contains(bound))
        || node.Children.Any(child => NamesOwnerParameterAsType(child, owner));

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
        symbol is { FunctionKind: FunctionKind.Initializer, IsSynthesized: false } && Owner(symbol)?.OriginalDefinition is { TypeKind: TypeKind.Struct, ExtendedArray: null, ExtendedOptional: null };

    /// <summary>An operator a generic type, or an extension of an optional or an array, declares with a body: a static member of it.</summary>
    static bool IsOwnOperator(FunctionSymbol symbol) =>
        symbol.FunctionKind == FunctionKind.Operator && Owner(symbol)?.OriginalDefinition is { TypeKind: not TypeKind.Protocol } owner
        && (!owner.TypeParameters.IsEmpty || owner.ExtendedArray is not null || owner.ExtendedOptional is not null);

    /// <summary>What a call of a function gives: its result, or what an initialiser makes; null for one of no type.</summary>
    static TypeSymbol? ResultOf(FunctionSymbol symbol) => symbol.FunctionKind == FunctionKind.Initializer ? SelfType(symbol, TypeSubstitution.Empty) : symbol.ReturnType;

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

    /// <summary>A function's body, bound: as it is written, or the one <c>@derive</c> made.</summary>
    BoundBlock? BodyOf(FunctionSymbol symbol, SyntaxNode syntax, SemanticModel model) =>
        _compilation.GetDerivedBody(symbol) ?? BodyModel(model, syntax).GetBoundNode(syntax) as BoundBlock;

    bool BodyAllows(FunctionSymbol symbol, GenericShape shape) {
        var (syntax, model) = _declarations[symbol];
        if (BodyOf(symbol, syntax, model) is not { } body) {
            return false;
        }

        // A function that only hands its parameters to a requirement —
        // 'sin(x) => T.sin(x)' — stays specialised: each copy is the
        // requirement's witness called directly, which becomes an intrinsic
        // or a library call where the type is known, and costs nothing.
        if (!_opening && IsForwarder(body)) {
            _refusals[symbol] = "it only hands its parameters to a requirement, so it is copied for each type";
            if (Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 }) {
                Console.Error.WriteLine($"specialised {TraceName(symbol)}: a forwarder to a requirement");
            }

            return false;
        }

        // One that names its type's type parameters is given their metadata after all: it stays specialised.
        if (Owner(symbol)?.OriginalDefinition is { } independentOwner && IsIndependentStatic(symbol, independentOwner) && NamesOwnerParameters(body, independentOwner) && !shape.Parameters.Any(p => independentOwner.TypeParameters.Contains(p))) {
            if (Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 }) {
                Console.Error.WriteLine($"specialised {TraceName(symbol)}: a static member naming its type's type parameters");
            }

            return false;
        }

        var checker = new ShapeChecker(this, shape, symbol);
        var initialValues = (IsValueInitializer(symbol) || IsClassInitializer(symbol)) && Owner(symbol) is { } built
            ? built.OriginalDefinition.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false, HasInitializer: true })
                .Select(FieldInitializer).OfType<BoundExpression>().ToList()
            : [];
        if (initialValues.TrueForAll(v => checker.Allows(v, false)) && checker.Allows(body, false)) {
            _describes[symbol] = checker.Describes;
            _debugDescribes[symbol] = checker.DebugDescribes;
            if (Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 }) {
                Console.Error.WriteLine($"unspecialised {TraceName(symbol)}");
            }

            return true;
        }

        if (checker.Refused is { } why) {
            _refusals[symbol] = $"'{why.Syntax.ToString().Trim().ReplaceLineEndings(" ")}' in its body";
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

        /// <summary>Maps each 'some P' parameter's type to the type parameter standing for it.</summary>
        readonly TypeSubstitution _asThemselves = OpaqueParametersAsThemselves(checkedSymbol);

        /// <summary>A type as the body's code compiled once reads it: a 'some P' parameter's as its type parameter.</summary>
        TypeSymbol V(TypeSymbol type) => _asThemselves.IsEmpty ? type : Substitute(_asThemselves, type);

        bool Mentions(TypeSymbol type) => SilGen.Mentions(V(type), _own);

        bool IsOwn(TypeSymbol type) => V(type) is var seen && (seen is TypeParameterSymbol parameter && _own.Contains(parameter) || IsAssociatedOf(seen, _own));

        /// <summary>The protocols, with those they refine, a type parameter or an associated type is known here to conform to.</summary>
        List<NamedTypeSymbol> Known(TypeSymbol type) => ProtocolClosure(generator.ProtocolsKnown(checkedSymbol, shape, V(type)));

        TypeSymbol Canonical(TypeSymbol type) => generator._compilation.CanonicalType(checkedSymbol, V(type));

        /// <summary>Whether a requirement's type, read through the type it is called on, is the type given, as the requirements in force here read both.</summary>
        bool SameThrough(TypeSymbol declared, NamedTypeSymbol protocol, TypeSymbol on, TypeSymbol given) {
            var through = Through(declared, protocol, V(on));
            return through.Equals(V(given)) || Canonical(through).Equals(Canonical(given));
        }

        /// <summary>
        ///     Whether a call of a requirement whose type mentions Self or its
        ///     associated types gives or takes a value this function holds:
        ///     each part one of its type parameters, an associated type, or a
        ///     type that mentions neither.
        /// </summary>
        bool HeldThrough(TypeSymbol declared, NamedTypeSymbol protocol, TypeSymbol on) => Simple(Canonical(Through(declared, protocol, V(on))));

        /// <summary>A type a value may have here: a type parameter, its metatype, or one that mentions none.</summary>
        bool Simple(TypeSymbol type) => !Mentions(type) || IsHeld(V(type), _own) || IsGenericFunction(V(type), _own);

        bool IsOwnArray(TypeSymbol type) => IsArrayOf(V(type), _own);

        /// <summary>The innermost node that kept the body from being unspecialised, for SABLE_GENERICS_TRACE.</summary>
        public BoundNode? Refused { get; private set; }

        /// <summary>Whether the body, or unspecialised code it calls, writes a value of a type parameter's type out: its callers make describers.</summary>
        public bool Describes { get; private set; }

        /// <summary>Whether it writes one out with <c>?</c>: its callers make debug describers too.</summary>
        public bool DebugDescribes { get; private set; }

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
            if (closure.IsAutoclosure || generator._compilation.GetGenerator(closure.Symbol) is not null
                || Mentions(closure.Type) && !IsGenericFunction(V(closure.Type), _own)
                || closure.Type is FunctionTypeSymbol { ThrownType: { } closureThrown } && Mentions(Canonical(closureThrown))) {
                return false;
            }

            // What it captures by value is what lowering finds held by value, its type parameters opaque.
            List<Capture> captures;
            using (generator.Opaque(shape))
            using (generator.AssociatedContext(checkedSymbol)) {
                captures = generator.CapturesOf(closure);
            }

            return captures.All(c => !Mentions(c.Type) || c is { Boxed: false, Kind: CaptureKind.Default or CaptureKind.ByValue, Key: LocalSymbol or ParameterSymbol }
                && (IsHeld(V(c.Type), _own) && c.Type is not MetatypeTypeSymbol || IsGenericFunction(V(c.Type), _own))
                // A 'var' it shares, in a box laid out at run time.
                || c is { Boxed: true, Kind: CaptureKind.Default, Key: LocalSymbol } && IsHeld(V(c.Type), _own) && c.Type is not MetatypeTypeSymbol
                // Its self: an object, or a copy of a value.
                || c is { Boxed: false, Kind: CaptureKind.Default or CaptureKind.ByValue, Key: SelfKey } && IsHeld(V(c.Type), _own));
        }

        /// <summary>
        ///     What a node reached through an optional, <c>x?.member</c>, is
        ///     reached from, and what it is of the value the optional wraps: as
        ///     lowering takes it apart (<see cref="FunctionLowering.OptionalChain" />).
        ///     Null for any other node.
        /// </summary>
        (BoundExpression From, BoundExpression Through)? Chained(BoundNode node) {
            bool Reached(BoundExpression receiver, Symbol member) =>
                receiver.Type is OptionalTypeSymbol && !(member.ContainingSymbol is NamedTypeSymbol owner && ReferenceEquals(owner.OriginalDefinition, generator.Core.Optional))
                && member.ContainingSymbol is not ExtensionSymbol { ExtendedType.ExtendedOptional: not null };

            BoundExpression Wrapped(BoundExpression receiver) {
                var type = ((OptionalTypeSymbol)receiver.Type).UnderlyingType;
                return new BoundParameterAccess(receiver.Syntax, ParameterSymbol.Synthetic("$chained", generator.Core.Module, type));
            }

            switch (node) {
                case BoundFieldAccess { Receiver: { } from, Type: OptionalTypeSymbol reached } access when Reached(from, access.Field):
                    return (from, new BoundFieldAccess(access.Syntax, Wrapped(from), access.Field, access.FlattensChain ? reached : reached.UnderlyingType));
                case BoundConversion { ConversionKind: ConversionKind.OptionalWrap, Operand: BoundCall { Receiver: { } from } call } when Reached(from, call.Function):
                    return (from, new BoundCall(call.Syntax, Wrapped(from), call.Function, call.Arguments, call.Type) { Substitution = call.Substitution });
                case BoundCall { Receiver: { } from, Type: OptionalTypeSymbol } call
                    when Reached(from, call.Function) && call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { OperatorToken.Kind: SyntaxKind.QuestionDotToken } }:
                    return (from, new BoundCall(call.Syntax, Wrapped(from), call.Function, call.Arguments, call.Type) { Substitution = call.Substitution });
                default:
                    return null;
            }
        }

        bool AllowsHere(BoundNode node, bool inClosure) {
            if (Chained(node) is var (from, through) && (Mentions(from.Type) || node is BoundExpression { Type: var chainedType } && Mentions(chainedType))) {
                return !inClosure && Simple(from.Type) && Simple(((BoundExpression)node).Type) && (!Mentions(from.Type) || Produces(from)) && Allows(from, false) && Allows(through, false);
            }

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

            // A value given a callee's 'some P' parameter is the value, of the type it is: the call says what that type is.
            if (node is BoundConversion { ConversionKind: ConversionKind.Existential, Type: OpaqueTypeSymbol { Origin: null } } opened) {
                return !inClosure && Simple(opened.Operand.Type) && Allows(opened.Operand, false);
            }

            // A type made of them as a value, 'T[].self', 'T?.self', 'Box<T>.self': its metadata, made at run time from theirs.
            if (node is BoundMetatype { InstanceType: var described } && Mentions(described) && described is not MetatypeTypeSymbol && IsHeld(V(described), _own)) {
                return !inClosure;
            }

            if (node is BoundExpression expression && Mentions(expression.Type) && (inClosure || !Simple(expression.Type) || !Produces(expression))) {
                return false;
            }

            switch (node) {
                case BoundLocalDeclaration declaration when Mentions(declaration.Local.Type) && (inClosure || !Simple(declaration.Local.Type)):
                case BoundPatternDeclaration pattern when Descendants(pattern).Any(n => n is BoundBindingPattern binding && Mentions(binding.Local.Type) && (inClosure || !Simple(binding.Local.Type))):
                case BoundBindingPattern binding when Mentions(binding.Local.Type) && (inClosure || !Simple(binding.Local.Type)):
                case BoundConditionBinding { Local: { } bound } when Mentions(bound.Type) && (inClosure || !Simple(bound.Type)):
                case BoundTypeTest test when Mentions(test.TestedType) || Mentions(test.Operand.Type) && (inClosure || !IsTestable(test.TestedType) || !IsOwn(test.Operand.Type)):
                case BoundForEach { Element: { } element } loop when Mentions(element.Type)
                                                                    && (inClosure || !IsOwnArray(loop.Sequence.Type) || !Simple(element.Type) || loop.Sequence is not (BoundLocalAccess or BoundParameterAccess or BoundSelfAccess or BoundFieldAccess { Receiver: BoundSelfAccess or BoundLocalAccess or BoundParameterAccess })):
                    return false;
                case BoundCall call when CallMentions(call) && (inClosure || !CallAllowed(call)):
                    return false;
                case BoundIndirectCall { Callee.Type: var called } when V(called) is FunctionTypeSymbol { ThrownType: { } thrownThere } && Mentions(thrownThere) && !ThrowsAllowed(thrownThere):
                    return false;
                // A protocol's property set on a type parameter's value: a call of its setter's entry.
                case BoundAssignment { Target: BoundFieldAccess { Receiver: { } setOn, Field: { ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } } setField } } setting when Mentions(setOn.Type):
                    return !inClosure && generator.SetterOf(setField) is { } fieldSetter
                           && RequirementAllowed(new BoundCall(setting.Syntax, setOn, fieldSetter, [setting.Value], generator.Core.Void))
                           && Allows(setOn, false) && Allows(setting.Value, false);
                // 'x == none' tests x's case, borrowed where it is: the 'none',
                // and x's view as the type the comparison reads it as, are nothing more.
                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual, Method: null } none
                    when (Mentions(none.Left.Type) || Mentions(none.Right.Type)) && (IsNone(none.Right) ? none.Left : IsNone(none.Left) ? none.Right : null) is { } tested:
                    tested = Viewed(tested);
                    return !inClosure && Simple(tested.Type) && Produces(tested) && Allows(tested, false);
                case BoundBinaryOperator binary when (Mentions(binary.Left.Type) || Mentions(binary.Right.Type)) && (inClosure || !BinaryAllowed(binary)):
                    return false;
                case BoundUnaryOperator { OperatorKind: not (UnaryOperatorKind.Await or UnaryOperatorKind.Move) } unary when Mentions(unary.Operand.Type)
                                                   && (inClosure || unary.Method is not { } method || !OperatorAllowed(method, unary.Operand, [unary.Operand], unary)):
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
                if (handles) {
                    _handlers.Push((node, _closureDepth));
                }

                var allowed = Allows(child, inClosure);
                if (handles) {
                    _handlers.Pop();
                }

                if (!allowed) {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The handlers of errors the node being checked is inside, a 'do' with catches or a 'try?', and how many closures each is in.</summary>
        readonly Stack<(BoundNode Node, int Depth)> _handlers = [];

        /// <summary>Whether the innermost handler takes an error of this type as it is: its catches', or any, for a 'try?'.</summary>
        bool Handled(TypeSymbol thrown) =>
            _handlers.Peek() is var (node, depth) && depth == _closureDepth && IsOwn(Canonical(thrown)) && node switch {
                BoundDo { Catches: [var first, ..] } => (first.ErrorLocal?.Type ?? first.Pattern?.InputType) is { } taken && Canonical(taken).Equals(Canonical(thrown))
                                                        && ((BoundDo)node).Catches.All(static c => c.Pattern is null),
                BoundTry { ErrorLocal: null } => true,
                _ => false,
            };

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

            if (_handlers.Count > 0) {
                return Handled(thrown);
            }

            if (_closureDepth > 0 || checkedSymbol.ThrownType is not { } own) {
                return false;
            }

            // Thrown on as it is, written where this function's caller says;
            // or, of a type parameter's or an associated type's, boxed as the
            // 'any Error' this function throws, by its type's eraser.
            return Canonical(own).Equals(Canonical(thrown))
                   || IsOwn(Canonical(thrown)) && own is ExistentialTypeSymbol { Constraint: var constraint } && constraint.Equals(generator.Core.Error);
        }

        static bool IsNone(BoundExpression expression) =>
            expression switch {
                BoundLiteral { Value: null } => true,
                BoundConversion conversion => IsNone(conversion.Operand),
                _ => false,
            };

        /// <summary>A value seen as a type that is the same as its own — an associated type named through another path, 'S.Iter.Element' as 'S.Element': the value.</summary>
        BoundExpression Viewed(BoundExpression expression) =>
            expression is BoundConversion { ConversionKind: ConversionKind.Identity, Operand: var operand } conversion && Canonical(conversion.Type).Equals(Canonical(operand.Type))
                ? Viewed(operand)
                : expression;

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
                BoundCall or BoundMetatype or BoundTypeExpression or BoundConditional or BoundAssignment or BoundDiscard or BoundTry => true,
                // A call of a function of them, in the generic convention.
                BoundIndirectCall { Callee.Type: var calleeType } => IsGenericFunction(V(calleeType), _own),
                BoundBinaryOperator { Method: not null } or BoundUnaryOperator { Method: not null } => true,
                // 'move x': a local's value, taken.
                BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Move, Operand: BoundLocalAccess or BoundParameterAccess } => true,
                // An await of a call: the call, which the task may suspend in.
                BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Await, Operand: var awaited } => Produces(awaited),
                BoundFieldAccess access => PropertyAllowed(access),
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion when conversion.Type.Equals(conversion.Operand.Type) => true,
                // A closure that throws nothing where one that may throw is taken.
                BoundConversion { ConversionKind: ConversionKind.Upcast } upcast => IsGenericFunction(V(upcast.Type), _own) && IsGenericFunction(V(upcast.Operand.Type), _own),
                // An array or a slice of them, seen as another kind of array of them.
                BoundConversion { ConversionKind: ConversionKind.Identity } view when IsOwnArray(view.Type) && IsOwnArray(view.Operand.Type) => Produces(view.Operand),
                BoundArray array => IsOwnArray(array.Type),
                BoundIndexAccess { Subscript: null, Indices: [_] } index => IsOwnArray(index.Receiver.Type),
                BoundIndexAccess { Subscript: not null } subscripted => SubscriptAllowed(subscripted, null),
                // An optional or a tuple of them, made, unwrapped, taken apart and chosen between.
                BoundConversion { ConversionKind: ConversionKind.OptionalWrap } wrap => Simple(wrap.Operand.Type),
                BoundConversion { ConversionKind: ConversionKind.Explicit, Operand.Type: OptionalTypeSymbol { UnderlyingType: var unwrapped } } force => unwrapped.Equals(force.Type),
                // 'lowerBound as! C.Index': the value, when its metadata is the other's.
                BoundConversion { ConversionKind: ConversionKind.Explicit } cast when IsOpaqueCast(cast) || IsOpenedCast(cast) => true,
                // A range of them, a generic struct.
                BoundRange range => Simple(range.Type),
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
                BoundLocalDeclaration or BoundPatternDeclaration or BoundExpressionStatement or BoundDiscard or BoundReturn => true,
                BoundAssignment assignment => ReferenceEquals(child, assignment.Value)
                                              || assignment.Target is BoundLocalAccess or BoundParameterAccess && ReferenceEquals(child, assignment.Target)
                                              || assignment.Target is BoundFieldAccess { Receiver: BoundSelfAccess or BoundLocalAccess { Local.IsMutable: true } } stored
                                              && ReferenceEquals(child, assignment.Target) && PropertyAllowed(stored)
                                              // A part of an element, 'nodes[i].value = v': read out, changed and written back.
                                              || assignment.Target is BoundFieldAccess { Receiver: BoundIndexAccess { Subscript: null, Indices: [_], Receiver: var parts }, Field: { IsComputed: false, IsStatic: false } } part
                                              && ReferenceEquals(child, part) && IsOwnArray(parts.Type) && PropertyAllowed(part) && Changeable(parts)
                                              || assignment.Target is BoundSelfAccess && OwnMutating && ReferenceEquals(child, assignment.Target)
                                              || assignment.Target is BoundIndexAccess { Subscript: not null } stores && ReferenceEquals(child, assignment.Target) && SubscriptAllowed(stores, assignment.Value)
                                              || assignment.Target is BoundIndexAccess { Subscript: null, Indices: [_], Receiver: var array } && IsOwnArray(array.Type) && ReferenceEquals(child, assignment.Target)
                                              && (array is BoundLocalAccess { Local.IsMutable: true } || array is BoundParameterAccess { Parameter.Modifier: ParameterModifier.InOut } && _closureDepth == 0 || array is BoundFieldAccess { Receiver: BoundSelfAccess } && FieldsMutable
                                                  || array is BoundSelfAccess && OwnMutating),
                BoundCall or BoundBinaryOperator or BoundUnaryOperator or BoundTry => true,
                BoundIndirectCall indirect => IsGenericFunction(V(indirect.Callee.Type), _own) && (ReferenceEquals(child, indirect.Callee) ? indirect.Callee is BoundParameterAccess or BoundLocalAccess || indirect.Callee is BoundFieldAccess { Field: { IsComputed: false, IsStatic: false } } kept && PropertyAllowed(kept) : true),
                BoundFieldAccess access => ReferenceEquals(child, access.Receiver) && PropertyAllowed(access),
                BoundConditional conditional => !ReferenceEquals(child, conditional.Condition),
                BoundConversion { ConversionKind: ConversionKind.Identity } => true,
                BoundConversion { ConversionKind: ConversionKind.Upcast } upcast => IsGenericFunction(V(upcast.Type), _own),
                BoundArray or BoundTuple or BoundMatchArm or BoundIf or BoundRecordExpression or BoundUnionCaseConstruction or BoundDictionary => true,
                BoundTupleElementAccess element => ReferenceEquals(child, element.Receiver),
                BoundMatch match => ReferenceEquals(child, match.Subject),
                BoundConditionBinding binding => ReferenceEquals(child, binding.Source),
                BoundConversion { ConversionKind: ConversionKind.OptionalWrap } => true,
                BoundConversion { ConversionKind: ConversionKind.Explicit, Operand.Type: OptionalTypeSymbol } => true,
                BoundConversion { ConversionKind: ConversionKind.Explicit } cast => IsOpaqueCast(cast) || IsErasure(cast),
                BoundConversion { ConversionKind: ConversionKind.Existential } erasure => IsErasure(erasure),
                BoundTypeTest test => IsOwn(test.Operand.Type) && IsTestable(test.TestedType),
                BoundRange => true,
                BoundIndexAccess { Subscript: not null } => true,
                BoundIndexAccess index => ReferenceEquals(child, index.Receiver),
                BoundForEach loop => ReferenceEquals(child, loop.Sequence),
                // An error of the type this function throws, out of it: written where its caller says.
                BoundThrow => _handlers.Count == 0 && _closureDepth == 0 && checkedSymbol.ThrownType is { } own && Canonical(own).Equals(Canonical(child.Type)),
                // Not inside a protocol's own 'debugDescription' (Error's
                // '"\(self)"'): a describer would call it back.
                BoundInterpolatedString interpolated => interpolated.Parts.IndexOf(child) is var at and >= 0
                                                        && (interpolated.Formats[at] is null || interpolated.Formats[at]!.Specifier == DebugFormat && (DebugDescribes = true)) && !DescribesSelf && (Describes = true),
                _ => false,
            };

        /// <summary>An array this code may change where it is: a local, an inout parameter, a stored field of its self.</summary>
        bool Changeable(BoundExpression array) =>
            array is BoundLocalAccess { Local.IsMutable: true } || array is BoundParameterAccess { Parameter.Modifier: ParameterModifier.InOut } && _closureDepth == 0
            || array is BoundFieldAccess { Receiver: BoundSelfAccess, Field: { IsComputed: false, IsStatic: false } } stored && FieldsMutable && PropertyAllowed(stored)
            || array is BoundSelfAccess && OwnMutating;

        bool DescribesSelf => checkedSymbol is { Name: "debugDescription" } && OwnerOf(checkedSymbol) is { TypeKind: TypeKind.Protocol };

        /// <summary>A type known here that a value's dynamic type can be asked about: a class, a struct, a protocol that is not generic.</summary>
        bool IsTestable(TypeSymbol type) =>
            !Mentions(type) && type switch {
                NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol => protocol is { IsConstructed: false, TypeParameters.IsEmpty: true } && !IsMarker(protocol) && !protocol.Equals(generator.Core.Error),
                NamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct or TypeKind.Enum or TypeKind.Union or TypeKind.Builtin } named => !named.IsNoncopyable && !named.Equals(generator.Core.Never),
                ExistentialTypeSymbol { MetatypeOf: null, Constraint: var constraint } => CompositionTypeSymbol.PartsOf(constraint).All(IsTestable),
                _ => false,
            };

        /// <summary>
        ///     A value of one of the function's opaque types as an existential
        ///     — of nothing, or of protocols it is known to conform to — or a
        ///     cast of one to an existential of any that are not generic
        ///     (<see cref="FunctionLowering.OpaqueAsExistential" />).
        /// </summary>
        bool IsErasure(BoundConversion erasure) {
            if (!IsOwn(erasure.Operand.Type) || _closureDepth > 0) {
                return false;
            }

            var target = erasure is { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } }
                ? erasure.Type is OptionalTypeSymbol { UnderlyingType: var wrapped } ? wrapped : erasure.Type
                : erasure.ConversionKind == ConversionKind.Existential ? erasure.Type : null;
            // 'x as? Dog': asked of the box it is put in.
            if (erasure.ConversionKind == ConversionKind.Explicit && target is NamedTypeSymbol { TypeKind: not TypeKind.Protocol } && IsTestable(target)) {
                return true;
            }

            if (target is not ExistentialTypeSymbol { MetatypeOf: null, Constraint: var constraint } || constraint.Equals(generator.Core.Error) || Mentions(constraint)) {
                return false;
            }

            var parts = CompositionTypeSymbol.PartsOf(constraint);
            return parts.All(static p => p is NamedTypeSymbol { TypeKind: TypeKind.Protocol, TypeParameters.IsEmpty: true })
                   && (erasure.ConversionKind == ConversionKind.Explicit || parts.All(p => Known(erasure.Operand.Type).Any(k => ReferenceEquals(k, p))));
        }

        /// <summary>An 'as?' or 'as!' of an existential to one of the function's opaque types, or a type made of them (<see cref="FunctionLowering.OpaqueDowncast" />).</summary>
        bool IsOpenedCast(BoundConversion cast) =>
            cast is { Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false }, Operand.Type: ExistentialTypeSymbol { Constraint: var constraint } }
            && !constraint.Equals(generator.Core.Error) && !Mentions(constraint)
            && (cast.Type is OptionalTypeSymbol { UnderlyingType: var wrapped } ? wrapped : cast.Type) is var target and not (ExistentialTypeSymbol or OptionalTypeSymbol or MetatypeTypeSymbol)
            && IsHeld(V(target), _own);

        /// <summary>An 'as!' from one of the function's opaque types to another (<see cref="FunctionLowering.OpaqueForcedCast" />).</summary>
        bool IsOpaqueCast(BoundConversion cast) =>
            cast is { Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } && cast.Type is not OptionalTypeSymbol && IsOwn(cast.Type) && IsOwn(cast.Operand.Type);
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
            // A protocol's subscript requirement, read on a type parameter: through its table, as a method's.
            if (index.Subscript is { ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } } required) {
                return stored is null
                    ? RequirementAllowed(new BoundCall(index.Syntax, index.Receiver, required, [..index.Indices], index.Type) { Substitution = index.Substitution })
                    : generator.SubscriptSetter(required) is { } setter
                      && RequirementAllowed(new BoundCall(index.Syntax, index.Receiver, setter, [..index.Indices, stored], generator.Core.Void) { Substitution = index.Substitution });
            }

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

            // Raw memory's values, and one shared between tasks: copied, moved and shared by its value witnesses.
            if (IsIntrinsic(callee) && callee.ContainingSymbol is NamedTypeSymbol { Name: "RawMemory" or "Runtime", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } } intrinsic
                && (intrinsic.Name == "RawMemory" || callee.Name == "share")) {
                return Simple(call.Type) && call.Arguments.All(a => Simple(a.Type));
            }

            // An array's own builtins, on an array of a type parameter's values held in a local.
            // One that only reads it: a slice of it, or a copy of a slice's run.
            if (callee.Builtin is "array_slice" or "array_of_slice" && call.Receiver is { } viewed && IsOwnArray(viewed.Type)) {
                return call.Arguments.All(a => !Mentions(a.Type));
            }

            // One of two of the kind, compared.
            if (callee.Builtin is "array_mismatch" or "array_shares" && call.Receiver is { } compared && IsOwnArray(compared.Type)) {
                return call.Arguments.All(a => Simple(a.Type));
            }

            if (callee.Builtin is "array_append" or "array_append_repeating" or "array_append_holes" or "array_take" or "array_append_all" or "array_remove_last" or "array_rotate" or "array_reserve" or "array_clear" or "array_truncate" && call.Receiver is { } array && IsOwnArray(array.Type)) {
                return (call.Receiver is BoundLocalAccess { Local.IsMutable: true }
                        || call.Receiver is BoundSelfAccess && OwnMutating
                        || call.Receiver is BoundParameterAccess { Parameter.Modifier: ParameterModifier.InOut } && _closureDepth == 0
                        || call.Receiver is BoundFieldAccess { Receiver: BoundSelfAccess } stored && FieldsMutable && PropertyAllowed(stored)
                        || call.Receiver is BoundFieldAccess { Receiver: BoundLocalAccess { Local.IsMutable: true }, Field: { IsComputed: false, IsStatic: false } } local && PropertyAllowed(local))
                       && call.Arguments.All(a => Simple(a.Type));
            }

            if (callee is { FunctionKind: FunctionKind.Initializer, IsSynthesized: true, Parameters.IsEmpty: false }
                && call.Type is NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Struct } made && IsHeld(V(made), _own)) {
                return call.Arguments.All(a => Simple(a.Type)) && call.Arguments.Length == StoredFields(made.OriginalDefinition).Count();
            }

            if (!IsGeneric(callee) || generator.UnspecialisedShape(callee) is not { } calleeShape) {
                return false;
            }

            Describes |= generator.DescribesValues(callee);
            DebugDescribes |= generator.DebugDescribesValues(callee);

            // A protocol extension's member: on a value of a type parameter of
            // this function, or on self, its Self that type parameter.
            var calleeSelf = OwnerOf(callee)?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } protocolSelf } ? protocolSelf : null;
            var receiverType = call.Receiver switch {
                null => calleeSelf is not null && IsOwn(calleeSelf) ? calleeSelf : null,
                BoundTypeExpression type => type.ReferencedType,
                { Type: MetatypeTypeSymbol metatype } => metatype.InstanceType,
                { } receiver => receiver.Type,
            } is { } read ? V(read) : null;
            // A member of a generic type, an optional or an array: on a value this
            // function holds, or on its own self, its type arguments from the receiver's.
            var calleeOwner = calleeSelf is null ? OwnerOf(callee)?.OriginalDefinition : null;
            var ownerBound = calleeOwner is null ? null : OwnerBindings(calleeOwner, call.Receiver is null ? (Owner(checkedSymbol) is null ? null : SelfType(checkedSymbol, TypeSubstitution.Empty)) : receiverType);
            if (call.Receiver is { } onValue && Mentions(onValue.Type) && (calleeSelf is null || receiverType is null || !IsOwn(receiverType))
                && (ownerBound is null || !Simple(onValue.Type))) {
                return false;
            }

            if (calleeOwner is not null && callee.IsMutating && call.Receiver is not (BoundLocalAccess { Local.IsMutable: true } or null or BoundSelfAccess)
                && !(call.Receiver is BoundParameterAccess { Parameter.Modifier: ParameterModifier.InOut } && _closureDepth == 0)
                && !(call.Receiver is BoundFieldAccess { Receiver: BoundSelfAccess, Field: { IsComputed: false, IsStatic: false } } changed && FieldsMutable && PropertyAllowed(changed))) {
                return false;
            }

            // One that changes self changes a local, or this function's own self.
            if (calleeSelf is not null && callee.IsMutating && receiverType is not null && IsOwn(receiverType)
                && call.Receiver is not (BoundLocalAccess { Local.IsMutable: true } or null or BoundSelfAccess) && !(call.Receiver is BoundParameterAccess { Parameter.Modifier: ParameterModifier.InOut } && _closureDepth == 0) || callee.IsMutating && call.Receiver is null or BoundSelfAccess && !OwnMutating) {
                return false;
            }

            var boundMap = new Dictionary<TypeParameterSymbol, TypeSymbol>();
            foreach (var parameter in calleeShape.Parameters) {
                if ((ReferenceEquals(parameter, calleeSelf) ? call.Substitution.Lookup(parameter) ?? receiverType
                        : call.Substitution.Lookup(parameter) ?? ownerBound?.GetValueOrDefault(parameter)) is not { } given) {
                    return false;
                }

                var bound = V(given);
                boundMap[parameter] = bound;

                if (!Mentions(bound)) {
                    continue;
                }

                // An array, an optional or a tuple of this function's type
                // parameter's values, its metadata made at run time —
                // polymorphic recursion — for a callee that asks nothing of it;
                // or a generic type's instance, whose conformances have templates.
                if (bound is not TypeParameterSymbol && IsHeld(V(bound), _own) && bound is not MetatypeTypeSymbol) {
                    if (!calleeShape.ProtocolsOf(parameter).All(p => generator.HasTemplate(bound, p))) {
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
                && (_handlers.Count > 0 || _closureDepth > 0 || checkedSymbol.ThrownType is not ExistentialTypeSymbol { Constraint: var erased } || !erased.Equals(generator.Core.Error))) {
                return false;
            }

            return callee.ThrownType is not { } calleeThrown || ThrowsAllowed(calleeSubstitution.Apply(calleeThrown));
        }

        /// <summary>A requirement of the protocols a type parameter has, called on one, as its generic witness takes it.</summary>
        bool RequirementAllowed(BoundCall call) {
            var callee = call.Function;
            if (RequirementProtocol(callee) is not { } declared || generator.GenericEntryType(declared, callee) is null
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
            } is { } read ? V(read) : null;

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
                && !(call.Receiver is BoundParameterAccess { Parameter.Modifier: ParameterModifier.InOut } && _closureDepth == 0)
                && !(call.Receiver is null or BoundSelfAccess && OwnMutating)
                && !(call.Receiver is BoundFieldAccess { Receiver: BoundSelfAccess, Field: { IsComputed: false, IsStatic: false } } stored && FieldsMutable && PropertyAllowed(stored))) {
                return false;
            }

            // Generic over parameters of its own: each bound to a type this
            // code knows by its metadata, or that the program names, with the
            // tables asked; what is called may write one out.
            var own = generator.OwnShape(callee);
            TypeSubstitution ownBound = TypeSubstitution.Empty;
            if (own is not null) {
                var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
                foreach (var parameter in own.Parameters) {
                    if (call.Substitution.Lookup(parameter) is not { } given) {
                        return false;
                    }

                    var bound = V(given);
                    map[parameter] = bound;
                    if (IsOwn(bound)) {
                        var has = Known(bound);
                        if (!own.ProtocolsOf(parameter).All(needed => has.Any(p => ReferenceEquals(p, needed)))) {
                            return false;
                        }
                    } else if (Mentions(bound) || bound is not (NamedTypeSymbol or ArrayTypeSymbol) || generator.Lower(bound) is not { } lowered || lowered is NominalType { IsNoncopyable: true } || bound.Equals(generator.Core.Never)
                               || !own.ProtocolsOf(parameter).All(needed => bound.Satisfies(needed))) {
                        return false;
                    }
                }

                ownBound = new TypeSubstitution(map);
                Describes = DebugDescribes = true;
                foreach (var (declaredParameter, argument) in callee.Parameters.Zip(call.Arguments)) {
                    if (!OwnTypeAs(declaredParameter.Type, argument.Type)) {
                        return false;
                    }
                }

                return OwnTypeAs(callee.ReturnType, call.Type);
            }

            bool OwnTypeAs(TypeSymbol declared, TypeSymbol given) {
                var read = ownBound.Apply(declared);
                return SilGen.Mentions(read, [self!]) ? SameThrough(read, protocol, selfType, given) : read.Equals(V(given)) || Canonical(read).Equals(Canonical(given));
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
            } is { } read ? V(read) : null;

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

            return binary.Method is { } method && OperatorAllowed(method, binary.Left, [binary.Left, binary.Right], binary);
        }

        /// <summary>An operator applied to values this code holds: a requirement's, through its table, or a generic type's own, compiled once as a static member is.</summary>
        bool OperatorAllowed(FunctionSymbol method, BoundExpression first, ImmutableArray<BoundExpression> operands, BoundExpression at) {
            var call = new BoundCall(at.Syntax, null, method, operands, at.Type);
            if (OwnerOf(method) is { TypeKind: TypeKind.Protocol }) {
                return RequirementAllowed(call);
            }

            return OwnerOf(method) is { ExtendedArray: null, ExtendedOptional: null } owner && V(first.Type) is NamedTypeSymbol { IsConstructed: true } named
                && ReferenceEquals(named.OriginalDefinition, owner.OriginalDefinition) && operands.All(o => V(o.Type).Equals(named))
                && CallAllowed(new BoundCall(at.Syntax, null, method, operands, at.Type) { Substitution = TypeSubstitution.For(named) });
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
            signature = SignatureOf(symbol, at, OpaqueParametersAsThemselves(symbol));
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
        var owned = signature.Owned.IsEmpty || IsValueInitializer(symbol) || IsClassInitializer(symbol) ? default : signature.Owned.Concat(hidden.Select(static _ => false)).ToImmutableArray();
        var type = new FunctionType([..signature.Parameters, ..hidden], result, thrown, owned, signature.IsAsync);
        _instances[name] = type;
        _instanceOrigins[name] = (symbol, OpaqueParametersAsThemselves(symbol), _loweringInstance);
        _unspecialised[name] = (symbol, shape);

        // A library's, compiled in its archive: called there, its body not
        // bound. An optimised build copies in the inlinable ones from the
        // library's interface, to specialise (LibraryInterface.LinkInlinable).
        if (CompiledBy(symbol) is not null) {
            _external[name] = () => type;
            return (name, type);
        }

        _pending.Enqueue((symbol, name, type, OpaqueParametersAsThemselves(symbol)));
        if (IsClassInitializer(symbol) && Owner(symbol) is { } madeClass) {
            GenericDeinitThunk(madeClass.OriginalDefinition, at);
        }

        return (name, type);
    }

    // -----------------------------------------------------------------------
    // Witness tables in the generic calling convention
    // -----------------------------------------------------------------------

    readonly Dictionary<NamedTypeSymbol, List<(string Key, Symbol Member)>> _genericRequirements = new(ReferenceEqualityComparer.Instance);

    /// <summary>The protocol a requirement is of: its own, or, for an accessor, its property's or subscript's.</summary>
    internal static NamedTypeSymbol? RequirementProtocol(FunctionSymbol requirement) =>
        requirement.ContainingSymbol switch {
            NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol => protocol,
            FieldSymbol { ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol } => protocol,
            FunctionSymbol { FunctionKind: FunctionKind.Subscript, ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol } => protocol,
            _ => null,
        };

    /// <summary>
    ///     The requirements a protocol itself declares that a witness table's
    ///     generic entries hold, in declaration order: its methods, static
    ///     ones and operators among them, its properties' getters and
    ///     setters, and its subscripts' getters and setters.
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

                        // 'var size: Int { get set }': its setter too, called as a mutating method of the new value.
                        if (!field.IsStatic && SetterOf(field) is { } setter) {
                            requirements.Add((SetterSlot(field), setter));
                        }

                        break;

                    // 'subscript(position: Index) -> Element': its getter, called as a method of its indices is;
                    // '{ get set }', its setter, of its indices and the new value.
                    case FunctionSymbol { FunctionKind: FunctionKind.Subscript, IsStatic: false } subscript:
                        requirements.Add((SubscriptName(subscript) + "$get", subscript));
                        if (SubscriptSetter(subscript) is { } subscriptSetter) {
                            requirements.Add((SubscriptName(subscript) + "$set", subscriptSetter));
                        }

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
        // An instance of a generic protocol's, with its arguments: 'next() -> T?' of a 'Source<Int>' gives an 'Int?'.
        var arguments = TypeSubstitution.For(protocol);
        protocol = protocol.OriginalDefinition;
        if (protocol.SelfTypeParameter is not { } self) {
            return null;
        }

        if (field.IsAsyncGetter) {
            return null;
        }

        var opaque = new OpaqueType(self.Name);
        ImmutableArray<SilType> taken = field.IsStatic ? [] : [opaque];
        var fieldType = arguments.Apply(field.Type);
        if (HeldOfSelf(fieldType, self) is { } held) {
            return new FunctionType([..taken, SilType.Address(held), SilType.Metadata], SilType.Unit);
        }

        return (Mentions(fieldType, [self, ..protocol.AssociatedTypes]) ? ArrayOfSelf(fieldType, self) : Lower(fieldType)) is not { } result ? null : new FunctionType([..taken, SilType.Metadata], result);
    }

    internal FunctionType? GenericEntryType(NamedTypeSymbol protocol, FunctionSymbol requirement) {
        var arguments = TypeSubstitution.For(protocol);
        protocol = protocol.OriginalDefinition;
        if (protocol.SelfTypeParameter is not { } self || requirement.IsRethrows || !requirement.OpaqueTypeParameters.IsEmpty
            || !requirement.TypeParameters.IsEmpty && OwnShape(requirement) is null) {
            return null;
        }

        var own = new List<TypeParameterSymbol>([self, ..protocol.AssociatedTypes, ..requirement.TypeParameters]);
        var opaque = new OpaqueType(self.Name);
        SilType? Slot(TypeSymbol type) {
            type = arguments.Apply(type);
            return type.Equals(self) ? opaque : HeldOfSelf(type, self, requirement.TypeParameters) is { } held ? held : Mentions(type, own) ? null : Lower(type);
        }

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

        if ((Slot(requirement.ReturnType) ?? ArrayOfSelf(arguments.Apply(requirement.ReturnType), self, requirement.TypeParameters)) is not { } result) {
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

        // Its own type parameters' metadata and tables, after where the result and the error go.
        if (OwnShape(requirement) is { } ownShape) {
            if (requirement.IsAsync) {
                return null;
            }

            parameters.AddRange(Enumerable.Repeat(SilType.Metadata, ownShape.Parameters.Length));
            parameters.AddRange(Enumerable.Repeat(SilType.WitnessTable, ownShape.Requirements.Length));
        }

        // An async one is awaited through its entry as any async call is.
        // Only self may be held by address — in the existential's box, or
        // the caller's frame, which outlives the call — not yet a value of
        // Self or of an associated type in the caller's storage, which a
        // suspension would have to keep.
        if (requirement.IsAsync && (parameters.Skip(HasSelf(requirement) ? 1 : 0).Any(static p => SilType.MentionsOpaque(p))
                || SilType.MentionsOpaque(result) || thrown is not null && SilType.MentionsOpaque(thrown))) {
            return null;
        }

        parameters.Add(SilType.Metadata);
        return new FunctionType(parameters.ToImmutable(), result, thrown, isAsync: requirement.IsAsync);
    }

    readonly Dictionary<FunctionSymbol, GenericShape?> _ownShapes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     What a requirement generic over type parameters of its own takes of
    ///     them: their metadata, and the tables their constraints name. Null
    ///     for one that has none, or with one the convention cannot pass.
    /// </summary>
    GenericShape? OwnShape(FunctionSymbol requirement) {
        if (requirement.TypeParameters.IsEmpty) {
            return null;
        }

        if (!_ownShapes.TryGetValue(requirement, out var shape)) {
            var requirements = new List<(TypeParameterSymbol, NamedTypeSymbol)>();
            var passed = requirement.ConformanceRequirements.IsEmpty && requirement.SameTypeRequirements.IsEmpty;
            foreach (var parameter in requirement.TypeParameters) {
                passed &= parameter is { IsValue: false, AllowsNoncopyable: false };
                foreach (var part in parameter.Constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c))) {
                    if (part is NamedTypeSymbol protocol && IsTabledProtocol(protocol)) {
                        requirements.Add((parameter, protocol.OriginalDefinition));
                    } else {
                        passed = false;
                    }
                }
            }

            _ownShapes[requirement] = shape = passed ? new([..requirement.TypeParameters], [..requirements]) : null;
        }

        return shape;
    }

    /// <summary>The tables with an entry for a requirement generic over its own parameters that its conformer's member, not compiled once, cannot meet: it traps.</summary>
    readonly HashSet<SilWitnessTable> _partial = new(ReferenceEqualityComparer.Instance);

    /// <summary>An entry that is never called by code that could be specialised instead (<see cref="GenericTable" />), and traps should it be.</summary>
    string TrapWitness(SilWitnessTable table, string key, FunctionType type) {
        var name = $"$generic$witness$trap${Sanitise(table.Conformer.ToString())}${Sanitise(table.Protocol)}${Sanitise(key)}";
        if (_module.Find(name) is null) {
            var function = new SilFunction(name, type);
            var block = function.AddBlock();
            foreach (var parameter in type.Parameters) {
                block.AddArgument(parameter);
            }

            block.Append(new CondFailInst(block.Append(new IntegerLiteralInst(SilType.Bool, 1)), $"'{key}' of '{TypeMetadata.DisplayName(table.Conformer)}' is copied for each type, not compiled once"));
            block.Append(new UnreachableInst());
            _module.Add(function);
        }

        _partial.Add(table);
        return name;
    }

    /// <summary>
    ///     A type's witness tables for a protocol and every protocol it
    ///     refines, with their entries in the generic calling convention: what
    ///     a call from code that knows the type passes unspecialised code,
    ///     which finds the refined ones in the registry. False when the type
    ///     does not declare each conformance itself, or it cannot be lowered.
    /// </summary>
    internal bool GenericTable(TypeSymbol concrete, NamedTypeSymbol protocol, SyntaxNode at, bool describe = true, bool debugDescribe = false) {
        if (Lower(concrete) is not { } value || concrete is not (NamedTypeSymbol or ArrayTypeSymbol)) {
            return false;
        }

        // An array conforms by its extensions, which say so for every array of its kind.
        var closure = ProtocolClosure([protocol]);
        if (concrete is NamedTypeSymbol named) {
            var declared = DeclaredProtocols(named.OriginalDefinition, conditional: true).ToHashSet(StringComparer.Ordinal);
            if (value is not NominalType) {
                declared.UnionWith(_module.PrimitiveConformances.GetValueOrDefault(value.ToString()) ?? []);
            }

            // Never declares nothing: the tables of an error that is none have nothing to call.
            if (!concrete.Equals(Core.Never) && !closure.TrueForAll(p => declared.Contains(QualifiedName(p)))) {
                return false;
            }
        } else if (!CompilesOnce || value is not ArrayType) {
            return false;
        }

        if (!closure.TrueForAll(concrete.Satisfies)) {
            return false;
        }

        foreach (var each in closure) {
            if (WitnessTable(concrete, value, each, at) is { } made && _partial.Contains(made)) {
                return false;
            }
        }

        // An error of the type, which unspecialised code may throw on as 'any Error'.
        if (CompilesOnce && !concrete.Equals(Core.Never) && closure.Exists(p => ReferenceEquals(p, Core.Error.OriginalDefinition))) {
            ErrorEraser(value);
        }

        // Unspecialised code reads the conformer's associated types through
        // the table, and finds what they conform to in the registry: those
        // tables are made too.
        // Written out by its describer, as a type parameter's value is — for
        // code that may write one out.
        var tabled = _associatedTabled.Add((concrete, protocol.OriginalDefinition));
        var described = describe && _associatedDescribed.Add((concrete, protocol.OriginalDefinition));
        var debugDescribed = debugDescribe && _associatedDebugDescribed.Add((concrete, protocol.OriginalDefinition));
        if (CompilesOnce && (tabled || described || debugDescribed)) {
            foreach (var each in closure) {
                foreach (var associated in each.AssociatedTypes) {
                    if ((described || debugDescribed) && AssociatedBinding(concrete, associated) is { } read && Lower(read) is { } lowered && !SilType.MentionsOpaque(lowered)) {
                        Describer(read, lowered, at, debugDescribed);
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

    readonly HashSet<(TypeSymbol, NamedTypeSymbol)> _associatedDebugDescribed = [];

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

        return _compilation.TreeOf(root)?.Text;
    }

    /// <summary>
    ///     A requirement's witness for a type in the generic calling
    ///     convention: the requirement lowered as if its body called itself
    ///     on the type — so it is met as code that knows the type meets it,
    ///     the type's own member, a default, a builtin — with what arrives
    ///     opaque copied out of its caller's storage first, and a result of
    ///     <c>Self</c> written where the caller says. Null when it cannot be made.
    /// </summary>
    string? GenericWitness(TypeSymbol concrete, SilType value, NamedTypeSymbol protocol, string key, Symbol member, FunctionType type, SyntaxNode at, (string Name, GenericShape Shape)? template = null) {
        if (member is FieldSymbol field) {
            return GenericGetter(concrete, value, protocol, field, type, at, template);
        }

        var requirement = (FunctionSymbol)member;
        // By its place in the table: operators sanitise alike (+ and - both to $).
        var index = GenericRequirements(protocol).FindIndex(r => ReferenceEquals(r.Member, requirement));
        var name = template?.Name ?? $"$generic$witness${Sanitise(value.ToString())}${Sanitise(QualifiedName(protocol))}${index}${(requirement.Name.All(char.IsLetterOrDigit) ? requirement.Name : "op")}";

        // A template's witness is compiled once for the generic type: given its arguments' metadata and tables, as hidden parameters.
        if (template is { } compiledOnce && _module.Find(name) is null) {
            _unspecialised[name] = (requirement, compiledOnce.Shape);
        }
        if (_module.Find(name) is not null) {
            return name;
        }

        // Generic over parameters of its own: compiled once for them, as any generic function is.
        var ownShape = template is null ? OwnShape(requirement) : null;
        if (ownShape is not null) {
            _unspecialised[name] = (requirement, ownShape);
            Opening();
        } else if (!requirement.TypeParameters.IsEmpty) {
            return null;
        }

        using var opaqueOwn = ownShape is null ? null : Opaque(ownShape);
        using var ownContext = ownShape is null ? null : AssociatedContext(requirement);
        var self = protocol.OriginalDefinition.SelfTypeParameter!;
        var syntax = requirement.DeclaringSyntax.FirstOrDefault() ?? at;
        if (SourceOf(syntax) is not { } source) {
            return null;
        }

        var bound = new Dictionary<TypeParameterSymbol, TypeSymbol> { [self] = concrete };
        foreach (var parameter in requirement.TypeParameters) {
            bound[parameter] = parameter;
        }

        var substitution = WithAssociatedTypes(requirement, new TypeSubstitution(WithProtocolArguments(protocol, bound)));
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

        // A subscript is indexed, as code that knows the type indexes it: an array's elements, or the type's own subscript.
        if (requirement.FunctionKind == FunctionKind.Subscript && receiver is not null) {
            call = new BoundIndexAccess(syntax, requirement.ReturnType, receiver, [..requirement.Parameters.Select(p => (BoundExpression)new BoundParameterAccess(syntax, p))]) {
                Subscript = requirement,
                Substitution = substitution,
            };
        }
        BoundStatement[] statements = requirement.ReturnType.Equals(Core.Void)
            ? [new BoundExpressionStatement(syntax, call), new BoundReturn(syntax, null)]
            : [new BoundReturn(syntax, call)];

        // A setter stores, as code that knows the type stores: to the type's own property or subscript.
        if (requirement.FunctionKind == FunctionKind.Accessor && receiver is not null && requirement.Parameters.Length > 0) {
            var given = requirement.Parameters.Select(p => (BoundExpression)new BoundParameterAccess(syntax, p)).ToList();
            BoundExpression? target = requirement.ContainingSymbol switch {
                FieldSymbol property => new BoundFieldAccess(syntax, receiver, property, property.Type),
                FunctionSymbol { FunctionKind: FunctionKind.Subscript } subscript => new BoundIndexAccess(syntax, subscript.ReturnType, receiver, [..given.SkipLast(1)]) {
                    Subscript = subscript,
                    Substitution = substitution,
                },
                _ => null,
            };
            if (target is null) {
                return null;
            }

            statements = [new BoundExpressionStatement(syntax, new BoundAssignment(syntax, target, given[^1])), new BoundReturn(syntax, null)];
        }

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
        TemplateRecipes(function, concrete, template);
        if (ownShape is not null) {
            AssociatedRecipes(function, requirement, ownShape);
        }

        AsTrampoline(function);
        _module.Add(function);
        return name;
    }

    /// <summary>How a template's witness, compiled once for the generic type, finds the metadata of what it reads through its type parameters.</summary>
    void TemplateRecipes(SilFunction function, TypeSymbol concrete, (string Name, GenericShape Shape)? template) {
        if (template is { } compiledOnce) {
            AssociatedRecipes(function, ((NamedTypeSymbol)concrete).OriginalDefinition, compiledOnce.Shape);
        }
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
    string? GenericGetter(TypeSymbol concrete, SilType value, NamedTypeSymbol protocol, FieldSymbol field, FunctionType type, SyntaxNode at, (string Name, GenericShape Shape)? template = null) {
        var index = GenericRequirements(protocol).FindIndex(r => ReferenceEquals(r.Member, field));
        var name = template?.Name ?? $"$generic$witness${Sanitise(value.ToString())}${Sanitise(QualifiedName(protocol))}${index}${field.Name}";
        if (_module.Find(name) is not null) {
            return name;
        }

        if (PrintSymbol() is not { } print || print.DeclaringSyntax.FirstOrDefault() is not { } syntax || SourceOf(syntax) is not { } source) {
            return null;
        }

        var self = protocol.OriginalDefinition.SelfTypeParameter!;
        var substitution = WithAssociatedTypes(field, new TypeSubstitution(WithProtocolArguments(protocol, new() { [self] = concrete, [print.TypeParameters[0]] = concrete })));
        BoundExpression receiver = field.IsStatic ? new BoundTypeExpression(syntax, self) : new BoundParameterAccess(syntax, print.Parameters[0]);
        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
        };
        var bridge = new Bridge(false, field.IsStatic ? [] : [print.Parameters[0]], Mentions(field.Type, [self])) {
            Returns = new BoundFieldAccess(syntax, receiver, concrete is NamedTypeSymbol named && !field.IsStatic && (FindField(named, field.Name) ?? DefaultProperty(named, field)) is { } own ? own : field, field.Type),
            TakesNoParameters = field.IsStatic,
            Self = concrete,
        };
        if (template is { } compiledOnce) {
            _unspecialised[name] = (print, compiledOnce.Shape);
        }

        var lowering = new FunctionLowering(this, print, function, source, substitution) { Bridged = bridge };
        lowering.Lower(new BoundBlock(syntax, [], []));
        if (lowering.Failed) {
            return null;
        }

        function.RemoveUnreachableBlocks();
        TemplateRecipes(function, concrete, template);
        AsTrampoline(function);
        _module.Add(function);
        return name;
    }

    /// <summary>
    ///     A witness is the compiler's, not the program's: no line of its
    ///     own, and, where it calls one function, a trampoline to it, so a
    ///     debugger's 'step' through an existential goes on into the member.
    /// </summary>
    static void AsTrampoline(SilFunction function) {
        function.SourceFile = null;
        function.Line = 0;
        foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
            instruction.Location = null;
        }

        var called = function.Blocks.SelectMany(static b => b.Instructions)
            .Where(static i => i is ApplyInst or TryApplyInst)
            .Select(static i => i.Operands[0] is FunctionRefInst reference ? reference.Function : null)
            .ToList();
        if (called is [{ } target]) {
            function.TrampolineTarget = target;
        }
    }

    /// <summary>A map with an instance of a generic protocol's arguments added: its <c>T</c> as a <c>Producer&lt;Dog&gt;</c> gives it.</summary>
    static Dictionary<TypeParameterSymbol, TypeSymbol> WithProtocolArguments(NamedTypeSymbol protocol, Dictionary<TypeParameterSymbol, TypeSymbol> map) {
        if (protocol.IsConstructed) {
            var arguments = TypeSubstitution.For(protocol);
            foreach (var parameter in arguments.Parameters) {
                map.TryAdd(parameter, arguments.Apply(parameter));
            }
        }

        return map;
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
    internal sealed record Bridge(bool OpaqueSelfValue, ImmutableHashSet<ParameterSymbol> OpaqueParameters, bool OpaqueResult, SyntaxNode? Describes = null, bool Debug = false) {
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

        var made = Reabstract(_module, source, target);
        _reabstractions[key] = made;
        return made;
    }

    /// <summary>The thunk of a pair (<see cref="Reabstraction" />), made in the module unless it has it.</summary>
    internal static (string Thunk, ClassType Context)? Reabstract(SilModule module, ThickFunctionType source, ThickFunctionType target) {
        // Named for the pair, so that every unit that makes one makes the same.
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{source} as {target}")))[..16];
        var name = $"$reabstract${hash}";
        return module.Find(name) is not null && module.FindType($"{name}$context") is ClassType context
            ? (name, context)
            : MakeReabstraction(module, source.Signature, target.Signature, name, source);
    }

    static (string Thunk, ClassType Context)? MakeReabstraction(SilModule module, FunctionType source, FunctionType target, string name, ThickFunctionType closure) {
        // A result written where the caller says, on either side: the last parameter.
        // After it, where an error goes that is thrown as its address.
        static bool WritesError(FunctionType type) => type is { Thrown: AddressType error, Parameters: [.., var last] } && last == error;
        static bool WritesResult(FunctionType type) =>
            type.Result == SilType.Unit && (WritesError(type) ? type.Parameters.SkipLast(1).LastOrDefault() : type.Parameters.LastOrDefault()) is AddressType { Pointee: var written } && SilType.IsIndirect(written);
        var outError = WritesError(target);
        var inError = WritesError(source);
        var own = target.Parameters.Length - (outError ? 1 : 0);
        var taken = source.Parameters.Length - (inError ? 1 : 0);
        var outResult = WritesResult(target) && own == taken + 1;
        var inResult = !outResult && WritesResult(source) && taken == own + 1;
        if (own - (outResult ? 1 : 0) != taken - (inResult ? 1 : 0) || target.IsAsync != source.IsAsync
            || source.Thrown is not null && source.Thrown != target.Thrown && (target.Thrown is null || !outError && !inError)) {
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
        var slots = new List<AllocStackInst>();
        AllocStackInst Slot(SilType held, bool unmanaged) {
            var slot = new AllocStackInst(held) { Block = entry, Unmanaged = unmanaged };
            entry.Instructions.Insert(slots.Count, slot);
            slots.Add(slot);
            return slot;
        }

        for (var i = 0; i < taken - (inResult ? 1 : 0); i++) {
            var (wanted, value) = (source.Parameters[i], given[i]);
            if (wanted == value.Type) {
                arguments.Add(value);
            } else if (value.Type is AddressType && wanted is AddressType && (SilType.MentionsOpaque(value.Type) || SilType.MentionsOpaque(wanted))) {
                arguments.Add(entry.Append(new BuiltinInst("opaque_cast", wanted, value)));
            } else if (SilType.SameErasingOpaque(value.Type, wanted) || SilType.IsIndirect(value.Type) && SilType.IsIndirect(wanted)
                       // An array or a slice of them, a generic type that holds them behind a reference: the same bits.
                       || !SilType.IsIndirect(value.Type) && !SilType.IsIndirect(wanted) && value.Type is not ThickFunctionType && SilType.SameErasingOpaque(value.Type, wanted, opaqueOnly: true)) {
                arguments.Add(entry.Append(new BuiltinInst("opaque_borrow_as", wanted, value)));
            } else if (SilType.IsIndirect(wanted) && value.Type is not AddressType && !SilType.IsIndirect(value.Type)) {
                // A value the caller passes as it is, where the closure takes it by address: in a slot that holds it unowned.
                var slot = Slot(value.Type, unmanaged: true);
                entry.Append(new StoreInst(value, slot, StoreKind.Assign));
                var lent = entry.Append(new LoadBorrowInst(entry.Append(new BuiltinInst("opaque_cast", SilType.Address(wanted), slot))));
                arguments.Add(lent);
                ends.Add(lent);
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
        AllocStackInst? made = null;
        if (inResult) {
            made = Slot(target.Result, unmanaged: false);
            arguments.Add(entry.Append(new BuiltinInst("opaque_cast", source.Parameters[taken - 1], made)));
        }

        // Where the closure writes its error: where this one's caller says, or a slot it is taken from.
        AllocStackInst? failure = null;
        if (inError) {
            SilValue at = outError ? given[own] : failure = Slot(target.Thrown!, unmanaged: false);
            arguments.Add(at.Type == source.Parameters[^1] ? at : entry.Append(new BuiltinInst("opaque_cast", source.Parameters[^1], at)));
        }

        void Free(SilBlock block) {
            for (var i = slots.Count - 1; i >= 0; i--) {
                block.Append(new DeallocStackInst(slots[i]));
            }
        }

        void Finish(SilBlock block, SilValue? result) {
            for (var i = ends.Count - 1; i >= 0; i--) {
                block.Append(new EndBorrowInst(ends[i]));
            }

            block.Append(new EndBorrowInst(function));
            if (outResult && result is not null && resultType != SilType.Unit) {
                var into = given[own - 1];
                SilValue at = ((AddressType)into.Type).Pointee == resultType ? into : block.Append(new BuiltinInst("opaque_cast", SilType.Address(resultType), into));
                block.Append(new StoreInst(result, at, StoreKind.Init));
                result = null;
            }

            if (made is not null) {
                result = block.Append(new LoadInst(made, target.Result.IsTrivial ? LoadKind.Trivial : LoadKind.Take));
            }

            if (!outResult && result is not null && result.Type != target.Result) {
                result = block.Append(new BuiltinInst("opaque_take_as", target.Result, result));
            }

            Free(block);
            block.Append(new ReturnInst(outResult || target.Result == SilType.Unit ? null : result));
        }

        if (!outResult && !inResult && resultType != target.Result && !SilType.SameErasingOpaque(resultType, target.Result)
            && (SilType.IsIndirect(resultType) || SilType.IsIndirect(target.Result) || resultType is ThickFunctionType || !SilType.SameErasingOpaque(resultType, target.Result, opaqueOnly: true))) {
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
            SilValue raised = error;
            if (outError) {
                if (!inError) {
                    failed.Append(new StoreInst(error, failed.Append(new BuiltinInst("opaque_cast", SilType.Address(error.Type), given[own])), StoreKind.Init));
                }

                raised = given[own];
            } else if (failure is not null) {
                raised = failed.Append(new LoadInst(failure, failure.ElementType.IsTrivial ? LoadKind.Trivial : LoadKind.Take));
            }

            Free(failed);
            failed.Append(new ThrowInst(raised));
        }

        module.AddType(context);
        module.Add(thunk);
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
    internal void Describer(TypeSymbol type, SilType value, SyntaxNode at, bool debug = false) {
        if (PrintSymbol() is not { } print || print.DeclaringSyntax.FirstOrDefault() is not { } syntax || SourceOf(syntax) is not { } source) {
            return;
        }

        var substitution = new TypeSubstitution(new Dictionary<TypeParameterSymbol, TypeSymbol> { [print.TypeParameters[0]] = type });
        var type0 = new FunctionType([new OpaqueType(print.TypeParameters[0].Name), SilType.Metadata], SilType.String);

        // And, for code that writes one out with '?', what writes it as '"\(x:?)"' would.
        if (debug && _debugDescribed.Add(value)) {
            var debugName = $"$debugdescribe${Sanitise(value.ToString())}";
            var made = new SilFunction(debugName, type0) { SourceFile = source.FileName, Line = source.GetLinePosition(syntax.Span.Start).Line };
            var debugLowering = new FunctionLowering(this, print, made, source, substitution) { Bridged = new Bridge(false, [print.Parameters[0]], false, at, Debug: true) };
            using (_diagnostics.Suppress()) {
                debugLowering.Lower(new BoundBlock(syntax, [], []));
            }

            if (!debugLowering.Failed) {
                made.RemoveUnreachableBlocks();
                _module.Add(made);
                _module.DebugDescribers[value.ToString()] = (value, debugName);
            }
        }

        if (!_described.Add(value)) {
            return;
        }

        var name = $"$describe${Sanitise(value.ToString())}";
        var function = new SilFunction(name, type0) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
        };
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

    /// <summary>The types whose debug describer has been asked for.</summary>
    readonly HashSet<SilType> _debugDescribed = [];

    /// <summary>What <c>?</c> in a hole says, and nothing else: <c>"\(x:?)"</c>.</summary>
    internal static readonly FormatSpecifier DebugFormat = FormatSpecifier.Default with { Kind = "Debug", KindLetter = '?' };

    sealed partial class FunctionLowering {
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
                if (_errorOut is { } own && _handlers.Count == 0) {
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
                    && (Bridged is not null || ResultOf(symbol) is { } made && generator.Lower(made) is { } returned && SilType.IsIndirect(returned))) {
                    _indirectResult = _entry.AddArgument(parameterTypes[next++]);
                    NameArgument("$out");
                }

                if (function.Type.Thrown is AddressType genericErrorAddress && next < parameterTypes.Length && parameterTypes[next] == genericErrorAddress) {
                    _errorOut = _entry.AddArgument(parameterTypes[next++]);
                    NameArgument("$error");
                }

                foreach (var parameter in generic.Shape.Parameters) {
                    _genericMetadata[parameter] = _entry.AddArgument(parameterTypes[next++]);
                    NameArgument($"$metadata.{ParameterName(parameter)}");
                }

                foreach (var (parameter, protocol) in generic.Shape.Requirements) {
                    _genericTables[(parameter, protocol)] = _entry.AddArgument(parameterTypes[next++]);
                    NameArgument($"$witnesses.{ParameterName(parameter)}.{protocol.Name}");
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
                NameArgument($"$metadata.{(opaque is null || function.ParameterNames.Contains($"$metadata.{opaque.Name}") ? "Self" : opaque.Name)}");
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

            // A debug describer writes it as '"\(x:?)"' would.
            var value = new BoundParameterAccess(at, symbol.Parameters[0]);
            var text = Bridged.Debug
                ? DebugSpec(at) is { } spec ? FormattedHole(value, new BoundFormatSpec(at, spec, DebugFormat)) : null
                : Hole(value, null);
            if (text is not null) {
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

            // An associated type of the type an existential was opened as: from the table of its conformance, here.
            if (type is DependentMemberTypeSymbol { BaseType: var on, Associated: { ContainingSymbol: NamedTypeSymbol declaring } associated } && IsOpenedHere(type)) {
                var index = declaring.OriginalDefinition.AssociatedTypes.IndexOf(associated);
                return Emit(new BuiltinInst($"witness_associated:{index}", SilType.Metadata, TableFor(on, declaring)));
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
            if (_genericMetadata.Count == 0 || RequirementProtocol(call.Function) is not { } protocol) {
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
            var protocol = RequirementProtocol(callee)!.OriginalDefinition;
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
            var ownShape = generator.OwnShape(callee);
            var ownHidden = ownShape is null ? 0 : ownShape.Parameters.Length + ownShape.Requirements.Length;
            var hasOut = arguments.Count < type.Parameters.Length - 1 - ownHidden - (errorOut is null ? 0 : 1);
            (SilValue Passed, AllocStackInst Slot)? result = null;
            SilValue? openedBuffer = null;
            if (hasOut && type.Parameters[arguments.Count] is AddressType && OpenedBufferMetadata(Sub(call.Type)) is { } resultMetadata) {
                // Of the type an existential was opened as, or one of its associated types: a buffer its metadata sizes, which the erasure takes, let go if the call throws.
                var buffer = Emit(new BuiltinInst("opaque_alloc", type.Parameters[arguments.Count], resultMetadata));
                openedBuffer = buffer;
                arguments.Add(buffer);
                borrows.Add(new OnError(() => Emit(new BuiltinInst("opaque_free", SilType.Unit, buffer))));
            } else if (hasOut && GenericOut(type.Parameters[arguments.Count], Sub(call.Type)) is { } into) {
                result = into;
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

            // Its own type parameters' metadata and tables: what the call binds them to.
            if (ownShape is not null) {
                var ownBound = new TypeSubstitution(ownShape.Parameters.ToDictionary(static p => p, p => Sub(call.Substitution.Lookup(p)!)));
                foreach (var (ownParameter, ownProtocol) in ownShape.Requirements) {
                    if (ownBound.Lookup(ownParameter) is { } given && !IsOpaqueHere(given)) {
                        generator.GenericTable(given, ownProtocol, call.Syntax, true, true);
                    }
                }

                if (GenericHiddenArguments(ownShape, ownBound, call.Syntax) is not { } hidden) {
                    EndAll(borrows);
                    return null;
                }

                arguments.AddRange(hidden);
            }

            arguments.Add(metadata);
            var invoked = Invoke(code, type, arguments, borrows, call.Syntax, cannotThrow: error is { CannotThrow: true }, errorSlot: error?.Slot);
            if (openedBuffer is not null) {
                return invoked is null || _current is null ? invoked : openedBuffer;
            }

            return result is { } written ? TakeOut(invoked, written.Slot) : AsSeen(invoked, call.Type);
        }

        /// <summary>An array of a type parameter's values a table's entry gives back, seen as this code's array of them.</summary>
        SilValue? AsSeen(SilValue? result, TypeSymbol wanted) =>
            result is { Type: ArrayType given } && SilType.MentionsOpaque(given) && LowerType(wanted) is { } here && here != given && _current is not null
                ? Emit(new BuiltinInst("opaque_take_as", here, result))
                : result;

        /// <summary>
        ///     Whether a type is one an existential was opened as in the code
        ///     being lowered — not a type parameter of the function's own — or
        ///     an associated type read through one: a value of it lives in a
        ///     buffer its metadata sizes, as nothing here can copy it.
        /// </summary>
        static bool IsOpenedHere(TypeSymbol type) =>
            type switch {
                TypeParameterSymbol { OpenedFrom: not null } => true,
                DependentMemberTypeSymbol member => IsOpenedHere(member.BaseType),
                _ => false,
            };

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

            (SilValue Passed, AllocStackInst Slot)? result = null;
            SilValue? openedBuffer = null;
            if (type.Parameters[arguments.Count] is AddressType && OpenedBufferMetadata(Sub(access.Type)) is { } resultMetadata) {
                // Of the type an existential was opened as, or one of its associated types: a buffer its metadata sizes, which the erasure takes.
                openedBuffer = Emit(new BuiltinInst("opaque_alloc", type.Parameters[arguments.Count], resultMetadata));
                arguments.Add(openedBuffer);
            } else if ((result = GenericOut(type.Parameters[arguments.Count], Sub(access.Type))) is { } into) {
                arguments.Add(into.Passed);
            }

            arguments.Add(MetadataOf(parameter)!);
            var invoked = Invoke(code, type, arguments, borrows, access.Syntax);
            if (openedBuffer is not null) {
                return invoked is null || _current is null ? invoked : openedBuffer;
            }

            return result is { } written ? TakeOut(invoked, written.Slot) : AsSeen(invoked, access.Type);
        }

        /// <summary>
        ///     Whether a call of a generic function goes to its unspecialised
        ///     instance: when it has one and what the call binds each type
        ///     parameter to can be passed — a type parameter here, as it is, or
        ///     a type whose metadata and witness tables are made for it.
        /// </summary>
        bool CallsUnspecialised(FunctionSymbol callee, TypeSubstitution bound, SyntaxNode at, out GenericShape? shape) {
            shape = null;

            // A specialised build calls generic code compiled once only from
            // code that knows a type by its metadata alone: what a call that
            // opens a Type runs, and the call itself.
            if (generator._generics != GenericsMode.Unspecialised && !_genericMetadata.Keys.Any(static k => k.OpenedFrom is null)
                && !bound.Parameters.Any(p => bound.Lookup(p) is TypeParameterSymbol { OpenedFrom: not null })) {
                return false;
            }

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
                    if (!found.ProtocolsOf(parameter).All(p => generator.HasTemplate(given, p))) {
                        return false;
                    }

                    continue;
                }

                if (Mentions(given, [.._genericMetadata.Keys]) || LowerType(given) is not { } lowered
                    || lowered is NominalType { IsNoncopyable: true }) {
                    return false;
                }

                foreach (var protocol in found.ProtocolsOf(parameter)) {
                    if (!generator.GenericTable(given, protocol, at, generator.DescribesValues(callee), generator.DebugDescribesValues(callee))) {
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
                    if (!generator.GenericTable(named, protocol.OriginalDefinition, at, generator.DescribesValues(callee), generator.DebugDescribesValues(callee))) {
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
        ///     type parameter here as it is; a local, or a field of one, in
        ///     its own storage when the call cannot change it (<paramref name="inPlace" />);
        ///     and any other in a slot that holds it without a copy, seen as
        ///     the callee's type parameter, until the call is done.
        /// </summary>
        SilValue? GenericArgument(BoundExpression argument, TypeSymbol given, SilType expected, List<SilValue> borrows, bool inPlace = false) {
            if (IsOpaqueHere(given) || LowerType(given) is { } held && SilType.IsIndirect(held)) {
                if (Borrow(argument) is not { } value) {
                    return null;
                }

                borrows.Add(value);
                return SilType.SameErasingOpaque(value.Type, expected) ? value : Emit(new BuiltinInst("opaque_borrow_as", expected, value));
            }

            if (LowerType(given) is not { } type) {
                return null;
            }

            if (inPlace && GenericInPlace(argument, type, expected, borrows) is { } lent) {
                return lent;
            }

            if (Borrow(argument) is not { } borrowed) {
                return null;
            }

            borrows.Add(borrowed);
            return GenericLent(borrowed, type, expected, borrows);
        }

        /// <summary>
        ///     <c>lowerBound as! C.Index</c> between two types only their
        ///     metadata knows: the same value, seen as the other, when the two
        ///     are one type — their metadata the same — and a trap when not.
        /// </summary>
        SilValue? OpaqueForcedCast(BoundConversion conversion) {
            if (MetadataOf(conversion.Operand.Type) is not { } from || MetadataOf(conversion.Type) is not { } to || LowerType(conversion.Type) is not { } target
                || Expression(conversion.Operand) is not { } value) {
                return null;
            }

            var same = Emit(new BuiltinInst("metadata_equal", SilType.Bool, from, to));
            Emit(new CondFailInst(Emit(new BuiltinInst("not_Bool", SilType.Bool, same)), $"cast to '{conversion.Type.ToDisplayString()}' failed"));
            var owned = Owned(value);
            return owned.Type == target ? owned : Emit(new BuiltinInst("opaque_take_as", target, owned));
        }

        /// <summary>
        ///     A value of a type only its metadata knows as an existential
        ///     (<c>x as any P</c>, <c>x as? any Q</c>): moved to a buffer of its
        ///     own and boxed by its type's boxer, or, of one that is an
        ///     existential already, the box it holds.
        /// </summary>
        SilValue? OpaqueAsExistential(BoundConversion conversion) {
            var type = Sub(conversion.Operand.Type);
            if (MetadataOf(type) is not { } metadata || Expression(conversion.Operand) is not { } value || value.Type is not OpaqueType opaque) {
                return Missing(conversion, $"the conversion to '{conversion.Type.ToDisplayString()}'");
            }

            generator.ErasesAny();
            var held = Emit(new BuiltinInst("opaque_type", SilType.Metadata, value, metadata));
            var buffer = Emit(new BuiltinInst("opaque_alloc", SilType.Address(opaque), metadata));
            Emit(new StoreInst(Owned(value), buffer, StoreKind.Init));
            var box = Emit(new BuiltinInst("opaque_box", generator.ExistentialBoxRoot(), buffer, metadata));
            if (conversion.ConversionKind == ConversionKind.Existential) {
                if (LowerType(conversion.Type) is not StructType layout || generator.TabledProtocols(layout) is not { } protocols) {
                    return Missing(conversion, $"the conversion to '{conversion.Type.ToDisplayString()}'");
                }

                var tables = protocols.Select(p => TableFor(CanonicalHere(type), p)).ToList();
                return Emit(new StructInst(layout, [box, held, ..tables]));
            }

            var target = CastTo(conversion);
            if (generator.ExistentialCast(target, conversion.Syntax) is not var (name, cast) || cast.Result is not EnumType optional) {
                return Missing(conversion, $"the cast to '{target.ToDisplayString()}'");
            }

            var result = Emit(new ApplyInst(Emit(new FunctionRefInst(name, cast)), optional, [box]));
            Destroy(box);
            if (Sub(conversion.Type) is OptionalTypeSymbol) {
                return result;
            }

            var present = function.AddBlock();
            var payload = Payload(present, optional.Cases[optional.CaseIndex("Some")].Payload);
            var absent = function.AddBlock();
            Jump(new SwitchEnumInst(result, [(optional.CaseIndex("Some"), present)], absent));
            _current = absent;
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), $"cast to '{target.ToDisplayString()}' failed"));
            Jump(new UnreachableInst());
            _current = present;
            var forced = Owned(payload);
            Destroy(result);
            return forced;
        }

        /// <summary>
        ///     <c>e as? T</c> and <c>e as! T</c> of an existential to a type
        ///     only its metadata knows: the runtime asked whether the box holds
        ///     one, which it copies to a slot of the type's.
        /// </summary>
        SilValue? OpaqueDowncast(BoundConversion conversion) {
            var target = CastTo(conversion);
            if (MetadataOf(target) is not { } metadata || LowerType(target) is not { } wanted || LowerType(new OptionalTypeSymbol(target)) is not EnumType optional
                || Borrow(conversion.Operand) is not { } existential) {
                return Missing(conversion, $"the cast to '{target.ToDisplayString()}'");
            }

            var slot = NewSlot(wanted);
            var isOne = Emit(new BuiltinInst("existential_cast", SilType.Bool, Emit(new StructExtractInst(existential, 0)), metadata, slot));
            End(existential);
            if (Sub(conversion.Type) is not OptionalTypeSymbol) {
                Emit(new CondFailInst(Emit(new BuiltinInst("not_Bool", SilType.Bool, isOne)), $"cast to '{target.ToDisplayString()}' failed"));
                return Emit(new LoadInst(slot, LoadKind.Take));
            }

            var result = NewSlot(optional);
            var yes = function.AddBlock();
            var no = function.AddBlock();
            var after = function.AddBlock();
            Jump(new CondBranchInst(isOne, yes, [], no, []));

            _current = yes;
            Emit(new StoreInst(Emit(new EnumInst(optional, optional.CaseIndex("Some"), Emit(new LoadInst(slot, LoadKind.Take)))), result, StoreKind.Init));
            Branch(after);

            _current = no;
            Emit(new StoreInst(Emit(new EnumInst(optional, optional.CaseIndex("None"), null)), result, StoreKind.Init));
            Branch(after);

            _current = after;
            return Emit(new LoadInst(result, LoadKind.Take));
        }

        /// <summary>
        ///     A local, or a stored field of one, for an unspecialised callee:
        ///     borrowed in its own storage, seen as the callee's type
        ///     parameter, with no copy and no slot of its own. Only where
        ///     nothing the call evaluates or passes can change it (see
        ///     <see cref="LendsInPlace" />); null for anything else.
        /// </summary>
        SilValue? GenericInPlace(BoundExpression argument, SilType type, SilType expected, List<SilValue> borrows) {
            if (!HasOwnPlace(argument) || UsesLent(argument) || LowerType(argument.Type) != type
                || Place(argument) is not { Type: AddressType { Pointee: var held } } place || held != type) {
                return null;
            }

            var lent = Hold(Emit(new LoadBorrowInst(Emit(new BuiltinInst("opaque_cast", SilType.Address(expected), place)))));
            borrows.Add(lent);
            return lent;
        }

        /// <summary>
        ///     A value a call borrows, for an unspecialised callee that takes
        ///     it by address: put in a slot that holds it unowned —
        ///     <c>alloc_stack [unmanaged]</c>, stored without a copy, never
        ///     destroyed — and borrowed from there, seen as the callee's type
        ///     parameter. Whoever lent the value keeps it alive until the
        ///     call is done: it is among <paramref name="borrows" /> already,
        ///     ended after the slot's borrow.
        /// </summary>
        SilValue GenericLent(SilValue value, SilType type, SilType expected, List<SilValue> borrows) {
            // An array or a slice of them, or a generic struct holding them by reference: the same bits, seen as the callee's.
            if (!SilType.IsIndirect(expected) && SilType.SameErasingOpaque(value.Type, expected, opaqueOnly: true)) {
                return Emit(new BuiltinInst("opaque_borrow_as", expected, value));
            }

            var slot = NewSlot(type);

            // A generator's slots are its iterator's fields, which own what
            // they hold: a copy there, destroyed after the call.
            if (Generator is not null) {
                Emit(new StoreInst(type.IsTrivial ? value : Emit(new CopyValueInst(value)), slot, StoreKind.Init));
                var copied = Emit(new LoadBorrowInst(Emit(new BuiltinInst("opaque_cast", SilType.Address(expected), slot))));
                borrows.Add(new WriteBack(() => {
                    Emit(new EndBorrowInst(copied));
                    if (!type.IsTrivial) {
                        Emit(new DestroyAddrInst(slot));
                    }
                }));
                return copied;
            }

            slot.Unmanaged = true;
            Emit(new StoreInst(value, slot, StoreKind.Assign));
            var lent = Hold(Emit(new LoadBorrowInst(Emit(new BuiltinInst("opaque_cast", SilType.Address(expected), slot)))));
            borrows.Add(lent);
            return lent;
        }

        /// <summary>True for a local in a slot of its own, or a stored field of a struct in one: an address reached with no code.</summary>
        bool HasOwnPlace(BoundExpression expression) =>
            expression switch {
                BoundLocalAccess local => _slots.ContainsKey(local.Local) && !_boxed.Contains(local.Local) && !_views.Contains(local.Local)
                                          && !_asyncLets.ContainsKey(local.Local),
                BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field } =>
                    !SilGen.IsGlobal(field) && Sub(receiver.Type).TypeKind == TypeKind.Struct && !IsInlineType(receiver.Type) && HasOwnPlace(receiver),
                _ => false,
            };

        /// <summary>
        ///     True when a call to an unspecialised callee may lend it the
        ///     locals it passes in their own storage: it changes nothing of
        ///     the caller's — no inout, out or reference parameter, no
        ///     mutating receiver — and its arguments are evaluated with no
        ///     code that could (<see cref="Quiet" />).
        /// </summary>
        bool LendsInPlace(FunctionSymbol callee, FunctionType type, BoundCall call) =>
            !callee.Parameters.Any(static p => p.IsByReference || p.Modifier is ParameterModifier.InOut or ParameterModifier.Out)
            && !(HasSelf(callee) && type.Parameters.Length > 0 && type.Parameters[0] is AddressType)
            && call.Arguments.All(Quiet);

        /// <summary>An expression evaluated with no call and no write: a literal, a variable, or a stored field of one.</summary>
        static bool Quiet(BoundExpression expression) =>
            expression switch {
                BoundLiteral or BoundLocalAccess or BoundParameterAccess or BoundSelfAccess => true,
                BoundFieldAccess { Receiver: { } receiver, Field.IsComputed: false } => Quiet(receiver),
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion => Quiet(conversion.Operand),
                _ => false,
            };

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
                contextType.Fields.Add(($"$metadata.{ParameterName(parameter)}", SilType.Metadata));
            }

            foreach (var (parameter, protocol) in shape.Requirements) {
                contextType.Fields.Add(($"$witnesses.{ParameterName(parameter)}.{protocol.Name}", SilType.WitnessTable));
            }

            // The code, generic as this function is.
            var line = source.GetLinePosition(closureExpression.Syntax.Span.Start).Line;
            var hidden = contextType.Fields.Skip(hiddenAt).Select(static f => f.Type).ToList();
            var bodyType = new FunctionType([..thick.Signature.Parameters, ..hidden, SilType.Object], thick.Signature.Result, thick.Signature.Thrown, isAsync: thick.Signature.IsAsync);
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
                    case SelfKey:
                        _selfAddress = slot;
                        break;
                }
            }
        }

        /// <summary>
        ///     A function, owned, as another type of it: what a generic type
        ///     keeps one in (<see cref="SilGen.StoredFunctionType" />), or what
        ///     the code reading it has it as — the same function where the two
        ///     differ only in the names of what they take, else one over it
        ///     that converts. The value itself, when it is neither's.
        /// </summary>
        SilValue AsFunction(SilValue value, SilType wanted) {
            if (value.Type is not ThickFunctionType given || wanted is not ThickFunctionType target || given == target) {
                return value;
            }

            if (SilType.SameErasingOpaque(given, target)) {
                return Emit(new BuiltinInst("opaque_take_as", target, Owned(value)));
            }

            if (generator.Reabstraction(given, target) is not var (thunk, context)) {
                return value;
            }

            var holder = Emit(new AllocRefInst(context));
            Emit(new StoreInst(Owned(value), Emit(new RefElementAddrInst(holder, 0)), StoreKind.Init));
            return Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk, target.Implementation)), holder, target));
        }

        /// <summary>A stored field read, as the type it has here: a function a generic type keeps in its own convention, as this code's.</summary>
        SilValue? AsRead(SilValue? read, BoundFieldAccess access) =>
            read is { Type: ThickFunctionType kept } && generator.StoredFunctionType(access.Field) is not null && LowerType(access.Type) is ThickFunctionType here && kept != here
                ? AsFunction(read, here)
                : read;

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
        ///     for one that changes it; else the value, already borrowed for
        ///     the call, lent from a slot that holds it without a copy.
        /// </summary>
        SilValue GenericReceiver(SilValue self, SilType expected, List<SilValue> borrows) =>
            expected is AddressType ? Emit(new BuiltinInst("opaque_cast", expected, self)) : GenericLent(self, self.Type, expected, borrows);

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
                    generator.Describer(given, lowered, at, callee is null || generator.DebugDescribesValues(callee));
                }

                // An existential a callee is given the type of, 'resolve((any P).self)', may be cast to there.
                if (callee is not null && given is ExistentialTypeSymbol && callee.Parameters.Any(p => p.Type is MetatypeTypeSymbol { InstanceType: var instance } && ReferenceEquals(instance, parameter))) {
                    generator.Caster(given, at);
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
