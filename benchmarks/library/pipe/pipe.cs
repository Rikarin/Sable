using System.IO.Pipelines;

// Port of pipe.sb, with System.IO.Pipelines.
[Benchmark("library/pipe")]
static class LibraryPipe {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var pipe = new Pipe();
        var writing = Task.Run(async () => {
            var bytes = new byte[1000];
            Array.Fill(bytes, (byte)120);
            bytes[499] = 10;
            bytes[999] = 10;
            for (var i = 0; i < 40000; i++) {
                await pipe.Writer.WriteAsync(bytes);
            }

            await pipe.Writer.CompleteAsync();
        });
        long count = 0;
        long lines = 0;
        while (true) {
            var result = await pipe.Reader.ReadAsync();
            var buffer = result.Buffer;
            count = checked(count + buffer.Length);
            foreach (var segment in buffer) {
                lines = checked(lines + Lines(segment.Span));
            }

            pipe.Reader.AdvanceTo(buffer.End);
            if (result.IsCompleted) {
                break;
            }
        }

        await pipe.Reader.CompleteAsync();
        await writing;
        return $"{count} {lines}";
    }

    static long Lines(ReadOnlySpan<byte> bytes) {
        long lines = 0;
        int at;
        while ((at = bytes.IndexOf((byte)10)) >= 0) {
            lines++;
            bytes = bytes[(at + 1)..];
        }

        return lines;
    }
}
