using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Throwing iteration (spec/SYNTAX.md §18): a generator declared
///     <c>throws(E)</c>, a hand-written iterator whose <c>next()</c> throws,
///     and what goes through them — <c>for try</c>, generic code over
///     <c>S.Failure</c>, Sequence's algorithms and lazy views. Native and
///     interpreted, held to the same answers and to freeing what they hold,
///     an iterator left by an error too.
/// </summary>
public sealed class ThrowingIterationTests {
    const string Declarations = """
                                union ReadError: Error { Broken(at: Int) }

                                class Resource {
                                    let name: String

                                    init(name: String) {
                                        self.name = name
                                        print("open \(name)")
                                    }

                                    deinit {
                                        print("free \(name)")
                                    }
                                }

                                fn read(_ name: String, failAt: Int) throws(ReadError) -> some Sequence<String> {
                                    let r = Resource(name: name)
                                    defer {
                                        print("close \(r.name)")
                                    }
                                    var i = 0
                                    while i < 3 {
                                        if i == failAt {
                                            throw .Broken(at: i)
                                        }
                                        yield "\(name) \(i)"
                                        i += 1
                                    }
                                }

                                fn counts(failAt: Int) throws -> some Sequence<Int> {
                                    for i in 0..<4 {
                                        if i == failAt {
                                            throw ReadError.Broken(at: i)
                                        }
                                        yield i
                                    }
                                }
                                """;

    [Fact]
    public void AGeneratorsErrorComesOutOfNextAndFinishesIt() =>
        Assert.Equal(
            "open a\na 0\na 1\nclose a\nfree a\ncaught Broken at 2\n--\nopen b\nb 0\nb 1\nb 2\nclose b\nfree b\n--\nopen c\nc 0\nclose c\nfree c\nthrew\nnone after\nnone again\n",
            AssertAgree(
                Program(
                    Declarations,
                    """
                        do {
                            for try line in read("a", failAt: 2) {
                                print(line)
                            }
                        } catch .Broken(let at) {
                            print("caught Broken at \(at)")
                        }
                        print("--")
                        do {
                            for try line in read("b", failAt: 9) {
                                print(line)
                            }
                        } catch {
                            print("not reached")
                        }
                        print("--")
                        var it = read("c", failAt: 1).makeIterator()
                        print((try? it.next())!!)
                        if (try? it.next()) == none {
                            print("threw")
                        }
                        if let after = try? it.next(), after == none {
                            print("none after")
                        }
                        if let again = try? it.next(), again == none {
                            print("none again")
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AnErrorLeavesTheLoopToTheFunctionsCaller() =>
        Assert.Equal(
            "0\n1\nfirst: ReadError.Broken(at: 2)\n0\n1\n2\n3\nsum 6\n",
            AssertAgree(
                $$"""
                    module Test
                    {{Declarations}}

                    fn sum(failAt: Int) throws -> Int {
                        var total = 0
                        for try n in counts(failAt: failAt) {
                            print(n)
                            total += n
                        }
                        return total
                    }

                    fn main() {
                        do {
                            let _ = try sum(failAt: 2)
                        } catch {
                            print("first: \(error)")
                        }
                        let all = try? sum(failAt: 9)
                        print("sum \(all!)")
                    }
                """
            ).Stdout
        );

    [Fact]
    public void GenericCodeAndAlgorithmsThrowWhatTheSequenceDoes() =>
        Assert.Equal(
            "6\n6\n[0, 2, 4, 6]\nmap: ReadError.Broken(at: 1)\n[1, 2, 3]\ncount: ReadError.Broken(at: 3)\n10\n20\nlazy: ReadError.Broken(at: 3)\n[0, 1, 2, 3]\n6\nzip 7\nzip 9\nzip: ReadError.Broken(at: 2)\n",
            AssertAgree(
                Program(
                    $$"""
                    {{Declarations}}

                    fn total<S: Sequence>(_ values: S) throws(S.Failure) -> Int where S.Element == Int {
                        var sum = 0
                        for try value in values {
                            sum += value
                        }
                        return sum
                    }
                    """,
                    """
                        print(total([1, 2, 3]))
                        print((try? total(counts(failAt: 9)))!)
                        print("\((try? counts(failAt: 9).map { $0 * 2 })!)")
                        do {
                            let _ = try counts(failAt: 1).map { $0 * 2 }
                        } catch {
                            print("map: \(error)")
                        }
                        print("\([0, 1, 2].map { $0 + 1 })")
                        do {
                            let _ = try counts(failAt: 3).count(where: { $0 > 0 })
                        } catch {
                            print("count: \(error)")
                        }
                        do {
                            for try n in counts(failAt: 3).lazy.map({ $0 * 10 }).filter({ $0 > 0 }) {
                                print(n)
                            }
                        } catch {
                            print("lazy: \(error)")
                        }
                        let collected: Int[>..] = try! counts(failAt: 9).collect()
                        print("\(collected)")
                        print((try? counts(failAt: 9).reduce(0) { $0 + $1 })!)
                        do {
                            for try (a, b) in zip(counts(failAt: 2), [7, 8, 9]) {
                                print("zip \(a + b)")
                            }
                        } catch {
                            print("zip: \(error)")
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AHandWrittenIteratorThatThrowsIsGoneThroughWithForTry() =>
        Assert.Equal(
            "3\n2\nfailed: ReadError.Broken(at: 1)\n",
            AssertAgree(
                Program(
                    """
                    union ReadError: Error { Broken(at: Int) }

                    struct Countdown: Sequence {
                        let from: Int
                        fn makeIterator() -> CountdownIterator { return CountdownIterator { left: from } }
                    }

                    struct CountdownIterator: Iterator {
                        var left: Int
                        mutating fn next() throws(ReadError) -> Int? {
                            if left == 1 {
                                left = 0
                                throw .Broken(at: 1)
                            }
                            if left == 0 {
                                return none
                            }
                            left -= 1
                            return left + 1
                        }
                    }
                    """,
                    """
                        do {
                            for try n in Countdown(from: 3) {
                                print(n)
                            }
                        } catch {
                            print("failed: \(error)")
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AnErrorFromAWhileLetsSourceDropsTheValueBoundBefore() =>
        Assert.Equal(
            "s 1\ns 2\ncaught\n",
            AssertAgree(
                Program(
                    """
                    union ReadError: Error { Broken(at: Int) }

                    struct Lines {
                        var i: Int
                        mutating fn next() throws(ReadError) -> String? {
                            i += 1
                            if i == 3 {
                                throw .Broken(at: i)
                            }
                            return "s \(i)"
                        }
                    }
                    """,
                    """
                        var lines = Lines { i: 0 }
                        do {
                            while let line = try lines.next() {
                                print(line)
                            }
                        } catch {
                            print("caught")
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AnAsyncGeneratorThatThrowsIsGoneThroughWithForTryAwait() =>
        Assert.Equal(
            "0\n10\ncaught ReadError.Broken(at: 2)\n",
            AssertAgree(
                """
                    module Test

                    union ReadError: Error { Broken(at: Int) }

                    async fn ticks(_ n: Int) throws(ReadError) -> some AsyncSequence<Int> {
                        for i in 0..<n {
                            await Task.yield()
                            if i == 2 {
                                throw .Broken(at: i)
                            }
                            yield i * 10
                        }
                    }

                    async fn run() {
                        do {
                            for try await t in ticks(5) {
                                print(t)
                            }
                        } catch {
                            print("caught \(error)")
                        }
                    }

                    fn main() {
                        let task = Task.spawn {
                            await run()
                        }
                    }
                """
            ).Stdout
        );
}
