using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>Where the pipeline stops, and what it says, when it cannot go on.</summary>
public sealed class PipelineTests {
    static CompileResult Compile(string source) => Pipeline.Compile(new SourceText(source, "test.sb"));

    [Fact]
    public void ATypeErrorStopsItBeforeLowering() {
        var result = Compile("module M\n@main\nfn main() { let x: Int = true }\n");
        Assert.False(result.Succeeded);
        Assert.Null(result.Sil);
        Assert.Contains(result.Errors, static e => e.Contains("S3004", StringComparison.Ordinal));
    }

    [Fact]
    public void SomethingNotLoweredYetSaysWhat() {
        var result = Compile("module M\n@noncopy struct T { var id: Int }\nstruct P { var x: T[] }\n@main\nfn main() { let p: P? = none }\n");
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, static e => e.Contains("S7000", StringComparison.Ordinal));
    }

    // In the library it says the function, and is placed at the program's own
    // call that led to it, however far off (#303).
    [Theory]
    [InlineData("keep(move made)", "fn unused() {}", 8)]
    [InlineData("through(move made)", "fn through<T: ~Copyable>(_ value: consuming T) -> Int { return keep(move value) }", 4)]
    public void SomethingInTheLibraryNotLoweredYetSaysWhereTheProgramNeedsIt(string call, string declaration, int line) {
        var library = SyntaxTree.Parse(
            new SourceText(
                "module Sable.Extra\npublic fn keep<T: ~Copyable>(_ value: consuming T) -> Int { return held(move value) }\n"
                + "fn held<T: ~Copyable>(_ value: consuming T) -> Int {\n    let held: T[] = []\n    return held.count\n}\n",
                "lib/Sable.Extra/Extra.sb"
            )
        );
        var program = SyntaxTree.Parse(
            new SourceText($"module M\nimport Sable.Extra\n@noncopy struct Token {{ var id: Int }}\n{declaration}\n@main\nfn main() {{\n    let made = Token(id: 1)\n    print({call})\n}}\n", "test.sb")
        );
        var result = Pipeline.Compile([program, library]);
        Assert.False(result.Succeeded);
        var error = result.Errors.FirstOrDefault(static e => e.Contains("S7000", StringComparison.Ordinal));
        Assert.True(error is not null, string.Join("\n", result.Errors));
        Assert.Contains("in 'held' of the standard library (lib/Sable.Extra/Extra.sb:4:", error, StringComparison.Ordinal);
        Assert.Contains($"--> test.sb:{line}:", error, StringComparison.Ordinal);
    }

    // 'sable check' reports what the checks on SIL find, as a build does (#130).
    [Theory]
    [InlineData("S3057", "@noncopy struct H { var v: Int }\nfn take(_ h: consuming H) {}\nfn f() {\n    let h = H(v: 1)\n    take(h)\n    take(h)\n}\n", 6)]
    [InlineData("S3057", "fn take(_ s: consuming String) {}\nfn f() {\n    let s = \"x\"\n    take(move s)\n    print(s)\n}\n", 5)]
    [InlineData("S3062", "fn both(_ a: inout Int, _ b: inout Int) {}\nfn f() {\n    var n = 1\n    both(&n, &n)\n}\n", 4)]
    [InlineData("S3061", "fn f() -> Int& {\n    var n = 1\n    return &n\n}\n", 3)]
    public void CheckReportsWhatTheSilChecksFind(string code, string body, int line) {
        var errors = Pipeline.Check([SyntaxTree.Parse(new SourceText("module M\n" + body, "test.sb"))]);
        var error = Assert.Single(errors);
        Assert.Contains(code, error, StringComparison.Ordinal);
        Assert.Contains($"test.sb:{line + 1}:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckLeavesWhatIsNotLoweredYetToTheBuild() =>
        Assert.Empty(Pipeline.Check([SyntaxTree.Parse(new SourceText("module M\nstruct P { var x: U128 }\nfn f() { let p: P? = none }\n", "test.sb"))]));

    [Fact]
    public void ALoweringErrorSaysWhichFileItIsIn() {
        var main = SyntaxTree.Parse(new SourceText("module M\nfn main() { print(f()) }\n", "main.sb"));
        var other = SyntaxTree.Parse(new SourceText("module M\nfn f() -> Int {\n    var a = 1\n    swap(&a, &a)\n    return a\n}\nfn swap(_ x: inout Int, _ y: inout Int) {}\n", "other.sb"));
        var result = Pipeline.Compile([main, other]);
        Assert.Contains(result.Errors, static e => e.Contains("S3062", StringComparison.Ordinal) && e.Contains("other.sb:4:", StringComparison.Ordinal));
    }

    [Fact]
    public void AProgramNeedsAnEntryPoint() {
        var result = Compile("module M\nfn helper() -> Int { return 1 }\n");
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, static e => e.Contains("no entry point", StringComparison.Ordinal));
    }

    [Fact]
    public void AFunctionCalledMainIsTheEntryWithoutTheAttribute() {
        var result = Compile("module M\nfn main() { print(1) }\n");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal("M.main", result.Sil!.EntryPoint);
    }

    [Fact]
    public void TheIrDeclaresTheRuntimeAndDefinesMain() {
        var result = Compile("module M\n@main\nfn start() { print(\"x\") }\n");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("declare void @sable_print_string(ptr)", result.LlvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @main(i32 %argc, ptr %argv)", result.LlvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @\"sable.M.start\"()", result.LlvmIr, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A whole program's functions are its own, and none unwinds: LLVM may
    ///     inline one with one caller, and drop one it no longer calls.
    /// </summary>
    [Fact]
    public void AProgramsFunctionsAreInternalAndDoNotUnwind() {
        var result = Compile("module M\nfn twice(_ x: Int) -> Int { return x * 2 }\n@main\nfn start() { print(twice(4)) }\n");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("define internal void @\"sable.M.start\"() nounwind", result.LlvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("define void @\"sable.", result.LlvmIr, StringComparison.Ordinal);
    }

    /// <summary>
    ///     '@inline(always)' and '@inline(never)' are LLVM's 'alwaysinline'
    ///     and 'noinline' too, so that an unoptimised build, whose SIL is not
    ///     inlined, keeps to them.
    /// </summary>
    [Fact]
    public void InlineAttributesReachLlvm() {
        var result = Compile("module M\n@inline(always)\nfn twice(_ x: Int) -> Int { return x * 2 }\n@inline(never)\nfn once(_ x: Int) -> Int { return x }\n@main\nfn start() { print(twice(once(4))) }\n");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("@\"sable.M.once\"(i64 %p0) nounwind noinline", result.LlvmIr, StringComparison.Ordinal);
        Assert.Contains("@\"sable.M.twice\"(i64 %p0) nounwind alwaysinline", result.LlvmIr, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The front end's warnings come back with the result — with or
    ///     without errors — each against its file, and none of the standard
    ///     library's.
    /// </summary>
    [Fact]
    public void WarningsComeBackWithTheResult() {
        const string program = "module M\nfn f() -> Int {\n    return 1\n    print(2)\n}\nfn main() {\n#warning(\"look here\")\n    let x = try? f()\n    print(f() + (x ?? 0))\n}\n";
        var result = Compile(program);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Collection(
            result.Warnings,
            static w => Assert.StartsWith("warning[S6005]: this statement can never be reached\n --> test.sb:4:", w, StringComparison.Ordinal),
            static w => Assert.StartsWith("warning[S0030]: look here\n --> test.sb:7:", w, StringComparison.Ordinal),
            static w => Assert.StartsWith("warning[S3036]:", w, StringComparison.Ordinal)
        );

        var failed = Compile(program.Replace("print(2)", "print(true + 1)", StringComparison.Ordinal));
        Assert.False(failed.Succeeded);
        Assert.Contains(failed.Warnings, static w => w.StartsWith("warning[S0030]", StringComparison.Ordinal));
    }

    /// <summary>'#if DEBUG' holds in an unoptimised build, '#if RELEASE' in an optimised one.</summary>
    [Theory]
    [InlineData(false, "in debug")]
    [InlineData(true, "in release")]
    public void DebugOrReleaseIsDefined(bool optimise, string expected) {
        const string source = "module M\nfn main() {\n#if DEBUG\n    print(\"in debug\")\n#elseif RELEASE\n    print(\"in release\")\n#else\n    print(\"in neither\")\n#endif\n}\n";
        var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(source, "test.sb"), Pipeline.ParseOptionsFor(optimise))]);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        var strings = SilPrinter.Print(result.Sil!);
        Assert.Contains($"\"{expected}\"", strings, StringComparison.Ordinal);
        Assert.DoesNotContain("in neither", strings, StringComparison.Ordinal);
    }

    /// <summary>
    ///     'try' on what cannot throw is warned about (S3036) and then does
    ///     what it says: 'try?' gives the value wrapped, 'try!' the value.
    /// </summary>
    [Fact]
    public void TryOnWhatCannotThrowIsTheValue() =>
        Assert.Equal(
            "1\n2\nsome\n",
            AssertAgree("module M\nfn quiet() { }\nfn main() {\n    let a = try? 1\n    let b = try! 2\n    print(a ?? 0)\n    print(b)\n    let c = try? quiet()\n    print(c == none ? \"none\" : \"some\")\n}\n").Stdout
        );

    [Fact]
    public void StringsAreEscapedAsUtf8Bytes() {
        var result = Compile("module M\n@main\nfn main() { print(\"tab\\there \\\"quoted\\\" ü\") }\n");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("c\"tab\\09here \\22quoted\\22 \\C3\\BC\"", result.LlvmIr, StringComparison.Ordinal);
    }
}
