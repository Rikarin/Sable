using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Tasks (docs/02 §2.7): <c>Task&lt;T&gt;</c> is a class holding the
///     runtime's handle, the body to run and, once run, its result. A thunk
///     per result type, async, runs the body and stores what it gives: it is
///     the root of the task's frames. Lowering marks where a task waits —
///     <c>task_wait</c>, <c>task_yield</c>, <c>task_sleep</c> — and treats an
///     async call as any other; <see cref="AsyncLowering" /> makes the waits
///     and the calls suspension points, after the passes.
/// </summary>
public sealed partial class SilGen {
    const int TaskHandle = 0, TaskResult = 1, TaskBody = 2, TaskError = 3;

    /// <summary>
    ///     The class a <c>Task&lt;T&gt;</c> is, and the thunk that runs one.
    ///     An 'async let' whose child may throw is a task of a class of its
    ///     own, which keeps the error the body threw, if it threw one,
    ///     where its result would have been.
    /// </summary>
    internal ClassType? TaskClass(TypeSymbol success, TypeSymbol? failure = null) {
        if (Lower(success) is not { } result || Lower(new OptionalTypeSymbol(success)) is not EnumType optional) {
            return null;
        }

        SilType? thrown = null;
        EnumType? optionalError = null;
        if (failure is not null && (!failure.Equals(Core.Never))
            && ((thrown = Lower(failure)) is null || (optionalError = Lower(new OptionalTypeSymbol(failure)) as EnumType) is null)) {
            return null;
        }

        var suffix = Sanitise(result.ToString()) + (thrown is null ? "" : $"$throws${Sanitise(thrown.ToString())}");
        var name = $"Task$of${suffix}";
        if (_module.FindType(name) is ClassType existing) {
            return existing;
        }

        // Compiled once for every result (#297): an instance of a generic
        // class, laid out and destroyed as one is, its body kept in the
        // generic convention, run by one thunk for all of them.
        var once = TasksCompiledOnce;
        var task = _module.AddType(new ClassType(name));
        task.Fields.Add(("handle", SilType.Int));
        task.Fields.Add(("result", optional));
        task.Fields.Add(("body", once ? TaskBodyOnce(thrown is not null) : new ThickFunctionType(new FunctionType([], result, thrown, isAsync: true))));
        if (optionalError is not null) {
            task.Fields.Add(("error", optionalError));
        }

        if (once) {
            task.TypeArguments = thrown is null ? [result] : [result, thrown];
            task.DisplayName = $"{TaskGenericName(thrown is not null)}<{string.Join(", ", task.TypeArguments.Select(TypeMetadata.DisplayName))}>";
            if (SilType.MentionsOpaque(task)) {
                task.HasGenericDeinit = true;
                TaskOnce(thrown is not null);
                return task;
            }
        }

        // Its deinit gives the runtime's task back.
        var deinit = new SilFunction($"$task.deinit${suffix}", new FunctionType([task], SilType.Unit));
        var entry = deinit.AddBlock();
        var self = entry.AddArgument(task);
        var handle = entry.Append(new LoadInst(entry.Append(new RefElementAddrInst(self, TaskHandle))));
        entry.Append(new BuiltinInst("task_release", SilType.Unit, handle));
        entry.Append(new ReturnInst(null));
        _module.Add(deinit);
        task.Deinit = deinit.Name;

        if (once) {
            TaskOnce(thrown is not null);
            return task;
        }

        // Run on the task: the body called, its result kept, the task's reference given back.
        var run = new SilFunction(TaskRunName(task), RunType(task));
        var block = run.AddBlock();
        var owned = block.AddArgument(task);
        var body = block.Append(new LoadInst(block.Append(new RefElementAddrInst(owned, TaskBody))));
        SilValue? value;
        if (thrown is not null && optionalError is not null) {
            // What it throws is kept as its error; the task finishes all the same.
            var normal = run.AddBlock();
            var failed = run.AddBlock();
            value = result == SilType.Unit ? null : normal.AddArgument(result);
            var error = failed.AddArgument(thrown);
            block.Append(new TryApplyInst(body, result, [], normal, failed));
            failed.Append(new DestroyValueInst(body));
            // Whoever waits for the task, on another thread, reads it.
            failed.Append(new BuiltinInst("share", SilType.Unit, error));
            var someError = failed.Append(new EnumInst(optionalError, optionalError.CaseIndex("Some"), error));
            failed.Append(new StoreInst(someError, failed.Append(new RefElementAddrInst(owned, TaskError)), StoreKind.Assign));
            failed.Append(new DestroyValueInst(owned));
            failed.Append(new ReturnInst(null));
            block = normal;
        } else {
            value = block.Append(new ApplyInst(body, result));
        }

        block.Append(new DestroyValueInst(body));
        if (value is not null && !value.Type.IsTrivial) {
            block.Append(new BuiltinInst("share", SilType.Unit, value));
        }

        // A body that gives nothing makes a Void?, whose 'Some' carries nothing.
        var carried = optional.Cases[optional.CaseIndex("Some")].Payload is null ? null : value;
        var some = block.Append(new EnumInst(optional, optional.CaseIndex("Some"), carried));
        block.Append(new StoreInst(some, block.Append(new RefElementAddrInst(owned, TaskResult)), StoreKind.Assign));
        block.Append(new DestroyValueInst(owned));
        block.Append(new ReturnInst(null));
        _module.Add(run);
        return task;
    }

