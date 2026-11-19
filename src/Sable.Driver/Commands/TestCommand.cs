using System.Globalization;
using Sable.SIL;
using Sable.Symbols;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable test</c>: build the <c>@test</c> functions into a harness and
///     run them, each in a process of its own; exits 0 when all pass.
/// </summary>
public sealed class TestCommand : HarnessCommand {
    public override string Name => "test";
    public override string Summary => "run the @test functions (and, with --doc, the doc examples)";

    public override string Usage => """
        usage: sable test [<file.sb>|<directory>...] [filter...] [--doc] [--bless] [--list] [--exact] [--show-output] [--release] [-g] [-- <harness options>]

        Without a file, tests the package in this directory or the nearest above it: its
        library with the files under tests/, and its dependencies. A directory stands for
        every .sb file under it, built as one program.

          filter             run the tests whose names (Module.function) contain it
          --doc              also run the ```sable examples in doc comments, as tests
          --bless            write each generator snapshot case's expected/ from what the generators produce (docs/08 §8.8)
          --list             list the tests instead of running them
          --exact            a filter is a whole name, not part of one
          --show-output      show what passing tests print, too
          --release          build optimised (tests build without the optimiser by default)
          -g                 build with debug information
          --exclusivity=...  keep or drop the dynamic exclusivity checks (kept by default)
          -j, --jobs <n>     run n tests at once (default: one per processor)
          --timeout <secs>   stop a test that runs longer, and fail it (default: none; one running
                             over 60 s is said to be)
          --in-process       run the tests one after another in one process, the first failure ending it
          --harness-out <p>  build the harness to p, with '-g' to debug it ('<p> --run-test <name>'), and run nothing
          --coverage         count the lines and branches of the package's code the tests run: a
                             summary per file, and target/coverage/lcov.info for a coverage viewer
          --cover <module>   count coverage in a standard library module instead (implies --coverage):
                             a name (Sable.Collections), its directory (lib/Sable.Collections) or one
                             file of it (lib/Sable.Collections/Stack.sb); may be given more than once
          --lcov <path>      write the LCOV file there instead
        """;

    int _jobs;
    TimeSpan? _timeout;
    bool _coverage;
    List<string> _covered = [];
    string? _lcov;

    protected override bool MeasureCoverage => _coverage;

    protected override Func<string, bool>? CoveredLibrary =>
        _covered.Count == 0 ? null : file => _covered.Any(c => file == c || (c.EndsWith('/') && file.StartsWith(c, StringComparison.Ordinal)));

