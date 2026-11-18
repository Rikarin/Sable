using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     async and await (docs/02 §2.7, docs/04 §5): tasks scheduled
///     cooperatively, one at a time, switching only where one waits — so
///     the interleaving is deterministic, and the interpreter's is the
///     native one. Actors serialise by construction; calls into one from
///     outside it await.
/// </summary>
public sealed class AsyncTests {
    const string Declarations = """
                                actor Counter {
                                    var count = 0
                                    fn bump() -> Int {
                                        count = count + 1
                                        return count
                                    }
                                }

                                fn work(_ n: Int) async -> Int {
                                    await Task.yield()
                                    return n * 2
                                }

                                fn worker(_ name: String, _ rounds: Int, _ counter: Counter) async -> Int {
                                    var last = 0
                                    for i in 0..<rounds {
                                        print("\(name) \(i)")
                                        last = await counter.bump()
                                        await Task.yield()
                                    }
                                    return last
                                }
                                """;

    static string AsyncProgram(string declarations, string body) =>
        $"module Test\n{declarations}\n@main\nfn main() async {{\n{body}\n}}\n";

    [Fact]
    public void TasksInterleaveWhereTheyWait() =>
        Assert.Equal(
            "43\na 0\nb 0\na 1\nb 1\na 2\ntotal 11 count 7\nlate 2\n",
            AssertAgree(AsyncProgram(Declarations, """
                    let c = Counter()
                    let x = await c.bump()
                    async let y = work(21)
                    let z = await y
                    print("\(x + z)")
                    let a = Task.spawn { await worker("a", 3, c) }
                    let b = Task.spawn { await worker("b", 2, c) }
                    let total = await a.value + await b.value
                    print("total \(total) count \(await c.bump())")
                    let late = Task.spawn { "late \(await work(1))" }
                    print(await late.value)
                """)).Stdout
        );

    [Fact]
    public void ATaskNobodyAwaitsStillRunsBeforeTheProgramEnds() =>
        Assert.Equal(
            "main\nbackground 1\nbackground 2\n",
            AssertAgree(AsyncProgram("", """
                    let ignored = Task.spawn {
                        print("background 1")
                        await Task.yield()
                        print("background 2")
                        return 0
                    }
                    print("main")
                """)).Stdout
        );

    [Fact]
    public void ATrapInATaskStopsTheProgram() {
        var execution = AssertAgree(AsyncProgram("", """
                let failing = Task.spawn {
                    let values: Int[>..] = []
                    return values[3]
                }
                print("before")
                print(await failing.value)
            """));
        Assert.Equal("before\n", execution.Stdout);
        Assert.Equal("fatal error: array index out of range\n", execution.Stderr);
    }

    [Theory]
    [InlineData("    let c = Counter()\n    c.bump()", "'bump', isolated to its actor, is async, so the expression needs 'await'")]
    [InlineData("    let t = Task.spawn { 1 }\n    print(t.value)", "reading 'value' is async, so the expression needs 'await'")]
    [InlineData("    async let y = work(1)\n    print(y)", "'y', an 'async let', is async, so the expression needs 'await'")]
    public void AwaitIsWrittenWhereATaskMayWait(string body, string message) {
        var result = Pipeline.Compile(new SourceText(AsyncProgram(Declarations, body), "test.sab"));
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    /// <summary>'var x: T { async get { … } }': a getter that may wait, as Task's 'value' is declared.</summary>
    [Fact]
    public void AnAsyncGetterWaitsWhereItsTaskMay() =>
        Assert.Equal(
            "a\ngot 42\n",
            AssertAgree(AsyncProgram(
                """
                class Later {
                    var n: Int = 41
                    var value: Int {
                        async get {
                            await Task.yield()
                            return n + 1
                        }
                    }
                }
                """,
                """
                    let later = Later()
                    let other = Task.spawn {
                        print("a")
                        return 0
                    }
                    let got = await later.value
                    let zero = await other.value
                    print("got \(got + zero)")
                """)).Stdout
        );
}
