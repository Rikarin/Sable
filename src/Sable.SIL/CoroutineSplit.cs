namespace Sable.SIL;

/// <summary>
///     The coroutine split's mechanics, shared by the two kinds of function
///     that stop part-way and are resumed later: generators, which stop at
///     each <c>yield</c> (<c>SilGen.Generators.cs</c>), and async functions,
///     which stop where their task waits (<c>AsyncLowering</c>).
/// </summary>
/// <remarks>
///     Either is one function — its <i>resume</i> function — whose state
///     lives in a frame rather than in the machine's: every slot the body
///     makes is a field of the frame, the frame records the state to resume
///     at, and the entry branches on that state to where the body stopped.
///     What else the body holds when it stops must be in the frame too: a
///     generator's lowering refuses anything else (<see cref="LiveIn" />
///     says what that is), and the async split moves it there.
/// </remarks>
internal static class CoroutineSplit {
    /// <summary>
    ///     Makes each slot a field of the frame: <paramref name="fields" />
    ///     gives each its field's address. An address not yet in a block
    ///     takes the slot's place; one already placed — in the entry, before
    ///     every state — replaces it. Every use of the slot then uses the
    ///     field, and the slot's <c>dealloc_stack</c>s go: the frame holds it.
    /// </summary>
    public static void SlotsToFields(SilFunction function, IReadOnlyDictionary<AllocStackInst, SilInstruction> fields) {
        if (fields.Count == 0) {
            return;
        }

        foreach (var (slot, field) in fields) {
            var block = slot.Block;
            var index = block.Instructions.IndexOf(slot);
            field.Location ??= slot.Location;
            if (field.Block is null) {
                field.Block = block;
                block.Instructions[index] = field;
            } else {
                block.Instructions.RemoveAt(index);
            }
        }

        foreach (var block in function.Blocks) {
            block.Instructions.RemoveAll(i => i is DeallocStackInst { Address: AllocStackInst slot } && fields.ContainsKey(slot));
            foreach (var instruction in block.Instructions) {
                for (var i = 0; i < instruction.Operands.Count; i++) {
                    if (instruction.Operands[i] is AllocStackInst slot && fields.TryGetValue(slot, out var field)) {
                        instruction.SetOperand(i, field);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Branches from <paramref name="block" /> on the state: to each
    ///     target for its state, counted from <paramref name="first" />. The
    ///     block left — for any other state — is returned, for the caller to end.
    /// </summary>
    public static SilBlock Dispatch(SilFunction function, SilBlock block, SilValue state, IReadOnlyList<SilBlock> targets, long first, SilLocation? location = null) {
        for (var k = 0; k < targets.Count; k++) {
            var value = block.Append(new IntegerLiteralInst(SilType.Int, first + k) { Location = location });
            var isState = block.Append(new BuiltinInst("cmp_eq_Int", SilType.Bool, state, value) { Location = location });
            var otherwise = function.AddBlock();
            block.Append(new CondBranchInst(isState, targets[k], [], otherwise, []) { Location = location });
            block = otherwise;
        }

        return block;
    }

    /// <summary>
    ///     For each block, the values it uses that are made before it — in
    ///     another block, or as its arguments — on some path to it: what is
    ///     live into it. A value the entry makes is there in every state.
    /// </summary>
    public static Dictionary<SilBlock, HashSet<SilValue>> LiveIn(SilFunction function) {
        var defined = new Dictionary<SilBlock, HashSet<SilValue>>();
        var used = new Dictionary<SilBlock, HashSet<SilValue>>();
        foreach (var block in function.Blocks) {
            var defs = new HashSet<SilValue>(ReferenceEqualityComparer.Instance);
            var uses = new HashSet<SilValue>(ReferenceEqualityComparer.Instance);
            foreach (var instruction in block.Instructions) {
                foreach (var operand in instruction.Operands) {
                    if (!defs.Contains(operand) && operand is SilInstruction or SilArgument) {
                        uses.Add(operand);
                    }
                }

                defs.Add(instruction);
            }

            // A block's own arguments are made as it is entered: they are live
            // into it only in that its predecessors send them.
            uses.ExceptWith(block.Arguments);
            defs.UnionWith(block.Arguments);
            defined[block] = defs;
            used[block] = uses;
        }

        var live = function.Blocks.ToDictionary(static b => b, b => new HashSet<SilValue>(used[b], ReferenceEqualityComparer.Instance));
        for (var changed = true; changed;) {
            changed = false;
            foreach (var block in Enumerable.Reverse(function.Blocks)) {
                foreach (var successor in block.Successors) {
                    foreach (var value in live[successor]) {
                        if (!defined[block].Contains(value) && live[block].Add(value)) {
                            changed = true;
                        }
                    }
                }
            }
        }

        return live;
    }

    /// <summary>True for a value the entry block makes: one there in every state.</summary>
    public static bool MadeByEntry(SilValue value, SilBlock entry) =>
        value is SilArgument { Block: var argumentBlock } && argumentBlock == entry || value is SilInstruction { Block: var block } && block == entry;
}
