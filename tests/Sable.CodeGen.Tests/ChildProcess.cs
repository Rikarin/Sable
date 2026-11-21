using System.Diagnostics;
using Sable.Driver;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A process a test starts — a program it built, or the <c>sable</c>
///     command — run to its end within a time limit. One that hangs is
///     stopped, with what it started, and fails the test that started it,
///     saying what it was and what it wrote: rather than holding the test
///     host until CI's hang timeout ends the whole job with no name.
/// </summary>
internal static class ChildProcess {
    /// <summary>The longest a built program is given: every one a test builds ends in seconds.</summary>
    public static readonly TimeSpan ProgramLimit = TimeSpan.FromMinutes(2);

    /// <summary>The longest the <c>sable</c> command is given: it may build a package and its dependencies.</summary>
    public static readonly TimeSpan CommandLimit = TimeSpan.FromMinutes(10);

    public sealed record Result(string Stdout, string Stderr, int ExitCode);

    /// <summary>Runs it with the input written to it (when its input is redirected) and its output and errors read as they come.</summary>
    public static Result Run(ProcessStartInfo start, TimeSpan limit, string input = "") {
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (start.RedirectStandardInput) {
            try {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            } catch (IOException) {
                // It ended, or closed its input, without reading it all.
            }
        }

        if (!process.WaitForExit(limit)) {
            var stopped = TestRunner.Stop(process);
            Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(10));
            Assert.Fail(
                $"{Path.GetFileName(start.FileName)} {string.Join(' ', start.ArgumentList)} did not finish within {limit.TotalSeconds:0} s, and {(stopped ? "was stopped" : "could not be stopped")}\n"
                + $"its output:\n{(stdout.IsCompletedSuccessfully ? stdout.Result : "")}\nits errors:\n{(stderr.IsCompletedSuccessfully ? stderr.Result : "")}");
        }

        // Ended; its output is read to its end, unless something it started holds it open.
        process.WaitForExit();
        Assert.True(Task.WaitAll([stdout, stderr], limit), $"{Path.GetFileName(start.FileName)} ended, but a process it started still holds its output open");
        return new(stdout.Result, stderr.Result, process.ExitCode);
    }
}
