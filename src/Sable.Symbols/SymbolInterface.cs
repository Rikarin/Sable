using System.Collections.Immutable;
using System.Text;

namespace Sable.Symbols;

/// <summary>
///     A library unit's bound symbols as bytes, and back (docs/05 §5.4, #182):
///     the symbol section of its interface (<c>.sbi</c>). What is written is
///     what binding another unit reads of a declaration — types and their
///     kinds, type parameters with their constraints, base types and
///     requirements, members with their signatures, attributes, raw values,
///     const-generic predicates, and the facts stage 2 moved off syntax
///     (synthesised, has a body, <c>nonisolated</c>) — and never syntax, so
///     symbols read back answer as the ones bound from source did, with none.
/// </summary>
/// <remarks>
///     <para>
///         The bytes are a table of declarations in preorder — each module's
///         members, each type's members (an extension's among them, by the
///         extension it is in), type parameters and parameters with what owns
///         them — then, for each, the types it refers to. A type is written
///         as a tree; a declaration of the table by its index, one of another
///         unit by its module and dotted path, which reading resolves against
///         the units already read (<see cref="Read" />).
///     </para>
///     <para>
///         What a declaration's body holds is not here: default arguments,
///         initial values and constants' values are expressions other units
///         lower, and come with the interface's SIL or its source. Accessors
///         are not either: a property's type and whether it has a setter are
///         what binding reads, and the accessors are functions of the SIL.
///     </para>
/// </remarks>
public static class SymbolInterface {
    /// <summary>The section's own version: bump when what is written, or what it means, changes.</summary>
    public const int Version = 1;

    enum Entry : byte {
        Module = 1,
        Type = 2,
        TypeParameter = 3,
        Function = 4,
        Parameter = 5,
        Field = 6,
        Alias = 7,
        UnionCase = 8,
        EnumCase = 9,
        Extension = 10,
    }

    enum Tag : byte {
        None = 0,
        Named = 1,
        TypeParameter = 2,
        Array = 3,
        Optional = 4,
        Reference = 5,
        Tuple = 6,
        Function = 7,
        StructuralUnion = 8,
        Existential = 9,
        Opaque = 10,
        Metatype = 11,
        Error = 12,
        Value = 13,
        Composition = 14,
        DependentMember = 15,
        RecursiveUnion = 16,
        ExternalNamed = 17,
        ExternalTypeParameter = 18,
    }

    /// <summary>What a type parameter is to what owns it.</summary>
    enum Role : byte {
        Declared = 0,
        Self = 1,
        Associated = 2,
    }

    // =======================================================================
    // Writing
    // =======================================================================

    /// <summary>
    ///     The symbols of the modules named, with every extension they declare
    ///     of other modules' types, as bytes. Those types are looked for in
    ///     <paramref name="context" /> — the compilation's modules, or the
    ///     units read before — and, without it, in what the modules import.
    /// </summary>
    public static byte[] Write(IEnumerable<ModuleSymbol> modules, IEnumerable<ModuleSymbol>? context = null) {
        var writer = new SymbolWriter([..modules], context?.ToList());
        return writer.Write();
    }

    sealed class SymbolWriter(List<ModuleSymbol> modules, List<ModuleSymbol>? context) {
        readonly List<Symbol> _table = [];
        readonly Dictionary<Symbol, int> _ids = new(ReferenceEqualityComparer.Instance);
        readonly HashSet<ModuleSymbol> _own = new(modules, ReferenceEqualityComparer.Instance);

        /// <summary>Types of the written modules a type names that are not declared yet.</summary>
        readonly List<NamedTypeSymbol> _missing = [];

        public byte[] Write() {
            foreach (var module in modules) {
                Declare(module);
            }

            // Members other units' extensions add to these modules' types are
            // theirs; what these modules' extensions add to other units' types
            // is these modules', and is written with what it extends.
            foreach (var module in modules) {
                foreach (var foreign in ForeignExtensions(module)) {
                    Declare(foreign);
                }
            }

            // Types of these modules no member list holds — the types an
            // array's or an optional's extension declares its members on —
            // are declared as the types written name them, until none is new.
            for (var done = 0; done < _table.Count;) {
                var upTo = _table.Count;
                using var nowhere = new BinaryWriter(Stream.Null);
                for (; done < upTo; done++) {
                    WriteBody(nowhere, _table[done]);
                }

                foreach (var missing in _missing.ToList()) {
                    Declare(missing);
                }

                _missing.Clear();
            }

            using var stream = new MemoryStream();
            using var output = new BinaryWriter(stream, Encoding.UTF8);
            output.Write(Version);
            output.Write(_table.Count);
            foreach (var symbol in _table) {
                WriteHeader(output, symbol);
            }

            foreach (var symbol in _table) {
                WriteBody(output, symbol);
            }

            output.Flush();
            return stream.ToArray();
        }

        /// <summary>The extensions a module declares of types that are not its own, in the order they extend them.</summary>
        IEnumerable<ExtensionSymbol> ForeignExtensions(ModuleSymbol module) {
            var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
            foreach (var type in AllTypes()) {
                if (seen.Add(type)) {
                    // A conformance's extension is listed; one that only adds members is found by them.
                    var found = type.Extensions.Concat(type.Members.Select(static m => m.ContainingSymbol).OfType<ExtensionSymbol>());
                    foreach (var extension in found) {
                        if (ReferenceEquals(extension.DeclaringModule, module) && !_ids.ContainsKey(extension)) {
                            yield return extension;
                        }
                    }
                }
            }
        }