    /// <summary>
    ///     Whether a task is an instance of one generic class, its code
    ///     compiled once: in an unoptimised build that compiles generics once,
    ///     as the functions a generic type stores are
    ///     (<see cref="StoredFunctionType" />).
    /// </summary>
    internal static bool TasksCompiledOnce => Generics == GenericsMode.Unspecialised && !AmbientOptimised.Value;

    /// <summary>The generic name of the class of tasks, or of those that keep an error too.</summary>
    string TaskGenericName(bool throws) => QualifiedName(Core.Task!) + (throws ? "$throws" : "");

    static readonly OpaqueType TaskSuccess = new("__Success"), TaskFailure = new("__Failure");

    /// <summary>A task's body as the class of tasks keeps it, whatever its result: told where it, and an error, go.</summary>
    static ThickFunctionType TaskBodyOnce(bool throws) =>
        new(throws
            ? new FunctionType([SilType.Address(TaskSuccess), SilType.Address(TaskFailure)], SilType.Unit, SilType.Address(TaskFailure), isAsync: true)
            : new FunctionType([SilType.Address(TaskSuccess)], SilType.Unit, isAsync: true));

    /// <summary>What runs a task of any result, and its deinit, each made once: given the task, whose class says what its result is.</summary>
    void TaskOnce(bool throws) {
        var run = TaskRunOnce(throws);
        if (_module.Find(run) is not null) {
            return;
        }

        // Held back until the class itself is, which making it asks for.
        var shape = throws ? (SilType[])[SilType.Metadata, SilType.Metadata] : [SilType.Metadata];
        var names = throws ? (string[])["$metadata.__Success", "$metadata.__Failure"] : ["$metadata.__Success"];
        var thunk = _module.Add(new SilFunction(run, TaskRunType));
        var optional = OptionalOf(TaskSuccess);
        var optionalError = OptionalOf(TaskFailure);
        var className = $"Task$of${Sanitise(TaskSuccess.ToString())}" + (throws ? $"$throws${Sanitise(TaskFailure.ToString())}" : "");
        if (_module.FindType(className) is not ClassType task) {
            task = _module.AddType(new ClassType(className));
            task.Fields.AddRange([("handle", SilType.Int), ("result", optional), ("body", TaskBodyOnce(throws))]);
            if (throws) {
                task.Fields.Add(("error", optionalError));
            }

            task.TypeArguments = throws ? [TaskSuccess, TaskFailure] : [TaskSuccess];
            task.DisplayName = $"{TaskGenericName(throws)}<{string.Join(", ", task.TypeArguments.Select(TypeMetadata.DisplayName))}>";
            task.HasGenericDeinit = true;
        }

        // The run, reading what its result is from the task's class, and the one compiled once it calls.
        var body = new SilFunction(run + "$generic", new FunctionType([task, ..shape], SilType.Unit, null, [true, ..shape.Select(static _ => false)], isAsync: true));
        {
            var entry = thunk.AddBlock();
            var given = entry.AddArgument(SilType.Object);
            var held = entry.Append(new CopyValueInst(entry.Append(new UncheckedRefCastInst(given, task))));
            entry.Append(new DestroyValueInst(given));
            var metadata = entry.Append(new BuiltinInst("object_metadata", SilType.Metadata, held));
            var arguments = shape.Select((_, i) => (SilValue)entry.Append(new BuiltinInst("metadata_argument", SilType.Metadata, metadata, entry.Append(new IntegerLiteralInst(SilType.Int, i))))).ToList();
            entry.Append(new ApplyInst(entry.Append(new FunctionRefInst(body.Name, body.Type)), SilType.Unit, [held, ..arguments]));
            entry.Append(new ReturnInst(null));
        }

        {
            body.ParameterNames.AddRange(["task", ..names]);
            var entry = body.AddBlock();
            var owned = entry.AddArgument(task);
            foreach (var hidden in shape) {
                entry.AddArgument(hidden);
            }

            var slot = entry.Append(new AllocStackInst(TaskSuccess));
            var failure = throws ? entry.Append(new AllocStackInst(TaskFailure)) : null;
            var code = entry.Append(new LoadInst(entry.Append(new RefElementAddrInst(owned, TaskBody))));
            var block = entry;
            if (failure is not null) {
                var normal = body.AddBlock();
                var failed = body.AddBlock();
                failed.AddArgument(SilType.Address(TaskFailure));
                entry.Append(new TryApplyInst(code, SilType.Unit, [slot, failure], normal, failed));
                failed.Append(new DestroyValueInst(code));
                var error = failed.Append(new LoadInst(failure, LoadKind.Take));
                failed.Append(new BuiltinInst("share", SilType.Unit, error));
                var someError = failed.Append(new EnumInst(optionalError, optionalError.CaseIndex("Some"), error));
                failed.Append(new StoreInst(someError, failed.Append(new RefElementAddrInst(owned, TaskError)), StoreKind.Assign));
                failed.Append(new DestroyValueInst(owned));
                failed.Append(new DeallocStackInst(failure));
                failed.Append(new DeallocStackInst(slot));
                failed.Append(new ReturnInst(null));
                block = normal;
            } else {
                entry.Append(new ApplyInst(code, SilType.Unit, [slot]));
            }

            block.Append(new DestroyValueInst(code));
            var value = block.Append(new LoadInst(slot, LoadKind.Take));
            block.Append(new BuiltinInst("share", SilType.Unit, value));
            var some = block.Append(new EnumInst(optional, optional.CaseIndex("Some"), value));
            block.Append(new StoreInst(some, block.Append(new RefElementAddrInst(owned, TaskResult)), StoreKind.Assign));
            block.Append(new DestroyValueInst(owned));
            if (failure is not null) {
                block.Append(new DeallocStackInst(failure));
            }

            block.Append(new DeallocStackInst(slot));
            block.Append(new ReturnInst(null));
            _module.Add(body);
        }

        // The deinit: the runtime's task given back, through the handle.
        var stem = $"$template$deinit$task{(throws ? "$throws" : "")}";
        _module.GenericDeinits[TaskGenericName(throws)] = stem;
        var release = new SilFunction(stem + "$generic", new FunctionType([task, ..shape], SilType.Unit));
        release.ParameterNames.AddRange(["task", ..names]);
        var start = release.AddBlock();
        var released = start.AddArgument(task);
        foreach (var hidden in shape) {
            start.AddArgument(hidden);
        }

        start.Append(new BuiltinInst("task_release", SilType.Unit, start.Append(new LoadInst(start.Append(new RefElementAddrInst(released, TaskHandle))))));
        start.Append(new ReturnInst(null));
        _module.Add(release);
        var deinit = new SilFunction(stem, new FunctionType([task], SilType.Unit));
        var at = deinit.AddBlock();
        var self = at.AddArgument(task);
        var of = at.Append(new BuiltinInst("object_metadata", SilType.Metadata, self));
        at.Append(new ApplyInst(at.Append(new FunctionRefInst(release.Name, release.Type)), SilType.Unit,
            [self, ..shape.Select((_, i) => (SilValue)at.Append(new BuiltinInst("metadata_argument", SilType.Metadata, of, at.Append(new IntegerLiteralInst(SilType.Int, i)))))]));
        at.Append(new ReturnInst(null));
        _module.Add(deinit);
    }

