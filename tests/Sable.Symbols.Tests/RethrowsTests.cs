namespace Sable.Symbols.Tests;

/// <summary>
///     <c>rethrows</c>, which spec/SYNTAX.md §18 defines as "throws iff a closure
///     parameter throws". So the error type is read off the parameters, a call
///     only throws when the arguments it is handed can, and the body may not
///     throw anything of its own.
/// </summary>
public sealed class RethrowsTests {
    const string Preamble = """
                            module M
                            union IoError { NotFound, Denied }
                            union Parse { Bad }
                            struct Lock { }
                            fn plain() -> Int { return 1 }
                            fn risky() throws(IoError) -> Int { return 1 }
                            """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create(Preamble + "\n" + source)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    static void AssertError(ErrorCode code, string source) => Assert.Contains(ErrorsIn(source), e => e.Code == code);

    const string WithLock = """
                            fn withLock<T>(_ l: Lock, _ body: () throws(IoError) -> T) rethrows -> T {
                                return try body()
                            }
                            """;

    // ---- The error type comes off the parameters ---------------------------

    [Fact]
    public void TheThrownTypeIsWhatTheClosureParameterThrows() {
        var compilation = Compilation.Create(Preamble + "\n" + WithLock);
        var withLock = compilation.GetModule("M")!
            .Members.OfType<FunctionSymbol>()
            .Single(static f => f.Name == "withLock");

        Assert.True(withLock.IsRethrows);
        Assert.Equal("IoError", withLock.ThrownType!.ToDisplayString());
    }

    [Fact]
    public void TwoClosureParametersGiveTheUnionOfWhatTheyThrow() {
        var compilation = Compilation.Create(
            Preamble
            + """

              fn both(
                  _ a: () throws(IoError) -> Int,
                  _ b: () throws(Parse) -> Int
              ) rethrows -> Int {
                  return try a() + try b()
              }
              """
        );

        var both = compilation.GetModule("M")!
            .Members.OfType<FunctionSymbol>()
            .Single(static f => f.Name == "both");

        Assert.Equal("IoError | Parse", both.ThrownType!.ToDisplayString());
    }

    [Fact]
    public void RethrowsNeedsSomethingToRethrowFrom() =>
        AssertError(
            ErrorCode.ERR_RethrowsWithoutThrowingParameter,
            """
            fn nothing(_ body: () -> Int) rethrows -> Int { return body() }
            """
        );

    [Fact]
    public void AThrowingClosureBehindAnOptionalStillCounts() =>
        AssertClean(
            """
            fn maybe(_ body: (() throws(IoError) -> Int)?) rethrows -> Int {
                guard let body else { return 0 }
                return try body()
            }
            """
        );

    // ---- The body may only rethrow ------------------------------------------

    [Fact]
    public void TheBodyMayCallItsOwnClosureParameter() => AssertClean(WithLock);

    [Fact]
    public void TheBodyMayNotThrowOnItsOwn() =>
        AssertError(
            ErrorCode.ERR_RethrowsThrowsItsOwn,
            """
            fn bad(_ body: () throws(IoError) -> Int) rethrows -> Int {
                throw IoError.Denied
            }
            """
        );

    [Fact]
    public void TheBodyMayNotCallSomeOtherThrowingFunction() =>
        AssertError(
            ErrorCode.ERR_RethrowsThrowsItsOwn,
            """
            fn bad(_ body: () throws(IoError) -> Int) rethrows -> Int {
                return try risky()
            }
            """
        );

    [Fact]
    public void TheBodyMayCallAThrowingFunctionItHandles() =>
        AssertClean(
            """
            fn fine(_ body: () throws(IoError) -> Int) rethrows -> Int {
                do {
                    return try risky()
                } catch {
                    return try body()
                }
            }
            """
        );

    [Fact]
    public void TheBodyMayHandOnItsClosureToAnotherRethrowingFunction() =>
        AssertClean(
            """
            fn inner(_ body: () throws(IoError) -> Int) rethrows -> Int { return try body() }
            fn outer(_ body: () throws(IoError) -> Int) rethrows -> Int {
                return try inner(body)
            }
            """
        );

    [Fact]
    public void HandingAnotherRethrowingFunctionItsOwnThrowingClosureIsNotRethrowing() =>
        AssertError(
            ErrorCode.ERR_RethrowsThrowsItsOwn,
            """
            fn inner(_ body: () throws(IoError) -> Int) rethrows -> Int { return try body() }
            fn outer(_ body: () throws(IoError) -> Int) rethrows -> Int {
                return try inner(() => { throw IoError.Denied })
            }
            """
        );

