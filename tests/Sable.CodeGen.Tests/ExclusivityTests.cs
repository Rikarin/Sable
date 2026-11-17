using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The law of exclusivity (docs/02 §2.3, docs/04 §3.4): accesses one
///     call makes to overlapping storage, one of them a change, are rejected
///     when the compiler can see them, and trap at run time when it cannot.
/// </summary>
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

                                fn copy(_ from: ref Int, _ to: inout Int) {
                                    to = from
                                }

                                fn both(_ one: Box, _ two: Box) {
                                    bump(&one.value, &two.value)
                                }
                                """;

    static IReadOnlyList<string> Errors(string body) =>
        Pipeline.Compile(new SourceText(Program(Declarations, body), "test.sab")).Errors;

    [Theory]
    [InlineData("var p = Pair { a: 1, b: 2 }\nbump(&p.a, &p.a)", "overlapping accesses to 'p.a'")]
    [InlineData("var p = Pair { a: 1, b: 2 }\nbump(&p.a, &p.a)", "argument 1 ('x') is inout, and argument 2 ('y') is inout")]
    [InlineData("var p = Pair { a: 1, b: 2 }\nvar q = p\nbump(&q.a, &q.a)", "overlapping accesses to 'q.a'")]
    [InlineData("var values = [1, 2, 3]\nbump(&values[0], &values[1])", "overlapping accesses to 'values[…]'")]
    [InlineData("var n = 1\ncopy(&n, &n)", "argument 1 ('from') reads it by reference, and argument 2 ('to') is inout")]
    [InlineData("var p = Pair { a: 1, b: 2 }\np.absorb(&p.a)", "the call to the mutating 'absorb' changes it")]
    [InlineData("let box = Box(1)\nbump(&box.value, &box.value)", "overlapping accesses to 'box.value'")]
    public void OverlappingAccessesInOneCallAreRejected(string body, string message) {
        var errors = Errors(body);
        Assert.Contains(errors, e => e.Contains("S3062", StringComparison.Ordinal) && e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void DistinctStorageIsFine() =>
        Assert.Equal(
            "5 4 7\n4 4 4\n",
            AssertAgree(
                Program(
                    Declarations,
                    """
                        var p = Pair { a: 1, b: 2 }
                        bump(&p.a, &p.b)
                        var n = 3
                        p.absorb(&n)
                        copy(&p.a, &p.b)
                        var q = Pair { a: 0, b: 0 }
                        bump(&p.b, &q.b)
                        print("\(p.a) \(p.b - 2) \(p.b + q.b - 1)")
                        let one = Box(3)
                        let two = Box(3)
                        both(one, two)
                        copy(&one.value, &two.pair.a)
                        print("\(one.value) \(two.pair.a) \(two.value - 1)")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void TwoReferencesToOneObjectTrapAsTheProgramRuns() {
        var execution = AssertAgree(Program(Declarations, "    let box = Box(1)\n    both(box, Box(2))\n    print(box.value)\n    both(box, box)\n    print(\"unreachable\")"));
        Assert.Equal("2\n", execution.Stdout);
        Assert.Equal("fatal error: overlapping accesses to the same storage\n", execution.Stderr);
    }

    [Fact]
    public void AReleaseBuildDropsTheDynamicChecks() {
        var source = new SourceText(Program(Declarations, "    let box = Box(1)\n    both(box, box)"), "test.sab");
        Assert.Contains("call void @sable_access_begin(", Pipeline.Compile(source).LlvmIr!, StringComparison.Ordinal);
        Assert.DoesNotContain("call void @sable_access_begin(", Pipeline.Compile(source, checkExclusivity: false).LlvmIr!, StringComparison.Ordinal);
    }
}
