using System.Diagnostics;
using Sable.Symbols;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The system's random source (runtime/random.c): a program it fails for
///     stops. The failure is the program's own getrandom, which the link takes in
///     place of the C library's; macOS's arc4random has none to say.
/// </summary>
[Native]
public sealed class RandomSourceTests {
    const string FailingSource = "\ndefine i64 @getrandom(ptr %bytes, i64 %count, i32 %flags) {\n  ret i64 -1\n}\n";

    const string Draw = """
                        module R
                        @main
                        fn main() {
                            var system = SystemRandomNumberGenerator()
                            let drawn = system.next()
                            print(drawn == drawn)
                        }
                        """;

    [Fact]
    public void ADrawWhoseSourceFailsTraps() {
        if (!CanBuild || !OperatingSystem.IsLinux()) {
            return;
        }

        var (exitCode, stdout, stderr) = Run(Draw, FailingSource);
        Assert.NotEqual(0, exitCode);
        Assert.Equal("", stdout);
        Assert.StartsWith("fatal error: the system's random source failed\n", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ADrawWhoseSourceWorksIsGiven() => Assert.Equal("true\n", AssertAgree(Draw).Stdout);

    static (int ExitCode, string Stdout, string Stderr) Run(string source, string ir) {
        var result = Pipeline.Compile(new SourceText(source, "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(result.LlvmIr! + ir, executable, false);
            Assert.True(build.Succeeded, build.Output);
            var run = ChildProcess.Run(
                new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8(), ChildProcess.ProgramLimit);
            return (run.ExitCode, run.Stdout, run.Stderr);
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }
}
