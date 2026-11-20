using System.Net;
using System.Net.Sockets;

namespace Sable.SIL;

/// <summary>
///     Sockets, interpreted as the runtime gives them (runtime/net.c): every
///     one nonblocking, named by a handle, a call that would block saying so
///     through <c>net_status</c>. A task waiting for one (<c>async_io_wait</c>)
///     is off the run queue until <see cref="Socket.Select" /> says the socket
///     is ready — what the runtime's reactor says natively — which the
///     executor asks when nothing else can run, and in passing otherwise.
///     Work for the blocking pool (<c>async_blocking</c>) runs at once, on
///     the one thread, and the task goes on.
/// </summary>
public sealed partial class SilInterpreter {
    sealed class InterpretedSocket(Socket socket) {
        public Socket Socket => socket;

        /// <summary>A connection begun and not yet known to be made.</summary>
        public bool Connecting;
    }

    readonly Dictionary<long, InterpretedSocket> _sockets = [];
    long _nextSocket;

    /// <summary>What the last socket call did: 0 done, 1 would block, 2 failed.</summary>
    long _netStatus;

    /// <summary>The tasks waiting for a socket, and which way.</summary>
    readonly List<(InterpretedTask Task, InterpretedSocket Socket, bool Writable)> _ioWaiters = [];

    InterpretedSocket? SocketOf(object? handle) => _sockets.GetValueOrDefault((long)handle!);

    long Opened(Socket socket) {
        socket.Blocking = false;
        _sockets[++_nextSocket] = new(socket);
        return _nextSocket;
    }

    /// <summary>A socket error: would block, or failed.</summary>
    long Failed(SocketException e) => _netStatus = e.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress or SocketError.TryAgain ? 1 : 2;

    object? Net(string name, object?[] operands) {
        if (Sandbox is not null) {
            throw new SilTrap("a build script may not use the network");
        }

        if (name == "net_status") {
            return _netStatus;
        }

        _netStatus = 0;
        switch (name) {
            case "net_listen": {
                try {
                    var host = Text(operands[0]).Value;
                    var address = host.Length == 0 ? IPAddress.Any : IPAddress.TryParse(host, out var parsed) ? parsed : Dns.GetHostAddresses(host)[0];
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    socket.Bind(new IPEndPoint(address, (int)(long)operands[1]!));
                    socket.Listen();
                    return Opened(socket);
                } catch (Exception e) when (e is SocketException or ArgumentException) {
                    _netStatus = 2;
                    return 0L;
                }
            }

            case "net_port":
                return (long)((SocketOf(operands[0])?.Socket.LocalEndPoint as IPEndPoint)?.Port ?? 0);

            case "net_accept": {
                if (SocketOf(operands[0]) is not { } listener) {
                    _netStatus = 2;
                    return 0L;
                }

                try {
                    var accepted = listener.Socket.Accept();
                    accepted.NoDelay = true;
                    return Opened(accepted);
                } catch (SocketException e) {
                    Failed(e);
                    return 0L;
                }
            }

            case "net_connect": {
                Socket? socket = null;
                try {
                    var host = Text(operands[0]).Value;
                    var address = IPAddress.TryParse(host, out var parsed) ? parsed : Dns.GetHostAddresses(host)[0];
                    socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { Blocking = false, NoDelay = true };
                    try {
                        socket.Connect(new IPEndPoint(address, (int)(long)operands[1]!));
                    } catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress) {
                    }

                    var handle = Opened(socket);
                    _sockets[handle].Connecting = true;
                    _netStatus = 0;
                    return handle;
                } catch (Exception e) when (e is SocketException or ArgumentException) {
                    socket?.Dispose();
                    _netStatus = 2;
                    return 0L;
                }
            }

            case "net_connected":
                return SocketOf(operands[0]) is { } connecting && connecting.Socket.Poll(0, SelectMode.SelectWrite) && connecting.Socket.Connected;

