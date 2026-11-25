using System.Globalization;
using System.Text;

namespace Sable.SIL;

/// <summary>
///     Writes SIL as text, in the form <see cref="SilParser" /> reads back.
/// </summary>
/// <remarks>
///     Values are numbered <c>%0</c>, <c>%1</c>, … in order of definition
///     through the function, block arguments included, and blocks are
///     <c>bb0</c>, <c>bb1</c>, … in order. Printing then parsing then printing
///     gives the same text, which the tests hold.
/// </remarks>
public static class SilPrinter {
    public static string Print(SilModule module) => Print(module, keepBlockOrder: false);

    /// <summary>
    ///     The module as SIL text; with <paramref name="keepBlockOrder" />, a
    ///     function whose blocks are printed in another order than its own —
    ///     a block before one that dominates it — says its own after its type,
    ///     <c>blocks(2, 0, 1)</c>, and reads back with its blocks in that order:
    ///     what a library's interface keeps, so that the passes see its
    ///     functions as lowering made them (<see cref="LibraryInterface" />).
    /// </summary>
    public static string Print(SilModule module, bool keepBlockOrder) => Print(module, keepBlockOrder, null);

    /// <summary>
    ///     <see cref="Print(SilModule, bool)" />, with only the functions
    ///     <paramref name="include" /> picks: the module's types, globals and
    ///     tables, and those functions' bodies.
    /// </summary>
    public static string Print(SilModule module, bool keepBlockOrder, Func<SilFunction, bool>? include) {
        var builder = new StringBuilder();

        // Types first, each after the types it holds, so that the parser
        // never meets a name it has not seen.
        var printed = new HashSet<NominalType>();
        void Declare(NominalType type) {
            if (!printed.Add(type)) {
                return;
            }

            foreach (var nested in Contained(type)) {
                Declare(nested);
            }

            switch (type) {
                case StructType structType:
                    builder.Append("sil_struct ").Append(structType.Name).Append(structType.IsNoncopyable ? " @noncopy" : "").Append(structType.IsInline ? " @inline" : "")
                        .Append(structType.IsPacked ? " @packed" : "").Append(structType.Alignment > 0 ? $" @align({structType.Alignment})" : "").Append(" { ")
                        .Append(string.Join(", ", structType.Fields.Select(static f => $"{f.Name}: ${f.Type}")))
                        .Append(" }");
                    if (structType.Deinit is { } structDeinit) {
                        builder.Append(" deinit @").Append(structDeinit);
                    }

                    builder.Append(Described(structType)).Append('\n');
                    break;

                case EnumType enumType:
                    builder.Append("sil_enum ").Append(enumType.Name).Append(enumType.IsNoncopyable ? " @noncopy" : "").Append(" { ")
                        .Append(string.Join(", ", enumType.Cases.Select(static c => c.Payload is null ? c.Name : $"{c.Name}(${c.Payload})")))
                        .Append(" }").Append(Described(enumType)).Append('\n');
                    break;

                case ClassType classType:
                    builder.Append("sil_class ").Append(classType.Name);
                    if (classType.Superclass is { } superclass) {
                        builder.Append(" : ").Append(superclass.Name);
                    }

                    builder.Append(" { ")
                        .Append(string.Join(", ", classType.Fields.Select(static f => $"{f.Name}: ${f.Type}")))
                        .Append(" }");
                    if (classType.VTable.Count > 0) {
                        builder.Append(" vtable { ")
                            .Append(string.Join(", ", classType.VTable.Select(static m => $"{m.Method}: @{m.Function}")))
                            .Append(" }");
                    }

                    if (classType.Deinit is { } deinit) {
                        builder.Append(" deinit @").Append(deinit);
                    }

                    if (classType.HasGenericDeinit) {
                        builder.Append(" generic_deinit");
                    }

                    if (classType.EmptyFrom is { } emptyFrom) {
                        builder.Append(" empty_from ").Append(emptyFrom);
                    }

                    builder.Append(Described(classType)).Append('\n');
                    break;
            }
        }

        // From the roots in name order, so that the text depends only on
        // which types there are — a cycle through a class or an array would
        // otherwise print differently for each order they were made in.
        foreach (var type in module.Types.OrderBy(static t => t.Name, StringComparer.Ordinal)) {
            Declare(type);
        }

        foreach (var (name, type) in module.Globals) {
            builder.Append("sil_global @").Append(name).Append(" : $").Append(type).Append('\n');
        }

        foreach (var (name, (symbol, type)) in module.Foreign) {
            builder.Append("sil_extern @").Append(name).Append(" : $").Append(type).Append(" = ").Append(Quote(symbol));
            if (module.ForeignVariadic.TryGetValue(name, out var fixedCount)) {
                builder.Append(" variadic ").Append(fixedCount);
            }

            builder.Append('\n');
        }

        foreach (var (name, symbol) in module.Exports) {
            builder.Append("sil_export @").Append(name).Append(" = ").Append(Quote(symbol)).Append('\n');
        }

        foreach (var (_, (type, function)) in module.Describers.OrderBy(static d => d.Key, StringComparer.Ordinal)) {
            builder.Append("sil_describer $").Append(type).Append(" = @").Append(function).Append('\n');
        }

        foreach (var (_, (type, function)) in module.DebugDescribers.OrderBy(static d => d.Key, StringComparer.Ordinal)) {
            builder.Append("sil_debug_describer $").Append(type).Append(" = @").Append(function).Append('\n');
        }

        foreach (var (_, (type, function)) in module.ErrorErasers.OrderBy(static d => d.Key, StringComparer.Ordinal)) {
            builder.Append("sil_error_eraser $").Append(type).Append(" = @").Append(function).Append('\n');
        }

        foreach (var (_, (type, function)) in module.Boxers.OrderBy(static d => d.Key, StringComparer.Ordinal)) {
            builder.Append("sil_boxer $").Append(type).Append(" = @").Append(function).Append('\n');
        }

        foreach (var (_, (type, function)) in module.Casters.OrderBy(static d => d.Key, StringComparer.Ordinal)) {
            builder.Append("sil_caster $").Append(type).Append(" = @").Append(function).Append('\n');
        }

        foreach (var (type, function) in module.GenericDeinits.OrderBy(static d => d.Key, StringComparer.Ordinal)) {
            builder.Append("sil_generic_deinit ").Append(Quote(type)).Append(" = @").Append(function).Append('\n');
        }

        foreach (var table in module.WitnessTables) {
            builder.Append(table.IsTemplate ? "sil_template_table $" : "sil_witness_table $").Append(table.Conformer).Append(" : ").Append(Quote(table.Protocol)).Append(" {\n");
            foreach (var (requirement, function, type) in table.Entries) {
                builder.Append("  ").Append(Quote(requirement)).Append(": ");
                builder.Append(function is null || type is null ? "none" : $"@{function} : ${type}").Append('\n');
            }

            foreach (var (name, type) in table.AssociatedTypes) {
                builder.Append("  associated ").Append(Quote(name)).Append(": $").Append(type).Append('\n');
            }

            foreach (var required in table.Required) {
                builder.Append("  requires ").Append(Quote(required)).Append('\n');
            }

            foreach (var accessor in table.Accessors) {
                builder.Append("  accessor @").Append(accessor).Append('\n');
            }

            foreach (var (associated, protocol) in table.AssociatedConformances) {
                builder.Append("  conforms ").Append(associated).Append(": ").Append(Quote(protocol)).Append('\n');
            }

            foreach (var refined in table.Bases) {
                builder.Append("  base ").Append(Quote(refined)).Append('\n');
            }

            builder.Append("}\n");
        }

        if ((module.Types.Count > 0 || module.Globals.Count > 0 || module.Foreign.Count > 0 || module.Exports.Count > 0 || module.WitnessTables.Count > 0 || module.Describers.Count > 0 || module.DebugDescribers.Count > 0 || module.ErrorErasers.Count > 0 || module.Boxers.Count > 0 || module.Casters.Count > 0 || module.GenericDeinits.Count > 0)
            && module.Functions.Exists(f => include?.Invoke(f) != false)) {
            builder.Append('\n');
        }

        var first = true;
        foreach (var function in module.Functions) {
            if (include?.Invoke(function) == false) {
                continue;
            }

            if (!first) {
                builder.Append('\n');
            }

            first = false;
            Print(function, builder, keepBlockOrder);
        }

        return builder.ToString();
    }

