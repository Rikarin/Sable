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
[Collection(TimingCollection.Name)]
[Trait("Category", "Timing")]
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

        // The criterion is five seconds on a development machine, where this
        // measures about one, of which two thirds is the parse. A shared CI
        // runner, or a machine busy with other work, is slower at everything
        // alike: the budget is scaled by how much slower it is than the
        // reference machine at a fixed workload that is not the compiler's
        // (Calibrate), measured just before. A measurement over budget is
        // taken again, up to three times, as a moment's load can spoil one;
        // a real slowdown — quadratic where it was linear — fails all three.
        var attempts = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++) {
            var slowdown = Math.Max(1.0, Calibrate() / ReferenceCalibrationMs);
            var budget = 5.0 * slowdown;

            var watch = Stopwatch.StartNew();
            var compilation = Compilation.Create(source);
            var diagnostics = compilation.GetAllDiagnostics();
            watch.Stop();

            Assert.True(
                diagnostics.Count == 0,
                string.Join("\n", diagnostics.Take(5).Select(static d => $"{d.Id}: {d.Message}"))
            );

            var seconds = watch.Elapsed.TotalSeconds;
            attempts.Add($"{seconds:F2} s against {budget:F1} s (the machine {slowdown:F1}x the reference)");
            if (seconds < budget) {
                // Printed so a run says what it measured, not just that it passed.
                Console.WriteLine($"type-checked {lines:N0} lines in {attempts[^1]}");
                return;
            }
        }

        Assert.Fail($"type-checking {lines:N0} lines took {string.Join("; then ", attempts)}");
    }

    /// <summary>
    ///     What <see cref="Calibrate" /> measures on the development machine
    ///     the five-second criterion was set on (an Apple M-series laptop).
    /// </summary>
    const double ReferenceCalibrationMs = 120;

    /// <summary>
    ///     How long this machine takes, now, for a fixed workload shaped like
    ///     a compiler's — strings built and hashed into a dictionary, a list
    ///     sorted, objects allocated — that does not use the compiler, so
    ///     that a slower compiler cannot make its own budget larger. The best
    ///     of five, in milliseconds.
    /// </summary>
    static double Calibrate() {
        var best = double.MaxValue;
        for (var run = 0; run < 5; run++) {
            var watch = Stopwatch.StartNew();
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < 200_000; i++) {
                map["name" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = i;
            }

            var sum = 0L;
            for (var i = 0; i < 200_000; i += 3) {
                sum += map["name" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            }

            var list = new List<int>(map.Values);
            list.Sort(static (a, b) => (b ^ 0x5555).CompareTo(a ^ 0x5555));
            var nodes = new List<object>();
            for (var i = 0; i < 200_000; i++) {
                nodes.Add(new KeyValuePair<int, string>(i, "x"));
            }

            watch.Stop();
            GC.KeepAlive(sum + list[0] + nodes.Count);
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        return best;
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

    [Fact]
    public void AMemberOfACollectionThatSaysNoneOfItsAssociatedTypesIsFoundQuickly() {
        // A generic type that conforms to RandomAccessCollection,
        // MutableCollection and RangeReplaceableCollection, and says none of
        // their associated types: each is inferred, and one can be inferred
        // from another through a protocol's where clause. Inferring one used
        // to resolve every other the protocol has, each of which resolved
        // every other again, so each member reached through self, 'storage'
        // here, cost tens of thousands of inferences — 70 ms apiece, and a
        // fuzzed file of a few hundred lines took longer than the fuzzer's
        // twenty seconds. The two hundred here took fourteen seconds, and now
        // take a fraction of one.
        var source = new StringBuilder("module Bench\n\npublic struct Pile<Element> {\n    var storage: Element[>..] = []\n\n    public fn look() {\n");
        for (var i = 0; i < 200; i++) {
            source.Append(System.Globalization.CultureInfo.InvariantCulture, $"        let a{i} = storage[{i}]\n");
        }

        source.Append("    }\n}\n\nextension Pile: RandomAccessCollection, MutableCollection, RangeReplaceableCollection {\n    public var startIndex: Int => 0\n}\n");

        _ = Bind(source.ToString()); // warm

        var attempts = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++) {
            var slowdown = Math.Max(1.0, Calibrate() / ReferenceCalibrationMs);
            var budget = 3.0 * slowdown;

            var watch = Stopwatch.StartNew();
            var diagnostics = Bind(source.ToString());
            watch.Stop();

            // It does not conform, as it says no endIndex, among others; the
            // lookups themselves all find 'storage'.
            Assert.DoesNotContain(diagnostics, static d => d.Message.Contains("no symbol named", StringComparison.Ordinal));

            var seconds = watch.Elapsed.TotalSeconds;
            attempts.Add($"{seconds:F2} s against {budget:F1} s (the machine {slowdown:F1}x the reference)");
            if (seconds < budget) {
                return;
            }
        }

        Assert.Fail($"binding two hundred lookups took {string.Join("; then ", attempts)}");

        static List<Diagnostic> Bind(string text) {
            var compilation = Compilation.Create(text);
            var diagnostics = new List<Diagnostic>(compilation.GetSemanticModel(compilation.SyntaxTrees[0]).GetDiagnostics());
            diagnostics.AddRange(compilation.GetDiagnostics());
            return diagnostics;
        }
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
