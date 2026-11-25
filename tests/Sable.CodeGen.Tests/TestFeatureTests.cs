using System.Xml.Linq;
using Sable.Driver.Commands;
using Sable.Symbols;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     What a test may say beyond an assertion (#73): expectations that show
///     their expression, arguments, throws, deaths, snapshots, properties,
///     time limits, tags and filters, and the reports a run is written as.
/// </summary>
[Collection("Console")]
public sealed class TestFeatureTests {
    static Run Runs(string declarations, params string[] arguments) =>
        Native($"module M\nimport Sable.Testing\n{declarations}\n", arguments: [..arguments]);

    static void Reports(string declarations, params string[] expected) {
        if (!CanBuild) {
            return;
        }

        var run = Runs(declarations);
        Assert.Equal(1, run.ExitCode);
        foreach (var text in expected) {
            Assert.True(run.Stdout.Contains(text.ReplaceLineEndings("\n"), StringComparison.Ordinal), $"expected:\n{text}\nin:\n{run.Stdout}\n{run.Stderr}");
        }

        Assert.DoesNotContain("try!' caught", run.Stdout, StringComparison.Ordinal);
    }

    /// <summary>The run passes, and, checked for leaks, says nothing on standard error.</summary>
    static void Passes(string declarations) {
        if (!CanBuild) {
            return;
        }

        var run = Runs(declarations);
        Assert.True(run.ExitCode == 0, run.Stdout + run.Stderr);
        Assert.Equal("", run.Stderr);
    }

    [Fact]
    public void AnExpectationShowsItsExpressionAndGoesOn() => Reports(
        """
        @test
        fn expects() {
            let names = ["a", "b"]
            expect(names.count == 3)
            expect(names.isEmpty
                || names[0] == "z", "a message")
        }
        """,
        """
            expectation failed: names.count == 3 (test.sb:6)
                the values differ
                   left: 2
                  right: 3
                         ^ first difference at character 0
                names: ["a", "b"]
            expectation failed: names.isEmpty || names[0] == "z": a message (test.sb:7)
                names: ["a", "b"]
                names.isEmpty: false
                names[0]: "a"
                names[0] == "z": false

        """);

    [Fact]
    public void ARequirementEndsTheTest() => Reports(
        """
        fn maybe(_ n: Int) -> Int? { return n > 0 ? n : none }
        @test
        fn requires() {
            let v = require(maybe(1))
            require(v == 2)
            print("not come to")
        }
        @test
        fn noValue() { print("got \(require(maybe(0)))") }
        """,
        "    expectation failed: v == 2 (test.sb:7)\n",
        "    expectation failed: maybe(0): there is no value (test.sb:11)\n");

    /// <summary>The parts are worked out once, in order, as without the capture; what is not evaluated is not told.</summary>
    [Fact]
    public void TheCaptureChangesNothing() => Passes(
        """
        union Oops: Error { Bad }
        final class Counter { var n: Int = 0
            fn next() -> Int { n += 1
                return n } }
        @noncopy
        struct Handle { var id: Int = 7 }
        fn half(_ n: Int) throws(Oops) -> Int { if n % 2 == 1 { throw .Bad }
            return n / 2 }
        async fn later(_ n: Int) -> Int { await Task.yield()
            return n }
        struct S { var xs: Int[>..]
            mutating fn take() -> Int { return xs.removeLast() }
            fn check() { expect(xs.count == 2 && self.xs[0] == 1) } }
        fn generic<T: Equatable>(_ a: T, _ b: T) { expect(a == b) }

        @test
        async fn captures() throws(Oops) {
            let counter = Counter()
            expect(counter.next() == 1)
            expect(counter.next() == 2 || counter.next() == 99)
            expect(counter.n == 2)
            let handle = Handle()
            expect(handle.id == 7)
            expect(try half(4) == 2)
            expect((try? half(3)) == none)
            expect(await later(5) == 5)
            expect(try half(await later(6)) == 3)
            var s = S(xs: [1, 2, 3])
            expect(s.take() == 3)
            s.check()
            let maybe: Int? = counter.n
            if maybe != none {
                expect(maybe + 1 == 3)
            }
            let text: String? = none
            expect(text?.count == none)
            expect((text ?? "ab").count == 2)
            expect(s.xs.map({ $0 * 2 }) == [2, 4])
            expect(s.xs[0..<1].count == 1)
            expect(s is S)
            generic("a", "a")
            generic([1], [1])
            expect(counter === counter)
            expect(!(s.xs.isEmpty ? true : s.xs.contains(9)))
        }
        """);

    [Fact]
    public void AnExpressionIsADefaultOfOneTypeAlone() {
        Assert.Contains(ErrorsOf("module M\nfn f(_ a: Int, _ e: String = #expression) { }\n"), static e => e.Contains("[S3517]", StringComparison.Ordinal));
        Assert.Contains(ErrorsOf("module M\nfn f() { let x = #expression }\n"), static e => e.Contains("[S3517]", StringComparison.Ordinal));
        Assert.Empty(ErrorsOf("module M\nimport Sable.Testing\nfn f(_ a: Int, _ e: CapturedExpression = #expression) -> String { return e.text }\nfn g() -> String { return f(1 + 2) }\n@main fn main() { }\n"));
    }

    [Fact]
    public void EachCaseIsATestOfItsOwn() {
        if (!CanBuild) {
            return;
        }

        const string program = """
            enum Colour { Red, Green }
            fn made() -> (Int, String)[>..] { return [(1, "one"), (2, "two")] }
            @test(arguments: [1, 2, 2])
            fn one(_ n: Int) { expect(n < 2) }
            @test(arguments: made())
            fn pairs(_ n: Int, named s: String) { expect(s.count == 3) }
            @test(arguments: [.Red, .Green], [true, false])
            async fn crossed(_ c: Colour, _ b: Bool) { await Task.yield() }
            struct S {
                static fn all() -> Int?[>..] { return [none, 4] }
                @test(arguments: all(), tags: ["slow"])
                static fn member(_ n: Int?) throws(String) { if n == none { throw "none" } }
            }
            private fn zero() -> Int[>..] { return [0] }
            @test(arguments: zero(), traps: "division")
            private fn traps(_ n: Int) { print(1 / n) }
            """;
        var run = Runs(program);
        Assert.Equal(1, run.ExitCode);
        foreach (var (name, outcome) in new[] {
                     ("one(1)", "ok"), ("one(2)", "FAILED"), ("one(2)#2", "FAILED"), ("pairs(1, \"one\")", "ok"), ("pairs(2, \"two\")", "ok"),
                     ("crossed(Colour.Red, true)", "ok"), ("crossed(Colour.Green, false)", "ok"), ("S.member(none)", "FAILED"), ("S.member(4)", "ok"), ("traps(0)", "ok"),
                 }) {
            Assert.Contains($"test M.{name} ... {outcome} (", run.Stdout, StringComparison.Ordinal);
        }

        Assert.Contains("9 passed; 3 failed; 0 filtered out", run.Stdout, StringComparison.Ordinal);

        Assert.Equal("M.one(2): test (test.sb:6)\nM.one(2)#2: test (test.sb:6)\n2 tests\n", Runs(program, "--list", "one(2").Stdout);
        Assert.Equal("M.pairs(1, \"one\"): test (test.sb:8)\n1 test\n", Runs(program, "--list", "--exact", "M.pairs(1, \"one\")").Stdout);
        Assert.EndsWith("\n4 tests\n", Runs(program, "--list", "--exact", "M.crossed").Stdout, StringComparison.Ordinal);
        Assert.EndsWith("\n2 tests\n", Runs(program, "--list", "--filter", "tag:slow").Stdout, StringComparison.Ordinal);
        Assert.Equal(2, Runs(program, "--filter", "tag:slow &").ExitCode);
    }

    [Fact]
    public void ArgumentsAreOfWhatTheTestTakes() {
        static void Rejects(string code, string test) =>
            Assert.Contains(ErrorsOf($"module M\nimport Sable.Testing\n{test}\n"), e => e.Contains($"[{code}]", StringComparison.Ordinal));

        Rejects("S3100", "@test(arguments: [1])\nfn t() { }");
        Rejects("S3100", "@test(arguments: [1], [2], [3])\nfn t(_ a: Int, _ b: Int) { }");
        Rejects("S3100", "@test(arguments: [1])\nfn t(_ a: inout Int) { }");
        Rejects("S3004", "@test(arguments: 7)\nfn t(_ a: Int) { }");
        Rejects("S2002", "@test(arguments: nothing)\nfn t(_ a: Int) { }");
        Rejects("S3107", "@test(tags: [\"two words\"])\nfn t() { }");
        Rejects("S3107", "@test(timeout: 5)\nfn t() { }");
        Rejects("S3107", "@test(timeout: .seconds(1), timeout: .seconds(2))\nfn t() { }");
        Rejects("S3107", "@test(unknown: 5)\nfn t() { }");
        Assert.Empty(ErrorsOf("module M\n@test(arguments: [1, 2], timeout: .milliseconds(1_500), tags: [\"a\", \"b-c\"], traps)\nfn t(_ a: Int) { }\n@main fn main() { }\n"));
    }

    [Fact]
    public void WhatIsThrown() => Reports(
        """
        @derive(Equatable, Debug)
        union Oops { Bad
        Worse(Int) }
        fn f(_ n: Int) throws(Oops) { if n > 0 { throw .Worse(n) } }
        @test
        async fn throwing() {
            expect(expectThrows(Oops.self) { try f(2) } == Oops.Worse(2))
            expectThrows(Oops.Worse(3)) { try f(3) }
            expectNoThrow { try f(0) }
            await expectThrows(Oops.self) { await Task.yield()
                try f(1) }
            expectThrows(Oops.Bad) { try f(4) }
            expectThrows(Oops.self) { try f(0) }
            expectThrows(ParseError.self) { try f(5) }
            expectNoThrow { try f(6) }
        }
        """,
        """
        ---- M.throwing (test.sb:8) ----
            expectation failed: 'Oops.Worse(4)' was thrown, and 'Oops.Bad' was to be (test.sb:14)
            expectation failed: nothing was thrown (test.sb:15)
            expectation failed: another error was thrown: Oops.Worse(5) (test.sb:16)
            expectation failed: an error was thrown: Oops.Worse(6) (test.sb:17)

        """);

    [Fact]
    public void DeathsAreProcessesOfTheirOwn() => Reports(
        """
        @test
        async fn deaths() {
            let xs = [1, 2]
            let end = expectPanic("out of range") { print(xs[2]) }
            expect(end?.message == "array index out of range")
            expect(expectExit(3) { Runtime.exit(3) } != none)
            await expectPanic("later") { await Task.yield()
                fatalError("later") }
            expectPanic { _ = xs.count }
            expectPanic("this") { fatalError("that") }
            expectPanic { Runtime.exit(4) }
            expectExit(5) { Runtime.exit(6) }
        }
        """,
        "---- M.deaths (test.sb:4) ----\n    expectation failed: the body was to trap, and it returned (test.sb:11)\n",
        "    expectation failed: the body trapped, but not with 'this' (test.sb:12)\n        fatal error: that\n",
        "    expectation failed: the body was to trap, and it ended the process with status 4 (test.sb:13)\n",
        "    expectation failed: the body was to end the process with status 5, and it was 6 (test.sb:14)\n");

    /// <summary>A failing case is made simple, whether it fails an expectation, throws or traps, and its seed is shown.</summary>
    [Fact]
    public void APropertyShowsTheSimplestCase() {
        Reports(
            """
            @derive(Debug, Arbitrary)
            @indirect
            union Tree { Node(Tree, Tree)
            Leaf(U8) }
            @derive(Debug, Arbitrary)
            struct Pair<A> { var a: A
            let b: Bool? }
            fn leaves(_ t: Tree) -> Int {
                return match t {
                    case .Node(let l, let r) => leaves(l) + leaves(r)
                    case .Leaf(_) => 1
                }
            }
            @test
            fn expectation() { forAll { (n: I64) in expect(n < 12345) } }
            @test
            fn structure() { forAll { (p: Pair<U16>) in expect(p.a < 7) } }
            @test
            fn recursive() { forAll { (t: Tree) in expect(leaves(t) < 3) } }
            @test
            fn thrown() { forAll { (n: Int) in if n < -9 { throw "low" } } }
            @test
            fn trapped() { forAll { (xs: Int[>..], i: U8) in if xs.count > 2 { _ = xs[Int(i)] } } }
            @test
            async fn awaited() { await forAll { (n: Int) in
                await Task.yield()
                expect(n < 99) } }
            @test
            fn filtered() { forAll(Gen.arbitrary(Int.self).filter { $0 == 123456789 }) { (n: Int) in expect(n > 0) } }
            """,
            "the property does not hold of 12345 (",
            "the property does not hold of Pair(a: 7, b: none) (",
            "Tree.Leaf(0)",
            "the property does not hold of -10 (",
            "the property does not hold of [0, 0, 0], 3 (",
            "fatal error: array index out of range",
            "the property does not hold of 99 (",
            "too few values were let through",
            "of seed ");
        if (!CanBuild) {
            return;
        }

        // The seed makes the same cases again.
        const string program = "@test\nfn p() { forAll { (xs: I32[>..]) in expect(xs.count < 8) } }";
        static string Cases(Run run) => run.Stdout.Split('\n').Single(static l => l.Contains("made simpler from", StringComparison.Ordinal));
        Assert.Equal(Cases(Runs(program, "--seed", "42")), Cases(Runs(program, "--seed", "42")));
        // With none, they are the same each run; 'random' chooses one.
        Assert.Equal(Cases(Runs(program)), Cases(Runs(program)));
        Assert.Contains("of seed ", Runs(program, "--seed", "random").Stdout, StringComparison.Ordinal);
        Assert.Contains("of seed 42: 'sable test --seed 42' runs it again", Runs(program, "--seed", "42").Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertiesThatHold() => Passes(
        """
        import Sable.Collections
        @test
        fn holds() {
            forAll { (xs: Int[>..], s: String, f: F64) in expect(xs.count >= 0 && s.count >= 0 && (f == f || f != f)) }
            forAll { (d: [String: Int?], s: Set<U8>, c: Char) in expect(d.count >= 0) }
            forAll { (r: Outcome<Bool, UUID>, a: I128, b: U128) in expect(a &+ 1 != a && b &- 1 != b) }
            forAll(Gen.number(in: -5 as I8...5)) { (n: I8) in expect(n >= -5 && n <= 5) }
            forAll(Gen.number(in: 10 as U64...U64.max)) { (n: U64) in expect(n >= 10) }
            forAll(Gen.float(in: -1.0...1.0), Gen.finite()) { (x: F64, y: F64) in expect(x >= -1.0 && x <= 1.0 && y.isFinite) }
            forAll(Gen.ascii(count: 2...4), Gen.element(of: ["a", "b"])) { (s: String, e: String) in expect(s.count >= 2 && s.count <= 4) }
            forAll(Gen.oneOf([Gen.always(1), Gen.number(in: 5...6)]).optional().array(count: 1...3)) { (xs: Int?[>..]) in expect(xs.count <= 3) }
            forAll(Gen.weighted([(1, Gen.always("x")), (3, Gen.always("y"))]).zip(Gen.arbitrary(Bool.self))) { (p: (String, Bool)) in expect(p.0.count == 1) }
            let indexed = Gen.number(in: 0...99).array(count: 1...10).flatMap { (all: Int[>..]) in
                Gen.number(in: 0...all.count - 1).map { (all, $0) }
            }
            forAll(indexed) { (p: (Int[>..], Int)) in expect(p.1 < p.0.count) }
        }
        """);

    [Fact]
    public void SnapshotsAreFilesBesideTheTest() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-snapshots-");
        try {
            var file = Path.Combine(directory.FullName, "shot.sb");
            static string Source(string text) => $"""
                module M
                import Sable.Testing
                @test
                fn shot() {"{"}
                    assertSnapshot([1, 2], named: "list")
                    assertSnapshot("a\n{text}\nc")
                    assertSnapshot("second")
                {"}"}
                """;
            File.WriteAllText(file, Source("b"));
            var (code, text) = Sable(file);
            Assert.Equal(1, code);
            Assert.Contains("there is no snapshot '__snapshots__/shot.shot.list.snap': SABLE_UPDATE_SNAPSHOTS=1", text, StringComparison.Ordinal);

            Assert.Equal(0, Sable(file, "--bless").Code);
            var kept = Path.Combine(directory.FullName, "__snapshots__");
            Assert.Equal(["shot.shot.2.snap", "shot.shot.list.snap", "shot.shot.snap"], Directory.GetFiles(kept).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal("a\nb\nc\n", File.ReadAllText(Path.Combine(kept, "shot.shot.snap")));
            Assert.Equal(0, Sable(file).Code);

            // A checkout with Windows's line breaks is the same snapshot.
            File.WriteAllText(Path.Combine(kept, "shot.shot.snap"), "a\r\nb\r\nc\r\n");
            Assert.Equal(0, Sable(file).Code);

            File.WriteAllText(file, Source("x"));
            (code, text) = Sable(file);
            Assert.Equal(1, code);
            Assert.Contains("- b\n", text, StringComparison.Ordinal);
            Assert.Contains("+ x\n", text, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    [Fact]
    public void ATestHasATimeLimitAndTheRunIsReported() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-reports-");
        try {
            var file = Path.Combine(directory.FullName, "t.sb");
            File.WriteAllText(file, """
                module M
                import Sable.Testing
                @test(timeout: .milliseconds(300))
                fn hangs() { while true { Runtime.sleep(nanoseconds: 1000000) } }
                @test(arguments: ["a.b", "<&\u{1}"], tags: ["fast", "x"])
                fn text(_ s: String) { print(s)
                    expect(s == "a.b") }
                """);
            var junit = Path.Combine(directory.FullName, "out", "j.xml");
            var trx = Path.Combine(directory.FullName, "t.trx");
            var (code, text) = Sable(file, "--junit", junit, "--trx", trx);
            Assert.Equal(1, code);
            Assert.Contains("the test did not finish within 0.3 s, and was stopped", text, StringComparison.Ordinal);

            var suites = XDocument.Load(junit).Root!;
            Assert.Equal(("3", "2"), ((string)suites.Attribute("tests")!, (string)suites.Attribute("failures")!));
            var cases = suites.Descendants("testcase").ToDictionary(static c => (string)c.Attribute("name")!);
            Assert.Equal("timeout", (string)cases["hangs"].Element("failure")!.Attribute("type")!);
            Assert.Null(cases["text(\"a.b\")"].Element("failure"));
            Assert.Equal(["fast", "x"], cases["text(\"a.b\")"].Descendants("property").Select(static p => (string)p.Attribute("value")!));
            Assert.All(cases.Values, static c => Assert.Equal("M", (string)c.Attribute("classname")!));

            XNamespace space = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var run = XDocument.Load(trx).Root!;
            Assert.Equal(["Failed", "Passed", "Failed"], run.Descendants(space + "UnitTestResult").Select(static r => (string)r.Attribute("outcome")!));
            Assert.Equal("1", (string)run.Descendants(space + "Counters").Single().Attribute("passed")!);
        } finally {
            directory.Delete(true);
        }
    }

    static (int Code, string Text) Sable(params string[] arguments) {
        var output = new StringWriter();
        var saved = Console.Out;
        try {
            Console.SetOut(output);
            return (Command.Find("test")!.Run(arguments), output.ToString().ReplaceLineEndings("\n"));
        } finally {
            Console.SetOut(saved);
        }
    }

    /// <summary>A closure's named parameters choose between overloads by how many there are; a generic struct's static needs none of its arguments.</summary>
    [Fact]
    public void WhatTheLibraryNeededOfTheCompiler() => Passes(
        """
        union Oops { Bad }
        struct Box<T> {
            let value: T
            static fn of<V>(_ value: V) -> Box<V> { return Box<V>(value: value) }
        }
        fn takes<T>(_ body: (T) -> Void) -> Int { return 1 }
        fn takes<A, B>(_ body: (A, B) -> Void) -> Int { return 2 }
        fn cast<E>(_ error: any Error, _ type: E.Type) -> E? { return error as? E }
        @test
        fn all() {
            assertEqual(takes { (a: Int) in }, 1)
            assertEqual(takes { (a: Int, b: String) in }, 2)
            assertEqual(takes((a: Int, b: Int) => { }), 2)
            assertEqual(Box.of("x").value, "x")
            fn fails() throws { throw Oops.Bad }
            do { try fails() } catch {
                assert(cast(error, Oops.self) != none && cast(error, Int.self) == none)
                assert((error as! Oops) is Oops)
            }
        }
        """);

    /// <summary>Suites, fixtures of each lifetime, skips, tries and the rest (#107), as the run and its reports tell of them.</summary>
    const string Structured = """
        module M
        import Sable.Testing

        final class Shared: Fixture {
            static var lifetime: FixtureLifetime => .PerSuite
            let pack: Pack
            init(pack: Pack) { self.pack = pack }
            async fn tearDown() throws { print("shared down") }
        }

        final class Pack: Fixture {
            static var lifetime: FixtureLifetime => .PerPackage
            async fn tearDown() throws { print("pack down") }
        }

        @suite(tags: ["slow"], retry: 1)
        final class Orders {
            let shared: Shared
            init(shared: Shared) { self.shared = shared }

            @test
            fn places() { expect(TestContext.current.tags == ["slow"]) }

            @test
            fn traps() { print([1][3]) }

            @test
            fn again() { expect(TestContext.current.attempt == 2) }

            @test(skip: "no <db>")
            fn skipped() { }

            @test
            fn found() throws { try skip("no network") }
        }

        @test(explicit)
        fn asked() { TestContext.current.attach("kept", named: "k.txt") }
        """;

    [Fact]
    public void SuitesAndTheirFixturesAreRunAndReported() {
        if (!CanBuild) {
            return;
        }

        // The harness on its own, and 'sable test', go the same way.
        var own = Native(Structured);
        var directory = Directory.CreateTempSubdirectory("sable-suites-");
        try {
            var file = Path.Combine(directory.FullName, "t.sb");
            File.WriteAllText(file, Structured);
            var json = Path.Combine(directory.FullName, "t.json");
            var junit = Path.Combine(directory.FullName, "t.xml");
            var trx = Path.Combine(directory.FullName, "t.trx");
            var (code, text) = Sable(file, "--json", json, "--junit", junit, "--trx", trx);
            Assert.Equal((1, 1), (own.ExitCode, code));
            foreach (var run in new[] { own.Stdout, text }) {
                Assert.Contains("test M.Orders.places ... ok (", run, StringComparison.Ordinal);
                Assert.Contains("test M.Orders.traps ... FAILED (", run, StringComparison.Ordinal);
                Assert.Matches(@"test M\.Orders\.again \.\.\. ok \(.*\), on try 2\n", run);
                Assert.Contains("test M.Orders.skipped ... skipped: no <db>\n", run, StringComparison.Ordinal);
                Assert.Contains("test M.Orders.found ... skipped: no network\n", run, StringComparison.Ordinal);
                Assert.Contains("test M.asked ... skipped: it is run only when asked for\n", run, StringComparison.Ordinal);
                Assert.Contains("---- M.Orders.traps (t", run, StringComparison.Ordinal);
                Assert.Contains("    shared down\n    pack down\n", run, StringComparison.Ordinal);
                Assert.Contains("2 passed; 1 failed; 3 skipped; 0 filtered out", run, StringComparison.Ordinal);
            }

            var events = File.ReadAllLines(json).Select(static l => System.Text.Json.JsonDocument.Parse(l).RootElement).ToList();
            Assert.Equal(["run", ..Enumerable.Repeat("finished", 6), "ended"], events.Select(static e => e.GetProperty("event").GetString()!));
            Assert.Equal("no <db>", events.Single(static e => e.TryGetProperty("name", out var n) && n.GetString() == "M.Orders.skipped").GetProperty("reason").GetString());

            var suites = XDocument.Load(junit).Root!;
            Assert.Equal(("6", "1", "3"), ((string)suites.Attribute("tests")!, (string)suites.Attribute("failures")!, (string)suites.Attribute("skipped")!));
            Assert.Equal(["no <db>", "no network", "it is run only when asked for"], suites.Descendants("skipped").Select(static s => (string)s.Attribute("message")!));
            XNamespace space = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            Assert.Equal("3", (string)XDocument.Load(trx).Descendants(space + "Counters").Single().Attribute("notExecuted")!);

            // Asked for, what it kept is named.
            var kept = Path.Combine(directory.FullName, "kept");
            (code, text) = Sable(file, "--exact", "M.asked", "--json", "-", "--attachments", kept);
            Assert.Equal(0, code);
            var attached = text.Split('\n').Where(static l => l.Contains("\"finished\"", StringComparison.Ordinal)).Select(static l => System.Text.Json.JsonDocument.Parse(l).RootElement).Single()
                .GetProperty("attachments")[0].GetString()!;
            Assert.StartsWith(kept, attached, StringComparison.Ordinal);
            Assert.Equal("kept", File.ReadAllText(attached));

            (code, text) = Sable(file, "--list", "--json", "-", "--filter", "tag:slow & name:places");
            Assert.Equal(0, code);
            Assert.StartsWith("{\"name\":\"M.Orders.places\",\"file\":\"t.sb\",\"line\":22,\"suite\":\"M.Orders\",\"tags\":[\"slow\"],\"retries\":1,\"group\":\"package\"}", text, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    [Fact]
    public void CoverageIsWrittenAsCobertura() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-cobertura-");
        try {
            var file = Path.Combine(directory.FullName, "t.sb");
            File.WriteAllText(file, "module M\nimport Sable.Testing\nfn sign(_ n: Int) -> Int {\n    if n < 0 {\n        return -1\n    }\n    return 1\n}\n@test\nfn positive() { assertEqual(sign(3), 1) }\n");
            var xml = Path.Combine(directory.FullName, "c.xml");
            Assert.Equal(0, Sable(file, "--cobertura", xml).Code);
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore };
            using var reader = System.Xml.XmlReader.Create(xml, settings);
            var root = XDocument.Load(reader).Root!;
            var line = root.Descendants("line").Single(static l => (string)l.Attribute("number")! == "4");
            Assert.Equal(("1", "true", "50% (1/2)"), ((string)line.Attribute("hits")!, (string)line.Attribute("branch")!, (string)line.Attribute("condition-coverage")!));
            Assert.Equal("0", (string)root.Descendants("line").Single(static l => (string)l.Attribute("number")! == "5").Attribute("hits")!);
            Assert.Equal("t.sb", (string)root.Descendants("class").Single().Attribute("filename")!);
        } finally {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("S3114", "@suite\nstruct S { let n: Int\n@test fn t() { } }")]
    [InlineData("S3114", "final class A: Fixture { init(a: A) { } }\n@suite\nstruct S { let a: A\n@test fn t() { } }")]
    [InlineData("S3114", "@suite\nfinal class S { init() { }\ninit(f: F) { }\n@test fn t() { } }")]
    [InlineData("S3109", "@suite\nstruct S<T> { @test fn t() { } }")]
    [InlineData("S3093", "@suite\nenum S { A }")]
    [InlineData("S3107", "@suite(traps)\nstruct S { @test fn t() { } }")]
    [InlineData("S3107", "@suite(arguments: [1])\nstruct S { @test fn t() { } }")]
    [InlineData("S3107", "@test(retry: 0)\nfn t() { }")]
    [InlineData("S3107", "@test(skip: 1)\nfn t() { }")]
    [InlineData("S3107", "@test(skip(if: true, 1))\nfn t() { }")]
    [InlineData("S3107", "@test(serialized, serialized)\nfn t() { }")]
    [InlineData("S3004", "@test(skip(if: 1, \"why\"))\nfn t() { }")]
    [InlineData("S3004", "@suite(skip(if: 1, \"why\"))\nstruct S { @test fn t() { } }")]
    [InlineData("S3100", "@test\nfn t(f: inout F) { }")]
    public void ASuiteIsOneTheHarnessCanMake(string code, string declarations) {
        var errors = ErrorsOf($"module M\nimport Sable.Testing\nfinal class F: Fixture {{ }}\n{declarations}\n@main fn main() {{ }}\n");
        Assert.True(errors.Any(e => e.Contains($"[{code}]", StringComparison.Ordinal)), string.Join("\n", errors));
    }
}
