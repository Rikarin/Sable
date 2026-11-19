using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>What a generator's result is: the protocol its <c>some</c> names.</summary>
public enum GeneratorKind {
    /// <summary><c>some Sequence&lt;T&gt;</c>: each <c>makeIterator()</c> runs the body afresh.</summary>
    Sequence,

    /// <summary><c>some Iterator&lt;T&gt;</c>: the body runs once, as <c>next()</c> is called.</summary>
    Iterator,

    /// <summary><c>some AsyncSequence&lt;T&gt;</c>, of an <c>async</c> body.</summary>
    AsyncSequence,

    /// <summary><c>some AsyncIterator&lt;T&gt;</c>, of an <c>async</c> body.</summary>
    AsyncIterator,
}

/// <summary>
///     A generator (spec/SYNTAX.md §5, "Generators"): a function, method,
///     computed property or closure whose result is <c>some Sequence&lt;T&gt;</c>
///     (or an iterator, or their async kinds) and whose body yields. Its
///     result hides two types the compilation makes for it: the sequence, a
///     struct holding what the call captured — its arguments, and
///     <c>self</c> — and the iterator, which runs the body a step at a time.
///     They are generic over every type parameter the body may name, the
///     generator's own and those of the types around it, so that each
///     construction is the generator for those types.
/// </summary>
/// <remarks>
///     Only their members are symbols here: <c>makeIterator()</c> and
///     <c>next()</c>, through which conformance and generic code find them.
///     What they hold, and the code of both, are SIL lowering's: the iterator
///     keeps the body's state between steps (SilGen.Generators.cs).
/// </remarks>
public sealed class GeneratorSymbols {
    internal GeneratorSymbols(
        FunctionSymbol function,
        GeneratorKind kind,
        TypeSymbol element,
        ImmutableArray<TypeParameterSymbol> typeParameters,
        NamedTypeSymbol? sequenceType,
        NamedTypeSymbol iteratorType,
        FunctionSymbol? makeIterator,
        FunctionSymbol next
    ) {
        Function = function;
        Kind = kind;
        Element = element;
        TypeParameters = typeParameters;
        SequenceType = sequenceType;
        IteratorType = iteratorType;
        MakeIterator = makeIterator;
        Next = next;
    }

    /// <summary>The generator: what declares the body.</summary>
    public FunctionSymbol Function { get; }

    public GeneratorKind Kind { get; }

    /// <summary>What each <c>yield</c> gives, in terms of <see cref="TypeParameters" />.</summary>
    public TypeSymbol Element { get; }

    /// <summary>Every type parameter the body may name: the sequence's and the iterator's own.</summary>
    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; }

    /// <summary>The sequence a call makes; null for an iterator generator, whose call makes the iterator.</summary>
    public NamedTypeSymbol? SequenceType { get; }

    /// <summary>The iterator, which runs the body.</summary>
    public NamedTypeSymbol IteratorType { get; }

    /// <summary>The sequence's <c>makeIterator()</c> (or <c>makeAsyncIterator()</c>).</summary>
    public FunctionSymbol? MakeIterator { get; }

    /// <summary>The iterator's <c>next()</c>.</summary>
    public FunctionSymbol Next { get; }

    public bool IsAsync => Kind is GeneratorKind.AsyncSequence or GeneratorKind.AsyncIterator;

    /// <summary>What a call gives: the sequence, or the iterator, of the generator's own type parameters.</summary>
    public NamedTypeSymbol Result => TypeBinder.OwnConstruction(SequenceType ?? IteratorType);
}

public sealed partial class Compilation {
    readonly Dictionary<FunctionSymbol, GeneratorSymbols> _generators = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Symbol, GeneratorSymbols> _generatorParts = new(ReferenceEqualityComparer.Instance);
    readonly List<GeneratorSymbols> _generatorList = [];
    readonly object _generatorLock = new();

    /// <summary>Every generator whose body has been bound, in the order they were.</summary>
    public IReadOnlyList<GeneratorSymbols> Generators {
        get {
            lock (_generatorLock) {
                return [.._generatorList];
            }
        }
    }

    /// <summary>
    ///     The generator a symbol is, or is part of: the generator itself, its
    ///     sequence or iterator type, or their <c>makeIterator()</c> or
    ///     <c>next()</c>. Null for anything else.
    /// </summary>
    public GeneratorSymbols? GetGenerator(Symbol symbol) {
        if (symbol is NamedTypeSymbol named) {
            symbol = named.OriginalDefinition;
        }

        lock (_generatorLock) {
            return symbol is FunctionSymbol function && _generators.TryGetValue(function, out var generator)
                ? generator
                : _generatorParts.GetValueOrDefault(symbol);
        }
    }