        /// <summary>Every type the written symbols' types and members name, of any module — where the foreign extensions are found.</summary>
        IEnumerable<NamedTypeSymbol> AllTypes() {
            var found = new List<NamedTypeSymbol>();
            var seen = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
            foreach (var module in AllModules()) {
                Collect(module);
            }

            return found;

            void Collect(Symbol symbol) {
                if (!seen.Add(symbol)) {
                    return;
                }

                var members = symbol switch {
                    ModuleSymbol m => m.Members,
                    NamedTypeSymbol t => t.OriginalDefinition.Members,
                    _ => [],
                };
                if (symbol is NamedTypeSymbol named) {
                    found.Add(named.OriginalDefinition);
                }

                foreach (var member in members) {
                    if (member is NamedTypeSymbol) {
                        Collect(member);
                    }
                }
            }
        }

        /// <summary>The modules the written ones import, and they themselves: where the types they extend are declared.</summary>
        IEnumerable<ModuleSymbol> AllModules() {
            var all = new List<ModuleSymbol>();
            var seen = new HashSet<ModuleSymbol>(ReferenceEqualityComparer.Instance);
            foreach (var module in modules.Concat(context ?? [])) {
                Add(module);
            }

            return all;

            void Add(ModuleSymbol module) {
                if (!seen.Add(module)) {
                    return;
                }

                all.Add(module);
                foreach (var imported in module.Imports) {
                    Add(imported);
                }
            }
        }

        int Declare(Symbol symbol) {
            if (_ids.TryGetValue(symbol, out var known)) {
                return known;
            }

            var id = _table.Count;
            _ids[symbol] = id;
            _table.Add(symbol);
            switch (symbol) {
                case ModuleSymbol module:
                    foreach (var member in module.Members) {
                        DeclareMember(member);
                    }

                    break;
                case NamedTypeSymbol type:
                    foreach (var parameter in type.TypeParameters) {
                        Declare(parameter);
                    }

                    if (type.SelfTypeParameter is { } self) {
                        Declare(self);
                    }

                    foreach (var associated in type.AssociatedTypes) {
                        Declare(associated);
                    }

                    // An extension first, so that its members find it.
                    foreach (var extension in type.Extensions) {
                        if (extension.DeclaringModule is null || _own.Contains(extension.DeclaringModule)) {
                            Declare(extension);
                        }
                    }

                    foreach (var member in type.Members) {
                        DeclareMember(member);
                    }

                    break;
                case ExtensionSymbol extension:
                    foreach (var alias in extension.TypeParameterAliases) {
                        Declare(alias);
                    }

                    // A foreign extension's members are in the type it extends, among that unit's.
                    if (!_own.Contains(extension.ExtendedType.OriginalDefinition.ContainingModule!)) {
                        foreach (var member in extension.ExtendedType.OriginalDefinition.Members) {
                            if (ReferenceEquals(member.ContainingSymbol, extension)) {
                                DeclareMember(member);
                            }
                        }
                    }

                    break;
                case FunctionSymbol function:
                    foreach (var parameter in function.TypeParameters) {
                        Declare(parameter);
                    }

                    foreach (var parameter in function.Parameters) {
                        Declare(parameter);
                    }

                    break;
                case UnionCaseSymbol unionCase:
                    foreach (var parameter in unionCase.Payload) {
                        Declare(parameter);
                    }

                    break;
                case AliasSymbol alias:
                    foreach (var parameter in alias.TypeParameters) {
                        Declare(parameter);
                    }

                    break;
            }

            return id;
        }

        void DeclareMember(Symbol member) {
            // A member another unit's extension adds is that unit's.
            if (member.ContainingSymbol is ExtensionSymbol { DeclaringModule: { } declaring } && !_own.Contains(declaring)) {
                return;
            }

            if (member is NamedTypeSymbol or FunctionSymbol or FieldSymbol or AliasSymbol or UnionCaseSymbol or EnumCaseSymbol) {
                // An extension the type does not list (an array's or an optional's carrier's) is declared as its first member is.
                if (member.ContainingSymbol is ExtensionSymbol extension) {
                    Declare(extension);
                }

                Declare(member);
            }
        }

        int IdOf(Symbol? symbol) => symbol is not null && _ids.TryGetValue(symbol, out var id) ? id : -1;

