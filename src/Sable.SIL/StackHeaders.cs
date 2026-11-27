namespace Sable.SIL;

/// <summary>
///     docs/04 §3.7: which objects made in the frame need no header. An
///     object's header — its metadata, count and flags — is read by a
///     counted retain or release, a call through its vtable, the runtime's
///     destroy (which finds the deinit through the metadata, and checks the
///     count), a deinit that tests whether the initialiser finished, and any
///     callee that does one of these to a parameter. An object promoted to the
///     stack that nothing copies (<see cref="PromoteToStack" />) and whose
///     every use is none of these — its fields reached, it borrowed, kept in
///     a local slot, handed to callees that only reach its fields, destroyed
///     uncounted — is made without one: its fields alone, when no callee is
///     handed it; otherwise its usual layout, the header never written.
/// </summary>
/// <remarks>
///     Which parameters' headers a function may read is a summary per
///     function, computed for the whole module to a fixed point, as escape
///     summaries are; a callee not known here reads them.
/// </remarks>
public static class StackHeaders {
    /// <summary>For every function, which of its parameters' headers it may read.</summary>
    public static IReadOnlyDictionary<string, bool[]> Summarise(SilModule module) {
        var summaries = new Dictionary<string, bool[]>();
        foreach (var function in module.Functions) {
            summaries[function.Name] = function.Type.Parameters.Select(static p => !IsReference(p) && p is not ThickFunctionType).ToArray();
            if (function.Blocks.Count == 0) {
                Array.Fill(summaries[function.Name], true);
            }
        }

        var uses = new Dictionary<SilFunction, Uses>();
        for (var changed = true; changed;) {
            changed = false;
            foreach (var function in module.Functions) {
                if (function.Blocks.Count == 0) {
                    continue;
                }

                var summary = summaries[function.Name];
                for (var i = 0; i < summary.Length && i < function.Entry.Arguments.Count; i++) {
                    if (summary[i]) {
                        continue;
                    }

                    if (!uses.TryGetValue(function, out var functionUses)) {
                        uses[function] = functionUses = new Uses(function);
                    }

                    if (Follow(function.Entry.Arguments[i], functionUses, summaries).Reads) {
                        summary[i] = true;
                        changed = true;
                    }
                }
            }
        }

        return summaries;
    }

    /// <summary>The objects a function makes in its frame with no header, by what it knows of its callees.</summary>
    public static HeaderlessObjects Find(SilFunction function, Func<IReadOnlyDictionary<string, bool[]>> summarise) {
        if (function.Module is null) {
            return HeaderlessObjects.None;
        }

        var allocs = function.Blocks.SelectMany(static b => b.Instructions).OfType<AllocRefInst>().Where(static a => a.OnStack).ToList();
        if (allocs.Count == 0) {
            return HeaderlessObjects.None;
        }

        var summaries = summarise();
        var uses = new Uses(function);
        var flows = allocs.ToDictionary(a => a, a => Follow(a, uses, summaries));

        // A value or slot two objects may reach is left to the header.
        var owner = new Dictionary<SilValue, AllocRefInst>();
        var shared = new HashSet<AllocRefInst>();
        foreach (var (alloc, flow) in flows) {
            foreach (var holder in flow.Values.Concat(flow.Slots)) {
                if (owner.TryGetValue(holder, out var other)) {
                    shared.Add(alloc);
                    shared.Add(other);
                } else {
                    owner[holder] = alloc;
                }
            }
        }

        var result = new HeaderlessObjects();
        foreach (var (alloc, flow) in flows) {
            if (flow.Reads || shared.Contains(alloc)
                || alloc.ClassType.IsCoroutineFrame || alloc.ClassType.IsLaidOutAtRunTime
                || alloc.ClassType.IsGenericInstance && SilType.MentionsOpaque(alloc.ClassType)) {
                continue;
            }

            result.Objects[alloc] = flow.Lent;
            foreach (var value in flow.Values) {
                result.Holders[value] = alloc;
            }

            foreach (var destroy in flow.Destroys) {
                result.Destroys[destroy] = alloc;
            }
        }

        return result;
    }

    /// <summary>A reference's type: a class's, or an object of a class not known (a closure's context).</summary>
    static bool IsReference(SilType type) => type is ClassType || ReferenceEquals(type, SilType.Object);

    sealed class Flow {
        public readonly HashSet<SilValue> Values = [];
        public readonly HashSet<AllocStackInst> Slots = [];
        public readonly List<SilInstruction> Destroys = [];
        public bool Reads;

