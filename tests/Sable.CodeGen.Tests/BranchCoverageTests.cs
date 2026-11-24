using System.Diagnostics;
using System.Globalization;
using Sable.SIL;
using Sable.Symbols;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Branch coverage for <c>sable test --coverage</c> (#205): each outcome
///     of every decision of the measured code counted — natively, built
///     with and without the optimiser, and in the interpreter, the same —
///     and its counters still there after every pass.
/// </summary>
public sealed class BranchCoverageTests {
    const string Program = """
        module Branches

        import Sable.Testing

        public union Shape {
            Circle(r: Int)
            Square(w: Int)
            Dot
        }

        public fn sign(_ n: Int) -> Int {
            if n < 0 {
                return -1
            } else if n == 0 {
                return 0
            }
            return 1
        }

        public fn both(_ a: Bool, _ b: Bool) -> Bool {
            return a && b
        }

        public fn either(_ a: Bool, _ b: Bool) -> Bool {
            return a || b
        }

        public fn pick(_ c: Bool) -> Int {
            return c ? 1 : 2
        }

        public fn orZero(_ x: Int?) -> Int {
            return x ?? 0
        }

        public fn unwrap(_ x: Int?) -> Int {
            if let y = x {
                return y
            }
            return -1
        }

        public fn guarded(_ x: Int?) -> Int {
            guard let y = x else {
                return 0
            }
            return y
        }

        public fn countDown(_ n: Int) -> Int {
            var i = n
            var steps = 0
            while i > 0 {
                i -= 1
                steps += 1
            }
            return steps
        }

        // A condition that is a literal is no decision: it has one outcome.
        public fn untilLeft(_ n: Int) -> Int {
            var i = 0
            while true {
                i += 1
                if i >= n {
                    break
                }
            }
            repeat {
                i += 1
            } while false
            return i
        }

        public fn area(_ s: Shape) -> Int {
            return match s {
                case .Circle(let r) => r * 3
                case .Square(let w) where w > 10 => 100
                case .Square(let w) => w * w
                case .Dot => 0
            }
        }

        public fn name(_ n: Int) -> String {
            switch n {
                case 1:
                    return "one"
                case 2, 3:
                    return "few"
                default:
                    return "many"
            }
        }

        public fn word(_ n: Int) -> String {
            var s = "none"
            switch n {
                case 1:
                    s = "one"
                case 2:
                    s = "two"
            }
            return s
        }

        public union Big {
            Big
        }

        fn fails(_ x: Int) throws(Big) -> Int {
            if x > 5 {
                throw Big.Big
            }
            return x
        }

        public fn attempt(_ x: Int) -> Int {
            return (try? fails(x)) ?? -1
        }

        @test
        fn exercise() {
            assertEqual(sign(5), 1)
            assertEqual(sign(0), 0)
            assertEqual(both(true, false), false)
            assertEqual(either(true, false), true)
            assertEqual(pick(true), 1)
            assertEqual(orZero(none), 0)
            assertEqual(unwrap(3), 3)
            assertEqual(guarded(4), 4)
            assertEqual(countDown(2), 2)
            assertEqual(untilLeft(2), 3)
            assertEqual(area(Shape.Circle(r: 2)), 6)
            assertEqual(area(Shape.Square(w: 3)), 9)
            assertEqual(name(2), "few")
            assertEqual(word(7), "none")
            assertEqual(attempt(1), 1)
            assertEqual(attempt(9), -1)
        }
        """;

    static int LineOf(string text) => Program[..Program.IndexOf(text, StringComparison.Ordinal)].Count(static c => c == '\n') + 1;

    /// <summary>Each decision, as <c>line:column kind name=count…</c>, for the counts a run made.</summary>
    static string[] Described(CoverageMap map, IReadOnlyDictionary<int, long> counts) =>
        [..map.Branches(counts)["branches.sb"].Select(static d => $"{d.Line}:{d.Column} {d.Kind} {string.Join(" ", d.Outcomes.Select(static o => $"{o.Label}={o.Count}"))}")];

    /// <summary>What the test takes of each decision: some outcomes, not others.</summary>
    static string[] Expected => [
        $"{LineOf("if n < 0")}:5 if true=0 false=2",
        $"{LineOf("} else if n == 0")}:12 if true=1 false=1",
        $"{LineOf("a && b")}:14 && right side run=1 right side skipped=0",
        $"{LineOf("a || b")}:14 || right side run=0 right side skipped=1",
        $"{LineOf("c ? 1 : 2")}:14 ?: true=1 false=0",
        $"{LineOf("x ?? 0")}:14 ?? value=0 fallback=1",
        $"{LineOf("if let y = x")}:5 if true=1 false=0",
        $"{LineOf("if let y = x")}:8 let some=1 none=0",
        $"{LineOf("guard let y = x")}:5 guard true=1 false=0",
        $"{LineOf("guard let y = x")}:11 let some=1 none=0",
        $"{LineOf("while i > 0")}:5 while true=2 false=1",
        $"{LineOf("if i >= n")}:9 if true=1 false=1",
        $"{LineOf("return match s")}:12 match .Circle(let r)=1 .Square(let w) where w > 10=0 .Square(let w)=1 .Dot=0",
        $"{LineOf("switch n {\n        case 1:\n            return")}:5 switch case 1=0 case 2, 3=1 default=0",
        $"{LineOf("switch n {\n        case 1:\n            s =")}:5 switch case 1=0 case 2=0 no case=1",
        $"{LineOf("if x > 5")}:5 if true=1 false=1",
        $"{LineOf("(try? fails(x))")}:13 try? value=1 error=1",
        $"{LineOf("?? -1")}:28 ?? value=1 fallback=1",
    ];

    static CompileResult Compile() {
        var result = TestHarness.Compile([SyntaxTree.Parse(new SourceText(Program, "branches.sb"))], TestKind.Test, out _, coverage: static _ => true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        return result;
    }

    [Fact]
    public void TheInterpreterCountsEachOutcome() {
        var result = Compile();
        var interpreter = new SilInterpreter(result.Sil!, 50_000_000);
        interpreter.Run(result.Sil!.EntryPoint!);
        Assert.Equal(Expected, Described(result.Coverage!, interpreter.CoverageCounts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCodeCountsTheSameAsTheInterpreter(bool optimise) {
        if (!CanBuild) {
            return;
        }

        var result = Compile();
        var interpreter = new SilInterpreter(result.Sil!, 50_000_000);
        interpreter.Run(result.Sil!.EntryPoint!);

        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-branches-{Guid.NewGuid():N}"));
        var counts = Path.Combine(Path.GetTempPath(), $"sable-branches-{Guid.NewGuid():N}.txt");
        try {
            var build = NativeBuild.Build(result.LlvmIr!, executable, optimise);
            Assert.True(build.Succeeded, build.Output);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
            start.Environment["SABLE_COVERAGE"] = counts;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await stdout + stderr);

            var native = Coverage.ReadCounts(counts);
            Assert.Equal(Expected, Described(result.Coverage!, native));

            // Every counter, lines' and branches', the same.
            Assert.Equal(interpreter.CoverageCounts.OrderBy(static c => c.Key), native.OrderBy(static c => c.Key));
        } finally {
            Platform.DeleteExecutable(executable);
            File.Delete(counts);
        }
    }

    /// <summary>
    ///     No pass drops or merges a counter: every outcome's is still in the
    ///     module the passes leave, once each — nothing in this program is
    ///     dead, so none may go.
    /// </summary>
    [Fact]
    public void EveryOutcomesCounterSurvivesThePasses() {
        var result = Compile();
        var left = result.Sil!.Functions
            .SelectMany(static f => f.Blocks)
            .SelectMany(static b => b.Instructions)
            .OfType<BuiltinInst>()
            .Where(static b => b.Name == Coverage.Builtin)
            .Select(static b => (int)((IntegerLiteralInst)b.Operands[0]).Value)
            .ToList();
        var outcomes = result.Coverage!.Decisions.SelectMany(static d => d.Outcomes).SelectMany(static o => o.Counters).ToList();
        Assert.NotEmpty(outcomes);
        Assert.All(outcomes, id => Assert.Contains(id, left));
    }

    /// <summary>Without coverage, lowering marks nothing: an ordinary build is as it was.</summary>
    [Fact]
    public void AnUnmeasuredBuildMarksNoDecisions() {
        var result = TestHarness.Compile([new SourceText(Program, "branches.sb")], TestKind.Test, out _);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.All(result.Sil!.Functions, static f => Assert.Empty(f.Decisions));
        Assert.All(result.Sil!.Functions.SelectMany(static f => f.Blocks), static b => Assert.Null(b.Outcome));
    }

    [Fact]
    public void LcovHasEachOutcomeOfEachDecision() {
        var map = new CoverageMap(
            [new("a.sb", [3]), new("a.sb", []), new("a.sb", []), new("a.sb", []), new("a.sb", [])],
            [
                new("a.sb", 3, 5, "if", [new("true", [1]), new("false", [2])]),
                new("a.sb", 3, 9, "&&", [new("right side run", [3]), new("right side skipped", [4])]),
                // The same decision lowered twice, as a generic's is: its counts added.
                new("a.sb", 3, 5, "if", [new("true", []), new("false", [2])]),
                new("a.sb", 7, 5, "while", [new("true", []), new("false", [])]),
            ]);
        var counts = new Dictionary<int, long> { [0] = 4, [1] = 3, [2] = 1, [4] = 1 };
        Assert.Equal(
            "SF:a.sb\nDA:3,4\nLF:1\nLH:1\n"
            + "BRDA:3,0,0,3\nBRDA:3,0,1,2\nBRDA:3,1,0,0\nBRDA:3,1,1,1\nBRDA:7,0,0,-\nBRDA:7,0,1,-\nBRF:6\nBRH:3\nend_of_record\n",
            Coverage.Lcov(map.Lines(counts), map.Branches(counts)));
    }
}
