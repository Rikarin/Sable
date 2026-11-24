using System.Globalization;
using System.Text;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The driver's TOML reader and the library's (lib/Sable.Serialize) held
///     to each other: the same texts are TOML to both, and with the same
///     values. The driver reads manifests and locks with its own, in C#, as it
///     must before it can compile anything — the library among it.
/// </summary>
[Native]
public sealed class TomlAgreementTests {
    /// <summary>Each line of input a text's UTF-8 in hexadecimal; each of output what it holds, or "invalid".</summary>
    const string Reader = """
        module Test

        import Sable.IO
        import Sable.Serialize
        import Sable.Text

        fn show(_ value: TomlValue) -> String {
            return match value {
                case .Text(let text)       => "s" + hex(text)
                case .Integer(let integer) => "i\(integer)"
                case .Float(let float)     => float == float ? "f\(float.bits)" : "fnan"
                case .Bool(let bool)       => bool ? "true" : "false"
                case .Array(let items)     => "[" + items.map({ show($0) }).joined(separator: ",") + "]"
                case .Table(let table)     => "{" + table.entries.map({ hex($0.key) + ":" + show($0.value) }).joined(separator: ",") + "}"
                case _                     => "date"
            }
        }

        fn hex(_ text: String) -> String {
            var bytes: U8[>..] = []
            bytes.append(utf8: text)
            return Hex.encode(bytes)
        }

        @main
        fn main() throws {
            for line in Console.readLines() {
                let text = String.fromUtf8Lossy(try Hex.decode(line))
                if let table = try? Toml.parse(text) {
                    // What it writes of the table reads as the same.
                    let again = try Toml.parse(try Toml.write(table))
                    print(show(.Table(table: table)) + (again.count == table.count ? "" : " written otherwise"))
                } else {
                    print("invalid")
                }
            }
        }
        """;

    static string Hex(string text) => Convert.ToHexStringLower(Encoding.UTF8.GetBytes(text));

    static string Show(TomlValue value) => value switch {
        TomlString text => "s" + Hex(text.Value),
        TomlInteger integer => "i" + integer.Value.ToString(CultureInfo.InvariantCulture),
        TomlFloat { Value: double.NaN } => "fnan",
        TomlFloat number => "f" + BitConverter.DoubleToUInt64Bits(number.Value).ToString(CultureInfo.InvariantCulture),
        TomlBoolean boolean => boolean.Value ? "true" : "false",
        TomlDateTime => "date",
        TomlArray array => "[" + string.Join(',', array.Items.Select(Show)) + "]",
        TomlTable table => "{" + string.Join(',', table.Entries.Select(static e => Hex(e.Key) + ":" + Show(e.Value))) + "}",
        _ => throw new InvalidOperationException(),
    };

    static string Driver(string text) {
        try {
            return Show(TomlReader.Parse(text));
        } catch (TomlException) {
            return "invalid";
        }
    }

    static void Agree(IReadOnlyList<string> texts) {
        if (!Programs.CanBuild) {
            return;
        }

        var run = Programs.BuildAndRun(Reader, input: string.Concat(texts.Select(static t => Hex(t) + "\n")));
        Assert.Equal("", run.Stderr);
        var lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(texts.Count, lines.Length);
        var apart = texts.Zip(lines).Select(static p => (Text: p.First, Library: p.Second, Driver: Driver(p.First)))
            .Where(static c => c.Library != c.Driver)
            .Take(20)
            .Select(static c => $"{c.Text.Replace("\n", "\\n", StringComparison.Ordinal)}\n    library {c.Library}\n    driver  {c.Driver}")
            .ToList();
        Assert.True(apart.Count == 0, string.Join("\n", apart));
    }

