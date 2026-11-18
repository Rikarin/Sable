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
}
