namespace Sable.Symbols.Tests;

/// <summary>
///     What binding and lowering read of another unit's declaration is a fact
///     the symbol holds, not something read from its syntax (#182): whether
///     the compiler made it, its attributes, whether it has a body, whether it
///     is <c>nonisolated</c>, and its const-generic predicates. A symbol read
///     from a library's interface has no syntax and answers the same.
/// </summary>
public sealed class SymbolFactTests {
    static Compilation Program(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        return compilation;
    }

    static NamedTypeSymbol Type(Compilation compilation, string name) => compilation.GetTypeByQualifiedName($"M.{name}")!;

    static FunctionSymbol Function(Symbol container, string name) =>
        (container is ModuleSymbol module ? module.GetMembers(name) : ((NamedTypeSymbol)container).GetMembers(name)).OfType<FunctionSymbol>().First();

    [Fact]
    public void AnAttributeIsHeldWithItsArgumentsAsWritten() {
        var compilation = Program("@inline(always)\nfn fast() -> Int => 1\n@inline(never)\nfn slow() -> Int => 2\nfn main() { }");
        var module = compilation.GetModule("M")!;
        var fast = Assert.Single(Function(module, "fast").Attributes);
        Assert.Equal(new SymbolAttribute("inline", "(always)"), fast);
        Assert.Equal<string>(["always"], fast.Arguments);
        Assert.Equal("inline(always)", fast.Written);
        Assert.Equal<string>(["never"], Function(module, "slow").GetAttribute("inline")!.Arguments);
        Assert.Empty(Function(module, "main").Attributes);
    }

    [Fact]
    public void ArgumentsAreSplitAtTheirOwnCommasOnly() =>
        Assert.Equal<string>(["\"c\"", "\"a, b\"", "(1, 2)"], new SymbolAttribute("extern", "(\"c\", \"a, b\", (1, 2))").Arguments);

    [Fact]
    public void AnExtensionsAttributesAreItsMembersToo() {
        var compilation = Program("struct S { }\n@MainActor\nextension S {\n    fn f() { }\n}\nfn main() { }");
        var f = Function(Type(compilation, "S"), "f");
        Assert.True(f.GetAttribute("MainActor") is { FromExtension: true });
        Assert.True(f.IsOnMainActor);
        Assert.False(Type(compilation, "S").IsOnMainActor);
    }

    [Fact]
    public void TheEntryPointIsKnownByItsAttribute() {
        var compilation = Program("@main\nfn start() { }");
        Assert.True(Function(compilation.GetModule("M")!, "start").IsInEntryPoint);
    }

    [Fact]
    public void WhatTheCompilerMadeSaysSo() {
        var compilation = Program("struct S { var x: Int }\nfn main() { }");
        var init = Function(Type(compilation, "S"), "init");
        Assert.True(init.IsSynthesized);
        Assert.False(init.HasBody);
        Assert.False(Function(compilation.GetModule("M")!, "main").IsSynthesized);
    }

    [Fact]
    public void WhetherAFunctionHasABodyIsItsOwn() {
        var compilation = Program("protocol P {\n    fn need() -> Int\n}\nfn main() { }\nfn short() -> Int => 1");
        Assert.False(Function(Type(compilation, "P"), "need").HasBody);
        Assert.True(Function(compilation.GetModule("M")!, "main").HasBody);
        Assert.True(Function(compilation.GetModule("M")!, "short").HasBody);
    }

    [Fact]
    public void NonisolatedIsAFact() {
        var compilation = Program("actor A {\n    var n: Int = 0\n    nonisolated fn name() -> String => \"a\"\n    fn count() -> Int => n\n}\nfn main() { }");
        Assert.True(Function(Type(compilation, "A"), "name").IsNonisolated);
        Assert.False(Function(Type(compilation, "A"), "count").IsNonisolated);
    }

    [Fact]
    public void APredicateIsBoundAndEvaluatesWithoutSyntax() {
        var compilation = Program("struct Buf<T, N: Int> where N > 0 && N % 2 == 0 {\n    var first: T\n}\nfn main() { }");
        var predicate = Assert.Single(Type(compilation, "Buf").ValuePredicates);
        Assert.Equal("N > 0 && N % 2 == 0", predicate.Text);
        Assert.True(predicate.Evaluate(new Dictionary<string, long> { ["N"] = 4 }));
        Assert.False(predicate.Evaluate(new Dictionary<string, long> { ["N"] = 3 }));
        Assert.Null(predicate.Evaluate(new Dictionary<string, long>()));
    }

    [Fact]
    public void AFailingPredicateIsStillReportedAsWritten() {
        var errors = Compilation.Create("module M\nstruct Buf<T, N: Int> where N > 0 {\n    var first: T\n}\nlet b: Buf<Int, 0>? = nil")
            .GetAllDiagnostics().Where(static d => d.Code == ErrorCode.ERR_ConstGenericPredicateFails).ToList();
        Assert.Contains("requires 'N > 0', which is false for N = 0", Assert.Single(errors).Message, StringComparison.Ordinal);
    }
}
