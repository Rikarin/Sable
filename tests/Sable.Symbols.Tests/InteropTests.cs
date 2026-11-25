namespace Sable.Symbols.Tests;

/// <summary>
///     <c>@extern</c> and <c>@export</c> (SYNTAX.md §24): what they declare,
///     and what C can be given (S3223, S3224).
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

    [Fact]
    public void PointersCross() =>
        AssertClean(
            """
            struct Handle {
                at: RawPtr<Opaque>
            }
            struct Node {
                next: RawPtr<Node>
                value: I32
            }
            @extern("c")
            unsafe fn take(_ p: RawPtr<U8>, _ q: NonNull<Int>, _ h: Handle, _ n: Node, _ rest: CVarArg...) -> RawPtr<RawPtr<Opaque>>
            @extern("c")
            fn give() -> RawPtr<String>
            @export("seen")
            fn seen(_ p: RawPtr<Int>) -> Bool => p.isNull
            """
        );

    [Theory]
    [InlineData("@extern(\"c\")\nfn f(_ p: RawPtr<U8>)", "'p'")]
    [InlineData("@extern(\"c\")\nfn f(_ n: Int, _ p: NonNull<U8>)", "'p'")]
    [InlineData("struct S {\n    a: Int\n    b: RawPtr<U8>\n}\n@extern(\"c\")\nfn f(_ s: S)", "'s'")]
    public void AnExternGivenAPointerIsUnsafe(string source, string names) =>
        Assert.Contains(names, AssertReports(ErrorCode.ERR_ForeignPointerNotUnsafe, source).Message, StringComparison.Ordinal);

    [Fact]
    public void AStructWithAPointerDoesNotCrossOnWebAssembly() {
        const string source = "module M\nstruct S {\n    a: Int\n    b: RawPtr<U8>\n}\n@extern(\"c\")\nunsafe fn f(_ s: S, _ p: RawPtr<S>) -> RawPtr<U8>";
        Diagnostic[] Errors(ParseOptions options) =>
            [..Compilation.Create([SyntaxTree.Parse(new SourceText(source, "m.sb"), options)]).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];
        Assert.Empty(Errors(ParseOptions.Default));
        Assert.Contains("32", Assert.Single(Errors(ParseOptions.Default.ForTarget("wasi", "wasm32"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalFunctionIsNotExtern() =>
        Assert.Contains("local", AssertReports(ErrorCode.ERR_MalformedForeign, "fn f() {\n    @extern(\"c\")\n    fn abs(_ n: I32) -> I32\n}").Message, StringComparison.Ordinal);

    [Fact]
    public void AClosuresTypeSettlesTheOnesAfterIt() =>
        AssertClean(
            """
            fn both<F, R>(_ f: F, _ g: (F) -> R) -> R { return g(f) }
            fn f() -> Int {
                return both({ (n: Int) in n + 1 }) { h in h(2) }
            }
            """
        );

    [Fact]
    public void ANamedFunctionSettlesAParameterOfAnyType() =>
        AssertClean(
            """
            fn keep<F>(_ f: F) -> F { return f }
            fn twice(_ n: Int) -> Int { return n * 2 }
            fn f() -> Int { return keep(twice)(4) }
            """
        );

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
