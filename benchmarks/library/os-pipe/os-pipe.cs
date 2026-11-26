using System.IO.Pipes;

// Port of os-pipe.sb: an anonymous pipe's two ends in one process.
[Benchmark("library/os-pipe")]
static class LibraryOsPipe {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        var writing = Task.Run(async () => {
            var bytes = new byte[64000];
            Array.Fill(bytes, (byte)42);
            for (var i = 0; i < 1000; i++) {
                await server.WriteAsync(bytes);
            }

            server.Close();
        });
        var buffer = new byte[65536];
        long count = 0;
        long total = 0;
        while (true) {
            var got = await reader.ReadAsync(buffer);
            if (got == 0) {
                break;
            }

            count = checked(count + got);
            total = checked(total + buffer[got / 2] - 42);
        }

        await writing;
        return $"{count} {total}";
    }
}
