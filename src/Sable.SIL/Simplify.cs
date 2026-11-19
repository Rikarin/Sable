namespace Sable.SIL;

/// <summary>
///     Tidies a function after the passes that reshape it — the inliner above
///     all: folds constants and the branches they decide, drops blocks
///     nothing reaches, merges a block into its only predecessor, routes
///     around blocks that only branch on, and deletes what nothing uses.
/// </summary>
/// <remarks>
///     <para>
///         LLVM does all of this again, but SIL's own passes look at one block,
///         or a block and its successor, at a time: an inlined call leaves its
///         body in a chain of blocks that, merged, they can see whole.
///     </para>
///     <para>
///         Folding is of <c>Int</c> and <c>Bool</c> arithmetic, logic and
///         comparison whose operands are literals; arithmetic that would
///         overflow is left to trap as the program runs. A <c>cond_br</c> on a
///         literal becomes a <c>br</c>, and a <c>switch_enum</c> of an enum
///         made in the function a <c>br</c> to its case — when the case takes
///         no payload, or a trivial one. An instruction is deleted when
///         nothing uses it and it has no effect: it neither traps, writes nor
///         owns anything.
///     </para>
/// </remarks>
public sealed class Simplify : ISilPass {
    public string Name => "simplify";

    public void Run(SilFunction function) {
        if (function.Blocks.Count == 0) {
            return;
        }

        for (var changed = true; changed;) {
            changed = FoldConstants(function);
            changed |= FoldBranches(function);
            var before = function.Blocks.Count;
            function.RemoveUnreachableBlocks();
            changed |= function.Blocks.Count != before;
            changed |= MergeBlocks(function);
            changed |= SkipForwardingBlocks(function);
            changed |= RemoveDead(function);
        }
    }

    // ---- Constants ----------------------------------------------------------

    static bool FoldConstants(SilFunction function) {
        var changed = false;
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is not BuiltinInst builtin || Fold(builtin) is not { } value) {
                    continue;
                }

                var literal = new IntegerLiteralInst(builtin.Type, value) { Location = builtin.Location, Block = block };
                block.Instructions[i] = literal;
                Uses.Replace(function, builtin, literal);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>The value of a builtin whose operands are literals, when it can be worked out and does not trap.</summary>
    static long? Fold(BuiltinInst builtin) {
        if (builtin.Operands.Count is 0 or > 2 || builtin.Operands.Any(static o => o is not IntegerLiteralInst)) {
            return null;
        }

        var a = ((IntegerLiteralInst)builtin.Operands[0]).Value;
        var b = builtin.Operands.Count == 2 ? ((IntegerLiteralInst)builtin.Operands[1]).Value : 0;
        static long Truth(bool value) => value ? 1 : 0;

        try {
            return builtin.Name switch {
                "add_Int" => checked(a + b),
                "sub_Int" => checked(a - b),
                "mul_Int" => checked(a * b),
                "wrapping_add_Int" => unchecked(a + b),
                "wrapping_sub_Int" => unchecked(a - b),
                "wrapping_mul_Int" => unchecked(a * b),
                "wrapping_neg_Int" => unchecked(-a),
                "wrapping_abs_Int" => a < 0 ? unchecked(-a) : a,
                "and_Int" or "and_Bool" => a & b,
                "or_Int" or "or_Bool" => a | b,
                "xor_Int" or "xor_Bool" => a ^ b,
                "not_Bool" => a ^ 1,
                "min_Int" => Math.Min(a, b),
                "max_Int" => Math.Max(a, b),
                "cmp_eq_Int" or "cmp_eq_Bool" or "cmp_eq_Char" => Truth(a == b),
                "cmp_ne_Int" or "cmp_ne_Bool" or "cmp_ne_Char" => Truth(a != b),
                "cmp_lt_Int" or "cmp_lt_Char" => Truth(a < b),
                "cmp_le_Int" or "cmp_le_Char" => Truth(a <= b),
                "cmp_gt_Int" or "cmp_gt_Char" => Truth(a > b),
                "cmp_ge_Int" or "cmp_ge_Char" => Truth(a >= b),
                _ => null,
            };
        } catch (OverflowException) {
            return null;
        }
    }

    // ---- Branches -------------------------------------------------------------

