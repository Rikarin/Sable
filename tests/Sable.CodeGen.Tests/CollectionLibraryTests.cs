using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Collections beyond the first four types (docs/04 §4.3, docs/06
///     §6.4): Deque, PriorityQueue, OrderedDictionary and BitSet, the in-place
///     sort, and the dictionary and String hashing the benchmarks against .NET
///     reworked. Native and interpreted, with leak checking.
/// </summary>
public sealed class CollectionLibraryTests {
    [Fact]
    public void DequeAddsAndRemovesAtBothEnds() =>
        Assert.Equal(
            "20 f9 b9 f6\nf9 b9 18\nzero f7 f6 f5 f4 f3 f2 f1 f0 b0 b1 b2 b3 b4 b5 b6 b7 b8 \n0 none none true\n3 4662\n",
            AssertAgree("""
                module P
                import Sable.Collections
                @main
                fn main() {
                    let d = Deque<String>()
                    var i = 0
                    while i < 10 {
                        d.append("b\(i)")
                        d.prepend("f\(i)")
                        i = i + 1
                    }
                    print("\(d.count) \(d.first ?? "-") \(d.last ?? "-") \(d.at(3))")
                    print("\(d.removeFirst() ?? "-") \(d.removeLast() ?? "-") \(d.count)")
                    d.set(0, "zero")
                    var line = ""
                    for s in d.elements() { line = line + s + " " }
                    print(line)
                    d.removeAll()
                    print("\(d.count) \(d.removeFirst() ?? "none") \(d.last ?? "none") \(d.isEmpty)")

                    // Round the end of the slots and back: a queue that never holds many.
                    let ring = Deque<Int>([1, 2, 3])
                    var total = 0
                    i = 0
                    while i < 100 {
                        ring.append(i)
                        total = total + (ring.removeFirst() ?? 0)
                        i = i + 1
                    }
                    print("\(ring.count) \(total)")
                }
                """).Stdout
        );

    /// <summary>
    ///     A removed element is released as it is removed, not when its slot is
    ///     next written: 'b', dropped by the caller, goes before 'end'.
    /// </summary>
    [Fact]
    public void DequeReleasesWhatItRemoves() =>
        Assert.Equal(
            "got a\ngone b\nend\ngone a\n",
            AssertAgree("""
                module P
                import Sable.Collections
                class Loud {
                    var name: String
                    init(name: String) { self.name = name }
                    deinit { print("gone " + name) }
                }
                @main
                fn main() {
                    let d = Deque<Loud>()
                    d.append(Loud(name: "a"))
                    d.append(Loud(name: "b"))
                    if let first = d.removeFirst() {
                        print("got " + first.name)
                    }
                    d.removeLast()
                    print("end")
                }
                """).Stdout
        );

    [Fact]
    public void DequeIndexOutsideItsElementsTraps() {
        var run = AssertAgree("""
            module P
            import Sable.Collections
            @main
            fn main() {
                let d = Deque<Int>([1, 2, 3, 4, 5, 6, 7, 8])
                print(d.at(7))
                print(d.at(8))
            }
            """);
        Assert.Equal("8\n", run.Stdout);
        Assert.Contains("array index out of range", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void PriorityQueueGivesTheFirstByItsOrdering() =>
        Assert.Equal(
            "1 1 3 5 7 9 \nzoo 3 apple\nc:1 a:2 b:3 \n0\n",
            AssertAgree("""
                module P
                import Sable.Collections
                struct Job: Comparable {
                    var name: String
                    var priority: Int
                    operator == (a: Job, b: Job) -> Bool { return a.priority == b.priority }
                    operator < (a: Job, b: Job) -> Bool { return a.priority < b.priority }
                }
                @main
                fn main() {
                    let q = PriorityQueue<Int>()
                    for v in [5, 1, 9, 3, 7, 1] { q.push(v) }
                    var line = ""
                    while let v = q.pop() { line = line + "\(v) " }
                    print(line)

                    let big = PriorityQueue<String>(by: (a: String, b: String) => a > b)
                    big.push("pear")
                    big.push("apple")
                    big.push("zoo")
                    print("\(big.peek() ?? "-") \(big.count) \(big.sortedElements()[2])")

                    let jobs = PriorityQueue<Job>()
                    jobs.push(Job { name: "a", priority: 2 })
                    jobs.push(Job { name: "b", priority: 3 })
                    jobs.push(Job { name: "c", priority: 1 })
                    line = ""
                    while let job = jobs.pop() { line = line + "\(job.name):\(job.priority) " }
                    print(line)
                    print(jobs.count)
                }
                """).Stdout
        );

    /// <summary>Many pushes and pops against a sorted copy: the heap keeps its order through every sift.</summary>
    [Fact]
    public void PriorityQueueAgreesWithSorting() =>
        Assert.Equal(
            "true 500\n",
            AssertAgree("""
                module P
                import Sable.Collections
                @main
                fn main() {
                    let q = PriorityQueue<Int>(by: (a: Int, b: Int) => a > b)
                    var all: Int[>..] = []
                    var x = 7
                    var i = 0
                    while i < 500 {
                        x = (x * 1103515245 + 12345) % 2147483648
                        q.push(x % 1000)
                        all.append(x % 1000)
                        i = i + 1
                    }
                    let expected = sorted(all, by: (a: Int, b: Int) => a > b)
                    var same = true
                    i = 0
                    while let v = q.pop() {
                        if v != expected[i] { same = false }
                        i = i + 1
                    }
                    print("\(same) \(i)")
                }
                """).Stdout
        );

    [Fact]
    public void OrderedDictionaryKeepsInsertionOrder() =>
        Assert.Equal(
            "2 2 three 1 11\none three four \n11 3 4 \nnone false 0\n",
            AssertAgree("""
                module P
                import Sable.Collections
                @main
                fn main() {
                    let o = OrderedDictionary<String, Int>()
                    o.set("one", 1)
                    o.set("two", 2)
                    o.set("three", 3)
                    o.set("one", 11)
                    print("\(o.remove("two") ?? 0) \(o.count) \(o.key(at: 1)) \(o.index(of: "three") ?? -1) \(o.get("one") ?? 0)")
                    o.set("four", 4)
                    var line = ""
                    for k in o.keys() { line = line + k + " " }
                    print(line)
                    line = ""
                    var i = 0
                    while i < o.count {
                        line = line + "\(o.value(at: i)) "
                        i = i + 1
                    }
                    print(line)
                    o.removeAll()
                    print("\(o.get("one") == none ? "none" : "some") \(o.contains("four")) \(o.count)")
                }
                """).Stdout
        );

    [Fact]
    public void BitSetHoldsSmallIntegers() =>
        Assert.Equal(
            "true false true false 4\n6 1 3 true false\n1 5 6 64 130 200 \ntrue 3 false\n",
            AssertAgree("""
                module P
                import Sable.Collections
                @main
                fn main() {
                    let b = BitSet([1, 64, 130])
                    print("\(b.insert(5)) \(b.insert(5)) \(b.contains(64)) \(b.contains(63)) \(b.count)")
                    let c = BitSet([5, 6, 200])
                    let u = b.union(c)
                    let n = b.intersection(c)
                    print("\(u.count) \(n.count) \(b.subtracting(c).count) \(n.isSubset(of: b)) \(b.isSubset(of: n))")
                    var line = ""
                    for e in u.elements() { line = line + "\(e) " }
                    print(line)
                    print("\(b.remove(64)) \(b.count) \(b.contains(-1))")
                }
                """).Stdout
        );

    [Fact]
    public void SortIsInPlaceAndStable() =>
        Assert.Equal(
            "0 1 2 3 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 \nfig pear kiwi date lime apple elder grape mango banana cherry orange papaya quince honeydew nectarine raspberry \ntrue true\n",
            AssertAgree("""
                module P
                import Sable.Collections
                @main
                fn main() {
                    var xs: Int[>..] = [5, 3, 9, 1, 3, 8, 2, 7, 6, 4, 0, 11, 15, 13, 12, 14, 10, 19, 18, 17, 16]
                    sort(&xs)
                    var line = ""
                    for x in xs { line = line + "\(x) " }
                    print(line)
                    let words = sorted(["pear", "apple", "fig", "kiwi", "banana", "cherry", "date", "elder", "grape", "honeydew", "lime", "mango", "nectarine", "orange", "papaya", "quince", "raspberry"], by: (a: String, b: String) => a.count < b.count)
                    line = ""
                    for w in words { line = line + w + " " }
                    print(line)

                    // Enough for several merge passes, and the original left as it was.
                    var many: Int[>..] = []
                    var x = 3
                    var i = 0
                    while i < 1000 {
                        x = (x * 1103515245 + 12345) % 2147483648
                        many.append(x % 100)
                        i = i + 1
                    }
                    let ordered = sorted(many)
                    var ascending = true
                    var sumBefore = 0
                    var sumAfter = 0
                    i = 0
                    while i < 1000 {
                        if i > 0 && ordered[i] < ordered[i - 1] { ascending = false }
                        sumBefore = sumBefore + many[i]
                        sumAfter = sumAfter + ordered[i]
                        i = i + 1
                    }
                    print("\(ascending) \(sumBefore == sumAfter && many[0] != ordered[0])")
                }
                """).Stdout
        );

    /// <summary>
    ///     The merge takes from the left run on a tie, without a branch: a
    ///     thousand values with ten keys stay in their first order within a key
    ///     through every pass, Ints and Strings both — the Strings counted out
    ///     and back by the leak check.
    /// </summary>
    [Fact]
    public void SortStaysStableThroughEveryMergePass() =>
        Assert.Equal(
            "true true 1000\ntrue true 1000\n",
            AssertAgree("""
                module P
                import Sable.Collections
                @main
                fn main() {
                    var keyed: Int[>..] = []
                    var tagged: String[>..] = []
                    var x = 11
                    var i = 0
                    while i < 1000 {
                        x = (x * 1103515245 + 12345) % 2147483648
                        keyed.append(x % 10 * 10000 + i)
                        tagged.append("\(x % 10)\(1000 + i)")
                        i = i + 1
                    }

                    sort(&keyed, by: (a: Int, b: Int) => a / 10000 < b / 10000)
                    var ordered = true
                    var stable = true
                    i = 1
                    while i < keyed.count {
                        if keyed[i] / 10000 < keyed[i - 1] / 10000 { ordered = false }
                        if keyed[i] / 10000 == keyed[i - 1] / 10000 && keyed[i] < keyed[i - 1] { stable = false }
                        i = i + 1
                    }
                    print("\(ordered) \(stable) \(keyed.count)")

                    // The key is the first digit; the four after it, the first index.
                    let byKey = sorted(tagged, by: (a: String, b: String) => a.slice(from: 0, to: 1) < b.slice(from: 0, to: 1))
                    ordered = true
                    stable = true
                    i = 1
                    while i < byKey.count {
                        let here = byKey[i]
                        let before = byKey[i - 1]
                        if here.slice(from: 0, to: 1) < before.slice(from: 0, to: 1) { ordered = false }
                        if here.slice(from: 0, to: 1) == before.slice(from: 0, to: 1) && here < before { stable = false }
                        i = i + 1
                    }
                    print("\(ordered) \(stable) \(byKey.count)")
                }
                """).Stdout
        );

    /// <summary>
    ///     Growth with removals in between, keys that differ only above bit 31,
    ///     and long string keys — which overflowed the hash written in Sable.
    /// </summary>
    [Fact]
    public void ADictionarySurvivesGrowthAndRemoval() =>
        Assert.Equal(
            "666 332667 false true\na b false 2\n2 true 9998\nx y \n",
            AssertAgree("""
                module P
                @main
                fn main() {
                    var m: [String: Int] = [:]
                    var i = 0
                    while i < 1000 {
                        m["a rather long key, number \(i)"] = i
                        i = i + 1
                    }
                    i = 0
                    while i < 1000 {
                        if i % 3 == 0 { m.remove("a rather long key, number \(i)") }
                        i = i + 1
                    }
                    var sum = 0
                    for (_, v) in m { sum = sum + v }
                    print("\(m.count) \(sum) \(m.contains("a rather long key, number 3")) \(m.contains("a rather long key, number 4"))")

                    var wide: [Int: String] = [:]
                    wide[1] = "a"
                    wide[4294967297] = "b"
                    wide[-1] = "c"
                    wide.remove(-1)
                    print("\(wide[1] ?? "-") \(wide[4294967297] ?? "-") \(wide.contains(2)) \(wide.count)")

                    // Removing and adding the same keys over and over reuses the removed slots.
                    var churn: [Int: Int] = [:]
                    i = 0
                    while i < 10000 {
                        churn[i % 2] = i
                        churn.remove(i % 2)
                        churn[i % 2] = i
                        i = i + 1
                    }
                    print("\(churn.count) \(churn.contains(1)) \(churn[0] ?? -1)")

                    let small: [String: Int] = ["x": 1, "y": 2]
                    var line = ""
                    for (k, _) in small { line = line + k + " " }
                    print(line)
                }
                """).Stdout
        );

    /// <summary>String hashing and ordering are the runtime's: the same natively and interpreted, and by scalar.</summary>
    [Fact]
    public void StringsHashAndOrderTheSameEverywhere() =>
        Assert.Equal(
            "true true true true\ntrue false\nfalse true\n",
            AssertAgree("""
                module P
                @main
                fn main() {
                    print("\("abc" < "abd") \("ab" < "abc") \("b" > "abc") \("é" > "z")")
                    print("\("abc".hashValue == "abc".hashValue) \("abc".hashValue == "abd".hashValue)")
                    print("\("same" < "same") \("a long string, past eight bytes".hashValue == "a long string, past eight bytes".hashValue)")
                }
                """).Stdout
        );

    /// <summary>
    ///     A hash under a given seed is the same number natively and
    ///     interpreted: the runtime's string hash and the interpreter's agree,
    ///     at every length it reads differently (none, under four bytes,
    ///     under eight, up to sixteen, more), and so do the Hasher's words.
    /// </summary>
    [Fact]
    public void AHashUnderASeedIsTheSameNumberEverywhere() {
        var run = AssertAgree("""
            module P
            fn hashOf(_ text: String) -> Int {
                var hasher = Hasher(seed: 12345)
                hasher.combine(text)
                return hasher.finalize()
            }
            @main
            fn main() {
                print(hashOf(""))
                print(hashOf("ab"))
                print(hashOf("seven b"))
                print(hashOf("twelve bytes"))
                print(hashOf("exactly sixteen!"))
                print(hashOf("héllo, wörld — and more"))
                var words = Hasher(seed: 0)
                words.combine(-1)
                words.combine(true)
                words.combine(2.5)
                print(words.finalize())
            }
            """);
        var lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, lines.Length);
        Assert.Equal(lines.Length, lines.Distinct().Count());
    }

    /// <summary>
    ///     Without a seed of its own, a hasher takes the process's: random,
    ///     so two runs differ, unless SABLE_DETERMINISTIC_HASHING=1 fixes it.
    /// </summary>
    [Fact]
    public void TheProcessSeedIsRandomUnlessFixed() {
        if (!CanBuild) {
            return;
        }

        const string source = """
            module P
            @main
            fn main() {
                print("seeded".hashValue)
            }
            """;
        // Unless the tests themselves run with fixed hashing.
        if (Environment.GetEnvironmentVariable("SABLE_DETERMINISTIC_HASHING") != "1") {
            var first = BuildAndRun(source).Stdout;
            var second = BuildAndRun(source).Stdout;
            Assert.NotEqual(first, second);
        }

        var fixedFirst = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_DETERMINISTIC_HASHING"] = "1" }).Stdout;
        var fixedSecond = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_DETERMINISTIC_HASHING"] = "1" }).Stdout;
        Assert.Equal(fixedFirst, fixedSecond);
    }

    /// <summary>
    ///     The runtime's string paths the benchmarks reworked: interpolations
    ///     and chains of '+' joined at once, integers written by hand, splits
    ///     on ASCII and non-ASCII separators, and slices of either kind of string.
    /// </summary>
    [Fact]
    public void StringsAreJoinedSplitAndSlicedTheSameEverywhere() =>
        Assert.Equal(
            "-9223372036854775808 0 -7 42|a-b-c-é|7 3\n[] [a] [] [b] []\n[x] [y z]\n3 é|ab|éa\nhéllo, wörld 12\n",
            AssertAgree("""
                module P
                fn piece(_ s: String) -> String { return "-" + s }
                @main
                fn main() {
                    let low = -9223372036854775807 - 1
                    let name = "é"
                    let joined = "a" + piece("b") + piece("c") + "-" + name
                    print("\(low) \(0) \(-7) \(42)|\(joined)|\(joined.count) \(name.count + 2)")
                    var line = ""
                    for part in ",a,,b,".split(',') { line = line + "[" + part + "] " }
                    print(line.trimmed())
                    line = ""
                    for part in "xéy z".split('é') { line = line + "[" + part + "] " }
                    print(line.trimmed())
                    let mixed = "éab"
                    print("\(mixed.count) \(mixed.slice(from: 0, to: 1))|\(mixed.slice(from: 1, to: 3))|\(mixed.slice(from: 0, to: 1) + "a")")
                    let greeting = "héllo" + ", " + "wörld"
                    print("\(greeting) \(greeting.count)")
                }
                """).Stdout
        );

    /// <summary>Large arrays built, dropped and built again reuse the memory the last one let go.</summary>
    [Fact]
    public void LargeArraysComeAndGo() =>
        Assert.Equal(
            "1499998500000\n",
            AssertAgree("""
                module P
                @main
                fn main() {
                    var total = 0
                    var round = 0
                    while round < 3 {
                        var values: Int[>..] = []
                        var i = 0
                        while i < 1000000 {
                            values.append(i)
                            i = i + 1
                        }
                        for v in values { total = total + v }
                        round = round + 1
                    }
                    print(total)
                }
                """, stepLimit: 100_000_000).Stdout
        );
}
