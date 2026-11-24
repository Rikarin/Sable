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
        SilValue? Erased(SilValue buffer, TypeParameterSymbol opened, BoundConversion erasure) {
            if (LowerType(erasure.Type) is not StructType layout || generator.TabledProtocols(layout) is not { } protocols) {
                return Missing(erasure, $"'{erasure.Type.ToDisplayString()}' made from a type known only at run time");
            }

            var metadata = _genericMetadata[opened];
            var box = Emit(new BuiltinInst("opaque_box", generator.ExistentialBoxRoot(), buffer, metadata));
            var tables = new List<SilValue>();
            foreach (var protocol in protocols) {
                var definition = protocol.OriginalDefinition;
                tables.Add(_genericTables.TryGetValue((opened, definition), out var given) ? given : Required(opened, definition));
            }

            return Emit(new StructInst(layout, [box, metadata, ..tables]));
        }
        /// <summary>
        ///     The type parameter a call of generic code binds to an
        ///     existential, and the one argument that gives it, when the call
        ///     opens it (#299, SE-0352): a type parameter constrained to
        ///     protocols, bound to <c>any P</c> of protocols that are not
        ///     generic, which one parameter of exactly that type takes and
        ///     nothing else of the callee's signature names but its result;
        ///     the callee compiled once. Null for any other call, which runs
        ///     the callee's instance for the existential as before.
        /// </summary>
        (TypeParameterSymbol Parameter, int Argument)? OpensExistential(BoundCall call) {
            var callee = call.Function;
            if (callee.TypeParameters.IsEmpty || call.Arguments.Length != callee.Parameters.Length || _current is null) {
                return null;
            }

            foreach (var parameter in callee.TypeParameters) {
                if (parameter.Constraints.IsEmpty || call.Substitution.Lookup(parameter) is not ExistentialTypeSymbol { Constraint: var constraint } existential
                    || constraint.Equals(Core.Error) || ExistentialProtocols(constraint) is not { Count: > 0 } protocols
                    || protocols.Exists(static p => p.IsConstructed || !p.TypeParameters.IsEmpty)
                    || callee.ThrownType is { } thrown && Mentions(thrown, [parameter])
                    || callee.ReturnType is var result && !result.Equals(parameter) && Mentions(result, [parameter])) {
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

                if (taking is not [var argument] || other || generator.OpenedShape(callee) is null) {
                    continue;
                }

                return (parameter, argument);
            }

            return null;
        }

        /// <summary>
        ///     A call that opens the existential it gives generic code for a
        ///     type parameter (#299, SE-0352): its value, where its box holds
        ///     it, is the argument, of a type known here only by the metadata
        ///     the existential holds, conforming by its tables; the callee
        ///     compiled once is called as it is for a Type opened, and a result
        ///     of the type erased to the existential again, by the type's boxer.
        /// </summary>
        SilValue? OpenedExistentialCall(BoundCall call, TypeParameterSymbol parameter, int index) {
            var argument = call.Arguments[index];
            var existentialType = (ExistentialTypeSymbol)call.Substitution.Lookup(parameter)!;
            if (LowerType(existentialType) is not StructType layout || generator.TabledProtocols(layout) is not { } protocols) {
                return Missing(call, $"opening '{existentialType.ToDisplayString()}'");
            }

            var erases = call.Function.ReturnType.Equals(parameter);
            foreach (var protocol in protocols) {
                generator.OpenedProtocol(protocol, erases, generator.DescribesValues(call.Function), call.Syntax);
            }

            // The arguments before the existential are evaluated before it, as the call would.
            var opened = TypeParameterSymbol.OpenedExistential(parameter, [..protocols]);
            if (Expression(argument) is not { } existential) {
                return null;
            }

            Hold(existential);
            var metadata = Emit(new StructExtractInst(existential, 1));
            _genericMetadata[opened] = metadata;
            for (var i = 0; i < protocols.Count; i++) {
                _genericTables[(opened, protocols[i].OriginalDefinition)] = Emit(new StructExtractInst(existential, SilGen.ExistentialTables + i));
            }

            var given = ParameterSymbol.Synthetic("$opened", symbol, opened);
            try {
                using (generator.Opaque(new GenericShape([opened], [..protocols.Select(p => (opened, p.OriginalDefinition))]))) {
                    _parameters[given] = Emit(new BuiltinInst("box_project", LowerType(opened)!, Emit(new StructExtractInst(existential, 0)), metadata));
                    var arguments = call.Arguments.ToArray();
                    arguments[index] = new BoundParameterAccess(argument.Syntax, given);
                    var opening = new BoundCall(call.Syntax, call.Receiver, call.Function, [..arguments], erases ? opened : call.Type) {
                        Substitution = Opening(call.Substitution, opened, parameter),
                    };
                    _openingCall = opening;
                    var result = Call(opening);
                    _openingCall = null;
                    if (result is null || _current is null) {
                        Destroy(Unhold(existential));
                        return result;
                    }

                    var value = erases ? Erased(result, opened, new BoundConversion(call.Syntax, existentialType, call, ConversionKind.Existential)) : result;
                    Destroy(Unhold(existential));
                    return value;
                }
            } finally {
                _openingCall = null;
                _parameters.Remove(given);
                _genericMetadata.Remove(opened);
                foreach (var protocol in protocols) {
                    _genericTables.Remove((opened, protocol.OriginalDefinition));
                }
            }
        }

        /// <summary>Whether a member, by its key, is a requirement of an existential's protocols, which its tables hold.</summary>
        bool IsRequirementOf(BoundExpression existential, string key) =>
            LowerType(existential.Type) is StructType layout && generator.IsExistentialLayout(layout) && generator.RequirementOf(layout, key) is not null;

        /// <summary>A call's substitution with the protocol's <c>Self</c> bound to the type an existential is opened as.</summary>
        static TypeSubstitution Opening(TypeSubstitution substitution, TypeParameterSymbol opened, TypeParameterSymbol? parameter = null) {
            var map = substitution.Parameters.ToDictionary(static p => p, substitution.Apply);
            map[parameter ?? opened.OpenedFrom!] = opened;
            return new TypeSubstitution(map);
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
        SilValue? OpenedExistential(BoundExpression receiver, Symbol member, bool changes, BoundNode at, Func<BoundExpression, TypeParameterSymbol, BoundExpression> rebuild) {
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
                        var result = Expression(rebuild(new BoundParameterAccess(at.Syntax, parameter), opened));
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
