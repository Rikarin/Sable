using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Type metadata and witness tables (docs/06 §6.2, #297): the witness
///     tables of the conformances existentials need, which the conformance
///     registry finds a cast's witnesses in; a type as a value,
///     <c>metadata $T</c>, and a value's dynamic type, <c>type(of:)</c>; and
///     <c>Type</c>'s <c>@intrinsic</c> members (lib/Sable.Core/Type.sb) — its
///     value witnesses applied to a value, as code that knows a type only by
///     its metadata applies them.
/// </summary>
public sealed partial class SilGen {

    // -----------------------------------------------------------------------
    // Witness tables (docs/06 §6.2): one per conformance an existential needs
    // -----------------------------------------------------------------------

    /// <summary>The protocols each existential layout was made for, in order.</summary>
    readonly Dictionary<StructType, List<NamedTypeSymbol>> _layoutProtocols = [];

    /// <summary>The types boxed in an existential so far, by what they lower to: each may be cast to any protocol it conforms to.</summary>
    readonly Dictionary<SilType, TypeSymbol> _boxed = [];

    /// <summary>The protocols cast to so far (<c>as? any Q</c>, <c>is any Q</c>): each boxed type that conforms has a table for each.</summary>
    readonly List<NamedTypeSymbol> _castTargets = [];

    /// <summary>
    ///     The protocols of a layout the conformance registry is asked about
    ///     in a cast: every protocol of <c>any P</c> or <c>any A &amp; B</c>,
    ///     when none is an instance of a generic protocol — whose existential
    ///     a conformer to another instance may fill by variance, so a cast to
    ///     it tries each type of the program instead. Null for such a layout.
    /// </summary>
    internal List<NamedTypeSymbol>? TabledProtocols(StructType layout) =>
        _layoutProtocols.TryGetValue(layout, out var protocols) && protocols.TrueForAll(static p => !p.IsConstructed) ? protocols : null;

    /// <summary>
    ///     The witness table of a type's conformance to a protocol (#299),
    ///     made the first time anything needs it: an existential, a cast, a
    ///     call of generic code. Its entries are the protocol's requirements
    ///     in the generic calling convention, each met as code that knows the
    ///     type meets it; then the tables of the protocols it refines, and
    ///     the type's associated types, with what each must conform to. For
    ///     an instance of a generic protocol, the table is of the conformer's
    ///     own instance (<c>Producer&lt;Dog&gt;</c>, for an
    ///     <c>any Producer&lt;Animal&gt;</c>), whose entries take and give what
    ///     the existential's do, as a class's upcast is the same reference.
    ///     Null when the type does not conform.
    /// </summary>
    internal SilWitnessTable? WitnessTable(TypeSymbol concrete, SilType value, NamedTypeSymbol protocol, SyntaxNode at) {
        var name = TableName(concrete, protocol);
        if (_module.FindWitnessTable(value, name) is { } existing) {
            return existing;
        }

        var conformance = ConformanceTo(concrete, protocol) ?? protocol.OriginalDefinition;
        var table = new SilWitnessTable(value, name);
        AssociatedTypes(table, concrete, conformance);
        var through = TypeSubstitution.For(conformance);
        foreach (var refined in Bases(conformance.OriginalDefinition)) {
            table.Bases.Add(TableName(concrete, (NamedTypeSymbol)through.Apply(refined)));
        }

        // Added first: a witness may box a value of the type, and so ask for this very table.
        _module.AddWitnessTable(table);
        foreach (var refined in Bases(conformance.OriginalDefinition)) {
            WitnessTable(concrete, value, (NamedTypeSymbol)through.Apply(refined), at);
        }

        _filling.Add(table);
        foreach (var (key, member) in GenericRequirements(conformance)) {
            string? witness = null;
            // There is no value of Never to call one on.
            var type = concrete.Equals(Core.Never) ? null : GenericEntryType(conformance, member);
            if (type is not null) {
                using (_diagnostics.Suppress()) {
                    witness = GenericWitness(concrete, value, conformance, key, member, type, at);
                }
            }

            if (witness is null && type is not null && member is FunctionSymbol generic && !OwnParameters(generic).IsEmpty) {
                witness = TrapWitness(table, key, type);
            }

            table.Entries.Add((key, witness, witness is null ? null : type));
        }

        _filling.Remove(table);
        return table;
    }

    /// <summary>
    ///     The instance of a generic protocol a type conforms to it by: the
    ///     one given itself, where it conforms to several, or the one whose
    ///     existential that one is by variance. Null for a protocol that is not generic.
    /// </summary>
    internal static NamedTypeSymbol? ConformanceTo(TypeSymbol concrete, NamedTypeSymbol protocol) {
        if (protocol.OriginalDefinition.TypeParameters.IsEmpty) {
            return null;
        }

        var all = concrete.SupertypesOf(protocol).ToList();
        return all.Find(s => s.Equals(protocol)) ?? all.Find(s => s.Satisfies(protocol)) ?? all.FirstOrDefault();
    }

    /// <summary>
    ///     What a type's witness table for a protocol is known by: the
    ///     protocol's name, and for a type that conforms to several instances
    ///     of a generic one, which: <c>M.Handles$of$Int</c>.
    /// </summary>
    internal string TableName(TypeSymbol concrete, NamedTypeSymbol protocol) {
        var name = QualifiedName(protocol);
        return !protocol.OriginalDefinition.TypeParameters.IsEmpty && concrete.SupertypesOf(protocol).Take(2).Count() > 1 && ConformanceTo(concrete, protocol) is { } instance
            ? $"{name}$of${string.Join("$", instance.TypeArguments.Select(a => Lower(a) is { } lowered ? Sanitise(lowered.ToString()) : "Never"))}"
            : name;
    }

    /// <summary>The tables whose entries are being made: one asked for again meanwhile is taken as it will be.</summary>
    readonly HashSet<SilWitnessTable> _filling = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     The first requirement of a table a call through an existential can
    ///     reach but that has no witness — one the type cannot meet that way
    ///     yet — or null when every one has. A table being made is taken to
    ///     have them all.
    /// </summary>
    internal string? MissingWitness(SilWitnessTable table, NamedTypeSymbol protocol) {
        if (_filling.Contains(table)) {
            return null;
        }

        var requirements = GenericRequirements(protocol);
        for (var i = 0; i < table.Entries.Count && i < requirements.Count; i++) {
            if (table.Entries[i] is { Function: null } && GenericEntryType(protocol, requirements[i].Member) is { } type && ReachableThroughExistential(requirements[i].Member, type)) {
                return table.Entries[i].Requirement;
            }
        }

        foreach (var (refined, name) in Bases(protocol.OriginalDefinition).Zip(table.Bases)) {
            var instance = (NamedTypeSymbol)TypeSubstitution.For(protocol).Apply(refined);
            if (_module.FindWitnessTable(table.Conformer, name) is { } baseTable && MissingWitness(baseTable, instance) is { } missing) {
                return missing;
            }
        }

        return null;
    }

    /// <summary>
    ///     A requirement a call through an existential can reach: an instance
    ///     member whose entry takes the value and nothing else of Self's or
    ///     an associated type's, and gives none back.
    /// </summary>
    internal static bool ReachableThroughExistential(Symbol member, FunctionType type) =>
        member switch {
            FunctionSymbol function => !function.IsStatic && function.FunctionKind != FunctionKind.Operator,
            FieldSymbol field => !field.IsStatic,
            _ => false,
        }
        && type.Parameters.Length >= 2 && !type.Parameters.Skip(1).Any(SilType.MentionsOpaque) && !SilType.MentionsOpaque(type.Result)
        && type.Thrown is not AddressType;

    /// <summary>
    ///     A type put in an existential: its witness tables for the
    ///     existential's protocols, and for each protocol already cast to
    ///     that it conforms to — so a cast finds them in the registry,
    ///     whichever of the two the program lowered first.
    /// </summary>
    internal void Boxed(TypeSymbol concrete, SilType value, StructType layout, SyntaxNode at) {
        foreach (var protocol in LayoutProtocols(layout)) {
            WitnessTable(concrete, value, protocol, at);
        }

        if (concrete is NamedTypeSymbol or ArrayTypeSymbol or OptionalTypeSymbol && concrete is var named && _boxed.TryAdd(value, named)) {
            foreach (var target in _castTargets.Where(named.Satisfies)) {
                WitnessTable(named, value, target, at);
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
    ///     shared, with that metadata and the tables. None when a table is missing.
    /// </summary>
    void RegistryCast(SilFunction cast, SilBlock entry, SilValue box, SilBlock joined, EnumType optional, StructType layout, List<NamedTypeSymbol> protocols,
        SyntaxNode at) {
        foreach (var protocol in protocols) {
            CastTarget(protocol, at);
        }

        // Every value is an 'any Any'.
        var none = protocols.Count == 0 ? null : cast.AddBlock();
        none?.Append(new BranchInst(joined, [none.Append(new EnumInst(optional, optional.CaseIndex("None"), null))]));
        var metadata = entry.Append(new BuiltinInst("box_metadata", SilType.Metadata, box));
        var block = entry;
        var tables = new List<SilValue>();
        foreach (var protocol in protocols) {
            var table = block.Append(new BuiltinInst($"witness_table:{QualifiedName(protocol)}", SilType.WitnessTable, metadata));
            var found = cast.AddBlock();
            block.Append(new CondBranchInst(block.Append(new BuiltinInst("witness_table_exists", SilType.Bool, table)), found, [], none!, []));
            tables.Add(table);
            block = found;
        }

        var existential = block.Append(new StructInst(layout, [block.Append(new CopyValueInst(box)), metadata, ..tables]));
        block.Append(new BranchInst(joined, [block.Append(new EnumInst(optional, optional.CaseIndex("Some"), existential))]));
    }

    sealed partial class FunctionLowering {
        /// <summary>
        ///     <c>Int.self</c>, <c>T.self</c>: the type's metadata, a constant —
        ///     in generic code too, as each instance is generated for its types.
        ///     A type that has no values to describe (a protocol named alone)
        ///     has the metadata of <c>()</c>'s.
        /// </summary>
        SilValue MetatypeValue(BoundMetatype metatype) =>
            Sub(metatype.InstanceType) is TypeParameterSymbol parameter && _genericMetadata.TryGetValue(parameter, out var given)
                ? given
                : _genericMetadata.Count > 0 && Mentions(Sub(metatype.InstanceType), [.._genericMetadata.Keys]) && MetadataOf(Sub(metatype.InstanceType)) is { } made
                    ? made
                    : Emit(new MetadataInst(LowerType(Sub(metatype.InstanceType)) ?? SilType.Unit));

        /// <summary>
        ///     <c>type(of: x)</c>: the metadata of the value's dynamic type — an
        ///     object's own class, read from its header, which may be a
        ///     subclass of the static type; what an existential's box holds;
        ///     and for any other value its static type's, a constant.
        /// </summary>
        SilValue? DynamicType(BoundCall call) {
            if (call.Arguments is not [var argument]) {
                return Missing(call, "'type(of:)' without a value");
            }

            var type = Sub(argument.Type);

            // A type parameter's value: its metadata, or for an object, its own class's.
            if (IsOpaqueHere(type) && MetadataOf(type) is { } typeMetadata) {
                if (Borrow(argument) is not { } given) {
                    return null;
                }

                var held = Emit(new BuiltinInst("opaque_type", SilType.Metadata, given, typeMetadata));
                End(given);
                return held;
            }

            if (LowerType(type) is not { } lowered) {
                return Missing(call, $"the type of a '{type.ToDisplayString()}'");
            }

            var dynamic = lowered is ClassType || generator.IsExistentialLayout(lowered);
            if (!dynamic) {
                Discard(argument);
                return Emit(new MetadataInst(lowered));
            }

            if (Borrow(argument) is not { } value) {
                return null;
            }

            SilValue metadata = lowered switch {
                // 'any Error' is a box class of its own, holding the error.
                ClassType { Name: "any$Error" } => Emit(new BuiltinInst("box_metadata", SilType.Metadata, value)),
                ClassType => Emit(new BuiltinInst("object_metadata", SilType.Metadata, value)),
                _ => Emit(new BuiltinInst("box_metadata", SilType.Metadata, Emit(new StructExtractInst(value, 0)))),
            };
            End(value);
            return metadata;
        }

        SilValue? MetadataIntrinsic(BoundCall call) {
            if (call.Receiver is not { } receiver) {
                return Missing(call, $"'Type.{call.Function.Name}' without its metadata");
            }

            if (call.Function.Name == "assign") {
                return Assign(call, receiver);
            }

            if (call.Arguments is not [var operand] || call.Function.Name is not ("copy" or "move" or "destroyCopy")) {
                return Missing(call, $"the intrinsic 'Type.{call.Function.Name}'");
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
                return Missing(call, "'Type.assign' without a value and a target");
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