    /// <summary>The named types a type mentions, directly or inside tuples and arrays.</summary>
    /// <remarks>
    ///     Only an order for reading: the parser knows every declared name
    ///     before it reads any, since a class or an array may name a type that
    ///     contains it.
    /// </remarks>
    static IEnumerable<NominalType> Contained(SilType type) {
        IEnumerable<SilType> parts = type switch {
            StructType structType => structType.Fields.Select(static f => f.Type),
            ClassType classType => classType.Fields.Select(static f => f.Type).Concat(classType.Superclass is { } s ? [s] : []),
            EnumType enumType => enumType.Cases.Where(static c => c.Payload is not null).Select(static c => c.Payload!),
            TupleType tuple => tuple.Elements,
            ArrayType array => [array.Element],
            SliceType slice => [slice.Element],
            WeakStorageType weak => [weak.Optional],
            UnownedStorageType unowned => [unowned.Referent],
            _ => [],
        };

        foreach (var part in parts) {
            if (part is NominalType nominal) {
                yield return nominal;
            } else {
                foreach (var inner in Contained(part)) {
                    yield return inner;
                }
            }
        }
    }

    public static string Print(SilFunction function) {
        var builder = new StringBuilder();
        Print(function, builder);
        return builder.ToString();
    }

    /// <summary>
    ///     The blocks in the order they are printed: as the function has them,
    ///     except that a block waits until every block that dominates it has
    ///     been printed. A value is then always printed before its uses, which
    ///     the parser needs for an instruction whose meaning depends on its
    ///     operand's type ('struct_extract' finds its field by the struct's).
    ///     A pass can break the order the function has: promoting a slot to a
    ///     value hands a block the value of one created after it, as the
    ///     block an 'if let' binds in is created after the one it leads to. Where
    ///     the function's order is already so, it is kept as it is.
    ///     SIL with a use its definition does not dominate (which the
    ///     verifier rejects, but the parser reads) can have a value printed
    ///     after a use in that order; it is then printed in the function's
    ///     order, the one it was read in.
    /// </summary>
    static List<SilBlock> InPrintOrder(SilFunction function) {
        var ordered = InDominanceOrder(function);
        return DefinesBeforeUses(ordered) ? ordered : [..function.Blocks];
    }

