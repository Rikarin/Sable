namespace Sable.Syntax.Tests;

/// <summary>
///     The formatter has two obligations, and both are checked here rather than
///     asserted on a sample of output: formatting is idempotent, and formatting
///     never changes the tree.
/// </summary>
public sealed class FormatterTests {
    static string Format(string source) => SyntaxFormatter.Format(source);

    static void AssertStable(string source) {
        var once = Format(source);
        var twice = Format(once);

        Assert.True(
            once == twice,
            $"formatting is not idempotent\n---- once ----\n{once}\n---- twice ----\n{twice}"
        );
    }

    static void AssertPreservesTree(string source) {
        var formatted = Format(source);

        var before = SyntaxTree.Parse(new(source, "before"));
        var after = SyntaxTree.Parse(new(formatted, "after"));

        Assert.True(
            TreeDump.Dump(before.Root) == TreeDump.Dump(after.Root),
            $"""
             formatting changed the tree
             ---- source ----
             {source}
             ---- formatted ----
             {formatted}
             """
        );
    }

    static void AssertKeepsComments(string source) {
        var formatted = Format(source);

        var before = SyntaxTree.Parse(new(source, "before"));
        var after = SyntaxTree.Parse(new(formatted, "after"));

        var commentsBefore = Comments(before);
        var commentsAfter = Comments(after);

        Assert.Equal(commentsBefore, commentsAfter);
    }

    static List<string> Comments(SyntaxTree tree) {
        var comments = new List<string>();
        foreach (var token in tree.Root.DescendantTokens().Append(tree.EndOfFileToken)) {
            foreach (var trivia in token.LeadingTrivia) {
                if (trivia.IsComment) {
                    comments.Add(trivia.Text.TrimEnd());
                }
            }

            foreach (var trivia in token.TrailingTrivia) {
                if (trivia.IsComment) {
                    comments.Add(trivia.Text.TrimEnd());
                }
            }
        }

        return comments;
    }

    // ---- The corpus --------------------------------------------------------