        /// <summary>A callee is handed the object: it needs its usual layout, though not its header.</summary>
        public bool Lent;
    }

    /// <summary>Where an object — made here, or a parameter — goes in the function, and whether anything there may read its header.</summary>
    static Flow Follow(SilValue root, Uses uses, IReadOnlyDictionary<string, bool[]> summaries) {
        var flow = new Flow();
        var pending = new Stack<SilValue>();
        var arguments = new List<SilArgument>();
        var stores = new List<StoreInst>();

        bool Callee(SilValue callee, int parameter) =>
            callee is FunctionRefInst reference && summaries.TryGetValue(reference.Function, out var summary)
            && parameter < summary.Length && !summary[parameter];

        void Push(SilValue value) {
            if (value is AllocStackInst slot ? flow.Slots.Add(slot) : flow.Values.Add(value)) {
                pending.Push(value);
            }
        }

        // An object's deinits are handed it as it goes.
        if (root is AllocRefInst made) {
            for (var level = made.ClassType; level is not null; level = level.Superclass) {
                if (level.HasGenericDeinit || level.Deinit is { } deinit && !(summaries.TryGetValue(deinit, out var summary) && summary is [false, ..])) {
                    flow.Reads = true;
                    return flow;
                }

                flow.Lent |= level.Deinit is not null;
            }
        }

        Push(root);
        while (pending.TryPop(out var current) && !flow.Reads) {
            if (current is AllocStackInst slot) {
                flow.Reads = !IsReference(slot.ElementType) && slot.ElementType is not ThickFunctionType || !Slot(slot);
                continue;
            }

            if (current is SilArgument argument && !ReferenceEquals(current, root)) {
                arguments.Add(argument);
            }

            foreach (var user in uses.Of(current)) {
                if (!Value(user, current)) {
                    flow.Reads = true;
                    break;
                }
            }
        }

        if (flow.Reads) {
            return flow;
        }

        // A block argument is only such an object when every branch to it brings one.
        foreach (var argument in arguments) {
            var index = argument.Block.Arguments.IndexOf(argument);
            foreach (var block in argument.Block.Function.Blocks) {
                if (block.Instructions.Count == 0 || !block.Successors.Contains(argument.Block)) {
                    continue;
                }

                switch (block.Instructions[^1]) {
                    case BranchInst branch when !flow.Values.Contains(branch.Operands[index]):
                    case CondBranchInst condition when ReferenceEquals(condition.WhenTrue, argument.Block) && !flow.Values.Contains(condition.TrueArguments.ElementAt(index)):
                    case CondBranchInst condition2 when ReferenceEquals(condition2.WhenFalse, argument.Block) && !flow.Values.Contains(condition2.FalseArguments.ElementAt(index)):
                    case not (BranchInst or CondBranchInst):
                        flow.Reads = true;
                        return flow;
                }
            }
        }

        // A slot holds such objects alone, and one is never stored over
        // another, whose counted release would read its header.
        foreach (var store in stores) {
            if (!flow.Values.Contains(store.Value)
                || store.Kind == StoreKind.Assign && store.Address is AllocStackInst { Unmanaged: false } slot && MayHold(slot, store)) {
                flow.Reads = true;
                return flow;
            }
        }

        return flow;

        bool Slot(AllocStackInst slot) {
            foreach (var user in uses.Of(slot)) {
                switch (user) {
                    case StoreInst store when ReferenceEquals(store.Address, slot) && !ReferenceEquals(store.Value, slot):
                        stores.Add(store);
                        break;

                    case LoadBorrowInst:
                    case LoadInst { Kind: LoadKind.Take }:
                        Push(user);
                        break;

                    case DestroyAddrInst when slot.Unmanaged:
                    case DestroyAddrInst { Uncounted: true }:
                        flow.Destroys.Add(user);
                        break;

                    case DeallocStackInst:
                        break;

                    default:
                        return false;
                }
            }

            return true;
        }

        bool Value(SilInstruction user, SilValue value) {
            switch (user) {
                case RefElementAddrInst element:
                    return element.Reference.Type is not ClassType { IsLaidOutAtRunTime: true };

                case EndBorrowInst:
                    return true;

                case DestroyValueInst { Uncounted: true }:
                    flow.Destroys.Add(user);
                    return true;

                case UpcastInst or OwnedUpcastInst or UncheckedRefCastInst:
                    Push(user);
                    return true;

                case StoreInst store when ReferenceEquals(store.Value, value) && store.Address is AllocStackInst slot:
                    Push(slot);
                    return true;

                case BranchInst branch:
                    for (var i = 0; i < branch.Operands.Count; i++) {
                        if (ReferenceEquals(branch.Operands[i], value)) {
                            Push(branch.Target.Arguments[i]);
                        }
                    }

                    return true;

                case CondBranchInst condition when !ReferenceEquals(condition.Condition, value): {
                    var trueArguments = condition.TrueArguments.ToList();
                    var falseArguments = condition.FalseArguments.ToList();
                    for (var i = 0; i < trueArguments.Count; i++) {
                        if (ReferenceEquals(trueArguments[i], value)) {
                            Push(condition.WhenTrue.Arguments[i]);
                        }
                    }

                    for (var i = 0; i < falseArguments.Count; i++) {
                        if (ReferenceEquals(falseArguments[i], value)) {
                            Push(condition.WhenFalse.Arguments[i]);
                        }
                    }

                    return true;
                }

                // A closure made with it as its context, whose code only reaches the context's fields.
                case PartialApplyInst closure when ReferenceEquals(closure.Context, value) && !ReferenceEquals(closure.Function, value):
                    if (closure.Function is not FunctionRefInst code || !Callee(code, ((FunctionType)code.Type).Parameters.Length - 1)) {
                        return false;
                    }

                    flow.Lent = true;
                    Push(closure);
                    return true;

                case ApplyInst apply:
                    return Call(apply.Callee, [..apply.Arguments], value);

                case TryApplyInst apply:
                    return Call(apply.Callee, [..apply.Arguments], value);

                default:
                    return false;
            }
        }

        // Calling a closure hands its context to the closure's code alone:
        // where the closure was made, that code was found to read no header.
        bool Call(SilValue callee, List<SilValue> arguments, SilValue value) {
            if (ReferenceEquals(callee, value) && value.Type is not ThickFunctionType) {
                return false;
            }

            for (var i = 0; i < arguments.Count; i++) {
                if (ReferenceEquals(arguments[i], value)) {
                    if (!Callee(callee, i)) {
                        return false;
                    }

                    flow.Lent = true;
                }
            }

            return true;
        }
    }

