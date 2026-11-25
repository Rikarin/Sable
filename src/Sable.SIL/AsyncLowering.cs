using System.Collections.Immutable;

namespace Sable.SIL;

/// <summary>
///     Async functions as state machines (#276): the coroutine split, made
///     once every pass has run, so that the passes see an async function and
///     its calls as any other — and inline, simplify and borrow through them.
/// </summary>
/// <remarks>
///     <para>
///         A function that may suspend becomes two. Its <i>start</i> function,
///         under its own name, takes its parameters, makes its frame on the
///         running task's frame slab — never the heap — and returns it,
///         running nothing. Its <i>resume</i> function runs the body from
///         where the frame's state says it stopped until it finishes or must
///         wait, and says which: <c>true</c> when it has finished, its result
///         (or error) in the frame. Every frame begins as its kind's head
///         does: the resume function, the state, the result, the error — so
///         a caller that knows only the signature, through a closure or a
///         vtable, starts and polls any of them alike.
///     </para>
///     <para>
///         A call to one starts the callee's frame and polls it there and
///         then: an await that need not wait costs a bump of the slab and a
///         call. When the callee must wait, so must the caller: it records
///         where it is and returns <c>false</c>, and so on up to the task,
///         which the executor resumes once it can go on — polling the chain
///         again from its root. The places a task itself waits are the
///         runtime's: <c>async_wait</c> (a task not yet finished),
///         <c>async_group_wait</c> (a group's next child to finish),
///         <c>async_hop</c> (an actor to be free, or the main thread),
///         <c>async_yield</c>, <c>async_sleep</c>, <c>async_park</c> (a
///         wait list to be woken), <c>async_park_cancellable</c> (one to be
///         woken, unless woken since a ticket, or the task cancelled), <c>async_io_wait</c> (a socket to be
///         ready, which the reactor says) and <c>async_blocking</c> (work
///         run on the blocking pool), each of which says whether the task must
///         suspend, having noted what it waits for: the executor puts it
///         where it will be woken once it has.
///     </para>
///     <para>
///         A task that can never go on — still waiting as the program ends,
///         on something no task will give — is dropped: each frame has a
///         <i>drop</i> function too, which destroys what the frame holds
///         where it stopped (the callee frame it waits in first, then the
///         owned values live there, then its slots), and the runtime calls
///         the root's.
///     </para>
///     <para>
///         As a generator's body (<see cref="CoroutineSplit" />), every slot the
///         body makes is a field of its frame. So is every value live across
///         a point where it may suspend: stored there as it is made and read
///         back where it is used — as its bits, since its ownership is the
///         body's, as it was. The frame is given back by its caller once its
///         result is read; nothing in it is destroyed with it.
///     </para>
///     <para>
///         An async function that cannot reach a point where it may suspend
///         — it calls nothing that may — stays an ordinary function, its
///         calls ordinary calls, where every caller is in view: in a program
///         compiled whole, for a function that is only called by name.
///     </para>
/// </remarks>
public static class AsyncLowering {
    /// <summary>The state a frame is in once its function has returned, and once it has thrown.</summary>
    internal const long Finished = -1, Threw = -2;

    const int ResumeField = 0, DropField = 1, StateField = 2;

    /// <summary>What a resume function is: given its frame, it says whether its function has finished.</summary>
    internal static readonly FunctionType ResumeType = new([SilType.Object], SilType.Bool);

    /// <summary>What a drop function is: given a frame suspended part-way, it destroys what the frame holds.</summary>
    internal static readonly FunctionType DropType = new([SilType.Object], SilType.Unit);

    /// <summary>Makes the module's async functions coroutines, once.</summary>
    public static void Run(SilModule module) {
        if (module.AsyncLowered) {
            return;
        }

        module.AsyncLowered = true;
        ShareStores.Run(module);
        new Lowering(module).Run();
    }

    /// <summary>The name of an async function's resume function.</summary>
    public static string ResumeName(string function) => function + "$resume";

    /// <summary>The name of an async function's drop function.</summary>
    public static string DropName(string function) => function + "$drop";

    sealed class Lowering(SilModule module) {
        readonly Dictionary<string, ClassType> _heads = new(StringComparer.Ordinal);

        /// <summary>The resume functions made: their calls are already polls.</summary>
        readonly HashSet<SilFunction> _resumes = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        ///     The class every frame of functions with this result and error
        ///     starts as: the resume function, the state, the result and the error.
        /// </summary>
        ClassType Head(SilType result, SilType? thrown) {
            var name = $"AsyncHead$of${SilGen.Sanitise(result.ToString())}" + (thrown is null ? "" : $"$throws${SilGen.Sanitise(thrown.ToString())}");
            if (_heads.TryGetValue(name, out var head)) {
                return head;
            }

            head = new ClassType(name) { IsCoroutineFrame = true };
            head.Fields.Add(("$resume", ResumeType));
            head.Fields.Add(("$drop", DropType));
            head.Fields.Add(("$state", SilType.Int));
            if (result != SilType.Unit) {
                head.Fields.Add(("$result", result));
            }

            if (thrown is not null) {
                head.Fields.Add(("$error", thrown));
            }

            // In the module's types, so that its printed SIL reads back.
            return _heads[name] = module.AddType(head);
        }

        /// <summary>An async function's start: its parameters in, its frame out.</summary>
        FunctionType StartType(FunctionType type) =>
            new(type.Parameters, Head(type.Result, type.Thrown), null, type.Owned.IsEmpty ? default : type.Owned);

        static bool IsAsyncCallee(SilValue callee) =>
            callee.Type is FunctionType { IsAsync: true } or ThickFunctionType { Signature.IsAsync: true };

