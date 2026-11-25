using System.Threading.Channels;

// Port of channel-bounded.sb.
[Benchmark("library/channel-bounded")]
static class LibraryChannelBounded {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var channel = Channel.CreateBounded<long>(64);
        var producers = new List<Task>();
        for (long p = 0; p < 4; p++) {
            var offset = p;
            producers.Add(Task.Run(async () => {
                for (long i = 0; i < 25000; i++) {
                    await channel.Writer.WriteAsync(checked(i + offset));
                }
            }));
        }

        var consumer = Task.Run(async () => {
            long total = 0;
            await foreach (var value in channel.Reader.ReadAllAsync()) {
                total = checked(total + value);
            }

            return total;
        });
        await Task.WhenAll(producers);
        channel.Writer.Complete();
        return (await consumer).ToString();
    }
}
