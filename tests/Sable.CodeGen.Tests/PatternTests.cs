using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Patterns as they run: open and half-open ranges, optional patterns,
///     array patterns, and <c>switch</c> — natively and in the interpreter,
///     which must agree.
/// </summary>
public sealed class PatternTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    // ---- Ranges ---------------------------------------------------------------

    [Fact]
    public void RangesThatCoverAByteNeedNoDefault() =>
        // '..<' leaves its upper bound out: 10 is the second arm's.
        Prints(
            "1\n2\n2\n",
            """
            fn small(_ b: U8) -> Int {
                return match b {
                    case 0..<10 => 1
                    case 10...255 => 2
                }
            }
            """,
            """
                print(small(9))
                print(small(10))
                print(small(255))
            """
        );

    [Fact]
    public void OpenRangesMatchToTheEndOfTheType() =>
        Prints(
            "negative\nzero\nsmall\nlarge\n",
            """
            fn sign(_ n: Int) -> String {
                return match n {
                    case ..<0 => "negative"
                    case 0 => "zero"
                    case 1...9 => "small"
                    case 10... => "large"
                }
            }
            """,
            """
                print(sign(-5))
                print(sign(0))
                print(sign(9))
                print(sign(1000))
            """
        );
}
