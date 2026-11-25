using System.Diagnostics;

// Port of process-spawn.sb. The runner counts the children's time, too.
[Benchmark("library/process-spawn")]
static class LibraryProcessSpawn {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        long total = 0;
        for (var i = 0; i < 100; i++) {
            var start = new ProcessStartInfo("/bin/echo") {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add(i.ToString());
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            var text = await output;
            await errors;
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) {
                total = checked(total + long.Parse(text.Trim()));
            }
        }

        return total.ToString();
    }
}
