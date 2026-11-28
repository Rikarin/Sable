namespace Sable.SIL;

/// <summary>
///     Drops the overflow check of an Int addition or subtraction of a
///     constant where what is known of the other operand shows it cannot
///     overflow: a comparison it passed on the way, or that it is never
///     negative. The arithmetic of a loop's indices — <c>i + 1</c> under
///     <c>i &lt; n</c>, <c>j - 1</c> under <c>j &gt; 0</c> or of an index
///     counted up from zero — in loops <see cref="VersionLoops" /> leaves as
///     they are.
/// </summary>
/// <remarks>
///     <para>
///         What is known of a value <c>x</c> where the addition is:
///     </para>
///     <list type="bullet">
///         <item>
///             A comparison of it the code branched on to get there: the
///             block is reached only through the branch's one side, its
///             successor having no other predecessor. <c>x &lt; y</c> and
///             <c>x &lt;= y</c>, where <c>y</c> is an array's count, leave
///             room for any addend up to 2³²; <c>x &lt; y</c> for
///             <c>x + 1</c> under any bound but a constant, where <c>x</c>
///             is not divided (LLVM, told the range of an index it
///             divides, narrows <c>i % 7</c> to 32 bits, which on arm64 is
///             longer; a constant bound it sees itself).
///             <c>x &gt; y</c> leaves
///             room for <c>x - 1</c>, and <c>x &gt; y</c> or <c>x &gt;= y</c>,
///             with <c>y</c> never negative or a constant no less than
///             -2⁶², for a subtrahend up to 2³².
///         </item>
///         <item>
///             That it is never negative: a constant that is not, an array's
///             count, a checked sum or product of such values (which traps
///             where it would not be), or a block's argument every value
///             handed to which is never negative. <c>x - k</c> then cannot
///             overflow for any <c>k</c> from 0 to 2⁶².
///         </item>
///     </list>
///     <para>
///         The operation becomes its wrapping form, which in range is the
///         same, marked as never wrapping (<c>[no_overflow]</c>, LLVM's
///         <c>nsw</c>) so that LLVM still knows what the checked form told
///         it; one whose overflow none of this rules out keeps its trap.
///     </para>
/// </remarks>
public sealed class ProveNoOverflow : ISilPass {
    public string Name => "prove-no-overflow";

    const long Small = 1L << 32;
    const long Large = 1L << 62;

    public void Run(SilFunction function) {
        if (function.Blocks.Count == 0) {
            return;
        }

        var sites = new List<(BuiltinInst Site, SilValue Value, long Delta)>();
        foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
            if (instruction is BuiltinInst { Name: "add_Int" or "sub_Int" } site && Delta(site) is var (value, delta)) {
                sites.Add((site, value, delta));
            }
        }

        if (sites.Count == 0) {
            return;
        }