        static FunctionType Signature(SilValue callee) =>
            callee.Type as FunctionType ?? ((ThickFunctionType)callee.Type).Signature;

        static bool IsWait(SilInstruction instruction) =>
            instruction is BuiltinInst { Name: "task_yield" or "task_sleep" or "task_wait" or "task_group_wait" or "task_hop" or "task_park" or "task_park_cancellable" or "task_io_wait" or "task_blocking" };

        static bool IsSuspension(SilInstruction instruction) =>
            IsWait(instruction) || instruction is ApplyInst apply && IsAsyncCallee(apply.Callee) || instruction is TryApplyInst tryApply && IsAsyncCallee(tryApply.Callee);

        public void Run() {
            Demote();

            var asyncFunctions = module.Functions.Where(static f => f.Type.IsAsync && f.Blocks.Count > 0).ToList();
            var convention = new HashSet<string>(asyncFunctions.Select(static f => f.Name), StringComparer.Ordinal);
            if (module.Unit is { } unit) {
                foreach (var (name, type) in unit.External.ToList()) {
                    if (type.IsAsync) {
                        convention.Add(name);
                        unit.External[name] = StartType(type);
                    }
                }
            }

            // An async entry point: run as the program's first task, by an
            // entry that polls it, letting the other tasks run while it waits.
            if (module.EntryPoint is { } entryName && module.Find(entryName) is { Type.IsAsync: true } entry) {
                Enter(entry);
            }

            foreach (var function in asyncFunctions) {
                Split(function);
            }

            // Every call to a function that waits — in an ordinary function,
            // one that cannot (a generator's frame's deinit leaving its body)
            // — polls it once, there and then.
            foreach (var function in module.Functions.ToList()) {
                if (!function.Type.IsAsync && !_resumes.Contains(function)) {
                    Block(function);
                }
            }

            // A task is given its body's start and resume functions: the
            // executor makes the root frame when the task first runs, and
            // polls it each time the task is resumed.
            foreach (var block in module.Functions.SelectMany(static f => f.Blocks)) {
                for (var i = 0; i < block.Instructions.Count; i++) {
                    if (block.Instructions[i] is BuiltinInst { Name: "task_spawn", Operands: [FunctionRefInst run, var context, var priority, var detached] } spawn) {
                        var resume = new FunctionRefInst(ResumeName(run.Function), ResumeType) { Block = block, Location = spawn.Location };
                        block.Instructions.Insert(i++, resume);
                        var drop = new FunctionRefInst(DropName(run.Function), DropType) { Block = block, Location = spawn.Location };
                        block.Instructions.Insert(i++, drop);
                        var started = new BuiltinInst("task_spawn", SilType.Int, run, resume, drop, context, priority, detached) { Block = block, Location = spawn.Location };
                        block.Instructions[i] = started;
                        ReplaceUses(block.Function, spawn, started);
                    }
                }
            }

            // Every reference to such a function is to its start.
            foreach (var instruction in module.Functions.SelectMany(static f => f.Blocks).SelectMany(static b => b.Instructions)) {
                switch (instruction) {
                    case FunctionRefInst reference when convention.Contains(reference.Function) && reference.Type is FunctionType { IsAsync: true } type:
                        reference.Retype(StartType(type));
                        break;
                    case ClassMethodInst method when method.Type is FunctionType { IsAsync: true } type:
                        method.Retype(StartType(type));
                        break;
                }
            }
        }

        // ---- Demotion ------------------------------------------------------

        /// <summary>
        ///     Makes ordinary every async function that cannot suspend and that
        ///     only calls by name reach — where every call is in view, in a
        ///     program compiled whole.
        /// </summary>
        void Demote() {
            if (module.Unit is not null || module.IsLibrary) {
                return;
            }

            var candidates = module.Functions.Where(static f => f.Type.IsAsync && f.Blocks.Count > 0).ToDictionary(static f => f.Name, StringComparer.Ordinal);
            if (candidates.Count == 0) {
                return;
            }

            // Reached otherwise than by name: through a value, a vtable, C.
            var escapes = new HashSet<string>(module.Exports.Keys, StringComparer.Ordinal);
            foreach (var type in module.Types.OfType<ClassType>()) {
                escapes.UnionWith(type.VTable.Select(static m => m.Function));
            }

            // A witness, called through its table's entry by code that knows only the entry's type (#299).
            foreach (var table in module.WitnessTables) {
                escapes.UnionWith(table.Entries.Select(static e => e.Function).OfType<string>().Concat(table.Accessors));
            }

            foreach (var instruction in module.Functions.SelectMany(static f => f.Blocks).SelectMany(static b => b.Instructions)) {
                for (var i = 0; i < instruction.Operands.Count; i++) {
                    if (instruction.Operands[i] is FunctionRefInst reference
                        && !(i == 0 && instruction is ApplyInst or TryApplyInst)) {
                        escapes.Add(reference.Function);
                    }
                }
            }

            // What may suspend: a wait of its own, a call that may — through a
            // value, or to a function that may — until nothing more is found.
            var suspends = new HashSet<string>(StringComparer.Ordinal);
            for (var changed = true; changed;) {
                changed = false;
                foreach (var function in candidates.Values) {
                    if (suspends.Contains(function.Name)) {
                        continue;
                    }

                    var may = function.Blocks.SelectMany(static b => b.Instructions).Any(instruction => {
                        if (IsWait(instruction)) {
                            return true;
                        }

                        var callee = instruction switch {
                            ApplyInst apply => apply.Callee,
                            TryApplyInst tryApply => tryApply.Callee,
                            _ => null,
                        };
                        return callee is not null && IsAsyncCallee(callee)
                            && (callee is not FunctionRefInst reference || !candidates.ContainsKey(reference.Function)
                                || escapes.Contains(reference.Function) || suspends.Contains(reference.Function));
                    });
                    if (may || escapes.Contains(function.Name)) {
                        changed |= suspends.Add(function.Name);
                    }
                }
            }

            var demoted = candidates.Values.Where(f => !suspends.Contains(f.Name)).ToDictionary(static f => f.Name, StringComparer.Ordinal);
            foreach (var function in demoted.Values) {
                function.Type = function.Type.WithAsync(false);
            }

            foreach (var instruction in module.Functions.SelectMany(static f => f.Blocks).SelectMany(static b => b.Instructions)) {
                if (instruction is FunctionRefInst reference && demoted.TryGetValue(reference.Function, out var function)) {
                    reference.Retype(function.Type);
                }
            }
        }

