using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Root inference (docs/02 §2.4, docs/04 §3.3): every reference's root is
///     inferred — no annotation is written — and a reference may not outlive
///     it. A function's result roots are part of its interface.
/// </summary>
public sealed class RootTests {
    const string Functions = """
                             struct Pair {
                                 var name: String
                                 var count: Int
                             }

                             fn longest(ref a: String, ref b: String) -> ref String {
                                 return a.count >= b.count ? a : b
                             }

                             fn longestOfThree(ref a: String, ref b: String, ref c: String) -> ref String {
                                 return longest(longest(a, b), c)
                             }

                             fn alternate(ref a: String, ref b: String, _ n: Int) -> ref String {
                                 if n == 0 {
                                     return a
                                 }
                                 return alternate(b, a, n - 1)
                             }

                             fn nameOf(_ p: Pair&) -> String& {
                                 return &p.name
                             }
                             """;

    [Fact]
    public void RootsAreInferredThroughJoinsCallsAndRecursion() =>
        Assert.Equal(
            "hello\nlongest one\nhi\nn\nn!\n",
            AssertAgree(
                Program(
                    Functions,
                    """
                        let x = "hello"
                        let y = "hi"
                        print(longest(x, y))
                        let z = "longest one"
                        print(longestOfThree(x, y, z))
                        print(alternate(x, y, 3))
                        var p = Pair { name: "n", count: 2 }
                        let q = &p
                        print(q.name)
                        p.name = "n!"
                        print(nameOf(q))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void TheSummariesArePartOfTheInterface() {
        var result = Pipeline.Compile(new SourceText(Program(Functions, ""), "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal([0, 1], result.Sil!.Find("Test.longest")!.ResultRoots);
        Assert.Equal([0, 1, 2], result.Sil!.Find("Test.longestOfThree")!.ResultRoots);
        Assert.Equal([0, 1], result.Sil!.Find("Test.alternate")!.ResultRoots);
        Assert.Equal([0], result.Sil!.Find("Test.nameOf")!.ResultRoots);
        Assert.Contains("roots(0, 1)", SilPrinter.Print(result.Sil!), StringComparison.Ordinal);
    }

    static void Rejects(string declarations, string body, string message) {
        var result = Pipeline.Compile(new SourceText(Program(Functions + "\n" + declarations, body), "test.sb"));
        Assert.False(result.Succeeded, "expected the program to be rejected");
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceToALocalCannotBeReturned() =>
        Rejects(
            """
            fn bad() -> String& {
                let tmp = Pair { name: "x", count: 1 }
                return &tmp.name
            }
            """,
            "",
            "cannot return a reference rooted in local 'tmp'"
        );

    [Fact]
    public void ACalleeSummaryCarriesALocalOut() =>
        Rejects(
            """
            fn leak() -> ref String {
                let s = "short"
                let t = "much longer"
                return longest(s, t)
            }
            """,
            "",
            "cannot return a reference rooted in local"
        );

    [Fact]
    public void AByValueParameterDiesWithTheFunction() =>
        Rejects(
            """
            fn echo(_ s: String) -> ref String {
                return s
            }
            """,
            "",
            "rooted in parameter 's', which is passed by value"
        );

    [Fact]
    public void AReferenceCannotOutliveTheScopeOfItsRoot() =>
        Rejects(
            "",
            """
                let outer = "outer"
                var r = &outer
                do {
                    let inner = "in" + "ner"
                    r = &inner
                }
                print(r)
            """,
            "this reference refers to local 'inner', which is gone — its scope ended at line"
        );

    // A trivial local has nothing to destroy where its scope ends, but its
    // scope ends all the same (#128); and the use is reported once.
    [Theory]
    [InlineData("Int", "1", "9", "r")]
    [InlineData("Node", "Node(v: 1)", "Node(v: 9)", "r.v")]
    public void AReferenceCannotOutliveTheScopeOfATrivialRoot(string type, string outer, string inner, string read) {
        var result = Pipeline.Compile(new SourceText(Program(
            "struct Node { var v: Int }",
            $$"""
                var outer: {{type}} = {{outer}}
                var r = &outer
                if {{read}} > 0 {
                    var inner: {{type}} = {{inner}}
                    r = &inner
                }
                print({{read}})
            """), "test.sb"));
        Assert.False(result.Succeeded, "expected the program to be rejected");
        var error = Assert.Single(result.Errors);
        Assert.Contains("this reference refers to local 'inner', which is gone — its scope ended at line", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ACopyOfATrivialLocalOutlivesItsScope() =>
        Assert.Equal(
            "9\n",
            AssertAgree(Program(
                "struct Node { var v: Int }",
                """
                    var outer = Node(v: 1)
                    if outer.v == 1 {
                        var inner = Node(v: 9)
                        inner.v = inner.v + 0
                        outer = inner
                    }
                    print(outer.v)
                """
            )).Stdout
        );

    [Fact]
    public void AReferenceToATrivialLocalWithinItsScopeIsFine() =>
        Assert.Equal(
            "2\n",
            AssertAgree(Program(
                "struct Node { var v: Int }",
                """
                    var total = 0
                    for i in 0..<2 {
                        var n = Node(v: i)
                        let r = &n
                        total = total + r.v
                    }
                    print(total + 1)
                """
            )).Stdout
        );

    [Fact]
    public void AReferenceCannotOutliveAMoveOutOfItsRoot() =>
        Rejects(
            "",
            """
                var s = "a" + "b"
                let r = &s
                let t = move s
                print(r)
            """,
            "its value was moved out at line"
        );
}
