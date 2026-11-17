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

    const string Linked = """
                          struct Link {
                              var value: Int
                              var next: Link&?
                          }

                          fn chain(in r: region, _ n: Int) -> ref Link {
                              var head = r.alloc(Link { value: 0, next: none })
                              for i in 1..<n {
                                  head = r.alloc(Link { value: i, next: head })
                              }
                              return head
                          }

                          fn sum(_ head: ref Link) -> Int {
                              var total = head.value
                              var current = head.next
                              while current != none {
                                  total = total + current!.value
                                  current = current!.next
                              }
                              return total
                          }
                          """;

    [Fact]
    public void ALinkedStructureLivesInOneRegion() =>
        Assert.Equal(
            "4950\n45\n",
            AssertAgree(
                Program(
                    Linked,
                    """
                        region arena {
                            print(sum(chain(arena, 100)))
                            print(sum(chain(arena, 10)))
                        }
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("fn bad(in r: region) -> ref Link {\n    var local = Link { value: 1, next: none }\n    return r.alloc(Link { value: 2, next: &local })\n}", "cannot store a reference rooted in local 'local' into region 'r', passed in")]
    [InlineData("fn bad(in r: region, in s: region) -> ref Link {\n    let first = r.alloc(Link { value: 1, next: none })\n    return s.alloc(Link { value: 2, next: first })\n}", "cannot store a reference rooted in parameter 'r' into region 's', passed in")]
    [InlineData("fn bad() -> Link {\n    let inner = Link { value: 1, next: none }\n    return Link { value: 2, next: &inner }\n}", "cannot return a reference rooted in local 'inner'")]
    public void AReferenceInAFieldIsHeldToItsRoot(string declaration, string message) =>
        Rejects(Linked + "\n" + declaration, "", message);

    [Fact]
    public void AResetRegionIsReusedAndASourcedBlockGivesBackWhatItTook() =>
        Assert.Equal(
            "0\n1\n2\nframe 13\nleft 3\n",
            AssertAgree(
                Program(
                    Linked,
                    """
                        region pool {
                            var keep = pool.alloc(Link { value: 7, next: none })
                            for i in 0..<3 {
                                pool.reset()
                                keep = chain(pool, i + 1)
                                print(keep.value)
                            }
                            region frame = pool {
                                print("frame \(sum(chain(frame, 5)) + keep.value + 1)")
                            }
                            print("left \(sum(keep))")
                        }
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("let a = pool.alloc(Link { value: 1, next: none })\npool.reset()\nprint(a.value)", "the region was reset at line")]
    [InlineData("var keep = pool.alloc(Link { value: 1, next: none })\nfor i in 0..<3 {\n    pool.reset()\n    print(keep.value)\n}", "the region was reset at line")]
    [InlineData("region frame = pool {\n    let x = pool.alloc(Link { value: 1, next: none })\n}", "'pool' is lent to 'frame' for this block")]
    [InlineData("var outer = pool.alloc(Link { value: 0, next: none })\nregion frame = pool {\n    outer = frame.alloc(Link { value: 2, next: none })\n}\nprint(outer.value)", "refers to region 'frame', which is gone — the region ended")]
    public void WhatAResetOrABlockTookCannotBeUsed(string body, string message) =>
        Rejects(Linked, "region pool {\n" + body + "\n}", message);
}
