using Sable.Symbols;

namespace Sable.Symbols.Tests;

/// <summary>
/// <c>Self</c> in a protocol: the conforming type, wherever the member is
/// reached from.
/// </summary>
public sealed class SelfTypeTests
{
    private const string Comparable = """
        protocol Comparable {
            fn lessThan(_ other: Self) -> Bool
        }
        struct Version: Comparable {
            var major: Int
            fn lessThan(_ other: Version) -> Bool { return major < other.major }
        }
        """;

    private static IReadOnlyList<Diagnostic> ErrorsIn(string source) =>
        [.. Compilation.Create($"module M\n{Comparable}\n{source}").GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)];

    private static void AssertClean(string source)
    {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void AConformerSatisfiesSelfWithItself() => AssertClean("""
        fn probe(_ a: Version, _ b: Version) -> Bool { return a.lessThan(b) }
        """);

    [Fact]
    public void SelfIsTheTypeParameterThroughAConstraint() => AssertClean("""
        fn probe<T: Comparable>(_ a: T, _ b: T) -> Bool { return a.lessThan(b) }
        """);

    [Fact]
    public void SelfIsTheOpaqueTypeThroughSome() => AssertClean("""
        fn probe(_ a: some Comparable, _ b: Version) -> Bool { return a.lessThan(a) }
        """);

    [Fact]
    public void ADifferentTypeDoesNotSatisfySelf()
    {
        var errors = ErrorsIn("""
            struct Other { var n: Int }
            fn probe<T: Comparable>(_ a: T, _ b: Other) -> Bool { return a.lessThan(b) }
            """);
        Assert.Contains(errors, e => e.Code == ErrorCode.ERR_NoMatchingOverload);
    }

    [Fact]
    public void AConformerWhoseSignatureUsesTheProtocolDoesNotMatch()
    {
        var errors = ErrorsIn("""
            struct Wrong: Comparable {
                fn lessThan(_ other: Int) -> Bool { return true }
            }
            """);
        Assert.Contains(errors, e => e.Code == ErrorCode.ERR_RequirementSignatureMismatch);
    }

    // ---- Generic unions ----------------------------------------------------

    private const string Tree = """
        union Tree<Value> {
            Leaf
            Node(value: Value, left: Tree<Value>, right: Tree<Value>)
        }
        """;

    [Fact]
    public void AGenericUnionTakesItsArgumentsFromThePayload()
    {
        var source = $"module M\n{Tree}\nfn probe() -> Tree<Int> "
            + "{ return Tree.Node(value: 1, left: Tree.Leaf, right: Tree.Leaf) }";

        var errors = Compilation.Create(source).GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void AMismatchedPayloadIsStillReported()
    {
        var source = $"module M\n{Tree}\nfn probe() -> Tree<Int> "
            + "{ return Tree.Node(value: \"x\", left: Tree.Leaf, right: Tree.Leaf) }";

        Assert.Contains(Compilation.Create(source).GetAllDiagnostics(),
            d => d.Code == ErrorCode.ERR_TypeMismatch);
    }
}
