namespace Sable.SIL;

/// <summary>
///     docs/04 §3.7: a class instance or an inline block that does not escape
///     the frame that made it is made on the stack — no allocation, no free.
/// </summary>
/// <remarks>
///     <para>
///         A value escapes when it, or a copy of it, may be reached once the
///         function has returned: returned or thrown; stored anywhere but a
///         local slot; put inside another value; captured; held weakly or
///         unowned; or passed to a parameter that escapes, or to a callee
///         not known here. Which parameters escape is a summary per function,
///         computed for the whole module to a fixed point — the same shape of
///         summary root inference keeps (docs/04 §3.3), so recursion converges.
///     </para>
///     <para>
///         The object keeps its header and its count: ARC is unchanged, and
///         the last release runs the deinit and the fields' releases as
///         before, but frees nothing. An allocation inside a loop is not
///         promoted, so one stack slot never holds two live objects; nor is
///         one a weak reference could outlive.
///     </para>
/// </remarks>
public sealed class PromoteToStack : ISilPass {
    public string Name => "promote-to-stack";

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<SilModule, Dictionary<string, bool[]>> _summaries = new();

    public void Run(SilFunction function) {
        if (function.Module is not { } module) {
            return;
        }

        var summaries = _summaries.GetValue(module, Summarise);
        InlineAllocators(function, module);

        var uses = new Uses(function);
        foreach (var block in function.Blocks) {
            if (InLoop(block)) {
                continue;
            }

            foreach (var instruction in block.Instructions) {
                switch (instruction) {
                    case AllocRefInst alloc when !alloc.OnStack && !Escapes(alloc, uses, summaries):
                        alloc.OnStack = true;
                        break;

                    case BuiltinInst { Name: "inline_new" } made when !Escapes(made, uses, summaries):
                        made.Rename("inline_new_stack");
                        break;
                }
            }
        }
    }

