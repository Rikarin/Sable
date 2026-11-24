using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The law of exclusivity (docs/02 §2.3, docs/04 §3.4): accesses one
///     call makes to overlapping storage, one of them a change, are rejected
///     when the compiler can see them, and trap at run time when it cannot.
/// </summary>
[Native]
public sealed class ExclusivityTests {
    const string Declarations = """
                                struct Pair {
                                    var a: Int
                                    var b: Int
                                    mutating fn absorb(_ n: inout Int) {
                                        a = a + n
                                        n = 0
                                    }
                                }

                                class Box {
                                    var value: Int
                                    var pair: Pair
                                    init(_ v: Int) {
                                        self.value = v
                                        self.pair = Pair { a: v, b: v }
                                    }
                                }

                                fn bump(_ x: inout Int, _ y: inout Int) {
                                    x = x + 1
                                    y = y + 2
                                }

                                fn copy(_ from: Int&, _ to: inout Int) {
                                    to = from
                                }

                                fn both(_ one: Box, _ two: Box) {
                                    bump(&one.value, &two.value)
                                }

                                fn withWrite(_ x: inout Int, _ body: () -> Void) {
                                    body()
                                    x = x + 1
                                }
                                """;

    static IReadOnlyList<string> Errors(string body) =>
        Pipeline.Compile(new SourceText(Program(Declarations, body), "test.sb")).Errors;

