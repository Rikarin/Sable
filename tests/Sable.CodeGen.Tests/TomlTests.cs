using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>The TOML reader: the language manifests and lock files are written in, and where it says a file is wrong.</summary>
public sealed class TomlTests {
    static TomlTable Parse(string text) => TomlReader.Parse(text, "test.toml");

    static TomlException Fails(string text) => Assert.Throws<TomlException>(() => Parse(text));

    [Fact]
    public void KeysAndScalarValues() {
        var table = Parse("""
            # a comment
            name = "render-engine"   # trailing comment
            count = 1_000
            hex = 0xff
            octal = 0o17
            binary = 0b101
            negative = -42
            ratio = 3.5e2
            on = true
            off = false
            literal = 'C:\path\no\escapes'
            escaped = "tab\there \u00e9 \"quoted\""
            when = 1979-05-27T07:32:00Z
            """);

        Assert.Equal("render-engine", ((TomlString)table["name"]!).Value);
        Assert.Equal(1000, ((TomlInteger)table["count"]!).Value);
        Assert.Equal(255, ((TomlInteger)table["hex"]!).Value);
        Assert.Equal(15, ((TomlInteger)table["octal"]!).Value);
        Assert.Equal(5, ((TomlInteger)table["binary"]!).Value);
        Assert.Equal(-42, ((TomlInteger)table["negative"]!).Value);
        Assert.Equal(350.0, ((TomlFloat)table["ratio"]!).Value);
        Assert.True(((TomlBoolean)table["on"]!).Value);
        Assert.False(((TomlBoolean)table["off"]!).Value);
        Assert.Equal(@"C:\path\no\escapes", ((TomlString)table["literal"]!).Value);
        Assert.Equal("tab\there é \"quoted\"", ((TomlString)table["escaped"]!).Value);
        Assert.Equal("1979-05-27T07:32:00Z", ((TomlDateTime)table["when"]!).Text);
        Assert.Equal(["name", "count", "hex", "octal", "binary", "negative", "ratio", "on", "off", "literal", "escaped", "when"], table.Keys);
    }

    [Fact]
    public void MultiLineStrings() {
        var table = Parse("""
            basic = \"\"\"
            one
            two \
                three\"\"\"
            literal = '''
            raw \n stays'''
            """.Replace("\\\"", "\"", StringComparison.Ordinal));

        Assert.Equal("one\ntwo three", ((TomlString)table["basic"]!).Value);
        Assert.Equal("raw \\n stays", ((TomlString)table["literal"]!).Value);
    }

    [Fact]
    public void TablesDottedKeysAndArraysOfTables() {
        var table = Parse("""
            [package]
            name = "a"

            [profile.release]
            opt-level = 3

            [dependencies]
            image = { version = "0.6", features = ["png", "jpeg"], default-features = false }
            site.path = "../site"
            "quoted key" = "1.0"

            [[bin]]
            name = "one"

            [[bin]]
            name = "two"
            """);

        var profile = (TomlTable)((TomlTable)table["profile"]!)["release"]!;
        Assert.Equal(3, ((TomlInteger)profile["opt-level"]!).Value);

        var dependencies = (TomlTable)table["dependencies"]!;
        var image = (TomlTable)dependencies["image"]!;
        Assert.True(image.IsInline);
        Assert.Equal(["png", "jpeg"], ((TomlArray)image["features"]!).Items.Cast<TomlString>().Select(static s => s.Value));
        Assert.Equal("../site", ((TomlString)((TomlTable)dependencies["site"]!)["path"]!).Value);
        Assert.NotNull(dependencies["quoted key"]);

        var bins = (TomlArray)table["bin"]!;
        Assert.True(bins.IsArrayOfTables);
        Assert.Equal(["one", "two"], bins.Items.Cast<TomlTable>().Select(static t => ((TomlString)t["name"]!).Value));
    }

    [Fact]
    public void ArraysSpanLinesWithCommentsAndATrailingComma() {
        var table = Parse("""
            members = [
                "crates/*",   # every crate
                "tools/x",
            ]
            nested = [[1, 2], ["a"]]
            """);

        Assert.Equal(2, ((TomlArray)table["members"]!).Items.Count);
        Assert.Equal(2, ((TomlArray)table["nested"]!).Items.Count);
    }

    [Fact]
    public void KeysRememberWhereTheyWereWritten() {
        var table = Parse("a = 1\n\n  b = 2\n");
        Assert.Equal(new TomlPosition(3, 3), table.KeyPosition("b"));
        Assert.Equal(new TomlPosition(3, 7), table["b"]!.Position);
    }