    /// <summary>An optional of an opaque type, as unspecialised code holds it.</summary>
    EnumType OptionalOf(OpaqueType wrapped) {
        var name = $"Optional${Sanitise(wrapped.ToString())}";
        if (_module.FindType(name) is EnumType existing) {
            return existing;
        }

        var made = new EnumType(name);
        made.Cases.Add(("Some", wrapped));
        made.Cases.Add(("None", null));
        return (EnumType)_module.AddType(made);
    }

    static string TaskRunOnce(bool throws) => throws ? "$task.run$once$throws" : "$task.run$once";

    static readonly FunctionType TaskRunType = new([SilType.Object], SilType.Unit, null, [true], isAsync: true);

    static string TaskRunName(ClassType task) => TasksCompiledOnce ? TaskRunOnce(task.Fields.Count > TaskError) : $"$task.run${task.Name}";

    /// <summary>What runs a task: async, the root of its task's frames, given the task to keep its result in.</summary>
    static FunctionType RunType(ClassType task) => TasksCompiledOnce ? TaskRunType : new([task], SilType.Unit, null, [true], isAsync: true);

    /// <summary>What a cancellation handler is: a closure taking nothing and giving nothing.</summary>
    static readonly ThickFunctionType HandlerBody = new(new FunctionType([], SilType.Unit));

