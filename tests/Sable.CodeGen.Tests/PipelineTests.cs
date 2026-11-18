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
        var result = Compile("module M\nstruct P { var x: U128 }\n@main\nfn main() { let p: P? = none }\n");
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, static e => e.Contains("S7000", StringComparison.Ordinal));
    }

    // 'sable check' reports what the checks on SIL find, as a build does (#130).
    [Theory]
    [InlineData("S3057", "@noncopy struct H { var v: Int }\nfn take(_ h: consuming H) {}\nfn f() {\n    let h = H(v: 1)\n    take(h)\n    take(h)\n}\n", 6)]
    [InlineData("S3062", "fn both(_ a: inout Int, _ b: inout Int) {}\nfn f() {\n    var n = 1\n    both(&n, &n)\n}\n", 4)]
    [InlineData("S3061", "fn f() -> ref Int {\n    var n = 1\n    return &n\n}\n", 3)]
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

    [Fact]
    public void StringsAreEscapedAsUtf8Bytes() {
        var result = Compile("module M\n@main\nfn main() { print(\"tab\\there \\\"quoted\\\" ü\") }\n");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("c\"tab\\09here \\22quoted\\22 \\C3\\BC\"", result.LlvmIr, StringComparison.Ordinal);
    }
}
