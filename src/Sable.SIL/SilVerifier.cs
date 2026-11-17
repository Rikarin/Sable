namespace Sable.SIL;

/// <summary>
///     Checks the invariants every SIL function must hold, after lowering and
///     after every pass (docs/04 §2.1: "a SIL verifier runs after every pass in
///     debug builds. Non-negotiable.").
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             <b>Structure.</b> Every block ends in exactly one terminator; the
///             entry block's arguments are the function's parameters.
///         </item>
///         <item>
///             <b>SSA.</b> Every use is dominated by its definition — an
///             earlier instruction in the same block, or a block that dominates.
///         </item>
///         <item>
///             <b>Types.</b> Every instruction's operands have the types it
///             takes; branches pass what the target's arguments expect; returns
///             return what the signature says; calls match the callee's type.
///         </item>
///         <item>
///             <b>Stack discipline.</b> <c>dealloc_stack</c> frees the most
///             recent <c>alloc_stack</c> still live; every path into a block
///             arrives with the same allocations live; none is live at a
///             <c>return</c>.
///         </item>
///         <item>
///             <b>Ownership.</b> Every value in this first subset is trivial.
///             The "every owned value consumed exactly once on every path" check
///             arrives with the first type that has ownership to track.
///         </item>
///     </list>
/// </remarks>
public static class SilVerifier {
    public static IReadOnlyList<string> Verify(SilModule module) {
        var errors = new List<string>();
        foreach (var function in module.Functions) {
            errors.AddRange(Verify(function, module));
        }

        return errors;
    }

