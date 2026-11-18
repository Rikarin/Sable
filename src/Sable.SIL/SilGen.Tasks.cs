using Sable.Symbols;

namespace Sable.SIL;

/// <summary>
///     Tasks (docs/02 §2.7): <c>Task&lt;T&gt;</c> is a class holding the
///     runtime's handle, the body to run and, once run, its result. A thunk
///     per result type runs the body on the task's own stack and stores what
///     it gives. The runtime schedules tasks cooperatively; nothing else about
///     async needs the compiler, since an async function is an ordinary one
///     that may reach a point where its task waits.
/// </summary>
public sealed partial class SilGen {
    const int TaskHandle = 0, TaskResult = 1, TaskBody = 2;

    /// <summary>The class a <c>Task&lt;T&gt;</c> is, and the thunk that runs one.</summary>
    internal ClassType? TaskClass(TypeSymbol success) {
        if (Lower(success) is not { } result || Lower(new OptionalTypeSymbol(success)) is not EnumType optional) {
            return null;
        }

        var name = $"Task$of${Sanitise(result.ToString())}";
        if (_module.FindType(name) is ClassType existing) {
            return existing;
        }

        var task = _module.AddType(new ClassType(name));
        task.Fields.Add(("handle", SilType.Int));
        task.Fields.Add(("result", optional));
        task.Fields.Add(("body", new ThickFunctionType(new FunctionType([], result))));

        // Its deinit gives the runtime's task back.
        var deinit = new SilFunction($"$task.deinit${Sanitise(result.ToString())}", new FunctionType([task], SilType.Unit));
        var entry = deinit.AddBlock();
        var self = entry.AddArgument(task);
        var handle = entry.Append(new LoadInst(entry.Append(new RefElementAddrInst(self, TaskHandle))));
        entry.Append(new BuiltinInst("task_release", SilType.Unit, handle));
        entry.Append(new ReturnInst(null));
        _module.Add(deinit);
        task.Deinit = deinit.Name;

        // Run on the task: the body called, its result kept, the task's reference given back.
        var run = new SilFunction(TaskRunName(task), new FunctionType([task], SilType.Unit, null, [true]));
        var block = run.AddBlock();
        var owned = block.AddArgument(task);
        var body = block.Append(new LoadInst(block.Append(new RefElementAddrInst(owned, TaskBody))));
        var value = block.Append(new ApplyInst(body, result));
        block.Append(new DestroyValueInst(body));
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

    sealed partial class FunctionLowering {
        /// <summary>'Task.spawn { … }': a task made, its body kept in it, the runtime given it to run.</summary>
        SilValue? Spawn(BoundCall call) {
            if (Sub(call.Type) is not NamedTypeSymbol { TypeArguments: [var success] } || generator.TaskClass(success) is not { } task) {
                return Missing(call, $"'{Describe(call.Syntax)}'");
            }

            if (Expression(call.Arguments[0]) is not { Type: ThickFunctionType } body) {
                return null;
            }

            var made = Emit(new AllocRefInst(task));
            Emit(new StoreInst(Owned(body), Emit(new RefElementAddrInst(made, TaskBody)), StoreKind.Assign));
            var optional = (EnumType)task.Fields[TaskResult].Type;
            Emit(new StoreInst(Emit(new EnumInst(optional, optional.CaseIndex("None"), null)), Emit(new RefElementAddrInst(made, TaskResult)), StoreKind.Assign));

            // The runtime holds a reference of its own until the task has run.
            var lent = Emit(new CopyValueInst(made));
            var run = Emit(new FunctionRefInst(TaskRunName(task), new FunctionType([task], SilType.Unit, null, [true])));
            var handle = Emit(new BuiltinInst("task_spawn", SilType.Int, run, lent));
            Emit(new StoreInst(handle, Emit(new RefElementAddrInst(made, TaskHandle)), StoreKind.Assign));
            return made;
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

            Emit(new BuiltinInst("task_wait", SilType.Unit, Emit(new LoadInst(Emit(new RefElementAddrInst(receiver, TaskHandle))))));
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
