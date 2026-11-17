using System.Text;

namespace Sable.Syntax.Tests;

public sealed class LiteralTests {
    // ---- Integers ---------------------------------------------------------

    [Theory]
    [InlineData("0", 0UL)]
    [InlineData("42", 42UL)]
    [InlineData("1_000_000", 1_000_000UL)]
    [InlineData("0xFF", 255UL)]
    [InlineData("0xff_ff", 65535UL)]
    [InlineData("0o755", 493UL)]
    [InlineData("0b1010_0110", 166UL)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    public void LexesIntegers(string text, ulong expected) {
        var token = TestHelpers.LexSingle(text);
        Assert.Equal(SyntaxKind.IntegerLiteralToken, token.Kind);
        Assert.Equal(expected, token.Value);
    }

    [Theory]
    [InlineData("18446744073709551616", ErrorCode.ERR_IntegerLiteralOverflow)]
    [InlineData("0xFFFFFFFFFFFFFFFFF", ErrorCode.ERR_IntegerLiteralOverflow)]
    [InlineData("0x", ErrorCode.ERR_NumericLiteralNoDigits)]
    [InlineData("0b", ErrorCode.ERR_NumericLiteralNoDigits)]
    [InlineData("0o", ErrorCode.ERR_NumericLiteralNoDigits)]
    [InlineData("1_", ErrorCode.ERR_TrailingUnderscoreInNumber)]
    public void ReportsBadIntegers(string text, ErrorCode expected) {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics(text);
        Assert.Contains(diagnostics, d => d.Code == expected);
    }

    [Fact]
    public void OctalRejectsDigitsOutOfRange() {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("0o9");
        Assert.Contains(
            diagnostics,
            static d => d.Code is ErrorCode.ERR_NumericLiteralNoDigits
                or ErrorCode.ERR_InvalidDigitForBase
        );
    }

    // ---- Floats -----------------------------------------------------------

    [Theory]
    [InlineData("3.14", 3.14)]
    [InlineData("1e9", 1e9)]
    [InlineData("1e-9", 1e-9)]
    [InlineData("1E+3", 1000.0)]
    [InlineData("2.5e2", 250.0)]
    [InlineData("1_000.000_1", 1000.0001)]
    [InlineData("0x1.8p3", 12.0)]
    [InlineData("0x1p4", 16.0)]
    public void LexesFloats(string text, double expected) {
        var token = TestHelpers.LexSingle(text);
        Assert.Equal(SyntaxKind.FloatLiteralToken, token.Kind);
        Assert.Equal(expected, (double)token.Value!, 9);
    }

    [Fact]
    public void ALiteralRunningIntoLettersIsReported() {
        // '1e' would lex as the integer 1 followed by the name 'e'. Since
        // spec/lexical.md §6 has no suffixes, that is never what was meant.
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("1e");
        Assert.Contains(diagnostics, static d => d.Code == ErrorCode.ERR_InvalidFloatLiteral);
    }

    [Theory]
    [InlineData("1.0f")]
    [InlineData("12abc")]
    [InlineData("1_000u")]
    public void NoSuffixIsAccepted(string text) {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics(text);
        Assert.Contains(diagnostics, static d => d.Code == ErrorCode.ERR_InvalidFloatLiteral);
    }

    [Fact]
    public void HexFloatWithoutExponentIsAnError() {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("0x1.8");
        Assert.Contains(diagnostics, static d => d.Code == ErrorCode.ERR_HexFloatNeedsExponent);
    }

    // ---- Characters -------------------------------------------------------

    [Theory]
    [InlineData("'a'", "a")]
    [InlineData("""'\n'""", "\n")]
    [InlineData("""'\t'""", "\t")]
    [InlineData("""'\0'""", "\0")]
    [InlineData("""'\\'""", """\""")]
    [InlineData("""'\''""", "'")]
    [InlineData("""'\u{41}'""", "A")]
    [InlineData("""'\u{1F600}'""", "\U0001F600")]
    [InlineData("'é'", "é")]
    public void LexesCharacters(string text, string expectedScalar) {
        var token = TestHelpers.LexSingle(text);
        Assert.Equal(SyntaxKind.CharLiteralToken, token.Kind);
        var rune = Assert.IsType<Rune>(token.Value);
        Assert.Equal(expectedScalar, rune.ToString());
    }

    [Theory]
    [InlineData("''", ErrorCode.ERR_EmptyCharLiteral)]
    [InlineData("'ab'", ErrorCode.ERR_CharLiteralTooLong)]
    [InlineData("'a", ErrorCode.ERR_UnterminatedChar)]
    [InlineData("""'\q'""", ErrorCode.ERR_InvalidEscapeSequence)]
    [InlineData("""'\u{110000}'""", ErrorCode.ERR_UnicodeScalarOutOfRange)]
    [InlineData("""'\u{}'""", ErrorCode.ERR_InvalidUnicodeEscape)]
    public void ReportsBadCharacters(string text, ErrorCode expected) {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics(text);
        Assert.Contains(diagnostics, d => d.Code == expected);
    }

    // ---- Simple strings ---------------------------------------------------

    [Theory]
    [InlineData("\"\"", "")]
    [InlineData("\"hello\"", "hello")]
    [InlineData("\"tab\\there\"", "tab\there")]
    [InlineData("\"quote\\\"inside\"", """quote"inside""")]
    [InlineData("\"\\u{1F600}\"", "\U0001F600")]
    public void LexesSimpleStrings(string text, string expected) {
        var token = TestHelpers.LexSingle(text);
        Assert.Equal(SyntaxKind.StringLiteralToken, token.Kind);
        Assert.Equal(expected, token.Value);
    }

    [Fact]
    public void UnterminatedStringIsReported() {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("\"oops");
        Assert.Contains(diagnostics, static d => d.Code == ErrorCode.ERR_UnterminatedString);
    }

    [Fact]
    public void StringDoesNotSpanALineBreak() {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("\"oops\nlet x = 1");
        Assert.Contains(diagnostics, static d => d.Code == ErrorCode.ERR_UnterminatedString);
    }

    // ---- Interpolation ----------------------------------------------------

    [Fact]
    public void CapturesInterpolationSegments() {
        var token = TestHelpers.LexSingle("\"a \\(x + 1) b\"");

        Assert.Equal(3, token.Parts.Length);
        Assert.Equal(StringPartKind.Literal, token.Parts[0].Kind);
        Assert.Equal("a ", token.Parts[0].Text);
        Assert.Equal(StringPartKind.Interpolation, token.Parts[1].Kind);
        Assert.Equal("x + 1", token.Parts[1].Text);
        Assert.Equal(StringPartKind.Literal, token.Parts[2].Kind);
        Assert.Equal(" b", token.Parts[2].Text);

        // An interpolated string has no single cooked value.
        Assert.Null(token.Value);
    }

    [Fact]
    public void InterpolationSpanCoversTheWholeHole() {
        const string source = "\"v=\\(value)\"";
        var token = TestHelpers.LexSingle(source);
        var hole = token.Parts[1];

        // The span tiles the token text, so it includes the delimiters; the
        // part's Text is the expression alone.
        Assert.Equal("""\(value)""", source.Substring(hole.Span.Start, hole.Span.Length));
        Assert.Equal("value", hole.Text);
    }

    [Fact]
    public void StringPartsTileTheTokenText() {
        const string source = "\"a \\(x) b \\(y)\"";
        var token = TestHelpers.LexSingle(source);

        var expected = token.Span.Start + 1; // after the opening quote
        foreach (var part in token.Parts) {
            Assert.Equal(expected, part.Span.Start);
            expected = part.Span.End;
        }

        Assert.Equal(token.Span.End - 1, expected); // before the closing quote
    }

    [Fact]
    public void HandlesNestedParenthesesInInterpolation() {
        var token = TestHelpers.LexSingle("\"\\(f(g(1), h(2)))\"");
        Assert.Equal("f(g(1), h(2))", token.Parts[0].Text);
    }

    [Fact]
    public void HandlesNestedStringInsideInterpolation() {
        var token = TestHelpers.LexSingle("\"\\(f(\")\"))\"");
        Assert.Equal(StringPartKind.Interpolation, token.Parts[0].Kind);
        Assert.Equal("""f(")")""", token.Parts[0].Text);
    }

    [Fact]
    public void UnterminatedInterpolationIsReported() {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("\"\\(1 + ");
        Assert.Contains(
            diagnostics,
            static d => d.Code is ErrorCode.ERR_UnterminatedInterpolation
                or ErrorCode.ERR_UnterminatedString
        );
    }

    // ---- Multi-line strings -----------------------------------------------

    [Fact]
    public void MultilineStringStripsDelimiterIndentation() {
        const string source = "\"\"\"\n    hello\n    world\n    \"\"\"";
        var token = TestHelpers.LexSingle(source);
        Assert.Equal(SyntaxKind.MultilineStringLiteralToken, token.Kind);
        Assert.Equal("hello\nworld", token.Value);
    }

    [Fact]
    public void MultilineStringWithNoIndentation() {
        const string source = "\"\"\"\nhello\nworld\n\"\"\"";
        Assert.Equal("hello\nworld", TestHelpers.LexSingle(source).Value);
    }

    [Fact]
    public void MultilineStringKeepsRelativeIndentation() {
        const string source = "\"\"\"\n  a\n    b\n  \"\"\"";
        Assert.Equal("a\n  b", TestHelpers.LexSingle(source).Value);
    }

    [Fact]
    public void MultilineStringSupportsInterpolation() {
        const string source = "\"\"\"\n  a \\(x)\n  b\n  \"\"\"";
        var token = TestHelpers.LexSingle(source);
        Assert.Equal(
            [StringPartKind.Literal, StringPartKind.Interpolation, StringPartKind.Literal],
            token.Parts.Select(static p => p.Kind)
        );
        Assert.Equal("a ", token.Parts[0].Text);
        Assert.Equal("x", token.Parts[1].Text);
        Assert.Equal("\nb", token.Parts[2].Text);
    }

    [Fact]
    public void MultilineStringRequiresALineBreakAfterTheOpeningDelimiter() {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("\"\"\"oops\n\"\"\"");
        Assert.Contains(
            diagnostics,
            static d => d.Code == ErrorCode.ERR_MultilineDelimiterMustBeOnOwnLine
        );
    }

    [Fact]
    public void MultilineStringBackslashJoinsLines() {
        const string source = "\"\"\"\na \\\nb\n\"\"\"";
        Assert.Equal("a b", TestHelpers.LexSingle(source).Value);
    }

    // ---- Raw strings ------------------------------------------------------

    [Theory]
    [InlineData("""#"raw \n"#""", """raw \n""")]
    [InlineData("""#"has " quote"#""", """has " quote""")]
    [InlineData("""##"has "# inside"##""", """has "# inside""")]
    [InlineData("""#"\(not interpolated)"#""", """\(not interpolated)""")]
    public void LexesRawStrings(string text, string expected) {
        var token = TestHelpers.LexSingle(text);
        Assert.Equal(SyntaxKind.RawStringLiteralToken, token.Kind);
        Assert.Equal(expected, token.Value);
    }

    [Fact]
    public void RawMultilineString() {
        const string source = "#\"\"\"\na \\n b\n\"\"\"#";
        var token = TestHelpers.LexSingle(source);
        Assert.Equal(SyntaxKind.RawStringLiteralToken, token.Kind);
        Assert.Equal("\na \\n b\n", token.Value);
    }

    [Fact]
    public void UnterminatedRawStringIsReported() {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("""#"oops""");
        Assert.Contains(diagnostics, static d => d.Code == ErrorCode.ERR_UnterminatedRawString);
    }
}
