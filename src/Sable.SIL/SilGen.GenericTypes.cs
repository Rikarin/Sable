using Sable.Symbols;

namespace Sable.SIL;

/// <summary>
///     A generic type without its type arguments, as a value (#81, #297):
///     <c>Stack.self</c>, Sable.Core's <c>GenericType</c>: its name, how many
///     type arguments it takes, and a function that makes its metadata from
///     theirs, as code compiled once makes <c>Stack&lt;T&gt;</c>'s, once each
///     meets its constraints.
/// </summary>
public sealed partial class SilGen {
    /// <summary>
    ///     The function that makes an instance's metadata from an array of its
    ///     type arguments'; null when the type is not one laid out at run
    ///     time, or a constraint of it is none the metadata can be asked about:
    ///     the function then gives none.
    /// </summary>
    internal (string Name, FunctionType Type)? GenericTypeMaker(NamedTypeSymbol definition) {
        if (Lower(new OptionalTypeSymbol(Core.Type)) is not EnumType optional) {
            return null;
        }

        var name = $"$generic$type${QualifiedName(definition)}";
        var type = new FunctionType([new ArrayType(SilType.Metadata)], optional);
        if (_module.Find(name) is not null) {
            return (name, type);
        }

        var made = InstanceMetadata(definition);
        var constraints = definition.TypeParameters.SelectMany((p, i) => p.Constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c)).Select(c => (Index: i, Constraint: c)))
            .Where(static c => c.Constraint is not NamedTypeSymbol { Name: "Sendable", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }).ToList();
        var asked = new List<(int Index, SilType Type)>();
        foreach (var (index, constraint) in constraints) {
            var wanted = constraint is NamedTypeSymbol { TypeKind: TypeKind.Protocol, TypeParameters.IsEmpty: true } protocol ? new ExistentialTypeSymbol(protocol) : constraint;
            if (wanted is NamedTypeSymbol { TypeKind: TypeKind.Protocol } || Mentions(wanted, definition.TypeParameters) || Lower(wanted) is not { } lowered) {
                made = null;
            } else {
                asked.Add((index, lowered));
            }
        }

        var function = new SilFunction(name, type);
        function.ParameterNames.Add("arguments");
        var entry = function.AddBlock();
        var given = entry.AddArgument(type.Parameters[0]);
        if (made is not var (maker, makerType)) {
            entry.Append(new ReturnInst(entry.Append(new EnumInst(optional, optional.CaseIndex("None"), null))));
            _module.Add(function);
            return (name, type);
        }

        var arguments = new List<SilValue>();
        for (var i = 0; i < definition.TypeParameters.Length; i++) {
            arguments.Add(entry.Append(new BuiltinInst("array_get", SilType.Metadata, given, entry.Append(new IntegerLiteralInst(SilType.Int, i)))));
        }

        // Where a constraint is not met.
        SilBlock? refused = null;
        if (asked.Count > 0) {
            refused = function.AddBlock();
            refused.Append(new ReturnInst(refused.Append(new EnumInst(optional, optional.CaseIndex("None"), null))));
        }

        var at = entry;
        foreach (var (index, wanted) in asked) {
            var met = at.Append(new BuiltinInst("metadata_is_subtype", SilType.Bool, arguments[index], at.Append(new MetadataInst(wanted))));
            var next = function.AddBlock();
            at.Append(new CondBranchInst(met, next, [], refused!, []));
            at = next;
        }

        var metadata = at.Append(new ApplyInst(at.Append(new FunctionRefInst(maker, makerType)), SilType.Metadata, [..arguments]));
        at.Append(new ReturnInst(at.Append(new EnumInst(optional, optional.CaseIndex("Some"), metadata))));
        _module.Add(function);
        return (name, type);
    }

    /// <summary>A generic type's metadata from its type arguments', compiled once; null when it is not laid out at run time.</summary>
    (string Name, FunctionType Type)? InstanceMetadata(NamedTypeSymbol definition) {
        if (definition.TypeKind is not (TypeKind.Struct or TypeKind.Class or TypeKind.Union) || !IsPlainGeneric(definition)
            || definition.TypeParameters.Any(static p => p.IsValue || p.AllowsNoncopyable)) {
            return null;
        }

        Opening();
        SilType? lowered;
        using (Opaque(new GenericShape(definition.TypeParameters, [])))
        using (_diagnostics.Suppress()) {
            lowered = Lower(definition.Construct([..definition.TypeParameters.Cast<TypeSymbol>()]));
        }

        if (lowered is null) {
            return null;
        }

        var name = $"$metadata$of${QualifiedName(definition)}";
        var type = new FunctionType([..definition.TypeParameters.Select(static _ => SilType.Metadata)], SilType.Metadata);
        if (_module.Find(name) is null) {
            var function = new SilFunction(name, type);
            function.ParameterNames.AddRange(definition.TypeParameters.Select(static p => $"$metadata.{p.Name}"));
            var block = function.AddBlock();
            foreach (var _ in definition.TypeParameters) {
                block.AddArgument(SilType.Metadata);
            }

            block.Append(new ReturnInst(block.Append(new MetadataInst(lowered))));
            _module.Add(function);
        }

        return (name, type);
    }

    sealed partial class FunctionLowering {
        /// <summary><c>Stack.self</c>: its name, its type parameters' count, and what makes a type of it.</summary>
        SilValue? GenericTypeValue(BoundGenericType generic) {
            var definition = generic.Definition;
            if (LowerType(generic.Type) is not StructType layout || generator.GenericTypeMaker(definition) is not var (name, type)
                || layout.Fields[2].Type is not ClassType holder || holder.Fields is not [{ Type: ThickFunctionType thick }]) {
                return Missing(generic, $"'{definition.Name}.self'");
            }

            var thunk = generator.Thunk(name, type);
            var make = Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk, thick.Implementation)), null, thick));
            var written = Emit(new StringLiteralInst((definition.TypeKind == TypeKind.Protocol ? "any " : "") + QualifiedName(definition)));
            var maker = Emit(new AllocRefInst(holder));
            Emit(new StoreInst(make, Emit(new RefElementAddrInst(maker, 0)), StoreKind.Init));
            return Emit(new StructInst(layout, [written, Emit(new IntegerLiteralInst(SilType.Int, definition.TypeParameters.Length)), maker]));
        }
    }
}
