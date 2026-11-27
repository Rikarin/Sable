using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Text written and read in Sable — a float's decimals by a format spec,
///     Sable.Text's parsing and escaping, String's search — natively and
///     interpreted, which must agree to the digit.
/// </summary>
public sealed class TextLibraryTests {
    static string Run(string body) =>
        AssertAgree($"module P\nimport Sable.Text\n@main\nfn main() {{\n{body}\n}}\n").Stdout;

    [Fact]
    public void IntegersAreParsedInARadix() =>
        Assert.Equal(
            "255 255 -255 1295 42\n-9223372036854775808 9223372036854775807\nnone none none none none none\n",
            Run("""
                print("\(parseInt("ff", radix: 16) ?? 0) \(parseInt("FF", radix: 16) ?? 0) \(parseInt("-ff", radix: 16) ?? 0) \(parseInt("zz", radix: 36) ?? 0) \(parseInt("+42") ?? 0)")
                print("\(parseInt("-9223372036854775808") ?? 0) \(parseInt("9223372036854775807") ?? 0)")
                var failed = ""
                for text in ["", "-", "12a", "9223372036854775808", "-9223372036854775809", "2"] {
                    if parseInt(text, radix: text == "2" ? 2 : 10) == none {
                        failed = failed + (failed.count > 0 ? " " : "") + "none"
                    }
                }
                print(failed)
            """)
        );

    /// <summary>
    ///     Fixed-point digits from the value's exact binary expansion, rounded
    ///     half to even — the same as C's "%.*f", checked against it here, so
    ///     neither the platform's printf nor .NET's formatting is relied on.
    /// </summary>
    [Fact]
    public void FloatsAreWrittenToFixedDecimals() =>
        Assert.Equal(
            """
            3.14
            0.12
            0.38
            2
            4
            -0.00
            -0.000
            0.000
            10000000000000000000000.0
            179769313486231570814527423731704356798070567525844996598917476803157260780028538760589558632766878171540458953514382464234321326889464182768467546703537516986049910576551282076245490090389328944075868508455133942304583236903222948165808559332123348274797826204144723168738177180919299881250404026184124858368
            0.000
            0.10000000000000000555
            123456.7890
            9.99
            0
            2
            100.0
            0.33333333333333331
            -1.50
            1e+300 0.0
            nan inf -inf

            """,
            Run("""
                print("\(3.14159:.2f)")
                print("\(0.125:.2f)")
                print("\(0.375:.2f)")
                print("\(2.5:.0f)")
                print("\(3.5:.0f)")
                print("\(-0.001:.2f)")
                let zero = 0.0
                print("\(-zero:.3f)")
                print("\(zero:.3f)")
                print("\(1e22:.1f)")
                print("\(1.7976931348623157e308:.0f)")
                print("\(5e-324:.3f)")
                print("\(0.1:.20f)")
                print("\(123456.789:.4f)")
                print("\(9.995:.2f)")
                print("\(0.5:.0f)")
                print("\(1.5:.0f)")
                print("\(99.99:.1f)")
                print("\(1.0 / 3.0:.17f)")
                print("\(-1.5:.2f)")
                print("\(1e300) \(1e-300:.1f)")
                print("\(zero / zero:.2f) \(1.0 / zero:.2f) \(-1.0 / zero:.2f)")
            """)
        );

    [Fact]
    public void HtmlIsEscaped() =>
        Assert.Equal(
            "&lt;a href=&quot;x&quot;&gt;Tom &amp; &#x27;Jerry&#x27;&lt;/a&gt;\nplain text\n",
            Run("""
                print(Html.escape("<a href=\"x\">Tom & 'Jerry'</a>"))
                print(Html.escape("plain text"))
            """)
        );

    /// <summary>
    ///     String's hash once overflowed past a dozen characters: it kept the
    ///     hash below 2^61 and then multiplied it by 33.
    /// </summary>
    [Fact]
    public void LongStringsHash() =>
        Assert.Equal(
            "true\n",
            AssertAgree("""
                module P
                @main
                fn main() {
                    let a = "a string a good deal longer than a dozen characters".hashValue
                    let b = "a string a good deal longer than a dozen characters".hashValue
                    let c = "a string a good deal longer than a dozen characterz".hashValue
                    print(a == b && a != c)
                }
                """).Stdout
        );

