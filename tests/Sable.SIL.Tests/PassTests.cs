namespace Sable.SIL.Tests;

/// <summary>Slot promotion, and the pass manager's promise to verify after every pass.</summary>
public sealed class PassTests {
    static SilModule Lowered(string source) {
        var (module, diagnostics) = SilGen.Lower(Compilation.Create($"module M\n{source}"));
        Assert.Empty(diagnostics);
        return module;
    }

    static int Count<T>(SilModule module) => module.Functions.Sum(static f => f.Blocks.Sum(static b => b.Instructions.OfType<T>().Count()));

    [Fact]
    public void ALoopsLocalsBecomeBlockArguments() {
        var module = Lowered(
            """
            fn gcd(_ a: Int, _ b: Int) -> Int {
                var x = a
                var y = b
                while y != 0 {
                    let t = x % y
                    x = y
                    y = t
                }
                return x
            }
            """
        );
        Assert.True(Count<AllocStackInst>(module) > 0);

        SilPassManager.Run(module, SilPassManager.Default);

        Assert.Equal(0, Count<AllocStackInst>(module));
        Assert.Equal(0, Count<LoadInst>(module));
        Assert.Equal(0, Count<StoreInst>(module));
        Assert.Equal(21L, new SilInterpreter(module).Run("M.gcd", 1071L, 462L));
    }

    [Fact]
    public void AStructBuiltFieldByFieldStaysInMemory() {
        // The initialiser's 'self' is written through struct_element_addr,
        // so its address escapes into other instructions: not promotable.
        var module = Lowered(
            """
            struct Pair {
                var a: Int
                var b: Int
                init(a: Int) {
                    self.a = a
                    self.b = a * 2
                }
            }
            fn make() -> Int { return Pair(a: 4).b }
            """
        );

        SilPassManager.Run(module, SilPassManager.Default);

        var init = module.Find("M.Pair.init")!;
        Assert.Single(init.Blocks.SelectMany(static b => b.Instructions).OfType<AllocStackInst>());
        Assert.Equal(8L, new SilInterpreter(module).Run("M.make"));
    }

    [Fact]
    public void ASlotThatCouldBeReadUnsetIsLeftAlone() {
        // Stored on one path only, then read after the join: there is no value
        // to give the other path, so the slot stays.
        var module = SilParser.Parse(
            """
            sil @f : $(Bool) -> Int {
            bb0(%0 : $Bool):
              %1 = alloc_stack $Int
              cond_br %0, bb1, bb2
            bb1:
              %2 = integer_literal $Int, 7
              store %2 to [init] %1
              br bb2
            bb2:
              %3 = load [trivial] %1
              dealloc_stack %1
              return %3
            }
            """
        );

        SilPassManager.Run(module, SilPassManager.Default);
        Assert.Equal(1, Count<AllocStackInst>(module));
    }

    /// <summary>
    ///     An array read whose index is chosen by '?:' is borrowed across the
    ///     choice, not copied — unless something on the way can write it, as
    ///     a call given the array inout can.
    /// </summary>
    [Fact]
    public void AnElementReadAcrossAChoiceBorrowsTheArray() {
        var module = Lowered(
            """
            fn grow(_ values: inout Int[>..]) -> Int {
                values.append(40)
                return 0
            }
            fn pick(_ c: Bool, _ i: Int, _ j: Int) -> Int {
                var values: Int[>..] = [10, 20, 30]
                values[0] = 5
                return values[c ? i : j]
            }
            fn picked(_ c: Bool, _ j: Int) -> Int {
                var values: Int[>..] = [10, 20, 30]
                values[0] = 5
                let old = values
                return values[c ? grow(&values) : j] + old.count
            }
            """
        );

        SilPassManager.Run(module, SilPassManager.Default);

        static int Copies(SilFunction function) =>
            function.Blocks.Sum(static b => b.Instructions.Count(static i => i is LoadInst { Kind: LoadKind.Copy } or CopyValueInst));

        var pick = module.Find("M.pick")!;
        Assert.Equal(0, Copies(pick));
        Assert.NotEmpty(pick.Blocks.SelectMany(static b => b.Instructions).OfType<LoadBorrowInst>());
        Assert.Equal(5L, new SilInterpreter(module).Run("M.pick", true, 0L, 2L));
        Assert.Equal(30L, new SilInterpreter(module).Run("M.pick", false, 0L, 2L));

        // The array is read before 'grow' appends to it, and the read is of it as it was.
        Assert.NotEqual(0, Copies(module.Find("M.picked")!));
        Assert.Equal(8L, new SilInterpreter(module).Run("M.picked", true, 2L));
        Assert.Equal(33L, new SilInterpreter(module).Run("M.picked", false, 2L));
    }

    sealed class Vandal : ISilPass {
        public string Name => "vandal";

        // Drops every terminator: blocks that no longer end.
        public void Run(SilFunction function) {
            foreach (var block in function.Blocks) {
                block.Instructions.RemoveAt(block.Instructions.Count - 1);
            }
        }
    }

    [Fact]
    public void APassThatBreaksSilIsCaughtAndNamed() {
        var module = Lowered("fn one() -> Int { return 1 }");
        var failure = Assert.Throws<SilPassException>(() => SilPassManager.Run(module, [new Vandal()]));
        Assert.Contains("'vandal'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("does not end in a terminator", failure.Message, StringComparison.Ordinal);
    }
}