    public static TheoryData<string> ExampleFiles => CorpusTests.ExampleFiles;

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void FormattingTheCorpusIsIdempotent(string fileName) {
        var path = Path.Combine(TestHelpers.RepositoryRoot, "examples", fileName);
        AssertStable(File.ReadAllText(path));
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void FormattingTheCorpusPreservesTheTree(string fileName) {
        var path = Path.Combine(TestHelpers.RepositoryRoot, "examples", fileName);
        AssertPreservesTree(File.ReadAllText(path));
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void FormattingTheCorpusKeepsEveryComment(string fileName) {
        var path = Path.Combine(TestHelpers.RepositoryRoot, "examples", fileName);
        AssertKeepsComments(File.ReadAllText(path));
    }

    // ---- Normalisation -----------------------------------------------------

    [Theory]
    [InlineData("fn f(){}", "fn f() { }\n")]
    [InlineData("let   x=1", "let x = 1\n")]
    [InlineData("let x:Int=1", "let x: Int = 1\n")]
    [InlineData("let xs=[1,2,3]", "let xs = [1, 2, 3]\n")]
    [InlineData("let v=a.b.c(1,2)", "let v = a.b.c(1, 2)\n")]
    [InlineData("let t:List<Int>=x", "let t: List<Int> = x\n")]
    [InlineData("let r=0..<10", "let r = 0..<10\n")]
    [InlineData("let n = -x", "let n = -x\n")]
    [InlineData("let n = a - b", "let n = a - b\n")]
    [InlineData("let o:Int?=none", "let o: Int? = none\n")]
    [InlineData("let c=a ? b : d", "let c = a ? b : d\n")]
    [InlineData("let p=Point{x:1,y:2}", "let p = Point { x: 1, y: 2 }\n")]
    public void NormalisesSpacing(string source, string expected) => Assert.Equal(expected, Format(source));

    [Theory]
    [InlineData("extension < Element > Element [ > .. ] { }", "extension<Element> Element[>..] { }\n")]
    [InlineData("extension<Element:Equatable>Element[:]{}", "extension<Element: Equatable> Element[:] { }\n")]
    [InlineData("extension<T> T[>..<] where T:Hashable { }", "extension<T> T[>..<] where T: Hashable { }\n")]
    [InlineData("extension U8[:] { }", "extension U8[:] { }\n")]
    public void FormatsArrayExtensions(string source, string expected) {
        Assert.Equal(expected, Format(source));
        Assert.Equal(expected, Format(expected));
    }

    [Fact]
    public void KeepsAPropertyWrittenWithAnArrowOnItsLine() {
        const string source = "struct R {\n    var w: F64\n    var area: F64 =>   w * w\n    public var isZero: Bool => w == 0\n}\n";
        Assert.Equal("struct R {\n    var w: F64\n    var area: F64 => w * w\n    public var isZero: Bool => w == 0\n}\n", Format(source));
    }

    [Fact]
    public void IndentsBodies() {
        const string source = "fn f() { let x = 1\nlet y = 2 }";
        Assert.Equal("fn f() {\n    let x = 1\n    let y = 2\n}\n", Format(source));
    }

    [Fact]
    public void IndentsNestedBodies() {
        // The 'if' is written over two lines: one on one line would stay there.
        const string source = "struct S { fn f() { if x {\ng() } } }";
        Assert.Equal(
            "struct S {\n    fn f() {\n        if x {\n            g()\n        }\n    }\n}\n",
            Format(source)
        );
    }

    [Fact]
    public void CollapsesRunsOfBlankLinesToOne() {
        const string source = "fn a() { }\n\n\n\n\nfn b() { }";
        Assert.Equal("fn a() { }\n\nfn b() { }\n", Format(source));
    }

    [Fact]
    public void KeepsAuthorParagraphing() {
        const string source = "fn a() { }\n\nfn b() { }\nfn c() { }";
        Assert.Equal("fn a() { }\n\nfn b() { }\nfn c() { }\n", Format(source));
    }

    [Fact]
    public void EndsWithExactlyOneNewline() {
        Assert.Equal("fn f() { }\n", Format("fn f() { }"));
        Assert.Equal("fn f() { }\n", Format("fn f() { }\n\n\n"));
    }

    [Fact]
    public void FormatsAnEmptyFile() => Assert.Equal(string.Empty, Format(string.Empty));

    // ---- Comments ----------------------------------------------------------

    [Fact]
    public void KeepsDocCommentsOnTheirOwnLine() {
        const string source = "/// doc\nfn f() { }";
        Assert.Equal("/// doc\nfn f() { }\n", Format(source));
    }

    [Fact]
    public void KeepsTrailingComments() {
        const string source = "fn f() { let x = 1 // note\n}";
        Assert.Contains("// note", Format(source), StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsDisabledRegionsVerbatim() {
        const string source = "#if NOPE\nthis is ( nonsense\n#endif\nfn f() { }";
        var formatted = Format(source);
        Assert.Contains("this is ( nonsense", formatted, StringComparison.Ordinal);
        Assert.Contains("#if NOPE", formatted, StringComparison.Ordinal);
    }

    // ---- Stability over generated input ------------------------------------

    [Fact]
    public void RandomWhitespaceDoesNotChangeTheFormattedResult() {
        const string source = """
                              module A
                              public struct P { x: F64, y: F64 }
                              public fn f(_ a: Int, b: Int = 0) throws(E) -> Int {
                                  let xs = [1, 2, 3]
                                  for i in 0..<10 where i > 2 {
                                      xs.filter { $0 > i }
                                  }
                                  return match a {
                                      case 1 => 2
                                      default => 3
                                  }
                              }
                              """;

        var canonical = Format(source);

        var random = new Random(20261122);
        for (var i = 0; i < 50; i++) {
            var builder = new System.Text.StringBuilder();
            foreach (var c in source) {
                builder.Append(c);
                if (char.IsWhiteSpace(c) && random.Next(4) == 0) {
                    builder.Append(' ');
                }
            }

            Assert.Equal(canonical, Format(builder.ToString()));
        }
    }

    [Fact]
    public void MalformedInputStillFormatsAndStaysStable() {
        string[] sources = [
            "fn", "fn f(", "struct S {", "}}}", "let x =", "@", "match x {",
            "fn f() { let y = ", "#if A", "\"unterminated"
        ];

        foreach (var source in sources) {
            AssertStable(source);
        }
    }
}
