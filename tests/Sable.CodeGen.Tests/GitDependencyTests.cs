using System.Diagnostics;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Git dependencies: written in a manifest, fetched from a repository
///     (a local one, through <c>file://</c>), pinned in the lock file, kept
///     there until updated, and built from the cache when the repository is gone.
/// </summary>
public sealed class GitDependencyTests {
    static IReadOnlyList<ManifestError> Errors(string text) {
        var error = Assert.Throws<ManifestException>(() => Manifest.Parse(text, "sable.toml"));
        return error.Errors;
    }

    const string Header = "[package]\nname = \"a\"\nversion = \"1.0.0\"\n[dependencies]\n";

    [Fact]
    public void AManifestSaysWhichCommitOfARepository() {
        var manifest = Manifest.Parse(Header + """
            b = { git = "https://github.com/someone/b" }
            c = { git = "https://github.com/someone/c.git", tag = "v1.2.0" }
            d = { git = "git@github.com:someone/d.git", branch = "main", version = "0.4" }
            e = { git = "file:///tmp/e", rev = "0123abcd" }
            """, "sable.toml");
        var byName = manifest.Dependencies.ToDictionary(static d => d.Name);
        Assert.Equal("https://github.com/someone/b", byName["b"].Git);
        Assert.Null(byName["b"].Reference);
        Assert.Equal(new GitReference("tag", "v1.2.0"), byName["c"].Reference);
        Assert.Equal(new GitReference("branch", "main"), byName["d"].Reference);
        Assert.Equal("0.4", byName["d"].Version?.Text);
        Assert.Equal(new GitReference("rev", "0123abcd"), byName["e"].Reference);
    }

