using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Text's formatting, parsing, lines and escaping, written in Sable —
///     natively and interpreted, which must agree to the digit.
/// </summary>
public sealed class TextLibraryTests {
    static string Run(string body) =>
        AssertAgree($"module P\nimport Sable.Text\n@main\nfn main() {{\n{body}\n}}\n").Stdout;

    [Fact]
    public void IntegersAreFormattedInARadix() =>
        Assert.Equal(
            "ff\n0000FF\n-000ff\n   42|\n-0042\n0\nzz\n101010\n"
            + "-1000000000000000000000000000000000000000000000000000000000000000\n-9223372036854775808\n9223372036854775807\n",
            Run("""
                print(formatted(255, radix: 16))
                print(formatted(255, radix: 16, width: 6, fill: '0', uppercase: true))
                print(formatted(-255, radix: 16, width: 6, fill: '0'))
                print(formatted(42, width: 5) + "|")
                print(formatted(-42, width: 5, fill: '0'))
                print(formatted(0, radix: 36))
                print(formatted(1295, radix: 36))
                print(formatted(42, radix: 2))
                print(formatted(-9223372036854775807 - 1, radix: 2))
                print(formatted(-9223372036854775807 - 1))
                print(formatted(9223372036854775807))
            """)
        );

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
    public void FloatsAreFormattedToFixedDecimals() =>
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
                print(formatted(3.14159, decimals: 2))
                print(formatted(0.125, decimals: 2))
                print(formatted(0.375, decimals: 2))
                print(formatted(2.5, decimals: 0))
                print(formatted(3.5, decimals: 0))
                print(formatted(-0.001, decimals: 2))
                let zero = 0.0
                print(formatted(-zero, decimals: 3))
                print(formatted(zero, decimals: 3))
                print(formatted(1e22, decimals: 1))
                print(formatted(1.7976931348623157e308, decimals: 0))
                print(formatted(5e-324, decimals: 3))
                print(formatted(0.1, decimals: 20))
                print(formatted(123456.789, decimals: 4))
                print(formatted(9.995, decimals: 2))
                print(formatted(0.5, decimals: 0))
                print(formatted(1.5, decimals: 0))
                print(formatted(99.99, decimals: 1))
                print(formatted(1.0 / 3.0, decimals: 17))
                print(formatted(-1.5, decimals: 2))
                print("\(1e300) \(formatted(1e-300, decimals: 1))")
                print("\(formatted(zero / zero, decimals: 2)) \(formatted(1.0 / zero, decimals: 2)) \(formatted(-1.0 / zero, decimals: 2))")
            """)
        );

    [Fact]
    public void TemplatesAreFilledIn() =>
        Assert.Equal(
            "a is b; b a {x} {5} {\nno arguments: {}\n",
            Run("""
                print(format("{} is {}; {1} {0} {{x}} {5} {", ["a", "b"]))
                print(format("no arguments: {}", []))
            """)
        );

    [Fact]
    public void LinesTrimmingAndSearching() =>
        Assert.Equal(
            "[a][b][][c]\n0\n[x  ][  x]\ntrue\n.mdfoo\n4 4 none\n2 0\n",
            Run("""
                var all = ""
                for line in lines("a\r\nb\n\nc\n") {
                    all = all + "[" + line + "]"
                }
                print(all)
                print(lines("").count)
                print("[" + trimmedStart("  x  ") + "][" + trimmedEnd("  x  ") + "]")
                print(isBlank(" \t ") && !isBlank(" a") && isBlank(""))
                print(removingPrefix("foo.md", "foo") + removingSuffix("foo.md", ".md"))
                print("\(findLast("abcabc", "bc") ?? -1) \(find("abcabc", "bc", from: 2) ?? -1) \(find("abc", "c", from: 3) == none ? "none" : "some")")
                print("\(occurrences(of: "aa", within: "aaaaa")) \(occurrences(of: "", within: "a"))")
            """)
        );

    [Fact]
    public void HtmlIsEscaped() =>
        Assert.Equal(
            "&lt;a href=&quot;x&quot;&gt;Tom &amp; &#39;Jerry&#39;&lt;/a&gt;\nplain text\n",
            Run("""
                print(escapedHtml("<a href=\"x\">Tom & 'Jerry'</a>"))
                print(escapedHtml("plain text"))
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
                    let a = "a string a good deal longer than a dozen characters".hash()
                    let b = "a string a good deal longer than a dozen characters".hash()
                    print(a == b && a >= 0)
                }
                """).Stdout
        );
}
