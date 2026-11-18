using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sable.Driver;

/// <summary>
///     How <c>sable test</c> runs a native test harness: each test in a
///     process of its own, as the harness would, but several at once and
///     each with a time limit — which the harness, one Sable program waiting
///     on one child at a time, cannot do.
/// </summary>
/// <remarks>
///     The harness says what its tests are with <c>--describe</c> (name,
///     place, and the trap a <c>@test(traps)</c> must end in), and runs one
///     with <c>--run-test</c>. What this prints is what the harness's own
///     runner prints, so the problem matcher and the editor read either; a
///     test's line is printed as it finishes, so with several at once they
///     come in the order they finish.
/// </remarks>
internal static class TestRunner {
    /// <summary>One test, as <c>--describe</c> gives it.</summary>
    internal sealed record TestEntry(string Name, string Location, string? Traps);

    /// <summary>How one test's process ended.</summary>
    sealed record Outcome(TestEntry Test, int Status, string Output, string Errors, TimeSpan Took, bool TimedOut);

    /// <summary>The marker a failed assertion's report starts with (Sable.Testing's <c>failureMarker</c>).</summary>
    const string FailureMarker = "assertion failed: ";

    /// <summary>After this long, a test with no time limit is said to be running long, once.</summary>
    static readonly TimeSpan SlowTest = TimeSpan.FromSeconds(60);

    /// <summary>Whether the arguments ask for a run this runner does: not a listing, nor a run in one process.</summary>
    public static bool Handles(IReadOnlyList<string> harnessArguments) =>
        !harnessArguments.Any(static a => a is "--list" or "--describe" or "--in-process" or "--run-test");

    /// <summary>Runs the tests the arguments select; returns the exit code, as the harness's runner would.</summary>
    public static int Run(string executable, IReadOnlyList<string> harnessArguments, int jobs, TimeSpan? timeout, TextWriter output, TextWriter error) {
        var exact = harnessArguments.Contains("--exact");
        var showOutput = harnessArguments.Contains("--show-output");
        var filters = harnessArguments.Where(static a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        // Every test, then those the filters select, as the harness selects them.
        var described = Capture(executable, ["--describe"], null);
        if (described.Status != 0) {
            error.Write(described.Errors);
            return described.Status < 0 ? 1 : described.Status;
        }

        var all = Parse(described.Output);
        var chosen = all.Where(t => Selected(t.Name, filters, exact)).ToList();
        output.WriteLine($"running {chosen.Count} test{Plural(chosen.Count)}");

        var started = Stopwatch.GetTimestamp();
        var outcomes = new Outcome?[chosen.Count];
        var gate = new object();
        Parallel.For(0, chosen.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, i => {
            var test = chosen[i];
            var outcome = RunOne(executable, test, timeout, () => {
                lock (gate) {
                    output.WriteLine($"test {test.Name} has been running for over {SlowTest.TotalSeconds:0} s");
                }
            });
            outcomes[i] = outcome;
            lock (gate) {
                var failed = Verdict(outcome) is not null;
                output.WriteLine($"test {test.Name} ... {(failed ? "FAILED" : "ok")} ({FormatDuration(outcome.Took)})");
                if (!failed && showOutput && outcome.Output.Length > 0) {
                    output.WriteLine(Indented(outcome.Output));
                }
            }
        });

        var failures = outcomes.Select(static o => o!).Where(static o => Verdict(o) is not null).ToList();
        if (failures.Count > 0) {
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

        output.WriteLine();
        var passed = chosen.Count - failures.Count;
        output.WriteLine(
            $"test result: {(failures.Count == 0 ? "ok" : "FAILED")}. {passed} passed; {failures.Count} failed; {all.Count - chosen.Count} filtered out; finished in {FormatDuration(Stopwatch.GetElapsedTime(started))}");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The tests <c>--describe</c> printed: name, place and trap, a line each, tab-separated.</summary>
    internal static List<TestEntry> Parse(string described) {
        var tests = new List<TestEntry>();
        foreach (var line in described.Split('\n')) {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length == 3) {
                tests.Add(new(fields[0], fields[1], fields[2] == "-" ? null : fields[2]));
            }
        }

        return tests;
    }

    /// <summary>Selected by any filter, as part of the name or, with <paramref name="exact" />, all of it; every test is, with none.</summary>
    static bool Selected(string name, IReadOnlyList<string> filters, bool exact) =>
        filters.Count == 0 || filters.Any(f => exact ? name == f : name.Contains(f, StringComparison.Ordinal));

    static Outcome RunOne(string executable, TestEntry test, TimeSpan? timeout, Action runningLong) {
        var clock = Stopwatch.StartNew();
        var result = Capture(executable, ["--run-test", test.Name], timeout, runningLong);
        return new(test, result.Status, result.Output, result.Errors, clock.Elapsed, result.TimedOut);
    }

    /// <summary>Runs the harness to its end (or its time limit), keeping what it wrote; status -1 when it could not be started.</summary>
    static (int Status, string Output, string Errors, bool TimedOut) Capture(string executable, IReadOnlyList<string> arguments, TimeSpan? timeout, Action? runningLong = null) {
        var start = new ProcessStartInfo(executable) {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try {
            process = Process.Start(start) ?? throw new InvalidOperationException();
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
            return (-1, "", e.Message, false);
        }

        using (process) {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            var timedOut = false;
            if (timeout is { } limit) {
                if (!process.WaitForExit(limit)) {
                    timedOut = true;
                    process.Kill(entireProcessTree: true);
                }
            } else if (!process.WaitForExit(SlowTest)) {
                runningLong?.Invoke();
            }

            process.WaitForExit();
            return (process.ExitCode, output.Result, errors.Result, timedOut);
        }
    }

    /// <summary>
    ///     Null when the test's process ended as it should have; otherwise
    ///     why not ("" when what it wrote says it all). A test that must trap
    ///     passes by trapping — not by failing an assertion, which is the
    ///     test failing — with the message it names. (Sable.Testing's <c>verdict</c>.)
    /// </summary>
    static string? Verdict(Outcome outcome) {
        if (outcome.TimedOut) {
            return $"the test did not finish within {outcome.Took.TotalSeconds:0.#} s, and was stopped";
        }

        if (outcome.Test.Traps is not { } expected) {
            return outcome.Status == 0 ? null : "";
        }

        if (outcome.Status == 0) {
            return $"the test was to trap{(expected.Length == 0 ? "" : $" with '{expected}'")}, and it returned";
        }

        if (outcome.Status < 0 || outcome.Errors.Contains(FailureMarker, StringComparison.Ordinal)) {
            return "";
        }

        return expected.Length > 0 && !outcome.Errors.Contains(expected, StringComparison.Ordinal) ? $"the test trapped, but not with '{expected}'" : null;
    }

    /// <summary>What a failed test's process said, and how it ended. (Sable.Testing's <c>failureReport</c>.)</summary>
    static string Report(Outcome outcome) {
        var report = new StringBuilder($"---- {outcome.Test.Name} ({outcome.Test.Location}) ----");
        if (outcome.Status < 0) {
            return report.Append("\nthe test's process could not be started").ToString();
        }

        if (outcome.Output.Length > 0) {
            report.Append("\nstdout:\n").Append(Indented(outcome.Output));
        }

        // An assertion's own trap says nothing its report has not.
        var assertion = outcome.Errors.Contains(FailureMarker, StringComparison.Ordinal);
        var shown = assertion ? outcome.Errors.Replace("fatal error: 'try!' caught an error\n", "", StringComparison.Ordinal) : outcome.Errors;
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
