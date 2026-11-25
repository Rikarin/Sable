using System.Text;

namespace Sable.Syntax.Tests;

/// <summary>The grammar-aware mutator and the token dictionary that fuzzing what reads Sable source uses.</summary>
public sealed class SyntaxMutatorTests {
    static readonly string[] Programs = [.. CorpusFiles().Select(File.ReadAllText)];

    static IEnumerable<string> CorpusFiles() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
            directory = directory.Parent;
        }

        return Directory.GetFiles(Path.Combine(directory!.FullName, "tests", "corpus"), "*.sb").Order(StringComparer.Ordinal);
    }

    static readonly Lazy<SyntaxMutator> Mutator = new(static () => new SyntaxMutator(Programs));

    static int Errors(string source) => SyntaxTree.Parse(source).Diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>The point of it: edits of whole nodes leave most mutants without a syntax error, all of them different.</summary>
    [Fact]
    public void MostMutantsParse() {
        var mutants = 0;
        var clean = 0;
        var changed = 0;
        foreach (var program in Programs.Where(static p => p.Length < 8000)) {
            for (var seed = 0UL; seed < 12; seed++) {
                var mutant = Mutator.Value.Mutate(program, seed);
                Assert.Equal(mutant, SyntaxTree.Parse(mutant).ToFullString());
                mutants++;
                changed += mutant != program ? 1 : 0;
                clean += Errors(mutant) == 0 ? 1 : 0;
            }
        }

        Assert.True(changed > mutants * 95 / 100, $"{changed} of {mutants} mutants are changed");
        Assert.True(clean > mutants * 55 / 100, $"{clean} of {mutants} mutants parse without an error");
    }

    [Fact]
    public void AMutantIsItsSeeds() {
        var input = Encoding.UTF8.GetBytes(Programs[0]);
        Assert.Equal(Mutator.Value.Mutate(input, 7, 100_000), Mutator.Value.Mutate(input, 7, 100_000));
        Assert.True(Enumerable.Range(0, 20).Select(s => Convert.ToHexString(Mutator.Value.Mutate(input, (ulong)s, 100_000))).Distinct().Count() > 15);
        var other = Encoding.UTF8.GetBytes(Programs[1]);
        Assert.Equal(Mutator.Value.CrossOver(input, other, 3, 100_000), Mutator.Value.CrossOver(input, other, 3, 100_000));
        Assert.NotEqual(input, Mutator.Value.CrossOver(input, other, 3, 100_000));
    }

    /// <summary>It is given whatever a fuzzer has: no text at all, half a program, bytes that are not UTF-8.</summary>
    [Fact]
    public void AnyInputIsMutatedWithinItsLength() {
        byte[][] inputs = [[], "\n"u8.ToArray(), [0xFF, 0xFE, 0], "fn f( {"u8.ToArray(), "let x = \"\\(a"u8.ToArray(), "\U0001F600é"u8.ToArray(), "#if os(linux)\nfn f() {}\n"u8.ToArray()];
        var random = new Random(20261125);
        foreach (var input in inputs.Concat(Enumerable.Range(0, 40).Select(_ => Enumerable.Range(0, random.Next(64)).Select(_ => (byte)random.Next(256)).ToArray()))) {
            for (var seed = 0UL; seed < 10; seed++) {
                Assert.True(Mutator.Value.Mutate(input, seed, 200).Length <= Math.Max(200, input.Length));
                Assert.True(Mutator.Value.CrossOver(input, inputs[3], seed, 200).Length <= Math.Max(200, input.Length));
            }
        }

        // With no programs, the input's own nodes are what there is.
        Assert.NotEqual("fn f() { a(1); b(2) }", new SyntaxMutator([]).Mutate("fn f() { a(1); b(2) }", 5));
    }

    /// <summary>Every keyword and operator of the lexer's is in the dictionary, and it reads as libFuzzer's does.</summary>
    [Fact]
    public void TheDictionaryIsTheLexers() {
        Assert.Equal(Fuzzing.EveryFixedToken, TokenDictionary.Fixed);
        foreach (var token in (string[])["fn", "match", "\\", "_", "..<", "=>", "where"]) {
            Assert.Contains(token, TokenDictionary.Tokens);
        }

        var lines = TokenDictionary.LibFuzzer(["a\"b\\\né", "fn"]).Split('\n');
        Assert.Equal("\"a\\x22b\\x5C\\x0A\\xC3\\xA9\"", lines[0]);
        Assert.Equal("\"fn\"", lines[1]);
        foreach (var line in TokenDictionary.LibFuzzer(TokenDictionary.Tokens).Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            Assert.Matches("^\"([ !#-\\[\\]-~]|\\\\x[0-9A-F]{2})+\"$", line);
        }
    }
}