        var dominators = SilVerifier.Dominators(function);
        var predecessors = function.Blocks.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in function.Blocks) {
            foreach (var successor in block.Successors.Distinct()) {
                if (predecessors.TryGetValue(successor, out var list)) {
                    list.Add(block);
                }
            }
        }

        var nonNegative = NonNegative(function, predecessors);
        var divided = Divided(function);
        foreach (var (site, value, delta) in sites) {
            if (!dominators.TryGetValue(site.Block, out var dominating)) {
                continue; // unreachable
            }

            if (Fits(value, delta, nonNegative, divided, Facts(dominating, predecessors))) {
                site.Rename(SilBuiltins.WrappingPrefix + site.Name);
                site.NoOverflow = true;
            }
        }
    }

    /// <summary>
    ///     The value an addition or subtraction moves by a constant, and by
    ///     how much; null for any other, and for a constant of zero, which
    ///     LLVM drops as it is (made wrapping, xxh3's short hashes, inlined
    ///     with many, came out two instructions longer a call).
    /// </summary>
    static (SilValue Value, long Delta)? Delta(BuiltinInst site) {
        var add = site.Name == "add_Int";
        if (site.Operands[1] is IntegerLiteralInst right && right.Value != long.MinValue && right.Value != 0) {
            return (site.Operands[0], add ? right.Value : -right.Value);
        }

        if (add && site.Operands[0] is IntegerLiteralInst left && left.Value != long.MinValue && left.Value != 0) {
            return (site.Operands[1], left.Value);
        }

        return null;
    }

    /// <summary>The comparisons known true where a block is: those of the branches only one side of which leads to it.</summary>
    static IEnumerable<(BuiltinInst Comparison, bool Holds)> Facts(HashSet<SilBlock> dominating, Dictionary<SilBlock, List<SilBlock>> predecessors) {
        foreach (var block in dominating) {
            if (predecessors[block] is [var from] && from.Terminator is CondBranchInst branch && branch.WhenTrue != branch.WhenFalse
                && branch.Condition is BuiltinInst { Name: "cmp_lt_Int" or "cmp_le_Int" or "cmp_gt_Int" or "cmp_ge_Int" } comparison) {
                yield return (comparison, branch.WhenTrue == block);
            }
        }
    }

    static bool Fits(SilValue value, long delta, HashSet<SilValue> nonNegative, HashSet<SilValue> divided, IEnumerable<(BuiltinInst Comparison, bool Holds)> facts) {
        if (delta < 0 && delta >= -Large && nonNegative.Contains(value)) {
            return true;
        }

        foreach (var (comparison, holds) in facts) {
            // As 'value < other', 'value <= other', 'value > other' or 'value >= other'.
            var (a, b) = (comparison.Operands[0], comparison.Operands[1]);
            string relation;
            SilValue other;
            if (ReferenceEquals(a, value)) {
                relation = comparison.Name[4..6];
                other = b;
            } else if (ReferenceEquals(b, value)) {
                relation = comparison.Name[4..6] switch { "lt" => "gt", "le" => "ge", "gt" => "lt", _ => "le" };
                other = a;
            } else {
                continue;
            }

            if (!holds) {
                relation = relation switch { "lt" => "ge", "le" => "gt", "gt" => "le", _ => "lt" };
            }

            // Any step under an array's count; one of 1 under another bound
            // that is no constant, unless the value is divided: LLVM, told
            // the range of an index it divides, narrows the division to 32
            // bits, which on arm64 is longer ('round % 7' under 'round <
            // 50000', 'i % 10' under 'i < lines': the kernels and words
            // benchmarks), and a constant bound it sees itself.
            var proves = relation switch {
                "lt" => delta > 0 && delta <= Small && AtMostLarge(other) || delta == 1 && other is not IntegerLiteralInst && !divided.Contains(value),
                "le" => delta > 0 && delta <= Small && AtMostLarge(other),
                "gt" => delta == -1 || delta < 0 && delta >= -Small && AtLeastMinusLarge(other, nonNegative),
                _ => delta < 0 && delta >= -Small && AtLeastMinusLarge(other, nonNegative),
            };
            if (proves) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The Int values a division or remainder takes, and those they are worked out from by addition, subtraction or multiplication.</summary>
    static HashSet<SilValue> Divided(SilFunction function) {
        var divided = new HashSet<SilValue>();
        var pending = new Stack<SilValue>();
        foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
            if (instruction is BuiltinInst { Name: "div_Int" or "rem_Int" or "wrapping_div_Int" or "wrapping_rem_Int" } division) {
                pending.Push(division.Operands[0]);
            }
        }

        while (pending.TryPop(out var value)) {
            if (divided.Add(value) && value is BuiltinInst { Name: "add_Int" or "sub_Int" or "mul_Int" or "wrapping_add_Int" or "wrapping_sub_Int" or "wrapping_mul_Int" } arithmetic) {
                foreach (var operand in arithmetic.Operands) {
                    pending.Push(operand);
                }
            }
        }

        return divided;
    }

    static bool AtMostLarge(SilValue value) => value is BuiltinInst { Name: "array_count" };

    static bool AtLeastMinusLarge(SilValue value, HashSet<SilValue> nonNegative) =>
        value is IntegerLiteralInst { Value: >= -Large } || nonNegative.Contains(value);

    /// <summary>
    ///     The Int values of a function that are never negative: assumed of
    ///     every block's argument at first, and taken back from each handed
    ///     a value that may be, until none is.
    /// </summary>
    static HashSet<SilValue> NonNegative(SilFunction function, Dictionary<SilBlock, List<SilBlock>> predecessors) {
        var assumed = new HashSet<SilArgument>();
        foreach (var block in function.Blocks.Skip(1)) {
            assumed.UnionWith(block.Arguments.Where(static a => a.Type == SilType.Int));
        }

        // What each block's arguments are handed, by every way in; null where a way in hands them what cannot be seen.
        var incoming = new Dictionary<SilArgument, List<SilValue>?>();
        foreach (var argument in assumed) {
            var block = argument.Block;
            var at = block.Arguments.IndexOf(argument);
            List<SilValue>? values = [];
            foreach (var from in predecessors[block]) {
                switch (from.Terminator) {
                    case BranchInst branch:
                        values.Add(branch.Operands[at]);
                        break;
                    case CondBranchInst branch when branch.WhenTrue != branch.WhenFalse:
                        values.Add(branch.WhenTrue == block ? branch.TrueArguments.ElementAt(at) : branch.FalseArguments.ElementAt(at));
                        break;
                    default:
                        values = null;
                        break;
                }

                if (values is null) {
                    break;
                }
            }

            incoming[argument] = values;
        }

        bool Holds(SilValue value, int depth) =>
            depth < 32 && value switch {
                IntegerLiteralInst literal => literal.Value >= 0,
                SilArgument argument => assumed.Contains(argument),
                BuiltinInst { Name: "array_count" } => true,
                BuiltinInst { Name: "add_Int" or "mul_Int" } checkedOp => Holds(checkedOp.Operands[0], depth + 1) && Holds(checkedOp.Operands[1], depth + 1),
                _ => false,
            };

        for (var changed = true; changed;) {
            changed = false;
            foreach (var argument in assumed.ToList()) {
                if (incoming[argument] is not { } values || !values.All(v => Holds(v, 0))) {
                    assumed.Remove(argument);
                    changed = true;
                }
            }
        }

        var result = new HashSet<SilValue>(assumed);
        foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
            if (instruction.HasResult && instruction.Type == SilType.Int && Holds(instruction, 0)) {
                result.Add(instruction);
            }
        }

        return result;
    }
}
