using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable explain</c>'s pages (docs/diagnostics/) say how the compiler
///     behaves, so they are checked against it: each page's erroneous example
///     produces its code and nothing else, and its fix produces nothing at all.
/// </summary>
public sealed class ExplainTests {
    /// <summary>
    ///     Codes without a page, each for a reason: the compiler never reports
    ///     it to a user, or no short program can provoke it.
    /// </summary>
    static readonly HashSet<ErrorCode> Undocumented = [
        ErrorCode.None,

        // Only SyntaxTree.ParseExpression/ParseType/ParseStatement report it; compiling a file never does.
        ErrorCode.ERR_UnexpectedTrailingTokens,

        // Builtins are Sable.Core's alone: elsewhere S3081 comes first and stops the compile.
        ErrorCode.ERR_MalformedBuiltin,
        ErrorCode.ERR_UnknownBuiltin,
        ErrorCode.ERR_BuiltinSignatureMismatch,

        // The binder's fallback for a syntax kind it has no case for; every kind has one.
        ErrorCode.ERR_NotSupportedYet,
    ];

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
        var tree = SyntaxTree.Parse(new SourceText(source, "example.sab"));
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
