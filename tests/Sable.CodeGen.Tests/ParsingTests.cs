using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Core's Parsable conformances, native and interpreted alike:
///     the runtime's <c>string_to_F32</c> and the interpreter's agree on
///     rounding a decimal once to an F32, and on what is not one.
/// </summary>
public sealed class ParsingTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void AnF32IsRoundedOnceFromTheDecimal() =>
        // Just past the midpoint between 1 and the next F32: an F64 would hold
        // the midpoint itself, and an F32 made from that would round down.
        Prints(
            "1.0000001 16777216.0 0.1 true\n",
            "",
            """
                let up = try! F32.parse("1.000000059604644775390626")
                let even = try! F32.parse("16777217")
                let tenth = try! F32.parse("0.1")
                let inf = try! F32.parse("1e39")
                print("\(up) \(even) \(tenth) \(F64(inf) > 1e300)")
            """
        );

    [Fact]
    public void IntegersReportWhyTheyAreNotOne() =>
        Prints(
            "255 -128 Overflow Underflow InvalidDigit(at: 2) Empty\n",
            """
            fn why(_ text: String) -> String {
                do {
                    let _ = try I8.parse(text)
                    return "fine"
                } catch {
                    return match error {
                        case .Empty => "Empty"
                        case .InvalidDigit(let at) => "InvalidDigit(at: \(at))"
                        case .Invalid(let at) => "Invalid(at: \(at))"
                        case .Overflow => "Overflow"
                        case .Underflow => "Underflow"
                        default => "another"
                    }
                }
            }
            """,
            """
                let hex = try! Int.parse("0xff", radix: 16, styles: .Prefix)
                print("\(hex) \(try! I8.parse("-128")) \(why("128")) \(why("-129")) \(why("12x")) \(why(""))")
            """
        );
}
