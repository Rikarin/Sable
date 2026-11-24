namespace Sable.Symbols.Tests;

/// <summary>
///     The two effects a signature can declare: <c>throws</c> and <c>async</c>.
///     Both are checked the same way — an effect has to be marked where it
///     happens, and has to have somewhere to go.
/// </summary>
public sealed class EffectTests {
    const string Helpers = """
                           union IoError { NotFound, Denied }
                           fn read(_ p: String) throws(IoError) -> String { return p }
                           fn plain(_ p: String) -> String { return p }
                           async fn fetch(_ p: String) -> String { return p }
                           """;

    static IReadOnlyList<Diagnostic> DiagnosticsIn(string source) =>
        Compilation.Create($"module M\n{Helpers}\n{source}").GetAllDiagnostics();

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) =>
        [..DiagnosticsIn(source).Where(static d => d.Severity == DiagnosticSeverity.Error)];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    static void AssertReports(ErrorCode code, string source) =>
        Assert.Contains(DiagnosticsIn(source), d => d.Code == code);

    // ---- Marking the call --------------------------------------------------

    [Fact]
    public void AThrowingCallNeedsTry() =>
        AssertReports(
            ErrorCode.ERR_MustUseTry,
            """
            fn probe() throws(IoError) -> String { return read("a") }
            """
        );

    [Fact]
    public void TryMarksIt() =>
        AssertClean(
            """
            fn probe() throws(IoError) -> String { return try read("a") }
            """
        );

    [Fact]
    public void OneTryCoversTheWholeExpression() =>
        AssertClean(
            """
            fn probe() throws(IoError) -> String { return try read("a") + read("b") }
            """
        );

    [Fact]
    public void TryOnSomethingThatCannotThrowIsAWarning() =>
        AssertReports(
            ErrorCode.WRN_NothingThrowsHere,
            """
            fn probe() -> String { return try plain("a") }
            """
        );

    // ---- Where the error goes ---------------------------------------------

    [Fact]
    public void AnErrorNeedsSomewhereToGo() =>
        AssertReports(
            ErrorCode.ERR_ThrowsNotDeclared,
            """
            fn probe() -> String { return try read("a") }
            """
        );

    [Fact]
    public void TryOptionalSwallowsIt() =>
        AssertClean(
            """
            fn probe() -> String? { return try? read("a") }
            """
        );

    [Fact]
    public void TryOptionalMakesItOptional() =>
        AssertReports(
            ErrorCode.ERR_TypeMismatch,
            """
            fn probe() -> String { return try? read("a") }
            """
        );

    [Fact]
    public void TryForceAssertsItCannotHappen() =>
        AssertClean(
            """
            fn probe() -> String { return try! read("a") }
            """
        );

    [Fact]
    public void AnInlineCatchSuppliesAValue() =>
        AssertClean(
            """
            fn probe() -> String { return try read("a") catch { "" } }
            """
        );

    [Fact]
    public void AnInlineCatchValueIsChecked() =>
        AssertReports(
            ErrorCode.ERR_TypeMismatch,
            """
            fn probe() -> String { return try read("a") catch { 1 } }
            """
        );

    [Fact]
    public void ADoBlockCatchesIt() =>
        AssertClean(
            """
            fn probe() -> Int {
                var n = 0
                do {
                    let t = try read("a")
                    n = 1
                } catch {
                    n = 2
                }
                return n
            }
            """
        );

    [Fact]
    public void ACatchMatchesWhatTheBodyThrows() =>
        AssertClean(
            """
            fn probe() -> Int {
                var n = 0
                do {
                    let t = try read("a")
                } catch .NotFound {
                    n = 1
                } catch {
                    n = 2
                }
                return n
            }
            """
        );

    [Fact]
    public void CatchesThatMissAnErrorLeaveItWithNowhereToGo() {
        var errors = ErrorsIn(
            """
            fn probe() -> Int {
                do { let s = try read("a"); return 1 } catch .NotFound { return 0 }
            }
            """
        );

        var error = Assert.Single(errors);
        Assert.Equal(ErrorCode.ERR_ThrowsNotDeclared, error.Code);
        Assert.Contains("'.Denied'", error.Message);
    }

    [Fact]
    public void AGuardedCatchCoversNothing() =>
        AssertReports(
            ErrorCode.ERR_ThrowsNotDeclared,
            """
            fn probe(_ b: Bool) -> Int {
                do { let s = try read("a"); return 1 } catch where b { return 0 }
            }
            """
        );

    [Fact]
    public void CatchesThatCoverEveryCaseNeedNoBareCatch() =>
        AssertClean(
            """
            fn probe() -> Int {
                do { let s = try read("a"); return 1 } catch .NotFound { return 0 } catch .Denied { return 1 }
            }
            """
        );

    [Fact]
    public void WhatTheCatchesMissPropagatesFromAThrowingFunction() =>
        AssertClean(
            """
            fn probe() throws(IoError) -> Int {
                do { let s = try read("a"); return 1 } catch .NotFound { return 0 }
            }
            """
        );

    [Fact]
    public void WhatTheCatchesMissHasToFitTheDeclaredError() =>
        AssertReports(
            ErrorCode.ERR_ErrorTypeDoesNotFit,
            """
            union AppError { Io }
            fn probe() throws(AppError) -> Int {
                do { let s = try read("a"); return 1 } catch .NotFound { return 0 }
            }
            """
        );

    [Fact]
    public void WhatTheCatchesMissGoesToAnEnclosingDo() =>
        AssertClean(
            """
            fn probe() -> Int {
                do {
                    do { let s = try read("a"); return 1 } catch .NotFound { return 0 }
                } catch {
                    return 1
                }
            }
            """
        );

    [Fact]
    public void ARethrowsFunctionMayNotLetItsOwnErrorPastItsCatches() =>
        AssertReports(
            ErrorCode.ERR_RethrowsThrowsItsOwn,
            """
            fn probe(_ f: () throws -> Int) rethrows -> Int {
                do { let s = try read("a"); return 1 } catch .NotFound { return 0 }
            }
            """
        );

    // ---- throw -------------------------------------------------------------

    [Fact]
    public void ThrowNeedsADeclaration() =>
        AssertReports(
            ErrorCode.ERR_ThrowsNotDeclared,
            """
            fn probe() { throw IoError.Denied }
            """
        );

    [Fact]
    public void ThrowIsFineWhenDeclared() =>
        AssertClean(
            """
            fn probe() throws(IoError) { throw IoError.Denied }
            """
        );

    [Fact]
    public void BareThrowsMeansAnyError() =>
        AssertClean(
            """
            fn probe() throws -> String { return try read("a") }
            """
        );

    // ---- async -------------------------------------------------------------

    [Fact]
    public void AnAsyncCallNeedsAwait() =>
        AssertReports(
            ErrorCode.ERR_MustUseAwait,
            """
            async fn probe() -> String { return fetch("a") }
            """
        );

    [Fact]
    public void AwaitMarksIt() =>
        AssertClean(
            """
            async fn probe() -> String { return await fetch("a") }
            """
        );

    [Fact]
    public void AsyncOutsideAnAsyncContextIsAnError() =>
        AssertReports(
            ErrorCode.ERR_AwaitOutsideAsync,
            """
            fn probe() -> String { return await fetch("a") }
            """
        );

    [Fact]
    public void BothEffectsAtOnce() =>
        AssertClean(
            """
            async fn probe() throws(IoError) -> String {
                let body = await fetch("a")
                return try read(body)
            }
            """
        );

    // ---- Closures carry their own ------------------------------------------

    [Fact]
    public void AClosureIsItsOwnEffectContext() =>
        AssertReports(
            ErrorCode.ERR_ThrowsNotDeclared,
            """
            fn probe() throws(IoError) -> String {
                let f = () => { return try read("a") }
                return try read("b")
            }
            """
        );

    [Fact]
    public void AThrowingClosureParameterAllowsIt() =>
        AssertClean(
            """
            fn withLock<T>(_ body: () throws(IoError) -> T) throws(IoError) -> T {
                return try body()
            }
            fn probe() throws(IoError) -> String {
                return try withLock(() => { return try read("a") })
            }
            """
        );

    /// <summary>
    ///     What a closure throws is its caller's: the closure, or the body,
    ///     it is written in does not throw it, and its inferred type does not
    ///     say it does.
    /// </summary>
    [Fact]
    public void AClosureWithinAClosureThrowsForItselfAlone() =>
        AssertClean(
            """
            fn thrown(_ body: () throws(IoError) -> String) -> String {
                do {
                    return try body()
                } catch {
                    return "failed"
                }
            }
            fn plain<T>(_ body: () -> T) -> T {
                return body()
            }
            fn probe() -> String {
                return plain { thrown { try read("a") } }
            }
            """
        );

    // ---- Error types and From ----------------------------------------------

    [Fact]
    public void AnErrorThatDoesNotFitIsReported() =>
        AssertReports(
            ErrorCode.ERR_ErrorTypeDoesNotFit,
            """
            union AppError { Io, Other }
            fn probe() throws(AppError) -> String { return try read("a") }
            """
        );

    [Fact]
    public void AFromConformanceConvertsIt() =>
        AssertClean(
            """
            union AppError { Io, Other }
            extension AppError: From<IoError> { }
            fn probe() throws(AppError) -> String { return try read("a") }
            """
        );

    [Fact]
    public void BareThrowsTakesAnyError() =>
        AssertClean("fn probe() throws -> String { return try read(\"a\") }");

    [Fact]
    public void InsideADoTheCatchesDecide() =>
        AssertClean(
            """
            union AppError { Io, Other }
            fn probe() throws(AppError) -> String {
                do {
                    return try read("a")
                } catch {
                    throw AppError.Io
                }
            }
            """
        );

    [Fact]
    public void AThrowStatementIsCheckedToo() =>
        AssertReports(
            ErrorCode.ERR_ErrorTypeDoesNotFit,
            """
            union AppError { Io, Other }
            fn probe() throws(AppError) { throw IoError.Denied }
            """
        );
}
