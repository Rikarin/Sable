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

    // What a closure, a generic passed on, an array in a part of a variable
    // or a reset leaves a closure holding is followed too (#312).
    const string Routes = """
                          fn passOn<T>(_ body: (Region) -> T) -> T {
                              let x = withRegion(body)
                              return x
                          }

                          class Holder2 {
                              var kept: () -> Int = { 0 }
                              fn keep(_ f: @escaping () -> Int) {
                                  kept = f
                              }
                          }

                          protocol Taker {
                              fn take(_ f: @escaping () -> Int)
                          }

                          class KeepingTaker: Taker {
                              var kept: () -> Int = { 0 }
                              fn take(_ f: @escaping () -> Int) {
                                  kept = f
                              }
                          }

                          protocol Peeker {
                              fn peek(_ f: () -> Int) -> Int
                          }

                          class Looker: Peeker {
                              fn peek(_ f: () -> Int) -> Int {
                                  return f() + 1
                              }
                          }

                          class Caller {
                              var kept: () -> Int = { 0 }

                              fn call(_ f: () -> Int) -> Int {
                                  return f()
                              }

                              fn hold(_ f: @escaping () -> Int) {
                                  kept = f
                              }
                          }

                          class LoudCaller: Caller {
                              override fn call(_ f: () -> Int) -> Int {
                                  return f() * 2
                              }

                              override fn hold(_ f: @escaping () -> Int) {
                                  kept = { f() * 2 }
                              }
                          }

                          struct Bag {
                              var items: Node&[>..]
                          }

                          struct Calls {
                              var fs: (() -> Int)[>..]
                          }

                          fn put(_ list: inout Node&[>..], _ n: Node&) {
                              list.append(n)
                          }

                          fn first(_ fs: inout (() -> Int)[>..]) -> () -> Int {
                              return fs[0]
                          }

                          fn keepFirst(_ keeper: Keeper, _ fs: inout (() -> Int)[>..]) {
                              keeper.kept = fs[0]
                          }

                          class Hold3 {
                              var f: () -> Int = { 0 }
                              var fs: (() -> Int)[>..] = []
                          }

                          fn putClosure(_ slot: inout () -> Int, _ c: @escaping () -> Int) {
                              slot = c
                          }

                          fn addClosure(_ list: inout (() -> Int)[>..], _ c: @escaping () -> Int) {
                              list.append(c)
                          }

                          fn boxRegion<T>(_ body: (Region) -> T) -> Box<T> {
                              region a {
                                  return Box(held: body(a))
                              }
                          }

                          fn tryRegion<R>(_ body: (Region) throws -> R) throws -> R {
                              region a {
                                  return try body(a)
                              }
                          }

                          fn wrap(_ f: inout () -> Int) -> () -> Int {
                              return { f() + 1 }
                          }

                          class MaybeHolder {
                              var kept: (() -> Int)? = none
                              fn hold(_ f: (() -> Int)?) {
                                  kept = f
                              }
                          }

                          fn holdMaybe(_ h: MaybeHolder, _ f: (() -> Int)?) {
                              h.kept = f
                          }

                          fn peekWith<T: Peeker>(_ t: T, _ f: () -> Int) -> Int {
                              return t.peek(f)
                          }
                          """;

    const string InRegion = "region a {\n    let n = a.alloc(Node { value: 5, label: \"x\" })\n";

    [Theory]
    // A generic caller compiled once passes on the metadata it is given.
    [InlineData(GenericsMode.Specialised, "let n = passOn { r in r.alloc(Node { value: 5, label: \"x\" }) }\nprint(n.value)", "cannot return a reference rooted in region 'a' (as Test.withRegion<Node&>)")]
    [InlineData(GenericsMode.Unspecialised, "let n = passOn { r in r.alloc(Node { value: 5, label: \"x\" }) }\nprint(n.value)", "'Test.passOn<T>' would return a reference rooted in region 'a' (in 'Test.withRegion<R>')")]
    // A closure put in an object's field or array, there or through an address lent to a call.
    [InlineData(GenericsMode.Specialised, "let o = Hold3()\n" + InRegion + "    o.fs.append({ n.value })\n}\nprint(o.fs[0]())", "cannot store a reference rooted in region 'a' into an object's field")]
    [InlineData(GenericsMode.Unspecialised, "let o = Hold3()\n" + InRegion + "    o.fs.append({ n.value })\n}\nprint(o.fs[0]())", "cannot store a reference rooted in region 'a' into an object's field")]
    [InlineData(GenericsMode.Specialised, "let o = Hold3()\n" + InRegion + "    addClosure(&o.fs, { n.value })\n}\nprint(o.fs[0]())", "and 'Test.addClosure' keeps it past the call")]
    [InlineData(GenericsMode.Specialised, "let o = Hold3()\n" + InRegion + "    putClosure(&o.f, { n.value })\n}\nprint(o.f())", "and 'Test.putClosure' keeps it past the call")]
    // Closures read from an array in a region's memory are rooted in the region.
    [InlineData(GenericsMode.Specialised, "var outside: () -> Int = { 0 }\n" + InRegion + "    let held = a.alloc(Calls(fs: []))\n    held.fs.append({ n.value })\n    outside = held.fs[0]\n}\nprint(outside())", "this closure captured a reference into region 'a', which is gone — the region ended")]
    // A 'var' closures share holds what is put in it: the closures that captured it hold that.
    [InlineData(GenericsMode.Specialised, "var reader: () -> Int = { 0 }\n" + InRegion + "    var cur = n\n    reader = { cur.value }\n}\nprint(reader())", "this closure captured a reference into region 'a', which is gone — the region ended")]
    [InlineData(GenericsMode.Unspecialised, "var reader: () -> Int = { 0 }\n" + InRegion + "    var cur = n\n    reader = { cur.value }\n}\nprint(reader())", "this closure captured a reference into region 'a', which is gone — the region ended")]
    [InlineData(GenericsMode.Specialised, "let keeper = Keeper()\n" + InRegion + "    var cur = n\n    keeper.kept = { cur.value }\n}\nprint(keeper.kept())", "cannot store a reference rooted in region 'a' into an object's field")]
    // A closure variable given another closure on one path only, after the region ended, may still hold the first.
    [InlineData(GenericsMode.Specialised, "var f: () -> Int = { 0 }\nvar flag = false\n" + InRegion + "    f = { n.value }\n}\nif flag {\n    f = { 2 }\n}\nprint(f())", "this closure captured a reference into region 'a', which is gone — the region ended")]
    [InlineData(GenericsMode.Specialised, "var f: () -> Int = { 0 }\nfor i in 0..<2 {\n    print(f())\n    region a {\n        let n = a.alloc(Node { value: i, label: \"x\" })\n        f = { n.value }\n    }\n}", "this closure captured a reference into region 'a', which is gone — the region ended")]
    // A generic result compiled once, in a generic struct's field or given by a call that may throw.
    [InlineData(GenericsMode.Specialised, "let b = boxRegion { r in r.alloc(Node { value: 5, label: \"x\" }) }\nprint(b.held.value)", "cannot return a reference rooted in region 'a' (as Test.boxRegion<Node&>)")]
    [InlineData(GenericsMode.Unspecialised, "let b = boxRegion { r in r.alloc(Node { value: 5, label: \"x\" }) }\nprint(b.held.value)", "'Test.boxRegion<T>' would return a reference rooted in region 'a'")]
    [InlineData(GenericsMode.Specialised, "do {\n    let n = try tryRegion { r in r.alloc(Node { value: 5, label: \"x\" }) }\n    print(n.value)\n} catch {\n}", "cannot return a reference rooted in region 'a' (as Test.tryRegion<Node&>)")]
    [InlineData(GenericsMode.Unspecialised, "do {\n    let n = try tryRegion { r in r.alloc(Node { value: 5, label: \"x\" }) }\n    print(n.value)\n} catch {\n}", "'Test.tryRegion<R>' would return a reference rooted in region 'a'")]
    // A closure given to a closure value, a protocol requirement or a method an override may change, whose type says '@escaping', may be kept.
    [InlineData(GenericsMode.Specialised, "let h = Holder2()\nlet k: (@escaping () -> Int) -> () = h.keep\n" + InRegion + "    k({ n.value })\n}\nprint(h.kept())", "and the closure called here may keep it past the call (its type's parameter is '@escaping')")]
    [InlineData(GenericsMode.Unspecialised, "let h = Holder2()\nlet k: (@escaping () -> Int) -> () = h.keep\n" + InRegion + "    k({ n.value })\n}\nprint(h.kept())", "and the closure called here may keep it past the call (its type's parameter is '@escaping')")]
    [InlineData(GenericsMode.Specialised, "let t = KeepingTaker()\nlet taker: any Taker = t\n" + InRegion + "    taker.take({ n.value })\n}\nprint(t.kept())", "and the protocol requirement called here may keep it past the call")]
    [InlineData(GenericsMode.Specialised, "let c: Caller = LoudCaller()\n" + InRegion + "    c.hold({ n.value })\n}\nprint(c.kept())", "may keep it past the call (its parameter is '@escaping')")]
    [InlineData(GenericsMode.Unspecialised, "let c: Caller = LoudCaller()\n" + InRegion + "    c.hold({ n.value })\n}\nprint(c.kept())", "may keep it past the call (its parameter is '@escaping')")]
    // An optional closure, which may always be kept: by a function of the module or a closure value.
    [InlineData(GenericsMode.Specialised, "let h = MaybeHolder()\n" + InRegion + "    holdMaybe(h, { n.value })\n}\nprint(h.kept!())", "and 'Test.holdMaybe' keeps it past the call")]
    [InlineData(GenericsMode.Unspecialised, "let h = MaybeHolder()\n" + InRegion + "    holdMaybe(h, { n.value })\n}\nprint(h.kept!())", "and 'Test.holdMaybe' keeps it past the call")]
    [InlineData(GenericsMode.Specialised, "let h = MaybeHolder()\nlet k: ((() -> Int)?) -> () = h.hold\n" + InRegion + "    k({ n.value })\n}\nprint(h.kept!())", "and the closure called here may keep it past the call")]
    // A closure whose body keeps what it captured, a variable it shares or a value, in an object.
    [InlineData(GenericsMode.Specialised, "let keeper = Keeper()\n" + InRegion + "    var f: () -> Int = { n.value }\n    f = { n.value + 1 }\n    let g = { keeper.kept = f }\n    g()\n}\nprint(keeper.kept())", "and its body keeps what it captured past its call")]
    [InlineData(GenericsMode.Unspecialised, "let keeper = Keeper()\n" + InRegion + "    var f: () -> Int = { n.value }\n    f = { n.value + 1 }\n    let g = { keeper.kept = f }\n    g()\n}\nprint(keeper.kept())", "and its body keeps what it captured past its call")]
    [InlineData(GenericsMode.Specialised, "let keeper = Keeper()\n" + InRegion + "    let f: () -> Int = { n.value }\n    let g = { keeper.kept = f }\n    g()\n}\nprint(keeper.kept())", "and its body keeps what it captured past its call")]
    [InlineData(GenericsMode.Specialised, "let keeper = Keeper()\n" + InRegion + "    let f: () -> Int = { n.value }\n    let g = { { keeper.kept = f }() }\n    g()\n}\nprint(keeper.kept())", "and its body keeps what it captured past its call")]
    // An array in a field of a variable, appended to there or through 'inout'.
    [InlineData(GenericsMode.Specialised, "var s = Bag(items: [])\n" + InRegion + "    s.items.append(n)\n}\nprint(s.items[0].value)", "this reference refers to region 'a', which is gone — the region ended")]
    [InlineData(GenericsMode.Unspecialised, "var s = Bag(items: [])\n" + InRegion + "    s.items.append(n)\n}\nprint(s.items[0].value)", "this reference refers to region 'a', which is gone — the region ended")]
    [InlineData(GenericsMode.Specialised, "var s = Bag(items: [])\n" + InRegion + "    put(&s.items, n)\n}\nprint(s.items[0].value)", "this reference refers to region 'a', which is gone — the region ended")]
    [InlineData(GenericsMode.Specialised, "var c = Calls(fs: [])\n" + InRegion + "    c.fs.append({ n.value })\n}\nprint(c.fs[0]())", "this closure captured a reference into region 'a', which is gone — the region ended")]
    // Closures in a variable given by address: given back, or kept.
    [InlineData(GenericsMode.Specialised, "var fs: (() -> Int)[>..] = []\nvar g: () -> Int = { 0 }\n" + InRegion + "    fs.append({ n.value })\n    g = first(&fs)\n}\nprint(g())", "this closure captured a reference into region 'a', which is gone — the region ended")]
    [InlineData(GenericsMode.Specialised, "let keeper = Keeper()\nvar fs: (() -> Int)[>..] = []\n" + InRegion + "    fs.append({ n.value })\n    keepFirst(keeper, &fs)\n}\nprint(keeper.kept())", "and 'Test.keepFirst' keeps it past the call")]
    // A closure made of an 'inout' closure parameter, by a function or a closure.
    [InlineData(GenericsMode.Specialised, "var h: () -> Int = { 0 }\nvar c: () -> Int = { 0 }\n" + InRegion + "    c = { n.value }\n    h = wrap(&c)\n}\nc = { 2 }\nprint(h())", "this closure captured a reference into region 'a', which is gone — the region ended")]
    [InlineData(GenericsMode.Unspecialised, "var h: () -> Int = { 0 }\nvar c: () -> Int = { 0 }\n" + InRegion + "    c = { n.value }\n    h = wrap(&c)\n}\nc = { 2 }\nprint(h())", "this closure captured a reference into region 'a', which is gone — the region ended")]
    [InlineData(GenericsMode.Specialised, "let g: (inout () -> Int) -> () -> Int = { (f: inout () -> Int) in return { f() + 1 } }\nvar h: () -> Int = { 0 }\nvar c: () -> Int = { 0 }\n" + InRegion + "    c = { n.value }\n    h = g(&c)\n}\nc = { 2 }\nprint(h())", "this closure captured a reference into region 'a', which is gone — the region ended")]
    // A closure that took a reference before a reset.
    [InlineData(GenericsMode.Specialised, "region pool {\n    let n = pool.alloc(Node { value: 1, label: \"x\" })\n    let f = { n.value }\n    pool.reset()\n    print(f())\n}", "this closure captured a reference into region 'pool', which is gone — the region was reset at line")]
    [InlineData(GenericsMode.Unspecialised, "region pool {\n    let n = pool.alloc(Node { value: 1, label: \"x\" })\n    let f = { n.value }\n    pool.reset()\n    print(f())\n}", "this closure captured a reference into region 'pool', which is gone — the region was reset at line")]
    public void NoRouteCarriesAReferenceOutOfItsRegion(GenericsMode mode, string body, string message) {
        using var _ = SilGen.UseGenerics(mode);
        Assert.Contains(message, Errors(Escapes + "\n" + Routes, body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void WhatTheRoutesUseInsideTheRegionIsAllowed(GenericsMode mode) {
        using var _ = SilGen.UseGenerics(mode);
        Assert.Equal(
            "5\n6\n3\n4\n7\n1 5\n2\n6\n7\n5 6 10\n15\n6\n6 6\n4\n5\n12\n9\n11\n11\n0 2\n1 2\n",
            AssertAgree(
                Program(
                    Declarations + "\n" + Escapes + "\n" + Routes,
                    """
                        print(passOn { r in r.alloc(Node { value: 5, label: "x" }).value })
                        print(passOn { r in r.alloc(Node { value: 6, label: "y" }).toOwned() }.value)
                        print(boxRegion { r in r.alloc(Node { value: 3, label: "z" }).value }.held)
                        do {
                            print(try tryRegion { r in r.alloc(Node { value: 4, label: "w" }).toOwned() }.value)
                        } catch {
                        }
                        let h = Holder2()
                        let k: (@escaping () -> Int) -> () = h.keep
                        let t = KeepingTaker()
                        let taker: any Taker = t
                        let peek: (() -> Int) -> Int = { f in f() }
                        let peeker: any Peeker = Looker()
                        let caller: Caller = LoudCaller()
                        region a {
                            let n = a.alloc(Node { value: 5, label: "x" })
                            let seven = 7
                            k({ seven })
                            print(h.kept())
                            var s = Bag(items: [])
                            s.items.append(n)
                            put(&s.items, n)
                            taker.take({ 1 })
                            print("\(t.kept()) \(s.items[1].value)")
                            var fs: (() -> Int)[>..] = []
                            fs.append({ n.value - 3 })
                            print(first(&fs)())
                            var c: () -> Int = { n.value }
                            print(wrap(&c)() - 0)
                            var calls = Calls(fs: [])
                            calls.fs.append({ n.value + 2 })
                            print(calls.fs[0]())
                            print("\(peek({ n.value })) \(peeker.peek({ n.value })) \(caller.call({ n.value }))")
                            let held = a.alloc(Calls(fs: []))
                            held.fs.append({ n.value * 3 })
                            print(held.fs[0]())
                            var cur = n
                            let bump = { cur = a.alloc(Node { value: cur.value + 1, label: "b" }) }
                            bump()
                            print(callNow({ cur.value }))
                            print("\(peekWith(peeker, { n.value })) \(peekWith(Looker(), { n.value }))")
                        }
                        var later: () -> Int = { 0 }
                        for i in 0..<2 {
                            region c {
                                let n = c.alloc(Node { value: 4 + i, label: "c" })
                                later = { n.value }
                                print(later())
                            }
                            later = { 12 }
                        }
                        print(later())
                        let keeper = Keeper()
                        var heap: (() -> Int)[>..] = [{ 9 }]
                        keepFirst(keeper, &heap)
                        print(keeper.kept())
                        let heapOnly: () -> Int = { 11 }
                        let keepIt = { keeper.kept = heapOnly }
                        keepIt()
                        print(keeper.kept())
                        var shared: () -> Int = { 0 }
                        let setIt = { shared = heapOnly }
                        setIt()
                        print(shared())
                        region pool {
                            let mk = { pool.alloc(Node { value: 2, label: "y" }).value }
                            var f = { 0 }
                            for i in 0..<2 {
                                pool.reset()
                                let n = pool.alloc(Node { value: i, label: "x" })
                                f = { n.value }
                                print("\(f()) \(mk())")
                            }
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
