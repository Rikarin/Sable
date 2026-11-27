using Sable.SIL;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Regions (docs/02 §2.1, docs/04 §3.5): an arena values are bump-allocated
///     into and released with all at once when its block ends; references
///     into it are rooted in it, so root inference keeps them from outliving
///     it. Native and interpreted, leak-checked.
/// </summary>
[Native]
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

                                fn build(in r: Region, _ v: Int) -> Node& {
                                    return r.alloc(Node { value: v, label: "n\(v)" })
                                }

                                fn total(in r: Region, _ n: Int) -> Int {
                                    var sum = 0
                                    for i in 0..<n {
                                        let node = build(in: r, i)
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
                            let a = build(in: arena, 1)
                            let b = arena.alloc(Node { value: 2, label: "two" })
                            print(a.label + " " + b.label)
                            print(a.value + b.value)
                            print(total(in: arena, 100))
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
        // The program reaches 'build', or the passes drop it.
        var body = """
                       region arena {
                           print(total(in: arena, 3))
                       }
                   """;
        var result = Pipeline.Compile(new SourceText(Program(Declarations, body), "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal([0], result.Sil!.Find("Test.build")!.ResultRoots);
    }

    static void Rejects(string declarations, string body, string message) {
        var result = Pipeline.Compile(new SourceText(Program(Declarations + "\n" + declarations, body), "test.sb"));
        Assert.False(result.Succeeded, "expected the program to be rejected");
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceCannotBeReturnedOutOfItsRegion() =>
        Rejects(
            """
            fn leak() -> Node& {
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

                          fn chain(in r: region, _ n: Int) -> Link& {
                              var head = r.alloc(Link { value: 0, next: none })
                              for i in 1..<n {
                                  head = r.alloc(Link { value: i, next: head })
                              }
                              return head
                          }

                          fn sum(_ head: Link&) -> Int {
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
                            print(sum(chain(in: arena, 100)))
                            print(sum(chain(in: arena, 10)))
                        }
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("fn bad(in r: region) -> Link& {\n    var local = Link { value: 1, next: none }\n    return r.alloc(Link { value: 2, next: &local })\n}", "cannot store a reference rooted in local 'local' into region 'r', passed in")]
    [InlineData("fn bad(in r: region, in s: region) -> Link& {\n    let first = r.alloc(Link { value: 1, next: none })\n    return s.alloc(Link { value: 2, next: first })\n}", "cannot store a reference rooted in parameter 'r' into region 's', passed in")]
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
                                keep = chain(in: pool, i + 1)
                                print(keep.value)
                            }
                            region frame = pool {
                                print("frame \(sum(chain(in: frame, 5)) + keep.value + 1)")
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

    const string Containers = """
                              fn lex(_ text: String, into tokens: inout String[>..]) {
                                  for word in text.split(separator: ' ') {
                                      tokens.append(word)
                                  }
                              }

                              fn copyOut() -> Node {
                                  region arena {
                                      let n = arena.alloc(Node { value: 1, label: "x" })
                                      n.value = 5
                                      n.label = n.label + "y"
                                      return n.toOwned()
                                  }
                              }
                              """;

    [Fact]
    public void WhatARegionAllocatesIsChangedThroughItsReferenceAndCopiedOutWithToOwned() =>
        Assert.Equal(
            "2\n3\n[\"a\", \"b\", \"c\"]\nxy 5\n",
            AssertAgree(
                Program(
                    Declarations + "\n" + Containers,
                    """
                        var kept: String[>..] = []
                        region arena {
                            let numbers = arena.alloc(Int[>..]())
                            numbers.append(1)
                            numbers.append(2)
                            print(numbers.count)
                            let words = arena.alloc(String[>..]())
                            lex("a b c", into: &words)
                            print(words.count)
                            kept = words.toOwned()
                        }
                        print(kept)
                        let owned = copyOut()
                        print("\(owned.label) \(owned.value)")
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("fn bad() -> Int[>..]& {\n    region arena {\n        let numbers = arena.alloc(Int[>..]())\n        return numbers\n    }\n}", "", "cannot return a reference rooted in region 'arena'")]
    [InlineData("fn bad() -> Link {\n    region arena {\n        let a = arena.alloc(Link { value: 1, next: none })\n        return arena.alloc(Link { value: 2, next: a }).toOwned()\n    }\n}", "", "cannot return a reference rooted in region 'arena'")]
    [InlineData("", "region pool {\n    let numbers = pool.alloc(Int[>..]())\n    numbers.append(1)\n    pool.reset()\n    numbers.append(2)\n}", "the region was reset at line")]
    [InlineData("", "var outer: Int[>..] = []\nvar held = &outer\nregion arena {\n    held = arena.alloc(Int[>..]())\n}\nheld.append(1)", "which is gone — the region ended")]
    public void AContainerInARegionCannotOutliveIt(string declarations, string body, string message) =>
        Rejects(Linked + "\n" + declarations, body, message);

    // A closure, a generic result or an array carries what it holds out of
    // the region no more than a bare reference does (#312).
    const string Escapes = """
                           fn withRegion<R>(_ body: (Region) -> R) -> R {
                               region a {
                                   return body(a)
                               }
                           }

                           struct Box<T> {
                               var held: T
                           }

                           class Keeper {
                               var kept: () -> Int = { 0 }
                           }

                           fn keep(_ keeper: Keeper, _ f: @escaping () -> Int) {
                               keeper.kept = f
                           }

                           fn callNow(_ f: () -> Int) -> Int {
                               return f()
                           }
                           """;

    [Theory]
    [InlineData(GenericsMode.Specialised, "cannot return a reference rooted in region 'a' (as Test.withRegion<Node&>)")]
    [InlineData(GenericsMode.Unspecialised, "'Test.withRegion<R>' would return a reference rooted in region 'a', which dies when it returns, as its result here is a reference (Test.Node&)")]
    public void AGenericResultCannotCarryAReferenceOutOfItsRegion(GenericsMode mode, string message) {
        using var _ = SilGen.UseGenerics(mode);
        var errors = Errors(Escapes, "let n = withRegion { r in r.alloc(Node { value: 5, label: \"x\" }) }\nprint(n.value)");
        Assert.Contains(message, errors, StringComparison.Ordinal);
        Assert.Contains("r.toOwned()", errors, StringComparison.Ordinal);
    }

    static string Errors(string declarations, string body) =>
        string.Join("\n", Pipeline.Compile(new SourceText(Program(Declarations + "\n" + declarations, body), "test.sb")).Errors);

    [Theory]
    // A closure kept past the region it captured a reference into.
    [InlineData("", "var f: () -> Int = { 0 }\nregion a {\n    let n = a.alloc(Node { value: 5, label: \"x\" })\n    f = { n.value }\n}\nprint(f())", "this closure captured a reference into region 'a', which is gone — the region ended at line")]
    // One that captured the region itself.
    [InlineData("", "var f: () -> Int = { 0 }\nregion a {\n    f = { a.alloc(Node { value: 5, label: \"x\" }).value }\n}\nprint(f())", "this closure captured a reference into region 'a', which is gone — the region ended at line")]
    // Given to a function that keeps it.
    [InlineData("", "let keeper = Keeper()\nregion a {\n    let n = a.alloc(Node { value: 5, label: \"x\" })\n    keep(keeper, { n.value })\n}\nprint(keeper.kept())", "this closure captured a reference into region 'a', and 'Test.keep' keeps it past the call")]
    // Returned out of the region.
    [InlineData("fn bad() -> () -> Int {\n    region a {\n        let n = a.alloc(Node { value: 5, label: \"x\" })\n        return { n.value }\n    }\n}", "", "cannot return a reference rooted in region 'a'")]
    // A generic container of a reference.
    [InlineData("fn bad() -> Box<Node&> {\n    region a {\n        let box: Box<Node&> = Box(held: a.alloc(Node { value: 1, label: \"x\" }))\n        return box\n    }\n}", "", "cannot return a reference rooted in region 'a'")]
    // An array of references.
    [InlineData("fn bad() -> Node&[>..] {\n    region a {\n        var list: Node&[>..] = []\n        list.append(a.alloc(Node { value: 1, label: \"x\" }))\n        return list\n    }\n}", "", "cannot return a reference rooted in region 'a'")]
    [InlineData("fn bad() -> Node& {\n    region a {\n        var list: Node&[>..] = []\n        list.append(a.alloc(Node { value: 1, label: \"x\" }))\n        return list[0]\n    }\n}", "", "cannot return a reference rooted in region 'a'")]
    public void AClosureAGenericOrAnArrayCannotCarryAReferenceOutOfItsRegion(string declarations, string body, string message) =>
        Rejects(Escapes + "\n" + declarations, body, message);

    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void WhatStaysInTheRegionOrIsCopiedOutIsAllowed(GenericsMode mode) {
        using var _ = SilGen.UseGenerics(mode);
        Assert.Equal(
            "5\n6 y\n8\n3\n",
            AssertAgree(
                Program(
                    Declarations + "\n" + Escapes,
                    """
                        print(withRegion { r in r.alloc(Node { value: 5, label: "x" }).value })
                        let owned = withRegion { r in r.alloc(Node { value: 6, label: "y" }).toOwned() }
                        print("\(owned.value) \(owned.label)")
                        region a {
                            let n = a.alloc(Node { value: 5, label: "x" })
                            let g = { n.value + 1 }
                            let h = { a.alloc(Node { value: 2, label: "y" }).value }
                            print(g() + h() + callNow({ n.value }) - 5)
                            var list: Node&[>..] = []
                            list.append(n)
                            list.append(a.alloc(Node { value: -2, label: "z" }))
                            print(list[0].value + list[1].value)
                        }
                    """
                )
            ).Stdout
        );
    }

    // With SABLE_CHECK_REGIONS (runtime/references.c): what a reset gives
    // back is poisoned, and a value's guard bytes are checked as it goes.
    // Native only: the interpreter's region has no bytes to poison, and
    // traps on any use of what a reset gave back.
    const string RawCell = """
                           struct Cell {
                               var value: Int
                           }

                           """;

    static readonly Dictionary<string, string> CheckingRegions = new() { ["SABLE_CHECK_REGIONS"] = "1" };

    [Theory]
    [InlineData(false, "0\n")]
    [InlineData(true, "222\n")]
    public void WhatAResetGivesBackIsPoisonedWhenRegionsAreChecked(bool checking, string expected) {
        if (!CanBuild) {
            return;
        }

        var run = BuildAndRun(
            Program(
                RawCell,
                """
                    region pool {
                        let cell = pool.alloc(Cell { value: 42 })
                        var address = 0
                        Memory.withPointer(to: &cell.value) { address = $0.address }
                        pool.reset()
                        unsafe {
                            print(RawPtr<U8>(address: address).load())
                        }
                    }
                """
            ),
            environment: checking ? CheckingRegions : null
        );
        Assert.Equal(expected, run.Stdout);
        Assert.Equal(0, run.ExitCode);
    }

    [Theory]
    [InlineData("pool.reset()")]
    [InlineData("")]
    public void AWritePastAValueInACheckedRegionIsCaught(string ending) {
        if (!CanBuild) {
            return;
        }

        var body = $$"""
                         region pool {
                             let cell = pool.alloc(Cell { value: 1 })
                             var address = 0
                        Memory.withPointer(to: &cell.value) { address = $0.address }
                             unsafe {
                                 RawPtr<U8>(address: address + 8).store(7)
                             }
                             {{ending}}
                             print("written")
                         }
                         print("after")
                     """;
        var plain = BuildAndRun(Program(RawCell, body));
        Assert.Equal("written\nafter\n", plain.Stdout);
        var run = BuildAndRun(Program(RawCell, body), environment: CheckingRegions);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("a write ran past a value of 8 bytes in a region: the guard bytes after it were overwritten", run.Stderr);
        Assert.DoesNotContain("after", run.Stdout);
    }
}
