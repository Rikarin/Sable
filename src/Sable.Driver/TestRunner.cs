using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sable.Driver;

/// <summary>
///     How <c>sable test</c> runs a native test harness: the tests in
///     processes of their own, as the harness would, but several at once and
///     each within a time limit — which the harness, one Sable program waiting
///     on one child at a time, cannot do.
/// </summary>
/// <remarks>
///     The harness says what the tests its filters select are with
///     <c>--describe</c>, a JSON object a line, and runs those named on its
///     standard input with <c>--run-tests</c>, one after another, saying of
///     each on standard error when it begins and how it ended. Most are given
///     a process each; those that share a fixture, or are cases of one
///     suite's functions, are given one together. A test that traps ends its process,
///     and those after it are given another. What this prints is what the
///     harness's own runner prints; a test's line is printed as it finishes,
///     so with several at once they come in the order they finish.
/// </remarks>
internal static partial class TestRunner {
    /// <summary>One test, as <c>--describe</c> gives it.</summary>
    /// <param name="Limit">The time limit it was declared with, if it was: shorter or longer than the run's.</param>
    internal sealed record TestEntry(string Name, string Location, string? Traps, TimeSpan? Limit = null, IReadOnlyList<string>? Tags = null) {
        /// <summary>The type it is a test of, or its module.</summary>
        public string Suite { get; init; } = "";

        /// <summary>Why it is not to be run, when it is not.</summary>
        public string? Skip { get; init; }

        /// <summary>The tests it is run in one process with; null for one that has a process to itself.</summary>
        public string? Group { get; init; }

        /// <summary>The tests it is not run at the same time as; null for one run whenever there is room.</summary>
        public string? Serial { get; init; }

        /// <summary>How many more times it is tried while it fails.</summary>
        public int Retries { get; init; }
    }

    /// <summary>How one test ended.</summary>
    /// <param name="Started">
    ///     Whether its process started at all. Not told by the status: on
    ///     Windows one that was stopped, or crashed, ends with a negative one.
    /// </param>
    /// <param name="Limit">The time limit it was stopped at, when it was.</param>
    /// <param name="Stuck">It would not stop, or left its output open after it ended, and was given up on.</param>
    internal sealed record Outcome(TestEntry Test, bool Started, int Status, string Output, string Errors, TimeSpan Took, TimeSpan? Limit, bool Stuck) {
        public bool TimedOut => Limit is not null;

        /// <summary>It said that it failed, in a process that went on.</summary>
        public bool SaidFailed { get; init; }

        /// <summary>Why it was skipped, when it was.</summary>
        public string? Skipped { get; init; }

        /// <summary>How many times it was tried.</summary>
        public int Attempts { get; init; } = 1;

        /// <summary>The files it kept (<c>TestContext.attach</c>).</summary>
        public IReadOnlyList<string> Attachments { get; init; } = [];
    }

    /// <summary>What a run is asked, besides which tests.</summary>
    internal sealed record Settings(int Jobs, TimeSpan? Timeout) {
        /// <summary>Variables set for each test process, and only for it.</summary>
        public IReadOnlyDictionary<string, string>? Environment { get; init; }

        /// <summary>Files to write the results to besides.</summary>
        public IReadOnlyList<TestReport> Reports { get; init; } = [];

        /// <summary>Whether to print the run as JSON, a line as each thing happens (<see cref="TestEvents" />), in place of text.</summary>
        public bool Events { get; init; }
    }

    /// <summary>The markers a failed assertion's and a failed expectation's reports start with (Sable.Testing's <c>failureMarker</c> and <c>expectationMarker</c>).</summary>
    static readonly string[] FailureMarkers = ["assertion failed: ", "expectation failed: "];

