using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Opening a <c>Type</c> to call a generic function (SYNTAX.md "Opening a
///     type", #297 stage 6): a <c>Type</c> given for a callee's <c>T.Type</c>
///     binds T to the type it holds at run time. The callee runs compiled once
///     (its <c>$generic</c> instance, made in a specialised build too), given
///     the value's metadata as T's and, for each protocol T must conform to,
///     the type's witness table, which the conformance registry is asked for
///     (<c>witness_table_required</c>: a trap, naming the type and the
///     protocol, when it does not conform). A result of T is written to a
///     buffer the metadata sizes (<c>opaque_alloc</c>) and boxed as the
///     existential of T's protocols by the type's <b>boxer</b>
///     (<c>opaque_box</c>, <see cref="SilModule.Boxers" />), with the
///     metadata and the tables.
/// </summary>
/// <remarks>
///     The registry finds only the tables a program makes, and the runtime
///     only the boxers and describers it registers: an opened call makes
///     them for every type of the program that conforms to T's protocols, as
///     a cast to <c>any P</c> makes tables for them.
/// </remarks>
public sealed partial class SilGen {
    /// <summary>The protocols whose conformers have had what opened calls need made: tables, and as asked, boxers and describers.</summary>
    readonly HashSet<(NamedTypeSymbol, bool Boxes, bool Describes)> _openedProtocols = [];

    /// <summary>
    ///     What a generic function takes compiled once for a call that opens a
    ///     Type, or null when it cannot be: a function that only forwards to a
    ///     requirement, kept specialised until a call opens a Type, is
    ///     decided again.
    /// </summary>
    internal GenericShape? OpenedShape(FunctionSymbol symbol) {
        Opening();
        if (_shapes.TryGetValue(symbol, out var known) && known is null && _refusals.GetValueOrDefault(symbol)?.StartsWith("it only hands", StringComparison.Ordinal) == true) {
            _shapes.Remove(symbol);
        }

        return UnspecialisedShape(symbol);
    }

    /// <summary>Says why a call cannot open a Type for its callee: the callee is not compiled once (S3164).</summary>
    internal void CannotOpen(FunctionSymbol callee, SyntaxNode at) {
        var name = callee.FunctionKind == FunctionKind.Initializer ? callee.ContainingSymbol?.Name ?? callee.Name : callee.Name;
        var why = _refusals.GetValueOrDefault(callee) ?? "it is not compiled once for every type";
        _diagnostics.Error(
            ErrorCode.ERR_OpenedCalleeSpecialised,
            at.Span,
            $"'{name}' cannot be called with a type known only at run time: it is copied for each type it is called with, not compiled once ({why})"
        );
    }

    /// <summary>
    ///     What a call that opens a Type as a type conforming to the protocol
    ///     needs of each type of the program that conforms: its witness table,
    ///     with its generic entries, which the registry finds; its boxer, for
    ///     a result erased to an existential; its describer, for a callee that
    ///     writes a value out.
    /// </summary>
    internal void OpenedProtocol(NamedTypeSymbol protocol, bool boxes, bool describes, SyntaxNode at) {
        protocol = protocol.OriginalDefinition;
        if (!_openedProtocols.Add((protocol, boxes, describes))) {
            return;
        }

        foreach (var conformer in ConformingTypes(protocol)) {
            if (Lower(conformer) is not { } value || SilType.MentionsOpaque(value)) {
                continue;
            }

            if (!GenericTable(conformer, protocol, at, describes)) {
                continue;
            }

            if (describes) {
                Describer(conformer, value, at);
            }

            if (boxes) {
                Boxer(value);
            }
        }
    }

    /// <summary>
    ///     Boxes a value of a type code knows only by its metadata as an
    ///     existential (<c>opaque_box</c>): a function given the value and its
    ///     metadata that copies it into the box an existential keeps a value of
    ///     its type in (<see cref="ExistentialBox" />). Made where the type is
    ///     known, for each type an opened call may erase a result of, and
    ///     registered with the runtime as a describer is.
    /// </summary>
    internal void Boxer(SilType value) {
        if (SilType.MentionsOpaque(value) || _module.Boxers.ContainsKey(value.ToString())) {
            return;
        }

        var root = ExistentialBoxRoot();
        var box = ExistentialBox(value);
        var name = $"$boxer${Sanitise(value.ToString())}";
        var opaque = new OpaqueType("E");
        var function = new SilFunction(name, new FunctionType([opaque, SilType.Metadata], root));
        function.ParameterNames.AddRange(["$value", "$metadata.E"]);
        var block = function.AddBlock();
        var given = block.AddArgument(opaque);
        block.AddArgument(SilType.Metadata);
        var address = block.Append(new BuiltinInst("opaque_address", SilType.Address(value), given));
        var copy = block.Append(new LoadInst(address, value.IsTrivial ? LoadKind.Trivial : LoadKind.Copy));
        var made = block.Append(new AllocRefInst(box));
        block.Append(new StoreInst(copy, block.Append(new RefElementAddrInst(made, 0)), StoreKind.Init));
        var own = block.Append(new CopyValueInst(block.Append(new UpcastInst(made, root))));
        block.Append(new DestroyValueInst(made));
        block.Append(new ReturnInst(own));
        _module.Add(function);
        _module.Boxers[value.ToString()] = (value, name);
    }

    sealed partial class FunctionLowering {
        /// <summary>The call being lowered that opens a Type, once its metadata and tables are in hand: lowered as any generic call then.</summary>
        BoundCall? _openingCall;

        /// <summary>The types a call opens Types as, each with the argument that opens it.</summary>
        static List<(TypeParameterSymbol Type, BoundConversion Source)> OpenedBy(BoundCall call) {
            var opened = new List<(TypeParameterSymbol, BoundConversion)>();
            foreach (var argument in call.Arguments) {
                if (argument is BoundConversion { ConversionKind: ConversionKind.Open, Type: MetatypeTypeSymbol { InstanceType: TypeParameterSymbol { OpenedFrom: not null } type } } source) {
                    opened.Add((type, source));
                }
            }

            return opened;
        }

        /// <summary>
        ///     A call that opens a Type, its result erased to an existential
        ///     when <paramref name="erasure" /> says so: each opening
        ///     argument's metadata, evaluated first, then the tables of its
        ///     conformances, asked of the registry; then the call, to the
        ///     callee compiled once, as unspecialised code makes one.
        /// </summary>
        SilValue? OpenedCall(BoundCall call, BoundConversion? erasure) {
            Locate(call.Syntax);
            var opened = OpenedBy(call);
            if (generator.OpenedShape(call.Function) is not { } shape) {
                generator.CannotOpen(call.Function, call.Syntax);
                _failed = true;
                return null;
            }

            foreach (var (type, source) in opened) {
                if (Expression(source.Operand) is not { } metadata) {
                    return null;
                }

                _genericMetadata[type] = metadata;
            }

            var tables = new List<(TypeParameterSymbol, NamedTypeSymbol)>();
            try {
                using (generator.Opaque(new GenericShape([..opened.Select(static o => o.Type)], []))) {
                    foreach (var (parameter, protocol) in shape.Requirements) {
                        if (call.Substitution.Lookup(parameter) is not TypeParameterSymbol { OpenedFrom: not null } type) {
                            continue;
                        }

                        generator.OpenedProtocol(protocol, erasure is not null, generator.DescribesValues(call.Function), call.Syntax);
                        _genericTables[(type, protocol)] = Required(type, protocol);
                        tables.Add((type, protocol));
                    }

                    _openingCall = call;
                    var result = Call(call);
                    _openingCall = null;
                    if (erasure is null || result is null || _current is null) {
                        return result;
                    }

                    return Erased(result, opened[0].Type, erasure);
                }
            } finally {
                _openingCall = null;
                foreach (var (type, _) in opened) {
                    _genericMetadata.Remove(type);
                }

                foreach (var key in tables) {
                    _genericTables.Remove(key);
                }
            }
        }

        /// <summary>The registry's table of the opened type's conformance to the protocol: a trap, naming both, when it has none.</summary>
        SilValue Required(TypeParameterSymbol opened, NamedTypeSymbol protocol) =>
            Emit(new BuiltinInst($"witness_table_required:{QualifiedName(protocol.OriginalDefinition)}", SilType.WitnessTable, _genericMetadata[opened]));

        /// <summary>
        ///     A result of the opened type, in the buffer the call wrote it to,
        ///     as the existential of the type's protocols: boxed by its type's
        ///     boxer (which takes it, and lets the buffer go), with the type's
        ///     metadata and its tables.
        /// </summary>
        SilValue? Erased(SilValue buffer, TypeSymbol opened, BoundConversion erasure) {
            if (LowerType(erasure.Type) is not StructType layout || generator.TabledProtocols(layout) is not { } protocols
                || MetadataOf(opened) is not { } metadata) {
                return Missing(erasure, $"'{erasure.Type.ToDisplayString()}' made from a type known only at run time");
            }

            // A value, not yet in a buffer of its own (a requirement's result through an opened existential): moved into one.
            if (buffer.Type is OpaqueType value) {
                var into = Emit(new BuiltinInst("opaque_alloc", SilType.Address(value), metadata));
                Emit(new StoreInst(Owned(buffer), into, StoreKind.Init));
                buffer = into;
            }

            var box = Emit(new BuiltinInst("opaque_box", generator.ExistentialBoxRoot(), buffer, metadata));
            var tables = new List<SilValue>();
            foreach (var protocol in protocols) {
                var definition = protocol.OriginalDefinition;
                tables.Add(opened is TypeParameterSymbol parameter && _genericTables.TryGetValue((parameter, definition), out var given)
                    ? given
                    : Emit(new BuiltinInst($"witness_table_required:{QualifiedName(definition)}", SilType.WitnessTable, metadata)));
            }

            return Emit(new StructInst(layout, [box, metadata, ..tables]));
        }
        /// <summary>
        ///     The type parameters a call of generic code binds to existentials,
        ///     each with the one argument that gives it, when the call opens
        ///     them (#299, SE-0352). A type parameter is opened when it is
        ///     constrained to protocols, bound to <c>any P</c> of protocols that
        ///     are not generic, and exactly one parameter, of exactly that type
        ///     and taken by value, names it — so several, each from its own
        ///     argument (<c>pair(a, b)</c> for <c>fn pair&lt;A: P, B: Q&gt;(_ a:
        ///     A, _ b: B)</c>) — and the result, if it names it at all, is it
        ///     (boxed again as the existential). Not opened: a type parameter
        ///     two parameters name (the two existentials may hold different
        ///     types), one a parameter names inside another type (<c>T?</c>,
        ///     <c>T[]</c>, <c>(T) -> Int</c>, which would need a value of that
        ///     type made of the opened one), one taken <c>inout</c>, one a result
        ///     or an error names inside another type, which cannot be erased —
        ///     those calls run the callee's instance for the existential, as
        ///     before. The callee is compiled once. Null when none opens.
        /// </summary>
        List<(TypeParameterSymbol Parameter, int Argument)>? OpensExistential(BoundCall call) {
            var callee = call.Function;
            if (callee.TypeParameters.IsEmpty || call.Arguments.Length != callee.Parameters.Length || _current is null) {
                return null;
            }

            var opens = new List<(TypeParameterSymbol, int)>();
            foreach (var parameter in callee.TypeParameters) {
                if (parameter.Constraints.IsEmpty || call.Substitution.Lookup(parameter) is not ExistentialTypeSymbol { Constraint: var constraint } existential
                    || constraint.Equals(Core.Error) || ExistentialProtocols(constraint) is not { Count: > 0 } protocols
                    || protocols.Exists(static p => p.IsConstructed || !p.TypeParameters.IsEmpty)
                    || callee.ThrownType is { } thrown && Mentions(thrown, [parameter])
                    || callee.ReturnType is var result && !result.Equals(parameter) && Mentions(result, [parameter])
                    || callee.SameTypeRequirements.Any(r => Mentions(r.Left, [parameter]) || Mentions(r.Right, [parameter]))) {
                    continue;
                }

                var taking = new List<int>();
                var other = false;
                for (var i = 0; i < callee.Parameters.Length; i++) {
                    var type = callee.Parameters[i].Type;
                    if (type.Equals(parameter) && callee.Parameters[i].Modifier is ParameterModifier.None or ParameterModifier.In
                        && Sub(call.Arguments[i].Type).Equals(existential)) {
                        taking.Add(i);
                    } else if (Mentions(type, [parameter])) {
                        other = true;
                    }
                }

                if (taking is not [var argument] || other) {
                    continue;
                }

                opens.Add((parameter, argument));
            }

            // Another type parameter's constraints naming an opened one ('U: Container, U.Item == T') would tie them: none opens then.
            if (opens.Count == 0 || callee.TypeParameters.Any(p => p.Constraints.Any(c => Mentions(c, [..opens.Select(static o => o.Item1)])))
                || generator.OpenedShape(callee) is null) {
                return null;
            }

            return opens;
        }

        /// <summary>
        ///     A call that opens the existentials it gives generic code for its
        ///     type parameters (#299, SE-0352): each one's value, where its box
        ///     holds it, is its argument, of a type known here only by the
        ///     metadata the existential holds, conforming by its tables; the
        ///     callee compiled once is called as it is for a Type opened, and a
        ///     result of one of the types erased to its existential again, by
        ///     the type's boxer. The existentials are evaluated first, in their
        ///     order, then the other arguments, as the call evaluates them.
        /// </summary>
        SilValue? OpenedExistentialCall(BoundCall call, List<(TypeParameterSymbol Parameter, int Argument)> opens) {
            var layouts = new List<(TypeParameterSymbol Parameter, int Argument, ExistentialTypeSymbol Type, List<NamedTypeSymbol> Protocols)>();
            foreach (var (parameter, index) in opens) {
                var existentialType = (ExistentialTypeSymbol)call.Substitution.Lookup(parameter)!;
                if (LowerType(existentialType) is not StructType layout || generator.TabledProtocols(layout) is not { } protocols) {
                    return Missing(call, $"opening '{existentialType.ToDisplayString()}'");
                }

                layouts.Add((parameter, index, existentialType, protocols));
            }

            var erasedFrom = layouts.FindIndex(l => call.Function.ReturnType.Equals(l.Parameter));
            foreach (var (_, _, _, protocols) in layouts) {
                foreach (var protocol in protocols) {
                    generator.OpenedProtocol(protocol, erasedFrom >= 0, generator.DescribesValues(call.Function), call.Syntax);
                }
            }

            // Each existential, held for the call, in the order of its argument.
            var held = new List<SilValue>();
            var openedTypes = new List<TypeParameterSymbol>();
            var givens = new List<ParameterSymbol>();
            var arguments = call.Arguments.ToArray();
            var substitution = call.Substitution;
            var requirements = new List<(TypeParameterSymbol, NamedTypeSymbol)>();
            try {
                foreach (var (parameter, index, _, protocols) in layouts.OrderBy(static l => l.Argument)) {
                    var opened = TypeParameterSymbol.OpenedExistential(parameter, [..protocols]);
                    if (Expression(call.Arguments[index]) is not { } existential) {
                        held.ForEach(e => Destroy(Unhold(e)));
                        return null;
                    }

                    held.Add(Hold(existential));
                    var metadata = Emit(new StructExtractInst(existential, 1));
                    _genericMetadata[opened] = metadata;
                    for (var i = 0; i < protocols.Count; i++) {
                        _genericTables[(opened, protocols[i].OriginalDefinition)] = Emit(new StructExtractInst(existential, SilGen.ExistentialTables + i));
                        requirements.Add((opened, protocols[i].OriginalDefinition));
                    }

                    var given = ParameterSymbol.Synthetic($"$opened{givens.Count}", symbol, opened);
                    givens.Add(given);
                    openedTypes.Add(opened);
                    arguments[index] = new BoundParameterAccess(call.Arguments[index].Syntax, given);
                    substitution = Opening(substitution, opened, parameter);
                }

                using (generator.Opaque(new GenericShape([..openedTypes], [..requirements]))) {
                    // Each value where its box holds it, of the type it is opened as.
                    for (var i = 0; i < givens.Count; i++) {
                        _parameters[givens[i]] = Emit(new BuiltinInst("box_project", LowerType(openedTypes[i])!, Emit(new StructExtractInst(held[i], 0)), _genericMetadata[openedTypes[i]]));
                    }

                    TypeSymbol? erased = erasedFrom >= 0 ? substitution.Lookup(layouts[erasedFrom].Parameter) : null;
                    var opening = new BoundCall(call.Syntax, call.Receiver, call.Function, [..arguments], erased ?? call.Type) {
                        Substitution = substitution,
                    };
                    _openingCall = opening;
                    var result = Call(opening);
                    _openingCall = null;
                    if (result is null || _current is null) {
                        held.ForEach(e => Destroy(Unhold(e)));
                        return result;
                    }

                    var value = erased is not null
                        ? Erased(result, erased, new BoundConversion(call.Syntax, layouts[erasedFrom].Type, call, ConversionKind.Existential))
                        : result;
                    held.ForEach(e => Destroy(Unhold(e)));
                    return value;
                }
            } finally {
                _openingCall = null;
                givens.ForEach(g => _parameters.Remove(g));
                openedTypes.ForEach(o => _genericMetadata.Remove(o));
                requirements.ForEach(r => _genericTables.Remove(r));
            }
        }

        /// <summary>Whether a member, by its key, is a requirement of an existential's protocols, which its tables hold.</summary>
        bool IsRequirementOf(BoundExpression existential, string key) =>
            LowerType(existential.Type) is StructType layout && generator.IsExistentialLayout(layout) && generator.RequirementOf(layout, key) is not null;

        /// <summary>
        ///     A call's substitution with the protocol's <c>Self</c> bound to the
        ///     type an existential is opened as, and — through the existential
        ///     erased — its associated types read through that type again
        ///     (<c>Opened.Element</c>).
        /// </summary>
        static TypeSubstitution Opening(TypeSubstitution substitution, TypeParameterSymbol opened, TypeParameterSymbol? parameter = null) {
            var map = substitution.Parameters.ToDictionary(static p => p, substitution.Apply);
            map[parameter ?? opened.OpenedFrom!] = opened;
            if (parameter is null) {
                foreach (var associated in AssociatedOf(opened)) {
                    map[associated] = new DependentMemberTypeSymbol(opened, associated);
                }
            }

            return new TypeSubstitution(map);
        }

        /// <summary>The associated types of the protocol an existential is opened as Self of, and of those it refines.</summary>
        static List<TypeParameterSymbol> AssociatedOf(TypeParameterSymbol opened) {
            var all = new List<TypeParameterSymbol>();
            var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
            void Visit(NamedTypeSymbol protocol) {
                if (!seen.Add(protocol.OriginalDefinition)) {
                    return;
                }

                all.AddRange(protocol.OriginalDefinition.AssociatedTypes);
                foreach (var refined in Bases(protocol.OriginalDefinition)) {
                    Visit(refined);
                }
            }

            if (opened.OpenedFrom?.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                Visit(protocol);
            }

            return all;
        }

        /// <summary>
        ///     Whether a requirement's result or error is its protocol's
        ///     <c>Self</c> or one of its associated types, which a call through
        ///     an existential gives back only by opening it and erasing the
        ///     value again (#299, SE-0352).
        /// </summary>
        static bool OpensForResult(Symbol member, TypeSymbol result, TypeSymbol? thrown) {
            if (member.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self } protocol) {
                return false;
            }

            var own = new List<TypeParameterSymbol> { self };
            own.AddRange(AssociatedOf(TypeParameterSymbol.OpenedExistential(self, [protocol])));
            return Mentions(result, own) || thrown is not null && Mentions(thrown, own);
        }

        /// <summary>A member's type, of a protocol's <c>Self</c> or associated type, read on the type an existential is opened as.</summary>
        static TypeSymbol OpenedMember(Symbol member, TypeSymbol type, TypeParameterSymbol opened, TypeSymbol given) {
            if (!OpensForResult(member, type, null)) {
                return given;
            }

            var map = new Dictionary<TypeParameterSymbol, TypeSymbol> { [opened.OpenedFrom!] = opened };
            foreach (var associated in AssociatedOf(opened)) {
                map[associated] = new DependentMemberTypeSymbol(opened, associated);
            }

            return new TypeSubstitution(map).Apply(type);
        }

        /// <summary>
        ///     A member of an extension of a protocol, which is no requirement,
        ///     used on an existential (#299, SE-0352): the existential opened —
        ///     its value where its box holds it, of a type known here only by
        ///     the metadata it holds, which its tables say conforms — and the
        ///     member, compiled once, used on that value as on a type
        ///     parameter's (<paramref name="rebuild" /> makes the use, given
        ///     the value and the type). One that changes the value is given its
        ///     address, the box first made the existential's own.
        /// </summary>
        SilValue? OpenedExistential(BoundExpression receiver, Symbol member, bool changes, BoundNode at, Func<BoundExpression, TypeParameterSymbol, BoundExpression> rebuild, TypeSymbol? erasedTo = null) {
            var protocol = (member.ContainingSymbol as ExtensionSymbol)?.ExtendedType as NamedTypeSymbol
                ?? ((member.ContainingSymbol as FieldSymbol)?.ContainingSymbol as ExtensionSymbol)?.ExtendedType as NamedTypeSymbol
                ?? member.ContainingSymbol as NamedTypeSymbol;
            if (LowerType(receiver.Type) is not StructType layout || generator.TabledProtocols(layout) is not { } protocols
                || protocol is not { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self }) {
                return Missing(at, $"'{member.Name}' through '{Sub(receiver.Type).ToDisplayString()}'");
            }

            // A specialised build compiles the member once from here on, as a call that opens a Type does.
            if (member is FunctionSymbol function) {
                generator.OpenedShape(function);
            } else {
                generator.Opening();
            }

            // A result given back erased is boxed by its type's boxer, which every conformer of the protocols it is erased to needs.
            var erasure = erasedTo is not null && Sub(erasedTo) is ExistentialTypeSymbol erasedType ? erasedType : null;
            foreach (var each in erasure is null ? [] : ExistentialProtocols(erasure.Constraint) ?? []) {
                generator.OpenedProtocol(each, true, false, at.Syntax);
            }

            var opened = TypeParameterSymbol.OpenedExistential(self, [..protocols]);
            var borrows = new List<SilValue>();
            SilValue? existential = null;
            SilValue metadata;
            var tables = new List<SilValue>();
            if (!changes) {
                if ((existential = Borrow(receiver)) is null) {
                    return null;
                }

                borrows.Add(existential);
                metadata = Emit(new StructExtractInst(existential, 1));
                for (var i = 0; i < protocols.Count; i++) {
                    tables.Add(Emit(new StructExtractInst(existential, SilGen.ExistentialTables + i)));
                }
            } else {
                if (Address(receiver, borrows) is not { } address) {
                    EndAll(borrows);
                    return null;
                }

                metadata = Emit(new LoadInst(Emit(new StructElementAddrInst(address, 1))));
                for (var i = 0; i < protocols.Count; i++) {
                    tables.Add(Emit(new LoadInst(Emit(new StructElementAddrInst(address, SilGen.ExistentialTables + i)))));
                }

                BeginAccess(address, modify: true, borrows);
                existential = address;
            }

            _genericMetadata[opened] = metadata;
            for (var i = 0; i < protocols.Count; i++) {
                _genericTables[(opened, protocols[i].OriginalDefinition)] = tables[i];
            }

            try {
                using (generator.Opaque(new GenericShape([opened], [..protocols.Select(p => (opened, p.OriginalDefinition))]))) {
                    var opaque = (OpaqueType)LowerType(opened)!;
                    SilValue held = changes
                        ? Emit(new BuiltinInst("box_project_unique", SilType.Address(opaque), Emit(new StructElementAddrInst(existential, 0)), metadata))
                        : Emit(new BuiltinInst("box_project", opaque, Emit(new StructExtractInst(existential, 0)), metadata));
                    var parameter = ParameterSymbol.Synthetic("self", symbol, opened, changes ? ParameterModifier.InOut : ParameterModifier.None);
                    _parameters[parameter] = held;
                    try {
                        var use = rebuild(new BoundParameterAccess(at.Syntax, parameter), opened);
                        var result = Expression(use);

                        // A result of Self or of an associated type, erased to the existential the call gives (#299, SE-0352).
                        if (erasure is not null && result is { Type: OpaqueType or AddressType { Pointee: OpaqueType } } && _current is not null) {
                            result = Erased(result, Sub(use.Type), new BoundConversion(at.Syntax, erasure, use, ConversionKind.Existential));
                        }

                        EndAll(borrows);
                        return result;
                    } finally {
                        _parameters.Remove(parameter);
                    }
                }
            } finally {
                _genericMetadata.Remove(opened);
                foreach (var protocolOf in protocols) {
                    _genericTables.Remove((opened, protocolOf.OriginalDefinition));
                }
            }
        }

        /// <summary>
        ///     A step a call takes only if its callee throws: what was made for
        ///     the call undone on the error's path (a result's buffer let go).
        ///     Nothing on the normal path.
        /// </summary>
        sealed class OnError(Action run) : SilValue {
            public void Run() => run();
            public override SilType Type => SilType.Unit;
        }
    }
}