    // ---- The call site ------------------------------------------------------

    [Fact]
    public void ACallWithANonThrowingClosureDoesNotThrow() =>
        AssertClean(
            $$"""
              {{WithLock}}
              fn use(_ l: Lock) -> Int { return withLock(l) { 1 } }
              """
        );

    [Fact]
    public void ACallWithANonThrowingClosureDoesNotWantTry() {
        var warnings = Compilation.Create(
                $$"""
                  {{Preamble}}
                  {{WithLock}}
                  fn use(_ l: Lock) -> Int { return try withLock(l) { 1 } }
                  """
            )
            .GetAllDiagnostics();

        Assert.Contains(warnings, static d => d.Code == ErrorCode.WRN_NothingThrowsHere);
    }

    [Fact]
    public void ACallWithAThrowingClosureNeedsTry() =>
        AssertError(
            ErrorCode.ERR_MustUseTry,
            $$"""
              {{WithLock}}
              fn use(_ l: Lock) throws(IoError) -> Int {
                  return withLock(l) { try risky() }
              }
              """
        );

    [Fact]
    public void ACallWithAThrowingClosureThrowsWhatThatClosureThrows() =>
        AssertClean(
            $$"""
              {{WithLock}}
              fn use(_ l: Lock) throws(IoError) -> Int {
                  return try withLock(l) { try risky() }
              }
              """
        );

    [Fact]
    public void ACallWithAThrowingClosureNeedsSomewhereForTheErrorToGo() =>
        AssertError(
            ErrorCode.ERR_ThrowsNotDeclared,
            $$"""
              {{WithLock}}
              fn use(_ l: Lock) -> Int {
                  return try withLock(l) { try risky() }
              }
              """
        );

    [Fact]
    public void ACallWithAThrowingClosureMayBeCaughtInstead() =>
        AssertClean(
            $$"""
              {{WithLock}}
              fn use(_ l: Lock) -> Int {
                  do {
                      return try withLock(l) { try risky() }
                  } catch {
                      return 0
                  }
              }
              """
        );

    // ---- Function types written 'rethrows' ----------------------------------

    [Fact]
    public void AFunctionTypeMayBeWrittenRethrows() =>
        AssertClean(
            """
            fn apply(_ f: (() throws(IoError) -> Int) rethrows -> Int) -> Int {
                return f({ 1 })
            }
            """
        );

    [Fact]
    public void CallingARethrowingValueWithAThrowingClosureNeedsTry() =>
        AssertError(
            ErrorCode.ERR_MustUseTry,
            """
            fn apply(
                _ f: (() throws(IoError) -> Int) rethrows -> Int
            ) throws(IoError) -> Int {
                return f({ try risky() })
            }
            """
        );

    [Fact]
    public void ARethrowingFunctionTypeAlsoNeedsAThrowingParameter() =>
        AssertError(
            ErrorCode.ERR_RethrowsWithoutThrowingParameter,
            """
            fn apply(_ f: (() -> Int) rethrows -> Int) -> Int { return f({ 1 }) }
            """
        );

    // ---- Conformance ---------------------------------------------------------

    [Fact]
    public void ARethrowingFunctionSatisfiesARethrowingRequirement() =>
        AssertClean(
            """
            protocol Runner {
                fn run<T>(_ body: () throws(IoError) -> T) rethrows -> T
            }
            struct Direct: Runner {
                fn run<T>(_ body: () throws(IoError) -> T) rethrows -> T { return try body() }
            }
            """
        );

    // ---- A binding that can be reassigned ----------------------------------

    [Fact]
    public void AVarThatHeldTheParameterIsNotASource() =>
        // 'k' may hold 'boom' by the time it is called, which throws on its own.
        AssertError(
            ErrorCode.ERR_RethrowsThrowsItsOwn,
            """
            fn boom() throws(IoError) -> Int { return 1 }
            fn run(_ g: () throws(IoError) -> Int) rethrows -> Int {
                var k = g
                k = boom
                return try k()
            }
            """
        );

    [Fact]
    public void ALetThatHoldsTheParameterIsASource() =>
        AssertClean(
            """
            fn run(_ g: () throws(IoError) -> Int) rethrows -> Int {
                let k = g
                return try k()
            }
            """
        );
}
