using System.Text.Json.Nodes;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The registry and the lock file: publishing, fetching, yanking,
///     signatures and checksums, offline and vendored builds, and a lock
///     file that is the same bytes every time.
/// </summary>
public sealed class RegistryTests {
    static Registry RegistryOf(PackageFixture fixture) => new(fixture.Environment.RegistryDirectory);

    static void WriteConsumer(PackageFixture fixture, string requirement, string directory = "app") =>
        fixture.Package(directory, "app", "1.0.0", $"[dependencies]\nnumbers = \"{requirement}\"\n",
            ("src/main.sb", "module App\n\nimport Sable.IO\nimport Numbers\n\nfn main() {\n    print(\"\\(numbersValue())\")\n}\n"));

    [Fact]
    public void ArchivesAreDeterministic() {
        var files = new Dictionary<string, byte[]> { ["src/lib.sb"] = "module A\n"u8.ToArray(), ["sable.toml"] = "[package]\n"u8.ToArray() };
        var first = Registry.Pack(files);
        var second = Registry.Pack(new Dictionary<string, byte[]>(files.Reverse()));
        Assert.Equal(first, second);
        Assert.Equal(Registry.Checksum(first), Registry.Checksum(second));
        Assert.Equal(files.Keys.Order(StringComparer.Ordinal), Registry.Unpack(first).Keys);
    }

    /// <summary>
    ///     An index that names a key twice is damaged like any other: it is
    ///     refused with a <see cref="RegistryException" />, not an
    ///     <see cref="ArgumentException" /> from deep in the JSON reader.
    ///     Found by the coverage-guided fuzzer (tools/fuzz/coverage.sh).
    /// </summary>
    [Theory]
    [InlineData("""{"owners":["a"],"owners":["b"],"versions":[]}""")]
    [InlineData("""{"versions":[{"dependencies":[],"dependencies":[]}]}""")]
    public void AnIndexWithAKeyTwiceIsDamaged(string json) {
        var refused = Assert.Throws<RegistryException>(() => PackageIndex.Parse("p", json));
        Assert.Contains("is damaged", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishThenDependAndBuild() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0", value: "10");
        fixture.PublishLibrary("numbers", "1.1.0", value: "11");
        fixture.PublishLibrary("numbers", "2.0.0", value: "20");
        WriteConsumer(fixture, "1");

        var graph = PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        var numbers = graph.Packages["numbers"];
        Assert.Equal(SemVersion.Parse("1.1.0"), numbers.Version);
        Assert.False(numbers.IsLocal);
        Assert.StartsWith(Path.Combine(fixture.Home, "cache"), numbers.Package.Directory, StringComparison.Ordinal);
        Assert.Equal(RegistryOf(fixture).Entry("numbers", SemVersion.Parse("1.1.0"))!.Checksum, graph.Lock.Find("numbers")!.Checksum);

        Assert.Equal(0, PackageBuilder.Check(fixture.Load("app"), fixture.Environment, new()));
        if (NativeBuild.IsAvailable()) {
            var binary = Assert.Single(PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new(Profile: "release"))!);
            Assert.Equal("11\n", PackageFixture.Execute(binary.Path).Output);
        }
    }

    [Fact]
    public void ThePublishedIndexIsSignedAndDescribesThePackage() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("base", "1.0.0");
        fixture.PublishLibrary("numbers", "1.0.0", "base = \"1\"");

