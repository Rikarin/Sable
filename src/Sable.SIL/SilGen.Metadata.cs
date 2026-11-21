using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Type metadata and witness tables (docs/06 §6.2, #297): the witness
///     tables of the conformances existentials need, which the conformance
///     registry finds a cast's witnesses in; and <c>TypeMetadata</c>'s
///     <c>@intrinsic</c> members (lib/Sable.Core/TypeMetadata.sb) — a type's
///     metadata, <c>metadata $T</c>, and its value witnesses applied to a
///     value, as code that knows a type only by its metadata applies them.
/// </summary>
public sealed partial class SilGen {

    // -----------------------------------------------------------------------
    // Witness tables (docs/06 §6.2): one per conformance an existential needs
    // -----------------------------------------------------------------------

    /// <summary>The protocols each existential layout was made for, in order.</summary>
    readonly Dictionary<StructType, List<NamedTypeSymbol>> _layoutProtocols = [];

    /// <summary>The types boxed in an existential so far, by what they lower to: each may be cast to any protocol it conforms to.</summary>
    readonly Dictionary<SilType, NamedTypeSymbol> _boxed = [];

    /// <summary>The protocols cast to so far (<c>as? any Q</c>, <c>is any Q</c>): each boxed type that conforms has a table for each.</summary>
    readonly List<NamedTypeSymbol> _castTargets = [];

    /// <summary>
    ///     The protocols of a layout whose conformances have witness tables:
    ///     every protocol of <c>any P</c> or <c>any A &amp; B</c>, when none is
    ///     an instance of a generic protocol — whose existentials are built,
    ///     and cast to, from witnesses the compiler picks for each type, with
    ///     the variance their slots allow. Null for such a layout.
    /// </summary>
    internal List<NamedTypeSymbol>? TabledProtocols(StructType layout) =>
        _layoutProtocols.TryGetValue(layout, out var protocols) && protocols.TrueForAll(static p => !p.IsConstructed) ? protocols : null;

    /// <summary>
    ///     For each slot of a layout, the witness table it is read from in a
    ///     cast — the first of the layout's protocols that has the slot — and
    ///     where in that table it is.
    /// </summary>
    internal List<(NamedTypeSymbol Protocol, int Entry)>? SlotTables(StructType layout) {
        if (TabledProtocols(layout) is not { } protocols) {
            return null;
        }

        var singles = protocols.Select(p => ExistentialSlots(ExistentialLayout([p])!).Select(static s => s.Slot).ToList()).ToList();
        var found = new List<(NamedTypeSymbol, int)>();
        foreach (var (slot, _) in ExistentialSlots(layout)) {
            var at = singles.FindIndex(s => s.Contains(slot));
            if (at < 0) {
                return null;
            }

            found.Add((protocols[at], singles[at].IndexOf(slot)));
        }

        return found;
    }

    /// <summary>
    ///     The witness table of a type's conformance to a protocol, made the
    ///     first time an existential needs it: the witnesses of the protocol's
    ///     existential's slots, in their order — the functions an existential
    ///     of the type holds — and the type's associated types, with what each
    ///     must conform to. False when a witness cannot be made: the type is
    ///     then not found to conform by a cast.
    /// </summary>
    internal bool WitnessTable(NamedTypeSymbol concrete, SilType value, NamedTypeSymbol protocol, SyntaxNode at) {
        var name = QualifiedName(protocol);
        if (_module.FindWitnessTable(value, name) is not null) {
            return true;
        }

        if (ExistentialLayout([protocol]) is not { } single) {
            return false;
        }

        var box = ExistentialBox(value);
        var slots = ExistentialSlots(single);
        var table = new SilWitnessTable(value, name);
        using (_diagnostics.Suppress()) {
            for (var i = 0; i < slots.Count; i++) {
                var thick = (ThickFunctionType)single.Fields[i + 1].Type;
                if (WitnessThunk(concrete, value, box, slots[i].Slot, slots[i].Member, thick, at) is not { } thunk) {
                    return false;
                }

                table.Entries.Add((slots[i].Slot, thunk.Name, thunk.Type));
            }
        }

        // The associated types, as the conformance binds them, and the protocols each must conform to.
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

        _module.AddWitnessTable(table);
        return true;
    }

    /// <summary>
    ///     A type put in an existential: its witness tables for the
    ///     existential's protocols, and for each protocol already cast to
    ///     that it conforms to — so a cast finds them in the registry,
    ///     whichever of the two the program lowered first.
    /// </summary>
    internal void Boxed(NamedTypeSymbol concrete, SilType value, StructType layout, SyntaxNode at) {
        if (TabledProtocols(layout) is not { } protocols) {
            return;
        }

        foreach (var protocol in protocols) {
            WitnessTable(concrete, value, protocol, at);
        }

        if (_boxed.TryAdd(value, concrete)) {
            foreach (var target in _castTargets.Where(concrete.Satisfies)) {
                WitnessTable(concrete, value, target, at);
            }
        }
    }

    /// <summary>
    ///     A protocol cast to: the witness tables of every type of the program
    ///     that conforms to it, and of every type boxed so far that does.
    /// </summary>
    void CastTarget(NamedTypeSymbol protocol, SyntaxNode at) {
        if (_castTargets.Contains(protocol)) {
            return;
        }

        _castTargets.Add(protocol);
        foreach (var candidate in ConformingTypes(protocol)) {
            if (Lower(candidate) is { } value) {
                WitnessTable(candidate, value, protocol, at);
            }
        }

        foreach (var (value, concrete) in _boxed.ToList()) {
            if (concrete.Satisfies(protocol)) {
                WitnessTable(concrete, value, protocol, at);
            }
        }
    }

    /// <summary>
    ///     <c>e as? any Q</c> on an existential's box: the conformance
    ///     registry asked, from the metadata of what the box holds — an
    ///     object's own class, for an object — for Q's witness table (each
    ///     protocol's, for <c>any A &amp; B</c>); the existential is the box,
    ///     shared, with the tables' witnesses. None when a table is missing.
    /// </summary>
    void RegistryCast(SilFunction cast, SilBlock entry, SilValue box, SilBlock joined, EnumType optional, StructType layout, List<NamedTypeSymbol> protocols,
        SyntaxNode at) {
        foreach (var protocol in protocols) {
            CastTarget(protocol, at);
        }

        var none = cast.AddBlock();
        none.Append(new BranchInst(joined, [none.Append(new EnumInst(optional, optional.CaseIndex("None"), null))]));
        var metadata = entry.Append(new BuiltinInst("box_metadata", SilType.Metadata, box));
        var block = entry;
        var tables = new Dictionary<NamedTypeSymbol, SilValue>();
        foreach (var protocol in protocols) {
            var table = block.Append(new BuiltinInst($"witness_table:{QualifiedName(protocol)}", SilType.WitnessTable, metadata));
            var found = cast.AddBlock();
            block.Append(new CondBranchInst(block.Append(new BuiltinInst("witness_table_exists", SilType.Bool, table)), found, [], none, []));
            tables[protocol] = table;
            block = found;
        }

        var fields = new List<SilValue> { block.Append(new CopyValueInst(box)) };
        var slots = SlotTables(layout)!;
        for (var i = 0; i < slots.Count; i++) {
            var thick = (ThickFunctionType)layout.Fields[i + 1].Type;
            var witness = block.Append(new BuiltinInst($"witness_entry:{slots[i].Entry}", thick.Implementation, tables[slots[i].Protocol]));
            fields.Add(block.Append(new PartialApplyInst(witness, null, thick)));
        }

        var existential = block.Append(new StructInst(layout, [..fields]));
        block.Append(new BranchInst(joined, [block.Append(new EnumInst(optional, optional.CaseIndex("Some"), existential))]));
    }

    sealed partial class FunctionLowering {
        SilValue? MetadataIntrinsic(BoundCall call) {
            // 'TypeMetadata.of(T.self)': T's metadata.
            if (call.Function.Name == "of") {
                if (call.Arguments is not [var argument]
                    || Sub(argument.Type) is not MetatypeTypeSymbol { InstanceType: var instance }
                    || LowerType(instance) is not { } described) {
                    return Missing(call, "the metadata of a type");
                }

                Discard(argument);
                return Emit(new MetadataInst(described));
            }

            if (call.Receiver is not { } receiver) {
                return Missing(call, $"'TypeMetadata.{call.Function.Name}' without its metadata");
            }

            if (call.Function.Name == "assign") {
                return Assign(call, receiver);
            }

            if (call.Arguments is not [var operand] || call.Function.Name is not ("copy" or "move" or "destroyCopy")) {
                return Missing(call, $"the intrinsic 'TypeMetadata.{call.Function.Name}'");
            }

            if (Borrow(receiver) is not { } metadata) {
                return null;
            }

            if (Borrow(operand) is not { } value) {
                End(metadata);
                return null;
            }

            CheckDescribes(metadata, value.Type);
            var copy = Emit(new BuiltinInst("value_witness_copy", value.Type, metadata, value));
            End(value);
            SilValue result = copy;
            switch (call.Function.Name) {
                case "move":
                    result = Emit(new BuiltinInst("value_witness_take", copy.Type, metadata, copy));
                    break;
                case "destroyCopy":
                    Emit(new BuiltinInst("value_witness_destroy", SilType.Unit, metadata, copy));
                    result = UnitValue.Instance;
                    break;
            }

            End(metadata);
            return result;
        }

        /// <summary><c>m.assign(value, to: &amp;target)</c>: the value copied over the target by the metadata's assignWithCopy.</summary>
        SilValue? Assign(BoundCall call, BoundExpression receiver) {
            if (call.Arguments is not [var operand, var target]) {
                return Missing(call, "'TypeMetadata.assign' without a value and a target");
            }

            if (Borrow(receiver) is not { } metadata) {
                return null;
            }

            if (Borrow(operand) is not { } value) {
                End(metadata);
                return null;
            }

            var borrows = new List<SilValue>();
            if (Address(target, borrows) is not { } address) {
                End(value);
                End(metadata);
                EndAll(borrows);
                return null;
            }

            CheckDescribes(metadata, value.Type);
            Emit(new BuiltinInst("value_witness_assign", SilType.Unit, metadata, address, value));
            EndAll(borrows);
            End(value);
            End(metadata);
            return UnitValue.Instance;
        }

        /// <summary>Traps unless the metadata is the type's: a value witness given another type's value would misread it.</summary>
        void CheckDescribes(SilValue metadata, SilType type) {
            var same = Emit(new BuiltinInst("metadata_equal", SilType.Bool, metadata, Emit(new MetadataInst(type))));
            var other = Emit(new BuiltinInst("xor_Bool", SilType.Bool, same, Emit(new IntegerLiteralInst(SilType.Bool, 1))));
            Emit(new CondFailInst(other, "the metadata is not of the value's type"));
        }
    }
}
