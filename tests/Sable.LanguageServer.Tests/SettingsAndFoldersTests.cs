using System.Text.Json;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     The editor's settings — lints and inlay hints on or off — and a
///     workspace of several folders (#206).
/// </summary>
public sealed class SettingsAndFoldersTests : IDisposable {
    readonly string _root = Path.Combine(Path.GetTempPath(), "sable-lsp-tests", Guid.NewGuid().ToString("N"));

    public SettingsAndFoldersTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        try {
            Directory.Delete(_root, true);
        } catch (IOException) {
            // Left for the system to clean.
        }
    }

    const string Unused = "module App\n\nfn main() {\n    let unused = 1\n    print(2)\n}\n";

    static bool HasLint(JsonElement diagnostics) => diagnostics.EnumerateArray().Any(static d => d.GetString("code") == "S3300");

    static Task<JsonElement> InitializeAsync(TestClient client, object? initializationOptions = null, IEnumerable<string>? folders = null) =>
        client.RequestAsync("initialize", new {
            processId = (int?)null,
            workspaceFolders = folders?.Select(static f => new { uri = Workspace.UriOf(f), name = Path.GetFileName(f) }).ToArray(),
            capabilities = new { },
            initializationOptions
        });

    string Folder(string name, params (string File, string Text)[] files) {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var (file, text) in files) {
            File.WriteAllText(Path.Combine(folder, file), text);
        }

        return folder;
    }

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

    [Fact]
    public async Task EachFolderOfAWorkspaceIsItsOwnProgram() {
        // Two folders of loose files that each declare 'helper': compiled
        // together, that would be declared twice over.
        var first = Folder("first", ("util.sb", "module App\n\npublic fn helper() -> Int {\n    return 1\n}\n"));
        var second = Folder("second", ("util.sb", "module App\n\npublic fn helper() -> Int {\n    return 2\n}\n"));
        await using var client = new TestClient();
        await InitializeAsync(client, folders: [first, second]);

        var main = Workspace.UriOf(Path.Combine(second, "main.sb"));
        const string text = "module App\n\nfn main() {\n    print(helper())\n}\n";
        await client.OpenAsync(main, text);
        Assert.Equal(0, (await client.DiagnosticsAsync(main)).GetArrayLength());

        // And a name resolves to the declaration in the file's own folder.
        var definition = await client.AtAsync("textDocument/definition", main, text, text.IndexOf("helper", StringComparison.Ordinal));
        var target = definition.ValueKind == JsonValueKind.Array ? definition[0] : definition;
        Assert.Equal(Workspace.UriOf(Path.Combine(second, "util.sb")), target.GetString("uri"));
    }

    [Fact]
    public async Task AFolderAddedLaterIsRead() {
        var first = Folder("first", ("other.sb", "module App\n\npublic fn other() -> Int {\n    return 1\n}\n"));
        var added = Folder("added", ("util.sb", "module App\n\npublic fn helper() -> Int {\n    return 1\n}\n"));
        await using var client = new TestClient();
        await InitializeAsync(client, folders: [first]);

        var main = Workspace.UriOf(Path.Combine(added, "main.sb"));
        const string text = "module App\n\nfn main() {\n    print(helper())\n}\n";
        await client.OpenAsync(main, text);
        // 'helper' is in a file of a folder the server does not know yet.
        await client.DiagnosticsAsync(main, static d => d.GetArrayLength() > 0);

        await client.NotifyAsync("workspace/didChangeWorkspaceFolders", new {
            @event = new {
                added = new[] { new { uri = Workspace.UriOf(added), name = "added" } },
                removed = Array.Empty<object>()
            }
        });
        await client.DiagnosticsAsync(main, static d => d.GetArrayLength() == 0);

        // Removed again: its files on disk go, and 'helper' with them.
        await client.NotifyAsync("workspace/didChangeWorkspaceFolders", new {
            @event = new {
                added = Array.Empty<object>(),
                removed = new[] { new { uri = Workspace.UriOf(added), name = "added" } }
            }
        });
        await client.DiagnosticsAsync(main, static d => d.GetArrayLength() > 0);
    }

    [Fact]
    public async Task TheServerSaysItTakesWorkspaceFolders() {
        await using var client = new TestClient();
        var result = await InitializeAsync(client);
        var folders = result.Get("capabilities")!.Value.Get("workspace")!.Value.Get("workspaceFolders")!.Value;
        Assert.True(folders.GetBool("supported"));
        Assert.True(folders.GetBool("changeNotifications"));
    }
}
