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
    public static string Print(SilModule module) {
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
                    builder.Append("sil_struct ").Append(structType.Name).Append(" { ")
                        .Append(string.Join(", ", structType.Fields.Select(static f => $"{f.Name}: ${f.Type}")))
                        .Append(" }\n");
                    break;

                case EnumType enumType:
                    builder.Append("sil_enum ").Append(enumType.Name).Append(" { ")
                        .Append(string.Join(", ", enumType.Cases.Select(static c => c.Payload is null ? c.Name : $"{c.Name}(${c.Payload})")))
                        .Append(" }\n");
                    break;
            }
        }

        foreach (var type in module.Types) {
            Declare(type);
        }

        if (module.Types.Count > 0 && module.Functions.Count > 0) {
            builder.Append('\n');
        }

        for (var i = 0; i < module.Functions.Count; i++) {
            if (i > 0) {
                builder.Append('\n');
            }

            Print(module.Functions[i], builder);
        }

        return builder.ToString();
    }

    /// <summary>The named types a type holds by value, directly or inside tuples.</summary>
    static IEnumerable<NominalType> Contained(SilType type) {
        IEnumerable<SilType> parts = type switch {
            StructType structType => structType.Fields.Select(static f => f.Type),
            EnumType enumType => enumType.Cases.Where(static c => c.Payload is not null).Select(static c => c.Payload!),
            TupleType tuple => tuple.Elements,
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

    static void Print(SilFunction function, StringBuilder builder) {
        var names = new Dictionary<SilValue, string>();
        var blocks = new Dictionary<SilBlock, string>();

        for (var i = 0; i < function.Blocks.Count; i++) {
            blocks[function.Blocks[i]] = $"bb{i}";
        }

        foreach (var block in function.Blocks) {
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
        if (function.SourceFile is { } file) {
            builder.Append(" loc ").Append(Quote(file)).Append(':').Append(function.Line);
        }

        builder.Append(" {\n");

        foreach (var block in function.Blocks) {
            builder.Append(blocks[block]);
            if (block.Arguments.Count > 0) {
                var isEntry = block == function.Blocks[0];
                builder.Append('(')
                    .Append(string.Join(", ", block.Arguments.Select((a, i) =>
                        isEntry && i < function.ParameterNames.Count
                            ? $"{Name(a)} : ${a.Type} var {Quote(function.ParameterNames[i])}"
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
                            .Append(literal.Value.ToString(CultureInfo.InvariantCulture));
                        break;

                    case StringLiteralInst literal:
                        builder.Append(' ').Append(Quote(literal.Value));
                        break;

                    case FloatLiteralInst literal:
                        builder.Append(" $F64, ").Append(literal.Value.ToString("R", CultureInfo.InvariantCulture));
                        break;

                    case BuiltinInst builtin:
                        builder.Append(" \"").Append(builtin.Name).Append("\"(")
                            .Append(string.Join(", ", builtin.Operands.Select(Name)))
                            .Append(") : $").Append(builtin.Type);
                        break;

                    case AllocStackInst alloc:
                        builder.Append(" $").Append(alloc.ElementType);
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
                        builder.Append(" [trivial] ").Append(Name(load.Address));
                        break;

                    case StoreInst store:
                        builder.Append(' ').Append(Name(store.Value)).Append(" to [")
                            .Append(store.Kind == StoreKind.Init ? "init" : "assign").Append("] ")
                            .Append(Name(store.Address));
                        break;

                    case FunctionRefInst reference:
                        builder.Append(" @").Append(reference.Function).Append(" : $").Append(reference.Type);
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
