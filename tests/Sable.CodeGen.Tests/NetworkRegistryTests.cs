using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The network registry (#200): the protocol between <see cref="HttpRegistry" />
///     and <see cref="RegistryServer" />, run against a server on this machine
///     — publishing, namespaces, owners, yanking, search, login and install —
///     and the checks <c>publish</c> makes and the cache's collection.
/// </summary>
public sealed class NetworkRegistryTests {
    /// <summary>A registry server on a free port of this machine, with a token for alice (who has the acme/ namespace).</summary>
    sealed class Server : IDisposable {
        public Server(PackageFixture fixture) {
            Root = Path.Combine(fixture.Root, "server");
            Alice = RegistryTokens.Issue(Root, new("alice", ["acme"], false));

            // A free port, asked of the system; another test may take it
            // before the server binds it, so a few tries.
            for (var attempt = 0; ; attempt++) {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                Url = $"http://127.0.0.1:{port}/";
                Host = new RegistryServer(Root, Url);
                try {
                    Host.Start();
                    break;
                } catch (HttpListenerException) when (attempt < 5) {
                    Host.Dispose();
                }
            }

            Credentials.Save(fixture.Home, Url, Alice);
            Environment = fixture.Environment with { RegistryDirectory = Url };
        }

        public string Root { get; }
        public string Url { get; }
        public string Alice { get; }
        public RegistryServer Host { get; }

        /// <summary>The fixture's environment, with this registry and alice logged in.</summary>
        public PackageEnvironment Environment { get; }

        public HttpRegistry Client(string? token = null) => new(Url, token ?? Alice);

        public void Dispose() => Host.Dispose();
    }

    static void Publish(PackageFixture fixture, PackageEnvironment environment, string name, string version, string rest = "", string? module = null, string value = "1") {
        module ??= Scaffold.ModuleName(name);
        var directory = $"published/{name.Replace('/', '-')}-{version}";
        fixture.Package(directory, name, version, rest,
            ("src/lib.sb", $"module {module}\n\npublic fn {module.ToLowerInvariant()}Value() -> Int {{\n    return {value}\n}}\n"));
        var workspace = fixture.Load(directory);
        PackageOperations.Publish(workspace, environment, workspace.Members[0], dryRun: false, allowDirty: true, verify: false);
    }

    [Fact]
    public void PublishResolveAndBuildOverTheNetwork() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        Publish(fixture, server.Environment, "alice/numbers", "1.0.0", value: "10");
        Publish(fixture, server.Environment, "alice/numbers", "1.1.0", value: "11");

        fixture.Package("app", "app", "1.0.0", "[dependencies]\n\"alice/numbers\" = \"1\"\n",
            ("src/main.sb", "module App\n\nimport Numbers\n\nfn main() {\n    print(\"\\(numbersValue())\")\n}\n"));
        var graph = PackageGraph.Resolve(fixture.Load("app"), server.Environment);
        Assert.Equal(SemVersion.Parse("1.1.0"), graph.Packages["alice/numbers"].Version);
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("app"), server.Environment, new()));
        if (Programs.CanBuild) {
            var binary = Assert.Single(PackageBuilder.Build(fixture.Load("app"), server.Environment, new(Profile: "release"))!);
            Assert.Equal("11\n", PackageFixture.Execute(binary.Path).Output);
        }

        // What the server keeps is what a directory registry keeps, signed by the publisher.
        using var client = server.Client();
        var entry = client.Entry("alice/numbers", SemVersion.Parse("1.0.0"))!;
        new Registry(Path.Combine(server.Root, "store")).Verify(entry);
        Assert.Equal(Registry.Checksum(client.Download(entry)), entry.Checksum);
    }

    [Fact]
    public void NamespacesAreTheTokensAndUnprefixedNamesAreReserved() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        var reserved = Assert.Throws<RegistryException>(() => Publish(fixture, server.Environment, "numbers", "1.0.0"));
        Assert.Contains("reserved for the standard ecosystem", reserved.Message, StringComparison.Ordinal);
        Assert.Contains("'alice/numbers'", reserved.Message, StringComparison.Ordinal);

        var other = Assert.Throws<RegistryException>(() => Publish(fixture, server.Environment, "bob/numbers", "1.0.0"));
        Assert.Contains("may not publish under 'bob/'", other.Message, StringComparison.Ordinal);

        // Her organisation's, she may.
        Publish(fixture, server.Environment, "acme/numbers", "1.0.0");

        // The standard ecosystem's token may take an unprefixed name.
        var core = RegistryTokens.Issue(server.Root, new("core", [], true));
        Credentials.Save(fixture.Home, server.Url, core);
        Publish(fixture, server.Environment, "numbers", "1.0.0");
        using var client = server.Client();
        Assert.Single(client.Entries("numbers"));
    }

    [Fact]
    public void WritingNeedsAToken() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        Credentials.Remove(fixture.Home, server.Url);
        var error = Assert.Throws<RegistryException>(() => Publish(fixture, server.Environment, "alice/numbers", "1.0.0"));
        Assert.Contains("not logged in", error.Message, StringComparison.Ordinal);

        Credentials.Save(fixture.Home, server.Url, "sbl_not-a-token");
        error = Assert.Throws<RegistryException>(() => Publish(fixture, server.Environment, "alice/numbers", "1.0.0"));
        Assert.Contains("did not accept your token", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(server.Root, "store", "index", "alice", "numbers.json")));
    }

    [Fact]
    public void OwnersAreKeysAndAnOwnerAddsAnother() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        Publish(fixture, server.Environment, "alice/numbers", "1.0.0");

        // Bob has a token of his own, in alice's organisation, but not her key.
        var bobHome = Path.Combine(fixture.Root, "bob");
        var bob = PackageEnvironment.At(bobHome) with { RegistryDirectory = server.Url };
        Credentials.Save(bobHome, server.Url, RegistryTokens.Issue(server.Root, new("bob", ["alice"], false)));
        var refused = Assert.Throws<RegistryException>(() => Publish(fixture, bob, "alice/numbers", "1.1.0"));
        Assert.Contains("owned by another key", refused.Message, StringComparison.Ordinal);

        using var bobKey = Registry.LoadOrCreateKey(bob.KeyPath);
        using var aliceKey = Registry.LoadOrCreateKey(fixture.Environment.KeyPath);
        using var client = server.Client();
        client.ChangeOwner("alice/numbers", Registry.PublicKeyOf(bobKey), add: true, aliceKey);
        Assert.Equal([Registry.PublicKeyOf(aliceKey), Registry.PublicKeyOf(bobKey)], client.Owners("alice/numbers"));
        Publish(fixture, bob, "alice/numbers", "1.1.0");

        // Removed, bob publishes no more; what he signed stays good.
        using (var fresh = server.Client()) {
            fresh.ChangeOwner("alice/numbers", Registry.PublicKeyOf(bobKey), add: false, aliceKey);
        }

        using var after = server.Client();
        var signedByBob = after.Entry("alice/numbers", SemVersion.Parse("1.1.0"))!;
        after.Download(signedByBob);
        Assert.Throws<RegistryException>(() => Publish(fixture, bob, "alice/numbers", "1.2.0"));

        var last = Assert.Throws<RegistryException>(() => after.ChangeOwner("alice/numbers", Registry.PublicKeyOf(aliceKey), add: false, aliceKey));
        Assert.Contains("last owner", last.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YankingIsSignedAndAReplayedRequestIsRefused() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        Publish(fixture, server.Environment, "alice/numbers", "1.0.0");
        PackageOperations.Yank(null, server.Environment, "alice/numbers", SemVersion.Parse("1.0.0"), undo: false);
        using (var client = server.Client()) {
            Assert.True(client.Entry("alice/numbers", SemVersion.Parse("1.0.0"))!.Yanked);
        }

        PackageOperations.Yank(null, server.Environment, "alice/numbers", SemVersion.Parse("1.0.0"), undo: true);

        // The request that yanked it, sent again, would yank it again: it names the state it changed.
        using var key = Registry.LoadOrCreateKey(fixture.Environment.KeyPath);
        var replay = new JsonObject {
            ["name"] = "alice/numbers",
            ["version"] = "1.0.0",
            ["yanked"] = false,
            ["key"] = Registry.PublicKeyOf(key),
            ["signature"] = Registry.SignText(key, RegistryRequests.Yank("alice/numbers", SemVersion.Parse("1.0.0"), false, true)),
        };
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", server.Alice);
        var response = await http.PostAsync(server.Url + "api/v1/yank", new StringContent(replay.ToJsonString()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("stale", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheServerChecksTheArchiveIsThePackageItsEntrySays() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        fixture.Package("lib", "alice/numbers", "1.0.0", "", ("src/lib.sb", "module Numbers\n"));
        var workspace = fixture.Load("lib");
        var plan = PackageOperations.Plan(workspace, fixture.Environment, workspace.Members[0]);

        // An entry for 2.0.0 over the archive of 1.0.0, consistently signed.
        using var key = Registry.LoadOrCreateKey(fixture.Environment.KeyPath);
        using var client = server.Client();
        var error = Assert.Throws<RegistryException>(() => client.Publish(plan.Entry with { Version = SemVersion.Parse("2.0.0") }, plan.Archive, key));
        Assert.Contains("the archive holds alice/numbers 1.0.0, not alice/numbers 2.0.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATamperedServerIsCaughtByTheClient() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        Publish(fixture, server.Environment, "alice/numbers", "1.0.0");
        var index = Path.Combine(server.Root, "store", "index", "alice", "numbers.json");
        var json = JsonNode.Parse(File.ReadAllText(index))!;
        json["versions"]![0]!["features"] = new JsonObject { ["evil"] = new JsonArray() };
        File.WriteAllText(index, json.ToJsonString());

        fixture.Package("app", "app", "1.0.0", "[dependencies]\n\"alice/numbers\" = \"1\"\n", ("src/lib.sb", "module App\n"));
        var error = Assert.Throws<RegistryException>(() => PackageGraph.Resolve(fixture.Load("app"), server.Environment));
        Assert.Contains("does not verify", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchFindsByNameAndDescription() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        Publish(fixture, server.Environment, "alice/numbers", "1.0.0", "description = \"Integer helpers\"\n");
        Publish(fixture, server.Environment, "alice/numbers", "1.2.0", "description = \"Integer helpers\"\n");
        Publish(fixture, server.Environment, "acme/parse", "0.3.0", "description = \"Parsing numbers from text\"\n", module: "Parse");

        using var client = server.Client();
        var hits = client.Search("numbers");
        Assert.Equal(["alice/numbers", "acme/parse"], hits.Select(static h => h.Name));
        Assert.Equal(SemVersion.Parse("1.2.0"), hits[0].Version);
        Assert.Equal("Integer helpers", hits[0].Description);
        Assert.Empty(client.Search("nothing like it"));

        var cli = SableProcess.Run(fixture.Root, fixture.Home, "search", "parsing", "--registry", server.Url);
        Assert.True(cli.ExitCode == 0, cli.Errors);
        Assert.Equal("acme/parse = \"0.3.0\"    # Parsing numbers from text\n", cli.Output.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void OfflineNeverReachesTheNetwork() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        Publish(fixture, server.Environment, "alice/numbers", "1.0.0");
        var offline = server.Environment with { Offline = true };
        Assert.False(offline.RegistryFor(null).IsAvailable);

        fixture.Package("app", "app", "1.0.0", "[dependencies]\n\"alice/numbers\" = \"1\"\n", ("src/lib.sb", "module App\n"));
        var error = Assert.ThrowsAny<Exception>(() => PackageGraph.Resolve(fixture.Load("app"), offline));
        Assert.Contains("--offline", error.Message, StringComparison.Ordinal);
        PackageGraph.Resolve(fixture.Load("app"), server.Environment);
        PackageGraph.Resolve(fixture.Load("app"), offline);
    }

    [Fact]
    public void LoginKeepsAndChecksTheToken() {
        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        Credentials.Remove(fixture.Home, server.Url);

        var bad = SableProcess.RunWithInput(fixture.Root, fixture.Home, null, "sbl_wrong\n", "login", "--registry", server.Url);
        Assert.Equal(1, bad.ExitCode);
        Assert.Null(Credentials.Token(fixture.Home, server.Url));

        var good = SableProcess.RunWithInput(fixture.Root, fixture.Home, null, server.Alice + "\n", "login", "--registry", server.Url);
        Assert.True(good.ExitCode == 0, good.Errors);
        Assert.Contains("as alice", good.Errors, StringComparison.Ordinal);
        Assert.Equal(server.Alice, Credentials.Token(fixture.Home, server.Url));
        if (!OperatingSystem.IsWindows()) {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Credentials.PathIn(fixture.Home)));
        }

        Assert.Equal(0, SableProcess.Run(fixture.Root, fixture.Home, "logout", "--registry", server.Url).ExitCode);
        Assert.Null(Credentials.Token(fixture.Home, server.Url));
    }

    /// <summary>docs/04's exit criterion, end to end: someone outside publishes, someone else depends on it.</summary>
    [Fact]
    public void AThirdPartyPublishesAndAnotherDependsOnIt() {
        if (!Programs.CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        var publisher = Path.Combine(fixture.Root, "publisher-home");
        var environment = new Dictionary<string, string?> { ["SABLE_REGISTRY"] = server.Url };
        string Run(string directory, string home, params string[] arguments) {
            var result = SableProcess.Run(directory, home, environment, arguments);
            Assert.True(result.ExitCode == 0, $"sable {string.Join(' ', arguments)}:\n{result.Errors}");
            return result.Output;
        }

        Assert.Equal(0, SableProcess.RunWithInput(fixture.Root, publisher, environment, server.Alice + "\n", "login").ExitCode);
        Run(fixture.Root, publisher, "new", "alice/mathx", "--lib");
        Run(Path.Combine(fixture.Root, "mathx"), publisher, "publish");

        Run(fixture.Root, fixture.Home, "new", "calc");
        var calc = Path.Combine(fixture.Root, "calc");
        Run(calc, fixture.Home, "add", "alice/mathx");
        File.WriteAllText(Path.Combine(calc, "src", "main.sb"), "module Calc\n\nimport Sable.IO\nimport Mathx\n\nfn main() {\n    print(\"\\(add(2, 3))\")\n}\n");
        Assert.Equal("5\n", Run(calc, fixture.Home, "run", "-q"));
    }

    [Fact]
    public void InstallBuildsTheBinariesIntoTheHome() {
        if (!Programs.CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        using var server = new Server(fixture);
        fixture.Package("hello", "alice/hello", "1.0.0", "", ("src/main.sb", "module Hello\n\nfn main() {\n    print(\"hello from an install\")\n}\n"));
        var workspace = fixture.Load("hello");
        PackageOperations.Publish(workspace, server.Environment, workspace.Members[0], dryRun: false, allowDirty: true, verify: false);

        using var registry = server.Client();
        var installed = Installer.Install(server.Environment, registry, "alice/hello@1", null, force: false);
        Assert.Equal([Platform.Executable("hello")], installed.Binaries);
        var binary = Path.Combine(fixture.Home, "bin", Platform.Executable("hello"));
        Assert.Equal("hello from an install\n", PackageFixture.Execute(binary).Output);
        Assert.Equal("alice/hello", Assert.Single(Installer.Load(server.Environment)).Name);

        // Installing it again replaces it; another package's binary of that name it does not, unforced.
        Installer.Install(server.Environment, registry, "alice/hello", null, force: false);
        Installer.Uninstall(server.Environment, "alice/hello");
        Assert.False(File.Exists(binary));
        Assert.Empty(Installer.Load(server.Environment));

        var library = Assert.Throws<PackageException>(() => {
            Publish(fixture, server.Environment, "alice/numbers", "1.0.0");
            Installer.Install(server.Environment, registry, "alice/numbers", null, false);
        });
        Assert.Contains("has no binaries", library.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TokensKeysAndTheCacheOnTheCommandLine() {
        using var fixture = new PackageFixture();
        var root = Path.Combine(fixture.Root, "server");
        var issued = SableProcess.Run(fixture.Root, fixture.Home, "registry", "token", "--root", root, "--user", "bob", "--org", "acme");
        Assert.True(issued.ExitCode == 0, issued.Errors);
        var token = issued.Output.Trim();
        Assert.StartsWith("sbl_", token, StringComparison.Ordinal);
        var user = RegistryTokens.Find(root, token)!;
        Assert.Equal("bob", user.Name);
        Assert.Equal(["acme"], user.Organisations);
        Assert.False(user.Reserved);
        Assert.Null(RegistryTokens.Find(root, token + "x"));
        Assert.DoesNotContain(token, File.ReadAllText(RegistryTokens.PathIn(root)), StringComparison.Ordinal);

        var key = SableProcess.Run(fixture.Root, fixture.Home, "owner", "--key");
        Assert.Equal(0, key.ExitCode);
        using (var made = Registry.LoadOrCreateKey(fixture.Environment.KeyPath)) {
            Assert.Equal(Registry.PublicKeyOf(made), key.Output.Trim());
        }

        fixture.PublishLibrary("numbers", "1.0.0");
        fixture.Package("app", "app", "1.0.0", "[dependencies]\nnumbers = \"1\"\n", ("src/lib.sb", "module App\n"));
        PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.Contains("numbers-1.0.0", SableProcess.Run(fixture.Root, fixture.Home, "cache", "list").Output, StringComparison.Ordinal);
        Assert.Equal(0, SableProcess.Run(fixture.Root, fixture.Home, "cache", "prune", "--max-age", "0").ExitCode);
        Assert.Empty(PackageCache.List(fixture.Environment));
    }

    [Fact]
    public void TheCacheCollectsWhatNoBuildUsed() {
        using var fixture = new PackageFixture();
        fixture.PublishLibrary("numbers", "1.0.0");
        fixture.PublishLibrary("other", "1.0.0");
        fixture.Package("app", "app", "1.0.0", "[dependencies]\nnumbers = \"1\"\nother = \"1\"\n", ("src/lib.sb", "module App\n"));
        PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.Equal(2, PackageCache.List(fixture.Environment).Count);

        // Unused for a year, 'other' goes; 'numbers', used now, stays.
        var stale = Path.Combine(fixture.Environment.CacheDirectory, "other-1.0.0", ".sable-used");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-365));
        var (removed, bytes) = PackageCache.Collect(fixture.Environment, TimeSpan.FromDays(30));
        Assert.Equal(Path.Combine(fixture.Environment.CacheDirectory, "other-1.0.0"), Assert.Single(removed));
        Assert.True(bytes > 0);
        Assert.Equal("numbers-1.0.0", Path.GetFileName(Assert.Single(PackageCache.List(fixture.Environment)).Directory));

        // What was collected is fetched again, checked as before.
        PackageGraph.Resolve(fixture.Load("app"), fixture.Environment);
        Assert.Equal(2, PackageCache.List(fixture.Environment).Count);

        Assert.True(PackageCache.Clear(fixture.Environment) > 0);
        Assert.Empty(PackageCache.List(fixture.Environment));
    }

    [Fact]
    public void PublishRunsTheTestsFirst() {
        if (!Programs.CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("lib", "numbers", "1.0.0", "", ("src/lib.sb", "module Numbers\n\npublic fn double(_ x: Int) -> Int {\n    return x + x\n}\n"),
            ("tests/numbers_tests.sb", "module NumbersTests\n\nimport Numbers\nimport Sable.Testing\n\n@test\nfn doubles() {\n    assertEqual(double(2), 5)\n}\n"));
        var workspace = fixture.Load("lib");
        var error = Assert.Throws<PackageException>(() => PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], dryRun: false, allowDirty: true));
        Assert.Contains("the tests of numbers fail", error.Message, StringComparison.Ordinal);
        Assert.Empty(new Registry(fixture.Environment.RegistryDirectory).Entries("numbers"));

        // Skipped when asked, and passed when they pass.
        PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], dryRun: true, allowDirty: true, verify: false);
        File.WriteAllText(Path.Combine(fixture.Root, "lib", "tests", "numbers_tests.sb"),
            "module NumbersTests\n\nimport Numbers\nimport Sable.Testing\n\n@test\nfn doubles() {\n    assertEqual(double(2), 4)\n}\n");
        PackageOperations.Publish(fixture.Load("lib"), fixture.Environment, workspace.Members[0], dryRun: false, allowDirty: true);
        Assert.Single(new Registry(fixture.Environment.RegistryDirectory).Entries("numbers"));
    }

    [Fact]
    public void PublishWantsATagForTheVersion() {
        using var fixture = new PackageFixture();
        var directory = fixture.Package("lib", "numbers", "1.0.0", "", ("src/lib.sb", "module Numbers\n"), (".gitignore", "target/\nsable.lock\n"));
        void Git(params string[] arguments) {
            var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", ..arguments]) {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors.Result);
        }

        try {
            Git("init", "-q");
        } catch (System.ComponentModel.Win32Exception) {
            return; // No git: nothing to tag.
        }

        Git("add", ".");
        Git("commit", "-q", "-m", "numbers");
        var workspace = fixture.Load("lib");
        var error = Assert.Throws<PackageException>(() => PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], dryRun: false, verify: false));
        Assert.Contains("has no tag for 1.0.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("git tag v1.0.0", error.Message, StringComparison.Ordinal);

        PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], dryRun: true, verify: false, allowUntagged: true);
        Git("tag", "v1.0.0");
        PackageOperations.Publish(workspace, fixture.Environment, workspace.Members[0], dryRun: false, verify: false);
        Assert.Single(new Registry(fixture.Environment.RegistryDirectory).Entries("numbers"));
    }
}
