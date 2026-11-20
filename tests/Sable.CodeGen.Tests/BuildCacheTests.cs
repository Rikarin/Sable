using System.Text.RegularExpressions;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The build cache under <c>$SABLE_HOME/cache</c> (#182): entries keyed by
///     everything that makes them, the compiler's own build among it, written
///     whole for builds running at once, shared by every project, and
///     collected by <c>sable cache</c>.
/// </summary>
public sealed class BuildCacheTests {
    static readonly bool CanBuild = NativeBuild.IsAvailable();

    static CacheKey KeyFor(BuildCache cache, string source = "source") =>
        cache.Key("library", "sable-std", "0.5.0", CacheKey.Checksum(source), "x86_64-unknown-linux-gnu", ["opt=0", "debug=True"]);

    static NativeBuildResult Make(string directory, string text = "archive") {
        File.WriteAllText(Path.Combine(directory, "libsable_std.a"), text);
        return new(true, "");
    }

    [Fact]
    public void TheCompilerIdentityIsAHashOfEveryPartOfTheCompiler() {
        (string, byte[])[] parts = [("version", "0.5.0"u8.ToArray()), ("sable", [1, 2, 3]), ("sable:lib/Sable.IO/File.sb", "fn a()"u8.ToArray()), ("Sable.CodeGen:Sable.CodeGen.runtime/memory.c", "int x;"u8.ToArray())];
        var identity = CompilerIdentity.Of(parts);
        Assert.Equal(identity, CompilerIdentity.Of([..parts]));

        // An assembly rebuilt, a file of lib/ or of the runtime changed, the
        // same version number: each is another compiler.
        for (var i = 0; i < parts.Length; i++) {
            var changed = parts.ToArray();
            changed[i] = (parts[i].Item1, [..parts[i].Item2, 0]);
            Assert.NotEqual(identity, CompilerIdentity.Of(changed));
        }

        Assert.Matches("^[0-9a-f]{64}$", CompilerIdentity.Current);
        Assert.Equal(CompilerIdentity.Current, new BuildCache(Path.GetTempPath()).Identity);
    }

    /// <summary>
    ///     A rebuilt compiler — whose identity is another, whatever its version
    ///     says — finds none of the entries an earlier build of it wrote, makes
    ///     its own, and prunes the earlier one's.
    /// </summary>
    [Fact]
    public void ARebuiltCompilerIgnoresEveryEntryAnEarlierBuildWrote() {
        var root = Directory.CreateTempSubdirectory("sable-build-cache-");
        try {
            var earlier = new BuildCache(root.FullName, identity: CompilerIdentity.Of([("sable", [1])]));
            var rebuilt = new BuildCache(root.FullName, identity: CompilerIdentity.Of([("sable", [2])]));
            var (old, made) = earlier.GetOrCreate(KeyFor(earlier), "dev", d => Make(d, "old"));
            Assert.True(made.Succeeded);

            var calls = 0;
            var (current, _) = rebuilt.GetOrCreate(KeyFor(rebuilt), "dev", d => {
                calls++;
                return Make(d, "new");
            });
            Assert.Equal(1, calls);
            Assert.NotEqual(old, current);
            Assert.Equal("new", File.ReadAllText(Path.Combine(current, "libsable_std.a")));

            // Built again, it is found.
            rebuilt.GetOrCreate(KeyFor(rebuilt), "dev", _ => throw new InvalidOperationException("made twice"));

            var entries = rebuilt.Entries();
            Assert.Equal(2, entries.Count);
            Assert.False(entries.Single(e => e.Directory == old).Current);
            Assert.Equal(("library", "sable-std", "dev"), (entries[0].Kind, entries[0].Package, entries[0].Profile));

            var (removed, bytes) = rebuilt.Prune(TimeSpan.FromDays(30));
            Assert.Equal(old, Assert.Single(removed));
            Assert.True(bytes > 0);
            Assert.Equal(current, Assert.Single(rebuilt.Entries()).Directory);
            Assert.False(Directory.Exists(old));
        } finally {
            root.Delete(true);
        }
    }

    [Fact]
    public void EveryPartOfTheKeyNamesAnotherEntry() {
        var cache = new BuildCache(Path.GetTempPath(), identity: "a");
        var key = KeyFor(cache);
        CacheKey[] others = [
            key with { Kind = "runtime" }, key with { Package = "other" }, key with { Version = "0.6.0" },
            key with { SourceChecksum = CacheKey.Checksum("edited") }, key with { CompilerIdentity = "b" },
            key with { Target = "aarch64-apple-darwin" }, key with { Options = ["opt=3", "debug=True"] },
            key with { Features = ["fancy"] }, key with { Dependencies = ["0123"] },
        ];
        Assert.Equal(others.Length + 1, others.Select(static k => k.Hash).Append(key.Hash).Distinct().Count());
    }

    /// <summary>
    ///     Builds running at once that each need an entry each make it, in
    ///     directories of their own; the first to rename its into place keeps
    ///     it, the others' are thrown away, and nothing half-made is left.
    /// </summary>
    [Fact]
    public void BuildsRunningAtOnceShareAnEntryWrittenWhole() {
        var root = Directory.CreateTempSubdirectory("sable-build-cache-");
        try {
            var cache = new BuildCache(root.FullName, identity: "a");
            var key = KeyFor(cache);
            var results = new (string Directory, NativeBuildResult Result)[8];
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                results[i] = cache.GetOrCreate(key, "dev", d => {
                    File.WriteAllText(Path.Combine(d, "part1"), i.ToString());
                    Thread.Sleep(20);
                    File.WriteAllText(Path.Combine(d, "part2"), i.ToString());
                    return new(true, "");
                }));

            Assert.All(results, static r => Assert.True(r.Result.Succeeded));
            var directory = Assert.Single(results.Select(static r => r.Directory).Distinct());
            Assert.Equal(File.ReadAllText(Path.Combine(directory, "part1")), File.ReadAllText(Path.Combine(directory, "part2")));
            Assert.Equal(directory, Assert.Single(Directory.GetDirectories(root.FullName)));
        } finally {
            root.Delete(true);
        }
    }

    [Fact]
    public void AnEntryThatFailedIsNotKept() {
        var root = Directory.CreateTempSubdirectory("sable-build-cache-");
        try {
            var cache = new BuildCache(root.FullName, identity: "a");
            var (_, result) = cache.GetOrCreate(KeyFor(cache), "dev", d => {
                Make(d);
                return new(false, "clang failed");
            });
            Assert.False(result.Succeeded);
            Assert.Empty(Directory.GetDirectories(root.FullName));
        } finally {
            root.Delete(true);
        }
    }

    [Fact]
    public void PruneRemovesWhatNoBuildUsedAndWhatAStoppedBuildLeft() {
        var root = Directory.CreateTempSubdirectory("sable-build-cache-");
        try {
            var cache = new BuildCache(root.FullName, identity: "a");
            var (stale, _) = cache.GetOrCreate(KeyFor(cache, "stale"), "dev", d => Make(d));
            var (used, _) = cache.GetOrCreate(KeyFor(cache, "used"), "release", d => Make(d));
            File.SetLastWriteTimeUtc(Path.Combine(stale, ".used"), DateTime.UtcNow.AddDays(-40));

            // A build stopped half-way through making an entry, two hours ago.
            var interrupted = AtomicDirectory.TemporaryFor(Path.Combine(root.FullName, KeyFor(cache, "stopped").Hash));
            Directory.CreateDirectory(interrupted);
            Directory.SetLastWriteTimeUtc(interrupted, DateTime.UtcNow.AddHours(-2));

            // And a fetched package, which is the package cache's to collect.
            Directory.CreateDirectory(Path.Combine(root.FullName, "numbers-1.0.0"));

            var (removed, _) = cache.Prune(TimeSpan.FromDays(30));
            Assert.Equal(new[] { stale, interrupted }.Order(StringComparer.Ordinal), removed.Order(StringComparer.Ordinal));
            Assert.True(Directory.Exists(used));
            Assert.True(Directory.Exists(Path.Combine(root.FullName, "numbers-1.0.0")));

            Assert.True(cache.Clean() > 0);
            Assert.Empty(cache.Entries());
            Assert.True(Directory.Exists(Path.Combine(root.FullName, "numbers-1.0.0")));
        } finally {
            root.Delete(true);
        }
    }

    /// <summary>A release's prebuilt entries, beside the compiler, are used as they are and never written.</summary>
    [Fact]
    public void APrebuiltEntryIsReadAndNeverWritten() {
        var root = Directory.CreateTempSubdirectory("sable-build-cache-");
        try {
            var prebuilt = new BuildCache(Path.Combine(root.FullName, "release", "cache"), identity: "a");
            var (shipped, _) = prebuilt.GetOrCreate(KeyFor(prebuilt), "dev", d => Make(d));

            var user = new BuildCache(Path.Combine(root.FullName, "home", "cache"), identity: "a", prebuilt: [prebuilt.Root]);
            var (found, result) = user.GetOrCreate(KeyFor(user), "dev", _ => throw new InvalidOperationException("built again"));
            Assert.True(result.Succeeded);
            Assert.Equal(shipped, found);
            Assert.False(Directory.Exists(user.Root));
        } finally {
            root.Delete(true);
        }
    }

    /// <summary>
    ///     The standard library is compiled once for a target and profile, and
    ///     every project built after links that archive: its first build
    ///     compiles only its own unit. (#182's "a library used by two projects
    ///     is built once", for the standard library.)
    /// </summary>
    [Fact]
    public void TheStandardLibraryIsCompiledOnceForEveryProject() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        foreach (var name in (string[])["first", "second"]) {
            fixture.Package(name, name, "0.1.0", "", ("src/main.sb", $"module App\n\nimport Sable.Text\n\n@main\nfn main() {{\n    print(\"{name}: \" + \"a,b\".split(\",\").joined(\"+\"))\n}}\n"));
        }

        Assert.Equal(("first: a+b\n", 2, 0), Build(fixture, "first"));
        Assert.Equal(("second: a+b\n", 1, 1), Build(fixture, "second"));

        var entries = fixture.Environment.BuildCache.Entries();
        Assert.Contains(entries, static e => e is { Package: "sable-std", Kind: "library", Profile: "dev", Current: true });
        Assert.Contains(entries, static e => e is { Package: "sable-runtime", Kind: "runtime" });
        Assert.All(entries, e => Assert.StartsWith(fixture.Environment.CacheDirectory, e.Directory, StringComparison.Ordinal));

        // Beside its archive, the entry keeps the unit's interface (#182),
        // which this compiler reads back: the core's and the imported
        // module's symbols, and the unit's SIL.
        var std = entries.First(static e => e is { Package: "sable-std", Current: true });
        var face = LibraryInterface.Read(File.ReadAllBytes(Path.Combine(std.Directory, "std.sbi")), Pipeline.CompilerIdentity);
        Assert.NotNull(face);
        Assert.Equal(["Sable.Core", "Sable.Text"], face.Modules);
        Assert.Equal(face.Modules, face.ReadSymbols().Select(static m => m.Name));
        Assert.NotEmpty(face.ReadSil().Functions);
    }

    /// <summary>
    ///     A registry dependency is built once (#182): into an entry of the
    ///     shared cache holding its archive and its interface, keyed by its
    ///     package, version, source and the entries of what it depends on.
    ///     A second project that uses it compiles only its own unit, and
    ///     reads the dependency's interface rather than working it out.
    /// </summary>
    [Fact]
    public void ADependencySharedByTwoProjectsIsBuiltOnce() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.PublishLibrary("base", "1.0.0", value: "7");
        fixture.PublishLibrary("numbers", "1.0.0", "base = \"1\"", value: "5");
        foreach (var name in (string[])["first", "second"]) {
            fixture.Package(name, name, "0.1.0", "[dependencies]\nnumbers = \"1\"\nbase = \"1\"\n",
                ("src/main.sb", $"module App\n\nimport Numbers\nimport Base\n\n@main\nfn main() {{\n    print(\"{name}: \\(numbersValue() + baseValue())\")\n}}\n"));
        }

        // The binary's unit, the two dependencies' and the standard library's.
        Assert.Equal(("first: 12\n", 4, 0), Build(fixture, "first"));
        Assert.Equal(("second: 12\n", 1, 3), Build(fixture, "second"));

        var entries = fixture.Environment.BuildCache.Entries();
        var numbers = Assert.Single(entries, static e => e is { Package: "numbers", Kind: "library", Current: true });
        Assert.Single(entries, static e => e is { Package: "base", Kind: "library" });
        Assert.Single(entries, static e => e is { Package: "numbers", Kind: "interface" });

        // Its key names the entries it was compiled against: base's, and the standard library's.
        var key = File.ReadAllLines(Path.Combine(numbers.Directory, BuildCache.EntryFile));
        Assert.Equal(2, key.Count(static l => l.StartsWith("dependency ", StringComparison.Ordinal)));
        Assert.Contains("version=1.0.0", key);

        Assert.True(File.Exists(Path.Combine(numbers.Directory, NativeBuild.LibraryFileName("sable_numbers", LibraryKind.Static, NativeTarget.Host))));
        var face = LibraryInterface.Read(File.ReadAllBytes(Path.Combine(numbers.Directory, "numbers.sbi")), Pipeline.CompilerIdentity);
        Assert.NotNull(face);
        Assert.Equal(["Numbers"], face.Modules);

        // Built again from scratch, the second project still finds it, and
        // parses both dependencies from their interfaces in the shared cache.
        Directory.Delete(Path.Combine(fixture.Root, "second", "target"), true);
        Assert.Equal(("second: 12\n", 1, 3), Build(fixture, "second"));
        Assert.Matches(@"files parsed from interfaces\s+2\b", fixture.Errors.ToString());
    }

    /// <summary>
    ///     <c>sable cache warm</c> fills the cache for a target and profile
    ///     ahead of a build, which then compiles only the program's own unit;
    ///     <c>list</c> shows the entries, and <c>clean</c> removes them.
    /// </summary>
    [Fact]
    public void WarmingTheCacheLeavesABuildOnlyItsOwnUnit() {
        if (!CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "0.1.0", "", ("src/main.sb", "module App\n\nimport Sable.Text\n\n@main\nfn main() {\n    print(\"a-b\".split(\"-\").joined(\",\"))\n}\n"));
        var warmed = SableProcess.Run(fixture.Root, fixture.Home, "cache", "warm", "--profile", "dev", "--import", "Sable.Text");
        Assert.True(warmed.ExitCode == 0, warmed.Errors);

        var listed = SableProcess.Run(fixture.Root, fixture.Home, "cache", "list");
        Assert.Contains("sable-std library for", listed.Output, StringComparison.Ordinal);
        Assert.Contains("(dev)", listed.Output, StringComparison.Ordinal);

        Assert.Equal(("a,b\n", 1, 1), Build(fixture, "app"));

        Assert.Equal(0, SableProcess.Run(fixture.Root, fixture.Home, "cache", "clean").ExitCode);
        Assert.Empty(fixture.Environment.BuildCache.Entries());

        Assert.NotEqual(0, SableProcess.Run(fixture.Root, fixture.Home, "cache", "warm", "--import", "Sable.Nothing").ExitCode);
    }

    /// <summary>Builds a package's binary with timings; what it printed, and how many units it compiled and reused.</summary>
    static (string Output, int Compiled, int Reused) Build(PackageFixture fixture, string directory) {
        fixture.Errors.GetStringBuilder().Clear();
        var binaries = PackageBuilder.Build(fixture.Load(directory), fixture.Environment, new() { Timings = true });
        Assert.True(binaries is [_], fixture.Errors.ToString());
        var report = Regex.Match(fixture.Errors.ToString(), @"Built \w+: (\w+), (\d+) unit\(s\) compiled, (\d+) reused");
        Assert.True(report.Success, fixture.Errors.ToString());
        var (output, exitCode) = PackageFixture.Execute(binaries[0].Path);
        Assert.Equal(0, exitCode);
        return (output, int.Parse(report.Groups[2].Value), int.Parse(report.Groups[3].Value));
    }
}
