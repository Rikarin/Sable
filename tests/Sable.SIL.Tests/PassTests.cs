namespace Sable.SIL.Tests;

/// <summary>Slot promotion, and the pass manager's promise to verify after every pass.</summary>
public sealed class PassTests {
    static SilModule Lowered(string source) {
        var (module, diagnostics) = SilGen.Lower(Compilation.Create($"module M\n{source}"));
        Assert.Empty(diagnostics);
        return module;
    }

    /// <summary>How many of an instruction the program's own functions have: Sable.Core's, lowered with it, are not counted.</summary>
    static int Count<T>(SilModule module) =>
        module.Functions.Where(static f => !f.Name.StartsWith("Sable.Core.", StringComparison.Ordinal))
            .Sum(static f => f.Blocks.Sum(static b => b.Instructions.OfType<T>().Count()));

    /// <summary>
    ///     An opt-level's passes: 0 the ARC passes alone, 1 without inlining,
    ///     2 and 3 everything, s without loop versioning, z without the inliner
    ///     either; anything else is not a level.
    /// </summary>
    [Fact]
    public void EachOptLevelHasItsPasses() {
        Assert.Same(SilPassManager.Default, SilPassManager.ForLevel("0"));
        Assert.Same(SilPassManager.Optimising, SilPassManager.ForLevel("2"));
        Assert.Same(SilPassManager.Optimising, SilPassManager.ForLevel("3"));
        Assert.Equal(["Devirtualise", "Simplify"], SilPassManager.ForLevel("1").Take(2).Select(static p => p.GetType().Name));
        Assert.DoesNotContain(SilPassManager.ForLevel("1"), static p => p is Inliner or VersionLoops);
        Assert.Contains(SilPassManager.ForLevel("s"), static p => p is Inliner);
        Assert.DoesNotContain(SilPassManager.ForLevel("s"), static p => p is VersionLoops);
        Assert.DoesNotContain(SilPassManager.ForLevel("z"), static p => p is Inliner or VersionLoops);
        Assert.Throws<ArgumentOutOfRangeException>(() => SilPassManager.ForLevel("4"));
    }

    static int Copies(SilModule module, string function) =>
        module.Find(function)!.Blocks.SelectMany(static b => b.Instructions).Count(static i => i is CopyValueInst or LoadInst { Kind: LoadKind.Copy });

    /// <summary>A view of a string, made for a byte to be read through it, is the string: nothing is retained.</summary>
    [Fact]
    public void AStructMadeToBeReadIsItsFields() {
        var module = Lowered(
            """
            struct View {
                let text: String
                let shift: Int

                fn at(_ i: Int) -> Int { return Int(text.utf8[i + shift]) }
            }

            fn sum(_ text: String) -> Int {
                var total = 0
                var i = 0
                while i < text.utf8Count {
                    total += Int(text.utf8[i]) + View { text: text, shift: 0 }.at(i)
                    i += 1
                }
                return total
            }

            fn probe() -> Int { return sum("ab") }
            """
        );
        SilPassManager.Run(module, SilPassManager.Optimising);
        Assert.Equal(0, Copies(module, "M.sum"));
        Assert.Equal(390L, new SilInterpreter(module).Run("M.probe"));
    }

    /// <summary>A local passed to a call that can throw is lent to it, not copied for it.</summary>
    [Fact]
    public void ALocalIsBorrowedAcrossACallThatCanThrow() {
        var module = Lowered(
            """
            enum Odd: Error { Found }

            struct Pair {
                let a: String
                let b: String

                @inline(never)
                fn check(_ n: Int) throws(Odd) -> Int {
                    if n == 7 {
                        throw .Found
                    }
                    return a.count + b.count + n
                }
            }

            @inline(never)
            fn make() -> Pair { return Pair { a: "ab", b: "cde" } }

            fn probe(_ n: Int) -> Int {
                let pair = make()
                var total = 0
                for i in 0..<n {
                    total += (try? pair.check(i)) ?? 100
                }
                return total
            }
            """
        );
        SilPassManager.Run(module, SilPassManager.Optimising);
        Assert.Equal(0, Copies(module, "M.probe"));
        Assert.Equal(169L, new SilInterpreter(module).Run("M.probe", 9L));
    }

