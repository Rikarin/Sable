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
///         which takes the result; a call that can throw, a
///         <c>try_apply</c>, has its callee's <c>return</c> branch to its
///         normal block and each <c>throw</c> to its error block.
///     </para>
///     <para>
///         What is inlined is decided by a cost: each instruction of the
///         callee costs one, but for what only names or reshapes a value
///         and is gone in machine code — a literal, a field extracted, a
///         cast, a borrow's start and end, a slot, a branch — which costs
///         nothing, and a call, which costs <see cref="CallCost" /> more. A
///         callee costing up to <see cref="Threshold" /> is inlined; one
///         named nowhere else, up to <see cref="SingleCallerThreshold" />,
///         since its body is then moved rather than copied; and each literal
///         argument the callee branches on adds <see cref="ConstantBonus" />,
///         since the branch it decides folds away with what it skips. A
///         callee with a loop is not inlined, nor one that never returns,
///         which is cold and whose frame a trap's backtrace shows, nor, into
///         a loop, one that allocates something it does not return: in its
///         own frame that can be on the stack, but in the caller's loop only
///         if it is gone before the loop comes round, which the callee's
///         blocks may not let it be. What it returns has left its frame
///         anyway, so an initialiser is inlined, and the caller may then
///         keep the object in its frame.
///     </para>
///     <para>
///         Callees are inlined into their callers bottom up, so a small
///         function is inlined with what it has inlined already; a recursive
///         call stays a call. The copied instructions take the call's source
///         position, as the debugger would otherwise place them in the wrong
///         function, and the callee's variables lose their names.
///     </para>
/// </remarks>
public sealed class Inliner : ISilPass {
    /// <summary>The most a callee may cost to be inlined everywhere it is called.</summary>
    public const int Threshold = 24;

    /// <summary>The most a callee named in one place alone may cost.</summary>
    public const int SingleCallerThreshold = 3 * Threshold;

    /// <summary>What a literal argument the callee branches on adds to what it may cost.</summary>
    public const int ConstantBonus = 8;

    /// <summary>What a call costs beyond an instruction's one.</summary>
    public const int CallCost = 4;

    public string Name => "inline";

    public void Run(SilModule module) {
        var references = References(module);
        foreach (var function in BottomUp(module)) {
            Run(function, references);
        }
    }

    public void Run(SilFunction function) => Run(function, null);

    static void Run(SilFunction function, Dictionary<string, int>? references) {
        if (function.Module is not { } module) {
            return;
        }

        // The calls the function makes now: what is copied in is not looked at again.
        var calls = function.Blocks.SelectMany(static b => b.Instructions)
            .Where(static i => i is ApplyInst or TryApplyInst)
            .Select(call => (Call: call, Callee: call.Operands[0] is FunctionRefInst reference ? module.Find(reference.Function) : null))
            .Where(call => call.Callee is not null && Inlinable(call.Callee, function, call.Call, references))
            .ToList();
        foreach (var (call, callee) in calls) {
            Inline(function, call, callee!);
        }

        // What follows a call to a function that never returns — one that
        // only traps — is not reached.
        if (calls.Count > 0) {
            function.RemoveUnreachableBlocks();
        }
    }

    /// <summary>
    ///     Whether to inline a call. <c>@inline(never)</c> says never;
    ///     <c>@inline(always)</c> says whatever the callee's size or loops —
    ///     but not when it calls itself, which no inliner can. Otherwise the
    ///     callee's cost decides.
    /// </summary>
    static bool Inlinable(SilFunction callee, SilFunction caller, SilInstruction call, Dictionary<string, int>? references) {
        if (callee == caller || callee.Inline == SilInline.Never || callee.Blocks.Count == 0) {
            return false;
        }

        if (callee.Inline == SilInline.Always) {
            return true;
        }

        // A callee that never comes back — one that only traps — is cold:
        // a call to it keeps its frame in the trap's backtrace.
        if (!callee.Blocks.Any(static b => b.Terminator is ReturnInst or ThrowInst)) {
            return false;
        }

        // What a callee allocates for itself stays in its frame, on the
        // stack, where it does not escape; inlined into a loop, it may be
        // made on the heap each time round, as an allocation in a loop is
        // promoted only when it is gone within its block.
        if (AllocatesForItself(callee) && InLoop(call.Block)) {
            return false;
        }

        var budget = references is not null && references.GetValueOrDefault(callee.Name) == 1 ? SingleCallerThreshold : Threshold;
        budget += ConstantBonus * DecidingConstants(callee, call);
        return Cost(callee, budget) <= budget && !Loops(callee);
    }

    /// <summary>
    ///     Whether the function allocates anything it does not return: what
    ///     it returns, an initialiser's object, has left its frame anyway.
    /// </summary>
    static bool AllocatesForItself(SilFunction function) {
        var returned = function.Blocks
            .Select(static b => b.Terminator)
            .OfType<ReturnInst>()
            .Select(static r => r.Value is UpcastInst or UncheckedRefCastInst ? ((SilInstruction)r.Value).Operands[0] : r.Value)
            .OfType<SilValue>()
            .ToHashSet();
        return function.Blocks.SelectMany(static b => b.Instructions)
            .Any(i => (i is AllocRefInst || i is BuiltinInst { Name: "inline_new" or "inline_new_stack" }) && !returned.Contains(i));
    }

