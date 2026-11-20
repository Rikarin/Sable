using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sable.CodeGen;
using Sable.Symbols;

namespace Sable.Driver.Commands;

/// <summary>What <c>sable fuzz</c> was asked for (<see cref="FuzzCommand.ParseArguments" />).</summary>
/// <param name="Files">Source files named, to build as one program; none for the package here.</param>
/// <param name="Target">The word that selects targets by name; null for all of them.</param>
/// <param name="Time">How long to fuzz; null until stopped.</param>
/// <param name="Inputs">With <paramref name="Replay" />, the inputs to run; none for every saved one.</param>
/// <param name="Minimise">The input to minimise, if that is what was asked.</param>
/// <param name="EngineArguments">What follows <c>--</c>: libFuzzer's own options, passed on as they are.</param>
public sealed record FuzzOptions(
    IReadOnlyList<string> Files,
    string? Target,
    TimeSpan? Time,
    bool List,
    bool Replay,
    IReadOnlyList<string> Inputs,
    string? Minimise,
    IReadOnlyList<string> EngineArguments);

/// <summary>
///     Where one fuzz target keeps what fuzzing finds, under the program's
///     <c>target/fuzz/</c>: <c>&lt;Module.function&gt;/corpus/</c>, the inputs
///     that reached code no other had, which each run starts from;
///     <c>crashes/</c>, the inputs that trapped, crashed or hung, each with
///     its minimised copy (<c>.min</c>); and <c>summary.txt</c>, the distinct
///     failures. The builds, which serve every target, are in <c>build/</c>.
/// </summary>
public sealed record FuzzLayout(string Root, string Target) {
    public string Directory => Path.Combine(Root, Target);
    public string Corpus => Path.Combine(Directory, "corpus");
    public string Crashes => Path.Combine(Directory, "crashes");
    public string Summary => Path.Combine(Directory, "summary.txt");
    public string Fuzzer => Path.Combine(Root, "build", "fuzzer");
    public string ReplayProgram => Path.Combine(Root, "build", "replay");

    /// <summary>The layout of target <paramref name="target" /> (<c>Module.function</c>) under a program's <c>target/</c> directory.</summary>
    public static FuzzLayout For(string targetDirectory, string target) => new(Path.Combine(targetDirectory, "fuzz"), target);

    /// <summary>The failing inputs kept: what is in <c>crashes/</c> but the minimised copies and notes.</summary>
    public IReadOnlyList<string> SavedCrashes() =>
        System.IO.Directory.Exists(Crashes)
            ? [..System.IO.Directory.GetFiles(Crashes).Where(static f => !f.EndsWith(".min", StringComparison.Ordinal) && !f.EndsWith(".txt", StringComparison.Ordinal)).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>The corpus's inputs.</summary>
    public IReadOnlyList<string> SavedCorpus() =>
        System.IO.Directory.Exists(Corpus) ? [..System.IO.Directory.GetFiles(Corpus).Order(StringComparer.Ordinal)] : [];
}

/// <summary>
///     <c>sable fuzz</c> (docs/05 §5.1): build the program's <c>@fuzz</c>
///     functions with LLVM's coverage instrumentation, AddressSanitizer and
///     UBSan, linked with libFuzzer, and fuzz one of them — keeping the corpus
///     and what fails under <c>target/fuzz/</c>, minimising each failure, and
///     summing up the distinct ones; or run the saved inputs once each
///     (<c>--replay</c>), or minimise one.
/// </summary>
/// <remarks>
///     The program is built as a library (<see cref="TestHarness" />), whose
///     exported functions the shim C calls once an input
///     (<see cref="FuzzBuild" />). libFuzzer runs in fork mode, so that one
///     failure does not end the run: each is saved, and fuzzing goes on until
///     the time is up or Ctrl-C, after which the command replays what failed,
///     minimises it and writes the summary. It exits 1 when a saved input
///     still fails.
/// </remarks>
public sealed partial class FuzzCommand : HarnessCommand {
    public override string Name => "fuzz";
    public override string Summary => "fuzz the @fuzz functions with libFuzzer";