    /// <summary>Whether, read through these blocks in order, every value of theirs is defined before it is used.</summary>
    static bool DefinesBeforeUses(IReadOnlyList<SilBlock> blocks) {
        var local = new HashSet<SilValue>();
        foreach (var block in blocks) {
            local.UnionWith(block.Arguments);
            local.UnionWith(block.Instructions);
        }

        var defined = new HashSet<SilValue>();
        foreach (var block in blocks) {
            defined.UnionWith(block.Arguments);
            foreach (var instruction in block.Instructions) {
                if (instruction.Operands.Any(o => local.Contains(o) && !defined.Contains(o))) {
                    return false;
                }

                defined.Add(instruction);
            }
        }

        return true;
    }

    static List<SilBlock> InDominanceOrder(SilFunction function) {
        if (function.Blocks.Count == 0) {
            return [];
        }

        var dominators = SilVerifier.Dominators(function);
        var printed = new HashSet<SilBlock>();
        var ordered = new List<SilBlock>(function.Blocks.Count);
        var waiting = new List<SilBlock>(function.Blocks);
        while (waiting.Count > 0) {
            var next = waiting.FindIndex(block =>
                !dominators.TryGetValue(block, out var above) || above.All(d => d == block || printed.Contains(d)));

            // Dominance has no cycles, so some block is always ready; this is a guard.
            next = next < 0 ? 0 : next;
            ordered.Add(waiting[next]);
            printed.Add(waiting[next]);
            waiting.RemoveAt(next);
        }

        return ordered;
    }

