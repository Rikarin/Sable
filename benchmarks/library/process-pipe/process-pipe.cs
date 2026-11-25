using System.Diagnostics;

// Port of process-pipe.sb. The runner counts the child's time, too.
[Benchmark("library/process-pipe")]
static class LibraryProcessPipe {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        using var process = Process.Start(new ProcessStartInfo("cat") { RedirectStandardInput = true, RedirectStandardOutput = true })!;
        var input = process.StandardInput.BaseStream;
        var writing = Task.Run(async () => {
            var bytes = new byte[64000];
            Array.Fill(bytes, (byte)42);
            for (var i = 0; i < 1000; i++) {
                await input.WriteAsync(bytes);
            }

            input.Close();
        });
        var output = process.StandardOutput.BaseStream;
        var buffer = new byte[65536];
        long count = 0;
        long total = 0;
        while (true) {
            var got = await output.ReadAsync(buffer);
            if (got == 0) {
                break;
            }

            count = checked(count + got);
            total = checked(total + buffer[got / 2] - 42);
        }

        await writing;
        await process.WaitForExitAsync();
        return $"{count} {total} {(process.ExitCode == 0 ? "true" : "false")}";
    }
}
