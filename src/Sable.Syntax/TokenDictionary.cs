using System.Globalization;
using System.Text;

namespace Sable.Syntax;

/// <summary>
///     Sable's tokens for a fuzzer to put into its inputs whole: every keyword
///     and every fixed-text token from the lexer's own tables, so that one
///     added to the language is there without anyone remembering, and the
///     shapes the tables cannot give — literals, comments, interpolation,
///     directives, attributes. What <c>sable fuzz --dict sable</c> and the
///     compiler's own fuzzers (tools/fuzz/Sable.Fuzz) use.
/// </summary>
public static class TokenDictionary {
    /// <summary>Every keyword and fixed-text token the lexer knows.</summary>
    public static IReadOnlyList<string> Fixed { get; } = [
        ..SyntaxFacts.KeywordTexts
            .Concat(Enum.GetValues<SyntaxKind>().Select(SyntaxFacts.GetText).OfType<string>())
            .Where(static text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The fixed tokens, and one or more of each of the other shapes a token has.</summary>
    public static IReadOnlyList<string> Tokens { get; } = [
        ..Fixed
            .Concat([
                "1", "0", "42", "0x1F", "0b101", "0o17", "1_000", "1.5", "1e10", "2.0e-3", "'c'", "'\\n'", "'\\u{1F600}'",
                "\"s\"", "\"\"", "\"\\(x)\"", "\"a\\(b)c\\(d)\"", "\"\\n\\t\\\\\"", "\"\"\"\n", "#\"raw\"#",
                "//", "// c\n", "/*", "*/", "/* /* */ */", "///", "/// doc\n",
                "#if", "#elseif", "#else", "#endif", "os(linux)", "arch(arm64)",
                "\n", "\r\n", "\t", "    ", "a", "x", "T", "Self", "self", "Int", "String", "List", "U8[>..]", "U8[:]", "I32", "F64", "Bool",
                "module M\n", "import Sable.IO\n", "@main\n", "@inline_layout\n", "@export(\"c\", \"f\")\n", "@derive(Equatable)\n",
                "fn f() {}", "struct S {}", "&<u32>", "$0", ".self", "\\.x", "\\S.x", "_ = ", "..<", "...",
            ])
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    ///     Tokens as a libFuzzer dictionary, a line each: in quotes, with the
    ///     quote, the backslash and every byte that does not print as <c>\xNN</c>.
    /// </summary>
    public static string LibFuzzer(IEnumerable<string> tokens) {
        var text = new StringBuilder();
        foreach (var token in tokens) {
            text.Append('"');
            foreach (var b in Encoding.UTF8.GetBytes(token)) {
                if (b is >= 0x20 and < 0x7F and not (byte)'"' and not (byte)'\\') {
                    text.Append((char)b);
                } else {
                    text.Append(CultureInfo.InvariantCulture, $"\\x{b:X2}");
                }
            }

            text.Append("\"\n");
        }

        return text.ToString();
    }
}