    /// <summary>
    ///     The object a cancellation handler is kept in while the runtime
    ///     holds it, and the function the runtime calls it through, given
    ///     that object: the closure, borrowed and called.
    /// </summary>
    ClassType CancellationHandler() {
        const string name = "$task.handler";
        if (_module.FindType(name) is ClassType existing) {
            return existing;
        }

        var handler = _module.AddType(new ClassType(name));
        handler.Fields.Add(("body", HandlerBody));

        var run = new SilFunction(HandlerRunName, HandlerRunType);
        var block = run.AddBlock();
        var self = block.Append(new UncheckedRefCastInst(block.AddArgument(SilType.Object), handler));
        var body = block.Append(new LoadBorrowInst(block.Append(new RefElementAddrInst(self, 0))));
        block.Append(new ApplyInst(body, SilType.Unit));
        block.Append(new EndBorrowInst(body));
        block.Append(new ReturnInst(null));
        _module.Add(run);
        return handler;
    }

    const string HandlerRunName = "$task.handler.run";

    static readonly FunctionType HandlerRunType = new([SilType.Object], SilType.Unit);

    sealed partial class FunctionLowering {
        /// <summary>
        ///     'Task.installCancellationHandler(handler)': the closure kept in
        ///     an object the runtime holds, and runs if the task is cancelled
        ///     (at once, if it already is), until the handler is removed.
        /// </summary>
        SilValue? InstallCancellationHandler(BoundCall call) {
            if (Expression(call.Arguments[0]) is not { Type: ThickFunctionType } body) {
                return null;
            }

            var handler = generator.CancellationHandler();
            var made = Emit(new AllocRefInst(handler));
            Emit(new StoreInst(Owned(body), Emit(new RefElementAddrInst(made, 0)), StoreKind.Assign));
            Emit(new BuiltinInst("task_handler_push", SilType.Unit, Emit(new FunctionRefInst(HandlerRunName, HandlerRunType)), made));
            return UnitValue.Instance;
        }

        /// <summary>
        ///     'Task.runBlocking(work)': the closure kept in an object, which
        ///     the frame holds while the task waits and the blocking pool runs
        ///     it through the same function a cancellation handler is run by;
        ///     the task goes on once it has run.
        /// </summary>
        SilValue? RunBlocking(BoundCall call) {
            if (Expression(call.Arguments[0]) is not { Type: ThickFunctionType } body) {
                return null;
            }

            var holder = generator.CancellationHandler();
            var made = Emit(new AllocRefInst(holder));
            Emit(new StoreInst(Owned(body), Emit(new RefElementAddrInst(made, 0)), StoreKind.Assign));
            Emit(new BuiltinInst("task_blocking", SilType.Unit, Emit(new FunctionRefInst(HandlerRunName, HandlerRunType)), made));
            Destroy(made);
            return UnitValue.Instance;
        }

