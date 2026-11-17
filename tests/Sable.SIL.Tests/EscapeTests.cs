namespace Sable.SIL.Tests;

/// <summary>
///     docs/04 §3.7: an object that does not outlive its frame is made in
///     it. Escape is decided by uses, and across calls by each function's
///     summary of which parameters escape, computed for the whole module.
/// </summary>
public sealed class EscapeTests {
    static SilModule Optimised(string source) {
        var (module, diagnostics) = SilGen.Lower(Compilation.Create($"module M\n{source}"));
        Assert.Empty(diagnostics);
        SilPassManager.Run(module, SilPassManager.Default);
        return module;
    }

    static IEnumerable<AllocRefInst> Allocations(SilModule module, string function) =>
        module.Find(function)!.Blocks.SelectMany(static b => b.Instructions.OfType<AllocRefInst>());

    const string Counter = """
                           class Counter {
                               var count: Int
                               init(_ count: Int) {
                                   self.count = count
                               }
                           }

                           fn read(_ c: Counter) -> Int {
                               return c.count
                           }

                           fn keep(_ c: Counter) -> Counter {
                               return c
                           }
                           """;

    [Fact]
    public void AnObjectUsedOnlyHereIsMadeOnTheStack() {
        var module = Optimised(
            Counter + "\n" + """
                      fn local() -> Int {
                          let c = Counter(41)
                          c.count = c.count + 1
                          return read(c)
                      }
                      """
        );

        // The allocating initialiser is made where it is called; the allocation, kept in the frame.
        Assert.True(Assert.Single(Allocations(module, "M.local")).OnStack);
        Assert.Equal(42L, new SilInterpreter(module).Run("M.local"));
    }

    [Fact]
    public void WhatEscapesStaysOnTheHeap() {
        var module = Optimised(
            Counter + "\n" + """
                      fn returned() -> Counter {
                          let c = Counter(1)
                          return c
                      }

                      fn passedOn() -> Int {
                          let c = Counter(2)
                          return keep(c).count
                      }

                      fn inALoop() -> Int {
                          var total = 0
                          for i in 0..<3 {
                              let c = Counter(i)
                              total = total + read(c)
                          }
                          return total
                      }
                      """
        );

        foreach (var function in new[] { "M.returned", "M.passedOn", "M.inALoop" }) {
            Assert.False(Assert.Single(Allocations(module, function)).OnStack);
        }
    }

    [Fact]
    public void AnInlineValueBuiltAndReadHereIsMadeOnTheStack() {
        var module = Optimised(
            """
            @inline_layout
            struct Line { var sku: U8[..8], var qty: U16 }

            @inline_layout
            struct Order { var id: U64, var lines: Line[>..] }

            fn count(_ order: Order) -> Int {
                return order.lines.count
            }

            fn build() -> Int {
                var order = Order { id: 1, lines: [] }
                order.lines.append(Line { sku: "a", qty: 1 })
                return count(order)
            }

            fn escaping() -> Order {
                return Order { id: 2, lines: [] }
            }
            """
        );

        static IEnumerable<string> Made(SilModule module, string function) =>
            module.Find(function)!.Blocks.SelectMany(static b => b.Instructions.OfType<BuiltinInst>())
                .Select(static b => b.Name).Where(static n => n.StartsWith("inline_new", StringComparison.Ordinal));

        Assert.Equal(["inline_new_stack", "inline_new_stack"], Made(module, "M.build"));
        Assert.Equal(["inline_new"], Made(module, "M.escaping"));
        Assert.Equal(1L, new SilInterpreter(module).Run("M.build"));
    }

    /// <summary>The SIL text of inline layout and promotion reads back as it was written.</summary>
    [Fact]
    public void InlineAndStackSilReadsBackAsWritten() {
        var (module, diagnostics) = SilGen.Lower(Compilation.Create(
            """
            module M
            @inline_layout
            struct Node { var value: I32, var parent: Node&<u16>?, var tag: U8[..4] }

            @inline_layout
            struct Tree { var nodes: Node[>..], var weight: F32 }

            class Counter {
                var count: U16
                init(_ count: U16) { self.count = count }
            }

            fn build() -> Int {
                var tree = Tree { nodes: [], weight: 1.5 }
                tree.nodes.append(Node { value: -1, parent: none, tag: "a" })
                let c = Counter(3)
                var total = Int(c.count)
                for node in tree.nodes {
                    total = total + Int(node.value) + node.tag.count
                }
                return total
            }
            """
        ));
        Assert.Empty(diagnostics);
        SilPassManager.Run(module, SilPassManager.Default);

        var text = SilPrinter.Print(module);
        Assert.Contains("alloc_ref [stack]", text, StringComparison.Ordinal);
        Assert.Contains("@inline", text, StringComparison.Ordinal);
        Assert.Contains("$@relative_optional(M.Node, 2)", text, StringComparison.Ordinal);
        var reparsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(reparsed));
        Assert.Empty(SilVerifier.Verify(reparsed));
        Assert.Equal(3L, new SilInterpreter(reparsed).Run("M.build"));
    }
}
