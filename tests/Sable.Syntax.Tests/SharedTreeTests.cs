namespace Sable.Syntax.Tests;

/// <summary>
///     A tree is shared between threads: the standard library's trees are
///     parsed once and bound by every compilation, in parallel when tests or
///     an editor compile at once. Its red nodes are made lazily, and each must
///     come out once — the same node for every thread, and never null.
/// </summary>
public sealed class SharedTreeTests {
    [Fact]
    public void ThreadsWalkingOneTreeSeeTheSameNodes() {
        var source = string.Join(
            "\n",
            Enumerable.Range(0, 200).Select(static i => $"fn f{i}(_ a: Int, b: Int = {i}, c: String) -> Int {{ return [a, b, {i}].count }}")
        );

        for (var round = 0; round < 20; round++) {
            var tree = SyntaxTree.Parse(new SourceText("module M\n" + source, "shared.sb"));
            var walks = Enumerable.Range(0, 8)
                .AsParallel()
                .WithDegreeOfParallelism(8)
                .Select(_ => tree.Root.DescendantNodes().ToList())
                .ToList();

            foreach (var walk in walks.Skip(1)) {
                Assert.Equal(walks[0].Count, walk.Count);
                for (var i = 0; i < walk.Count; i++) {
                    Assert.Same(walks[0][i], walk[i]);
                }
            }
        }
    }
}
