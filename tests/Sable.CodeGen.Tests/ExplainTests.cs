using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable explain</c>'s pages (docs/diagnostics/) say how the compiler
///     behaves, so they are checked against it: each page's erroneous example
///     produces its code and nothing else, and its fix produces nothing at all.
/// </summary>
public sealed class ExplainTests {
    /// <summary>
    ///     Codes without a page. Every code the compiler has gets one, even
    ///     those a user rarely meets (S1005 from the single-construct parsers,
    ///     the builtins' S3082, S7001 and S7002, the binder's fallback S3099):
    ///     their pages explain without an example.
    /// </summary>
    static readonly HashSet<ErrorCode> Undocumented = [ErrorCode.None];

    static string RepositoryRoot {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sable.slnx"))) {
                dir = dir.Parent;
            }

            return dir!.FullName;
        }
    }

    public static TheoryData<string> Pages {
        get {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(Path.Combine(RepositoryRoot, "docs", "diagnostics"), "S*.md").Order(StringComparer.Ordinal)) {
                data.Add(Path.GetFileNameWithoutExtension(file));
            }

            return data;
        }
    }

    /// <summary>The ids of every error and warning a program produces, front end and lowering.</summary>
    static bool IsLint(string code) => code.CompareTo("S3300") >= 0 && code.CompareTo("S3399") <= 0;

    /// <remarks>A lint's page (S3300-S3399) is checked with the lints run too, at their default levels.</remarks>
    static List<string> Diagnose(string source, bool lints = false) {
        // For one platform, whichever runs the tests: a page's example may
        // be about a platform (S3096's is a declaration only for Windows).
        var tree = SyntaxTree.Parse(new SourceText(source, "example.sb"), ParseOptions.Default.ForTarget("linux", "x86_64"));
        var compilation = Compilation.Create([tree, ..Library.For(tree)]);
        var found = compilation.GetAllDiagnosticsByFile()
            .Where(d => d.Tree is null || d.Tree == tree)
            .Select(static d => d.Diagnostic)
            .Where(static d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToList();

        if (!found.Exists(static d => d.Severity == DiagnosticSeverity.Error)) {
            found.AddRange(SilGen.Lower(compilation).Diagnostics);
        }

        if (lints && !found.Exists(static d => d.Severity == DiagnosticSeverity.Error)) {
            found.AddRange(Lints.Run(compilation, LintConfiguration.Default, [tree]));
        }

        return [..found.Select(static d => $"{d.Id}: {d.Message}")];
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void EachPageIsEmbeddedAndNamesAKnownCode(string code) {
        Assert.NotNull(DiagnosticDocs.Known(code));
        var page = DiagnosticDocs.Page(code);
        Assert.NotNull(page);
        Assert.StartsWith($"# {code}: ", page, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(DiagnosticDocs.Title(page)));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void EachExampleProducesExactlyItsCode(string code) {
        var page = DiagnosticDocs.Page(code)!;
        if (DiagnosticDocs.Example(page, "Example") is not { } example) {
            // A page may explain without an example, when no short program shows it.
            Assert.Null(DiagnosticDocs.Example(page, "Fix"));
            return;
        }

        var found = Diagnose(example, IsLint(code));
        Assert.True(found.Count > 0, $"{code}'s example compiles cleanly");
        Assert.True(
            found.TrueForAll(d => d.StartsWith(code + ":", StringComparison.Ordinal)),
            $"{code}'s example reports something else:\n{string.Join("\n", found)}"
        );
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void EachFixCompilesCleanly(string code) {
        var page = DiagnosticDocs.Page(code)!;
        if (DiagnosticDocs.Example(page, "Fix") is not { } fix) {
            return;
        }

        var found = Diagnose(fix, IsLint(code));
        Assert.True(found.Count == 0, $"{code}'s fix still reports:\n{string.Join("\n", found)}");
    }

    [Fact]
    public void EveryDiagnosticAUserCanMeetHasAPage() {
        var missing = Enum.GetValues<ErrorCode>()
            .Where(static c => !Undocumented.Contains(c))
            .Select(static c => $"S{(int)c:D4}")
            .Where(static c => DiagnosticDocs.Page(c) is null)
            .ToList();
        Assert.True(missing.Count == 0, $"no page for {string.Join(", ", missing)}");
    }

    [Fact]
    public void NoUndocumentedCodeHasAPage() {
        foreach (var code in Undocumented.Where(static c => c != ErrorCode.None)) {
            Assert.Null(DiagnosticDocs.Page($"S{(int)code:D4}"));
        }
    }

    [Fact]
    public void ReportsPointAtSableExplain() {
        const string one = "error[S3004]: type mismatch\n --> a.sb:1:1";
        Assert.Equal("note: 'sable explain S3004' says more about this diagnostic", DiagnosticDocs.Hint([one]));
        Assert.Equal("note: 'sable explain <code>' says more about S2002, S3004",
            DiagnosticDocs.Hint([one, "warning[S3004]: again", "error[S2002]: no symbol named 'x'"]));
        Assert.Null(DiagnosticDocs.Hint(["internal error: SIL verification failed"]));
    }

    [Fact]
    public void ACheckThatFailsSaysWhereToReadMore() {
        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "1.0.0", "", ("src/main.sb", "module App\n\nfn main() {\n    let x: Int = true\n}\n"));
        Assert.Equal(1, Sable.Driver.Packages.PackageBuilder.Check(fixture.Load("app"), fixture.Environment, new()));
        Assert.Contains("note: 'sable explain S3004'", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("S3004", "S3004")]
    [InlineData("s3004", "S3004")]
    [InlineData("3004", "S3004")]
    [InlineData("E0001", "S0001")]
    [InlineData("1", "S0001")]
    [InlineData("S30045", null)]
    [InlineData("hello", null)]
    public void ReadsACodeHowAUserTypesIt(string text, string? expected) => Assert.Equal(expected, DiagnosticDocs.Normalise(text));
}
