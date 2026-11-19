namespace Sable.Symbols.Tests;

/// <summary>
///     Throwing iteration (spec/SYNTAX.md §18, "Throwing iteration"): an
///     iterator's <c>next()</c> throws its <c>Failure</c>, a sequence's is its
///     iterator's, and a loop over one whose Failure is not Never is
///     <c>for try</c>. What cannot fail needs no <c>try</c>, and Sequence's
///     algorithms throw only where the sequence or a closure does.
/// </summary>
public class ThrowingIterationTests {
    const string Lines = """
                         union ReadError: Error { Broken }
                         struct Lines: Sequence {
                             fn makeIterator() -> LinesIterator { return LinesIterator { left: 2 } }
                         }
                         struct LinesIterator: Iterator {
                             var left: Int
                             mutating fn next() throws(ReadError) -> String? {
                                 if left == 0 { throw .Broken }
                                 left = left - 1
                                 return "line"
                             }
                         }
                         struct Quiet: Sequence {
                             fn makeIterator() -> QuietIterator { return QuietIterator { } }
                         }
                         struct QuietIterator: Iterator {
                             mutating fn next() -> Int? { return none }
                         }
                         fn gen() throws(ReadError) -> some Sequence<Int> {
                             yield 1
                         }
                         fn quietGen() -> some Sequence<Int> {
                             yield 1
                         }
                         """;

    static Compilation Compile(string code) => Compilation.Create($"module M\n{Lines}\n{code}");

    static IReadOnlyList<Diagnostic> Errors(string code) =>
        [..Compile(code).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];

