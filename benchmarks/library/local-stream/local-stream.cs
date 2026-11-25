using System.IO.Pipes;

// Port of local-stream.sb: a named pipe for this user alone (a Unix domain
// socket on macOS and Linux, as Sable's is).
[Benchmark("library/local-stream")]
static class LibraryLocalStream {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var name = $"sable-bench-cs-{Environment.ProcessId}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Task.Run(async () => {
            await server.WaitForConnectionAsync();
            var buffer = new byte[4096];
            while (true) {
                var got = await server.ReadAsync(buffer);
                if (got == 0) {
                    break;
                }

                await server.WriteAsync(buffer.AsMemory(0, got));
            }
        });
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync();
        var message = new byte[64];
        Array.Fill(message, (byte)7);
        var into = new byte[64];
        long total = 0;
        for (var i = 0; i < 10_000; i++) {
            await client.WriteAsync(message);
            var got = 0;
            while (got < 64) {
                var read = await client.ReadAsync(into.AsMemory(0, 64 - got));
                got += read;
                total = checked(total + into[0]);
            }
        }

        client.Close();
        await serving;
        return $"{total}";
    }
}
