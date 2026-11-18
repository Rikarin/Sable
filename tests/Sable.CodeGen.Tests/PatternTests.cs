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

    // ---- switch -----------------------------------------------------------------

    [Fact]
    public void SwitchOnIntegersFallsThroughWhenAsked() =>
        // The spec's own example, SYNTAX.md §15.
        Prints(
            "ok\nok\nredirect\nredirect\nfail\n",
            """
            fn handle(_ code: Int) {
                switch code {
                    case 200, 201: print("ok")
                    case 301: fallthrough
                    case 302: print("redirect")
                    default: print("fail")
                }
            }
            """,
            """
                handle(200)
                handle(201)
                handle(301)
                handle(302)
                handle(404)
            """
        );

    [Fact]
    public void SwitchOnAnEnumComparesCases() =>
        Prints(
            "warm\nwarm\ncool\n",
            """
            enum Colour { Red, Orange, Green, Blue }

            fn feel(_ c: Colour) {
                switch c {
                    case .Red, .Orange: print("warm")
                    case .Green, .Blue: print("cool")
                }
            }
            """,
            """
                feel(.Red)
                feel(Colour.Orange)
                feel(.Blue)
            """
        );

    [Fact]
    public void ASwitchWithNoMatchingCaseDoesNothing() =>
        Prints(
            "after\n",
            """
            fn only(_ n: Int) {
                switch n {
                    case 1: print("one")
                }
                print("after")
            }
            """,
            """
                only(2)
            """
        );

    [Fact]
    public void DefaultIsTriedLastWhereverItIsWritten() =>
        // A 'default' in the middle falls through into the case after it.
        Prints(
            "two\nother\nlast\n",
            """
            fn pick(_ n: Int) {
                switch n {
                    default:
                        print("other")
                        fallthrough
                    case 3: print("last")
                    case 2: print("two")
                }
            }
            """,
            """
                pick(2)
                pick(9)
            """
        );

    [Fact]
    public void BreakLeavesTheSwitchAndContinueTheLoop() =>
        Prints(
            "0 small\n1 skip\n2 small\n3 big\n",
            """
            fn walk() {
                for i in 0..<5 {
                    switch i {
                        case 1:
                            print("\(i) skip")
                            continue
                        case 3:
                            print("\(i) big")
                            break
                        case 4: break
                        default:
                            if i > 3 {
                                break
                            }
                            print("\(i) small")
                    }
                    if i == 3 {
                        return
                    }
                }
            }
            """,
            """
                walk()
            """
        );

    [Fact]
    public void ACasesDefersRunBeforeItFallsThrough() =>
        Prints(
            "first\nleaving first\nsecond\nleaving second\n",
            """
            fn run(_ n: Int) {
                switch n {
                    case 1:
                        defer { print("leaving first") }
                        print("first")
                        fallthrough
                    case 2:
                        defer { print("leaving second") }
                        print("second")
                }
            }
            """,
            """
                run(1)
            """
        );

    [Fact]
    public void SwitchOnStringsComparesTheirText() =>
        Prints(
            "greeting\nfarewell\nunknown\n",
            """
            fn kind(_ word: String) {
                let owned = word + ""
                switch owned {
                    case "hello", "hi": print("greeting")
                    case "bye": print("farewell")
                    default: print("unknown")
                }
            }
            """,
            """
                kind("hi")
                kind("bye")
                kind("what")
            """
        );
}