    /// <summary>Every TOML file of the repository, its packages' manifests as canonical TOML among them, and what the tests' manifests and locks are written like.</summary>
    [Fact]
    public void OverTheRepositorysTomlFiles() {
        var texts = new List<string>();
        foreach (var directory in new[] { "examples", "lib", "tests", "tools", "editors", "benchmarks" }) {
            var root = Path.Combine(Harnesses.Root, directory);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}target{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
                    continue;
                }

                var name = Path.GetFileName(file);
                if (name.EndsWith(".toml", StringComparison.Ordinal) || name == Workspace.LockName) {
                    texts.Add(File.ReadAllText(file));
                } else if (name == "package.sb") {
                    texts.Add(ManifestEvaluator.Evaluate(file));
                }
            }
        }

        // The manifests and locks in the tests' raw strings.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Harnesses.Root, "tests"), "*.cs", SearchOption.AllDirectories)) {
            var parts = File.ReadAllText(file).Split("\"\"\"");
            texts.AddRange(parts.Where(static p => p.Contains("\nversion", StringComparison.Ordinal) || p.Contains(" version", StringComparison.Ordinal) || p.Contains("[package]", StringComparison.Ordinal)));
        }

        Assert.True(texts.Count > 20, $"only {texts.Count} were found");
        Assert.Contains(texts, static t => Driver(t) != "invalid");
        Agree(texts);
    }

    /// <summary>Every pair, and many longer runs, of the pieces TOML is made of.</summary>
    [Fact]
    public void OverGeneratedTexts() {
        string[] values = [
            "1", "+1", "-0", "01", "1_0", "1__0", "_1", "1_", "0x1F", "0X1", "0x_1", "0o17", "0o8", "0b10", "0b2", "+0x1", "9223372036854775807", "9223372036854775808", "-9223372036854775808",
            "0xffffffffffffffff", "0x8000000000000000", "0x00000000000000000001", "1.5", "1.", ".5", "1e5", "1e+5", "1E-5", "1e", "1e_5", "1e1_0", "1_0.5", "1_.5", "1._5", "01.5", "-01.5", "0.1", "1e400", "00e1", "1.5.5", "1e5.5", "inf", "+inf", "-inf", "Inf",
            "nan", "-nan", "true", "false", "True", "truex", "null", "x", "1979-05-27", "1979-05-27T07:32:00Z", "1979-05-27 07:32:00", "1979-05-27t07:32:00.5-07:00", "1979-05-27T07:32", "1979-13-01", "1979-02-29", "2000-02-29", "0000-02-29", "07:32:00",
            "07:32:00.999", "07:32", "24:00:00", "23:59:60", "07:32:00Z", "1979-05-27T07:32:00+24:00", "1979-05-27T", "1979-05-27 ", "1979-5-27", "\"\"", "\"a\"", "\"a", "'a'", "'a", "\"\\n\\t\\\\\\\"\\b\\f\\r\"", "\"\\e\"", "\"\\x41\"", "\"\\u00e9\"",
            "\"\\uD800\"", "\"\\U0001F600\"", "\"\\U00110000\"", "\"\\u12\"", "\"\t\"", "\"\u0001\"", "'\u0001'", "\"\u007f\"", "'\u007f'", "\"\"\"a\nb\"\"\"", "\"\"\"\na\\\n  b\"\"\"", "\"\"\"a\\  \n b\"\"\"", "\"\"\"a\\ b\"\"\"", "\"\"\"\"\"\"", "\"\"\"\"a\"\"\"\"",
            "\"\"\"\"\"a\"\"\"\"\"", "\"\"\"a\"\"\"\"\"\"", "\"\"\"\u0001\"\"\"", "'''a\nb'''", "'''''a'''''", "'''a''''''", "'''\u0001'''", "'''\u007f'''", "\"\"\"\r\na\r\nb\"\"\"", "\"a\rb\"", "'''a\rb'''", "\"\"\"a\rb\"\"\"", "[]", "[1]", "[1,]", "[,]", "[1,,2]", "[1 2]",
            "[1,\n 2 # c\n]", "[[1], ['a']]", "[", "{}", "{a=1}", "{a=1,}", "{a=1,b=2}", "{a=1 b=2}", "{a=1,a=2}", "{a.b=1,a.c=2}", "{a={b=1},a.c=2}", "{a=1,\nb=2}", "{\n}", "{", "{,}", "{a}", "[{a=1},{a=2}]", "é", "", "1 2", "1 # c", "1 # \u0001",
        ];
        string[] keys = ["a", "a-b_1", "1", "\"a b\"", "'a.b'", "\"\"", "a.b", "a . b", "a.\"b c\".d", "a.", ".a", "a b", "é", "\"a", "\"\\u0061\"", "\"\\q\"", "\"\"\"a\"\"\"", "", "a.a", "true", "-", "'a\tb'"];
        string[] lines = [
            "a = 1\n", "a.b = 1\n", "a.b.c = 1\n", "b = {}\n", "a = {b = {}}\n", "a = []\n", "a = [{}]\n", "[a]\n", "[a.b]\n", "[a.b.c]\n", "[b]\n", "[[a]]\n", "[[a.b]]\n", "[[b]]\n", "[ a . b ]\n", "['a']\n", "b = 1\n", "b.c = 1\n", "c = 1\n", "\n", "# c\n", "x = 1 ",
            "[a] # c\n", "[a] x\n", "[a\n", "[a]]\n", "[[a]\n", "[]\n", "[a.]\n", "\t\n", "\r\n", "\r", "\uFEFF", "[a.b]\r\n",
        ];

        var texts = new List<string>();
        texts.AddRange(values.Select(static v => $"a = {v}"));
        texts.AddRange(values.Select(static v => $"a = {v}\nb = 1\n"));
        texts.AddRange(values.Select(static v => $"a = [{v}, {v}]"));
        texts.AddRange(values.Select(static v => $"a = {{b = {v}}}"));
        texts.AddRange(keys.Select(static k => $"{k} = 1"));
        texts.AddRange(keys.Select(static k => $"[{k}]\nx = 1"));
        texts.AddRange(keys.Select(static k => $"[[{k}]]"));
        texts.AddRange(keys.Select(static k => $"a = {{{k} = 1}}"));
        texts.AddRange(keys.SelectMany(k => keys.Select(l => $"{k} = 1\n{l} = 2")));
        texts.AddRange(lines.SelectMany(a => lines.Select(b => a + b)));
        texts.AddRange(lines.SelectMany(a => lines.SelectMany(b => lines.Select(c => a + b + c))).Where(static (_, i) => i % 5 == 0));
        texts.Add("a = " + new string('[', 128) + new string(']', 128));
        texts.Add("a = " + new string('[', 129) + new string(']', 129));
        texts.Add("a = " + new string('[', 100000));

        var random = new Random(53);
        string[] pieces = [.. values.Where(static v => v.Length < 12), .. keys, "=", " = ", "\n", "[", "]", "[[", "]]", "{", "}", ",", ".", " ", "\"", "'", "#"];
        for (var i = 0; i < 3000; i++) {
            texts.Add(string.Concat(Enumerable.Range(0, random.Next(2, 10)).Select(_ => pieces[random.Next(pieces.Length)])));
            texts.Add(string.Concat(Enumerable.Range(0, random.Next(3, 8)).Select(_ => lines[random.Next(lines.Length)])));
        }

        Agree(texts);
    }
}