    /// <summary>
    ///     Whether a test's errors hold Sable.Testing's report of a failed
    ///     assertion: a line that starts with a marker. Sable.Core's
    ///     debug <c>assert</c> traps with the same words after
    ///     <c>fatal error: </c>, which is a trap like any other.
    /// </summary>
    static bool IsTestAssertion(string errors) =>
        FailureMarkers.Any(m => errors.StartsWith(m, StringComparison.Ordinal) || errors.Contains("\n" + m, StringComparison.Ordinal));

    /// <summary>After this long, a test with no time limit is said to be running long, once.</summary>
    static readonly TimeSpan SlowTest = TimeSpan.FromSeconds(60);

    /// <summary>Whether the arguments ask for a run this runner does: not a listing, nor a run in one process.</summary>
    public static bool Handles(IReadOnlyList<string> harnessArguments) =>
        !harnessArguments.Any(static a => a is "--list" or "--describe" or "--in-process" or "--run-test" or "--run-tests");

    /// <summary>Runs the tests the arguments select; returns the exit code, as the harness's runner would.</summary>
    /// <param name="environment">Variables set for each test process (and only for it): a coverage run's counts file.</param>
    /// <param name="reports">Files to write the results to besides (<see cref="TestReport" />).</param>
    public static int Run(string executable, IReadOnlyList<string> harnessArguments, int jobs, TimeSpan? timeout, TextWriter output, TextWriter error,
        IReadOnlyDictionary<string, string>? environment = null, IReadOnlyList<TestReport>? reports = null) =>
        Run(executable, harnessArguments, new Settings(jobs, timeout) { Environment = environment, Reports = reports ?? [] }, output, error);

