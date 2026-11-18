using System.Diagnostics;
using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Builds programs as <c>sable test</c> and <c>sable bench</c> do — with
///     the harness the compiler writes as their entry point — and runs them,
///     natively or in the SIL interpreter.
/// </summary>
internal static class Harnesses {
    public static readonly bool CanBuild = NativeBuild.IsAvailable();

    public sealed record Run(string Stdout, string Stderr, int ExitCode);

    public static CompileResult Compile(string source, TestKind kind = TestKind.Test, bool doc = false) =>
        Compile([new SourceText(source, "test.sb")], kind, doc);

    public static CompileResult Compile(IReadOnlyList<SourceText> sources, TestKind kind = TestKind.Test, bool doc = false) =>
        TestHarness.Compile(sources, kind, out _, doc);

    /// <summary>Builds the harness natively and runs it with the arguments, leak-checked.</summary>
    public static Run Native(string source, TestKind kind = TestKind.Test, IReadOnlyList<string>? arguments = null, bool doc = false) =>
        Native([new SourceText(source, "test.sb")], kind, arguments, doc);

    public static Run Native(IReadOnlyList<SourceText> sources, TestKind kind = TestKind.Test, IReadOnlyList<string>? arguments = null, bool doc = false) {
        var result = Compile(sources, kind, doc);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-harness-{Guid.NewGuid():N}"));
        try {
            // A test build is unoptimised, as 'sable test' builds it.
            var build = NativeBuild.Build(result.LlvmIr!, executable, kind == TestKind.Bench);
            Assert.True(build.Succeeded, build.Output);

            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["SABLE_CHECK_LEAKS"] = "1";
            foreach (var argument in arguments ?? []) {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new(stdout.Result, stderr, process.ExitCode);
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }

    /// <summary>
    ///     Runs the harness in the interpreter, where there is no executable to
    ///     start again, so the tests run in one process; the trap, if one ended it.
    /// </summary>
    public static (string Output, string Errors, string? Trap) Interpret(string source, IReadOnlyList<string>? arguments = null) {
        var result = Compile(source);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var interpreter = new SilInterpreter(result.Sil!, 50_000_000) { Arguments = arguments ?? [] };
        try {
            interpreter.Run(result.Sil!.EntryPoint!);
            Assert.True(interpreter.LiveObjects == 0, $"the interpreter found {interpreter.LiveObjects} object(s) leaked");
            return (interpreter.Output.ToString(), interpreter.ErrorOutput.ToString(), null);
        } catch (SilTrap trap) {
            return (interpreter.Output.ToString(), interpreter.ErrorOutput.ToString(), trap.Message);
        }
    }

    /// <summary>The errors of compiling the source as an ordinary program would be.</summary>
    public static IReadOnlyList<string> ErrorsOf(string source) => Pipeline.Compile(new SourceText(source, "test.sb")).Errors;

    /// <summary>The repository's root, where Sable.slnx is.</summary>
    public static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }
}
