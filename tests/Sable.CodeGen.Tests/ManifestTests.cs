using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary><c>sable.toml</c>: what a manifest says, and how it says what is wrong with one.</summary>
public sealed class ManifestTests {
    static Manifest Parse(string text) => Manifest.Parse(text, "pkg/sable.toml");

    static IReadOnlyList<ManifestError> Errors(string text) => Assert.Throws<ManifestException>(() => Parse(text)).Errors;

    [Fact]
    public void TheDesignDocumentsManifestReads() {
        var manifest = Parse("""
            [package]
            name        = "render-engine"
            version     = "0.4.2"
            edition     = "2027"
            authors     = ["A. Person"]
            license     = "MIT OR Apache-2.0"
            description = "A small forward renderer"
            repository  = "https://example.com/render-engine"

            [lib]
            path = "src/lib.sb"

            [[bin]]
            name = "render"
            path = "src/main.sb"

            [dependencies]
            sable-math  = "1.2"
            image       = { version = "0.6", features = ["png", "jpeg"], default-features = false }
            platform    = { path = "../platform" }
            vulkan-sys  = { version = "1", optional = true }
            metal-sys   = { version = "1", optional = true }

            [dev-dependencies]
            bench-harness = "0.3"

            [build-dependencies]
            shader-compiler = "1.0"

            [features]
            default   = ["vulkan"]
            vulkan    = ["dep:vulkan-sys"]
            metal     = ["dep:metal-sys"]
            debug-gpu = ["vulkan-sys?/validation"]

            [profile.release]
            opt-level        = 3
            lto              = "fat"
            codegen-units    = 1
            panic            = "abort"
            strip            = "debuginfo"
            arc-checks       = false
            exclusivity      = "unchecked"
            overflow-checks  = true

            [profile.dev]
            opt-level        = 0
            debug            = true
            exclusivity      = "checked"
            leak-check       = true

            [workspace]
            members = ["crates/*", "tools/shader-tool"]
            exclude = ["experiments"]
            """);

        var package = manifest.Package!;
        Assert.Equal("render-engine", package.Name);
        Assert.Equal(SemVersion.Parse("0.4.2"), package.Version);
        Assert.Equal("2027", package.Edition);
        Assert.Equal(["A. Person"], package.Authors);
        Assert.Equal("src/lib.sb", manifest.LibPath);
        Assert.Equal([new BinTarget("render", "src/main.sb")], manifest.Bins);

        var image = manifest.Dependencies.Single(static d => d.Name == "image");
        Assert.Equal("0.6", image.Version!.Text);
        Assert.False(image.DefaultFeatures);
        Assert.Equal(["png", "jpeg"], image.Features);
        Assert.Equal("../platform", manifest.Dependencies.Single(static d => d.Name == "platform").Path);
        Assert.True(manifest.Dependencies.Single(static d => d.Name == "vulkan-sys").Optional);
        Assert.Equal("bench-harness", Assert.Single(manifest.DevDependencies).Name);
        Assert.Equal("shader-compiler", Assert.Single(manifest.BuildDependencies).Name);
        Assert.Equal(["dep:vulkan-sys"], manifest.Features["vulkan"]);

        Assert.True(manifest.Release.Optimise);
        Assert.False(manifest.Release.Exclusivity);
        Assert.False(manifest.Release.ArcChecks);
        Assert.Equal("fat", manifest.Release.Recorded["lto"]);
        Assert.Equal("1", manifest.Release.Recorded["codegen-units"]);
        Assert.False(manifest.Dev.Optimise);
        Assert.True(manifest.Dev.Debug);
        Assert.True(manifest.Dev.LeakCheck);

        Assert.Equal(["crates/*", "tools/shader-tool"], manifest.Workspace!.Members);
        Assert.Equal(["experiments"], manifest.Workspace.Exclude);
    }

