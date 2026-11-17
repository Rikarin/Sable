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
        for (var i = 0; i < module.Functions.Count; i++) {
            if (i > 0) {
                builder.Append('\n');
            }

            Print(module.Functions[i], builder);
        }

        return builder.ToString();
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

        string Target(SilBlock block, IEnumerable<SilValue> arguments) {
            var list = arguments.Select(Name).ToList();
            var label = blocks.TryGetValue(block, out var name) ? name : "bb<undefined>";
            return list.Count == 0 ? label : $"{label}({string.Join(", ", list)})";
        }

        builder.Append("sil @").Append(function.Name).Append(" : $").Append(function.Type).Append(" {\n");

        foreach (var block in function.Blocks) {
            builder.Append(blocks[block]);
            if (block.Arguments.Count > 0) {
                builder.Append('(')
                    .Append(string.Join(", ", block.Arguments.Select(a => $"{Name(a)} : ${a.Type}")))
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

                builder.Append('\n');
            }
        }

        builder.Append("}\n");
    }
}
