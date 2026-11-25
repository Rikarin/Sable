using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using Sable.CodeGen;
using Sable.SIL;
using Sable.Symbols;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable bench</c>: build the <c>@bench</c> functions, optimised, into a
///     harness that runs each in a process of its own, and compares with a
///     saved baseline, another way to build or run them, or another commit.
/// </summary>
public sealed class BenchCommand : HarnessCommand {
    public override string Name => "bench";
    public override string Summary => "run the @bench functions and compare with a baseline, a job or a commit";

    public override string Usage => """
        usage: sable bench [<file.sb>|<directory>...] [filter...] [--filter <expression>] [options]

        Without a file, benchmarks the package in this directory or the nearest above it: its
        library with the files under benches/, and its dependencies. A directory stands for
        every .sb file under it, built as one program.

          filter                  run the benchmarks whose names (Module.function) contain it; a case
                                  of a @bench(arguments:) one is named Module.function(arguments)
          --filter <expr>         run those it selects, as 'sable test --filter' does: 'tag:fast & !name:*Slow*'
          --exact                 a filter is a whole name, not part of one
          --list                  list the benchmarks instead of running them
          --in-process            run them in the harness's own process, not one each

        How much is measured: each benchmark is warmed up, its iterations chosen so that a sample lasts
        10 ms, the loop's own time taken out, and 15 to 100 samples taken, until the mean's 99.9 %
        confidence interval is 2 % either way or 3 s have passed.

          --samples <n>           that many samples, unless the time for them is over
          --sample-ms, --warm-up-ms, --budget-ms <n>  a sample's time, the warm-up's, and the most there is for samples
          --quick                 a few short samples: to check the benchmarks run, not to time them
          --metric wall|processor what is reported and compared: the clock's time (the default), or
                                  processor time, of every thread and of the processes waited for
          --outliers remove-upper|remove-all|keep  which samples beyond Tukey's fences are left out
          --launches <n>          run each in n processes (1, or 3 of each job), all of their samples counted
          --counters              instructions and cycles too, where the system gives them without
                                  privileges: macOS; Linux where perf_event_paranoid is 2 or less and
                                  the (virtual) machine has counters; Windows cycles alone

        What it is compared with, and what comes of it: a benchmark regressed when the Mann-Whitney test
        says p < 0.001 and its median is slower by more than the threshold.

          --save-baseline <name>  keep the results, as <baseline-dir>/<name>.txt
          --baseline <name|file>  compare with a kept baseline (by default, with 'default' if kept)
          --baseline-dir <path>   where baselines are kept (default: target/bench)
          --compare <ref|file>    compare with the benchmarks as they are at that git commit, branch or
                                  tag, built by this compiler and run in turn with these; or a baseline
          --job <name>=<how>      build or run the benchmarks that way, words of: -O0 ... -Oz, -g,
                                  -generics=<mode>, --exclusivity=<...>, VARIABLE=value (SABLE_WORKERS=1),
                                  ref=<git ref>, sable=<another sable>; given more than once, each is run
                                  in turn, and compared with the first
          --threshold <percent>   how much slower is a regression (default 5)
          --fail-on-regression    exit with 1 if there is one

          --markdown, --csv, --json <path>  write the results there
          --history <path>        add a row for each there: date, commit, benchmark, ms, allocations, retains, releases
          --sil, --asm            print the benchmarks' SIL or assembly, and their closures', as built, and run nothing
          -O0 ... -O3, -Os, -Oz   the optimisation level (default -O2)
          --harness-out <path>    build the harness there and run nothing
        """;

    protected override TestKind Kind => TestKind.Bench;
    protected override bool OptimiseByDefault => true;
    protected override bool KeepsHarness => !_sil && !_asm;

    protected override IReadOnlyDictionary<string, bool> HarnessOptions { get; } = new Dictionary<string, bool>(StringComparer.Ordinal) {
        ["--list"] = false,
        ["--describe"] = false,
        ["--exact"] = false,
        ["--in-process"] = false,
        ["--filter"] = true,
        ["--save-baseline"] = true,
        ["--baseline"] = true,
        ["--baseline-dir"] = true,
        ["--quick"] = false,
        ["--samples"] = true,
        ["--sample-ms"] = true,
        ["--warm-up-ms"] = true,
        ["--budget-ms"] = true,
        ["--metric"] = true,
        ["--outliers"] = true,
        ["--launches"] = true,
        ["--counters"] = false,
        ["--threshold"] = true,
        ["--fail-on-regression"] = false,
        ["--markdown"] = true,
        ["--csv"] = true,
        ["--json"] = true,
        ["--history"] = true,
        // The command's own: what a history's rows begin with.
        ["--stamp"] = true,
    };

