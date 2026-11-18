namespace Sable.SIL;

/// <summary>
///     Tasks, interpreted as the runtime runs them: each on a thread of its
///     own, one at a time, handed the turn at the same points in the same
///     order — so the interpreter's interleaving is the native one.
/// </summary>
public sealed partial class SilInterpreter {
    sealed class InterpretedTask {
        public SemaphoreSlim Wake { get; } = new(0);
        public bool Done;
        public List<InterpretedTask> Waiters { get; } = [];
    }

    readonly Queue<InterpretedTask> _runQueue = new();
    readonly Dictionary<long, InterpretedTask> _tasks = [];
    InterpretedTask? _mainTask, _currentTask;
    Exception? _taskFailure;

    InterpretedTask Current => _currentTask ??= _mainTask ??= new InterpretedTask();

    void SwitchAway() {
        var self = Current;
        if (!_runQueue.TryDequeue(out var next)) {
            throw new SilTrap("every task is waiting: the program cannot go on", located: false);
        }

        _currentTask = next;
        next.Wake.Release();
        self.Wake.Wait();
        if (_taskFailure is { } failure) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    object? Task(string name, object?[] operands) {
        switch (name) {
            case "task_spawn": {
                var function = module.Find((string)operands[0]!) ?? throw new SilTrap($"call to undefined @{operands[0]}");
                var context = operands[1];
                var task = new InterpretedTask();
                var handle = (long)_tasks.Count + 1;
                _tasks[handle] = task;
                _ = Current;
                var thread = new Thread(() => {
                        task.Wake.Wait();
                        try {
                            if (_taskFailure is null) {
                                Call(function, [context], 0);
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
                        if (!_runQueue.TryDequeue(out var next)) {
                            _taskFailure ??= new SilTrap("every task is waiting: the program cannot go on", located: false);
                            next = _mainTask!;
                        }

                        if (_taskFailure is not null) {
                            next = _mainTask!;
                        }

                        _currentTask = next;
                        next.Wake.Release();
                    },
                    64 * 1024 * 1024
                ) { IsBackground = true };
                thread.Start();
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
                if (_runQueue.Count > 0) {
                    _runQueue.Enqueue(Current);
                    SwitchAway();
                }

                return null;

            case "task_drain":
                while (_runQueue.Count > 0) {
                    _runQueue.Enqueue(Current);
                    SwitchAway();
                }

                return null;

            default:
                return null;
        }
    }
}
