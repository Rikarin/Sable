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
        var result = Pipeline.Compile(new SourceText(Program(Functions, ""), "test.sab"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal([0, 1], result.Sil!.Find("Test.longest")!.ResultRoots);
        Assert.Equal([0, 1, 2], result.Sil!.Find("Test.longestOfThree")!.ResultRoots);
        Assert.Equal([0, 1], result.Sil!.Find("Test.alternate")!.ResultRoots);
        Assert.Equal([0], result.Sil!.Find("Test.nameOf")!.ResultRoots);
        Assert.Contains("roots(0, 1)", SilPrinter.Print(result.Sil!), StringComparison.Ordinal);
    }

    static void Rejects(string declarations, string body, string message) {
        var result = Pipeline.Compile(new SourceText(Program(Functions + "\n" + declarations, body), "test.sab"));
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
