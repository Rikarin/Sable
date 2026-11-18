using System.Text.Json;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     The methods beyond the first set (#194): declaration, type
///     definition, implementation, highlights, workspace symbols, folding,
///     selection ranges, semantic tokens, quick fixes, completion
///     documentation, and the call and type hierarchies.
/// </summary>
public sealed class FeatureTests {
    const string Shapes = """
                          module App

                          import Sable.Collections

                          /// Something with an area.
                          protocol Shape {
                              fn area() -> Int
                          }

                          struct Square: Shape {
                              var side: Int

                              fn area() -> Int {
                                  return side * side
                              }
                          }

                          struct Rect: Shape {
                              var w: Int
                              var h: Int

                              fn area() -> Int {
                                  return w * h
                              }
                          }

                          // Two shapes' areas,
                          // added.
                          fn total(_ a: Square, _ b: Rect) -> Int {
                              return a.area() + b.area()
                          }

                          fn main() {
                              var s = Square(side: 2)
                              print(total(s, Rect(w: 1, h: 2)))
                          }
                          """;

    const string Uri = "file:///tmp/shapes.sb";

    static int At(string marker, int offset = 0) {
        var index = Shapes.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"no '{marker}'");
        return index + offset;
    }

    static int Line(string marker) => LineIndex.Of(Shapes).PositionOf(At(marker)).Line;

    static int StartLine(JsonElement located) => located.Get("range")!.Value.Get("start")!.Value.GetInt("line")!.Value;

    static async Task<TestClient> OpenAsync() {
        var client = new TestClient();
        await client.InitializeAsync();
        await client.OpenAsync(Uri, Shapes);
        return client;
    }

    [Fact]
    public async Task EveryMethodIsAnnounced() {
        await using var client = new TestClient();
        var capabilities = (await client.InitializeAsync()).Get("capabilities")!.Value;
        foreach (var provider in new[] {
                     "declarationProvider", "typeDefinitionProvider", "implementationProvider", "documentHighlightProvider",
                     "workspaceSymbolProvider", "foldingRangeProvider", "selectionRangeProvider", "callHierarchyProvider", "typeHierarchyProvider",
                 }) {
            Assert.True(capabilities.GetBool(provider), provider);
        }

        Assert.True(capabilities.Get("completionProvider")!.Value.GetBool("resolveProvider"));
        Assert.NotNull(capabilities.Get("semanticTokensProvider")?.Get("legend"));
        Assert.NotNull(capabilities.Get("codeActionProvider"));
    }

    [Fact]
    public async Task DeclarationAndTypeDefinition() {
        await using var client = await OpenAsync();
        var declaration = await client.AtAsync("textDocument/declaration", Uri, Shapes, At("total(s", 1));
        Assert.Equal(Line("fn total"), StartLine(declaration));

        // 's' is a Square: its type is declared on the struct's line.
        var type = await client.AtAsync("textDocument/typeDefinition", Uri, Shapes, At("total(s", 6));
        Assert.Equal(Line("struct Square"), StartLine(type));

        // A parameter's type, and a call's result's.
        var parameter = await client.AtAsync("textDocument/typeDefinition", Uri, Shapes, At("b.area()"));
        Assert.Equal(Line("struct Rect"), StartLine(parameter));
    }

    [Fact]
    public async Task ImplementationsOfAProtocolAndOfItsRequirement() {
        await using var client = await OpenAsync();
        var types = await client.AtAsync("textDocument/implementation", Uri, Shapes, At("protocol Shape", 10));
        Assert.Equal([Line("struct Square"), Line("struct Rect")], types.EnumerateArray().Select(StartLine).Order());

        var members = await client.AtAsync("textDocument/implementation", Uri, Shapes, At("fn area() -> Int\n", 4));
        Assert.Equal(2, members.GetArrayLength());
        Assert.All(members.EnumerateArray(), static m => Assert.True(StartLine(m) > 8));
    }

    [Fact]
    public async Task HighlightsAreTheUsesInTheFile() {
        await using var client = await OpenAsync();
        var highlights = await client.AtAsync("textDocument/documentHighlight", Uri, Shapes, At("side * side"));
        var kinds = highlights.EnumerateArray().Select(static h => h.GetInt("kind")).ToList();
        Assert.Equal(3, kinds.Count);
        Assert.Equal(1, kinds.Count(static k => k == 3));
    }

    [Fact]
    public async Task WorkspaceSymbolsAreFoundByTheirLetters() {
        await using var client = await OpenAsync();
        var symbols = await client.RequestAsync("workspace/symbol", new { query = "ar" });
        var names = symbols.EnumerateArray().Select(static s => (s.GetString("name"), s.GetString("containerName"))).ToList();
        Assert.Contains(("area", "Square"), names);
        Assert.Contains(("area", "Shape"), names);
        Assert.Equal("area", symbols[0].GetString("name"));
        Assert.DoesNotContain(names, static n => n.Item1 == "main");
    }

    [Fact]
    public async Task FoldingRangesFollowBracesImportsAndComments() {
        await using var client = await OpenAsync();
        var ranges = await client.RequestAsync("textDocument/foldingRange", new { textDocument = new { uri = Uri } });
        var folds = ranges.EnumerateArray().Select(static r => (r.GetInt("startLine"), r.GetInt("endLine"), r.GetString("kind"))).ToList();
        var square = Line("struct Square");
        Assert.Contains((square, Line("struct Rect") - 3, null), folds);
        Assert.Contains((Line("// Two"), Line("// added"), "comment"), folds);
    }

    [Fact]
    public async Task SelectionGrowsFromTheNameOutwards() {
        await using var client = await OpenAsync();
        var result = await client.RequestAsync("textDocument/selectionRange", new {
            textDocument = new { uri = Uri },
            positions = new[] { TestClient.Position(Shapes, At("side * side", 1)) }
        });

        var lengths = new List<int>();
        var lines = LineIndex.Of(Shapes);
        for (JsonElement? range = result[0]; range is { } r; range = r.Get("parent")) {
            var span = r.Get("range")!.Value;
            var start = lines.OffsetOf(new(span.Get("start")!.Value.GetInt("line")!.Value, span.Get("start")!.Value.GetInt("character")!.Value));
            var end = lines.OffsetOf(new(span.Get("end")!.Value.GetInt("line")!.Value, span.Get("end")!.Value.GetInt("character")!.Value));
            lengths.Add(end - start);
        }

        Assert.Equal(4, lengths[0]);
        Assert.Equal(lengths.Order(), lengths);
        Assert.True(lengths.Count >= 4);
    }

    [Fact]
    public async Task SemanticTokensClassifyNames() {
        await using var client = await OpenAsync();
        var tokens = await client.RequestAsync("textDocument/semanticTokens/full", new { textDocument = new { uri = Uri } });
        var data = tokens.Get("data")!.Value.EnumerateArray().Select(static d => d.GetInt32()).ToList();
        Assert.Equal(0, data.Count % 5);

        // Decoded back to absolute positions, by the legend's order.
        var found = new Dictionary<(int, int), (int Type, int Modifiers)>();
        var (line, character) = (0, 0);
        for (var i = 0; i < data.Count; i += 5) {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            found[(line, character)] = (data[i + 3], data[i + 4]);
        }

        (int, int) Of(int offset) {
            var position = LineIndex.Of(Shapes).PositionOf(offset);
            return (position.Line, position.Character);
        }

        Assert.Equal((5, 1), found[Of(At("protocol Shape", 9))]); // interface, declaration
        Assert.Equal(2, found[Of(At("struct Square", 7))].Type); // struct
        Assert.Equal(7, found[Of(At("a.area()"))].Type); // parameter
        Assert.Equal(12, found[Of(At("a.area()", 2))].Type); // method
        Assert.Equal(9, found[Of(At("side * side"))].Type); // property
        Assert.Equal(11, found[Of(At("total(s"))].Type); // function
        Assert.Equal(8, found[Of(At("total(s", 6))].Type); // variable
        Assert.Equal(8, found[Of(At("print"))].Modifiers & 8); // the library's
    }

    [Fact]
    public async Task LintsHaveQuickFixes() {
        await using var client = await OpenAsync();
        var diagnostics = await client.DiagnosticsAsync(Uri, static d => d.EnumerateArray().Any(static e => e.GetString("code") == "S3303"));
        var actions = await client.RequestAsync("textDocument/codeAction", new {
            textDocument = new { uri = Uri },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } },
            context = new { diagnostics }
        });

        var titles = actions.EnumerateArray().Select(static a => a.GetString("title")).ToList();
        Assert.Contains("Declare it with 'let'", titles);
        Assert.Contains("Allow 'var_never_mutated' in this declaration", titles);

        var let = actions.EnumerateArray().First(static a => a.GetString("title") == "Declare it with 'let'");
        var edit = let.Get("edit")!.Value.Get("changes")!.Value.Get(Uri)!.Value[0];
        Assert.Equal("let", edit.GetString("newText"));
        Assert.Equal(Line("var s ="), StartLine(edit));

        var allow = actions.EnumerateArray().First(static a => a.GetString("title")!.StartsWith("Allow", StringComparison.Ordinal));
        var inserted = allow.Get("edit")!.Value.Get("changes")!.Value.Get(Uri)!.Value[0];
        Assert.Equal("@allow(var_never_mutated)\n", inserted.GetString("newText"));
        Assert.Equal(Line("fn main"), StartLine(inserted));
    }

    [Fact]
    public async Task AQuickFixForAnUnusedParameterKeepsTheLabel() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/unused-parameter.sb";
        const string text = "module App\n\nfn scale(by factor: Int, _ ignored: Int) -> Int {\n    return 2\n}\n";
        await client.OpenAsync(uri, text);
        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.EnumerateArray().Count(static e => e.GetString("code") == "S3301") == 2);
        var actions = await client.RequestAsync("textDocument/codeAction", new {
            textDocument = new { uri },
            range = new { start = new { line = 2, character = 0 }, end = new { line = 2, character = 0 } },
            context = new { diagnostics }
        });

        var texts = actions.EnumerateArray()
            .Where(static a => a.GetBool("isPreferred"))
            .Select(static a => a.Get("edit")!.Value.Get("changes")!.Value.Get(uri)!.Value[0].GetString("newText"))
            .Order()
            .ToList();
        Assert.Equal(["_factor", "_ignored"], texts);
    }

    [Fact]
    public async Task ALintsOwnFixIsAQuickFix() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/self-assignment.sb";
        const string text = "module App\n\nfn clamp(_ value: Int, _ high: Int) -> Int {\n    var result = value\n    if result > high {\n        result = result\n    }\n    return result\n}\n";
        await client.OpenAsync(uri, text);
        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.EnumerateArray().Any(static e => e.GetString("code") == "S3391"));
        var actions = await client.RequestAsync("textDocument/codeAction", new {
            textDocument = new { uri },
            range = new { start = new { line = 5, character = 0 }, end = new { line = 5, character = 0 } },
            context = new { diagnostics }
        });

        var remove = actions.EnumerateArray().First(static a => a.GetString("title") == "Remove the assignment");
        Assert.True(remove.GetBool("isPreferred"));
        var edit = remove.Get("edit")!.Value.Get("changes")!.Value.Get(uri)!.Value[0];
        Assert.Equal("", edit.GetString("newText"));
        Assert.Equal(5, StartLine(edit));
        Assert.Equal(6, edit.Get("range")!.Value.Get("end")!.Value.GetInt("line"));
    }

    [Fact]
    public async Task ACompletionItemIsDocumentedWhenResolved() {
        await using var client = await OpenAsync();
        var completion = await client.AtAsync("textDocument/completion", Uri, Shapes, At("print(total"));
        var shape = completion.Get("items")!.Value.EnumerateArray().First(static i => i.GetString("label") == "Shape");
        Assert.Null(shape.Get("documentation"));
        var resolved = await client.RequestAsync("completionItem/resolve", shape);
        Assert.Contains("Something with an area.", resolved.Get("documentation")!.Value.GetString("value"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallHierarchyGoesBothWays() {
        await using var client = await OpenAsync();
        var prepared = await client.AtAsync("textDocument/prepareCallHierarchy", Uri, Shapes, At("fn total", 3));
        var item = prepared[0];
        Assert.Equal("total", item.GetString("name"));

        var incoming = await client.RequestAsync("callHierarchy/incomingCalls", new { item });
        Assert.Equal("main", incoming[0].Get("from")!.Value.GetString("name"));

        var outgoing = await client.RequestAsync("callHierarchy/outgoingCalls", new { item });
        // Square's area and Rect's, each called once.
        Assert.Equal(["App.Rect", "App.Square"], outgoing.EnumerateArray().Select(static c => c.Get("to")!.Value.GetString("detail")).Order());
        Assert.All(outgoing.EnumerateArray(), static c => Assert.Equal("area", c.Get("to")!.Value.GetString("name")));
        Assert.All(outgoing.EnumerateArray(), static c => Assert.Equal(1, c.Get("fromRanges")!.Value.GetArrayLength()));
    }

    [Fact]
    public async Task TypeHierarchyGoesBothWays() {
        await using var client = await OpenAsync();
        var prepared = await client.AtAsync("textDocument/prepareTypeHierarchy", Uri, Shapes, At("struct Square", 7));
        var square = prepared[0];
        var supertypes = await client.RequestAsync("typeHierarchy/supertypes", new { item = square });
        Assert.Equal("Shape", Assert.Single(supertypes.EnumerateArray()).GetString("name"));

        var shape = supertypes[0];
        var subtypes = await client.RequestAsync("typeHierarchy/subtypes", new { item = shape });
        Assert.Equal(["Rect", "Square"], subtypes.EnumerateArray().Select(static s => s.GetString("name")).Order());
    }
}
