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

    /// <summary>
    ///     A builtin family — a name with a 'Self' segment — belongs in a
    ///     protocol's extension, where the conforming type fills the segment;
    ///     and a builtin there must be one, as nothing else says whose it is.
    /// </summary>
    [Theory]
    [InlineData("extension String {\n    @builtin(\"string_count_Self\")\n    public var n: Int { get }\n}", "only a protocol's extension")]
    [InlineData("public protocol Sized {\n    var n: Int { get }\n}\nextension Sized {\n    @builtin(\"string_count\")\n    public var n: Int { get }\n}", "names a family")]
    [InlineData("public protocol Summed {\n    operator + (lhs: Self, rhs: Self) -> Self\n}\nextension Summed {\n    @builtin(\"add\")\n    public operator + (lhs: Self, rhs: Self) -> Self\n}", "names a family")]
    [InlineData("extension String {\n    @builtin(\"string_concat\")\n    public operator + (lhs: String, rhs: String) -> String { return lhs }\n}", "has no body")]
    public void AFamilyBelongsInAProtocolsExtension(string core, string message) {
        var error = Single(ErrorCode.ERR_MalformedBuiltin, $"module Sable.Core\n{core}", "module M");
        Assert.Contains(message, error.Message);
    }

    /// <summary>
    ///     An operator can be a builtin, and a family's member in a protocol's
    ///     extension is the default for the requirement of the same name — a
    ///     property's too, which is not a second declaration of it.
    /// </summary>
    [Fact]
    public void AFamilyMeetsTheProtocolsRequirement() {
        var compilation = Compilation.Create("module M\nfn f() { }");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var additive = compilation.Core.Module.GetMembers("AdditiveArithmetic").OfType<NamedTypeSymbol>().Single();
        var plus = additive.GetMembers("+").OfType<FunctionSymbol>().Single(static f => f.Builtin is not null);
        Assert.Equal("add_Self", plus.Builtin);
        Assert.False(plus.IsAbstract);

        var fixedWidth = compilation.Core.Module.GetMembers("FixedWidthInteger").OfType<NamedTypeSymbol>().Single();
        var counts = fixedWidth.GetMembers("leadingZeroCount").OfType<FieldSymbol>().ToList();
        Assert.Equal(2, counts.Count);
        Assert.Contains(counts, static c => c.IsAbstract);
        Assert.Contains(counts, static c => c.Builtin == "count_leading_zeros_Self");
    }

    /// <summary>
    ///     A family is the default only the built-in types take: a type of
    ///     the program's own that conforms meets the requirement itself.
    /// </summary>
    [Fact]
    public void AFamilyIsNoDefaultForATypeOfTheProgramsOwn() {
        var errors = ErrorsIn(
            """
            module M
            struct Bits: Equatable {
                var value: Int
                operator == (lhs: Bits, rhs: Bits) -> Bool { return lhs.value == rhs.value }
            }
            extension Bits: AdditiveArithmetic {
                static var zero: Bits => Bits { value: 0 }
            }
            extension Bits: FixedWidthInteger { }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_DoesNotConform && e.Message.Contains("'leadingZeroCount", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, static e => e.Message.Contains("'zero", StringComparison.Ordinal));
    }
}
