using System.Diagnostics;
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
            "7\na\n10\n21\n12\n1099511627776\ntrue\nfalse\n1.4142135623730951\n-3.0 3.0 3.0 -3.0\n1546998764402558742 1 true\ntrue\n",
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
    var r = SeededRandomNumberGenerator(seed: 42)
    print("\(r.next()) \(Int.random(in: 1..<7, using: &r)) \(F64.random(in: 0.0..<1.0, using: &r) < 1.0)")
    var system = SystemRandomNumberGenerator()
    print(system.next() != system.next() || system.next() != system.next())
}
""").Stdout
        );

    /// <summary>
    ///     The primitives' members are Sable.Core's, in Sable: builtins applied
    ///     in place, and Sable over them — among them the narrower parses, the
    ///     other widths' abs, min and max, and Char.code, which the compiler
    ///     used to declare and could not lower.
    /// </summary>
    [Fact]
    public void ThePrimitivesMembersAreWrittenInSable() =>
        Assert.Equal(
            "3 false 3 2\n42 -1 none -7 200 none 4000000000\n5 3 9 3 1.5 -2.5\ntrue 7 65 A qr\nnone 2.5\n",
            AssertAgree("""
module P
@main
fn main() {
    let t = "  a,b,c  ".trimmed()
    print("\(t.count == 5 ? 3 : 0) \(t.isEmpty) \(t.split(',').count) \(t.indexOf('b') ?? -1)")
    let narrow = "300".toU8() == none ? "none" : "some"
    print("\("42".toInt() ?? 0) \("x".toInt() ?? -1) \(narrow) \("-7".toI8() ?? 0) \("200".toU8() ?? 0) \("-1".toU32() == none ? "none" : "some") \("4000000000".toU64() ?? 0)")
    print("\((-5).abs()) \(I32(3).min(7)) \(I16(9).max(-2)) \(U8(3).abs()) \(F32(1.5).max(0.5)) \((-2.5).min(1.0))")
    print("\(true.toString()) \(Char.fromCodePoint(55) ?? 'z') \('A'.code) \(Char.fromCodePoint(65) ?? 'z') \(String.fromChar('q').appending("r"))")
    let surrogate = Char.fromCodePoint(55296) == none ? "none" : "some"
    print("\(surrogate) \("2.5".toF64() ?? 0.0)")
}
""").Stdout
        );

    /// <summary>
    ///     Characters order by scalar value, an instruction. Sable.Core's
    ///     'operator &lt;' on Char is written 'lhs &lt; rhs', which used to bind to
    ///     itself and never return — directly, through Comparable, and in a
    ///     range's 'contains'.
    /// </summary>
    [Fact]
    public void CharactersAreOrderedByScalar() =>
        Assert.Equal(
            "true false true true false\nz true false true\n",
            AssertAgree("""
