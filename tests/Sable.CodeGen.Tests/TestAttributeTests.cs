using Sable.Symbols;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     What may be marked <c>@test</c>, <c>@bench</c> or <c>@fuzz</c>:
///     S3100–S3110, reported in every build, not only a test build.
/// </summary>
public sealed class TestAttributeTests {
    static string Program(string declarations) =>
        $"module M\nimport Sable.Testing\n{declarations}\n@main\nfn main() {{ }}\n";

    static void Rejects(string code, string declarations, string because) {
        var errors = ErrorsOf(Program(declarations));
        Assert.Contains(errors, e => e.Contains($"[{code}]", StringComparison.Ordinal) && e.Contains(because, StringComparison.Ordinal));

        // A test build says the same, and writes no harness.
        var test = Compile(Program(declarations));
        Assert.False(test.Succeeded);
        Assert.Contains(test.Errors, e => e.Contains($"[{code}]", StringComparison.Ordinal));
        Assert.DoesNotContain(test.Errors, static e => e.Contains("internal error", StringComparison.Ordinal));
    }

    [Fact]
    public void ATestTakesNoParameters() =>
        Rejects("S3100", "@test\nfn t(_ n: Int) { }", "'Int' is not a fixture: a test is passed fixtures");

    [Fact]
    public void ATestIsNotGeneric() =>
        Rejects("S3101", "@test\nfn t<T>() { }", "cannot be generic");

    [Fact]
    public void ATestReturnsNothing() =>
        Rejects("S3102", "@test\nfn t() -> Int { return 1 }", "a test returns nothing");

    [Fact]
    public void ATestIsNotAnInstanceMethod() =>
        Rejects("S3103", "struct S {\n    @test\n    fn t() { }\n}", "marks an instance method");

    [Fact]
    public void ABenchmarkMayBeAsync() =>
        Assert.Empty(ErrorsOf(Program("@bench\nasync fn b() { }\n@bench\nasync fn c(_ x: Bencher) { await x.iterateAsync(() => { await Task.yield() }) }")));

    [Fact]
    public void ABenchmarkTakesNothingOrABencher() {
        Rejects("S3105", "@bench\nfn b(_ n: Int) { }", "one 'Bencher'");
        Rejects("S3105", "@bench\nfn b(_ x: Bencher, _ y: Bencher) { }", "one 'Bencher'");
        Assert.Empty(ErrorsOf(Program("@bench\nfn b(_ x: Bencher) { x.iterate(() => { }) }\n@bench\nfn c() { }")));
    }

    [Fact]
    public void ABenchmarksArguments() {
        Assert.Empty(ErrorsOf(Program("@bench(arguments: [1, 2], tags: [\"fast\"])\nfn b(_ x: Bencher, _ n: Int) { x.iterate(() => { }) }\n@bench(arguments: [(1, \"a\")])\nfn c(_ n: Int, _ s: String) { }")));
        Rejects("S3100", "@bench(arguments: [1])\nfn b() { }", "takes a parameter for them");
        Rejects("S3100", "@bench(arguments: [1], [2], [3])\nfn b(_ x: Bencher, _ n: Int, _ m: Int) { }", "tuples of the 2 values");
        Assert.NotEmpty(ErrorsOf(Program("@bench(arguments: [\"a\"])\nfn b(_ x: Bencher, _ n: Int) { x.iterate(() => { }) }")));
    }

    [Fact]
    public void NotBoth() =>
        Rejects("S3106", "@test @bench\nfn t() { }", "one of a test, a benchmark and a fuzz target");

    [Fact]
    public void TheAttributesTakeOnlyWhatTheyTake() {
        Rejects("S3107", "@test(\"slow\")\nfn t() { }", "'@test' takes nothing, or 'traps'");
        Rejects("S3107", "@test(traps: 1)\nfn t() { }", "'@test' takes nothing, or 'traps'");
        Rejects("S3107", "@bench(traps)\nfn b() { }", "'@bench' takes nothing, or 'arguments: [...]'");
        Rejects("S3107", "@bench(timeout: .seconds(1))\nfn b() { }", "'@bench' takes nothing, or 'arguments: [...]'");
        Rejects("S3107", "@fuzz(1)\nfn f(_ data: U8[:]) { }", "'@fuzz' takes nothing, or 'mutator: f'");
    }