    public override string Usage => """
        usage: sable fuzz [<file.sb>...] [<target>] [--time <duration>] [-j <n>] [--list] [--replay [<input>...]] [--minimise <input>] [-- <libFuzzer option>...]

        Without a file, fuzzes the package in this directory or the nearest above it: its library with
        the files under tests/ and benches/. A target is a @fuzz function, Module.function; a word
        selects those whose names contain it. Fuzzing and minimising take one target.

          --time <duration>      fuzz this long (90s, 30m, 8h), then stop and sum up; Ctrl-C stops sooner
          -j <n>                 fuzz in n processes at once (libFuzzer's -fork)
          --list                 list the fuzz targets
          --replay [<input>...]  run the target's saved inputs (or those given) once each, and say what fails
          --minimise <input>     the smallest input that fails as this one does, written as <input>.min
          -- <option>...         passed to libFuzzer: -max_len=4096, -dict=<file>, -timeout=<s>

        What fuzzing finds is kept under target/fuzz/<target>/: corpus/, the inputs that reached new
        code, and crashes/, those that failed, each minimised, with summary.txt. Inputs in the
        package's fuzz/<target>/ start the corpus. Fuzzing needs a clang with libFuzzer's runtime
        (CC, or clang): Linux's has it; Apple's does not, and there --replay is what runs.
        """;

    protected override TestKind Kind => TestKind.Fuzz;
    protected override bool OptimiseByDefault => false;
    protected override IReadOnlyDictionary<string, bool> HarnessOptions { get; } = new Dictionary<string, bool>();

    /// <summary>How long one input may run before libFuzzer calls it a hang, unless <c>-- -timeout=</c> says otherwise.</summary>
    const int InputSeconds = 20;

    /// <summary>How long minimising one failing input may take.</summary>
    const int MinimiseSeconds = 60;

    /// <summary>The command line, or null with the reason.</summary>
    public static FuzzOptions? ParseArguments(IReadOnlyList<string> arguments, out string? error) {
        error = null;
        var files = new List<string>();
        var inputs = new List<string>();
        string? target = null, minimise = null;
        TimeSpan? time = null;
        bool list = false, replay = false;
        IReadOnlyList<string> engine = [];
        for (var i = 0; i < arguments.Count; i++) {
            var argument = arguments[i];
            switch (argument) {
                case "--":
                    engine = [..arguments.Skip(i + 1)];
                    i = arguments.Count;
                    continue;
                case "--list":
                    list = true;
                    continue;
                case "--replay":
                    replay = true;
                    continue;
                case "--time" or "--minimise":
                    if (i + 1 >= arguments.Count) {
                        error = $"{argument} needs a value";
                        return null;
                    }

                    var value = arguments[++i];
                    if (argument == "--minimise") {
                        minimise = value;
                    } else if (ParseDuration(value) is { } duration) {
                        time = duration;
                    } else {
                        error = $"--time takes a duration such as 90s, 30m or 8h, not '{value}'";
                        return null;
                    }

                    continue;
            }

            if (argument.StartsWith('-')) {
                error = $"unknown option '{argument}'";
                return null;
            }

            if (argument.EndsWith(".sb", StringComparison.Ordinal)) {
                files.Add(argument);
            } else if (target is null && !(replay && File.Exists(argument))) {
                target = argument;
            } else if (replay) {
                inputs.Add(argument);
            } else {
                error = $"one target at a time: '{target}' and '{argument}'";
                return null;
            }
        }

        if (new[] { list, replay, minimise is not null }.Count(static b => b) > 1) {
            error = "--list, --replay and --minimise are each a command of their own";
            return null;
        }

        if (time is not null && (list || replay || minimise is not null)) {
            error = "--time is how long to fuzz, so it goes with fuzzing alone";
            return null;
        }

        return new(files, target, time, list, replay, inputs, minimise, engine);
    }

    /// <summary>A duration: a whole number with <c>ms</c>, <c>s</c>, <c>m</c> or <c>h</c>, or none for seconds.</summary>
    public static TimeSpan? ParseDuration(string text) {
        var match = DurationPattern().Match(text);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var count) || count <= 0 || count > 1_000_000_000) {
            return null;
        }

