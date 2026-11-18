using System.Text.Json;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     Completion, inlay hints and references that know more (#195):
///     snippets, argument labels, implicit members, imports added; parameter
///     and closure hints; argument labels found as references.
/// </summary>
public sealed class AssistTests : IDisposable {
    readonly string _root = Directory.CreateTempSubdirectory("sable-lsp-assist-").FullName;

    public void Dispose() {
        try {
            Directory.Delete(_root, true);
        } catch (IOException) {
            // Left for the system to clean.
        }
    }

    string Write(string name, string text) {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return Workspace.UriOf(path);
    }

    const string Adding = """
                          module App

                          enum Color {
                              red,
                              green
                          }

                          fn paint(_ color: Color, times count: Int) -> Int {
                              return count
                          }

                          fn add(_ a: Int, to b: Int) -> Int {
                              return a + b
                          }

                          fn main() {
                              let n = add(1, to: 2)
                              print(n)
                          }
                          """;

    static object Snippets => new { textDocument = new { completion = new { completionItem = new { snippetSupport = true } } } };

    static JsonElement Item(JsonElement completion, string label) =>
        completion.Get("items")!.Value.EnumerateArray().First(i => i.GetString("label") == label);

    static IReadOnlyList<string> Labels(JsonElement completion) =>
        [..completion.Get("items")!.Value.EnumerateArray().Select(static i => i.GetString("label")!)];

    /// <summary>Opens <paramref name="text" /> with <paramref name="typed" /> inserted before a marker, and completes after it.</summary>
    static async Task<JsonElement> CompleteAsync(TestClient client, string uri, string text, string before, string typed) {
        await client.OpenAsync(uri, text);
        var at = text.IndexOf(before, StringComparison.Ordinal);
        var edited = text.Insert(at, typed);
        await client.ChangeAsync(uri, 2, text, at, at, typed);
        return await client.AtAsync("textDocument/completion", uri, edited, at + typed.Length);
    }

    [Fact]
    public async Task AFunctionIsCompletedAsACallWithPlaceholders() {
        await using var client = new TestClient();
        await client.InitializeAsync((string?)null, Snippets);
        var completion = await CompleteAsync(client, "file:///tmp/snippet.sb", Adding, "    print(n)", "    ad");
        var add = Item(completion, "add");
        Assert.Equal(2, add.GetInt("insertTextFormat"));
        Assert.Equal("add(${1:a}, to: ${2:b})$0", add.Get("textEdit")!.Value.GetString("newText"));
    }

    [Fact]
    public async Task WithoutSnippetsAFunctionIsItsName() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        var completion = await CompleteAsync(client, "file:///tmp/plain.sb", Adding, "    print(n)", "    ad");
        var add = Item(completion, "add");
        Assert.Null(add.Get("insertTextFormat"));
        Assert.Equal("add", add.Get("textEdit")!.Value.GetString("newText"));
    }

    [Fact]
    public async Task ArgumentLabelsAreOfferedFirst() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        var completion = await CompleteAsync(client, "file:///tmp/labels.sb", Adding, "    print(n)", "    let m = add(1, t");
        var labels = Labels(completion);
        Assert.Equal("to:", labels.OrderBy(l => Item(completion, l).GetString("sortText"), StringComparer.Ordinal).First());
        Assert.Equal("to: ", Item(completion, "to:").Get("textEdit")!.Value.GetString("newText"));
    }

    [Fact]
    public async Task AnImplicitMemberIsCompletedFromTheTypeWanted() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        foreach (var (typed, name) in new[] {
                     ("    let c: Color = .", "declaration"),
                     ("    let k = paint(.", "argument"),
                 }) {
            var uri = $"file:///tmp/implicit-{name}.sb";
            var completion = await CompleteAsync(client, uri, Adding, "    print(n)", typed);
            var labels = Labels(completion);
            Assert.Contains("red", labels);
            Assert.Contains("green", labels);
            Assert.DoesNotContain("let", labels);
        }
    }

    [Fact]
    public async Task ANameFromALibraryModuleNoFileImportsBringsItsImport() {
        const string main = "module App\n\nfn main() {\n    print(1)\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/library-import.sb";
        await client.OpenAsync(uri, main);
        var at = main.IndexOf("    print(1)", StringComparison.Ordinal);
        var edited = main.Insert(at, "    gc");
        await client.ChangeAsync(uri, 2, main, at, at, "    gc");

        // The library is indexed in the background, once something asks.
        JsonElement? gcd = null;
        for (var attempt = 0; attempt < 300 && gcd is null; attempt++) {
            var completion = await client.AtAsync("textDocument/completion", uri, edited, at + "    gc".Length);
            gcd = completion.Get("items")!.Value.EnumerateArray().Where(static i => i.GetString("label") == "gcd").Cast<JsonElement?>().FirstOrDefault();
            if (gcd is null) {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(gcd);
        var edit = gcd.Value.Get("additionalTextEdits")!.Value[0];
        Assert.Equal("import Sable.Math\n", edit.GetString("newText"));
        Assert.Equal(1, edit.Get("range")!.Value.Get("start")!.Value.GetInt("line"));
    }

    [Fact]
    public async Task ANameFromAModuleNotImportedBringsItsImport() {
        Write("util.sb", "module Util\n\n/// Twice its argument.\npublic fn twice(_ x: Int) -> Int {\n    return x * 2\n}\n\nfn hidden() -> Int {\n    return 0\n}\n");
        const string main = "module App\n\nfn main() {\n    print(1)\n}\n";
        var uri = Write("main.sb", main);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        var completion = await CompleteAsync(client, uri, main, "    print(1)", "    tw");
        var twice = Item(completion, "twice");
        var edit = twice.Get("additionalTextEdits")!.Value[0];
        Assert.Equal("import Util\n", edit.GetString("newText"));
        Assert.Equal(1, edit.Get("range")!.Value.Get("start")!.Value.GetInt("line"));
        Assert.DoesNotContain("hidden", Labels(await CompleteAsync(client, uri, main, "    print(1)", "    hi")));
    }

    [Fact]
    public async Task ParameterNamesAndClosureTypesAreHinted() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/hints.sb";
        const string text = """
                            module App

                            fn add(_ a: Int, to b: Int) -> Int {
                                return a + b
                            }

                            fn both(_ first: Int, _ second: Int) -> Int {
                                return first + second
                            }

                            fn mapped(_ items: String[>..], _ transform: (String) -> Int) -> Int[>..] {
                                var result: Int[>..] = []
                                for item in items {
                                    result.append(transform(item))
                                }

                                return result
                            }

                            fn main() {
                                let first = 1
                                print(add(1, to: 2))
                                print(both(first, 2))
                                let lengths = mapped(["a"]) { word in
                                    return word.count
                                }
                                print(lengths.count)
                            }
                            """;
        await client.OpenAsync(uri, text);
        var hints = await client.RequestAsync("textDocument/inlayHint", new {
            textDocument = new { uri },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 100, character = 0 } }
        });

        var found = hints.EnumerateArray()
            .Select(h => (Label: h.GetString("label")!, Offset: LineIndex.Of(text).OffsetOf(new(h.Get("position")!.Value.GetInt("line")!.Value, h.Get("position")!.Value.GetInt("character")!.Value))))
            .ToList();
        Assert.Contains(("a:", text.IndexOf("1, to: 2", StringComparison.Ordinal)), found);
        Assert.Contains(("second:", text.IndexOf("2))\n    let lengths", StringComparison.Ordinal)), found);

        // 'first' is the parameter's own name: nothing to add.
        Assert.DoesNotContain(found, static f => f.Label == "first:");
        Assert.Contains((": String", text.IndexOf("word in", StringComparison.Ordinal) + 4), found);
        Assert.Contains(found, static f => f.Label == "-> Int");
    }

    [Fact]
    public async Task AnArgumentsLabelIsAReferenceToItsParameter() {
        var area = Write("area.sb", "module App\n\npublic fn area(width: Int, height: Int) -> Int {\n    return width * height\n}\n");
        const string main = "module App\n\nfn main() {\n    print(area(width: 2, height: 3))\n}\n";
        var uri = Write("main.sb", main);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, main);
        await client.OpenAsync(area, File.ReadAllText(Workspace.PathOf(area)!));

        var text = File.ReadAllText(Workspace.PathOf(area)!);
        var references = await client.AtAsync("textDocument/references", area, text, text.IndexOf("width", StringComparison.Ordinal), new { context = new { includeDeclaration = true } });
        var places = references.EnumerateArray().Select(static r => r.GetString("uri")).ToList();
        Assert.Equal(2, places.Count(p => p == area));
        Assert.Equal(1, places.Count(p => p == uri));

        // Renamed, the label is too: the call still matches.
        var edit = await client.AtAsync("textDocument/rename", area, text, text.IndexOf("width", StringComparison.Ordinal), new { newName = "across" });
        var label = Assert.Single(edit.Get("changes")!.Value.Get(uri)!.Value.EnumerateArray());
        Assert.Equal(main.IndexOf("width", StringComparison.Ordinal) - main.LastIndexOf('\n', main.IndexOf("width", StringComparison.Ordinal)) - 1,
            label.Get("range")!.Value.Get("start")!.Value.GetInt("character"));

        // A parameter with a label of its own is not named by the label.
        const string labelled = "module App\n\nfn scale(by factor: Int) -> Int {\n    return factor\n}\n\nfn main() {\n    print(scale(by: 2))\n}\n";
        const string other = "file:///tmp/labelled.sb";
        await client.OpenAsync(other, labelled);
        var own = await client.AtAsync("textDocument/references", other, labelled, labelled.IndexOf("factor", StringComparison.Ordinal), new { context = new { includeDeclaration = true } });
        Assert.Equal(2, own.GetArrayLength());
    }
}