    public static IReadOnlyList<string> Verify(SilFunction function, SilModule? module = null) {
        var errors = new List<string>();
        void Error(SilBlock? block, string message) =>
            errors.Add(block is null
                ? $"@{function.Name}: {message}"
                : $"@{function.Name} bb{function.Blocks.IndexOf(block)}: {message}");

        if (function.Blocks.Count == 0) {
            Error(null, "has no blocks");
            return errors;
        }

        // ---- Structure ----------------------------------------------------
        var entry = function.Entry;
        if (entry.Arguments.Count != function.Type.Parameters.Length
            || !entry.Arguments.Select(static a => a.Type).SequenceEqual(function.Type.Parameters)) {
            Error(entry, $"the entry block's arguments must be the parameters ({string.Join(", ", function.Type.Parameters)})");
        }

        var blocks = new HashSet<SilBlock>(function.Blocks);
        if (function.Blocks.Any(block => block.Successors.Contains(entry))) {
            // The entry block's arguments are the parameters; a branch to it
            // would give them second values, which no backend can express.
            Error(entry, "the entry block cannot be branched to");
        }

        foreach (var block in function.Blocks) {
            if (block.Terminator is null) {
                Error(block, "does not end in a terminator");
            }

            for (var i = 0; i < block.Instructions.Count - 1; i++) {
                if (block.Instructions[i].IsTerminator) {
                    Error(block, $"has '{block.Instructions[i].Opcode}' before its end");
                }
            }

            foreach (var successor in block.Successors) {
                if (!blocks.Contains(successor)) {
                    Error(block, "branches to a block outside the function");
                }
            }
        }

        if (errors.Count > 0) {
            return errors;
        }

        // ---- SSA: definitions dominate uses ---------------------------------
        var dominators = Dominators(function);
        var definedIn = new Dictionary<SilValue, (SilBlock Block, int Index)>();
        foreach (var block in function.Blocks) {
            foreach (var argument in block.Arguments) {
                definedIn[argument] = (block, -1);
            }

            for (var i = 0; i < block.Instructions.Count; i++) {
                definedIn[block.Instructions[i]] = (block, i);
            }
        }

        foreach (var block in function.Blocks) {
            if (!dominators.ContainsKey(block)) {
                continue; // unreachable: nothing runs, nothing to check
            }

            for (var i = 0; i < block.Instructions.Count; i++) {
                var instruction = block.Instructions[i];
                foreach (var operand in instruction.Operands) {
                    if (!definedIn.TryGetValue(operand, out var definition)) {
                        Error(block, $"'{instruction.Opcode}' uses a value defined outside the function");
                        continue;
                    }

                    var dominates = definition.Block == block
                        ? definition.Index < i
                        : dominators[block].Contains(definition.Block);
                    if (!dominates) {
                        Error(block, $"'{instruction.Opcode}' uses a value whose definition does not dominate it");
                    }
                }
            }
        }

        // ---- Types ----------------------------------------------------------
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                if (TypeError(instruction, function, module) is { } message) {
                    Error(block, message);
                }

                if (instruction.HasResult && !instruction.Type.IsTrivial) {
                    Error(block, $"'{instruction.Opcode}' produces a non-trivial value, which this SIL cannot track yet");
                }
            }
        }

        if (errors.Count > 0) {
            return errors;
        }

        // ---- Stack discipline ------------------------------------------------
        VerifyStack(function, Error);

        return errors;
    }

    static string? TypeError(SilInstruction instruction, SilFunction function, SilModule? module) {
        static string Types(IEnumerable<SilValue> values) => string.Join(", ", values.Select(static v => v.Type));

        switch (instruction) {
            case IntegerLiteralInst literal when literal.Type != SilType.Int && literal.Type != SilType.Bool:
                return $"integer_literal of type {literal.Type}; only Int and Bool";

            case IntegerLiteralInst { Value: not (0 or 1) } literal when literal.Type == SilType.Bool:
                return $"integer_literal $Bool must be 0 or 1, not {literal.Value}";

            case BuiltinInst builtin: {
                if (SilBuiltins.Find(builtin.Name) is not { } signature) {
                    return $"no builtin named \"{builtin.Name}\"";
                }

                if (!builtin.Operands.Select(static o => o.Type).SequenceEqual(signature.Operands)) {
                    return $"builtin \"{builtin.Name}\" takes ({string.Join(", ", signature.Operands)}), "
                        + $"not ({Types(builtin.Operands)})";
                }

                return builtin.Type == signature.Result
                    ? null
                    : $"builtin \"{builtin.Name}\" gives {signature.Result}, not {builtin.Type}";
            }

            case DeallocStackInst dealloc when dealloc.Address is not AllocStackInst:
                return "dealloc_stack must free an alloc_stack";

            case LoadInst load when load.Address.Type is not AddressType:
                return $"load needs an address, not {load.Address.Type}";

            case StoreInst store when store.Address.Type is not AddressType address || address.Pointee != store.Value.Type:
                return $"store of {store.Value.Type} to {store.Address.Type}";

            case FunctionRefInst reference when module is not null: {
                if (module.Find(reference.Function) is not { } callee) {
                    return $"function_ref names @{reference.Function}, which the module does not define";
                }

                return callee.Type == reference.Type
                    ? null
                    : $"function_ref @{reference.Function} is {callee.Type}, not {reference.Type}";
            }

            case ApplyInst apply: {
                if (apply.Callee.Type is not FunctionType callee) {
                    return $"apply needs a function, not {apply.Callee.Type}";
                }

                if (!apply.Arguments.Select(static a => a.Type).SequenceEqual(callee.Parameters)) {
                    return $"apply passes ({Types(apply.Arguments)}) to {callee}";
                }

                return apply.Type == callee.Result ? null : $"apply of {callee} gives {callee.Result}, not {apply.Type}";
            }

            case BranchInst branch:
                return Arguments(branch.Target, branch.Operands);

            case CondBranchInst conditional:
                if (conditional.Condition.Type != SilType.Bool) {
                    return $"cond_br needs a Bool, not {conditional.Condition.Type}";
                }

                return Arguments(conditional.WhenTrue, conditional.TrueArguments)
                    ?? Arguments(conditional.WhenFalse, conditional.FalseArguments);

            case ReturnInst returnInst: {
                var returned = returnInst.Value?.Type ?? SilType.Unit;
                return returned == function.Type.Result
                    ? null
                    : $"returns {returned} from a function returning {function.Type.Result}";
            }

            default:
                return null;
        }
    }

    static string? Arguments(SilBlock target, IEnumerable<SilValue> arguments) {
        var passed = arguments.Select(static a => a.Type).ToList();
        var expected = target.Arguments.Select(static a => a.Type).ToList();
        return passed.SequenceEqual(expected)
            ? null
            : $"branch passes ({string.Join(", ", passed)}) to a block taking ({string.Join(", ", expected)})";
    }

    /// <summary>
    ///     Walks the blocks from the entry carrying the live stack allocations,
    ///     which every path into a block must agree on.
    /// </summary>
    static void VerifyStack(SilFunction function, Action<SilBlock?, string> error) {
        var onEntry = new Dictionary<SilBlock, List<AllocStackInst>> { [function.Entry] = [] };
        var pending = new Queue<SilBlock>();
        pending.Enqueue(function.Entry);
        var done = new HashSet<SilBlock>();

        while (pending.Count > 0) {
            var block = pending.Dequeue();
            if (!done.Add(block)) {
                continue;
            }

            var stack = new List<AllocStackInst>(onEntry[block]);
            foreach (var instruction in block.Instructions) {
                switch (instruction) {
                    case AllocStackInst alloc:
                        stack.Add(alloc);
                        break;

                    case DeallocStackInst dealloc:
                        if (stack.Count == 0 || !ReferenceEquals(stack[^1], dealloc.Address)) {
                            error(block, "dealloc_stack does not free the most recent live alloc_stack");
                            return;
                        }

                        stack.RemoveAt(stack.Count - 1);
                        break;

                    case LoadInst { Address: AllocStackInst slot } when !stack.Contains(slot):
                    case StoreInst { Address: AllocStackInst storeSlot } when !stack.Contains(storeSlot):
                        error(block, $"'{instruction.Opcode}' uses stack storage that is not live");
                        return;

                    case ReturnInst when stack.Count > 0:
                        error(block, $"returns with {stack.Count} stack allocation(s) still live");
                        return;
                }
            }

            foreach (var successor in block.Successors) {
                if (onEntry.TryGetValue(successor, out var existing)) {
                    if (!existing.SequenceEqual(stack)) {
                        error(successor, "is entered with different stack allocations live on different paths");
                        return;
                    }

                    continue;
                }

                onEntry[successor] = stack;
                pending.Enqueue(successor);
            }
        }
    }

    /// <summary>
    ///     For each block reachable from the entry, the blocks that dominate it,
    ///     itself included. The simple iterative algorithm: fine for the sizes a
    ///     function has.
    /// </summary>
    public static Dictionary<SilBlock, HashSet<SilBlock>> Dominators(SilFunction function) {
        var reachable = new List<SilBlock>();
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>();
        pending.Push(function.Entry);
        while (pending.Count > 0) {
            var block = pending.Pop();
            if (!seen.Add(block)) {
                continue;
            }

            reachable.Add(block);
            foreach (var successor in block.Successors) {
                pending.Push(successor);
            }
        }

        var predecessors = reachable.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in reachable) {
            foreach (var successor in block.Successors) {
                predecessors[successor].Add(block);
            }
        }

        var dominators = reachable.ToDictionary(static b => b, _ => new HashSet<SilBlock>(reachable));
        dominators[function.Entry] = [function.Entry];

        var changed = true;
        while (changed) {
            changed = false;
            foreach (var block in reachable) {
                if (block == function.Entry) {
                    continue;
                }

                HashSet<SilBlock>? meet = null;
                foreach (var predecessor in predecessors[block]) {
                    if (meet is null) {
                        meet = [..dominators[predecessor]];
                    } else {
                        meet.IntersectWith(dominators[predecessor]);
                    }
                }

                meet ??= [];
                meet.Add(block);

                if (!meet.SetEquals(dominators[block])) {
                    dominators[block] = meet;
                    changed = true;
                }
            }
        }

        return dominators;
    }
}