        var entry = RegistryOf(fixture).Entry("numbers", SemVersion.Parse("1.0.0"))!;
        Assert.Equal("base", Assert.Single(entry.Dependencies).Name);
        Assert.Equal("1", entry.Dependencies[0].Requirement);
        Assert.Equal(RegistryOf(fixture).Owner("numbers"), entry.PublicKey);
        RegistryOf(fixture).Verify(entry);
        Assert.Contains("BEGIN PRIVATE KEY", File.ReadAllText(fixture.Environment.KeyPath), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Environment.KeyPath));
        }
    }

    [Fact]
    public void APublishedVersionNeverChanges() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0");
        var error = Assert.Throws<RegistryException>(() => fixture.Publish("published/numbers-1.0.0"));
        Assert.Contains("already published", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheOwnersKeyMayPublishOrYank() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0");
        var stranger = PackageEnvironment.At(Path.Combine(fixture.Root, "stranger")) with { RegistryDirectory = fixture.Environment.RegistryDirectory };
        fixture.Package("theirs", "numbers", "1.0.1", "", ("src/lib.sb", "module Numbers\n"));
        var workspace = fixture.Load("theirs");
        var error = Assert.Throws<RegistryException>(() => PackageOperations.Publish(workspace, stranger, workspace.Members[0], false, true));
        Assert.Contains("owned by another key", error.Message, StringComparison.Ordinal);
        error = Assert.Throws<RegistryException>(() => PackageOperations.Yank(null, stranger, "numbers", SemVersion.Parse("1.0.0"), false));
        Assert.Contains("only its owner may yank it", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADryRunShowsWhatWouldBeUploadedAndUploadsNothing() {
        using var fixture = new PackageFixture();
        fixture.Package("lib", "numbers", "1.0.0", "", ("src/lib.sb", "module Numbers\n"), ("README.md", "# numbers"), ("target/dev/junk", "x"), (".hidden", "x"));
        var workspace = fixture.Load("lib");
        var plan = PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], dryRun: true, allowDirty: true);

        Assert.Equal(["README.md", "sable.toml", "src/lib.sb"], plan.Files.Keys);
        var shown = fixture.Output.ToString();
        Assert.Contains("src/lib.sb", shown, StringComparison.Ordinal);
        Assert.Contains(plan.Entry.Checksum, shown, StringComparison.Ordinal);
        Assert.Contains("Dry run: nothing was uploaded", shown, StringComparison.Ordinal);
        Assert.Empty(RegistryOf(fixture).Entries("numbers"));
        Assert.False(File.Exists(fixture.Environment.KeyPath));
    }

    [Fact]
    public void IncludeExcludeReadmeAndKeywordsSayWhatIsPublished() {
        using var fixture = new PackageFixture();
        fixture.Package("lib", "numbers", "1.0.0", "readme = \"docs/README.md\"\nkeywords = [\"maths\", \"bignum\"]\ninclude = [\"src/**\"]\nexclude = [\"*.png\"]\n",
            ("src/lib.sb", "module Numbers\n"), ("src/logo.png", "png"), ("docs/README.md", "# numbers"), ("docs/guide.md", "guide"), ("notes.txt", "x"));
        var workspace = fixture.Load("lib");
        var plan = PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], dryRun: true, allowDirty: true);

        // The manifest and the readme whatever the patterns say.
        Assert.Equal(["docs/README.md", "sable.toml", "src/lib.sb"], plan.Files.Keys);
        Assert.Equal(["maths", "bignum"], plan.Entry.Keywords);
        Assert.Equal(["maths", "bignum"], IndexEntry.Read("numbers", plan.Entry.Write()).Keywords);
        Assert.Equal(2, Registry.SearchRank("numbers", null, "bignum", plan.Entry.Keywords));
        Assert.Null(Registry.SearchRank("numbers", null, "bignum"));

        // A readme that is not there.
        fixture.Package("missing", "missing", "1.0.0", "readme = \"README.md\"\n", ("src/lib.sb", "module Missing\n"));
        workspace = fixture.Load("missing");
        var error = Assert.Throws<PackageException>(() => PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], true, true));
        Assert.Contains("no such file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageThatSaysPublishFalseIsNotPublished() {
        using var fixture = new PackageFixture();
        fixture.Package("lib", "internal", "1.0.0", "publish = false\n", ("src/lib.sb", "module Internal\n"));
        var workspace = fixture.Load("lib");
        var error = Assert.Throws<PackageException>(() => PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], true, true));
        Assert.Contains("'publish = false'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishRefusesWhatCannotBeUsedFromTheRegistry() {
        using var fixture = new PackageFixture();
        fixture.Package("util", "util", "1.0.0", "", ("src/lib.sb", "module Util\n"));
        fixture.Package("lib", "numbers", "1.0.0", "[dependencies]\nutil = { path = \"../util\" }\n", ("src/lib.sb", "module Numbers\n"));
        var workspace = fixture.Load("lib");
        var error = Assert.Throws<PackageException>(() => PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], true, true));
        Assert.Contains("has only a path", error.Message, StringComparison.Ordinal);

        // And what does not type-check.
        fixture.Package("broken", "broken", "1.0.0", "", ("src/lib.sb", "module Broken\nfn f() -> Int { return true }\n"));
        workspace = fixture.Load("broken");
        error = Assert.Throws<PackageException>(() => PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], true, true));
        Assert.Contains("has errors", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLockFileIsTheSameBytesEveryTime() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("zeta", "1.0.0");
        fixture.PublishLibrary("alpha", "1.0.0", "zeta = \"1\"");
        fixture.PublishLibrary("numbers", "1.0.0", "alpha = \"1\"\nzeta = \"1\"");
        WriteConsumer(fixture, "1");
        var workspace = fixture.Load("app");

        PackageGraph.Resolve(workspace, fixture.Environment);
        var first = File.ReadAllText(workspace.LockPath);
        PackageGraph.Resolve(workspace, fixture.Environment);
        Assert.Equal(first, File.ReadAllText(workspace.LockPath));

        File.Delete(workspace.LockPath);
        PackageGraph.Resolve(workspace, fixture.Environment);
        Assert.Equal(first, File.ReadAllText(workspace.LockPath));

        // Sorted, and each registry package with its checksum.
        var locked = LockFile.Parse(first);
        Assert.Equal(["alpha", "app", "numbers", "zeta"], locked.Packages.Select(static p => p.Name));
        Assert.All(locked.Packages.Where(static p => p.IsRegistry), static p => Assert.StartsWith("sha256:", p.Checksum, StringComparison.Ordinal));
        Assert.Equal(["alpha 1.0.0", "zeta 1.0.0"], locked.Find("numbers")!.Dependencies);
        Assert.Equal(first, locked.Write());
    }

    [Fact]
    public void TheLockIsHonouredUntilUpdate() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0", value: "10");
        WriteConsumer(fixture, "1");
        var workspace = fixture.Load("app");
        PackageGraph.Resolve(workspace, fixture.Environment);

        fixture.PublishLibrary("numbers", "1.1.0", value: "11");
        Assert.Equal(SemVersion.Parse("1.0.0"), PackageGraph.Resolve(workspace, fixture.Environment).Packages["numbers"].Version);

        var changes = PackageOperations.Update(workspace, fixture.Environment, ["numbers"]);
        Assert.Equal(["    Updating numbers v1.0.0 -> v1.1.0"], changes);
        Assert.Equal(SemVersion.Parse("1.1.0"), LockFile.Load(workspace.LockPath)!.Find("numbers")!.Version);
        Assert.Empty(PackageOperations.Update(workspace, fixture.Environment, []));

        // A manifest that no longer fits the lock file re-resolves; --locked refuses to.
        WriteConsumer(fixture, ">=1.0, <1.1");
        Assert.Throws<PackageException>(() => PackageGraph.Resolve(fixture.Load("app"), fixture.Environment, new(Locked: true)));
        Assert.Equal(SemVersion.Parse("1.0.0"), PackageGraph.Resolve(fixture.Load("app"), fixture.Environment).Packages["numbers"].Version);
    }

    [Fact]
    public void AYankedVersionIsNotChosenAnewButALockKeepsIt() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0");
        fixture.PublishLibrary("numbers", "1.1.0");
        WriteConsumer(fixture, "1");
        var workspace = fixture.Load("app");
        Assert.Equal(SemVersion.Parse("1.1.0"), PackageGraph.Resolve(workspace, fixture.Environment).Packages["numbers"].Version);

        PackageOperations.Yank(null, fixture.Environment, "numbers", SemVersion.Parse("1.1.0"), undo: false);
        Assert.True(RegistryOf(fixture).Entry("numbers", SemVersion.Parse("1.1.0"))!.Yanked);

        // The lock file has it: kept.
        Assert.Equal(SemVersion.Parse("1.1.0"), PackageGraph.Resolve(workspace, fixture.Environment).Packages["numbers"].Version);

        // A new resolution does not choose it.
        File.Delete(workspace.LockPath);
        Assert.Equal(SemVersion.Parse("1.0.0"), PackageGraph.Resolve(workspace, fixture.Environment).Packages["numbers"].Version);

        PackageOperations.Yank(null, fixture.Environment, "numbers", SemVersion.Parse("1.1.0"), undo: true);
        Assert.Equal(SemVersion.Parse("1.1.0"), PackageGraph.Resolve(workspace, fixture.Environment, new(Update: new HashSet<string>())).Packages["numbers"].Version);
    }

    [Fact]
    public void ATamperedArchiveIsRefused() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0");
        var entry = RegistryOf(fixture).Entry("numbers", SemVersion.Parse("1.0.0"))!;
        var blob = Path.Combine(fixture.Environment.RegistryDirectory, "blobs", entry.Checksum["sha256:".Length..] + ".tar");
        var bytes = File.ReadAllBytes(blob);
        bytes[600] ^= 0x20;
        File.WriteAllBytes(blob, bytes);

        WriteConsumer(fixture, "1");
        var error = Assert.Throws<RegistryException>(() => PackageGraph.Resolve(fixture.Load("app"), fixture.Environment));
        Assert.Contains("does not match its checksum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATamperedIndexEntryFailsItsSignature() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0");
        var index = Path.Combine(fixture.Environment.RegistryDirectory, "index", "numbers.json");
        var json = JsonNode.Parse(File.ReadAllText(index))!;
        json["versions"]![0]!["dependencies"] = new JsonArray(new JsonObject {
            ["name"] = "evil", ["req"] = "1", ["kind"] = "normal", ["optional"] = false, ["default-features"] = true, ["features"] = new JsonArray(),
        });
        File.WriteAllText(index, json.ToJsonString());

        var error = Assert.Throws<RegistryException>(() => RegistryOf(fixture).Verify(RegistryOf(fixture).Entry("numbers", SemVersion.Parse("1.0.0"))!));
        Assert.Contains("does not verify", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntrySignedByAnotherKeyIsRefused() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0");
        var index = Path.Combine(fixture.Environment.RegistryDirectory, "index", "numbers.json");
        var json = JsonNode.Parse(File.ReadAllText(index))!;

        // Re-signed, consistently, by someone else: still not the owner's.
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var entry = RegistryOf(fixture).Entry("numbers", SemVersion.Parse("1.0.0"))! with { PublicKey = Registry.PublicKeyOf(other) };
        json["versions"]![0]!["key"] = entry.PublicKey;
        json["versions"]![0]!["signature"] = Convert.ToBase64String(other.SignData(System.Text.Encoding.UTF8.GetBytes(entry.SignedText()), System.Security.Cryptography.HashAlgorithmName.SHA256));
        File.WriteAllText(index, json.ToJsonString());

        var error = Assert.Throws<RegistryException>(() => RegistryOf(fixture).Download(RegistryOf(fixture).Entry("numbers", SemVersion.Parse("1.0.0"))!));
        Assert.Contains("not signed by the package's owner", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockedChecksumThatDiffersFromTheRegistryIsRefused() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0");
        WriteConsumer(fixture, "1");
        var workspace = fixture.Load("app");
        PackageGraph.Resolve(workspace, fixture.Environment);

        File.WriteAllText(workspace.LockPath, File.ReadAllText(workspace.LockPath).Replace("sha256:", "sha256:0", StringComparison.Ordinal));
        Directory.Delete(fixture.Environment.CacheDirectory, true);
        var error = Assert.Throws<RegistryException>(() => PackageGraph.Resolve(workspace, fixture.Environment));
        Assert.Contains("tampered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnceTheCacheHasItTheRegistryIsNotNeeded() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0");
        WriteConsumer(fixture, "1");
        var workspace = fixture.Load("app");
        PackageGraph.Resolve(workspace, fixture.Environment);

        Directory.Move(fixture.Environment.RegistryDirectory, fixture.Environment.RegistryDirectory + "-away");
        Assert.Equal(0, PackageBuilder.Check(workspace, fixture.Environment, new()));

        // Without the lock file, an offline resolution has nothing to choose from, and says so.
        File.Delete(workspace.LockPath);
        var error = Assert.Throws<ResolutionException>(() => PackageGraph.Resolve(workspace, fixture.Environment));
        Assert.Contains("cannot be read", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVendoredBuildNeedsNeitherRegistryNorCache() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("base", "1.0.0");
        fixture.PublishLibrary("numbers", "1.0.0", "base = \"1\"", "3");
        WriteConsumer(fixture, "1");
        var workspace = fixture.Load("app");

        Assert.Equal(["base v1.0.0", "numbers v1.0.0"], PackageOperations.Vendor(workspace, fixture.Environment));
        Assert.True(File.Exists(Path.Combine(workspace.VendorDirectory, "numbers-1.0.0", "src", "lib.sb")));

        Directory.Delete(fixture.Environment.RegistryDirectory, true);
        Directory.Delete(fixture.Environment.CacheDirectory, true);
        var graph = PackageGraph.Resolve(workspace, fixture.Environment);
        Assert.StartsWith(workspace.VendorDirectory, graph.Packages["numbers"].Package.Directory, StringComparison.Ordinal);
        Assert.Equal(0, PackageBuilder.Check(workspace, fixture.Environment, new()));
        if (NativeBuild.IsAvailable()) {
            var binary = Assert.Single(PackageBuilder.Build(workspace, fixture.Environment, new())!);
            Assert.Equal("3\n", PackageFixture.Execute(binary.Path).Output);
        }
    }

    [Fact]
    public void AConflictInTheRegistryIsExplained() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("color-space", "1.8.0");
        fixture.PublishLibrary("color-space", "2.0.0");
        fixture.PublishLibrary("image", "0.6.0", "color-space = \"2.0\"");
        fixture.PublishLibrary("platform", "1.4.0", "color-space = \"1.8\"");
        fixture.Package("engine", "render-engine", "0.4.2", "[dependencies]\nimage = \"0.6\"\nplatform = \"1.4\"\n", ("src/lib.sb", "module Engine\n"));

        var error = Assert.Throws<ResolutionException>(() => PackageGraph.Resolve(fixture.Load("engine"), fixture.Environment));
        Assert.StartsWith("error: cannot resolve dependencies", error.Message, StringComparison.Ordinal);
        foreach (var expected in new[] { "image depends on color-space ^2.0.0", "platform depends on color-space ^1.8.0", "render-engine depends on both", "one major version" }) {
            Assert.True(error.Message.Contains(expected, StringComparison.Ordinal), error.Message);
        }
    }

    [Fact]
    public void RegistryFeaturesAreResolved() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("png", "1.0.0");
        fixture.Package("published/image", "image", "0.6.0", "[dependencies]\npng = { version = \"1\", optional = true }\n[features]\ndefault = []\npng = [\"dep:png\"]\n",
            ("src/lib.sb", "module Image\n"));
        fixture.Publish("published/image");

        fixture.Package("app", "app", "1.0.0", "[dependencies]\nimage = { version = \"0.6\", features = [\"png\"] }\n", ("src/main.sb", "module App\nfn main() {}\n"));
        var graph = PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.Contains("png", graph.Packages["image"].Features);
        Assert.Equal(["png"], graph.Packages["image"].Dependencies);

        fixture.Package("app", "app", "1.0.0", "[dependencies]\nimage = \"0.6\"\n", ("src/main.sb", "module App\nfn main() {}\n"));
        File.Delete(fixture.Load("app").LockPath);
        graph = PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.False(graph.Packages.ContainsKey("png"));
    }
}
