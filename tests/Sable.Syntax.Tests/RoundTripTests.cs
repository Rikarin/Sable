using System.Text;

namespace Sable.Syntax.Tests;

/// <summary>
/// The full-fidelity invariant: concatenating every token's full text
/// reproduces the source byte for byte. Nothing in the lexer may break this —
/// it is what the formatter, the IDE and the refactoring engine depend on.
/// </summary>
public sealed class RoundTripTests
{
    private static void AssertRoundTrip(string source)
    {
        var tokens = Lexer.Tokenize(new SourceText(source), out _);
        var builder = new StringBuilder();
        foreach (var token in tokens) builder.Append(token.ToFullString());
        Assert.Equal(source, builder.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n\n\n")]
    [InlineData("let x = 1")]
    [InlineData("let x = 1\n")]
    [InlineData("  let   x   =   1  \n\n")]
    [InlineData("// just a comment")]
    [InlineData("/* block */ let x = 1 /* trailing */")]
    [InlineData("/* nested /* inner */ outer */")]
    [InlineData("/// doc\nfn f() { }")]
    [InlineData("//! module doc\nmodule A")]
    [InlineData("#!/usr/bin/env sable\nfn main() { }")]
    [InlineData("\"hello\"")]
    [InlineData("\"with \\(interpolation) inside\"")]
    [InlineData("\"\"\"\nmultiline\n\"\"\"")]
    [InlineData("#\"raw \\n string\"#")]
    [InlineData("##\"more #\\\" hashes\"##")]
    [InlineData("'a' '\\n' '\\u{1F600}'")]
    [InlineData("0xFF 0o755 0b1010_0110 1_000_000 3.14 1e-9 0x1.8p3")]
    [InlineData("a?.b ?? c!")]
    [InlineData("x ..< y ... z")]
    [InlineData("a <<= 1 >>= 2 ??= 3")]
    [InlineData("@inline(always) fn f() -> some P { }")]
    [InlineData("`class` `match`")]
    [InlineData("T&<u32 in pool>?")]
    [InlineData("union Node.. { Leaf }")]
    [InlineData("é_ident ünïcödé")]
    [InlineData("\t\tindented\t\t")]
    public void RoundTripsExactly(string source) => AssertRoundTrip(source);

    [Fact]
    public void RoundTripsUnterminatedConstructs()
    {
        AssertRoundTrip("\"unterminated");
        AssertRoundTrip("/* unterminated");
        AssertRoundTrip("'unterminated");
        AssertRoundTrip("#\"unterminated");
        AssertRoundTrip("`unterminated");
        AssertRoundTrip("\"\"\"\nunterminated");
        AssertRoundTrip("\"hole \\(1 + ");
    }

    [Fact]
    public void RoundTripsEveryCorpusFile()
    {
        var examples = Path.Combine(TestHelpers.RepositoryRoot, "examples");
        var files = Directory.GetFiles(examples, "*.sab", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var tokens = Lexer.Tokenize(new SourceText(source, file), out _);
            var builder = new StringBuilder();
            foreach (var token in tokens) builder.Append(token.ToFullString());
            Assert.True(source == builder.ToString(), $"round-trip failed for {file}");
        }
    }

    [Fact]
    public void CorpusFilesLexWithoutDiagnostics()
    {
        var examples = Path.Combine(TestHelpers.RepositoryRoot, "examples");
        foreach (var file in Directory.GetFiles(examples, "*.sab", SearchOption.AllDirectories))
        {
            var source = new SourceText(File.ReadAllText(file), file);
            Lexer.Tokenize(source, out var diagnostics);
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0,
                $"{Path.GetFileName(file)}:\n" +
                string.Join("\n", errors.Select(e => e.Format(source))));
        }
    }

    [Fact]
    public void FullSpansTileTheSourceWithoutGapsOrOverlaps()
    {
        const string source = """
            module A.B

            /// doc
            public fn f(x: Int) -> Int {
                let s = "hi \(x)"
                return x + 1   // trailing
            }
            """;

        var tokens = Lexer.Tokenize(new SourceText(source), out _);
        var expected = 0;
        foreach (var token in tokens)
        {
            Assert.Equal(expected, token.FullSpan.Start);
            expected = token.FullSpan.End;
        }
        Assert.Equal(source.Length, expected);
    }
}