        // ---- The entry point -------------------------------------------------

        /// <summary>
        ///     An async entry point: an ordinary entry made for it, which starts
        ///     it, polls it while the other tasks run, and, once every task has
        ///     finished, destroys the globals — which the entry point did.
        /// </summary>
        void Enter(SilFunction main) {
            var drained = false;
            string? globalsDeinit = null;
            foreach (var block in main.Blocks) {
                foreach (var instruction in block.Instructions.ToList()) {
                    if (instruction is BuiltinInst { Name: "task_drain" }) {
                        drained = true;
                        block.Instructions.Remove(instruction);
                    } else if (instruction is ApplyInst { Callee: FunctionRefInst { Function: "$globals.deinit" } reference } apply) {
                        globalsDeinit = reference.Function;
                        block.Instructions.Remove(apply);
                        if (block.Instructions.All(i => !i.Operands.Contains(reference))) {
                            block.Instructions.Remove(reference);
                        }
                    }
                }
            }

            var head = Head(main.Type.Result, main.Type.Thrown);
            var entry = new SilFunction(main.Name + "$entry", new FunctionType([], main.Type.Result)) { SourceFile = main.SourceFile, Line = main.Line };
            var start = entry.AddBlock();
            var poll = entry.AddBlock();
            var wait = entry.AddBlock();
            var done = entry.AddBlock();
            var frame = start.Append(new ApplyInst(start.Append(new FunctionRefInst(main.Name, StartType(main.Type))), head));
            start.Append(new BranchInst(poll));
            var resume = poll.Append(new LoadInst(poll.Append(new RefElementAddrInst(frame, ResumeField)), LoadKind.Trivial));
            var finished = poll.Append(new ApplyInst(resume, SilType.Bool, frame));
            poll.Append(new CondBranchInst(finished, done, [], wait, []));
            wait.Append(new BuiltinInst("task_run_until_main", SilType.Unit));
            wait.Append(new BranchInst(poll));
            SilValue? result = main.Type.Result == SilType.Unit
                ? null
                : done.Append(new LoadInst(done.Append(new RefElementAddrInst(frame, head.FieldIndex("$result"))), LoadKind.Trivial));
            done.Append(new BuiltinInst("async_frame_free", SilType.Unit, frame));
            if (drained) {
                done.Append(new BuiltinInst("task_drain", SilType.Unit));
            }

            if (globalsDeinit is not null) {
                done.Append(new ApplyInst(done.Append(new FunctionRefInst(globalsDeinit, new FunctionType([], SilType.Unit))), SilType.Unit));
            }

            done.Append(new ReturnInst(result));
            module.Add(entry);
            module.EntryPoint = entry.Name;
            module.Unit?.Owned.Add(entry.Name);
        }

        // ---- The split -----------------------------------------------------

        /// <summary>A place the body may suspend at: where it records its state, and where it goes on from when resumed.</summary>
        /// <param name="Owned">The owned values live where it suspends, which a drop destroys.</param>
        /// <param name="Callee">For a call, the callee's frame, which a drop drops first.</param>
        sealed record Suspension(SilBlock Suspend, SilBlock Resume, List<SilValue> Owned, SilValue? Callee);

        /// <summary>For the function being split, the owned values live at each place it may suspend.</summary>
        Dictionary<SilInstruction, List<SilValue>> _ownedAt = new(ReferenceEqualityComparer.Instance);

