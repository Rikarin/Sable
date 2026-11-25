// Runs the .NET port of a benchmark: the [Benchmark] class of that name, in
// ../../library/<name>/<name>.cs or ../../programs/<name>/<name>.cs, whose
// Run does what the Sable program does and returns what it prints.
//
//     dotnet run -c Release -- <name> [runs]
//
// Without a name, lists them. Runs the benchmark for a second and at least
// once, so that the JIT has tiered up even a short one, then 'runs' times
// more (default 5), and prints its output once, then 'times', each run's
// milliseconds, on standard error: processor time (user and system) in the
// steady state, which is the hardest .NET time to match. Processor time
// rather than wall time, as tools/bench/compare.py measures Sable, so that
// a busy machine skews both sides less, and that of the processes it waited
// for, as compare.py counts Sable's. Integer arithmetic is checked, as
// Sable's is.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

var benchmarks = new SortedDictionary<string, Func<string>>(StringComparer.Ordinal);
foreach (var type in Assembly.GetExecutingAssembly().GetTypes()) {
    if (type.GetCustomAttribute<BenchmarkAttribute>() is { } benchmark) {
        benchmarks.Add(benchmark.Name, type.GetMethod("Run")!.CreateDelegate<Func<string>>());
    }
}

if (args.Length == 0) {
    Console.WriteLine(string.Join('\n', benchmarks.Keys));
    return 0;
}

if (!benchmarks.TryGetValue(args[0], out var run)) {
    Console.Error.WriteLine($"no benchmark '{args[0]}'");
    return 2;
}

CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var runs = args.Length > 1 ? int.Parse(args[1]) : 5;
string? output = null;
var times = new List<double>();
var clock = Stopwatch.StartNew();
do {
    run();
} while (clock.ElapsedMilliseconds < 1000);

for (var i = 0; i < runs; i++) {
    // Not Process.TotalProcessorTime, which on Linux counts ticks of 10 ms.
    var before = Environment.CpuUsage.TotalTime + ChildUsage.TotalTime;
    output = run();
    times.Add((Environment.CpuUsage.TotalTime + ChildUsage.TotalTime - before).TotalMilliseconds);
}

Console.WriteLine(output);
Console.Error.WriteLine($"times {string.Join(' ', times.Select(ms => ms.ToString("F2")))}");
return 0;

/// <summary>A benchmark's port: a static class with 'public static string Run()'.</summary>
[AttributeUsage(AttributeTargets.Class)]
sealed class BenchmarkAttribute(string name) : Attribute {
    public string Name { get; } = name;
}

/// <summary>The processor time of this process's children that have ended and been waited for.</summary>
static class ChildUsage {
    // struct rusage of 64-bit Linux and macOS: two timevals, whose
    // microseconds are a long in one and an int and padding in the other,
    // then fourteen longs.
    [StructLayout(LayoutKind.Sequential, Size = 144)]
    struct Usage {
        public long UserSeconds;
        public int UserMicroseconds;
        int userPadding;
        public long SystemSeconds;
        public int SystemMicroseconds;
        int systemPadding;
    }

    const int Children = -1;

    [DllImport("libc", EntryPoint = "getrusage")]
    static extern int GetUsage(int who, out Usage usage);

    public static TimeSpan TotalTime {
        get {
            if (OperatingSystem.IsWindows() || GetUsage(Children, out var usage) != 0) {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(usage.UserSeconds + usage.SystemSeconds)
                + TimeSpan.FromMicroseconds((long)usage.UserMicroseconds + usage.SystemMicroseconds);
        }
    }
}
