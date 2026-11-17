using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Regions (docs/02 §2.1, docs/04 §3.5): an arena values are bump-allocated
///     into and released with all at once when its block ends; references
///     into it are rooted in it, so root inference keeps them from outliving
///     it. Native and interpreted, leak-checked.
/// </summary>
public sealed class RegionTests {
    const string Declarations = """
                                struct Node {
                                    var value: Int
                                    var label: String
                                }

                                class Noisy {
                                    var name: String
                                    init(_ name: String) {
                                        self.name = name
                                    }
                                    deinit {
                                        print(name + " released with the region")
                                    }
                                }

                                struct Holder {
                                    var held: Noisy
                                }

                                fn build(in r: Region, _ v: Int) -> ref Node {
                                    return r.alloc(Node { value: v, label: "n\(v)" })
                                }

                                fn total(in r: Region, _ n: Int) -> Int {
                                    var sum = 0
                                    for i in 0..<n {
                                        let node = build(r, i)
                                        sum = sum + node.value + node.label.count
                                    }
                                    return sum
                                }
                                """;

    [Fact]
    public void ValuesLiveInTheRegionAndGoWithIt() =>
        Assert.Equal(
            "n1 two\n3\n5240\nin the region\nheld released with the region\nafter\n",
            AssertAgree(
                Program(
                    Declarations,
                    """
                        region arena {
                            let a = build(arena, 1)
                            let b = arena.alloc(Node { value: 2, label: "two" })
                            print(a.label + " " + b.label)
                            print(a.value + b.value)
                            print(total(arena, 100))
                            let h = arena.alloc(Holder { held: Noisy("held") })
                            print("in the region")
                        }
                        print("after")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AReferenceIntoAParameterRegionIsRootedThere() {
        var result = Pipeline.Compile(new SourceText(Program(Declarations, ""), "test.sab"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal([0], result.Sil!.Find("Test.build")!.ResultRoots);
    }

    static void Rejects(string declarations, string body, string message) {
        var result = Pipeline.Compile(new SourceText(Program(Declarations + "\n" + declarations, body), "test.sab"));
        Assert.False(result.Succeeded, "expected the program to be rejected");
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceCannotBeReturnedOutOfItsRegion() =>
        Rejects(
            """
            fn leak() -> ref Node {
                region arena {
                    return arena.alloc(Node { value: 1, label: "x" })
                }
            }
            """,
            "",
            "cannot return a reference rooted in region 'arena'"
        );

    [Fact]
    public void AReferenceCannotOutliveItsRegion() =>
        Rejects(
            "",
            """
                let outer = Node { value: 0, label: "outer" }
                var r = &outer
                region arena {
                    r = arena.alloc(Node { value: 1, label: "inner" })
                }
                print(r.label)
            """,
            "this reference refers to region 'arena', which is gone — the region ended at line"
        );
}
