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
///     parameter's type is a <see cref="SilType.Box" /> — the runtime's box,
///     sized by the type's metadata — which is shared when copied and changed
///     in place only once it is its holder's own; the function takes each
///     type parameter's metadata, and a witness table for each protocol it
///     is constrained to, after its own parameters. A requirement is called
///     through the table's entries in the generic calling convention
///     (<see cref="SilWitnessTable.GenericEntries" />), which take the value's
///     box and Self's metadata.
/// </summary>
/// <remarks>
///     <para>
///         A function is compiled this way when the build asks for it
///         (<see cref="GenericsMode.Unspecialised" />) and every use it makes
///         of a type parameter is one boxes can serve (<see cref="ShapeChecker" />):
///         values of the type parameter itself, passed, stored, returned,
///         compared by metatype, written out, given to a requirement of its
///         protocols or to another such function. Anything else — a
///         <c>T[]</c>, a <c>T?</c>, a closure over a <c>T</c>, a method of a
///         generic type — is still specialised for its type arguments, so a
///         program builds either way.
///     </para>
///     <para>
///         Where the types are known, a call boxes each argument of a type
///         parameter's type and takes the result back out of its box; inside
///         unspecialised code, the boxes are passed on as they are.
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

    /// <summary>The type parameters of the unspecialised code being lowered: each lowers to a <see cref="SilType.Box" />.</summary>
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
    ///     its signature or its body uses a type parameter in a way boxes
    ///     cannot serve yet.
    /// </summary>
    internal GenericShape? UnspecialisedShape(FunctionSymbol symbol) {
        if (_generics != GenericsMode.Unspecialised) {
            return null;
        }

        if (_shapes.TryGetValue(symbol, out var known)) {
            return known;
        }

        if (SignatureShape(symbol) is not { } shape) {
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

    /// <summary>What a generic function's signature lets it take compiled once: null for one it does not.</summary>
    GenericShape? SignatureShape(FunctionSymbol symbol) {
        if (symbol.TypeParameters.IsEmpty || !_declarations.ContainsKey(symbol) || _localGenerics.ContainsKey(symbol)
            || symbol is { IsPolymorphic: true } or { IsRethrows: true } or { IsAsync: true } || symbol.FunctionKind != FunctionKind.Ordinary
            || symbol.ForeignName is not null || !symbol.OpaqueTypeParameters.IsEmpty
            || _compilation.GetGenerator(symbol) is not null || _compilation.GetDerivedBody(symbol) is not null
            || _compilation.GetLazyBody(symbol) is not null || _compilation.GetInheritedInitializerBody(symbol) is not null
            || !TypeParametersOf(symbol).SequenceEqual(symbol.TypeParameters)
            || !symbol.SameTypeRequirements.IsEmpty || !symbol.ConformanceRequirements.IsEmpty) {
            return null;
        }

        var requirements = new List<(TypeParameterSymbol, NamedTypeSymbol)>();
        foreach (var parameter in symbol.TypeParameters) {
            if (parameter is { IsValue: true } or { AllowsNoncopyable: true } || parameter.OpaqueParameter is not null) {
                return null;
            }

            foreach (var part in parameter.Constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c))) {
                if (part is not NamedTypeSymbol protocol || !IsTabledProtocol(protocol)) {
                    return null;
                }

                if (!requirements.Any(r => ReferenceEquals(r.Item1, parameter) && ReferenceEquals(r.Item2, protocol.OriginalDefinition))) {
                    requirements.Add((parameter, protocol.OriginalDefinition));
                }
            }
        }

        var own = symbol.TypeParameters;
        bool Passable(TypeSymbol type) =>
            !Mentions(type, own) || type is TypeParameterSymbol || type is MetatypeTypeSymbol { InstanceType: TypeParameterSymbol };

        foreach (var parameter in symbol.Parameters) {
            if (!Passable(parameter.Type)
                || Mentions(parameter.Type, own) && (parameter.IsVariadic || parameter.IsByReference || parameter.Modifier is not (ParameterModifier.None or ParameterModifier.In))) {
                return null;
            }
        }

        if (!Passable(symbol.ReturnType) || symbol.ThrownType is { } thrown && Mentions(thrown, own)) {
            return null;
        }

        return new(own, [..requirements]);
    }

    bool BodyAllows(FunctionSymbol symbol, GenericShape shape) {
        var (syntax, model) = _declarations[symbol];
        if (BodyModel(model, syntax).GetBoundNode(syntax) is not BoundBlock body) {
            return false;
        }

        var checker = new ShapeChecker(this, shape);
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
    ///     Whether a body uses its type parameters only as boxes can serve:
    ///     each expression of a type parameter's type (or its metatype's) is
    ///     a parameter, a local, a call, a metatype or a choice between two;
    ///     and what takes one is a declaration, an assignment to a local, a
    ///     return, a statement, an interpolation, a metatype comparison, a
    ///     requirement of the parameter's protocols, or a generic function
    ///     that is unspecialised too, given the parameter itself.
    /// </summary>
    sealed class ShapeChecker(SilGen generator, GenericShape shape) {
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
                BoundParameterAccess { IsNarrowed: false } or BoundLocalAccess { IsNarrowed: false } => true,
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
                BoundInterpolatedString interpolated => interpolated.Parts.IndexOf(child) is var at and >= 0 && interpolated.Formats[at] is null,
                _ => false,
            };

        bool CallMentions(BoundCall call) =>
            Mentions(call.Type) || call.Receiver is { } receiver && Mentions(receiver.Type) || call.Arguments.Any(a => Mentions(a.Type))
            || call.Substitution.Parameters.Any(p => call.Substitution.Lookup(p) is { } bound && Mentions(bound));

        bool CallAllowed(BoundCall call) {
            var callee = call.Function;

            // 'type(of: x)': what the box holds.
            if (callee is { Name: "type", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }) {
                return call.Arguments is [var argument] && IsOwn(argument.Type);
            }

            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol }) {
                return RequirementAllowed(call);
            }

            if (!IsGeneric(callee) || generator.UnspecialisedShape(callee) is not { } calleeShape) {
                return false;
            }

            if (call.Receiver is { } receiver && Mentions(receiver.Type)) {
                return false;
            }

            foreach (var parameter in calleeShape.Parameters) {
                if (call.Substitution.Lookup(parameter) is not { } bound) {
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

            // A method that changes its value changes it in a local.
            if (callee.IsMutating && call.Receiver is not BoundLocalAccess { Local.IsMutable: true }) {
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
    ///     own parameters, a type parameter's as a box, then each type
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

        var hidden = Enumerable.Repeat(SilType.Metadata, shape.Parameters.Length).Concat(Enumerable.Repeat(SilType.WitnessTable, shape.Requirements.Length)).ToList();
        var owned = signature.Owned.IsEmpty ? default : signature.Owned.Concat(hidden.Select(static _ => false)).ToImmutableArray();
        var type = new FunctionType([..signature.Parameters, ..hidden], signature.Result, signature.Thrown, owned, signature.IsAsync);
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
    ///     <c>self</c>'s box first (its slot's address, for one that changes
    ///     it) unless it is static, then its parameters, a value of
    ///     <c>Self</c> as a box, then Self's metadata. Null for a requirement
    ///     the convention cannot pass yet: one generic itself, or with
    ///     <c>Self</c> or an associated type inside another type.
    /// </summary>
    internal FunctionType? GenericEntryType(NamedTypeSymbol protocol, Symbol requirement) =>
        requirement switch {
            FunctionSymbol function => GenericEntryType(protocol, function),
            FieldSymbol field => GenericGetterType(protocol, field),
            _ => null,
        };

    /// <summary>A property's getter in the generic calling convention: <c>self</c>'s box unless it is static, then Self's metadata.</summary>
    FunctionType? GenericGetterType(NamedTypeSymbol protocol, FieldSymbol field) {
        protocol = protocol.OriginalDefinition;
        if (protocol.SelfTypeParameter is not { } self) {
            return null;
        }

        var result = field.Type.Equals(self) ? SilType.Box : Mentions(field.Type, [self, ..protocol.AssociatedTypes]) ? null : Lower(field.Type);
        return result is null ? null : new FunctionType(field.IsStatic ? [SilType.Metadata] : [SilType.Box, SilType.Metadata], result, isAsync: field.IsAsyncGetter);
    }

    internal FunctionType? GenericEntryType(NamedTypeSymbol protocol, FunctionSymbol requirement) {
        protocol = protocol.OriginalDefinition;
        if (protocol.SelfTypeParameter is not { } self || !requirement.TypeParameters.IsEmpty || requirement.IsRethrows || !requirement.OpaqueTypeParameters.IsEmpty) {
            return null;
        }

        var own = new List<TypeParameterSymbol>([self, ..protocol.AssociatedTypes]);
        SilType? Slot(TypeSymbol type) =>
            type.Equals(self) ? SilType.Box : Mentions(type, own) ? null : Lower(type);

        var parameters = ImmutableArray.CreateBuilder<SilType>();
        if (HasSelf(requirement)) {
            parameters.Add(requirement.IsMutating ? SilType.Address(SilType.Box) : SilType.Box);
        }

        foreach (var parameter in requirement.Parameters) {
            if (parameter.IsVariadic || parameter.IsByReference || Slot(parameter.Type) is not { } type) {
                return null;
            }

            switch (parameter.Modifier) {
                case ParameterModifier.None or ParameterModifier.In:
                    parameters.Add(type);
                    break;
                case ParameterModifier.InOut when type != SilType.Box:
                    parameters.Add(SilType.Address(type));
                    break;
                default:
                    return null;
            }
        }

        parameters.Add(SilType.Metadata);
        if (Slot(requirement.ReturnType) is not { } result) {
            return null;
        }

        SilType? thrown = null;
        if (requirement.ThrownType is { } thrownType && !thrownType.Equals(Core.Never) && (thrown = Slot(thrownType)) is null) {
            return null;
        }

        return new FunctionType(parameters.ToImmutable(), result, thrown, isAsync: requirement.IsAsync);
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
    ///     boxed taken out of its box first, and a result of <c>Self</c> put
    ///     in one. Null when it cannot be made.
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
    ///     the box it is given — or, for a static one, of the type.
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

    /// <summary>Sable.Core's <c>print&lt;T&gt;(_ value: T)</c>: what bridged functions of one boxed value are lowered as.</summary>
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
    ///     boxed, the parameters given boxed, the result to be boxed; for a
    ///     describer, the one parameter to write out as a string; for a
    ///     getter, the expression it returns, and whether it takes its
    ///     symbol's parameters at all.
    /// </summary>
    internal sealed record Bridge(bool BoxesSelf, ImmutableHashSet<ParameterSymbol> Boxed, bool BoxesResult, SyntaxNode? Describes = null) {
        public BoundExpression? Returns { get; init; }
        public bool TakesNoParameters { get; init; }

        /// <summary>The type the function is for: what a boxed result of Self holds.</summary>
        public TypeSymbol? Self { get; init; }
    }

    // -----------------------------------------------------------------------
    // Describers: what '"\(x)"' writes for a boxed value
    // -----------------------------------------------------------------------

    /// <summary>The types boxed so far whose describer has been asked for.</summary>
    readonly HashSet<SilType> _described = [];

    /// <summary>
    ///     Writes out a value of each type put in a box, as an interpolation
    ///     in code that knows the type would (<c>generic_describe</c>):
    ///     a function given the box. Made the first time a type is boxed.
    /// </summary>
    internal void Describer(TypeSymbol type, SilType value, SyntaxNode at) {
        if (!_described.Add(value)) {
            return;
        }

        if (PrintSymbol() is not { } print || print.DeclaringSyntax.FirstOrDefault() is not { } syntax || SourceOf(syntax) is not { } source) {
            return;
        }

        var name = $"$describe${Sanitise(value.ToString())}";
        var function = new SilFunction(name, new FunctionType([SilType.Box], SilType.String)) {
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
        /// <summary>For a function generic code calls (<see cref="Bridge" />), what arrives boxed.</summary>
        internal Bridge? Bridged { get; init; }

        /// <summary>True when the body could not be lowered: the function is a stub.</summary>
        internal bool Failed => _failed;

        /// <summary>In unspecialised code, each type parameter's metadata, as the function is given it.</summary>
        readonly Dictionary<TypeParameterSymbol, SilValue> _genericMetadata = new(ReferenceEqualityComparer.Instance);

        /// <summary>In unspecialised code, the witness tables it is given, by type parameter and protocol.</summary>
        readonly Dictionary<(TypeParameterSymbol, NamedTypeSymbol), SilValue> _genericTables = [];

        /// <summary>Self's metadata, in a bridged function.</summary>
        SilValue? _bridgeMetadata;

        /// <summary>What a bridged function arrived with, waiting for the entry block: self, and the boxed parameters.</summary>
        SilArgument? _bridgeSelf;

        readonly List<(ParameterSymbol Parameter, SilArgument Box)> _bridgeParameters = [];

        /// <summary>The boxes a bridged function holds while it changes what they hold, let go of on the way out.</summary>
        readonly List<SilValue> _bridgeHeld = [];

        /// <summary>True for a type parameter of this unspecialised function.</summary>
        bool IsOpaqueHere(TypeSymbol type) => type is TypeParameterSymbol parameter && _genericMetadata.ContainsKey(parameter);

        /// <summary>Takes the hidden parameters of an unspecialised function, after its own.</summary>
        void GenericParameters(ImmutableArray<SilType> parameterTypes, ref int next) {
            if (generator._unspecialised.TryGetValue(function.Name, out var generic)) {
                foreach (var parameter in generic.Shape.Parameters) {
                    _genericMetadata[parameter] = _entry.AddArgument(parameterTypes[next++]);
                    function.ParameterNames.Add($"$metadata.{parameter.Name}");
                }

                foreach (var (parameter, protocol) in generic.Shape.Requirements) {
                    _genericTables[(parameter, protocol)] = _entry.AddArgument(parameterTypes[next++]);
                    function.ParameterNames.Add($"$witnesses.{parameter.Name}.{protocol.Name}");
                }
            }

            if (Bridged is not null && next < parameterTypes.Length && parameterTypes[next] == SilType.Metadata) {
                _bridgeMetadata = _entry.AddArgument(parameterTypes[next++]);
            }
        }

        /// <summary>
        ///     In a bridged function, what arrived boxed is taken out of its
        ///     box: a copy into a slot of its own for a parameter or a
        ///     <c>self</c> only read, and for a <c>self</c> to change, the box
        ///     made its slot's own and changed where it is.
        /// </summary>
        void BridgeArguments() {
            if (Bridged is null) {
                return;
            }

            if (_bridgeSelf is { } boxed && LowerType(SelfType(symbol, substitution)) is { } self) {
                if (boxed.Type is AddressType) {
                    Emit(new BuiltinInst("generic_box_make_unique", SilType.Unit, boxed));
                    var box = Emit(new LoadInst(boxed));
                    _bridgeHeld.Add(box);
                    var at = Emit(new BuiltinInst("generic_box_project", SilType.Address(self), box));
                    if (self is ClassType) {
                        var slot = NewSlot(self, "self");
                        Emit(new StoreInst(Emit(new LoadInst(at)), slot, StoreKind.Init));
                        _selfAddress = slot;
                    } else {
                        _selfAddress = at;
                    }
                } else {
                    var slot = NewSlot(self, "self");
                    Emit(new StoreInst(Emit(new LoadInst(Emit(new BuiltinInst("generic_box_project", SilType.Address(self), boxed)))), slot, StoreKind.Init));
                    _selfAddress = slot;
                }
            }

            foreach (var (parameter, box) in _bridgeParameters) {
                if (LowerType(parameter.Type) is not { } type) {
                    _failed = true;
                    continue;
                }

                var slot = NewSlot(type, parameter.Name);
                Emit(new StoreInst(Emit(new LoadInst(Emit(new BuiltinInst("generic_box_project", SilType.Address(type), box)))), slot, StoreKind.Init));
                _spilled[parameter] = slot;
                _spilledArguments.Add(box);
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

        /// <summary>What a bridged function returns: a result of Self put in a box made with Self's metadata.</summary>
        SilValue? BridgeResult(SilValue? value) {
            if (Bridged is not { BoxesResult: true } || value is null or UnitValue || value.Type == SilType.Box || _bridgeMetadata is not { } metadata) {
                return value;
            }

            return BoxUp(Owned(value), metadata, Bridged.Self ?? Sub(SelfType(symbol, substitution)));
        }

        /// <summary>The boxes a bridged function held to change what they hold, let go of as it leaves.</summary>
        void ReleaseBridged() {
            foreach (var box in _bridgeHeld) {
                Emit(new DestroyValueInst(box));
            }
        }

        /// <summary>An owned value in a new box of the type the metadata describes; the type's describer is made with it.</summary>
        SilValue BoxUp(SilValue value, SilValue metadata, TypeSymbol type) {
            var box = Emit(new BuiltinInst("generic_box_alloc", SilType.Box, metadata));
            var at = Emit(new BuiltinInst("generic_box_project", SilType.Address(value.Type), box));
            Emit(new StoreInst(value, at, StoreKind.Init));
            generator.Describer(type, value.Type, (SyntaxNode?)_body?.Syntax ?? symbol.DeclaringSyntax.FirstOrDefault()!);
            return box;
        }

        /// <summary>The value a box holds, as an owned copy of the type it is; the box let go of.</summary>
        SilValue Unbox(SilValue box, SilType type) {
            var at = Emit(new BuiltinInst("generic_box_project", SilType.Address(type), box));
            var value = Emit(new LoadInst(at));
            Destroy(box);
            return value;
        }

        /// <summary>A type parameter's witness table for a protocol: the one given, or for a protocol one given refines, the registry's.</summary>
        SilValue TableFor(TypeParameterSymbol parameter, NamedTypeSymbol protocol) {
            protocol = protocol.OriginalDefinition;
            if (_genericTables.TryGetValue((parameter, protocol), out var table)) {
                return table;
            }

            return Emit(new BuiltinInst($"witness_table:{QualifiedName(protocol)}", SilType.WitnessTable, _genericMetadata[parameter]));
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
        ///     A requirement called on a type parameter in unspecialised code:
        ///     its witness, from the type parameter's table, given the value's
        ///     box and the type's metadata.
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
                var self = callee.IsMutating ? call.Receiver is { } receiver ? Address(receiver, borrows) : null : call.Receiver is { } value ? Borrow(value) : null;
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

            arguments.Add(metadata);
            return Invoke(code, type, arguments, borrows, call.Syntax);
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

        /// <summary>A property requirement read on a type parameter: its getter, from the table, given the box and the metadata.</summary>
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

            arguments.Add(_genericMetadata[parameter]);
            return Invoke(code, type, arguments, borrows, access.Syntax);
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
        ///     unspecialised callee: a box here passed as it is, and any other
        ///     value put in a box of its own, held until the call is done.
        /// </summary>
        SilValue? GenericArgument(BoundExpression argument, TypeSymbol given, bool owned, List<SilValue> borrows) {
            if (IsOpaqueHere(given)) {
                if (owned) {
                    return Expression(argument) is { } handed ? Hold(Owned(handed)) : null;
                }

                if (Borrow(argument) is not { } box) {
                    return null;
                }

                borrows.Add(box);
                return box;
            }

            if (MetadataOf(given) is not { } metadata || Expression(argument) is not { } value) {
                return null;
            }

            var boxed = Hold(BoxUp(Owned(value), metadata, given));
            if (!owned) {
                borrows.Add(boxed);
            }

            return boxed;
        }

        /// <summary>The metadata and witness tables an unspecialised callee takes after its own parameters.</summary>
        IEnumerable<SilValue>? GenericHiddenArguments(GenericShape shape, TypeSubstitution bound) {
            var hidden = new List<SilValue>();
            foreach (var parameter in shape.Parameters) {
                if (MetadataOf(bound.Lookup(parameter)!) is not { } metadata) {
                    return null;
                }

                hidden.Add(metadata);
            }

            foreach (var (parameter, protocol) in shape.Requirements) {
                var given = bound.Lookup(parameter)!;
                hidden.Add(IsOpaqueHere(given)
                    ? TableFor((TypeParameterSymbol)given, protocol)
                    : Emit(new BuiltinInst($"witness_table:{QualifiedName(protocol)}", SilType.WitnessTable, MetadataOf(given)!)));
            }

            return hidden;
        }

        /// <summary>An unspecialised callee's result of a type parameter's type, taken out of its box where the type is known.</summary>
        SilValue? GenericResult(SilValue? result, FunctionSymbol callee, TypeSubstitution bound) {
            if (result is null or UnitValue || callee.ReturnType is not TypeParameterSymbol returned || bound.Lookup(returned) is not { } given || IsOpaqueHere(given)) {
                return result;
            }

            return LowerType(given) is { } type ? Unbox(result, type) : result;
        }
    }
}