    [Fact]
    public void ProfilesDefaultToDebugAndOptimised() {
        var manifest = Parse("[package]\nname = \"a\"\nversion = \"1.0.0\"\n");
        Assert.Equal(Profile.Dev, manifest.Dev);
        Assert.True(manifest.Release.Optimise);
        Assert.False(manifest.Release.Debug);
        Assert.False(manifest.Release.Exclusivity);
        Assert.True(manifest.Dev.Exclusivity);
    }

    [Fact]
    public void EveryErrorNamesTheFileLineAndKey() {
        var errors = Errors("""
            [package]
            name = "Bad Name"
            version = "1.0"
            edition = "twenty"

            [dependencies]
            good = "1.0"
            bad = "not a version"
            weird = 3

            [features]
            on = ["dep:good", "nothing"]

            [profile.release]
            opt-level = 9
            exclusivity = "sometimes"

            [profile.fast]
            opt-level = 3
            """);

        Assert.Contains(errors, static e => e is { File: "pkg/sable.toml", Line: 2, Key: "package.name" } && e.Message.Contains("'Bad Name' is not a package name", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e is { Line: 3, Key: "package.version" } && e.Message.Contains("not a semantic version", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e is { Line: 4, Key: "package.edition" });
        Assert.Contains(errors, static e => e is { Line: 8, Key: "dependencies.bad" });
        Assert.Contains(errors, static e => e is { Line: 9, Key: "dependencies.weird" } && e.Message.Contains("not an integer", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e is { Line: 12, Key: "features.on" } && e.Message.Contains("'dep:good' names a dependency that is not optional", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e is { Line: 12, Key: "features.on" } && e.Message.Contains("'nothing' is neither a feature nor an optional dependency", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e is { Line: 15, Key: "profile.release.opt-level" });
        Assert.Contains(errors, static e => e is { Line: 16, Key: "profile.release.exclusivity" });
        Assert.Contains(errors, static e => e is { Line: 18, Key: "profile.fast" });

        var exception = Assert.Throws<ManifestException>(() => Parse("[package]\nname = \"Bad Name\"\nversion = \"1.0.0\"\n"));
        Assert.StartsWith("error: pkg/sable.toml:2: package.name: ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASyntaxErrorIsAManifestErrorToo() {
        var error = Assert.Single(Errors("[package]\nname = \"a\nversion = \"1.0.0\"\n"));
        Assert.Equal(2, error.Line);
        Assert.Contains("not closed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageNeedsANameAndAVersion() {
        var errors = Errors("[package]\nedition = \"2027\"\n");
        Assert.Contains(errors, static e => e.Key == "package.name");
        Assert.Contains(errors, static e => e.Key == "package.version");
        Assert.Contains(Errors("[dependencies]\n"), static e => e.Message.Contains("needs a [package] table", StringComparison.Ordinal));
    }

    [Fact]
    public void ADependencyNeedsSomewhereToComeFrom() {
        Assert.Contains(Errors("[package]\nname = \"a\"\nversion = \"1.0.0\"\n[dependencies]\nb = { features = [\"x\"] }\n"),
            static e => e is { Line: 5, Key: "dependencies.b" } && e.Message.Contains("needs a version, a path", StringComparison.Ordinal));
        Assert.Contains(Errors("[package]\nname = \"a\"\nversion = \"1.0.0\"\n[dependencies]\nb = { version = \"1\", colour = \"red\" }\n"),
            static e => e.Key == "dependencies.b.colour");
    }

    [Fact]
    public void UnknownKeysAreWarnedAboutNotRefused() {
        var manifest = Parse("[package]\nname = \"a\"\nversion = \"1.0.0\"\nflavour = \"mint\"\n[badges]\nx = 1\n[profile.dev]\nturbo = true\n");
        Assert.Contains(manifest.Warnings, static w => w is { Line: 4, Key: "package.flavour" });
        Assert.Contains(manifest.Warnings, static w => w.Key == "badges");
        Assert.Contains(manifest.Warnings, static w => w.Key == "profile.dev.turbo");
    }

    /// <summary>
    ///     Every profile setting means what it says: each opt-level its own
    ///     clang level, and lto, codegen-units, strip, rpath, arc-checks,
    ///     debug-assertions and incremental acted on. What cannot be acted on —
    ///     unwinding, a count of codegen units — is warned about.
    /// </summary>
    [Fact]
    public void ProfileSettingsReachTheNativeBuild() {
        var manifest = Parse("""
            [package]
            name = "a"
            version = "1.0.0"

            [profile.release]
            opt-level = "z"
            debug = true
            lto = "thin"
            codegen-units = 1
            strip = "debuginfo"
            rpath = true
            panic = "unwind"
            arc-checks = true
            debug-assertions = true

            [profile.dev]
            opt-level = 1
            lto = false
            strip = true
            codegen-units = 16
            incremental = false
            arc-checks = false
            """);

        Assert.Equal(
            new NativeOptions { OptLevel = "z", DebugInfo = true, Lto = LtoMode.Thin, Strip = StripMode.DebugInfo, Rpath = true },
            manifest.Release.Native
        );
        Assert.Equal(1, manifest.Release.CodegenUnits);
        Assert.Equal(new NativeOptions { OptLevel = "1", DebugInfo = true, Lto = LtoMode.Off, Strip = StripMode.Symbols }, manifest.Dev.Native);
        Assert.True(manifest.Dev.Optimise);
        Assert.Contains(manifest.Warnings, static w => w is { Line: 12, Key: "profile.release.panic" });
        Assert.True(manifest.Release.ArcChecks);
        Assert.True(manifest.Release.DebugAssertions);
        Assert.False(manifest.Dev.ArcChecks);
        Assert.False(manifest.Dev.DebugAssertions);
        Assert.False(manifest.Dev.Incremental);
        Assert.True(Profile.Dev.DebugAssertions);
        Assert.False(Profile.Release.DebugAssertions);
        Assert.Equal(
            ["profile.release.panic", "profile.dev.codegen-units"],
            manifest.Warnings.Where(static w => w.Key.StartsWith("profile.", StringComparison.Ordinal)).Select(static w => w.Key)
        );

        foreach (var (level, expected) in new[] { ("0", "0"), ("2", "2"), ("3", "3"), ("\"s\"", "s") }) {
            Assert.Equal(expected, Parse($"[package]\nname = \"a\"\nversion = \"1.0.0\"\n[profile.release]\nopt-level = {level}\n").Release.OptLevel);
        }

        var errors = Errors("[package]\nname = \"a\"\nversion = \"1.0.0\"\n[profile.release]\nlto = \"medium\"\ncodegen-units = 0\nstrip = 3\npanic = \"retry\"\nrpath = \"yes\"\n");
        Assert.Equal(
            ["profile.release.lto", "profile.release.codegen-units", "profile.release.strip", "profile.release.panic", "profile.release.rpath"],
            errors.Select(static e => e.Key)
        );
    }

    [Fact]
    public void SettingsNothingActsOnAreRefusedOrWarnedAbout() {
        const string Package = "[package]\nname = \"a\"\nversion = \"1.0.0\"\n";

        // Refused: building without them would build something else than asked.
        Assert.Contains(Errors(Package + "[dependencies]\nb = { version = \"1\", package = \"c\" }\n"),
            static e => e.Key == "dependencies.b.package" && e.Message.Contains("cannot be renamed", StringComparison.Ordinal));
        Assert.Contains(Errors(Package + "[dependencies]\nb = { version = \"1\", registry = \"other\" }\n"),
            static e => e.Key == "dependencies.b.registry");
        // Taken from a workspace there is none of.
        Assert.Contains(Errors(Package + "[dependencies]\nb = { workspace = true }\n"),
            static e => e.Key == "dependencies.b" && e.Message.Contains("no workspace above", StringComparison.Ordinal));
        Assert.Contains(Errors("[package]\nname = \"a\"\nversion.workspace = true\n"),
            static e => e.Key == "package.version" && e.Message.Contains("no workspace above", StringComparison.Ordinal));
        Assert.Contains(Errors(Package + "[target.'cfg(unix)'.dependencies]\nb = \"1\"\n"),
            static e => e.Key == "target" && e.Message.Contains("when", StringComparison.Ordinal));

        // Not part of the format: [reflect] is said in code. A runtime profile is one of three.
        Assert.Contains(Errors(Package + "[reflect]\ninclude = [\"Scene\"]\n"),
            static e => e.Key == "reflect" && e.Message.Contains("@reflectable", StringComparison.Ordinal));
        Assert.Contains(Errors(Package + "[targets.wasm32-wasi]\nruntime = \"tiny\"\n"),
            static e => e.Key == "targets.wasm32-wasi.runtime" && e.Message.Contains("\"minimal\"", StringComparison.Ordinal));
        Assert.Contains(Errors(Package + "[targets.wasm32-wasi]\nruntime = 1\n"),
            static e => e.Key == "targets.wasm32-wasi.runtime");
        Assert.Equal(RuntimeProfile.Minimal, Parse(Package + "[targets.wasm32-wasi]\nruntime = \"minimal\"\n").TargetSettingsFor(NativeTarget.Of("wasm32-wasi"))!.Runtime);

        // Warned about: accepted, with a word that nothing uses them.
        var manifest = Parse(Package + "categories = [\"x\"]\nhomepage = \"https://a\"\n[build-dependencies]\nb = \"1\"\n[targets.wasm32-wasi]\nx = 1\n" +
            "[registry]\npath = \"r\"\nurl = \"u\"\n[generators]\ninputs = []\nmore = 1\n[profile.dev]\ndebug = \"line-tables-only\"\n");
        Assert.Contains(manifest.Warnings, static w => w.Key == "build-dependencies");
        Assert.Contains(manifest.Warnings, static w => w.Key == "targets.wasm32-wasi.x");
        Assert.Contains(manifest.Warnings, static w => w.Key == "package.categories" && w.Message.Contains("keywords", StringComparison.Ordinal));
        Assert.Contains(manifest.Warnings, static w => w.Key == "package.homepage");
        Assert.Contains(manifest.Warnings, static w => w.Key == "registry.url");
        Assert.Contains(manifest.Warnings, static w => w.Key == "generators.more");
        Assert.Contains(manifest.Warnings, static w => w.Key == "profile.dev.debug");
        Assert.Contains(Parse(Package + "[build]\nscript = \"build.sb\"\nallow-everything = true\n").Warnings, static w => w.Key == "build.allow-everything");
        Assert.Contains(Parse("[workspace]\nmembers = [\"a\"]\n[workspace.metadata]\nb = 1\n").Warnings,
            static w => w.Key == "workspace.metadata");
        Assert.Contains(Parse("[workspace]\nmembers = [\"a\"]\nresolver = \"2\"\n").Warnings,
            static w => w.Key == "workspace.resolver" && w.Message.Contains("one resolver", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePackagesPublishingSettingsAreReadAndChecked() {
        const string Package = "[package]\nname = \"a\"\nversion = \"1.0.0\"\n";
        var package = Parse(Package + "readme = \"README.md\"\nkeywords = [\"render\", \"gpu\"]\ninclude = [\"src/**\", \"*.md\"]\nexclude = [\"*.png\"]\npublish = false\n").Package!;
        Assert.Equal("README.md", package.Readme);
        Assert.Equal(["render", "gpu"], package.Keywords);
        Assert.Equal(["src/**", "*.md"], package.Include);
        Assert.Equal(["*.png"], package.Exclude);
        Assert.False(package.Publish);
        Assert.True(Parse(Package).Package!.Publish);

        Assert.Contains(Errors(Package + "keywords = [\"Not A Keyword\"]\n"), static e => e.Key == "package.keywords");
        Assert.Contains(Errors(Package + "keywords = [\"a\", \"b\", \"c\", \"d\", \"e\", \"f\"]\n"),
            static e => e.Key == "package.keywords" && e.Message.Contains("at most 5", StringComparison.Ordinal));
        Assert.Contains(Errors(Package + "include = [\"../outside\"]\n"), static e => e.Key == "package.include");
        Assert.Contains(Errors(Package + "exclude = [\"\"]\n"), static e => e.Key == "package.exclude");
        Assert.Contains(Errors(Package + "readme = \"../README.md\"\n"), static e => e.Key == "package.readme");
        Assert.Contains(Errors(Package + "publish = \"no\"\n"), static e => e.Key == "package.publish");
    }

    [Theory]
    [InlineData("*.png", "logo.png", true)]
    [InlineData("*.png", "assets/logo.png", true)]
    [InlineData("assets/*.png", "assets/logo.png", true)]
    [InlineData("assets/*.png", "assets/big/logo.png", false)]
    [InlineData("assets/**/*.png", "assets/big/logo.png", true)]
    [InlineData("assets/**/*.png", "assets/logo.png", true)]
    [InlineData("/README.md", "README.md", true)]
    [InlineData("/README.md", "docs/README.md", false)]
    [InlineData("tests", "tests/a/b.sb", true)]
    [InlineData("tests/", "tests/b.sb", true)]
    [InlineData("src/**", "src/a/b.sb", true)]
    [InlineData("src/**", "srcs/a.sb", false)]
    [InlineData("?.sb", "a.sb", true)]
    [InlineData("?.sb", "ab.sb", false)]
    public void AnIncludeOrExcludePatternMatchesFilesAndDirectories(string pattern, string path, bool matches) =>
        Assert.Equal(matches, FileGlob.Matches(pattern, path));

    [Fact]
    public void APackageMayHaveProfilesOfItsOwn() {
        var manifest = Parse("""
            [package]
            name = "a"
            version = "1.0.0"

            [profile.release]
            lto = "thin"

            [profile.profiling]
            inherits = "release"
            debug = true

            [profile.fast-dev]
            inherits = "dev"
            opt-level = 1
            """);
        var profiling = manifest.ProfileNamed("profiling");
        Assert.Equal("profiling", profiling.Name);
        Assert.Equal("3", profiling.OptLevel);
        Assert.True(profiling.Debug);
        // As the manifest has release, not as release is by default.
        Assert.Equal(LtoMode.Thin, profiling.Lto);
        Assert.Equal("thin", profiling.Recorded["lto"]);
        var fast = manifest.ProfileNamed("fast-dev");
        Assert.Equal("1", fast.OptLevel);
        Assert.True(fast.Exclusivity);
        Assert.Same(manifest.Release, manifest.ProfileNamed("release"));
        Assert.Contains("there is no profile 'nope'", Assert.Throws<PackageException>(() => manifest.ProfileNamed("nope")).Message, StringComparison.Ordinal);

        const string Package = "[package]\nname = \"a\"\nversion = \"1.0.0\"\n";
        Assert.Contains(Errors(Package + "[profile.bench-fast]\nopt-level = 3\n"),
            static e => e.Key == "profile.bench-fast" && e.Message.Contains("inherits", StringComparison.Ordinal));
        Assert.Contains(Errors(Package + "[profile.x]\ninherits = \"x\"\n"), static e => e.Key == "profile.x.inherits");
        Assert.Contains(Errors(Package + "[profile.release]\ninherits = \"dev\"\n"), static e => e.Key == "profile.release.inherits");
        Assert.Contains(Errors(Package + "[profile.Fast]\ninherits = \"dev\"\n"), static e => e.Key == "profile.Fast");
        Assert.Contains(Errors(Package + "[profile.doc]\ninherits = \"dev\"\n"), static e => e.Key == "profile.doc");
    }

    [Fact]
    public void AMemberTakesSettingsAndDependenciesFromItsWorkspace() {
        using var fixture = new PackageFixture();
        fixture.Write("ws/sable.toml", """
            [workspace]
            members = ["crates/*"]

            [workspace.package]
            version = "0.4.2"
            edition = "2027"
            authors = ["A. Person"]

            [workspace.dependencies]
            image = { version = "0.6", features = ["png"] }
            shared = { path = "libs/shared" }
            """);
        fixture.Write("ws/crates/app/sable.toml", """
            [package]
            name = "app"
            version.workspace = true
            edition = { workspace = true }
            authors.workspace = true

            [dependencies]
            image = { workspace = true, features = ["jpeg"], optional = true }
            shared.workspace = true
            """);

        var manifest = ManifestFiles.Load(Path.Combine(fixture.Root, "ws", "crates", "app"));
        Assert.True(manifest.InheritsFromWorkspace);
        Assert.Equal("0.4.2", manifest.Package!.Version.ToString());
        Assert.Equal("2027", manifest.Package.Edition);
        Assert.Equal(["A. Person"], manifest.Package.Authors);

        var image = manifest.Dependencies.Single(static d => d.Name == "image");
        Assert.Equal(["png", "jpeg"], image.Features);
        Assert.True(image.Optional);
        Assert.Equal("0.6", image.Version!.ToString());

        // A path is the workspace's, written from the member.
        Assert.Equal("../../libs/shared", manifest.Dependencies.Single(static d => d.Name == "shared").Path);

        // A published package has no workspace to take them from.
        var refused = Assert.Throws<PackageException>(() => PackageOperations.Published(Path.Combine(fixture.Root, "ws", "crates", "app")));
        Assert.Contains("write them out", refused.Message, StringComparison.Ordinal);

        // What the workspace does not have is said, with where it looked.
        fixture.Write("ws/crates/other/sable.toml", "[package]\nname = \"other\"\nversion = \"1.0.0\"\nlicense.workspace = true\n\n[dependencies]\nmissing = { workspace = true }\n");
        var errors = Assert.Throws<ManifestException>(() => ManifestFiles.Load(Path.Combine(fixture.Root, "ws", "crates", "other"))).Errors;
        Assert.Contains(errors, static e => e.Key == "package.license" && e.Message.Contains("no 'license' under [workspace.package]", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e.Key == "dependencies.missing" && e.Message.Contains("no 'missing' under [workspace.dependencies]", StringComparison.Ordinal));
    }

    [Fact]
    public void AWorkspacesSharedSettingsAreChecked() {
        var errors = Errors("[workspace]\nmembers = []\n[workspace.package]\nname = \"x\"\nversion = \"one\"\n[workspace.dependencies]\nb = { version = \"1\", optional = true }\n");
        Assert.Contains(errors, static e => e.Key == "workspace.package.name");
        Assert.Contains(errors, static e => e.Key == "workspace.package.version");
        Assert.Contains(errors, static e => e.Key == "workspace.dependencies.b");
    }

    [Fact]
    public void AVirtualWorkspaceHasNoPackage() {
        var manifest = Parse("[workspace]\nmembers = [\"a\", \"b\"]\n");
        Assert.Null(manifest.Package);
        Assert.Equal(["a", "b"], manifest.Workspace!.Members);
    }

    [Fact]
    public void BuildScriptsAreReadButNotRun() {
        var manifest = Parse("[package]\nname = \"a\"\nversion = \"1.0.0\"\n[build]\nscript = \"build.sb\"\nallow-exec = [\"pkg-config\"]\n");
        Assert.Equal("build.sb", manifest.Build!.Script);
        Assert.Equal(["pkg-config"], manifest.Build.AllowExec);
        Assert.False(manifest.Build.AllowNet);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("render-engine", true)]
    [InlineData("org/name_2", true)]
    [InlineData("Upper", false)]
    [InlineData("2fast", false)]
    [InlineData("a/b/c", false)]
    [InlineData("", false)]
    public void PackageNames(string name, bool valid) => Assert.Equal(valid, Manifest.IsValidPackageName(name));
}
