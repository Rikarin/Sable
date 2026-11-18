using Sable.Driver.Commands;
using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The lints over real code — the corpus, the examples, the standard
///     library — and what they say checked against the compiler: a
///     <c>var</c> reported as never changed compiles as a <c>let</c>.
/// </summary>
public sealed class LintCorpusTests {
    static string Root {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sable.slnx"))) {
                dir = dir.Parent;
            }

            return dir!.FullName;
        }
    }

    public static TheoryData<string> Programs {
        get {
            var data = new TheoryData<string>();
            foreach (var directory in new[] { "tests/corpus", "examples", "tests/programs" }) {
                foreach (var file in Directory.GetFiles(Path.Combine(Root, directory), "*.sb", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
                    data.Add(Path.GetRelativePath(Root, file));
                }
            }

            return data;
        }
    }

    static (Compilation Compilation, SyntaxTree Tree) Compile(string text) {
        var tree = SyntaxTree.Parse(new SourceText(text, "program.sb"));
        return (Compilation.Create([tree, ..Library.For(tree)]), tree);
    }

    static List<string> Errors(Compilation compilation, SyntaxTree tree) =>
        [..compilation.GetAllDiagnosticsByFile().Where(d => d.Diagnostic.Severity == DiagnosticSeverity.Error && (d.Tree is null || d.Tree == tree)).Select(static d => d.Diagnostic.ToString())];

    [Theory]
    [MemberData(nameof(Programs))]
    public void AVarReportedAsNeverChangedCompilesAsALet(string file) {
        var text = File.ReadAllText(Path.Combine(Root, file));
        var (compilation, tree) = Compile(text);
        var before = Errors(compilation, tree);

        var found = Lints.Run(compilation, LintConfiguration.Default, [tree]);
        var vars = found.Where(static d => d.Code == ErrorCode.LNT_VarNeverMutated).ToList();
        if (vars.Count == 0) {
            return;
        }

        var rewritten = text.ToCharArray();
        foreach (var report in vars) {
            var at = report.Span.Start;
            Assert.Equal("var", text.Substring(at, 3));
            "let".CopyTo(0, rewritten, at, 3);
        }

        var (after, afterTree) = Compile(new string(rewritten));
        Assert.Equal(before, Errors(after, afterTree));
    }

    [Fact]
    public void TheStandardLibraryLintsWithoutCrashing() {
        var compilation = DocCommand.StandardLibraryCompilation();
        var found = Lints.RunByFile(compilation, LintConfiguration.Default, compilation.SyntaxTrees);
        Assert.All(found, static d => Assert.Equal(DiagnosticSeverity.Warning, d.Diagnostic.Severity));
        Assert.DoesNotContain(found, static d => d.Diagnostic.Code == ErrorCode.LNT_UnknownLint);
    }
}
