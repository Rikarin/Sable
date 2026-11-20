using System.Collections.Immutable;
using System.Text.Json;
using Sable.Syntax;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     The workspace as the editor has it (#196): several folders, added and
///     removed; saves, settings and watched files; progress while loading;
///     formatting a range, and a file with a syntax error.
/// </summary>
public sealed class WorkspaceTests : IDisposable {
    readonly string _top = Directory.CreateTempSubdirectory("sable-lsp-workspace-").FullName;

    public void Dispose() {
        try {
            Directory.Delete(_top, true);
        } catch (IOException) {
            // Left for the system to clean.
        }
    }

    string Folder(string name) => Directory.CreateDirectory(Path.Combine(_top, name)).FullName;

    static string Write(string folder, string name, string text) {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return Workspace.UriOf(path);
    }

    const string Broken = "module App\n\nfn broken() -> Int {\n    let s: String = 1\n    return 2\n}\n";

    [Fact]
    public async Task ACompilationThatThrowsIsNotTriedAgainUntilItsFilesChange() {
        // Each request and each round asks for the package's analysis; one
        // that throws would otherwise be built again for every snapshot.
        var tries = 0;
        var cache = new AnalysisCache((_, _) => {
            Interlocked.Increment(ref tries);
            throw new InvalidOperationException("the compiler gave up");
        });

        const string Uri = "file:///w/a.sb";
        Document Of(string text) => new(Uri, SyntaxTree.Parse(new SourceText(text, "/w/a.sb")), 1, true);
        var a = Of("module A\n");
        WorkspaceSnapshot Snapshot(long generation, params Document[] documents) =>
            new(documents.ToImmutableDictionary(static d => Workspace.KeyOf(d.Uri), StringComparer.Ordinal), generation, ProjectLayout.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Snapshot(1, a), ProjectLayout.Loose));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Snapshot(2, a), ProjectLayout.Loose));
        Assert.Equal(1, tries);

        // Its file changed: tried again.
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Snapshot(3, Of("module A\n\nfn f() {}\n")), ProjectLayout.Loose));
        Assert.Equal(2, tries);
    }

    [Fact]
    public async Task EveryFolderIsLoaded() {
        var first = Folder("first");
        var second = Folder("second");
        const string caller = "module App\n\nfn main() {\n    print(helper())\n}\n";
        var main = Write(first, "main.sb", caller);
        Write(second, "helper.sb", "module App\n\nfn helper() -> Int {\n    return 1\n}\n");
        var broken = Write(second, "broken.sb", Broken);

        await using var client = new TestClient();
        await client.InitializeAsync([first, second]);
        await client.NotifyAsync("initialized", new { });
        await client.DiagnosticsAsync(broken, static d => d.GetArrayLength() > 0);

        // A name in the second folder is found from the first.
        await client.OpenAsync(main, caller);
        await client.DiagnosticsAsync(main, static d => d.GetArrayLength() == 0);
        var definition = await client.AtAsync("textDocument/definition", main, caller, caller.IndexOf("helper", StringComparison.Ordinal));
        Assert.EndsWith("helper.sb", definition.GetString("uri"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FoldersAreAddedAndRemoved() {
        var first = Folder("first");
        var second = Folder("second");
        var kept = Write(first, "kept.sb", Broken);
        var added = Write(second, "added.sb", Broken.Replace("broken", "other", StringComparison.Ordinal));

        await using var client = new TestClient();
        var capabilities = (await client.InitializeAsync([first])).Get("capabilities")!.Value;
        Assert.True(capabilities.Get("workspace")!.Value.Get("workspaceFolders")!.Value.GetBool("changeNotifications"));
        await client.NotifyAsync("initialized", new { });
        await client.DiagnosticsAsync(kept, static d => d.GetArrayLength() > 0);

        await client.NotifyAsync("workspace/didChangeWorkspaceFolders", new {
            @event = new { added = new[] { new { uri = Workspace.UriOf(second), name = "second" } }, removed = Array.Empty<object>() }
        });
        await client.DiagnosticsAsync(added, static d => d.GetArrayLength() > 0);

        await client.NotifyAsync("workspace/didChangeWorkspaceFolders", new {
            @event = new { added = Array.Empty<object>(), removed = new[] { new { uri = Workspace.UriOf(second), name = "second" } } }
        });
        await client.DiagnosticsAsync(added, static d => d.GetArrayLength() == 0);
        Assert.False(client.Sent("textDocument/publishDiagnostics").Last(n => n.Get("params")!.Value.GetString("uri") == kept)
            .Get("params")!.Value.Get("diagnostics")!.Value.GetArrayLength() == 0);
    }

    [Fact]
    public async Task SavingAManifestReadsThePackagesAgain() {
        var root = Folder("root");
        var reads = 0;
        await using var client = new TestClient(projects: _ => {
            Interlocked.Increment(ref reads);
            return [];
        });
        var capabilities = (await client.InitializeAsync(root)).Get("capabilities")!.Value;
        Assert.False(capabilities.Get("textDocumentSync")!.Value.Get("save")!.Value.GetBool("includeText"));
        await client.NotifyAsync("initialized", new { });
        await Until(() => Volatile.Read(ref reads) == 1);

        var manifest = Write(root, "sable.toml", "[package]\nname = \"x\"\nversion = \"0.1.0\"\n");
        await client.NotifyAsync("textDocument/didSave", new { textDocument = new { uri = manifest } });
        await Until(() => Volatile.Read(ref reads) == 2);

        // Any other file saved is the editor's business alone.
        await client.NotifyAsync("textDocument/didSave", new { textDocument = new { uri = Write(root, "x.sb", "module App\n") } });
        await client.ShutdownAsync();
        Assert.Equal(2, Volatile.Read(ref reads));
    }

    static async Task Until(Func<bool> condition) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition()) {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ASettingTurnsOffTheFilesNobodyOpened() {
        var root = Folder("root");
        var closed = Write(root, "closed.sb", Broken);
        const string open = "module App\n\nfn shown() -> Int {\n    let s: String = 1\n    return 2\n}\n";
        var opened = Write(root, "open.sb", open);

        await using var client = new TestClient();
        await client.InitializeAsync(root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(opened, open);
        await client.DiagnosticsAsync(closed, static d => d.GetArrayLength() > 0);

        await client.NotifyAsync("workspace/didChangeConfiguration", new { settings = new { sable = new { diagnostics = new { workspace = false } } } });
        await client.DiagnosticsAsync(closed, static d => d.GetArrayLength() == 0);
        await client.DiagnosticsAsync(opened, static d => d.GetArrayLength() > 0);

        await client.NotifyAsync("workspace/didChangeConfiguration", new { settings = new { sable = new { diagnostics = new { workspace = true } } } });
        await client.DiagnosticsAsync(closed, static d => d.GetArrayLength() > 0);
    }

    [Fact]
    public async Task TheClientIsAskedToWatchTheFilesAndShownTheLoading() {
        var root = Folder("root");
        Write(root, "a.sb", "module App\n");
        await using var client = new TestClient();
        await client.InitializeAsync(root, new {
            workspace = new { didChangeWatchedFiles = new { dynamicRegistration = true } },
            window = new { workDoneProgress = true }
        });
        await client.NotifyAsync("initialized", new { });

        var registration = await client.MessageAsync("client/registerCapability");
        var watchers = registration.Get("registrations")!.Value[0].Get("registerOptions")!.Value.Get("watchers")!.Value;
        Assert.Contains(watchers.EnumerateArray(), static w => w.GetString("globPattern") == "**/*.sb");
        Assert.Contains(watchers.EnumerateArray(), static w => w.GetString("globPattern") == "**/sable.toml");
    }

    [Fact]
    public async Task TheLoadingIsReportedAsProgress() {
        var root = Folder("root");
        for (var i = 0; i < 200; i++) {
            Write(root, $"f{i}.sb", $"module App\n\nfn f{i}() -> Int {{\n    return {i}\n}}\n");
        }

        // The loading waits for nothing of the client's; it is held back
        // here, so the progress is shown before it is over.
        using var gate = new ManualResetEventSlim();
        await using var client = new TestClient(projects: _ => {
            gate.Wait(TimeSpan.FromSeconds(30));
            return [];
        });
        await client.InitializeAsync(root, new { window = new { workDoneProgress = true } });
        await client.NotifyAsync("initialized", new { });

        var create = await client.MessageAsync("window/workDoneProgress/create");
        var token = create.GetString("token");
        var begin = await client.MessageAsync("$/progress", p => p.GetString("token") == token && p.Get("value")!.Value.GetString("kind") == "begin");
        Assert.Equal("Loading the workspace", begin.Get("value")!.Value.GetString("title"));
        gate.Set();
        var end = await client.MessageAsync("$/progress", p => p.GetString("token") == token && p.Get("value")!.Value.GetString("kind") == "end");
        Assert.Equal("200 file(s)", end.Get("value")!.Value.GetString("message"));
    }

    [Fact]
    public async Task HoverSeesWhatWasJustTypedWithoutWaitingForACompilation() {
        // A long delay: no compilation is built after the edit.
        await using var client = new TestClient(TimeSpan.FromSeconds(30));
        await client.InitializeAsync();
        const string uri = "file:///tmp/hover-typing.sb";
        const string text = "module App\n\nfn main() {\n    let a = 1\n    print(a)\n}\n";
        await client.OpenAsync(uri, text);
        await client.AtAsync("textDocument/hover", uri, text, text.IndexOf("a = 1", StringComparison.Ordinal));

        var at = text.IndexOf("    print(a)", StringComparison.Ordinal);
        const string typed = "    let fresh = \"x\"\n    print(fresh)\n";
        var edited = text.Insert(at, typed);
        await client.ChangeAsync(uri, 2, text, at, at, typed);
        var hover = await client.AtAsync("textDocument/hover", uri, edited, edited.IndexOf("fresh)", StringComparison.Ordinal));
        Assert.Contains("fresh: String", hover.Get("contents")!.Value.GetString("value"), StringComparison.Ordinal);
    }

    // ---- Formatting ----------------------------------------------------------

    const string Messy = "module App\n\nfn first()->Int{\nreturn   1\n}\n\nfn second()->Int{\nreturn   2\n}\n";

    static string Apply(string text, JsonElement edits) {
        var lines = LineIndex.Of(text);
        foreach (var edit in edits.EnumerateArray().Reverse()) {
            var range = edit.Get("range")!.Value;
            var start = lines.OffsetOf(new(range.Get("start")!.Value.GetInt("line")!.Value, range.Get("start")!.Value.GetInt("character")!.Value));
            var end = lines.OffsetOf(new(range.Get("end")!.Value.GetInt("line")!.Value, range.Get("end")!.Value.GetInt("character")!.Value));
            text = text[..start] + edit.GetString("newText") + text[end..];
        }

        return text;
    }

    [Fact]
    public async Task ARangeIsFormattedAlone() {
        await using var client = new TestClient();
        var capabilities = (await client.InitializeAsync()).Get("capabilities")!.Value;
        Assert.True(capabilities.GetBool("documentRangeFormattingProvider"));
        const string uri = "file:///tmp/range.sb";
        await client.OpenAsync(uri, Messy);

        // Part of the second function's body: its lines, no more.
        var lines = LineIndex.Of(Messy);
        var at = lines.PositionOf(Messy.IndexOf("return   2", StringComparison.Ordinal) + 3);
        var edits = await client.RequestAsync("textDocument/rangeFormatting", new {
            textDocument = new { uri },
            range = new { start = new { line = at.Line, character = at.Character }, end = new { line = at.Line, character = at.Character + 2 } },
            options = new { tabSize = 4, insertSpaces = true }
        });
        Assert.Equal(Messy.Replace("return   2", "    return 2", StringComparison.Ordinal), Apply(Messy, edits));

        var whole = await client.RequestAsync("textDocument/rangeFormatting", new {
            textDocument = new { uri },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 100, character = 0 } },
            options = new { tabSize = 4, insertSpaces = true }
        });
        Assert.Equal(SyntaxFormatter.Format(Messy), Apply(Messy, whole));
    }

    [Fact]
    public async Task AFileWithASyntaxErrorHasItsWholeDeclarationsFormatted() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/half-broken.sb";
        var text = Messy + "\nfn third( -> Int {\nreturn   3\n}\n";
        await client.OpenAsync(uri, text);

        var edits = await client.RequestAsync("textDocument/formatting", new { textDocument = new { uri }, options = new { tabSize = 4, insertSpaces = true } });
        var formatted = Apply(text, edits);
        Assert.Contains("fn first() -> Int {\n    return 1\n}", formatted, StringComparison.Ordinal);
        Assert.Contains("fn second() -> Int {\n    return 2\n}", formatted, StringComparison.Ordinal);
        Assert.EndsWith("\nfn third( -> Int {\nreturn   3\n}\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATypedBraceFormatsWhatItCloses() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/on-type.sb";
        await client.OpenAsync(uri, Messy);
        var brace = Messy.LastIndexOf('}') + 1;
        var edits = await client.RequestAsync("textDocument/onTypeFormatting", new {
            textDocument = new { uri },
            position = TestClient.Position(Messy, brace),
            ch = "}",
            options = new { tabSize = 4, insertSpaces = true }
        });
        Assert.Equal(Messy.Replace("fn second()->Int{\nreturn   2", "fn second() -> Int {\n    return 2", StringComparison.Ordinal), Apply(Messy, edits));
    }
}
