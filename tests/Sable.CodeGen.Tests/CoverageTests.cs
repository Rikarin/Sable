using Sable.Driver.Commands;
using Sable.SIL;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable test --coverage</c> (#205): each block of the measured code
///     counted as it runs, in every test process — one that traps too — and
///     reported per file and line, and as LCOV.
/// </summary>
public sealed class CoverageTests {
    const string Calc = """
        module Calc

        import Sable.Testing

        public fn classify(_ n: Int) -> String {
            if n < 0 {
                return "negative"
            }
            if n == 0 {
                return "zero"
            }
            return "positive"
        }

        fn unused() -> Int {
            return 42
        }

        fn last(_ xs: Int[>..]) -> Int {
            return xs[xs.count]
        }

        @test
        fn positives() {
            assertEqual(classify(3), "positive")
        }

        @test
        fn zero() {
            assertEqual(classify(0), "zero")
        }

        @test(traps)
        fn pastTheEnd() {
            print(last([1, 2]))
        }
        """;

    static int LineOf(string text) => Calc[..Calc.IndexOf(text, StringComparison.Ordinal)].Count(static c => c == '\n') + 1;

    [Fact]
    public void TheTestsLinesAreCountedAndReported() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-coverage-");
        var output = new StringWriter();
        var saved = Console.Out;
        string lcov;
        try {
            var file = Path.Combine(directory.FullName, "calc.sb");
            File.WriteAllText(file, Calc);
            Console.SetOut(output);
            Assert.Equal(0, Command.Find("test")!.Run([file, "--coverage", "-j", "2"]));
            lcov = File.ReadAllText(Path.Combine(directory.FullName, "target", "coverage", "lcov.info"));
        } finally {
            Console.SetOut(saved);
            directory.Delete(true);
        }

        var lines = lcov.Split('\n')
            .Where(static l => l.StartsWith("DA:", StringComparison.Ordinal))
            .Select(static l => l[3..].Split(','))
            .ToDictionary(static p => int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), static p => long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(0, lines[LineOf("return \"negative\"")]);
        Assert.Equal(1, lines[LineOf("return \"zero\"")]);
        Assert.Equal(1, lines[LineOf("return \"positive\"")]);
        Assert.Equal(2, lines[LineOf("if n == 0")]);
        Assert.Equal(0, lines[LineOf("return 42")]);

        // The test that traps ran the line it trapped on, and its counts were kept.
        Assert.Equal(1, lines[LineOf("return xs[xs.count]")]);

        var text = output.ToString().ReplaceLineEndings("\n");
        Assert.Contains("coverage: ", text, StringComparison.Ordinal);
        Assert.Matches(@"  calc\.sb: [0-9.]+% \(\d+ of \d+\), not run: ", text);
    }

    [Fact]
    public void ACounterCoversTheLinesOfItsBlock() {
        var map = new CoverageMap([new("a.sb", [3, 4]), new("a.sb", [4, 7]), new("b.sb", [1])]);
        var lines = map.Lines(new Dictionary<int, long> { [0] = 2, [1] = 5 });
        Assert.Equal([3, 4, 7], lines["a.sb"].Keys);
        Assert.Equal([2L, 5L, 5L], lines["a.sb"].Values);
        Assert.Equal(0, lines["b.sb"][1]);
        Assert.Equal("SF:b.sb\nDA:1,0\nLF:1\nLH:0\nend_of_record\n", Coverage.Lcov(new() { ["b.sb"] = lines["b.sb"] }));
    }
}
