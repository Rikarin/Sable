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
///         protocols or to another such function. Anything else — a
///         <c>T[]</c>, a <c>T?</c>, a closure over a <c>T</c>, a method of a
///         generic type — is still specialised for its type arguments, so a
///         program builds either way.
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

    /// <summary>True for a type parameter of the unspecialised code being lowered.</summary>
    internal bool IsOpaque(TypeSymbol type) => type is TypeParameterSymbol parameter && _opaque.Contains(parameter);

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
                Console.Error.WriteLine($"specialised {QualifiedName(symbol)}: {refused}");
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

    /// <summary>True when the type is one of <paramref name="parameters" />, or is made of one.</summary>
    internal static bool Mentions(TypeSymbol type, IReadOnlyCollection<TypeParameterSymbol> parameters) =>
        parameters.Count > 0 && (type switch {
            TypeParameterSymbol parameter => parameters.Contains(parameter),
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
            : symbol.IsRethrows ? "rethrows"
            : symbol.IsAsync ? "async"
            : symbol.FunctionKind != FunctionKind.Ordinary ? $"{(symbol.FunctionKind.ToString()[0] is 'A' or 'E' or 'I' or 'O' or 'U' ? "an" : "a")} {symbol.FunctionKind.ToString().ToLowerInvariant()}"
            : symbol.ForeignName is not null ? "foreign"
            : !symbol.OpaqueTypeParameters.IsEmpty ? "a 'some P' parameter"
            : _compilation.GetGenerator(symbol) is not null ? "a generator"
            : _compilation.GetDerivedBody(symbol) is not null || _compilation.GetLazyBody(symbol) is not null
              || _compilation.GetInheritedInitializerBody(symbol) is not null ? "a body the compiler writes"
            : !symbol.SameTypeRequirements.IsEmpty || !symbol.ConformanceRequirements.IsEmpty ? "a 'where' clause"
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
            refused = "a member of a generic type";
            return null;
        }

        own.AddRange(symbol.TypeParameters);
        if (!all.SequenceEqual(own)) {
            refused = "type parameters from around it";
            return null;
        }

        foreach (var parameter in symbol.TypeParameters) {
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

        // A protocol's associated types, which its extension names bare, are not passed yet.
        var associated = owner is { TypeKind: TypeKind.Protocol } ? ProtocolClosure([owner]).SelectMany(static p => p.AssociatedTypes).ToList() : [];
        bool Passable(TypeSymbol type) =>
            !Mentions(type, associated) && (!Mentions(type, own) || type is TypeParameterSymbol || type is MetatypeTypeSymbol { InstanceType: TypeParameterSymbol });

        foreach (var parameter in symbol.Parameters) {
            if (!Passable(parameter.Type)
                || Mentions(parameter.Type, own) && (parameter.IsVariadic || parameter.IsByReference || parameter.Modifier is not (ParameterModifier.None or ParameterModifier.In))) {
                refused = $"the parameter '{parameter.Name}: {parameter.Type.ToDisplayString()}'";
                return null;
            }
        }

        if (!Passable(symbol.ReturnType) || symbol.ThrownType is { } thrown && Mentions(thrown, own)) {
            refused = $"the result '{symbol.ReturnType.ToDisplayString()}'";
            return null;
        }

        return new([..own], [..requirements]);
    }

    bool BodyAllows(FunctionSymbol symbol, GenericShape shape) {
        var (syntax, model) = _declarations[symbol];
        if (BodyModel(model, syntax).GetBoundNode(syntax) is not BoundBlock body) {
            return false;
        }

        var checker = new ShapeChecker(this, shape, symbol);
        if (checker.Allows(body, false)) {
            if (Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 }) {
                Console.Error.WriteLine($"unspecialised {QualifiedName(symbol)}");
            }

            return true;
        }

        // Why each function that could be is not, for whoever is widening what can be.
        if (Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 } && checker.Refused is { } refused) {
            Console.Error.WriteLine($"specialised {QualifiedName(symbol)}: {refused.GetType().Name} '{refused.Syntax.ToString().Trim().ReplaceLineEndings(" ")}'");
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

        bool IsOwn(TypeSymbol type) => type is TypeParameterSymbol parameter && _own.Contains(parameter);

        /// <summary>A type a value may have here: a type parameter, its metatype, or one that mentions none.</summary>
        bool Simple(TypeSymbol type) => !Mentions(type) || IsOwn(type) || type is MetatypeTypeSymbol { InstanceType: var instance } && IsOwn(instance);

        /// <summary>The innermost node that kept the body from being unspecialised, for SABLE_GENERICS_TRACE.</summary>
        public BoundNode? Refused { get; private set; }

        public bool Allows(BoundNode node, bool inClosure) {
            var allowed = AllowsHere(node, inClosure);
            if (!allowed) {
                Refused ??= node;
            }

            return allowed;
        }

        bool AllowsHere(BoundNode node, bool inClosure) {
            if (node is BoundClosure or BoundLocalFunction or BoundUnboundClosure) {
                inClosure = true;
            }

            if (node is BoundExpression expression && Mentions(expression.Type) && (inClosure || !Simple(expression.Type) || !Produces(expression))) {
                return false;
            }

            switch (node) {
                case BoundLocalDeclaration declaration when Mentions(declaration.Local.Type) && (inClosure || !IsOwn(declaration.Local.Type)):
                case BoundPatternDeclaration pattern when Descendants(pattern).Any(n => n is BoundBindingPattern binding && Mentions(binding.Local.Type)):
                case BoundTypeTest test when Mentions(test.TestedType):
                case BoundForEach { Element: { } element } when Mentions(element.Type):
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
            }

            foreach (var child in node.Children) {
                if (child is BoundExpression value && Mentions(value.Type) && !Consumes(node, value)) {
                    return false;
                }

                if (!Allows(child, inClosure)) {
                    return false;
                }
            }

            return true;
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
                BoundCall or BoundMetatype or BoundTypeExpression or BoundConditional or BoundAssignment or BoundTry => true,
                BoundBinaryOperator { Method: not null } or BoundUnaryOperator { Method: not null } => true,
                BoundFieldAccess access => PropertyAllowed(access),
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion => conversion.Type.Equals(conversion.Operand.Type),
                _ => false,
            };

        bool Consumes(BoundNode parent, BoundExpression child) =>
            parent switch {
                BoundLocalDeclaration or BoundExpressionStatement or BoundReturn => true,
                BoundAssignment assignment => ReferenceEquals(child, assignment.Value)
                                              || assignment.Target is BoundLocalAccess or BoundParameterAccess && ReferenceEquals(child, assignment.Target),
                BoundCall or BoundBinaryOperator or BoundUnaryOperator or BoundTry => true,
                BoundFieldAccess access => ReferenceEquals(child, access.Receiver) && PropertyAllowed(access),
                BoundConditional conditional => !ReferenceEquals(child, conditional.Condition),
                BoundConversion { ConversionKind: ConversionKind.Identity } => true,
                // Not inside a protocol's own 'debugDescription' (Error's
                // '"\(self)"'): a describer would call it back.
                BoundInterpolatedString interpolated => interpolated.Parts.IndexOf(child) is var at and >= 0 && interpolated.Formats[at] is null && !DescribesSelf,
                _ => false,
            };

        bool DescribesSelf => checkedSymbol is { Name: "debugDescription" } && OwnerOf(checkedSymbol) is { TypeKind: TypeKind.Protocol };

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

            if (!IsGeneric(callee) || generator.UnspecialisedShape(callee) is not { } calleeShape) {
                return false;
            }

            // A protocol extension's member: on a value of a type parameter of
            // this function, or on self, its Self that type parameter.
            var calleeSelf = OwnerOf(callee)?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } protocolSelf } ? protocolSelf : null;
            var receiverType = call.Receiver switch {
                null => calleeSelf is not null && IsOwn(calleeSelf) ? calleeSelf : null,
                BoundTypeExpression type => type.ReferencedType,
                { Type: MetatypeTypeSymbol metatype } => metatype.InstanceType,
                { } receiver => receiver.Type,
            };
            if (call.Receiver is { } onValue && Mentions(onValue.Type) && (calleeSelf is null || receiverType is null || !IsOwn(receiverType))) {
                return false;
            }

            // One that changes self changes a local, or this function's own self.
            if (calleeSelf is not null && callee.IsMutating && receiverType is not null && IsOwn(receiverType)
                && call.Receiver is not (BoundLocalAccess { Local.IsMutable: true } or null or BoundSelfAccess) || callee.IsMutating && call.Receiver is null or BoundSelfAccess && !checkedSymbol.IsMutating) {
                return false;
            }

            foreach (var parameter in calleeShape.Parameters) {
                if ((ReferenceEquals(parameter, calleeSelf) ? call.Substitution.Lookup(parameter) ?? receiverType : call.Substitution.Lookup(parameter)) is not { } bound) {
                    return false;
                }

                if (!Mentions(bound)) {
                    continue;
                }

                // What the callee requires of it, this function has: its own tables, or what they refine.
                if (bound is not TypeParameterSymbol given || !IsOwn(given)) {
                    return false;
                }

                var has = ProtocolClosure(shape.ProtocolsOf(given));
                if (!calleeShape.ProtocolsOf(parameter).All(needed => has.Any(p => ReferenceEquals(p, needed)))) {
                    return false;
                }
            }

            return true;
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

            if (selfType is not TypeParameterSymbol parameter || !IsOwn(parameter)
                || !ProtocolClosure(shape.ProtocolsOf(parameter)).Any(p => ReferenceEquals(p, protocol))) {
                return false;
            }

            // A method that changes its value changes it in a local, or a
            // protocol extension's own self, when it may change it.
            if (callee.IsMutating && call.Receiver is not BoundLocalAccess { Local.IsMutable: true }
                && !(call.Receiver is null or BoundSelfAccess && checkedSymbol.IsMutating)) {
                return false;
            }

            foreach (var (declaredParameter, argument) in callee.Parameters.Zip(call.Arguments)) {
                if (declaredParameter.Type.Equals(self) ? !argument.Type.Equals(parameter) : Mentions(argument.Type)) {
                    return false;
                }
            }

            return callee.ReturnType.Equals(self) ? call.Type.Equals(parameter) : !Mentions(call.Type);
        }

        /// <summary>A property requirement of a type parameter's protocols, read on one: through its getter's generic entry.</summary>
        bool PropertyAllowed(BoundFieldAccess access) {
            var field = access.Field;
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

            return on is TypeParameterSymbol parameter && IsOwn(parameter) && field.IsStatic == access.Receiver is BoundTypeExpression or { Type: MetatypeTypeSymbol }
                   && ProtocolClosure(shape.ProtocolsOf(parameter)).Any(p => ReferenceEquals(p, protocol))
                   && (field.Type.Equals(protocol.SelfTypeParameter) ? access.Type.Equals(parameter) : !Mentions(access.Type));
        }

        bool BinaryAllowed(BoundBinaryOperator binary) {
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
        using (Opaque(shape)) {
            signature = SignatureOf(symbol, at, TypeSubstitution.Empty);
        }

        if (signature is null) {
            return null;
        }

        // A result of a type parameter's type is written where the caller says, before the hidden parameters.
        var hidden = Enumerable.Repeat(SilType.Metadata, shape.Parameters.Length).Concat(Enumerable.Repeat(SilType.WitnessTable, shape.Requirements.Length)).ToList();
        var result = signature.Result;
        if (result is OpaqueType) {
            hidden.Insert(0, SilType.Address(result));
            result = SilType.Unit;
        }

        var owned = signature.Owned.IsEmpty ? default : signature.Owned.Concat(hidden.Select(static _ => false)).ToImmutableArray();
        var type = new FunctionType([..signature.Parameters, ..hidden], result, signature.Thrown, owned, signature.IsAsync);
        _instances[name] = type;
        _instanceOrigins[name] = (symbol, TypeSubstitution.Empty, _loweringInstance);
        _unspecialised[name] = (symbol, shape);
        _pending.Enqueue((symbol, name, type, TypeSubstitution.Empty));
        return (name, type);
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
        if (field.Type.Equals(self)) {
            return new FunctionType([..taken, SilType.Address(opaque), SilType.Metadata], SilType.Unit);
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
            type.Equals(self) ? opaque : Mentions(type, own) ? null : Lower(type);

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
                case ParameterModifier.InOut when type is not OpaqueType:
                    parameters.Add(SilType.Address(type));
                    break;
                default:
                    return null;
            }
        }

        if (Slot(requirement.ReturnType) is not { } result) {
            return null;
        }

        // A result of Self is written where the caller says.
        if (result is OpaqueType) {
            parameters.Add(SilType.Address(result));
            result = SilType.Unit;
        }

        parameters.Add(SilType.Metadata);

        SilType? thrown = null;
        if (requirement.ThrownType is { } thrownType && !thrownType.Equals(Core.Never) && (thrown = Slot(thrownType)) is null or OpaqueType) {
            return null;
        }

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
    internal bool GenericTable(NamedTypeSymbol concrete, NamedTypeSymbol protocol, SyntaxNode at) {
        if (Lower(concrete) is not { } value) {
            return false;
        }

        var declared = DeclaredProtocols(concrete.OriginalDefinition).ToHashSet(StringComparer.Ordinal);
        if (value is not NominalType) {
            declared.UnionWith(_module.PrimitiveConformances.GetValueOrDefault(value.ToString()) ?? []);
        }

        var closure = ProtocolClosure([protocol]);
        if (!closure.TrueForAll(p => declared.Contains(QualifiedName(p)) && concrete.Satisfies(p))) {
            return false;
        }

        foreach (var each in closure) {
            var name = QualifiedName(each);
            var table = _module.FindWitnessTable(value, name);
            if (table is null) {
                if (!WitnessTable(concrete, value, each, at) || (table = _module.FindWitnessTable(value, name)) is null) {
                    table = new SilWitnessTable(value, name) { HasEntries = false };
                    AssociatedTypes(table, concrete, each);
                    _module.AddWitnessTable(table);
                }
            }

            GenericEntries(table, concrete, value, each, at);
        }

        return true;
    }

    /// <summary>
    ///     Fills a table's entries in the generic calling convention, once, in
    ///     an unspecialised build: every table it makes has them, whatever
    ///     asked for the table first.
    /// </summary>
    void GenericEntries(SilWitnessTable table, NamedTypeSymbol concrete, SilType value, NamedTypeSymbol protocol, SyntaxNode at) {
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
    void AssociatedTypes(SilWitnessTable table, NamedTypeSymbol concrete, NamedTypeSymbol protocol) {
        foreach (var associated in protocol.OriginalDefinition.AssociatedTypes) {
            if (_compilation.AssociatedTypeOf(concrete, associated) is not { } bound || Lower(bound) is not { } lowered) {
                continue;
            }

            var index = table.AssociatedTypes.Count;
            table.AssociatedTypes.Add((associated.Name, lowered));
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
    string? GenericWitness(NamedTypeSymbol concrete, SilType value, NamedTypeSymbol protocol, string key, Symbol member, FunctionType type, SyntaxNode at) {
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
        var call = new BoundCall(syntax, receiver, requirement, [..requirement.Parameters.Select(p => (BoundExpression)new BoundParameterAccess(syntax, p))], requirement.ReturnType) {
            Substitution = substitution,
        };
        BoundStatement[] statements = requirement.ReturnType.Equals(Core.Void)
            ? [new BoundExpressionStatement(syntax, call), new BoundReturn(syntax, null)]
            : [new BoundReturn(syntax, call)];

        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
        };
        var bridge = new Bridge(HasSelf(requirement), [..requirement.Parameters.Where(p => p.Type.Equals(self))], requirement.ReturnType.Equals(self));
        var lowering = new FunctionLowering(this, requirement, function, source, substitution) { Bridged = bridge with { Self = concrete } };
        lowering.Lower(new BoundBlock(syntax, [], [..statements]));
        if (lowering.Failed) {
            return null;
        }

        function.RemoveUnreachableBlocks();
        _module.Add(function);
        return name;
    }

    /// <summary>
    ///     A property requirement's getter for a type in the generic calling
    ///     convention: lowered as <c>print</c>'s instance would be if its body
    ///     read the property of its one parameter — the value, taken out of
    ///     the value it is given — or, for a static one, of the type.
    /// </summary>
    string? GenericGetter(NamedTypeSymbol concrete, SilType value, NamedTypeSymbol protocol, FieldSymbol field, FunctionType type, SyntaxNode at) {
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
        var bridge = new Bridge(false, field.IsStatic ? [] : [print.Parameters[0]], field.Type.Equals(self)) {
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

        /// <summary>What a bridged function arrived with, waiting for the entry block: self, and the parameters of Self's type.</summary>
        SilArgument? _bridgeSelf;

        readonly List<(ParameterSymbol Parameter, SilArgument Value)> _bridgeParameters = [];

        /// <summary>True for a type parameter of this unspecialised function.</summary>
        bool IsOpaqueHere(TypeSymbol type) => type is TypeParameterSymbol parameter && _genericMetadata.ContainsKey(parameter);

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
                if (function.Type.Result == SilType.Unit && next < parameterTypes.Length && parameterTypes[next] is AddressType { Pointee: OpaqueType }
                    && generator.IsOpaque(symbol.ReturnType)) {
                    _indirectResult = _entry.AddArgument(parameterTypes[next++]);
                    NameArgument("$out");
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

            if (Bridged.OpaqueResult && next < parameterTypes.Length && parameterTypes[next] is AddressType { Pointee: OpaqueType }) {
                _indirectResult = _entry.AddArgument(parameterTypes[next++]);
                NameArgument("$out");
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
            SilValue into = value.Type is OpaqueType ? result : Emit(new BuiltinInst("opaque_cast", SilType.Address(value.Type), result));
            Emit(new StoreInst(owned, into, StoreKind.Init));
            return null;
        }

        /// <summary>A type parameter's witness table for a protocol: the one given, the one a given one's refines through its bases, or the registry's.</summary>
        SilValue TableFor(TypeParameterSymbol parameter, NamedTypeSymbol protocol) {
            protocol = protocol.OriginalDefinition;
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

            return Emit(new BuiltinInst($"witness_table:{QualifiedName(protocol)}", SilType.WitnessTable, _genericMetadata[parameter]));
        }

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

            return LowerType(type) is { } lowered ? Emit(new MetadataInst(lowered)) : null;
        }

        /// <summary>The type parameter of this function a requirement's call is on, as Self; null for a call on anything else.</summary>
        TypeParameterSymbol? OpaqueSelf(BoundCall call) {
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
            return selfType is TypeParameterSymbol parameter && _genericMetadata.ContainsKey(parameter) ? parameter : null;
        }

        /// <summary>
        ///     Where a call writes a result of a type parameter's type: a slot
        ///     of the type it is here, which the call is given the address of —
        ///     seen as the callee's type parameter, when the type is known here.
        ///     Null when the result is not one.
        /// </summary>
        (SilValue Passed, AllocStackInst Slot)? GenericOut(SilType expected, TypeSymbol given) {
            if (expected is not AddressType { Pointee: OpaqueType } || LowerType(given) is not { } type) {
                return null;
            }

            var slot = NewSlot(type);
            return (type is OpaqueType ? slot : (SilValue)Emit(new BuiltinInst("opaque_cast", expected, slot)), slot);
        }

        /// <summary>The result a call wrote where it was told: taken out of the slot.</summary>
        SilValue? TakeOut(SilValue? invoked, AllocStackInst slot) =>
            invoked is null || _current is null ? invoked : Emit(new LoadInst(slot, slot.ElementType.IsTrivial ? LoadKind.Trivial : LoadKind.Take));

        /// <summary>
        ///     A requirement called on a type parameter in unspecialised code:
        ///     its witness, from the type parameter's table, given the value
        ///     and the type's metadata, and for a result of Self, where to put it.
        /// </summary>
        SilValue? GenericRequirementCall(BoundCall call, TypeParameterSymbol parameter) {
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
            SilValue? metadata = call.Receiver is { Type: MetatypeTypeSymbol } and not BoundTypeExpression ? Expression(call.Receiver) : _genericMetadata[parameter];
            if (metadata is null) {
                EndAll(borrows);
                return null;
            }

            var result = GenericOut(type.Parameters[arguments.Count], parameter);
            if (result is { } into) {
                arguments.Add(into.Passed);
            }

            arguments.Add(metadata);
            var invoked = Invoke(code, type, arguments, borrows, call.Syntax);
            return result is { } written ? TakeOut(invoked, written.Slot) : invoked;
        }

        /// <summary>The type parameter of this function a property requirement is read on, or null for any other read.</summary>
        internal TypeParameterSymbol? GenericPropertyOf(BoundFieldAccess access) {
            if (_genericMetadata.Count == 0 || access.Field.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol }) {
                return null;
            }

            var on = access.Receiver switch {
                BoundTypeExpression type => Sub(type.ReferencedType),
                { Type: MetatypeTypeSymbol metatype } => Sub(metatype.InstanceType),
                { } receiver => Sub(receiver.Type),
                null => null,
            };
            return on is TypeParameterSymbol parameter && _genericMetadata.ContainsKey(parameter) ? parameter : null;
        }

        /// <summary>A property requirement read on a type parameter: its getter, from the table, given the value and the metadata.</summary>
        SilValue? GenericPropertyRead(BoundFieldAccess access, TypeParameterSymbol parameter) {
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

            var result = GenericOut(type.Parameters[arguments.Count], parameter);
            if (result is { } into) {
                arguments.Add(into.Passed);
            }

            arguments.Add(_genericMetadata[parameter]);
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

                if (Mentions(given, [.._genericMetadata.Keys]) || LowerType(given) is not { } lowered || lowered == SilType.Unit
                    || lowered is NominalType { IsNoncopyable: true } || given.Equals(Core.Never)) {
                    return false;
                }

                foreach (var protocol in found.ProtocolsOf(parameter)) {
                    if (given is not NamedTypeSymbol named || !generator.GenericTable(named, protocol, at)) {
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
            if (IsOpaqueHere(given)) {
                if (Borrow(argument) is not { } value) {
                    return null;
                }

                borrows.Add(value);
                return value;
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

        /// <summary>
        ///     What a call binds its callee's type parameters to, with a
        ///     protocol extension's Self, which a call on this function's own
        ///     self leaves unsaid, bound to this function's Self.
        /// </summary>
        TypeSubstitution OwnSelfBound(FunctionSymbol callee, TypeSubstitution bound) {
            if (OwnerOf(callee)?.OriginalDefinition is not { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self } || bound.Lookup(self) is not null || !IsOpaqueHere(self)) {
                return bound;
            }

            var map = bound.Parameters.ToDictionary(p => p, p => bound.Lookup(p)!);
            map[self] = self;
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
        IEnumerable<SilValue>? GenericHiddenArguments(GenericShape shape, TypeSubstitution bound, SyntaxNode at) {
            var hidden = new List<SilValue>();
            foreach (var parameter in shape.Parameters) {
                var given = bound.Lookup(parameter)!;
                if (MetadataOf(given) is not { } metadata) {
                    return null;
                }

                if (!IsOpaqueHere(given) && LowerType(given) is { } lowered) {
                    generator.Describer(given, lowered, at);
                }

                hidden.Add(metadata);
            }

            foreach (var (parameter, protocol) in shape.Requirements) {
                var given = bound.Lookup(parameter)!;
                hidden.Add(IsOpaqueHere(given)
                    ? TableFor((TypeParameterSymbol)given, protocol)
                    : Emit(new BuiltinInst($"witness_table_known:{QualifiedName(protocol)}", SilType.WitnessTable, MetadataOf(given)!)));
            }

            return hidden;
        }
    }
}
