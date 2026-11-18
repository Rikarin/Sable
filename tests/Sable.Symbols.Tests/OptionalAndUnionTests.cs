namespace Sable.Symbols.Tests;

/// <summary>
///     Optionals and structural unions as the type checker sees them:
///     promotion through a literal or a union (docs/03 §3.7), union
///     normalisation (docs/03 §3.1), chains through optional members.
/// </summary>
public sealed class OptionalAndUnionTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    // ---- Promotion (#145) ---------------------------------------------------

    [Fact]
    public void AnArrayLiteralPromotesToAnOptionalArray() =>
        AssertClean(
            """
            fn f() {
                let x: Int[>..]? = [9]
                let y: U8[]? = [1, 2]
            }
            """
        );

    [Fact]
    public void AUnionMemberPromotesToAnOptionalUnion() =>
        AssertClean(
            """
            fn take(_ u: (Int | String)?) {}
            fn f() {
                let u: (Int | String)? = "x"
                take(3)
                take("y")
            }
            """
        );

    [Fact]
    public void AnOptionalUnionIsShownWithItsParentheses() {
        var error = Assert.Single(ErrorsIn("fn f() { let u: (Int | String)? = 1.5 }"));
        Assert.Contains("'(Int | String)?'", error.Message);
    }

    [Fact]
    public void AnArrayOfAUnionIsShownWithItsParentheses() {
        var error = Assert.Single(ErrorsIn("fn f() { let u: (Int | String)[] = 1 }"));
        Assert.Contains("'(Int | String)[]'", error.Message);
    }
}
