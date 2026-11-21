namespace Sable.SIL;

/// <summary>A transformation of SIL: of each function, or of the module as a whole.</summary>
public interface ISilPass {
    string Name { get; }

    void Run(SilFunction function);

    /// <summary>
    ///     Runs the pass over a module: each function in turn, unless the pass
    ///     looks across functions — the inliner, devirtualisation — and runs
    ///     over the module itself.
    /// </summary>
    void Run(SilModule module) {
        foreach (var function in module.Functions) {
            Run(function);
        }
    }
}

/// <summary>A pass left SIL the verifier rejects: a compiler bug, named by the pass.</summary>
public sealed class SilPassException(string message) : Exception(message);

/// <summary>
///     Runs passes over a module, verifying after every one (docs/04 §2.1: "A
///     SIL verifier runs after every pass in debug builds. Non-negotiable.").
/// </summary>
public static class SilPassManager {
    /// <summary>The passes every build runs, in order, but a debug one: what keeps ARC's traffic down.</summary>
    /// <remarks>
    ///     Copies are forwarded before loads are borrowed and after: each
    ///     uncovers more of the other.
    /// </remarks>
    public static IReadOnlyList<ISilPass> Default { get; } = [
        new PromoteSlots(), new ForwardCopies(), new BorrowLoads(), new ForwardCopies(), new BorrowFields(), new BorrowLocals(), new BorrowLets(), new BorrowWalks(),
        new PromoteToStack(), new HoistUniqueness(),
    ];

    /// <summary>The passes an optimised build runs, in order.</summary>
    /// <remarks>
    ///     Unspecialised generic code is first specialised where its types are
    ///     known (<see cref="Specialise" />), so that what follows sees it as
    ///     the specialised lowering writes it. Calls the vtable can only answer one way are made direct, and
    ///     those it answers with one of a few small overrides test for each
    ///     first; loads only borrowed for a call are borrowed before the call
    ///     is inlined, as afterwards the borrow would span the callee's
    ///     blocks; then small callees are inlined, then the '@autoclosure'
    ///     closures they call or drop (<see cref="InlineClosures" />), and the chain of blocks each call leaves
    ///     merged, so that the passes after see the callee's body in the
    ///     caller's; what is left is tidied last. An unoptimised build keeps
    ///     its calls, so that a trap's backtrace names every frame.
    /// </remarks>
    public static IReadOnlyList<ISilPass> Optimising { get; } = [
        new Specialise(), new Devirtualise(), new SpeculativeDevirtualise(), new BorrowLoads(), new Inliner(), new InlineClosures(), new Simplify(), ..Default, new VersionLoops(), new Simplify(),
    ];

    /// <summary>
    ///     The passes a build at a profile's <c>opt-level</c> runs (docs/05
    ///     §5.2.5): clang's level, <c>0</c> to <c>3</c>, <c>s</c> or <c>z</c>.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item><c>0</c>: <see cref="Default" />, the ARC passes alone; every call stays a call.</item>
    ///         <item>
    ///             <c>1</c>: those, after devirtualising and tidying the blocks;
    ///             nothing is inlined, so a backtrace still names every frame
    ///             SIL wrote, and nothing is duplicated.
    ///         </item>
    ///         <item><c>2</c> and <c>3</c>: <see cref="Optimising" />. What <c>3</c> adds is LLVM's.</item>
    ///         <item>
    ///             <c>s</c>: <see cref="Optimising" /> without loop versioning, which copies a loop for its fast
    ///             path, or speculative devirtualisation, which copies a call for each guess; generic code is
    ///             specialised only where it is small or called in a loop.
    ///         </item>
    ///         <item><c>z</c>: without the inliner too, which copies callees into every caller.</item>
    ///     </list>
    /// </remarks>
    public static IReadOnlyList<ISilPass> ForLevel(string optLevel) =>
        optLevel switch {
            "0" => Default,
            "1" => [new Devirtualise(), new Simplify(), ..Default],
            "2" or "3" => Optimising,
            "s" => [new Specialise(forSize: true), new Devirtualise(), new BorrowLoads(), new Inliner(), new InlineClosures(), new Simplify(), ..Default, new Simplify()],
            "z" => [new Devirtualise(), new Simplify(), ..Default, new Simplify()],
            _ => throw new ArgumentOutOfRangeException(nameof(optLevel), optLevel, "an opt-level is 0, 1, 2, 3, s or z"),
        };

