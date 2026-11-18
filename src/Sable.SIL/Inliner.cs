namespace Sable.SIL;

/// <summary>
///     Inlines calls to small functions: the callee's body, copied into the
///     caller in place of the <c>apply</c>.
/// </summary>
/// <remarks>
///     <para>
///         LLVM inlines too, but late: what SIL's own passes see is a call,
///         across which they assume the worst — that it may write any object,
///         or keep what it is given. A getter's <c>load [copy]</c>, inlined,
///         is a copy the caller destroys, which <see cref="BorrowLoads" />
///         turns into a borrow; an initialiser inlined is an object
///         <see cref="PromoteToStack" /> can see does not escape.
///     </para>
///     <para>
///         The callee's parameters become the call's arguments: an owned
///         one was consumed by the call and is now consumed by the body; a
///         borrowed one stays the caller's, borrowed for longer. Each
///         <c>return</c> becomes a branch to the rest of the caller's block,
///         which takes the result. A call that can throw is left alone, and
///         so is a callee of more than <see cref="Threshold" /> instructions
///         or with a loop.
///         Callees are inlined into their callers bottom up, so a small
///         function is inlined with what it has inlined already; a recursive
///         call stays a call. The copied instructions take the call's source
///         position, as the debugger would otherwise place them in the wrong
///         function, and the callee's variables lose their names.
///     </para>
/// </remarks>
public sealed class Inliner : ISilPass {
    /// <summary>The largest callee inlined, in instructions.</summary>
    public const int Threshold = 24;

    public string Name => "inline";

    public void Run(SilModule module) {
        foreach (var function in BottomUp(module)) {
            Run(function);
        }
    }

    public void Run(SilFunction function) {
        if (function.Module is not { } module) {
            return;
        }

        // The calls the function makes now: what is copied in is not looked at again.
        var calls = function.Blocks.SelectMany(static b => b.Instructions).OfType<ApplyInst>()
            .Select(apply => (Apply: apply, Callee: apply.Callee is FunctionRefInst reference ? module.Find(reference.Function) : null))
            .Where(call => call.Callee is not null && Inlinable(call.Callee, function))
            .ToList();
        foreach (var (apply, callee) in calls) {
            Inline(function, apply, callee!);
        }

        // What follows a call to a function that never returns — one that
        // only traps — is not reached.
        if (calls.Count > 0) {
            function.RemoveUnreachableBlocks();
        }
    }

    static bool Inlinable(SilFunction callee, SilFunction caller) =>
        callee != caller
        && callee.Blocks.Count > 0
        && callee.Type.Thrown is null
        && callee.Blocks.Sum(static b => b.Instructions.Count) <= Threshold
        && !Loops(callee);

    /// <summary>
    ///     True when the function has a loop. A call is cheap beside a loop,
    ///     and a loop inlined spreads what it borrows over blocks the ARC
    ///     passes do not look across: a dictionary's lookup, inlined, copies
    ///     the dictionary its call only borrowed.
    /// </summary>
    static bool Loops(SilFunction function) {
        var order = SilVerifier.ReversePostorder(function);
        var position = order.Select(static (b, i) => (b, i)).ToDictionary(static p => p.b, static p => p.i);
        return order.Any(block => block.Successors.Any(next => position[next] <= position[block]));
    }

    /// <summary>The module's functions, each after the functions it calls, as far as recursion allows.</summary>
    static List<SilFunction> BottomUp(SilModule module) {
        var order = new List<SilFunction>();
        var seen = new HashSet<SilFunction>();
        foreach (var root in module.Functions) {
            if (!seen.Add(root)) {
                continue;
            }

            var stack = new Stack<(SilFunction Function, IEnumerator<SilFunction> Callees)>();
            stack.Push((root, Callees(root).GetEnumerator()));
            while (stack.Count > 0) {
                var (function, callees) = stack.Peek();
                if (callees.MoveNext()) {
                    if (seen.Add(callees.Current)) {
                        stack.Push((callees.Current, Callees(callees.Current).GetEnumerator()));
                    }

                    continue;
                }

                stack.Pop();
                order.Add(function);
            }
        }

        return order;

        IEnumerable<SilFunction> Callees(SilFunction function) =>
            function.Blocks.SelectMany(static b => b.Instructions).OfType<FunctionRefInst>()
                .Select(reference => module.Find(reference.Function))
                .OfType<SilFunction>()
                .Distinct()
                .ToList();
    }

    static void Inline(SilFunction caller, ApplyInst apply, SilFunction callee) {
        // The rest of the call's block becomes a block of its own, which
        // takes the result.
        var block = apply.Block;
        var at = block.Instructions.IndexOf(apply);
        var rest = caller.AddBlock();
        foreach (var moved in block.Instructions.Skip(at + 1)) {
            moved.Block = rest;
            rest.Instructions.Add(moved);
        }

        block.Instructions.RemoveRange(at, block.Instructions.Count - at);
        if (apply.HasResult) {
            var result = rest.AddArgument(apply.Type);
            Uses.Replace(caller, apply, result);
        }

        // The callee's blocks, copied; its entry takes no arguments, its
        // parameters being the call's arguments.
        var blocks = new Dictionary<SilBlock, SilBlock>();
        var values = new Dictionary<SilValue, SilValue>();
        foreach (var original in callee.Blocks) {
            var copy = caller.AddBlock();
            blocks[original] = copy;
            if (original == callee.Entry) {
                foreach (var (parameter, argument) in original.Arguments.Zip(apply.Arguments)) {
                    values[parameter] = argument;
                }

                continue;
            }

            foreach (var argument in original.Arguments) {
                values[argument] = copy.AddArgument(argument.Type, argument.IsGuaranteed);
            }
        }

        var copied = new List<SilInstruction>();
        foreach (var original in callee.Blocks) {
            var into = blocks[original];
            foreach (var instruction in original.Instructions) {
                SilInstruction copy = instruction switch {
                    ReturnInst returned => new BranchInst(rest, returned.Value is { } value ? [value] : []),
                    AllocStackInst slot => new AllocStackInst(slot.ElementType) { Unmanaged = slot.Unmanaged },
                    _ => instruction.Clone(static v => v, b => blocks[b]),
                };

                // The debugger steps over the call as a line; a trap still names the line inside.
                copy.InlinedSite = instruction.TrapSite(callee);
                copy.Location = apply.Location;
                values[instruction] = copy;
                into.Append(copy);
                copied.Add(copy);
            }
        }

        // Every operand that names something of the callee's names its copy.
        foreach (var instruction in copied) {
            for (var i = 0; i < instruction.Operands.Count; i++) {
                if (values.TryGetValue(instruction.Operands[i], out var replacement)) {
                    instruction.SetOperand(i, replacement);
                }
            }
        }

        block.Append(new BranchInst(blocks[callee.Entry]) { Location = apply.Location });
    }
}
