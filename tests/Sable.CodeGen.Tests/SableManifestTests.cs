using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>package.sb</c> and <c>workspace.sb</c> (docs/05 §5.2): evaluated to
///     the manifest a <c>sable.toml</c> would give, held to the rules that keep
///     a manifest data, and edited by <c>sable add</c> and <c>remove</c>.
/// </summary>
public sealed class SableManifestTests {
    const string Full = """
                        import Sable.Build

                        // The design document's example.
                        let manifest = Package {
                            name: "render-engine",
                            version: "0.4.2",
                            authors: ["A. \"Quoted\" Person"],
                            license: "MIT OR Apache-2.0",
                            targets: [.library(path: "src/lib.sb"), .executable("render", path: "src/main.sb")],
                            dependencies: [
                                .registry("sable-math", "1.2"),
                                .registry("image", "0.6", features: ["png", "jpeg"], defaultFeatures: false),
                                .path("platform", "../platform"),
                                .registry("vulkan-sys", "1", optional: true),
                                .registry("metal-sys", "1", optional: true, when: .os(.MacOS)),
                            ],
                            devDependencies: [.registry("bench-harness", "0.3")],
                            features: [
                                "default": ["vulkan"],
                                "vulkan": ["dep:vulkan-sys"],
                                "metal": ["dep:metal-sys"],
                            ],
                            profiles: [.release(optLevel: 3, lto: .fat, exclusivity: .off), .dev(leakCheck: true)],
                            lints: ["unused_import": "deny"],
                            build: .script("build.sb", allowExec: ["pkg-config"], allowWrite: ["$OUT_DIR"]),
                        }
                        """;

    static Manifest Load(PackageFixture fixture, string text, string directory = "pkg") {
        fixture.Write($"{directory}/package.sb", text);
        return ManifestFiles.Load(Path.Combine(fixture.Root, directory));
    }

    static IReadOnlyList<ManifestError> Errors(string text) {
        using var fixture = new PackageFixture();
        return Assert.Throws<ManifestException>(() => Load(fixture, text)).Errors;
    }

    [Fact]
    public void AProfileSetsSizeStripRpathAndOverflowChecks() {
        using var fixture = new PackageFixture();
        var manifest = Load(fixture, """
                                     import Sable.Build

                                     let manifest = Package {
                                         name: "small",
                                         version: "1.0.0",
                                         profiles: [
                                             .release(optLevel: 3, overflowChecks: false, optSize: .minSize, strip: .symbols, rpath: true),
                                             .dev(arcChecks: false, optSize: .size, strip: .debuginfo, debugAssertions: false, incremental: false),
                                         ],
                                     }
                                     """);

        Assert.Equal("z", manifest.Release.OptLevel);
        Assert.Equal(StripMode.Symbols, manifest.Release.Strip);
        Assert.True(manifest.Release.Rpath);
        Assert.False(manifest.Release.OverflowChecks);
        Assert.Equal("s", manifest.Dev.OptLevel);
        Assert.Equal(StripMode.DebugInfo, manifest.Dev.Strip);
        Assert.False(manifest.Dev.Rpath);
        Assert.True(manifest.Dev.OverflowChecks);
        Assert.False(manifest.Dev.ArcChecks);
        Assert.False(manifest.Dev.DebugAssertions);
        Assert.False(manifest.Dev.Incremental);
        Assert.True(manifest.Release.Incremental);
    }

    [Fact]
    public void APackageSbHasPublishingSettingsAndProfilesOfItsOwn() {
        using var fixture = new PackageFixture();
        var manifest = Load(fixture, """
                                     import Sable.Build

                                     let manifest = Package {
                                         name: "shown",
                                         version: "1.0.0",
                                         readme: "README.md",
                                         keywords: ["render", "gpu"],
                                         include: ["src/**", "*.md"],
                                         exclude: ["*.png"],
                                         publish: false,
                                         profiles: [
                                             .release(lto: .thin),
                                             .named("profiling", inherits: .release, debug: true),
                                         ],
                                     }
                                     """);

        var package = manifest.Package!;
        Assert.Equal("README.md", package.Readme);
        Assert.Equal(["render", "gpu"], package.Keywords);
        Assert.Equal(["src/**", "*.md"], package.Include);
        Assert.Equal(["*.png"], package.Exclude);
        Assert.False(package.Publish);
        var profiling = manifest.ProfileNamed("profiling");
        Assert.True(profiling.Debug);
        Assert.Equal("3", profiling.OptLevel);
        Assert.Equal(LtoMode.Thin, profiling.Lto);
    }

