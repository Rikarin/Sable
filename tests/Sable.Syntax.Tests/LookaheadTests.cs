namespace Sable.Syntax.Tests;

/// <summary>
///     What the parser decides by parsing ahead (a metatype of a compound
///     type, a <c>where</c> item that is a constraint) is decided once for
///     each place in the text, however the questions nest.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class LookaheadTests {
    static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    /// <summary>
    ///     Far more than any of these takes when nothing doubles, on a busy
    ///     machine too, and far less than any takes when something does.
    /// </summary>
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    static void ParseInTime(string source) {
        SyntaxTree? tree = null;
        var thread = new Thread(() => tree = SyntaxTree.Parse(source)) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(Limit), $"still parsing after {Limit}: {source[..Math.Min(100, source.Length)]}");
        Assert.Equal(source, tree!.ToFullString());
    }

    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(100)]
    [InlineData(1000)]
    public void NestedIndexesOfAMetatype(int levels) =>
        ParseInTime($"module M\nfn f() {{ let y = {Repeat("n[", levels)}x.self");

    [Theory]
    [InlineData(24)]
    [InlineData(1000)]
    public void NestedIndexesOfAMetatypeThatClose(int levels) =>
        ParseInTime($"module M\nfn f() {{ let y = {Repeat("n[", levels)}x.self{Repeat("]", levels)} }}");

    /// <summary>Each shape with a <c>.self</c> at every level, so that every level asks, to the nesting limit and past it.</summary>
    [Theory]
    [InlineData("n[x.self + ", "", "]")]
    [InlineData("n[x.self, ", "", "]")]
    [InlineData("n[x.self, ", "", "")]
    [InlineData("n[", "x", "].self")]
    [InlineData("n[(", "x", ")].self")]
    [InlineData("n[..", "x", "].self")]
    [InlineData("[n[x.self + ", "1", "]: Int]()")]
    [InlineData("[", "Int", ": Int].self")]
    [InlineData("n[{ ", "x", " }].self")]
    [InlineData("n[x.self + { {{{{ n[[[[[[[[ ", "x.self", "")]
    [InlineData("n[x.self + { fn g<T>(_ t: T) where ", "T: P", " { } }].self")]
    [InlineData("n[\"\\(x[y.self])\" + y[", "x", "].self]")]
    [InlineData("(", "x", ").self")]
    [InlineData("a<n[", "x", "]>.self")]
    public void NestedQuestions(string open, string middle, string close) {
        foreach (var levels in (int[])[20, 60, 300]) {
            ParseInTime($"module M\nfn f() {{\n let y = {Repeat(open, levels)}{middle}{Repeat(close, levels)}\n}}\n");
        }
    }

    [Theory]
    [InlineData("n[{ fn g<T>() where ", "T", " { } }]: P")]
    [InlineData("n[{ fn g<T>() where ", "T", "")]
    public void NestedWhereClauses(string open, string middle, string close) {
        foreach (var levels in (int[])[20, 60, 300]) {
            ParseInTime($"module M\nfn f<T>() where {Repeat(open, levels)}{middle}{Repeat(close, levels)} {{ }}\n");
        }
    }

    [Fact]
    public void ALongChainOfComparisons() => ParseInTime($"module M\nfn f() {{ let y = {Repeat("a < ", 4000)}a }}\n");

    [Fact]
    public void ALongRunOfMetatypes() =>
        ParseInTime($"module M\nfn f() {{ let y = [{Repeat("Int?.self, [Int: List<List<Int>>].self, n[x.self], ", 4000)}] }}\n");

    [Theory]
    [InlineData("xs[[a.self, b.self].count].self")]
    [InlineData("Int[n[x]].self")]
    [InlineData("[Int: List<List<Int>>].self")]
    public void AQuestionWithinAnotherIsNotAnErrorOfTheOuterOne(string source) {
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        Assert.StartsWith("(MemberAccess (Type ", TreeDump.Dump(tree.Root), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNestingLimitIsStillReported() {
        var source = $"let y = {Repeat("n[", 300)}x{Repeat("]", 300)}.self";
        var tree = SyntaxTree.ParseStatement(source);
        Assert.Equal(source, tree.ToFullString());
        Assert.Single(tree.Diagnostics, static d => d.Code == ErrorCode.ERR_NestedTooDeeply);
    }

    /// <summary>
    ///     The answers remembered must not reach an incremental parse as
    ///     anything a full one would not have found.
    /// </summary>
    [Fact]
    public void AnIncrementalParseAnswersTheSame() {
        var source = $$"""
            module M
            fn a() { let y = {{Repeat("n[", 30)}}x.self{{Repeat("]", 30)}} }
            fn b<T>() where n[{ fn g<U>() where U: P { } }].count > 0 { let z = n[m[x.self]].self }
            fn c() { let y = n[x.self, {{Repeat("n[", 140)}}x.self

            fn d() { let t = [Int: List<List<Int>>].self }
            """;
        var tree = SyntaxTree.Parse(source);
        foreach (var insert in (string[])["]", "[", ".self", "{", "}", "x", ">", " "]) {
            for (var at = 9; at < source.Length; at += 29) {
                var change = new TextChange(new(at, 0), insert);
                var incremental = tree.WithChange(change);
                var full = SyntaxTree.Parse(tree.Text.WithChange(change), tree.Options);
                Assert.Equal(TreeDump.Dump(full.Root), TreeDump.Dump(incremental.Root));
                Assert.Equal(
                    full.Diagnostics.Select(static d => $"{d.Id}@{d.Span}"),
                    incremental.Diagnostics.Select(static d => $"{d.Id}@{d.Span}")
                );
            }
        }
    }
}
