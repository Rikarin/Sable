using System.Diagnostics;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Packages on disk: many files, path dependencies, workspaces, features,
///     scaffolding, manifest edits — built and, where a C compiler is, run.
/// </summary>
public sealed class PackageTests {
    static readonly bool CanBuild = NativeBuild.IsAvailable();

    /// <summary>A library of two files in one module, one calling the other's private function.</summary>
    static void WriteUtil(PackageFixture fixture, string directory = "util") =>
        fixture.Package(directory, "util", "0.3.0", "",
            ("src/lib.sb", """
                module Util

                /// Twice the number.
                public fn double(_ x: Int) -> Int {
                    return twice(x)
                }
                """),
            ("src/arithmetic.sb", """
                module Util

                fn twice(_ x: Int) -> Int {
                    return x + x
                }
                """));

    /// <summary>A program of two files that uses <c>util</c>.</summary>
    static void WriteApp(PackageFixture fixture, string directory = "app", string utilPath = "../util") =>
        fixture.Package(directory, "app", "1.0.0", $"[dependencies]\nutil = {{ path = \"{utilPath}\" }}\n",
            ("src/main.sb", """
                module App

                import Sable.IO
                import Util

                fn main() {
                    print(greeting())
                    print("\(double(21))")
                }
                """),
            ("src/greeting.sb", """
                module App

                fn greeting() -> String {
                    return "hello from a package"
                }
                """));

