using System.Text.Json;
using Sable.Syntax;
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
        var geometry = Write("geometry.sb", Geometry);
        var main = Write("main.sb", Main);
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
        var response = await client.SendAsync("textDocument/hover", new { textDocument = new { uri = "file:///x.sb" }, position = new { line = 0, character = 0 } });
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
        const string uri = "file:///tmp/cancel.sb";
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
        const string uri = "file:///tmp/broken.sb";
        var text = "module App\n\nfn f() -> Int {\n    let _s: String = 1\n    return 2\n}\n";
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
    public async Task AUseAfterMoveIsPublished() {
        // Move checking runs on SIL, which only a build used to lower (#130).
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/moved.sb";
        var text = "module App\n\n@noncopy struct H { var v: Int }\n\nfn take(_ h: consuming H) -> Int { return h.v }\n\nfn f() -> Int {\n    let h = H(v: 1)\n    let a = take(h)\n    return a + take(h)\n}\n";
        await client.OpenAsync(uri, text);

        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.EnumerateArray().Any(static e => e.GetString("code") == "S3057"));
        var moved = diagnostics.EnumerateArray().First(static e => e.GetString("code") == "S3057");
        Assert.Equal(9, moved.Get("range")!.Value.Get("start")!.Value.GetInt("line"));
    }

    [Fact]
    public async Task ASignatureProblemIsPublishedInItsFile() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/signature.sb";
        await client.OpenAsync(uri, "module App\n\nfn f(_ x: Nowhere) -> Int {\n    return 1\n}\n");
        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);
        Assert.Contains(diagnostics.EnumerateArray(), static d => d.GetString("message")!.Contains("Nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeclarationProblemIsPublishedInItsFileAlone() {
        // A declaration diagnostic carries a span but not its file. One
        // whose span starts where a token of another file does was published
        // in that file too, so this other file lines a token up with it.
        const string extension = "module App\n\nextension Int {\n    init(_ n: Int) { }\n}\n";
        var at = extension.IndexOf("init", StringComparison.Ordinal);
        var innocent = "module App\n" + new string(' ', at - "module App\n".Length) + "fn g() -> Int { return 1 }\n";
        Assert.Equal("fn", innocent.Substring(at, 2));

        var extensionUri = Write("extension.sb", extension);
        var innocentUri = Write("innocent.sb", innocent);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(extensionUri, extension);
        await client.OpenAsync(innocentUri, innocent);

        var placed = await client.DiagnosticsAsync(extensionUri, static d => d.GetArrayLength() > 0);
        Assert.Contains(placed.EnumerateArray(), static d => d.GetString("message")!.Contains("initialiser", StringComparison.Ordinal));
        var elsewhere = await client.DiagnosticsAsync(innocentUri);
        Assert.DoesNotContain(elsewhere.EnumerateArray(), static d => d.GetString("message")!.Contains("initialiser", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManifestsAreCheckedEachOnItsOwn() {
        // Two members' package.sb, each declaring `let manifest`: in modules
        // of their own, so they neither clash nor reach the packages' code.
        const string manifest = "import Sable.Build\n\nlet manifest = Package {\n    name: \"a\",\n    version: \"0.1.0\",\n}\n";
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        var first = Write("a/package.sb", manifest);
        Write("b/package.sb", manifest.Replace("\"a\"", "\"b\"", StringComparison.Ordinal));
        var wrong = manifest.Replace("version: \"0.1.0\"", "version: 1", StringComparison.Ordinal);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(first, manifest);
        await client.DiagnosticsAsync(first, static d => d.GetArrayLength() == 0);

        await client.ChangeAsync(first, 2, manifest, 0, manifest.Length, wrong);
        var errors = await client.DiagnosticsAsync(first, static d => d.GetArrayLength() > 0);
        Assert.Contains(errors.EnumerateArray(), static d => d.GetString("message")!.Contains("String", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AParseErrorIsPublished() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/parse.sb";
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
    public async Task HoverOnAnArrayMemberShowsItsSableDeclaration() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/arrays.sb";
        const string text = "module App\n\nfn f(_ xs: Int[>..]) -> Bool {\n    var ys = xs\n    ys.append(1)\n    return ys.contains(2)\n}\n";
        await client.OpenAsync(uri, text);

        var append = (await client.AtAsync("textDocument/hover", uri, text, At(text, "append", 1))).Get("contents")!.Value.GetString("value")!;
        Assert.Contains("fn append(element: Element)", append, StringComparison.Ordinal);
        Assert.Contains("Sable.Core.Element[>..]", append, StringComparison.Ordinal);
        Assert.Contains("added at the end", append, StringComparison.Ordinal);

        var contains = (await client.AtAsync("textDocument/hover", uri, text, At(text, "contains", 1))).Get("contents")!.Value.GetString("value")!;
        Assert.Contains("Sable.Core.Element[:]", contains, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoverOnAnUnsafeFunctionSaysItIsUnsafe() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/unsafe.sb";
        const string text = "module App\n\nunsafe static fn peek() -> Int {\n    return 1\n}\n\nfn f() -> Int {\n    unsafe {\n        return peek()\n    }\n}\n";
        await client.OpenAsync(uri, text);

        var peek = (await client.AtAsync("textDocument/hover", uri, text, At(text, "return peek()", 8))).Get("contents")!.Value.GetString("value")!;
        Assert.Contains("unsafe static fn peek() -> Int", peek, StringComparison.Ordinal);
    }

    // One reference type (#292): a 'T&' parameter and result show as written.
    [Fact]
    public async Task HoverShowsAReferenceAsTAmpersand() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/references.sb";
        const string text = "module App\n\nstruct Node { var value: Int }\n\nfn pick(_ a: Node&, _ b: Node&) -> Node& {\n    return a.value >= b.value ? a : b\n}\n\nfn f(_ n: Node) -> Int {\n    return pick(n, n).value\n}\n";
        await client.OpenAsync(uri, text);

        var pick = (await client.AtAsync("textDocument/hover", uri, text, At(text, "pick(n, n)", 1))).Get("contents")!.Value.GetString("value")!;
        Assert.Contains("fn pick(_ a: Node&, _ b: Node&) -> Node&", pick, StringComparison.Ordinal);
        Assert.DoesNotContain("ref", pick, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionNoLongerOffersRefOrBorrowing() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/keywords.sb";
        const string text = "module App\n\nfn f() {\n    \n}\n";
        await client.OpenAsync(uri, text);
        var labels = Labels(await client.AtAsync("textDocument/completion", uri, text, At(text, "    \n", 4)));
        Assert.Contains("inout", labels);
        Assert.DoesNotContain("ref", labels);
        Assert.DoesNotContain("borrowing", labels);
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
    public async Task ReferencesAfterAnEditInABodyNeedNoNewCompilation() {
        var geometry = Write("geometry.sb", Geometry);
        var main = Write("main.sb", Main);
        // No round of diagnostics runs: only the requests build analyses.
        await using var client = new TestClient(TimeSpan.FromHours(1));
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(main, Main);
        await client.AtAsync("textDocument/definition", main, Main, At(Main, "origin()", 2));
        var built = client.Server.AnalysesBuilt;

        // A statement added to main's body, before the use there was.
        var at = At(Main, "    let total");
        const string Added = "    let twice = p.sum() + p.sum()\n";
        var text = Main.Insert(at, Added);
        await client.ChangeAsync(main, 2, Main, at, at, Added);
        var references = await client.AtAsync("textDocument/references", main, text, At(text, "p.sum() +", 3), new { context = new { includeDeclaration = false } });
        Assert.Equal(built, client.Server.AnalysesBuilt);

        var lines = LineIndex.Of(text);
        var expected = new[] { At(text, "p.sum() +", 2), At(text, "+ p.sum()", 4), At(text, "total = p.sum()", 10) }
            .Select(o => lines.PositionOf(o)).ToList();
        var found = references.EnumerateArray()
            .Where(r => r.GetString("uri") == main)
            .Select(static r => r.Get("range")!.Value.Get("start")!.Value)
            .Select(static s => new LspPosition(s.GetInt("line")!.Value, s.GetInt("character")!.Value))
            .OrderBy(static p => p.Line)
            .ThenBy(static p => p.Character)
            .ToList();
        Assert.Equal(expected, found);

        // A local the edit declared renames where it is.
        var rename = await client.AtAsync("textDocument/rename", main, text, At(text, "twice", 1), new { newName = "doubled" });
        var edits = rename.Get("changes")!.Value.Get(main)!.Value;
        Assert.Equal(lines.PositionOf(At(text, "twice")), ReadStart(edits[0]));
        Assert.Equal(built, client.Server.AnalysesBuilt);

        // More edits, in another file and in two of its members: the body of
        // 'sum' calls itself, and 'origin' gets a statement before it.
        var geometryText = Geometry;
        await client.OpenAsync(geometry, geometryText);
        var inSum = At(geometryText, "        return x + y");
        const string Recursion = "        let again = sum()\n";
        await client.ChangeAsync(geometry, 2, geometryText, inSum, inSum, Recursion);
        geometryText = geometryText.Insert(inSum, Recursion);
        var inOrigin = At(geometryText, "    return Point {");
        const string Spare = "    let spare = 1\n";
        await client.ChangeAsync(geometry, 3, geometryText, inOrigin, inOrigin, Spare);
        geometryText = geometryText.Insert(inOrigin, Spare);
        var everywhere = await client.AtAsync("textDocument/references", main, text, At(text, "p.sum() +", 3), new { context = new { includeDeclaration = true } });
        Assert.Equal(built, client.Server.AnalysesBuilt);
        var geometryLines = LineIndex.Of(geometryText);
        var inGeometry = everywhere.EnumerateArray()
            .Where(r => r.GetString("uri") == geometry)
            .Select(static r => r.Get("range")!.Value.Get("start")!.Value)
            .Select(static s => new LspPosition(s.GetInt("line")!.Value, s.GetInt("character")!.Value))
            .OrderBy(static p => p.Line)
            .ToList();
        Assert.Equal([geometryLines.PositionOf(At(geometryText, "fn sum", 3)), geometryLines.PositionOf(At(geometryText, "again = sum", 8))], inGeometry);
        Assert.Equal(3, everywhere.EnumerateArray().Count(r => r.GetString("uri") == main));

        // An edit to a declaration does need one: main is renamed.
        var name = At(text, "fn main()", 7);
        await client.ChangeAsync(main, 3, text, name, name, "Program");
        var renamed = text.Insert(name, "Program");
        var after = await client.AtAsync("textDocument/references", main, renamed, At(renamed, "p.sum() +", 3), new { context = new { includeDeclaration = false } });
        Assert.True(client.Server.AnalysesBuilt > built);
        Assert.Equal(3, after.EnumerateArray().Count(r => r.GetString("uri") == main));
    }

    static LspPosition ReadStart(JsonElement edit) {
        var start = edit.Get("range")!.Value.Get("start")!.Value;
        return new(start.GetInt("line")!.Value, start.GetInt("character")!.Value);
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
        const string uri = "file:///tmp/typing.sb";
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
        const string uri = "file:///tmp/imports.sb";
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
        const string uri = "file:///tmp/signature-help.sb";
        const string text = "module App\n\nfn add(_ a: Int, to b: Int) -> Int => a + b\n\nfn main() {\n    let n = add(1, 2)\n}\n";
        await client.OpenAsync(uri, text);
        var help = await client.AtAsync("textDocument/signatureHelp", uri, text, At(text, "add(1, ", 7));
        var signature = help.Get("signatures")!.Value[0];
        Assert.Equal("add(_ a: Int, to b: Int) -> Int", signature.GetString("label"));
        Assert.Equal(1, help.GetInt("activeParameter"));
    }

    /// <summary>
    ///     An '@autoclosure' parameter's argument is written as the value it
    ///     gives, which the signature has to say, or a '() -> Bool' reads as
    ///     wanting a closure (#289).
    /// </summary>
    [Fact]
    public async Task SignatureHelpAndHoverSayAParameterIsAnAutoclosure() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/autoclosure.sb";
        const string text = "module App\n\nfn check(_ ok: @autoclosure () -> Bool) -> Bool => ok()\n\nfn main() {\n    let n = check(1 < 2)\n}\n";
        await client.OpenAsync(uri, text);
        var help = await client.AtAsync("textDocument/signatureHelp", uri, text, At(text, "check(1", 6));
        Assert.Equal("check(_ ok: @autoclosure () -> Bool) -> Bool", help.Get("signatures")!.Value[0].GetString("label"));

        var hover = await client.AtAsync("textDocument/hover", uri, text, At(text, "ok()", 0));
        Assert.Contains("ok: @autoclosure () -> Bool", hover.Get("contents")!.Value.GetString("value"), StringComparison.Ordinal);
    }

    // ---- A workspace with the library's source -------------------------------

    /// <summary>
    ///     The compiler's own repository open in an editor: lib/ is in the
    ///     workspace, and it is the library — not a second copy of it beside
    ///     the embedded one, which made every call into it ambiguous between
    ///     two identical declarations.
    /// </summary>
    [Fact]
    public async Task TheLibrarysOwnSourceInTheWorkspaceIsTheLibrary() {
        // The repository's root, with src/ beside lib/: tests/ has a lib/ of
        // its own, the library's tests, laid out by module as lib/ is.
        var repository = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(repository, "lib", "Sable.Core")) || !Directory.Exists(Path.Combine(repository, "src"))) {
            repository = Path.GetDirectoryName(repository) ?? throw new InvalidOperationException("no lib/ above the tests");
        }

        foreach (var module in new[] { "Sable.Core", "Sable.Text" }) {
            var target = Directory.CreateDirectory(Path.Combine(_root, "lib", module)).FullName;
            foreach (var file in Directory.GetFiles(Path.Combine(repository, "lib", module), "*.sb")) {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        const string source = """
                              module App

                              import Sable.Text

                              fn main() {
                                  let a = "  padded  ".trimmed()
                                  let b = escapedHtml(a)
                                  print(b)
                              }
                              """;
        var main = Write("main.sb", source);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(main, source);

        var diagnostics = await client.DiagnosticsAsync(main);
        Assert.True(diagnostics.GetArrayLength() == 0, diagnostics.GetRawText());

        var definition = await client.AtAsync("textDocument/definition", main, source, At(source, "trimmed()", 1));
        Assert.StartsWith(new Uri(Path.Combine(_root, "lib", "Sable.Core")).AbsoluteUri, definition.GetString("uri"), StringComparison.Ordinal);
    }

    // ---- Formatting ----------------------------------------------------------

    [Fact]
    public async Task FormattingRewritesOnlyTheLinesThatChange() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/format.sb";
        const string text = "module App\n\nfn first() -> Int {\n    return 1\n}\n\nfn second()->Int{\nreturn   2\n}\n\nfn third() -> Int {\n    return 3\n}\n";
        await client.OpenAsync(uri, text);
        var edits = await client.RequestAsync("textDocument/formatting", new {
            textDocument = new { uri },
            options = new { tabSize = 2, insertSpaces = false }
        });

        var edit = Assert.Single(edits.EnumerateArray());
        var range = edit.Get("range")!.Value;
        Assert.Equal(6, range.Get("start")!.Value.GetInt("line"));
        Assert.Equal(0, range.Get("start")!.Value.GetInt("character"));
        Assert.Equal(8, range.Get("end")!.Value.GetInt("line"));
        Assert.Equal(0, range.Get("end")!.Value.GetInt("character"));

        var lines = LineIndex.Of(text);
        var start = lines.OffsetOf(new(6, 0));
        var end = lines.OffsetOf(new(8, 0));
        var applied = text[..start] + edit.GetString("newText") + text[end..];
        Assert.Equal(SyntaxFormatter.Format(SyntaxTree.Parse(new SourceText(text, "format.sb"))), applied);
    }

    [Fact]
    public async Task FormattingLeavesAFormattedOrBrokenDocumentAlone() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string formatted = "file:///tmp/formatted.sb";
        const string broken = "file:///tmp/broken.sb";
        var text = SyntaxFormatter.Format(SyntaxTree.Parse(new SourceText(Geometry, "g.sb")));
        await client.OpenAsync(formatted, text);
        await client.OpenAsync(broken, "module App\n\nfn f( -> Int {\nreturn   1\n");
        foreach (var uri in new[] { formatted, broken }) {
            var edits = await client.RequestAsync("textDocument/formatting", new { textDocument = new { uri }, options = new { tabSize = 4, insertSpaces = true } });
            Assert.Empty(edits.EnumerateArray());
        }
    }

    // ---- Outline and inlay hints -------------------------------------------

    [Fact]
    public async Task DocumentSymbolsOutlineTheFile() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/outline.sb";
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
        const string uri = "file:///tmp/roots.sb";
        const string text = """
                            module Test

                            struct Pair {
                                var name: String
                                var count: Int
                            }

                            fn longest(a: String&, b: String&) -> String& {
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
        Assert.Equal(TestClient.Position(text, At(text, "-> String&", "-> String&".Length)).ToString(), Shape(longest.Get("position")!.Value));
        Assert.Contains("'a' and 'b'", longest.Get("tooltip")!.Value.GetString("value"), StringComparison.Ordinal);
    }

    // ---- Positions ---------------------------------------------------------

    [Fact]
    public async Task PositionsCountUtf16CodeUnits() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/utf16.sb";

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
        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.EnumerateArray().Any(static e => e.GetString("code") == "S2002"));
        var start = diagnostics.EnumerateArray().First(static e => e.GetString("code") == "S2002").Get("range")!.Value.Get("start")!.Value;
        Assert.True(new { line = 5, character = 28 }.ToString() == Shape(start), diagnostics.ToString());
    }
}
