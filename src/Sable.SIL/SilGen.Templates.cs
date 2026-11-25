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
                changed |= Templates(symbol.OriginalDefinition, type, wanted);
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

    /// <summary>Makes the template tables of a generic type's conformances to the protocols named that it has not been asked for yet: true when there was one.</summary>
    bool Templates(NamedTypeSymbol definition, NominalType type, HashSet<string> wanted) {
        var made = false;
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
                made = true;
                TemplateTable(definition, type, protocol, [..requirements.Select(r => (definition.TypeParameters[r.Item1], protocols[r.Item2]))]);
            }
        }

        return made;
    }

    readonly Dictionary<(NamedTypeSymbol, NamedTypeSymbol), bool> _templatesAsked = [];

    /// <summary>
    ///     Whether a type made of type parameters, <c>Pair&lt;T&gt;</c>, has a
    ///     template table of a conformance, which code compiled once finds in
    ///     the registry by the metadata it makes: made now, when its generic
    ///     type's conformance can have one.
    /// </summary>
    internal bool HasTemplate(TypeSymbol type, NamedTypeSymbol protocol) {
        if (type is StructuralUnionTypeSymbol) {
            return HasUnionTemplate(protocol.OriginalDefinition);
        }

        if (type is ArrayTypeSymbol or OptionalTypeSymbol) {
            return HasStructuralTemplate(type, protocol.OriginalDefinition);
        }

        if (type is not NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Struct or TypeKind.Class or TypeKind.Union, OriginalDefinition: var definition }
            || definition is not { ExtendedOptional: null, ExtendedArray: null } || !IsPlainGeneric(definition) || definition.TypeParameters.Any(static p => p.IsValue || p.AllowsNoncopyable)) {
            return false;
        }

        protocol = protocol.OriginalDefinition;
        if (_templatesAsked.TryGetValue((definition, protocol), out var has)) {
            return has;
        }

        // Asked again while it is made, from a witness of its own: not.
        _templatesAsked[(definition, protocol)] = false;
        NominalType? instance;
        using (Opaque(new GenericShape(definition.TypeParameters, [])))
        using (_diagnostics.Suppress()) {
            instance = Lower(definition.Construct([..definition.TypeParameters.Cast<TypeSymbol>()])) as NominalType;
        }

        if (instance is null) {
            return false;
        }

        var closure = ProtocolClosure([protocol]).ToList();
        Templates(definition, instance, closure.Select(QualifiedName).ToHashSet(StringComparer.Ordinal));
        return _templatesAsked[(definition, protocol)] = closure.TrueForAll(p => _module.FindWitnessTable(instance, QualifiedName(p)) is { IsTemplate: true });
    }

    readonly Dictionary<(string Family, NamedTypeSymbol Protocol), bool> _structuralAsked = [];

    /// <summary>
    ///     Whether an array, a slice or an optional of type parameters,
    ///     <c>T[&gt;..]</c> given as a <c>C: Collection</c>, has a template
    ///     table of a conformance: one for every array, every slice and every
    ///     optional, found by the kind of the metadata and, where its
    ///     extension asks something of the element, by what that conforms to.
    /// </summary>
    bool HasStructuralTemplate(TypeSymbol type, NamedTypeSymbol protocol) {
        if (!CompilesOnce || !IsTabledProtocol(protocol) || !protocol.TypeParameters.IsEmpty) {
            return false;
        }

        // A struct has extensions for an optional, an array for its elements' kinds.
        var family = type switch {
            ArrayTypeSymbol { ArrayKind: ArrayKind.Slice } => "slice",
            ArrayTypeSymbol { ArrayKind: ArrayKind.Fixed or ArrayKind.Grow or ArrayKind.GrowShrink } => "array",
            OptionalTypeSymbol => "optional",
            _ => null,
        };
        if (family is null || type is ArrayTypeSymbol array && !protocol.ArrayConformers.Any(c => Symbols.Compilation.MatchArray(c, array) is not null)) {
            return false;
        }

        // Asked again while it is made, from a witness of its own: as it will be.
        if (_structuralAsked.TryGetValue((family, protocol), out var has)) {
            return has;
        }

        _structuralAsked[(family, protocol)] = true;
        return _structuralAsked[(family, protocol)] = MakeStructuralTemplate(type, family, protocol);
    }

    /// <summary>The conformer of the template every structural union of errors has.</summary>
    public static readonly OpaqueType UnionConformer = new("union");

    /// <summary>
    ///     Whether every structural union, <c>A | B</c> of types known at run
    ///     time, has a table of a conformance — to Error, as each of its cases
    ///     does: its entries ask which case a value holds, and call the
    ///     case's own (<c>union_case_metadata</c>, <c>union_case_payload</c>).
    /// </summary>
    internal bool HasUnionTemplate(NamedTypeSymbol protocol) {
        if (!CompilesOnce || !ProtocolClosure([Core.Error]).Any(p => ReferenceEquals(p, protocol)) || !protocol.AssociatedTypes.IsEmpty) {
            return false;
        }

        if (_structuralAsked.TryGetValue(("union", protocol), out var has)) {
            return has;
        }

        _structuralAsked[("union", protocol)] = true;
        return _structuralAsked[("union", protocol)] = MakeUnionTemplate(protocol);
    }

    bool MakeUnionTemplate(NamedTypeSymbol protocol) {
        if (!Bases(protocol).All(b => HasUnionTemplate(b.OriginalDefinition))) {
            return false;
        }

        var table = new SilWitnessTable(UnionConformer, QualifiedName(protocol)) { IsTemplate = true };
        table.Bases.AddRange(Bases(protocol).Select(QualifiedName));
        foreach (var (key, member) in GenericRequirements(protocol)) {
            if (GenericEntryType(protocol, member) is not { Thrown: null, IsAsync: false } type || type.Parameters[0] is not OpaqueType) {
                return false;
            }

            var stem = $"$template$witness$union${Sanitise(QualifiedName(protocol))}${table.Entries.Count}";
            if (_module.Find(stem) is null) {
                var thunk = new SilFunction(stem, type);
                thunk.ParameterNames.AddRange([..Enumerable.Range(0, type.Parameters.Length - 1).Select(static i => $"$argument{i}"), "$metadata.Self"]);
                var entry = thunk.AddBlock();
                var given = type.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();
                var held = entry.Append(new BuiltinInst("union_case_metadata", SilType.Metadata, given[0], given[^1]));
                var payload = entry.Append(new BuiltinInst("union_case_payload", given[0].Type, given[0], given[^1]));
                var asked = entry.Append(new BuiltinInst($"witness_table_required:{QualifiedName(protocol)}", SilType.WitnessTable, held));
                var code = entry.Append(new BuiltinInst($"witness_generic:{table.Entries.Count}", type, asked));
                var result = entry.Append(new ApplyInst(code, type.Result, [payload, ..given.Skip(1).SkipLast(1), held]));
                entry.Append(new ReturnInst(type.Result == SilType.Unit ? null : result));
                _module.Add(thunk);
            }

            table.Entries.Add((key, stem, type));
        }

        _module.AddWitnessTable(table);
        return true;
    }

    bool MakeStructuralTemplate(TypeSymbol type, string family, NamedTypeSymbol protocol) {
        if (!Bases(protocol).All(b => HasStructuralTemplate(type, b.OriginalDefinition))) {
            return false;
        }

        NamedTypeSymbol? definition;
        TypeSymbol concrete;
        var required = new List<(TypeParameterSymbol, NamedTypeSymbol)>();
        if (type is ArrayTypeSymbol array) {
            // The first that declares it of one type parameter's elements: 'extension<Element> Element[:]: Collection'.
            definition = protocol.ArrayConformers.FirstOrDefault(c => Symbols.Compilation.MatchArray(c, array) is not null && c is { ExtendedArray.ElementType: TypeParameterSymbol, TypeParameters.Length: 1 });
            if (definition is null) {
                return false;
            }

            concrete = new ArrayTypeSymbol(definition.TypeParameters[0], array.ArrayKind);
        } else {
            definition = Core.Optional;
            concrete = definition.ExtendedOptional!;
            var symbols = ProtocolSymbols(definition);
            var conditional = ConditionalProtocols(definition).Where(c => c.Protocol == QualifiedName(protocol)).Select(static c => ((string, ImmutableArray<(int, string)> Requirements)?)c).FirstOrDefault();
            if (conditional is null && !DeclaredProtocols(definition).Contains(QualifiedName(protocol))) {
                return false;
            }

            foreach (var (_, requirement) in conditional?.Requirements ?? []) {
                if (!symbols.TryGetValue(requirement, out var each)) {
                    return false;
                }

                required.Add((definition.TypeParameters[0], each));
            }
        }

        var element = new OpaqueType("Element");
        SilType? instance = family switch {
            "array" => new ArrayType(element),
            "slice" => new SliceType(element),
            _ => null,
        };
        if (instance is null) {
            using (Opaque(new GenericShape(definition.TypeParameters, []))) {
                instance = Lower(concrete);
            }
        }

        return instance is not null && TemplateTable(definition, instance, protocol, [..required], concrete, family);
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
    bool TemplateTable(
        NamedTypeSymbol definition, SilType instance, NamedTypeSymbol protocol, ImmutableArray<(TypeParameterSymbol, NamedTypeSymbol)> required, TypeSymbol? structural = null, string family = ""
    ) {
        if (!IsTabledProtocol(protocol) || !protocol.TypeParameters.IsEmpty || structural is null && (definition.ExtendedOptional is not null || definition.ExtendedArray is not null)
            || Bases(protocol).Any(b => _module.FindWitnessTable(instance, QualifiedName(b)) is null)
            || (definition.DeclaringSyntax.FirstOrDefault() ?? protocol.DeclaringSyntax.FirstOrDefault()) is not { } at) {
            return false;
        }

        // What the witnesses compiled once are given: each argument's metadata, and the tables its constraints and the conformance require.
        var requirements = new List<(TypeParameterSymbol, NamedTypeSymbol)>();
        foreach (var parameter in definition.TypeParameters) {
            foreach (var constraint in parameter.Constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c))) {
                if (constraint is not NamedTypeSymbol each || !IsTabledProtocol(each)) {
                    return false;
                }

                requirements.Add((parameter, each.OriginalDefinition));
            }
        }

        // One '@derive' gave it holds where each argument conforms.
        if (structural is null && definition.Members.OfType<FunctionSymbol>().Any(m => _compilation.GetDerivation(m) is { Protocol: var derived } && ReferenceEquals(derived.OriginalDefinition, protocol.OriginalDefinition))) {
            required = [..required, ..definition.TypeParameters.Select(p => (p, protocol.OriginalDefinition))];
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
        if (structural is not null) {
            table.Required.AddRange(shape.Requirements.Select(static r => QualifiedName(r.Protocol)).Distinct());
        }

        var concrete = structural ?? definition.Construct([..definition.TypeParameters.Cast<TypeSymbol>()]);
        for (var i = 0; i < protocol.OriginalDefinition.AssociatedTypes.Length; i++) {
            if (!TemplateAssociated(definition, concrete, family, table, protocol, i, shape, at)) {
                Refused(protocol.OriginalDefinition.AssociatedTypes[i].Name);
                return false;
            }
        }

        foreach (var (key, member) in GenericRequirements(protocol)) {
            if (member is FunctionSymbol { TypeParameters.IsEmpty: false } || GenericEntryType(protocol, member) is not { } type) {
                Refused(key);
                return false;
            }

            string? witness;
            using (_diagnostics.Suppress()) {
                witness = TemplateWitness(definition, concrete, family, protocol, table.Entries.Count, member, key, type, shape, at);
            }

            if (witness is null) {
                Refused(key);
                return false;
            }

            table.Entries.Add((key, witness, type));
        }

        _module.AddWitnessTable(table);
        return true;
    }

    /// <summary>
    ///     A template's associated type: what the conformance binds it to for
    ///     any type arguments, and the function that gives its metadata from
    ///     Self's — which reads the type arguments there, as an entry does.
    ///     False when its metadata, or what it conforms to, cannot be found so.
    /// </summary>
    bool TemplateAssociated(NamedTypeSymbol definition, TypeSymbol concrete, string family, SilWitnessTable table, NamedTypeSymbol protocol, int index, GenericShape shape, SyntaxNode at) {
        var associated = protocol.OriginalDefinition.AssociatedTypes[index];
        using (Opaque(shape))
        using (AssociatedContext(definition)) {
            var read = concrete is NamedTypeSymbol named ? _compilation.AssociatedTypeOf(named, associated) is { } declared ? TypeSubstitution.For(named).Apply(declared) : null
                : new TypeSubstitution(new Dictionary<TypeParameterSymbol, TypeSymbol> { [protocol.OriginalDefinition.SelfTypeParameter!] = concrete }).Apply(new DependentMemberTypeSymbol(protocol.OriginalDefinition.SelfTypeParameter!, associated));
            if (read is null or DependentMemberTypeSymbol { BaseType: ArrayTypeSymbol or OptionalTypeSymbol }) {
                return false;
            }

            var bound = read.Equals(Core.Never) ? null : read;
            if ((bound is null ? SilType.Unit : Lower(bound)) is not { } lowered) {
                return false;
            }

            var stem = $"$template$associated${Sanitise(QualifiedName(definition))}{(family.Length > 0 ? "$" + family : "")}${Sanitise(QualifiedName(protocol))}${index}";
            var hidden = shape.Parameters.Select(static _ => SilType.Metadata).Concat(shape.Requirements.Select(static _ => SilType.WitnessTable));
            var body = new SilFunction(stem + "$body", new FunctionType([..hidden], SilType.Metadata));
            body.ParameterNames.AddRange(shape.Parameters.Select(static p => $"$metadata.{ParameterName(p)}"));
            body.ParameterNames.AddRange(shape.Requirements.Select(static r => $"$witnesses.{ParameterName(r.Parameter)}.{r.Protocol.Name}"));
            var block = body.AddBlock();
            var given = body.Type.Parameters.Select(p => (SilValue)block.AddArgument(p)).ToList();
            SilValue? Metadata(TypeSymbol? type) =>
                type switch {
                    TypeParameterSymbol parameter when shape.Parameters.IndexOf(parameter) is var i and >= 0 => given[i],
                    StructuralUnionTypeSymbol when Canonical(type) is var union && IsOpaque(union) => block.Append(new BuiltinInst($"opaque_metadata:{OpaqueName(union)}", SilType.Metadata)),
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
                } else if (bound is not NamedTypeSymbol && !HasTemplate(bound, constraint.OriginalDefinition)) {
                    // A tuple's conformance has no template, nor an array's that cannot be made.
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
        NamedTypeSymbol definition, TypeSymbol concrete, string family, NamedTypeSymbol protocol, int index, Symbol member, string key, FunctionType type, GenericShape shape, SyntaxNode at
    ) {
        var stem = $"$template$witness${Sanitise(QualifiedName(definition))}{(family.Length > 0 ? "$" + family : "")}${Sanitise(QualifiedName(protocol))}${index}${(member.Name.All(char.IsLetterOrDigit) ? member.Name : "op")}";
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
