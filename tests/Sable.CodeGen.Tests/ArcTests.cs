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

    [Fact]
    public void WhatIsHeldWithoutACountStaysAliveAsLongAsItIsUsed() =>
        Assert.Equal(
            "3\n1\nxy\nbye a\nbye b\nbye c\nbye x\nbye y\n",
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
        var result = Pipeline.Compile(new SourceText(Program(Shapes, "    let links: Link[>..] = [Link(\"a\", none)]\n    print(\"\\(length(links[0])) \\(find(links, \"a\"))\")"), "test.sab"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var sil = SilPrinter.Print(result.Sil!);

        // 'if let next = link.next' in length: held unretained, not copied.
        Assert.Contains("alloc_stack [unmanaged] $Test.Link, var \"next\"", sil, StringComparison.Ordinal);

        // The element compared in find is read in place.
        Assert.Contains("builtin \"array_get_borrowed\"", sil, StringComparison.Ordinal);

        // An optional class reference is one word.
        Assert.Contains("icmp eq ptr", result.LlvmIr!, StringComparison.Ordinal);
    }
}