    /// <summary>A method taken as a closure borrows the receiver its context holds.</summary>
    [Fact]
    public void ABoundMethodBorrowsItsReceiver() {
        var module = Lowered(
            """
            struct Adder {
                let name: String

                @inline(never)
                fn add(_ n: Int) -> Int { return n + name.count }
            }

            @inline(never)
            fn twice(_ body: (Int) -> Int) -> Int { return body(body(1)) }

            fn probe() -> Int { return twice(Adder { name: "abc" }.add) }
            """
        );
        SilPassManager.Run(module, SilPassManager.Optimising);
        Assert.Equal(0, Copies(module, "M.Adder.add$bound"));
        Assert.Equal(7L, new SilInterpreter(module).Run("M.probe"));
    }

    /// <summary>
    ///     A method an extension gives a type under a requirement's name, with
    ///     other labels, does not stand in for the protocol's own default.
    /// </summary>
    [Fact]
    public void AMethodOfOtherLabelsIsNotTheDefaultItSharesANameWith() {
        var module = Lowered(
            """
            protocol Scalable {
                fn scaled(byPowerOfTwo power: Int) -> Int
            }

            extension Scalable {
                fn scaled(byPowerOfTwo power: Int) -> Int { return 1 << power }
            }

            struct One: Scalable { }

            extension One {
                fn scaled(places: Int) -> String { return "\(places)" }
            }

            fn through<T: Scalable>(_ x: T) -> Int { return x.scaled(byPowerOfTwo: 4) }

            fn probe() -> Int { return through(One()) * 100 + One().scaled(byPowerOfTwo: 5) + One().scaled(places: 1234).count }
            """
        );
        Assert.Equal(1636L, new SilInterpreter(module).Run("M.probe"));
    }

    /// <summary>
    ///     <c>@inline(never)</c> keeps a call however small the callee;
    ///     <c>@inline(always)</c> inlines one however large, loops and all.
    ///     Both are printed and parsed back.
    /// </summary>
    [Fact]
    public void InlineAttributesDecideWhatIsInlined() {
        var module = Lowered(
            """
            @inline(never)
            fn one() -> Int { return 1 }

            @inline(always)
            fn sum(_ n: Int) -> Int {
                var total = 0
                var i = 0
                while i < n {
                    total += i
                    i += 1
                }
                return total
            }

            fn probe(_ n: Int) -> Int { return one() + sum(n) }
            """
        );
        Assert.Equal(SilInline.Never, module.Find("M.one")!.Inline);
        Assert.Equal(SilInline.Always, module.Find("M.sum")!.Inline);

        SilPassManager.Run(module, SilPassManager.Optimising);

        var called = module.Find("M.probe")!.Blocks.SelectMany(static b => b.Instructions).OfType<ApplyInst>()
            .Select(static a => (a.Callee as FunctionRefInst)?.Function).ToList();
        Assert.Contains("M.one", called);
        Assert.DoesNotContain("M.sum", called);

        // Printed before it runs: the interpreter splits the module's async
        // functions (Sable.Core's among them), and split SIL has no text yet.
        var text = SilPrinter.Print(module);
        Assert.Contains("sil @M.one : $() -> Int inline(never)", text, StringComparison.Ordinal);
        Assert.Equal(SilInline.Never, SilParser.Parse(text).Find("M.one")!.Inline);

        Assert.Equal(11L, new SilInterpreter(module).Run("M.probe", 5L));
    }

