// The concurrency benchmarks' counterparts in .NET (docs/04, "Async, as
// implemented"): spawn/await throughput, ping-pong between two tasks, and a
// million tasks each sleeping a second. Run with
//     dotnet run -c Release --project benchmarks/concurrency/compare
using System.Diagnostics;
using System.Threading.Channels;

static async Task<long> SpawnAwait() {
    var clock = Stopwatch.StartNew();
    long total = 0;
    for (var batch = 0; batch < 100; batch++) {
        var tasks = new Task<int>[10_000];
        for (var i = 0; i < tasks.Length; i++) {
            var n = i + batch;
            tasks[i] = Task.Run(() => n);
        }

        foreach (var task in tasks) {
            total += await task;
        }
    }

    Console.WriteLine($"spawn+await: {clock.Elapsed.TotalNanoseconds / 1_000_000:F0} ns per task");
    return total;
}

static async Task PingPong() {
    var ping = Channel.CreateUnbounded<int>();
    var pong = Channel.CreateUnbounded<int>();
    const int rounds = 1_000_000;
    var clock = Stopwatch.StartNew();
    var other = Task.Run(async () => {
        for (var i = 0; i < rounds; i++) {
            await pong.Writer.WriteAsync(await ping.Reader.ReadAsync() + 1);
        }
    });
    var value = 0;
    for (var i = 0; i < rounds; i++) {
        await ping.Writer.WriteAsync(value);
        value = await pong.Reader.ReadAsync();
    }

    await other;
    Console.WriteLine($"ping-pong through channels: {clock.Elapsed.TotalNanoseconds / rounds:F0} ns per round");
}

static async Task Sleepers(int count) {
    var clock = Stopwatch.StartNew();
    var tasks = new Task[count];
    for (var i = 0; i < count; i++) {
        var n = i;
        tasks[i] = Task.Run(async () => {
            await Task.Delay(1000);
            return n & 1;
        });
    }

    await Task.WhenAll(tasks);
    Console.WriteLine($"{count} sleepers: {clock.ElapsedMilliseconds} ms");
}

// fan-out-http.sb's: fifty clients at once, forty times over, each
// connecting, asking, and reading a 512-byte reply a task of the server's
// writes.
static async Task FanOut() {
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start(128);
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    using var stop = new CancellationTokenSource();
    var reply = new byte[512];
    for (var i = 0; i < reply.Length; i++) {
        reply[i] = (byte)(48 + i % 10);
    }

    var server = Task.Run(async () => {
        try {
            while (true) {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(async () => {
                    using (client) {
                        var stream = client.GetStream();
                        var request = new byte[1024];
                        _ = await stream.ReadAsync(request);
                        await stream.WriteAsync(reply);
                    }
                });
            }
        } catch (OperationCanceledException) {
        }
    });

    const int clients = 50, rounds = 40;
    var clock = Stopwatch.StartNew();
    long bytes = 0;
    for (var round = 0; round < rounds; round++) {
        var asking = new Task<int>[clients];
        for (var i = 0; i < clients; i++) {
            asking[i] = Task.Run(async () => {
                using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
                var stream = client.GetStream();
                await stream.WriteAsync("GET /\n"u8.ToArray());
                var buffer = new byte[4096];
                int got, total = 0;
                while ((got = await stream.ReadAsync(buffer)) > 0) {
                    total += got;
                }

                return total;
            });
        }

        foreach (var task in asking) {
            bytes += await task;
        }
    }

    Console.WriteLine($"fan-out over loopback: {clock.Elapsed.TotalMicroseconds / (clients * rounds):F0} us per request, {bytes / (clients * rounds)} bytes each");
    stop.Cancel();
    await server;
    listener.Stop();
}

var which = args.Length > 0 ? args[0] : "all";
if (which is "all" or "spawn") {
    await SpawnAwait();
}

if (which is "all" or "ping") {
    await PingPong();
}

if (which is "all" or "sleep") {
    await Sleepers(1_000_000);
}

if (which is "all" or "fanout") {
    await FanOut();
}