    static bool FoldBranches(SilFunction function) {
        var changed = false;
        foreach (var block in function.Blocks) {
            BranchInst? replacement = block.Terminator switch {
                CondBranchInst { Condition: IntegerLiteralInst literal } conditional => literal.Value != 0
                    ? new BranchInst(conditional.WhenTrue, [..conditional.TrueArguments])
                    : new BranchInst(conditional.WhenFalse, [..conditional.FalseArguments]),
                CondBranchInst conditional when conditional.WhenTrue == conditional.WhenFalse
                    && conditional.TrueArguments.SequenceEqual(conditional.FalseArguments) =>
                    new BranchInst(conditional.WhenTrue, [..conditional.TrueArguments]),
                SwitchEnumInst { Value: EnumInst made } switchEnum => Decided(switchEnum, made),
                _ => null,
            };

            if (replacement is null) {
                continue;
            }

            replacement.Location = block.Terminator!.Location;
            replacement.Block = block;
            block.Instructions[^1] = replacement;
            changed = true;
        }

        return changed;
    }

    /// <summary>A switch on an enum the function made, as a branch to its case.</summary>
    static BranchInst? Decided(SwitchEnumInst switchEnum, EnumInst made) {
        var target = switchEnum.Cases.FirstOrDefault(c => c.Case == made.Case).Target ?? switchEnum.Default;
        return target switch {
            null => null,
            { Arguments.Count: 0 } => new BranchInst(target),
            { Arguments: [{ Type.IsTrivial: true }] } when made.Payload is { } payload && target != switchEnum.Default => new BranchInst(target, payload),
            _ => null,
        };
    }

    // ---- Blocks --------------------------------------------------------------

    static Dictionary<SilBlock, int> Edges(SilFunction function) {
        var edges = function.Blocks.ToDictionary(static b => b, static _ => 0);
        foreach (var block in function.Blocks) {
            foreach (var successor in block.Successors) {
                edges[successor]++;
            }
        }

        return edges;
    }

