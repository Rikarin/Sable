namespace Sable.Symbols.Tests;

/// <summary>
///     <c>@builtin("name")</c>: Sable.Core's way of declaring a member whose
///     body is a SIL builtin. What the declaration alone can be checked for
///     is checked here; its agreement with the builtin's signature is SIL
///     lowering's to check (Sable.SIL.Tests).
/// </summary>
public sealed class BuiltinTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(params string[] sources) => [
        ..Compilation.Create(sources)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static Diagnostic Single(ErrorCode code, params string[] sources) {
        var errors = ErrorsIn(sources);
        Assert.True(errors.Count == 1, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        Assert.Equal(code, errors[0].Code);
        return errors[0];
    }

    [Fact]
    public void TheCoresMembersAreBuiltinsOrSable() {
        var compilation = Compilation.Create("module M\nfn probe(_ s: String) -> Bool { return s.isEmpty || s.trimmed().count == 0 }");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);

        var count = compilation.Core.String.GetMembers("count").OfType<FieldSymbol>().Single();
        Assert.Equal("string_count", count.Builtin);
        Assert.False(count.IsAbstract);

        var trimmed = compilation.Core.String.GetMembers("trimmed").OfType<FunctionSymbol>().Single(static f => f.Parameters.IsEmpty);
        Assert.Equal("string_trimmed", trimmed.Builtin);
        Assert.False(trimmed.IsAbstract);

        // 'isEmpty' is Sable over 'count': no builtin of its own.
        Assert.Null(compilation.Core.String.GetMembers("isEmpty").OfType<FieldSymbol>().Single().Builtin);
    }

    [Fact]
    public void OnlySableCoreMayDeclareOne() {
        var error = Single(
            ErrorCode.ERR_BuiltinOutsideCore,
            """
            module M
            extension String {
                @builtin("string_trimmed")
                public fn tidied() -> String
            }
            """
        );
        Assert.Contains("only Sable.Core", error.Message);
    }

    [Theory]
    [InlineData("@builtin public fn a() -> String", "one string literal")]
    [InlineData("@builtin(\"string_trimmed\", \"string_count\") public fn a() -> String", "one string literal")]
    [InlineData("@builtin(trimmed) public fn a() -> String", "one string literal")]
    [InlineData("@builtin(\"\") public fn a() -> String", "one string literal")]
    [InlineData("@builtin(\"string_trimmed\") public fn a() -> String { return self }", "has no body")]
    [InlineData("@builtin(\"string_trimmed\") public fn a<T>() -> String", "cannot be generic")]
    [InlineData("@builtin(\"string_trimmed\") @builtin(\"string_trimmed\") public fn a() -> String", "names one builtin")]
    [InlineData("@builtin(\"string_count\") public var n: Int { get { return 0 } }", "bodiless getter")]
    [InlineData("@builtin(\"string_count\") public static var n: Int { get }", "bodiless getter")]
    public void AMalformedOneIsAnError(string member, string message) {
        var error = Single(
            ErrorCode.ERR_MalformedBuiltin,
            $"module Sable.Core\nextension String {{\n    {member}\n}}",
            "module M"
        );
        Assert.Contains(message, error.Message);
    }
}
