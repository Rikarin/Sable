namespace Sable.Symbols.Tests;

/// <summary>
///     <c>@extern</c> and <c>@export</c> (SYNTAX.md §24): what they declare,
///     and what C can be given (S3210, S3211).
/// </summary>
public sealed class InteropTests {
    static Compilation Compile(string source) => Compilation.Create($"module M\n{source}");

    static List<Diagnostic> ErrorsIn(string source) =>
        [..Compile(source).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static Diagnostic AssertReports(ErrorCode code, string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 1 && errors[0].Code == code, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        return errors[0];
    }

    static FunctionSymbol Function(Compilation compilation, string name) =>
        (FunctionSymbol)compilation.GetDeclaredSymbol(compilation.SyntaxTrees.SelectMany(static t => t.Root.DescendantNodes())
            .OfType<FunctionDeclarationSyntax>().Single(f => f.Identifier.Text == name))!;

    [Fact]
    public void AnExternFunctionNamesItsCSymbol() {
        var compilation = Compile("@extern(\"c\", \"abs\")\nfn cAbs(_ x: I32) -> I32");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("abs", Function(compilation, "cAbs").ForeignName);
    }

    [Fact]
    public void AnExternWithoutANameTakesTheFunctions() =>
        Assert.Equal("labs", Function(Compile("@extern(\"c\")\nfn labs(_ x: Int) -> Int"), "labs").ForeignName);

    [Theory]
    [InlineData("@export(\"c\", \"sable_entry\")")]
    [InlineData("@export(\"sable_entry\")")]
    public void AnExportedFunctionNamesItsCSymbol(string attribute) =>
        Assert.Equal("sable_entry", Function(Compile($"{attribute}\nfn entry(_ x: I32) -> I32 {{\n    return x\n}}"), "entry").ExportName);

    [Fact]
    public void StructsOfCTypesCross() =>
        AssertClean(
            """
            struct Inner {
                x: F32
                flag: Bool
            }
            struct Outer {
                inner: Inner
                n: U16
            }
            @extern("c", "take")
            fn take(_ o: Outer, _ d: F64) -> Inner
            """
        );

    [Theory]
    [InlineData("@extern(\"cpp\", \"abs\")\nfn a(_ x: I32) -> I32", "not a language")]
    [InlineData("@extern(\"c\", \"not a name\")\nfn a(_ x: I32) -> I32", "not a name C")]
    [InlineData("@extern\nfn a(_ x: I32) -> I32", "string literals")]
    [InlineData("@extern(\"c\")\nfn a(_ x: I32) -> I32 {\n    return x\n}", "has no body")]
    [InlineData("@extern(\"c\")\nfn a<T>(_ x: T) -> I32", "not generic")]
    [InlineData("@extern(\"c\")\nasync fn a(_ x: I32) -> I32", "neither waits nor throws")]
    [InlineData("struct S {\n    @extern(\"c\")\n    fn a() -> I32\n}", "C has no methods")]
    public void AMalformedExternIsReported(string source, string message) =>
        Assert.Contains(message, AssertReports(ErrorCode.ERR_MalformedForeign, source).Message, StringComparison.Ordinal);

    [Fact]
    public void AnExportWithoutABodyIsReported() =>
        Assert.Contains("has a body", ErrorsIn("@export(\"c\", \"e\")\nfn e() -> I32").First(static e => e.Code == ErrorCode.ERR_MalformedForeign).Message,
            StringComparison.Ordinal);

    [Theory]
    [InlineData("@extern(\"c\", \"puts\")\nfn puts(_ text: String) -> I32", "'String' is not a C type")]
    [InlineData("@extern(\"c\")\nfn f(_ x: Int?) -> I32", "not a C type")]
    [InlineData("class Box { }\n@extern(\"c\")\nfn f(_ b: Box)", "not a C type")]
    [InlineData("@extern(\"c\")\nfn f() -> String", "the result cannot cross")]
    [InlineData("@extern(\"c\")\nfn f(_ x: inout Int)", "passed by value")]
    [InlineData("struct H {\n    var n: Int\n    deinit { }\n}\n@extern(\"c\")\nfn f(_ h: H)", "has a deinit")]
    [InlineData("struct P {\n    name: String\n}\n@export(\"c\", \"p\")\nfn f(_ p: P) { }", "'P.name' is 'String'")]
    public void WhatCDoesNotHaveIsReported(string source, string message) =>
        Assert.Contains(message, AssertReports(ErrorCode.ERR_NotACType, source).Message, StringComparison.Ordinal);
}
