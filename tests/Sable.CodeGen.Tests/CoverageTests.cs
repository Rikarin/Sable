using Sable.Driver;
using Sable.Driver.Commands;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable test --coverage</c> (#205): each block of the measured code
///     counted as it runs, in every test process — one that traps too — and
///     reported per file and line, and as LCOV.
/// </summary>
[Collection("Console")]
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

        // Both tests found n not negative; one found it zero, one not.
        var negative = LineOf("if n < 0");
        var zero = LineOf("if n == 0");
        Assert.Equal(
            [$"BRDA:{negative},0,0,0", $"BRDA:{negative},0,1,2", $"BRDA:{zero},0,0,1", $"BRDA:{zero},0,1,1", "BRF:4", "BRH:3"],
            lcov.Split('\n').Where(static l => l.StartsWith("BR", StringComparison.Ordinal)));

        var text = output.ToString().ReplaceLineEndings("\n");
        Assert.Contains("coverage: ", text, StringComparison.Ordinal);
        Assert.Contains("75% of branches (3 of 4)", text, StringComparison.Ordinal);
        Assert.Matches(@"  calc\.sb: [0-9.]+% \(\d+ of \d+\), not run: .*; branches 75% \(3 of 4\)", text);
        Assert.Contains($"    {negative}:5 'if' never: true\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"    {zero}:5", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     '--cover' counts the standard library's code the tests run instead
    ///     of the program's: the lines of lib/Sable.Collections/Stack.sb, by
    ///     the name it is embedded with, and of no other file. A method no
    ///     test calls is there as not run, though a generic class's method
    ///     nothing uses is never lowered.
    /// </summary>
    [Fact]
    public void CoverCountsTheLibrarysCode() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-cover-");
        var saved = Console.Out;
        string lcov;
        try {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "tests"));
            File.WriteAllText(Path.Combine(directory.FullName, "tests", "StackTests.sb"), """
                module StackTests

                import Sable.Collections
                import Sable.Testing

                @test
                fn popsWhatWasPushed() {
                    let stack = Stack<Int>()
                    stack.push(1)
                    assertEqual(stack.pop() ?? 0, 1)
                }
                """);
            var path = Path.Combine(directory.FullName, "stack.info");
            Console.SetOut(new StringWriter());
            // A directory is its files, as one program.
            Assert.Equal(0, Command.Find("test")!.Run([Path.Combine(directory.FullName, "tests"), "--cover", "lib/Sable.Collections/Stack.sb", "--lcov", path]));
            lcov = File.ReadAllText(path);
            Assert.Equal(2, Command.Find("test")!.Run([Path.Combine(directory.FullName, "tests"), "--cover", "Sable.NoSuchModule"]));
        } finally {
            Console.SetOut(saved);
            directory.Delete(true);
        }

        Assert.Equal(["SF:lib/Sable.Collections/Stack.sb"], lcov.Split('\n').Where(static l => l.StartsWith("SF:", StringComparison.Ordinal)));
        var source = File.ReadAllText(Path.Combine(Root, "lib/Sable.Collections/Stack.sb"));
        int LibraryLine(string text) => source[..source.IndexOf(text, StringComparison.Ordinal)].Count(static c => c == '\n') + 1;
        var lines = lcov.Split('\n')
            .Where(static l => l.StartsWith("DA:", StringComparison.Ordinal))
            .Select(static l => l[3..].Split(','))
            .ToDictionary(static p => int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), static p => long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, lines[LibraryLine("items.append(item)")]);
        Assert.Equal(0, lines[LibraryLine("return items[items.count - 1]")]);
        Assert.Equal(0, lines[LibraryLine("public var isEmpty")]);
    }

    /// <summary>A generic function nothing calls has no SIL, yet its lines are reported, as not run.</summary>
    [Fact]
    public void CodeNeverLoweredIsReportedAsNotRun() {
        const string source = """
            module Generic

            import Sable.Testing

            fn twice<T>(_ value: T) -> T[>..] {
                let pair: T[>..] = [value, value]
                return pair
            }

            @test
            fn nothing() { }
            """;
        var result = TestHarness.Compile([SyntaxTree.Parse(new SourceText(source, "generic.sb"))], TestKind.Test, out _, coverage: static _ => true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var lines = result.Coverage!.Lines(new Dictionary<int, long>())["generic.sb"];
        Assert.Equal([6, 7], lines.Keys);
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

    /// <summary>
    ///     A command keeps one run's options (--coverage, -j) and what it
    ///     built, so two runs at once — tests in parallel — each need an
    ///     instance of their own: sharing one, a run without --coverage
    ///     turned another's off half-way through.
    /// </summary>
    [Fact]
    public void EachRunOfACommandHasItsOwnState() =>
        Assert.NotSame(Command.Find("test"), Command.Find("test"));
}