    /// <summary>True when a block can be reached again from itself.</summary>
    static bool InLoop(SilBlock block) {
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>(block.Successors);
        while (pending.TryPop(out var next)) {
            if (next == block) {
                return true;
            }

            if (seen.Add(next)) {
                foreach (var successor in next.Successors) {
                    pending.Push(successor);
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Whether a call to the function, anywhere, would be inlined as it
    ///     stands: what another pass asks before making a call direct only
    ///     for the inliner's sake.
    /// </summary>
    public static bool WouldInline(SilFunction callee) =>
        callee.Inline != SilInline.Never
        && callee.Blocks.Count > 0
        && (callee.Inline == SilInline.Always || Cost(callee, Threshold) <= Threshold && !Loops(callee));

    /// <summary>What inlining the function costs, counted until it passes <paramref name="limit" />.</summary>
    public static int Cost(SilFunction function, int limit = int.MaxValue) {
        var cost = 0;
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                cost += instruction switch {
                    IntegerLiteralInst or FloatLiteralInst or FunctionRefInst => 0,
                    StructExtractInst or TupleExtractInst or UpcastInst or UncheckedRefCastInst => 0,
                    StructElementAddrInst or TupleElementAddrInst or RefElementAddrInst => 0,
                    LoadBorrowInst or EndBorrowInst or AllocStackInst or DeallocStackInst => 0,
                    BranchInst or ReturnInst or UnreachableInst => 0,
                    ApplyInst or TryApplyInst => 1 + CallCost,
                    _ => 1,
                };

                if (cost > limit) {
                    return cost;
                }
            }
        }

        return cost;
    }

    /// <summary>How many of the call's arguments are literals the callee branches on, directly or through a comparison.</summary>
    static int DecidingConstants(SilFunction callee, SilInstruction call) {
        var count = 0;
        var parameters = callee.Entry.Arguments;
        for (var i = 0; i < parameters.Count && i + 1 < call.Operands.Count; i++) {
            if (call.Operands[i + 1] is not (IntegerLiteralInst or EnumInst { Type.IsTrivial: true })) {
                continue;
            }

            var parameter = parameters[i];
            var decides = callee.Blocks.SelectMany(static b => b.Instructions).Any(instruction =>
                instruction switch {
                    CondBranchInst branch => ReferenceEquals(branch.Condition, parameter)
                        || branch.Condition is BuiltinInst compare && compare.Operands.Any(o => ReferenceEquals(o, parameter)),
                    SwitchEnumInst switchEnum => ReferenceEquals(switchEnum.Value, parameter),
                    _ => false,
                });
            if (decides) {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     How many places name each function: its <c>function_ref</c>s, and
    ///     a class's vtable, a type's deinit, the entry point and the exports,
    ///     each one more — a function they name is called from elsewhere too.
    /// </summary>
    static Dictionary<string, int> References(SilModule module) {
        var references = new Dictionary<string, int>(StringComparer.Ordinal);

        void Count(string? name) {
            if (name is not null) {
                references[name] = references.GetValueOrDefault(name) + 1;
            }
        }

        foreach (var reference in module.Functions.SelectMany(static f => f.Blocks).SelectMany(static b => b.Instructions).OfType<FunctionRefInst>()) {
            Count(reference.Function);
        }

        foreach (var type in module.Types) {
            switch (type) {
                case ClassType classType:
                    foreach (var (_, method) in classType.VTable) {
                        Count(method);
                    }

                    Count(classType.Deinit);
                    break;
                case StructType structType:
                    Count(structType.Deinit);
                    break;
            }
        }

        Count(module.EntryPoint);
        Count("$globals.init");
        Count("$globals.deinit");
        foreach (var name in module.Exports.Keys) {
            Count(name);
        }

        return references;
    }

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

    static void Inline(SilFunction caller, SilInstruction apply, SilFunction callee) {
        var block = apply.Block;
        var at = block.Instructions.IndexOf(apply);
        SilBlock rest;
        SilBlock? failed = null;
        if (apply is TryApplyInst tryApply) {
            // A call that can throw returns to its normal block and throws to its error block.
            rest = tryApply.Normal;
            failed = tryApply.Error;
            block.Instructions.RemoveAt(at);
        } else {
            // The rest of the call's block becomes a block of its own, which
            // takes the result.
            rest = caller.AddBlock();
            foreach (var moved in block.Instructions.Skip(at + 1)) {
                moved.Block = rest;
                rest.Instructions.Add(moved);
            }

            block.Instructions.RemoveRange(at, block.Instructions.Count - at);
            if (apply.HasResult) {
                var result = rest.AddArgument(apply.Type);
                Uses.Replace(caller, apply, result);
            }
        }

        var arguments = apply.Operands.Skip(1).ToList();

        // The callee's blocks, copied; its entry takes no arguments, its
        // parameters being the call's arguments.
        var blocks = new Dictionary<SilBlock, SilBlock>();
        var values = new Dictionary<SilValue, SilValue>();
        foreach (var original in callee.Blocks) {
            var copy = caller.AddBlock();
            blocks[original] = copy;
            if (original == callee.Entry) {
                foreach (var (parameter, argument) in original.Arguments.Zip(arguments)) {
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
                    ReturnInst returned => new BranchInst(rest, returned.Value is { } value && rest.Arguments.Count > 0 ? [value] : []),
                    ThrowInst thrown when failed is not null => new BranchInst(failed, thrown.Error),
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
