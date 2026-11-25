using System.Collections.Immutable;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Witness tables for instances of a generic type made at run time
///     (#297, #299): <c>Box&lt;T&gt;.self</c> for a T known only by its
///     metadata is laid out by <c>sable_aggregate_type</c> or
///     <c>sable_class_type</c>, whose conformance records come from its
///     declaration's. Each such conformance has a <b>template</b> table, one
///     for every instance, made here for the instance with its type arguments
///     opaque and registered by the generic type's name. Its entries are thunks that read the type arguments from
///     Self's metadata (<c>metadata_argument</c>), ask the registry for the
///     tables the conformance requires of them
///     (<c>witness_table_required</c>), and call the requirement's witness
///     compiled once for the generic type with those as hidden parameters.
/// </summary>
/// <remarks>
///     Only for the protocols the registry is asked about: those a call opens
///     a type as, or a cast is to. A table is made whole or not at all, so
///     that a requirement nothing meets leaves the type with no table (the
///     lookup traps, naming both, or the cast gives none) and never an entry
///     that is called and empty. A protocol's associated types differ per
///     instance: the template has a function for each that gives its metadata
///     from Self's, and the runtime makes an instance a table of its own with
///     them asked. In a program built in units, the unit that opens a type as
///     a protocol, or casts to it, makes the templates of every generic type
///     of the program that conforms: the instance may be another unit's.
/// </remarks>
public sealed partial class SilGen {
    /// <summary>The instances of generic types with an opaque type argument lowered, whose metadata is made at run time.</summary>
    readonly List<(NamedTypeSymbol Symbol, NominalType Type)> _runtimeInstances = [];

    /// <summary>The template tables asked for, made or not.</summary>
    readonly HashSet<(string Instance, string Protocol)> _templated = [];

    /// <summary>Makes the template tables of the instances made at run time, until lowering them reaches no more.</summary>
    void TemplateTables() {
        for (var changed = true; changed;) {
            changed = _module.Unit is not null && LowerProgramInstances();
            for (var i = 0; i < _runtimeInstances.Count; i++) {
                var (symbol, type) = _runtimeInstances[i];
                var wanted = ProtocolClosure(AskedProtocols()).Select(QualifiedName).ToHashSet(StringComparer.Ordinal);
                var definition = symbol.OriginalDefinition;
                var protocols = ProtocolSymbols(definition);
                var conformances = type.ConditionalConformances
                    .Concat(type.Conformances.Distinct(StringComparer.Ordinal).Where(p => !type.ConditionalConformances.Exists(c => c.Protocol == p))
                        .Select(static p => (Protocol: p, Requirements: ImmutableArray<(int, string)>.Empty)))
                    .Where(c => wanted.Contains(c.Protocol))
                    .ToList();
                // A protocol refined is tabled before the ones that refine it, whose tables point at its.
                foreach (var (name, requirements) in conformances.OrderBy(c => protocols.TryGetValue(c.Protocol, out var p) ? ProtocolClosure([p]).Count : 0)) {
                    if (_templated.Add((QualifiedName(definition), name)) && protocols.TryGetValue(name, out var protocol)
                        && requirements.All(r => r.Item1 < definition.TypeParameters.Length && protocols.ContainsKey(r.Item2))) {
                        changed = true;
                        TemplateTable(definition, type, protocol, [..requirements.Select(r => (definition.TypeParameters[r.Item1], protocols[r.Item2]))]);
                    }
                }
            }

            // A box the program names the class of — 'as? Box<Meters>' asks for it — is what a value made at run time is boxed in too.
            // In a unit, an instance's box is one another unit may open a call to make.
            if (_erasesAny || _openedProtocols.Any(static o => o.Boxes) || _unit is { CompilesGenerics: false } || _module.Unit is not null && CompilesOnce) {
                foreach (var box in _module.Types.OfType<ClassType>().Where(static t => t.Superclass?.Name == "any$box").ToList()) {
                    if (_module.Unit is null || _unit is { CompilesGenerics: false } || box.TypeArguments[0] is NominalType { TypeArguments.IsDefaultOrEmpty: false }) {
                        Boxer(box.TypeArguments[0]);
                    }
                }
            }

            // An instance the program names too has metadata of its own, which one
            // made at run time is the same type as: its tables, and its boxer, are made as a conforming type's are.
            var templates = _runtimeInstances.Select(static r => r.Symbol.OriginalDefinition).ToHashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
            var asked = ProtocolClosure(AskedProtocols());
            var named = new List<(NamedTypeSymbol Symbol, NominalType Type)>();
            foreach (var (key, lowered) in _types) {
                if (key is NamedTypeSymbol { IsConstructed: true } n && templates.Contains(n.OriginalDefinition)
                    && lowered is NominalType { TypeArguments: { IsDefaultOrEmpty: false } arguments } instance && !arguments.Any(SilType.MentionsOpaque)) {
                    named.Add((n, instance));
                }
            }

            foreach (var (symbol, value) in named) {
                foreach (var protocol in asked.Where(p => p.TypeParameters.IsEmpty && symbol.Satisfies(p))) {
                    if (symbol.OriginalDefinition.DeclaringSyntax.FirstOrDefault() is { } at && _templated.Add((value.ToString(), QualifiedName(protocol)))) {
                        changed = true;
                        GenericTable(symbol, protocol, at, describe: _openedProtocols.Any(static o => o.Describes));
                        if (_openedProtocols.Any(static o => o.Boxes)) {
                            Boxer(value);
                        }
                    }
                }
            }

            if (changed) {
                LowerReached();
            }
        }
    }

