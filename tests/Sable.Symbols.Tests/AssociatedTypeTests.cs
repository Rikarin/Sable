using Sable.Symbols;

namespace Sable.Symbols.Tests;

/// <summary>
/// <c>associatedtype</c>: a type the conformer supplies by name rather than
/// positionally, per docs/03 §3.2.
/// </summary>
public sealed class AssociatedTypeTests
{
    private const string Container = """
        protocol Container {
            associatedtype Item
            fn first() -> Item?
            fn holds(_ item: Item) -> Bool
        }
        struct WordBag: Container {
            alias Item = String
            var words: String[>..]
            fn first() -> String? { return none }
            fn holds(_ item: String) -> Bool { return words.contains(item) }
        }
        """;

    private static IReadOnlyList<Diagnostic> ErrorsIn(string source) =>
        [.. Compilation.Create($"module M\n{Container}\n{source}").GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)];

    private static void AssertClean(string source)
    {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void AnAliasSatisfiesIt() => AssertClean("""
        fn probe(_ bag: WordBag) -> Bool { return bag.holds("x") }
        """);

    [Fact]
    public void ARequirementIsCheckedAgainstTheBinding()
    {
        var errors = ErrorsIn("""
            struct Wrong: Container {
                alias Item = String
                fn first() -> Int? { return none }
                fn holds(_ item: Int) -> Bool { return true }
            }
            """);
        Assert.Contains(errors, e => e.Code == ErrorCode.ERR_RequirementSignatureMismatch);
    }

    [Fact]
    public void NotSayingWhatItIsIsAnError()
    {
        var errors = ErrorsIn("""
            struct Silent: Container {
                fn first() -> String? { return none }
                fn holds(_ item: String) -> Bool { return true }
            }
            """);
        Assert.Contains(errors, e =>
            e.Code == ErrorCode.ERR_DoesNotConform
            && e.Message.Contains("Item", StringComparison.Ordinal));
    }

    [Fact]
    public void ADefaultStandsIn() => AssertClean("""
        protocol Indexed {
            associatedtype Index = Int
            fn at(_ i: Index) -> String
        }
        struct Row: Indexed {
            fn at(_ i: Int) -> String { return "" }
        }
        """);

    [Fact]
    public void AConstrainedParameterNamesIt() => AssertClean("""
        fn firstOrDefault<C: Container>(_ c: C, _ fallback: C.Item) -> C.Item {
            guard let value = c.first() else { return fallback }
            return value
        }
        fn probe(_ bag: WordBag) -> String { return firstOrDefault(bag, "none") }
        """);

    [Fact]
    public void AnUnknownAssociatedNameIsReported()
    {
        var errors = ErrorsIn("""
            fn probe<C: Container>(_ c: C, _ x: C.Missing) { }
            """);
        Assert.Contains(errors, e => e.Code == ErrorCode.ERR_TypeNotFound);
    }

    [Fact]
    public void TheBindingFlowsThroughACall()
    {
        // 'firstOrDefault(bag, 1)' must fail: Item is String for a WordBag.
        var errors = ErrorsIn("""
            fn firstOrDefault<C: Container>(_ c: C, _ fallback: C.Item) -> C.Item {
                guard let value = c.first() else { return fallback }
                return value
            }
            fn probe(_ bag: WordBag) -> String { return firstOrDefault(bag, 1) }
            """);
        Assert.NotEmpty(errors);
    }
}