            case "net_read": {
                if (SocketOf(operands[0]) is not { } source) {
                    _netStatus = 2;
                    return null;
                }

                var buffer = new byte[Math.Clamp((long)operands[1]!, 0, 65536)];
                try {
                    var got = buffer.Length == 0 ? 0 : source.Socket.Receive(buffer);
                    return got == 0 ? null : Allocate(new HeapArray(new ArrayType(SilType.U8), [..buffer.Take(got).Select(static b => (object?)(long)b)]));
                } catch (SocketException e) {
                    Failed(e);
                    return null;
                }
            }

            case "net_write": {
                if (SocketOf(operands[0]) is not { } target) {
                    _netStatus = 2;
                    return 0L;
                }

                var bytes = BytesOf(operands[1]);
                try {
                    return bytes.Length == 0 ? 0L : (long)target.Socket.Send(bytes);
                } catch (SocketException e) {
                    Failed(e);
                    return 0L;
                }
            }

            case "net_shutdown":
                try {
                    SocketOf(operands[0])?.Socket.Shutdown(SocketShutdown.Send);
                } catch (SocketException) {
                }

                return null;

            case "net_close": {
                var handle = (long)operands[0]!;
                if (_sockets.Remove(handle, out var closed)) {
                    // Whoever waits on it wakes, to find it closed.
                    foreach (var waiter in _ioWaiters.Where(w => ReferenceEquals(w.Socket, closed)).ToList()) {
                        _ioWaiters.Remove(waiter);
                        Enqueue(waiter.Task);
                    }

                    closed.Socket.Dispose();
                }

                return null;
            }

            default:
                throw new SilTrap($"no socket call '{name}'");
        }
    }

    /// <summary>
    ///     Whether the running task must wait for the socket to be ready that
    ///     way: not when it is, nor when it is closed.
    /// </summary>
    bool IoWait(object? handle, bool writable) {
        if (SocketOf(handle) is not { } source || Ready(source, writable)) {
            return false;
        }

        _ioWaiters.Add((Current, source, writable));
        return true;
    }

    static bool Ready(InterpretedSocket source, bool writable) =>
        source.Socket.Poll(0, writable ? SelectMode.SelectWrite : SelectMode.SelectRead) || source.Socket.Poll(0, SelectMode.SelectError);

    /// <summary>
    ///     The tasks whose sockets are ready, queued: waiting for one at most
    ///     until <paramref name="until" /> (a monotonic time; -1, for ever; 0,
    ///     not at all).
    /// </summary>
    void PollSockets(long until) {
        if (_ioWaiters.Count == 0) {
            return;
        }

        var reads = _ioWaiters.Where(static w => !w.Writable).Select(static w => w.Socket.Socket).Distinct().ToList();
        var writes = _ioWaiters.Where(static w => w.Writable).Select(static w => w.Socket.Socket).Distinct().ToList();
        var errors = _ioWaiters.Select(static w => w.Socket.Socket).Distinct().ToList();
        var microseconds = until < 0 ? -1 : (int)Math.Clamp((until - Monotonic() + 999) / 1000, 0, int.MaxValue);
        Socket.Select(reads.Count == 0 ? null : reads, writes.Count == 0 ? null : writes, errors, microseconds);
        foreach (var waiter in _ioWaiters.ToList()) {
            var socket = waiter.Socket.Socket;
            if ((waiter.Writable ? writes : reads).Contains(socket) || errors.Contains(socket)) {
                _ioWaiters.Remove(waiter);
                Enqueue(waiter.Task);
            }
        }
    }

    /// <summary>A task cancelled, or dropped, while it waits for a socket: woken, or forgotten.</summary>
    void IoCancel(InterpretedTask task, bool resume) {
        var removed = _ioWaiters.RemoveAll(w => ReferenceEquals(w.Task, task));
        if (removed > 0 && resume) {
            Enqueue(task);
        }
    }
}
