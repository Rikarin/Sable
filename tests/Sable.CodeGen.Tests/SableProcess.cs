using System.Diagnostics;

namespace Sable.CodeGen.Tests;

/// <summary>The <c>sable</c> command, run as a user runs it: a process of its own, in a directory, with its own <c>SABLE_HOME</c>.</summary>
internal static class SableProcess {
    public sealed record Result(string Output, string Errors, int ExitCode);

    public static Result Run(string directory, string home, IReadOnlyDictionary<string, string?>? environment, params string[] arguments) =>
        RunWithInput(directory, home, environment, "", arguments);

    public static Result RunWithInput(string directory, string home, IReadOnlyDictionary<string, string?>? environment, string input, params string[] arguments) {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "sable.dll"));
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        start.Environment["SABLE_HOME"] = home;
        start.Environment.Remove("SABLE_REGISTRY");
        start.Environment.Remove("NO_COLOR");
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>()) {
            if (value is null) {
                start.Environment.Remove(name);
            } else {
                start.Environment[name] = value;
            }
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new(output.Result, errors, process.ExitCode);
    }

    public static Result Run(string directory, string home, params string[] arguments) => Run(directory, home, null, arguments);
}