    /// <summary>The protocols the registry may be asked about: those a call opens a type as, those cast to, and what a template's associated types conform to.</summary>
    IEnumerable<NamedTypeSymbol> AskedProtocols() => [.._openedProtocols.Select(static o => o.Item1), .._castTargets, .._templateAssociated];

    readonly HashSet<NamedTypeSymbol> _templateAssociated = new(ReferenceEqualityComparer.Instance);

    readonly HashSet<NamedTypeSymbol> _programInstances = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     In a unit, the generic types of the program that conform to a
    ///     protocol the registry is asked about here, each lowered as its
    ///     instance made at run time, which another unit may make. True when
    ///     it lowered one.
    /// </summary>
    bool LowerProgramInstances() {
        var asked = ProtocolClosure(AskedProtocols()).Select(QualifiedName).ToHashSet(StringComparer.Ordinal);
        if (asked.Count == 0) {
            return false;
        }

        // A specialised unit compiles the witnesses once from here, as one that opens a type does.
        var lowered = false;
        void Visit(IEnumerable<Symbol> members) {
            foreach (var type in members.OfType<NamedTypeSymbol>()) {
                if (type.TypeParameters.IsEmpty) {
                    Visit(type.Members);
                    continue;
                }

                // Optional's declaration carries members alone: 'T?' is laid out as no union of its is.
                if (type is { ExtendedOptional: null, ExtendedArray: null, TypeKind: TypeKind.Struct or TypeKind.Class or TypeKind.Union } && IsPlainGeneric(type) && !type.TypeParameters.Any(static p => p.IsValue || p.AllowsNoncopyable)
                    && DeclaredProtocols(type, conditional: true).Any(asked.Contains) && _programInstances.Add(type)) {
                    Opening();
                    using (Opaque(new GenericShape(type.TypeParameters, [])))
                    using (_diagnostics.Suppress()) {
                        lowered |= Lower(type.Construct([..type.TypeParameters.Cast<TypeSymbol>()])) is not null;
                    }
                }
            }
        }

        foreach (var module in _compilation.Modules) {
            Visit(module.Members);
        }

        return lowered;
    }