        void Split(SilFunction function) {
            var unspecialised = function.IsUnspecialised || function.AssociatedMetadata.Count > 0;
            Reborrow(function);

            _ownedAt = OwnedAtSuspensions(function);
            var name = function.Name;
            var original = function.Type;
            var head = Head(original.Result, original.Thrown);
            var frame = module.AddType(new ClassType(name + "$frame") { Superclass = head, IsCoroutineFrame = true });
            frame.Fields.AddRange(head.Fields);

            // The body is the resume function's; the start takes the name.
            module.Rename(function, ResumeName(name));
            _resumes.Add(function);
            function.Type = ResumeType;
            var parameterNames = function.ParameterNames.ToList();
            function.ParameterNames.Clear();
            function.ParameterNames.Add("$frame");
            if (module.Unit is { } unit && unit.Owned.Contains(name)) {
                unit.Owned.Add(function.Name);
            }

            var body = function.Entry;
            var at = body.Instructions.FirstOrDefault()?.Location;
            var entry = new SilBlock(function);
            function.Blocks.Insert(0, entry);
            var self = entry.Append(new UncheckedRefCastInst(entry.AddArgument(SilType.Object), frame));

            SilInstruction Field(string fieldName, SilType type) {
                var index = frame.Fields.Count;
                frame.Fields.Add((fieldName, type));
                return new RefElementAddrInst(self, index) { Location = at };
            }

            // The storage of a value of a type parameter's type (#297): a
            // buffer sized at run time after the frame's fixed part, whose
            // address the field holds, read in every state.
            SilInstruction Tail(string fieldName, SilType type) {
                var field = entry.Append(Field(fieldName, new AddressType(type)));
                frame.TailSlots.Add(frame.Fields.Count - 1);
                return entry.Append(new LoadInst(field, LoadKind.Trivial) { Location = at });
            }

            // The parameters, in the frame from the start: read in every state.
            // One of a type parameter's type is where its caller holds it, for
            // the whole call: the frame keeps its address. A shorthand one, '$0', is no field's name.
            var parameters = body.Arguments.ToList();
            var parameterFields = new List<int>();
            for (var i = 0; i < parameters.Count; i++) {
                var indirect = SilType.IsIndirect(parameters[i].Type);
                var field = entry.Append(Field(i < parameterNames.Count && parameterNames[i] is not ['$', >= '0' and <= '9', ..] ? parameterNames[i] : $"$parameter{i}", indirect ? new AddressType(parameters[i].Type) : parameters[i].Type));
                parameterFields.Add(frame.Fields.Count - 1);
                SilValue value = entry.Append(new LoadInst(field, LoadKind.Trivial) { Location = at });
                if (indirect) {
                    value = entry.Append(new LoadBorrowInst(value) { Location = at });
                }

                ReplaceUses(function, parameters[i], value);
            }

            body.Arguments.Clear();

            // Every slot, a field. One that holds without owning
            // (BorrowFields) is written as bits and never destroyed, as the
            // code generator treats such a slot.
            var slots = function.Blocks.SelectMany(static b => b.Instructions).OfType<AllocStackInst>().ToList();
            var unmanaged = slots.Where(static s => s.Unmanaged).ToHashSet();
            if (unmanaged.Count > 0) {
                foreach (var block in function.Blocks) {
                    block.Instructions.RemoveAll(i => i is DestroyAddrInst { Address: AllocStackInst slot } && unmanaged.Contains(slot));
                    for (var i = 0; i < block.Instructions.Count; i++) {
                        if (block.Instructions[i] is StoreInst { Kind: StoreKind.Assign, Address: AllocStackInst slot } store && unmanaged.Contains(slot)) {
                            var raw = new StoreInst(store.Value, store.Address, StoreKind.Init) { Location = store.Location, Block = block };
                            block.Instructions[i] = raw;
                        }
                    }
                }
            }

            var fields = new Dictionary<AllocStackInst, SilInstruction>(ReferenceEqualityComparer.Instance);
            var slotFields = new List<int>();
            foreach (var slot in slots) {
                if (!unmanaged.Contains(slot) && !slot.ElementType.IsTrivial) {
                    slotFields.Add(frame.Fields.Count);
                }

                var slotName = $"{slot.VariableName ?? "$slot"}${frame.Fields.Count}";
                fields[slot] = SilType.IsIndirect(slot.ElementType) ? Tail(slotName, slot.ElementType) : entry.Append(Field(slotName, slot.ElementType));
            }

            CoroutineSplit.SlotsToFields(function, fields);

            // An object, an array or a block made in the frame would be gone when it suspends.
            foreach (var inFrame in function.Blocks.SelectMany(static b => b.Instructions)) {
                switch (inFrame) {
                    case AllocRefInst alloc:
                        alloc.OnStack = false;
                        break;

                    case BuiltinInst { Name: "array_literal_stack" } literal:
                        literal.Rename("array_literal");
                        break;

                    case BuiltinInst { Name: "inline_new_stack" } block:
                        block.Rename("inline_new");
                        break;
                }
            }

            // Each place it may suspend, a state.
            var suspensions = new List<Suspension>();
            foreach (var block in function.Blocks.Skip(1).ToList()) {
                for (var i = 0; i < block.Instructions.Count; i++) {
                    if (IsSuspension(block.Instructions[i])) {
                        Suspend(function, block, i, self, suspensions, async: true);
                        // The rest of the block has moved on: nothing more here.
                        break;
                    }
                }
            }

            // Leaving: the result or the error in the frame, and finished.
            var resultField = head.FieldIndex("$result");
            var errorField = head.FieldIndex("$error");
            foreach (var block in function.Blocks.ToList()) {
                if (block.Terminator is ReturnInst returned) {
                    if (IsSuspendBlock(block, suspensions)) {
                        continue;
                    }

                    Leave(block, returned, returned.Value is { } value && resultField >= 0 ? (value, resultField) : null, Finished, self);
                } else if (block.Terminator is ThrowInst thrown) {
                    Leave(block, thrown, (thrown.Error, errorField), Threw, self);
                }
            }

            // The entry: on to where the state says.
            var state = entry.Append(new LoadInst(entry.Append(new RefElementAddrInst(self, StateField) { Location = at }), LoadKind.Trivial) { Location = at });
            var targets = new List<SilBlock> { body };
            targets.AddRange(suspensions.Select(static s => s.Resume));
            var otherwise = CoroutineSplit.Dispatch(function, entry, state, targets, 0, at);
            otherwise.Append(new CondFailInst(otherwise.Append(new IntegerLiteralInst(SilType.Bool, 1)), "a finished async function resumed"));
            otherwise.Append(new UnreachableInst());

            // What is live where it goes on, kept in the frame meanwhile.
            var spilled = Spill(function, entry, suspensions.Select(static s => s.Resume), Field, Tail);
            for (var i = 0; i < parameters.Count; i++) {
                spilled[parameters[i]] = parameterFields[i];
            }

            Drop(name, frame, suspensions, spilled, slotFields, unspecialised ? function : null);
            if (unspecialised) {
                function.HiddenFrame = frame;
            }

            // The start: the frame made, the parameters in it, nothing run.
            var start = new SilFunction(name, StartType(original)) { SourceFile = function.SourceFile, Line = function.Line };
            start.ParameterNames.AddRange(parameterNames);
            var startBlock = start.AddBlock();
            var made = startBlock.Append(new AllocRefInst(frame) { Location = at });
            startBlock.Append(new StoreInst(startBlock.Append(new FunctionRefInst(function.Name, ResumeType)), startBlock.Append(new RefElementAddrInst(made, ResumeField)), StoreKind.Init));
            startBlock.Append(new StoreInst(startBlock.Append(new FunctionRefInst(DropName(name), DropType)), startBlock.Append(new RefElementAddrInst(made, DropField)), StoreKind.Init));
            startBlock.Append(new StoreInst(startBlock.Append(new IntegerLiteralInst(SilType.Int, 0)), startBlock.Append(new RefElementAddrInst(made, StateField)), StoreKind.Init));
            for (var i = 0; i < parameters.Count; i++) {
                SilValue argument = startBlock.AddArgument(original.Parameters[i]);
                if (SilType.IsIndirect(argument.Type)) {
                    argument = startBlock.Append(new BuiltinInst("opaque_address", new AddressType(argument.Type), argument));
                }

                startBlock.Append(new StoreInst(argument, startBlock.Append(new RefElementAddrInst(made, parameterFields[i])), StoreKind.Init));
            }

            // Its frame's buffers are sized by the metadata it is given, as its body's are.
            start.AssociatedMetadata.AddRange(function.AssociatedMetadata);

            startBlock.Append(new ReturnInst(startBlock.Append(new UpcastInst(made, head))));
            module.Add(start);
            if (module.Unit is { } owner && owner.Owned.Contains(function.Name)) {
                owner.Owned.Add(name);
                owner.Owned.Add(DropName(name));
            }
        }