    /// <summary>Whether the slot may hold one of the objects where the store is: on some path from the function's entry, stored and not yet emptied.</summary>
    static bool MayHold(AllocStackInst slot, StoreInst at) {
        var function = at.Block.Function;
        var holdingOut = new Dictionary<SilBlock, bool>();
        bool Walk(SilBlock block, bool holding, SilInstruction? stop) {
            foreach (var instruction in block.Instructions) {
                if (ReferenceEquals(instruction, stop)) {
                    return holding;
                }

                switch (instruction) {
                    case StoreInst store when ReferenceEquals(store.Address, slot):
                        holding = true;
                        break;

                    case DestroyAddrInst destroy when ReferenceEquals(destroy.Address, slot):
                    case LoadInst { Kind: LoadKind.Take } load when ReferenceEquals(load.Address, slot):
                        holding = false;
                        break;
                }
            }

            return holding;
        }

        // To a fixed point: what each block ends with, from what may reach it.
        var predecessors = function.Blocks.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in function.Blocks) {
            foreach (var successor in block.Successors) {
                predecessors[successor].Add(block);
            }
        }

        bool Into(SilBlock block) => block != function.Entry && predecessors[block].Any(p => holdingOut.GetValueOrDefault(p));
        for (var changed = true; changed;) {
            changed = false;
            foreach (var block in function.Blocks) {
                var after = Walk(block, Into(block), null);
                if (after && !holdingOut.GetValueOrDefault(block)) {
                    holdingOut[block] = true;
                    changed = true;
                }
            }
        }

        return Walk(at.Block, Into(at.Block), at);
    }
}

/// <summary>
///     The objects a function makes in its frame with no header (<see cref="StackHeaders" />):
///     each with whether a callee is handed it, the values that are it, and its destroys.
/// </summary>
public sealed class HeaderlessObjects {
    public static HeaderlessObjects None { get; } = new();

    /// <summary>Each such object, and true when a callee is handed it, so that it keeps its usual layout.</summary>
    public Dictionary<AllocRefInst, bool> Objects { get; } = [];

    /// <summary>The values that are such an object, or a closure made with one as its context.</summary>
    public Dictionary<SilValue, AllocRefInst> Holders { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The uncounted destroys of such an object, of a slot holding one, or of a closure made with one.</summary>
    public Dictionary<SilInstruction, AllocRefInst> Destroys { get; } = new(ReferenceEqualityComparer.Instance);
}