    /// <summary>
    ///     Indexing text that is not ASCII walks from the nearest place the
    ///     runtime knows: a loop over a string's scalars, forwards or back,
    ///     is linear. It was quadratic — 131,072 scalars took 11 s — so a
    ///     million in under five seconds, compilation aside, says it is not.
    /// </summary>
    [Fact]
    public void IndexingTextThatIsNotAsciiIsLinear() {
        if (!CanBuild) {
            return;
        }

        var run = BuildAndRun("""
            module P
            import Sable.Text
            import Sable.Time

            @main
            fn main() {
                let text = "aé😀".repeated(333334)
                let start = Instant.now()
                var forward = 0
                var i = 0
                while i < text.count {
                    forward = forward + text.at(i).codePoint
                    i = i + 1
                }
                var backward = 0
                i = text.count - 1
                while i >= 0 {
                    backward = backward + text.at(i).codePoint
                    i = i - 1
                }
                // Hopping between two strings, and jumping about in one.
                let other = "ü".repeated(1000)
                var mixed = 0
                i = 0
                while i < 1000 {
                    mixed = mixed + text.at(i * 997).codePoint + other.at(999 - i).codePoint
                    i = i + 1
                }
                print(text.count)
                print(forward == backward && forward == 333334 * (97 + 233 + 128512))
                print(mixed)
                print(start.elapsed.totalMilliseconds < 5000)
            }
            """);
        var expected = 0L;
        var scalars = new[] { 97, 233, 128512 };
        for (var i = 0; i < 1000; i++) {
            expected += scalars[i * 997 % 3] + 252;
        }

        Assert.Equal($"1000002\ntrue\n{expected}\ntrue\n", run.Stdout);
    }

    /// <summary>
    ///     The runtime's vectorised search finds what a scalar-by-scalar one
    ///     does — the interpreter's, and the expectation worked out here — on
    ///     random texts of 0 to 130 scalars, ASCII and not, from every kind of
    ///     start: lengths around the sixteen-byte blocks, and parts that match
    ///     at their first and last scalar but not between.
    /// </summary>
    [Fact]
    public void SearchFindsWhatAScalarSearchFinds() {
        var random = new Random(229);
        string[] alphabet = ["a", "b", "c", "ab", "é", "漢", "😀", "x"];
        var body = new System.Text.StringBuilder();
        var expected = new System.Text.StringBuilder();
        for (var n = 0; n < 60; n++) {
            var length = n < 20 ? n * 7 % 131 : random.Next(0, 131);
            var scalars = Enumerable.Range(0, length).Select(_ => random.Next(10) < 7 ? "ab"[random.Next(2)].ToString() : alphabet[random.Next(alphabet.Length)]).ToList();
            var text = string.Concat(scalars);
            var runes = text.EnumerateRunes().ToList();
            var partRunes = random.Next(3) switch {
                0 when runes.Count > 0 => runes.Skip(random.Next(runes.Count)).Take(random.Next(1, 6)).ToList(),
                1 => [new System.Text.Rune('a'), new System.Text.Rune('c'), new System.Text.Rune('b')],
                _ => Enumerable.Range(0, random.Next(0, 4)).Select(_ => new System.Text.Rune("ab"[random.Next(2)])).ToList(),
            };
            var part = string.Concat(partRunes);
            var from = random.Next(-2, runes.Count + 3);

            body.Append($"    print(\"\\(find(\"{text}\", \"{part}\", from: {from}) ?? -1) \\(occurrences(of: \"{part}\", within: \"{text}\")) \\(splitting(\"{text}\", by: \"{part}\").count)\")\n");

            int Find(int start) {
                for (var i = Math.Max(0, start); i + partRunes.Count <= runes.Count; i++) {
                    if (runes.Skip(i).Take(partRunes.Count).SequenceEqual(partRunes)) {
                        return i;
                    }
                }

                return -1;
            }

            var count = 0;
            if (partRunes.Count > 0) {
                for (var at = Find(0); at >= 0; at = Find(at + partRunes.Count)) {
                    count++;
                }
            }

            expected.Append($"{Find(from)} {count} {(partRunes.Count == 0 ? 1 : count + 1)}\n");
        }

        // Scalar positions, as the runtime's search takes them.
        const string Helpers = """
                                   fn find(_ text: String, _ part: String, from start: Int) -> Int? {
                                       let at = Runtime.findString(text, part, start)
                                       return at < 0 ? none : at
                                   }
                                   fn occurrences(of part: String, within text: String) -> Int {
                                       return part.isEmpty ? 0 : text.count(of: part)
                                   }
                                   fn splitting(_ text: String, by separator: String) -> String[>..] {
                                       return separator.isEmpty ? [text] : text.split(separator)
                                   }
                               """;
        Assert.Equal(expected.ToString(), Run(Helpers + "\n" + body));
    }
}
