using System.Text.Json;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     The editor's settings: lints and inlay hints on or off (#206).
/// </summary>
public sealed class SettingsTests {
    const string Unused = "module App\n\nfn main() {\n    let unused = 1\n    print(2)\n}\n";

    static bool HasLint(JsonElement diagnostics) => diagnostics.EnumerateArray().Any(static d => d.GetString("code") == "S3300");

    static Task<JsonElement> InitializeAsync(TestClient client, object? initializationOptions = null) =>
        client.RequestAsync("initialize", new {
            processId = (int?)null,
            capabilities = new { },
            initializationOptions
        });

    [Fact]
    public async Task LintsArePublishedWithTheDiagnostics() {
        await using var client = new TestClient();
        await InitializeAsync(client);
        const string uri = "file:///tmp/lints.sb";
        await client.OpenAsync(uri, Unused);

        var diagnostics = await client.DiagnosticsAsync(uri, HasLint);
        var lint = diagnostics.EnumerateArray().First(static d => d.GetString("code") == "S3300");
        Assert.Equal(2, lint.GetInt("severity"));
        Assert.Contains("[unused_local]", lint.GetString("message"), StringComparison.Ordinal);
        Assert.Equal(3, lint.Get("range")!.Value.Get("start")!.Value.GetInt("line"));
    }

    [Fact]
    public async Task LintsAreOffWhenTheEditorSaysSo() {
        await using var client = new TestClient();
        await InitializeAsync(client, new { lints = new { enable = false } });
        const string uri = "file:///tmp/quiet.sb";
        await client.OpenAsync(uri, Unused);
        Assert.Empty(await client.DiagnosticsAsync(uri).ContinueWith(static t => t.Result.EnumerateArray().ToList()));

        // Turned on again: published again, with them.
        await client.NotifyAsync("workspace/didChangeConfiguration", new { settings = new { sable = new { lints = new { enable = true } } } });
        await client.DiagnosticsAsync(uri, HasLint);

        // And off.
        await client.NotifyAsync("workspace/didChangeConfiguration", new { settings = new { sable = new { lints = false } } });
        await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() == 0);
    }

    [Fact]
    public async Task LintsAreNotReportedInAFileWithErrors() {
        await using var client = new TestClient();
        await InitializeAsync(client);
        const string uri = "file:///tmp/errors.sb";
        await client.OpenAsync(uri, "module App\n\nfn main() {\n    let unused = 1\n    let s: String = 2\n}\n");
        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);
        Assert.False(HasLint(diagnostics));
    }

    [Fact]
    public async Task InlayHintsAreOffWhenTheEditorSaysSo() {
        await using var client = new TestClient();
        await InitializeAsync(client, new { inlayHints = new { enable = false } });
        const string uri = "file:///tmp/hints.sb";
        const string text = "module App\n\nfn main() {\n    let total = 1 + 2\n    print(total)\n}\n";
        await client.OpenAsync(uri, text);
        var everything = new { start = new { line = 0, character = 0 }, end = new { line = 100, character = 0 } };

        Assert.Equal(0, (await client.RequestAsync("textDocument/inlayHint", new { textDocument = new { uri }, range = everything })).GetArrayLength());

        await client.NotifyAsync("workspace/didChangeConfiguration", new { settings = new { sable = new { inlayHints = new { enable = true } } } });
        var hints = await client.RequestAsync("textDocument/inlayHint", new { textDocument = new { uri }, range = everything });
        Assert.Contains(": Int", hints.EnumerateArray().Select(static h => h.GetString("label")));
    }
}
