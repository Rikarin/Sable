using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NetTask = System.Threading.Tasks.Task;

namespace Sable.SIL;

/// <summary>
///     Processes and signals, interpreted as the runtime gives them
///     (runtime/process.c). A child is a .NET <see cref="Process" />, its
///     pipes' ends and its ending sources a task waits for as it waits for a
///     socket: .NET's threads read, write and wait, and the interpreter's one
///     thread hears through <see cref="_happened" />. A stream inherited goes
///     to <see cref="Output" /> and <see cref="ErrorOutput" />, as everything
///     the program prints does here, and standard input is at its end.
/// </summary>
public sealed partial class SilInterpreter {
    /// <summary>What a task waits for that is no socket: a pipe's end, a child's ending, a signal.</summary>
    abstract class InterpretedSource {
        public abstract bool Ready(bool writable);

        /// <summary>It may never happen, and a program whose main has returned does not wait for it.</summary>
        public virtual bool Background => false;

        public virtual void Close() { }
    }

    /// <summary>A pipe from a child, read ahead a chunk at a time by a thread of .NET's.</summary>
    sealed class PipeFrom : InterpretedSource {
        readonly Stream _stream;
        readonly SemaphoreSlim _taken = new(0);
        byte[] _chunk = [];
        int _at;
        bool _ended, _closed;

        public PipeFrom(Stream stream, Action happened) {
            _stream = stream;
            NetTask.Run(async () => {
                var buffer = new byte[65536];
                while (true) {
                    int got;
                    try {
                        got = await stream.ReadAsync(buffer);
                    } catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) {
                        got = 0;
                    }

                    lock (_taken) {
                        _chunk = buffer[..got];
                        _at = 0;
                        _ended = got == 0;
                    }

                    happened();
                    if (got == 0) {
                        return;
                    }

                    // No more until this is read: a child that writes faster waits.
                    await _taken.WaitAsync();
                    lock (_taken) {
                        if (_closed) {
                            return;
                        }
                    }
                }
            });
        }

        public override bool Ready(bool writable) {
            lock (_taken) {
                return _at < _chunk.Length || _ended || _closed;
            }
        }

        /// <summary>Some bytes, none at the end; null when none have come.</summary>
        public byte[]? Read(int most) {
            lock (_taken) {
                if (_at == _chunk.Length) {
                    return _ended ? [] : null;
                }

                var take = Math.Min(most, _chunk.Length - _at);
                var bytes = _chunk[_at..(_at + take)];
                _at += take;
                if (_at == _chunk.Length && !_ended) {
                    _taken.Release();
                }

                return bytes;
            }
        }

