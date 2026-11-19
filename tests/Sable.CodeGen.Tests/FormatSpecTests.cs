using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Format specs in interpolation, <c>"\(x:08x)"</c> (SYNTAX.md §16): the
///     spec becomes a <c>FormatSpec</c> passed to the value's
///     <c>Formattable.format</c>, or pads the value's usual text for a type
///     that does not write itself; native and interpreted alike, leak-checked.
/// </summary>
public sealed class FormatSpecTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void IntegersTakeARadixASignPaddingAndSeparators() =>
        Prints(
            "0x00ff|     255|255     |   255   |+255|11111111|FF|377\n1,234,567|1_0000_0000|-0000042|-80\n",
            "",
            """
                let n = 255
                print("\(n:#06x)|\(n:>8)|\(n:<8)|\(n:^9)|\(n:+)|\(n:b)|\(n:X)|\(n:o)")
                let small: I8 = -128
                print("\(1234567:,)|\(4294967296:_x)|\(-42:08)|\(small:x)")
            """
        );

    [Fact]
    public void FloatsTakeAPrecisionAndANotation() =>
        Prints(
            "3.14|1,234,567.9|25%|1e-07|1.235e+03|0.000123|2|4|-0.0|+1.5\n0.1|0.1000000015|nan|  inf\n",
            "",
            """
                let half: F64 = 0.5
                print("\(3.14159:.2)|\(1234567.891:,.1)|\(half / 2.0:%)|\(1e-7:e)|\(1234.5678:.3e)|\(0.000123:g)|\(2.5:.0)|\(3.5:.0)|\(-0.0:+)|\(1.5:+)")
                let f: F32 = 0.1
                let zero: F64 = 0.0
                print("\(f)|\(f:.10)|\(zero / zero)|\(1.0 / zero:>5)")
            """
        );

    [Fact]
    public void TextIsPaddedCutAndQuoted() =>
        Prints(
            "[**ab**] [  c] [true  ] \"q\\\"d\" 'c' [sab]\n",
            "",
            """
                print("[\("ab":*^6)] [\('c':>3)] [\(true:<6)] \("q\"d":?) \('c':?) [\("sable":.3)]")
            """
        );

    [Fact]
    public void ATypeThatWritesItselfIsGivenTheSpec() =>
        // With no spec it is given the one that says nothing; a type that
        // does not write itself is written as always, and padded.
        Prints(
            "2.5 m [  2.5 m]\n[ Plain(x: 1)] Plain(x: 1) [  East] East\n",
            """
            struct Meters: Formattable {
                var value: F64
                fn format(_ spec: FormatSpec) -> String {
                    return spec.padded(self.value.format(FormatSpec()) + " m")
                }
            }

            struct Plain {
                var x: Int
            }

            enum Direction {
                North
                East
            }
            """,
            """
                print("\(Meters(value: 2.5)) [\(Meters(value: 2.5):>7)]")
                print("[\(Plain(x: 1):>12)] \(Plain(x: 1)) [\(Direction.East:>6)] \(Direction.East)")
            """
        );

    [Fact]
    public void AGenericHoleIsWrittenAsItsInstanceIs() =>
        Prints(
            "[    42] [    hi] 0xff ff\n",
            """
            fn padded<T>(_ value: T) -> String {
                return "[\(value:>6)]"
            }

            fn hex<T: Formattable>(_ value: T) -> String {
                return "\(value:#x)"
            }
            """,
            """
                let flag = true
                print("\(padded(42)) \(padded("hi")) \(hex(255)) \(flag ? 255 : 0:x)")
            """
        );
}
