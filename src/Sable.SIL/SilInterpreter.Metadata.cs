namespace Sable.SIL;

/// <summary>
///     Type metadata and the conformance registry in the interpreter: a
///     <c>metadata $T</c> is a model of the record the code generator writes,
///     built from the same <see cref="TypeMetadata" /> description, so the two
///     answer alike; its value witnesses copy and destroy as the interpreter
///     copies and destroys any value of the type.
/// </summary>
public sealed partial class SilInterpreter {
    /// <summary>A type's metadata, as the interpreter holds it: one per type of the program, so that two are equal when they describe one type.</summary>
    internal sealed class MetadataModel(TypeMetadata described, SilModule module) {
        public TypeMetadata Described { get; } = described;
        public SilType Type => Described.Type;
        public IReadOnlyList<MetadataField> Fields { get; } = described.FieldsIn(module);
        public int Flags { get; } = (int)described.FlagsIn(module);
    }

    readonly Dictionary<SilType, MetadataModel> _metadata = [];

    MetadataModel MetadataOf(SilType type) {
        if (!_metadata.TryGetValue(type, out var model)) {
            _metadata[type] = model = new MetadataModel(TypeMetadata.Of(type), module);
        }

        return model;
    }

    /// <summary>
    ///     The registry: the witness table of a type's conformance to a
    ///     protocol — its own, or a superclass's for a class — or null, as
    ///     <c>sable_witness_table</c> finds it in the records.
    /// </summary>
    SilWitnessTable? WitnessTableOf(SilType type, string protocol) {
        for (var at = type; at is not null; at = (at as ClassType)?.Superclass) {
            foreach (var (name, table) in module.ConformancesOf(at)) {
                if (name == protocol) {
                    return table;
                }
            }
        }

        return null;
    }

    static MetadataField FieldOf(MetadataModel metadata, object? index) {
        var at = (long)index!;
        return at < 0 || at >= metadata.Fields.Count ? throw new SilTrap("field index out of range") : metadata.Fields[(int)at];
    }

    /// <summary>A builtin over metadata, witness tables or value witnesses: its result; false when it is another.</summary>
    bool MetadataBuiltin(BuiltinInst builtin, object?[] operands, int depth, out object? result) {
        result = null;
        switch (builtin.Name) {
            case "metadata_kind":
                result = (long)((MetadataModel)operands[0]!).Described.Kind;
                return true;
            case "metadata_size":
                result = (long)((MetadataModel)operands[0]!).Described.Size;
                return true;
            case "metadata_alignment":
                result = (long)((MetadataModel)operands[0]!).Described.Alignment;
                return true;
            case "metadata_stride":
                result = (long)((MetadataModel)operands[0]!).Described.Stride;
                return true;
            case "metadata_flags":
                result = (long)((MetadataModel)operands[0]!).Flags;
                return true;
            case "metadata_field_count":
                result = (long)((MetadataModel)operands[0]!).Fields.Count;
                return true;
            case "metadata_argument_count":
                result = (long)((MetadataModel)operands[0]!).Described.Arguments.Length;
                return true;
            case "metadata_name":
                result = Allocate(new HeapString(((MetadataModel)operands[0]!).Described.Name));
                return true;
            case "metadata_field_name": {
                var field = FieldOf((MetadataModel)operands[0]!, operands[1]);
                result = Allocate(new HeapString(module.DenyReflection ? "" : field.Name));
                return true;
            }
            case "metadata_field_offset":
                result = (long)FieldOf((MetadataModel)operands[0]!, operands[1]).Offset;
                return true;
            case "metadata_field_type": {
                var field = FieldOf((MetadataModel)operands[0]!, operands[1]);
                result = field.Type is { } type && module.FieldDescriptors ? MetadataOf(type) : throw new SilTrap("the metadata has no field types");
                return true;
            }
            case "metadata_argument": {
                var metadata = (MetadataModel)operands[0]!;
                var at = (long)operands[1]!;
                result = at < 0 || at >= metadata.Described.Arguments.Length
                    ? throw new SilTrap("type argument index out of range")
                    : MetadataOf(metadata.Described.Arguments[(int)at]);
                return true;
            }
            case "metadata_equal":
                result = ReferenceEquals(operands[0], operands[1]);
                return true;

            // The value witnesses: what copying, moving, assigning and destroying a value of the type does.
            case "value_witness_copy":
                Retain(((MetadataModel)operands[0]!).Type, operands[1]);
                result = operands[1];
                return true;
            case "value_witness_take":
                result = operands[1];
                return true;
            case "value_witness_assign": {
                var type = ((MetadataModel)operands[0]!).Type;
                var target = (IReference)operands[1]!;
                Retain(type, operands[2]);
                var old = target.Get();
                target.Set(operands[2]);
                Release(type, old, depth);
                return true;
            }
            case "value_witness_destroy":
                Release(((MetadataModel)operands[0]!).Type, operands[1], depth);
                return true;

            // What an existential's box holds: the type its class was made for, or an object's own class.
            case "box_metadata": {
                var box = Alive((HeapObject)operands[0]!);
                var held = box.Type.TypeArguments is [var argument] ? argument : box.Type.Fields[0].Type;
                result = held is ClassType && box.Fields[0] is HeapObject inner ? MetadataOf(Alive(inner).Type) : MetadataOf(held);
                return true;
            }
            case "witness_table_exists":
                result = operands[0] is not null;
                return true;
        }

        if (builtin.Name.StartsWith("witness_table:", StringComparison.Ordinal)) {
            result = WitnessTableOf(((MetadataModel)operands[0]!).Type, builtin.Name["witness_table:".Length..]);
            return true;
        }

        if (builtin.Name.StartsWith("witness_entry:", StringComparison.Ordinal)) {
            var table = (SilWitnessTable)operands[0]!;
            var index = int.Parse(builtin.Name["witness_entry:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            result = table.Entries[index].Function;
            return true;
        }

        return false;
    }
}