        void WriteHeader(BinaryWriter output, Symbol symbol) {
            output.Write((byte)(symbol switch {
                ModuleSymbol => Entry.Module,
                NamedTypeSymbol => Entry.Type,
                TypeParameterSymbol => Entry.TypeParameter,
                FunctionSymbol => Entry.Function,
                ParameterSymbol => Entry.Parameter,
                FieldSymbol => Entry.Field,
                AliasSymbol => Entry.Alias,
                UnionCaseSymbol => Entry.UnionCase,
                EnumCaseSymbol => Entry.EnumCase,
                ExtensionSymbol => Entry.Extension,
                _ => throw new InvalidOperationException($"no entry for a {symbol.Kind}"),
            }));
            output.Write(symbol.Name);
            output.Write(symbol is ExtensionSymbol ? -1 : IdOf(symbol.ContainingSymbol));
            output.Write((byte)symbol.DeclaredAccessibility);
            output.Write(symbol.IsSynthesized);
            output.Write(symbol.IsNonisolated);
            output.Write(symbol.Attributes.Length);
            foreach (var attribute in symbol.Attributes) {
                output.Write(attribute.Name);
                WriteString(output, attribute.ArgumentList);
                output.Write(attribute.FromExtension);
            }

            switch (symbol) {
                case NamedTypeSymbol type:
                    output.Write(Listed(type));
                    output.Write((byte)type.TypeKind);
                    output.Write(type.IsVariableSize);
                    output.Write(Flags(type.IsOpen, type.IsNoncopyable, type.IsInlineLayout, type.IsPacked, type.IsReflectable, type.IsFlags, type.IsSealed, type.IsFinal, type.IsAbstract));
                    output.Write(type.Alignment);
                    break;
                case TypeParameterSymbol parameter:
                    output.Write(parameter.Ordinal);
                    output.Write((byte)RoleOf(parameter));
                    output.Write((byte)parameter.Variance);
                    output.Write(parameter.AllowsNoncopyable);
                    break;
                case FunctionSymbol function:
                    output.Write((byte)function.FunctionKind);
                    output.Write((byte)function.Fixity);
                    output.Write(Flags(function.IsStatic, function.IsAsync, function.IsMutating, function.IsPolymorphic, function.IsOverride, function.IsOpen,
                        function.IsFinal, function.IsConst, function.IsUnsafe, function.IsRethrows, function.IsAbstract, function.HasBody));
                    WriteString(output, function.ForeignName);
                    WriteString(output, function.ExportName);
                    WriteString(output, function.Builtin);
                    break;
                case ParameterSymbol parameter:
                    WriteString(output, parameter.Label);
                    output.Write(parameter.Ordinal);
                    output.Write((byte)parameter.Modifier);
                    output.Write(Flags(parameter.HasDefaultValue, parameter.IsVariadic, parameter.IsEscaping, parameter.IsAutoclosure, parameter.IsSendable));
                    break;
                case FieldSymbol field:
                    output.Write(Flags(field.IsMutable, field.IsAsyncGetter, field.IsStatic, field.IsLazy, field.IsImmortal, field.IsOverride, field.IsFinal,
                        field.IsComputed, field.HasSetter, field.IsAbstract, field.HasInitializer));
                    output.Write(field.SetterAccessibility is { } setter ? (byte)setter : byte.MaxValue);
                    output.Write((byte)field.ReferenceKind);
                    WriteString(output, field.ExportName);
                    WriteString(output, field.Builtin);
                    break;
                case AliasSymbol alias:
                    output.Write(Listed(alias));
                    break;
                case EnumCaseSymbol enumCase:
                    output.Write(enumCase.HasExplicitValue);
                    output.Write(enumCase.RawValue);
                    break;
                case ExtensionSymbol extension:
                    WriteString(output, extension.DeclaringModule?.Name);
                    WriteType(output, extension.ExtendedType.OriginalDefinition);
                    output.Write(extension.ExtendedType.OriginalDefinition.Extensions.Contains(extension));
                    break;
            }
        }

        /// <summary>Whether a symbol is among its container's members, as all but a carrier type are.</summary>
        static bool Listed(Symbol symbol) =>
            symbol.ContainingSymbol switch {
                ModuleSymbol module => module.Members.Contains(symbol),
                NamedTypeSymbol type => type.Members.Contains(symbol),
                ExtensionSymbol extension => extension.ExtendedType.OriginalDefinition.Members.Contains(symbol),
                _ => true,
            };

        static Role RoleOf(TypeParameterSymbol parameter) =>
            parameter.ContainingSymbol is NamedTypeSymbol owner
                ? ReferenceEquals(owner.SelfTypeParameter, parameter) ? Role.Self
                : owner.AssociatedTypes.Contains(parameter) ? Role.Associated
                : Role.Declared
                : Role.Declared;

        void WriteBody(BinaryWriter output, Symbol symbol) {
            switch (symbol) {
                case NamedTypeSymbol type:
                    WriteType(output, type.ExtendedArray);
                    WriteType(output, type.ExtendedOptional);
                    WriteTypes(output, type.BaseTypes);
                    WriteRequirements(output, type.SameTypeRequirements, type.ConformanceRequirements);
                    WriteTypes(output, [..type.ArrayConformers]);
                    WriteTypes(output, [..type.DerivedConformances]);
                    output.Write(type.OptionalConformances.Length);
                    foreach (var extension in type.OptionalConformances) {
                        output.Write(IdOf(extension));
                    }

                    output.Write(type.ValuePredicates.Length);
                    foreach (var predicate in type.ValuePredicates) {
                        WritePredicate(output, predicate);
                    }

                    break;
                case TypeParameterSymbol parameter:
                    WriteTypes(output, parameter.Constraints);
                    WriteType(output, parameter.DefaultType);
                    WriteType(output, parameter.ValueType);
                    break;
                case FunctionSymbol function:
                    WriteType(output, function.ReturnType);
                    WriteType(output, function.ThrownType);
                    WriteType(output, function.RethrownAlso);
                    WriteType(output, function.HiddenResultType);
                    WriteType(output, function.ExplicitProtocol);
                    WriteRequirements(output, function.SameTypeRequirements, function.ConformanceRequirements);
                    break;
                case ParameterSymbol parameter:
                    WriteType(output, parameter.Type);
                    break;
                case FieldSymbol field:
                    WriteType(output, field.Type);
                    WriteType(output, field.HiddenType);
                    output.Write(IdOf(field.BackingField));
                    output.Write(IdOf(field.BackedProperty));
                    break;
                case AliasSymbol alias:
                    WriteType(output, alias.Type);
                    break;
                case ExtensionSymbol extension:
                    WriteTypes(output, extension.Conformances);
                    WriteRequirements(output, extension.SameTypeRequirements, extension.ConformanceRequirements);
                    break;
            }
        }

        void WriteRequirements(BinaryWriter output, ImmutableArray<SameTypeRequirement> same, ImmutableArray<ConformanceRequirement> conformance) {
            output.Write(same.Length);
            foreach (var (left, right) in same) {
                WriteType(output, left);
                WriteType(output, right);
            }

            output.Write(conformance.Length);
            foreach (var (subject, constraint) in conformance) {
                WriteType(output, subject);
                WriteType(output, constraint);
            }
        }

        void WriteTypes(BinaryWriter output, ImmutableArray<TypeSymbol> types) {
            output.Write(types.Length);
            foreach (var type in types) {
                WriteType(output, type);
            }
        }

