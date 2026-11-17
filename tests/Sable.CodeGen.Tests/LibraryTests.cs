using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The standard library (docs/04 §4.3), written in Sable in lib/ and
///     compiled with the programs that import it: Sable.Core's protocols,
///     Sable.Math, Sable.Text, Sable.Collections. Native and interpreted.
/// </summary>
public sealed class LibraryTests {
    [Fact]
    public void MathIsWrittenInSable() =>
        Assert.Equal(
            "7\na\n10\n21\n12\n1099511627776\ntrue\nfalse\n1.414213562373095\n-3 3 3 -3\n2027381 3 true\n",
            AssertAgree("""
module P
import Sable.Math
@main
fn main() {
    print(max(3, 7))
    print(min("b", "a"))
    print(clamp(15, 0, 10))
    print(gcd(1071, 462))
    print(lcm(4, 6))
    print(power(2, 40))
    print(isPrime(97))
    print(isPrime(91))
    print(squareRoot(2.0))
    print("\(floor(-2.5)) \(ceiling(2.1)) \(round(2.5)) \(round(-2.5))")
    var r = Random(seed: 42)
    print("\(r.next()) \(r.next(from: 1, to: 7)) \(r.nextUnit() < 1.0)")
}
""").Stdout
        );

    [Fact]
    public void TextIsWrittenInSable() =>
        Assert.Equal(
            "ababab\na, b, c\nolléh\ntrue true true\n2\na+b+c\n1||2\n007\nab..|\nHELLO, WORLD abc\nxy\nz\n",
            AssertAgree("""
module P
import Sable.Text
@main
fn main() {
    print(repeating("ab", 3))
    print(joined(["a", "b", "c"], separator: ", "))
    print(reversed("héllo"))
    print("\(hasPrefix("swift", "sw")) \(hasSuffix("swift", "ft")) \(contains("haystack", "st"))")
    print(find("banana", "nan") ?? -1)
    print(replacing("a-b-c", "-", with: "+"))
    print(joined(splitting("1,,2", by: ","), separator: "|"))
    print(paddedLeft("7", to: 3, with: '0'))
    print(paddedRight("ab", to: 4, with: '.') + "|")
    print(uppercased("Hello, World") + " " + lowercased("ABC"))
    let b = TextBuilder()
    b.append("x")
    b.appendLine("y")
    b.append("z")
    print(b.build())
}
""").Stdout
        );

    [Fact]
    public void CollectionsAreWrittenInSable() =>
        Assert.Equal(
            "2 1\na b 1\n4 2 1 false\ntrue\nto be not \ntrue false true 2\n1 9\naccc\n",
            AssertAgree("""
module P
import Sable.Collections

@main
fn main() {
    let s = Stack<Int>()
    s.push(1)
    s.push(2)
    print("\(s.pop() ?? 0) \(s.count)")
    let q = Queue<String>()
    q.enqueue("a")
    q.enqueue("b")
    q.enqueue("c")
    print("\(q.dequeue() ?? "") \(q.dequeue() ?? "") \(q.count)")
    let m = HashMap<String, Int>()
    for word in ["to", "be", "or", "not", "to", "be"] {
        m.set(word, (m.get(word) ?? 0) + 1)
    }
    print("\(m.count) \(m.get("to") ?? 0) \(m.get("not") ?? 0) \(m.contains("xyz"))")
    print(m.remove("or"))
    var keys = ""
    for k in m.allKeys() {
        keys = keys + k + " "
    }
    print(keys)
    let unique = HashSet<Int>()
    print("\(unique.insert(3)) \(unique.insert(3)) \(unique.insert(5)) \(unique.count)")
    let ordered = sorted([5, 3, 9, 1])
    print("\(ordered[0]) \(ordered[3])")
    let byLength = sorted(["ccc", "a", "bb"], by: (a: String, b: String) => a.count < b.count)
    print(byLength[0] + byLength[2])
}
""").Stdout
        );

    [Fact]
    public void GenericCodeComparesThroughTheCoreProtocols() =>
        Assert.Equal(
            "9\nzoo\ntrue\nfalse\n",
            AssertAgree("""
                module P
                fn biggest<T: Comparable>(_ values: T[>..]) -> T? {
                    if values.count == 0 { return none }
                    var best = values[0]
                    for v in values {
                        if v > best { best = v }
                    }
                    return best
                }
                fn has<T: Equatable>(_ values: T[>..], _ wanted: T) -> Bool {
                    for v in values {
                        if v == wanted { return true }
                    }
                    return false
                }
                struct Money: Equatable {
                    var cents: Int
                    operator == (a: Money, b: Money) -> Bool { return a.cents == b.cents }
                }
                @main
                fn main() {
                    print(biggest([3, 9, 2]) ?? 0)
                    print(biggest(["pear", "apple", "zoo"]) ?? "")
                    print(has([Money { cents: 5 }], Money { cents: 5 }))
                    print(has([Money { cents: 5 }], Money { cents: 6 }))
                }
                """).Stdout
        );

