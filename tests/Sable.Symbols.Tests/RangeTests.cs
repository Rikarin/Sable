namespace Sable.Symbols.Tests;

/// <summary>
///     Ranges: <c>a..&lt;b</c> is a <c>Range&lt;Bound&gt;</c> and <c>a...b</c> a
///     <c>ClosedRange&lt;Bound&gt;</c>, both declared in Sable, in
///     <c>lib/Sable.Core/Range.sb</c>, and found there by the binder.
/// </summary>
public sealed class RangeTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void TheRangeTypesAreDeclaredInSable() {
        var compilation = Compilation.Create("module M");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var core = compilation.Core;

        foreach (var range in new[] { core.Range, core.ClosedRange }) {
            Assert.NotNull(range);
            Assert.False(range.IsSynthesized);
            Assert.Equal(TypeKind.Struct, range.TypeKind);
            Assert.Equal("Comparable", Assert.Single(Assert.Single(range.TypeParameters).Constraints).Name);
            Assert.All(
                new[] { "lowerBound", "upperBound", "isEmpty", "contains" },
                name => Assert.Contains(range.Members, m => m.Name == name && !m.IsSynthesized)
            );
        }
    }

    [Fact]
    public void HalfOpenAndClosedRangesAreTheirOwnTypes() =>
        AssertClean("""
                    fn halfOpen(_ n: Int) -> Range<Int> { return 0..<n }
                    fn closed(_ n: Int) -> ClosedRange<Int> { return 1...n }
                    fn ofStrings() -> Range<String> { return "a"..<"z" }
                    fn ofFloats() -> ClosedRange<F64> { return 0.0...1.0 }
                    """);

    [Fact]
    public void AClosedRangeIsNotAHalfOpenOne() {
        var errors = ErrorsIn("fn f() -> Range<Int> { return 1...5 }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_TypeMismatch);
    }

    [Fact]
    public void AnExpectedRangeTypeGivesTheBoundsTheirType() =>
        AssertClean("""
                    fn f() -> Range<I32> { return 0..<10 }
                    fn g() -> ClosedRange<U8> { return 1...9 }
                    """);

    [Fact]
    public void TheMembersAreSables() =>
        AssertClean("""
                    fn probe(_ r: Range<Int>, _ c: ClosedRange<Int>) -> Bool {
                        let sum: Int = r.lowerBound + r.upperBound + c.lowerBound + c.upperBound
                        return sum > 0 && !r.isEmpty && !c.isEmpty && r.contains(3) && c.contains(sum)
                    }
                    """);

    [Fact]
    public void ABoundMustBeComparable() {
        var errors = ErrorsIn("""
                              struct Point { var x: Int }
                              fn f() { let r = Point { x: 0 }..<Point { x: 1 } }
                              """);
        var error = Assert.Single(errors, static e => e.Code == ErrorCode.ERR_DoesNotConform);
        Assert.Contains("'Point' does not conform to 'Comparable'", error.Message);
    }

    [Fact]
    public void ARangeHeldInAVariableIsIterated() =>
        AssertClean("""
                    fn sum(_ r: Range<Int>, _ c: ClosedRange<I32>) -> Int {
                        var total = 0
                        for i in r { total = total + i }
                        for j in c { total = total + Int(j) }
                        return total
                    }
                    """);

    [Fact]
    public void OnlyARangeOfIntegersIsIterated() {
        var errors = ErrorsIn("""
                              fn f(_ r: ClosedRange<String>) {
                                  for s in r { }
                              }
                              """);
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NotIterable);
    }
}
