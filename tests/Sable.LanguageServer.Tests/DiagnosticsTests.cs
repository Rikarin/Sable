using System.Text.Json;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     Diagnostics beyond the type errors of open documents (#193): the
///     lints, files nobody opened, links to the long-form pages, and the
///     pull model.
/// </summary>
public sealed class DiagnosticsTests : IDisposable {
    readonly string _root = Path.Combine(Path.GetTempPath(), "sable-lsp-tests", Guid.NewGuid().ToString("N"));

    public DiagnosticsTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        try {
            Directory.Delete(_root, true);
        } catch (IOException) {
            // Left for the system to clean.
        }
    }

    string Write(string name, string text) {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return Workspace.UriOf(path);
    }

    const string Unused = "module App\n\nfn area(_ width: Int, _ height: Int) -> Int {\n    let perimeter = 2 * (width + height)\n    return width * height\n}\n";

    const string Moved = "module App\n\n@noncopy struct H { var v: Int }\n\nfn take(_ h: consuming H) -> Int { return h.v }\n\nfn f() -> Int {\n    let h = H(v: 1)\n    let a = take(h)\n    return a + take(h)\n}\n";

    static bool Has(JsonElement diagnostics, string code) => diagnostics.EnumerateArray().Any(d => d.GetString("code") == code);

    /// <summary>What a client that pulls diagnostics says it can do.</summary>
    static object Pulling(bool refresh) =>
        new { textDocument = new { diagnostic = new { dynamicRegistration = false } }, workspace = new { diagnostics = new { refreshSupport = refresh } } };

    [Fact]
    public async Task LintsArePublishedWithALinkAndFaded() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/lint.sb";
        await client.OpenAsync(uri, Unused);

        var diagnostics = await client.DiagnosticsAsync(uri, static d => Has(d, "S3300"));
        var unused = diagnostics.EnumerateArray().Single(static d => d.GetString("code") == "S3300");
        Assert.Equal(2, unused.GetInt("severity"));
        Assert.Equal(3, unused.Get("range")!.Value.Get("start")!.Value.GetInt("line"));
        Assert.EndsWith("docs/diagnostics/S3300.md", unused.Get("codeDescription")!.Value.GetString("href"), StringComparison.Ordinal);
        Assert.Equal([1], unused.Get("tags")!.Value.EnumerateArray().Select(static t => t.GetInt32()));
    }

    [Fact]
    public async Task AFileWithErrorsIsNotLinted() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/lint-broken.sb";
        await client.OpenAsync(uri, Unused.Replace("return width * height", "return nowhere", StringComparison.Ordinal));

        // The lowering round comes after the type errors; wait for it too.
        var diagnostics = await client.DiagnosticsAsync(uri, static d => Has(d, "S2002") || Has(d, "S2000"));
        await Task.Delay(500);
        Assert.DoesNotContain(client.Sent("textDocument/publishDiagnostics"),
            static n => n.Get("params")!.Value.GetString("uri") == uri && Has(n.Get("params")!.Value.Get("diagnostics")!.Value, "S3300"));
        Assert.True(diagnostics.GetArrayLength() > 0);
    }

    [Fact]
    public async Task APackagesLintTableSetsTheLevels() {
        const string Scaled = "module App\n\nfn scale(_ factor: Int, _ ignored: Int) -> Int {\n    let spare = factor\n    return factor * 2\n}\n";
        var main = Write("app/src/main.sb", Scaled);
        var lints = Symbols.LintConfiguration.Default.With("unused_local", Symbols.LintLevel.Allow).With("unused_parameter", Symbols.LintLevel.Error);
        var app = new LanguageProject("app", Path.Combine(_root, "app"), [], []) { Lints = lints };
        await using var client = new TestClient(projects: _ => [app]);
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(main, Scaled);

        var diagnostics = await client.DiagnosticsAsync(main, static d => Has(d, "S3301"));
        Assert.False(Has(diagnostics, "S3300"));
        Assert.Equal(1, diagnostics.EnumerateArray().Single(static d => d.GetString("code") == "S3301").GetInt("severity"));
    }

    [Fact]
    public async Task AFileNobodyOpenedIsDiagnosed() {
        var closed = Write("closed.sb", "module App\n\nfn f() -> Int {\n    let s: String = 1\n    return 2\n}\n");
        var moved = Write("moved.sb", Moved);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });

        var diagnostics = await client.DiagnosticsAsync(closed, static d => d.GetArrayLength() > 0);
        Assert.Equal(3, diagnostics[0].Get("range")!.Value.Get("start")!.Value.GetInt("line"));
        var published = await client.MessageAsync("textDocument/publishDiagnostics", p => p.GetString("uri") == closed);
        Assert.Null(published.Get("version"));

        // Fixed on disk, and the editor told so: cleared.
        File.WriteAllText(Workspace.PathOf(closed)!, "module App\n\nfn f() -> Int {\n    return 2\n}\n");
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = closed, type = 2 } } });
        await client.DiagnosticsAsync(closed, static d => d.GetArrayLength() == 0);

        // And deleted: cleared too.
        File.Delete(Workspace.PathOf(moved)!);
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = moved, type = 3 } } });
        await client.DiagnosticsAsync(moved, static d => d.GetArrayLength() == 0);
    }

    [Fact]
    public async Task ClosingAFileOfTheWorkspaceKeepsItsDiagnosticsFromDisk() {
        const string broken = "module App\n\nfn f() -> Int {\n    let s: String = 1\n    return 2\n}\n";
        var uri = Write("kept.sb", broken);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, "module App\n\nfn f() -> Int {\n    return 2\n}\n");
        await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() == 0);

        // Closed without saving: what is on disk is what is wrong.
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri } });
        await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);

        // A file outside the workspace is forgotten on closing.
        const string loose = "file:///tmp/loose-closed.sb";
        await client.OpenAsync(loose, broken);
        await client.DiagnosticsAsync(loose, static d => d.GetArrayLength() > 0);
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = loose } });
        await client.DiagnosticsAsync(loose, static d => d.GetArrayLength() == 0);
    }

    [Fact]
    public async Task AClientThatPullsIsAnsweredAndNotPushed() {
        await using var client = new TestClient();
        var capabilities = (await client.InitializeAsync((string?)null, Pulling(false))).Get("capabilities")!.Value;
        var provider = capabilities.Get("diagnosticProvider")!.Value;
        Assert.True(provider.GetBool("interFileDependencies"));
        Assert.True(provider.GetBool("workspaceDiagnostics"));

        const string uri = "file:///tmp/pulled.sb";
        await client.OpenAsync(uri, Moved);

        // Without refresh, a pull waits for the SIL checks.
        var report = await client.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri } });
        Assert.Equal("full", report.GetString("kind"));
        Assert.True(Has(report.Get("items")!.Value, "S3057"), report.ToString());

        var again = await client.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri }, previousResultId = report.GetString("resultId") });
        Assert.Equal("unchanged", again.GetString("kind"));
        Assert.Equal(report.GetString("resultId"), again.GetString("resultId"));

        await Task.Delay(500);
        Assert.Empty(client.Sent("textDocument/publishDiagnostics"));
    }

    [Fact]
    public async Task AClientThatPullsIsToldWhenTheChecksAreIn() {
        var closed = Write("closed.sb", Unused.Replace("module App", "module Other", StringComparison.Ordinal));
        var open = Write("open.sb", Moved);
        await using var client = new TestClient();
        await client.InitializeAsync(_root, Pulling(true));
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(open, Moved);

        // The type errors are refreshed first, the checks on SIL after.
        JsonElement report;
        for (var refreshes = 1;; refreshes++) {
            var count = refreshes;
            await client.MessageAsync("workspace/diagnostic/refresh", _ => client.Sent("workspace/diagnostic/refresh").Count >= count);
            report = await client.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri = open } });
            if (Has(report.Get("items")!.Value, "S3057")) {
                break;
            }
        }

        // The files nobody opened are in the workspace's report; the open one is not.
        var workspace = await client.RequestAsync("workspace/diagnostic", new { previousResultIds = Array.Empty<object>() });
        var items = workspace.Get("items")!.Value.EnumerateArray().ToList();
        Assert.DoesNotContain(items, static i => i.GetString("uri")!.EndsWith("open.sb", StringComparison.Ordinal));
        var item = items.Single(i => i.GetString("uri") == closed);
        Assert.Equal("full", item.GetString("kind"));
        Assert.True(item.Get("items")!.Value.GetArrayLength() > 0);

        var unchanged = await client.RequestAsync("workspace/diagnostic", new { previousResultIds = new[] { new { uri = closed, value = item.GetString("resultId") } } });
        Assert.Equal("unchanged", unchanged.Get("items")!.Value.EnumerateArray().Single(i => i.GetString("uri") == closed).GetString("kind"));
    }
}
