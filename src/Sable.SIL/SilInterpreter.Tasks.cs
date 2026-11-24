namespace Sable.SIL;

/// <summary>
///     Tasks, interpreted as the runtime runs them (runtime/tasks.c) with no
///     pool (SABLE_WORKERS=0): one executor, on one thread, that resumes each
///     task that can go on, in the same order — by priority band, then in the
///     order they became ready — so the interpreter's interleaving is the
///     native one's with one thread. The interpreter stays on one thread; a
///     program whose output does not depend on how its tasks interleave
///     prints the same natively on the pool. With one thread running at a
///     time, an actor is never held by another task when one hops to it —
///     a task lets its actor go as it suspends — so a hop never waits here. A
///     task is its root frame, made from its start function when it first
///     runs and polled by its resume function each time it is resumed
///     (<see cref="AsyncLowering" />); its frames come from a slab of its own.
///     Tasks are structured as the runtime's are: a child starts with its
///     parent's priority and cancellation, is cancelled with it, and keeps it
///     from finishing until the child has; a group hears of its children as
///     they finish; and a task that can no longer go on as the program ends
///     is dropped, its frames destroyed by their drop functions.
/// </summary>
public sealed partial class SilInterpreter {
    sealed class InterpretedTask(string? start = null, string? resume = null, string? drop = null, object? context = null) {
        public string? Start => start;
        public string? Resume => resume;
        public string? Drop => drop;
        public object? Context => context;

        /// <summary>The root frame, once the task has begun.</summary>
        public HeapObject? Frame;

        public bool Done, Finishing, Cancelled, Sleeping, Queued;
        public long Deadline, SleepTicket, Priority = MediumPriority, Escalated = MediumPriority;

        /// <summary>The task it waits for, which its escalation reaches.</summary>
        public InterpretedTask? WaitingOn;
        public List<InterpretedTask> Waiters { get; } = [];

        /// <summary>Its parent, unless detached; its children not yet finished.</summary>
        public InterpretedTask? Parent;

        public List<InterpretedTask> Children { get; } = [];

        /// <summary>The group it was spawned in, until taken from it.</summary>
        public InterpretedGroup? Group;

        /// <summary>Its cancellation handlers, the latest last: each a function and the object it is given.</summary>
        public List<(string Run, HeapObject Context)> Handlers { get; } = [];

        /// <summary>The task's frames, the most recent last: pushed and popped as its calls begin and end.</summary>
        public Stack<HeapObject> Frames { get; } = new();
    }

    /// <summary>A task group's children (Sable.Core's TaskGroup): those finished and not yet taken, how many run, and who waits.</summary>
    sealed class InterpretedGroup {
        public readonly Queue<long> Done = new();
        public long Running;
        public InterpretedTask? Waiter;
    }

    /// <summary>TaskPriority.medium: the main task's, and a detached task's unless it says.</summary>
    const long MediumPriority = 21;

    /// <summary>The run queue: a queue per priority band, high first, as the runtime's.</summary>
    readonly Queue<InterpretedTask>[] _runQueues = [new(), new(), new(), new()];

    static int Band(long priority) => priority >= 25 ? 0 : priority >= 21 ? 1 : priority >= 17 ? 2 : 3;

    void Enqueue(InterpretedTask task) {
        task.Queued = true;
        _runQueues[Band(task.Escalated)].Enqueue(task);
    }

