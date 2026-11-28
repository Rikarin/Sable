using System.Net;
using System.Net.NetworkInformation;
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

    /// <summary>Why the last that failed did, as runtime/platform.h numbers it, and the system's code.</summary>
    long _netError, _netErrorCode;

    /// <summary>The address the last call that gives one gave, and whether its datagram was cut short.</summary>
    EndPoint? _netAddress;

    bool _netTruncated;
    readonly Dictionary<long, IPAddress[]> _lookups = [];
    readonly List<(NetworkInterface Interface, int Index, UnicastIPAddressInformation? Address)> _interfaces = [];

    /// <summary>The calls that are given no handle first, which a number of theirs could be taken for.</summary>
    static readonly HashSet<string> _handleless = [
        "net_error", "net_error_code", "net_address", "net_path", "net_listen", "net_connect", "net_datagram", "net_resolve", "net_resolve_event",
        "net_resolved", "net_resolved_take", "net_resolve_end", "net_interfaces", "net_interface_name", "net_interface", "net_interface_index",
    ];

    /// <summary>A call that failed: it would block, or why it did.</summary>
    void Failed(SocketException e) {
        _netErrorCode = e.NativeErrorCode;
        _netError = e.SocketErrorCode switch {
            SocketError.ConnectionReset or SocketError.NotConnected or SocketError.ConnectionAborted or SocketError.Shutdown => 1,
            SocketError.ConnectionRefused => 2,
            SocketError.TimedOut => 3,
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.HostDown
                or SocketError.AddressNotAvailable => 4,
            SocketError.AddressAlreadyInUse => 5,
            SocketError.AccessDenied => 6,
            SocketError.TooManyOpenSockets or SocketError.NoBufferSpaceAvailable or SocketError.TryAgain => 7,
            SocketError.InvalidArgument or SocketError.DestinationAddressRequired or SocketError.IsConnected => 8,
            SocketError.MessageSize => 9,
            SocketError.OperationNotSupported or SocketError.ProtocolOption or SocketError.AddressFamilyNotSupported
                or SocketError.ProtocolNotSupported or SocketError.SocketNotSupported or SocketError.ProtocolType => 10,
            SocketError.HostNotFound or SocketError.NoData => 11,
            _ => 0,
        };
        _netStatus = e.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress ? 1 : 2;
        // .NET has no name for ENOENT: a Unix domain socket's path not there.
        if (_netError == 0 && e.NativeErrorCode == 2) {
            _netError = 11;
        }
    }

    void FailedAs(long kind) {
        _netStatus = 2;
        _netError = kind;
        _netErrorCode = 0;
    }

    /// <summary>The address six operands make; null for none.</summary>
    EndPoint? AddressOf(object?[] operands, int at) {
        var (family, high, low, scope, port) = ((long)operands[at]!, (ulong)(long)operands[at + 1]!, (ulong)(long)operands[at + 2]!, (long)operands[at + 3]!, (int)(long)operands[at + 4]!);
        switch (family) {
            case 4:
                return new IPEndPoint(new IPAddress(Bytes((uint)low, 4)), port);
            case 6:
                return new IPEndPoint(new IPAddress([..Bytes(high, 8), ..Bytes(low, 8)], scope), port);
            case 1:
                return Text(operands[at + 5]).Value is { Length: > 0 } path ? new UnixDomainSocketEndPoint(path) : null;
            default:
                return null;
        }
    }

    static byte[] Bytes(ulong word, int count) => [..Enumerable.Range(0, count).Select(i => (byte)(word >> (8 * (count - 1 - i))))];

    static ulong Word(ReadOnlySpan<byte> bytes) {
        ulong word = 0;
        foreach (var b in bytes) {
            word = word << 8 | b;
        }

        return word;
    }

    long AddressPart(long part) {
        var ip = _netAddress as IPEndPoint;
        var bytes = ip?.Address.GetAddressBytes() ?? [];
        return part switch {
            0 => _netAddress switch { IPEndPoint => bytes.Length == 4 ? 4 : 6, UnixDomainSocketEndPoint => 1, _ => 0 },
            1 => bytes.Length == 16 ? (long)Word(bytes.AsSpan(0, 8)) : 0,
            2 => bytes.Length == 16 ? (long)Word(bytes.AsSpan(8)) : (long)Word(bytes),
            3 => bytes.Length == 16 ? ip!.Address.ScopeId : 0,
            4 => ip?.Port ?? 0,
            _ => _netTruncated ? 1 : 0,
        };
    }

    Socket MakeSocket(EndPoint? at, bool datagram, long flags) {
        var family = at?.AddressFamily ?? AddressFamily.Unix;
        var protocol = family == AddressFamily.Unix ? ProtocolType.Unspecified : datagram ? ProtocolType.Udp : ProtocolType.Tcp;
        var socket = new Socket(family, datagram ? SocketType.Dgram : SocketType.Stream, protocol) { Blocking = false };
        if (family == AddressFamily.InterNetworkV6) {
            socket.DualMode = (flags & 4) != 0;
        }

        if (family != AddressFamily.Unix && (flags & 3) != 0) {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        return socket;
    }

    /// <summary>The socket made as the body says: its handle, or 0.</summary>
    long Opening(Func<Socket> make, Action<Socket> body) {
        Socket? socket = null;
        try {
            socket = make();
            body(socket);
            return Opened(socket);
        } catch (SocketException e) {
            socket?.Dispose();
            Failed(e);
            _netStatus = 2;
        } catch (Exception e) when (e is ArgumentException or PlatformNotSupportedException or NotSupportedException) {
            socket?.Dispose();
            FailedAs(e is ArgumentException ? 8 : 10);
        }

        return 0;
    }

    /// <summary>Up to so many bytes of a socket or a pipe from a child, as net_read reads them, for bytes_net_read.</summary>
    byte[]? NetRead(long handle, int most) {
        if (_sources.TryGetValue(handle, out var source)) {
            _netStatus = 0;
            if (source is not PipeFrom from) {
                _netStatus = 2;
                return null;
            }

            var bytes = from.Read(Math.Clamp(most, 0, 65536));
            if (bytes is null) {
                _netStatus = 1;
            }

            return bytes;
        }

        if (Sandbox is not null) {
            throw new SilTrap("a build script may not use the network");
        }

        _netStatus = 0;
        return WithSocket<byte[]?>([handle], null, socket => {
            var buffer = new byte[Math.Clamp(most, 0, 65536)];
            var got = buffer.Length == 0 ? 0 : socket.Receive(buffer);
            return buffer[..got];
        });
    }

    object? Net(string name, object?[] operands) {
        // A pipe to a child, or what is not a socket at all (SilInterpreter.Processes.cs).
        if (!_handleless.Contains(name) && SourceCall(name, operands, out var answer)) {
            return answer;
        }

        if (Sandbox is not null) {
            throw new SilTrap("a build script may not use the network");
        }

        switch (name) {
            case "net_error":
                return _netError;
            case "net_error_code":
                return _netErrorCode;
            case "net_address":
                return AddressPart((long)operands[0]!);
            case "net_path":
                return Allocate(new HeapString(_netAddress is UnixDomainSocketEndPoint unix ? unix.ToString() : ""));
        }

        _netStatus = 0;
        switch (name) {
            case "net_listen": {
                if (AddressOf(operands, 0) is not { } at) {
                    FailedAs(8);
                    return 0L;
                }

                return Opening(
                    () => MakeSocket(at, datagram: false, (long)operands[7]! & ~3),
                    socket => {
                        Replace(at, (long)operands[7]!);
                        socket.Bind(at);
                        socket.Listen((int)(long)operands[6]!);
                    }
                );
            }

            case "net_datagram": {
                var at = AddressOf(operands, 0);
                return Opening(
                    () => MakeSocket(at, datagram: true, (long)operands[6]!),
                    socket => {
                        Replace(at, (long)operands[6]!);
                        if (at is not null) {
                            socket.Bind(at);
                        }
                    }
                );
            }

            case "net_connect": {
                if (AddressOf(operands, 0) is not { } at) {
                    FailedAs(8);
                    return 0L;
                }

                var handle = Opening(
                    () => MakeSocket(at, datagram: false, 0),
                    socket => {
                        if (at is IPEndPoint) {
                            socket.NoDelay = true;
                        }

                        try {
                            socket.Connect(at);
                        } catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress) {
                        }
                    }
                );
                return handle;
            }

            case "net_accept": {
                return WithSocket(operands, 0L, listener => {
                    var accepted = listener.Accept();
                    if (accepted.RemoteEndPoint is IPEndPoint) {
                        accepted.NoDelay = true;
                    }

                    _netAddress = accepted.RemoteEndPoint;
                    return Opened(accepted);
                });
            }

            case "net_connected":
                return WithSocket(operands, false, socket => {
                    if (!socket.Poll(0, SelectMode.SelectWrite) && !socket.Poll(0, SelectMode.SelectError)) {
                        _netStatus = 1;
                        return false;
                    }

                    var error = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
                    if (error != 0) {
                        throw new SocketException(error);
                    }

                    _ = socket.RemoteEndPoint ?? throw new SocketException((int)SocketError.ConnectionRefused);
                    return true;
                });

            case "net_connect_datagram":
                return WithSocket(operands, false, socket => {
                    socket.Connect(AddressOf(operands, 1) ?? throw new SocketException((int)SocketError.InvalidArgument));
                    return true;
                });

            case "net_read":
                return WithSocket<object?>(operands, null, source => {
                    var buffer = new byte[Math.Clamp((long)operands[1]!, 0, 65536)];
                    var got = buffer.Length == 0 ? 0 : source.Receive(buffer);
                    return ArrayOf(buffer.AsSpan(0, got));
                });

            case "net_write":
                return WithSocket(operands, 0L, target => {
                    var bytes = BytesOf(operands[1]);
                    return bytes.Length == 0 ? 0L : (long)target.Send(bytes);
                });

            case "net_send":
                return WithSocket(operands, 0L, target => {
                    var bytes = BytesOf(operands[1]);
                    return AddressOf(operands, 2) is { } to ? target.SendTo(bytes, to) : target.Send(bytes);
                });

            case "net_receive":
                return WithSocket<object?>(operands, null, source => {
                    var buffer = new byte[Math.Clamp((long)operands[1]!, 0, 65536)];
                    _netTruncated = false;
                    _netAddress = null;
                    int got;
                    if (source.AddressFamily == AddressFamily.Unix) {
                        got = source.Receive(buffer);
                    } else {
                        var flags = SocketFlags.None;
                        EndPoint from = new IPEndPoint(source.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
                        got = source.ReceiveMessageFrom(buffer, 0, buffer.Length, ref flags, ref from, out _);
                        _netTruncated = (flags & SocketFlags.Truncated) != 0;
                        _netAddress = from;
                    }

                    return ArrayOf(buffer.AsSpan(0, got));
                });

            case "net_name":
                return WithSocket(operands, false, socket => {
                    _netAddress = (bool)operands[1]! ? socket.RemoteEndPoint : socket.LocalEndPoint;
                    if (_netAddress is null) {
                        throw new SocketException((int)SocketError.NotConnected);
                    }

                    return true;
                });

            case "net_set_option":
                return WithSocket(operands, false, socket => {
                    SetOption(socket, (long)operands[1]!, (int)(long)operands[2]!);
                    return true;
                });

            case "net_option":
                return WithSocket(operands, 0L, socket => Option(socket, (long)operands[1]!));

            case "net_membership":
                return WithSocket(operands, false, socket => {
                    var group = ((IPEndPoint)AddressOf([operands[2], operands[3], operands[4], 0L, 0L, null], 0)!).Address;
                    var index = (int)(long)operands[5]!;
                    var option = (bool)operands[1]! ? SocketOptionName.AddMembership : SocketOptionName.DropMembership;
                    if (group.AddressFamily == AddressFamily.InterNetwork) {
                        socket.SetSocketOption(SocketOptionLevel.IP, option, new MulticastOption(group, index));
                    } else {
                        socket.SetSocketOption(SocketOptionLevel.IPv6, option, new IPv6MulticastOption(group, index));
                    }

                    return true;
                });

            case "net_shutdown":
                WithSocket(operands, false, socket => {
                    socket.Shutdown((bool)operands[1]! ? SocketShutdown.Send : SocketShutdown.Receive);
                    return true;
                });
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

            // Looked up at once, on the one thread there is.
            case "net_resolve": {
                var handle = ++_nextSocket;
                try {
                    var family = (long)operands[1]! switch { 4 => AddressFamily.InterNetwork, 6 => AddressFamily.InterNetworkV6, _ => AddressFamily.Unspecified };
                    _lookups[handle] = Dns.GetHostAddresses(Text(operands[0]).Value, family);
                } catch (Exception e) when (e is SocketException or ArgumentException) {
                    _lookups[handle] = [];
                }

                return handle;
            }

            case "net_resolve_event":
                return 0L;

            case "net_resolved":
                return (long)_lookups[(long)operands[0]!].Length;

            case "net_resolved_take":
                _netAddress = new IPEndPoint(_lookups[(long)operands[0]!][(int)(long)operands[1]!], 0);
                return null;

            case "net_resolve_end":
                _lookups.Remove((long)operands[0]!);
                return null;

            case "net_interfaces":
                _interfaces.Clear();
                if ((bool)operands[0]!) {
                    foreach (var one in NetworkInterface.GetAllNetworkInterfaces()) {
                        var properties = one.GetIPProperties();
                        var index = IndexOf(one);
                        _interfaces.Add((one, index, null));
                        _interfaces.AddRange(properties.UnicastAddresses.Select(address => (one, index, (UnicastIPAddressInformation?)address)));
                    }
                }

                return (long)_interfaces.Count;

            case "net_interface_name":
                return Allocate(new HeapString(_interfaces[(int)(long)operands[0]!].Interface.Name));

            case "net_interface": {
                var (one, index, address) = _interfaces[(int)(long)operands[0]!];
                switch ((long)operands[1]!) {
                    case 0:
                        return (long)index;
                    case 1:
                        return (one.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown ? 3L : 0L)
                               | (one.NetworkInterfaceType == NetworkInterfaceType.Loopback ? 4L : 0L)
                               | (one.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel ? 8L : 0L)
                               | (one.SupportsMulticast ? 16L : 0L);
                    case 2:
                        return 0L;
                    case 3: {
                        var hardware = one.GetPhysicalAddress().GetAddressBytes();
                        return hardware.Length == 6 && one.NetworkInterfaceType != NetworkInterfaceType.Loopback ? (long)Word(hardware) : -1L;
                    }

                    case 4:
                        return (long)(address?.PrefixLength ?? 0);
                    default:
                        _netAddress = address is null ? null : new IPEndPoint(address.Address, 0);
                        return AddressPart(0);
                }
            }

            case "net_interface_index": {
                var wanted = Text(operands[0]).Value;
                return (long)(NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(one => one.Name == wanted) is { } found ? IndexOf(found) : 0);
            }

            default:
                throw new SilTrap($"no socket call '{name}'");
        }
    }

    static int IndexOf(NetworkInterface one) {
        try {
            return one.GetIPProperties().GetIPv4Properties()?.Index ?? one.GetIPProperties().GetIPv6Properties()?.Index ?? 0;
        } catch (NetworkInformationException) {
            return 0;
        }
    }

    object? ArrayOf(ReadOnlySpan<byte> bytes) {
        if (bytes.Length == 0) {
            return null;
        }

        var elements = new List<object?>(bytes.Length);
        foreach (var b in bytes) {
            elements.Add((long)b);
        }

        return Allocate(new HeapArray(new ArrayType(SilType.U8), elements));
    }

    /// <summary>A Unix domain socket's file that may be replaced, removed.</summary>
    static void Replace(EndPoint? at, long flags) {
        if (at is UnixDomainSocketEndPoint && (flags & 8) != 0) {
            var path = at.ToString()!;
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) == 0 && new FileInfo(path).Length == 0) {
                File.Delete(path);
            }
        }
    }

    /// <summary>What the body does with the socket of the first operand, which may fail.</summary>
    T WithSocket<T>(object?[] operands, T otherwise, Func<Socket, T> body) {
        if (SocketOf(operands[0]) is not { } found) {
            FailedAs(12);
            return otherwise;
        }

        try {
            return body(found.Socket);
        } catch (SocketException e) {
            Failed(e);
        } catch (Exception e) when (e is ArgumentException or ObjectDisposedException or NotSupportedException or InvalidOperationException) {
            FailedAs(e is ArgumentException ? 8 : 10);
        }

        return otherwise;
    }

    static (SocketOptionLevel Level, SocketOptionName Name) OptionName(Socket socket, long option) {
        var six = socket.AddressFamily == AddressFamily.InterNetworkV6;
        var level = six ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
        return option switch {
            1 => (SocketOptionLevel.Tcp, SocketOptionName.NoDelay),
            2 => (SocketOptionLevel.Socket, SocketOptionName.KeepAlive),
            3 => (SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime),
            4 => (SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval),
            5 => (SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount),
            6 => (SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer),
            7 => (SocketOptionLevel.Socket, SocketOptionName.SendBuffer),
            9 => (level, six ? SocketOptionName.HopLimit : SocketOptionName.IpTimeToLive),
            10 => (SocketOptionLevel.Socket, SocketOptionName.Broadcast),
            11 => (level, SocketOptionName.MulticastLoopback),
            12 => (level, SocketOptionName.MulticastTimeToLive),
            13 => (level, SocketOptionName.MulticastInterface),
            14 => (SocketOptionLevel.IPv6, SocketOptionName.IPv6Only),
            _ => throw new SocketException((int)SocketError.ProtocolOption),
        };
    }

    static void SetOption(Socket socket, long option, int value) {
        if (option == 8) {
            socket.LingerState = new LingerOption(value >= 0, Math.Max(0, value));
            return;
        }

        var (level, name) = OptionName(socket, option);
        if (option == 13 && level == SocketOptionLevel.IP) {
            value = IPAddress.HostToNetworkOrder(value);
        }

        socket.SetSocketOption(level, name, value);
    }

    static long Option(Socket socket, long option) {
        if (option == 8) {
            return socket.LingerState is { Enabled: true } linger ? linger.LingerTime : -1;
        }

        var (level, name) = OptionName(socket, option);
        return (int)socket.GetSocketOption(level, name)!;
    }

    /// <summary>
    ///     Whether the running task must wait for the socket to be ready that
    ///     way: not when it is, nor when it is closed.
    /// </summary>
    bool IoWait(object? handle, bool writable) {
        if (SourceWait((long)handle!, writable, out var waits)) {
            return waits;
        }

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
        var removed = _ioWaiters.RemoveAll(w => ReferenceEquals(w.Task, task)) + _sourceWaiters.RemoveAll(w => ReferenceEquals(w.Task, task));
        if (removed > 0 && resume) {
            Enqueue(task);
        }
    }
}