    public override int Run(IReadOnlyList<string> arguments) {
        // The runner's own options; the rest are the harness's and the build's.
        _jobs = Environment.ProcessorCount;
        _timeout = null;
        _coverage = false;
        _covered = [];
        _lcov = null;
        var rest = new List<string>();
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "-j" or "--jobs" when i + 1 < arguments.Count && int.TryParse(arguments[i + 1], CultureInfo.InvariantCulture, out var jobs) && jobs > 0:
                    _jobs = jobs;
                    i++;
                    break;
                case "--timeout" when i + 1 < arguments.Count && double.TryParse(arguments[i + 1], CultureInfo.InvariantCulture, out var seconds) && seconds > 0:
                    _timeout = TimeSpan.FromSeconds(seconds);
                    i++;
                    break;
                case "--coverage":
                    _coverage = true;
                    break;
                case "--cover" when i + 1 < arguments.Count:
                    if (LibraryFiles(arguments[++i]) is not { } covered) {
                        return 2;
                    }

                    _covered.Add(covered);
                    _coverage = true;
                    break;
                case "--lcov" when i + 1 < arguments.Count:
                    _lcov = Path.GetFullPath(arguments[++i]);
                    break;
                case "-j" or "--jobs" or "--timeout" or "--cover" or "--lcov":
                    return BadUsage();
                case "--":
                    rest.AddRange(arguments.Skip(i));
                    i = arguments.Count;
                    break;
                default:
                    rest.Add(arguments[i]);
                    break;
            }
        }

        return base.Run(rest);
    }

    /// <summary>
    ///     What '--cover' names, as the prefix of the names the library's
    ///     files are embedded with: <c>lib/Sable.Collections/</c> for the
    ///     module (by name or directory), or one file's whole name. Null,
    ///     having said why, when it names no module of the library.
    /// </summary>
    static string? LibraryFiles(string named) {
        var path = named.Replace('\\', '/').TrimEnd('/');
        // A path is taken from its 'lib' directory on: lib/Sable.Collections,
        // ./lib/Sable.Collections/Stack.sb, /checkout/lib/Sable.Collections.
        var within = path.StartsWith("lib/", StringComparison.Ordinal) ? path["lib/".Length..]
            : path.LastIndexOf("/lib/", StringComparison.Ordinal) is var at and >= 0 ? path[(at + "/lib/".Length)..]
            : path;
        var parts = within.Split('/');
        var module = parts[0];
        if ((module != Library.Core && !Library.ModuleNames.Contains(module)) || parts.Length > 2 || (parts.Length == 2 && !parts[1].EndsWith(".sb", StringComparison.Ordinal))) {
            Console.Error.WriteLine($"sable: --cover: '{named}' is not a module of the standard library, or a file of one");
            return null;
        }

        return parts.Length == 2 ? $"lib/{module}/{parts[1]}" : $"lib/{module}/";
    }

    /// <summary>
    ///     Runs the tests several at once, each in a process of its own and
    ///     within the time limit (<see cref="TestRunner" />); a listing, or a
    ///     run in one process, is the harness's own.
    /// </summary>
    protected override int Execute(string executable, IReadOnlyList<string> arguments) {
        if (CoverageCounters is not { } counters) {
            return Run(executable, arguments);
        }

        // Each test process appends its counts to one file, which is then
        // read. The file is named to those processes alone, not set in this
        // process's environment, which every command running here shares.
        var counts = Path.Combine(Path.GetTempPath(), $"sable-coverage-{Guid.NewGuid():N}.txt");
        try {
            var code = Run(executable, arguments, new Dictionary<string, string> { ["SABLE_COVERAGE"] = counts });
            if (!arguments.Contains("--list")) {
                Report(counters, Coverage.ReadCounts(counts));
            }

            return code;
        } finally {
            File.Delete(counts);
        }
    }

    int Run(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null) =>
        TestRunner.Handles(arguments)
            ? TestRunner.Run(executable, arguments, _jobs, _timeout, Console.Out, Console.Error, environment)
            : Execute(executable, arguments, environment);

    /// <summary>
    ///     The lines and branches of each measured file the tests ran: the
    ///     share of each, the lines that never ran, the decisions some of
    ///     whose outcomes never happened, and an LCOV file of the whole for
    ///     a coverage viewer.
    /// </summary>
    void Report(CoverageMap counters, IReadOnlyDictionary<int, long> counts) {
        var files = counters.Lines(counts);
        var branches = counters.Branches(counts);
        var lcov = _lcov ?? Path.Combine(ProgramDirectory, "target", "coverage", "lcov.info");
        Directory.CreateDirectory(Path.GetDirectoryName(lcov)!);
        File.WriteAllText(lcov, Coverage.Lcov(files, branches));

        var total = files.Values.Sum(static f => f.Count);
        var hit = files.Values.Sum(static f => f.Values.Count(static c => c > 0));
        var outcomes = branches.Values.Sum(static f => f.Sum(static d => d.Outcomes.Count));
        var happened = branches.Values.Sum(static f => f.Sum(static d => d.Outcomes.Count(static o => o.Count > 0)));
        Console.Out.WriteLine();
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"coverage: {Percent(hit, total)} of lines ({hit} of {total}), {Percent(happened, outcomes)} of branches ({happened} of {outcomes}); {Path.GetRelativePath(Directory.GetCurrentDirectory(), lcov)}"));
        foreach (var (file, lines) in files) {
            var ran = lines.Values.Count(static c => c > 0);
            var missed = Ranges([..lines.Where(static l => l.Value == 0).Select(static l => l.Key)]);
            var decisions = branches.GetValueOrDefault(file) ?? [];
            var fileOutcomes = decisions.Sum(static d => d.Outcomes.Count);
            var fileHappened = decisions.Sum(static d => d.Outcomes.Count(static o => o.Count > 0));
            var name = Path.IsPathRooted(file) ? Path.GetRelativePath(ProgramDirectory, file) : file;
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {name}: {Percent(ran, lines.Count)} ({ran} of {lines.Count}){(missed.Length > 0 ? $", not run: {missed}" : "")}; branches {Percent(fileHappened, fileOutcomes)} ({fileHappened} of {fileOutcomes})"));

            // A decision made but not every way: the line ran, yet some of
            // it did not. One never made is on a line not run, said above.
            foreach (var decision in decisions.Where(static d => d.Reached && d.Missed.Any())) {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"    {decision.Line}:{decision.Column} '{decision.Kind}' never: {string.Join(", ", decision.Missed)}"));
            }
        }
    }

    static string Percent(int part, int whole) =>
        whole == 0 ? "100%" : (100.0 * part / whole).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>Line numbers as runs: <c>3, 7-9, 12</c>.</summary>
    static string Ranges(IReadOnlyList<int> lines) {
        var parts = new List<string>();
        for (var i = 0; i < lines.Count; i++) {
            var start = lines[i];
            while (i + 1 < lines.Count && lines[i + 1] == lines[i] + 1) {
                i++;
            }

            parts.Add(start == lines[i] ? start.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{start}-{lines[i]}"));
        }

        return string.Join(", ", parts);
    }

    protected override TestKind Kind => TestKind.Test;
    protected override bool OptimiseByDefault => false;

    protected override IReadOnlyDictionary<string, bool> HarnessOptions { get; } = new Dictionary<string, bool>(StringComparer.Ordinal) {
        ["--list"] = false,
        ["--exact"] = false,
        ["--show-output"] = false,
        ["--in-process"] = false,
    };
}
