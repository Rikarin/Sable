namespace Sable.SIL.Tests;

/// <summary>
///     docs/04 §3.7: an object made in the frame that nothing counts, and
///     nothing reads the header of — no copy, no call through its vtable, no
///     callee that does either — is made with no header: its fields alone,
///     or, handed to a callee, its usual layout with the header never written.
/// </summary>
public sealed class StackHeadersTests {
    static SilModule Lowered(string source, IReadOnlyList<ISilPass> passes) {
        var (module, diagnostics) = SilGen.Lower(Compilation.Create($"module M\n{source}"));
        Assert.Empty(diagnostics);
        SilPassManager.Run(module, passes);
        return module;
    }

    static HeaderlessObjects Headerless(SilModule module, string function) =>
        StackHeaders.Find(module.Find(function)!, () => StackHeaders.Summarise(module));

    static AllocRefInst Allocation(SilModule module, string function) =>
        Assert.Single(module.Find(function)!.Blocks.SelectMany(static b => b.Instructions.OfType<AllocRefInst>()));

    const string Point = """
                         class Point {
                             var x: Int
                             var name: String
                             init(_ x: Int, _ name: String) {
                                 self.x = x
                                 self.name = name
                             }
                         }

                         fn read(_ p: Point) -> Int {
                             return p.x + p.name.count
                         }

                         fn keep(_ p: Point) -> Point {
                             return p
                         }
                         """;

    [Fact]
    public void ACalleeThatOnlyReachesTheFieldsReadsNoHeader() {
        var module = Lowered(Point, SilPassManager.Default);
        var summaries = StackHeaders.Summarise(module);
        Assert.Equal([false], summaries["M.read"]);
        Assert.Equal([true], summaries["M.keep"]);
        // Its initialiser only stores to its fields.
        Assert.False(summaries["M.Point.init$initialize"][0]);
    }

    [Fact]
    public void AnObjectHandedToCalleesThatOnlyReachItsFieldsKeepsItsLayoutWithNoHeader() {
        var module = Lowered(
            Point + "\n" + """
                    fn local(_ i: Int) -> Int {
                        let p = Point(i, "p")
                        return read(p)
                    }
                    """,
            SilPassManager.Default
        );

        var alloc = Allocation(module, "M.local");
        Assert.True(alloc.OnStack);
        var headerless = Headerless(module, "M.local");
        // Its initialiser and 'read' are handed it: its usual layout.
        Assert.True(headerless.Objects[alloc]);
        Assert.NotEmpty(headerless.Destroys);
        Assert.Equal(3L, new SilInterpreter(module).Run("M.local", 2L));
    }

    [Fact]
    public void AnObjectNoCalleeIsHandedIsItsFieldsAlone() {
        var module = Lowered(
            Point + "\n" + """
                    fn local(_ n: Int) -> Int {
                        var total = 0
                        for i in 0..<n {
                            let p = Point(i, "p")
                            total = total + read(p)
                        }
                        return total
                    }
                    """,
            SilPassManager.Optimising
        );

        var alloc = Allocation(module, "M.local");
        Assert.False(Headerless(module, "M.local").Objects[alloc]);
        Assert.Equal(36L, new SilInterpreter(module).Run("M.local", 8L));
    }

    /// <summary>A call through the vtable, which finds it in the header's metadata; a store over an object still held, whose release is counted.</summary>
    [Theory]
    [InlineData("""
                class Named: Point {
                    override fn describe() -> String { return "named" }
                }

                fn local() -> Int {
                    let p = Point(1, "p")
                    return p.describe().count
                }
                """)]
    [InlineData("""
                fn local() -> Int {
                    var p = Point(1, "p")
                    p = Point(2, "q")
                    return read(p)
                }
                """)]
    public void WhatReadsTheHeaderKeepsIt(string body) {
        var module = Lowered(
            Point.Replace("init(_ x: Int, _ name: String) {", "fn describe() -> String { return \"point\" }\n    init(_ x: Int, _ name: String) {", StringComparison.Ordinal)
                .Replace("class Point {", "open class Point {", StringComparison.Ordinal)
            + "\n" + body,
            SilPassManager.Default
        );

        foreach (var alloc in module.Find("M.local")!.Blocks.SelectMany(static b => b.Instructions.OfType<AllocRefInst>())) {
            Assert.False(Headerless(module, "M.local").Objects.ContainsKey(alloc));
        }
    }

    /// <summary>A deinit is handed its object: one that only reads a field reads no header; one that hands it on may.</summary>
    [Theory]
    [InlineData("print(x)", true)]
    [InlineData("_ = keep(self)", false)]
    public void ADeinitIsACalleeOfItsObject(string deinit, bool headerless) {
        var module = Lowered(
            $$"""
              class Noisy {
                  var x: Int
                  init(_ x: Int) {
                      self.x = x
                  }
                  deinit {
                      {{deinit}}
                  }
              }

              fn keep(_ n: Noisy) -> Noisy {
                  return n
              }

              fn local() -> Int {
                  let n = Noisy(1)
                  return n.x
              }
              """,
            SilPassManager.Default
        );

        var alloc = Allocation(module, "M.local");
        Assert.Equal(headerless, Headerless(module, "M.local").Objects.ContainsKey(alloc));
    }

    [Theory]
    [InlineData(8, 16)]
    [InlineData(4, 16)]
    public void TheBytesLeftOutAreTheHeadersAsFarAsTheHeapsAlignment(int pointerBytes, int dropped) {
        var saved = TypeLayout.PointerBytes;
        try {
            TypeLayout.PointerBytes = pointerBytes;
            var wide = new ClassType($"M.Wide{pointerBytes}") { Fields = { ("x", SilType.Int) } };
            Assert.Equal(dropped, TypeLayout.HeaderlessDropped(wide));

            // A 12-byte header and a 4-byte field after it: nothing to leave out at wasm32's width.
            var narrow = new ClassType($"M.Narrow{pointerBytes}") { Fields = { ("x", SilType.I32) } };
            Assert.Equal(pointerBytes == 8 ? 16 : 0, TypeLayout.HeaderlessDropped(narrow));
        } finally {
            TypeLayout.PointerBytes = saved;
        }
    }
}