    bool TryDequeue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out InterpretedTask? task) {
        foreach (var queue in _runQueues) {
            if (queue.TryDequeue(out task)) {
                task.Queued = false;
                return true;
            }
        }

        task = null;
        return false;
    }

    bool AnyQueued => _runQueues.Any(static q => q.Count > 0);

    /// <summary>
    ///     Raised to at least this priority, with what it waits for and its
    ///     children: a higher-priority task waits for it. One queued already
    ///     moves to the back of its new band, as the runtime's does.
    /// </summary>
    void Escalate(InterpretedTask? task, long priority) {
        while (task is { Done: false } && task.Escalated < priority) {
            var from = Band(task.Escalated);
            task.Escalated = priority;
            if (task.Queued && Band(priority) < from) {
                var others = _runQueues[from].Where(t => !ReferenceEquals(t, task)).ToList();
                _runQueues[from].Clear();
                others.ForEach(_runQueues[from].Enqueue);
                Enqueue(task);
            }

            foreach (var child in task.Children) {
                Escalate(child, priority);
            }

            task = task.WaitingOn;
        }
    }

    /// <summary>The wait lists, by handle: tasks parked until woken, in the order they parked.</summary>
    readonly Dictionary<long, List<InterpretedTask>> _waitLists = [];

    // The sleepers, by deadline and then by when they began to sleep; a
    // ticket that is no longer the task's is one it has left (cancelled).
    readonly PriorityQueue<(InterpretedTask Task, long Ticket), (long Deadline, long Ticket)> _sleepers = new();

    // Every task by handle, until the program releases it and it is done;
    // and in the order spawned, while not done.
    readonly Dictionary<long, InterpretedTask> _tasks = [];
    readonly Dictionary<InterpretedTask, long> _handles = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<long> _released = [];
    readonly Dictionary<long, InterpretedGroup> _groups = [];
    InterpretedTask? _currentTask;

    /// <summary>The next task spawned starts cancelled: a cancelled group's child.</summary>
    bool _spawnCancelled;
    long _nextHandle, _nextTicket;

    /// <summary>The entry's own task: what runs when no task does, and an async entry point's frames' owner.</summary>
    readonly InterpretedTask _mainTask = new();

    InterpretedTask Current => _currentTask ??= _mainTask;

    /// <summary>The most frames a task has held at once, and how many it holds now, across tasks: what the slab tests see.</summary>
    public int MostFrames { get; private set; }

    /// <summary>How many tasks were dropped as the program ended, still waiting.</summary>
    public int DroppedTasks { get; private set; }

    static long Monotonic() => System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000L / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>A frame for the running task, on its slab.</summary>
    HeapObject PushFrame(ClassType type) {
        var frame = new HeapObject(type);
        foreach (var slot in type.TailSlots) {
            frame.Fields[slot] = new FrameTail();
        }

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
                Enqueue(task);
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
            PollSockets(0);
            PollSources();
            if (!TryDequeue(out var task)) {
                // A pipe, a child's end or a signal, with whatever else is waited for.
                if (WaitForSources(draining: until is null)) {
                    continue;
                }

                // Waiting for a socket, until the earliest sleeper at the latest.
                if (_ioWaiters.Count > 0) {
                    PollSockets(AnySleeping() && _sleepers.TryPeek(out _, out var earliest) ? earliest.Deadline : -1);
                    continue;
                }

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
        bool finished;
        try {
            if (task.Frame is null) {
                var start = module.Find(task.Start!) ?? throw new SilTrap($"call to undefined @{task.Start}");
                task.Frame = (HeapObject)Call(start, [task.Context], 0)!;
            }

            var resume = module.Find(task.Resume!) ?? throw new SilTrap($"call to undefined @{task.Resume}");
            finished = (bool)Call(resume, [task.Frame], 0)!;
            if (finished) {
                // Its body is done, and its frames go; it is done once its children are.
                PopFrame(task, task.Frame);
                task.Frame = null;
            }
        } finally {
            _currentTask = previous;
        }

        if (finished) {
            if (task.Children.Count > 0) {
                task.Finishing = true;
            } else {
                Complete(task);
            }
        }
    }

    /// <summary>Finished, its children too: its waiters go on, its group hears, and a parent waiting only for it finishes.</summary>
    void Complete(InterpretedTask task) {
        task.Done = true;
        task.Finishing = false;
        foreach (var waiter in task.Waiters) {
            waiter.WaitingOn = null;
            Enqueue(waiter);
        }

        task.Waiters.Clear();
        if (task.Group is { } group) {
            group.Done.Enqueue(_handles[task]);
            group.Running--;
            if (group.Waiter is { } waiter) {
                Enqueue(waiter);
                group.Waiter = null;
            }
        }

        var parent = task.Parent;
        LeaveParent(task);
        Forget(task);
        if (parent is { Finishing: true, Children.Count: 0 }) {
            Complete(parent);
        }
    }

    void LeaveParent(InterpretedTask task) {
        task.Parent?.Children.Remove(task);
        task.Parent = null;
    }

    /// <summary>A task done and released: nothing refers to it any more.</summary>
    void Forget(InterpretedTask task) {
        if (task.Done && _handles.TryGetValue(task, out var handle) && _released.Remove(handle)) {
            _tasks.Remove(handle);
            _handles.Remove(task);
        }
    }

    bool Yield() {
        WakeSleepers();
        if (!AnyQueued) {
            return false;
        }

        Enqueue(Current);
        return true;
    }

    /// <summary>Marked; its handlers run, the latest first; woken if it sleeps; and so on through its children.</summary>
    void Cancel(InterpretedTask task, int depth) {
        if (task.Done || task.Cancelled) {
            return;
        }

        task.Cancelled = true;
        for (var i = task.Handlers.Count - 1; i >= 0; i--) {
            var (run, context) = task.Handlers[i];
            Call(module.Find(run) ?? throw new SilTrap($"call to undefined @{run}"), [context], depth + 1);
        }

        if (task.Sleeping) {
            task.Sleeping = false;
            Enqueue(task);
        }

        IoCancel(task, resume: true);
        foreach (var child in task.Children.ToList()) {
            Cancel(child, depth);
        }
    }

    /// <summary>A task that can no longer go on, as the program ends: its frames destroyed from the root, and given back.</summary>
    void DropTask(InterpretedTask task) {
        IoCancel(task, resume: false);
        if (task.Frame is { } frame) {
            var previous = _currentTask;
            _currentTask = task;
            try {
                Call(module.Find(task.Drop!) ?? throw new SilTrap($"call to undefined @{task.Drop}"), [frame], 0);
                PopFrame(task, frame);
                task.Frame = null;
            } finally {
                _currentTask = previous;
            }
        }

        DroppedTasks++;
        task.Done = true;
        task.Waiters.Clear();
        LeaveParent(task);
        Forget(task);
    }

    /// <summary>As the entry point returns: every task run that can be, then each still waiting dropped, the earliest spawned first.</summary>
    void Drain() {
        RunTasks(null);
        foreach (var task in _tasks.OrderBy(static t => t.Key).Select(static t => t.Value).Where(static t => !t.Done).ToList()) {
            DropTask(task);
        }
    }

    object? Task(string name, object?[] operands, int depth) {
        switch (name) {
            case "task_spawn": {
                var detached = (bool)operands[5]!;
                var parent = detached ? null : Current;
                var priority = (long)operands[4]!;
                var task = new InterpretedTask((string)operands[0]!, (string)operands[1]!, (string)operands[2]!, operands[3]) {
                    Parent = parent,
                    Cancelled = (parent?.Cancelled ?? false) || _spawnCancelled,
                    Priority = priority >= 0 ? priority : parent?.Priority ?? MediumPriority,
                };
                task.Escalated = task.Priority;
                _spawnCancelled = false;
                parent?.Children.Add(task);
                var handle = ++_nextHandle;
                _tasks[handle] = task;
                _handles[task] = handle;
                Enqueue(task);
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
                Current.WaitingOn = task;
                Escalate(task, Current.Escalated);
                return true;
            }

            case "task_spawn_cancelled":
                _spawnCancelled = true;
                return null;

            // A hop never waits on one thread (see above); where a task runs is not kept.
            case "async_hop":
                return false;

            case "task_actor_current":
                return 0L;

            case "task_waitlist_new": {
                var handle = ++_nextHandle;
                _waitLists[handle] = [];
                return handle;
            }

            case "task_waitlist_free":
                _waitLists.Remove((long)operands[0]!);
                return null;

            case "async_park":
                _waitLists[(long)operands[0]!].Add(Current);
                return true;

            case "task_waitlist_wake": {
                var list = _waitLists[(long)operands[0]!];
                foreach (var parked in list) {
                    Enqueue(parked);
                }

                list.Clear();
                return null;
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

            // A socket to be ready: off the run queue until it is.
            case "async_io_wait":
                return IoWait(operands[0], (bool)operands[1]!);

            // Blocking work runs at once here, on the one thread: nothing waits.
            case "async_blocking":
                Call(module.Find((string)operands[0]!) ?? throw new SilTrap($"call to undefined @{operands[0]}"), [operands[1]], depth + 1);
                return false;

            case "async_frame_free":
                PopFrame(Current, (HeapObject)operands[0]!);
                return null;

            case "task_cancel":
                Cancel(_tasks[(long)operands[0]!], depth);
                return null;

            case "task_is_cancelled":
                return Current.Cancelled;

            case "task_priority":
                return Current.Priority;

            // A handler for the running task, run at once if it is already cancelled.
            case "task_handler_push": {
                var handler = ((string)operands[0]!, (HeapObject)operands[1]!);
                Current.Handlers.Add(handler);
                if (Current.Cancelled) {
                    Call(module.Find(handler.Item1) ?? throw new SilTrap($"call to undefined @{handler.Item1}"), [handler.Item2], depth + 1);
                }

                return null;
            }

            case "task_handler_pop": {
                var handlers = Current.Handlers;
                var (_, context) = handlers[^1];
                handlers.RemoveAt(handlers.Count - 1);
                Release(context.Type, context, depth);
                return null;
            }

            // The program no longer holds it: once it has finished, nothing does.
            case "task_release": {
                var handle = (long)operands[0]!;
                if (_tasks.TryGetValue(handle, out var task)) {
                    _released.Add(handle);
                    Forget(task);
                }

                return null;
            }

            case "task_group_new": {
                var handle = ++_nextHandle;
                _groups[handle] = new();
                return handle;
            }

            case "task_group_add": {
                var group = _groups[(long)operands[0]!];
                _tasks[(long)operands[1]!].Group = group;
                group.Running++;
                return null;
            }

            // Whether the running task must wait for a child to finish: not if one has, or none runs.
            case "async_group_wait": {
                var group = _groups[(long)operands[0]!];
                if (group.Done.Count > 0 || group.Running == 0) {
                    return false;
                }

                group.Waiter = Current;
                return true;
            }

            case "task_group_take": {
                var group = _groups[(long)operands[0]!];
                if (!group.Done.TryDequeue(out var handle)) {
                    return 0L;
                }

                if (_tasks.TryGetValue(handle, out var task)) {
                    task.Group = null;
                }

                return handle;
            }

            case "task_group_free":
                _groups.Remove((long)operands[0]!);
                return null;

            case "task_run_until_main":
                RunTasks(_mainTask);
                return null;

            case "task_drain":
                Drain();
                return null;

            default:
                return null;
        }
    }
}