    /// <summary>The generator a function's body makes it, made once: its types and their members.</summary>
    internal GeneratorSymbols DeclareGenerator(FunctionSymbol function, GeneratorKind kind, TypeSymbol element, TypeSymbol? thrown) {
        lock (_generatorLock) {
            if (_generators.TryGetValue(function, out var existing)) {
                return existing;
            }

            var typeParameters = GeneratorTypeParameters(function);

            // In a protocol's extension the body names the protocol's
            // associated types bare; through the iterator they are Self's.
            if (typeParameters is [{ ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } owner } self, ..]
                && ReferenceEquals(owner.SelfTypeParameter, self)) {
                element = AssociatedTypes.ThroughBase(owner, self).Apply(element);
            }
            var module = ModuleOf(function);
            var position = function.DeclaringSyntax.FirstOrDefault()?.Span.Start ?? _generatorList.Count;
            var name = (function.Name.Length > 0 ? function.Name : "closure") + "$" + position;

            var isAsync = kind is GeneratorKind.AsyncSequence or GeneratorKind.AsyncIterator;
            var iterator = new NamedTypeSymbol(name + "$Iterator", TypeKind.Struct, module, accessibility: Accessibility.Public) {
                TypeParameters = typeParameters,
            };
            var iteratorProtocol = isAsync ? CoreProtocol("AsyncIterator") : CoreProtocol("Iterator");
            iterator.BaseTypes = iteratorProtocol is null ? [] : [iteratorProtocol];
            iterator.AddMember(Alias("Element", iterator, element));

            var next = new FunctionSymbol("next", iterator, null, Accessibility.Public, FunctionKind.Ordinary) {
                IsMutating = true,
                IsAsync = isAsync,
            };
            var optional = new OptionalTypeSymbol(element);
            next.ReturnTypeResolver = () => optional;
            next.ThrownTypeResolver = () => thrown;
            iterator.AddMember(next);

            NamedTypeSymbol? sequence = null;
            FunctionSymbol? makeIterator = null;
            if (kind is GeneratorKind.Sequence or GeneratorKind.AsyncSequence) {
                sequence = new NamedTypeSymbol(name + "$Sequence", TypeKind.Struct, module, accessibility: Accessibility.Public) {
                    TypeParameters = typeParameters,
                };
                var sequenceProtocol = isAsync ? CoreProtocol("AsyncSequence") : CoreProtocol("Sequence");
                sequence.BaseTypes = sequenceProtocol is null ? [] : [sequenceProtocol];
                var made = TypeBinder.OwnConstruction(iterator);
                sequence.AddMember(Alias("Element", sequence, element));
                sequence.AddMember(Alias("Iter", sequence, made));
                makeIterator = new FunctionSymbol(isAsync ? "makeAsyncIterator" : "makeIterator", sequence, null, Accessibility.Public, FunctionKind.Ordinary);
                makeIterator.ReturnTypeResolver = () => made;
                sequence.AddMember(makeIterator);
            }

            var generator = new GeneratorSymbols(function, kind, element, typeParameters, sequence, iterator, makeIterator, next);
            _generators[function] = generator;
            _generatorParts[iterator] = generator;
            _generatorParts[next] = generator;
            if (sequence is not null) {
                _generatorParts[sequence] = generator;
                _generatorParts[makeIterator!] = generator;
            }

            _generatorList.Add(generator);
            return generator;
        }
    }

    /// <summary>
    ///     The kind of generator a result type makes a yielding body, and what
    ///     it yields: <c>some Sequence&lt;Int&gt;</c> is a sequence of Ints.
    ///     Null for a result that names no sequence or iterator of Sable.Core's,
    ///     or names no element.
    /// </summary>
    internal (GeneratorKind Kind, TypeSymbol Element)? GeneratorKindOf(TypeSymbol result) {
        if (result is not OpaqueTypeSymbol { Constraint: NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol } opaque
            || opaque.Arguments.IsEmpty) {
            return null;
        }

        foreach (var (name, kind) in s_generatorProtocols) {
            if (CoreProtocol(name) is { } core && ReferenceEquals(core, protocol.OriginalDefinition)) {
                return (kind, opaque.Arguments[0]);
            }
        }

        return null;
    }

    static readonly (string Protocol, GeneratorKind Kind)[] s_generatorProtocols = [
        ("Sequence", GeneratorKind.Sequence),
        ("Iterator", GeneratorKind.Iterator),
        ("AsyncSequence", GeneratorKind.AsyncSequence),
        ("AsyncIterator", GeneratorKind.AsyncIterator),
    ];

    static AliasSymbol Alias(string name, NamedTypeSymbol container, TypeSymbol type) =>
        new(name, container, null, Accessibility.Public) { TypeResolver = () => type };

    static ModuleSymbol? ModuleOf(Symbol symbol) {
        for (var current = (Symbol?)symbol; current is not null; current = current.ContainingSymbol) {
            if (current is ModuleSymbol module) {
                return module;
            }
        }

        return null;
    }

    /// <summary>
    ///     Every type parameter a body may name, outermost first: those of the
    ///     type it is a member of (a protocol's Self among them), then each
    ///     enclosing function's and its own.
    /// </summary>
    static ImmutableArray<TypeParameterSymbol> GeneratorTypeParameters(FunctionSymbol function) {
        var parameters = new List<TypeParameterSymbol>();
        for (var current = (Symbol?)function; current is not null; current = current.ContainingSymbol) {
            switch (current) {
                case FunctionSymbol enclosing:
                    parameters.InsertRange(0, enclosing.TypeParameters);
                    continue;

                case FieldSymbol:
                    continue;

                case ExtensionSymbol extension:
                    Owner(extension.ExtendedType.OriginalDefinition);
                    return [..parameters];

                case NamedTypeSymbol type:
                    Owner(type.OriginalDefinition);
                    return [..parameters];
            }
        }

        return [..parameters];

        void Owner(NamedTypeSymbol type) {
            parameters.InsertRange(0, type.TypeParameters);
            if (type is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self }) {
                parameters.Insert(0, self);
            }
        }
    }
}