        return match.Groups[2].Value switch {
            "ms" => TimeSpan.FromMilliseconds(count),
            "m" => TimeSpan.FromMinutes(count),
            "h" => TimeSpan.FromHours(count),
            _ => TimeSpan.FromSeconds(count),
        };
    }

    [GeneratedRegex("^([0-9]+)(ms|s|m|h)?$")]
    private static partial Regex DurationPattern();

    /// <summary>
    ///     What libFuzzer is run with to fuzz: the corpus first, where it
    ///     writes what it keeps, then the seed directories; where failures go;
    ///     fork mode with <paramref name="jobs" /> processes, which outlives a
    ///     failure; the time limit; and what the user passed on.
    /// </summary>
    public static IReadOnlyList<string> EngineArguments(FuzzLayout layout, IReadOnlyList<string> seeds, TimeSpan? time, int jobs, IReadOnlyList<string> extra) => [
        layout.Corpus, ..seeds,
        $"-artifact_prefix={layout.Crashes}{Path.DirectorySeparatorChar}",
        $"-fork={jobs}", "-ignore_crashes=1", "-ignore_timeouts=1", "-ignore_ooms=1",
        ..(extra.Any(static a => a.StartsWith("-timeout=", StringComparison.Ordinal)) ? [] : new[] { $"-timeout={InputSeconds}" }),
        "-print_final_stats=1",
        ..(time is { } t ? [$"-max_total_time={(long)Math.Ceiling(t.TotalSeconds)}"] : Array.Empty<string>()),
        ..extra,
    ];

    /// <summary>What libFuzzer is run with to minimise a failing input into <c>&lt;input&gt;.min</c>.</summary>
    public static IReadOnlyList<string> MinimiseArguments(string input, int seconds) =>
        ["-minimize_crash=1", $"-max_total_time={seconds}", $"-timeout={InputSeconds}", $"-exact_artifact_path={input}.min", input];

    public override int Run(IReadOnlyList<string> arguments) {
        if (ParseArguments(arguments, out var error) is not { } options) {
            Console.Error.WriteLine($"sable fuzz: {error}");
            return BadUsage();
        }

        if (!NativeBuild.IsAvailable()) {
            Console.Error.WriteLine($"sable: cannot run '{NativeBuild.CompilerPath}'; set CC to a clang that can link for this platform");
            return 1;
        }

        // Said before anything is compiled: fuzzing and minimising are libFuzzer's.
        var engine = FuzzBuild.HasLibFuzzer ? FuzzEngine.LibFuzzer : FuzzEngine.Replay;
        if (engine == FuzzEngine.Replay && !options.List && !options.Replay) {
            Console.Error.WriteLine(FuzzBuild.MissingLibFuzzer(NativeBuild.CompilerPath, NativeBuild.CompilerVersion, NativeBuild.Host));
            return 1;
        }

        if (engine == FuzzEngine.Replay && options.Replay && !FuzzBuild.HasSanitizers) {
            Console.Error.WriteLine($"sable fuzz: '{NativeBuild.CompilerPath}' cannot link -fsanitize=address,undefined, which the replay build is made with; set CC to a clang that has its sanitizers");
            return 1;
        }

        if (Program(new Options(options.Files, [], false, false, false, null)) is not { } program) {
            return 1;
        }

        var result = TestHarness.Compile(program.Trees, TestKind.Fuzz, out var targets);
        foreach (var warning in result.Warnings) {
            Console.Error.WriteLine(warning);
            Console.Error.WriteLine();
        }

        if (!result.Succeeded) {
            foreach (var problem in result.Errors) {
                Console.Error.WriteLine(problem);
                Console.Error.WriteLine();
            }

            DiagnosticDocs.WriteHint(Console.Error, result.Errors);
            return 1;
        }

        var chosen = targets.Select(static (t, i) => (Target: t, Index: i))
            .Where(t => options.Target is null || t.Target.QualifiedName.Contains(options.Target, StringComparison.Ordinal)).ToList();
        if (options.List) {
            foreach (var (target, _) in chosen) {
                Console.WriteLine($"{target.QualifiedName}: fuzz ({target.Location})");
            }

            Console.WriteLine(chosen.Count == 1 ? "1 fuzz target" : $"{chosen.Count} fuzz targets");
            return 0;
        }

        if (chosen.Count == 0) {
            Console.Error.WriteLine(targets.Count == 0
                ? "sable fuzz: the program has no @fuzz functions"
                : $"sable fuzz: no fuzz target is called '{options.Target}'; there are {string.Join(", ", targets.Select(static t => t.QualifiedName))}");
            return 1;
        }

        if (!options.Replay && chosen.Count > 1) {
            Console.Error.WriteLine($"sable fuzz: fuzzing takes one target, and {(options.Target is null ? "there are" : $"'{options.Target}' names")} {chosen.Count}: {string.Join(", ", chosen.Select(static t => t.Target.QualifiedName))}");
            return 1;
        }

        var root = FuzzLayout.For(program.TargetDirectory, "").Root;
        var binary = engine == FuzzEngine.LibFuzzer ? Path.Combine(root, "build", "fuzzer") : Path.Combine(root, "build", "replay");
        var build = FuzzBuild.Build(result.LlvmIr!, binary, engine, program.Link);
        if (!build.Succeeded) {
            Console.Error.WriteLine("internal error: the fuzz build failed:");
            Console.Error.Write(build.Output);
            return 1;
        }

        if (options.Replay) {
            var failed = false;
            foreach (var (target, index) in chosen) {
                var layout = FuzzLayout.For(program.TargetDirectory, target.QualifiedName);
                IReadOnlyList<string> inputs = options.Inputs.Count > 0 ? options.Inputs : [..layout.SavedCrashes(), ..layout.SavedCorpus()];
                var outcomes = Replay(binary, index, inputs);
                var failures = outcomes.Where(static o => o.Report is not null).ToList();
                Console.WriteLine($"{target.QualifiedName}: {inputs.Count} input{(inputs.Count == 1 ? "" : "s")} run, {failures.Count} fail{(failures.Count == 1 ? "s" : "")}");
                foreach (var (input, report) in failures) {
                    Console.WriteLine($"  {input}: {Signature(report!)}");
                }

                failed |= failures.Count > 0;
            }

            return failed ? 1 : 0;
        }

        var (only, number) = chosen[0];
        var fuzzed = FuzzLayout.For(program.TargetDirectory, only.QualifiedName);
        if (options.Minimise is { } minimised) {
            var code = RunEngine(binary, MinimiseArguments(minimised, MinimiseSeconds), Environment(number));
            Console.WriteLine(File.Exists(minimised + ".min") ? $"{minimised}: minimised to {new FileInfo(minimised + ".min").Length} bytes, in {minimised}.min" : $"{minimised}: not minimised");
            return code == 0 ? 0 : 1;
        }

        Directory.CreateDirectory(fuzzed.Corpus);
        Directory.CreateDirectory(fuzzed.Crashes);
        var seeds = Path.Combine(program.Directory, "fuzz", only.QualifiedName);
        var jobs = GlobalOptions.Current.Jobs ?? 1;
        var before = fuzzed.SavedCrashes().ToHashSet(StringComparer.Ordinal);

        // Ctrl-C reaches libFuzzer too, which stops and prints its numbers;
        // this waits for it, and sums up.
        ConsoleCancelEventHandler stay = static (_, e) => e.Cancel = true;
        Console.CancelKeyPress += stay;
        try {
            RunEngine(binary, EngineArguments(fuzzed, Directory.Exists(seeds) ? [seeds] : [], options.Time, jobs, options.EngineArguments), Environment(number));
        } finally {
            Console.CancelKeyPress -= stay;
        }

        return Summarise(fuzzed, binary, number, before);
    }

    /// <summary>The environment the fuzz build runs a target in: its number, and ASan without its leak check (the runtime has its own) unless asked otherwise.</summary>
    static Dictionary<string, string> Environment(int target) {
        var environment = new Dictionary<string, string> { ["SABLE_FUZZ_TARGET"] = target.ToString(CultureInfo.InvariantCulture) };
        if (System.Environment.GetEnvironmentVariable("ASAN_OPTIONS") is not { Length: > 0 }) {
            environment["ASAN_OPTIONS"] = "detect_leaks=0";
        }

        return environment;
    }

    /// <summary>
    ///     After a run: each failing input kept is replayed, minimised if it has
    ///     not been, and grouped by what it says; the groups go to the console
    ///     and to <c>summary.txt</c>. 1 when any still fails.
    /// </summary>
    static int Summarise(FuzzLayout layout, string fuzzer, int target, IReadOnlySet<string> before) {
        var crashes = layout.SavedCrashes();
        var outcomes = Replay(fuzzer, target, crashes);
        var text = new StringBuilder();
        var failing = outcomes.Where(static o => o.Report is not null).ToList();
        text.Append(CultureInfo.InvariantCulture, $"{layout.Target}: {failing.Count} failing input{(failing.Count == 1 ? "" : "s")}");
        foreach (var group in failing.GroupBy(static o => Signature(o.Report!)).OrderBy(static g => g.Key, StringComparer.Ordinal)) {
            text.Append(CultureInfo.InvariantCulture, $"\n  {group.Key}");
            foreach (var (input, report) in group) {
                var name = Path.GetFileName(input);
                if (!File.Exists(input + ".min") && !name.StartsWith("timeout-", StringComparison.Ordinal) && !name.StartsWith("slow-unit-", StringComparison.Ordinal)) {
                    RunEngine(fuzzer, MinimiseArguments(input, MinimiseSeconds), Environment(target), quiet: true);
                }

                File.WriteAllText(input + ".txt", report);
                var minimised = File.Exists(input + ".min") ? $", minimised to {new FileInfo(input + ".min").Length} bytes in {name}.min" : "";
                text.Append(CultureInfo.InvariantCulture, $"\n    {name}{(before.Contains(input) ? "" : " (new)")}: {new FileInfo(input).Length} bytes{minimised}");
            }
        }

        foreach (var (input, _) in outcomes.Where(static o => o.Report is null)) {
            text.Append(CultureInfo.InvariantCulture, $"\n  passes now: {Path.GetFileName(input)} (keep it as a regression test, or delete it)");
        }

        var summary = text.Append('\n').ToString();
        File.WriteAllText(layout.Summary, summary);
        Console.WriteLine();
        Console.Write(summary);
        Console.WriteLine($"kept under {layout.Directory}");
        return failing.Count > 0 ? 1 : 0;
    }

    /// <summary>
    ///     Runs each input once, in as few processes as it can: a process stops
    ///     at the first that fails, which is the last it said it was running,
    ///     and the next starts after it. Each input with what failing it said, or null.
    /// </summary>
    public static IReadOnlyList<(string Input, string? Report)> Replay(string program, int target, IReadOnlyList<string> inputs) {
        var outcomes = new List<(string, string?)>();
        var at = 0;
        while (at < inputs.Count) {
            var batch = inputs.Skip(at).Take(256).ToList();
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var input in batch) {
                start.ArgumentList.Add(input);
            }

            foreach (var (name, value) in Environment(target)) {
                start.Environment[name] = value;
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {program}");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var output = stdout.Result + stderr;
            if (process.ExitCode == 0) {
                outcomes.AddRange(batch.Select(static i => (i, (string?)null)));
                at += batch.Count;
                continue;
            }

            // The last input it began is the one that failed; those before it passed.
            var lines = output.Split('\n');
            var last = Array.FindLastIndex(lines, static l => l.StartsWith(FuzzBuild.RunningPrefix, StringComparison.Ordinal));
            var failed = last < 0 ? -1 : batch.IndexOf(lines[last][FuzzBuild.RunningPrefix.Length..].TrimEnd('\r'));
            if (failed < 0) {
                // It failed before it ran any: every input is said to fail with it.
                outcomes.AddRange(batch.Select(i => (i, (string?)output)));
                at += batch.Count;
                continue;
            }

            outcomes.AddRange(batch.Take(failed).Select(static i => (i, (string?)null)));
            outcomes.Add((batch[failed], string.Join('\n', lines.Skip(last + 1)).Trim()));
            at += failed + 1;
        }

        return outcomes;
    }

    /// <summary>
    ///     What sets a failure apart from others: a trap's message and where it
    ///     was, or the sanitizer's summary, or libFuzzer's word for it (a hang,
    ///     running out of memory); else the report's first line.
    /// </summary>
    public static string Signature(string report) {
        var lines = report.Split('\n').Select(static l => l.TrimEnd('\r')).ToList();
        var fatal = lines.FindIndex(static l => l.StartsWith("fatal error: ", StringComparison.Ordinal) || l.StartsWith("panic: ", StringComparison.Ordinal));
        if (fatal >= 0) {
            var where = fatal + 1 < lines.Count && lines[fatal + 1].TrimStart().StartsWith("at ", StringComparison.Ordinal) ? " " + lines[fatal + 1].Trim() : "";
            return lines[fatal] + where;
        }

        foreach (var marker in new[] { "SUMMARY: ", "runtime error: ", "ERROR: libFuzzer: " }) {
            if (lines.FirstOrDefault(l => l.Contains(marker, StringComparison.Ordinal)) is { } line) {
                return line[line.IndexOf(marker, StringComparison.Ordinal)..].Trim();
            }
        }

        return lines.FirstOrDefault(static l => l.Trim().Length > 0)?.Trim() ?? "failed with nothing to say";
    }

    /// <summary>Runs the fuzz build, its output to this console (or to nowhere), and gives its exit code.</summary>
    static int RunEngine(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, bool quiet = false) {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = quiet, RedirectStandardError = quiet };
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment) {
            start.Environment[name] = value;
        }

        NativeBuild.LogCommand(program, arguments);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {program}");
        if (quiet) {
            var stdout = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEnd();
            stdout.Wait();
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
