using Sable.Driver;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The standard library's source is parsed only where a build needs it
///     (docs/05 §5.4): the modules a program imports are found from the files'
///     headers, read without parsing them.
/// </summary>
public sealed class LibrarySourceTests {
    [Fact]
    public void EveryFilesHeaderIsWhatParsingItSays() {
        var everything = SyntaxTree.Parse(string.Concat(Library.ModuleNames.Select(static m => $"import {m}\n")));
        var files = Library.For(everything).Concat(CoreLibrary.Trees).ToList();
        Assert.Superset(Library.ModuleNames.Append(Library.Core).ToHashSet(), files.Select(static f => f.Header.Module!).ToHashSet());
        foreach (var file in files) {
            var parsed = SyntaxTree.Parse(file.Text, file.Options);
            var scanned = SourceHeader.Scan(file.Text.Text);
            Assert.True(scanned is not null, file.Text.FileName);
            Assert.Equal(parsed.Header.Module, scanned.Module);
            Assert.Equal(parsed.Header.Imports, scanned.Imports);

            // A module's files are in the directory of its name.
            Assert.StartsWith($"lib/{scanned.Module}/", file.Text.FileName, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("module A.B\n\n// why\nimport C // trailing\nimport D.E\n\nfn main() {}\n", "A.B", "C,D.E")]
    [InlineData("module A\n", "A", "")]
    [InlineData("", null, "")]
    [InlineData("module A\n/* a comment */\nimport B\n", null, null)]
    [InlineData("module A\n#if DEBUG\nimport B\n#endif\n", null, null)]
    [InlineData("module A\nimport B as C\n", null, null)]
    [InlineData("module A\nimport B.{ X }\n", null, null)]
    [InlineData("module A; import B\n", null, null)]
    [InlineData("fn main() {}\n", null, null)]
    public void AHeaderIsScannedOnlyWhereItIsPlain(string text, string? module, string? imports) {
        var header = SourceHeader.Scan(text);
        if (imports is null) {
            Assert.Null(header);
        } else {
            Assert.NotNull(header);
            Assert.Equal(module, header.Module);
            Assert.Equal(imports, string.Join(",", header.Imports));
        }

        // Scanned or parsed, a tree's header is what it says.
        var parsed = SyntaxTree.Parse(text).Header;
        var deferred = SyntaxTree.Deferred(new SourceText(text, "a.sb"), ParseOptions.Default);
        Assert.Equal(parsed.Module, deferred.Header.Module);
        Assert.Equal(parsed.Imports, deferred.Header.Imports);
        Assert.Equal(header is null, deferred.IsParsed);
    }

    [Fact]
    public void ADeferredTreeIsTheParsedOne() {
        const string text = "module A\n\nfn f( {\n";
        var parsed = SyntaxTree.Parse(text);
        var deferred = SyntaxTree.Deferred(new SourceText(text, "a.sb"), ParseOptions.Default);
        Assert.False(deferred.IsParsed);
        Assert.Equal(parsed.Diagnostics, deferred.Diagnostics);
        Assert.True(deferred.IsParsed);
        Assert.Equal(text, deferred.ToFullString());
        Assert.Same(deferred.Root, deferred.Root);
    }
}
