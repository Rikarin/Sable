using System.Diagnostics;
using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Differential fuzzing: small generated programs
///     (<see cref="ProgramGenerator" />) compiled and run two ways that must
///     agree — in the interpreter against natively, <c>-O0</c> against
///     <c>-O3</c>, separately against whole, generics specialised against not,
///     and against the library's interface rather than its source.
/// </summary>
/// <remarks>
///     What it finds is not a crash but a wrong answer: a miscompile. The same
///     program through every way is one case; a disagreement names the two and
///     prints the program, made smaller while it still disagrees. Settings are
///     the parser fuzzer's: <c>SABLE_FUZZ_SEED</c> moves the seed and
///     <c>SABLE_FUZZ_SCALE</c> multiplies the count. The self-hosted compiler
///     against this one is another pair, for when there are both.
/// </remarks>
[Trait("Category", "Fuzz")]
[Collection("Console")]
public sealed class DifferentialFuzzTests(Xunit.Abstractions.ITestOutputHelper output) {
    static int? Setting(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    static int Seed(int usual) => Setting("SABLE_FUZZ_SEED") is { } night ? unchecked(usual * 31 + night) : usual;

    static int Cases(int usual) => usual * Math.Max(1, Setting("SABLE_FUZZ_SCALE") ?? 1);

    /// <summary>Generated programs type-check, whatever the bytes: one that did not would compare nothing.</summary>
    [Fact]
    public void EveryGeneratedProgramTypeChecks() {
        var wrong = new List<string>();
        for (var i = 0; i < Cases(40); i++) {
            var source = ProgramGenerator.Generate(ProgramGenerator.Choices(Seed(1) + i, 20 + i * 37 % 800));
            var compilation = Compilation.Create([SyntaxTree.Parse(new SourceText(source, "generated.sb"))]);
            var errors = compilation.GetSemanticModel(compilation.SyntaxTrees[0]).GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
            wrong.AddRange(errors.Select(d => d.Format(compilation.SyntaxTrees[0].Text)));
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(12)));
    }

    /// <summary>In the interpreter alone: unspecialised, optimised, and against the library's interface.</summary>
    [Fact]
    public void TheInterpretersAgree() {
        var (ran, trapped) = (0, 0);
        for (var i = 0; i < Cases(4); i++) {
            var choices = ProgramGenerator.Choices(Seed(2) + i);
            try {
                if (DifferentialCheck.Check(ProgramGenerator.Generate(choices)) is { } outcome) {
                    ran++;
                    trapped += outcome.Trap is null ? 0 : 1;
                }
            } catch (InvalidOperationException failure) {
                Assert.Fail($"case {i}: {failure.Message}\n\nmade smaller:\n{Smaller(choices, static source => DifferentialCheck.Check(source))}");
            }
        }

        output.WriteLine($"{ran} programs agreed, {trapped} of them by trapping");
    }

    /// <summary>Natively, at -O0 and -O3, unspecialised, and compiled separately, against the interpreter.</summary>
    [Fact]
    public void NativeCodeAgrees() {
        if (!Programs.CanBuild) {
            return;
        }

        var (ran, trapped) = (0, 0);
        for (var i = 0; i < Cases(2); i++) {
            var choices = ProgramGenerator.Choices(Seed(3) + i);
            try {
                if (CheckNative(ProgramGenerator.Generate(choices)) is { } outcome) {
                    ran++;
                    trapped += outcome.Trap is null ? 0 : 1;
                }
            } catch (InvalidOperationException failure) {
                Assert.Fail($"case {i}: {failure.Message}\n\nmade smaller:\n{Smaller(choices, static source => CheckNative(source))}");
            }
        }

        output.WriteLine($"{ran} programs agreed, {trapped} of them by trapping");
    }

    static DifferentialCheck.Outcome? CheckNative(string source) {
        var reference = DifferentialCheck.Reference(source);
        if (reference.TooLong) {
            return null;
        }

        void Agree(string way, Programs.Execution native) {
            var agrees = native.Stdout == reference.Output
                && (reference.Trap is null
                    ? native.ExitCode == 0 && native.Stderr.Length == 0
                    : native.ExitCode != 0 && native.Stderr.Contains($"fatal error: {reference.Trap}", StringComparison.Ordinal));
            if (!agrees) {
                throw new InvalidOperationException($"{way} disagrees: the interpreter {reference}\n{way} exited with {native.ExitCode}, having printed\n{native.Stdout}\n{native.Stderr}\nthe program:\n{source}");
            }
        }

        Agree("-O0", Build(source, "0"));
        Agree("-O3", Build(source, "3"));
        using (SilGen.UseGenerics(GenericsMode.Unspecialised)) {
            Agree("-O0 unspecialised", Build(source, "0"));
        }

        Agree("separate compilation", Programs.BuildSeparatelyAndRun(source));
        return reference;
    }

    static Programs.Execution Build(string source, string level) {
        var optimised = level != "0";
        var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(source, "generated.sb"))], optimised: optimised, optLevel: level);
        if (!result.Succeeded) {
            throw new InvalidOperationException($"a generated program does not compile at -O{level}:\n{string.Join("\n", result.Errors)}\nthe program:\n{source}");
        }

        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-differential-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(result.LlvmIr!, executable, NativeOptions.Of(optimised, false) with { OptLevel = level });
            Assert.True(build.Succeeded, build.Output);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true }.Utf8();
            start.Environment["SABLE_CHECK_LEAKS"] = "1";
            var run = ChildProcess.Run(start, ChildProcess.ProgramLimit, "");
            return new(run.Stdout, run.Stderr, run.ExitCode);
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }

    /// <summary>
    ///     A smaller program that still disagrees: fewer choices, or simpler
    ///     ones, tried for two minutes. (The choices after the last are the
    ///     simplest, so cutting them short is always a program.)
    /// </summary>
    static string Smaller(byte[] choices, Action<string> check) {
        var clock = Stopwatch.StartNew();
        bool Fails(byte[] candidate) {
            try {
                check(ProgramGenerator.Generate(candidate));
                return false;
            } catch (InvalidOperationException) {
                return true;
            }
        }

        var best = choices;
        for (var length = best.Length / 2; length >= 1 && clock.Elapsed < TimeSpan.FromMinutes(2); length /= 2) {
            for (var at = best.Length - length; at >= 0 && clock.Elapsed < TimeSpan.FromMinutes(2); at -= length) {
                if (at + length > best.Length) {
                    continue;
                }

                byte[] without = [..best[..at], ..best[(at + length)..]];
                if (Fails(without)) {
                    best = without;
                }
            }
        }

        return ProgramGenerator.Generate(best);
    }
}
