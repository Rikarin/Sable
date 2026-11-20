namespace Sable.SIL.Tests;

/// <summary>
///     Async functions as state machines (#276): the coroutine split a
///     generator's body has, made after the passes. A function that may
///     suspend is a start, which makes its frame on its task's slab, and a
///     resume function; one that cannot is an ordinary function.
/// </summary>
public sealed class AsyncLoweringTests {
    static SilModule Lower(string source, bool optimise = false) {
        var compilation = Compilation.Create(source);
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => d.Message)));
        Assert.Empty(SilVerifier.Verify(module));
        if (optimise) {
            SilPassManager.Run(module, SilPassManager.Optimising);
        }

        return module;
    }

    static (string Output, SilInterpreter Interpreter) Run(SilModule module) {
        var interpreter = new SilInterpreter(module, 50_000_000);
        interpreter.Run(module.EntryPoint!);
        Assert.Equal(0, interpreter.LiveObjects);
        return (interpreter.Output.ToString(), interpreter);
    }

    const string Chain = """
                         module M
                         fn leaf(_ n: Int) async -> Int {
                             await Task.yield()
                             return n + 1
                         }
                         fn middle(_ n: Int) async -> Int {
                             let a = await leaf(n)
                             let b = await leaf(a)
                             return a + b
                         }
                         fn top(_ name: String, _ n: Int) async -> Int {
                             var total = 0
                             for i in 0..<n {
                                 total += await middle(i)
                                 print("\(name) \(i)")
                             }
                             return total
                         }
                         @main
                         fn main() async {
                             let a = Task.spawn { await top("a", 3) }
                             let b = Task.spawn { await top("b", 2) }
                             print("spawned")
                             let total = await a.value + await b.value
                             print("total \(total)")
                         }
                         """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAwaitChainSuspendsAndResumesAtEachLevel(bool optimise) {
        var module = Lower(Chain, optimise);
        var (output, interpreter) = Run(module);
        Assert.Equal("spawned\na 0\nb 0\na 1\nb 1\na 2\ntotal 23\n", output);

        // Three frames deep at most beneath a task's root — top, middle and
        // leaf — however many awaits: frames are pushed and popped, not kept.
        Assert.InRange(interpreter.MostFrames, 1, 5);
    }

    [Fact]
    public void AFunctionThatMaySuspendIsAStartAndAResumeFunction() {
        var module = Lower(Chain);
        AsyncLowering.Run(module);
        var resume = module.Find(AsyncLowering.ResumeName("M.middle"))!;
        var start = module.Find("M.middle")!;
        Assert.Equal(AsyncLowering.ResumeType, resume.Type);
        Assert.False(start.Type.IsAsync);
        Assert.Equal("(Int) -> $async.head$Int", start.Type.ToString());

        // The start makes the frame and runs nothing; every slot is a field of it.
        var made = Assert.Single(start.Blocks.SelectMany(static b => b.Instructions).OfType<AllocRefInst>());
        Assert.True(made.ClassType.IsCoroutineFrame);
        Assert.DoesNotContain(start.Blocks.SelectMany(static b => b.Instructions), static i => i is ApplyInst);
        Assert.Empty(resume.Blocks.SelectMany(static b => b.Instructions).OfType<AllocStackInst>());
    }

    [Fact]
    public void AnAsyncFunctionThatCannotSuspendIsAnOrdinaryFunction() {
        var module = Lower("""
                           module M
                           fn double(_ n: Int) async -> Int {
                               return n * 2
                           }
                           fn twice(_ n: Int) async -> Int {
                               return await double(await double(n))
                           }
                           @main
                           fn main() async {
                               print(await twice(5))
                           }
                           """);
        var (output, _) = Run(module);
        Assert.Equal("20\n", output);
        Assert.Null(module.Find(AsyncLowering.ResumeName("M.double")));
        Assert.Null(module.Find(AsyncLowering.ResumeName("M.twice")));
        Assert.False(module.Find("M.twice")!.Type.IsAsync);
    }

    [Fact]
    public void ValuesLiveAcrossAnAwaitAreKeptInTheFrame() {
        var module = Lower("""
                           module M
                           fn later(_ s: String) async -> String {
                               await Task.yield()
                               return s + "!"
                           }
                           @main
                           fn main() async {
                               let other = Task.spawn { print("other") }
                               var parts: String[>..] = []
                               for word in ["a", "b"] {
                                   parts.append("\(word) \(await later(word)) \(word.count)")
                               }
                               print(parts.count)
                               print("\(parts[0]) / \(parts[1]) \(await later("end"))")
                               await other.value
                           }
                           """);
        var (output, _) = Run(module);
        Assert.Equal("other\n2\na a! 1 / b b! 1 end!\n", output);
    }

    [Fact]
    public void AnAsyncThrowingCallPassesItsErrorOn() {
        var module = Lower("""
                           module M
                           union Failed {
                               Because(why: String)
                           }
                           fn risky(_ n: Int) async throws(Failed) -> Int {
                               await Task.yield()
                               if n > 2 {
                                   throw .Because(why: "too big")
                               }
                               return n
                           }
                           fn sum(_ n: Int) async throws(Failed) -> Int {
                               var total = 0
                               for i in 0...n {
                                   total += try await risky(i)
                               }
                               return total
                           }
                           @main
                           fn main() async {
                               do {
                                   print(try await sum(2))
                                   print(try await sum(5))
                               } catch .Because(let why) {
                                   print("failed: \(why)")
                               }
                           }
                           """);
        Assert.Equal("3\nfailed: too big\n", Run(module).Output);
    }

    [Fact]
    public void SleepersWakeInTheOrderOfTheirDeadlines() {
        var module = Lower("""
                           module M
                           @main
                           fn main() async {
                               let slow = Task.spawn {
                                   try? await Task.sleep(nanoseconds: 60_000_000)
                                   print("slow")
                               }
                               let fast = Task.spawn {
                                   try? await Task.sleep(nanoseconds: 20_000_000)
                                   print("fast")
                               }
                               await slow.value
                               await fast.value
                               print("done")
                           }
                           """);
        Assert.Equal("fast\nslow\ndone\n", Run(module).Output);
    }

    /// <summary>
    ///     Every frame can be dropped part-way: its drop function destroys
    ///     what it holds where it stopped — the callee frame it waits in, the
    ///     owned values live there, its slots. Two tasks waiting on each
    ///     other are dropped as the program ends, and nothing leaks.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TasksThatCanNeverGoOnAreDroppedAndWhatTheyHeldIsGivenBack(bool optimise) {
        var module = Lower("""
                           module M
                           class Box {
                               var task: Task<Int>? = none
                           }
                           fn wait(_ other: Task<Int>, _ label: String) async -> Int {
                               let held = [label, label + "!"]
                               let n = await other.value
                               return n + held.count
                           }
                           @main
                           fn main() async {
                               let box = Box()
                               let a = Task.detached {
                                   let other = box.task!
                                   box.task = none
                                   return await wait(other, "a")
                               }
                               let b = Task.detached { await wait(a, "b") }
                               box.task = b
                               print("main ends")
                           }
                           """, optimise);
        AsyncLowering.Run(module);
        var drop = module.Find(AsyncLowering.DropName("M.wait"));
        Assert.NotNull(drop);
        Assert.Equal(AsyncLowering.DropType, drop.Type);

        var (output, interpreter) = Run(module);
        Assert.Equal("main ends\n", output);
        Assert.Equal(2, interpreter.DroppedTasks);
    }

    /// <summary>
    ///     'async let' is a child task, spawned where it is declared, read by
    ///     waiting for it, and cancelled and waited for as its scope ends.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAsyncLetIsAChildTaskOfItsScope(bool optimise) {
        var module = Lower("""
                           module M
                           fn work(_ name: String) async -> String {
                               print("\(name) starts")
                               await Task.yield()
                               print("\(name) ends")
                               return name
                           }
                           fn unread() async {
                               async let never = work("unread")
                               print("leaving")
                           }
                           @main
                           fn main() async {
                               async let a = work("a")
                               print("declared")
                               print(await a)
                               await unread()
                           }
                           """, optimise);
        Assert.Contains(module.Functions.SelectMany(static f => f.Blocks).SelectMany(static b => b.Instructions), static i => i is BuiltinInst { Name: "task_spawn" });
        Assert.Equal("declared\na starts\na ends\na\nleaving\nunread starts\nunread ends\n", Run(module).Output);
    }
}