module P
fn larger<T: Comparable>(a: T, b: T) -> T {
    return a < b ? b : a
}
@main
fn main() {
    let a = 'a'
    let z = 'z'
    print("\(a < z) \(z < a) \(a <= a) \(z > a) \(a >= z)")
    print("\(larger(a, z)) \(('a'...'z').contains('q')) \(('a'...'z').contains('Q')) \('é' > 'z')")
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
    print("ab".repeated(3))
    print(["a", "b", "c"].joined(separator: ", "))
    print("héllo".reversed())
    print("\("swift".hasPrefix("sw")) \("swift".hasSuffix("ft")) \("haystack".contains("st"))")
    print("banana".count(of: "an"))
    print("a-b-c".replacing("-", with: "+"))
    print(("1,,2".split(",")).joined(separator: "|"))
    print("7".paddedStart(toWidth: 3, with: '0'))
    print("ab".paddedEnd(toWidth: 4, with: '.') + "|")
    print("Hello, World".asciiUppercased() + " " + "ABC".asciiLowercased())
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
    var m: [String: Int] = [:]
    for word in ["to", "be", "or", "not", "to", "be"] {
        m[word] = (m[word] ?? 0) + 1
    }
    print("\(m.count) \(m["to"] ?? 0) \(m["not"] ?? 0) \(m.contains("xyz"))")
    print(m.remove("or") != none)
    var keys = ""
    for (k, _) in m {
        keys = keys + k + " "
    }
    print(keys)
    var unique = Set<Int>()
    print("\(unique.insert(3)) \(unique.insert(3)) \(unique.insert(5)) \(unique.count)")
    let ordered = [5, 3, 9, 1].sorted()
    print("\(ordered[0]) \(ordered[3])")
    let byLength = ["ccc", "a", "bb"].sorted(by: (a: String, b: String) => a.count < b.count)
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
                    let dir = Path.join("{{Platform.Literal(root)}}", "work")
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

    /// <summary>
    ///     URLs parsed, resolved and set, and an internationalised host
    ///     through IDNA and Punycode: the same natively and interpreted.
    /// </summary>
    [Fact]
    public void UrlsAgreeBothWays() {
        var run = AssertAgree(
            """
            module P
            import Sable.Net

            @main
            fn main() {
                let base = try! Url.parse("HTTP://user:pa ss@Bücher.Example:80/a/b/../c?q=1 2#f")
                print(base.href)
                print(try! base.resolve("../d?x#y").href)
                print(try! Url.parse("http://[::FFFF:192.168.0.1]:8080/").hostAndPort)
                print(try! Url.parse("http://0x7f.1/").hostname)
                print(Url.canParse("http://exa mple.com"))
                var url = try! Url.parse("https://example.com/")
                url.setQueryItems([QueryItem("k", "v w")])
                print(url.href)
                print(Idna.toUnicode("xn--bcher-kva.example").text)
            }
            """
        );
        Assert.Equal(
            "http://user:pa%20ss@xn--bcher-kva.example/a/c?q=1%202#f\nhttp://user:pa%20ss@xn--bcher-kva.example/d?x#y\n[::ffff:c0a8:1]:8080\n127.0.0.1\nfalse\nhttps://example.com/?k=v+w\nbücher.example\n",
            run.Stdout
        );
    }

    /// <summary>
    ///     A promise, a continuation, a timeout, a race, a token and the
    ///     tasks waited for as they finish: the same natively and
    ///     interpreted, actors and detached tasks between them.
    /// </summary>
    [Fact]
    public void CancellationAndCombinatorsAgreeBothWays() {
        var run = AssertAgree(
            """
            module P
            import Sable.Concurrency
            import Sable.Time

            @main
            fn main() async {
                let promise = Promise<Int, Never>()
                let waiter = Task.spawn { await promise.value() }
                print(promise.succeed(5))
                print(await waiter.value)
                let resumed = await withContinuation(returning: String.self) { continuation in
                    let _ = Task.detached { continuation.resume(returning: "back") }
                }
                print(resumed)
                do {
                    let _ = try await withTimeout(Duration.milliseconds(5)) {
                        try await Task.sleep(for: Duration.seconds(60))
                        return 0
                    }
                } catch let error as TimeoutError {
                    print("timed out")
                } catch {
                    print("other")
                }
                print(try! await race({ "first" }, {
                    try await Task.sleep(for: Duration.seconds(60))
                    return "second"
                }))
                let source = CancellationTokenSource()
                let _ = source.token.register { print("cancelled") }
                source.cancel()
                let a = Task.spawn { 1 }
                let b = Task.spawn { 2 }
                var total = 0
                for await done in whenEach([a, b]) {
                    total += done.value
                }
                print(total)
            }
            """
        );
        Assert.Equal("true\n5\nback\ntimed out\nfirst\ncancelled\n3\n", run.Stdout);
    }

    /// <summary>
    ///     The environment written and read back, the known folders by each
    ///     system's rules, and the platform as the program and the compiler
    ///     name it: the same natively and interpreted, where set and remove
    ///     write the program's own variables, never the compiler's.
    /// </summary>
    [Fact]
    public void TheEnvironmentAndPlatformAgreeBothWays() {
        var run = AssertAgree(
            """
            module P
            import Sable.IO

            @main
            fn main() {
                unsafe {
                    Environment.set("SABLE_AGREE_VALUE", "a=b")
                }
                print(Environment.variable("SABLE_AGREE_VALUE") ?? "-")
                print(Environment.variables()["SABLE_AGREE_VALUE"] ?? "-")
                unsafe {
                    Environment.remove("SABLE_AGREE_VALUE")
                }
                print(Environment.variable("SABLE_AGREE_VALUE") ?? "unset")
                var variables = Dictionary<String, String>()
                variables["XDG_DATA_HOME"] = "/data"
                let home = FilePath("/home/x", style: .Posix)
                print(Environment.knownFolder(.Data, on: .Linux, home: home, variables: variables)!)
                print(Environment.knownFolder(.Cache, on: .MacOS, home: home, variables: variables)!)
                print(Platform.operatingSystem.name == Platform.os && Platform.architecture.name == Platform.arch)
                print(Platform.processorCount > 0 && Platform.pageSize > 0 && !Platform.userName.isEmpty)
                print(Environment.temporaryDirectory.isAbsolute && Environment.currentDirectory.isAbsolute)
                print(OperatingSystemVersion.parse("10.0.22631").patch)
            }
            """
        );
        Assert.Equal("a=b\na=b\nunset\n/data\n/home/x/Library/Caches\ntrue\ntrue\ntrue\n22631\n", run.Stdout);
        Assert.Null(Environment.GetEnvironmentVariable("SABLE_AGREE_VALUE"));
    }

    /// <summary>
    ///     TextBuilder's appends — text, characters and integers into its
    ///     byte buffer — and its edits in the middle, and a byte array's
    ///     append(utf8:): the same natively and interpreted.
    /// </summary>
    [Fact]
    public void TextBuildingAgreesBothWays() {
        var run = AssertAgree(
            """
            module P
            import Sable.Text

            @main
            fn main() {
                let builder = TextBuilder()
                builder.append("héllo ")
                builder.append('ж')
                builder.append(-42)
                builder.append(U64.max)
                builder.insert("<", at: 1)
                builder.remove(0..<1)
                builder[0] = 'H'
                print(builder.replace("l", with: "L"))
                builder.truncate(to: 12)
                print(builder.build())
                print("\(builder.count) \(builder.utf8Count) \(builder[3])")
                var bytes: U8[>..] = []
                bytes.append(utf8: "añb", from: 1, to: 3)
                bytes.append(utf8: "!")
                print(bytes.count)
            }
            """
        );
        Assert.Equal("2\nHéLLo ж-4218\n12 14 L\n3\n", run.Stdout);
    }

    /// <summary>
    ///     A file mapped to read, to write through and to write a private
    ///     copy of, JSON parsed from its bytes, and bytes passed through a
    ///     pipe: the same natively, where the mapping is the system's, and
    ///     interpreted, where a copy stands in for it.
    /// </summary>
    [Fact]
    public void MappedFilesAndPipesAgreeBothWays() {
        var root = Directory.CreateTempSubdirectory("sable-mapped-").FullName;
        try {
            var run = AssertAgree(
                $$"""
                module P
                import Sable.IO
                import Sable.Serialize

                @main
                fn main() async {
                    let path = "{{Platform.Literal(root)}}/m.json"
                    try! File.write(path, "[1, 2, 3]")
                    let reading = try! MappedFile.open(path)
                    print(render(try! parse(bytes: reading.bytes)))
                    var copy = reading.bytes
                    copy[0] = 0x7B
                    print(reading.bytes[0] == 0x5B)
                    do {
                        try reading.write([0x20], at: 0)
                    } catch {
                        print("read-only")
                    }
                    reading.close()

                    let writing = try! MappedFile.open(path, mode: .ReadWrite)
                    try! writing.write([0x39], at: 1)
                    try! writing.flush()
                    try! writing.resize(to: 11)
                    try! writing.write([0x2C, 0x34], at: 9)
                    try! writing.write([0x5D], at: 10)
                    try! writing.write([0x20], at: 8)
                    try! writing.flush()
                    writing.close()
                    print(try! File.read(path))

                    let copied = try! MappedFile.open(path, mode: .CopyOnWrite)
                    try! copied.write([0x38], at: 1)
                    print(copied.bytes[1] == 0x38)
                    copied.close()
                    print(try! File.read(path))

                    let pipe = Pipe(pauseWriterThreshold: 8, resumeWriterThreshold: 4, segmentSize: 3)
                    let writer = Task.spawn {
                        var i = 0
                        while i < 20 {
                            try! await pipe.writer.write([U8.truncating(i), 10])
                            i += 1
                        }
                        await pipe.writer.complete()
                    }
                    var total = 0
                    while true {
                        let bytes = try! await pipe.reader.read(upTo: 5)
                        if bytes.isEmpty {
                            break
                        }
                        for byte in bytes {
                            total += Int(byte)
                        }
                    }
                    await writer.value
                    print(total)
                }
                """
            );
            Assert.Equal("[1,2,3]\ntrue\nread-only\n[9, 2, 3 ,]\ntrue\n[9, 2, 3 ,]\n390\n", run.Stdout);
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    ///     A path resolved by the system — through a link to a directory —
    ///     the same natively (realpath) and interpreted, and a missing one
    ///     an error both ways.
    /// </summary>
    [PosixFact("a link to a directory needs no privilege on POSIX, but on Windows does")]
    public void ACanonicalPathResolvesLinksBothWays() {
        var root = Directory.CreateTempSubdirectory("sable-canonical-").FullName;
        try {
            Directory.CreateDirectory(Path.Combine(root, "real"));
            File.WriteAllText(Path.Combine(root, "real", "f.txt"), "x");
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "real"));
            var run = AssertAgree(
                $$"""
                module P
                import Sable.IO

                @main
                fn main() {
                    let resolved = try! FilePath("{{Platform.Literal(root)}}/link/./f.txt").canonical()
                    let real = try! FilePath("{{Platform.Literal(root)}}/real").canonical()
                    print(resolved.parent! == real)
                    print(resolved.fileName ?? "-")
                    print(resolved.isAbsolute)
                    do {
                        let _ = try FilePath("{{Platform.Literal(root)}}/missing").canonical()
                    } catch .NotFound(_) {
                        print("not found")
                    } catch {
                        print("other")
                    }
                }
                """
            );
            Assert.Equal("true\nf.txt\ntrue\nnot found\n", run.Stdout);
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    ///     A file's bytes need not be UTF-8, but a String's are: each
    ///     ill-formed sequence (its longest start that could have been well
    ///     formed, as Unicode recommends) reads as U+FFFD, and a byte-order mark is just
    ///     U+FEFF — not a request for UTF-16, and not dropped.
    /// </summary>
    [Fact]
    public void AFileOfIllFormedUtf8ReadsAsReplacementCharacters() {
        var path = Path.Combine(Path.GetTempPath(), $"sable-utf8-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, 0xFF, 0xFE, 0x41, 0xE2, 0x82, 0x41, 0xC3, 0xA9, 0xED, 0xA0, 0x80, 0xF4, 0x90, 0x80, 0x80]);
        try {
            var run = AssertAgree(
                $$"""
                module P
                import Sable.IO

                @main
                fn main() {
                    let text = try! File.read("{{Platform.Literal(path)}}")
                    print(text.count)
                    var i = 0
                    while i < text.count {
                        Console.write("\(text.at(i).codePoint) ")
                        i += 1
                    }
                    Console.writeLine("")
                }
                """
            );
            Assert.Equal(
                "14\n65279 65533 65533 65 65533 65 233 65533 65533 65533 65533 65533 65533 65533 \n",
                run.Stdout
            );
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     The same for a line, an argument and an environment variable, which
    ///     only a shell can hand a program as raw bytes.
    /// </summary>
    [PosixFact("a Windows program's arguments and environment are UTF-16, and its input is read by a shell this test does not have: nothing can hand it ill-formed UTF-8 there")]
    public void IllFormedUtf8FromOutsideReadsAsReplacementCharacters() {
        if (!CanBuild) {
            return;
        }

        var result = Pipeline.Compile(new SourceText(
            """
            module P
            import Sable.IO

            fn show(_ text: String) {
                var i = 0
                while i < text.count {
                    Console.write("\(text.at(i).codePoint) ")
                    i += 1
                }
                Console.writeLine("")
            }

            @main
            fn main() {
                show(Console.readLine() ?? "")
                show(Environment.arguments()[0])
                show(Environment.variable("SABLE_BYTES") ?? "")
            }
            """,
            "test.sb"
        ));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            Assert.True(NativeBuild.Build(result.LlvmIr!, executable, optimise: true).Succeeded);
            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("""printf 'x\377\376A\n' | SABLE_BYTES="$(printf '\360\220\200')z" "$0" "$(printf 'a\300\257b')" """);
            start.ArgumentList.Add(executable);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("120 65533 65533 65 \n97 65533 65533 98 \n65533 65533 65533 122 \n", stdout);
        } finally {
            Platform.DeleteExecutable(executable);
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
                fn main() async {
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
                    if let leap = try? DateTime.parse("2024-02-29T23:59:59Z") {
                        print(leap.secondsSinceEpoch)
                        print(leap.adding(Duration.seconds(1)).iso8601())
                        print(leap.weekday)
                    }
                    print((try? DateTime.parse("2023-02-29")) == none)
                    print((try? DateTime.parse("1600-03-01"))?.isoDate() ?? "-")
                    let a = DateTime.of(year: 2000, month: 1, day: 1, hour: 0, minute: 0, second: 0) ?? epoch
                    let b = DateTime.of(year: 2026, month: 11, day: 17, hour: 12, minute: 30, second: 0) ?? epoch
                    print(b.durationSince(a).toString())
                    print(a < b)
                    print(isLeapYear(1900))
                    print(daysInMonth(2024, 2))
                    let start = Instant.now()
                    try? await Clock.sleep(Duration.milliseconds(20))
                    print(start.elapsed >= Duration.milliseconds(20))
                    let took = Clock.measure({ Runtime.sleep(nanoseconds: 5_000_000) })
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
        var result = Pipeline.Compile(new SourceText("module H\nimport Sable.Time\n@main\nfn main() {\n    let d: Duration = 5\n}\n", "test.sb"));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, static e => e.Contains("test.sb:5:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, static e => e.Contains("lib/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Sable.Process: a program run to its end with its input given and its
    ///     output, errors and status kept; a name that is nothing; the directory.
    /// </summary>
    /// <remarks>
    ///     The programs are the system's own: a POSIX shell's tools, and on
    ///     Windows cmd's and find's, where the root a program can change to is
    ///     the current drive's.
    /// </remarks>
    [Fact]
    public void ProcessIsWrittenInSable() {
        // By their paths on Windows: a POSIX 'find' may come first on PATH.
        var cmd = Platform.Literal(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        var find = Platform.Literal(Path.Combine(Environment.SystemDirectory, "find.exe"));
        var (echo, count, fail, root) = OperatingSystem.IsWindows()
            ? ($$"""Process.run("{{cmd}}", ["/c", "echo hello world"])""", $$"""Process.run("{{find}}", ["/c", "/v", ""], input: "a\r\nb\r\nc\r\n")""",
                $$"""Process.run("{{cmd}}", ["/c", "echo oops 1>&2 & exit 3"])""", Path.GetPathRoot(Environment.CurrentDirectory)!)
            : ("""Process.run("echo", ["hello", "world"])""", """Process.run("wc", ["-l"], input: "a\nb\nc\n")""",
                """Process.run("sh", ["-c", "echo oops >&2; exit 3"])""", "/");
        Assert.Equal(
            $"hello world\ntrue\n3\n3\noops\n0\nnot found: surely-not-a-program-here\ntrue\n{root}\nfalse\ntrue\ntrue\n",
            AssertAgree($$"""
                module P
                import Sable.Process

                @main
                fn main() {
                    let echoed = try! {{echo}}
                    print(echoed.output.trimmed())
                    print(echoed.succeeded)
                    let counted = try! {{count}}
                    print(counted.output.trimmed())
                    let failing = try! {{fail}}
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
    }

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

    /// <summary>Sable.Serialize's JSON beside the other modules: no names clash, and a document round-trips.</summary>
    [Fact]
    public void JsonIsWrittenInSable() =>
        Assert.Equal(
            "{\"name\":\"Sable\",\"tags\":[\"fast\",\"safe\"],\"version\":4}\n4.0\nsafe\n{\n  \"name\": \"Sable\",\n  \"tags\": [\n    \"fast\",\n    \"safe\"\n  ],\n  \"version\": 4\n}\nerror\n",
            AssertAgree("""
                module P
                import Sable.Serialize
                import Sable.Text
                import Sable.Collections

                @main
                fn main() {
                    let document = try! parse("{\"name\": \"Sable\", \"tags\": [\"fast\", \"safe\"], \"version\": 4}")
                    print(render(document))
                    print(asNumber(field(document, "version") ?? null()) ?? 0.0)
                    print(asText(try! resolve(document, "/tags/1")) ?? "-")
                    print(renderWith(document, pretty(2)))
                    do {
                        let _ = try parse("{\"open\": ")
                        print("parsed")
                    } catch {
                        print("error")
                    }
                    let words = Stack<String>()
                    words.push(["a", "b"].joined(separator: "-"))
                }
                """).Stdout
        );

    /// <summary>Sable.Diagnostics: levels, a threshold, context fields, and values quoted where they would be misread.</summary>
    [Fact]
    public void LoggingIsWrittenInSable() {
        var run = AssertAgree("""
                module P
                import Sable.Diagnostics

                @main
                fn main() {
                    let log = Logger("app")
                    log.keepLines()
                    log.debug("hidden")
                    log.info("started", [field("port", 8080), field("tls", false)])
                    let request = log.with([field("id", "r-1")])
                    request.keepLines()
                    request.warning("slow", [field("path", "/a b"), field("note", "say \"hi\"")])
                    log.threshold = Level.Debug
                    log.debug("now shown")
                    log.error("")
                    print(log.lines().count)
                    print(request.lines()[0])
                    print(parseLevel("warn") != none)
                    print(log.isEnabled(Level.Trace))
                }
                """);
        Assert.Equal("3\nWARN app: slow id=r-1 path=\"/a b\" note=\"say \\\"hi\\\"\"\ntrue\nfalse\n", run.Stdout);
        Assert.Equal(
            "INFO app: started port=8080 tls=false\nWARN app: slow id=r-1 path=\"/a b\" note=\"say \\\"hi\\\"\"\nDEBUG app: now shown\nERROR app: \n",
            run.Stderr
        );
    }

    /// <summary>
    ///     Sable.Concurrency over the core's tasks: a channel between a producer
    ///     and a consumer, a task group, and a sleep other tasks run through.
    /// </summary>
    [Fact]
    public void ConcurrencyIsWrittenInSable() =>
        Assert.Equal(
            "sent 0\ngot 0\nsent 1\ngot 1\nsent 2\ngot 4\nsent 3\ngot 9\nsum 14 of 4\ntrue\n30\ntrue\ntrue\n",
            AssertAgree("""
                module P
                import Sable.Concurrency
                import Sable.Time

                fn produce(_ channel: Channel<Int>, _ count: Int) async -> Int {
                    for i in 0..<count {
                        await channel.send(i * i)
                        print("sent \(i)")
                        await yieldNow()
                    }
                    await channel.close()
                    return count
                }

                fn consume(_ channel: Channel<Int>) async -> Int {
                    var total = 0
                    while let value = await channel.receive() {
                        print("got \(value)")
                        total += value
                    }
                    return total
                }

                fn square(_ n: Int) async -> Int {
                    await yieldNow()
                    return n * n
                }

                @main
                fn main() async {
                    let channel = Channel<Int>()
                    let consumer = Task.spawn { await consume(channel) }
                    let producer = Task.spawn { await produce(channel, 4) }
                    print("sum \(await consumer.value) of \(await producer.value)")
                    print(await channel.tryReceive() == none)

                    let squares = await withTaskGroup(of: Int.self) { group in
                        for i in 1...4 {
                            group.spawn { await square(i) }
                        }
                        var total = 0
                        for await square in group {
                            total += square
                        }
                        return total
                    }
                    print(squares)

                    let start = Instant.now()
                    let stop = Channel<Int>()
                    let ticker = Task.spawn {
                        var ticks = 0
                        while !(await stop.isClosed) {
                            ticks += 1
                            await yieldNow()
                        }
                        return ticks > 0
                    }
                    try? await sleep(Duration.milliseconds(10))
                    await stop.close()
                    print(start.elapsed >= Duration.milliseconds(10))
                    print(await ticker.value)
                }
                """, environment: OneThread).Stdout
        );

    /// <summary>
    ///     A task spawned in a loop: forward-copies once folded the copy that
    ///     task_spawn consumes into the task itself, which was then used after.
    /// </summary>
    [Fact]
    public void ATaskSpawnedInALoopIsStillUsableAfterwards() =>
        Assert.Equal(
            "3\n0 1 4\n",
            AssertAgree("""
                module P
                fn square(_ n: Int) async -> Int {
                    await Task.yield()
                    return n * n
                }
                @main
                fn main() async {
                    var tasks: Task<Int>[>..] = []
                    for i in 0..<3 {
                        tasks.append(Task.spawn { await square(i) })
                    }
                    print(tasks.count)
                    print("\(await tasks[0].value) \(await tasks[1].value) \(await tasks[2].value)")
                }
                """).Stdout
        );

    /// <summary>joined is one runtime call, sized once: none, one and many parts, and a separator of any width.</summary>
    [Fact]
    public void JoiningIsLinear() =>
        Assert.Equal(
            "[]\n[a]\n[a·b·é]\n15000 4999\nxyz\n",
            AssertAgree("""
                module P
                import Sable.Text
                @main
                fn main() {
                    let empty: String[>..] = []
                    print("[" + empty.joined(separator: "·") + "]")
                    print("[" + ["a"].joined(separator: "·") + "]")
                    print("[" + ["a", "b", "é"].joined(separator: "·") + "]")
                    var many: String[>..] = []
                    for i in 0..<5000 {
                        many.append("abc")
                    }
                    let all = many.joined(separator: "·")
                    print("\(all.count - 4999) \(all.count - 15000)")
                    let builder = TextBuilder()
                    builder.append("x")
                    builder.append("y")
                    builder.append("z")
                    print(builder.build())
                }
                """).Stdout
        );

    /// <summary>Only what a program reaches is built: the library costs a program that does not use it nothing.</summary>
    [Fact]
    public void WhatAProgramDoesNotUseIsNotBuilt() {
        var result = Pipeline.Compile(new SourceText("module H\nimport Sable.Collections\n@main\nfn main() {\n    print(1)\n}\n", "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.DoesNotContain("Sable.Collections.Set", result.LlvmIr!, StringComparison.Ordinal);
        Assert.DoesNotContain("Sable.Core.Int.<", result.LlvmIr!, StringComparison.Ordinal);
    }
}
