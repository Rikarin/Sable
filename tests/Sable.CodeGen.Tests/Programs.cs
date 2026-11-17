using System.Diagnostics;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Builds and runs Sable programs natively and in the SIL interpreter, and
///     holds the two to the same output and the same traps.
/// </summary>
internal static class Programs {
    public static readonly bool CanBuild = NativeBuild.IsAvailable();

    public sealed record Execution(string Stdout, string Stderr, int ExitCode);

    public static Execution BuildAndRun(string source, bool optimise = true) {
        var result = Pipeline.Compile(new SourceText(source, "test.sab"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        var executable = Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}");
        try {
            var build = NativeBuild.Build(result.LlvmIr!, executable, optimise);
            Assert.True(build.Succeeded, build.Output + "\n\n" + result.LlvmIr);

            // Every test program is held to freeing what it allocates.
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["SABLE_CHECK_LEAKS"] = "1";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new(stdout.Result, stderr, process.ExitCode);
        } finally {
            File.Delete(executable);
        }
    }

    public static (string Output, string? Trap) Interpret(string source, int stepLimit = 10_000_000) {
        var result = Pipeline.Compile(new SourceText(source, "test.sab"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var interpreter = new SilInterpreter(result.Sil!, stepLimit);
        try {
            interpreter.Run(result.Sil!.EntryPoint!);
            Assert.True(interpreter.LiveObjects == 0, $"the interpreter found {interpreter.LiveObjects} object(s) leaked");
            return (interpreter.Output.ToString(), null);
        } catch (SilTrap trap) {
            return (interpreter.Output.ToString(), trap.Message);
        }
    }

    /// <summary>
    ///     Runs the program both ways and requires the same output and the same
    ///     trap. Without a C compiler, the interpreter's answer is all there is.
    /// </summary>
    public static Execution AssertAgree(string source, int stepLimit = 10_000_000) {
        var (output, trap) = Interpret(source, stepLimit);
        if (!CanBuild) {
            return new(output, trap is null ? "" : $"fatal error: {trap}\n", trap is null ? 0 : 134);
        }

        var native = BuildAndRun(source);
        Assert.True(
            (trap is null) == (native.ExitCode == 0),
            $"the interpreter {(trap is null ? "ran to the end" : $"trapped: {trap}")}, "
            + $"but the native program exited with {native.ExitCode}: {native.Stderr}"
        );
        Assert.Equal(output, native.Stdout);
        if (trap is null) {
            Assert.Equal(0, native.ExitCode);
            Assert.Equal("", native.Stderr);
        } else {
            Assert.NotEqual(0, native.ExitCode);
            Assert.Equal($"fatal error: {trap}\n", native.Stderr);
        }

        return native;
    }

    /// <summary>A program with one main, as most tests want.</summary>
    public static string Program(string declarations, string body) =>
        $"module Test\n{declarations}\n@main\nfn main() {{\n{body}\n}}\n";
}
