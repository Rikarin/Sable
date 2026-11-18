using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>for</c> over what the compiler does not know by sight: a
///     <c>Sequence</c>, through its iterator, a string's characters, and
///     <c>for await</c> over an <c>AsyncSequence</c> (spec/SYNTAX.md §14, §19).
///     Native and interpreted, held to the same answers.
/// </summary>
public sealed class IterationTests {
    const string Countdown = """
                             struct Countdown: Sequence {
                                 alias Iter = CountdownIterator
                                 let from: Int

                                 fn makeIterator() -> CountdownIterator {
                                     return CountdownIterator { current: from }
                                 }
                             }

                             struct CountdownIterator: Iterator {
                                 alias Element = Int
                                 var current: Int

                                 mutating fn next() -> Int? {
                                     if current <= 0 {
                                         return none
                                     }
                                     current = current - 1
                                     return current + 1
                                 }
                             }

                             fn countdown(_ n: Int) -> Countdown {
                                 return Countdown { from: n }
                             }
                             """;

    [Fact]
    public void AUserSequenceIsGoneThroughByItsIterator() =>
        Assert.Equal(
            "3\n1\nlast 2\nb5 b4\n3\n",
            AssertAgree(
                Program(
                    Countdown,
                    """
                        for i in countdown(3) where i != 2 {
                            print(i)
                        }
                        for i in countdown(5) {
                            if i == 2 {
                                print("last \(i)")
                                break
                            }
                        }
                        var line = ""
                        outer: for c in ["a", "b"] {
                            for d in countdown(5) {
                                if c == "a" { continue outer }
                                if d == 3 { break outer }
                                line = line + "\(c)\(d) "
                            }
                        }
                        print(line.trimmed())
                        var count = 0
                        for _ in countdown(3) {
                            count = count + 1
                        }
                        print(count)
                    """
                )
            ).Stdout
        );

    [Fact]
    public void GenericCodeGoesThroughAnySequence() =>
        Assert.Equal(
            "10\n3\n",
            AssertAgree(
                Program(
                    Countdown
                    + """

                      fn total<S: Sequence>(_ items: S) -> Int where S.Iter.Element == Int {
                          var sum = 0
                          for x in items {
                              sum = sum + x
                          }
                          return sum
                      }

                      fn count<S: Sequence>(_ items: S) -> Int {
                          var n = 0
                          for _ in items {
                              n = n + 1
                          }
                          return n
                      }
                      """,
                    """
                        print(total(countdown(4)))
                        print(count("h\u{E9}!"))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AStringGivesItsCharacters() =>
        Assert.Equal(
            "h\né\nl\n\U0001F600\n4\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let text = "h\u{E9}l\u{1F600}"
                        for c in text {
                            print(c)
                        }
                        var n = 0
                        for c in text where c != 'x' {
                            n = n + 1
                        }
                        print(n)
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ForAwaitReceivesFromAChannelUntilItCloses() =>
        Assert.Equal(
            "got 10\ngot 30\n40\n",
            AssertAgree(
                Program(
                    """
                    import Sable.Concurrency

                    fn produce(_ channel: Channel<Int>) async {
                        for i in 1...3 {
                            channel.send(i * 10)
                            await Task.yield()
                        }
                        channel.close()
                    }

                    fn consume(_ channel: Channel<Int>) async -> Int {
                        var total = 0
                        for await value in channel where value != 20 {
                            print("got \(value)")
                            total = total + value
                        }
                        return total
                    }
                    """,
                    """
                        let channel = Channel<Int>()
                        let producer = Task.spawn { await produce(channel) }
                        print(await consume(channel))
                        await producer.value
                    """
                ).Replace("fn main() {", "fn main() async {", StringComparison.Ordinal)
            ).Stdout
        );
}
