namespace Sable.Symbols.Tests;

/// <summary>
///     Slices (SYNTAX.md §4, §11): every kind of array can be viewed as a
///     <c>T[:]</c>, and a range of an array's elements is read as one.
/// </summary>
public sealed class SliceTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static string TypeOf(string declarations, string expression) {
        var compilation = Compilation.Create($"module M\n{declarations}\nfn probe() {{ let value = {expression} }}");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var tree = compilation.SyntaxTrees[0];
        var value = tree.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Last().Declarator.Initializer!.Value;
        return compilation.GetSemanticModel(tree).GetTypeOf(value)!.ToDisplayString();
    }

    [Fact]
    public void EveryKindOfArrayIsASlice() =>
        AssertClean(
            """
            fn sum(_ xs: Int[:]) -> Int { return xs.count }
            fn f(_ grow: Int[>..], _ fixed: Int[], _ shrink: Int[>..<], _ eight: Int[8], _ inline: Int[..4]) -> Int {
                let viewed: Int[:] = grow
                return sum(grow) + sum(fixed) + sum(shrink) + sum(eight) + sum(inline) + viewed.count
            }
            """
        );

    [Fact]
    public void ASliceOfOtherElementsIsNot() {
        var error = Assert.Single(ErrorsIn("fn sum(_ xs: Int[:]) {}\nfn f(_ xs: String[>..]) { sum(xs) }"));
        Assert.Equal(ErrorCode.ERR_NoMatchingOverload, error.Code);
    }

    [Fact]
    public void ASliceIsNotAnotherKind() {
        var error = Assert.Single(ErrorsIn("fn grow(_ xs: Int[>..]) {}\nfn f(_ xs: Int[:]) { grow(xs) }"));
        Assert.Equal(ErrorCode.ERR_NoMatchingOverload, error.Code);
    }

    [Fact]
    public void AGenericSliceInfersItsElement() =>
        AssertClean(
            """
            fn first<T>(_ xs: T[:]) -> T { return xs[0] }
            fn f(_ xs: String[>..]) -> String { return first(xs) }
            """
        );

    [Fact]
    public void TheArraysOwnKindIsTheCloserOverload() =>
        AssertClean(
            """
            fn pick(_ xs: Int[:]) -> Int { return 1 }
            fn pick(_ xs: Int[>..]) -> Bool { return true }
            fn f(_ xs: Int[>..], _ ys: Int[]) -> Bool {
                let other: Int = pick(ys)
                return pick(xs)
            }
            """
        );

    [Theory]
    [InlineData("xs[1..<3]")]
    [InlineData("xs[1...3]")]
    [InlineData("xs[2...]")]
    [InlineData("xs[..<2]")]
    [InlineData("xs[...2]")]
    [InlineData("xs[1..<3][0...]")]
    public void ARangeOfElementsIsASlice(string expression) =>
        Assert.Equal("String[:]", TypeOf("let xs: String[>..] = []", expression));

    /// <summary>A range of a 'var' array is a place, stored to and passed inout, by any range (#301).</summary>
    [Theory]
    [InlineData("var xs: Int[>..] = [1, 2]; xs[0..<1] = [3]")]
    [InlineData("var xs: Int[] = [1, 2]; xs[0..<1] = [3]")]
    [InlineData("var xs: Int[] = [1, 2]; xs[^1...] = [3]")]
    [InlineData("var xs: Int[>..<] = [1, 2]; g(&xs[1..<^0])")]
    [InlineData("var xs: Int[] = [1, 2]; g(&xs[...0])")]
    [InlineData("var xs: Int[>..] = [1, 2]; xs[1...].sort()")]
    public void ARangeOfAVarArrayIsAPlace(string body) =>
        Assert.Empty(ErrorsIn($"fn g(_ s: inout Int[:]) {{ }}\nfn f() {{ {body} }}"));

    /// <summary>A range, or an element, of a 'let' array or of one a call gives is no place (S3010).</summary>
    [Theory]
    [InlineData("let xs: Int[>..] = [1, 2]; xs[0..<1] = [3]")]
    [InlineData("let xs: Int[] = [1, 2]; xs[0] = 3")]
    [InlineData("let xs: Int[] = [1, 2]; g(&xs[^1...])")]
    [InlineData("g(&make()[0..<1])")]
    public void ARangeOfAnArrayThatCannotChangeIsNotAPlace(string body) {
        var error = Assert.Single(ErrorsIn($"fn g(_ s: inout Int[:]) {{ }}\nfn make() -> Int[>..] {{ return [1] }}\nfn f() {{ {body} }}"));
        Assert.Equal(ErrorCode.ERR_NotAssignable, error.Code);
    }

    [Fact]
    public void AnIndexIsStillAnInt() {
        var error = Assert.Single(ErrorsIn("fn f(_ xs: Int[>..]) -> Int { return xs[\"a\"] }"));
        Assert.Equal(ErrorCode.ERR_TypeMismatch, error.Code);
    }
}