        void WriteType(BinaryWriter output, TypeSymbol? type) {
            switch (type) {
                case null:
                    output.Write((byte)Tag.None);
                    break;
                case NamedTypeSymbol named:
                    if (IdOf(named.OriginalDefinition) is var id and >= 0) {
                        output.Write((byte)Tag.Named);
                        output.Write(id);
                    } else {
                        if (named.OriginalDefinition.ContainingModule is { } module && _own.Contains(module)) {
                            _missing.Add(named.OriginalDefinition);
                        }

                        output.Write((byte)Tag.ExternalNamed);
                        WriteExternal(output, named.OriginalDefinition);
                    }

                    WriteTypes(output, named.TypeArguments);
                    break;
                case TypeParameterSymbol parameter:
                    if (IdOf(parameter) is var parameterId and >= 0) {
                        output.Write((byte)Tag.TypeParameter);
                        output.Write(parameterId);
                    } else if (parameter.ContainingSymbol is NamedTypeSymbol owner && IdOf(owner.OriginalDefinition) < 0) {
                        // Another unit's type's parameter: its 'Element', by its owner and name.
                        output.Write((byte)Tag.ExternalTypeParameter);
                        WriteExternal(output, owner.OriginalDefinition);
                        output.Write(parameter.Name);
                    } else {
                        // A parameter no declaration here owns: a function's 'some P', made as its type is asked for.
                        output.Write((byte)Tag.Error);
                        output.Write(parameter.Name);
                    }

                    break;
                case ArrayTypeSymbol array:
                    output.Write((byte)Tag.Array);
                    WriteType(output, array.ElementType);
                    output.Write((byte)array.ArrayKind);
                    output.Write(array.Capacity ?? -1);
                    WriteType(output, array.CapacityParameter);
                    break;
                case OptionalTypeSymbol optional:
                    output.Write((byte)Tag.Optional);
                    WriteType(output, optional.UnderlyingType);
                    output.Write(optional.IsImplicitlyUnwrapped);
                    break;
                case ReferenceTypeSymbol reference:
                    output.Write((byte)Tag.Reference);
                    WriteType(output, reference.ReferencedType);
                    WriteString(output, reference.RelativeWidth);
                    WriteString(output, reference.Pool);
                    break;
                case TupleTypeSymbol tuple:
                    output.Write((byte)Tag.Tuple);
                    WriteTypes(output, tuple.ElementTypes);
                    foreach (var name in tuple.ElementNames) {
                        WriteString(output, name);
                    }

                    break;
                case FunctionTypeSymbol function:
                    output.Write((byte)Tag.Function);
                    WriteTypes(output, function.ParameterTypes);
                    WriteType(output, function.ReturnType);
                    output.Write(function.IsAsync);
                    WriteType(output, function.ThrownType);
                    output.Write(function.IsRethrows);
                    output.Write(function.InOutParameters.Length);
                    foreach (var inOut in function.InOutParameters) {
                        output.Write(inOut);
                    }

                    break;
                case StructuralUnionTypeSymbol union:
                    output.Write((byte)Tag.StructuralUnion);
                    WriteTypes(output, union.Cases);
                    break;
                case ExistentialTypeSymbol existential:
                    output.Write((byte)Tag.Existential);
                    WriteType(output, existential.Constraint);
                    break;
                case OpaqueTypeSymbol opaque:
                    output.Write((byte)Tag.Opaque);
                    WriteType(output, opaque.Constraint);
                    output.Write(IdOf(opaque.Origin));
                    WriteTypes(output, opaque.Arguments);
                    break;
                case MetatypeTypeSymbol metatype:
                    output.Write((byte)Tag.Metatype);
                    WriteType(output, metatype.InstanceType);
                    break;
                case ErrorTypeSymbol error:
                    output.Write((byte)Tag.Error);
                    output.Write(error.Name);
                    break;
                case ValueTypeArgumentSymbol value:
                    output.Write((byte)Tag.Value);
                    output.Write(value.Value);
                    break;
                case CompositionTypeSymbol composition:
                    output.Write((byte)Tag.Composition);
                    WriteTypes(output, composition.Parts);
                    break;
                case DependentMemberTypeSymbol dependent:
                    output.Write((byte)Tag.DependentMember);
                    WriteType(output, dependent.BaseType);
                    WriteType(output, dependent.Associated);
                    break;
                case RecursiveUnionTypeSymbol recursive:
                    output.Write((byte)Tag.RecursiveUnion);
                    output.Write(IdOf(recursive.Alias));
                    break;
                default:
                    throw new InvalidOperationException($"no tag for the type '{type.ToDisplayString()}' ({type.GetType().Name})");
            }
        }

        /// <summary>A type of another unit: its module's name and its dotted path in it.</summary>
        static void WriteExternal(BinaryWriter output, NamedTypeSymbol type) {
            var path = new List<string>();
            Symbol? at = type;
            for (; at is NamedTypeSymbol named; at = named.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : named.ContainingSymbol) {
                path.Insert(0, named.Name);
            }

            output.Write((at as ModuleSymbol)?.Name ?? "");
            output.Write(string.Join(".", path));
        }

        static void WritePredicate(BinaryWriter output, ValuePredicate predicate) {
            output.Write(predicate.Text);
            WriteNode(output, predicate.Root);

            static void WriteNode(BinaryWriter output, ValuePredicate.Node node) {
                switch (node) {
                    case ValuePredicate.Number number:
                        output.Write((byte)0);
                        output.Write(number.Value);
                        break;
                    case ValuePredicate.Truth truth:
                        output.Write((byte)1);
                        output.Write(truth.Value);
                        break;
                    case ValuePredicate.Parameter parameter:
                        output.Write((byte)2);
                        output.Write(parameter.Name);
                        break;
                    case ValuePredicate.Unary unary:
                        output.Write((byte)3);
                        output.Write(unary.Operator);
                        WriteNode(output, unary.Operand);
                        break;
                    case ValuePredicate.Binary binary:
                        output.Write((byte)4);
                        output.Write(binary.Operator);
                        WriteNode(output, binary.Left);
                        WriteNode(output, binary.Right);
                        break;
                }
            }
        }