        /// <summary>
        ///     The drop function: for a frame suspended part-way, whose task
        ///     is dropped, what it holds where it stopped destroyed — the
        ///     callee's frame it waits in first, dropped and given back, then
        ///     the owned values live there, then every slot. Slots hold a
        ///     value or nothing (all zeros), and destroying nothing does
        ///     nothing; defers do not run, as the task goes no further.
        /// </summary>
        void Drop(string name, ClassType frame, List<Suspension> suspensions, Dictionary<SilValue, int> spilled, List<int> slotFields, SilFunction? unspecialised) {
            var drop = new SilFunction(DropName(name), DropType);
            drop.ParameterNames.Add("$frame");
            if (unspecialised is not null) {
                // What it destroys is sized and destroyed by the metadata in the frame.
                drop.HiddenFrame = frame;
                drop.AssociatedMetadata.AddRange(unspecialised.AssociatedMetadata);
            }

            var entry = drop.AddBlock();
            var self = entry.Append(new UncheckedRefCastInst(entry.AddArgument(SilType.Object), frame));

            // A field that holds the address of a buffer, or of a caller's
            // storage: what it holds is destroyed there.
            SilValue Held(SilBlock block, int field) {
                var address = block.Append(new RefElementAddrInst(self, field));
                return frame.Fields[field].Type is AddressType { Pointee: var pointee } && SilType.IsIndirect(pointee)
                    ? block.Append(new LoadInst(address, LoadKind.Trivial))
                    : address;
            }
            var state = entry.Append(new LoadInst(entry.Append(new RefElementAddrInst(self, StateField)), LoadKind.Trivial));
            var slots = drop.AddBlock();
            var targets = new List<SilBlock>();
            foreach (var suspension in suspensions) {
                var block = drop.AddBlock();
                targets.Add(block);
                if (suspension.Callee is { } callee && spilled.TryGetValue(callee, out var calleeField)) {
                    var calleeFrame = block.Append(new LoadInst(block.Append(new RefElementAddrInst(self, calleeField)), LoadKind.Trivial));
                    var calleeDrop = block.Append(new LoadInst(block.Append(new RefElementAddrInst(calleeFrame, DropField)), LoadKind.Trivial));
                    block.Append(new ApplyInst(calleeDrop, SilType.Unit, calleeFrame));
                    block.Append(new BuiltinInst("async_frame_free", SilType.Unit, calleeFrame));
                }

                foreach (var value in suspension.Owned) {
                    if (spilled.TryGetValue(value, out var field)) {
                        if (SilType.IsIndirect(value.Type)) {
                            block.Append(new DestroyAddrInst(Held(block, field)));
                        } else {
                            block.Append(new DestroyValueInst(block.Append(new LoadInst(block.Append(new RefElementAddrInst(self, field)), LoadKind.Trivial))));
                        }
                    }
                }

                block.Append(new BranchInst(slots));
            }

            var otherwise = CoroutineSplit.Dispatch(drop, entry, state, targets, 1);
            otherwise.Append(new BranchInst(slots));
            foreach (var field in slotFields) {
                slots.Append(new DestroyAddrInst(Held(slots, field)));
            }

            slots.Append(new ReturnInst(null));
            module.Add(drop);
        }

