namespace Sable.CodeGen.Tests;

/// <summary>Where the pipeline stops, and what it says, when it cannot go on.</summary>
public sealed class PipelineTests {
    static CompileResult Compile(string source) => Pipeline.Compile(new SourceText(source, "test.sab"));

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
        Assert.Contains("define i32 @main()", result.LlvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @\"sable.M.start\"()", result.LlvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void StringsAreEscapedAsUtf8Bytes() {
        var result = Compile("module M\n@main\nfn main() { print(\"tab\\there \\\"quoted\\\" ü\") }\n");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("c\"tab\\09here \\22quoted\\22 \\C3\\BC\"", result.LlvmIr, StringComparison.Ordinal);
    }
}