        static int Flags(params bool[] flags) {
            var value = 0;
            for (var i = 0; i < flags.Length; i++) {
                if (flags[i]) {
                    value |= 1 << i;
                }
            }

            return value;
        }
    }

    static void WriteString(BinaryWriter output, string? value) {
        output.Write(value is not null);
        if (value is not null) {
            output.Write(value);
        }
    }

    // =======================================================================
    // Reading
    // =======================================================================

    /// <summary>
    ///     The modules written in <paramref name="bytes" />, as symbols with no
    ///     syntax. A type of another unit is found among
    ///     <paramref name="dependencies" /> — the modules of the units this
    ///     one was compiled against, read first.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a symbol section this compiler wrote, or name a type no dependency declares.</exception>
    public static IReadOnlyList<ModuleSymbol> Read(ReadOnlySpan<byte> bytes, IEnumerable<ModuleSymbol>? dependencies = null) {
        try {
            return new SymbolReader(bytes.ToArray(), [..dependencies ?? []]).Read();
        } catch (Exception failure) when (failure is EndOfStreamException or IOException or ArgumentException or InvalidCastException
                                              or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException or FormatException
                                              or InvalidOperationException or NullReferenceException) {
            throw new InvalidDataException($"not a symbol section this compiler can read: {failure.Message}", failure);
        }
    }

    sealed class SymbolReader(byte[] bytes, List<ModuleSymbol> dependencies) {
        readonly BinaryReader _input = new(new MemoryStream(bytes, writable: false), Encoding.UTF8);
        Symbol[] _table = [];

        public IReadOnlyList<ModuleSymbol> Read() {
            if (_input.ReadInt32() != Version) {
                throw new InvalidDataException("the symbol section is of another version");
            }

            var count = _input.ReadInt32();
            if (count < 0 || count > bytes.Length) {
                throw new InvalidDataException("the symbol table's length is wrong");
            }

            _table = new Symbol[count];
            var entries = new Entry[count];
            for (var i = 0; i < count; i++) {
                (entries[i], _table[i]) = ReadHeader(i);
            }

            for (var i = 0; i < count; i++) {
                ReadBody(_table[i]);
            }

            if (_input.BaseStream.Position != _input.BaseStream.Length) {
                throw new InvalidDataException("the symbol section has bytes after its end");
            }

            // Each symbol's lists, in the order they were declared.
            foreach (var symbol in _table) {
                switch (symbol) {
                    case NamedTypeSymbol type:
                        type.TypeParameters = [..Owned<TypeParameterSymbol>(type).Where(p => !ReferenceEquals(p, type.SelfTypeParameter) && !type.AssociatedTypes.Contains(p))];
                        break;
                    case FunctionSymbol function:
                        function.TypeParameters = [..Owned<TypeParameterSymbol>(function)];
                        function.Parameters = [..Owned<ParameterSymbol>(function)];
                        break;
                    case UnionCaseSymbol unionCase:
                        unionCase.Payload = [..Owned<ParameterSymbol>(unionCase)];
                        break;
                    case AliasSymbol alias:
                        alias.TypeParameters = [..Owned<TypeParameterSymbol>(alias)];
                        break;
                }
            }

            return [.._table.OfType<ModuleSymbol>()];
        }

        readonly Dictionary<Symbol, List<Symbol>> _owned = new(ReferenceEqualityComparer.Instance);

        IEnumerable<T> Owned<T>(Symbol owner) => _owned.TryGetValue(owner, out var list) ? list.OfType<T>() : [];

        Symbol At(int id) =>
            id >= 0 && id < _table.Length && _table[id] is { } symbol ? symbol : throw new InvalidDataException($"no declaration {id} is read yet");

        Symbol? AtOrNull(int id) => id < 0 ? null : At(id);

