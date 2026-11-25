// Runs the .NET port of a benchmark: the [Benchmark] class of that name, in
// ../../library/<name>/<name>.cs or ../../programs/<name>/<name>.cs, whose
// Run does what the Sable program does and returns what it checks — or whose
// Setup makes what that needs, and gives the function that does it.
//
//     dotnet run -c Release -- <name> --measure [--warm-up-ms n] [--sample-ms n] [--samples n] [--budget-ms n]
//     dotnet run -c Release -- <name>
//
// Without a name, lists them. It measures as Sable's Bencher does (README.md,
// "How it is measured"): runs to learn how many iterations make a sample;
// warm-up, until its time has passed, which is the JIT's too, and runs have
// stopped getting faster; and the samples. It prints 'check', what was
// computed; 'sample', each one's iterations, and its nanoseconds by the clock
// and of processor time (this process's, and those of the processes it waited
// for); and 'allocated', the bytes an iteration allocates. Without --measure,
// it runs the benchmark once and prints what it computed: for the whole
// process's time. Integer arithmetic is checked, as Sable's is.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 0) {
    foreach (var each in Assembly.GetExecutingAssembly().GetTypes().OrderBy(static t => t.Name, StringComparer.Ordinal)) {
        foreach (var attribute in each.GetCustomAttributes<BenchmarkAttribute>()) {
            Console.WriteLine(attribute.FullName);
        }
    }

    return 0;
}

// 'library/aes-gcm(64, 100000)' is LibraryAesGcm's, found by its name so that
// a start-up is not that of looking at them all.
var name = args[0];
var type = Assembly.GetExecutingAssembly().GetType(string.Concat(
    name.Split('(')[0].Split('/', '-').Select(static part => char.ToUpperInvariant(part[0]) + part[1..])));
var benchmark = type?.GetCustomAttributes<BenchmarkAttribute>().FirstOrDefault(b => b.FullName == name);
if (type is null || benchmark is null) {
    Console.Error.WriteLine($"no benchmark '{name}'");
    return 2;
}

var setup = type.GetMethod("Setup");
var run = setup is not null ? (Func<string>)setup.Invoke(null, benchmark.Arguments)! : type.GetMethod("Run")!.CreateDelegate<Func<string>>();
if (args.Contains("--measure")) {
    Measure();
} else {
    Console.WriteLine(run());
}

type.GetMethod("Cleanup")?.Invoke(null, null);
return 0;

long Option(string option, long otherwise) =>
    Array.IndexOf(args, option) is var at and >= 0 ? long.Parse(args[at + 1]) : otherwise;

void Measure() {
    var warmUp = Option("--warm-up-ms", 100) * 1e6;
    var sample = Option("--sample-ms", 10) * 1e6;
    var samples = Option("--samples", 20);
    var budget = Option("--budget-ms", 3000) * 1e6;

    string check = "";
    (double Wall, double Processor) Iterate(long iterations) {
        var processor = ProcessorTime.Nanoseconds;
        var begun = Stopwatch.GetTimestamp();
        for (long i = 0; i < iterations; i++) {
            check = run();
        }

        var wall = Stopwatch.GetElapsedTime(begun).TotalNanoseconds;
        return (wall, ProcessorTime.Nanoseconds - processor);
    }

    // The pilot.
    long iterations = 1;
    double warmed = 0;
    double wall;
    while (true) {
        (wall, _) = Iterate(iterations);
        warmed += wall;
        if (wall * 8 >= sample) {
            break;
        }

        iterations *= 2;
    }

    long Choose(double per) => Math.Max(1, (long)Math.Ceiling(sample / per));
    iterations = Choose(wall / iterations);

    // The warm-up.
    double last = 0;
    var (runs, rises) = (0, 0);
    while (warmed < warmUp || (rises < 2 && runs < 50 && warmed < 3 * warmUp)) {
        (wall, _) = Iterate(iterations);
        warmed += wall;
        runs++;
        var per = wall / iterations;
        rises += runs > 1 && per >= last ? 1 : 0;
        last = per;
        iterations = Choose(per);
    }

    var lines = new List<string>();
    double measured = 0;
    for (var i = 0; i < samples && (i < 5 || measured < budget); i++) {
        (wall, var processor) = Iterate(iterations);
        measured += wall;
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"sample\t{iterations}\t{wall}\t{processor}"));
    }

    var before = GC.GetTotalAllocatedBytes(precise: true);
    Iterate(iterations);
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    Console.WriteLine($"check\t{check.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t")}");
    Console.WriteLine(string.Join('\n', lines));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"allocated\t{(double)allocated / iterations}"));
}

/// <summary>
///     A benchmark's port: a static class, named after it, with 'public
///     static string Run()', or 'public static Func&lt;string&gt; Setup(…)'
///     of the arguments, and perhaps 'public static void Cleanup()'. Given
///     once for each case of a benchmark with arguments.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
sealed class BenchmarkAttribute(string name, params object[] arguments) : Attribute {
    public object[] Arguments { get; } = arguments;

    /// <summary>As the Sable harness names its case: <c>library/regex("words")</c>.</summary>
    public string FullName => Arguments.Length == 0 ? name
        : $"{name}({string.Join(", ", Arguments.Select(static a => a is string s ? $"\"{s}\"" : a.ToString()))})";
}

/// <summary>Of this process, and of its children that have ended and been waited for.</summary>
static class ProcessorTime {
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

    [StructLayout(LayoutKind.Sequential)]
    struct TimeSpec {
        public long Seconds;
        public long Nanoseconds;
    }

    const int Children = -1;

    [DllImport("libc", EntryPoint = "getrusage")]
    static extern int GetUsage(int who, out Usage usage);

    [DllImport("libc", EntryPoint = "clock_gettime")]
    static extern int GetTime(int clock, out TimeSpec time);

    public static double Nanoseconds {
        get {
            if (OperatingSystem.IsWindows()) {
                return Environment.CpuUsage.TotalTime.TotalNanoseconds;
            }

            // CLOCK_PROCESS_CPUTIME_ID, as Sable's runtime has it.
            _ = GetTime(OperatingSystem.IsMacOS() ? 12 : 2, out var time);
            var own = time.Seconds * 1e9 + time.Nanoseconds;
            return GetUsage(Children, out var usage) != 0 ? own
                : own + (usage.UserSeconds + usage.SystemSeconds) * 1e9 + ((double)usage.UserMicroseconds + usage.SystemMicroseconds) * 1e3;
        }
    }
}