        /// <summary>
        ///     The owned values live — made and not yet consumed — at each
        ///     place the function may suspend, as the verifier's walk finds
        ///     them, less what that place consumes itself.
        /// </summary>
        static Dictionary<SilInstruction, List<SilValue>> OwnedAtSuspensions(SilFunction function) {
            var found = new Dictionary<SilInstruction, List<SilValue>>(ReferenceEqualityComparer.Instance);
            var liveIn = new Dictionary<SilBlock, HashSet<SilValue>> { [function.Entry] = new(ReferenceEqualityComparer.Instance) };

            void Enter(SilBlock target, HashSet<SilValue> live) => liveIn.TryAdd(target, new(live, ReferenceEqualityComparer.Instance));

            static bool Owned(SilValue value) => !value.Type.IsTrivial && SilVerifier.OwnershipOf(value) == Ownership.Owned;

            foreach (var block in SilVerifier.ReversePostorder(function)) {
                if (!liveIn.TryGetValue(block, out var incoming)) {
                    continue;
                }

                var live = new HashSet<SilValue>(incoming, ReferenceEqualityComparer.Instance);
                live.UnionWith(block.Arguments.Where(Owned));
                foreach (var instruction in block.Instructions) {
                    if (instruction is CondBranchInst conditional) {
                        HashSet<SilValue> Edge(IEnumerable<SilValue> passed, SilBlock target) {
                            var after = new HashSet<SilValue>(live, ReferenceEqualityComparer.Instance);
                            var i = 0;
                            foreach (var value in passed) {
                                if (!target.Arguments[i++].IsGuaranteed) {
                                    after.Remove(value);
                                }
                            }

                            return after;
                        }

                        Enter(conditional.WhenTrue, Edge(conditional.TrueArguments, conditional.WhenTrue));
                        Enter(conditional.WhenFalse, Edge(conditional.FalseArguments, conditional.WhenFalse));
                        continue;
                    }

                    var consumed = SilVerifier.Consumed(instruction).Select(i => instruction.Operands[i]).ToList();
                    if (IsSuspension(instruction)) {
                        found[instruction] = live.Where(v => !consumed.Contains(v)).ToList();
                    }

                    live.ExceptWith(consumed);
                    if (instruction.HasResult && Owned(instruction)) {
                        live.Add(instruction);
                    }

                    switch (instruction) {
                        case BranchInst branch:
                            Enter(branch.Target, live);
                            break;

                        case SwitchEnumInst switchEnum:
                            foreach (var (_, target) in switchEnum.Cases) {
                                Enter(target, live);
                            }

                            if (switchEnum.Default is { } fallback) {
                                Enter(fallback, live);
                            }

                            break;

                        case TryApplyInst tryApply:
                            Enter(tryApply.Normal, live);
                            Enter(tryApply.Error, live);
                            break;
                    }
                }
            }

            return found;
        }

        static bool IsSuspendBlock(SilBlock block, List<Suspension> suspensions) => suspensions.Any(s => ReferenceEquals(s.Suspend, block));

        /// <summary>A return or a throw: what it gives, in the frame, the state finished, and <c>true</c> returned.</summary>
        static void Leave(SilBlock block, SilInstruction exit, (SilValue Value, int Field)? kept, long state, SilValue self) {
            var location = exit.Location;
            block.Instructions.Remove(exit);
            if (kept is var (value, field)) {
                block.Append(new StoreInst(value, block.Append(new RefElementAddrInst(self, field) { Location = location }), StoreKind.Init) { Location = location });
            }

            SetState(block, self, state, location);
            block.Append(new ReturnInst(block.Append(new IntegerLiteralInst(SilType.Bool, 1) { Location = location })) { Location = location });
        }

        static void SetState(SilBlock block, SilValue self, long state, SilLocation? location) {
            var value = block.Append(new IntegerLiteralInst(SilType.Int, state) { Location = location });
            block.Append(new StoreInst(value, block.Append(new RefElementAddrInst(self, StateField) { Location = location }), StoreKind.Init) { Location = location });
        }