    /// <summary>
    ///     A class's allocating initialiser is an allocation and a call to its
    ///     <c>$initialize</c>: made where it is called, so that the caller
    ///     owns the allocation from the start and may keep it in its frame.
    /// </summary>
    static void InlineAllocators(SilFunction function, SilModule module) {
        foreach (var block in function.Blocks) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (block.Instructions[i] is not ApplyInst { Callee: FunctionRefInst reference } apply
                    || module.Find(reference.Function) is not { } callee
                    || ReferenceEquals(callee, function)
                    || Allocator(callee) is not var (classType, initialize)) {
                    continue;
                }

                var alloc = new AllocRefInst(classType) { Block = block, Location = apply.Location };
                var target = new FunctionRefInst(initialize.Function, (FunctionType)initialize.Type) { Block = block, Location = apply.Location };
                var call = new ApplyInst(target, SilType.Unit, [alloc, ..apply.Arguments]) { Block = block, Location = apply.Location };
                block.Instructions[i] = call;
                block.Instructions.Insert(i, target);
                block.Instructions.Insert(i, alloc);
                Uses.Replace(function, apply, alloc);
                i += 2;
            }
        }
    }

    /// <summary>The class and the <c>$initialize</c> of a function that is only an allocating initialiser.</summary>
    static (ClassType, FunctionRefInst)? Allocator(SilFunction callee) {
        if (callee.Blocks is not [var body]
            || body.Instructions is not [AllocRefInst alloc, FunctionRefInst initialize, ApplyInst call, ReturnInst returned]
            || !ReferenceEquals(call.Callee, initialize)
            || !ReferenceEquals(returned.Value, alloc)
            || initialize.Type is not FunctionType initializeType) {
            return null;
        }

        var arguments = call.Arguments.ToList();
        if (arguments.Count != body.Arguments.Count + 1 || !ReferenceEquals(arguments[0], alloc)) {
            return null;
        }

        for (var i = 0; i < body.Arguments.Count; i++) {
            if (!ReferenceEquals(arguments[i + 1], body.Arguments[i]) || callee.Type.IsOwned(i) != initializeType.IsOwned(i + 1)) {
                return null;
            }
        }

        return (alloc.ClassType, initialize);
    }

    /// <summary>For every function, which of its parameters may escape it.</summary>
    static Dictionary<string, bool[]> Summarise(SilModule module) {
        var summaries = module.Functions.ToDictionary(static f => f.Name, static f => new bool[f.Type.Parameters.Length]);
        var uses = module.Functions.ToDictionary(static f => f, static f => new Uses(f));
        for (var changed = true; changed;) {
            changed = false;
            foreach (var function in module.Functions) {
                if (function.Blocks.Count == 0) {
                    continue;
                }

                var summary = summaries[function.Name];
                for (var i = 0; i < summary.Length && i < function.Entry.Arguments.Count; i++) {
                    if (!summary[i] && Escapes(function.Entry.Arguments[i], uses[function], summaries)) {
                        summary[i] = true;
                        changed = true;
                    }
                }
            }
        }

        return summaries;
    }

    /// <summary>True when the value, or a copy of it, may outlive the function.</summary>
    static bool Escapes(SilValue value, Uses uses, Dictionary<string, bool[]> summaries) {
        var seen = new HashSet<SilValue>();
        var pending = new Stack<SilValue>();
        pending.Push(value);
        while (pending.Count > 0) {
            var current = pending.Pop();
            if (!seen.Add(current) || current.Type.IsTrivial && current is not AllocStackInst) {
                continue;
            }

            foreach (var user in uses.Of(current)) {
                if (Escapes(user, current, pending, uses, summaries)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when this use lets the value escape; values it flows on into are pushed to be followed.</summary>
    static bool Escapes(SilInstruction user, SilValue value, Stack<SilValue> pending, Uses uses, Dictionary<string, bool[]> summaries) {
        switch (user) {
            case DestroyValueInst or DestroyAddrInst or DeallocStackInst or EndBorrowInst:
                return false;

            // Its fields and methods are reached through it; it does not go anywhere.
            case RefElementAddrInst or ClassMethodInst:
                return false;

            case CopyValueInst or UpcastInst or UncheckedRefCastInst or LoadBorrowInst:
                pending.Push(user);
                return false;

            // A local slot: what is loaded from it is followed; anything else
            // given the slot's address may keep what it holds.
            case LoadInst load when value is AllocStackInst:
                pending.Push(load);
                return false;

            case StoreInst store when ReferenceEquals(store.Value, value):
                if (store.Address is AllocStackInst slot) {
                    pending.Push(slot);
                    return false;
                }

                return true;

            case StoreInst when value is AllocStackInst:
                return false;

            case BranchInst branch:
                for (var i = 0; i < branch.Operands.Count; i++) {
                    if (ReferenceEquals(branch.Operands[i], value)) {
                        pending.Push(branch.Target.Arguments[i]);
                    }
                }

                return false;

            case CondBranchInst condition: {
                var trueArguments = condition.TrueArguments.ToList();
                var falseArguments = condition.FalseArguments.ToList();
                for (var i = 0; i < trueArguments.Count; i++) {
                    if (ReferenceEquals(trueArguments[i], value)) {
                        pending.Push(condition.WhenTrue.Arguments[i]);
                    }
                }

                for (var i = 0; i < falseArguments.Count; i++) {
                    if (ReferenceEquals(falseArguments[i], value)) {
                        pending.Push(condition.WhenFalse.Arguments[i]);
                    }
                }

                return false;
            }

            case ApplyInst apply:
                return ArgumentEscapes(apply.Callee, [..apply.Arguments], value, summaries);

            case TryApplyInst apply:
                return ArgumentEscapes(apply.Callee, [..apply.Arguments], value, summaries);

            case BuiltinInst builtin:
                return !BorrowsOnly(builtin, value);

            default:
                return true;
        }
    }

    /// <summary>A known callee keeps an argument only where its summary says the parameter escapes.</summary>
    static bool ArgumentEscapes(SilValue callee, List<SilValue> arguments, SilValue value, Dictionary<string, bool[]> summaries) {
        if (ReferenceEquals(callee, value) || callee is not FunctionRefInst reference || !summaries.TryGetValue(reference.Function, out var summary)) {
            return true;
        }

        for (var i = 0; i < arguments.Count; i++) {
            if (ReferenceEquals(arguments[i], value) && (i >= summary.Length || summary[i])) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The builtins that read a value, or the slot it is in, and keep neither.</summary>
    static bool BorrowsOnly(BuiltinInst builtin, SilValue value) {
        if (builtin.Name.StartsWith("inline_", StringComparison.Ordinal)) {
            // Every inline operation reads the block or changes it where it is;
            // only the element and value operands are copied, as bytes.
            return true;
        }

        // A slice ('array_slice', 'slice_of') keeps the buffer it views, so
        // it is not one of these.
        return builtin.Name switch {
            "string_count" or "string_at" or "string_slice" or "string_contains" or "string_index_of" or "string_trimmed"
                or "string_split" or "string_concat" or "string_concat_many" or "array_count" or "array_get" or "array_get_borrowed" or "print_String" => true,
            _ when builtin.Name.StartsWith("cmp_", StringComparison.Ordinal) => true,
            _ => SilBuiltins.Resolve(builtin.Name, [..builtin.Operands.Select(static o => o.Type)], builtin.Type) is { } signature
                && signature.Consumed.IsEmpty
                && builtin.Type.IsTrivial
                && !builtin.Operands.Any(static o => o.Type is AddressType),
        };
    }

    static bool InLoop(SilBlock block) {
        var seen = new HashSet<SilBlock>();
        var pending = new Stack<SilBlock>(block.Successors);
        while (pending.Count > 0) {
            var next = pending.Pop();
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
}