    readonly List<Job> _jobs = [];
    bool _sil, _asm;

    /// <summary>A way to build and run the benchmarks: '--job name=words'.</summary>
    sealed record Job(string Name, IReadOnlyList<string> BuildFlags, IReadOnlyList<string> Variables, string? Ref, string? Compiler);

    public override int Run(IReadOnlyList<string> arguments) {
        _jobs.Clear();
        _sil = _asm = false;
        var rest = new List<string>();
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--sil": _sil = true; break;
                case "--asm": _asm = true; break;
                case "--job" when i + 1 < arguments.Count:
                    if (ParseJob(arguments[++i]) is not { } job) {
                        return BadUsage();
                    }

                    _jobs.Add(job);
                    break;
                case "--compare" when i + 1 < arguments.Count: {
                    var with = arguments[++i];
                    if (File.Exists(with)) {
                        rest.AddRange(["--baseline", Path.GetFullPath(with)]);
                    } else {
                        _jobs.Add(new(with, [], [], with, null));
                        _jobs.Add(new("this", [], [], null, null));
                    }

                    break;
                }

                case "--job" or "--compare": return BadUsage();
                case "--history":
                    rest.AddRange(["--stamp", Stamp(), arguments[i]]);
                    break;
                default: rest.Add(arguments[i]); break;
            }
        }

        if (_jobs.Count == 0) {
            return base.Run(rest);
        }

        if (_jobs.GroupBy(static j => j.Name).FirstOrDefault(static g => g.Count() > 1) is { } twice) {
            Console.Error.WriteLine($"sable: there are two jobs named '{twice.Key}'");
            return 2;
        }

        return RunJobs(rest);
    }

    static Job? ParseJob(string text) {
        if (text.Split('=', 2) is not [{ Length: > 0 } name, var how]) {
            return null;
        }

        var (flags, variables, at, compiler) = (new List<string>(), new List<string>(), (string?)null, (string?)null);
        foreach (var word in how.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            if (word.StartsWith('-')) {
                flags.Add(word);
            } else if (word.StartsWith("ref=", StringComparison.Ordinal)) {
                at = word[4..];
            } else if (word.StartsWith("sable=", StringComparison.Ordinal)) {
                compiler = Path.GetFullPath(word[6..]);
            } else if (word.IndexOf('=') > 0) {
                variables.Add(word);
            } else {
                return null;
            }
        }

        return new(name, flags, variables, at, compiler);
    }

    /// <summary>The date and the commit, and '+' for changes not committed: a history's first two columns.</summary>
    static string Stamp() {
        var commit = Git(".", "rev-parse", "--short", "HEAD") ?? "-";
        var changed = Git(".", "status", "--porcelain") is { Length: > 0 };
        return string.Create(CultureInfo.InvariantCulture, $"{DateTime.Today:yyyy-MM-dd}\t{commit}{(changed ? "+" : "")}");
    }

    /// <summary>A harness built for each job, by this compiler or the one it names, and the first of them run with the others to start.</summary>
    int RunJobs(IReadOnlyList<string> arguments) {
        if (Parse(arguments) is not { } options || options.Output is not null) {
            return BadUsage();
        }

        var directory = Directory.CreateTempSubdirectory("sable-bench-");
        try {
            var harness = new List<string>(options.HarnessArguments);
            var first = "";
            var builds = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < _jobs.Count; i++) {
                var job = _jobs[i];
                // Jobs that differ in what they are run with alone share a harness.
                var building = job.Variables.Where(static v => v.StartsWith("SABLE_GENERICS", StringComparison.Ordinal) || v.StartsWith("CC=", StringComparison.Ordinal));
                var key = string.Join('\n', [..job.BuildFlags, "", job.Ref ?? "", job.Compiler ?? "", ..building]);
                var isBuilt = builds.TryGetValue(key, out var executable);
                executable ??= builds[key] = Path.Combine(directory.FullName, $"job{i}{NativeTarget.Host.ExecutableSuffix}");
                var place = Directory.GetCurrentDirectory();
                if (!isBuilt && job.Ref is { } at) {
                    if (Extract(at, Path.Combine(directory.FullName, $"ref{i}")) is not { } extracted) {
                        return 2;
                    }

                    place = extracted;
                }

                List<string> build = ["bench", ..options.Files.Select(f => job.Ref is null ? f : Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(f)))];
                build.AddRange([..BuildArguments(options), ..job.BuildFlags, "--harness-out", executable]);
                if (!isBuilt && RunSable(job.Compiler, build, place, job.Variables) is not 0 and var failed) {
                    Console.Error.WriteLine($"sable: the job '{job.Name}' could not be built");
                    return failed;
                }

                first = i == _jobs.Count - 1 ? executable : first;
                harness.AddRange(["--job", string.Join('\t', [job.Name, executable, ..job.Variables])]);
            }

            return Execute(first, harness, null);
        } finally {
            directory.Delete(true);
        }
    }

    static IEnumerable<string> BuildArguments(Options options) {
        if (options.OptLevel is { } level) {
            yield return "-O" + level;
        }

        if (options.Debug) {
            yield return "-g";
        }

        if (options.Exclusivity is { } kept) {
            yield return kept ? "--exclusivity=checked" : "--exclusivity=unchecked";
        }
    }

    /// <summary>This compiler, or the one given, run in a process of its own: each job's environment is its own.</summary>
    static int RunSable(string? compiler, IReadOnlyList<string> arguments, string directory, IReadOnlyList<string> variables) {
        var start = new ProcessStartInfo { WorkingDirectory = directory };
        var self = Environment.ProcessPath ?? "";
        var assembly = typeof(BenchCommand).Assembly.Location;
        if (compiler is not null) {
            start.FileName = compiler;
        } else if (Path.GetFileNameWithoutExtension(self) == "sable" || assembly.Length == 0) {
            start.FileName = self;
        } else {
            // In another program, as a test is: the driver's assembly, by the .NET host.
            start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host
                : Path.GetFileNameWithoutExtension(self) == "dotnet" ? self : "dotnet";
            start.ArgumentList.Add(assembly);
        }

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        foreach (var variable in variables) {
            var parts = variable.Split('=', 2);
            start.Environment[parts[0]] = parts[1];
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    ///     The repository's files as they are at a commit, written to a
    ///     directory; gives where this directory is among them, or null,
    ///     having said why.
    /// </summary>
    static string? Extract(string reference, string directory) {
        var prefix = Git(".", "rev-parse", "--show-prefix");
        var root = Git(".", "rev-parse", "--show-toplevel");
        if (root is null || Git(".", "rev-parse", "--verify", "--quiet", "--end-of-options", reference + "^{commit}") is not { } commit) {
            Console.Error.WriteLine($"sable: '{reference}' is neither a file nor a commit of a git repository here");
            return null;
        }

        Directory.CreateDirectory(directory);
        var archive = directory + ".tar";
        if (Git(root, "archive", "--format=tar", "-o", archive, commit) is null) {
            Console.Error.WriteLine($"sable: 'git archive {reference}' failed");
            return null;
        }

        TarFile.ExtractToDirectory(archive, directory, overwriteFiles: true);
        File.Delete(archive);
        var here = Path.Combine(directory, prefix ?? "");
        if (!Directory.Exists(here)) {
            Console.Error.WriteLine($"sable: there is no '{prefix}' at '{reference}'");
            return null;
        }

        return here;
    }

    static string? Git(string directory, params string[] arguments) {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Path.GetFullPath(directory), RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        try {
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = errors.Result;
            return process.ExitCode == 0 ? output.Trim() : null;
        } catch (System.ComponentModel.Win32Exception) {
            return null;
        }
    }

    /// <summary>With '--sil' or '--asm', what the benchmarks chosen were compiled to, in place of a run.</summary>
    protected override int Execute(string executable, IReadOnlyList<string> arguments) {
        if (!_sil && !_asm) {
            return base.Execute(executable, arguments);
        }

        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true };
        foreach (var argument in arguments.Append("--describe")) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var described = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || Built is not { Sil: { } module, LlvmIr: { } ir }) {
            return process.ExitCode;
        }

        // A name is 'Module.function' or 'Module.function(arguments)': the
        // function's symbol, and those of its closures, 'Module.function$…'.
        var chosen = described.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split('\t')[0].Split('(')[0])
            .Where(static name => name != "filtered out")
            .ToHashSet(StringComparer.Ordinal);
        bool Wanted(string symbol) => chosen.Contains(symbol.Split('$')[0]);

        if (_sil) {
            foreach (var function in module.Functions.Where(f => f.Blocks.Count > 0 && Wanted(f.Name))) {
                Console.WriteLine(SilPrinter.Print(function));
            }
        }

        if (_asm) {
            var assembly = NativeBuild.CompileToAssembly(ir, Native);
            if (!assembly.Succeeded) {
                Console.Error.Write(assembly.Output);
                return 1;
            }

            foreach (var (symbol, text) in NativeBuild.FunctionsOf(assembly.Output)) {
                if (symbol.StartsWith("sable.", StringComparison.Ordinal) && Wanted(symbol["sable.".Length..])) {
                    Console.WriteLine(text);
                }
            }
        }

        return 0;
    }
}
