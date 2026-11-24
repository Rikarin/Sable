using System.Diagnostics;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Builds and runs Sable programs natively and in the SIL interpreter, and
///     holds the two to the same output and the same traps.
/// </summary>
internal static class Programs {
    public static readonly bool CanBuild = NativeBuild.IsAvailable();

    public sealed record Execution(string Stdout, string Stderr, int ExitCode);

    public static Execution BuildAndRun(
        string source,
        bool optimise = true,
        string input = "",
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null
    ) {
        var result = Pipeline.Compile(new SourceText(source, "test.sb"), optimised: optimise);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(result.LlvmIr!, executable, optimise);
            Assert.True(build.Succeeded, build.Output + "\n\n" + result.LlvmIr);

            // Every test program is held to freeing what it allocates.
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true }.Utf8();
            start.Environment["SABLE_CHECK_LEAKS"] = "1";
            foreach (var (name, value) in environment ?? new Dictionary<string, string>()) {
                start.Environment[name] = value;
            }

            foreach (var argument in arguments ?? []) {
                start.ArgumentList.Add(argument);
            }

            var run = ChildProcess.Run(start, ChildProcess.ProgramLimit, input);
            return new(run.Stdout, run.Stderr, run.ExitCode);
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }

    public static (string Output, string? Trap) Interpret(string source, int stepLimit = 10_000_000) {
        var (output, _, trap) = Interpret(source, stepLimit, "", []);
        return (output, trap?.Message);
    }

    /// <summary>Interpreted only, with arguments, for a program that exits with a failure on purpose.</summary>
    public static (string Output, string Errors, string? Trap) InterpretWithArguments(string source, IReadOnlyList<string> arguments) {
        var (output, errors, trap) = Interpret(source, 10_000_000, "", arguments);
        return (output, errors, trap?.Message);
    }

    /// <summary>Interpreted, with standard input and arguments given and standard error kept.</summary>
    static (string Output, string Errors, SilTrap? Trap) Interpret(string source, int stepLimit, string input, IReadOnlyList<string> arguments) {
        var result = Pipeline.Compile(new SourceText(source, "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var interpreter = new SilInterpreter(result.Sil!, stepLimit) { Input = new StringReader(input), Arguments = arguments };
        try {
            interpreter.Run(result.Sil!.EntryPoint!);
            Assert.True(interpreter.LiveObjects == 0, $"the interpreter found {interpreter.LiveObjects} object(s) leaked");
            return (interpreter.Output.ToString(), interpreter.ErrorOutput.ToString(), null);
        } catch (SilTrap trap) {
            return (interpreter.Output.ToString(), interpreter.ErrorOutput.ToString(), trap);
        }
    }

    /// <summary>
    ///     Runs the program both ways and requires the same output and the same
    ///     trap. Without a C compiler, the interpreter's answer is all there is;
    ///     so it is for a test not marked <see cref="NativeAttribute" /> unless
    ///     <c>SABLE_TEST_NATIVE=all</c> (<see cref="NativeMode" />), as CI runs.
    ///     A program that writes files is checked by <paramref name="afterEach" />,
    ///     called with "interpreter" and then "native" after each run, so it can
    ///     keep what the first wrote to compare with what the second does.
    /// </summary>
    public static Execution AssertAgree(
        string source,
        int stepLimit = 10_000_000,
        string input = "",
        IReadOnlyList<string>? arguments = null,
        Action<string>? afterEach = null,
        IReadOnlyDictionary<string, string>? environment = null
    ) {
        var (output, errors, trap) = Interpret(source, stepLimit, input, arguments ?? []);
        afterEach?.Invoke("interpreter");
        if (!CanBuild || !NativeMode.Wanted) {
            return new(output, errors + trap?.Report, trap is { Exits: true } ? trap.ExitStatus : trap is null ? 0 : 134);
        }

        var native = BuildAndRun(source, input: input, arguments: arguments, environment: environment);
        afterEach?.Invoke("native");
        if (trap is { Exits: true }) {
            // It ended itself, which is no trap.
            Assert.Equal(trap.ExitStatus, native.ExitCode);
            Assert.Equal(output, native.Stdout);
            Assert.Equal(errors, native.Stderr);
            return native;
        }

        Assert.True(
            (trap is null) == (native.ExitCode == 0),
            $"the interpreter {(trap is null ? "ran to the end" : $"trapped: {trap.Message}")}, "
            + $"but the native program exited with {native.ExitCode}: {native.Stderr}"
        );
        Assert.Equal(output, native.Stdout);
        if (trap is null) {
            Assert.Equal(0, native.ExitCode);
            Assert.Equal(errors, native.Stderr);
        } else {
            Assert.NotEqual(0, native.ExitCode);
            Assert.Equal($"{errors}{trap.Report}", native.Stderr);
        }

        return native;
    }

    /// <summary>
    ///     The executor with no pool (runtime/tasks.c): the main thread runs
    ///     every task, one at a time, in the order the interpreter does. For
    ///     a test of how tasks interleave where they wait, which on the pool
    ///     depends on how the threads are scheduled.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> OneThread = new Dictionary<string, string> { ["SABLE_WORKERS"] = "0" };

    /// <summary>
    ///     The program compiled separately (docs/05 §5.4) — its file's
    ///     declarations dealt into files of their own, each a unit — and run
    ///     natively, leak-checked, as a dev build is.
    /// </summary>
    public static Execution BuildSeparatelyAndRun(string source, int parts = 3) {
        var root = (CompilationUnitSyntax)SyntaxTree.Parse(new SourceText(source, "test.sb")).Root;
        var members = root.Members.ToList();
        var bodies = Enumerable.Range(0, Math.Min(parts, members.Count)).Select(_ => new System.Text.StringBuilder(source[..members[0].FullSpan.Start])).ToList();
        for (var i = 0; i < members.Count; i++) {
            bodies[i % bodies.Count].Append(source.AsSpan(members[i].FullSpan.Start, members[i].FullSpan.Length)).Append('\n');
        }

        var directory = Directory.CreateTempSubdirectory("sable-units-").FullName;
        try {
            var units = bodies.Select((body, i) => {
                var path = Path.Combine(directory, $"part{i}.sb");
                File.WriteAllText(path, body.ToString());
                return new Driver.Build.UnitPlan($"part{i}", [new Driver.Build.SourceFile(path, $"part{i}.sb", [])]);
            }).ToList();
            var executable = Path.Combine(directory, "program");
            var built = Driver.Build.IncrementalBuild.Build(units, new Driver.Build.BuildSettings("dev", false, null, true), executable, Path.Combine(directory, "cache"));
            Assert.True(built.Succeeded, string.Join("\n", built.Errors));
            Assert.True(built.FellBack is null, built.FellBack);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true }.Utf8();
            start.Environment["SABLE_CHECK_LEAKS"] = "1";
            var run = ChildProcess.Run(start, ChildProcess.ProgramLimit, "");
            return new(run.Stdout, run.Stderr, run.ExitCode);
        } finally {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>A program with one main, as most tests want.</summary>
    public static string Program(string declarations, string body) =>
        $"module Test\n{declarations}\n@main\nfn main() {{\n{body}\n}}\n";
}
