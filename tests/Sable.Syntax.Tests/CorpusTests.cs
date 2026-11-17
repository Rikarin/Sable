using System.Text;

namespace Sable.Syntax.Tests;

/// <summary>
/// The examples are the corpus: every one of them must parse as a whole file,
/// with no diagnostics, and round-trip exactly.
/// </summary>
public sealed class CorpusTests
{
    public static TheoryData<string> ExampleFiles
    {
        get
        {
            var data = new TheoryData<string>();
            var directory = Path.Combine(TestHelpers.RepositoryRoot, "examples");
            foreach (var file in Directory.GetFiles(directory, "*.sab", SearchOption.AllDirectories))
                data.Add(Path.GetFileName(file));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void ExampleParsesCleanlyAndRoundTrips(string fileName)
    {
        var path = Path.Combine(TestHelpers.RepositoryRoot, "examples", fileName);
        var source = new SourceText(File.ReadAllText(path), fileName);

        var tree = SyntaxTree.Parse(source);

        var errors = tree.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0,
            $"{fileName} did not parse:\n"
            + string.Join("\n\n", errors.Take(5).Select(e => e.Format(source))));

        Assert.Equal(source.Text, tree.ToFullString());
        Assert.IsType<CompilationUnitSyntax>(tree.Root);
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void ExampleHasAModuleDeclarationAndMembers(string fileName)
    {
        var path = Path.Combine(TestHelpers.RepositoryRoot, "examples", fileName);
        var tree = SyntaxTree.Parse(new SourceText(File.ReadAllText(path), fileName));

        var unit = Assert.IsType<CompilationUnitSyntax>(tree.Root);
        Assert.NotNull(unit.Module);
        Assert.NotEmpty(unit.Members);
    }

    [Fact]
    public void EveryExampleNodeSpanTilesItsParent()
    {
        var directory = Path.Combine(TestHelpers.RepositoryRoot, "examples");
        foreach (var file in Directory.GetFiles(directory, "*.sab"))
        {
            var tree = SyntaxTree.Parse(new SourceText(File.ReadAllText(file), file));
            AssertTiles(tree.Root, Path.GetFileName(file));
        }
    }

    private static void AssertTiles(SyntaxNode node, string file)
    {
        var expected = node.FullSpan.Start;
        foreach (var child in node.ChildNodesAndTokens())
        {
            Assert.True(expected == child.FullSpan.Start,
                $"{file}: {node.Kind} expected a child at {expected}, found {child.FullSpan.Start}");
            expected = child.FullSpan.End;
            if (child.AsNode() is { } childNode) AssertTiles(childNode, file);
        }
        Assert.Equal(node.FullSpan.End, expected);
    }

    [Fact]
    public void DocCommentsSurviveOnTheirDeclaration()
    {
        var path = Path.Combine(TestHelpers.RepositoryRoot, "examples", "02-shapes.sab");
        var tree = SyntaxTree.Parse(new SourceText(File.ReadAllText(path), "02-shapes.sab"));
        var unit = (CompilationUnitSyntax)tree.Root;

        var protocolDeclaration = unit.Members
            .OfType<ProtocolDeclarationSyntax>()
            .Single();

        var leading = protocolDeclaration.GetFirstToken().LeadingTrivia;
        Assert.Contains(leading, t => t.Kind == SyntaxKind.DocCommentTrivia);
    }

    [Fact]
    public void ReparsingTheFormattedOutputGivesTheSameTree()
    {
        var directory = Path.Combine(TestHelpers.RepositoryRoot, "examples");
        foreach (var file in Directory.GetFiles(directory, "*.sab"))
        {
            var source = File.ReadAllText(file);
            var first = SyntaxTree.Parse(new SourceText(source, file));
            var second = SyntaxTree.Parse(new SourceText(first.ToFullString(), file));
            Assert.Equal(TreeDump.Dump(first.Root), TreeDump.Dump(second.Root));
        }
    }

    [Fact]
    public void SpecSnippetsInTheSyntaxReferenceParse()
    {
        // Fenced `sable` blocks in spec/SYNTAX.md that are whole declarations
        // should parse. Blocks that are fragments are skipped by the filter.
        var path = Path.Combine(TestHelpers.RepositoryRoot, "spec", "SYNTAX.md");
        var lines = File.ReadAllLines(path);

        var blocks = new List<string>();
        var current = new StringBuilder();
        var inside = false;

        foreach (var line in lines)
        {
            if (!inside && line.StartsWith("```sable", StringComparison.Ordinal))
            {
                inside = true;
                current.Clear();
                continue;
            }
            if (inside && line.StartsWith("```", StringComparison.Ordinal))
            {
                inside = false;
                blocks.Add(current.ToString());
                continue;
            }
            if (inside) current.AppendLine(line);
        }

        Assert.NotEmpty(blocks);

        // Only blocks that are declarations all the way down: many snippets mix
        // a declaration with the statements that use it, and a compilation unit
        // holds declarations only.
        string[] declarationStarts =
            ["public ", "struct ", "union ", "enum ", "extension ", "protocol ", "class ",
             "actor ", "fn ", "alias ", "@"];

        var wholeDeclarations = blocks
            .Where(b =>
            {
                var topLevel = b.Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]))
                    .ToList();

                return topLevel.Count > 0
                    && topLevel.Exists(l => declarationStarts.Any(
                        d => l.StartsWith(d, StringComparison.Ordinal)))
                    && topLevel.TrueForAll(l =>
                        l.StartsWith('}')
                        || l.StartsWith("//", StringComparison.Ordinal)
                        || declarationStarts.Any(
                            d => l.StartsWith(d, StringComparison.Ordinal)));
            })
            .ToList();

        Assert.NotEmpty(wholeDeclarations);

        foreach (var block in wholeDeclarations)
        {
            var text = new SourceText(block, "SYNTAX.md");
            var tree = SyntaxTree.Parse(text);
            var errors = tree.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            Assert.True(errors.Count == 0,
                "spec snippet did not parse:\n" + block + "\n"
                + string.Join("\n", errors.Take(3).Select(e => e.Format(text))));
            Assert.Equal(block, tree.ToFullString());
        }
    }
}