        /// <summary>
        ///     'Task.spawn { … }' and 'Task.detached { … }', with a priority
        ///     or not: a task made, its body kept in it, the runtime given it
        ///     to run — as a child of the running task, or detached.
        /// </summary>
        SilValue? Spawn(BoundCall call, bool detached) {
            if (Sub(call.Type) is not NamedTypeSymbol { TypeArguments: [var success] } || generator.TaskClass(success) is not { } task) {
                return Missing(call, $"'{Describe(call.Syntax)}'");
            }

            // A priority given is its raw value; -1 has the runtime choose.
            SilValue priority;
            if (call.Arguments.Length == 2) {
                if (Expression(call.Arguments[0]) is not { Type: StructType { Fields.Count: 1 } } given) {
                    return null;
                }

                priority = Emit(new StructExtractInst(given, 0));
            } else {
                priority = Emit(new IntegerLiteralInst(SilType.Int, -1));
            }

            if (Expression(call.Arguments[^1]) is not { Type: ThickFunctionType } body) {
                return null;
            }

            var made = Emit(new AllocRefInst(task));
            Emit(new StoreInst(Owned(AsFunction(body, task.Fields[TaskBody].Type)), Emit(new RefElementAddrInst(made, TaskBody)), StoreKind.Assign));
            var optional = (EnumType)task.Fields[TaskResult].Type;
            Emit(new StoreInst(Emit(new EnumInst(optional, optional.CaseIndex("None"), null)), Emit(new RefElementAddrInst(made, TaskResult)), StoreKind.Assign));

            // The runtime holds a reference of its own until the task has run.
            var lent = Emit(new CopyValueInst(made));
            var run = Emit(new FunctionRefInst(TaskRunName(task), RunType(task)));
            var handle = Emit(new BuiltinInst("task_spawn", SilType.Int, run, lent, priority, Emit(new IntegerLiteralInst(SilType.Bool, detached ? 1 : 0))));
            Emit(new StoreInst(handle, Emit(new RefElementAddrInst(made, TaskHandle)), StoreKind.Assign));
            return made;
        }

        /// <summary>
        ///     'async let x = value': the value's closure run as a child task
        ///     of the running one, which this scope holds in a slot of its own.
        ///     As the scope ends, on every way out, the child is cancelled —
        ///     which does nothing once it has finished — and waited for: it
        ///     never outlives the scope.
        /// </summary>
        void AsyncLet(BoundLocalDeclaration declaration) {
            if (declaration.Initializer is not BoundClosure { Type: FunctionTypeSymbol child } closureExpression
                || generator.TaskClass(child.ReturnType, child.ThrownType) is not { } task) {
                Missing(declaration, $"the 'async let' '{declaration.Local.Name}'");
                return;
            }

            if (Expression(closureExpression) is not { Type: ThickFunctionType } body) {
                return;
            }

            var made = Emit(new AllocRefInst(task));
            Emit(new StoreInst(Owned(AsFunction(body, task.Fields[TaskBody].Type)), Emit(new RefElementAddrInst(made, TaskBody)), StoreKind.Assign));
            foreach (var field in (int[])[TaskResult, TaskError]) {
                if (field < task.Fields.Count && task.Fields[field].Type is EnumType optional) {
                    Emit(new StoreInst(Emit(new EnumInst(optional, optional.CaseIndex("None"), null)), Emit(new RefElementAddrInst(made, field)), StoreKind.Assign));
                }
            }

            var lent = Emit(new CopyValueInst(made));
            var run = Emit(new FunctionRefInst(TaskRunName(task), RunType(task)));
            var handle = Emit(new BuiltinInst("task_spawn", SilType.Int, run, lent, Emit(new IntegerLiteralInst(SilType.Int, -1)), Emit(new IntegerLiteralInst(SilType.Bool, 0))));
            Emit(new StoreInst(handle, Emit(new RefElementAddrInst(made, TaskHandle)), StoreKind.Assign));

            var slot = NewSlot(task, declaration.Local.Name);
            Emit(new StoreInst(made, slot, StoreKind.Init));
            _asyncLets[declaration.Local] = slot;
            EndsWithScope(slot);
            _scopes[^1].Add(new AsyncLetEnd(slot));
        }

