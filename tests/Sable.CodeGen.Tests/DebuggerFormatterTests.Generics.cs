using System.Text.RegularExpressions;
using Sable.Driver.Commands;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Values of a type only its metadata knows (#297 stage 9): in code compiled
///     once with <c>-generics=unspecialised</c> they are shown through the
///     metadata the frame holds, with the types' own names; and what is built
///     for each type is shown as before.
/// </summary>
public sealed partial class DebuggerFormatterTests {
    const string Generics = """
                            module Gen

                            protocol Named {
                                fn name() -> String
                            }

                            struct Point {
                                var x: Int
                                var y: Int
                            }

                            struct Dog: Named {
                                var breed: String
                                fn name() -> String { return breed }
                            }

                            struct Pair<A, B> {
                                var first: A
                                var second: B

                                fn swapped() -> Pair<B, A> {
                                    let a = first
                                    return Pair<B, A> { first: second, second: a } // in pair
                                }
                            }

                            union Reply<T> {
                                Ok(value: T)
                                Both(left: T, right: Int)
                            }

                            final class Box<T> {
                                var value: T
                                var count: Int
                                init(_ value: T) {
                                    self.value = value
                                    self.count = 1
                                }

                                fn replace(_ other: T) -> T {
                                    let old = value
                                    value = other
                                    count = count + 1 // in box
                                    return old
                                }
                            }

                            struct Holder<T> {
                                var items: T[>..]
                                mutating fn add(_ item: T) {
                                    items.append(item)
                                    print(items.count) // in holder
                                }
                            }

                            fn show<T>(_ value: T, _ list: T[>..], _ maybe: T?, _ other: inout T) -> Int {
                                let local = value
                                let pair = (value, 7)
                                let filled: T? = value
                                var nothing: T? = none
                                let box = Box<T>(value)
                                let st = Pair<T, Int> { first: value, second: 3 }
                                let ok = Reply<T>.Ok(value: value)
                                let both = Reply<T>.Both(left: value, right: 5)
                                let nested = Pair<Pair<T, Bool>, T?> { first: Pair<T, Bool> { first: value, second: true }, second: value }
                                let slice = list[1...]
                                let lists: T[>..][>..] = [list, list]
                                let dict: [String: T] = ["k": value]
                                other = value
                                print(list.count) // in show
                                nothing = local
                                return inner(st)
                            }

                            fn inner<U>(_ u: U) -> Int {
                                let many: U[>..] = [u, u]
                                let g = { () -> Int in
                                    let first = u
                                    print(many.count) // in closure
                                    return 1
                                }
                                return g()
                            }

                            fn greet<P: Named>(_ p: P, _ e: any Named) {
                                print(e.name()) // in greet
                            }

                            async fn later<T>(_ value: T) -> T {
                                let copy = value
                                let r = await idle(copy)
                                print("z") // in async
                                return r
                            }

                            async fn idle<T>(_ v: T) -> T { return v }

                            fn each<S: Sequence>(_ s: S) throws(S.Failure) -> Int {
                                var n = 0
                                for try x in s {
                                    n = n + 1 // in each
                                }
                                return n
                            }

                            @main
                            async fn main() {
                                var o = 5
                                print(show(4, [1, 2, 3], none, &o))
                                var q = Point { x: 0, y: 0 }
                                print(show(Point { x: 1, y: 2 }, [Point { x: 3, y: 4 }, Point { x: 5, y: 6 }], Point { x: 7, y: 8 }, &q))
                                var s = "s"
                                print(show("four", ["a", "b"], "c", &s))
                                let b = Box<String>("boxed")
                                print(b.replace("new"))
                                var h = Holder<String> { items: ["one"] }
                                h.add("two")
                                let p = Pair<Int, String> { first: 1, second: "two" }
                                print(p.swapped().second)
                                greet(Dog { breed: "a" }, Dog { breed: "collie" })
                                print(await later("async"))
                                print(each([10, 20]))
                            }
                            """;

    const string MadeAtRunTime = """
                                 module Rt

                                 protocol Zero {
                                     static fn zero() -> Self
                                     fn count() -> Int
                                 }

                                 struct Meters: Zero {
                                     var value: Int
                                     var text: String
                                     static fn zero() -> Meters => Meters { value: 7, text: "long" }
                                     fn count() -> Int => value
                                 }

                                 struct Crate<T> {
                                     var item: T
                                     var tags: (T, Bool)
                                 }

                                 union Maybe<T> {
                                     Just(value: T)
                                     Two(a: T, b: T)
                                 }

                                 extension Crate: Zero where T: Zero {
                                     static fn zero() -> Crate<T> => Crate { item: T.zero(), tags: (T.zero(), true) }
                                     fn count() -> Int => item.count()
                                 }

                                 final class Cell<T> {
                                     var item: T
                                     var bumps = 0
                                     init(item: T) {
                                         self.item = item
                                     }
                                 }

                                 extension Cell: Zero where T: Zero {
                                     static fn zero() -> Cell<T> => Cell(item: T.zero())
                                     fn count() -> Int => item.count() + bumps
                                 }

                                 fn crateOf<T>(_ type: T.Type) -> Type => Crate<T>.self

                                 fn cellOf<T>(_ type: T.Type) -> Type => Cell<T>.self

                                 fn make<T: Zero>(_ type: T.Type) -> T {
                                     let made = T.zero()
                                     let list: T[>..] = [made, T.zero()]
                                     let maybe: T? = made
                                     let no: T? = none
                                     let pair = (made, 5)
                                     let two = Maybe<T>.Two(a: made, b: made)
                                     let cell = Cell<T>(item: made)
                                     let dict: [Int: T] = [4: made]
                                     print(made.count()) // in make
                                     return made
                                 }

                                 @main
                                 fn main() {
                                     let types: [String: Type] = ["meters": Meters.self]
                                     let meters = types["meters"]!
                                     for t in [crateOf(meters), cellOf(meters)] {
                                         let made = make(t)
                                         print(made.count()) // in main
                                     }
                                 }
                                 """;

    /// <summary>Runs a generic program under lldb in one of the modes; empty where there is no lldb.</summary>
    static async Task<string> RunGeneric(GenericsMode mode, string source, string fileName, IReadOnlyList<string> commands, bool separately = false) {
        if (Lldb.Value is not { } lldb) {
            return "";
        }

        using var generics = SilGen.UseGenerics(mode);
        return await Run(lldb, source, fileName, commands, separately);
    }

    /// <summary>That a session shows each text, with no formatter having raised an exception; nothing where there was no session.</summary>
    static void ExpectAll(string output, params string[] expected) {
        if (output.Length == 0) {
            return;
        }

        Assert.DoesNotContain("Traceback", output, StringComparison.Ordinal);
        foreach (var text in expected) {
            Expect(output, text);
        }
    }

    static string Break(string source, string file, string marker) => $"b {file}:{LineOf(source, marker)}";

    /// <summary>
    ///     A type parameter's value, and what is built on it, show the value
    ///     under its type's own name, whichever way generic code is compiled —
    ///     and, unspecialised, however the program is put together.
    /// </summary>
    [PosixTheory(NeedsLldb)]
    [InlineData(GenericsMode.Specialised, false)]
    [InlineData(GenericsMode.Unspecialised, false)]
    [InlineData(GenericsMode.Unspecialised, true)]
    public async Task GenericFramesShowValuesAndTheirTypes(GenericsMode mode, bool separately) {
        var output = await RunGeneric(mode, Generics, "gen.sb", [
            Break(Generics, "gen.sb", "// in show"), "run", "frame variable", "continue", "frame variable", "continue", "frame variable",
        ], separately);
        ExpectAll(output,
            "(Int) value = 4\n([Int]) list = count = 3 {\n  [0] = 1\n  [1] = 2\n  [2] = 3\n}\n(Int?) maybe = none\n",
            "(Int) local = 4\n((Int, Int)) pair = (4, 7)\n(Int?) filled = 4\n(Int?) nothing = none\n",
            "(Gen.Box<Int>) box = Gen.Box<Int> {\n  value = 4\n  count = 1\n  [strong] = 1\n}\n",
            mode == GenericsMode.Specialised ? "(Gen.Pair<Int, Int>) st = (first = 4, second = 3)" : "(Gen.Pair<Int, Int>) st = {\n  first = 4\n  second = 3\n}\n",
            "(Gen.Reply<Int>) ok = Ok(",
            "(Gen.Reply<Int>) both = Both(",
            "([Int:]) slice = count = 2 {\n  [0] = 2\n  [1] = 3\n}\n",
            "([[Int]]) lists = count = 2 {\n  [0] = count = 3 {\n    [0] = 1\n",
            "(Sable.Core.Dictionary<String, Int>) dict = count = 1 {\n  [\"k\"] = 4\n}\n",

            "([Gen.Point]) list = count = 2 {\n  [0] = (x = 3, y = 4)\n  [1] = (x = 5, y = 6)\n}\n",
            "(Gen.Point?) maybe = (x: 7, y: 8)",
            mode == GenericsMode.Specialised ? "(Gen.Point) local = (x = 1, y = 2)" : "(Gen.Point) local = {\n  x = 1\n  y = 2\n}\n",
            "((Gen.Point, Int)) pair = ((x: 1, y: 2), 7)",
            "  value = (x = 1, y = 2)\n  count = 1\n",

            "(String) value = \"four\"\n",
            "(String?) maybe = \"c\"",
            "((String, Int)) pair = (\"four\", 7)"
        );
        if (mode == GenericsMode.Unspecialised) {
            ExpectAll(output,
                // An inout parameter is where the value is: it shows the value.
                "(Int) other = 4\n",
                "(Gen.Reply<Int>) ok = Ok(4)",
                "(Gen.Reply<Int>) both = Both(4, 5)",
                "(Gen.Pair<Gen.Pair<Int, Bool>, Int?>) nested = {\n  first = {\n    first = 4\n    second = true\n  }\n  second = 4\n}\n"
            );

            // An empty optional of each: a tag, a niche, and a string's null.
            Assert.Equal(output.Length == 0 ? 0 : 3, Regex.Matches(output, @"(?m)^\((Int|Gen\.Point|String)\?\) nothing = none\r?$").Count);

            // What the compiler passes is no variable of the program's.
            Assert.DoesNotContain("$metadata", output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     A method's self, a closure's captures, an async function's frame, an
    ///     associated type's value and an existential, each in code compiled
    ///     once; and frames named for the types they run with.
    /// </summary>
    [PosixTheory(NeedsLldb)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnspecialisedFramesAreNamedAndShowWhatTheyHold(bool separately) {
        string[] places = ["pair", "box", "holder", "closure", "greet", "async", "each"];
        var output = await RunGeneric(GenericsMode.Unspecialised, Generics, "gen.sb", [
            ..places.Select(static p => Break(Generics, "gen.sb", "// in " + p)), "run", ..Enumerable.Repeat<string[]>(["bt 2", "frame variable", "continue"], 9).SelectMany(static c => c),
        ], separately);
        ExpectAll(output,
            "Gen.inner$closure0<Gen.Pair<Int, Int>> at gen.sb:",
            "(Gen.Pair<Int, Int>) u = {\n  first = 4\n  second = 3\n}\n(Gen.Pair<Int, Int>) first = {\n  first = 4\n  second = 3\n}\n([Gen.Pair<Int, Int>]) many = count = 2 {",
            "Gen.Box<String>.replace(self=Gen.Box<String>, other=\"new\") at gen.sb:",
            "(Gen.Box<String>) self = Gen.Box<String> {\n  value = \"new\"\n  count = 1\n",
            "(String) other = \"new\"\n(String) old = \"boxed\"\n",
            "Gen.Holder<String>.add(self=(items: count = 2), item=\"two\") at gen.sb:",
            "(Gen.Holder<String>) self = {\n  items = count = 2 {\n    [0] = \"one\"\n    [1] = \"two\"\n  }\n}\n(String) item = \"two\"\n",
            "Gen.Pair<Int, String>.swapped(self=(first: 1, second: \"two\")) at gen.sb:",
            "(Gen.Pair<Int, String>) self = {\n  first = 1\n  second = \"two\"\n}\n(Int) a = 1\n",
            "Gen.greet<Gen.Dog>(p=(breed: \"a\"), e=Gen.Dog) at gen.sb:",
            "(Gen.Dog) p = {\n  breed = \"a\"\n}\n(any Gen.Named) e = Gen.Dog {\n  [value] = (breed = \"collie\")\n}\n",
            "Gen.later<String>",
            // On Linux arm64 lldb looks for an async frame's locals in the wrong place, and says they are not strings (#297).
            OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "(String) value = \"async\"\n"
                : "(String) value = \"async\"\n(String) copy = \"async\"\n(String) r = \"async\"\n",
            "Gen.each<[Int]>(s=count = 2) at gen.sb:",
            "([Int]) s = count = 2 {\n  [0] = 10\n  [1] = 20\n}\n(Int) n = 0\n(Int) x = 10\n"
        );
    }

    /// <summary>
    ///     Types the program makes as it runs, which no code names and no DWARF
    ///     describes, are shown from their metadata's field descriptors: in the
    ///     frame that makes them, and in the existential it gives.
    /// </summary>
    [PosixFact(NeedsLldb)]
    public async Task TypesMadeAtRunTimeShowFromTheirMetadata() {
        var output = await RunGeneric(GenericsMode.Unspecialised, MadeAtRunTime, "rt.sb", [
            Break(MadeAtRunTime, "rt.sb", "// in make"), Break(MadeAtRunTime, "rt.sb", "// in main"), "run", "frame variable", "continue", "frame variable made t", "continue", "frame variable", "continue", "frame variable made",
        ]);
        const string crate = "{\n  item = (value = 7, text = \"long\")\n  tags = ((value: 7, text: \"long\"), true)\n}\n";
        ExpectAll(output,
            "Rt.make<Rt.Crate<Rt.Meters>>(type=Rt.Crate<Rt.Meters>) at rt.sb:",
            "(Type) type = Rt.Crate<Rt.Meters>\n",
            "(Rt.Crate<Rt.Meters>) made = " + crate,
            "([Rt.Crate<Rt.Meters>]) list = count = 2 {\n  [0] = {\n    item = (value = 7, text = \"long\")\n",
            "(Rt.Crate<Rt.Meters>?) maybe = (item: (value: 7, text: \"long\"), tags: ((value: 7, text: \"long\"), true))",
            "(Rt.Crate<Rt.Meters>?) no = none\n",
            "((Rt.Crate<Rt.Meters>, Int)) pair = ((item: (value: 7, text: \"long\"), tags: ((value: 7, text: \"long\"), true)), 5)\n",
            "(Rt.Maybe<Rt.Crate<Rt.Meters>>) two = Two((item: ",
            "(Rt.Cell<Rt.Crate<Rt.Meters>>) cell = Rt.Cell<Rt.Crate<Rt.Meters>> {\n  item = {\n    item = (value = 7, text = \"long\")\n",
            "(Sable.Core.Dictionary<Int, Rt.Crate<Rt.Meters>>) dict = count = 1 {\n  [4] = {\n    item = (value = 7, text = \"long\")\n",
            "(any Rt.Zero) made = Rt.Crate<Rt.Meters> {\n  [value] = {\n    item = (value = 7, text = \"long\")\n",
            "(Rt.Cell<Rt.Meters>?) maybe = Rt.Cell<Rt.Meters> {\n  item = (value = 7, text = \"long\")\n  bumps = 0\n}\n",
            "(any Rt.Zero) made = Rt.Cell<Rt.Meters> {\n  [value] = Rt.Cell<Rt.Meters> {\n    item = (value = 7, text = \"long\")\n    bumps = 0\n"
        );
    }

    /// <summary>A breakpoint on a generic function stops at its first statement, its parameters there to read.</summary>
    [PosixTheory(NeedsLldb)]
    [InlineData(GenericsMode.Specialised, "Gen.show<Int>")]
    [InlineData(GenericsMode.Unspecialised, "Gen.show<T>")]
    public async Task ABreakpointOnAGenericFunctionStopsAtItsFirstStatement(GenericsMode mode, string name) {
        var output = await RunGeneric(mode, Generics, "gen.sb", [$"b '{name}'", "run", "frame variable value list", "next", "kill"]);
        ExpectAll(output,
            $"Gen.show<Int>(value=4, list=count = 3, maybe=none, other={(mode == GenericsMode.Specialised ? "0x" : "5")}",
            $" at gen.sb:{LineOf(Generics, "let local = value")}:",
            "(Int) value = 4\n([Int]) list = count = 3 {",
            $" at gen.sb:{LineOf(Generics, "let pair = (value, 7)")}:"
        );
    }

    /// <summary>
    ///     At a function's first instruction its variables are whatever the
    ///     stack held, and a program may overwrite its memory: each is a
    ///     marker then, or what it happens to say; never an exception, a hang,
    ///     or a name made of it.
    /// </summary>
    [PosixFact(NeedsLldb)]
    public async Task UninitialisedAndOverwrittenMemoryShowAsMarkers() {
        string[] names = ["value", "list", "maybe", "box", "st", "ok", "slice", "lists", "dict"];
        string[] words = ["1", "0xfffffffffffffff0", "0x4141414141414141", "`$sp`", "`$pc`"];
        var output = await RunGeneric(GenericsMode.Unspecialised, Generics, "gen.sb", [
            "br set -K false -n 'Gen.show<T>'", "run", "script print('entered')", "frame variable", "breakpoint delete -f",
            Break(Generics, "gen.sb", "// in show"), "continue",

            // Their storage, then what it refers to, word by word: as each is, and as two are.
            ..from word in words
              from name in names
              from command in (string[])[$"memory write -s 8 `&{name}` {word}", "frame variable", "frame info"]
              select command,
            "script print('survived')",
        ]);
        ExpectAll(output, "entered", "survived", "<invalid metadata at 0x", "<invalid list at 0x", "<invalid object at 0x");
    }

    /// <summary>
    ///     A list's, a string's and an object's own memory overwritten: counts
    ///     past any sense, and headers that are none.
    /// </summary>
    [PosixFact(NeedsLldb)]
    public async Task OverwrittenObjectsShowAsMarkers() {
        const string lists = "frame variable numbers names greeting pet node ages";
        var output = await RunGeneric(GenericsMode.Unspecialised, Values, "values.sb", [
            Break(Values, "values.sb", "// stop"), "run", lists,
            "memory write -s 8 `*(long*)&numbers + 16` 0x7fffffffffffffff", lists,
            "memory write -s 8 `*(long*)&names + 16` 0x3fffffffffff 0x7fffffffffff", lists,
            "memory write -s 8 `*(long*)&greeting` 0x10", lists,
            "memory write -s 8 `*(long*)&node` 0x4141414141414141", lists,
            "memory write -s 8 `(long)&pet + 8` 0x1000", lists,
            "memory write -s 8 `*(long*)&ages` 0x0", lists,
            "script print('survived')",
        ]);
        ExpectAll(output,
            "survived",
            ": count = 9223372036854775807, capacity = ",
            "([String]) names = <invalid list at 0x",
            "(Values.Node) node = <invalid object at 0x",
            "(any Values.Named) pet = <invalid existential at 0x",
            "ages = <invalid list at 0x"
        );
    }

    /// <summary>The metadata a frame holds is there for a formatter to find, in the IR, whatever debugger there is: artificial, and no argument.</summary>
    [Fact]
    public void TheMetadataIsAnArtificialVariable() {
        var directory = Directory.CreateTempSubdirectory("sable-generics-");
        using var generics = SilGen.UseGenerics(GenericsMode.Unspecialised);
        try {
            var result = Pipeline.Compile(new SourceText(Generics, "gen.sb"), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var metadata = Regex.Match(result.LlvmIr!, @"!DILocalVariable\(name: ""\$metadata\.T"", scope.*");
            Assert.True(metadata.Success, "no $metadata.T");
            Assert.Contains("flags: DIFlagArtificial", metadata.Value, StringComparison.Ordinal);
            Assert.Contains("!DICompositeType(tag: DW_TAG_structure_type, name: \"SableValue$dynamic\", size: 128", result.LlvmIr!, StringComparison.Ordinal);
            Assert.DoesNotContain("@opaque(", Regex.Replace(result.LlvmIr!, "(?m)^(?!!).*$", ""), StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>The DWARF of code compiled once is well formed, too.</summary>
    [PosixFact("the debug information is DWARF, for lldb and gdb; a Windows build is CodeView, checked by AWindowsTargetIsDescribedInCodeView")]
    public async Task UnspecialisedDwarfVerifies() {
        using var generics = SilGen.UseGenerics(GenericsMode.Unspecialised);
        await Verifies(Generics, "gen.sb");
    }

    /// <summary>
    ///     <c>sable build -g</c> of a file, unoptimised, leaves debug
    ///     information a debugger finds, though the object the program was
    ///     linked from is gone.
    /// </summary>
    [PosixFact(NeedsLldb)]
    public async Task AFileBuiltWithDebugInformationKeepsIt() {
        if (Lldb.Value is not { } lldb) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-build-g-");
        try {
            var source = Path.Combine(directory.FullName, "steps.sb");
            File.WriteAllText(source, Steps);
            var executable = Path.Combine(directory.FullName, "steps");
            Assert.Equal(0, Command.Find("build")!.Run([source, "-g", "-o", executable]));
            var output = await Debug(lldb, executable, ["b steps.sb:5", "run", "frame variable", "kill"]);
            Expect(output, "(Int) doubled = 2");
        } finally {
            directory.Delete(true);
        }
    }
}
