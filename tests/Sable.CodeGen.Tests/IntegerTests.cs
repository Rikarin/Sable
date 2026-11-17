using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The sized integers and F32 (docs/03 §3.1), which inline layout stores:
///     arithmetic that traps on overflow at each width, comparisons by
///     signedness, conversions that trap when the value does not fit, and
///     printing. Native and interpreted, held to the same answers.
/// </summary>
public sealed class IntegerTests {
    [Fact]
    public void EachWidthComputesAsItsOwn() =>
        Assert.Equal(
            "200 70000 -5 1099511627776\n255\n18446744073709551615\n6148914691236517205\n1.1\n2.2\n201\n5\n-128\n"
            + "total 17850000\ntrue\ntrue\n1.100000023841858\n65535\n5\n-1 255\n4294967295\n7\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let a: U8 = 200
                        let b: U32 = 70000
                        let c: I16 = -5
                        let d: U64 = U64(1) << 40
                        print("\(a) \(b) \(c) \(d)")
                        var x: U8 = 250
                        x = x + 5
                        print(x)
                        let big: U64 = 18446744073709551615
                        print(big)
                        print(big / 3)
                        let f: F32 = 1.1
                        print(f)
                        print(f * 2)
                        print(Int(a) + 1)
                        print(U8(c + 10))
                        print(I8(-128) / I8(1))
                        let total = U64(x) * U64(b)
                        print("total \(total)")
                        print(c < 0)
                        print(a > U8(100))
                        print(F64(f))
                        print(U16(65535))
                        print(-c)
                        let m: I8 = -1
                        print("\(m) \(U8(255) & x)")
                        print(U32(4294967295) | U32(0))
                        print(U8(15) >> U8(1))
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("var x: U8 = 255\nx = x + 1\nprint(x)", "integer overflow")]
    [InlineData("var x: U16 = 0\nx = x - 1\nprint(x)", "integer overflow")]
    [InlineData("var x: I8 = -128\nx = x / -1\nprint(x)", "integer overflow")]
    [InlineData("let x: I32 = 300000\nprint(I16(x))", "I32 does not fit in I16")]
    [InlineData("let x = -1\nprint(U64(x))", "Int does not fit in U64")]
    [InlineData("let x: F32 = 300.5\nprint(U8(x))", "F32 does not fit in U8")]
    [InlineData("let x: U8 = 1\nprint(x << U8(8))", "shift out of range")]
    public void EachWidthTrapsAtItsOwnLimits(string body, string trap) {
        var execution = AssertAgree(Program("", body));
        Assert.Equal($"fatal error: {trap}\n", execution.Stderr);
    }

    [Fact]
    public void SizedValuesLiveInStructsArraysAndOptionals() =>
        Assert.Equal(
            "6 7 8\n21\n3\nnone\n",
            AssertAgree(
                Program(
                    """
                    struct Pixel {
                        var r: U8
                        var g: U8
                        var b: U8
                    }

                    fn sum(_ values: U16[]) -> U64 {
                        var total: U64 = 0
                        for v in values {
                            total = total + U64(v)
                        }
                        return total
                    }

                    fn half(_ x: I32) -> I32? {
                        if x % 2 == 1 {
                            return none
                        }
                        return x / 2
                    }
                    """,
                    """
                        let p = Pixel { r: 6, g: 7, b: 8 }
                        print("\(p.r) \(p.g) \(p.b)")
                        print(sum([1, 2, 3, 4, 5, 6]))
                        if let h = half(6) {
                            print(h)
                        }
                        if half(7) == none {
                            print("none")
                        }
                    """
                )
            ).Stdout
        );
}
