// Port of dataflow-pipeline.sb: the three stages — transform, batch by a
// hundred, sum — as tasks joined by bounded channels of the same capacities
// (TPL Dataflow is a package, not part of .NET's standard library).
using System.Threading.Channels;

[Benchmark("library/dataflow-pipeline")]
static class LibraryDataflowPipeline {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var parsed = Channel.CreateBounded<long>(1000);
        var input = Channel.CreateBounded<long>(1000);
        var batches = Channel.CreateBounded<List<long>>(10);
        long total = 0;

        var parse = Task.Run(async () => {
            await foreach (var value in input.Reader.ReadAllAsync()) {
                await parsed.Writer.WriteAsync(checked(value * 3 + 1));
            }

            parsed.Writer.Complete();
        });
        var batch = Task.Run(async () => {
            var pending = new List<long>(100);
            await foreach (var value in parsed.Reader.ReadAllAsync()) {
                pending.Add(value);
                if (pending.Count == 100) {
                    await batches.Writer.WriteAsync(pending);
                    pending = new List<long>(100);
                }
            }

            if (pending.Count > 0) {
                await batches.Writer.WriteAsync(pending);
            }

            batches.Writer.Complete();
        });
        var save = Task.Run(async () => {
            await foreach (var rows in batches.Reader.ReadAllAsync()) {
                long sum = 0;
                foreach (var row in rows) {
                    sum = checked(sum + row);
                }

                total = checked(total + sum);
            }
        });

        for (long i = 0; i < 100_000; i++) {
            await input.Writer.WriteAsync(i);
        }

        input.Writer.Complete();
        await Task.WhenAll(parse, batch, save);
        return total.ToString();
    }
}