    [Fact]
    public void AManyFilePackageWithAPathDependencyBuildsAndRuns() {
        using var fixture = new PackageFixture();
        WriteUtil(fixture);
        WriteApp(fixture);
        var workspace = fixture.Load("app");

        Assert.Equal(0, PackageBuilder.Check(workspace, fixture.Environment, new()));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "app", "sable.lock")));
        if (!CanBuild) {
            return;
        }

        foreach (var release in new[] { false, true }) {
            var built = PackageBuilder.Build(workspace, fixture.Environment, new(Release: release));
            Assert.True(built is not null, fixture.Errors.ToString());
            var binary = Assert.Single(built);
            Assert.Equal(Path.Combine(fixture.Root, "app", "target", release ? "release" : "dev", "app"), binary.Path);
            Assert.Equal(("hello from a package\n42\n", 0), PackageFixture.Execute(binary.Path));
        }
    }

    /// <summary>
    ///     A profile's settings reach the build: an optimised release with
    ///     debug information (the SIL passes run, and on macOS the dSYM holds
    ///     the program's functions), and a dev build of one codegen unit
    ///     compiled whole. What it cannot honour is said.
    /// </summary>
    [Fact]
    public void AProfileBuildsAsItsSettingsSay() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        WriteUtil(fixture);
        WriteApp(fixture);
        var manifest = Path.Combine(fixture.Root, "app", "sable.toml");
        File.AppendAllText(manifest, "\n[profile.release]\nopt-level = 1\ndebug = true\nlto = \"thin\"\npanic = \"unwind\"\n\n[profile.dev]\ncodegen-units = 1\n");
        var workspace = fixture.Load("app");

        var release = Assert.Single(PackageBuilder.Build(workspace, fixture.Environment, new(Release: true, Timings: true))!);
        Assert.Equal(("hello from a package\n42\n", 0), PackageFixture.Execute(release.Path));
        Assert.Contains("profile.release.panic: a trap always aborts the program", fixture.Errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("[optimised + debuginfo]", fixture.Errors.ToString(), StringComparison.Ordinal);
        if (OperatingSystem.IsMacOS()) {
            using var dwarf = Process.Start(new ProcessStartInfo("dwarfdump", ["--debug-info", release.Path + ".dSYM"]) { RedirectStandardOutput = true })!;
            var info = dwarf.StandardOutput.ReadToEnd();
            dwarf.WaitForExit();
            Assert.Contains("src/main.sb", info, StringComparison.Ordinal);
        }

        var dev = Assert.Single(PackageBuilder.Build(workspace, fixture.Environment, new(Timings: true))!);
        Assert.Equal(("hello from a package\n42\n", 0), PackageFixture.Execute(dev.Path));
        Assert.Contains("Built app: whole", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorsAreReportedAgainstTheFileTheyAreIn() {
        using var fixture = new PackageFixture();
        WriteUtil(fixture);
        WriteApp(fixture);
        fixture.Write("util/src/arithmetic.sb", "module Util\n\nfn twice(_ x: Int) -> Int {\n    return x + true\n}\n");

        Assert.Equal(1, PackageBuilder.Check(fixture.Load("app"), fixture.Environment, new()));
        Assert.Contains("--> ../util/src/arithmetic.sb:4:", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANewPackageBuildsAndRuns() {
        using var fixture = new PackageFixture();
        var directory = Scaffold.New(fixture.Root, "hello-world", library: false, fixture.Environment);
        var workspace = Workspace.Load(directory);
        Assert.Equal("hello-world", workspace.Members[0].Name);
        Assert.Contains("module HelloWorld", File.ReadAllText(Path.Combine(directory, "src", "main.sb")), StringComparison.Ordinal);
        Assert.Equal("/target\n", File.ReadAllText(Path.Combine(directory, ".gitignore")));
        Assert.Throws<PackageException>(() => Scaffold.New(fixture.Root, "hello-world", false, fixture.Environment));

        Assert.Equal(0, PackageBuilder.Check(workspace, fixture.Environment, new()));
        if (CanBuild) {
            var binary = Assert.Single(PackageBuilder.Build(workspace, fixture.Environment, new())!);
            Assert.Equal("hello-world", binary.Name);
            Assert.Equal(("Hello, world!\n", 0), PackageFixture.Execute(binary.Path));
        }
    }

    [Fact]
    public void InitMakesALibraryInAnExistingDirectory() {
        using var fixture = new PackageFixture();
        var directory = Path.Combine(fixture.Root, "My Lib");
        Directory.CreateDirectory(directory);
        Scaffold.Init(directory, null, library: true, fixture.Environment);

        var workspace = Workspace.Load(directory);
        Assert.Equal("my-lib", workspace.Members[0].Name);
        Assert.Empty(workspace.Members[0].Bins);
        Assert.Equal(0, PackageBuilder.Check(workspace, fixture.Environment, new()));

        // A library builds by being checked: nothing to link.
        Assert.Empty(PackageBuilder.Build(workspace, fixture.Environment, new())!);
        Assert.Throws<PackageException>(() => Scaffold.Init(directory, null, true, fixture.Environment));
    }

    [Fact]
    public void AWorkspaceSharesOneLockAndOneTarget() {
        using var fixture = new PackageFixture();
        fixture.Write("ws/sable.toml", "[workspace]\nmembers = [\"crates/*\"]\nexclude = [\"crates/scratch\"]\n");
        WriteUtil(fixture, "ws/crates/util");
        WriteApp(fixture, "ws/crates/app");
        fixture.Package("ws/crates/scratch", "scratch", "0.0.1", "", ("src/lib.sb", "module Scratch\n"));

        // From inside a member, the workspace is found, and that member is current.
        var workspace = fixture.Load("ws/crates/app/src");
        Assert.Equal(Path.Combine(fixture.Root, "ws"), workspace.Root);
        Assert.Equal(["app", "util"], workspace.Members.Select(static m => m.Name).Order());
        Assert.Equal("app", workspace.Current!.Name);

        Assert.Equal(0, PackageBuilder.Check(workspace, fixture.Environment, new()));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "ws", "sable.lock")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "ws", "crates", "app", "sable.lock")));
        var locked = LockFile.Load(Path.Combine(fixture.Root, "ws", "sable.lock"))!;
        Assert.Equal(["app", "util"], locked.Packages.Select(static p => p.Name));
        Assert.Equal("path+crates/util", locked.Find("util")!.Source);

        // From the root, everything; a member by name.
        var root = fixture.Load("ws");
        Assert.Null(root.Current);
        Assert.Equal(0, PackageBuilder.Check(root, fixture.Environment, new()));
        Assert.Equal(0, PackageBuilder.Check(root, fixture.Environment, new(Package: "util")));
        Assert.Throws<PackageException>(() => PackageBuilder.Check(root, fixture.Environment, new(Package: "nobody")));

        if (CanBuild) {
            var binary = Assert.Single(PackageBuilder.Build(root, fixture.Environment, new())!);
            Assert.Equal(Path.Combine(fixture.Root, "ws", "target", "dev", "app"), binary.Path);
        }
    }

    [Fact]
    public void FeaturesTurnOnCodeAndOptionalDependencies() {
        using var fixture = new PackageFixture();
        fixture.Package("extra", "extra", "1.0.0", "", ("src/lib.sb", "module Extra\n\npublic fn extraValue() -> Int {\n    return 7\n}\n"));
        fixture.Package("shapes", "shapes", "1.0.0", """
            [dependencies]
            extra = { path = "../extra", optional = true }

            [features]
            default = ["fancy"]
            fancy = []
            more = ["dep:extra", "fancy"]
            """,
            ("src/lib.sb", """
                module Shapes

                public fn describe() -> String {
                #if feature(fancy)
                    return "fancy"
                #else
                    return "plain"
                #endif
                }
                """));

        void App(string dependency) => fixture.Package("app", "app", "1.0.0", $"[dependencies]\nshapes = {dependency}\n",
            ("src/main.sb", "module App\n\nimport Sable.IO\nimport Shapes\n\nfn main() {\n    print(describe())\n}\n"));

        // Default features: fancy, and no optional dependency.
        App("{ path = \"../shapes\" }");
        var graph = PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.Equal(["default", "fancy"], graph.Packages["shapes"].Features.Order());
        Assert.False(graph.Packages.ContainsKey("extra"));

        // Without them: plain.
        App("{ path = \"../shapes\", default-features = false }");
        graph = PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.Empty(graph.Packages["shapes"].Features);

        // A feature that turns on a dependency, and another feature.
        App("{ path = \"../shapes\", default-features = false, features = [\"more\"] }");
        graph = PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.Equal(["fancy", "more"], graph.Packages["shapes"].Features.Order());
        Assert.Equal(["extra"], graph.Packages["shapes"].Dependencies);

        // The lock file holds what any feature could need, whatever this build chose.
        Assert.NotNull(graph.Lock.Find("extra"));

        if (CanBuild) {
            App("{ path = \"../shapes\", default-features = false }");
            var plain = Assert.Single(PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new())!);
            Assert.Equal("plain\n", PackageFixture.Execute(plain.Path).Output);

            App("{ path = \"../shapes\" }");
            var fancy = Assert.Single(PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new())!);
            Assert.Equal("fancy\n", PackageFixture.Execute(fancy.Path).Output);
        }
    }

    [Fact]
    public void AWeakFeatureTurnsOnOnlyWhenItsDependencyIsOn() {
        using var fixture = new PackageFixture();
        fixture.Package("extra", "extra", "1.0.0", "[features]\nvalidation = []\n", ("src/lib.sb", "module Extra\n"));
        fixture.Package("app", "app", "1.0.0", """
            [dependencies]
            extra = { path = "../extra", optional = true }

            [features]
            debug = ["extra?/validation"]
            with-extra = ["dep:extra"]
            """,
            ("src/main.sb", "module App\nfn main() {}\n"));
        var workspace = fixture.Load("app");

        // Alone, the weak feature turns nothing on: no dependency, no feature.
        var graph = PackageGraph.Resolve(workspace, fixture.Environment, new(new(["debug"])));
        Assert.False(graph.Packages.ContainsKey("extra"));

        // With the dependency on, it turns the dependency's feature on.
        graph = PackageGraph.Resolve(workspace, fixture.Environment, new(new(["debug", "with-extra"])));
        Assert.Equal(["default", "validation"], graph.Packages["extra"].Features.Order());

        // The dependency on without the weak feature: not turned on.
        graph = PackageGraph.Resolve(workspace, fixture.Environment, new(new(["with-extra"])));
        Assert.DoesNotContain("validation", graph.Packages["extra"].Features);
    }

    [Fact]
    public void WhatAManifestSaysThatIsNotActedOnIsWarnedAbout() {
        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "1.0.0", "flavour = \"mint\"\n[reflect]\ninclude = [\"Scene\"]\n",
            ("src/main.sb", "module App\nfn main() {}\n"));
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("app"), fixture.Environment, new()));
        var errors = fixture.Errors.ToString();
        Assert.Contains("warning: ", errors, StringComparison.Ordinal);
        Assert.Contains("package.flavour: unknown key, ignored", errors, StringComparison.Ordinal);
        Assert.Contains("reflect: has no effect yet", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRootPackagesOwnFeaturesComeFromTheCommandLine() {
        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "1.0.0", "[features]\nloud = []\n",
            ("src/main.sb", "module App\n\nimport Sable.IO\n\nfn main() {\n#if feature(loud)\n    print(\"LOUD\")\n#else\n    print(\"quiet\")\n#endif\n}\n"));
        var workspace = fixture.Load("app");

        var graph = PackageGraph.Resolve(workspace, fixture.Environment, new(new(["loud"])));
        Assert.Contains("loud", graph.Packages["app"].Features);
        Assert.Throws<PackageException>(() => PackageGraph.Resolve(workspace, fixture.Environment, new(new(["absent"]))));

        if (CanBuild) {
            var loud = Assert.Single(PackageBuilder.Build(workspace, fixture.Environment, new(Features: new(["loud"])))!);
            Assert.Equal("LOUD\n", PackageFixture.Execute(loud.Path).Output);
            var quiet = Assert.Single(PackageBuilder.Build(workspace, fixture.Environment, new())!);
            Assert.Equal("quiet\n", PackageFixture.Execute(quiet.Path).Output);
        }
    }

    [Fact]
    public void APathDependencyMustBeThePackageItSays() {
        using var fixture = new PackageFixture();
        WriteUtil(fixture);
        fixture.Package("app", "app", "1.0.0", "[dependencies]\nutils = { path = \"../util\" }\n", ("src/main.sb", "module App\nfn main() {}\n"));
        var error = Assert.Throws<PackageException>(() => PackageGraph.Resolve(fixture.Load("app"), fixture.Environment));
        Assert.Contains("is called 'util'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAndRemoveEditTheManifestAndKeepItsComments() {
        using var fixture = new PackageFixture();
        WriteUtil(fixture);
        fixture.Package("app", "app", "1.0.0", "# my dependencies\n[dependencies]\n\n# tools\n[profile.dev]\nopt-level = 0\n", ("src/main.sb", "module App\nfn main() {}\n"));
        var workspace = fixture.Load("app");

        PackageOperations.Add(workspace, fixture.Environment, workspace.Members[0], "util", false, "../util");
        var text = File.ReadAllText(workspace.Members[0].ManifestPath);
        Assert.Contains("[dependencies]\nutil = { path = \"../util\" }\n", text, StringComparison.Ordinal);
        Assert.Contains("# tools", text, StringComparison.Ordinal);
        Assert.NotNull(LockFile.Load(workspace.LockPath)!.Find("util"));

        // A requirement nothing in the registry meets is refused, and the manifest left alone.
        Assert.Throws<PackageException>(() => PackageOperations.Add(workspace, fixture.Environment, workspace.Members[0], "missing@1.0", false, null));
        Assert.Equal(text, File.ReadAllText(workspace.Members[0].ManifestPath));

        PackageOperations.Remove(fixture.Environment, fixture.Load("app").Members[0], "util");
        text = File.ReadAllText(workspace.Members[0].ManifestPath);
        Assert.DoesNotContain("util", text, StringComparison.Ordinal);
        Assert.Contains("# my dependencies", text, StringComparison.Ordinal);
        Assert.Null(LockFile.Load(workspace.LockPath)!.Find("util"));
        Assert.Throws<PackageException>(() => PackageOperations.Remove(fixture.Environment, fixture.Load("app").Members[0], "util"));
    }

    [Fact]
    public void RemovingADependencyAFeatureNeedsIsRefused() {
        using var fixture = new PackageFixture();
        WriteUtil(fixture);
        var directory = fixture.Package("app", "app", "1.0.0", "[dependencies]\nutil = { path = \"../util\", optional = true }\n[features]\nu = [\"dep:util\"]\n", ("src/main.sb", "module App\nfn main() {}\n"));
        var before = File.ReadAllText(Path.Combine(directory, "sable.toml"));
        Assert.Throws<ManifestException>(() => PackageOperations.Remove(fixture.Environment, fixture.Load("app").Members[0], "util"));
        Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "sable.toml")));
    }

    [Fact]
    public void TreeShowsTheGraph() {
        using var fixture = new PackageFixture();
        WriteUtil(fixture);
        WriteApp(fixture);
        var tree = PackageOperations.Tree(fixture.Load("app"), fixture.Environment, null);
        Assert.Equal($"app v1.0.0 ({Path.Combine(fixture.Root, "app")})\n└── util v0.3.0 (path ../util)\n", tree);
    }

    [Fact]
    public void CleanRemovesTheTarget() {
        using var fixture = new PackageFixture();
        WriteApp(fixture);
        WriteUtil(fixture);
        Directory.CreateDirectory(fixture.Load("app").TargetDirectory + "/dev");
        PackageBuilder.Clean(fixture.Load("app"), fixture.Environment);
        Assert.False(Directory.Exists(fixture.Load("app").TargetDirectory));
    }

    [Fact]
    public void TheSableCommandScaffoldsBuildsAndRuns() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        var sable = Path.Combine(AppContext.BaseDirectory, "sable.dll");

        (string Output, string Errors, int ExitCode) Run(string directory, params string[] arguments) {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(sable);
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

        Assert.Equal(0, Run(fixture.Root, "new", "mathx", "--lib").ExitCode);
        var published = Run(Path.Combine(fixture.Root, "mathx"), "publish");
        Assert.True(published.ExitCode == 0, published.Errors);
        Assert.True(File.Exists(Path.Combine(fixture.Home, "key")));

        Assert.Equal(0, Run(fixture.Root, "new", "calc").ExitCode);
        var calc = Path.Combine(fixture.Root, "calc");
        Assert.Equal(0, Run(calc, "add", "mathx").ExitCode);
        File.WriteAllText(Path.Combine(calc, "src", "main.sb"), "module Calc\n\nimport Sable.IO\nimport Mathx\n\nfn main() {\n    print(\"\\(add(2, 3))\")\n}\n");

        var run = Run(calc, "run", "--release");
        Assert.True(run.ExitCode == 0, run.Errors);
        Assert.Equal("5\n", run.Output);
        Assert.Contains("mathx v0.1.0", Run(calc, "tree").Output, StringComparison.Ordinal);
        Assert.Equal(0, Run(calc, "check").ExitCode);
        Assert.Equal(0, Run(calc, "clean").ExitCode);

        var bad = Run(calc, "add", "nothing-here");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("no package called 'nothing-here'", bad.Errors, StringComparison.Ordinal);

        var outside = Run(fixture.Home, "build");
        Assert.Equal(1, outside.ExitCode);
        Assert.Contains("could not find package.sb", outside.Errors, StringComparison.Ordinal);
    }
}