        /// <summary>An 'async let''s child, waited for and, if it has not finished, cancelled first, as its scope ends.</summary>
        sealed record AsyncLetEnd(AllocStackInst Slot);

        /// <summary>Each 'async let' in scope: the slot holding its child task.</summary>
        readonly Dictionary<LocalSymbol, AllocStackInst> _asyncLets = [];

        void EndAsyncLet(AsyncLetEnd end) {
            var task = Emit(new LoadBorrowInst(end.Slot));
            var handle = Emit(new LoadInst(Emit(new RefElementAddrInst(task, TaskHandle))));
            Emit(new BuiltinInst("task_cancel", SilType.Unit, handle));
            Emit(new BuiltinInst("task_wait", SilType.Unit, handle));
            Emit(new EndBorrowInst(task));
        }

        /// <summary>A read of an 'async let': its child's result, waiting for it — or the error it threw.</summary>
        SilValue? AsyncLetValue(BoundLocalAccess access, AllocStackInst slot) =>
            Waited(Hold(Emit(new LoadBorrowInst(slot))), (ClassType)slot.ElementType, access.Syntax);

        /// <summary>'task.handle', Sable.Core's own: the runtime's handle, which a group is given.</summary>
        SilValue? HandleOf(BoundFieldAccess access) {
            if (access.Receiver is null || Borrow(access.Receiver) is not { Type: ClassType } receiver) {
                return Missing(access, "this task's handle");
            }

            var handle = Emit(new LoadInst(Emit(new RefElementAddrInst(receiver, TaskHandle))));
            End(receiver);
            return handle;
        }

        /// <summary>'task.cancel()': the runtime told, through the task's handle.</summary>
        SilValue? CancelTask(BoundCall call) {
            if (call.Receiver is null || Borrow(call.Receiver) is not { Type: ClassType } receiver) {
                return Missing(call, $"'{Describe(call.Syntax)}'");
            }

            Emit(new BuiltinInst("task_cancel", SilType.Unit, Emit(new LoadInst(Emit(new RefElementAddrInst(receiver, TaskHandle))))));
            End(receiver);
            return UnitValue.Instance;
        }

        /// <summary>'await task.value': the task waited for, then its result, copied.</summary>
        SilValue? TaskValue(BoundFieldAccess access) {
            if (access.Receiver is null || Borrow(access.Receiver) is not { Type: ClassType task } receiver) {
                return Missing(access, "this task's value");
            }

            return Waited(receiver, task, access.Syntax);
        }