    /// <summary>A block whose only predecessor ends in a <c>br</c> to it joins that predecessor.</summary>
    static bool MergeBlocks(SilFunction function) {
        var changed = false;
        var edges = Edges(function);
        for (var i = 0; i < function.Blocks.Count; i++) {
            var block = function.Blocks[i];
            while (block.Terminator is BranchInst branch && branch.Target is var next
                   && next != block && next != function.Entry && edges[next] == 1) {
                for (var k = 0; k < next.Arguments.Count; k++) {
                    Uses.Replace(function, next.Arguments[k], branch.Operands[k]);
                }

                block.Instructions.RemoveAt(block.Instructions.Count - 1);
                foreach (var moved in next.Instructions) {
                    moved.Block = block;
                    block.Instructions.Add(moved);
                }

                next.Instructions.Clear();
                function.Blocks.Remove(next);
                if (function.Blocks.IndexOf(block) is var at && at < i) {
                    i = at;
                }

                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    ///     A block with no arguments that only branches on is routed around:
    ///     what branches to it branches where it does. The values it passes
    ///     are defined above it, so they are there in every predecessor too.
    /// </summary>
    static bool SkipForwardingBlocks(SilFunction function) {
        var changed = false;
        foreach (var block in function.Blocks.ToList()) {
            if (block == function.Entry || block.Arguments.Count > 0
                || block.Instructions is not [BranchInst { Target: var target } forward] || target == block) {
                continue;
            }

            var passed = forward.Operands.ToArray();
            foreach (var predecessor in function.Blocks) {
                if (predecessor == block || !predecessor.Successors.Contains(block)) {
                    continue;
                }

                SilInstruction? replacement = predecessor.Terminator switch {
                    BranchInst => new BranchInst(target, passed),

                    // Both ways to one block is a branch, when both pass the
                    // same; with different values it cannot be expressed.
                    CondBranchInst conditional when (conditional.WhenTrue == block ? target : conditional.WhenTrue)
                        == (conditional.WhenFalse == block ? target : conditional.WhenFalse) =>
                        (conditional.WhenTrue == block ? passed : [..conditional.TrueArguments])
                        .SequenceEqual(conditional.WhenFalse == block ? passed : [..conditional.FalseArguments])
                            ? new BranchInst(target, passed)
                            : null,
                    CondBranchInst conditional => new CondBranchInst(
                        conditional.Condition,
                        conditional.WhenTrue == block ? target : conditional.WhenTrue,
                        conditional.WhenTrue == block ? passed : [..conditional.TrueArguments],
                        conditional.WhenFalse == block ? target : conditional.WhenFalse,
                        conditional.WhenFalse == block ? passed : [..conditional.FalseArguments]
                    ),
                    _ => null,
                };

                if (replacement is null) {
                    if (predecessor.Terminator is CondBranchInst || passed.Length > 0 || target.Arguments.Count > 0) {
                        continue;
                    }

                    // A switch or a try_apply passes nothing to a block without arguments.
                    switch (predecessor.Terminator) {
                        case SwitchEnumInst switchEnum:
                            for (var k = 0; k < switchEnum.Cases.Count; k++) {
                                if (switchEnum.Cases[k].Target == block) {
                                    switchEnum.Cases[k] = (switchEnum.Cases[k].Case, target);
                                }
                            }

                            if (switchEnum.Default == block) {
                                switchEnum.Default = target;
                            }

                            break;
                        case TryApplyInst tryApply:
                            if (tryApply.Normal == block) {
                                tryApply.Normal = target;
                            }

                            if (tryApply.Error == block) {
                                tryApply.Error = target;
                            }

                            break;
                    }

                    changed = true;
                    continue;
                }

                replacement.Location = predecessor.Terminator!.Location;
                replacement.Block = predecessor;
                predecessor.Instructions[^1] = replacement;
                changed = true;
            }
        }

        return changed;
    }

    // ---- Dead code --------------------------------------------------------------

    static bool RemoveDead(SilFunction function) {
        var changed = false;
        for (var again = true; again;) {
            again = false;
            var used = new HashSet<SilValue>();
            foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
                foreach (var operand in instruction.Operands) {
                    used.Add(operand);
                }
            }

            foreach (var block in function.Blocks) {
                again |= block.Instructions.RemoveAll(i => !used.Contains(i) && Removable(i)) > 0;
            }

            changed |= again;
        }

        return changed;
    }

    /// <summary>An instruction that only computes a value: unused, it can go.</summary>
    static bool Removable(SilInstruction instruction) =>
        instruction.HasResult
        && (instruction.Type.IsTrivial || SilVerifier.OwnershipOf(instruction) != Ownership.Owned)
        && instruction switch {
            IntegerLiteralInst or FloatLiteralInst or FunctionRefInst or GlobalAddrInst or ClassMethodInst => true,
            RefElementAddrInst or StructElementAddrInst or TupleElementAddrInst => true,
            StructExtractInst or TupleExtractInst or UpcastInst or UncheckedRefCastInst => true,
            StructInst or TupleInst or EnumInst => instruction.Type.IsTrivial,
            LoadInst { Kind: LoadKind.Trivial } => true,
            BuiltinInst builtin => Pure(builtin.Name),
            _ => false,
        };

    /// <summary>A builtin that neither traps nor has an effect: comparison, logic, floating-point arithmetic, wrapping.</summary>
    internal static bool Pure(string name) =>
        name.StartsWith("cmp_", StringComparison.Ordinal)
        || name.StartsWith("wrapping_", StringComparison.Ordinal)
        || name.StartsWith("overflow_at_", StringComparison.Ordinal)
        || name.StartsWith("and_", StringComparison.Ordinal)
        || name.StartsWith("or_", StringComparison.Ordinal)
        || name.StartsWith("xor_", StringComparison.Ordinal)
        || name.StartsWith("not_", StringComparison.Ordinal)
        || name.StartsWith("min_", StringComparison.Ordinal)
        || name.StartsWith("max_", StringComparison.Ordinal)
        || name.StartsWith("wrap_", StringComparison.Ordinal)
        || name.StartsWith(SilBuiltins.WrappingPrefix, StringComparison.Ordinal)
        || name.StartsWith(FloatFunctions.Prefix, StringComparison.Ordinal)
        || name.StartsWith("byte_swap_", StringComparison.Ordinal)
        || name.StartsWith("bitcast_", StringComparison.Ordinal)
        || name is "add_F64" or "sub_F64" or "mul_F64" or "div_F64" or "rem_F64" or "neg_F64" or "abs_F64"
            or "add_F32" or "sub_F32" or "mul_F32" or "div_F32" or "rem_F32" or "neg_F32" or "abs_F32"
            or "string_count" or "string_compare" or "string_hash" or "hash_seed" or "string_contains" or "string_index_of" or "string_find";
}
