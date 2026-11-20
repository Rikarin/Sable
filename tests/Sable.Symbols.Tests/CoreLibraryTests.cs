namespace Sable.Symbols.Tests;

/// <summary>
///     Sable.Core's trees, parsed once per target and build profile: what
///     the library's <c>#if DEBUG</c> sees follows the program's, and a
///     second profile parses only the files that ask.
/// </summary>
public sealed class CoreLibraryTests {
    static readonly ParseOptions Plain = ParseOptions.Default;
    static readonly ParseOptions Debug = ParseOptions.Default.WithSymbols("DEBUG");
    static readonly ParseOptions Release = ParseOptions.Default.WithSymbols("RELEASE");

    [Fact]
    public void EachProfileHasTreesOfItsOwnForTheFilesThatTestIt() {
        var plain = CoreLibrary.TreesFor(Plain);
        var debug = CoreLibrary.TreesFor(Debug);
        var release = CoreLibrary.TreesFor(Release);

        Assert.Equal(plain.Count, debug.Count);
        Assert.Equal(plain.Count, release.Count);
        Assert.Same(debug, CoreLibrary.TreesFor(Debug));

        var assertions = debug.Single(static t => t.Text.FileName == "lib/Sable.Core/Assertions.sb");
        Assert.True(assertions.Options.IsDefined("DEBUG"));
        Assert.DoesNotContain(assertions, plain);
        Assert.True(CoreLibrary.Contains(assertions));
    }

    [Fact]
    public void AFileThatNeverNamesTheProfileIsShared() {
        var plain = CoreLibrary.TreesFor(Plain);
        var debug = CoreLibrary.TreesFor(Debug);
        for (var i = 0; i < plain.Count; i++) {
            var text = plain[i].Text.Text;
            if (!text.Contains("DEBUG", StringComparison.Ordinal) && !text.Contains("RELEASE", StringComparison.Ordinal)) {
                Assert.Same(plain[i], debug[i]);
            }
        }
    }

    [Fact]
    public void TheProfileIsTheDebugOrReleaseSymbol() {
        Assert.Equal("", CoreLibrary.ProfileOf(Plain));
        Assert.Equal("DEBUG", CoreLibrary.ProfileOf(Debug));
        Assert.Equal("RELEASE", CoreLibrary.ProfileOf(Release));
    }

    [Fact]
    public void ACompilationsCoreFollowsItsFilesProfile() {
        var program = SyntaxTree.Parse(new SourceText("module M\nfn main() { assert(true) }", "m.sb"), Debug);
        var compilation = Compilation.Create([program]);
        Assert.Contains(compilation.SyntaxTrees, static t => t.Text.FileName == "lib/Sable.Core/Assertions.sb" && t.Options.IsDefined("DEBUG"));
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
    }
}
