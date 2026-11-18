using System.Diagnostics;
using System.Text;

namespace Sable.Symbols.Tests;

/// <summary>
///     The exit criterion from docs/04 §1.4: type-checking 100,000 lines
///     completes in under five seconds, single-threaded.
/// </summary>
/// <remarks>
///     A throughput test is not a benchmark. What it catches is a regression from
///     linear to quadratic — a lookup that walks every declaration, a lazily bound
///     signature that stops being cached — which shows up as orders of magnitude,
///     not percent. The bound is loose enough to survive a loaded machine.
/// </remarks>
public sealed class ThroughputTests {
    /// <summary>Builds a program of roughly <paramref name="types" /> × 24 lines.</summary>
    static string LargeProgram(int types) {
        var builder = new StringBuilder("module Bench\n");

        for (var i = 0; i < types; i++) {
            builder.Append(
                $$"""
                  public struct Item{{i}} {
                      public var id: Int
                      public var name: String

                      public fn describe() -> String {
                          return name
                      }

                      public fn total(_ quantity: Int) -> Int {
                          var sum = 0
                          var k = 0
                          while k < quantity {
                              sum = sum + k * {{i}}
                              k = k + 1
                          }
                          return sum
                      }

                      public fn combined(_ other: Item{{i}}) -> Int {
                          let mine = total(1)
                          let theirs = other.total(2)
                          return mine + theirs + id + other.id
                      }
                  }

                  """
            );
        }

        return builder.ToString();
    }

    [Fact]
    public void TypeCheckingAHundredThousandLinesIsUnderFiveSeconds() {
        var source = LargeProgram(4_200);
        var lines = source.Count(static c => c == '\n');
        Assert.True(lines > 100_000, $"expected 100,000 lines, built {lines:N0}");

        // Warm the JIT on a small program, so the measurement is of the work
        // rather than of the first-call cost.
        _ = Compilation.Create(LargeProgram(10)).GetAllDiagnostics();

        var watch = Stopwatch.StartNew();
        var compilation = Compilation.Create(source);
        var diagnostics = compilation.GetAllDiagnostics();
        watch.Stop();

        Assert.True(
            diagnostics.Count == 0,
            string.Join("\n", diagnostics.Take(5).Select(static d => $"{d.Id}: {d.Message}"))
        );

        // The criterion is five seconds; this measures about one on a
        // development machine, of which two thirds is the parse. A shared CI
        // runner (CI is set) measures five times that on a bad day, so it is
        // given three times the budget: enough to stay quiet, not enough to
        // miss a real slowdown.
        var budget = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ? 5.0 : 15.0;
        Assert.True(
            watch.Elapsed.TotalSeconds < budget,
            $"type-checking {lines:N0} lines took {watch.Elapsed.TotalSeconds:F2} s, over {budget} s"
        );

        // Printed so a run says what it measured, not just that it passed.
        Console.WriteLine($"type-checked {lines:N0} lines in {watch.Elapsed.TotalSeconds:F2} s");
    }

    [Fact]
    public void TheCostIsLinearInTheNumberOfDeclarations() {
        // Ten times the input should cost roughly ten times as much. A lookup
        // that walks every declaration would make it a hundred.
        var small = Measure(200);
        var large = Measure(2_000);

        var ratio = large / Math.Max(small, 0.001);
        Assert.True(
            ratio < 20,
            $"ten times the input cost {ratio:F1} times as much "
            + $"({small:F0} ms then {large:F0} ms)"
        );
    }

    static double Measure(int types) {
        var source = LargeProgram(types);
        _ = Compilation.Create(source).GetAllDiagnostics(); // warm

        var watch = Stopwatch.StartNew();
        _ = Compilation.Create(source).GetAllDiagnostics();
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }
}
