using System.Text.RegularExpressions;
using Sable.Syntax;

namespace Sable.CodeGen.Tests;

/// <summary>
///     What the threads of a <c>Thread.scope</c> may share with the function
///     around it: each line that shares what it may not says, as the files of
///     tests/negative do — which the library is not there for — the S3236 it
///     is refused with, and there are no others.
/// </summary>
public sealed partial class ThreadScopeTests {
    const string Source = """
        module Negative.ThreadScopes

        import Sable.Concurrency

        // What the threads of a 'Thread.scope' may share with the function around it
        // (docs/06 §6.7): a 'let', by its value; and a 'var' where it is, if it is one
        // thread's alone for the length of the scope.

        class Counter {
            var count: Int = 0
        }

        fn fine(_ given: inout Int) -> Int {
            let numbers = [1, 2, 3]
            var sum = 0
            var texts: String[>..] = []
            Thread.scope { s in
                s.spawn { sum += numbers.count }
                s.spawn { texts.append("\(numbers.count)") }
                s.spawn { given = 1 }
                for n in 0..<3 {
                    s.spawn { numbers[n] }
                }
                if numbers.isEmpty {
                    return
                }
            }
            sum += 1
            Thread.scope { s in
                s.spawn { sum += 1 }
            }
            return sum + texts.count
        }

        fn two() {
            var n = 0
            Thread.scope { s in
                s.spawn { n += 1 }                 //! S3236 cannot share 'n', a 'var' of the code around it: the scope uses it elsewhere
                s.spawn { n += 1 }                 //! S3236 the scope uses it elsewhere, while the thread may be changing it
            }
        }

        fn scopeUsesIt() {
            var n = 0
            Thread.scope { s in
                s.spawn { n += 1 }                 //! S3236 the scope uses it elsewhere, while the thread may be changing it
                print(n)
            }
        }

        fn looped() {
            var n = 0
            Thread.scope { s in
                for _ in 0..<4 {
                    s.spawn { n += 1 }             //! S3236 the thread is spawned in a loop, so that several would change it at once
                }
            }
        }

        fn outlived() {
            Thread.scope { s in
                var n = 0
                s.spawn { n += 1 }                 //! S3236 it ends with the scope's closure, before the thread has
            }
        }

        fn calledFromTheScope() {
            var n = 0
            let more = { n += 1 }
            Thread.scope { s in
                s.spawn { n += 1 }                 //! S3236 another closure of the function uses it, which the scope may call
                more()
            }
        }

        fn furtherOut() {
            var n = 0
            let run = {
                Thread.scope { s in
                    s.spawn { n += 1 }             //! S3236 it is one of a function further out than the one that calls 'Thread.scope'
                }
            }
            run()
        }

        fn onAnotherScope() {
            var n = 0
            Thread.scope { outer in
                Thread.scope { inner in
                    outer.spawn { n += 1 }         //! S3236 the thread is not spawned on a scope in the closure that 'Thread.scope' gave it to
                }
            }
        }

        fn inAHelper(_ s: ThreadScope) {
            var n = 0
            s.spawn { n += 1 }                     //! S3236 the thread is not spawned on a scope in the closure that 'Thread.scope' gave it to
        }

        fn byReference() {
            var n = 0
            let reference: Int& = &n
            Thread.scope { s in
                s.spawn { n += 1 }                 //! S3236 the scope uses 'reference', a reference, which may be to it
                print(reference)
            }
        }

        fn notSendable(_ counter: Counter) {
            var kept = counter
            Thread.scope { s in
                s.spawn { counter.count += 1 }     //! S3236 'Counter' is a class, whose state is shared
                s.spawn { kept = Counter() }       //! S3236 'Counter' is a class, whose state is shared
            }
        }

        // A thread that no scope holds shares no 'var', nor does a loop's body.
        fn unscoped(_ given: inout Int) {
            var n = 0
            let a = Thread.spawn { n += 1 }        //! S3236 'n' is a 'var'
            parallelFor(0..<3) { i in
                n += i                             //! S3236 'n' is a 'var'
            }
            let b = Thread.spawn { given += 1 }    //! S3236 'given' is an 'inout' parameter
            a.join()
            b.join()
        }

        struct Point {
            var x: Int

            mutating fn move() {
                let t = Thread.spawn { x += 1 }    //! S3236 'self' is the value a 'mutating' member changes
                t.join()
            }
        }

        fn chunks() -> Int[>..] {
            var xs: Int[>..] = [1, 2, 3]
            xs.parallelModify { $0 += 1 }
            var kept: ArrayChunk<Int>? = none
            xs.parallelModifyChunks(of: 1) { chunk in
                kept = chunk                       //! S3236 'kept' is a 'var'
            }
            return xs
        }
        """;

    [Fact]
    public void EachLineIsRefusedOrNotAsItSays() {
        var result = Pipeline.Compile(new SourceText(Source, "test.sb"), optimised: false);
        var errors = string.Join("\n", result.Errors);
        var reported = Reports().Matches(errors).Select(static m => (Line: int.Parse(m.Groups[2].Value), Message: m.Groups[1].Value)).Order().ToList();
        var expected = Source.Split('\n').Select(static (text, index) => (Line: index + 1, Claim: text.Split("//! S3236 ")))
            .Where(static l => l.Claim.Length == 2).Select(static l => (l.Line, Message: l.Claim[1].Trim())).ToList();
        Assert.Equal(expected.Select(static e => e.Line), reported.Select(static r => r.Line));
        Assert.All(expected.Zip(reported), static pair => Assert.Contains(pair.First.Message, pair.Second.Message, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"error\[S\d+\]: (.*)\n\s*--> test\.sb:(\d+):")]
    private static partial Regex Reports();
}
