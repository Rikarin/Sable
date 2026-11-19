using System.Text.Json;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     The rest of the editor's methods (#194): quick fixes for the
///     compiler's own errors, each checked by applying it and seeing the
///     error go; code lenses; document links; and the fix-all command.
/// </summary>
public sealed class CommandTests {
    static bool Has(JsonElement diagnostics, string code) => diagnostics.EnumerateArray().Any(d => d.GetString("code") == code);

    /// <summary>The code actions for the document's diagnostics of a code, once it has one.</summary>
    static async Task<JsonElement> ActionsAsync(TestClient client, string uri, string code) {
        var diagnostics = await client.DiagnosticsAsync(uri, d => Has(d, code));
        return await client.RequestAsync("textDocument/codeAction", new {
            textDocument = new { uri },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } },
            context = new { diagnostics = diagnostics.EnumerateArray().Where(d => d.GetString("code") == code).ToArray() }
        });
    }

    /// <summary>A text with edits applied, the last first.</summary>
    static string Apply(string text, JsonElement edits) {
        var lines = LineIndex.Of(text);
        foreach (var edit in edits.EnumerateArray()
                     .Select(e => (Start: lines.OffsetOf(Position(e, "start")), End: lines.OffsetOf(Position(e, "end")), Text: e.GetString("newText")!))
                     .OrderByDescending(static e => e.Start)) {
            text = text[..edit.Start] + edit.Text + text[edit.End..];
        }

        return text;
    }

    static LspPosition Position(JsonElement edit, string which) {
        var at = edit.Get("range")!.Value.Get(which)!.Value;
        return new(at.GetInt("line")!.Value, at.GetInt("character")!.Value);
    }

    static JsonElement EditsOf(JsonElement action, string uri) => action.Get("edit")!.Value.Get("changes")!.Value.Get(uri)!.Value;

    /// <summary>Applies the action with a title, then checks the text has no errors left.</summary>
    static async Task<string> FixAsync(TestClient client, string uri, string text, string code, string title) {
        var actions = await ActionsAsync(client, uri, code);
        var action = actions.EnumerateArray().FirstOrDefault(a => a.GetString("title") == title);
        Assert.True(action.ValueKind == JsonValueKind.Object, $"no '{title}' in {actions}");
        var fixedText = Apply(text, EditsOf(action, uri));
        await AssertNoErrorsAsync(client, uri, text, fixedText);
        return fixedText;
    }

    /// <summary>The document's text replaced, and its next report free of errors.</summary>
    static async Task AssertNoErrorsAsync(TestClient client, string uri, string text, string fixedText) {
        await client.ChangeAsync(uri, 2, text, 0, text.Length, fixedText);
        var after = await client.MessageAsync("textDocument/publishDiagnostics", p => p.GetString("uri") == uri && p.GetInt("version") == 2);
        var diagnostics = after.Get("diagnostics")!.Value;
        Assert.True(diagnostics.EnumerateArray().All(static d => d.GetInt("severity") != 1), $"{fixedText}\n{diagnostics}");
    }

    [Fact]
    public async Task ACallThatCanThrowGetsTry() {
        const string Text = "module App\n\nfn risky() throws -> Int {\n    return 1\n}\n\nfn f() throws -> Int {\n    let x = risky()\n    return x\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/fix-try.sb";
        await client.OpenAsync(uri, Text);
        var fixedText = await FixAsync(client, uri, Text, "S3032", "Add 'try'");
        Assert.Contains("let x = try risky()", fixedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALoopOverASequenceThatCanThrowGetsForTry() {
        const string Text = "module App\n\nfn lines() throws -> some Sequence<String> {\n    yield \"a\"\n}\n\nfn f() throws {\n    for line in lines() {\n        print(line)\n    }\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/fix-for-try.sb";
        await client.OpenAsync(uri, Text);
        var fixedText = await FixAsync(client, uri, Text, "S3032", "Add 'try'");
        Assert.Contains("    for try line in lines() {", fixedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMatchGetsItsMissingCases() {
        const string Text = "module App\n\nunion Shape { Circle(F64), Rect(F64, F64), Empty }\n\nfn f(_ s: Shape) -> Int {\n    return match s {\n        case .Empty => 0\n    }\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/fix-match.sb";
        await client.OpenAsync(uri, Text);
        var fixedText = await FixAsync(client, uri, Text, "S6003", "Add the missing cases");
        Assert.Contains("        case .Empty => 0\n        case .Circle(_) => unreachable()\n        case .Rect(_, _) => unreachable()\n    }", fixedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATypeGetsAStubOfWhatItsProtocolRequires() {
        const string Text = "module App\n\nprotocol Shape {\n    fn area() -> Int\n    var name: String { get }\n}\n\nstruct Square: Shape {\n    var side: Int\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/fix-stub.sb";
        await client.OpenAsync(uri, Text);
        var diagnostics = await client.DiagnosticsAsync(uri, static d => Has(d, "S3023"));
        var actions = await client.RequestAsync("textDocument/codeAction", new {
            textDocument = new { uri },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } },
            context = new { diagnostics }
        });
        var stubs = actions.EnumerateArray().Where(static a => a.GetString("title")!.StartsWith("Add a stub", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, stubs.Count);

        // Both at once, as fix-all would.
        var text = Apply(Text, JsonDocument.Parse("[" + string.Join(",", stubs.Select(s => EditsOf(s, uri)[0].GetRawText())) + "]").RootElement);
        Assert.Contains("    fn area() -> Int {\n        unreachable()\n    }\n", text, StringComparison.Ordinal);
        Assert.Contains("    var name: String => unreachable()\n", text, StringComparison.Ordinal);
        await AssertNoErrorsAsync(client, uri, Text, text);
    }

    [Fact]
    public async Task ANameNotFoundGetsTheImportOfTheLibraryModuleWithIt() {
        const string Text = "module App\n\nfn f() -> Int {\n    return gcd(4, 6)\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/fix-import.sb";
        await client.OpenAsync(uri, Text);
        var fixedText = await FixAsync(client, uri, Text, "S2002", "Import Sable.Math");
        Assert.StartsWith("module App\nimport Sable.Math\n", fixedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongLabelGetsTheParametersLabel() {
        const string Text = "module App\n\nfn scale(_ v: Int, by factor: Int) -> Int {\n    return v * factor\n}\n\nfn f() -> Int {\n    return scale(3, with: 2)\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/fix-label.sb";
        await client.OpenAsync(uri, Text);
        var fixedText = await FixAsync(client, uri, Text, "S3007", "Label it 'by'");
        Assert.Contains("scale(3, by: 2)", fixedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixAllAppliesEveryFixThroughTheEditor() {
        const string Text = "module App\n\nfn risky() throws -> Int {\n    return 1\n}\n\nfn f() throws -> Int {\n    let a = risky()\n    let b = risky()\n    return a + b\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/fix-all.sb";
        await client.OpenAsync(uri, Text);
        await client.DiagnosticsAsync(uri, static d => Has(d, "S3032"));

        // The test client answers the server's requests with nothing, which
        // is not "applied": what matters is what it was asked to apply.
        await client.SendAsync("workspace/executeCommand", new { command = "sable.fixAll", arguments = new[] { uri } });
        var apply = await client.MessageAsync("workspace/applyEdit");
        var fixedText = Apply(Text, apply.Get("edit")!.Value.Get("changes")!.Value.Get(uri)!.Value);
        Assert.Contains("let a = try risky()\n    let b = try risky()", fixedText, StringComparison.Ordinal);

        var source = await client.RequestAsync("textDocument/codeAction", new {
            textDocument = new { uri },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } },
            context = new { diagnostics = Array.Empty<object>(), only = new[] { "source.fixAll" } }
        });
        Assert.Equal(fixedText, Apply(Text, EditsOf(source[0], uri)));
    }

    [Fact]
    public async Task ALensCountsTheReferencesToEachDeclaration() {
        const string Text = "module App\n\nstruct Point {\n    var x: Int\n}\n\nfn origin() -> Point {\n    return Point { x: 0 }\n}\n\nfn main() {\n    let a = origin()\n    let b = origin()\n    print(a.x + b.x)\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/lenses.sb";
        await client.OpenAsync(uri, Text);
        var lenses = await client.RequestAsync("textDocument/codeLens", new { textDocument = new { uri } });
        Assert.Equal(3, lenses.GetArrayLength());

        var titles = new List<string>();
        foreach (var lens in lenses.EnumerateArray()) {
            var resolved = await client.RequestAsync("codeLens/resolve", lens);
            var command = resolved.Get("command")!.Value;
            Assert.Equal("sable.showReferences", command.GetString("command"));
            titles.Add(command.GetString("title")!);
        }

        // Point: the result type and the literal; origin: two calls; main: none.
        Assert.Equal(["2 references", "2 references", "0 references"], titles);
    }

    [Fact]
    public async Task ImportsAndUrlsInCommentsAreLinks() {
        const string Text = "module App\n\nimport Sable.Math\n\n// See https://example.com/sable for more.\nfn f() -> Int {\n    return gcd(4, 6)\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/links.sb";
        await client.OpenAsync(uri, Text);
        var links = await client.RequestAsync("textDocument/documentLink", new { textDocument = new { uri } });
        var targets = links.EnumerateArray().Select(static l => l.GetString("target")!).ToList();
        Assert.Contains("https://example.com/sable", targets);
        Assert.Contains(targets, static t => t.StartsWith("file://", StringComparison.Ordinal) && t.EndsWith(".sb", StringComparison.Ordinal));
        var url = links.EnumerateArray().First(static l => l.GetString("target")!.StartsWith("https", StringComparison.Ordinal));
        Assert.Equal(4, url.Get("range")!.Value.Get("start")!.Value.GetInt("line"));
    }
}