    /// <summary>
    ///     Drops every function the entry point cannot reach: through calls
    ///     and function values, the globals' initialiser, the deinits and
    ///     vtables of the classes it makes, and every type's deinit.
    /// </summary>
    public static void RemoveUnreachable(SilModule module) {
        if (Reachable(module) is { } reached) {
            module.RemoveAll(f => !reached.Contains(f.Name));
        }
    }

    /// <summary>
    ///     The functions the entry point can reach, by name — or, in a
    ///     library, its exports; null for any other module with no entry point.
    /// </summary>
    public static HashSet<string>? Reachable(SilModule module) {
        if (module.EntryPoint is null && !module.IsLibrary) {
            return null;
        }

        var reached = new HashSet<string>(StringComparer.Ordinal);
        // What C calls is reached from outside.
        var pending = new Stack<string>(["$globals.init", "$globals.deinit", ..module.Exports.Keys]);
        if (module.EntryPoint is { } entry) {
            pending.Push(entry);
        }

        foreach (var type in module.Types) {
            switch (type) {
                case StructType { Deinit: { } deinit }:
                    pending.Push(deinit);
                    break;
                case ClassType @class:
                    if (@class.Deinit is { } classDeinit) {
                        pending.Push(classDeinit);
                    }

                    // A class's vtable is part of its metadata, which is
                    // emitted whether or not anything makes one (an
                    // existential's box among them), so what it names stays.
                    foreach (var (_, method) in @class.VTable) {
                        pending.Push(method);
                    }

                    break;
            }
        }

        // A witness table's witnesses are reached with its conformer's
        // metadata: a type made, boxed in an existential, or asked about.
        var tables = module.WitnessTables.ToLookup(static t => t.Conformer.ToString(), StringComparer.Ordinal);
        var unspecialised = module.Functions.Any(static f => f.IsUnspecialised);
        var tabled = new HashSet<string>(StringComparer.Ordinal);
        void Witnesses(SilType? type) {
            for (; type is not null && tabled.Add(type.ToString()); type = (type as ClassType)?.Superclass) {
                foreach (var table in tables[type.ToString()]) {
                    foreach (var entry in table.Entries) {
                        pending.Push(entry.Function);
                    }

                    foreach (var entry in table.GenericEntries) {
                        if (entry.Function is { } witness) {
                            pending.Push(witness);
                        }
                    }

                    // Its associated types' metadata, which unspecialised code
                    // reads from it and finds their own tables by.
                    if (unspecialised) {
                        foreach (var (_, associated) in table.AssociatedTypes) {
                            Witnesses(associated);
                        }
                    }
                }
            }
        }

        var made = new HashSet<ClassType>();
        var describing = new HashSet<bool>();
        var debugDescribing = new HashSet<bool>();
        var erasing = new HashSet<bool>();
        while (pending.Count > 0) {
            var name = pending.Pop();
            if (!reached.Add(name) || module.Find(name) is not { } function) {
                continue;
            }

            foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
                switch (instruction) {
                    case FunctionRefInst reference:
                        pending.Push(reference.Function);
                        break;

                    // A class made, or called through: every method its vtable, or a subclass's, may dispatch to.
                    case AllocRefInst alloc:
                        for (var type = alloc.ClassType; type is not null; type = type.Superclass) {
                            if (made.Add(type)) {
                                foreach (var (_, method) in type.VTable) {
                                    pending.Push(method);
                                }
                            }
                        }

                        foreach (var (_, method) in alloc.ClassType.VTable) {
                            pending.Push(method);
                        }

                        Witnesses(alloc.ClassType);
                        if (alloc.ClassType.TypeArguments is [var held] && alloc.ClassType.Name.StartsWith("any$box$of$", StringComparison.Ordinal)) {
                            Witnesses(held);
                        }

                        break;

                    case MetadataInst metadata:
                        Witnesses(metadata.Described);
                        break;

                    // A boxed value written out: whatever type it is, its describer.
                    case BuiltinInst { Name: "generic_describe" } when describing.Add(true):
                        foreach (var (_, (_, describer)) in module.Describers) {
                            pending.Push(describer);
                        }

                        break;

                    case BuiltinInst { Name: "generic_debug_describe" } when debugDescribing.Add(true):
                        foreach (var (_, (_, describer)) in module.DebugDescribers) {
                            pending.Push(describer);
                        }

                        break;

                    case BuiltinInst { Name: "opaque_erase_error" } when erasing.Add(true):
                        foreach (var (_, (_, eraser)) in module.ErrorErasers) {
                            pending.Push(eraser);
                        }

                        break;

                    case ClassMethodInst dispatch:
                        foreach (var type in module.Types.OfType<ClassType>()) {
                            if (type.VTable.FirstOrDefault(m => m.Method == dispatch.Method) is { Function: { } target }) {
                                pending.Push(target);
                            }
                        }

                        break;
                }
            }
        }

