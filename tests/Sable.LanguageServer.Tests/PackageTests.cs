using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using Sable.Driver.Packages;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>Evaluated manifests go to a temporary directory, never the real <c>~/.sable</c>.</summary>
static class ManifestCache {
    [ModuleInitializer]
    internal static void Isolate() =>
        ManifestEvaluator.CacheDirectory = Directory.CreateTempSubdirectory("sable-lsp-manifests-").FullName;
}

/// <summary>
///     The server compiles each package as <c>sable check</c> does (#192):
///     with its default features, its dependencies, and apart from the other
///     members of its workspace.
/// </summary>
public sealed class PackageTests : IDisposable {
    readonly string _top = Directory.CreateTempSubdirectory("sable-lsp-packages-").FullName;

    /// <summary>The editor's folder; <c>util</c> is beside it, outside it.</summary>
    string Root => Path.Combine(_top, "root");

    public void Dispose() {
        try {
            Directory.Delete(_top, true);
        } catch (IOException) {
            // Left for the system to clean.
        }
    }

    string Write(string relative, string text) {
        var path = Path.Combine(_top, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    const string Main = """
                        module App

                        import Util

                        #if feature(fancy)
                        fn bonus() -> Int {
                            return 1
                        }
                        #endif

                        fn main() {
                            print("\(twice(bonus()))")
                        }
                        """;

    const string Shared = "module Shared\n\npublic fn one() -> Int {\n    return 1\n}\n";

    TestClient Client() {
        var environment = PackageEnvironment.At(Path.Combine(_top, "home"));
        return new TestClient(projects: root => LanguageProjects.For(root, environment));
    }

    void WritePackages() {
        // A path dependency outside the folder, in sable.toml.
        Write("util/sable.toml", "[package]\nname = \"util\"\nversion = \"1.0.0\"\n");
        Write("util/src/lib.sb", "module Util\n\n/// Twice its argument.\npublic fn twice(_ x: Int) -> Int {\n    return x * 2\n}\n");

        // A package with a default feature, in package.sb.
        Write("root/app/package.sb", """
                                     import Sable.Build

                                     let manifest = Package {
                                         name: "app",
                                         version: "0.1.0",
                                         dependencies: [.path("util", "../../util")],
                                         features: ["default": ["fancy"], "fancy": []],
                                     }
                                     """);

        // A workspace whose two members declare the same module and function.
        Write("root/ws/sable.toml", "[workspace]\nmembers = [\"a\", \"b\"]\n");
        Write("root/ws/a/sable.toml", "[package]\nname = \"a\"\nversion = \"0.1.0\"\n\n[lints]\nunused_local = \"allow\"\n");
        Write("root/ws/a/src/lib.sb", Shared);
        Write("root/ws/b/sable.toml", "[package]\nname = \"b\"\nversion = \"0.1.0\"\n");
        Write("root/ws/b/src/lib.sb", Shared);
    }

    [Fact]
    public void ThePackagesAreReadFromTheirManifests() {
        WritePackages();
        var projects = LanguageProjects.For(Root, PackageEnvironment.At(Path.Combine(_top, "home")));
        Assert.Equal(["a", "app", "b"], projects.Select(static p => p.Name).Order());

        var app = projects.Single(static p => p.Name == "app");
        Assert.Equal(["default", "fancy"], app.Features);
        Assert.Equal([Path.Combine(_top, "util", "src", "lib.sb")], app.Dependencies.Select(static d => d.Path));
        Assert.Empty(projects.Single(static p => p.Name == "a").Dependencies);

        // Each with its [lints], which the editor reports at.
        var unusedLocal = Sable.Symbols.Lints.Find("unused_local")!;
        Assert.Equal(Sable.Symbols.LintLevel.Allow, projects.Single(static p => p.Name == "a").Lints.LevelOf(unusedLocal));
        Assert.Equal(Sable.Symbols.LintLevel.Warn, app.Lints.LevelOf(unusedLocal));
    }

    [Fact]
    public async Task ADependencyThatDoesNotCompileIsShownOnce() {
        // A dependency's file is no document, so no diagnostic shows why the
        // package's own files fail; the user is told once (#284), not with
        // each compilation of the package after an edit.
        WritePackages();
        Write("util/src/lib.sb", "module Util\n\npublic fn twice(_ x: Int) -> Int {\n    return x *\n}\n");
        var main = Write("root/app/src/main.sb", Main);
        var uri = Workspace.UriOf(main);
        await using var client = Client();
        await client.InitializeAsync(Root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, Main);

        var shown = await client.MessageAsync("window/showMessage");
        Assert.Equal(2, shown.GetInt("type"));
        Assert.Matches($@"{Regex.Escape(Path.Combine("util", "src", "lib.sb"))}:[45]:\d+: error S\d+", shown.GetString("message"));
        Assert.StartsWith("A dependency does not compile", shown.GetString("message"), StringComparison.Ordinal);

        // An edit compiles the package again, with the dependency as it was.
        var built = client.Server.AnalysesBuilt;
        await client.ChangeAsync(uri, 2, Main, Main.Length, Main.Length, "\nfn more() {}\n");
        await client.DiagnosticsAsync(uri);
        Assert.True(client.Server.AnalysesBuilt > built);
        Assert.Single(client.Sent("window/showMessage"));
    }

    [Fact]
    public async Task APackageIsCheckedWithItsFeaturesAndDependencies() {
        WritePackages();
        var main = Write("root/app/src/main.sb", Main);
        var uri = Workspace.UriOf(main);
        await using var client = Client();
        await client.InitializeAsync(Root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, Main);

        // No S2006 for Util, no S2002 for 'twice' or for the feature's 'bonus'.
        var diagnostics = await client.DiagnosticsAsync(uri);
        Assert.True(diagnostics.GetArrayLength() == 0, diagnostics.ToString());

        // The dependency's declaration is in its file on disk.
        var definition = await client.AtAsync("textDocument/definition", uri, Main, Main.IndexOf("twice", StringComparison.Ordinal));
        Assert.Equal(Workspace.UriOf(Path.Combine(_top, "util", "src", "lib.sb")), definition.GetString("uri"));
        var hover = await client.AtAsync("textDocument/hover", uri, Main, Main.IndexOf("twice", StringComparison.Ordinal));
        Assert.Contains("Twice its argument", hover.Get("contents")!.Value.GetString("value"), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A dependency's files read from disk are library files to the SIL
    ///     checks (#182): the package's own are its documents (its manifest
    ///     among them), and the dependency's, known by their text, can be
    ///     known to check cleanly as the standard library's are — so a
    ///     package's checks need not bind its dependencies' bodies.
    /// </summary>
    [Fact]
    public async Task ADependencysFilesAreLibraryFilesToTheChecks() {
        WritePackages();
        var main = Write("root/app/src/main.sb", Main);
        var uri = Workspace.UriOf(main);
        var environment = PackageEnvironment.At(Path.Combine(_top, "home"));
        var asked = new List<(IReadOnlyCollection<Sable.Syntax.SyntaxTree> Own, bool Checkable)>();
        await using var client = new TestClient(projects: root => LanguageProjects.For(root, environment), libraryCheck: (compilation, own) => {
            lock (asked) {
                asked.Add((own, Sable.Driver.Library.CheckFor(compilation, own) is not null));
            }

            return null;
        });
        await client.InitializeAsync(Root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, Main);
        await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() == 0);
        for (var i = 0; i < 600 && asked.Count == 0; i++) {
            await Task.Delay(50);
        }

        var (own, checkable) = Assert.Single(asked);
        Assert.Contains(main, own.Select(static t => t.Text.FileName));
        Assert.DoesNotContain(own, static t => t.Text.FileName.EndsWith(Path.Combine("util", "src", "lib.sb"), StringComparison.Ordinal));
        Assert.True(checkable);
    }

    [Fact]
    public async Task TheMembersOfAWorkspaceAreCompiledApart() {
        WritePackages();
        var a = Workspace.UriOf(Path.Combine(Root, "ws", "a", "src", "lib.sb"));
        var b = Workspace.UriOf(Path.Combine(Root, "ws", "b", "src", "lib.sb"));
        await using var client = Client();
        await client.InitializeAsync(Root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(a, Shared);
        await client.OpenAsync(b, Shared);

        // Merged, each 'one' would be declared twice.
        foreach (var uri in (string[])[a, b]) {
            var diagnostics = await client.DiagnosticsAsync(uri);
            Assert.True(diagnostics.GetArrayLength() == 0, diagnostics.ToString());
        }
    }

    [Fact]
    public async Task WithoutItsPackagesAFileIsLoose() {
        // What the server did before it read manifests: the same file, with
        // no package, cannot see its dependency or its feature.
        WritePackages();
        var main = Write("root/app/src/main.sb", Main);
        var uri = Workspace.UriOf(main);
        await using var client = new TestClient();
        await client.InitializeAsync(Root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, Main);

        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);
        Assert.Contains(diagnostics.EnumerateArray(), static d => d.GetString("code") == "S2006");
    }

    [Fact]
    public async Task ChangingAManifestReadsThePackagesAgain() {
        WritePackages();
        var main = Write("root/app/src/main.sb", Main);
        var uri = Workspace.UriOf(main);
        await using var client = Client();
        await client.InitializeAsync(Root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, Main);
        await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() == 0);

        // Without the default feature, 'bonus' is gone.
        var manifest = Path.Combine(Root, "app", "package.sb");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"default\": [\"fancy\"], ", "", StringComparison.Ordinal));
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = Workspace.UriOf(manifest), type = 2 } } });
        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);
        Assert.Contains(diagnostics.EnumerateArray(), static d => d.GetString("message")!.Contains("bonus", StringComparison.Ordinal));
    }
}
