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

    // ---- Array patterns -------------------------------------------------------

    [Fact]
    public void ArrayPatternsMatchByLengthAndElement() =>
        Prints(
            "empty\none: x\nthree: a c\nmany: a +1\nmany: a +3\n",
            """
            fn describe(_ xs: String[]) -> String {
                return match xs {
                    case [] => "empty"
                    case [let a] => "one: \(a)"
                    case [let first, _, let last] => "three: \(first) \(last)"
                    case [let head, ...let tail] => "many: \(head) +\(tail.count)"
                }
            }
            """,
            """
                print(describe([]))
                print(describe(["x"]))
                print(describe(["a", "b", "c"]))
                print(describe(["a", "b"]))
                print(describe(["a", "b", "c", "d"]))
            """
        );

    [Fact]
    public void TheRestOfAnArrayIsASlice() =>
        Prints(
            "10\n0\n",
            """
            fn sum(_ xs: Int[:]) -> Int {
                return match xs {
                    case [] => 0
                    case [let head, ...let tail] => head + sum(tail)
                }
            }
            """,
            """
                print(sum([1, 2, 3, 4]))
                print(sum([]))
            """
        );

    [Fact]
    public void ElementsAfterTheRestCountFromTheEnd() =>
        Prints(
            "7\n9\n-1\n-1\n",
            """
            fn ends(_ xs: Int[]) -> Int {
                return match xs {
                    case [1, ..., let last] => last
                    case [..., 9] => 9
                    default => -1
                }
            }
            """,
            """
                print(ends([1, 5, 7]))
                print(ends([2, 9]))
                print(ends([2, 3]))
                print(ends([]))
            """
        );

    [Fact]
    public void ArrayPatternsNestAndBindObjects() =>
        // A failed match after an element has been looked at gives nothing back twice.
        Prints(
            "ann and bob\nnot a pair\nann alone\n",
            """
            class Person {
                let name: String
                init(name: String) { self.name = name }
            }

            fn pair(_ people: Person[], _ flag: Bool) -> String {
                return match (people, flag) {
                    case ([let a, let b], true) => "\(a.name) and \(b.name)"
                    case ([let a], _) => "\(a.name) alone"
                    default => "not a pair"
                }
            }
            """,
            """
                let ann = Person(name: "ann")
                let bob = Person(name: "bob")
                print(pair([ann, bob], true))
                print(pair([ann, bob], false))
                print(pair([ann], true))
                let both = [bob, ann]
                if case [let first, ...] = both {
                    if first.name != "bob" {
                        print("wrong")
                    }
                }
            """
        );

    // ---- Optional patterns ----------------------------------------------------

    [Fact]
    public void SomeAndNoneMatchAnOptional() =>
        Prints(
            "30\n4\n0\n7\n-1\n0\n",
            """
            union Outcome { Ok(Int), Failed(String) }

            fn plain(_ o: Int?) -> Int {
                return match o {
                    case .Some(3) => 30
                    case .Some(let v) => v
                    case .None => 0
                }
            }

            fn nested(_ o: Outcome?) -> Int {
                return match o {
                    case .Some(.Ok(let v)) => v
                    case .Some(.Failed(_)) => -1
                    case .None => 0
                }
            }
            """,
            """
                let three: Int? = .Some(3)
                print(plain(three))
                print(plain(.Some(4)))
                print(plain(.None))
                print(nested(.Some(.Ok(7))))
                print(nested(.Some(.Failed("no"))))
                print(nested(none))
            """
        );

    [Fact]
    public void AQuestionMarkPatternUnwrapsAnObject() =>
        Prints(
            "hi\nnothing\nhi there\n",
            """
            fn say(_ o: String?) -> String {
                if case let text? = o {
                    return text
                }
                return "nothing"
            }

            fn both(_ a: String?, _ b: String?) -> String {
                return match (a, b) {
                    case (let x?, let y?) => x + " " + y
                    default => "not both"
                }
            }
            """,
            """
                print(say("hi"))
                print(say(none))
                print(both("hi", "there"))
            """
        );

    [Fact]
    public void ABindingAfterEarlierArmsBindsWhatIsLeft() =>
        Prints(
            "0\n7\n1\n5\n",
            """
            fn next(_ o: Int?) -> Int {
                return match o {
                    case none => 0
                    case let v => v + 1
                }
            }

            fn size(_ o: Int | String) -> Int {
                return match o {
                    case let s as String => 1
                    case let rest => rest
                }
            }
            """,
            """
                print(next(none))
                print(next(6))
                print(size("a"))
                print(size(5))
            """
        );

    [Fact]
    public void AThrowInAnArmOrAfterACoalesceLeavesWithTheError() =>
        Prints(
            "7\nskipped\n3\nambiguous\n4\nmissing\n",
            """
            union Failure { Skipped, Ambiguous, Missing }
            union Fit { One(Int), Gap(Int, Int), Overlap }

            fn resolve(_ f: Fit) throws(Failure) -> Int {
                let n = match f {
                    case .Gap(_, _) => throw Failure.Skipped
                    case .One(let v) => v
                    case _ => throw .Ambiguous
                }
                return n + 1
            }

            fn need(_ o: Int?) throws(Failure) -> Int {
                return o ?? throw Failure.Missing
            }

            fn show(_ f: Failure) -> String {
                return match f {
                    case .Skipped => "skipped"
                    case .Ambiguous => "ambiguous"
                    case .Missing => "missing"
                }
            }
            """,
            """
                print(try resolve(.One(6)) catch { -1 })
                do { print(try resolve(.Gap(1, 2))) } catch { print(show(error)) }
                print(try resolve(.One(2)) catch { -1 })
                do { print(try resolve(.Overlap)) } catch { print(show(error)) }
                print(try need(4) catch { -1 })
                do { print(try need(none)) } catch { print(show(error)) }
            """
        );
}
