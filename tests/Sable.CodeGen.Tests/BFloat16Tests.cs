using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     BF16: the interpreter's bfloat16 and LLVM's agree on every operation,
///     conversion, intrinsic and text, over values across the whole range.
/// </summary>
[Native]
public sealed class BFloat16Tests {
    [Fact]
    public void TheInterpreterAndTheNativeCodeAgree() {
        var run = AssertAgree("""
            module P
            fn show(_ x: BF16) {
                print("\(x) \(x.bits) \(x.squareRoot()) \(x.rounded()) \(x.nextUp) \(x.exponent) \(Int(x.isFinite && x < 1e18 && x > -1e18 ? x : 0.0))")
            }
            @main
            fn main() {
                var seed = 12345
                var i = 0
                while i < 300 {
                    seed = (seed * 1103515245 + 12345) % 2147483648
                    let a = BF16.fromBits(U16(seed % 65536))
                    seed = (seed * 1103515245 + 12345) % 2147483648
                    let b = BF16.fromBits(U16(seed % 65536))
                    print("\(a + b) \(a - b) \(a * b) \(a / b) \(a < b) \(a == b)")
                    print("\(a.addingProduct(b, b)) \(a.truncatingRemainder(dividingBy: b)) \(a.remainder(dividingBy: b))")
                    print("\(F64(a)) \(F32(a)) \(F16(a)) \(BF16(F64(seed) / 7.0)) \(BF16(seed)) \(BF16(F32(seed) * 1e30))")
                    print("\(a.scaled(byPowerOfTwo: seed % 300 - 150)) \(a.maximum(b)) \(a.minimum(b))")
                    show(a)
                    i += 1
                }
                print(BF16(I128.max))
                print(BF16(U128.max))
                print(BF16(-9007199254740993))
                print(BF16.pi)
            }
            """);
        Assert.Contains("3.14\n", run.Stdout, StringComparison.Ordinal);
    }
}