    [Fact]
    public void AGitDependencyThatDoesNotHoldTogetherIsRefused() {
        Assert.Contains(Errors(Header + "b = { git = \"https://x.org/b\", tag = \"v1\", branch = \"main\" }\n"),
            static e => e.Key == "dependencies.b.branch" && e.Message.Contains("one of 'tag', 'branch' and 'rev'", StringComparison.Ordinal));
        Assert.Contains(Errors(Header + "b = { version = \"1\", tag = \"v1\" }\n"),
            static e => e.Key == "dependencies.b.tag" && e.Message.Contains("no 'git'", StringComparison.Ordinal));
        Assert.Contains(Errors(Header + "b = { git = \"https://x.org/b\", path = \"../b\" }\n"),
            static e => e.Key == "dependencies.b.git" && e.Message.Contains("not both", StringComparison.Ordinal));
        Assert.Contains(Errors(Header + "b = { git = \"not a url\" }\n"),
            static e => e.Key == "dependencies.b.git" && e.Message.Contains("is not a git repository", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("git+https://x.org/b#abc", "https://x.org/b", null, null, "abc")]
    [InlineData("git+https://x.org/b?tag=v1.0#abc", "https://x.org/b", "tag", "v1.0", "abc")]
    [InlineData("git+git@x.org:b.git?branch=feature/x#abc", "git@x.org:b.git", "branch", "feature/x", "abc")]
    public void ALockedSourceReadsBack(string source, string url, string? kind, string? value, string commit) {
        Assert.True(GitCheckout.TryParse(source, out var parsedUrl, out var reference, out var parsedCommit));
        Assert.Equal(url, parsedUrl);
        Assert.Equal(kind is null ? null : new GitReference(kind, value!), reference);
        Assert.Equal(commit, parsedCommit);
        Assert.Equal(source, new GitCheckout(parsedUrl, reference, parsedCommit, "").Source);
        Assert.False(GitCheckout.TryParse("registry", out _, out _, out _));
    }

    /// <summary>Runs git in a directory, as a person with a name would, and returns what it printed.</summary>
    static string Git(string directory, params string[] arguments) {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var argument in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", ..arguments]) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error.Result}");
        return output.Trim();
    }

    /// <summary>A repository holding the <c>util</c> library, whose <c>utilValue()</c> is <paramref name="value" />; returns its file:// URL.</summary>
    static string MakeRepository(PackageFixture fixture, string directory = "repos/util", string packagePath = "") {
        var root = Path.Combine(fixture.Root, directory);
        Directory.CreateDirectory(root);
        Git(root, "init", "--quiet", "-b", "main");
        WriteUtil(fixture, directory, packagePath, "1");
        Git(root, "add", "-A");
        Git(root, "commit", "--quiet", "-m", "one");
        Git(root, "tag", "v0.1.0");
        return new Uri(root).AbsoluteUri;
    }

    static void WriteUtil(PackageFixture fixture, string directory, string packagePath, string value) {
        var at = packagePath.Length == 0 ? directory : $"{directory}/{packagePath}";
        fixture.Package(at, "util", $"0.{value}.0", "",
            ("src/lib.sb", $"module Util\n\npublic fn utilValue() -> Int {{\n    return {value}\n}}\n"));
    }

    static string Commit(PackageFixture fixture, string directory, string value, string packagePath = "") {
        WriteUtil(fixture, directory, packagePath, value);
        var root = Path.Combine(fixture.Root, directory);
        Git(root, "add", "-A");
        Git(root, "commit", "--quiet", "-m", $"value {value}");
        return Git(root, "rev-parse", "HEAD");
    }

    static void WriteApp(PackageFixture fixture, string dependency) =>
        fixture.Package("app", "app", "1.0.0", $"[dependencies]\nutil = {dependency}\n",
            ("src/main.sb", "module App\n\nimport Util\n\nfn main() {\n    let x = utilValue()\n}\n"));

    static LockedPackage LockedUtil(PackageFixture fixture) =>
        LockFile.Load(Path.Combine(fixture.Root, "app", Workspace.LockName))!.Find("util")!;

    [Fact]
    public void ADependencyAtATagIsFetchedLockedAndBuilt() {
        using var fixture = new PackageFixture();
        var url = MakeRepository(fixture);
        var tagged = Git(Path.Combine(fixture.Root, "repos/util"), "rev-parse", "HEAD");
        Commit(fixture, "repos/util", "2");
        WriteApp(fixture, $"{{ git = \"{url}\", tag = \"v0.1.0\" }}");

        var workspace = fixture.Load("app");
        Assert.True(PackageBuilder.Check(workspace, fixture.Environment, new()) == 0, fixture.Errors.ToString());
        var graph = PackageGraph.Resolve(workspace, fixture.Environment);
        var util = graph.Packages["util"];
        Assert.Equal(new SemVersion(0, 1, 0), util.Version);
        Assert.False(util.IsLocal);
        Assert.Equal(tagged, util.Git?.Commit);
        Assert.StartsWith(Path.Combine(fixture.Home, "cache", "git", "checkouts"), util.Package.Directory, StringComparison.Ordinal);
        Assert.Equal($"git+{url}?tag=v0.1.0#{tagged}", LockedUtil(fixture).Source);
    }

    [Fact]
    public void ABranchStaysAtItsLockedCommitUntilUpdated() {
        using var fixture = new PackageFixture();
        var url = MakeRepository(fixture);
        var first = Git(Path.Combine(fixture.Root, "repos/util"), "rev-parse", "HEAD");
        WriteApp(fixture, $"{{ git = \"{url}\", branch = \"main\" }}");
        var workspace = fixture.Load("app");

        PackageGraph.Resolve(workspace, fixture.Environment);
        Assert.EndsWith($"#{first}", LockedUtil(fixture).Source, StringComparison.Ordinal);

        var second = Commit(fixture, "repos/util", "2");
        var graph = PackageGraph.Resolve(workspace, fixture.Environment);
        Assert.Equal(first, graph.Packages["util"].Git?.Commit);
        Assert.Equal(new SemVersion(0, 1, 0), graph.Packages["util"].Version);

        graph = PackageGraph.Resolve(workspace, fixture.Environment, new(Update: new HashSet<string> { "util" }));
        Assert.Equal(second, graph.Packages["util"].Git?.Commit);
        Assert.Equal(new SemVersion(0, 2, 0), graph.Packages["util"].Version);
        Assert.EndsWith($"#{second}", LockedUtil(fixture).Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockedCommitBuildsWithoutTheRepository() {
        using var fixture = new PackageFixture();
        var url = MakeRepository(fixture);
        WriteApp(fixture, $"{{ git = \"{url}\" }}");
        var workspace = fixture.Load("app");
        PackageGraph.Resolve(workspace, fixture.Environment);

        Directory.Move(Path.Combine(fixture.Root, "repos"), Path.Combine(fixture.Root, "gone"));
        Assert.True(PackageBuilder.Check(workspace, fixture.Environment, new(Locked: true)) == 0, fixture.Errors.ToString());
    }

    [Fact]
    public void AChangedReferenceIsResolvedAgain() {
        using var fixture = new PackageFixture();
        var url = MakeRepository(fixture);
        var second = Commit(fixture, "repos/util", "2");
        WriteApp(fixture, $"{{ git = \"{url}\", tag = \"v0.1.0\" }}");
        PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.NotEqual($"#{second}", LockedUtil(fixture).Source[^(second.Length + 1)..]);

        WriteApp(fixture, $"{{ git = \"{url}\", rev = \"{second}\" }}");
        var graph = PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.Equal(second, graph.Packages["util"].Git?.Commit);
    }

    [Fact]
    public void APackageInsideARepositoryIsFound() {
        using var fixture = new PackageFixture();
        var url = MakeRepository(fixture, "repos/mono", "crates/util");
        fixture.Write("repos/mono/README.md", "several packages");
        WriteApp(fixture, $"{{ git = \"{url}\" }}");
        var graph = PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.EndsWith(Path.Combine("crates", "util"), graph.Packages["util"].Package.Directory, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingTagOrAVersionThatDoesNotFitIsSaid() {
        using var fixture = new PackageFixture();
        var url = MakeRepository(fixture);
        WriteApp(fixture, $"{{ git = \"{url}\", tag = \"v9\" }}");
        var error = Assert.Throws<PackageException>(() => PackageGraph.Resolve(fixture.Load("app"), fixture.Environment));
        Assert.Contains("has no tag 'v9'", error.Message, StringComparison.Ordinal);

        WriteApp(fixture, $"{{ git = \"{url}\", version = \"2\" }}");
        Assert.Throws<ResolutionException>(() => PackageGraph.Resolve(fixture.Load("app"), fixture.Environment));
    }
}
