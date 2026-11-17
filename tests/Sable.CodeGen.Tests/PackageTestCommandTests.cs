using System.Diagnostics;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable test</c> with no file named: the package's library, with its
///     <c>tests/</c> files (which an ordinary build leaves out) and its path
///     dependencies, run under the harness.
/// </summary>
public sealed class PackageTestCommandTests {
    static readonly bool CanBuild = NativeBuild.IsAvailable();

    static (string Output, string Errors, int ExitCode) Sable(PackageFixture fixture, string directory, params string[] arguments) {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "sable.dll"));
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        start.Environment["SABLE_HOME"] = fixture.Home;
        start.Environment.Remove("SABLE_REGISTRY");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (output.Result, errors, process.ExitCode);
    }

    [Fact]
    public void TestsAPackageWithItsTestsDirectoryAndAPathDependency() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("base", "base", "0.1.0", "",
            ("src/lib.sab", "module Base\n\npublic fn twice(_ n: Int) -> Int {\n    return n * 2\n}\n"));
        var app = fixture.Package("app", "app", "0.1.0", "[dependencies]\nbase = { path = \"../base\" }\n",
            ("src/lib.sab", "module App\n\nimport Base\n\nfn quadruple(_ n: Int) -> Int {\n    return twice(twice(n))\n}\n"),
            ("src/main.sab", "module Main\n\nimport App\n\nfn main() {\n    print(\"main\")\n}\n"),
            ("tests/quadruple.sab", """
                module App

                import Sable.Testing

                @test
                fn quadruplesSmallNumbers() {
                    assertEqual(quadruple(3), 12)
                }

                @test
                fn quadruplesZero() {
                    assertEqual(quadruple(0), 1)
                }
                """));

        var run = Sable(fixture, app, "test");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("quadruplesSmallNumbers", run.Output + run.Errors, StringComparison.Ordinal);
        Assert.Contains("quadruplesZero", run.Output + run.Errors, StringComparison.Ordinal);

        var filtered = Sable(fixture, app, "test", "SmallNumbers");
        Assert.True(filtered.ExitCode == 0, filtered.Output + filtered.Errors);

        // The tests/ files are not part of an ordinary build.
        var built = Sable(fixture, app, "build");
        Assert.True(built.ExitCode == 0, built.Errors);
    }
}
