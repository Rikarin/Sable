using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Structured concurrency (SYNTAX.md §19, #276): a task spawned from
///     another is its child — it takes its priority, is cancelled with it, and
///     keeps it from finishing until it has — task groups whose children
///     cannot outlive them, cancellation handlers, and tasks that can never
///     go on dropped as the program ends, what their frames held given back.
///     Each program runs natively and interpreted, the two agreeing, and
///     neither leaking.
/// </summary>
public sealed class StructuredConcurrencyTests {
    static string AsyncProgram(string declarations, string body) =>
        $"module Test\n{declarations}\n@main\nfn main() async {{\n{body}\n}}\n";

    const string Napper = """
                          fn napper(_ name: String) async -> String {
                              do {
                                  try await Task.sleep(nanoseconds: 10_000_000_000)
                                  return "\(name) woke"
                              } catch {
                                  return "\(name): \(error.description())"
                              }
                          }
                          """;

    [Fact]
    public void CancellingATaskCancelsItsChildren() =>
        Assert.Equal(
            "false\nparent: the task was cancelled; child: the task was cancelled\n",
            AssertAgree(AsyncProgram(Napper, """
                    print(Task.isCancelled)
                    let parent = Task.spawn {
                        let child = Task.spawn { await napper("child") }
                        let mine = await napper("parent")
                        return "\(mine); \(await child.value)"
                    }
                    await Task.yield()
                    parent.cancel()
                    print(await parent.value)
                """)).Stdout
        );

    [Fact]
    public void ADetachedTaskIsNotCancelledWithTheTaskThatSpawnedIt() =>
        Assert.Equal(
            "true false\n",
            AssertAgree(AsyncProgram("", """
                    let outer = Task.spawn {
                        let free = Task.detached {
                            await Task.yield()
                            return Task.isCancelled
                        }
                        let child = Task.spawn {
                            await Task.yield()
                            return Task.isCancelled
                        }
                        return "\(await child.value) \(await free.value)"
                    }
                    outer.cancel()
                    print(await outer.value)
                """)).Stdout
        );

    [Fact]
    public void ACheckForCancellationThrowsOnceTheTaskIsCancelled() =>
        Assert.Equal(
            "fine\nthe task was cancelled\n",
            AssertAgree(AsyncProgram("", """
                    let task = Task.spawn {
                        var said: String[>..] = []
                        for _ in 0..<2 {
                            do {
                                try Task.checkCancellation()
                                said.append("fine")
                            } catch {
                                said.append(error.description())
                            }
                            await Task.yield()
                        }
                        return said
                    }
                    await Task.yield()
                    task.cancel()
                    for line in await task.value {
                        print(line)
                    }
                """)).Stdout
        );

    [Fact]
    public void ATaskFinishesOnlyOnceItsChildrenHave() =>
        Assert.Equal(
            "parent's body done\nchild done\n1\n",
            AssertAgree(AsyncProgram("", """
                    let parent = Task.spawn {
                        let child = Task.spawn {
                            await Task.yield()
                            await Task.yield()
                            print("child done")
                        }
                        print("parent's body done")
                        return 1
                    }
                    print(await parent.value)
                """)).Stdout
        );

    [Fact]
    public void AChildTakesItsParentsPriorityUnlessGivenOne() =>
        Assert.Equal(
            "21\n25 25 9\n21\n",
            AssertAgree(AsyncProgram("", """
                    print(Task.currentPriority.rawValue)
                    let high = Task.detached(priority: .high) {
                        let child = Task.spawn { Task.currentPriority }
                        let low = Task.spawn(priority: .background) { Task.currentPriority }
                        return "\(Task.currentPriority.rawValue) \((await child.value).rawValue) \((await low.value).rawValue)"
                    }
                    print(await high.value)
                    let plain = Task.detached { Task.currentPriority == TaskPriority.medium ? 21 : 0 }
                    print(await plain.value)
                """)).Stdout
        );

    [Fact]
    public void AGroupGivesItsChildrensResultsAsTheyFinish() =>
        Assert.Equal(
            "child 3\ngot 30\nchild 2\ngot 20\nchild 1\ngot 10\n60\n",
            AssertAgree(AsyncProgram("", """
                    let total = await withTaskGroup(of: Int.self) { group in
                        for n in 1...3 {
                            group.spawn {
                                try? await Task.sleep(nanoseconds: (4 - n) * 20_000_000)
                                print("child \(n)")
                                return n * 10
                            }
                        }
                        var sum = 0
                        for await part in group {
                            print("got \(part)")
                            sum += part
                        }
                        return sum
                    }
                    print(total)
                """)).Stdout
        );

    /// <summary>The children a body leaves are waited for as the group's scope ends.</summary>
    [Fact]
    public void AGroupWaitsAtTheEndOfItsScopeForTheChildrenLeft() =>
        Assert.Equal(
            "body done\nslow done\nafter 2\n",
            AssertAgree(AsyncProgram("", """
                    let taken = await withTaskGroup(of: Int.self) { group in
                        group.spawn {
                            await Task.yield()
                            await Task.yield()
                            print("slow done")
                            return 1
                        }
                        group.spawn { 2 }
                        let first = await group.next() ?? 0
                        print("body done")
                        return first
                    }
                    print("after \(taken)")
                """)).Stdout
        );

    const string Odd = """
                       union Bad: Error {
                           Odd(n: Int)
                       }

                       fn check(_ n: Int) async throws -> Int {
                           try await Task.sleep(nanoseconds: n * 2_000_000)
                           if n % 2 == 1 {
                               throw Bad.Odd(n: n)
                           }
                           return n
                       }
                       """;

    [Fact]
    public void AThrowingGroupThrowsAChildsErrorInItsTurn() =>
        Assert.Equal(
            "got 2\ngot 4\ncaught Bad.Odd(n: 5)\ntrue\n",
            AssertAgree(AsyncProgram(Odd, """
                    do {
                        let sum = try await withThrowingTaskGroup(of: Int.self) { group in
                            for n in [4, 2, 5] {
                                group.spawn { try await check(n) }
                            }
                            var sum = 0
                            for try await n in group {
                                print("got \(n)")
                                sum += n
                            }
                            return sum
                        }
                        print(sum)
                    } catch {
                        print("caught \(error.description())")
                    }
                    let failed = try? await withThrowingTaskGroup(of: Int.self) { group in
                        group.spawn { 7 }
                        group.spawn { try await check(5) }
                        try await group.waitForAll()
                        return 1
                    }
                    print(failed == none)
                """)).Stdout
        );

    /// <summary>A body that throws cancels the children it leaves: a ten-second sleeper ends at once.</summary>
    [Fact]
    public void AThrowingGroupsBodyThatThrowsCancelsItsChildren() =>
        Assert.Equal(
            "caught Bad.Odd(n: 1)\ntrue\n",
            AssertAgree(AsyncProgram("import Sable.Time\n" + Odd, """
                    let start = Instant.now()
                    do {
                        let _ = try await withThrowingTaskGroup(of: Int.self) { group in
                            group.spawn {
                                try await Task.sleep(nanoseconds: 10_000_000_000)
                                return 1
                            }
                            group.spawn { try await check(1) }
                            return try await group.next() ?? 0
                        }
                    } catch {
                        print("caught \(error.description())")
                    }
                    print(start.elapsed < Duration.seconds(5))
                """)).Stdout
        );

    [Fact]
    public void ACancellationHandlerRunsAsItsTaskIsCancelled() =>
        Assert.Equal(
            "handler ran\nhandled: the task was cancelled\nhandler ran at once\nlate woke\n",
            AssertAgree(AsyncProgram(Napper, """
                    let handled = Task.spawn {
                        return await withTaskCancellationHandler(operation: {
                            await napper("handled")
                        }, onCancel: {
                            print("handler ran")
                        })
                    }
                    await Task.yield()
                    handled.cancel()
                    print(await handled.value)

                    // Cancelled already: the handler runs as it is installed.
                    let late = Task.spawn {
                        await Task.yield()
                        return await withTaskCancellationHandler(operation: {
                            "late woke"
                        }, onCancel: {
                            print("handler ran at once")
                        })
                    }
                    late.cancel()
                    print(await late.value)
                """)).Stdout
        );

    /// <summary>
    ///     Two tasks waiting on each other can never go on: as the program
    ///     ends they are dropped, and what their frames held — a chain of
    ///     calls three deep, an async generator part-way through, whose
    ///     defer runs — is destroyed, so nothing leaks (#276).
    /// </summary>
    [Fact]
    public void TasksThatCanNeverGoOnAreDroppedAsTheProgramEnds() =>
        Assert.Equal(
            "main ends\ntick 1\nlevel 0 gone\nticks left\ngenerator's gone\nlevel 1 gone\nlevel 2 gone\n",
            AssertAgree(AsyncProgram("""
                class Box {
                    var task: Task<Int>? = none
                }

                class Noisy {
                    let name: String
                    init(_ name: String) { self.name = name }
                    deinit { print("\(name) gone") }
                }

                async fn ticks(_ blocker: Task<Int>) -> some AsyncSequence<Int> {
                    let held = Noisy("generator's")
                    defer { print("ticks left") }
                    yield 1
                    let n = await blocker.value
                    yield n + held.name.count
                }

                fn deep(_ level: Int, _ blocker: Task<Int>) async -> Int {
                    let mine = Noisy("level \(level)")
                    if level == 0 {
                        var total = 0
                        for await t in ticks(blocker) {
                            print("tick \(t)")
                            total += t
                        }
                        return total
                    }
                    let below = await deep(level - 1, blocker)
                    return below + mine.name.count
                }
                """, """
                    let box = Box()
                    let a = Task.detached {
                        let other = box.task!
                        box.task = none
                        return await deep(2, other)
                    }
                    let b = Task.detached {
                        let n = await a.value
                        return n
                    }
                    box.task = b
                    print("main ends")
                """)).Stdout
        );
}
