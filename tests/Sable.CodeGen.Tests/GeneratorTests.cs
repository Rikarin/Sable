using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Generators (spec/SYNTAX.md §5): a body that yields, run a step at a
///     time as its elements are asked for. Native and interpreted, held to
///     the same answers, and to freeing what they allocate — an iterator
///     dropped part-way too.
/// </summary>
public sealed class GeneratorTests {
    static string Repository {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sable.slnx"))) {
                dir = dir.Parent;
            }

            return dir!.FullName;
        }
    }

    [Fact]
    public void TheGeneratorsProgramRuns() {
        var source = File.ReadAllText(Path.Combine(Repository, "tests", "programs", "generators.sb"));
        Assert.Equal(
            "the\nquick\nbrown\nfox\nab ac ba bc ca cb \nhi\nhi\n21\n3\n2\n1\ntrue\n9\nANN\nBOB\none\nx\ny\ny\nz\nz\nx\n",
            AssertAgree(source).Stdout
        );
    }

    [Fact]
    public void NothingRunsUntilTheFirstElementIsAskedFor() =>
        Assert.Equal(
            "made\nstart\n1\nmore\n2\nend\n",
            AssertAgree(
                Program(
                    """
                    fn traced() -> some Sequence<Int> {
                        print("start")
                        yield 1
                        print("more")
                        yield 2
                        print("end")
                    }
                    """,
                    """
                        let s = traced()
                        print("made")
                        for x in s {
                            print(x)
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ReturnEndsAGenerator() =>
        Assert.Equal(
            "1\n2\n",
            AssertAgree(
                Program(
                    """
                    fn upTo(_ n: Int) -> some Sequence<Int> {
                        var i = 1
                        while true {
                            if i > n {
                                return
                            }
                            yield i
                            i += 1
                        }
                    }
                    """,
                    """
                        for x in upTo(2) {
                            print(x)
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ClosuresAndComputedPropertiesYield() =>
        Assert.Equal(
            "0\n2\n4\n4\n4\n10\n11\n14\n5\n6\n",
            AssertAgree(
                """
                    module Test

                    struct Box {
                        let n: Int

                        var evens: some Sequence<Int> {
                            get {
                                var i = 0
                                while i <= n {
                                    yield i
                                    i += 2
                                }
                            }
                        }

                        var plain: some Sequence<Int> {
                            get => [n, n]
                        }
                    }

                    fn main() {
                        let b = Box { n: 4 }
                        for x in b.evens {
                            print(x)
                        }
                        for x in b.plain {
                            print(x)
                        }
                        let base = 10
                        let squares = { () -> some Sequence<Int> in
                            for i in 0..<3 {
                                yield base + i * i
                            }
                        }
                        for x in squares() {
                            print(x)
                        }
                        let shifted: some Sequence<Int> = { (k: Int) -> some Sequence<Int> in
                            yield k
                            yield k + 1
                        }(5)
                        for x in shifted {
                            print(x)
                        }
                    }
                """
            ).Stdout
        );

    [Fact]
    public void GenericGeneratorsAndGeneratorsOverGeneratorsRun() =>
        Assert.Equal(
            "c\nb\na\n0\n4\n8\nn1\nn2\n",
            AssertAgree(
                """
                    module Test

                    struct Stack<T> {
                        var items: T[]

                        fn reversed() -> some Sequence<T> {
                            var i = items.count - 1
                            while i >= 0 {
                                yield items[i]
                                i -= 1
                            }
                        }
                    }

                    extension Sequence where Failure == Never {
                        fn everyOther() -> some Sequence<Element> {
                            var keep = true
                            for x in self {
                                if keep {
                                    yield x
                                }
                                keep = !keep
                            }
                        }
                    }

                    fn evens(upTo n: Int) -> some Sequence<Int> {
                        var i = 0
                        while i <= n {
                            yield i
                            i += 2
                        }
                    }

                    fn doubled(upTo n: Int) -> some Sequence<Int> {
                        for x in evens(upTo: n) {
                            yield x * 2
                        }
                    }

                    fn labelled<T>(_ values: T[], lead: String) -> some Sequence<String> {
                        let make = { (p: String) -> some Sequence<String> in
                            for v in values {
                                yield p + "\(v)"
                            }
                        }
                        for s in make(lead) {
                            yield s
                        }
                    }

                    fn main() {
                        let s = Stack<String> { items: ["a", "b", "c"] }
                        for x in s.reversed() {
                            print(x)
                        }
                        for x in doubled(upTo: 4) {
                            print(x)
                        }
                        for x in labelled([1, 2], lead: "n") {
                            print(x)
                        }
                    }
                """
            ).Stdout
        );

    [Fact]
    public void DefersRunWhenTheBodyEndsAndWhenItsIteratorIsDropped() =>
        Assert.Equal(
            "open a\na 0\nstep 0 done\na 1\nstep 1 done\na 2\nstep 2 done\nend of a\nclose a\nfree a\n--\nopen b\nb 0\nstep 0 done\nclose b\nfree b\n--\nopen c\nc 0\nstep 0 done\nc 1\ndropping\nstep 1 done\nclose c\nfree c\nd made\ne never started\n",
            AssertAgree(
                """
                    module Test

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

                    fn lines(_ name: String) -> some Sequence<String> {
                        let r = Resource(name: name)
                        defer {
                            print("close \(r.name)")
                        }
                        for i in 0..<3 {
                            defer {
                                print("step \(i) done")
                            }
                            yield "\(r.name) \(i)"
                        }
                        print("end of \(name)")
                    }

                    fn main() {
                        for line in lines("a") {
                            print(line)
                        }
                        print("--")
                        for line in lines("b") {
                            print(line)
                            break
                        }
                        print("--")
                        var it = lines("c").makeIterator()
                        print(it.next()!)
                        print(it.next()!)
                        print("dropping")
                        it = lines("d").makeIterator()
                        print("d made")
                        let unused = lines("e")
                        print("e never started")
                    }
                """
            ).Stdout
        );

    [Fact]
    public void AsyncGeneratorsAwaitBetweenElements() =>
        Assert.Equal(
            "0\n10\n20\nother task ran\ntick 0\ntick 10\n",
            AssertAgree(
                """
                    module Test

                    async fn ticks(_ n: Int) -> some AsyncSequence<Int> {
                        for i in 0..<n {
                            await Task.yield()
                            yield i * 10
                        }
                    }

                    async fn relay(_ n: Int) -> some AsyncSequence<String> {
                        for await t in ticks(n) {
                            yield "tick \(t)"
                        }
                    }

                    async fn run() {
                        for await t in ticks(3) {
                            print(t)
                        }
                        let other = Task.spawn {
                            print("other task ran")
                        }
                        for await s in relay(2) {
                            print(s)
                        }
                        await other.value
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
