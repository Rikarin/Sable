using System.Text.Json.Nodes;
using Sable.Driver.Packages;
using Sable.Driver.Repl;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>doc</c>, <c>lint</c>, <c>debug</c> and <c>repl</c> in a package
///     (#198): each works on the package as a build sees it — its own files,
///     compiled with its dependencies — and not on the loose files under the
///     directory.
/// </summary>
public sealed class PackageToolsTests {
    const string Util = "module Util\n\n/// The sum.\npublic fn add(_ a: Int, _ b: Int) -> Int {\n    let unused = 3\n    return a + b\n}\n";

    const string AppLibrary = "module App\n\nimport Util\n\n/// Doubles.\npublic fn twice(_ x: Int) -> Int {\n    let spare = 1\n    return add(x, x)\n}\n";

    /// <summary>An app with a library, a binary, tests, and a path dependency that lints badly.</summary>
    static string WriteApp(PackageFixture fixture, string lints = "") {
        fixture.Package("util", "util", "1.0.0", "", ("src/lib.sb", Util));
        fixture.Package("app", "app", "1.0.0", $"[dependencies]\nutil = {{ path = \"../util\" }}\n{lints}",
            ("src/lib.sb", AppLibrary),
            ("src/main.sb", "module Main\n\nimport App\n\nfn main() {\n    print(twice(21))\n}\n"),
            ("tests/app_tests.sb", "module AppTests\n\nimport App\nimport Sable.Testing\n\n@test\nfn doubles() {\n    assert(twice(2) == 4)\n}\n"));
        return Path.Combine(fixture.Root, "app");
    }

    [Fact]
    public void DocDocumentsThePackagesLibraryWithItsDependencies() {
        using var fixture = new PackageFixture();
        var app = WriteApp(fixture);
        var result = SableProcess.Run(app, fixture.Home, "doc", "--json");
        Assert.True(result.ExitCode == 0, result.Errors);

        var symbols = JsonNode.Parse(File.ReadAllText(Path.Combine(app, "target", "doc", "symbols.json")))!.ToJsonString();
        Assert.Contains("twice", symbols, StringComparison.Ordinal);
        // Neither the tests nor the dependency are this package's documentation.
        Assert.DoesNotContain("AppTests", symbols, StringComparison.Ordinal);
        Assert.DoesNotContain("The sum.", symbols, StringComparison.Ordinal);

        // From a subdirectory, it is the same package, written at its root.
        var nested = SableProcess.Run(Path.Combine(app, "src"), fixture.Home, "doc");
        Assert.True(nested.ExitCode == 0, nested.Errors);
        Assert.True(File.Exists(Path.Combine(app, "target", "doc", "App.html")));
        Assert.False(File.Exists(Path.Combine(app, "target", "doc", "AppTests.html")));
    }

