using Sable.Symbols;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable test --doc</c>: the <c>```sable</c> blocks of doc comments,
///     compiled and run as tests of the module they document.
/// </summary>
public sealed class DocExampleTests {
    const string Documented = """
        module Maths

        import Sable.Text
        import Sable.Testing

        /// Adds two numbers.
        ///
        /// ```sable
        /// assertEqual(add(2, 3), 5)
        /// assertEqual(hidden(), 1)
        /// ```
        ///
        /// A wrong one:
        ///
        /// ```sable
        /// assertEqual(add(2, 2), 5)
        /// ```
        ///
        /// ```sable ignore
        /// this is not even Sable
        /// ```
        ///
        /// ```
        /// not marked as Sable, so not run
        /// ```
        public fn add(_ a: Int, _ b: Int) -> Int { return a + b }

        // Internal: an example reaches it, as a function of the module.
        fn hidden() -> Int { return 1 }

        /// Pads, with the file's imports in scope.
        ///
        /// ```sable
        /// assertEqual("7".paddedStart(toWidth: 3, with: '0'), "007")
        /// ```
        @test
        fn padding() {
            assertEqual("7".paddedStart(toWidth: 2, with: '0'), "07")
        }
        """;

    [Fact]
    public void EachBlockIsATestOfTheModule() {
        var examples = DocExamples.Extract(SyntaxTree.Parse(new SourceText(Documented, "maths.sb")));
        Assert.Equal(3, examples.Count);

        // The code is on the lines it was written on, in a file of that name.
        var first = examples[0];
        Assert.Equal("maths.sb", first.Text.FileName);
        var lines = first.Text.Text.Split('\n');
        Assert.Equal("module Maths import Sable.Text import Sable.Testing", lines[0]);
        Assert.Equal("@test fn doc_add_1() {", lines[7]);
        Assert.Equal("assertEqual(add(2, 3), 5)", lines[8]);
        Assert.Equal("assertEqual(add(2, 3), 5)", Documented.Split('\n')[8][4..]);
    }

    [Fact]
    public void TheyRunWithTheTests() {
        var result = TestHarness.Compile([new SourceText(Documented, "maths.sb")], TestKind.Test, out var found, docExamples: true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["Maths.padding", "Maths.doc_add_1", "Maths.doc_add_2", "Maths.doc_padding_1"], found.Select(static t => t.QualifiedName));
        Assert.Equal(["maths.sb:37", "maths.sb:8", "maths.sb:15", "maths.sb:33"], found.Select(static t => t.Location));

        // Without --doc, they are only comments.
        TestHarness.Compile([new SourceText(Documented, "maths.sb")], TestKind.Test, out var plain);
        Assert.Equal(["Maths.padding"], plain.Select(static t => t.QualifiedName));

        if (!CanBuild) {
            return;
        }

        var run = Native([new SourceText(Documented, "maths.sb")], doc: true);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("test Maths.doc_add_1 ... ok", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("test Maths.doc_padding_1 ... ok", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("---- Maths.doc_add_2 (maths.sb:15) ----\n    assertion failed: the values differ\n       left: 4\n      right: 5\n", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("3 passed; 1 failed", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ExamplesOfOneNameInTwoFilesOfAModuleAreNumberedTogether() {
        const string first = "module M\n\n/// ```sable\n/// assert(parse() == 1)\n/// ```\nfn parse() -> Int { return 1 }\n";
        const string second = "module M\n\nstruct S {\n    /// ```sable\n    /// assert(S.parse() == 2)\n    /// ```\n    static fn parse() -> Int { return 2 }\n}\n";
        var result = TestHarness.Compile([new SourceText(first, "a.sb"), new SourceText(second, "b.sb")], TestKind.Test, out var found, docExamples: true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["M.doc_parse_1", "M.doc_parse_2"], found.Select(static t => t.QualifiedName));
    }

    /// <summary>
    ///     <c>sable test --doc lib/Sable.Text</c>, or one file of it: the
    ///     files read from disk stand in for the embedded ones of their names,
    ///     rather than declaring everything a second time, and the module's
    ///     other files come with them.
    /// </summary>
    [Theory]
    [InlineData("Sable.Text", null)]
    [InlineData("Sable.Core", "Numeric.sb")]
    [InlineData("Sable.Text", "String+Encoding.sb")]
    public void TheLibrarysOwnExamplesCompileFromDisk(string module, string? file) {
        var directory = Path.Combine(RepositoryRoot, "lib", module);
        var files = file is null ? Directory.GetFiles(directory, "*.sb").Order(StringComparer.Ordinal).ToArray() : [Path.Combine(directory, file)];
        var result = TestHarness.Compile([..files.Select(f => new SourceText(File.ReadAllText(f), Path.GetFileName(f)))], TestKind.Test, out var found, docExamples: true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.NotEmpty(found);
    }

    static string RepositoryRoot {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sable.slnx"))) {
                dir = dir.Parent;
            }

            return dir!.FullName;
        }
    }

    [Fact]
    public void AnExampleThatDoesNotCompileSaysWhereItIs() {
        var result = TestHarness.Compile(
            [new SourceText("module M\n\n/// ```sable\n/// let x: Int = \"text\"\n/// ```\nfn f() { }\n", "m.sb")],
            TestKind.Test,
            out _,
            docExamples: true
        );
        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Contains("S3004", error, StringComparison.Ordinal);
        Assert.Contains("--> m.sb:4:", error, StringComparison.Ordinal);
    }
}
