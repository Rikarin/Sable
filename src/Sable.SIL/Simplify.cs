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
///         Folding is of <c>Int</c> and <c>Bool</c> arithmetic, logic, shifts
///         and comparison whose operands are literals; arithmetic that would
///         overflow, divide by zero or shift out of range is left to trap as
///         the program runs; two types' metadata, known here, are compared.
///         A trivial field extracted from a struct or tuple
///         made in the function is the value it was made from. A
///         <c>cond_br</c> on a literal becomes a <c>br</c>, and a
///         <c>switch_enum</c> of an enum made in the function a <c>br</c> to
///         its case — when the case takes no payload, or a trivial one; a
///         <c>cond_fail</c> on a literal false goes. An instruction is deleted
///         when nothing uses it and it has no effect: it neither traps,
///         writes nor owns anything.
///     </para>
///     <para>
///         Common subexpressions go: of two instructions computing the same
///         trivial value from the same operands — a literal, a field, a pure
///         or only-trapping builtin — the one dominated by the other uses the
///         other's result. (If the first would trap, it traps first.) A block
///         argument every branch passes the same value is that value, and a
///         trivial one nothing uses is dropped, from the block and from each
///         branch to it.
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
            changed |= FoldExtracts(function);
            changed |= FoldBranches(function);
            changed |= RemoveDecidedChecks(function);
            var before = function.Blocks.Count;
            function.RemoveUnreachableBlocks();
            changed |= function.Blocks.Count != before;
            changed |= MergeBlocks(function);
            changed |= SkipForwardingBlocks(function);
            changed |= RemoveDead(function);
            changed |= SimplifyArguments(function);
            changed |= EliminateCommonSubexpressions(function);
            changed |= DirectCalls(function);
            changed |= DirectWitnesses(function);
            changed |= FoldBoxProjections(function);
        }
    }

    // ---- Calls --------------------------------------------------------------

    /// <summary>
    ///     A call of a closure the function made with no context — an
    ///     existential's witness, made where the existential was — calls its
    ///     code directly, passing no context: LLVM can then inline it. The
    ///     closure is found through the struct it was put in, and a local
    ///     slot stored once, before the call, with what holds it.
    /// </summary>
    internal static bool DirectCalls(SilFunction function) {
        var changed = false;
        Dictionary<SilBlock, HashSet<SilBlock>>? dominators = null;
        Uses? uses = null;
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                if (instruction is not (ApplyInst or TryApplyInst) || instruction.Operands[0].Type is not ThickFunctionType { Signature.IsAsync: false } thick) {
                    continue;
                }

                uses ??= new Uses(function);
                if (ContextFree(instruction.Operands[0], instruction, uses, () => dominators ??= SilVerifier.Dominators(function), 0) is not { Function: FunctionRefInst code }) {
                    continue;
                }

                var target = new FunctionRefInst(code.Function, thick.Implementation) { Block = block, Location = instruction.Location };
                var none = new BuiltinInst("no_context", SilType.Object, []) { Block = block, Location = instruction.Location };
                SilValue[] arguments = [..instruction.Operands.Skip(1), none];
                SilInstruction direct = instruction switch {
                    ApplyInst apply => new ApplyInst(target, apply.Type, arguments),
                    TryApplyInst tryApply => new TryApplyInst(target, tryApply.ResultType, arguments, tryApply.Normal, tryApply.Error),
                    _ => throw new InvalidOperationException(),
                };

                direct.Block = block;
                direct.Location = instruction.Location;
                block.Instructions[i] = direct;
                block.Instructions.Insert(i, none);
                block.Instructions.Insert(i, target);
                Uses.Replace(function, instruction, direct);
                i += 2;
                uses = null;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    ///     A requirement called through a witness table the function knows —
    ///     an existential made here, of a type known here, or a table code
    ///     that knows the type refers to (#299) — calls the conformance's
    ///     witness directly: the entry is a constant of the program. LLVM can
    ///     then inline it. The table is found through the struct the
    ///     existential is, and a local slot stored once before the call.
    /// </summary>
    internal static bool DirectWitnesses(SilFunction function) {
        if (function.Module is not { } module) {
            return false;
        }

        var changed = false;
        Dictionary<SilBlock, HashSet<SilBlock>>? dominators = null;
        Uses? uses = null;
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is not BuiltinInst { Operands: [var tableValue], Type: FunctionType type } entry
                    || !entry.Name.StartsWith("witness_generic:", StringComparison.Ordinal)) {
                    continue;
                }

                uses ??= new Uses(function);
                var index = int.Parse(entry.Name["witness_generic:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                if (KnownTable(module, tableValue, entry, uses, () => dominators ??= SilVerifier.Dominators(function), 0) is not { } table
                    || index >= table.Entries.Count || table.Entries[index] is not { Function: { } witness, Type: { } witnessType } || witnessType != type
                    || module.Find(witness) is null && module.Unit is null) {
                    continue;
                }

                var direct = new FunctionRefInst(witness, type) { Block = block, Location = entry.Location };
                block.Instructions[i] = direct;
                Uses.Replace(function, entry, direct);
                uses = null;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>The witness table a value is, when the function knows which: one code that knows the type refers to, an existential's made here, or a base of one.</summary>
    static SilWitnessTable? KnownTable(SilModule module, SilValue value, SilInstruction at, Uses uses, Func<Dictionary<SilBlock, HashSet<SilBlock>>> dominators, int depth) {
        if (depth > 8) {
            return null;
        }

        switch (value) {
            case StructExtractInst extract:
                return Made(extract.Value, at, uses, dominators, depth + 1) is StructInst made
                    ? KnownTable(module, made.Operands[extract.Field], at, uses, dominators, depth + 1)
                    : null;

            case BuiltinInst { Operands: [var metadata] } builtin when builtin.Name.StartsWith("witness_table_known:", StringComparison.Ordinal):
                return KnownType(metadata, at, uses, dominators, depth + 1) is { } type
                    ? module.FindWitnessTable(type, builtin.Name["witness_table_known:".Length..])
                    : null;

            case BuiltinInst { Operands: [var refining] } builtin when builtin.Name.StartsWith("witness_base:", StringComparison.Ordinal): {
                var index = int.Parse(builtin.Name["witness_base:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                return KnownTable(module, refining, at, uses, dominators, depth + 1) is { } table && index < table.Bases.Count
                    ? module.FindWitnessTable(table.Conformer, table.Bases[index])
                    : null;
            }

            default:
                return null;
        }
    }

    /// <summary>The type a metadata value describes, when it is a constant: directly, or as an existential's made here.</summary>
    static SilType? KnownType(SilValue value, SilInstruction at, Uses uses, Func<Dictionary<SilBlock, HashSet<SilBlock>>> dominators, int depth) =>
        depth > 8 ? null
        : value switch {
            MetadataInst { Described: var described } when !SilType.MentionsOpaque(described) => described,
            StructExtractInst extract => Made(extract.Value, at, uses, dominators, depth + 1) is StructInst made
                ? KnownType(made.Operands[extract.Field], at, uses, dominators, depth + 1)
                : null,
            _ => null,
        };

    /// <summary>
    ///     The storage of an existential's value whose box the function knows
    ///     the class of — one it made, of a type known here — is the box's
    ///     field: what a witness called directly and inlined reads it from.
    /// </summary>
    static bool FoldBoxProjections(SilFunction function) {
        var changed = false;
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is not BuiltinInst { Name: "opaque_address", Operands: [BuiltinInst { Name: "box_project", Operands: [var box, _] }], Type: AddressType { Pointee: var held } } address) {
                    continue;
                }

                var known = box;
                while (known is UpcastInst or OwnedUpcastInst or CopyValueInst) {
                    known = ((SilInstruction)known).Operands[0];
                }

                if (known.Type is not ClassType { Fields: [(_, var field)] } boxClass || field != held || !boxClass.Name.StartsWith("any$box$of$", StringComparison.Ordinal)) {
                    continue;
                }

                // The box itself, which the existential keeps alive, seen as its class.
                var cast = new UncheckedRefCastInst(box, boxClass) { Block = block, Location = address.Location };
                var element = new RefElementAddrInst(cast, 0) { Block = block, Location = address.Location };
                block.Instructions[i] = element;
                block.Instructions.Insert(i, cast);
                Uses.Replace(function, address, element);
                i++;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>The closure with no context a value is, when the function made it: directly, as a field of a struct it made, or through a slot stored once before <paramref name="at" />.</summary>
    static PartialApplyInst? ContextFree(SilValue value, SilInstruction at, Uses uses, Func<Dictionary<SilBlock, HashSet<SilBlock>>> dominators, int depth) {
        if (depth > 8) {
            return null;
        }

        switch (value) {
            case PartialApplyInst { Context: null } closure:
                return closure;

            case StructExtractInst extract:
                return Made(extract.Value, at, uses, dominators, depth + 1) is StructInst made
                    ? ContextFree(made.Operands[extract.Field], at, uses, dominators, depth + 1)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>The instruction a value was made by, seen through a borrow or copy of a local slot stored once, before <paramref name="at" />, and otherwise only read.</summary>
    static SilValue Made(SilValue value, SilInstruction at, Uses uses, Func<Dictionary<SilBlock, HashSet<SilBlock>>> dominators, int depth) {
        if (depth > 8 || value is not (LoadBorrowInst { Address: AllocStackInst } or LoadInst { Kind: LoadKind.Copy, Address: AllocStackInst })) {
            return value;
        }

        var slot = (AllocStackInst)((SilInstruction)value).Operands[0];
        StoreInst? only = null;
        foreach (var user in uses.Of(slot)) {
            switch (user) {
                case StoreInst store when ReferenceEquals(store.Address, slot) && only is null:
                    only = store;
                    break;
                case LoadBorrowInst or LoadInst { Kind: not LoadKind.Take } or DestroyAddrInst or DeallocStackInst:
                    break;
                default:
                    return value;
            }
        }

        if (only is null) {
            return value;
        }

        var stored = only.Block == at.Block
            ? at.Block.Instructions.IndexOf(only) < at.Block.Instructions.IndexOf(at)
            : dominators().TryGetValue(at.Block, out var set) && set.Contains(only.Block);
        return stored ? Made(only.Value, at, uses, dominators, depth + 1) : value;
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
        // Two types known here: 'T.self == Int.self' in an instance of generic code.
        if (builtin is { Name: "metadata_equal", Operands: [MetadataInst left, MetadataInst right] }) {
            return left.Described.Equals(right.Described) ? 1 : 0;
        }

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
                "div_Int" when b != 0 && !(a == long.MinValue && b == -1) => a / b,
                "rem_Int" when b != 0 && !(a == long.MinValue && b == -1) => a % b,
                "shl_Int" when b is >= 0 and <= 63 => a << (int)b,
                "shr_Int" when b is >= 0 and <= 63 => a >> (int)b,
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

    /// <summary>A trivial field of a struct or tuple the function made is the value it was made from.</summary>
    static bool FoldExtracts(SilFunction function) {
        var changed = false;
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                SilValue? part = block.Instructions[i] switch {
                    StructExtractInst { Type.IsTrivial: true, Value: StructInst made } extract => made.Operands[extract.Field],
                    TupleExtractInst { Type.IsTrivial: true, Value: TupleInst made } extract => made.Operands[extract.Element],
                    _ => null,
                };

                if (part is null) {
                    continue;
                }

                Uses.Replace(function, block.Instructions[i], part);
                block.Instructions.RemoveAt(i);
                i--;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>A check whose condition is a literal false never fails.</summary>
    static bool RemoveDecidedChecks(SilFunction function) {
        var changed = false;
        foreach (var block in function.Blocks) {
            changed |= block.Instructions.RemoveAll(static i => i is CondFailInst { Condition: IntegerLiteralInst { Value: 0 } }) > 0;
        }

        return changed;
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

            // A switch that took the enum it was given passes the payload on, owned: the enum is never made.
            if (block.Terminator is SwitchEnumInst { Takes: true, Value: EnumInst taken }) {
                if (function.Blocks.Any(b => b.Instructions.Any(i => i != block.Terminator && i.Operands.Contains(taken)))) {
                    continue;
                }

                taken.Block.Instructions.Remove(taken);
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
            { Arguments: [{ IsGuaranteed: false }] } when switchEnum.Takes && made.Payload is { } owned && target != switchEnum.Default => new BranchInst(target, owned),
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

    // ---- Block arguments ----------------------------------------------------------

    /// <summary>
    ///     A trivial block argument every branch to its block passes the same
    ///     value — or the argument itself, round a loop — is that value; one
    ///     nothing uses is dropped. Only a block every predecessor reaches by
    ///     <c>br</c> or <c>cond_br</c> is changed, as those can drop what they
    ///     pass.
    /// </summary>
    internal static bool SimplifyArguments(SilFunction function) {
        var edges = new Dictionary<SilBlock, List<SilInstruction>>();
        foreach (var block in function.Blocks) {
            foreach (var successor in block.Successors.Distinct()) {
                if (!edges.TryGetValue(successor, out var list)) {
                    edges[successor] = list = [];
                }

                list.Add(block.Terminator!);
            }
        }

        var used = new HashSet<SilValue>();
        foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
            foreach (var operand in instruction.Operands) {
                used.Add(operand);
            }
        }

        foreach (var block in function.Blocks) {
            if (block == function.Entry || block.Arguments.Count == 0 || !edges.TryGetValue(block, out var incoming)
                || incoming.Any(static t => t is not (BranchInst or CondBranchInst))) {
                continue;
            }

            var changed = false;
            for (var i = block.Arguments.Count - 1; i >= 0; i--) {
                var argument = block.Arguments[i];
                if (!argument.Type.IsTrivial) {
                    continue;
                }

                var passed = incoming.SelectMany(t => Passed(t, block, i)).Where(v => !ReferenceEquals(v, argument)).Distinct().ToList();
                if (used.Contains(argument) && passed is not [_]) {
                    continue;
                }

                if (passed is [var only]) {
                    Uses.Replace(function, argument, only);
                    used.Add(only);
                }

                block.Arguments.RemoveAt(i);
                for (var k = 0; k < incoming.Count; k++) {
                    incoming[k] = DropPassed(incoming[k], block, i);
                }

                changed = true;
            }

            // A branch replaced here may also lead to another block, whose
            // list of branches still holds the old one: start again.
            if (changed) {
                return true;
            }
        }

        return false;
    }

    /// <summary>What a branch passes a block's argument, on each of its edges to it.</summary>
    static IEnumerable<SilValue> Passed(SilInstruction terminator, SilBlock block, int index) {
        switch (terminator) {
            case BranchInst branch:
                yield return branch.Operands[index];
                break;
            case CondBranchInst conditional:
                if (conditional.WhenTrue == block) {
                    yield return conditional.TrueArguments.ElementAt(index);
                }

                if (conditional.WhenFalse == block) {
                    yield return conditional.FalseArguments.ElementAt(index);
                }

                break;
        }
    }

    static SilInstruction DropPassed(SilInstruction terminator, SilBlock block, int index) {
        SilInstruction replacement = terminator switch {
            BranchInst branch => new BranchInst(block, [..branch.Operands.Where((_, k) => k != index)]),
            CondBranchInst conditional => new CondBranchInst(
                conditional.Condition,
                conditional.WhenTrue,
                [..conditional.TrueArguments.Where((_, k) => conditional.WhenTrue != block || k != index)],
                conditional.WhenFalse,
                [..conditional.FalseArguments.Where((_, k) => conditional.WhenFalse != block || k != index)]
            ),
            _ => throw new InvalidOperationException("only a br or cond_br drops what it passes"),
        };

        var from = terminator.Block;
        replacement.Location = terminator.Location;
        replacement.Block = from;
        from.Instructions[^1] = replacement;
        return replacement;
    }

    // ---- Common subexpressions ----------------------------------------------------

    /// <summary>
    ///     Walks the dominator tree with what each block's dominators have
    ///     computed: an instruction computing what one of those did already
    ///     uses that one's result instead.
    /// </summary>
    static bool EliminateCommonSubexpressions(SilFunction function) {
        var dominators = SilVerifier.Dominators(function);
        var children = new Dictionary<SilBlock, List<SilBlock>>();
        foreach (var (block, set) in dominators) {
            var immediate = set.Where(d => d != block).OrderByDescending(d => dominators[d].Count).FirstOrDefault();
            if (immediate is null) {
                continue;
            }

            if (!children.TryGetValue(immediate, out var list)) {
                children[immediate] = list = [];
            }

            list.Add(block);
        }

        var ids = new Dictionary<SilValue, int>(ReferenceEqualityComparer.Instance);
        string Id(SilValue value) {
            if (!ids.TryGetValue(value, out var id)) {
                ids[value] = id = ids.Count;
            }

            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var available = new Dictionary<string, SilInstruction>(StringComparer.Ordinal);
        var replace = new Dictionary<SilValue, SilValue>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(SilBlock Block, List<string>? Added)>();
        pending.Push((function.Entry, null));
        while (pending.TryPop(out var item)) {
            // Leaving a block's subtree: what it computed is no longer available.
            if (item.Added is { } added) {
                foreach (var key in added) {
                    available.Remove(key);
                }

                continue;
            }

            var mine = new List<string>();
            foreach (var instruction in item.Block.Instructions) {
                for (var i = 0; i < instruction.Operands.Count; i++) {
                    if (replace.TryGetValue(instruction.Operands[i], out var earlier)) {
                        instruction.SetOperand(i, earlier);
                    }
                }

                if (Key(instruction, Id) is not { } key) {
                    continue;
                }

                if (available.TryGetValue(key, out var same)) {
                    replace[instruction] = same;
                } else {
                    available[key] = instruction;
                    mine.Add(key);
                }
            }

            pending.Push((item.Block, mine));
            foreach (var child in children.GetValueOrDefault(item.Block) ?? []) {
                pending.Push((child, null));
            }
        }

        if (replace.Count == 0) {
            return false;
        }

        // Every use was rewritten as the walk reached it, a use being
        // dominated by what it uses; a block the walk cannot reach is
        // rewritten too, until it is dropped.
        foreach (var block in function.Blocks) {
            block.Instructions.RemoveAll(i => replace.ContainsKey(i));
            foreach (var instruction in block.Instructions) {
                for (var i = 0; i < instruction.Operands.Count; i++) {
                    if (replace.TryGetValue(instruction.Operands[i], out var earlier)) {
                        instruction.SetOperand(i, earlier);
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    ///     What an instruction computes, as text equal for two that compute
    ///     the same: only for a trivial value that depends on nothing but its
    ///     operands; null for anything else.
    /// </summary>
    static string? Key(SilInstruction instruction, Func<SilValue, string> id) {
        if (!instruction.HasResult || !instruction.Type.IsTrivial || instruction.Operands.Any(static o => o.Type is AddressType)) {
            return null;
        }

        var operands = string.Join(",", instruction.Operands.Select(id));
        return instruction switch {
            IntegerLiteralInst literal => $"int {literal.Type} {literal.WideValue}",
            FloatLiteralInst literal => $"float {literal.Type} {BitConverter.DoubleToInt64Bits(literal.Value)}",
            FunctionRefInst reference => $"fn {reference.Function} {reference.Type}",
            StructExtractInst extract => $"se {extract.Field} {operands}",
            TupleExtractInst extract => $"te {extract.Element} {operands}",
            StructInst made => $"s {made.Type} {operands}",
            TupleInst made => $"t {made.Type} {operands}",
            EnumInst made => $"e {made.Type} {made.Case} {operands}",
            BuiltinInst builtin when Pure(builtin.Name) || OnlyTraps(builtin.Name) =>
                $"b {builtin.Name} {builtin.InBounds} {builtin.Type} {operands}",
            _ => null,
        };
    }

    /// <summary>A builtin that computes its result from its operands alone, but may trap: checked arithmetic and conversion.</summary>
    static bool OnlyTraps(string name) =>
        name.StartsWith("add_", StringComparison.Ordinal)
        || name.StartsWith("sub_", StringComparison.Ordinal)
        || name.StartsWith("mul_", StringComparison.Ordinal)
        || name.StartsWith("div_", StringComparison.Ordinal)
        || name.StartsWith("rem_", StringComparison.Ordinal)
        || name.StartsWith("shl_", StringComparison.Ordinal)
        || name.StartsWith("shr_", StringComparison.Ordinal)
        || name.StartsWith("neg_", StringComparison.Ordinal)
        || name.StartsWith("convert_", StringComparison.Ordinal);

    /// <summary>An instruction that only computes a value: unused, it can go.</summary>
    static bool Removable(SilInstruction instruction) =>
        instruction.HasResult
        && (instruction.Type.IsTrivial || SilVerifier.OwnershipOf(instruction) != Ownership.Owned)
        && instruction switch {
            IntegerLiteralInst or FloatLiteralInst or FunctionRefInst or GlobalAddrInst or ClassMethodInst => true,
            RefElementAddrInst or StructElementAddrInst or TupleElementAddrInst or EnumPayloadAddrInst => true,
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
        || name.StartsWith("select_", StringComparison.Ordinal)
        || name.StartsWith("min_", StringComparison.Ordinal)
        || name.StartsWith("max_", StringComparison.Ordinal)
        || name.StartsWith("wrap_", StringComparison.Ordinal)
        || name.StartsWith(SilBuiltins.WrappingPrefix, StringComparison.Ordinal)
        || SilBuiltins.IsNumericIntrinsic(name)
        || name.StartsWith(FloatFunctions.Prefix, StringComparison.Ordinal)
        || name.StartsWith("bitcast_", StringComparison.Ordinal)
        || name is "add_F64" or "sub_F64" or "mul_F64" or "div_F64" or "rem_F64" or "neg_F64" or "abs_F64"
            or "add_F32" or "sub_F32" or "mul_F32" or "div_F32" or "rem_F32" or "neg_F32" or "abs_F32"
            or "string_count" or "string_compare" or "string_hash" or "hash_seed" or "string_contains" or "string_index_of" or "string_find"
            or "string_utf8_count" or "string_utf8_find" or "string_utf8_find_last" or "utf8_validate"
            or "string_count_range" or "string_compare_range" or "string_utf8_find_range" or "string_utf8_find_last_range"
            or "string_utf8_find_any"
            or "string_hash_range" or "metadata_equal" or "metadata_hash" or "metadata_is_subtype";
}