    [Fact]
    public void ALoopsLocalsBecomeBlockArguments() {
        // Of specialised code (#297): each instance its own.
        using var specialised = SilGen.UseGenerics(GenericsMode.Specialised);
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

    /// <summary>
    ///     An optional element unwrapped — a dictionary's entry — is read in
    ///     place: the copy of its payload pairs with the element's release,
    ///     sunk past what only looks at it, and the element itself is
    ///     borrowed across the switch. A call between keeps the copy: the
    ///     release does not move past what could see it.
    /// </summary>
    [Fact]
    public void AnOptionalElementUnwrappedIsReadInPlace() {
        var module = Lowered(
            """
            struct Entry {
                var key: String
                var value: Int
            }
            fn value(_ entries: Entry?[>..], _ i: Int) -> Int { return entries[i]!.value }
            fn keyCount(_ entries: Entry?[>..], _ i: Int) -> Int { return entries[i]!.key.count }
            fn touch() -> Int { return 1 }
            fn keyAfterCall(_ entries: Entry?[>..], _ i: Int) -> Int {
                let key = entries[i]!.key
                let n = touch()
                return key.count + n
            }
            fn make() -> Entry?[>..] { return [Entry { key: "ab", value: 1 }, none, Entry { key: "cde", value: 3 }] }
            fn run() -> Int { return value(make(), 2) * 100 + keyCount(make(), 0) * 10 + keyAfterCall(make(), 2) }
            """
        );

        SilPassManager.Run(module, SilPassManager.Default);

        foreach (var name in (string[])["M.value", "M.keyCount"]) {
            var function = module.Find(name)!;
            Assert.Empty(Instructions(function).OfType<CopyValueInst>());
            Assert.Empty(Instructions(function).OfType<DestroyValueInst>());
            Assert.DoesNotContain(Instructions(function).OfType<BuiltinInst>(), static b => b.Name == "array_get");
        }

        // The array is the caller's, and stays as it is for the call: the key
        // is read where it is even across another call, its slot unowned.
        Assert.Empty(Instructions(module.Find("M.keyAfterCall")!).OfType<CopyValueInst>());
        var interpreter = new SilInterpreter(module);
        Assert.Equal(324L, interpreter.Run("M.run"));
    }

    /// <summary>
    ///     A dictionary updated through an inout parameter is read without a
    ///     copy, though the read calls out: exclusivity keeps the callee from
    ///     the parameter's storage. The key, held across the <c>??</c>, is
    ///     borrowed to the update.
    /// </summary>
    [Fact]
    public void ADictionaryUpdatedInPlaceIsNotCopied() {
        // Of specialised code (#297): the inliner leaves unspecialised code alone.
        using var specialised = SilGen.UseGenerics(GenericsMode.Specialised);
        var module = Lowered(
            """
            fn bump(_ counts: inout [String: Int], _ words: String[>..]) {
                for word in words {
                    counts[word] = (counts[word] ?? 0) + 1
                }
            }
            fn run() -> Int {
                var counts: [String: Int] = [:]
                bump(&counts, ["a", "b", "a"])
                return (counts["a"] ?? 0) * 10 + (counts["b"] ?? 0)
            }
            """
        );

        SilPassManager.Run(module, SilPassManager.Default);

        // What is left is the loop's own copy of the array it walks.
        var bump = module.Find("M.bump")!;
        Assert.DoesNotContain(Instructions(bump).OfType<LoadInst>(), static l => l.Kind == LoadKind.Copy);
        Assert.Single(Instructions(bump).OfType<CopyValueInst>());
        Assert.Equal(21L, new SilInterpreter(module).Run("M.run"));

        // Borrowed for the getter's call before the getter is inlined: after,
        // the copy would span the getter's blocks, which no borrow is found across.
        var optimised = Lowered(
            """
            fn total(_ keys: Int[>..]) -> Int {
                var map: [Int: Int] = [:]
                for key in keys {
                    map[key] = key * 2
                }
                var sum = 0
                for key in keys {
                    sum = sum + (map[key] ?? 0)
                }
                return sum
            }
            fn run() -> Int { return total([1, 2, 3]) }
            """
        );
        SilPassManager.Run(optimised, SilPassManager.Optimising);
        Assert.DoesNotContain(Instructions(optimised.Find("M.total")!).OfType<LoadInst>(), static l => l.Kind == LoadKind.Copy);
        Assert.Equal(12L, new SilInterpreter(optimised).Run("M.run"));
    }

    static List<SilInstruction> Instructions(SilFunction function) => [..function.Blocks.SelectMany(static b => b.Instructions)];

    static bool Calls(SilFunction function, string callee) =>
        Instructions(function).OfType<FunctionRefInst>().Any(r => r.Function == callee);

    [Fact]
    public void ASmallCalleeIsInlinedAndItsBlocksMerged() {
        var module = Lowered(
            """
            fn square(_ x: Int) -> Int { return x * x }
            fn sumOfSquares(_ a: Int, _ b: Int) -> Int { return square(a) + square(b) }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);

        var sum = module.Find("M.sumOfSquares")!;
        Assert.Empty(Instructions(sum).OfType<ApplyInst>());
        Assert.Single(sum.Blocks);
        Assert.Equal(25L, new SilInterpreter(module).Run("M.sumOfSquares", 3L, 4L));
    }

    /// <summary>
    ///     A method no subclass overrides is called directly, and then, small,
    ///     inlined: its borrowed self is the caller's value, borrowed longer.
    /// </summary>
    [Fact]
    public void AMethodNothingOverridesIsCalledDirectlyAndInlined() {
        var module = Lowered(
            """
            class Shape {
                var name: String
                init(_ name: String) { self.name = name }
                fn label() -> String { return name + "!" }
                fn area() -> Int { return 0 }
            }
            class Square: Shape {
                var side: Int
                init(_ side: Int) {
                    self.side = side
                    super.init("square")
                }
                override fn area() -> Int { return side * side }
            }
            fn describe(_ s: Square) -> String { return s.label() }
            fn measure(_ s: Shape) -> Int { return s.area() }
            fn run() -> String { return describe(Square(3)) + measure(Square(4)).toString() }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);

        // 'label' is Shape's in every class: called directly, then inlined.
        var describe = module.Find("M.describe")!;
        Assert.Empty(Instructions(describe).OfType<ClassMethodInst>());
        Assert.False(Calls(describe, "M.Shape.label"));

        // 'area' is overridden: a Shape could be either, so the vtable decides.
        Assert.Single(Instructions(module.Find("M.measure")!).OfType<ClassMethodInst>());
        Assert.Equal("square!16", new SilInterpreter(module).Run("M.run"));
    }

    [Fact]
    public void RecursionStaysACallAndMutualRecursionEnds() {
        var module = Lowered(
            """
            fn even(_ n: Int) -> Bool { return n == 0 ? true : odd(n - 1) }
            fn odd(_ n: Int) -> Bool { return n == 0 ? false : even(n - 1) }
            fn factorial(_ n: Int) -> Int { return n <= 1 ? 1 : n * factorial(n - 1) }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);

        Assert.True(Calls(module.Find("M.factorial")!, "M.factorial"));
        Assert.Equal(true, new SilInterpreter(module).Run("M.even", 10L));
        Assert.Equal(true, new SilInterpreter(module).Run("M.odd", 7L));
        Assert.Equal(120L, new SilInterpreter(module).Run("M.factorial", 5L));
    }

    /// <summary>
    ///     A call that can throw is inlined: the callee's return goes to the
    ///     call's normal block, its throw to the error block.
    /// </summary>
    [Fact]
    public void ACallThatCanThrowIsInlinedIntoItsTry() {
        var module = Lowered(
            """
            enum Problem: Error { case negative }
            fn checked(_ x: Int) throws(Problem) -> Int {
                if x < 0 {
                    throw Problem.negative
                }
                return x
            }
            fn total(_ x: Int) -> Int {
                return (try? checked(x)) ?? -1
            }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);

        Assert.Empty(Instructions(module.Find("M.total")!).OfType<TryApplyInst>());
        Assert.Equal(-1L, new SilInterpreter(module).Run("M.total", -5L));
        Assert.Equal(5L, new SilInterpreter(module).Run("M.total", 5L));
    }

    /// <summary>
    ///     The inliner's cost: a callee too large to copy everywhere is
    ///     inlined where it is the only caller, or where a literal argument
    ///     decides a branch in it; elsewhere it stays a call.
    /// </summary>
    [Fact]
    public void ACalleesCostAndItsCallersDecideInlining() {
        var module = Lowered(
            """
            fn wide(_ x: Int, _ y: Int) -> Int { return x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y }
            fn shared(_ x: Int, _ y: Int) -> Int { return x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y + x * y }
            fn chosen(_ fast: Bool, _ x: Int) -> Int {
                if fast {
                    return x
                }
                return x * x + x * x + x * x + x * x + x * x + x * x + x * x + x * x + x * x + x * x + x * x + x * x + x * x + x * x
            }
            fn once(_ a: Int) -> Int { return wide(a, 2) }
            fn first(_ a: Int) -> Int { return shared(a, 2) + chosen(true, a) }
            fn second(_ a: Int) -> Int { return shared(a, 3) + chosen(true, a) }
            fn third(_ a: Int, _ fast: Bool) -> Int { return chosen(fast, a) }
            class Cell {
                var v: Int
                init(_ v: Int) { self.v = v }
            }
            fn made(_ i: Int) -> Int { return Cell(i).v }
            fn looped(_ n: Int) -> Int {
                var t = 0
                var i = 0
                while i < n {
                    t = t + made(i)
                    i = i + 1
                }
                return t
            }
            """
        );

        Assert.InRange(Inliner.Cost(module.Find("M.wide")!), Inliner.Threshold + 1, Inliner.SingleCallerThreshold);
        SilPassManager.Run(module, SilPassManager.Optimising);

        Assert.False(Calls(module.Find("M.once")!, "M.wide"));
        Assert.True(Calls(module.Find("M.first")!, "M.shared"));
        Assert.False(Calls(module.Find("M.first")!, "M.chosen"));
        Assert.False(Calls(module.Find("M.second")!, "M.chosen"));
        Assert.True(Calls(module.Find("M.third")!, "M.chosen"));

        // What 'made' allocates stays on its own frame's stack, rather than
        // being made on the heap round the caller's loop.
        Assert.True(Calls(module.Find("M.looped")!, "M.made"));
        Assert.Equal(6L, new SilInterpreter(module).Run("M.looped", 4L));
        Assert.Equal(56L, new SilInterpreter(module).Run("M.once", 2L));
        Assert.Equal(29L, new SilInterpreter(module).Run("M.first", 1L));
    }

    /// <summary>
    ///     An initialiser is inlined into a loop: the object it returns has
    ///     left its frame anyway, and in the caller's it is gone before the
    ///     loop comes round, so it is made on the stack.
    /// </summary>
    [Fact]
    public void AnInitialiserIsInlinedIntoALoopAndItsObjectMadeOnTheStack() {
        var module = Lowered(
            """
            class Cell {
                var v: Int
                init(_ v: Int) { self.v = v }
            }
            fn looped(_ n: Int) -> Int {
                var t = 0
                var i = 0
                while i < n {
                    t = t + Cell(i).v
                    i = i + 1
                }
                return t
            }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);

        var looped = module.Find("M.looped")!;
        Assert.False(Calls(looped, "M.Cell.init"));
        Assert.True(Assert.Single(Instructions(looped).OfType<AllocRefInst>()).OnStack);
        Assert.Equal(6L, new SilInterpreter(module).Run("M.looped", 4L));
    }

    /// <summary>
    ///     A call through the vtable that can reach a few small overrides
    ///     tests for each, calls it directly and inlines it; anything else —
    ///     here the base class itself — still goes through the vtable.
    /// </summary>
    [Fact]
    public void AFewSmallOverridesAreTestedForAndInlined() {
        var module = Lowered(
            """
            open class Animal {
                open fn weight() -> Int { return 7 }
            }
            class Bird: Animal {
                override fn weight() -> Int { return 1 }
            }
            class Dog: Animal {
                override fn weight() -> Int { return 20 }
            }
            fn weigh(_ a: Animal) -> Int { return a.weight() }
            fn total(_ animals: Animal[>..]) -> Int {
                var t = 0
                for a in animals {
                    t = t + a.weight()
                }
                return t
            }
            fn run() -> Int { return weigh(Bird()) * 10000 + weigh(Dog()) * 100 + weigh(Animal()) }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);

        var weigh = module.Find("M.weigh")!;
        Assert.Equal(
            ["class_is:M.Bird", "class_is:M.Dog"],
            Instructions(weigh).OfType<BuiltinInst>().Select(static b => b.Name).Where(static n => n.StartsWith("class_is:", StringComparison.Ordinal))
        );
        Assert.Single(Instructions(weigh).OfType<ApplyInst>());
        Assert.Single(Instructions(weigh).OfType<ClassMethodInst>());
        Assert.Equal(12007L, new SilInterpreter(module).Run("M.run"));

        // A loop's element, held in place, is borrowed across the guesses as across a call.
        Assert.DoesNotContain(Instructions(module.Find("M.total")!).OfType<LoadInst>(), static l => l.Kind == LoadKind.Copy);
        Assert.DoesNotContain(SilPassManager.ForLevel("s"), static p => p is SpeculativeDevirtualise);
    }

    /// <summary>Constants fold, and the branches they decide go with what they skip.</summary>
    [Fact]
    public void ConstantsFoldAndDecidedBranchesGo() {
        var module = Lowered(
            """
            fn pick() -> Int {
                let x = 2
                if x * 3 == 6 {
                    return 10
                }
                return 20
            }
            fn overflows() -> Int {
                let big = 9223372036854775807
                return big + 1
            }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);

        var pick = module.Find("M.pick")!;
        Assert.Single(pick.Blocks);
        Assert.Empty(Instructions(pick).OfType<BuiltinInst>());
        Assert.Equal(10L, new SilInterpreter(module).Run("M.pick"));

        // Arithmetic that overflows is left to trap as the program runs.
        Assert.Single(Instructions(module.Find("M.overflows")!).OfType<BuiltinInst>(), static b => b.Name == "add_Int");
        Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.overflows"));
    }

    /// <summary>
    ///     What is computed twice is computed once; division, remainder and
    ///     shifts of literals fold, but not by zero; a field of a tuple made
    ///     here is what it was made from; a loop's value that never changes
    ///     is no argument of its header.
    /// </summary>
    [Fact]
    public void CommonValuesAreComputedOnceAndArgumentsThatNeverChangeGo() {
        var module = Lowered(
            """
            fn twice(_ a: Int, _ b: Int) -> Int { return (a * b + 1) + (a * b + 1) }
            fn divided() -> Int { return 100 / 7 + 100 % 7 + (1 << 4) }
            fn byZero(_ a: Int) -> Int { return a / 0 }
            fn pair(_ x: Int) -> Int {
                let p = (x, x + 1)
                return p.0 + p.1
            }
            fn steady(_ n: Int) -> Int {
                var k = 5
                var i = 0
                var t = 0
                while i < n {
                    t = t + k
                    i = i + 1
                }
                return t
            }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);

        var twice = module.Find("M.twice")!;
        Assert.Single(Instructions(twice).OfType<BuiltinInst>(), static b => b.Name == "mul_Int");
        Assert.Equal(14L, new SilInterpreter(module).Run("M.twice", 3L, 2L));

        Assert.Empty(Instructions(module.Find("M.divided")!).OfType<BuiltinInst>());
        Assert.Equal(32L, new SilInterpreter(module).Run("M.divided"));
        Assert.Single(Instructions(module.Find("M.byZero")!).OfType<BuiltinInst>(), static b => b.Name == "div_Int");

        Assert.Empty(Instructions(module.Find("M.pair")!).OfType<TupleExtractInst>());
        Assert.Equal(7L, new SilInterpreter(module).Run("M.pair", 3L));

        // The header takes the count and the total; 5 is a literal in the body.
        var steady = module.Find("M.steady")!;
        Assert.Equal(2, steady.Blocks.Where(b => b != steady.Entry).Max(static b => b.Arguments.Count));
        Assert.Equal(20L, new SilInterpreter(module).Run("M.steady", 4L));
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

    /// <summary>
    ///     A counted loop over arrays is versioned: before it, one check that
    ///     every index it will use is in range picks a copy whose accesses are
    ///     not checked again; the loop as written runs otherwise.
    /// </summary>
    [Fact]
    public void ACountedLoopChecksItsIndicesOnceBeforeIt() {
        var module = Lowered(
            """
            fn dot(_ a: Int[:], _ b: Int[:]) -> Int {
                var total = 0
                var i = 0
                while i < a.count {
                    total = total + a[i] * b[i]
                    i = i + 1
                }
                return total
            }
            fn run(_ n: Int) -> Int {
                var a: Int[>..] = []
                var b: Int[>..] = []
                var i = 0
                while i < 10 {
                    a.append(i)
                    i = i + 1
                }
                i = 0
                while i < n {
                    b.append(2)
                    i = i + 1
                }
                return dot(a, b)
            }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);

        var dot = module.Find("M.dot")!;
        var reads = Instructions(dot).OfType<BuiltinInst>().Where(static b => b.Name == "array_get").ToList();
        Assert.Equal(4, reads.Count);
        Assert.Equal(2, reads.Count(static r => r.InBounds));

        // The flag is printed, and read back.
        var text = SilPrinter.Print(module);
        Assert.Contains("builtin [in_bounds] \"array_get\"", text, StringComparison.Ordinal);
        Assert.Equal(text, SilPrinter.Print(SilParser.Parse(text)));

        Assert.Equal(90L, new SilInterpreter(module).Run("M.run", 10L));
        Assert.Equal("array index out of range", Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.run", 9L)).Message);
    }

    /// <summary>A program lowered as a release build lowers it: Sable.Core's '#if RELEASE' side.</summary>
    static SilModule LoweredForRelease(string source) {
        var options = ParseOptions.Default.WithSymbols("RELEASE");
        var (module, diagnostics) = SilGen.Lower(Compilation.Create([SyntaxTree.Parse(new SourceText($"module M\n{source}", "file0.sb"), options)], options));
        Assert.Empty(diagnostics);
        return module;
    }

    /// <summary>The closures the program's own functions still make, and the contexts they still allocate.</summary>
    static List<SilInstruction> Closures(SilModule module) =>
        [
            ..module.Functions.Where(static f => !f.Name.StartsWith("Sable.Core.", StringComparison.Ordinal))
                .SelectMany(Instructions)
                .Where(static i => i is PartialApplyInst || i is AllocRefInst { ClassType.Name: var name } && name.EndsWith("$context", StringComparison.Ordinal)),
        ];

    /// <summary>
    ///     An '@autoclosure' argument is a closure inlined away (#289): in a
    ///     release build, 'assert' is empty, so its condition and message,
    ///     closures before the passes, are made and destroyed and nothing
    ///     more; afterwards nothing of them is left — no closure, no
    ///     context, no call of their code — and nothing they would have
    ///     done is done.
    /// </summary>
    [Fact]
    public void AReleaseAssertLeavesNoClosureBehind() {
        // Of specialised code (#297): each instance its own.
        using var specialised = SilGen.UseGenerics(GenericsMode.Specialised);
        var module = LoweredForRelease(
            """
            fn counted(_ value: Bool) -> Bool {
                print("evaluated")
                return value
            }
            fn probe(_ limit: Int) -> Int {
                var n = 2
                assert(counted(n < limit), "n is \(n), and the limit \(limit)")
                n = n + 1
                return n
            }
            """
        );
        var probe = module.Find("M.probe")!;
        Assert.NotEmpty(Closures(module));
        Assert.Contains(Instructions(probe), i => i is PartialApplyInst { Function: FunctionRefInst { Function: var code } } && module.Find(code)!.Inline == SilInline.Always);

        SilPassManager.Run(module, SilPassManager.Optimising);

        Assert.Empty(Closures(module));
        Assert.DoesNotContain(Instructions(probe), static i => i is FunctionRefInst { Function: var name } && name.Contains("$closure", StringComparison.Ordinal));
        // The box the closure captured 'n' in is made in the frame, as nothing else holds it.
        Assert.DoesNotContain(Instructions(probe), static i => i is AllocRefInst { OnStack: false });
        var interpreter = new SilInterpreter(module);
        Assert.Equal(3L, interpreter.Run("M.probe", 1L));
        Assert.Equal("", interpreter.Output.ToString());
    }

    /// <summary>
    ///     A callee that asks for an '@autoclosure' argument, inlined, has the
    ///     argument's code inlined where it asks, its captures read straight
    ///     from the caller: no closure is made, and the result is as before.
    /// </summary>
    [Fact]
    public void AnAutoclosureCalledByAnInlinedCalleeIsInlinedWhereItIsCalled() {
        var module = Lowered(
            """
            fn orElse(_ value: Int?, _ fallback: @autoclosure () -> Int) -> Int {
                if let v = value {
                    return v
                }
                return fallback()
            }
            fn probe(_ n: Int) -> Int {
                var calls = 0
                let words = ["a", "b"]
                let first = orElse(none, n * 2 + words.count)
                let second = orElse(7, n * 1000)
                calls = calls + first + second
                return calls
            }
            """
        );
        var before = new SilInterpreter(module).Run("M.probe", 5L);

        SilPassManager.Run(module, SilPassManager.Optimising);

        Assert.DoesNotContain(Instructions(module.Find("M.probe")!), static i => i is PartialApplyInst);
        Assert.DoesNotContain(Instructions(module.Find("M.probe")!), static i => i is ApplyInst { Callee: not FunctionRefInst });
        Assert.Equal(19L, before);
        Assert.Equal(before, new SilInterpreter(module).Run("M.probe", 5L));
    }

    /// <summary>
    ///     A <c>let</c> in a loop that borrows what stays put is held in an
    ///     unmanaged slot, stored each turn and never destroyed: the slot owns
    ///     nothing, so the store of the next turn, <c>[init]</c> as the slot
    ///     was managed, writes over the last turn's borrow and leaks nothing.
    /// </summary>
    [Fact]
    public void ALetBorrowedInALoopIsStoredEachTurnOverTheLastTurnsBorrow() {
        var module = SilParser.Parse(
            """
            sil @f : $() -> Int {
            bb0:
              %0 = alloc_stack $String
              %1 = alloc_stack $String
              %2 = string_literal "abc"
              store %2 to [init] %0
              %3 = integer_literal $Int, 0
              %4 = integer_literal $Int, 3
              %5 = integer_literal $Int, 1
              br bb1(%3)
            bb1(%6 : $Int):
              %7 = builtin "cmp_lt_Int"(%6, %4) : $Bool
              cond_br %7, bb2, bb3
            bb2:
              %8 = load [copy] %0
              store %8 to [init] %1
              %9 = load_borrow %1
              end_borrow %9
              destroy_addr %1
              %10 = builtin "add_Int"(%6, %5) : $Int
              br bb1(%10)
            bb3:
              destroy_addr %0
              dealloc_stack %1
              dealloc_stack %0
              return %6
            }
            """
        );

        SilPassManager.Run(module, [new BorrowLets()]);
        var function = module.Find("f")!;
        Assert.True(Instructions(function).OfType<AllocStackInst>().ElementAt(1).Unmanaged, SilPrinter.Print(function));
        Assert.DoesNotContain(Instructions(function), static i => i is LoadInst { Kind: LoadKind.Copy });
        Assert.Equal(3L, new SilInterpreter(module).Run("f"));
    }

    /// <summary>
    ///     An object's fields are initialised where its allocation is in
    ///     view, with nothing to destroy there and nothing to share it into;
    ///     a field stored again is assigned, and so is one stored once the
    ///     object has been given away.
    /// </summary>
    [Fact]
    public void AnObjectJustMadeHasItsFieldsInitialised() {
        var module = Lowered(
            """
            class Node {
                var name: String
                var next: Node?
                init(_ name: String, _ next: Node?) {
                    self.name = name
                    self.next = next
                }
            }
            fn keep(_ node: Node) -> Int { return node.name.count }
            fn make(_ next: Node?) -> Node {
                let node = Node("a", next)
                node.name = "bc"
                return node
            }
            fn run() -> Int { return keep(make(make(none))) }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);
        var stores = Instructions(module.Find("M.make")!).OfType<StoreInst>().Where(static s => s.Address is RefElementAddrInst).ToList();
        Assert.Equal([StoreKind.Init, StoreKind.Init], stores.Where(static s => s.IntoFresh).Take(2).Select(static s => s.Kind));
        Assert.Contains(stores, static s => s.Kind == StoreKind.Assign);
        Assert.Equal(2L, new SilInterpreter(module).Run("M.run"));
    }

    /// <summary>
    ///     A <c>let</c> field of an object a caller lends is borrowed across a
    ///     call; a <c>var</c> field, which the call could change, is copied,
    ///     and so is one of an object that goes before the copy does.
    /// </summary>
    [Fact]
    public void ALetFieldIsBorrowedAcrossACall() {
        var module = Lowered(
            """
            class Item { var n: Int = 1 }
            class Box {
                let fixed: Item
                var changing: Item
                init() {
                    fixed = Item()
                    changing = Item()
                }
            }
            fn touch(_ box: Box) -> Int {
                box.changing = Item()
                return 1
            }
            fn ofLet(_ box: Box) -> Int {
                let item = box.fixed
                let n = touch(box)
                return item.n + n
            }
            fn ofVar(_ box: Box) -> Int {
                let item = box.changing
                let n = touch(box)
                return item.n + n
            }
            fn outliving() -> Item {
                return Box().fixed
            }
            fn run() -> Int { return ofLet(Box()) * 10 + ofVar(Box()) + outliving().n }
            """
        );

        SilPassManager.Run(module, SilPassManager.Default);
        bool Copies(string name) => Instructions(module.Find(name)!).Any(static i => i is LoadInst { Kind: LoadKind.Copy } or CopyValueInst);
        Assert.False(Copies("M.ofLet"), SilPrinter.Print(module.Find("M.ofLet")!));
        Assert.True(Copies("M.ofVar"));
        Assert.True(Copies("M.outliving"));
        Assert.Equal(23L, new SilInterpreter(module).Run("M.run"));
    }

    /// <summary>
    ///     An array viewed whole for a member of <c>Element[:]</c> is not
    ///     retained while what holds it lasts.
    /// </summary>
    [Fact]
    public void AnArrayViewedWholeIsBorrowed() {
        var module = Lowered(
            """
            fn empty(_ xs: Int[>..]) -> Bool { return xs.isEmpty }
            fn run() -> Int { return empty([1]) ? 1 : 2 }
            """
        );

        SilPassManager.Run(module, SilPassManager.Optimising);
        var builtins = Instructions(module.Find("M.empty")!).OfType<BuiltinInst>().Select(static b => b.Name).ToList();
        Assert.DoesNotContain("slice_of", builtins);
        Assert.Equal(2L, new SilInterpreter(module).Run("M.run"));
    }

    /// <summary>
    ///     <c>a[i].x = v</c> stores the field where it is: the element is not
    ///     read out and written back, and in a loop the array is made unique
    ///     once. A copy made before keeps what it had.
    /// </summary>
    [Fact]
    public void AnElementsFieldIsStoredWhereItIs() {
        var module = Lowered(
            """
            struct Point {
                var x: Int
                var name: String
            }
            fn bump(_ points: Point[>..]) -> Point[>..] {
                var moved = points
                var i = 0
                while i < moved.count {
                    moved[i].x = moved[i].x + i
                    i = i + 1
                }
                return moved
            }
            fn run() -> Int {
                let points: Point[>..] = [Point { x: 1, name: "a" }, Point { x: 2, name: "b" }]
                let moved = bump(points)
                return points[1].x * 100 + moved[1].x * 10 + moved[0].name.count
            }
            """
        );

        SilPassManager.Run(module, SilPassManager.Default);
        var builtins = Instructions(module.Find("M.bump")!).OfType<BuiltinInst>().Select(static b => b.Name).ToList();
        Assert.Contains("array_element_addr_unique", builtins);
        Assert.Contains("array_make_unique", builtins);
        Assert.DoesNotContain("array_set", builtins);
        Assert.DoesNotContain("array_set_unique", builtins);
        Assert.Equal(231L, new SilInterpreter(module).Run("M.run"));
    }
}
