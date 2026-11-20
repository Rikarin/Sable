namespace Sable.Symbols.Tests;

/// <summary>
///     <c>associatedtype</c>: a type the conformer supplies by name rather than
///     positionally, per docs/03 §3.2.
/// </summary>
public sealed class AssociatedTypeTests {
    const string Container = """
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

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{Container}\n{source}")
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

    [Fact]
    public void AnAliasSatisfiesIt() =>
        AssertClean(
            """
            fn probe(_ bag: WordBag) -> Bool { return bag.holds("x") }
            """
        );

    [Fact]
    public void ARequirementIsCheckedAgainstTheBinding() {
        var errors = ErrorsIn(
            """
            struct Wrong: Container {
                alias Item = String
                fn first() -> Int? { return none }
                fn holds(_ item: Int) -> Bool { return true }
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_RequirementSignatureMismatch);
    }

    [Fact]
    public void NotSayingWhatItIsIsAnError() {
        var errors = ErrorsIn(
            """
            struct Silent: Container { }
            """
        );
        Assert.Contains(
            errors,
            static e =>
                e.Code == ErrorCode.ERR_DoesNotConform
                && e.Message.Contains("Item", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void AWitnessSaysWhatItIs() =>
        AssertClean(
            """
            struct Inferred: Container {
                fn first() -> String? { return none }
                fn holds(_ item: String) -> Bool { return true }
            }
            fn probe<C: Container>(_ c: C) -> C.Item? => c.first()
            fn use(_ i: Inferred) -> String? => probe(i)
            """
        );

    [Fact]
    public void WitnessesThatDisagreeAreAMismatch() {
        var errors = ErrorsIn(
            """
            struct Torn: Container {
                fn first() -> String? { return none }
                fn holds(_ item: Int) -> Bool { return true }
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_RequirementSignatureMismatch);
    }

    [Fact]
    public void ADefaultStandsIn() =>
        AssertClean(
            """
            protocol Indexed {
                associatedtype Index = Int
                fn at(_ i: Index) -> String
            }
            struct Row: Indexed {
                fn at(_ i: Int) -> String { return "" }
            }
            """
        );

    [Fact]
    public void AConstrainedParameterNamesIt() =>
        AssertClean(
            """
            fn firstOrDefault<C: Container>(_ c: C, _ fallback: C.Item) -> C.Item {
                guard let value = c.first() else { return fallback }
                return value
            }
            fn probe(_ bag: WordBag) -> String { return firstOrDefault(bag, "none") }
            """
        );

    [Fact]
    public void AnUnknownAssociatedNameIsReported() {
        var errors = ErrorsIn(
            """
            fn probe<C: Container>(_ c: C, _ x: C.Missing) { }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_TypeNotFound);
    }

    [Fact]
    public void TheBindingFlowsThroughACall() {
        // 'firstOrDefault(bag, 1)' must fail: Item is String for a WordBag.
        var errors = ErrorsIn(
            """
            fn firstOrDefault<C: Container>(_ c: C, _ fallback: C.Item) -> C.Item {
                guard let value = c.first() else { return fallback }
                return value
            }
            fn probe(_ bag: WordBag) -> String { return firstOrDefault(bag, 1) }
            """
        );
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void TwoParametersHaveDifferentAssociatedTypes() {
        // Nothing says two containers hold the same thing, so B's item is not
        // an A.Item.
        var errors = ErrorsIn(
            """
            fn mix<A: Container, B: Container>(_ a: A, _ b: B) -> A.Item? {
                return b.first()
            }
            """
        );
        Assert.Contains(errors, static e => e.Message.Contains("B.Item", StringComparison.Ordinal));
    }

    [Fact]
    public void ParametersOfTheSameParameterAgree() =>
        AssertClean(
            """
            fn pick<A: Container, B: Container>(_ a: A, _ b: B, _ x: A.Item) -> Bool {
                return a.holds(x)
            }
            """
        );

    [Fact]
    public void PassingOneParametersItemToAnotherIsAnError() {
        var errors = ErrorsIn(
            """
            fn cross<A: Container, B: Container>(_ a: A, _ b: B, _ x: A.Item) -> Bool {
                return b.holds(x)
            }
            """
        );
        Assert.NotEmpty(errors);
    }

    const string Both = """
                        struct Numbers: Container {
                            alias Item = Int
                            fn first() -> Int? { return none }
                            fn holds(_ item: Int) -> Bool { return true }
                        }
                        fn both<A: Container, B: Container>(_ a: A, _ b: B, _ x: A.Item, _ y: B.Item) { }
                        """;

    [Fact]
    public void EachParameterResolvesThroughItsOwnArgument() =>
        AssertClean($"{Both}\nfn good(_ w: WordBag, _ n: Numbers) {{ both(w, n, \"word\", 1) }}");

    [Fact]
    public void SwappingTheirItemsIsAnError() =>
        Assert.NotEmpty(ErrorsIn($"{Both}\nfn bad(_ w: WordBag, _ n: Numbers) {{ both(w, n, 1, \"word\") }}"));

    [Fact]
    public void AGenericCallerKeepsItDependent() =>
        AssertClean(
            """
            fn firstOrDefault<C: Container>(_ c: C, _ fallback: C.Item) -> C.Item {
                guard let value = c.first() else { return fallback }
                return value
            }
            fn relay<D: Container>(_ d: D, _ x: D.Item) -> D.Item { return firstOrDefault(d, x) }
            """
        );

    [Fact]
    public void AnAssociatedTypesConstraintsAreUsable() =>
        AssertClean(
            """
            protocol Keyed {
                associatedtype Key: Container
                fn key() -> Key
            }
            fn probe<K: Keyed>(_ k: K, _ x: K.Key.Item) -> Bool { return k.key().holds(x) }
            """
        );

    [Fact]
    public void ItDisplaysThroughItsBase() {
        var errors = ErrorsIn(
            """
            fn wrong<C: Container>(_ c: C) -> Int { return c.first()! }
            """
        );
        Assert.Contains(errors, static e => e.Message.Contains("C.Item", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A type that inherits from itself is an error (S3026), but binding
    ///     goes on: appending one to an array asks for its associated types,
    ///     and the walk up its bases came back round to it until the stack
    ///     overflowed. Found by the compiler fuzzer, from 'struct AddHeader:
    ///     AddHeader: Middleware'.
    /// </summary>
    [Theory]
    [InlineData("struct Loop: Loop { }", "Loop")]
    [InlineData("struct RingA: RingB { }\nstruct RingB: RingA { }", "RingA")]
    [InlineData("class RingA: RingB { init() { } }\nclass RingB: RingA { init() { } }", "RingA")]
    public void AskingATypeThatInheritsFromItselfEnds(string types, string type) {
        var errors = ErrorsIn(
            $$"""
              {{types}}
              fn grow() {
                  var items: {{type}}[>..] = []
                  items.append({{type}}())
              }
              """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_CircularInheritance);
    }
}