    /// <summary>The protocols a generic type's declaration and extensions name, what they refine, and what is required of its arguments, by qualified name.</summary>
    static Dictionary<string, NamedTypeSymbol> ProtocolSymbols(NamedTypeSymbol definition) {
        var found = new Dictionary<string, NamedTypeSymbol>(StringComparer.Ordinal);
        var pending = new Queue<TypeSymbol>([
            ..definition.BaseTypes,
            ..definition.TypeParameters.SelectMany(static p => p.Constraints),
            ..definition.Extensions.SelectMany(static e => e.Conformances),
            ..definition.Extensions.SelectMany(static e => e.ConformanceRequirements).Select(static r => r.Constraint),
        ]);
        while (pending.Count > 0) {
            foreach (var part in CompositionTypeSymbol.PartsOf(pending.Dequeue())) {
                if (part is NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol && found.TryAdd(QualifiedName(protocol), protocol.OriginalDefinition)) {
                    foreach (var refined in protocol.BaseTypes) {
                        pending.Enqueue(refined);
                    }
                }
            }
        }

        return found;
    }

    /// <summary>The template table of a generic type's conformance to a protocol, for its instance made at run time; none when an entry cannot be made.</summary>
    void TemplateTable(NamedTypeSymbol definition, NominalType instance, NamedTypeSymbol protocol, ImmutableArray<(TypeParameterSymbol, NamedTypeSymbol)> required) {
        if (!IsTabledProtocol(protocol) || !protocol.TypeParameters.IsEmpty || definition.ExtendedOptional is not null || definition.ExtendedArray is not null
            || Bases(protocol).Any(b => _module.FindWitnessTable(instance, QualifiedName(b)) is null)
            || (definition.DeclaringSyntax.FirstOrDefault() ?? protocol.DeclaringSyntax.FirstOrDefault()) is not { } at) {
            return;
        }

        // What the witnesses compiled once are given: each argument's metadata, and the tables its constraints and the conformance require.
        var requirements = new List<(TypeParameterSymbol, NamedTypeSymbol)>();
        foreach (var parameter in definition.TypeParameters) {
            foreach (var constraint in parameter.Constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c))) {
                if (constraint is not NamedTypeSymbol each || !IsTabledProtocol(each)) {
                    return;
                }

                requirements.Add((parameter, each.OriginalDefinition));
            }
        }

        foreach (var (parameter, each) in required) {
            if (!requirements.Exists(r => ReferenceEquals(r.Item1, parameter) && ReferenceEquals(r.Item2, each))) {
                requirements.Add((parameter, each));
            }
        }

        // An argument's tables are the registry's too: each type of the program that may be one has its own.
        foreach (var (_, each) in requirements) {
            OpenedProtocol(each, false, false, at);
        }

        // Why a conformance has no template, for whoever is widening what can have one.
        void Refused(string what) {
            if (Environment.GetEnvironmentVariable("SABLE_GENERICS_TRACE") is { Length: > 0 }) {
                Console.Error.WriteLine($"no template {QualifiedName(definition)}: {QualifiedName(protocol)}: {what}");
            }
        }

        var shape = new GenericShape(definition.TypeParameters, [..requirements]);
        var table = new SilWitnessTable(instance, QualifiedName(protocol)) { IsTemplate = true };
        table.Bases.AddRange(Bases(protocol).Select(QualifiedName));
        for (var i = 0; i < protocol.OriginalDefinition.AssociatedTypes.Length; i++) {
            if (!TemplateAssociated(definition, table, protocol, i, shape, at)) {
                Refused(protocol.OriginalDefinition.AssociatedTypes[i].Name);
                return;
            }
        }

        foreach (var (key, member) in GenericRequirements(protocol)) {
            if (member is FunctionSymbol { TypeParameters.IsEmpty: false } || GenericEntryType(protocol, member) is not { } type) {
                Refused(key);
                return;
            }

            string? witness;
            using (_diagnostics.Suppress()) {
                witness = TemplateWitness(definition, protocol, table.Entries.Count, member, key, type, shape, at);
            }

            if (witness is null) {
                Refused(key);
                return;
            }

            table.Entries.Add((key, witness, type));
        }

        _module.AddWitnessTable(table);
    }

    /// <summary>
    ///     A template's associated type: what the conformance binds it to for
    ///     any type arguments, and the function that gives its metadata from
    ///     Self's — which reads the type arguments there, as an entry does.
    ///     False when its metadata, or what it conforms to, cannot be found so.
    /// </summary>
    bool TemplateAssociated(NamedTypeSymbol definition, SilWitnessTable table, NamedTypeSymbol protocol, int index, GenericShape shape, SyntaxNode at) {
        var associated = protocol.OriginalDefinition.AssociatedTypes[index];
        var concrete = definition.Construct([..definition.TypeParameters.Cast<TypeSymbol>()]);
        using (Opaque(shape))
        using (AssociatedContext(definition)) {
            var read = _compilation.AssociatedTypeOf(concrete, associated) is { } declared ? TypeSubstitution.For(concrete).Apply(declared) : null;
            if (read is null) {
                return false;
            }

            var bound = read.Equals(Core.Never) ? null : read;
            if ((bound is null ? SilType.Unit : Lower(bound)) is not { } lowered) {
                return false;
            }

            var stem = $"$template$associated${Sanitise(QualifiedName(definition))}${Sanitise(QualifiedName(protocol))}${index}";
            var hidden = shape.Parameters.Select(static _ => SilType.Metadata).Concat(shape.Requirements.Select(static _ => SilType.WitnessTable));
            var body = new SilFunction(stem + "$body", new FunctionType([..hidden], SilType.Metadata));
            body.ParameterNames.AddRange(shape.Parameters.Select(static p => $"$metadata.{ParameterName(p)}"));
            body.ParameterNames.AddRange(shape.Requirements.Select(static r => $"$witnesses.{ParameterName(r.Parameter)}.{r.Protocol.Name}"));
            var block = body.AddBlock();
            var given = body.Type.Parameters.Select(p => (SilValue)block.AddArgument(p)).ToList();
            SilValue? Metadata(TypeSymbol? type) =>
                type switch {
                    TypeParameterSymbol parameter when shape.Parameters.IndexOf(parameter) is var i and >= 0 => given[i],
                    not null when IsAssociatedOf(type, shape.Parameters) => block.Append(new BuiltinInst($"opaque_metadata:{OpaqueName(Canonical(type))}", SilType.Metadata)),
                    ArrayTypeSymbol array when Mentions(array, shape.Parameters) => IsArrayOf(array, shape.Parameters) && Metadata(array.ElementType) is { } element
                        ? block.Append(new BuiltinInst(array.ArrayKind == ArrayKind.Slice ? "metadata_slice" : "metadata_array", SilType.Metadata, element))
                        : null,
                    not null when Mentions(type, shape.Parameters) && !IsHeld(type, shape.Parameters) => null,
                    _ => block.Append(new MetadataInst(lowered)),
                };
            if (Metadata(bound) is not { } metadata) {
                return false;
            }

            // What the associated type must conform to, the registry is asked for from its metadata.
            foreach (var constraint in associated.Constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c)).OfType<NamedTypeSymbol>()) {
                if (constraint.TypeKind != TypeKind.Protocol || bound is null) {
                    continue;
                }

                if (!IsTabledProtocol(constraint)) {
                    return false;
                }

                _templateAssociated.Add(constraint.OriginalDefinition);
                if (!Mentions(bound, shape.Parameters)) {
                    GenericTable(bound, constraint.OriginalDefinition, at);
                } else if (bound is TypeParameterSymbol or DependentMemberTypeSymbol) {
                    OpenedProtocol(constraint.OriginalDefinition, false, false, at);
                } else if (bound is not NamedTypeSymbol) {
                    // An array's, an optional's or a tuple's conformance has no template.
                    return false;
                }

                table.AssociatedConformances.Add((index, QualifiedName(constraint)));
            }

            block.Append(new ReturnInst(metadata));
            AssociatedRecipes(body, definition, shape);

            var accessor = new SilFunction(stem, new FunctionType([SilType.Metadata], SilType.Metadata));
            accessor.ParameterNames.Add("$metadata.Self");
            var entry = accessor.AddBlock();
            var arguments = TemplateArguments(entry, entry.AddArgument(SilType.Metadata), shape);
            entry.Append(new ReturnInst(entry.Append(new ApplyInst(entry.Append(new FunctionRefInst(body.Name, body.Type)), SilType.Metadata, [..arguments]))));
            if (_module.Find(stem) is null) {
                _module.Add(body);
                _module.Add(accessor);
            }

            table.AssociatedTypes.Add((associated.Name, lowered));
            table.Accessors.Add(stem);
            return true;
        }
    }

    /// <summary>What a template's thunk gives the code compiled once for the generic type: its arguments' metadata, read from Self's, and their tables, the registry's.</summary>
    static List<SilValue> TemplateArguments(SilBlock entry, SilValue self, GenericShape shape) {
        var arguments = new List<SilValue>();
        var metadata = new Dictionary<TypeParameterSymbol, SilValue>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < shape.Parameters.Length; i++) {
            var position = entry.Append(new IntegerLiteralInst(SilType.Int, i));
            metadata[shape.Parameters[i]] = entry.Append(new BuiltinInst("metadata_argument", SilType.Metadata, self, position));
            arguments.Add(metadata[shape.Parameters[i]]);
        }

        foreach (var (parameter, each) in shape.Requirements) {
            arguments.Add(entry.Append(new BuiltinInst($"witness_table_required:{QualifiedName(each)}", SilType.WitnessTable, metadata[parameter])));
        }

        return arguments;
    }

    /// <summary>
    ///     A template table's entry: a thunk in the entry's convention that
    ///     reads the type arguments from Self's metadata and their tables from
    ///     the registry, and calls the witness compiled once for the generic
    ///     type with them. Null when the witness cannot be made.
    /// </summary>
    string? TemplateWitness(
        NamedTypeSymbol definition, NamedTypeSymbol protocol, int index, Symbol member, string key, FunctionType type, GenericShape shape, SyntaxNode at
    ) {
        var stem = $"$template$witness${Sanitise(QualifiedName(definition))}${Sanitise(QualifiedName(protocol))}${index}${(member.Name.All(char.IsLetterOrDigit) ? member.Name : "op")}";
        if (_module.Find(stem) is not null) {
            return stem;
        }

        // The witness compiled once: self and the arguments, then the type
        // arguments' metadata and tables, then, as any witness's, where a
        // result and an error go, and Self's metadata.
        var own = member switch {
            FunctionSymbol function => (HasSelf(function) ? 1 : 0) + function.Parameters.Length,
            FieldSymbol field => field.IsStatic ? 0 : 1,
            _ => 0,
        };
        var hidden = shape.Parameters.Select(static _ => SilType.Metadata).Concat(shape.Requirements.Select(static _ => SilType.WitnessTable));
        var innerType = new FunctionType([..type.Parameters.Take(own), ..hidden, ..type.Parameters.Skip(own)], type.Result, type.Thrown, isAsync: type.IsAsync);
        string? inner;
        using (Opaque(shape)) {
            var concrete = definition.Construct([..definition.TypeParameters.Cast<TypeSymbol>()]);
            inner = Lower(concrete) is { } value ? GenericWitness(concrete, value, protocol, key, member, innerType, at, ($"{stem}$body", shape)) : null;
        }

        if (inner is null) {
            return null;
        }

        var thunk = new SilFunction(stem, type);
        thunk.ParameterNames.AddRange([..Enumerable.Range(0, type.Parameters.Length - 1).Select(static i => $"$argument{i}"), "$metadata.Self"]);
        var entry = thunk.AddBlock();
        var given = type.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();
        var arguments = new List<SilValue>([..given.Take(own), ..TemplateArguments(entry, given[^1], shape), ..given.Skip(own)]);
        var callee = entry.Append(new FunctionRefInst(inner, innerType));
        if (type.Thrown is null) {
            var result = entry.Append(new ApplyInst(callee, type.Result, [..arguments]));
            entry.Append(new ReturnInst(type.Result == SilType.Unit ? null : result));
        } else {
            var normal = thunk.AddBlock();
            var result = type.Result == SilType.Unit ? null : normal.AddArgument(type.Result);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(type.Thrown);
            entry.Append(new TryApplyInst(callee, type.Result, [..arguments], normal, failed));
            normal.Append(new ReturnInst(result));
            failed.Append(new ThrowInst(error));
        }

        _module.Add(thunk);
        return stem;
    }
}