        /// <summary>
        ///     Splits the block at a place the body may suspend: a wait, or a
        ///     call to an async function. In a resume function it becomes a
        ///     state; in an ordinary function (<paramref name="async" /> false),
        ///     a call polled once, which must finish.
        /// </summary>
        void Suspend(SilFunction function, SilBlock block, int index, SilValue? self, List<Suspension>? suspensions, bool async) {
            var instruction = block.Instructions[index];
            var location = instruction.Location;
            var after = function.AddBlock();
            after.Instructions.AddRange(block.Instructions.Skip(index + 1));
            foreach (var moved in after.Instructions) {
                moved.Block = after;
            }

            block.Instructions.RemoveRange(index, block.Instructions.Count - index);

            // The suspension: the state to resume at, and 'false' returned.
            SilBlock Suspended(SilBlock resume, SilValue? callee = null) {
                var suspend = function.AddBlock();
                if (!async) {
                    suspend.Append(new CondFailInst(suspend.Append(new IntegerLiteralInst(SilType.Bool, 1)), "an async call suspended where nothing can wait") { Location = location });
                    suspend.Append(new UnreachableInst());
                    return suspend;
                }

                suspensions!.Add(new(suspend, resume, _ownedAt.GetValueOrDefault(instruction) ?? [], callee));
                SetState(suspend, self!, suspensions.Count, location);
                suspend.Append(new ReturnInst(suspend.Append(new IntegerLiteralInst(SilType.Bool, 0))) { Location = location });
                return suspend;
            }

            T At<T>(SilBlock target, T made) where T : SilInstruction {
                made.Location = location;
                return target.Append(made);
            }

            switch (instruction) {
                // A task's own waits: the runtime says whether to suspend,
                // having queued the task where it will be woken.
                case BuiltinInst { Name: "task_wait" or "task_group_wait" or "task_hop" } wait: {
                    if (!async) {
                        throw new InvalidOperationException($"'{wait.Name}' outside an async function, in {function.Name}");
                    }

                    // Woken, it asks again: the task may not have finished.
                    var check = function.AddBlock();
                    At(block, new BranchInst(check));
                    var must = At(check, new BuiltinInst(wait.Name switch { "task_wait" => "async_wait", "task_hop" => "async_hop", _ => "async_group_wait" }, SilType.Bool, wait.Operands[0]));
                    At(check, new CondBranchInst(must, Suspended(check), [], after, []));
                    break;
                }

                case BuiltinInst { Name: "task_yield" or "task_sleep" or "task_park" or "task_park_cancellable" or "task_io_wait" or "task_blocking" } wait: {
                    if (!async) {
                        throw new InvalidOperationException($"'{wait.Name}' outside an async function, in {function.Name}");
                    }

                    var must = At(block, new BuiltinInst(wait.Name switch {
                        "task_yield" => "async_yield", "task_park" => "async_park", "task_park_cancellable" => "async_park_cancellable", "task_io_wait" => "async_io_wait", "task_blocking" => "async_blocking", _ => "async_sleep",
                    }, SilType.Bool, [..wait.Operands]));
                    At(block, new CondBranchInst(must, Suspended(after), [], after, []));
                    break;
                }

                // A call: the callee's frame started, then polled until it has finished.
                default: {
                    var (callee, arguments, result) = instruction switch {
                        ApplyInst apply => (apply.Callee, apply.Arguments.ToArray(), (SilType?)apply.Type),
                        TryApplyInst throwing => (throwing.Callee, throwing.Arguments.ToArray(), null),
                        _ => throw new InvalidOperationException(instruction.Opcode),
                    };
                    var signature = Signature(callee);
                    var head = Head(signature.Result, signature.Thrown);
                    var frame = At(block, new ApplyInst(callee, head, arguments));
                    var poll = function.AddBlock();
                    At(block, new BranchInst(poll));
                    var resume = At(poll, new LoadInst(At(poll, new RefElementAddrInst(frame, ResumeField)), LoadKind.Trivial));
                    var finished = At(poll, new ApplyInst(resume, SilType.Bool, frame));
                    var done = function.AddBlock();
                    At(poll, new CondBranchInst(finished, done, [], Suspended(poll, frame), []));

                    if (instruction is TryApplyInst tryApply) {
                        var normal = function.AddBlock();
                        var failed = function.AddBlock();
                        var state = At(done, new LoadInst(At(done, new RefElementAddrInst(frame, StateField)), LoadKind.Trivial));
                        var threw = At(done, new BuiltinInst("cmp_eq_Int", SilType.Bool, state, At(done, new IntegerLiteralInst(SilType.Int, Threw))));
                        At(done, new CondBranchInst(threw, failed, [], normal, []));
                        var error = At(failed, new LoadInst(At(failed, new RefElementAddrInst(frame, head.FieldIndex("$error"))), LoadKind.Trivial));
                        At(failed, new BuiltinInst("async_frame_free", SilType.Unit, frame));
                        At(failed, new BranchInst(tryApply.Error, error));
                        SilValue[] normalArguments = signature.Result == SilType.Unit || tryApply.Normal.Arguments.Count == 0
                            ? []
                            : [At(normal, new LoadInst(At(normal, new RefElementAddrInst(frame, head.FieldIndex("$result"))), LoadKind.Trivial))];
                        At(normal, new BuiltinInst("async_frame_free", SilType.Unit, frame));
                        At(normal, new BranchInst(tryApply.Normal, normalArguments));
                        function.Blocks.Remove(after);
                        break;
                    }

                    if (result is { } type && type != SilType.Unit) {
                        var value = At(done, new LoadInst(At(done, new RefElementAddrInst(frame, head.FieldIndex("$result"))), LoadKind.Trivial));
                        ReplaceUses(function, instruction, value);
                    }

                    At(done, new BuiltinInst("async_frame_free", SilType.Unit, frame));
                    At(done, new BranchInst(after));
                    break;
                }
            }

            // What follows may suspend again.
            for (var i = 0; i < after.Instructions.Count; i++) {
                if (IsSuspension(after.Instructions[i])) {
                    Suspend(function, after, i, self, suspensions, async);
                    break;
                }
            }
        }

        /// <summary>An ordinary function's calls to async functions: each polled once, where it must finish.</summary>
        void Block(SilFunction function) {
            foreach (var block in function.Blocks.ToList()) {
                for (var i = 0; i < block.Instructions.Count; i++) {
                    if (block.Instructions[i] is ApplyInst or TryApplyInst && IsSuspension(block.Instructions[i])) {
                        Suspend(function, block, i, null, null, async: false);
                        break;
                    }
                }
            }
        }

