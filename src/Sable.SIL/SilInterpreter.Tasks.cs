namespace Sable.SIL;

/// <summary>
///     Tasks, interpreted as the runtime runs them: one at a time, handed the
///     turn at the same points in the same order — so the interpreter's
///     interleaving is the native one. As natively, a task is given a thread
///     only when it first runs, a thread whose task finished runs the next
///     task that has not begun itself or waits to be given one, and a
///     sleeping task is off the run queue until its time comes.
/// </summary>
public sealed partial class SilInterpreter {
    sealed class InterpretedTask(SilFunction? function = null, object? context = null) {
        public SilFunction? Function => function;
        public object? Context => context;
        public SemaphoreSlim Wake { get; } = new(0);
        public bool Started, Done, Cancelled;
        public long Deadline;
        public List<InterpretedTask> Waiters { get; } = [];
    }

    /// <summary>A thread that runs tasks: the one it runs, or none while it waits for one.</summary>
    sealed class Worker {
        public SemaphoreSlim Wake { get; } = new(0);
        public InterpretedTask? Task;
    }

    readonly Queue<InterpretedTask> _runQueue = new();
    readonly List<InterpretedTask> _sleepers = []; // by deadline, the earliest first
    readonly Stack<Worker> _idleWorkers = new();
    readonly Dictionary<long, InterpretedTask> _tasks = [];
    InterpretedTask? _mainTask, _currentTask;
    Exception? _taskFailure;

    InterpretedTask Current => _currentTask ??= _mainTask ??= new InterpretedTask { Started = true };

    static long Monotonic() => System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000L / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>Every sleeper whose time has come, queued in the order of their deadlines.</summary>
    void WakeSleepers() {
        if (_sleepers.Count == 0) {
            return;
        }

        var now = Monotonic();
        while (_sleepers.Count > 0 && _sleepers[0].Deadline <= now) {
            _runQueue.Enqueue(_sleepers[0]);
            _sleepers.RemoveAt(0);
        }
    }

    /// <summary>Nothing is queued: waits for the earliest sleeper.</summary>
    void WaitForSleeper() {
        var left = _sleepers[0].Deadline - Monotonic();
        if (left > 0) {
            Thread.Sleep(TimeSpan.FromTicks((left + 99) / 100));
        }

        WakeSleepers();
    }

    /// <summary>The task to run next: the first queued, once the sleepers due are.</summary>
    InterpretedTask NextTask() {
        WakeSleepers();
        while (_runQueue.Count == 0 && _sleepers.Count > 0) {
            WaitForSleeper();
        }

        return _runQueue.TryDequeue(out var next)
            ? next
            : throw new SilTrap("every task is waiting: the program cannot go on", located: false);
    }

    /// <summary>
    ///     Makes the task the running one: its thread woken, or, for one that
    ///     has not begun, an idle thread given it, or a new one started for
    ///     it. The caller touches nothing shared after this.
    /// </summary>
    void Resume(InterpretedTask next) {
        _currentTask = next;
        if (next.Started) {
            next.Wake.Release();
            return;
        }

        next.Started = true;
        if (_idleWorkers.TryPop(out var idle)) {
            idle.Task = next;
            idle.Wake.Release();
            return;
        }

        var worker = new Worker { Task = next };

        // Each SIL call is a .NET call here: a task's thread needs the room
        // the entry's has (see Run).
        new Thread(() => Work(worker), 64 * 1024 * 1024) { IsBackground = true }.Start();
    }

    void SwitchAway() {
        var self = Current;
        var next = NextTask();
        if (next != self) {
            Resume(next);
            self.Wake.Wait();
        }

        if (_taskFailure is { } failure) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    ///     Runs tasks: the one it was started for, then, as each finishes, the
    ///     next if it has not begun; otherwise it hands over and waits to be
    ///     given one. A trap in a task ends the program: the turn goes to the
    ///     entry's task, which reports it.
    /// </summary>
    void Work(Worker self) {
        var task = self.Task!;
        while (true) {
            try {
                if (_taskFailure is null) {
                    Call(task.Function!, [task.Context], 0);
                }
            } catch (Exception exception) {
                _taskFailure ??= exception;
            }

            // Finished: its waiters go on, and the turn passes.
            task.Done = true;
            foreach (var waiter in task.Waiters) {
                _runQueue.Enqueue(waiter);
            }

            task.Waiters.Clear();
            InterpretedTask? next = null;
            if (_taskFailure is null) {
                try {
                    next = NextTask();
                } catch (SilTrap trap) {
                    _taskFailure = trap;
                }
            }

            if (next is null) {
                _currentTask = _mainTask!;
                _mainTask!.Wake.Release();
                return;
            }

            if (!next.Started) {
                next.Started = true;
                _currentTask = task = next;
                continue;
            }

            // Idle before handing over: whoever runs next may give this thread a task.
            self.Task = null;
            _idleWorkers.Push(self);
            Resume(next);
            self.Wake.Wait();
            task = self.Task!;
        }
    }

    object? Task(string name, object?[] operands) {
        switch (name) {
            case "task_spawn": {
                var function = module.Find((string)operands[0]!) ?? throw new SilTrap($"call to undefined @{operands[0]}");
                var task = new InterpretedTask(function, operands[1]);
                var handle = (long)_tasks.Count + 1;
                _tasks[handle] = task;
                _ = Current;
                _runQueue.Enqueue(task);
                return handle;
            }

            case "task_wait": {
                var task = _tasks[(long)operands[0]!];
                while (!task.Done) {
                    task.Waiters.Add(Current);
                    SwitchAway();
                }

                return null;
            }

            case "task_yield":
                WakeSleepers();
                if (_runQueue.Count > 0) {
                    _runQueue.Enqueue(Current);
                    SwitchAway();
                }

                return null;

            // Marked, and woken now if it sleeps, as a sleeper whose time has come would be.
            case "task_cancel": {
                var task = _tasks[(long)operands[0]!];
                if (!task.Done && !task.Cancelled) {
                    task.Cancelled = true;
                    if (_sleepers.Remove(task)) {
                        _runQueue.Enqueue(task);
                    }
                }

                return null;
            }

            case "task_is_cancelled":
                return Current.Cancelled;

            // A cancelled task does not sleep.
            case "task_sleep": {
                var nanoseconds = (long)operands[0]!;
                if (nanoseconds <= 0 || Current.Cancelled) {
                    return Task("task_yield", []);
                }

                var self = Current;
                self.Deadline = Monotonic() + nanoseconds;
                var at = _sleepers.FindIndex(s => s.Deadline > self.Deadline);
                _sleepers.Insert(at < 0 ? _sleepers.Count : at, self);
                SwitchAway();
                return null;
            }

            case "task_drain":
                while (true) {
                    WakeSleepers();
                    if (_runQueue.Count == 0 && _sleepers.Count == 0) {
                        return null;
                    }

                    if (_runQueue.Count == 0) {
                        WaitForSleeper();
                        continue;
                    }

                    _runQueue.Enqueue(Current);
                    SwitchAway();
                }

            default:
                return null;
        }
    }
}
