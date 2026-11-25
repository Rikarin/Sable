using Sable.Symbols;

namespace Sable.SIL;

/// <summary>
///     Threads, interpreted (runtime/parallel.c): the interpreter has one, so
///     a thread a program starts runs to its end as it is started, and a loop
///     several threads would share is run once, by the thread that began it.
///     One that waits for another to do something — parks with no token — can
///     never go on: a trap that says so. Thread-locals are the thread's that
///     asks, which the one started is while it runs.
/// </summary>
public sealed partial class SilInterpreter {
    sealed class InterpretedThread(long id, string name) {
        public long Id => id;
        public string Name => name;
        public bool Ended, Token;

        /// <summary>Its thread-locals, the latest made last: where each is, and what destroys it.</summary>
        public List<(long Key, long Cell, string Destroy, HeapObject Holder)> Locals { get; } = [];
    }

    /// <summary>A binding of a task-local: held by each task that has it, and the binding made inside it.</summary>
    sealed class TaskLocal(long key, long cell, string destroy, HeapObject holder, TaskLocal? outer) {
        public long Key => key;
        public long Cell => cell;
        public string Destroy => destroy;
        public HeapObject Holder => holder;
        public TaskLocal? Outer => outer;
        public int References = 1;
    }

    readonly Dictionary<long, InterpretedThread> _threads = [];
    readonly InterpretedThread _mainThread = new(1, "main");
    readonly HashSet<long> _blocksDone = [];
    InterpretedThread? _currentThread;
    long _nextThread = 1, _nextLocal;

    InterpretedThread CurrentThread => _currentThread ??= _mainThread;

    static TaskLocal? Retained(TaskLocal? local) {
        if (local is not null) {
            local.References++;
        }

        return local;
    }

    void ReleaseLocal(TaskLocal? local) {
        while (local is not null && --local.References == 0) {
            RunHeld(local.Destroy, local.Holder, 0);
            local = local.Outer;
        }
    }

    void LeaveLocals(InterpretedTask task) {
        var locals = task.Locals;
        task.Locals = null;
        ReleaseLocal(locals);
    }

    /// <summary>A closure the runtime was given, run and let go of.</summary>
    void RunHeld(string run, HeapObject holder, int depth) {
        Call(module.Find(run) ?? throw new SilTrap($"call to undefined @{run}"), [holder], depth + 1);
        Release(holder.Type, holder, depth);
    }

    /// <summary>A thread's locals destroyed, the latest made first, with those their deinits make: whether there were any.</summary>
    bool DestroyLocals(InterpretedThread thread) {
        var any = false;
        for (var left = 1 << 20; thread.Locals.Count > 0 && left > 0; left--) {
            var local = thread.Locals[^1];
            thread.Locals.RemoveAt(thread.Locals.Count - 1);
            RunHeld(local.Destroy, local.Holder, 0);
            any = true;
        }

        return any;
    }

    bool EndThreadLocals() => DestroyLocals(_mainThread);

    object? ThreadBuiltin(string name, object?[] operands, int depth) {
        switch (name) {
            case "thread_new": {
                var id = ++_nextThread;
                _threads[id] = new(id, Text(operands[0]).Value);
                return id;
            }

            case "thread_release":
                return null;

            case "thread_current":
                _threads[CurrentThread.Id] = CurrentThread;
                return CurrentThread.Id;

            case "thread_id":
                return operands[0];

            case "thread_name":
                return Allocate(new HeapString(_threads[(long)operands[0]!].Name));

            // It runs to its end, as a task of its own would.
            case "thread_start": {
                var (thread, outer, task) = (_threads[(long)operands[2]!], CurrentThread, _currentTask);
                _currentThread = thread;
                _currentTask = new();
                try {
                    RunHeld((string)operands[0]!, (HeapObject)operands[1]!, depth);
                    DestroyLocals(thread);
                } finally {
                    _currentThread = outer;
                    _currentTask = task;
                }

                thread.Ended = true;
                return null;
            }

            case "thread_join": {
                var thread = _threads[(long)operands[0]!];
                if (ReferenceEquals(thread, CurrentThread)) {
                    throw new SilTrap("a thread waits for itself to end");
                }

                if (!thread.Ended && (long)operands[1]! < 0) {
                    throw new SilTrap("a thread is waited for that waits for this one: the interpreter runs one to its end as it is started");
                }

                return thread.Ended;
            }

            case "thread_ended":
                return _threads[(long)operands[0]!].Ended;

            case "thread_park": {
                var thread = CurrentThread;
                if (!thread.Token && (long)operands[0]! < 0) {
                    throw new SilTrap("a thread parks with no token: the interpreter runs one thread at a time, so no other would give it one");
                }

                var token = thread.Token;
                thread.Token = false;
                return token;
            }

            case "thread_unpark":
                _threads[(long)operands[0]!].Token = true;
                return null;

            case "thread_yield" or "thread_loop_more":
                return null;

            // As with SABLE_WORKERS=0: the one thread runs every task.
            case "thread_workers":
                return 0L;

            case "thread_loop":
                RunHeld((string)operands[0]!, (HeapObject)operands[1]!, depth);
                return null;

            case "thread_block_new":
                return ++_nextHandle;

            case "thread_block_done":
                _blocksDone.Add((long)operands[0]!);
                return null;

            case "thread_block_wait": {
                var block = (long)operands[0]!;
                RunTasks(null, () => _blocksDone.Contains(block));
                _blocksDone.Remove(block);
                return null;
            }

            case "thread_local_key" or "task_local_key":
                return ++_nextLocal;

            case "thread_local_find": {
                var found = CurrentThread.Locals.FindIndex(l => l.Key == (long)operands[0]!);
                return found < 0 ? 0L : CurrentThread.Locals[found].Cell;
            }

            case "thread_local_set":
                CurrentThread.Locals.Add(((long)operands[2]!, (long)operands[3]!, (string)operands[0]!, (HeapObject)operands[1]!));
                return null;

            case "thread_local_free": {
                var locals = CurrentThread.Locals;
                var found = locals.FindIndex(l => l.Key == (long)operands[0]!);
                if (found >= 0) {
                    var local = locals[found];
                    locals.RemoveAt(found);
                    RunHeld(local.Destroy, local.Holder, depth);
                }

                return null;
            }

            case "task_local_push":
                Current.Locals = new((long)operands[2]!, (long)operands[3]!, (string)operands[0]!, (HeapObject)operands[1]!, Current.Locals);
                return null;

            case "task_local_pop": {
                var local = Current.Locals!;
                Current.Locals = Retained(local.Outer);
                ReleaseLocal(local);
                return null;
            }

            case "task_local_find": {
                for (var local = Current.Locals; local is not null; local = local.Outer) {
                    if (local.Key == (long)operands[0]!) {
                        return local.Cell;
                    }
                }

                return 0L;
            }

            default:
                throw new SilTrap($"no thread builtin '{name}'");
        }
    }
}
