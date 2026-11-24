using System.Threading.Channels;

// Port of channel.sb: two tasks and an unbounded channel.
[Benchmark("library/channel")]
static class LibraryChannel {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var channel = Channel.CreateUnbounded<long>();
        var producer = Task.Run(async () => {
            for (long i = 0; i < 500000; i++) {
                await channel.Writer.WriteAsync(i);
            }

            channel.Writer.Complete();
        });
        var consumer = Task.Run(async () => {
            long total = 0;
            await foreach (var value in channel.Reader.ReadAllAsync()) {
                total = checked(total + value);
            }

            return total;
        });
        await producer;
        return (await consumer).ToString();
    }
}