    static void Print(SilFunction function, StringBuilder builder, bool keepBlockOrder = false) {
        var names = new Dictionary<SilValue, string>();
        var blocks = new Dictionary<SilBlock, string>();
        var ordered = InPrintOrder(function);

        for (var i = 0; i < ordered.Count; i++) {
            blocks[ordered[i]] = $"bb{i}";
        }

        foreach (var block in ordered) {
            foreach (var argument in block.Arguments) {
                names[argument] = $"%{names.Count}";
            }

            foreach (var instruction in block.Instructions) {
                if (instruction.HasResult) {
                    names[instruction] = $"%{names.Count}";
                }
            }
        }

        string Name(SilValue value) => names.TryGetValue(value, out var name) ? name : "%<undefined>";

        string Label(SilBlock block) => blocks.TryGetValue(block, out var name) ? name : "bb<undefined>";

        string Target(SilBlock block, IEnumerable<SilValue> arguments) {
            var list = arguments.Select(Name).ToList();
            var label = blocks.TryGetValue(block, out var name) ? name : "bb<undefined>";
            return list.Count == 0 ? label : $"{label}({string.Join(", ", list)})";
        }

        builder.Append("sil @").Append(function.Name).Append(" : $").Append(function.Type);
        if (function.ResultRoots is { } roots) {
            builder.Append(" roots(").Append(string.Join(", ", roots)).Append(')');
        }

        if (keepBlockOrder && !ordered.SequenceEqual(function.Blocks)) {
            builder.Append(" blocks(").Append(string.Join(", ", function.Blocks.Select(b => blocks.TryGetValue(b, out var label) ? label[2..] : "0"))).Append(')');
        }

        if (function.Inline != SilInline.Default) {
            builder.Append(" inline(").Append(function.Inline == SilInline.Always ? "always" : "never").Append(')');
        }

        if (function.Vectorised) {
            builder.Append(" vectorised");
        }

        foreach (var associated in function.AssociatedMetadata) {
            builder.Append(" associated(").Append(Quote(associated.Name)).Append(", ").Append(Quote(associated.Base)).Append(", ")
                .Append(Quote(associated.Protocol)).Append(", ").Append(associated.Index);
            if (associated.Table is { } table) {
                builder.Append(", ").Append(Quote(table));
                foreach (var step in associated.Path) {
                    builder.Append(", ").Append(step);
                }
            }

            builder.Append(')');
        }

        if (function.SourceFile is { } file) {
            builder.Append(" loc ").Append(Quote(file)).Append(':').Append(function.Line);
        }

        builder.Append(" {\n");

        foreach (var block in ordered) {
            builder.Append(blocks[block]);
            if (block.Arguments.Count > 0) {
                var isEntry = block == function.Blocks[0];
                builder.Append('(')
                    .Append(string.Join(", ", block.Arguments.Select((a, i) =>
                        isEntry && i < function.ParameterNames.Count
                            ? $"{Name(a)} : ${a.Type} var {Quote(function.ParameterNames[i])}"
                            : a.IsGuaranteed
                                ? $"{Name(a)} : @guaranteed ${a.Type}"
                                : $"{Name(a)} : ${a.Type}")))
                    .Append(')');
            }

            builder.Append(":\n");

            foreach (var instruction in block.Instructions) {
                builder.Append("  ");
                if (instruction.HasResult) {
                    builder.Append(Name(instruction)).Append(" = ");
                }

                builder.Append(instruction.Opcode);

                switch (instruction) {
                    case IntegerLiteralInst literal:
                        builder.Append(" $").Append(literal.Type).Append(", ")
                            .Append(literal.WideValue.ToString(CultureInfo.InvariantCulture));
                        break;

                    case StringLiteralInst literal:
                        builder.Append(' ').Append(Quote(literal.Value));
                        break;

                    case MetadataInst metadata:
                        builder.Append(" $").Append(metadata.Described);
                        break;

                    case FloatLiteralInst literal:
                        builder.Append(" $").Append(literal.Type).Append(", ").Append(literal.Value.ToString("R", CultureInfo.InvariantCulture));
                        break;

                    case BuiltinInst builtin:
                        builder.Append(builtin.InBounds ? " [in_bounds]" : "").Append(' ').Append(Quote(builtin.Name)).Append('(')
                            .Append(string.Join(", ", builtin.Operands.Select(Name)))
                            .Append(") : $").Append(builtin.Type);
                        break;

                    case AllocStackInst alloc:
                        builder.Append(alloc.Unmanaged ? " [unmanaged]" : "").Append(" $").Append(alloc.ElementType);
                        if (alloc.VariableName is { } variable) {
                            builder.Append(", var ").Append(Quote(variable));
                        }

                        break;

                    case StructInst structInst:
                        builder.Append(" $").Append(structInst.Type).Append(" (")
                            .Append(string.Join(", ", structInst.Operands.Select(Name))).Append(')');
                        break;

                    case StructExtractInst extract:
                        builder.Append(' ').Append(Name(extract.Value)).Append(", #")
                            .Append(((StructType)extract.Value.Type).Fields[extract.Field].Name);
                        break;

                    case StructElementAddrInst element:
                        builder.Append(' ').Append(Name(element.Address)).Append(", #")
                            .Append(((StructType)((AddressType)element.Address.Type).Pointee).Fields[element.Field].Name);
                        break;

                    case TupleInst tuple:
                        builder.Append(" $").Append(tuple.Type).Append(" (")
                            .Append(string.Join(", ", tuple.Operands.Select(Name))).Append(')');
                        break;

                    case TupleExtractInst extract:
                        builder.Append(' ').Append(Name(extract.Value)).Append(", ").Append(extract.Element);
                        break;

                    case TupleElementAddrInst element:
                        builder.Append(' ').Append(Name(element.Address)).Append(", ").Append(element.Element);
                        break;

                    case EnumInst enumInst:
                        builder.Append(" $").Append(enumInst.Type).Append(", #").Append(enumInst.EnumType.Cases[enumInst.Case].Name);
                        if (enumInst.Payload is { } payload) {
                            builder.Append(", ").Append(Name(payload));
                        }

                        break;

                    case SwitchEnumInst switchEnum: {
                        var enumType = (EnumType)switchEnum.Value.Type;
                        builder.Append(' ').Append(Name(switchEnum.Value));
                        foreach (var (caseIndex, target) in switchEnum.Cases) {
                            builder.Append(", case #").Append(enumType.Cases[caseIndex].Name).Append(": ").Append(Label(target));
                        }

                        if (switchEnum.Default is { } fallback) {
                            builder.Append(", default ").Append(Label(fallback));
                        }

                        break;
                    }

                    case CondFailInst condFail:
                        builder.Append(' ').Append(Name(condFail.Condition)).Append(", ").Append(Quote(condFail.Message));
                        break;

                    case ThrowInst throwInst:
                        builder.Append(' ').Append(Name(throwInst.Error));
                        break;

                    case TryApplyInst tryApply:
                        builder.Append(' ').Append(Name(tryApply.Callee)).Append('(')
                            .Append(string.Join(", ", tryApply.Arguments.Select(Name)))
                            .Append(") : $").Append(tryApply.ResultType)
                            .Append(", normal ").Append(Label(tryApply.Normal))
                            .Append(", error ").Append(Label(tryApply.Error));
                        break;

                    case DeallocStackInst dealloc:
                        builder.Append(' ').Append(Name(dealloc.Address));
                        break;

                    case LoadInst load:
                        builder.Append(" [").Append(load.Kind.ToString().ToLowerInvariant()).Append("] ").Append(Name(load.Address));
                        break;

                    case LoadWeakInst weak:
                        builder.Append(' ').Append(Name(weak.Address));
                        break;

                    case LoadUnownedInst unowned:
                        builder.Append(' ').Append(Name(unowned.Address));
                        break;

                    case StoreWeakInst weak:
                        builder.Append(' ').Append(Name(weak.Value)).Append(" to ").Append(Name(weak.Address));
                        break;

                    case StoreUnownedInst unowned:
                        builder.Append(' ').Append(Name(unowned.Value)).Append(" to ").Append(Name(unowned.Address));
                        break;

                    case LoadBorrowInst borrow:
                        builder.Append(' ').Append(Name(borrow.Address));
                        break;

                    case EndBorrowInst end:
                        builder.Append(' ').Append(Name(end.Borrowed));
                        break;

                    case CopyValueInst copy:
                        builder.Append(' ').Append(Name(copy.Value));
                        break;

                    case WeakCopyValueInst or UnownedCopyValueInst or StrongCopyWeakValueInst or StrongCopyUnownedValueInst:
                        builder.Append(' ').Append(Name(instruction.Operands[0]));
                        break;

                    case DestroyValueInst destroy:
                        builder.Append(destroy.Uncounted ? " [uncounted] " : " ").Append(Name(destroy.Value));
                        break;

                    case DestroyAddrInst destroy:
                        builder.Append(destroy.Uncounted ? " [uncounted] " : " ").Append(Name(destroy.Address));
                        break;

                    case AllocRefInst alloc:
                        builder.Append(alloc.OnStack ? " [stack]" : "").Append(" $").Append(alloc.ClassType);
                        break;

                    case RefElementAddrInst element:
                        builder.Append(' ').Append(Name(element.Reference)).Append(", #")
                            .Append(((ClassType)element.Reference.Type).Fields[element.Field].Name);
                        break;

                    case StoreInst store:
                        builder.Append(' ').Append(Name(store.Value)).Append(" to [")
                            .Append(store.Kind == StoreKind.Init ? "init" : "assign").Append("] ")
                            .Append(Name(store.Address));
                        break;

                    case PartialApplyInst partial:
                        builder.Append(' ').Append(Name(partial.Function)).Append('(')
                            .Append(partial.Context is { } context ? Name(context) : "")
                            .Append(") : $").Append(partial.Type);
                        break;

                    case UpcastInst upcast:
                        builder.Append(' ').Append(Name(upcast.Value)).Append(" : $").Append(upcast.Type);
                        break;

                    case ClassMethodInst method:
                        builder.Append(' ').Append(Name(method.Reference)).Append(", #").Append(method.Method)
                            .Append(" : $").Append(method.Type);
                        break;

                    case UncheckedRefCastInst cast:
                        builder.Append(' ').Append(Name(cast.Value)).Append(" : $").Append(cast.Type);
                        break;

                    case FunctionRefInst reference:
                        builder.Append(" @").Append(reference.Function).Append(" : $").Append(reference.Type);
                        break;

                    case GlobalAddrInst global:
                        builder.Append(" @").Append(global.Global).Append(" : $").Append(global.Type);
                        break;

                    case ApplyInst apply:
                        builder.Append(' ').Append(Name(apply.Callee)).Append('(')
                            .Append(string.Join(", ", apply.Arguments.Select(Name)))
                            .Append(") : $").Append(apply.Type);
                        break;

                    case BranchInst branch:
                        builder.Append(' ').Append(Target(branch.Target, branch.Operands));
                        break;

                    case CondBranchInst conditional:
                        builder.Append(' ').Append(Name(conditional.Condition)).Append(", ")
                            .Append(Target(conditional.WhenTrue, conditional.TrueArguments)).Append(", ")
                            .Append(Target(conditional.WhenFalse, conditional.FalseArguments));
                        break;

                    case ReturnInst { Value: { } value }:
                        builder.Append(' ').Append(Name(value));
                        break;
                }

                if (instruction.Location is { } location) {
                    builder.Append(" loc ").Append(location);
                }

                builder.Append('\n');
            }
        }

        builder.Append("}\n");
    }

