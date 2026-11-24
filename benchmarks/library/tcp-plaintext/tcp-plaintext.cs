using System.Net;
using System.Net.Sockets;
using System.Text;

// Port of tcp-plaintext.sb: Socket's own async calls, into a buffer kept for
// each connection.
[Benchmark("library/tcp-plaintext")]
static class LibraryTcpPlaintext {
    const int Connections = 32, Rounds = 100, Pipelined = 16;

    static int Ends(ReadOnlySpan<byte> bytes, ref int matched) {
        var end = "\r\n\r\n"u8;
        var count = 0;
        foreach (var b in bytes) {
            if (b == end[matched]) {
                matched++;
                if (matched == 4) {
                    count++;
                    matched = 0;
                }
            } else {
                matched = b == 13 ? 1 : 0;
            }
        }

        return count;
    }

    static async Task Write(Socket socket, ReadOnlyMemory<byte> bytes) {
        while (bytes.Length > 0) {
            bytes = bytes[await socket.SendAsync(bytes)..];
        }
    }

    static async Task Answer(Socket connection, byte[] reply) {
        using var _ = connection;
        var buffer = new byte[16384];
        var replies = Array.Empty<byte>();
        var matched = 0;
        while (true) {
            var got = await connection.ReceiveAsync(buffer.AsMemory());
            if (got == 0) {
                return;
            }

            var count = Ends(buffer.AsSpan(0, got), ref matched);
            if (count > 0) {
                if (replies.Length < count * reply.Length) {
                    replies = [..Enumerable.Repeat(reply, count).SelectMany(static r => r)];
                }

                await Write(connection, replies.AsMemory(0, count * reply.Length));
            }
        }
    }

    static async Task Serve(Socket listener, byte[] reply) {
        List<Task> answering = [];
        for (var i = 0; i < Connections; i++) {
            var connection = await listener.AcceptAsync();
            connection.NoDelay = true;
            answering.Add(Task.Run(() => Answer(connection, reply)));
        }

        await Task.WhenAll(answering);
    }

    static async Task<int> Ask(EndPoint address, byte[] requests, int expected) {
        using var connection = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await connection.ConnectAsync(address);
        var buffer = new byte[16384];
        var total = 0;
        for (var i = 0; i < Rounds; i++) {
            await Write(connection, requests);
            var got = 0;
            while (got < expected) {
                var read = await connection.ReceiveAsync(buffer.AsMemory());
                if (read == 0) {
                    return total;
                }

                got = checked(got + read);
            }

            total = checked(total + got);
        }

        return total;
    }

    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var request = "GET /plaintext HTTP/1.1\r\nHost: localhost\r\nAccept: text/plain\r\nConnection: keep-alive\r\n\r\n"u8.ToArray();
        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nServer: s\r\nContent-Type: text/plain\r\nContent-Length: 13\r\n\r\nHello, World!");
        byte[] requests = [..Enumerable.Repeat(request, Pipelined).SelectMany(static r => r)];
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1024);
        var address = listener.LocalEndPoint!;
        var server = Task.Run(() => Serve(listener, reply));
        var clients = Enumerable.Range(0, Connections).Select(_ => Task.Run(() => Ask(address, requests, Pipelined * reply.Length))).ToList();
        var total = (await Task.WhenAll(clients)).Sum();
        await server;
        return total.ToString();
    }
}
