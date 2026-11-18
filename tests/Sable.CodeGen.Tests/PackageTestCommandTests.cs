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
            ("src/lib.sb", "module Base\n\npublic fn twice(_ n: Int) -> Int {\n    return n * 2\n}\n"));
        var app = fixture.Package("app", "app", "0.1.0", "[dependencies]\nbase = { path = \"../base\" }\n",
            ("src/lib.sb", "module App\n\nimport Base\n\nfn quadruple(_ n: Int) -> Int {\n    return twice(twice(n))\n}\n"),
            ("src/main.sb", "module Main\n\nimport App\n\nfn main() {\n    print(\"main\")\n}\n"),
            ("tests/quadruple.sb", """
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

    /// <summary>
    ///     A dev-dependency is compiled into the test build, with what it
    ///     depends on — and kept out of an ordinary one, where using it is an error.
    /// </summary>
    [Fact]
    public void ATestBuildHasTheDevDependencies() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("digits", "digits", "0.1.0", "",
            ("src/lib.sb", "module Digits\n\npublic fn seven() -> Int {\n    return 7\n}\n"));
        fixture.Package("helper", "helper", "0.1.0", "[dependencies]\ndigits = { path = \"../digits\" }\n",
            ("src/lib.sb", "module Helper\n\nimport Digits\n\npublic fn expected() -> Int {\n    return seven() * 2\n}\n"));
        var app = fixture.Package("app", "app", "0.1.0", "[dev-dependencies]\nhelper = { path = \"../helper\" }\n",
            ("src/lib.sb", "module App\n\npublic fn fourteen() -> Int {\n    return 14\n}\n"),
            ("src/main.sb", "module Main\n\nimport App\n\nfn main() {\n    print(fourteen())\n}\n"),
            ("tests/fourteen.sb", """
                module App

                import Helper
                import Sable.Testing

                @test
                fn fourteenIsTwiceSeven() {
                    assertEqual(fourteen(), expected())
                }
                """));

        var run = Sable(fixture, app, "test");
        Assert.True(run.ExitCode == 0, run.Output + run.Errors);
        Assert.Contains("fourteenIsTwiceSeven", run.Output + run.Errors, StringComparison.Ordinal);

        var built = Sable(fixture, app, "build");
        Assert.True(built.ExitCode == 0, built.Errors);

        // The library itself may not use a dev-dependency.
        File.WriteAllText(Path.Combine(app, "src/lib.sb"), "module App\n\nimport Helper\n\npublic fn fourteen() -> Int {\n    return expected()\n}\n");
        var refused = Sable(fixture, app, "build");
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("Helper", refused.Errors, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A package's harness is kept in <c>target/harness/</c>: running the
    ///     tests again on unchanged sources runs the harness built last, and
    ///     an edit builds it again.
    /// </summary>
    [Fact]
    public void AnUnchangedPackagesTestsAreNotCompiledAgain() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        var app = fixture.Package("app", "app", "0.1.0", "",
            ("src/lib.sb", "module App\n\nfn twice(_ n: Int) -> Int {\n    return n * 2\n}\n"),
            ("tests/twice.sb", "module App\n\nimport Sable.Testing\n\n@test\nfn twicesThree() {\n    assertEqual(twice(3), 6)\n}\n"));

        var first = Sable(fixture, app, "test");
        Assert.True(first.ExitCode == 0, first.Output + first.Errors);
        var harness = Path.Combine(app, "target", "harness", "app", "test");
        Assert.True(File.Exists(harness));
        var built = File.GetLastWriteTimeUtc(harness);

        var again = Sable(fixture, app, "test");
        Assert.True(again.ExitCode == 0, again.Output + again.Errors);
        Assert.Contains("twicesThree ... ok", again.Output, StringComparison.Ordinal);
        Assert.Equal(built, File.GetLastWriteTimeUtc(harness));

        fixture.Write("app/src/lib.sb", "module App\n\nfn twice(_ n: Int) -> Int {\n    return n * 3\n}\n");
        var edited = Sable(fixture, app, "test");
        Assert.Equal(1, edited.ExitCode);
        Assert.NotEqual(built, File.GetLastWriteTimeUtc(harness));
    }
}
