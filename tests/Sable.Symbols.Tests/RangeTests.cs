namespace Sable.Symbols.Tests;

/// <summary>
///     Ranges: <c>a..&lt;b</c> is a <c>Range&lt;Bound&gt;</c> and <c>a...b</c> a
///     <c>ClosedRange&lt;Bound&gt;</c>, both declared in Sable, in
///     <c>lib/Sable.Core/Range.sb</c> and <c>ClosedRange.sb</c>, and found there by the binder.
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

    [Fact]
    public void ARangeOfCharactersOrOfAnyIntegerIsIterated() =>
        AssertClean("""
                    fn f(_ u: ClosedRange<U8>, _ i: Range<I128>) {
                        for c in 'a'...'z' { let d: Char = c }
                        for x in u { let y: U8 = x }
                        for x in i { let y: I128 = x }
                    }
                    """);

    /// <summary>The one-sided ranges are types of their own, with the one bound each has.</summary>
    [Fact]
    public void OneSidedRangesAreTheirOwnTypes() =>
        AssertClean("""
                    fn upTo() -> PartialRangeUpTo<Int> { return ..<10 }
                    fn through() -> PartialRangeThrough<U8> { return ...9 }
                    fn from(_ n: Int) -> PartialRangeFrom<Int> { return n... }
                    fn probe() -> Bool {
                        let r = from(3)
                        for i in r { if i > 5 { break } }
                        return upTo().contains(9) && through().upperBound == 9 && r.lowerBound == 3
                    }
                    """);

    [Fact]
    public void ARangeOpenAtTheStartIsNotIterated() {
        var error = Assert.Single(ErrorsIn("fn f() { for i in ..<5 { } }"));
        Assert.Equal(ErrorCode.ERR_NotIterable, error.Code);
        Assert.Contains("no lower bound", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHalfOpenRangeNeedsItsUpperBound() {
        var error = Assert.Single(ErrorsIn("fn f() -> Bool { let r = (5..<)\nreturn true }"));
        Assert.Equal(ErrorCode.ERR_HalfOpenRangeWithoutEnd, error.Code);
    }

    /// <summary>'count' is declared 'where Bound == Int', so a range of anything else has none.</summary>
    [Fact]
    public void ARangeOfIntsCountsItsValues() {
        AssertClean("""
                    fn f(_ r: Range<Int>, _ c: ClosedRange<Int>) -> Int { return r.count + c.count + (0..<5).count }
                    """);

        var error = Assert.Single(ErrorsIn("fn f(_ r: Range<F64>) -> Int { return r.count }"));
        Assert.Equal(ErrorCode.ERR_DoesNotConform, error.Code);
        Assert.Contains("'Range<F64>' has 'count' only where 'Bound == Int'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrideCountsBySteps() =>
        AssertClean("""
                    fn f() -> Int {
                        var total = 0
                        for i in stride(from: 0, to: 10, by: 3) { total = total + i }
                        for i in stride(from: 10, through: 0, by: -2) { total = total + i }
                        return total
                    }
                    """);
}
