using System.Text.Json;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     The server through its message loop, as an editor drives it:
///     initialise, open, edit, and ask.
/// </summary>
public sealed class LanguageServerTests : IDisposable {
    readonly string _root = Path.Combine(Path.GetTempPath(), "sable-lsp-tests", Guid.NewGuid().ToString("N"));

    public LanguageServerTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        try {
            Directory.Delete(_root, true);
        } catch (IOException) {
            // Left for the system to clean.
        }
    }

    const string Geometry = """
                            module App

                            /// A point in the plane.
                            public struct Point {
                                public var x: Int
                                public var y: Int

                                /// The sum of the coordinates.
                                public fn sum() -> Int {
                                    return x + y
                                }
                            }

                            public fn origin() -> Point {
                                return Point { x: 0, y: 0 }
                            }
                            """;

    const string Main = """
                        module App

                        fn main() {
                            let p = origin()
                            let total = p.sum()
                            print(total)
                        }
                        """;

    string Write(string name, string text) {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return Workspace.UriOf(path);
    }

    static int At(string text, string marker, int offset = 0) {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"no '{marker}'");
        return index + offset;
    }

    static IReadOnlyList<string> Labels(JsonElement completion) =>
        [..completion.Get("items")!.Value.EnumerateArray().Select(static i => i.GetString("label")!)];

    /// <summary>A workspace of two files on disk, the second open in the editor.</summary>
    async Task<(TestClient Client, string Geometry, string Main)> OpenWorkspaceAsync() {
        var geometry = Write("geometry.sab", Geometry);
        var main = Write("main.sab", Main);
        var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(main, Main);
        return (client, geometry, main);
    }

    // ---- Lifecycle ---------------------------------------------------------

    [Fact]
    public async Task InitializeAnnouncesWhatTheServerDoes() {
        await using var client = new TestClient();
        var result = await client.InitializeAsync();
        var capabilities = result.Get("capabilities")!.Value;
        Assert.Equal("utf-16", capabilities.GetString("positionEncoding"));
        Assert.Equal(2, capabilities.Get("textDocumentSync")!.Value.GetInt("change"));
        Assert.True(capabilities.GetBool("hoverProvider"));
        Assert.True(capabilities.Get("renameProvider")!.Value.GetBool("prepareProvider"));
        Assert.True(capabilities.GetBool("inlayHintProvider"));
        Assert.Equal(0, await client.ShutdownAsync());
    }

    [Fact]
    public async Task ExitWithoutShutdownIsAnError() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        await client.NotifyAsync("exit", null);
        Assert.Equal(1, await client.Running.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ARequestBeforeInitializeIsRefused() {
        await using var client = new TestClient();
        var response = await client.SendAsync("textDocument/hover", new { textDocument = new { uri = "file:///x.sab" }, position = new { line = 0, character = 0 } });
        Assert.Equal(-32002, response.Get("error")!.Value.GetInt("code"));
    }

    [Fact]
    public async Task AnUnknownMethodIsMethodNotFound() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        var response = await client.SendAsync("textDocument/nonsense", new { });
        Assert.Equal(-32601, response.Get("error")!.Value.GetInt("code"));
    }

    [Fact]
    public async Task CancellingIsHarmless() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/cancel.sab";
        await client.OpenAsync(uri, Main);

        // Whether the cancellation lands before the answer is a race; either
        // outcome is a proper response, and the server carries on.
        var pending = client.SendAsync("textDocument/hover", new { textDocument = new { uri }, position = TestClient.Position(Main, At(Main, "origin")) });
        await client.NotifyAsync("$/cancelRequest", new { id = 1 });
        await client.NotifyAsync("$/cancelRequest", new { id = 999 });
        var response = await pending;
        Assert.True(response.TryGetProperty("result", out _) || response.Get("error")?.GetInt("code") == -32800);
        Assert.Equal(0, await client.ShutdownAsync());
    }

    // ---- Diagnostics -------------------------------------------------------

    [Fact]
    public async Task DiagnosticsArePublishedAndClearedByAnEdit() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/broken.sab";
        var text = "module App\n\nfn f() -> Int {\n    let s: String = 1\n    return 2\n}\n";
        await client.OpenAsync(uri, text);

        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);
        var first = diagnostics[0];
        Assert.Equal(1, first.GetInt("severity"));
        Assert.StartsWith("S", first.GetString("code"), StringComparison.Ordinal);
        Assert.Equal(3, first.Get("range")!.Value.Get("start")!.Value.GetInt("line"));

        // Fix it with an incremental edit: replace '1' with '"one"'.
        var one = At(text, "= 1", 2);
        await client.ChangeAsync(uri, 2, text, one, one + 1, "\"one\"");
        await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() == 0);
    }

    [Fact]
    public async Task ASignatureProblemIsPublishedInItsFile() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/signature.sab";
        await client.OpenAsync(uri, "module App\n\nfn f(_ x: Nowhere) -> Int {\n    return 1\n}\n");
        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);
        Assert.Contains(diagnostics.EnumerateArray(), static d => d.GetString("message")!.Contains("Nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AParseErrorIsPublished() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/parse.sab";
        await client.OpenAsync(uri, "module App\n\nfn f( {\n");
        await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);
    }

    // ---- Hover, definition, references, rename ----------------------------

    [Fact]
    public async Task HoverShowsTheSignatureAndTheDocComment() {
        var (client, _, main) = await OpenWorkspaceAsync();
        await using var _ = client;
        var hover = await client.AtAsync("textDocument/hover", main, Main, At(Main, "p.sum()", 3));
        var value = hover.Get("contents")!.Value.GetString("value")!;
        Assert.Contains("fn sum() -> Int", value, StringComparison.Ordinal);
        Assert.Contains("The sum of the coordinates.", value, StringComparison.Ordinal);
        Assert.Contains("App.Point", value, StringComparison.Ordinal);

        var local = await client.AtAsync("textDocument/hover", main, Main, At(Main, "let p", 4));
        Assert.Contains("let p: Point", local.Get("contents")!.Value.GetString("value")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefinitionCrossesFiles() {
        var (client, geometry, main) = await OpenWorkspaceAsync();
        await using var _ = client;
        var definition = await client.AtAsync("textDocument/definition", main, Main, At(Main, "origin()", 2));
        Assert.Equal(geometry, definition.GetString("uri"));
        var start = definition.Get("range")!.Value.Get("start")!.Value;
        Assert.Equal(LineIndex.Of(Geometry).PositionOf(At(Geometry, "origin")).Line, start.GetInt("line"));
    }

    [Fact]
    public async Task DefinitionOfALibraryFunctionOpensACopyOfTheLibrary() {
        var (client, _, main) = await OpenWorkspaceAsync();
        await using var _ = client;
        var definition = await client.AtAsync("textDocument/definition", main, Main, At(Main, "print", 1));
        var path = Workspace.PathOf(definition.GetString("uri")!)!;
        Assert.True(File.Exists(path));
        Assert.Contains("print", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferencesFindEveryUseInEveryFile() {
        var (client, geometry, main) = await OpenWorkspaceAsync();
        await using var _ = client;
        var references = await client.AtAsync("textDocument/references", main, Main, At(Main, "p.sum()", 3), new { context = new { includeDeclaration = true } });
        var uris = references.EnumerateArray().Select(static r => r.GetString("uri")).ToList();
        Assert.Equal(2, uris.Count);
        Assert.Contains(geometry, uris);
        Assert.Contains(main, uris);

        var withoutDeclaration = await client.AtAsync("textDocument/references", main, Main, At(Main, "p.sum()", 3), new { context = new { includeDeclaration = false } });
        Assert.Single(withoutDeclaration.EnumerateArray());

        var point = await client.AtAsync("textDocument/references", geometry, Geometry, At(Geometry, "struct Point", 8), new { context = new { includeDeclaration = true } });
        Assert.Equal(3, point.GetArrayLength());
    }

    [Fact]
    public async Task RenameEditsEveryFile() {
        var (client, geometry, main) = await OpenWorkspaceAsync();
        await using var _ = client;
        var prepared = await client.AtAsync("textDocument/prepareRename", main, Main, At(Main, "origin()", 2));
        Assert.Equal("origin", prepared.GetString("placeholder"));

        var edit = await client.AtAsync("textDocument/rename", main, Main, At(Main, "origin()", 2), new { newName = "start" });
        var changes = edit.Get("changes")!.Value;
        Assert.Single(changes.Get(geometry)!.Value.EnumerateArray());
        Assert.Single(changes.Get(main)!.Value.EnumerateArray());
        Assert.All(changes.EnumerateObject().SelectMany(static p => p.Value.EnumerateArray()), static e => Assert.Equal("start", e.GetString("newText")));
    }

    [Fact]
    public async Task RenameRefusesTheLibraryAndKeywords() {
        var (client, _, main) = await OpenWorkspaceAsync();
        await using var _ = client;
        var refused = await client.SendAsync("textDocument/prepareRename", new { textDocument = new { uri = main }, position = TestClient.Position(Main, At(Main, "print", 1)) });
        var error = refused.Get("error")!.Value;
        Assert.Contains("standard library", error.GetString("message"), StringComparison.Ordinal);

        var keyword = await client.SendAsync("textDocument/rename", new { textDocument = new { uri = main }, position = TestClient.Position(Main, At(Main, "total", 1)), newName = "while" });
        Assert.NotNull(keyword.Get("error"));
    }

    // ---- Completion --------------------------------------------------------

    [Fact]
    public async Task CompletionOffersMembersAfterADot() {
        var (client, _, main) = await OpenWorkspaceAsync();
        await using var _ = client;

        // Type "p." on a new line, incrementally, and ask at once.
        var insertAt = At(Main, "    print(total)");
        var text = Main.Insert(insertAt, "    p.\n");
        await client.ChangeAsync(main, 2, Main, insertAt, insertAt, "    p.\n");
        var completion = await client.AtAsync("textDocument/completion", main, text, insertAt + "    p.".Length);
        var labels = Labels(completion);
        Assert.Contains("x", labels);
        Assert.Contains("sum", labels);
        Assert.DoesNotContain("let", labels);
        Assert.DoesNotContain("total", labels);
    }

    [Fact]
    public async Task CompletionOffersLocalsInScopeKeywordsAndTypes() {
        var (client, _, main) = await OpenWorkspaceAsync();
        await using var _ = client;
        var labels = Labels(await client.AtAsync("textDocument/completion", main, Main, At(Main, "print(total)")));
        Assert.Contains("p", labels);
        Assert.Contains("total", labels);
        Assert.Contains("Point", labels);
        Assert.Contains("origin", labels);
        Assert.Contains("print", labels);
        Assert.Contains("let", labels);

        // Filtered by what has been typed.
        var typed = Labels(await client.AtAsync("textDocument/completion", main, Main, At(Main, "total)", 2)));
        Assert.Contains("total", typed);
        Assert.DoesNotContain("p", typed);
    }

    [Fact]
    public async Task CompletionSeesWhatWasJustTypedWithoutWaitingForACompilation() {
        // A long delay: no compilation is built after the edits, so the
        // completion has to bind the edited function against the old one.
        await using var client = new TestClient(TimeSpan.FromSeconds(30));
        await client.InitializeAsync();
        const string uri = "file:///tmp/typing.sab";
        await client.OpenAsync(uri, Geometry + "\n" + Main.Replace("module App\n", "", StringComparison.Ordinal));
        var text = Geometry + "\n" + Main.Replace("module App\n", "", StringComparison.Ordinal);

        // Something must have built the first compilation.
        await client.AtAsync("textDocument/hover", uri, text, At(text, "origin()"));

        var insertAt = At(text, "    print(total)");
        var edited = text.Insert(insertAt, "    let fresh = 3\n    fr");
        await client.ChangeAsync(uri, 2, text, insertAt, insertAt, "    let fresh = 3\n    fr");
        var labels = Labels(await client.AtAsync("textDocument/completion", uri, edited, insertAt + "    let fresh = 3\n    fr".Length));
        Assert.Contains("fresh", labels);
    }

    [Fact]
    public async Task CompletionAfterImportOffersModules() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/imports.sab";
        const string text = "module App\nimport Sable.Co\n";
        await client.OpenAsync(uri, text);
        var labels = Labels(await client.AtAsync("textDocument/completion", uri, text, At(text, "Sable.Co", 8)));
        Assert.Contains("Sable.Collections", labels);
        Assert.Contains("Sable.Concurrency", labels);
        Assert.DoesNotContain("Sable.Text", labels);
    }

    [Fact]
    public async Task SignatureHelpShowsTheParameterBeingTyped() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/signature-help.sab";
        const string text = "module App\n\nfn add(_ a: Int, to b: Int) -> Int => a + b\n\nfn main() {\n    let n = add(1, 2)\n}\n";
        await client.OpenAsync(uri, text);
        var help = await client.AtAsync("textDocument/signatureHelp", uri, text, At(text, "add(1, ", 7));
        var signature = help.Get("signatures")!.Value[0];
        Assert.Equal("add(_ a: Int, to b: Int) -> Int", signature.GetString("label"));
        Assert.Equal(1, help.GetInt("activeParameter"));
    }

    // ---- Outline and inlay hints -------------------------------------------

    [Fact]
    public async Task DocumentSymbolsOutlineTheFile() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/outline.sab";
        await client.OpenAsync(uri, Geometry);
        var symbols = await client.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } });
        var point = symbols.EnumerateArray().First(static s => s.GetString("name") == "Point");
        Assert.Equal(23, point.GetInt("kind"));
        var members = point.Get("children")!.Value.EnumerateArray().Select(static c => c.GetString("name")).ToList();
        Assert.Equal(["x", "y", "sum"], members);
        Assert.Contains(symbols.EnumerateArray(), static s => s.GetString("name") == "origin");
    }

    [Fact]
    public async Task InlayHintsShowInferredTypes() {
        var (client, _, main) = await OpenWorkspaceAsync();
        await using var _ = client;
        var hints = await client.RequestAsync("textDocument/inlayHint", new {
            textDocument = new { uri = main },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 100, character = 0 } }
        });
        var labels = hints.EnumerateArray().Select(static h => h.GetString("label")).ToList();
        Assert.Contains(": Point", labels);
        Assert.Contains(": Int", labels);
        var point = hints.EnumerateArray().First(static h => h.GetString("label") == ": Point");
        Assert.Equal(TestClient.Position(Main, At(Main, "let p", 5)).ToString(), Shape(point.Get("position")!.Value));
    }

    static string Shape(JsonElement position) => new { line = position.GetInt("line")!.Value, character = position.GetInt("character")!.Value }.ToString()!;

    [Fact]
    public async Task InlayHintsShowInferredRoots() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/roots.sab";
        const string text = """
                            module Test

                            struct Pair {
                                var name: String
                                var count: Int
                            }

                            fn longest(ref a: String, ref b: String) -> ref String {
                                return a.count >= b.count ? a : b
                            }

                            fn nameOf(_ p: Pair&) -> String& {
                                return &p.name
                            }

                            fn main() {
                                var p = Pair { name: "n", count: 2 }
                                let q = &p
                                let x = "hello"
                                let y = "hi"
                                print(longest(x, y))
                                print(nameOf(q))
                            }
                            """;
        await client.OpenAsync(uri, text);
        var hints = await client.RequestAsync("textDocument/inlayHint", new {
            textDocument = new { uri },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 100, character = 0 } }
        });
        var labels = hints.EnumerateArray().Select(static h => h.GetString("label")).ToList();
        Assert.Contains("roots(a, b)", labels);
        Assert.Contains("roots(p)", labels);

        var longest = hints.EnumerateArray().First(static h => h.GetString("label") == "roots(a, b)");
        Assert.Equal(TestClient.Position(text, At(text, "-> ref String", "-> ref String".Length)).ToString(), Shape(longest.Get("position")!.Value));
        Assert.Contains("'a' and 'b'", longest.Get("tooltip")!.Value.GetString("value"), StringComparison.Ordinal);
    }

    // ---- Positions ---------------------------------------------------------

    [Fact]
    public async Task PositionsCountUtf16CodeUnits() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/utf16.sab";

        // An emoji is two UTF-16 code units: the column of 'shout' after it
        // is counted in those, not in characters or bytes.
        const string text = "module App\n\nfn shout() -> String => \"😀\" + \"!\"\n\nfn main() {\n    let s = \"😀😀\"; let t = shout()\n}\n";
        await client.OpenAsync(uri, text);
        var offset = At(text, "shout()\n}", 1);
        var position = TestClient.Position(text, offset);
        Assert.Equal(new { line = 5, character = 29 }.ToString(), position.ToString());

        var definition = await client.AtAsync("textDocument/definition", uri, text, offset);
        Assert.Equal(new { line = 2, character = 3 }.ToString(), Shape(definition.Get("range")!.Value.Get("start")!.Value));

        // An edit after an emoji lands where the client meant it.
        var at = At(text, "shout()\n}");
        await client.ChangeAsync(uri, 2, text, at, at + "shout".Length, "shout2");
        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);
        var start = diagnostics[0].Get("range")!.Value.Get("start")!.Value;
        Assert.Equal(new { line = 5, character = 28 }.ToString(), Shape(start));
    }
}
