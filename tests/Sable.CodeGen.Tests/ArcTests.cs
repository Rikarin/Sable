using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The ARC traffic the passes leave out (docs/04 §3.8): unretained
///     bindings of borrowed fields, elements read in place, loop elements
///     held without a count. Leaving them out must change nothing a program
///     sees — native and interpreted, leak-checked.
/// </summary>
public sealed class ArcTests {
    const string Declarations = """
                                class Box {
                                    var name: String
                                    var next: Box?
                                    init(_ name: String, _ next: Box?) {
                                        self.name = name
                                        self.next = next
                                    }
                                    deinit {
                                        print("bye " + name)
                                    }
                                }

                                fn length(_ box: Box) -> Int {
                                    var count = 1
                                    if let next = box.next {
                                        count = count + length(next)
                                    }
                                    return count
                                }

                                fn find(_ boxes: Box[>..], _ name: String) -> Int {
                                    var i = 0
                                    while i < boxes.count {
                                        if boxes[i].name == name {
                                            return i
                                        }
                                        i = i + 1
                                    }
                                    return -1
                                }
                                """;

    /// <summary>
    ///     The loop's copy of 'boxes' keeps x and y alive after 'boxes = []',
    ///     and goes as the loop ends: 'kept = []' is then their last reference.
    /// </summary>
    [Fact]
    public void WhatIsHeldWithoutACountStaysAliveAsLongAsItIsUsed() =>
        Assert.Equal(
            "3\n1\nxy\nbye x\nbye y\nbye a\nbye b\nbye c\n",
            AssertAgree(
                Program(
                    Declarations,
                    """
                        let list = Box("a", Box("b", Box("c", none)))
                        print("\(length(list))")
                        var boxes: Box[>..] = [Box("x", none), Box("y", none)]
                        let at = find(boxes, "y")
                        print("\(at)")
                        var kept: Box[>..] = []
                        for box in boxes {
                            boxes = []
                            kept.append(box)
                        }
                        print(kept[0].name + kept[1].name)
                        kept = []
                    """
                )
            ).Stdout
        );