        /// <summary>
        ///     The result of the task <paramref name="receiver" /> borrows,
        ///     waiting for it to finish; the borrow ended. A task that keeps
        ///     an error, and has one, throws it here instead.
        /// </summary>
        SilValue? Waited(SilValue receiver, ClassType task, SyntaxNode at) {
            Emit(new BuiltinInst("task_wait", SilType.Unit, Emit(new LoadInst(Emit(new RefElementAddrInst(receiver, TaskHandle))))));
            if (task.Fields.Count > TaskError && task.Fields[TaskError].Type is EnumType optionalError) {
                var kept = Emit(new LoadInst(Emit(new RefElementAddrInst(receiver, TaskError))));
                var failed = function.AddBlock();
                var thrown = Payload(failed, optionalError.Cases[optionalError.CaseIndex("Some")].Payload!);
                var fine = function.AddBlock();
                Jump(new SwitchEnumInst(kept, [(optionalError.CaseIndex("Some"), failed)], fine));
                _current = failed;
                var error = Owned(thrown);
                if (!ReferenceEquals(error, thrown) || thrown.Type.IsTrivial) {
                    Destroy(kept);
                }

                // The borrow is held: on the way to the handler it ends with the temporaries.
                RouteError(error, at);
                if (_current is not null) {
                    Jump(new UnreachableInst());
                }

                _current = fine;
                Destroy(kept);
            }

            var optional = (EnumType)task.Fields[TaskResult].Type;
            var result = Emit(new LoadInst(Emit(new RefElementAddrInst(receiver, TaskResult))));
            End(receiver);

            var some = function.AddBlock();
            var payloadType = optional.Cases[optional.CaseIndex("Some")].Payload;
            var payload = payloadType is null ? null : Payload(some, payloadType);
            var none = function.AddBlock();
            Jump(new SwitchEnumInst(result, [(optional.CaseIndex("Some"), some)], none));
            _current = none;
            Destroy(result);
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), "a task finished without a result"));
            Jump(new UnreachableInst());
            _current = some;

            // A task whose body gives nothing has finished, and that is all
            // there is to know: its value is Void.
            if (payload is null) {
                return UnitValue.Instance;
            }

            var copied = Owned(payload);
            if (!ReferenceEquals(copied, payload) || payload.Type.IsTrivial) {
                Destroy(result);
            }

            return copied;
        }

        /// <summary>
        ///     'Counter()' for a class or actor that declares no initialiser:
        ///     the object, each field given its initial value.
        /// </summary>
        SilValue? ImplicitInit(BoundCall call, ClassType classType) {
            if (classType.Superclass is not null || Sub(call.Type) is not NamedTypeSymbol owner) {
                return Missing(call, $"the implicit initialiser of '{Sub(call.Type).ToDisplayString()}', which has a superclass");
            }

            var made = Emit(new AllocRefInst(classType));
            foreach (var field in owner.OriginalDefinition.Members.OfType<FieldSymbol>()) {
                if (field is not { IsStatic: false, IsComputed: false } || generator.FieldInitializer(field) is not { } initial) {
                    continue;
                }

                var index = classType.FieldIndex(field.Name);
                if (index < 0 || Expression(initial) is not { } value) {
                    continue;
                }

                // A weak or unowned field is given the object without taking the value.
                var address = Emit(new RefElementAddrInst(made, index));
                switch (classType.Fields[index].Type) {
                    case WeakStorageType:
                        Emit(new StoreWeakInst(value, address));
                        Destroy(value);
                        break;

                    case UnownedStorageType:
                        Emit(new StoreUnownedInst(value, address));
                        Destroy(value);
                        break;

                    default:
                        Emit(new StoreInst(Owned(value), address, StoreKind.Assign));
                        break;
                }
            }

            return made;
        }

        /// <summary>'flags.contains(other)': '(flags & other) == other', on the integers they are.</summary>
        SilValue? FlagsContains(BoundCall call, NamedTypeSymbol flagsType) {
            if (call is not { Receiver: { } receiver, Arguments: [var argument] } || LowerType(flagsType) is not { } raw
                || SilBuiltins.Find($"and_{raw}") is null || SilBuiltins.Find($"cmp_eq_{raw}") is null) {
                return Missing(call, $"'contains' on '{flagsType.Name}'");
            }

            if (Borrow(receiver) is not { } flags) {
                return null;
            }

            if (Borrow(argument) is not { } other) {
                End(flags);
                return null;
            }

            var common = Emit(new BuiltinInst($"and_{raw}", raw, flags, other));
            var contains = Emit(new BuiltinInst($"cmp_eq_{raw}", SilType.Bool, common, other));
            End(other);
            End(flags);
            return contains;
        }

        /// <summary>
        ///     'super.init()' to a superclass that declares no initialiser:
        ///     its fields' initial values, stored into this object — and its
        ///     own superclass's, had it one, by the initialiser it inherits.
        /// </summary>
        SilValue? SuperImplicitInit(BoundCall call, BoundSuperAccess super) {
            if (SelfType(symbol, substitution) is not { } selfType) {
                return Missing(call, "'super.init()' here");
            }

            foreach (var field in super.BaseType.OriginalDefinition.Members.OfType<FieldSymbol>()) {
                if (field is not { IsStatic: false, IsComputed: false, HasInitializer: true } || generator.FieldInitializer(field) is not { } initial) {
                    continue;
                }

                var self = new BoundSelfAccess(initial.Syntax, selfType, true);
                Statement(new BoundExpressionStatement(initial.Syntax, new BoundAssignment(initial.Syntax, new BoundFieldAccess(initial.Syntax, self, field), initial)));
            }

            return UnitValue.Instance;
        }
    }
}