        return reached;
    }

    public static void Run(SilModule module, IEnumerable<ISilPass> passes) {
        // A build without dynamic exclusivity checks loses them before the
        // passes, which then see the code as it runs.
        if (!module.CheckExclusivity) {
            SharedStorage.DropChecks(module);
        }

        foreach (var pass in passes) {
            pass.Run(module);

            var problems = SilVerifier.Verify(module);
            if (problems.Count > 0) {
                // With SABLE_PASS_DUMP set, the functions the problems name, as the pass left them.
                var dump = Environment.GetEnvironmentVariable("SABLE_PASS_DUMP") is null
                    ? ""
                    : "\n\n" + string.Join("\n", module.Functions
                        .Where(f => problems.Any(p => p.StartsWith("@" + f.Name + " ", StringComparison.Ordinal)))
                        .Select(SilPrinter.Print));
                throw new SilPassException(
                    $"SIL failed verification after the '{pass.Name}' pass:\n{string.Join("\n", problems)}{dump}"
                );
            }
        }
    }
}

/// <summary>
///     Promotes stack slots to SSA values: SIL's mem2reg.
/// </summary>
/// <remarks>
///     <para>
///         A slot whose only uses are <c>load</c>, <c>store</c> and its
///         <c>dealloc_stack</c> holds nothing anyone else can see, so it can be
///         a value instead. Each load becomes the value last stored on the way
///         to it; where paths join and the slot is read afterwards, the value
///         becomes a block argument — pruned SSA, so no argument is added
///         where nothing reads it. Edges that cannot take new arguments, from a
///         <c>cond_br</c>, <c>switch_enum</c> or <c>try_apply</c>, are given a
///         block of their own first.
///     </para>
///     <para>
///         The lowering keeps every local in a slot, which is simple and
///         obviously right; this is what makes it cheap. A slot that would need
///         a value on some path where none was ever stored is left alone, rather
///         than given an invented one.
///     </para>
/// </remarks>
public sealed class PromoteSlots : ISilPass {
    public string Name => "promote-slots";

    public void Run(SilFunction function) {
        var candidates = Promotable(function);
        while (candidates.Count > 0) {
            var refused = TryPromote(function, candidates);
            if (refused is null) {
                return;
            }

            // A slot needing a value from nowhere: leave it in memory and
            // promote the rest.
            candidates.Remove(refused);
        }
    }