    [Fact]
    public void DocOpensWhatItWrote() {
        if (OperatingSystem.IsWindows()) {
            return;
        }

        using var fixture = new PackageFixture();
        var app = WriteApp(fixture);
        var opened = Path.Combine(fixture.Root, "opened.txt");
        var browser = fixture.Write("browser.sh", $"#!/bin/sh\necho \"$1\" > '{opened}'\n");
        File.SetUnixFileMode(browser, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = SableProcess.Run(app, fixture.Home, new Dictionary<string, string?> { ["BROWSER"] = browser }, "doc", "--open");
        Assert.True(result.ExitCode == 0, result.Errors);
        // The path as the process saw it: the temporary directory may be reached through a link.
        Assert.EndsWith(Path.Combine("app", "target", "doc", "index.html"), File.ReadAllText(opened).Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void DocReportsAPackageThatDoesNotCompile() {
        using var fixture = new PackageFixture();
        var app = WriteApp(fixture);
        File.WriteAllText(Path.Combine(app, "src", "lib.sb"), "module App\n\npublic fn broken() -> Int {\n    return \"no\"\n}\n");
        var result = SableProcess.Run(app, fixture.Home, "doc");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("S3004", result.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void LintLintsThePackagesOwnFilesAtItsLevels() {
        using var fixture = new PackageFixture();
        var app = WriteApp(fixture);
        var result = SableProcess.Run(app, fixture.Home, "lint");
        Assert.True(result.ExitCode == 0, result.Errors);
        Assert.Contains("'spare' is never read", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("'unused'", result.Output, StringComparison.Ordinal);
        Assert.Contains("1 lint(s), 0 of them error(s)", result.Output, StringComparison.Ordinal);

        // The command line's level goes over the manifest's.
        Assert.Equal(1, SableProcess.Run(app, fixture.Home, "lint", "--error", "unused_local").ExitCode);
        Assert.Equal(0, SableProcess.Run(app, fixture.Home, "lint", "--allow", "unused_local").ExitCode);
    }

    [Fact]
    public void LintTakesTheManifestsLevels() {
        using var fixture = new PackageFixture();
        var app = WriteApp(fixture, "[lints]\nunused_local = \"error\"\n");
        var result = SableProcess.Run(app, fixture.Home, "lint");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error[S3300]", result.Output, StringComparison.Ordinal);
        Assert.Equal(0, SableProcess.Run(app, fixture.Home, "lint", "--warn", "unused_local").ExitCode);
    }

    [Fact]
    public void CheckLintsTheMembersFilesNotItsDependencies() {
        using var fixture = new PackageFixture();
        WriteApp(fixture);
        var errors = new StringWriter();
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("app"), fixture.Environment with { Error = errors }, new()));
        Assert.Contains("'spare' is never read", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("'unused' is never read", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DebugBuildsThePackagesBinaryAndHandsItToLldb() {
        if (!Programs.CanBuild || OperatingSystem.IsWindows()) {
            return;
        }

        using var fixture = new PackageFixture();
        var app = WriteApp(fixture);

        // A stand-in lldb: says what it was given, and runs the program.
        var lldb = fixture.Write("lldb.sh", "#!/bin/sh\nfor a; do echo \"arg: $a\"; done\nwhile [ \"$1\" != \"--\" ]; do shift; done\nshift\n\"$@\"\n");
        File.SetUnixFileMode(lldb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = SableProcess.Run(app, fixture.Home, new Dictionary<string, string?> { ["LLDB"] = lldb }, "debug", "--batch", "-o", "run", "--", "extra");
        Assert.True(result.ExitCode == 0, result.Errors);
        Assert.Contains($"{Path.Combine("app", "target", "dev", "app")}\n", result.Output, StringComparison.Ordinal);
        Assert.Contains("arg: --batch\n", result.Output, StringComparison.Ordinal);
        Assert.Contains("arg: extra\n", result.Output, StringComparison.Ordinal);
        Assert.Contains("command script import", result.Output, StringComparison.Ordinal);
        Assert.EndsWith("42\n", result.Output, StringComparison.Ordinal);

        var none = SableProcess.Run(app, fixture.Home, new Dictionary<string, string?> { ["LLDB"] = lldb }, "debug", "--bin", "nothing");
        Assert.Equal(1, none.ExitCode);
        Assert.Contains("no binary called 'nothing'", none.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplImportsThePackageAndItsDependencies() {
        using var fixture = new PackageFixture();
        var app = WriteApp(fixture);
        var result = SableProcess.RunWithInput(app, fixture.Home, null, "import App\ntwice(4)\nimport Util\nadd(1, 2)\n", "repl");
        Assert.True(result.ExitCode == 0, result.Errors);
        Assert.Contains("res0: Int = 8", result.Output, StringComparison.Ordinal);
        Assert.Contains("res1: Int = 3", result.Output, StringComparison.Ordinal);

        // Without the package, its modules are not there.
        var loose = SableProcess.RunWithInput(app, fixture.Home, null, "import App\n", "repl", "--no-package");
        Assert.Contains("no library module 'App'", loose.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplSessionWithAPackagesTrees() {
        var library = SyntaxTree.Parse(new SourceText("module Shapes\n\npublic fn area(_ w: Int, _ h: Int) -> Int {\n    return w * h\n}\n", "shapes.sb"));
        var session = new ReplSession([library]);
        Assert.Equal("imported Shapes", session.Submit("import Shapes").Output);
        Assert.Equal("res0: Int = 12", session.Submit("area(3, 4)").Output);
        Assert.True(new ReplSession().Submit("import Shapes").Failed);
    }
}