    static IReadOnlyList<Diagnostic> Warnings(string code) =>
        [..Compile(code).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Warning)];

    [Fact]
    public void AnIteratorsFailureIsWhatItsNextThrowsAndASequencesIsItsIterators() {
        const string Needs = """
                             fn failsWith<S: Sequence>(_ s: S) where S.Failure == ReadError { }
                             fn cannotFail<S: Sequence>(_ s: S) where S.Failure == Never { }
                             fn iteratorFailsWith<I: Iterator>(_ i: I) where I.Failure == ReadError { }
                             fn iteratorCannotFail<I: Iterator>(_ i: I) where I.Failure == Never { }
                             """;
        Assert.Empty(Errors($"{Needs}\nfn f() {{ failsWith(Lines())\niteratorFailsWith(LinesIterator {{ left: 1 }})\nfailsWith(gen())\ncannotFail(Quiet())\niteratorCannotFail(QuietIterator {{ }})\ncannotFail(quietGen())\ncannotFail([1])\ncannotFail(0..<2) }}"));
        Assert.Equal(3, Errors($"{Needs}\nfn f() {{ cannotFail(Lines())\nfailsWith(Quiet())\ncannotFail(gen()) }}").Count);
    }

    [Fact]
    public void ALoopOverAFailingSequenceNeedsForTry() {
        var error = Assert.Single(Errors("fn f() throws(ReadError) { for line in Lines() { } }"));
        Assert.Equal(ErrorCode.ERR_MustUseTry, error.Code);
        Assert.Contains("'Lines()' can throw 'ReadError'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'for try'", error.Message, StringComparison.Ordinal);

        // The fix writes 'try' after 'for'.
        var fix = Assert.Single(error.Fixes);
        Assert.Equal("try ", Assert.Single(fix.Changes).NewText);
    }

    [Fact]
    public void ForTryLetsTheErrorOutOrToACatch() {
        Assert.Empty(Errors("fn f() throws(ReadError) { for try line in Lines() { } }"));
        Assert.Empty(Errors("fn f() { do { for try line in Lines() { } } catch { } }"));
        Assert.Empty(Errors("fn f() throws { for try x in gen() { } }"));
        Assert.Equal(ErrorCode.ERR_ThrowsNotDeclared, Assert.Single(Errors("fn f() { for try line in Lines() { } }")).Code);
    }

    [Fact]
    public void WhatCannotFailNeedsNoTry() {
        Assert.Empty(Errors("fn f() { for x in Quiet() { }\nfor x in quietGen() { }\nfor x in [1, 2] { }\nfor x in 0..<3 { }\nfor x in stride(from: 0, to: 4, by: 2) { } }"));

        // ... and saying 'try' anyway is pointless.
        foreach (var loop in (string[])["for try x in Quiet() { }", "for try x in [1, 2] { }", "for try x in 0..<3 { }"]) {
            var warning = Assert.Single(Warnings($"fn f() throws {{ {loop} }}"));
            Assert.Equal(ErrorCode.WRN_NothingThrowsHere, warning.Code);
        }
    }

    [Fact]
    public void AGeneratorsCallThrowsNothingAndItsSequenceFailsWithItsError() {
        Assert.Empty(Errors("fn f() { let s = gen() }"));
        var compilation = Compile("");
        var gen = (FunctionSymbol)compilation.GetModule("M")!.GetMembers("gen").Single();
        Assert.Equal("some Sequence<Int, ReadError>", gen.ReturnType.ToDisplayString());
        var quiet = (FunctionSymbol)compilation.GetModule("M")!.GetMembers("quietGen").Single();
        Assert.Equal("some Sequence<Int, Never>", quiet.ReturnType.ToDisplayString());
    }

    [Fact]
    public void GenericCodeLetsTheFailureOutOrAsksForNone() {
        Assert.Equal(
            ErrorCode.ERR_MustUseTry,
            Assert.Single(Errors("fn f<S: Sequence>(_ s: S) { for x in s { } }")).Code
        );
        Assert.Empty(Errors("fn f<S: Sequence>(_ s: S) throws(S.Failure) { for try x in s { } }"));
        Assert.Empty(Errors("fn f<S: Sequence>(_ s: S) where S.Failure == Never { for x in s { } }"));
        Assert.Empty(Errors("fn f<C: Collection>(_ c: C) { for x in c { } }"));
        Assert.Empty(Errors("fn f<S: Sequence>(_ s: S) throws(S.Failure) { var i = s.makeIterator()\nlet x = try i.next() }"));

        // 'throws(S.Failure)' throws nothing for a sequence that cannot fail.
        Assert.Empty(Errors("fn g<S: Sequence>(_ s: S) throws(S.Failure) { }\nfn f() { g([1])\ng(Quiet()) }"));
        Assert.Equal(ErrorCode.ERR_MustUseTry, Assert.Single(Errors("fn g<S: Sequence>(_ s: S) throws(S.Failure) { }\nfn f() throws { g(Lines()) }")).Code);

        // 'where S.Failure == Never' turns away one that can.
        Assert.NotEmpty(Errors("fn g<S: Sequence>(_ s: S) where S.Failure == Never { }\nfn f() { g(Lines()) }"));
    }

    [Fact]
    public void SequenceAlgorithmsThrowWhereTheSequenceOrAClosureDoes() {
        Assert.Empty(Errors("fn f() { let a = [1, 2].map { $0 + 1 }\nlet b = Quiet().filter { $0 > 0 }\nlet c = quietGen().reduce(0) { $0 + $1 }\nlet d = quietGen().sorted() }"));
        Assert.Empty(Errors("fn f() throws(ReadError) { let a = try gen().map { $0 + 1 }\nlet b = try Lines().count(where: { $0.isEmpty })\nlet d = try gen().sorted() }"));

        var missing = Errors("fn f() throws(ReadError) { let a = gen().map { $0 + 1 } }");
        Assert.Equal(ErrorCode.ERR_MustUseTry, Assert.Single(missing).Code);

        // What a lazy view of a failing sequence throws is the sequence's.
        Assert.Empty(Errors("fn f() throws(ReadError) { for try x in gen().lazy.map({ $0 * 2 }).filter({ $0 > 0 }) { } }"));
        Assert.Equal(ErrorCode.ERR_MustUseTry, Assert.Single(Errors("fn f() throws(ReadError) { for x in gen().lazy.map({ $0 * 2 }) { } }")).Code);
    }

    [Fact]
    public void RethrowsWithAnErrorTypeThrowsItAsWell() {
        const string Declared = """
                                fn each<S: Sequence>(_ s: S, _ body: (S.Element) throws -> Void) rethrows(S.Failure) {
                                    for try x in s {
                                        try body(x)
                                    }
                                }
                                """;
        Assert.Empty(Errors($"{Declared}\nfn f() {{ each([1]) {{ print($0) }} }}"));
        Assert.Equal(ErrorCode.ERR_MustUseTry, Assert.Single(Errors($"{Declared}\nfn f() throws {{ each(Lines()) {{ print($0) }} }}")).Code);
        Assert.Empty(Errors($"{Declared}\nfn f() throws {{ try each(Lines()) {{ print($0) }} }}"));

        // Anything else of its own is still not let out.
        Assert.Equal(
            ErrorCode.ERR_RethrowsThrowsItsOwn,
            Assert.Single(Errors("fn g(_ body: () throws -> Void) rethrows(ReadError) { throw ReadError.Broken }\nfn h(_ body: () throws -> Void) rethrows(Never) { try gen().forEach { print($0) } }")).Code
        );
    }
}