    /// <summary>
    ///     Sable.IO over the runtime's calls: files, directories, paths, the
    ///     standard streams and the environment — the same answers natively
    ///     and interpreted, from the same directory and the same input.
    /// </summary>
    [Fact]
    public void IoIsWrittenInSable() {
        var root = Path.Combine(Path.GetTempPath(), $"sable-io-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try {
            var run = AssertAgree(
                $$"""
                module P
                import Sable.IO

                @main
                fn main() {
                    let dir = Path.join("{{root}}", "work")
                    if Directory.exists(dir) {
                        for name in try! Directory.list(dir) {
                            try! File.remove(Path.join(dir, name))
                        }
                        try! Directory.remove(dir)
                    }
                    try! Directory.create(dir)
                    let notes = Path.join(dir, "notes.txt")
                    try! File.write(notes, "one\ntwo\r\n")
                    try! File.append(notes, "three\n")
                    print(try! File.read(notes).count)
                    for line in try! File.readLines(notes) {
                        Console.writeLine("[" + line + "]")
                    }
                    try! File.write(Path.join(dir, "b.md"), "")
                    for name in try! Directory.list(dir) {
                        Console.write(name + " ")
                    }
                    Console.writeLine("")
                    print(File.exists(notes))
                    print(Directory.exists(notes))
                    do {
                        print(try File.read(Path.join(dir, "missing")))
                    } catch .NotFound(let path) {
                        print("not found: " + Path.fileName(path))
                    } catch {
                        print("other")
                    }
                    print(Path.extension("a/b.tar.gz") ?? "-")
                    print(Path.extension(".hidden") ?? "-")
                    print(Path.withoutExtension("x/y/report.md"))
                    print(Path.directory("x/y/report.md"))
                    print(Path.directory("report.md"))
                    print(Environment.variable("SABLE_SURELY_UNSET") ?? "unset")
                    print(Environment.arguments().count)
                    var total = 0
                    while let line = Console.readLine() {
                        total += line.toInt() ?? 0
                    }
                    print(total)
                    Console.writeErrorLine("done")
                }
                """,
                input: "1\n2\n39"
            );
            Assert.Equal(
                "15\n[one]\n[two]\n[three]\nb.md notes.txt \ntrue\nfalse\nnot found: missing\ngz\n-\nx/y/report\nx/y\n.\nunset\n0\n42\n",
                run.Stdout
            );
            Assert.Equal("done\n", run.Stderr);
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadingAnArgumentThatIsNotThereTraps() {
        var run = AssertAgree("""
            module P
            @main
            fn main() {
                print(Runtime.argumentCount())
                print(Runtime.argument(0))
            }
            """);
        Assert.Equal("0\n", run.Stdout);
        Assert.Contains("argument index out of range", run.Stderr, StringComparison.Ordinal);
    }

    /// <summary>Sable.Time: durations, the monotonic clock, sleeping, and UTC dates either side of the epoch.</summary>
    [Fact]
    public void TimeIsWrittenInSable() =>
        Assert.Equal(
            "1m 30.250s\n1.500s\n3.250ms\n17ns\n2h 0m 0.000s\n-12.000ms\ntrue\n90\n1970-01-01T00:00:00Z\n4\n2026-11-18T11:06:40Z\n322\n1969-12-31T23:59:59Z\n2000-02-29T00:00:00Z\n1709251199\n2024-03-01T00:00:00Z\n4\ntrue\n1600-03-01\n235620h 30m 0.000s\ntrue\nfalse\n29\ntrue\ntrue\ntrue\n",
            AssertAgree("""
                module P
                import Sable.Time

                @main
                fn main() {
                    let d = Duration.seconds(90) + Duration.milliseconds(250)
                    print(d.toString())
                    print(Duration.milliseconds(1500).toString())
                    print(Duration.microseconds(3250).toString())
                    print(Duration.nanoseconds(17).toString())
                    print(Duration.hours(2).toString())
                    print((Duration.zero() - Duration.milliseconds(12)).toString())
                    print(d > Duration.minutes(1))
                    print(d.totalSeconds)
                    let epoch = DateTime.fromUnix(0)
                    print(epoch.iso8601())
                    print(epoch.weekday)
                    let t = DateTime.fromUnix(1795000000)
                    print(t.iso8601())
                    print(t.dayOfYear)
                    print(DateTime.fromUnix(-1).iso8601())
                    print(DateTime.fromUnix(951782400).iso8601())
                    if let leap = DateTime.parse("2024-02-29T23:59:59Z") {
                        print(leap.secondsSinceEpoch)
                        print(leap.adding(Duration.seconds(1)).iso8601())
                        print(leap.weekday)
                    }
                    print(DateTime.parse("2023-02-29") == none)
                    print(DateTime.parse("1600-03-01")?.isoDate() ?? "-")
                    let a = DateTime.of(year: 2000, month: 1, day: 1, hour: 0, minute: 0, second: 0) ?? epoch
                    let b = DateTime.of(year: 2026, month: 11, day: 17, hour: 12, minute: 30, second: 0) ?? epoch
                    print(b.durationSince(a).toString())
                    print(a < b)
                    print(isLeapYear(1900))
                    print(daysInMonth(2024, 2))
                    let start = Instant.now()
                    Clock.sleep(Duration.milliseconds(20))
                    print(start.elapsed >= Duration.milliseconds(20))
                    let took = Clock.measure({ Clock.sleep(Duration.milliseconds(5)) })
                    print(took >= Duration.milliseconds(5))
                    print(DateTime.now().year >= 2026)
                }
                """).Stdout
        );

    /// <summary>'?.' reaches a field or a method through any optional, not only a local's.</summary>
    [Fact]
    public void OptionalChainingReachesThroughACallsResult() =>
        Assert.Equal(
            "6\n8\n-1\n",
            AssertAgree("""
                module P
                struct Box {
                    var n: Int
                    fn twice() -> Int { return n * 2 }
                    static fn make(_ ok: Bool) -> Box? {
                        if ok { return Box { n: 4 } }
                        return none
                    }
                }
                @main
                fn main() {
                    let b: Box? = Box { n: 3 }
                    print(b?.twice() ?? -1)
                    print(Box.make(true)?.twice() ?? -1)
                    print(Box.make(false)?.n ?? -1)
                }
                """).Stdout
        );

    /// <summary>An error in the library is reported in the library's file, whatever its offset.</summary>
    [Fact]
    public void AnErrorInTheLibraryIsNotPlacedInTheProgram() {
        var result = Pipeline.Compile(new SourceText("module H\nimport Sable.Time\n@main\nfn main() {\n    let d: Duration = 5\n}\n", "test.sab"));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, static e => e.Contains("test.sab:5:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, static e => e.Contains("lib/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Sable.Process: a program run to its end with its input given and its
    ///     output, errors and status kept; a name that is nothing; the directory.
    /// </summary>
    [Fact]
    public void ProcessIsWrittenInSable() =>
        Assert.Equal(
            "hello world\ntrue\n3\n3\noops\n0\nnot found: surely-not-a-program-here\ntrue\n/\nfalse\ntrue\ntrue\n",
            AssertAgree("""
                module P
                import Sable.Process

                @main
                fn main() {
                    let echoed = try! Process.run("echo", ["hello", "world"])
                    print(echoed.output.trimmed())
                    print(echoed.succeeded)
                    let counted = try! Process.run("wc", ["-l"], input: "a\nb\nc\n")
                    print(counted.output.trimmed())
                    let failing = try! Process.run("sh", ["-c", "echo oops >&2; exit 3"])
                    print(failing.status)
                    print(failing.errors.trimmed())
                    print(failing.output.count)
                    do {
                        let _ = try Process.run("surely-not-a-program-here", [])
                        print("ran")
                    } catch .NotFound(let name) {
                        print("not found: " + name)
                    }
                    let start = Process.currentDirectory()
                    print(Process.changeDirectory("/"))
                    print(Process.currentDirectory())
                    print(Process.changeDirectory("/surely/not/here"))
                    print(Process.changeDirectory(start))
                    print(Process.id() > 0)
                }
                """).Stdout
        );

    /// <summary>'[]' as an argument takes its type from the parameter, overloaded or not.</summary>
    [Fact]
    public void AnEmptyArrayArgumentTakesTheParametersType() =>
        Assert.Equal(
            "0\n2\n",
            AssertAgree("""
                module P
                fn f(_ a: Int[>..]) -> Int { return a.count }
                fn g(_ name: String, _ a: String[>..]) -> Int { return a.count }
                fn g(_ name: String, _ a: String[>..], extra: Int) -> Int { return a.count + extra }
                @main
                fn main() {
                    print(f([]))
                    print(g("x", [], extra: 2))
                }
                """).Stdout
        );

    /// <summary>Only what a program reaches is built: the library costs a program that does not use it nothing.</summary>
    [Fact]
    public void WhatAProgramDoesNotUseIsNotBuilt() {
        var result = Pipeline.Compile(new SourceText("module H\nimport Sable.Collections\n@main\nfn main() {\n    print(1)\n}\n", "test.sab"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.DoesNotContain("HashMap", result.LlvmIr!, StringComparison.Ordinal);
        Assert.DoesNotContain("Sable.Core.Int.<", result.LlvmIr!, StringComparison.Ordinal);
    }
}