    [Theory]
    [InlineData("a = 1\na = 2", 2, 1, "the key 'a' is defined twice")]
    [InlineData("[t]\n[t]", 2, 2, "the table 't' is defined twice")]
    [InlineData("a = \"open", 1, 5, "the string is not closed on its line")]
    [InlineData("a = ", 1, 5, "expected a value after '='")]
    [InlineData("a = hello", 1, 5, "'hello' is not a value: a string needs quotes")]
    [InlineData("a = [1, 2", 1, 5, "the array is not closed with ']'")]
    [InlineData("a = { b = 1,\n c = 2 }", 1, 13, "an inline table must be written on one line")]
    [InlineData("a = { b = 1, }", 1, 14, "a trailing comma is not allowed in an inline table")]
    [InlineData("a = 1 b = 2", 1, 7, "expected the end of the line, found 'b'")]
    [InlineData("a = 01", 1, 5, "'01' is not a value: a string needs quotes")]
    [InlineData("a = \"\\q\"", 1, 6, "'\\q' is not an escape TOML knows")]
    // Found by the packages fuzz target: eight hex digits with the top bit
    // set read as a negative int and reached ConvertFromUtf32 unchecked.
    [InlineData("a = \"\\U87777777\"", 1, 6, "'\\U87777777' is not a Unicode scalar value")]
    [InlineData("\"\\UFFFFFFFF\" = 1", 1, 2, "'\\UFFFFFFFF' is not a Unicode scalar value")]
    [InlineData("a = \"\\U00110000\"", 1, 6, "'\\U00110000' is not a Unicode scalar value")]
    [InlineData("a = \"\\uD800\"", 1, 6, "'\\uD800' is not a Unicode scalar value")]
    [InlineData("a = \"\\u12\"", 1, 6, "'\\u' needs 4 hexadecimal digits")]
    [InlineData("a = \"\\U1234", 1, 6, "'\\U' needs 8 hexadecimal digits")]
    [InlineData("a = \"\"\"\\U80000000\"\"\"", 1, 8, "'\\U80000000' is not a Unicode scalar value")]
    [InlineData("a = \"\\", 1, 6, "'\\' must be followed by an escape on the same line")]
    [InlineData("a = \"\\\nb", 1, 6, "'\\' must be followed by an escape on the same line")]
    // Past long.MaxValue in hex, octal and binary used to wrap to a negative.
    [InlineData("a = 0xFFFFFFFFFFFFFFFF", 1, 5, "'0xFFFFFFFFFFFFFFFF' is not a value: a string needs quotes")]
    [InlineData("a = 0o1777777777777777777777", 1, 5, "'0o1777777777777777777777' is not a value: a string needs quotes")]
    [InlineData("a = 0b1111111111111111111111111111111111111111111111111111111111111111", 1, 5, "'0b1111111111111111111111111111111111111111111111111111111111111111' is not a value: a string needs quotes")]
    [InlineData("a = 9223372036854775808", 1, 5, "'9223372036854775808' is not a value: a string needs quotes")]
    [InlineData("[a\nb = 1", 1, 3, "expected ']' to close the table header")]
    [InlineData("a = 1\n[a]", 2, 2, "'a' is already an integer, not a table")]
    [InlineData("x = { y = 1 }\n[x.z]", 2, 2, "'x' is an inline table, closed once written")]
    [InlineData("= 1", 1, 1, "'=' cannot start a key")]
    public void ErrorsSayWhereAndWhy(string text, int line, int column, string reason) {
        var error = Fails(text);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(new TomlPosition(line, column), error.Position);
        Assert.StartsWith($"test.toml:{line}:{column}: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapesAndIntegersAtTheirLimits() {
        var table = Parse("""
            top = "\U0010FFFF"
            below = "\uD7FF"
            hex = 0x7FFFFFFFFFFFFFFF
            least = -9223372036854775808
            """);
        Assert.Equal(char.ConvertFromUtf32(0x10FFFF), ((TomlString)table["top"]!).Value);
        Assert.Equal("\uD7FF", ((TomlString)table["below"]!).Value);
        Assert.Equal(long.MaxValue, ((TomlInteger)table["hex"]!).Value);
        Assert.Equal(long.MinValue, ((TomlInteger)table["least"]!).Value);
    }

    [Fact]
    public void QuotingRoundTrips() {
        const string awkward = "a \"b\" \\c\nd\te";
        var table = Parse($"{TomlText.Key("needs quoting")} = {TomlText.Quote(awkward)}");
        Assert.Equal(awkward, ((TomlString)table["needs quoting"]!).Value);
    }
}