        (Entry, Symbol) ReadHeader(int index) {
            var entry = (Entry)_input.ReadByte();
            var name = _input.ReadString();
            var containerId = _input.ReadInt32();
            if (containerId >= index) {
                throw new InvalidDataException("a declaration is inside one after it");
            }

            var container = AtOrNull(containerId);
            var accessibility = (Accessibility)_input.ReadByte();
            var synthesized = _input.ReadBoolean();
            var nonisolated = _input.ReadBoolean();
            var attributeCount = _input.ReadInt32();
            if (attributeCount < 0 || attributeCount > bytes.Length) {
                throw new InvalidDataException("an attribute list's length is wrong");
            }

            var attributes = ImmutableArray.CreateBuilder<SymbolAttribute>(attributeCount);
            for (var i = 0; i < attributeCount; i++) {
                attributes.Add(new(_input.ReadString(), ReadString(_input), _input.ReadBoolean()));
            }

            Symbol symbol;
            switch (entry) {
                case Entry.Module:
                    symbol = new ModuleSymbol(name);
                    break;
                case Entry.Type: {
                    var listed = _input.ReadBoolean();
                    var kind = (TypeKind)_input.ReadByte();
                    var variableSize = _input.ReadBoolean();
                    var flags = _input.ReadInt32();
                    var type = new NamedTypeSymbol(name, kind, container, null, accessibility, variableSize) {
                        IsOpen = Has(flags, 0), IsNoncopyable = Has(flags, 1), IsInlineLayout = Has(flags, 2), IsPacked = Has(flags, 3),
                        IsReflectable = Has(flags, 4), IsFlags = Has(flags, 5), IsSealed = Has(flags, 6), IsFinal = Has(flags, 7), IsAbstract = Has(flags, 8),
                        Alignment = _input.ReadInt32(),
                    };
                    if (listed) {
                        AddMember(container, type);
                    }

                    symbol = type;
                    break;
                }
                case Entry.TypeParameter: {
                    var ordinal = _input.ReadInt32();
                    var role = (Role)_input.ReadByte();
                    var parameter = new TypeParameterSymbol(name, Need(container), null, ordinal) {
                        Variance = (VarianceKind)_input.ReadByte(),
                        AllowsNoncopyable = _input.ReadBoolean(),
                    };
                    switch (role) {
                        case Role.Self when container is NamedTypeSymbol owner:
                            owner.SelfTypeParameter = parameter;
                            break;
                        case Role.Associated when container is NamedTypeSymbol owner:
                            owner.AssociatedTypes = owner.AssociatedTypes.Add(parameter);
                            break;
                        default:
                            Own(Need(container), parameter);
                            break;
                    }

                    symbol = parameter;
                    break;
                }
                case Entry.Function: {
                    var kind = (FunctionKind)_input.ReadByte();
                    var fixity = (OperatorFixity)_input.ReadByte();
                    var flags = _input.ReadInt32();
                    var function = new FunctionSymbol(name, Need(container), null, accessibility, kind) {
                        Fixity = fixity,
                        IsStatic = Has(flags, 0), IsAsync = Has(flags, 1), IsMutating = Has(flags, 2), IsPolymorphic = Has(flags, 3), IsOverride = Has(flags, 4),
                        IsOpen = Has(flags, 5), IsFinal = Has(flags, 6), IsConst = Has(flags, 7), IsUnsafe = Has(flags, 8), IsRethrows = Has(flags, 9),
                        IsAbstract = Has(flags, 10), HasBody = Has(flags, 11),
                        ForeignName = ReadString(_input), ExportName = ReadString(_input), Builtin = ReadString(_input),
                    };
                    AddMember(container, function);
                    symbol = function;
                    break;
                }
                case Entry.Parameter: {
                    var label = ReadString(_input);
                    var ordinal = _input.ReadInt32();
                    var modifier = (ParameterModifier)_input.ReadByte();
                    var flags = _input.ReadInt32();
                    var parameter = new ParameterSymbol(name, label, Need(container), null, ordinal) {
                        Modifier = modifier,
                        HasDefaultValue = Has(flags, 0), IsVariadic = Has(flags, 1), IsEscaping = Has(flags, 2), IsAutoclosure = Has(flags, 3), IsSendable = Has(flags, 4),
                    };
                    Own(Need(container), parameter);
                    symbol = parameter;
                    break;
                }
                case Entry.Field: {
                    var flags = _input.ReadInt32();
                    var setter = _input.ReadByte();
                    var field = new FieldSymbol(name, Need(container), null, accessibility, Has(flags, 0)) {
                        IsAsyncGetter = Has(flags, 1), IsStatic = Has(flags, 2), IsLazy = Has(flags, 3), IsImmortal = Has(flags, 4), IsOverride = Has(flags, 5),
                        IsFinal = Has(flags, 6), IsComputed = Has(flags, 7), HasSetter = Has(flags, 8), IsAbstract = Has(flags, 9), HasInitializer = Has(flags, 10),
                        SetterAccessibility = setter == byte.MaxValue ? null : (Accessibility)setter,
                        ReferenceKind = (ReferenceKind)_input.ReadByte(),
                        ExportName = ReadString(_input),
                        Builtin = ReadString(_input),
                    };
                    AddMember(container, field);
                    symbol = field;
                    break;
                }
                case Entry.Alias: {
                    var alias = new AliasSymbol(name, Need(container), null, accessibility);
                    if (_input.ReadBoolean()) {
                        AddMember(container, alias);
                    } else {
                        // An extension's name for a type parameter of what it extends.
                        Own(Need(container), alias);
                    }

                    symbol = alias;
                    break;
                }
                case Entry.UnionCase: {
                    var unionCase = new UnionCaseSymbol(name, container as NamedTypeSymbol ?? throw new InvalidDataException("a union case outside a type"), null);
                    AddMember(container, unionCase);
                    symbol = unionCase;
                    break;
                }
                case Entry.EnumCase: {
                    var enumCase = new EnumCaseSymbol(name, container as NamedTypeSymbol ?? throw new InvalidDataException("an enum case outside a type"), null) {
                        HasExplicitValue = _input.ReadBoolean(),
                        RawValue = _input.ReadInt64(),
                    };
                    AddMember(container, enumCase);
                    symbol = enumCase;
                    break;
                }
                case Entry.Extension: {
                    var declaringModule = ReadString(_input);
                    var extended = ReadType() as NamedTypeSymbol ?? throw new InvalidDataException("an extension of no type");
                    var extension = new ExtensionSymbol(extended, null) {
                        DeclaringModule = declaringModule is null ? null : FindModule(declaringModule, index),
                    };
                    if (_input.ReadBoolean()) {
                        extended.Extensions = extended.Extensions.Add(extension);
                    }
                    symbol = extension;
                    break;
                }
                default:
                    throw new InvalidDataException($"no declaration of kind {(int)entry}");
            }

            symbol.IsSynthesized = synthesized;
            symbol.IsNonisolated = nonisolated;
            symbol.Attributes = attributes.MoveToImmutable();
            return (entry, symbol);
        }

        static Symbol Need(Symbol? container) => container ?? throw new InvalidDataException("a declaration that needs a container has none");


        void Own(Symbol owner, Symbol owned) {
            if (!_owned.TryGetValue(owner, out var list)) {
                _owned[owner] = list = [];
            }

            list.Add(owned);
        }