        public override void Close() {
            lock (_taken) {
                _closed = true;
                _taken.Release();
            }

            _stream.Dispose();
        }
    }

    /// <summary>A pipe to a child: a write is done by a thread of .NET's, and said to be once it is.</summary>
    sealed class PipeTo(Stream stream, Action happened) : InterpretedSource {
        Task? _writing;
        byte[] _bytes = [];

        public override bool Ready(bool writable) => _writing is null or { IsCompleted: true };

        /// <summary>How many of the bytes were written; -1 while that is not known; -2 when they cannot be.</summary>
        public long Write(byte[] bytes) {
            if (_writing is { IsCompleted: false }) {
                return -1;
            }

            if (_writing is { } done) {
                _writing = null;
                if (!done.IsCompletedSuccessfully) {
                    return -2;
                }

                // Others are a write's that was given up: these follow them.
                if (bytes.AsSpan().StartsWith(_bytes)) {
                    return _bytes.Length;
                }
            }

            _bytes = bytes.Length > 65536 ? bytes[..65536] : bytes;
            var chunk = _bytes;
            _writing = NetTask.Run(() => {
                try {
                    stream.Write(chunk);
                    stream.Flush();
                } finally {
                    happened();
                }
            });
            return -1;
        }

        public override void Close() {
            try {
                stream.Dispose();
            } catch (IOException) {
            }
        }
    }

    /// <summary>A child ended, for one task to wait for.</summary>
    sealed class EndedEvent(InterpretedChild child) : InterpretedSource {
        public override bool Ready(bool writable) => child.Ended.IsCompleted;
    }

    sealed class InterpretedChild(Process process, Task ended) {
        public Process Process => process;

        /// <summary>Done once the child has ended and what it wrote to streams not piped has been kept.</summary>
        public Task Ended => ended;
        public long[] Pipes = new long[3];
        public long Id;
    }

    /// <summary>Who listens for signals, as bits by number, and those that came.</summary>
    sealed class SignalWatch(long wanted) : InterpretedSource {
        long _came;
        public long Wanted => wanted;
        public long Event;
        public List<PosixSignalRegistration> Registrations = [];
        public override bool Background => true;
        public override bool Ready(bool writable) => Interlocked.Read(ref _came) != 0;

        public void Arrived(int signal) => Interlocked.Or(ref _came, 1L << signal);

        public long Take() {
            while (Interlocked.Read(ref _came) is var came and not 0) {
                if (Interlocked.CompareExchange(ref _came, came & (came - 1), came) == came) {
                    return System.Numerics.BitOperations.TrailingZeroCount(came);
                }
            }

            return 0;
        }

        public override void Close() => Registrations.ForEach(static r => r.Dispose());
    }

    readonly Dictionary<long, InterpretedSource> _sources = [];
    readonly Dictionary<long, InterpretedChild> _children = [];
    readonly Dictionary<long, SignalWatch> _signalWatches = [];
    readonly List<(InterpretedTask Task, InterpretedSource Source, bool Writable)> _sourceWaiters = [];

    /// <summary>Set by .NET's threads as any source may have become ready.</summary>
    readonly ManualResetEventSlim _happened = new();

    /// <summary>What children wrote to streams they inherited, for the interpreter's thread to print.</summary>
    readonly ConcurrentQueue<(bool Errors, string Text)> _written = new();

    void PrintWritten() {
        while (_written.TryDequeue(out var written)) {
            (written.Errors ? ErrorOutput : Output).Append(written.Text);
        }
    }

    long _processError, _processErrorCode, _lastSignal;

    long Opened(InterpretedSource source) {
        _sources[++_nextSocket] = source;
        return _nextSocket;
    }

    /// <summary>The tasks whose sources are ready, queued.</summary>
    void PollSources() {
        PrintWritten();
        _happened.Reset();
        for (var i = 0; i < _sourceWaiters.Count;) {
            var (task, source, writable) = _sourceWaiters[i];
            if (source.Ready(writable)) {
                _sourceWaiters.RemoveAt(i);
                Enqueue(task);
            } else {
                i++;
            }
        }
    }

    /// <summary>
    ///     Nothing can run: waits for a source or a socket to be ready, until
    ///     the earliest sleeper. False when there is nothing to wait for but
    ///     what the program, its main having returned, need not.
    /// </summary>
    bool WaitForSources(bool draining) {
        if (_sourceWaiters.Count == 0 || (draining && _sourceWaiters.All(static w => w.Source.Background))) {
            return false;
        }

        if (Interrupt.IsCancellationRequested) {
            throw new SilTrap("interrupted", located: false);
        }

        var wait = TimeSpan.FromMilliseconds(50);
        if (AnySleeping() && _sleepers.TryPeek(out _, out var earliest)) {
            var left = TimeSpan.FromTicks(Math.Max(0, (earliest.Deadline - Monotonic() + 99) / 100));
            wait = left < wait ? left : wait;
        }

        // With sockets to ask of too, which nothing says are ready, not for long.
        if (_ioWaiters.Count > 0 && wait > TimeSpan.FromMilliseconds(2)) {
            wait = TimeSpan.FromMilliseconds(2);
        }

        _happened.Wait(wait);
        return true;
    }

    bool SourceWait(long handle, bool writable, out bool waits) {
        waits = false;
        if (!_sources.TryGetValue(handle, out var source)) {
            return false;
        }

        if (!source.Ready(writable)) {
            _sourceWaiters.Add((Current, source, writable));
            waits = true;
        }

        return true;
    }

    /// <summary>net_read, net_write, net_shutdown and net_close of a source that is no socket.</summary>
    bool SourceCall(string name, object?[] operands, out object? result) {
        result = null;
        if (name == "net_status") {
            result = _netStatus;
            return true;
        }

        if (operands.Length == 0 || operands[0] is not long handle || !_sources.TryGetValue(handle, out var source)) {
            return false;
        }

        _netStatus = 0;
        switch (name) {
            case "net_read" when source is PipeFrom from: {
                var bytes = from.Read((int)Math.Clamp((long)operands[1]!, 0, 65536));
                if (bytes is null) {
                    _netStatus = 1;
                } else if (bytes.Length > 0) {
                    result = Allocate(new HeapArray(new ArrayType(SilType.U8), [..bytes.Select(static b => (object?)(long)b)]));
                }

                return true;
            }

            case "net_write" when source is PipeTo to: {
                var bytes = BytesOf(operands[1]);
                var put = bytes.Length == 0 ? 0 : to.Write(bytes);
                _netStatus = put >= 0 ? 0 : put == -1 ? 1 : 2;
                result = Math.Max(0, put);
                return true;
            }

            case "net_close":
                CloseSource(handle);
                return true;

            default:
                _netStatus = name is "net_shutdown" ? 0 : 2;
                result = name is "net_accept" or "net_write" or "net_send" or "net_option" ? 0L
                    : name is "net_connected" or "net_connect_datagram" or "net_name" or "net_set_option" or "net_membership" ? false
                    : null;
                return true;
        }
    }

    void CloseSource(long handle) {
        if (_sources.Remove(handle, out var source)) {
            foreach (var waiter in _sourceWaiters.Where(w => ReferenceEquals(w.Source, source)).ToList()) {
                _sourceWaiters.Remove(waiter);
                Enqueue(waiter.Task);
            }

            source.Close();
        }
    }

    /// <summary>How to start the program, in the directory and with the environment it is to have.</summary>
    ProcessStartInfo ProgramStart(object? program, object? arguments) {
        var name = Text(program).Value;
        var start = new ProcessStartInfo(Sandbox?.CheckProgram(name) ?? name) {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        if (Sandbox is { } confined) {
            start.WorkingDirectory = confined.CurrentDirectory;
            foreach (var (variable, setting) in confined.ProgramEnvironment) {
                start.Environment[variable] = setting;
            }
        }

        // What the program set in its own environment, as a child of
        // a native one would inherit it.
        foreach (var (variable, setting) in _environment) {
            if (setting is null) {
                start.Environment.Remove(variable);
            } else {
                start.Environment[variable] = setting;
            }
        }

        foreach (var argument in TextsOf(arguments)) {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    IEnumerable<string> TextsOf(object? array) => array is HeapArray given ? Alive(given).Elements.Select(e => Text(e).Value).ToList() : [];

    Process? Started(ProcessStartInfo start) {
        _processError = 0;
        _processErrorCode = 0;
        try {
            if (Process.Start(start) is { } process) {
                return process;
            }

            _processError = 7;
        } catch (Win32Exception e) {
            _processErrorCode = e.NativeErrorCode;
            var there = start.FileName.Contains('/', StringComparison.Ordinal) && File.Exists(start.FileName);
            // ENOENT, EACCES, ENOEXEC; and Windows's ERROR_FILE_NOT_FOUND,
            // ERROR_PATH_NOT_FOUND, ERROR_ACCESS_DENIED, ERROR_BAD_EXE_FORMAT.
            _processError = e.NativeErrorCode switch {
                2 => there ? 4 : 1,
                3 when OperatingSystem.IsWindows() => 1,
                5 when OperatingSystem.IsWindows() => 2,
                193 when OperatingSystem.IsWindows() => 4,
                13 => 2,
                8 => 4,
                _ => 7,
            };
        }

        return null;
    }

    /// <summary>How the process ended: its exit code, and the signal that ended it or 0.</summary>
    static (long Code, long Signal) EndOf(Process process) =>
        (process.ExitCode, process.TryWaitForExitStatus(TimeSpan.Zero, out var status) && status.Signal is not null ? process.ExitCode - 128 : 0);

    object? Processes(string name, object?[] operands) {
        PrintWritten();
        switch (name) {
            case "process_run": {
                var start = ProgramStart(operands[0], operands[1]);
                // Text goes to and from the program as UTF-8, as the native
                // runtime passes it, and not in the console's code page,
                // which is what Windows would use.
                start.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
                using var process = Started(start);
                if (process is null) {
                    _ioFailed = true;
                    return Allocate(new HeapString(""));
                }

                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                try {
                    process.StandardInput.Write(Text(operands[2]).Value);
                    process.StandardInput.Close();
                } catch (IOException) {
                    // It stopped reading: what it was given is all it wanted.
                }

                process.WaitForExit();
                (_lastStatus, _lastSignal) = EndOf(process);
                _lastErrorOutput = errors.Result;
                _ioFailed = false;
                return Allocate(new HeapString(output.Result));
            }

            case "process_status_signal":
                return _lastSignal;

            case "process_error":
                return _processError;

            case "process_error_code":
                return _processErrorCode;

            case "process_spawn":
                return Spawn(operands);

            case "process_pipe":
                return _children[(long)operands[0]!].Pipes[(long)operands[1]!];

            case "process_child_id":
                return _children[(long)operands[0]!].Id;

            case "process_ended":
                return _children[(long)operands[0]!].Ended.IsCompleted;

            case "process_exit_code": {
                var (code, signal) = EndOf(_children[(long)operands[0]!].Process);
                return (bool)operands[1]! ? signal : code;
            }

            case "process_exit_event": {
                var child = _children[(long)operands[0]!];
                return child.Ended.IsCompleted ? 0L : Opened(new EndedEvent(child));
            }

            case "process_signal": {
                var child = _children[(long)operands[0]!];
                var signal = (long)operands[1]!;
                if (child.Ended.IsCompleted) {
                    return 1L;
                }

                try {
                    if (signal == -1) {
                        child.Process.Kill(entireProcessTree: true);
                    } else if (signal == 9 || (OperatingSystem.IsWindows() && signal == 15)) {
                        child.Process.Kill();
                    } else if (OperatingSystem.IsWindows() || SendSignal((int)child.Id, (int)signal) != 0) {
                        return 2L;
                    }

                    return 0L;
                } catch (InvalidOperationException) {
                    return 1L;
                } catch (Win32Exception) {
                    return 2L;
                }
            }

            case "process_release":
                _children.Remove((long)operands[0]!);
                return null;

            case "process_exit": {
                var status = (long)operands[0]!;
                throw new SilTrap($"the program ended itself with status {status}", located: false) { Exits = true, ExitStatus = (int)(OperatingSystem.IsWindows() ? status : status & 0xFF) };
            }

            case "process_abort":
                throw new SilTrap("the program aborted itself", located: false) { Exits = true };

            default:
                throw new SilTrap($"no process call '{name}'");
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    static extern int SendSignal(int process, int signal);

    object? Spawn(object?[] operands) {
        var start = ProgramStart(operands[0], operands[1]);
        var flags = (long)operands[3]!;
        if ((flags & 64) != 0) {
            start.Environment.Clear();
            foreach (var variable in TextsOf(operands[2])) {
                // A name may begin with '=', as cmd's do on Windows.
                var equals = variable.IndexOf('=', Math.Min(1, variable.Length));
                if (equals > 0) {
                    start.Environment[variable[..equals]] = variable[(equals + 1)..];
                }
            }
        }

        _processErrorCode = 0;
        if (Text(operands[4]).Value is { Length: > 0 } directory) {
            start.WorkingDirectory = PathFor(operands[4], SilAccess.Read);
            if (!Directory.Exists(start.WorkingDirectory)) {
                _processError = 3;
                return 0L;
            }
        }

        var names = operands[5] is HeapArray given ? Alive(given).Elements : [];
        var kinds = new[] { (int)flags & 3, (int)(flags >> 2) & 3, (int)(flags >> 4) & 3 };
        var files = new Stream?[3];
        for (var i = 0; i < 3; i++) {
            if (kinds[i] == 3) {
                try {
                    files[i] = i == 2 && kinds[1] == 3 && Text(names[1]).Value == Text(names[2]).Value ? files[1]
                        : i == 0 ? new FileStream(PathFor(names[i], SilAccess.Read), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        : new FileStream(PathFor(names[i], SilAccess.Write), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
                    Array.ForEach(files, static f => f?.Dispose());
                    _processError = 5;
                    _processErrorCode = i;
                    return 0L;
                }
            }
        }

        if (Started(start) is not { } process) {
            Array.ForEach(files, static f => f?.Dispose());
            return 0L;
        }

        var pumps = new List<Task>();
        var pipes = new long[3];
        if (kinds[0] == 2) {
            pipes[0] = Opened(new PipeTo(process.StandardInput.BaseStream, _happened.Set));
        } else {
            _ = NetTask.Run(() => {
                try {
                    files[0]?.CopyTo(process.StandardInput.BaseStream);
                    process.StandardInput.BaseStream.Dispose();
                } catch (Exception e) when (e is IOException or ObjectDisposedException) {
                }
            });
        }

        for (var i = 1; i < 3; i++) {
            var from = (i == 1 ? process.StandardOutput : process.StandardError).BaseStream;
            if (kinds[i] == 2) {
                pipes[i] = Opened(new PipeFrom(from, _happened.Set));
                continue;
            }

            var (file, kind, errors) = (files[i], kinds[i], i == 2);
            pumps.Add(NetTask.Run(() => {
                var kept = new MemoryStream();
                var buffer = new byte[65536];
                for (int got; (got = from.Read(buffer)) > 0;) {
                    if (file is not null) {
                        lock (file) {
                            file.Write(buffer, 0, got);
                            file.Flush();
                        }
                    } else {
                        kept.Write(buffer, 0, got);
                    }
                }

                if (kind == 0) {
                    _written.Enqueue((errors, Encoding.UTF8.GetString(kept.ToArray())));
                }
            }));
        }

        // Ended once what it wrote is kept, unless something it started
        // holds its streams open after it.
        var ended = NetTask.Run(async () => {
            await process.WaitForExitAsync();
            await NetTask.WhenAny(NetTask.WhenAll(pumps), NetTask.Delay(TimeSpan.FromSeconds(1)));
            Array.ForEach(files, static f => f?.Dispose());
        });
        ended.ContinueWith(_ => _happened.Set(), TaskScheduler.Default);
        _children[++_nextSocket] = new(process, ended) { Id = process.Id, Pipes = pipes };
        return _nextSocket;
    }

    object? Signals(string name, object?[] operands) {
        switch (name) {
            case "signal_watch": {
                if (Sandbox is not null) {
                    throw new SilTrap("a build script may not catch signals");
                }

                var wanted = (long)operands[0]!;
                var watch = new SignalWatch(wanted);
                for (var signal = 0; signal < 64; signal++) {
                    if ((wanted & (1L << signal)) == 0) {
                        continue;
                    }

                    // As the runtime refuses them: a fault's, and what no program can catch.
                    if (signal is 0 or 4 or 5 or 6 or 8 or 9 or 11) {
                        watch.Close();
                        return 0L;
                    }

                    var number = signal;
                    try {
                        watch.Registrations.Add(PosixSignalRegistration.Create(signal switch {
                            1 => PosixSignal.SIGHUP,
                            2 => PosixSignal.SIGINT,
                            3 => PosixSignal.SIGQUIT,
                            15 => PosixSignal.SIGTERM,
                            _ => (PosixSignal)signal,
                        }, context => {
                            context.Cancel = true;
                            watch.Arrived(number);
                            _happened.Set();
                        }));
                    } catch (Exception e) when (e is IOException or PlatformNotSupportedException or ArgumentException) {
                        watch.Close();
                        return 0L;
                    }
                }

                watch.Event = Opened(watch);
                _signalWatches[watch.Event] = watch;
                return watch.Event;
            }

            case "signal_event":
                return operands[0];

            case "signal_take":
                return _signalWatches[(long)operands[0]!].Take();

            case "signal_unwatch":
                _signalWatches.Remove((long)operands[0]!);
                CloseSource((long)operands[0]!);
                return null;

            // The program's own, and not the process's that interprets it: to
            // who listens here, or the program's end.
            case "signal_raise": {
                var signal = (int)(long)operands[0]!;
                var heard = false;
                foreach (var watch in _signalWatches.Values.Where(w => signal is >= 0 and < 64 && (w.Wanted & (1L << signal)) != 0)) {
                    watch.Arrived(signal);
                    heard = true;
                }

                if (!heard) {
                    throw new SilTrap($"the program sent itself signal {signal}", located: false) { Exits = true, ExitStatus = 128 + signal };
                }

                return true;
            }

            default:
                throw new SilTrap($"no signal call '{name}'");
        }
    }

    /// <summary>The runtime's unix_datagram_send: 0 sent, 1 the system has none such, 2 it could not be.</summary>
    static long Notify(string name, string message) {
        var isAbstract = name.StartsWith('@');
        if (OperatingSystem.IsWindows() || name.Length < 2 || (isAbstract ? !OperatingSystem.IsLinux() : name[0] != '/')) {
            return 1;
        }

        try {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Unspecified);
            socket.SendTimeout = 5000;
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            var to = new System.Net.Sockets.UnixDomainSocketEndPoint(isAbstract ? "\0" + name[1..] : name);
            return socket.SendTo(bytes, to) == bytes.Length ? 0 : 2;
        } catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException) {
            return 2;
        }
    }
}
