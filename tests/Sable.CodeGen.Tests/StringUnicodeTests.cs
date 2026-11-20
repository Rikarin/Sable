using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Char's Unicode properties, read from the generated tables, and
///     String.Index — a UTF-8 offset in the interpreter as natively — run
///     both ways, which must agree; and the traps an index out of place
///     raises, with the same message both ways.
/// </summary>
public sealed class StringUnicodeTests {
    static string Run(string body) => AssertAgree(Program("", body)).Stdout;

    [Fact]
    public void CharPropertiesComeFromTheUnicodeTables() =>
        Assert.Equal(
            "true true true false\nLowercaseLetter OtherLetter Unassigned\nSS ǆ σ k\ntrue false\n",
            Run("""
                print("\('é'.isLetter) \('Ж'.isUppercase) \(Char.fromCodePoint(0x3000)!.isWhitespace) \('1'.isLetter)")
                print("\('ß'.generalCategory) \('中'.generalCategory) \(Char.fromCodePoint(0x0378)!.generalCategory)")
                print("\('ß'.uppercased()) \('ǅ'.lowercased()) \('Σ'.caseFolded) \(Char.fromCodePoint(0x212A)!.caseFolded)")
                print("\(Char.fromCodePoint(0xA0)!.isWhitespace) \(Char.fromCodePoint(0x200B)!.isWhitespace)")
            """)
        );

    [Fact]
    public void IndicesAreUtf8OffsetsBothWays() =>
        Assert.Equal(
            "0 1 3 6 10\n😀€éa\n€😀 aé\n1 1 2\n",
            Run("""
                let text = "aé€😀"
                var index = text.startIndex
                var offsets = ""
                while index < text.endIndex {
                    offsets = offsets + "\(index.utf8Offset) "
                    index = text.index(after: index)
                }
                print(offsets + "\(index.utf8Offset)")
                print(text.reversed())
                let third = text.index(atScalar: 2)
                print("\(text[third...]) \(text[..<third])")
                var seen = 0
                for c in text {
                    seen = seen + 1
                }
                print("\(text.firstIndex(of: 'é')!.utf8Offset) \(text.distance(from: text.startIndex, to: text.endIndex) + seen - 7) \(text.count(of: CharSet("aé")))")
            """)
        );

    [Fact]
    public void SearchingAndCaseAgree() =>
        Assert.Equal(
            "a|b||c\nab-c\nkey value=more\nSTRASSE οδος\n-1 0\n4 10\n",
            Run("""
                print("a,b,,c".split(",").joined(separator: "|"))
                print("a-b-c".replacing("-", with: "", maxCount: 1))
                let pair = "key=value=more".splitOnce('=')!
                print("\(pair.0) \(pair.1)")
                print("\("Straße".uppercased()) \("ΟΔΟΣ".lowercased())")
                print("\("file9".compare("file10", options: .Numeric)) \("ABC".compare("abc", options: .IgnoringCase))")
                print("\("aéaé".lastIndex(of: "é")!.utf8Offset) \("x\u{3000}é\u{2003}".trimmed().utf8Count + 4)")
            """)
        );

    [Fact]
    public void TrimmingTakesOffUnicodeWhiteSpace() =>
        Assert.Equal("[hi]\n[é  é]\n", Run("""
            print("[\("\u{3000} hi\u{A0}\n".trimmed())]")
            print("[\("\u{85}é \u{2003}é\u{2029}".trimmed())]")
        """));

    /// <summary>
    ///     A Substring is read through the runtime's forms over a range of
    ///     its string's bytes — counted, compared, searched both ways,
    ///     hashed and parsed as a float — which the interpreter has too.
    /// </summary>
    [Fact]
    public void ASubstringIsReadWhereItLiesBothWays() =>
        Assert.Equal(
            "3 13 true true false\n16 5 -1\ntrue 2.5 1280\nfin é\n",
            Run("""
                let text = "naïve café, fin, é, 2.5e0, 1280"
                let fields = text.asSubstring.split(", ")
                let word = fields[0]
                print("\(word[^3...].count) \(word.utf8Count + text[..<text.index(atScalar: 1)].count) \(word[^4...] == "café") \(fields[1] < "fio") \(fields[2] > Substring("é"))")
                print("\(text.lastIndex(of: fields[3])!.utf8Offset - 7) \(fields[1].firstIndex(of: "n")!.utf8Offset - 11) \(word.firstIndex(of: fields[1])?.utf8Offset ?? -1)")
                let sizes: [String: Int] = ["café": 2, "fin": 3]
                print("\(word[^4...].hashValue == "café".hashValue && sizes[word[^4...]] == 2) \(try! F64.parse(fields[3])) \(fields[4].toInt()!)")
                print("\(fields[1]) \(fields[2])")
            """)
        );

    [Theory]
    [InlineData("let text = \"aé\"\nlet _ = text[String.Index(utf8Offset: 2)]", "string index not on a character boundary")]
    [InlineData("let text = \"ab\"\nlet _ = text[text.endIndex]", "string index out of range")]
    [InlineData("let text = \"ab\"\nlet _ = text.index(before: text.startIndex)", "string index out of range")]
    [InlineData("let text = \"é\"\nlet _ = text.index(before: String.Index(utf8Offset: 1))", "string index not on a character boundary")]
    [InlineData("let text = \"éa\"\nlet _ = text[String.Index(utf8Offset: 1)...]", "string index not on a character boundary")]
    [InlineData("let text = \"abc\"\nlet _ = text[String.Index(utf8Offset: 2)..<String.Index(utf8Offset: 1)]", "string slice out of range")]
    public void AnIndexOutOfPlaceTrapsBothWays(string body, string message) {
        var (_, trap) = Interpret(Program("", body));
        Assert.Equal(message, trap);
        AssertAgree(Program("", body));
    }
}