    /// <summary>A string as SIL text writes it: quoted, with escapes for what cannot appear bare.</summary>
    /// <summary>
    ///     What a nominal type's declaration says after its body: a generic
    ///     instance's type arguments and its name as Sable writes it, and the
    ///     protocols its metadata's conformance records name.
    /// </summary>
    static string Described(NominalType type) {
        var text = new StringBuilder();
        if (!type.TypeArguments.IsDefaultOrEmpty) {
            text.Append(" type_arguments(").Append(string.Join(", ", type.TypeArguments.Select(static a => $"${a}"))).Append(')');
        }

        if (type.DisplayName is { } display) {
            text.Append(" display ").Append(Quote(display));
        }

        if (type.Conformances.Count > 0) {
            text.Append(" conforms(").Append(string.Join(", ", type.Conformances.Select(Quote))).Append(')');
        }

        if (type.ConditionalConformances.Count > 0) {
            var each = type.ConditionalConformances.Select(c => $"{Quote(c.Protocol)} where({string.Join(", ", c.Requirements.Select(r => $"{r.Argument} {Quote(r.Protocol)}"))})");
            text.Append(" conforms_where(").Append(string.Join(", ", each)).Append(')');
        }

        return text.ToString();
    }

    public static string Quote(string value) {
        var builder = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes()) {
            switch (rune.Value) {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                case < 0x20 or 0x7F:
                    builder.Append("\\u{").Append(rune.Value.ToString("X", CultureInfo.InvariantCulture)).Append('}');
                    break;
                default: builder.Append(rune.ToString()); break;
            }
        }

        return builder.Append('"').ToString();
    }
}
