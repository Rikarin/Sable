using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Ranges (spec/SYNTAX.md §14, §16): of any integer or of characters, one-sided,
///     counted, and strides. Native and interpreted, held to the same answers.
/// </summary>
public sealed class RangeTests {
    [Fact]
    public void RangesOfAnyIntegerAndOfCharactersAreCounted() =>
        Assert.Equal(
            "abc\n253 254 255\n-2 -1\n5 3 0\nxy\n",
            AssertAgree(
                Program(
                    "",
                    """
                        var letters = ""
                        for c in 'a'...'c' { letters = letters + String.fromChar(c) }
                        print(letters)
                        let low: U8 = 253
                        let high: U8 = 255
                        var bytes = ""
                        for b in low...high { bytes = bytes + "\(b) " }
                        print(bytes.trimmed())
                        let start: I128 = -2
                        var wide = ""
                        for i in start..<0 { wide = wide + "\(i) " }
                        print(wide.trimmed())
                        print("\((0..<5).count) \((1...3).count) \((5..<2).count)")
                        let r = 'x'...'y'
                        var held = ""
                        for c in r { held = held + String.fromChar(c) }
                        print(held)
                    """
                )
            ).Stdout
        );

    [Fact]
    public void OneSidedRangesHoldTheirBound() =>
        Assert.Equal(
            "true false false true\n22\n5 6 7\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let below = ..<10
                        let from = 5...
                        let upTo = ...7
                        print("\(below.contains(9)) \(below.contains(10)) \(from.contains(4)) \(upTo.contains(7))")
                        print(upTo.upperBound + below.upperBound + from.lowerBound)
                        var line = ""
                        for x in from {
                            if x > 7 { break }
                            line = line + "\(x) "
                        }
                        print(line.trimmed())
                    """
                )
            ).Stdout
        );

    [Fact]
    public void StrideStepsUpDownAndStopsShortOfOverflow() =>
        Assert.Equal(
            "0 3 6 9\n10 5 0\n9 6\n9223372036854775800 9223372036854775804\n\n",
            AssertAgree(
                Program(
                    """
                    fn line<S: Sequence>(_ values: S) -> String where S.Iter.Element == Int {
                        var text = ""
                        for v in values { text = text + "\(v) " }
                        return text.trimmed()
                    }
                    """,
                    """
                        print(line(stride(from: 0, to: 10, by: 3)))
                        print(line(stride(from: 10, through: 0, by: -5)))
                        print(line(stride(from: 9, to: 3, by: -3)))
                        print(line(stride(from: 9223372036854775800, through: 9223372036854775807, by: 4)))
                        print(line(stride(from: 0, to: 10, by: 0)))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ARangeOfABytePastItsLargestValueTraps() {
        var execution = AssertAgree(Program("", "let b: U8 = 254\nfor x in b... { print(x) }"));
        Assert.Equal("254\n255\n", execution.Stdout);
        Assert.Equal("fatal error: integer overflow\n  at test.sb:6:17\n", execution.Stderr);
    }
}
