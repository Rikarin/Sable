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

    /// <summary>A generic box (<see cref="SilType.Box" />): a value of the type its metadata describes, counted as an object is.</summary>
    sealed class GenericBox(MetadataModel type) : Counted {
        public MetadataModel Type { get; } = type;
        public object? Value;
    }

    /// <summary>The value a generic box holds, where it is.</summary>
    sealed class BoxedValue(GenericBox box) : IReference {
        public object? Get() => Peek() ?? throw new SilTrap("load from uninitialised storage");
        public object? Peek() => Alive(box).Value;
        public void Set(object? value) => Alive(box).Value = value;
    }

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

    /// <summary>
    ///     Whether a value of the first type is one of the second, as
    ///     <c>sable_metadata_is_subtype</c> answers: the same type; a subclass
    ///     of a class; a type that conforms to each protocol of an existential
    ///     (every type is an <c>any Any</c>).
    /// </summary>
    bool IsSubtype(MetadataModel type, MetadataModel of) {
        if (ReferenceEquals(type, of)) {
            return true;
        }

        if (of.Described.Kind == MetadataKind.Existential && ExistentialProtocolNames(of.Described.Name) is { } protocols) {
            return protocols.All(p => ConformsTo(type.Type, p));
        }

        for (var at = (type.Type as ClassType)?.Superclass; at is not null; at = at.Superclass) {
            if (at.Equals(of.Type)) {
                return true;
            }
        }

        return false;
    }

    bool ConformsTo(SilType type, string protocol) {
        for (var at = type; at is not null; at = (at as ClassType)?.Superclass) {
            if (module.ConformancesOf(at).Exists(c => c.Protocol == protocol)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The protocols an existential's metadata names, as the runtime reads
    ///     them from its name: <c>any M.Shape &amp; M.Named</c>; none for
    ///     <c>any Any</c>. Null when one is an instance of a generic protocol,
    ///     which the registry does not know by name.
    /// </summary>
    internal static string[]? ExistentialProtocolNames(string name) {
        if (!name.StartsWith("any ", StringComparison.Ordinal)) {
            return null;
        }

        var parts = name["any ".Length..].Split(" & ");
        return parts is ["Any"] ? [] : parts.Any(static p => p.Contains('$', StringComparison.Ordinal) || p.Contains('<', StringComparison.Ordinal)) ? null : parts;
    }

    /// <summary>FNV-1a over a type's name's UTF-8 bytes, as <c>sable_metadata_hash</c> computes it: equal types, by name, hash alike.</summary>
    internal static ulong NameHash(string name) {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(name)) {
            hash = unchecked((hash ^ b) * 1099511628211UL);
        }

        return hash;
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
            // One model per type, as natively one record per type in an image.
            case "metadata_equal":
                result = ReferenceEquals(operands[0], operands[1]);
                return true;
            case "metadata_hash":
                result = NameHash(((MetadataModel)operands[0]!).Described.Name);
                return true;
            case "metadata_is_subtype":
                result = IsSubtype((MetadataModel)operands[0]!, (MetadataModel)operands[1]!);
                return true;

            // An object's own class, from its header.
            case "object_metadata":
                result = MetadataOf(Alive((HeapObject)operands[0]!).Type);
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
                var held = box.Type.TypeArguments is [var argument] ? argument : box.Type.Fields.Count > 0 ? box.Type.Fields[0].Type : null;
                if (held is null) {
                    result = MetadataOf(box.Type);
                    return true;
                }

                result = held is ClassType && box.Fields[0] is HeapObject inner ? MetadataOf(Alive(inner).Type) : MetadataOf(held);
                return true;
            }
            case "witness_table_exists":
                result = operands[0] is not null;
                return true;

            case "generic_box_alloc":
                result = Allocate(new GenericBox((MetadataModel)operands[0]!));
                return true;
            case "generic_box_project":
                result = new BoxedValue(Alive((GenericBox)operands[0]!));
                return true;
            // A boxed value written out, by its type's describer.
            case "generic_describe": {
                var box = Alive((GenericBox)operands[0]!);
                if (!module.Describers.TryGetValue(box.Type.Type.ToString(), out var describer) || module.Find(describer.Function) is not { } describe) {
                    throw new SilTrap($"a '{box.Type.Described.Name}' with no description");
                }

                result = Call(describe, [box], depth + 1);
                return true;
            }
            case "generic_box_type": {
                var box = Alive((GenericBox)operands[0]!);
                result = box.Type.Type is ClassType && box.Value is HeapObject held ? MetadataOf(Alive(held).Type) : box.Type;
                return true;
            }
            case "generic_box_make_unique": {
                var slot = (IReference)operands[0]!;
                var box = Alive((GenericBox)slot.Get()!);
                if (box is { Strong: 1, Immortal: false }) {
                    return true;
                }

                var copy = Allocate(new GenericBox(box.Type));
                Retain(box.Type.Type, box.Value);
                copy.Value = box.Value;
                slot.Set(copy);
                Release(SilType.Box, box, depth);
                return true;
            }
        }

        if (builtin.Name.StartsWith("witness_generic:", StringComparison.Ordinal)) {
            var table = (SilWitnessTable)operands[0]!;
            var index = int.Parse(builtin.Name["witness_generic:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            result = table.GenericEntries[index].Function ?? throw new SilTrap("a requirement with no witness for generic code");
            return true;
        }

        if (builtin.Name.StartsWith("witness_associated:", StringComparison.Ordinal)) {
            var table = (SilWitnessTable)operands[0]!;
            var index = int.Parse(builtin.Name["witness_associated:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            result = MetadataOf(table.AssociatedTypes[index].Type);
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
