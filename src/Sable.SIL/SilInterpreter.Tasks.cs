namespace Sable.SIL;

/// <summary>
///     Tasks, interpreted as the runtime runs them (runtime/tasks.c): one
///     executor, on one thread, that resumes each task that can go on, in the
///     same order — so the interpreter's interleaving is the native one. A
///     task is its root frame, made from its start function when it first
///     runs and polled by its resume function each time it is resumed
///     (<see cref="AsyncLowering" />); its frames come from a slab of its own.
/// </summary>
public sealed partial class SilInterpreter {
    sealed class InterpretedTask(string? start = null, string? resume = null, object? context = null) {
        public string? Start => start;
        public string? Resume => resume;
        public object? Context => context;

        /// <summary>The root frame, once the task has begun.</summary>
        public HeapObject? Frame;

        public bool Done, Cancelled, Sleeping;
        public long Deadline, SleepTicket;
        public List<InterpretedTask> Waiters { get; } = [];

        /// <summary>The task's frames, the most recent last: pushed and popped as its calls begin and end.</summary>
        public Stack<HeapObject> Frames { get; } = new();
    }

    readonly Queue<InterpretedTask> _runQueue = new();

    // The sleepers, by deadline and then by when they began to sleep; a
    // ticket that is no longer the task's is one it has left (cancelled).
    readonly PriorityQueue<(InterpretedTask Task, long Ticket), (long Deadline, long Ticket)> _sleepers = new();
    readonly Dictionary<long, InterpretedTask> _tasks = [];
    InterpretedTask? _currentTask;
    long _nextHandle, _nextTicket;

    /// <summary>The entry's own task: what runs when no task does, and an async entry point's frames' owner.</summary>
    readonly InterpretedTask _mainTask = new();

    InterpretedTask Current => _currentTask ??= _mainTask;

    /// <summary>The most frames a task has held at once, and how many it holds now, across tasks: what the slab tests see.</summary>
    public int MostFrames { get; private set; }

    static long Monotonic() => System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000L / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>A frame for the running task, on its slab.</summary>
    HeapObject PushFrame(ClassType type) {
        var frame = new HeapObject(type);
        var frames = Current.Frames;
        frames.Push(frame);
        MostFrames = Math.Max(MostFrames, frames.Count);
        return frame;
    }

    /// <summary>A frame given back: the most recent of its task's, as a slab's must be.</summary>
    void PopFrame(InterpretedTask task, HeapObject frame) {
        if (!task.Frames.TryPeek(out var top) || !ReferenceEquals(top, frame)) {
            throw new SilTrap("an async frame freed out of order", located: false);
        }

        task.Frames.Pop();
        frame.Freed = true;
    }

    bool SleepersDue(long now) =>
        _sleepers.TryPeek(out _, out var key) && key.Deadline <= now;

    /// <summary>Every sleeper whose time has come, queued in the order of their deadlines.</summary>
    void WakeSleepers() {
        if (_sleepers.Count == 0) {
            return;
        }

        var now = Monotonic();
        while (SleepersDue(now)) {
            var (task, ticket) = _sleepers.Dequeue();
            if (task.Sleeping && task.SleepTicket == ticket) {
                task.Sleeping = false;
                _runQueue.Enqueue(task);
            }
        }
    }

    /// <summary>True while a sleeper is still asleep: the stale entries of those woken early are dropped.</summary>
    bool AnySleeping() {
        while (_sleepers.TryPeek(out var entry, out _)) {
            if (entry.Task.Sleeping && entry.Task.SleepTicket == entry.Ticket) {
                return true;
            }

            _sleepers.Dequeue();
        }

        return false;
    }

    /// <summary>Nothing is queued: waits for the earliest sleeper.</summary>
    void WaitForSleeper() {
        _sleepers.TryPeek(out _, out var key);
        var left = key.Deadline - Monotonic();
        if (left > 0) {
            Thread.Sleep(TimeSpan.FromTicks((left + 99) / 100));
        }

        WakeSleepers();
    }

