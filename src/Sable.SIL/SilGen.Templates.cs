using System.Collections.Immutable;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Witness tables for instances of a generic type made at run time
///     (#297, #299): <c>Box&lt;T&gt;.self</c> for a T known only by its
///     metadata is laid out by <c>sable_aggregate_type</c> or
///     <c>sable_class_type</c>, whose conformance records come from its
///     declaration's. Each such record points at a <b>template</b> table, one
///     for every instance, made here for the instance with its type arguments
///     opaque. Its entries are thunks that read the type arguments from
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
///     that is called and empty. Not made for a protocol with associated
///     types, which would differ per instance.
/// </remarks>
public sealed partial class SilGen {
    /// <summary>The instances of generic types with an opaque type argument lowered, whose metadata is made at run time.</summary>
    readonly List<(NamedTypeSymbol Symbol, NominalType Type)> _runtimeInstances = [];

    /// <summary>The template tables asked for, made or not.</summary>
    readonly HashSet<(string Instance, string Protocol)> _templated = [];

    /// <summary>Makes the template tables of the instances made at run time, until lowering them reaches no more.</summary>
    void TemplateTables() {
        for (var changed = true; changed;) {
            changed = false;
            for (var i = 0; i < _runtimeInstances.Count; i++) {
                var (symbol, type) = _runtimeInstances[i];
                var wanted = ProtocolClosure([.._openedProtocols.Select(static o => o.Item1), .._castTargets]).Select(QualifiedName).ToHashSet(StringComparer.Ordinal);
                var definition = symbol.OriginalDefinition;
                var protocols = ProtocolSymbols(definition);
                var conformances = type.ConditionalConformances
                    .Concat(type.Conformances.Distinct(StringComparer.Ordinal).Where(p => !type.ConditionalConformances.Exists(c => c.Protocol == p))
                        .Select(static p => (Protocol: p, Requirements: ImmutableArray<(int, string)>.Empty)))
                    .Where(c => wanted.Contains(c.Protocol))
                    .ToList();
                foreach (var (name, requirements) in conformances) {
                    if (_templated.Add((type.ToString(), name)) && protocols.TryGetValue(name, out var protocol)
                        && requirements.All(r => r.Item1 < definition.TypeParameters.Length && protocols.ContainsKey(r.Item2))) {
                        changed = true;
                        TemplateTable(definition, type, protocol, [..requirements.Select(r => (definition.TypeParameters[r.Item1], protocols[r.Item2]))]);
                    }
                }
            }

            // A box the program names the class of — 'as? Box<Meters>' asks for it — is what a value made at run time is boxed in too.
            if (_openedProtocols.Any(static o => o.Boxes)) {
                foreach (var box in _module.Types.OfType<ClassType>().Where(static t => t.Superclass?.Name == "any$box").ToList()) {
                    Boxer(box.TypeArguments[0]);
                }
            }

            // An instance the program names too has metadata of its own, which one
            // made at run time is the same type as: its tables, and its boxer, are made as a conforming type's are.
            var templates = _runtimeInstances.Select(static r => r.Symbol.OriginalDefinition).ToHashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
            var asked = ProtocolClosure([.._openedProtocols.Select(static o => o.Item1), .._castTargets]);
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
        if (!IsTabledProtocol(protocol) || !protocol.TypeParameters.IsEmpty || ProtocolClosure([protocol]).Exists(static p => !p.AssociatedTypes.IsEmpty)
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

        var shape = new GenericShape(definition.TypeParameters, [..requirements]);
        var table = new SilWitnessTable(instance, QualifiedName(protocol)) { IsTemplate = true };
        table.Bases.AddRange(Bases(protocol).Select(QualifiedName));
        foreach (var (key, member) in GenericRequirements(protocol)) {
            if (GenericEntryType(protocol, member) is not { } type) {
                return;
            }

            string? witness;
            using (_diagnostics.Suppress()) {
                witness = TemplateWitness(definition, protocol, table.Entries.Count, member, key, type, shape, at);
            }

            if (witness is null) {
                return;
            }

            table.Entries.Add((key, witness, type));
        }

        _module.AddWitnessTable(table);
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
        var arguments = new List<SilValue>(given.Take(own));
        var metadata = new Dictionary<TypeParameterSymbol, SilValue>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < shape.Parameters.Length; i++) {
            var position = entry.Append(new IntegerLiteralInst(SilType.Int, i));
            metadata[shape.Parameters[i]] = entry.Append(new BuiltinInst("metadata_argument", SilType.Metadata, given[^1], position));
            arguments.Add(metadata[shape.Parameters[i]]);
        }

        foreach (var (parameter, each) in shape.Requirements) {
            arguments.Add(entry.Append(new BuiltinInst($"witness_table_required:{QualifiedName(each)}", SilType.WitnessTable, metadata[parameter])));
        }

        arguments.AddRange(given.Skip(own));
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
