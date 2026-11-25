using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>@vectorised</c>: a function compiled for AVX2 too on x86-64, unless it
///     gives another function a vector, and what it gives the same interpreted,
///     natively, and natively told that the processor has no more than the
///     baseline.
/// </summary>
[Native]
public sealed class VectorisedTests {
    const string Source = """
        module V

        @vectorised
        fn least(_ xs: I32[:]) -> I32 {
            var lanes = SIMD<I32, 8>(repeating: I32.max)
            var i = 0
            while i + 8 <= xs.count {
                lanes = lanes.min(SIMD<I32, 8>.load(from: xs, at: i))
                i += 8
            }
            var found = lanes.reduceMin()
            while i < xs.count {
                found = found < xs[i] ? found : xs[i]
                i += 1
            }
            return found
        }

        @vectorised
        fn fill(_ xs: inout I32[>..], _ value: I32) {
            for i in 0..<xs.count { xs[i] = xs[i] &+ value }
        }

        @vectorised
        fn failing(_ x: Int) throws(Failure) -> Int {
            if x < 0 { throw Failure.Negative }
            return x * 2
        }

        enum Failure: Error { Negative }

        @inline(never)
        fn doubled(_ v: SIMD<I32, 8>) -> SIMD<I32, 8> => v &+ v

        @vectorised
        fn passing(_ xs: I32[:]) -> I32 => doubled(SIMD<I32, 8>.load(from: xs, at: 0)).reduceMax()

        @main
        fn main() {
            var xs: I32[>..] = []
            var x = 12345
            for i in 0..<100 {
                x = (x * 1103515245 + 12345) % 2147483648
                xs.append(I32(x % 20001) - 10000)
                print(least(xs))
                if i >= 8 { print(passing(xs)) }
            }
            fill(&xs, 5)
            print(least(xs))
            print(((try? failing(21)) ?? 0) + ((try? failing(-1)) ?? 100))
        }
        """;

    [Fact]
    public void OneThatPassesNoVectorIsCompiledForAvx2Too() {
        var ir = LlvmFor("x86_64-unknown-linux-gnu");
        Assert.Contains("sable.V.least$avx2\"", Simplified(ir));
        Assert.Contains("sable.V.fill$avx2\"", Simplified(ir));
        Assert.Contains("sable.V.failing$avx2\"", Simplified(ir));
        Assert.DoesNotContain("sable.V.passing$avx2\"", Simplified(ir));
        Assert.Contains("\"target-features\"=\"+avx2", ir);
        Assert.DoesNotContain("$avx2\"", LlvmFor("aarch64-unknown-linux-gnu"));
        // Under link-time optimisation the target's own features are kept, each once.
        var linked = NativeBuild.WithHostAttributes(ir, "\"target-cpu\"=\"x86-64\" \"target-features\"=\"+cx8,+sse2\"");
        Assert.Contains("attributes #9001 = { \"target-cpu\"=\"x86-64\" \"target-features\"=\"+avx2,+bmi,+bmi2,+popcnt,+cx8,+sse2\" }", linked);
        Assert.Contains("#9001 nounwind noinline", linked);
    }

    [Fact]
    public void EachGivesTheSameHoweverItRuns() {
        var expected = AssertAgree(Source).Stdout;
        if (CanBuild) {
            Assert.Equal(expected, BuildAndRun(Source, environment: new Dictionary<string, string> { ["SABLE_PORTABLE"] = "1" }).Stdout);
        }
    }

    // A name without the mangled signature between it and the suffix.
    static string Simplified(string ir) => System.Text.RegularExpressions.Regex.Replace(ir, @"(sable\.V\.\w+)\$[^""]*?(\$avx2"")", "$1$2");

    static string LlvmFor(string triple) {
        var target = NativeTarget.Of(triple);
        var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(Source, "test.sb"), Pipeline.ParseOptionsFor(true, target))], optimised: true, target: target);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        return result.LlvmIr!;
    }
}
