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
///     (<c>opaque_box</c>, <see cref="SilModule.Boxers" />), its slots the
///     table's witnesses.
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
        ///     boxer (which takes it, and lets the buffer go), with the
        ///     witnesses of its tables in the existential's slots.
        /// </summary>
        SilValue? Erased(SilValue buffer, TypeParameterSymbol opened, BoundConversion erasure) {
            if (LowerType(erasure.Type) is not StructType layout || generator.SlotTables(layout) is not { } slots) {
                return Missing(erasure, $"'{erasure.Type.ToDisplayString()}' made from a type known only at run time");
            }

            var box = Emit(new BuiltinInst("opaque_box", generator.ExistentialBoxRoot(), buffer, _genericMetadata[opened]));
            var fields = new List<SilValue> { box };
            var found = new Dictionary<NamedTypeSymbol, SilValue>();
            for (var i = 0; i < slots.Count; i++) {
                var protocol = slots[i].Protocol.OriginalDefinition;
                if (!found.TryGetValue(protocol, out var table)) {
                    found[protocol] = table = _genericTables.TryGetValue((opened, protocol), out var given) ? given : Required(opened, protocol);
                }

                var thick = (ThickFunctionType)layout.Fields[i + 1].Type;
                var witness = Emit(new BuiltinInst($"witness_entry:{slots[i].Entry}", thick.Implementation, table));
                fields.Add(Emit(new PartialApplyInst(witness, null, thick)));
            }

            return Emit(new StructInst(layout, [..fields]));
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
