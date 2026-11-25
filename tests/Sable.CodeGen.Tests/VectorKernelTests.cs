using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Math's kernels give the same bits interpreted, natively — where an
///     x86-64 processor with AVX2 runs the runtime's own — and natively told it
///     has no more than the baseline, over every length that has a tail.
/// </summary>
[Native]
public sealed class VectorKernelTests {
    [Fact]
    public void EveryKernelGivesTheSameBitsHoweverItRuns() {
        const string source = """
            module V
            import Sable.Math

            fn mixed(_ hash: U64, _ bits: U64) -> U64 {
                return (hash ^ bits) &* 1099511628211
            }

            fn mixedAll(_ hash: U64, _ values: F32[:]) -> U64 {
                var hash = hash
                for value in values { hash = mixed(hash, U64(value.bits)) }
                return hash
            }

            fn mixedAll(_ hash: U64, _ values: F64[:]) -> U64 {
                var hash = hash
                for value in values { hash = mixed(hash, value.bits) }
                return hash
            }

            @main
            fn main() {
                var x: F32[>..] = []
                var y: F32[>..] = []
                var u: F64[>..] = []
                var v: F64[>..] = []
                var state: U64 = 88172645463325252
                for count in 0..<150 {
                    var hash: U64 = 14695981039346656037
                    var a = F32[>..](repeating: 7.0, count: count + 2)
                    var b = F64[>..](repeating: 7.0, count: count + 2)
                    hash = mixed(hash, U64(TensorPrimitives.sum(x).bits))
                    hash = mixed(hash, U64(TensorPrimitives.dot(x, y).bits))
                    hash = mixed(hash, U64(TensorPrimitives.norm(x).bits))
                    hash = mixed(hash, TensorPrimitives.sum(u).bits)
                    hash = mixed(hash, TensorPrimitives.dot(u, v).bits)
                    if count > 0 {
                        hash = mixed(hash, U64(TensorPrimitives.cosineSimilarity(x, y).bits))
                        hash = mixed(hash, TensorPrimitives.cosineSimilarity(u, v).bits)
                        hash = mixed(hash, TensorPrimitives.sum(u[1..<count]).bits)
                        hash = mixed(hash, U64(TensorPrimitives.dot(x[1..<count], y[0..<count - 1]).bits))
                        TensorPrimitives.softmax(x, into: &a)
                        hash = mixedAll(hash, a)
                        TensorPrimitives.softmax(u, into: &b)
                        hash = mixedAll(hash, b)
                    }
                    TensorPrimitives.add(x, y, into: &a)
                    hash = mixedAll(hash, a)
                    TensorPrimitives.subtract(x, y, into: &a)
                    hash = mixedAll(hash, a)
                    TensorPrimitives.multiply(x, y, into: &a)
                    hash = mixedAll(hash, a)
                    TensorPrimitives.divide(x, y, into: &a)
                    hash = mixedAll(hash, a)
                    let before = b
                    TensorPrimitives.add(u, v, into: &b)
                    hash = mixedAll(hash, b)
                    TensorPrimitives.subtract(u, v, into: &b)
                    hash = mixedAll(hash, b)
                    TensorPrimitives.multiply(u, v, into: &b)
                    hash = mixedAll(hash, b)
                    TensorPrimitives.divide(u, v, into: &b)
                    hash = mixedAll(hash, b)
                    hash = mixedAll(hash, before)
                    print(hash)
                    state = state ^ (state << 13)
                    state = state ^ (state >> 7)
                    state = state ^ (state << 17)
                    x.append(F32(state % 100003) / 331.0 - 150.0)
                    y.append(F32(state % 70001) / 77.0 + 0.1)
                    u.append(F64(state % 1000003) / 33.0 - 15000.0)
                    v.append(F64(state % 700001) / 7.7 + 0.1)
                }
            }
            """;
        var expected = AssertAgree(source, stepLimit: 100_000_000).Stdout;
        if (CanBuild) {
            Assert.Equal(expected, BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_PORTABLE"] = "1" }).Stdout);
        }
    }
}
