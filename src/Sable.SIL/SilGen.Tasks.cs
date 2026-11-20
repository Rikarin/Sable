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

        var task = _module.AddType(new ClassType(name));
        task.Fields.Add(("handle", SilType.Int));
        task.Fields.Add(("result", optional));
        task.Fields.Add(("body", new ThickFunctionType(new FunctionType([], result, thrown, isAsync: true))));
        if (optionalError is not null) {
            task.Fields.Add(("error", optionalError));
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

    static string TaskRunName(ClassType task) => $"$task.run${task.Name}";

    /// <summary>What runs a task: async, the root of its task's frames, given the task to keep its result in.</summary>
    static FunctionType RunType(ClassType task) => new([task], SilType.Unit, null, [true], isAsync: true);

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
            Emit(new StoreInst(Owned(body), Emit(new RefElementAddrInst(made, TaskBody)), StoreKind.Assign));
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
            Emit(new StoreInst(Owned(body), Emit(new RefElementAddrInst(made, TaskBody)), StoreKind.Assign));
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
