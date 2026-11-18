using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     What may be marked <c>@test</c> or <c>@bench</c>: S3100–S3110,
///     reported in every build, not only a test build.
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
        Rejects("S3100", "@test\nfn t(_ n: Int) { }", "a test takes no parameters");

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
    public void ABenchmarkIsNotAsync() =>
        Rejects("S3104", "@bench\nasync fn b() { }", "cannot be async");

    [Fact]
    public void ABenchmarkTakesNothingOrABencher() {
        Rejects("S3105", "@bench\nfn b(_ n: Int) { }", "one 'Bencher'");
        Rejects("S3105", "@bench\nfn b(_ x: Bencher, _ y: Bencher) { }", "one 'Bencher'");
        Assert.Empty(ErrorsOf(Program("@bench\nfn b(_ x: Bencher) { x.iterate(() => { }) }\n@bench\nfn c() { }")));
    }

    [Fact]
    public void NotBoth() =>
        Rejects("S3106", "@test @bench\nfn t() { }", "not both");

    [Fact]
    public void TheAttributesTakeOnlyWhatTheyTake() {
        Rejects("S3107", "@test(\"slow\")\nfn t() { }", "'@test' takes nothing, or 'traps'");
        Rejects("S3107", "@test(traps: 1)\nfn t() { }", "'@test' takes nothing, or 'traps'");
        Rejects("S3107", "@bench(traps)\nfn b() { }", "'@bench' takes no arguments");
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