        /// <summary>A member, added to what it is declared in: an extension's to the type it extends.</summary>
        static void AddMember(Symbol? container, Symbol member) {
            switch (container) {
                case ModuleSymbol module:
                    module.AddMember(member);
                    break;
                case NamedTypeSymbol type:
                    type.AddMember(member);
                    break;
                case ExtensionSymbol extension:
                    extension.ExtendedType.AddMember(member);
                    break;
                default:
                    throw new InvalidDataException($"'{member.Name}' is declared in nothing that has members");
            }
        }

        ModuleSymbol FindModule(string name, int before) {
            for (var i = 0; i < before; i++) {
                if (_table[i] is ModuleSymbol { } module && module.Name == name) {
                    return module;
                }
            }

            return dependencies.FirstOrDefault(m => m.Name == name) ?? new ModuleSymbol(name);
        }

        void ReadBody(Symbol symbol) {
            switch (symbol) {
                case NamedTypeSymbol type: {
                    type.ExtendedArray = ReadType() as ArrayTypeSymbol;
                    type.ExtendedOptional = ReadType() as OptionalTypeSymbol;
                    type.BaseTypes = ReadTypes();
                    (type.SameTypeRequirements, type.ConformanceRequirements) = ReadRequirements();
                    type.ArrayConformers = [..ReadTypes().Select(static t => t as NamedTypeSymbol ?? throw new InvalidDataException("an array conformer is not a named type"))];
                    type.DerivedConformances = [..ReadTypes().Select(static t => t as NamedTypeSymbol ?? throw new InvalidDataException("a derived conformance is not a named type"))];
                    var optional = Count();
                    var conformances = ImmutableArray.CreateBuilder<ExtensionSymbol>(optional);
                    for (var i = 0; i < optional; i++) {
                        conformances.Add(At(_input.ReadInt32()) as ExtensionSymbol ?? throw new InvalidDataException("an optional conformance is not an extension"));
                    }

                    type.OptionalConformances = conformances.MoveToImmutable();
                    var predicates = Count();
                    var read = ImmutableArray.CreateBuilder<ValuePredicate>(predicates);
                    for (var i = 0; i < predicates; i++) {
                        read.Add(new(_input.ReadString(), ReadNode()));
                    }

                    type.ValuePredicates = read.MoveToImmutable();
                    break;
                }
                case TypeParameterSymbol parameter:
                    parameter.Constraints = ReadTypes();
                    parameter.DefaultType = ReadType();
                    parameter.ValueType = ReadType();
                    break;
                case FunctionSymbol function: {
                    var result = ReadType() ?? throw new InvalidDataException("a function with no return type");
                    var thrown = ReadType();
                    var rethrown = ReadType();
                    function.ReturnTypeResolver = () => result;
                    function.ThrownTypeResolver = () => thrown;
                    function.RethrownAlsoResolver = () => rethrown;
                    function.HiddenResultType = ReadType();
                    var explicitProtocol = ReadType();
                    function.ExplicitProtocolResolver = () => explicitProtocol;
                    (function.SameTypeRequirements, function.ConformanceRequirements) = ReadRequirements();
                    break;
                }
                case ParameterSymbol parameter: {
                    var type = ReadType() ?? throw new InvalidDataException("a parameter with no type");
                    parameter.TypeResolver = () => type;
                    break;
                }
                case FieldSymbol field: {
                    var type = ReadType() ?? throw new InvalidDataException("a field with no type");
                    field.TypeResolver = () => type;
                    field.HiddenType = ReadType();
                    field.BackingField = AtOrNull(_input.ReadInt32()) as FieldSymbol;
                    field.BackedProperty = AtOrNull(_input.ReadInt32()) as FieldSymbol;
                    break;
                }
                case AliasSymbol alias: {
                    var type = ReadType() ?? throw new InvalidDataException("an alias of no type");
                    alias.TypeResolver = () => type;
                    break;
                }
                case ExtensionSymbol extension:
                    extension.Conformances = ReadTypes();
                    (extension.SameTypeRequirements, extension.ConformanceRequirements) = ReadRequirements();
                    extension.TypeParameterAliases = [..Owned<AliasSymbol>(extension)];
                    break;
            }
        }

        ValuePredicate.Node ReadNode() =>
            _input.ReadByte() switch {
                0 => new ValuePredicate.Number(_input.ReadInt64()),
                1 => new ValuePredicate.Truth(_input.ReadBoolean()),
                2 => new ValuePredicate.Parameter(_input.ReadString()),
                3 => new ValuePredicate.Unary(_input.ReadString(), ReadNode()),
                4 => new ValuePredicate.Binary(_input.ReadString(), ReadNode(), ReadNode()),
                var other => throw new InvalidDataException($"no predicate part of kind {other}"),
            };

        int Count() {
            var count = _input.ReadInt32();
            return count >= 0 && count <= bytes.Length ? count : throw new InvalidDataException("a list's length is wrong");
        }

        (ImmutableArray<SameTypeRequirement>, ImmutableArray<ConformanceRequirement>) ReadRequirements() {
            var same = Count();
            var sameTypes = ImmutableArray.CreateBuilder<SameTypeRequirement>(same);
            for (var i = 0; i < same; i++) {
                sameTypes.Add(new(ReadType() ?? ErrorTypeSymbol.Instance, ReadType() ?? ErrorTypeSymbol.Instance));
            }

            var conformance = Count();
            var conformances = ImmutableArray.CreateBuilder<ConformanceRequirement>(conformance);
            for (var i = 0; i < conformance; i++) {
                conformances.Add(new(ReadType() ?? ErrorTypeSymbol.Instance, ReadType() ?? ErrorTypeSymbol.Instance));
            }

            return (sameTypes.MoveToImmutable(), conformances.MoveToImmutable());
        }

        ImmutableArray<TypeSymbol> ReadTypes() {
            var count = Count();
            var types = ImmutableArray.CreateBuilder<TypeSymbol>(count);
            for (var i = 0; i < count; i++) {
                types.Add(ReadType() ?? throw new InvalidDataException("a list of types holds none"));
            }

            return types.MoveToImmutable();
        }