    [Theory]
    [InlineData("var p = Pair { a: 1, b: 2 }\nbump(&p.a, &p.a)", "overlapping accesses to 'p.a'")]
    [InlineData("var p = Pair { a: 1, b: 2 }\nbump(&p.a, &p.a)", "argument 1 ('x') is inout, and argument 2 ('y') is inout")]
    [InlineData("var p = Pair { a: 1, b: 2 }\nvar q = p\nbump(&q.a, &q.a)", "overlapping accesses to 'q.a'")]
    [InlineData("var values = [1, 2, 3]\nbump(&values[0], &values[1])", "overlapping accesses to 'values[…]'")]
    [InlineData("var n = 1\ncopy(n, &n)", "argument 1 ('from') reads it by reference, and argument 2 ('to') is inout")]
    [InlineData("var p = Pair { a: 1, b: 2 }\np.absorb(&p.a)", "the call to the mutating 'absorb' changes it")]
    [InlineData("let box = Box(1)\nbump(&box.value, &box.value)", "overlapping accesses to 'box.value'")]
    // A closure written into the call may run during it (#125).
    [InlineData("var m = 10\nwithWrite(&m) { m = 99 }", "argument 1 ('x') is inout, and argument 2 is a closure that changes it")]
    [InlineData("var m = 10\nwithWrite(&m) { print(m) }", "argument 1 ('x') is inout, and argument 2 is a closure that reads it")]
    [InlineData("var p = Pair { a: 1, b: 2 }\nwithWrite(&p.a) { p = Pair { a: 0, b: 0 } }", "overlapping accesses to 'p'")]
    public void OverlappingAccessesInOneCallAreRejected(string body, string message) {
        var errors = Errors(body);
        Assert.Contains(errors, e => e.Contains("S3062", StringComparison.Ordinal) && e.Contains(message, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A closure that changes a mutating member's self, written into a
    ///     call that changes a part of self too, is two accesses to self for
    ///     the length of the call, as in Swift: the closure has all of self.
    /// </summary>
    [Theory]
    [InlineData("withWrite(&n) { n += 1 }", "argument 1 ('x') is inout, and argument 2 is a closure that changes it")]
    [InlineData("withWrite(&n) { reset() }", "argument 2 is a closure that changes it")]
    [InlineData("inner.with { count += 1 }", "the call to the mutating 'with' changes it")]
    [InlineData("with { inner.x = 1 }", "the call to the mutating 'with' changes it")]
    public void AClosureChangingSelfOverlapsTheCallsOtherAccessesToIt(string body, string message) {
        var declarations = Declarations + """

                                          struct Inner {
                                              var x: Int

                                              mutating fn with(_ f: () -> Void) {
                                                  x += 1
                                                  f()
                                              }
                                          }

                                          struct Outer {
                                              var n: Int
                                              var count: Int
                                              var inner: Inner

                                              mutating fn reset() {
                                                  n = 0
                                              }

                                              mutating fn with(_ f: () -> Void) {
                                                  f()
                                              }

                                              mutating fn probe() {
                                                  BODY
                                              }
                                          }
                                          """;
        var errors = Pipeline.Compile(new SourceText(Program(declarations.Replace("BODY", body), ""), "test.sb")).Errors;
        Assert.Contains(errors, e => e.Contains("S3062", StringComparison.Ordinal) && e.Contains("overlapping accesses to 'self", StringComparison.Ordinal) && e.Contains(message, StringComparison.Ordinal));
    }

    /// <summary>One that changes self beside a call that changes other storage is fine.</summary>
    [Fact]
    public void AClosureChangingSelfBesideOtherStorageIsFine() =>
        Assert.Equal(
            "3 2\n",
            AssertAgree(Program(Declarations + """

                                               struct Outer {
                                                   var n: Int
                                                   var count: Int

                                                   mutating fn probe(_ other: inout Int) {
                                                       withWrite(&other) { n += 1 }
                                                       withWrite(&other) { count = n }
                                                   }
                                               }
                                               """, """
                                                   var o = Outer { n: 1, count: 0 }
                                                   var m = 0
                                                   o.probe(&m)
                                                   print("\(o.n + o.count - 1) \(m)")
                                               """)).Stdout
        );

    [Fact]
    public void DistinctStorageIsFine() =>
        Assert.Equal(
            "5 4 7\n4 4 4\n2 2\n",
            AssertAgree(
                Program(
                    Declarations,
                    """
                        var p = Pair { a: 1, b: 2 }
                        bump(&p.a, &p.b)
                        var n = 3
                        p.absorb(&n)
                        copy(p.a, &p.b)
                        var q = Pair { a: 0, b: 0 }
                        bump(&p.b, &q.b)
                        print("\(p.a) \(p.b - 2) \(p.b + q.b - 1)")
                        let one = Box(3)
                        let two = Box(3)
                        both(one, two)
                        copy(one.value, &two.pair.a)
                        print("\(one.value) \(two.pair.a) \(two.value - 1)")
                        var m = 1
                        var k = 1
                        withWrite(&m) { k = k + 1 }
                        print("\(m) \(k)")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void TwoReferencesToOneObjectTrapAsTheProgramRuns() {
        var execution = AssertAgree(Program(Declarations, "    let box = Box(1)\n    both(box, Box(2))\n    print(box.value)\n    both(box, box)\n    print(\"unreachable\")"));
        Assert.Equal("2\n", execution.Stdout);
        Assert.Equal("fatal error: overlapping accesses to the same storage\n  at test.sb:30:5\n", execution.Stderr);
    }

    [Fact]
    public void AReleaseBuildDropsTheDynamicChecks() {
        var source = new SourceText(Program(Declarations, "    let box = Box(1)\n    both(box, box)\n    print(box.value)"), "test.sb");
        Assert.Contains("call void @sable_access_begin(", Pipeline.Compile(source).LlvmIr!, StringComparison.Ordinal);
        Assert.Contains("@sable_access_count", Pipeline.Compile(source).LlvmIr!, StringComparison.Ordinal);
        Assert.DoesNotContain("call void @sable_access_begin(", Pipeline.Compile(source, checkExclusivity: false).LlvmIr!, StringComparison.Ordinal);
        Assert.DoesNotContain("call void @sable_access_check(", Pipeline.Compile(source, checkExclusivity: false).LlvmIr!, StringComparison.Ordinal);
    }

    // ---- What the runtime sees (#186) ----------------------------------------

    const string Shared = """
                          module Test

                          var counter = 0

                          fn bump(_ x: inout Int) {
                              counter += 1
                              x += 1
                          }

                          fn add(_ x: inout Int, _ y: Int) {
                              x += y
                          }

                          fn withWrite(_ x: inout Int, _ body: () -> Void) {
                              body()
                              x = x + 1
                          }

                          class Box {
                              var value = 0
                              var items: Int[>..] = [1, 2, 3]
                          }

                          fn grow(_ x: inout Int, _ box: Box) {
                              box.items.append(x)
                              x = 0
                          }

                          """;

    static string SharedProgram(string body) => $"{Shared}@main\nfn main() {{\n{body}\n}}\n";

    /// <summary>A function given '&amp;counter' that changes 'counter' itself is two accesses to one global.</summary>
    [Fact]
    public void AGlobalPassedInoutAndTouchedInsideTraps() {
        var execution = AssertAgree(SharedProgram("    var other = 0\n    bump(&other)\n    print(counter)\n    bump(&counter)\n    print(\"unreachable\")"));
        Assert.Equal("1\n", execution.Stdout);
        Assert.Equal("fatal error: overlapping accesses to the same storage\n  at test.sb:6:5\n", execution.Stderr);
    }

    /// <summary>A plain read of a field during a change to it — through a closure the call cannot see into — traps.</summary>
    [Fact]
    public void AReadOfAFieldDuringAChangeToItTraps() {
        var execution = AssertAgree(SharedProgram("    let box = Box()\n    let peek = { print(box.value) }\n    peek()\n    withWrite(&box.value, peek)\n    print(\"unreachable\")"));
        Assert.Equal("0\n", execution.Stdout);
        Assert.Equal("fatal error: overlapping accesses to the same storage\n  at test.sb:31:18\n", execution.Stderr);
    }

    /// <summary>A captured variable lives in a box: a closure that changes it during an inout access to it traps.</summary>
    [Fact]
    public void ACapturedVariableChangedDuringAnAccessTraps() {
        var execution = AssertAgree(SharedProgram("    var n = 0\n    let inc = { n += 1 }\n    inc()\n    print(n)\n    withWrite(&n, inc)\n    print(\"unreachable\")"));
        Assert.Equal("1\n", execution.Stdout);
        Assert.Equal("fatal error: overlapping accesses to the same storage\n  at test.sb:31:17\n", execution.Stderr);
    }

    /// <summary>
    ///     An inout access begins once every argument is evaluated, so an
    ///     argument after it may read the same storage; an element passes
    ///     inout from an array anywhere — a local, a field.
    /// </summary>
    [Fact]
    public void ArgumentsAreReadBeforeTheAccessAndElementsPassInout() =>
        Assert.Equal(
            "1\n6\n11\n4\n",
            AssertAgree(SharedProgram("""
                    let box = Box()
                    add(&box.value, box.value + 1)
                    print(box.value)
                    var local = [1, 2]
                    add(&local[0], 5)
                    print(local[0])
                    add(&box.items[0], 10)
                    print(box.items[0])
                    add(&box.items[2], box.items.count - 2)
                    print(box.items[2])
                """)).Stdout
        );

    /// <summary>An element lent inout keeps its array from changing: growing it would move the element.</summary>
    [Fact]
    public void AnArrayCannotChangeWhileAnElementIsLent() {
        var execution = AssertAgree(SharedProgram("    let box = Box()\n    grow(&box.items[0], box)\n    print(\"unreachable\")"));
        Assert.Equal("fatal error: overlapping accesses to the same storage\n  at test.sb:25:5\n", execution.Stderr);
    }

    // ---- A range of an array passed inout (#301) -------------------------------

    const string Ranges = """
                          class Rows {
                              var items: Int[>..] = [1, 2, 3, 4]
                          }

                          fn swapRuns(_ a: inout Int[:], _ b: inout Int[:]) {
                              let first = a[0]
                              a[0] = b[0]
                              b[0] = first
                          }

                          fn runs(_ one: Rows, _ two: Rows) {
                              swapRuns(&one.items[0..<2], &two.items[2...])
                          }
                          """;

    /// <summary>Two ranges of one array passed inout to one call are two accesses to the array, whether or not they overlap.</summary>
    [Theory]
    [InlineData("var values: Int[>..] = [1, 2, 3]\nswapRuns(&values[0..<2], &values[1..<3])")]
    [InlineData("var values: Int[>..] = [1, 2, 3, 4]\nswapRuns(&values[0..<2], &values[^2...])")]
    public void TwoRangesOfOneArrayInOneCallAreRejected(string body) {
        var errors = Pipeline.Compile(new SourceText(Program(Ranges, body), "test.sb")).Errors;
        Assert.Contains(errors, e => e.Contains("S3062", StringComparison.Ordinal) && e.Contains("overlapping accesses to 'values[…]'", StringComparison.Ordinal));
    }

    /// <summary>Ranges of two objects' arrays are fine; of one object's, reached through two references, they trap as the program runs.</summary>
    [Fact]
    public void TwoRangesOfOneObjectsArrayTrapAsTheProgramRuns() {
        var execution = AssertAgree(Program(Ranges, "    let rows = Rows()\n    runs(rows, Rows())\n    print(\"\\(rows.items)\")\n    runs(rows, rows)\n    print(\"unreachable\")"));
        Assert.Equal("[3, 2, 3, 4]\n", execution.Stdout);
        Assert.StartsWith("fatal error: overlapping accesses to the same storage\n", execution.Stderr, StringComparison.Ordinal);
    }
}