    [Fact]
    public void TheCountTrafficIsGoneFromTheCommonShapes() {
        // No deinit: a release here can run nothing that writes the heap.
        const string Shapes = """
                              class Link {
                                  var name: String
                                  var next: Link?
                                  init(_ name: String, _ next: Link?) {
                                      self.name = name
                                      self.next = next
                                  }
                              }

                              fn length(_ link: Link) -> Int {
                                  var count = 1
                                  if let next = link.next {
                                      count = count + length(next)
                                  }
                                  return count
                              }

                              fn find(_ links: Link[>..], _ name: String) -> Int {
                                  var i = 0
                                  while i < links.count {
                                      if links[i].name == name {
                                          return i
                                      }
                                      i = i + 1
                                  }
                                  return -1
                              }
                              """;
        var result = Pipeline.Compile(new SourceText(Program(Shapes, "    let links: Link[>..] = [Link(\"a\", none)]\n    print(\"\\(length(links[0])) \\(find(links, \"a\"))\")"), "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var sil = SilPrinter.Print(result.Sil!);

        // 'if let next = link.next' in length: held unretained, not copied.
        Assert.Contains("alloc_stack [unmanaged] $Test.Link, var \"next\"", sil, StringComparison.Ordinal);

        // The element compared in find is read in place.
        Assert.Contains("builtin \"array_get_borrowed\"", sil, StringComparison.Ordinal);

        // An optional class reference is one word.
        Assert.Contains("icmp eq ptr", result.LlvmIr!, StringComparison.Ordinal);
    }

    /// <summary>A loop's writes, checked for sharing once before it: a copy made then, as each write would have.</summary>
    [Fact]
    public void AnArraySharedBeforeALoopIsCopiedNotWrittenThrough() {
        var source = Program(
            """
            fn zero(_ values: inout Int[>..]) {
                var i = 0
                while i < values.count {
                    values[i] = values[i] * 0 + i
                    i = i + 1
                }
            }
            """,
            """
                var a: Int[>..] = [7, 8, 9]
                let b = a
                zero(&a)
                var c = a
                var i = 0
                while i < c.count {
                    c[i] = c[i] + 10
                    i = i + 1
                }
                print("\(a[0]) \(a[1]) \(a[2]) \(b[0]) \(b[1]) \(b[2]) \(c[0]) \(c[2])")
            """
        );
        Assert.Equal("0 1 2 7 8 9 10 12\n", AssertAgree(source).Stdout);
        Assert.Contains("array_set_unique", SilPrinter.Print(Pipeline.Compile(new SourceText(source, "test.sb")).Sil!), StringComparison.Ordinal);
    }

    const string Walk = """
                        class Node {
                            var value: Int
                            var next: Node?
                            init(_ value: Int, _ next: Node?) {
                                self.value = value
                                self.next = next
                            }
                        }

                        fn total(_ list: Node?) -> Int {
                            var sum = 0
                            var p = list
                            while let n = p {
                                sum = sum + n.value
                                p = n.next
                            }
                            return sum
                        }
                        """;

    /// <summary>
    ///     A list walk — <c>while let n = p { …; p = n.next }</c> — reads
    ///     <c>n</c> only while <c>p</c> still holds its node, so <c>n</c>
    ///     borrows it: a node is retained once on the way past, for
    ///     <c>p</c>, not twice.
    /// </summary>
    [Fact]
    public void ABindingOfALocalBorrowsItWhileTheLocalIsUnchanged() {
        var source = Program(
            Walk,
            """
                var list: Node? = none
                var i = 1
                while i <= 100 {
                    list = Node(i, list)
                    i = i + 1
                }
                var sum = 0
                var k = 0
                while k < 10 {
                    sum = sum + total(list)
                    k = k + 1
                }
                print(sum)
            """
        );
        Assert.Equal("50500\n", AssertAgree(source).Stdout);

        var sil = SilPrinter.Print(Pipeline.Compile(new SourceText(source, "test.sb")).Sil!);
        Assert.Contains("alloc_stack [unmanaged] $Test.Node, var \"n\"", sil, StringComparison.Ordinal);

        if (CanBuild) {
            // Building the list retains each node but the last for the node
            // made before it; a walk retains nothing — not 'n', which borrows
            // 'p', nor 'p', which only holds nodes the list keeps.
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            Assert.Contains("retains 99,", stats, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Retains and releases are inline in the program, not calls into the
    ///     runtime that only link-time optimisation could inline: only a last
    ///     release calls it, to run the deinit and free the memory.
    /// </summary>
    [Fact]
    public void RetainAndReleaseAreInline() {
        var ir = Pipeline.Compile(new SourceText(Program(Walk, "    print(total(Node(1, Node(2, none))))"), "test.sb"), optimised: true).LlvmIr!;
        Assert.Contains("define internal void @sable.retain(ptr %object) alwaysinline", ir, StringComparison.Ordinal);
        Assert.Contains("call void @sable_release_last(ptr %object)", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("call void @sable_retain(", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("call void @sable_release(", ir, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The walking local of a read-only walk holds its nodes unretained:
    ///     each is kept by the field of the one before, back to the borrowed
    ///     parameter.
    /// </summary>
    [Fact]
    public void AReadOnlyWalkHoldsItsNodesUnretained() {
        var sil = SilPrinter.Print(Pipeline.Compile(new SourceText(Program(Walk, "    print(total(Node(1, Node(2, none))))"), "test.sb")).Sil!);
        Assert.Contains("alloc_stack [unmanaged] $Optional$Test.Node, var \"p\"", sil, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A walk that writes a field — here the one it steps along — keeps its
    ///     own count of the node it is on: the write could drop the node's
    ///     last other reference.
    /// </summary>
    [Fact]
    public void AWalkThatWritesKeepsItsNodesRetained() {
        var source = Program(
            """
            class Node {
                var value: Int
                var next: Node?
                init(_ value: Int, _ next: Node?) {
                    self.value = value
                    self.next = next
                }
                deinit {
                    print("free \(value)")
                }
            }

            fn cut(_ list: Node?) -> Int {
                var sum = 0
                var p = list
                while let n = p {
                    sum = sum + n.value
                    p = n.next
                    n.next = none
                }
                return sum
            }
            """,
            """
                let list = Node(1, Node(2, Node(3, none)))
                print(cut(list))
                print("end")
            """
        );
        Assert.Equal("free 2\nfree 3\n6\nend\nfree 1\n", AssertAgree(source).Stdout);
        Assert.DoesNotContain("[unmanaged] $Optional$Test.Node, var \"p\"", SilPrinter.Print(Pipeline.Compile(new SourceText(source, "test.sb")).Sil!), StringComparison.Ordinal);
    }

    /// <summary>A binding read after its local changes keeps its own count.</summary>
    [Fact]
    public void ABindingReadAfterItsLocalChangesKeepsItsCopy() {
        var source = Program(
            """
            class Named {
                var name: String
                init(_ name: String) {
                    self.name = name
                }
                deinit {
                    print("bye " + name)
                }
            }
            """,
            """
                var p: Named? = Named("a")
                if let n = p {
                    p = Named("b")
                    print(n.name)
                }
                print("end")
            """
        );
        Assert.Equal("a\nend\nbye a\nbye b\n", AssertAgree(source).Stdout);
        Assert.DoesNotContain("[unmanaged] $Test.Named, var \"n\"", SilPrinter.Print(Pipeline.Compile(new SourceText(source, "test.sb")).Sil!), StringComparison.Ordinal);
    }

    /// <summary>
    ///     #179's loop-wide ARC: a 'let' copying a field of a borrowed object
    ///     in a loop that changes nothing borrows it, its retain and release
    ///     paired across the turn and gone; one whose loop writes the field
    ///     keeps its copy. A dictionary's entry read and switched on, and an
    ///     entry copied out to change and stored back, are read in place and
    ///     moved.
    /// </summary>
    [Fact]
    public void ALetOfWhatALoopLeavesAloneIsBorrowedAcrossTheTurn() {
        var source = Program(
            """
            class Node {
                var value: Int
                var name: String
                init(_ value: Int, _ name: String) {
                    self.value = value
                    self.name = name
                }
            }

            fn walk(_ head: Node, _ n: Int) -> Int {
                var total = 0
                var i = 0
                while i < n {
                    let label = head.name
                    if i % 2 == 0 {
                        total = total + label.count
                    } else {
                        total = total + head.value
                    }
                    i = i + 1
                }
                return total
            }

            fn renaming(_ head: Node, _ n: Int) -> Int {
                var total = 0
                for i in 0..<n {
                    let label = head.name
                    head.name = "n\(i)"
                    total = total + label.count
                }
                return total
            }
            """,
            """
                let head = Node(3, "head\(3)")
                var counts: [String: Int] = [:]
                let keys = ["a\(1)", "b\(2)", "c\(3)"]
                var found = 0
                for round in 0..<100 {
                    for key in keys {
                        counts[key] = (counts[key] ?? 0) + round
                        found = found + (counts[key] ?? 0)
                    }
                }
                print(walk(head, 100))
                print(renaming(head, 100))
                print(head.name)
                print(found)
            """
        );
        Assert.Equal("400\n292\nn99\n499950\n", AssertAgree(source).Stdout);

        var sil = SilPrinter.Print(Pipeline.Compile(new SourceText(source, "test.sb"), optimised: true).Sil!);
        Assert.Contains("alloc_stack [unmanaged] $String, var \"label\"", sil, StringComparison.Ordinal);
        Assert.Contains("alloc_stack $String, var \"label\"", sil, StringComparison.Ordinal);
        if (CanBuild) {
            // Was 899: walk's label each turn, and the dictionary's entries
            // as they were looked up and updated; renaming's labels stay.
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            Assert.Contains("retains 502,", stats, StringComparison.Ordinal);
        }
    }
}
