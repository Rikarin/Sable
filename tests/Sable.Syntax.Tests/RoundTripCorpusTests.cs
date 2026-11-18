namespace Sable.Syntax.Tests;

/// <summary>
///     The round-trip invariant (docs/04 §0.5) over all the Sable the
///     repository has: every <c>.sb</c> file, and every <c>```sable</c>
///     block in its Markdown — the design documents, the specification, the
///     <c>sable explain</c> pages, the examples' sites — including those that
///     do not parse, since the tree must give back broken text as exactly as
///     good text.
/// </summary>
/// <remarks>
///     Each input is held to more than its text coming back: the node spans
///     tile their parents with nothing missing or overlapping. Then each is cut short
///     at a handful of points, as an editor sees a file being typed, and the
///     prefix must round-trip too.
/// </remarks>
public sealed class RoundTripCorpusTests {
    static readonly string[] Skipped = [".git", ".claude", "node_modules", "bin", "obj", "target", "artifacts", "TestResults"];

    /// <summary>Each input: a file's path, or <c>path#n</c> for a Markdown file's n-th Sable block.</summary>
    public static TheoryData<string> Inputs {
        get {
            var data = new TheoryData<string>();
            foreach (var input in All()) {
                data.Add(input.Name);
            }

            return data;
        }
    }

    static readonly Lazy<Dictionary<string, string>> Corpus = new(() => All().ToDictionary(static i => i.Name, static i => i.Text, StringComparer.Ordinal));

    static IEnumerable<(string Name, string Text)> All() {
        var root = TestHelpers.RepositoryRoot;
        foreach (var file in Files(root, "*.sb")) {
            yield return (Relative(file), File.ReadAllText(file));
        }

        foreach (var file in Files(root, "*.md")) {
            var blocks = Blocks(File.ReadAllText(file));
            for (var i = 0; i < blocks.Count; i++) {
                yield return ($"{Relative(file)}#{i}", blocks[i]);
            }
        }
    }

    static IEnumerable<string> Files(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(static part => Skipped.Contains(part)))
            .Order(StringComparer.Ordinal);

    static string Relative(string file) => Path.GetRelativePath(TestHelpers.RepositoryRoot, file).Replace('\\', '/');

    /// <summary>The <c>```sable</c> blocks of a Markdown file, in order, however indented.</summary>
    static List<string> Blocks(string markdown) {
        var blocks = new List<string>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++) {
            if (!lines[i].TrimStart().StartsWith("```sable", StringComparison.Ordinal)) {
                continue;
            }

            var end = Array.FindIndex(lines, i + 1, static l => l.Trim() == "```");
            if (end < 0) {
                break;
            }

            blocks.Add(string.Join('\n', lines[(i + 1)..end]) + "\n");
            i = end;
        }

        return blocks;
    }

    [Fact]
    public void TheCorpusIsAllOfIt() {
        var inputs = All().ToList();

        // A floor, so that a change to how inputs are found cannot quietly
        // shrink the corpus to nothing and pass.
        Assert.True(inputs.Count >= 500, $"only {inputs.Count} inputs");
        Assert.Contains(inputs, static i => i.Name.StartsWith("lib/", StringComparison.Ordinal));
        Assert.Contains(inputs, static i => i.Name.StartsWith("docs/diagnostics/", StringComparison.Ordinal));
        Assert.Contains(inputs, static i => i.Name.StartsWith("spec/", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void RoundTrips(string name) {
        var text = Corpus.Value[name];
        Check(name, text);

        // As it is being typed: cut at a few points spread through the text.
        for (var cut = 1; cut <= 7; cut++) {
            var at = text.Length * cut / 8;
            if (at > 0 && at < text.Length && !char.IsLowSurrogate(text[at])) {
                Check($"{name}, cut at {at}", text[..at]);
            }
        }
    }

    static void Check(string name, string text) {
        var tree = SyntaxTree.Parse(new SourceText(text, name));
        Assert.True(text == tree.ToFullString(), $"{name} does not round-trip");
        AssertTiles(tree.Root, name);
    }

    static void AssertTiles(SyntaxNode node, string name) {
        var expected = node.FullSpan.Start;
        foreach (var child in node.ChildNodesAndTokens()) {
            Assert.True(expected == child.FullSpan.Start, $"{name}: in {node.Kind}, a child at {child.FullSpan.Start} where {expected} was expected");
            expected = child.FullSpan.End;
            if (child.AsNode() is { } childNode) {
                AssertTiles(childNode, name);
            }
        }

        Assert.True(node.FullSpan.End == expected, $"{name}: {node.Kind} ends at {node.FullSpan.End}, its children at {expected}");
    }
}