        /// <summary>
        ///     In unspecialised code (#297), a value of a type parameter's type
        ///     borrowed from storage is that storage: borrowed again where it is
        ///     used, so what lives across a suspension is the storage's address
        ///     — the frame's, or a caller's — and not a copy of what it holds.
        /// </summary>
        static void Reborrow(SilFunction function) {
            // One seen as a callee's type parameter is seen as it again there, as only the callee has that type's metadata.
            foreach (var seen in function.Blocks.SelectMany(static b => b.Instructions).OfType<BuiltinInst>()
                         .Where(static b => b.Name == "opaque_borrow_as" && SilType.IsIndirect(b.Type) && SilType.IsIndirect(b.Operands[0].Type)).Reverse().ToList()) {
                foreach (var user in function.Blocks.SelectMany(static b => b.Instructions).Where(i => i.Operands.Any(o => ReferenceEquals(o, seen))).ToList()) {
                    var again = new BuiltinInst(seen.Name, seen.Type, seen.Operands[0]) { Location = user.Location };
                    Insert(user.Block, user.Block.Instructions.IndexOf(user), again);
                    Substitute(user, seen, again);
                }

                seen.Block.Instructions.Remove(seen);
            }

            foreach (var borrow in function.Blocks.SelectMany(static b => b.Instructions).OfType<LoadBorrowInst>().Where(static b => SilType.IsIndirect(b.Type)).ToList()) {
                var users = function.Blocks.SelectMany(static b => b.Instructions).Where(i => i.Operands.Any(o => ReferenceEquals(o, borrow))).ToList();
                foreach (var user in users) {
                    var again = new LoadBorrowInst(borrow.Address) { Location = user.Location };
                    Insert(user.Block, user.Block.Instructions.IndexOf(user), again);
                    Substitute(user, borrow, again);
                }

                borrow.Block.Instructions.Remove(borrow);
            }
        }

        /// <summary>
        ///     Every value made in one state and used in a later one, kept in
        ///     the frame: stored as it is made, and read back where it is used.
        ///     A constant is made again where it is used instead.
        /// </summary>
        static Dictionary<SilValue, int> Spill(SilFunction function, SilBlock entry, IEnumerable<SilBlock> resumes, Func<string, SilType, SilInstruction> field,
            Func<string, SilType, SilInstruction> tail) {
            var kept = new Dictionary<SilValue, int>(ReferenceEqualityComparer.Instance);
            var live = CoroutineSplit.LiveIn(function);
            var spilled = new HashSet<SilValue>(ReferenceEqualityComparer.Instance);
            var order = new List<SilValue>();
            foreach (var resume in resumes) {
                foreach (var value in live[resume]) {
                    if (!CoroutineSplit.MadeByEntry(value, entry) && spilled.Add(value)) {
                        order.Add(value);
                    }
                }
            }

            if (order.Count == 0) {
                return kept;
            }

            // Each use of each, by the instruction that uses it.
            var users = new Dictionary<SilValue, List<SilInstruction>>(ReferenceEqualityComparer.Instance);
            foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
                foreach (var operand in instruction.Operands.Distinct()) {
                    if (spilled.Contains(operand)) {
                        (users.TryGetValue(operand, out var list) ? list : users[operand] = []).Add(instruction);
                    }
                }
            }

            // The entry's last instruction is its dispatch: the fields' addresses go before it.
            var dispatchAt = entry.Instructions.FindIndex(static i => i is LoadInst { Address: RefElementAddrInst { Field: StateField } });
            foreach (var value in order) {
                if (value.Type == SilType.Unit) {
                    throw new InvalidOperationException($"a value of type () live across an await in {function.Name}");
                }

                var uses = users.GetValueOrDefault(value) ?? [];
                if (value is IntegerLiteralInst or FloatLiteralInst or StringLiteralInst or FunctionRefInst or GlobalAddrInst or MetadataInst) {
                    var made = (SilInstruction)value;
                    foreach (var user in uses) {
                        var copy = made.Clone(static v => v, static b => b);
                        Insert(user.Block, user.Block.Instructions.IndexOf(user), copy);
                        Substitute(user, value, copy);
                    }

                    continue;
                }

                // A value of a type parameter's type (#297) is kept in a buffer
                // of the frame's, its bits moved there as it is made: where it is
                // used, it is that buffer's.
                var indirect = SilType.IsIndirect(value.Type);
                SilInstruction address;
                if (indirect) {
                    address = tail($"$live{entry.Instructions.Count}", value.Type);
                    kept[value] = ((RefElementAddrInst)((LoadInst)address).Address).Field;
                    foreach (var made in new[] { (SilInstruction)((LoadInst)address).Address, address }) {
                        entry.Instructions.Remove(made);
                        entry.Instructions.Insert(dispatchAt++, made);
                    }
                } else {
                    address = field($"$live{entry.Instructions.Count}", value.Type);
                    kept[value] = ((RefElementAddrInst)address).Field;
                    address.Block = entry;
                    entry.Instructions.Insert(dispatchAt++, address);
                }

                // Stored as it is made.
                var (block, index) = value switch {
                    SilArgument argument => (argument.Block, 0),
                    SilInstruction instruction => (instruction.Block, instruction.Block.Instructions.IndexOf(instruction) + 1),
                    _ => throw new InvalidOperationException(value.ToString()),
                };
                Insert(block, index, new StoreInst(value, address, StoreKind.Init) { Location = (value as SilInstruction)?.Location });

                // Read back at each use.
                foreach (var user in uses) {
                    SilInstruction load = indirect ? new LoadBorrowInst(address) { Location = user.Location } : new LoadInst(address, LoadKind.Trivial) { Location = user.Location };
                    Insert(user.Block, user.Block.Instructions.IndexOf(user), load);
                    Substitute(user, value, load);
                }
            }

            return kept;
        }

        static void Insert(SilBlock block, int index, SilInstruction instruction) {
            instruction.Block = block;
            block.Instructions.Insert(index, instruction);
        }

        static void Substitute(SilInstruction user, SilValue value, SilValue replacement) {
            for (var i = 0; i < user.Operands.Count; i++) {
                if (ReferenceEquals(user.Operands[i], value)) {
                    user.SetOperand(i, replacement);
                }
            }
        }

        static void ReplaceUses(SilFunction function, SilValue value, SilValue replacement) {
            foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
                if (!ReferenceEquals(instruction, replacement)) {
                    Substitute(instruction, value, replacement);
                }
            }
        }
    }
}