        int _depth;

        TypeSymbol? ReadType() {
            if (++_depth > 512) {
                throw new InvalidDataException("a type nested past any a program writes");
            }

            try {
                return ReadTypeAt();
            } finally {
                _depth--;
            }
        }

        TypeSymbol? ReadTypeAt() {
            var tag = (Tag)_input.ReadByte();
            switch (tag) {
                case Tag.None:
                    return null;
                case Tag.Named: {
                    var definition = At(_input.ReadInt32()) as NamedTypeSymbol ?? throw new InvalidDataException("a named type that is not a type");
                    return definition.Construct(ReadTypes());
                }
                case Tag.ExternalNamed: {
                    var definition = ReadExternal();
                    return definition.Construct(ReadTypes());
                }
                case Tag.TypeParameter:
                    return At(_input.ReadInt32()) as TypeParameterSymbol ?? throw new InvalidDataException("a type parameter that is not one");
                case Tag.ExternalTypeParameter: {
                    var owner = ReadExternal();
                    var name = _input.ReadString();
                    return owner.TypeParameters.FirstOrDefault(p => p.Name == name)
                           ?? owner.AssociatedTypes.FirstOrDefault(p => p.Name == name)
                           ?? (owner.SelfTypeParameter is { } self && self.Name == name ? self : null)
                           ?? throw new InvalidDataException($"'{owner.Name}' has no type parameter '{name}'");
                }
                case Tag.Array: {
                    var element = ReadType() ?? throw new InvalidDataException("an array of nothing");
                    var kind = (ArrayKind)_input.ReadByte();
                    var capacity = _input.ReadInt32();
                    var parameter = ReadType() as TypeParameterSymbol;
                    return new ArrayTypeSymbol(element, kind, capacity < 0 ? null : capacity, parameter);
                }
                case Tag.Optional:
                    return new OptionalTypeSymbol(ReadType() ?? throw new InvalidDataException("an optional of nothing"), _input.ReadBoolean());
                case Tag.Reference:
                    return new ReferenceTypeSymbol(ReadType() ?? throw new InvalidDataException("a reference to nothing"), ReadString(_input), ReadString(_input));
                case Tag.Tuple: {
                    var elements = ReadTypes();
                    var names = ImmutableArray.CreateBuilder<string?>(elements.Length);
                    for (var i = 0; i < elements.Length; i++) {
                        names.Add(ReadString(_input));
                    }

                    return new TupleTypeSymbol(elements, names.MoveToImmutable());
                }
                case Tag.Function: {
                    var parameters = ReadTypes();
                    var result = ReadType() ?? throw new InvalidDataException("a function type with no result");
                    var isAsync = _input.ReadBoolean();
                    var thrown = ReadType();
                    var rethrows = _input.ReadBoolean();
                    var inOut = Count();
                    var inOuts = ImmutableArray.CreateBuilder<bool>(inOut);
                    for (var i = 0; i < inOut; i++) {
                        inOuts.Add(_input.ReadBoolean());
                    }

                    return new FunctionTypeSymbol(parameters, result, isAsync, thrown) { IsRethrows = rethrows, InOutParameters = inOuts.MoveToImmutable() };
                }
                case Tag.StructuralUnion:
                    return new StructuralUnionTypeSymbol(ReadTypes());
                case Tag.Existential:
                    return new ExistentialTypeSymbol(ReadType() ?? throw new InvalidDataException("an existential of nothing"));
                case Tag.Opaque: {
                    var constraint = ReadType() ?? throw new InvalidDataException("an opaque type of nothing");
                    var origin = AtOrNull(_input.ReadInt32());
                    return new OpaqueTypeSymbol(constraint, origin, null, ReadTypes());
                }
                case Tag.Metatype:
                    return new MetatypeTypeSymbol(ReadType() ?? throw new InvalidDataException("a metatype of nothing"));
                case Tag.Error:
                    return new ErrorTypeSymbol(_input.ReadString());
                case Tag.Value:
                    return new ValueTypeArgumentSymbol(_input.ReadInt64());
                case Tag.Composition:
                    return new CompositionTypeSymbol(ReadTypes());
                case Tag.DependentMember: {
                    var baseType = ReadType() ?? throw new InvalidDataException("a member type of nothing");
                    var associated = ReadType() as TypeParameterSymbol ?? throw new InvalidDataException("a member type that names no associated type");
                    return new DependentMemberTypeSymbol(baseType, associated);
                }
                case Tag.RecursiveUnion: {
                    var alias = At(_input.ReadInt32()) as AliasSymbol ?? throw new InvalidDataException("a recursive union of no alias");
                    return new RecursiveUnionTypeSymbol(alias);
                }
                default:
                    throw new InvalidDataException($"no type of kind {(int)tag}");
            }
        }

        NamedTypeSymbol ReadExternal() {
            var moduleName = _input.ReadString();
            var path = _input.ReadString();
            var module = dependencies.FirstOrDefault(m => m.Name == moduleName)
                         ?? throw new InvalidDataException($"the module '{moduleName}' this unit was compiled against is not given (for '{path}')");
            Symbol? at = module;
            foreach (var part in path.Split('.')) {
                at = (at switch {
                    ModuleSymbol m => m.GetMembers(part),
                    NamedTypeSymbol t => t.GetMembers(part),
                    _ => [],
                }).OfType<NamedTypeSymbol>().FirstOrDefault();
            }

            return at as NamedTypeSymbol ?? throw new InvalidDataException($"'{moduleName}' declares no type '{path}'");
        }

        static bool Has(int flags, int bit) => (flags & (1 << bit)) != 0;
    }

    static string? ReadString(BinaryReader input) => input.ReadBoolean() ? input.ReadString() : null;
}
