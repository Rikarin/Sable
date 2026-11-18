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

            [reflect]
            include = ["Scene"]

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
        Assert.Contains(Errors("[package]\nname = \"a\"\nversion = \"1.0.0\"\n[dependencies]\nb = { git = \"https://example.com/b\" }\n"),
            static e => e.Key == "dependencies.b.git");
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
    ///     clang level, and lto, codegen-units, strip and rpath acted on. What
    ///     cannot be acted on — unwinding, ARC checks — is warned about.
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

            [profile.dev]
            opt-level = 1
            lto = false
            strip = true
            """);

        Assert.Equal(
            new NativeOptions { OptLevel = "z", DebugInfo = true, Lto = LtoMode.Thin, Strip = StripMode.DebugInfo, Rpath = true },
            manifest.Release.Native
        );
        Assert.Equal(1, manifest.Release.CodegenUnits);
        Assert.Equal(new NativeOptions { OptLevel = "1", DebugInfo = true, Lto = LtoMode.Off, Strip = StripMode.Symbols }, manifest.Dev.Native);
        Assert.True(manifest.Dev.Optimise);
        Assert.Contains(manifest.Warnings, static w => w is { Line: 12, Key: "profile.release.panic" });
        Assert.Contains(manifest.Warnings, static w => w is { Line: 13, Key: "profile.release.arc-checks" });

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
        Assert.Contains(Errors(Package + "[dependencies]\nb = { workspace = true }\n"),
            static e => e.Key == "dependencies.b.workspace" && e.Message.Contains("not supported yet", StringComparison.Ordinal));
        Assert.Contains(Errors("[package]\nname = \"a\"\nversion.workspace = true\n"),
            static e => e.Key == "package.version" && e.Message.Contains("inheriting", StringComparison.Ordinal));
        Assert.Contains(Errors(Package + "[target.'cfg(unix)'.dependencies]\nb = \"1\"\n"),
            static e => e.Key == "target" && e.Message.Contains("when", StringComparison.Ordinal));

        // Warned about: accepted, with a word that nothing uses them yet.
        var manifest = Parse(Package + "[build-dependencies]\nb = \"1\"\n[reflect]\ninclude = [\"Scene\"]\n[targets.wasm]\nx = 1\n");
        Assert.Contains(manifest.Warnings, static w => w.Key == "build-dependencies");
        Assert.Contains(manifest.Warnings, static w => w.Key == "reflect");
        Assert.Contains(manifest.Warnings, static w => w.Key == "targets");
        Assert.Contains(Parse("[workspace]\nmembers = [\"a\"]\n[workspace.dependencies]\nb = \"1\"\n").Warnings,
            static w => w.Key == "workspace.dependencies" && w.Message.Contains("inherit", StringComparison.Ordinal));
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