    [Fact]
    public void TheDesignDocumentsManifestEvaluates() {
        using var fixture = new PackageFixture();
        var manifest = Load(fixture, Full);

        Assert.Equal("render-engine", manifest.Package!.Name);
        Assert.Equal("0.4.2", manifest.Package.Version.ToString());
        Assert.Equal(["A. \"Quoted\" Person"], manifest.Package.Authors);
        Assert.Equal("src/lib.sb", manifest.LibPath);
        Assert.Equal(("render", "src/main.sb"), (manifest.Bins[0].Name, manifest.Bins[0].Path));

        Assert.Equal(["sable-math", "image", "platform", "vulkan-sys", "metal-sys"], manifest.Dependencies.Select(static d => d.Name));
        var image = manifest.Dependencies[1];
        Assert.False(image.DefaultFeatures);
        Assert.Equal(["png", "jpeg"], image.Features);
        Assert.Equal("../platform", manifest.Dependencies[2].Path);
        Assert.Equal("os(macos)", manifest.Dependencies[4].When);
        Assert.Equal(["bench-harness"], manifest.DevDependencies.Select(static d => d.Name));

        Assert.Equal(["dep:vulkan-sys"], manifest.Features["vulkan"]);
        Assert.Equal("3", manifest.Release.OptLevel);
        Assert.Equal(LtoMode.Fat, manifest.Release.Lto);
        Assert.False(manifest.Release.Exclusivity);
        Assert.Equal("fat", manifest.Release.Recorded["lto"]);
        Assert.True(manifest.Dev.LeakCheck);
        Assert.Equal("build.sb", manifest.Build!.Script);
        Assert.Equal(["pkg-config"], manifest.Build.AllowExec);
        Assert.EndsWith("package.sb", manifest.File, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestMayComputeWithFunctionsAndLoops() {
        using var fixture = new PackageFixture();
        var manifest = Load(fixture, """
            import Sable.Build

            let backends = ["vulkan", "metal"]

            fn featureMap() -> [String: String[>..]] {
                var map: [String: String[>..]] = ["default": [backends[0]]]
                for backend in backends {
                    let enables: String[>..] = ["dep:\(backend)-sys"]
                    map[backend] = enables
                }
                return map
            }

            fn optionalBackends() -> Dependency[>..] {
                var all: Dependency[>..] = []
                for backend in backends {
                    all.append(.registry("\(backend)-sys", "1", optional: true))
                }
                return all
            }

            let manifest = Package {
                name: "renderer",
                version: "1.0.0",
                dependencies: optionalBackends(),
                features: featureMap(),
            }
            """);

        Assert.Equal(["vulkan-sys", "metal-sys"], manifest.Dependencies.Select(static d => d.Name));
        Assert.Equal(["dep:metal-sys"], manifest.Features["metal"]);
        Assert.Equal(["vulkan"], manifest.Features["default"]);
    }

    [Theory]
    [InlineData("import Sable.Build\nimport Sable.IO\nlet manifest = Package { name: \"a\", version: \"1.0.0\" }", 2, "cannot import 'Sable.IO'")]
    [InlineData("import Sable.Build\n#if os(macos)\nlet x = 1\n#endif\nlet manifest = Package { name: \"a\", version: \"1.0.0\" }", 2, "no conditional compilation")]
    [InlineData("import Sable.Build\nlet manifest = Package { name: Runtime.environment(name: \"USER\") ?? \"a\", version: \"1.0.0\" }", 2, "cannot use 'Runtime'")]
    [InlineData("import Sable.Build\nfn noisy() -> String {\n    print(\"hi\")\n    return \"a\"\n}\nlet manifest = Package { name: noisy(), version: \"1.0.0\" }", 3, "cannot use 'print'")]
    [InlineData("module Pkg\nimport Sable.Build\nlet manifest = Package { name: \"a\", version: \"1.0.0\" }", 1, "no module declaration")]
    [InlineData("import Sable.Build\nlet other = 1", 1, "declares `let manifest")]
    [InlineData("import Sable.Build\n\nlet manifest = 42", 3, "'manifest' must be a Package")]
    [InlineData("import Sable.Build\nlet manifest = Package { name: \"a\", version: \"one\" }", 1, "is not a semantic version")]
    [InlineData("import Sable.Build\nlet manifest = Package { name: \"a\", version: \"1.0.0\", unknown: 1 }", 2, "")]
    public void AManifestThatBreaksARuleSaysWhereAndWhy(string text, int line, string message) {
        var error = Errors(text)[0];
        Assert.EndsWith("package.sb", error.File, StringComparison.Ordinal);
        Assert.Equal(line, error.Line);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestThatNeverFinishesIsStopped() {
        var error = Errors("""
            import Sable.Build

            fn spin() -> String {
                var i = 0
                while true {
                    i = i + 1
                }
                return "a"
            }

            let manifest = Package { name: spin(), version: "1.0.0" }
            """)[0];
        Assert.Contains("steps", error.Message, StringComparison.Ordinal);
        Assert.Equal(11, error.Line);
    }

    [Fact]
    public void ADirectoryWithBothKindsOfManifestIsRefused() {
        using var fixture = new PackageFixture();
        fixture.Write("pkg/sable.toml", "[package]\nname = \"a\"\nversion = \"1.0.0\"");
        var errors = Assert.Throws<ManifestException>(() => Load(fixture, "import Sable.Build\nlet manifest = Package { name: \"a\", version: \"1.0.0\" }")).Errors;
        Assert.Contains("has both package.sb and sable.toml", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkspaceIsDeclaredInWorkspaceSb() {
        using var fixture = new PackageFixture();
        fixture.Write("ws/workspace.sb", """
            import Sable.Build

            let workspace = Workspace {
                members: ["crates/*"],
                exclude: ["crates/scratch"],
            }
            """);
        fixture.Write("ws/crates/a/package.sb", "import Sable.Build\nlet manifest = Package { name: \"a\", version: \"0.1.0\" }");
        fixture.Write("ws/crates/a/src/lib.sb", "module A\n\npublic fn one() -> Int {\n    return 1\n}");
        fixture.Write("ws/crates/b/package.sb", "import Sable.Build\nlet manifest = Package { name: \"b\", version: \"0.1.0\", dependencies: [.path(\"a\", \"../a\")] }");
        fixture.Write("ws/crates/b/src/lib.sb", "module B\n\nimport A\n\npublic fn two() -> Int {\n    return one() + 1\n}");
        fixture.Write("ws/crates/scratch/package.sb", "import Sable.Build\nlet manifest = Package { name: \"scratch\", version: \"0.1.0\" }");

        var workspace = fixture.Load("ws/crates/b");
        Assert.True(workspace.IsWorkspace);
        Assert.Equal(["a", "b"], workspace.Members.Select(static m => m.Name).Order());
        Assert.Equal("b", workspace.Current!.Name);
        Assert.Equal(0, PackageBuilder.Check(workspace, fixture.Environment, new()));
    }

    [Fact]
    public void AGitDependencyTakesATagABranchOrARev() {
        using var fixture = new PackageFixture();
        fixture.Write("app/package.sb", """
            import Sable.Build

            let manifest = Package {
                name: "app",
                version: "0.1.0",
                dependencies: [
                    .git("a", "https://example.com/a", tag: "v1.0.0"),
                    .git("b", "https://example.com/b", branch: "main", version: "0.3"),
                    .git("c", "https://example.com/c", rev: "0123abcd"),
                    .git("d", "https://example.com/d"),
                ],
            }
            """);

        var dependencies = fixture.Load("app").Members[0].Manifest.Dependencies.ToDictionary(static d => d.Name);
        Assert.Equal(new GitReference("tag", "v1.0.0"), dependencies["a"].Reference);
        Assert.Equal(new GitReference("branch", "main"), dependencies["b"].Reference);
        Assert.Equal("0.3", dependencies["b"].Version?.Text);
        Assert.Equal(new GitReference("rev", "0123abcd"), dependencies["c"].Reference);
        Assert.Equal("https://example.com/d", dependencies["d"].Git);
        Assert.Null(dependencies["d"].Reference);
    }

    [Fact]
    public void ADependencyForAnotherPlatformIsLockedButNotBuilt() {
        var other = OperatingSystem.IsMacOS() ? ".Linux" : ".MacOS";
        using var fixture = new PackageFixture();
        fixture.Write("helper/package.sb", "import Sable.Build\nlet manifest = Package { name: \"helper\", version: \"0.1.0\" }");
        fixture.Write("helper/src/lib.sb", "module Helper\n\npublic fn help() -> Int {\n    return 1\n}");
        fixture.Write("app/package.sb", $"import Sable.Build\nlet manifest = Package {{ name: \"app\", version: \"0.1.0\", dependencies: [.path(\"helper\", \"../helper\", when: .os({other}))] }}");
        fixture.Write("app/src/main.sb", "module App\n\nfn main() {\n    print(1)\n}");

        var workspace = fixture.Load("app");
        var graph = PackageGraph.Resolve(workspace, fixture.Environment);
        Assert.Contains(graph.Lock.Packages, static p => p.Name == "helper");
        Assert.DoesNotContain("helper", graph.Packages["app"].Dependencies);
        Assert.False(workspace.Members[0].Manifest.Dependencies[0].AppliesHere);
    }

    [Fact]
    public void NewAndInitWriteAPackageSbThatEvaluates() {
        using var fixture = new PackageFixture();
        var directory = Scaffold.New(fixture.Root, "my-app", library: false, fixture.Environment);
        Assert.True(File.Exists(Path.Combine(directory, "package.sb")));
        Assert.False(File.Exists(Path.Combine(directory, "sable.toml")));
        var manifest = ManifestFiles.Load(directory);
        Assert.Equal("my-app", manifest.Package!.Name);
        Assert.DoesNotContain("import Sable.IO", File.ReadAllText(Path.Combine(directory, "src", "main.sb")), StringComparison.Ordinal);

        // Its first check is clean: no warning about an import it does not use (#199).
        Assert.Equal(0, PackageBuilder.Check(Workspace.Load(directory), fixture.Environment, new()));
        Assert.DoesNotContain("warning", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALibraryIgnoresItsLockFileAndABinaryDoesNot() {
        using var fixture = new PackageFixture();
        var library = Scaffold.New(fixture.Root, "my-lib", library: true, fixture.Environment);
        var binary = Scaffold.New(fixture.Root, "my-bin", library: false, fixture.Environment);
        Assert.Equal("/target\n/sable.lock\n", File.ReadAllText(Path.Combine(library, ".gitignore")));
        Assert.Equal("/target\n", File.ReadAllText(Path.Combine(binary, ".gitignore")));
    }

    [Fact]
    public void NewInsideAWorkspaceSbJoinsItsMembers() {
        using var fixture = new PackageFixture();
        fixture.Write("ws/workspace.sb", """
                                         import Sable.Build

                                         let workspace = Workspace {
                                             members: [
                                                 "a", // the first
                                             ],
                                             exclude: ["scratch"],
                                         }
                                         """);
        fixture.Package("ws/a", "a", "0.1.0", "", ("src/lib.sb", "module A\n"));

        Scaffold.New(Path.Combine(fixture.Root, "ws"), "c", library: true, fixture.Environment);
        var text = File.ReadAllText(Path.Combine(fixture.Root, "ws", "workspace.sb"));
        Assert.Contains("        \"a\", // the first\n        \"c\",\n    ],", text, StringComparison.Ordinal);
        Assert.Equal(["a", "c"], fixture.Load("ws").Members.Select(static m => m.Name).Order());

        // From inside the new package, the workspace is found and it is current.
        Assert.Equal("c", fixture.Load("ws/c").Current!.Name);

        // An excluded directory stays out, and the manifest is untouched.
        Scaffold.New(Path.Combine(fixture.Root, "ws"), "scratch", library: true, fixture.Environment);
        Assert.Equal(text, File.ReadAllText(Path.Combine(fixture.Root, "ws", "workspace.sb")));
    }

    [Fact]
    public void NewInsideASableTomlWorkspaceJoinsItsMembers() {
        using var fixture = new PackageFixture();
        fixture.Write("ws/sable.toml", "# the workspace\n[workspace]\nmembers = [\"a\"] # one so far\n");
        fixture.Package("ws/a", "a", "0.1.0", "", ("src/lib.sb", "module A\n"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "ws", "tools"));

        Scaffold.New(Path.Combine(fixture.Root, "ws", "tools"), "b", library: false, fixture.Environment);
        Assert.Equal("# the workspace\n[workspace]\nmembers = [\"a\", \"tools/b\"] # one so far\n", File.ReadAllText(Path.Combine(fixture.Root, "ws", "sable.toml")));
        Assert.Equal(["a", "b"], fixture.Load("ws").Members.Select(static m => m.Name).Order());

        // A member a glob already covers is not added again.
        fixture.Write("ws2/sable.toml", "[workspace]\nmembers = [\"crates/*\"]\n");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "ws2", "crates"));
        Scaffold.New(Path.Combine(fixture.Root, "ws2", "crates"), "d", library: true, fixture.Environment);
        Assert.Equal("[workspace]\nmembers = [\"crates/*\"]\n", File.ReadAllText(Path.Combine(fixture.Root, "ws2", "sable.toml")));
    }

    [Fact]
    public void AWorkspaceWhoseMembersAreComputedGetsAWarningNotAnEdit() {
        using var fixture = new PackageFixture();
        const string text = """
                            import Sable.Build

                            fn all() -> String[>..] {
                                return ["a"]
                            }

                            let workspace = Workspace { members: all() }
                            """;
        fixture.Write("ws/workspace.sb", text);
        Scaffold.New(Path.Combine(fixture.Root, "ws"), "e", library: true, fixture.Environment);
        Assert.Equal(text + "\n", File.ReadAllText(Path.Combine(fixture.Root, "ws", "workspace.sb")));
        Assert.Contains("add \"e\" to its members", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PublishingCarriesTheEvaluatedManifestNotTheSable() {
        using var fixture = new PackageFixture();
        fixture.Write("lib/package.sb", "import Sable.Build\n// a comment\nlet manifest = Package { name: \"lib\", version: \"0.1.0\" }");
        fixture.Write("lib/src/lib.sb", "module Lib\n\npublic fn one() -> Int {\n    return 1\n}");

        var files = PackageOperations.Published(Path.Combine(fixture.Root, "lib"));
        Assert.DoesNotContain("package.sb", files.Keys);
        var toml = System.Text.Encoding.UTF8.GetString(files["sable.toml"]);
        Assert.Contains("name = \"lib\"", toml, StringComparison.Ordinal);
        Assert.Equal("lib", Manifest.Parse(toml).Package!.Name);
    }

    // ---- sable add / remove ------------------------------------------------

    const string Multiline = """
                             import Sable.Build

                             let manifest = Package {
                                 name: "app",
                                 version: "0.1.0",
                                 dependencies: [
                                     // the maths
                                     .registry("sable-math", "1.2"),
                                 ],
                             }
                             """;

    [Fact]
    public void AddingToAListOfLinesKeepsItsLayoutAndComments() {
        var edited = SableManifestEditor.Set(Multiline, "package.sb", "dependencies", "image", SableManifestEditor.Element("image", "0.6", null));
        Assert.Equal("""
                     import Sable.Build

                     let manifest = Package {
                         name: "app",
                         version: "0.1.0",
                         dependencies: [
                             // the maths
                             .registry("sable-math", "1.2"),
                             .registry("image", "0.6"),
                         ],
                     }
                     """, edited);
    }

    [Theory]
    [InlineData("dependencies: [],", "dependencies: [.path(\"util\", \"../util\")],")]
    [InlineData("dependencies: [.registry(\"a\", \"1\")],", "dependencies: [.registry(\"a\", \"1\"), .path(\"util\", \"../util\")],")]
    [InlineData("dependencies: [.path(\"util\", \"../old\")],", "dependencies: [.path(\"util\", \"../util\")],")]
    public void AddingToAOneLineListStaysOnOneLine(string before, string after) {
        var text = $"import Sable.Build\nlet manifest = Package {{\n    name: \"app\",\n    version: \"0.1.0\",\n    {before}\n}}\n";
        var edited = SableManifestEditor.Set(text, "package.sb", "dependencies", "util", SableManifestEditor.Element("util", null, "../util"));
        Assert.Equal(text.Replace(before, after, StringComparison.Ordinal), edited);
    }

    [Fact]
    public void AddingAKindOfDependencyTheManifestDoesNotHaveAddsTheField() {
        var edited = SableManifestEditor.Set(Multiline, "package.sb", "devDependencies", "bench", SableManifestEditor.Element("bench", "0.3", null));
        Assert.Contains("""
                            ],
                            devDependencies: [
                                .registry("bench", "0.3"),
                            ],
                        }
                        """, edited, StringComparison.Ordinal);
    }

    [Fact]
    public void AComputedListIsNotGuessedAt() {
        const string text = "import Sable.Build\nfn all() -> Dependency[>..] { return [] }\nlet manifest = Package { name: \"app\", version: \"0.1.0\", dependencies: all() }\n";
        var error = Assert.Throws<PackageException>(() => SableManifestEditor.Set(text, "package.sb", "dependencies", "a", ".registry(\"a\", \"1\")"));
        Assert.Contains("computed, not a list", error.Message, StringComparison.Ordinal);
        Assert.Contains(".registry(\"a\", \"1\")", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingTakesTheEntryAndItsLine() {
        var added = SableManifestEditor.Set(Multiline, "package.sb", "dependencies", "image", SableManifestEditor.Element("image", "0.6", null));
        Assert.Equal(Multiline, SableManifestEditor.Remove(added, "package.sb", "image"));
        Assert.Null(SableManifestEditor.Remove(Multiline, "package.sb", "nothing"));

        const string oneLine = "import Sable.Build\nlet manifest = Package { name: \"a\", version: \"1.0.0\", dependencies: [.registry(\"x\", \"1\"), .registry(\"y\", \"2\")] }\n";
        Assert.Equal(oneLine.Replace(".registry(\"x\", \"1\"), ", "", StringComparison.Ordinal), SableManifestEditor.Remove(oneLine, "package.sb", "x"));
        Assert.Equal(oneLine.Replace(", .registry(\"y\", \"2\")", "", StringComparison.Ordinal), SableManifestEditor.Remove(oneLine, "package.sb", "y"));
    }

    [Fact]
    public void AddAndRemoveWorkOnAPackageEndToEnd() {
        using var fixture = new PackageFixture();
        fixture.Write("util/package.sb", "import Sable.Build\nlet manifest = Package { name: \"util\", version: \"0.1.0\" }");
        fixture.Write("util/src/lib.sb", "module Util\n\npublic fn add(_ a: Int, _ b: Int) -> Int {\n    return a + b\n}");
        fixture.Write("app/package.sb", Multiline.Replace("""
                                                              // the maths
                                                              .registry("sable-math", "1.2"),

                                                      """, "", StringComparison.Ordinal));
        fixture.Write("app/src/main.sb", "module App\n\nimport Util\n\nfn main() {\n    print(add(2, 3))\n}");

        var workspace = fixture.Load("app");
        PackageOperations.Add(workspace, fixture.Environment, workspace.Members[0], "util", dev: false, path: "../util");
        Assert.Contains(".path(\"util\", \"../util\")", File.ReadAllText(Path.Combine(fixture.Root, "app", "package.sb")), StringComparison.Ordinal);
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("app"), fixture.Environment, new()));

        PackageOperations.Remove(fixture.Environment, fixture.Load("app").Members[0], "util");
        Assert.DoesNotContain("util", File.ReadAllText(Path.Combine(fixture.Root, "app", "package.sb")), StringComparison.Ordinal);
    }
}