    /// <summary>
    ///     The executor: resumes each task queued, in turn, until none can go
    ///     on — or, with <paramref name="until" />, until that task can.
    /// </summary>
    void RunTasks(InterpretedTask? until) {
        while (true) {
            WakeSleepers();
            if (!_runQueue.TryDequeue(out var task)) {
                if (AnySleeping()) {
                    WaitForSleeper();
                    continue;
                }

                if (until is null) {
                    return;
                }

                throw new SilTrap("every task is waiting: the program cannot go on", located: false);
            }

            if (ReferenceEquals(task, until)) {
                return;
            }

            Step(task);
        }
    }

    /// <summary>Resumes a task until it finishes or waits: its root frame made first, the first time.</summary>
    void Step(InterpretedTask task) {
        var previous = _currentTask;
        _currentTask = task;
        try {
            if (task.Frame is null) {
                var start = module.Find(task.Start!) ?? throw new SilTrap($"call to undefined @{task.Start}");
                task.Frame = (HeapObject)Call(start, [task.Context], 0)!;
            }

            var resume = module.Find(task.Resume!) ?? throw new SilTrap($"call to undefined @{task.Resume}");
            if (!(bool)Call(resume, [task.Frame], 0)!) {
                return;
            }

            // Finished: its frame goes, and whoever waits for it may go on.
            PopFrame(task, task.Frame);
            task.Frame = null;
            task.Done = true;
            foreach (var waiter in task.Waiters) {
                _runQueue.Enqueue(waiter);
            }

            task.Waiters.Clear();
        } finally {
            _currentTask = previous;
        }
    }

    bool Yield() {
        WakeSleepers();
        if (_runQueue.Count == 0) {
            return false;
        }

        _runQueue.Enqueue(Current);
        return true;
    }

    object? Task(string name, object?[] operands) {
        switch (name) {
            case "task_spawn": {
                var task = new InterpretedTask((string)operands[0]!, (string)operands[1]!, operands[2]);
                var handle = ++_nextHandle;
                _tasks[handle] = task;
                _runQueue.Enqueue(task);
                return handle;
            }

            // Whether the running task must wait for this one: if so, it is
            // among its waiters, and resumed once it has finished.
            case "async_wait": {
                var task = _tasks[(long)operands[0]!];
                if (task.Done) {
                    return false;
                }

                task.Waiters.Add(Current);
                return true;
            }

            case "async_yield":
                return Yield();

            // A cancelled task does not sleep.
            case "async_sleep": {
                var nanoseconds = (long)operands[0]!;
                if (nanoseconds <= 0 || Current.Cancelled) {
                    return Yield();
                }

                var self = Current;
                self.Deadline = Monotonic() + nanoseconds;
                self.Sleeping = true;
                self.SleepTicket = ++_nextTicket;
                _sleepers.Enqueue((self, self.SleepTicket), (self.Deadline, self.SleepTicket));
                return true;
            }

            case "async_frame_free":
                PopFrame(Current, (HeapObject)operands[0]!);
                return null;

            // Marked, and woken now if it sleeps, as a sleeper whose time has come would be.
            case "task_cancel": {
                var task = _tasks[(long)operands[0]!];
                if (!task.Done && !task.Cancelled) {
                    task.Cancelled = true;
                    if (task.Sleeping) {
                        task.Sleeping = false;
                        _runQueue.Enqueue(task);
                    }
                }

                return null;
            }

            case "task_is_cancelled":
                return Current.Cancelled;

            // The program no longer holds it: once it has finished, nothing does.
            case "task_release":
                _tasks.Remove((long)operands[0]!);
                return null;

            case "task_run_until_main":
                RunTasks(_mainTask);
                return null;

            case "task_drain":
                RunTasks(null);
                return null;

            default:
                return null;
        }
    }
}