    public static int Run(string executable, IReadOnlyList<string> harnessArguments, Settings settings, TextWriter output, TextWriter error) {
        var showOutput = harnessArguments.Contains("--show-output");
        var begun = DateTimeOffset.Now;

        // The tests the filters select, as the harness selects them.
        var described = Capture(executable, ["--describe", ..harnessArguments], null, settings.Environment);
        if (!described.Started || described.Status != 0) {
            error.Write(described.Errors);
            return described.Started && described.Status > 0 ? described.Status : 1;
        }

        var (chosen, filteredOut) = Parse(described.Output);

        // The seed the run's property tests make their cases from, if one was given.
        var seed = harnessArguments.SkipWhile(static a => a != "--seed").Take(2).ToList();
        var events = settings.Events ? new TestEvents(output) : null;
        if (events is null) {
            output.WriteLine($"running {chosen.Count} test{Plural(chosen.Count)}");
        } else {
            events.Begin(chosen);
        }

        var started = Stopwatch.GetTimestamp();
        var outcomes = new Dictionary<TestEntry, Outcome>(ReferenceEqualityComparer.Instance);
        var gate = new object();

        void Note(Outcome outcome) {
            lock (gate) {
                outcomes[outcome.Test] = outcome;
                if (events is not null) {
                    events.Finished(outcome);
                    return;
                }

                if (outcome.Skipped is { } reason) {
                    output.WriteLine($"test {outcome.Test.Name} ... skipped: {reason}");
                    return;
                }

                var failed = Verdict(outcome) is not null;
                output.WriteLine($"test {outcome.Test.Name} ... {(failed ? "FAILED" : "ok")} ({FormatDuration(outcome.Took)}){(outcome.Attempts > 1 && !failed ? $", on try {outcome.Attempts}" : "")}");
                if (!failed && showOutput && outcome.Output.Length > 0) {
                    output.WriteLine(Indented(outcome.Output));
                }
            }
        }

        foreach (var test in chosen.Where(static t => t.Skip is not null)) {
            Note(new(test, true, 0, "", "", TimeSpan.Zero, null, false) { Skipped = test.Skip });
        }

        var run = new BatchRun(
            executable,
            seed,
            settings,
            test => {
                lock (gate) {
                    events?.Started(test);
                }
            },
            test => {
                lock (gate) {
                    if (events is null) {
                        output.WriteLine($"test {test.Name} has been running for over {SlowTest.TotalSeconds:0} s");
                    }
                }
            },
            Note
        );
        Schedule(Batches([..chosen.Where(static t => t.Skip is null)], settings.Jobs), settings.Jobs, run.Run);

        var ended = chosen.Select(t => outcomes[t]).ToList();
        var failures = ended.Where(static o => Verdict(o) is not null).ToList();
        if (failures.Count > 0 && events is null) {
            output.WriteLine();
            output.WriteLine("failures:");
            foreach (var failure in failures) {
                output.WriteLine();
                output.WriteLine(Report(failure));
            }

            output.WriteLine();
            output.WriteLine("failed:");
            foreach (var failure in failures) {
                output.WriteLine($"    {failure.Test.Name}");
            }
        }

        var took = Stopwatch.GetElapsedTime(started);
        foreach (var report in settings.Reports) {
            report.Write(ended, begun, took);
        }

        var skipped = ended.Count(static o => o.Skipped is not null);
        var passed = chosen.Count - failures.Count - skipped;
        if (events is null) {
            output.WriteLine();
            output.WriteLine(
                $"test result: {(failures.Count == 0 ? "ok" : "FAILED")}. {passed} passed; {failures.Count} failed; {(skipped == 0 ? "" : $"{skipped} skipped; ")}{filteredOut} filtered out; finished in {FormatDuration(took)}");
        } else {
            events.End(passed, failures.Count, skipped, filteredOut, took);
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    ///     The tests <c>--describe</c> printed, a JSON object a line, and how
    ///     many the filters left out.
    /// </summary>
    internal static (List<TestEntry> Tests, int FilteredOut) Parse(string described) {
        var tests = new List<TestEntry>();
        var filteredOut = 0;
        foreach (var line in described.Split('\n')) {
            if (!line.StartsWith('{')) {
                continue;
            }

            using var parsed = JsonDocument.Parse(line);
            var test = parsed.RootElement;
            if (test.TryGetProperty("filteredOut", out var left)) {
                filteredOut = left.GetInt32();
                continue;
            }

            string? Text(string name) => test.TryGetProperty(name, out var value) ? value.GetString() : null;
            tests.Add(
                new(
                    Text("name")!,
                    string.Create(CultureInfo.InvariantCulture, $"{Text("file")}:{test.GetProperty("line").GetInt32()}"),
                    Text("traps"),
                    test.TryGetProperty("timeout", out var limit) ? TimeSpan.FromTicks(limit.GetInt64() / 100) : null,
                    [..test.GetProperty("tags").EnumerateArray().Select(static t => t.GetString()!)]
                ) {
                    Suite = Text("suite") ?? "",
                    Skip = Text("skip"),
                    Group = Text("group"),
                    Serial = Text("serial"),
                    Retries = test.TryGetProperty("retries", out var retries) ? retries.GetInt32() : 0,
                }
            );
        }

        return (tests, filteredOut);
    }

    /// <summary>
    ///     The tests of each process (Sable.Testing's <c>batches</c>): those of
    ///     a group together, its suites apart; a suite's cases so many to a
    ///     process, at least a few, so that slow ones run at once.
    /// </summary>
    internal static List<List<TestEntry>> Batches(IReadOnlyList<TestEntry> tests, int jobs) {
        var batches = new List<List<TestEntry>>();
        var groups = new Dictionary<string, List<TestEntry>>(StringComparer.Ordinal);
        var sizes = tests.Where(static t => t.Group is not null).GroupBy(static t => t.Group!).ToDictionary(static g => g.Key, g => Math.Clamp((g.Count() + jobs - 1) / Math.Max(1, jobs), 8, 64));
        foreach (var test in tests) {
            if (test.Group is null) {
                batches.Add([test]);
            } else if (groups.TryGetValue(test.Group, out var batch) && (!test.Group.StartsWith("cases ", StringComparison.Ordinal) || batch.Count < sizes[test.Group])) {
                batch.Add(test);
            } else {
                batches.Add(groups[test.Group] = [test]);
            }
        }

        return [..batches.Select(static b => b.OrderBy(static t => t.Suite, StringComparer.Ordinal).ToList())];
    }

    /// <summary>Runs the batches, so many at once, in their order but for those whose tests are not run at once with one running.</summary>
    static void Schedule(List<List<TestEntry>> batches, int jobs, Action<IReadOnlyList<TestEntry>> run) {
        var waiting = new LinkedList<(List<TestEntry> Batch, HashSet<string> Serial)>(
            batches.Select(static b => (b, b.Select(static t => t.Serial).OfType<string>().ToHashSet(StringComparer.Ordinal)))
        );
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var gate = new object();

        void Work() {
            while (true) {
                (List<TestEntry> Batch, HashSet<string> Serial) next;
                lock (gate) {
                    LinkedListNode<(List<TestEntry> Batch, HashSet<string> Serial)>? free;
                    while (true) {
                        for (free = waiting.First; free is not null && free.Value.Serial.Overlaps(busy); free = free.Next) {
                        }

                        if (free is not null || waiting.Count == 0) {
                            break;
                        }

                        Monitor.Wait(gate);
                    }

                    if (free is null) {
                        return;
                    }

                    next = free.Value;
                    waiting.Remove(free);
                    busy.UnionWith(next.Serial);
                }

                try {
                    run(next.Batch);
                } finally {
                    lock (gate) {
                        busy.ExceptWith(next.Serial);
                        Monitor.PulseAll(gate);
                    }
                }
            }
        }

        // A thread each, not the pool's: each waits on a process all along.
        var workers = Enumerable.Range(0, Math.Min(Math.Max(1, jobs), Math.Max(1, batches.Count))).Select(_ => Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(workers);
    }

    /// <summary>What a process that runs tests says of them (Sable.Testing's <c>begunMark</c> and the rest).</summary>
    internal const string BegunMark = "\n\u0001test begun ", EndedMark = "\n\u0001test ended ", TimedOutMark = "\n\u0001test timed out\n", AttachmentMark = "\n\u0001attachment ";

    /// <summary>How long a test past its time has to tear down, once it has been told, before its process is stopped.</summary>
    static readonly TimeSpan TestGrace = TimeSpan.FromSeconds(2);

    /// <summary>How much longer its process has to tell it.</summary>
    static readonly TimeSpan TellGrace = TimeSpan.FromSeconds(3);

    /// <summary>Runs the tests of batches.</summary>
    sealed class BatchRun(string executable, IReadOnlyList<string> seed, Settings settings, Action<TestEntry> started, Action<TestEntry> runningLong, Action<Outcome> note) {
        public void Run(IReadOnlyList<TestEntry> batch) {
            var left = batch.ToList();
            while (left.Count > 0) {
                var ended = RunSome(left, 1, announce: true);
                left.RemoveRange(0, ended.Count);
                foreach (var first in ended) {
                    var outcome = first;
                    for (var attempt = 2; attempt <= first.Test.Retries + 1 && Verdict(outcome) is not null; attempt++) {
                        outcome = RunSome([first.Test], attempt, announce: false)[0] with { Attempts = attempt };
                    }

                    note(outcome);
                }
            }
        }

        /// <summary>A process for the tests: what came of as many as it ran, at least the first.</summary>
        List<Outcome> RunSome(IReadOnlyList<TestEntry> tests, int attempt, bool announce) {
            List<string> arguments = ["--run-tests", "--grace", "60000000000", ..seed];
            if (attempt > 1) {
                arguments.AddRange(["--attempt", attempt.ToString(CultureInfo.InvariantCulture)]);
            }

            if (settings.Timeout is { } all) {
                arguments.AddRange(["--timeout", all.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)]);
            }

            // Which test it is in, by what it has said: heard as it is said.
            var gate = new object();
            var heard = new StringBuilder();
            var begun = 0;
            var clock = Stopwatch.StartNew();
            var since = TimeSpan.Zero;
            TimeSpan? told = null;
            TimeSpan? stoppedAt = null;

            void Hear(string text) {
                lock (gate) {
                    heard.Append(text);
                    var all = heard.ToString();
                    var end = all.LastIndexOf('\n');
                    if (end < 0) {
                        return;
                    }

                    heard.Clear().Append(all[(end + 1)..]);
                    foreach (var line in all[..end].Split('\n')) {
                        if (line.StartsWith(BegunMark[1..], StringComparison.Ordinal)) {
                            since = clock.Elapsed;
                            told = null;
                            if (begun < tests.Count && announce) {
                                started(tests[begun]);
                            }

                            begun++;
                        } else if (line == TimedOutMark[1..^1]) {
                            told ??= clock.Elapsed;
                        }
                    }
                }
            }

            var noticed = -1;
            var captured = Capture(
                executable,
                arguments,
                settings.Environment,
                string.Concat(tests.Select(static t => t.Name + "\n")),
                Hear,
                process => {
                    while (!process.WaitForExit(Wait())) {
                        TimeSpan? over = null;
                        TestEntry? slow = null;
                        lock (gate) {
                            var current = Math.Clamp(begun - 1, 0, tests.Count - 1);
                            var limit = tests[current].Limit ?? settings.Timeout;
                            var now = clock.Elapsed;
                            if (limit is { } within && ((told is { } at && now >= at + TestGrace) || now >= since + within + TellGrace)) {
                                over = within;
                            } else if (limit is null && noticed != current && now - since >= SlowTest) {
                                noticed = current;
                                slow = tests[current];
                            }
                        }

                        if (over is not null) {
                            stoppedAt = over;
                            return Stop(process) ? Ending.Stopped : Ending.Stuck;
                        }

                        if (slow is not null) {
                            runningLong(slow);
                        }
                    }

                    return Ending.Ended;

                    TimeSpan Wait() {
                        lock (gate) {
                            var current = Math.Clamp(begun - 1, 0, tests.Count - 1);
                            var due = told is { } at ? at + TestGrace
                                : (tests[current].Limit ?? settings.Timeout) is { } within ? since + within + TellGrace
                                : since + SlowTest;
                            var left = due - clock.Elapsed;
                            return left < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : left > TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : left;
                        }
                    }
                }
            );
            return Read(tests, captured, stoppedAt, settings.Timeout, clock.Elapsed);
        }
    }

    /// <summary>What a process said of the tests it was started for, as far as it got: at least the first (Sable.Testing's <c>TestReport.read</c>).</summary>
    static List<Outcome> Read(IReadOnlyList<TestEntry> tests, Captured captured, TimeSpan? stoppedAt, TimeSpan? timeout, TimeSpan took) {
        var outputs = Sections(captured.Output);
        var said = Sections(captured.Errors);
        var outcomes = new List<Outcome>();
        for (var i = 0; i < tests.Count && (i == 0 || i < said.Count); i++) {
            var isLast = i + 1 >= said.Count || i + 1 == tests.Count;
            var time = isLast && said.Count <= 1 ? took : TimeSpan.Zero;
            string? ended = null, skipped = null;
            var attachments = new List<string>();
            var errors = i < said.Count ? said[i] : captured.Errors;

            // A mark, with what is on its line after it, taken out of the errors.
            string? Take(string mark) {
                var at = errors.IndexOf(mark, StringComparison.Ordinal);
                if (at < 0) {
                    return null;
                }

                var start = at + mark.Length;
                var end = mark.EndsWith('\n') ? start : errors.IndexOf('\n', start) is var close and >= 0 ? close + 1 : errors.Length;
                var rest = errors[start..end].TrimEnd('\n', '\r');
                errors = errors[..at] + errors[end..];
                return rest;
            }

            if (Take(EndedMark) is { } how) {
                var parts = how.Split(' ', 3);
                ended = parts[0];
                if (parts.Length > 1 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var nanoseconds)) {
                    time = TimeSpan.FromTicks(nanoseconds / 100);
                }

                skipped = ended == "skipped" ? parts.ElementAtOrDefault(2) ?? "" : null;
            }

            var timedOut = false;
            while (Take(TimedOutMark) is not null) {
                timedOut = true;
            }

            while (Take(AttachmentMark) is { } attachment) {
                attachments.Add(attachment);
            }

            var limit = timedOut ? tests[i].Limit ?? timeout ?? stoppedAt : isLast ? stoppedAt : null;
            outcomes.Add(
                new(tests[i], captured.Started, isLast ? captured.Status : 0, i < outputs.Count ? outputs[i] : "", errors, time, limit, isLast && captured.Stuck) {
                    SaidFailed = ended == "failed",
                    // What ended its process after it said so is a failure.
                    Skipped = isLast && captured.Status != 0 ? null : skipped,
                    Attachments = attachments,
                }
            );
        }

        return outcomes;
    }

    /// <summary>What was written in each test, in order: what came before the first is the first's.</summary>
    static List<string> Sections(string text) {
        var parts = text.Split(BegunMark);
        var sections = new List<string>();
        for (var i = 1; i < parts.Length; i++) {
            var end = parts[i].IndexOf('\n', StringComparison.Ordinal);
            var own = end < 0 ? "" : parts[i][(end + 1)..];
            sections.Add(i == 1 ? parts[0] + own : own);
        }

        return sections;
    }

    /// <summary>The JSON a line of the tests there are (<c>sable test --list --json</c>), or as the harness lists them.</summary>
    public static int List(string executable, IReadOnlyList<string> harnessArguments, TextWriter output, TextWriter error) {
        var described = Capture(executable, ["--describe", ..harnessArguments.Where(static a => a != "--list")], null, null);
        error.Write(described.Errors);
        output.Write(described.Output);
        return described.Started ? described.Status : 1;
    }

    /// <summary>How long a process told to stop is given to be gone, each time; and its output to end, once it is.</summary>
    static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    /// <summary>How a harness's process ended, and what it wrote.</summary>
    /// <param name="TimedOut">It ran past its time limit, and was told to stop.</param>
    /// <param name="Stuck">It would not stop, or left its output open after it ended: it was given up on.</param>
    internal sealed record Captured(bool Started, int Status, string Output, string Errors, bool TimedOut, bool Stuck = false);

    /// <summary>
    ///     Runs the harness to its end (or its time limit), keeping what it
    ///     wrote; not started, with why in its errors, when it could not be.
    ///     Past the limit nothing here waits without a bound: a process that
    ///     cannot be stopped is given up on, and said to be, rather than
    ///     holding up the whole run.
    /// </summary>
    internal static Captured Capture(string executable, IReadOnlyList<string> arguments, TimeSpan? timeout,
        IReadOnlyDictionary<string, string>? environment, Action? runningLong = null) =>
        Capture(
            executable,
            arguments,
            environment,
            null,
            null,
            process => {
                if (timeout is { } limit) {
                    return process.WaitForExit(limit) ? Ending.Ended : Stop(process) ? Ending.Stopped : Ending.Stuck;
                }

                if (!process.WaitForExit(SlowTest)) {
                    runningLong?.Invoke();
                    // No limit was asked for: the test takes as long as it takes.
                    process.WaitForExit();
                }

                return Ending.Ended;
            }
        );

    /// <summary>How waiting for a process ended: by itself; stopped; told to stop, and still there.</summary>
    internal enum Ending {
        Ended,
        Stopped,
        Stuck,
    }

    /// <param name="input">What it reads; nothing, when null.</param>
    /// <param name="errorsHeard">Given what it writes to standard error, as it does.</param>
    /// <param name="wait">Waits for it to end, or stops it.</param>
    static Captured Capture(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, string? input,
        Action<string>? errorsHeard, Func<Process, Ending> wait) {
        var start = new ProcessStartInfo(executable) {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The harness writes UTF-8 whatever the console's code page,
            // which Windows would otherwise read it in.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) {
            start.Environment[name] = value;
        }

        Process process;
        try {
            process = Process.Start(start) ?? throw new InvalidOperationException();
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
            return new(false, -1, "", e.Message, false);
        }

        using (process) {
            var output = new Collected(process.StandardOutput);
            var errors = new Collected(process.StandardError, errorsHeard);
            try {
                process.StandardInput.Write(input ?? "");
                process.StandardInput.Close();
            } catch (IOException) {
                // It ended without reading what it was given: its status says how.
            }

            var ending = wait(process);
            if (ending != Ending.Ended) {
                // What a stopped test wrote is kept, to show; one that
                // would not stop is not waited for any longer. Its
                // output stays open if something it started lives on.
                var gone = Task.WaitAll([output.Done, errors.Done], ending == Ending.Stopped ? StopGrace : TimeSpan.Zero);
                return new(true, ending == Ending.Stopped ? process.ExitCode : -1, output.Text, errors.Text, true, !gone);
            }

            // Ended; but a process it started may hold its output open still.
            var closed = Task.WaitAll([output.Done, errors.Done], StopGrace);
            return new(true, process.ExitCode, output.Text, errors.Text, false, !closed);
        }
    }

    /// <summary>
    ///     Stops a process and what it started, and says whether it is gone.
    ///     Stopping the whole tree looks through every process there is, and
    ///     can fail on one that comes or goes meanwhile; then the process is
    ///     killed alone. Each kill is given a while to take, and tried again,
    ///     a bounded number of times.
    /// </summary>
    internal static bool Stop(Process process) {
        for (var attempt = 0; attempt < 3; attempt++) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (Exception e) when (e is AggregateException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
                try {
                    process.Kill();
                } catch (Exception again) when (again is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
                    // Gone already, or out of reach: the wait says which.
                }
            }

            if (process.WaitForExit(StopGrace)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     A stream read to its end in the background, whose text so far can
    ///     be had at any time: the output of a process given up on is shown
    ///     as far as it got, not waited for.
    /// </summary>
    /// <remarks>
    ///     Read on a thread of its own, not the thread pool's. On Windows a
    ///     process's redirected output is a synchronous pipe, whose
    ///     asynchronous reads each hold a pool thread; with tests at once, and
    ///     whatever else the process runs, the pool was starved long enough
    ///     that the end of a stopped or trapped test's output was read later
    ///     than <see cref="StopGrace" />, and the test was failed as one that
    ///     left its output open.
    /// </remarks>
    sealed class Collected {
        readonly StringBuilder _text = new();
        readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Collected(StreamReader reader, Action<string>? heard = null) =>
            new Thread(() => {
                var buffer = new char[4096];
                try {
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) {
                        lock (_text) {
                            _text.Append(buffer, 0, read);
                        }

                        heard?.Invoke(new string(buffer, 0, read));
                    }
                } catch (Exception e) when (e is IOException or ObjectDisposedException) {
                    // Closed under it, once given up on: what came is kept.
                } finally {
                    _done.SetResult();
                }
            }) { IsBackground = true, Name = "sable test output" }.Start();

        public Task Done => _done.Task;

        public string Text {
            get {
                lock (_text) {
                    return _text.ToString();
                }
            }
        }
    }

    /// <summary>
    ///     Null when the test's process ended as it should have; otherwise
    ///     why not ("" when what it wrote says it all). A test that must trap
    ///     passes by trapping — not by failing an assertion, which is the
    ///     test failing — with the message it names. (Sable.Testing's <c>verdict</c>.)
    /// </summary>
    internal static string? Verdict(Outcome outcome) {
        if (outcome.Skipped is not null) {
            return null;
        }

        if (outcome.TimedOut) {
            // The limit, not the time it took: stopping a process tree can
            // take a while, on Windows most of all.
            return $"the test did not finish within {outcome.Limit!.Value.TotalSeconds:0.#} s, and {(outcome.Stuck ? "could not be stopped" : "was stopped")}";
        }

        if (outcome.Stuck) {
            return "the test ended, but a process it started still holds its output open";
        }

        if (outcome.SaidFailed) {
            return "";
        }

        if (outcome.Test.Traps is not { } expected) {
            return outcome.Status == 0 ? null : "";
        }

        if (outcome.Status == 0) {
            return $"the test was to trap{(expected.Length == 0 ? "" : $" with '{expected}'")}, and it returned";
        }

        if (!outcome.Started || IsTestAssertion(outcome.Errors)) {
            return "";
        }

        return expected.Length > 0 && !outcome.Errors.Contains(expected, StringComparison.Ordinal) ? $"the test trapped, but not with '{expected}'" : null;
    }

    /// <summary>What a failed test's process said, and how it ended. (Sable.Testing's <c>failureReport</c>.)</summary>
    static string Report(Outcome outcome) => $"---- {outcome.Test.Name} ({outcome.Test.Location}) ----{Details(outcome)}";

    internal static string Details(Outcome outcome) {
        var report = new StringBuilder();
        if (!outcome.Started) {
            return report.Append("\nthe test's process could not be started").ToString();
        }

        if (outcome.Output.Length > 0) {
            report.Append("\nstdout:\n").Append(Indented(outcome.Output));
        }

        // An assertion's own trap says nothing its report has not.
        var assertion = IsTestAssertion(outcome.Errors);
        var shown = Said(outcome);
        if (shown.Length > 0) {
            report.Append('\n').Append(Indented(shown));
        }

        if (Verdict(outcome) is { Length: > 0 } why) {
            report.Append("\n    ").Append(why);
        }

        if (!assertion && !outcome.TimedOut) {
            report.Append(CultureInfo.InvariantCulture, $"\n    (exit status {outcome.Status})");
        }

        return report.ToString();
    }

    /// <summary>What the test wrote to standard error, without the trap a failed assertion ends with.</summary>
    internal static string Said(Outcome outcome) =>
        IsTestAssertion(outcome.Errors) ? AssertionTrap().Replace(outcome.Errors, "") : outcome.Errors;

    /// <summary>The trap a failed assertion ends with, and the line that says where: inside Sable.Testing.</summary>
    [GeneratedRegex(@"^fatal error: 'try!' caught an error\r?\n(?:  at .*\r?\n)?", RegexOptions.Multiline)]
    private static partial Regex AssertionTrap();

    static string Indented(string text) {
        var lines = text.Split('\n');
        // The harness's 'lines' drops the empty one after a final line break.
        if (lines.Length > 1 && lines[^1].Length == 0) {
            lines = lines[..^1];
        }

        return string.Join("\n", lines.Select(static l => "    " + l));
    }

    static string Plural(int count) => count == 1 ? "" : "s";

    /// <summary>A time in the unit that reads best, as the harness prints one: '850 ns', '12.40 µs', '3.05 ms', '1.20 s'.</summary>
    internal static string FormatDuration(TimeSpan time) {
        var ns = time.Ticks * 100.0;
        return ns switch {
            < 1000 => ns.ToString(ns < 10 ? "F2" : ns < 100 ? "F1" : "F0", CultureInfo.InvariantCulture) + " ns",
            < 1_000_000 => (ns / 1000).ToString("F2", CultureInfo.InvariantCulture) + " µs",
            < 1_000_000_000 => (ns / 1_000_000).ToString("F2", CultureInfo.InvariantCulture) + " ms",
            _ => (ns / 1_000_000_000).ToString("F2", CultureInfo.InvariantCulture) + " s",
        };
    }
}