    static List<AllocStackInst> Promotable(SilFunction function) {
        var slots = new List<AllocStackInst>();
        var disqualified = new HashSet<AllocStackInst>();

        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                // A non-trivial slot's storage is what holds its value alive,
                // and starts empty: it stays in memory.
                if (instruction is AllocStackInst slot) {
                    if (slot.ElementType.IsTrivial) {
                        slots.Add(slot);
                    }

                    continue;
                }

                for (var i = 0; i < instruction.Operands.Count; i++) {
                    if (instruction.Operands[i] is not AllocStackInst used) {
                        continue;
                    }

                    var fine = instruction switch {
                        LoadInst => true,
                        StoreInst => i == 1, // stored to, not stored somewhere
                        DeallocStackInst => true,
                        _ => false,
                    };

                    if (!fine) {
                        disqualified.Add(used);
                    }
                }
            }
        }

        return [..slots.Where(s => !disqualified.Contains(s))];
    }

    /// <summary>Promotes the slots, or returns one that cannot be, having changed nothing.</summary>
    static AllocStackInst? TryPromote(SilFunction function, List<AllocStackInst> slots) {
        var reachable = SilVerifier.Dominators(function);
        var blocks = function.Blocks.Where(reachable.ContainsKey).ToList();
        var predecessors = Predecessors(blocks);

        // Liveness: a slot is live into a block when some path from its start
        // reads the slot before writing it.
        var liveIn = new Dictionary<AllocStackInst, HashSet<SilBlock>>();
        foreach (var slot in slots) {
            liveIn[slot] = LiveIn(slot, blocks, predecessors);
        }

        // Where values meet: a block with several predecessors that the slot
        // is live into needs an argument. (Iterating over the dominance
        // frontier would place fewer; with pruning by liveness, this is
        // correct and simple, and a later pass can drop arguments that only
        // ever receive one value.)
        var arguments = new Dictionary<(SilBlock, AllocStackInst), int>();
        foreach (var slot in slots) {
            foreach (var block in blocks) {
                if (block != function.Entry && predecessors[block].Count > 1 && liveIn[slot].Contains(block)) {
                    arguments[(block, slot)] = 0;
                }
            }
        }

        // Check, before changing anything, that every value can be found.
        foreach (var slot in slots) {
            if (!Defined(slot, function, blocks, predecessors, liveIn)) {
                return slot;
            }
        }

        Rewrite(function, slots, blocks, predecessors, arguments);
        return null;
    }

    static Dictionary<SilBlock, List<SilBlock>> Predecessors(List<SilBlock> blocks) {
        var predecessors = blocks.ToDictionary(static b => b, static _ => new List<SilBlock>());
        foreach (var block in blocks) {
            foreach (var successor in block.Successors) {
                if (predecessors.TryGetValue(successor, out var list)) {
                    list.Add(block);
                }
            }
        }

        return predecessors;
    }

    static HashSet<SilBlock> LiveIn(
        AllocStackInst slot,
        List<SilBlock> blocks,
        Dictionary<SilBlock, List<SilBlock>> predecessors
    ) {
        // Per block: does it read before writing, and does it write at all?
        var readsFirst = new HashSet<SilBlock>();
        var writes = new HashSet<SilBlock>();
        foreach (var block in blocks) {
            foreach (var instruction in block.Instructions) {
                if (instruction is LoadInst load && load.Address == slot) {
                    if (!writes.Contains(block)) {
                        readsFirst.Add(block);
                    }
                } else if (instruction is StoreInst store && store.Address == slot) {
                    writes.Add(block);
                }
            }
        }

        var live = new HashSet<SilBlock>(readsFirst);
        var pending = new Queue<SilBlock>(readsFirst);
        while (pending.Count > 0) {
            foreach (var predecessor in predecessors[pending.Dequeue()]) {
                if (!writes.Contains(predecessor) && live.Add(predecessor)) {
                    pending.Enqueue(predecessor);
                }
            }
        }

        return live;
    }

    /// <summary>
    ///     True when every read of the slot will find a value: walking forward
    ///     from the entry, a block whose predecessors disagree takes an
    ///     argument, and one where no value has arrived must not read.
    /// </summary>
    static bool Defined(
        AllocStackInst slot,
        SilFunction function,
        List<SilBlock> blocks,
        Dictionary<SilBlock, List<SilBlock>> predecessors,
        Dictionary<AllocStackInst, HashSet<SilBlock>> liveIn
    ) {
        // A forward "may be undefined" analysis: it starts out defined
        // everywhere and grows from the entry, so a loop's back edge does not
        // hold its header undefined for ever.
        var undefinedOut = blocks.ToDictionary(static b => b, static _ => false);
        var changed = true;
        while (changed) {
            changed = false;
            foreach (var block in blocks) {
                var undefined = block == function.Entry || predecessors[block].Any(p => undefinedOut[p]);

                foreach (var instruction in block.Instructions) {
                    if (instruction is StoreInst store && store.Address == slot) {
                        undefined = false;
                    }
                }

                if (undefinedOut[block] != undefined) {
                    undefinedOut[block] = undefined;
                    changed = true;
                }
            }
        }

        // A block the slot is live into must not be reachable without a value.
        foreach (var block in liveIn[slot]) {
            if (block == function.Entry || predecessors[block].Any(p => undefinedOut[p])) {
                return false;
            }
        }

        return true;
    }

    static void Rewrite(
        SilFunction function,
        List<AllocStackInst> slots,
        List<SilBlock> blocks,
        Dictionary<SilBlock, List<SilBlock>> predecessors,
        Dictionary<(SilBlock, AllocStackInst), int> arguments
    ) {
        // Blocks that take new arguments must only be branched to by 'br',
        // which can carry them; give any other edge a block of its own.
        var takingArguments = arguments.Keys.Select(static k => k.Item1).ToHashSet();
        foreach (var target in takingArguments) {
            foreach (var predecessor in predecessors[target].Distinct().ToList()) {
                if (predecessor.Terminator is BranchInst) {
                    continue;
                }

                SplitEdges(function, predecessor, target);
            }
        }


        // The new arguments, in slot order.
        var argumentOf = new Dictionary<(SilBlock, AllocStackInst), SilArgument>();
        foreach (var slot in slots) {
            foreach (var target in takingArguments) {
                if (arguments.ContainsKey((target, slot))) {
                    argumentOf[(target, slot)] = target.AddArgument(slot.ElementType);
                }
            }
        }

        // Renaming, block by block in dominator-tree order: each block starts
        // with its own argument, or its immediate dominator's value.
        var dominators = SilVerifier.Dominators(function);
        var order = function.Blocks.Where(dominators.ContainsKey)
            .OrderBy(b => dominators[b].Count)
            .ToList();

        var valueOut = new Dictionary<(SilBlock, AllocStackInst), SilValue>();
        var replace = new Dictionary<SilValue, SilValue>();

        SilValue? ValueIn(SilBlock block, AllocStackInst slot) {
            if (argumentOf.TryGetValue((block, slot), out var argument)) {
                return argument;
            }

            // The value flowing in is what its immediate dominator ended with.
            var immediate = dominators[block]
                .Where(d => d != block)
                .OrderByDescending(d => dominators[d].Count)
                .FirstOrDefault();
            return immediate is null ? null : valueOut.GetValueOrDefault((immediate, slot));
        }

        foreach (var block in order) {
            var current = slots.ToDictionary(static s => s, s => ValueIn(block, s));

            foreach (var instruction in block.Instructions.ToList()) {
                switch (instruction) {
                    case LoadInst load when load.Address is AllocStackInst slot && current.ContainsKey(slot):
                        replace[load] = current[slot] ?? throw new InvalidOperationException("a promoted load with no value");
                        block.Instructions.Remove(load);
                        break;

                    case StoreInst store when store.Address is AllocStackInst slot && current.ContainsKey(slot):
                        current[slot] = Resolve(store.Value, replace);
                        block.Instructions.Remove(store);
                        break;

                    case DeallocStackInst dealloc when dealloc.Address is AllocStackInst slot && current.ContainsKey(slot):
                        block.Instructions.Remove(dealloc);
                        break;

                    case AllocStackInst slot when current.ContainsKey(slot):
                        block.Instructions.Remove(slot);
                        break;
                }
            }

            foreach (var slot in slots) {
                if (current[slot] is { } value) {
                    valueOut[(block, slot)] = value;
                }
            }
        }

        // Each branch into a block with new arguments passes the slots' values.
        foreach (var target in takingArguments) {
            var passed = slots.Where(s => argumentOf.ContainsKey((target, s))).ToList();
            foreach (var predecessor in order.Where(b => b.Successors.Contains(target)).ToList()) {
                var branch = (BranchInst)predecessor.Terminator!;
                var extra = passed.Select(s => valueOut.GetValueOrDefault((predecessor, s))
                    ?? throw new InvalidOperationException("a branch with no value to pass")).ToArray();
                var replacement = new BranchInst(target, [..branch.Operands, ..extra]) { Location = branch.Location, Block = predecessor };
                predecessor.Instructions[^1] = replacement;
            }
        }

        // Every remaining use of a promoted load now uses its value.
        foreach (var block in function.Blocks) {
            foreach (var instruction in block.Instructions) {
                for (var i = 0; i < instruction.Operands.Count; i++) {
                    if (replace.ContainsKey(instruction.Operands[i])) {
                        instruction.SetOperand(i, Resolve(instruction.Operands[i], replace));
                    }
                }
            }
        }
    }

    static SilValue Resolve(SilValue value, Dictionary<SilValue, SilValue> replace) {
        while (replace.TryGetValue(value, out var next)) {
            value = next;
        }

        return value;
    }

    /// <summary>
    ///     Routes every edge from <paramref name="from" /> to <paramref name="to" />
    ///     through a new block that takes what the edge passed and branches on.
    /// </summary>
    static void SplitEdges(SilFunction function, SilBlock from, SilBlock to) {
        SilBlock NewEdge() {
            var edge = function.AddBlock();
            var passed = to.Arguments.Select(a => edge.AddArgument(a.Type, a.IsGuaranteed)).ToArray<SilValue>();
            edge.Append(new BranchInst(to, passed) { Location = from.Terminator?.Location });
            return edge;
        }

        switch (from.Terminator) {
            case CondBranchInst conditional: {
                var whenTrue = conditional.WhenTrue == to ? NewEdge() : conditional.WhenTrue;
                var whenFalse = conditional.WhenFalse == to ? NewEdge() : conditional.WhenFalse;
                var replacement = new CondBranchInst(
                    conditional.Condition,
                    whenTrue,
                    [..conditional.TrueArguments],
                    whenFalse,
                    [..conditional.FalseArguments]
                ) { Location = conditional.Location, Block = from };
                from.Instructions[^1] = replacement;
                return;
            }

            case SwitchEnumInst switchEnum:
                for (var i = 0; i < switchEnum.Cases.Count; i++) {
                    if (switchEnum.Cases[i].Target == to) {
                        switchEnum.Cases[i] = (switchEnum.Cases[i].Case, NewEdge());
                    }
                }

                if (switchEnum.Default == to) {
                    switchEnum.Default = NewEdge();
                }

                return;

            case TryApplyInst tryApply:
                if (tryApply.Normal == to) {
                    tryApply.Normal = NewEdge();
                }

                if (tryApply.Error == to) {
                    tryApply.Error = NewEdge();
                }

                return;
        }
    }
}