    [Fact]
    public void NotALocalFunction() =>
        Rejects("S3108", "fn outer() {\n    @test\n    fn inner() { }\n    inner()\n}", "declared inside a body");

    [Fact]
    public void NotOnAGenericTypeOrPrivateMethod() {
        Rejects("S3109", "struct Box<T> {\n    @test\n    static fn t() { }\n}", "a method of a generic type");
        Rejects("S3109", "struct S {\n    @test\n    private static fn t() { }\n}", "a private method");
    }

    [Fact]
    public void AFuzzTargetTakesTheBytesOrValues() {
        Rejects("S3104", "@fuzz\nfn f() { }", "one 'U8[:]'");
        Rejects("S3104", "@fuzz\nfn f(_ a: Int, _ b: Int, _ c: Int, _ d: Int, _ e: Int) { }", "4 at most");
        Rejects("S3104", "@fuzz\nfn f(_ n: inout Int) { }", "plain ones");
        Rejects("S3104", "@fuzz\nfn f(_ data: I8[:]) { }", "one 'U8[:]'");
        Rejects("S3104", "@fuzz\nfn f(_ data: U8[:], _ n: Int) { }", "one 'U8[:]'");
        Rejects("S3104", "@fuzz\nasync fn f(_ data: U8[:]) { }", "not async");
    }

    [Fact]
    public void AFuzzTargetIsWhatTheHarnessCanCall() {
        Rejects("S3101", "@fuzz\nfn f<T>(_ data: U8[:]) { }", "a fuzz target cannot be generic");
        Rejects("S3102", "@fuzz\nfn f(_ data: U8[:]) -> Int { return 1 }", "a fuzz target returns nothing");
        Rejects("S3103", "struct S {\n    @fuzz\n    fn f(_ data: U8[:]) { }\n}", "'@fuzz' marks an instance method");
        Rejects("S3106", "@test @fuzz\nfn f() { }", "one of a test, a benchmark and a fuzz target");
        Rejects("S3107", "@fuzz(slow)\nfn f(_ data: U8[:]) { }", "'@fuzz' takes nothing, or 'mutator: f'");
        Rejects("S3108", "fn outer() {\n    @fuzz\n    fn inner(_ data: U8[:]) { }\n}", "declared inside a body");
    }

    [Fact]
    public void WhatAFuzzTargetMayBe() {
        Assert.Empty(ErrorsOf(Program("""
            union E { A }
            @fuzz
            fn plain(_ data: U8[:]) { }
            @fuzz
            fn values(_ data: U8[>..], by name: String) { }
            @fuzz(mutator: longer)
            fn mutated(_ data: U8[:]) { }
            fn longer(_ input: U8[:], _ seed: U64) -> U8[>..] { return input.toArray() }
            @fuzz
            fn throwing(_ bytes: U8[:]) throws(E) { }
            @fuzz
            private fn hidden(_ data: U8[:]) { }
            struct S {
                @fuzz
                static fn inType(_ data: U8[:]) { }
            }
            """)));

        // And the harness lists them, in order, by their names.
        var result = TestHarness.Compile([new SourceText(Program("""
            @fuzz
            fn first(_ data: U8[:]) { }
            struct S {
                @fuzz
                static fn second(_ data: U8[:]) { }
            }
            @test
            fn notAFuzzTarget() { }
            """), "test.sb")], TestKind.Fuzz, out var targets);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["M.first", "M.S.second"], targets.Select(static t => t.QualifiedName));
    }

    [Fact]
    public void ABenchmarkDoesNotThrow() =>
        Rejects("S3110", "union E { A }\n@bench\nfn b() throws(E) { }", "a benchmark cannot throw");

    [Fact]
    public void WhatIsAllowed() =>
        Assert.Empty(ErrorsOf(Program("""
            union E { A }
            @test
            fn plain() { }
            @test
            fn throwing() throws(E) { }
            @test
            private fn hidden() { }
            struct S {
                @test
                static fn inType() { }
            }
            extension S {
                @test
                static fn inExtension() { }
            }
            @test
            async fn awaits() { }
            @test(traps)
            fn traps() { }
            @test(traps: "out of range")
            fn trapsSaying() { }
            """)));
}
