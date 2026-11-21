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

    /// <summary>
    ///     The metadata of each type parameter the function running now is
    ///     given (<c>$metadata.T</c>), by name: what copies and destroys a
    ///     value of an <see cref="OpaqueType" />, which the interpreter holds as
    ///     it holds a value of the type it is, as native code does through
    ///     its value witnesses.
    /// </summary>
    Dictionary<string, MetadataModel>? _opaqueTypes;

    readonly Dictionary<SilFunction, (int Index, string Name)[]> _opaqueParameters = new(ReferenceEqualityComparer.Instance);

    /// <summary>The metadata parameters of a function, by where they are among its arguments and the type parameter each is.</summary>
    (int Index, string Name)[] OpaqueParametersOf(SilFunction function) {
        if (!_opaqueParameters.TryGetValue(function, out var found)) {
            found = [..function.ParameterNames.Select((name, i) => (i, name)).Where(static p => p.name.StartsWith("$metadata.", StringComparison.Ordinal))
                .Select(static p => (p.i, p.name["$metadata.".Length..]))];
            _opaqueParameters[function] = found;
        }

        return found;
    }

    /// <summary>The type an opaque type is in the function running now.</summary>
    SilType OpaqueTypeOf(OpaqueType opaque) => OpaqueModel(opaque.Name).Type;

    /// <summary>The function running now and its arguments, when it finds associated types' metadata from its tables.</summary>
    (SilFunction Function, IReadOnlyList<object?> Arguments)? _associatedTypes;

    /// <summary>
    ///     An opaque type's metadata in the function running now: a type
    ///     parameter's as given, an associated type's found from the table of
    ///     its base's conformance the first time it is asked for.
    /// </summary>
    MetadataModel OpaqueModel(string name) {
        if (_opaqueTypes is not null && _opaqueTypes.TryGetValue(name, out var model)) {
            return model;
        }

        if (_opaqueTypes is null || _associatedTypes is not var (function, arguments) || function.AssociatedMetadata.Find(a => a.Name == name) is not { } associated) {
            throw new SilTrap($"no metadata for @opaque({name}) here");
        }

        var baseModel = OpaqueModel(associated.Base);
        SilWitnessTable table;
        if (associated.Table is { } given && function.ParameterNames.IndexOf(given) is var at and >= 0 && arguments[at] is SilWitnessTable passed) {
            table = passed;
            foreach (var step in associated.Path) {
                table = module.FindWitnessTable(table.Conformer, table.Bases[step]) ?? WitnessTableOf(table.Conformer, table.Bases[step])
                    ?? throw new SilTrap($"no table of {table.Conformer}'s conformance to {table.Bases[step]}");
            }
        } else {
            table = WitnessTableOf(baseModel.Type, associated.Protocol) ?? throw new SilTrap($"no table of {baseModel.Type}'s conformance to {associated.Protocol}");
        }

        model = MetadataOf(table.AssociatedTypes[associated.Index].Type);
        _opaqueTypes[name] = model;
        return model;
    }

    /// <summary>
    ///     A type as the function running now has it: an opaque type, or an
    ///     array of them, as the metadata it was given says — what an array
    ///     made in unspecialised code holds, so that it is destroyed as that
    ///     wherever it goes.
    /// </summary>
    SilType Concrete(SilType type) =>
        type switch {
            OpaqueType opaque => OpaqueTypeOf(opaque),
            ArrayType { Element: var element } when SilType.MentionsOpaque(element) => new ArrayType(Concrete(element)),
            TupleType tuple when SilType.MentionsOpaque(tuple) => SilType.Tuple([..tuple.Elements.Select(Concrete)]),
            EnumType optional when SilType.MentionsOpaque(optional) && TypeMetadata.OptionalPayload(optional) is { } payload => ConcreteOptional(Concrete(payload)),
            NominalType { TypeArguments.IsEmpty: false } generic when SilType.MentionsOpaque(generic) => ConcreteGeneric(generic),
            _ => type,
        };

    readonly Dictionary<string, NominalType> _concreteGenerics = new(StringComparer.Ordinal);

    /// <summary>
    ///     A generic struct or union of a type known here, as lowering names
    ///     and lays one out: the module's own, or one made alike, its parts
    ///     and type arguments as the function running now has them.
    /// </summary>
    NominalType ConcreteGeneric(NominalType generic) {
        var arguments = generic.TypeArguments.Select(Concrete).ToList();
        var at = generic.Name.IndexOf("$of$", StringComparison.Ordinal);
        var name = $"{(at < 0 ? generic.Name : generic.Name[..at])}$of${string.Join("$", arguments.Select(static a => SilGen.Sanitise(a.ToString())))}";
        if (module.FindType(name) is { } existing) {
            return existing;
        }

        if (_concreteGenerics.TryGetValue(name, out var made)) {
            return made;
        }

        var display = generic.DisplayName is { } written && written.IndexOf('<') is > 0 and var open
            ? $"{written[..open]}<{string.Join(", ", arguments.Select(TypeMetadata.DisplayName))}>"
            : null;
        switch (generic) {
            case StructType structType: {
                var concrete = new StructType(name) { DisplayName = display, TypeArguments = [..arguments] };
                _concreteGenerics[name] = concrete;
                concrete.Fields.AddRange(structType.Fields.Select(f => (f.Name, Concrete(f.Type))));
                concrete.Conformances.AddRange(structType.Conformances);
                return concrete;
            }

            case EnumType enumType: {
                var concrete = new EnumType(name) { DisplayName = display, TypeArguments = [..arguments], IsUnion = enumType.IsUnion };
                _concreteGenerics[name] = concrete;
                concrete.Cases.AddRange(enumType.Cases.Select(c => (c.Name, c.Payload is { } payload ? Concrete(payload) : null)));
                foreach (var (key, labels) in enumType.PayloadLabels) {
                    concrete.PayloadLabels[key] = labels;
                }

                concrete.Conformances.AddRange(enumType.Conformances);
                return concrete;
            }

            default:
                return generic;
        }
    }

    readonly Dictionary<string, EnumType> _concreteOptionals = new(StringComparer.Ordinal);

    /// <summary>An optional of a type known here, as lowering names and lays one out: the module's own, or one made alike.</summary>
    EnumType ConcreteOptional(SilType payload) {
        var name = $"Optional${SilGen.Sanitise(payload.ToString())}";
        if (module.FindType(name) is EnumType existing) {
            return existing;
        }

        if (!_concreteOptionals.TryGetValue(name, out var made)) {
            made = new EnumType(name);
            made.Cases.Add(("Some", payload == SilType.Unit ? null : payload));
            made.Cases.Add(("None", null));
            _concreteOptionals[name] = made;
        }

        return made;
    }

    /// <summary>A value's storage seen as itself: what <c>opaque_address</c> gives, read-only.</summary>
    sealed class Held(object? value) : IReference {
        public object? Get() => value ?? throw new SilTrap("load from uninitialised storage");
        public object? Peek() => value;
        public void Set(object? replaced) => throw new SilTrap("a borrowed value was written");
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

            // A type parameter's value is held as the value of the type it
            // is: an address seen as another type is the same storage, and a
            // value's own storage is the value.
            case "opaque_cast" or "opaque_borrow_as" or "opaque_take_as":
                result = operands[0];
                return true;
            case "metadata_array":
                result = MetadataOf(new ArrayType(((MetadataModel)operands[0]!).Type));
                return true;
            case "opaque_address":
                result = new Held(operands[0]);
                return true;
            // Written out by its type's describer.
            case "generic_describe": {
                var type = (MetadataModel)operands[1]!;
                if (!module.Describers.TryGetValue(type.Type.ToString(), out var describer) || module.Find(describer.Function) is not { } describe) {
                    throw new SilTrap($"a '{type.Described.Name}' with no description");
                }

                result = Call(describe, [operands[0], type], depth + 1);
                return true;
            }
            // An error of one, boxed as 'any Error' by its type's eraser; the value, taken, let go.
            case "opaque_erase_error": {
                var type = OpaqueModel(((OpaqueType)builtin.Operands[0].Type).Name);
                if (!module.ErrorErasers.TryGetValue(type.Type.ToString(), out var eraser) || module.Find(eraser.Function) is not { } erase) {
                    throw new SilTrap($"an error of '{type.Type}' with no way to box it as 'any Error'");
                }

                result = Call(erase, [operands[0], type], depth + 1);
                Release(type.Type, operands[0], depth);
                return true;
            }
            case "opaque_type": {
                var type = (MetadataModel)operands[1]!;
                result = type.Type is ClassType && operands[0] is HeapObject held ? MetadataOf(Alive(held).Type) : type;
                return true;
            }
        }

        // The table of a protocol the table's refines: as the registry finds it for the conformer.
        if (builtin.Name.StartsWith("witness_base:", StringComparison.Ordinal)) {
            var table = (SilWitnessTable)operands[0]!;
            var index = int.Parse(builtin.Name["witness_base:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            result = module.FindWitnessTable(table.Conformer, table.Bases[index]) ?? WitnessTableOf(table.Conformer, table.Bases[index]);
            return true;
        }

        if (builtin.Name.StartsWith("witness_generic:", StringComparison.Ordinal)) {
            var table = (SilWitnessTable)operands[0]!;
            var index = int.Parse(builtin.Name["witness_generic:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            result = table.GenericEntries[index].Function ?? throw new SilTrap("a requirement with no witness for generic code");
            return true;
        }

        if (builtin.Name.StartsWith("opaque_metadata:", StringComparison.Ordinal)) {
            result = OpaqueModel(builtin.Name["opaque_metadata:".Length..]);
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

        // A table code that knows the type refers to: the one made for it, else the registry's.
        if (builtin.Name.StartsWith("witness_table_known:", StringComparison.Ordinal)) {
            var type = ((MetadataModel)operands[0]!).Type;
            var protocol = builtin.Name["witness_table_known:".Length..];
            result = module.FindWitnessTable(type, protocol) ?? WitnessTableOf(type, protocol);
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
