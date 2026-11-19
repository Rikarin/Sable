namespace Sable.Symbols.Tests;

/// <summary>
///     A protocol's own <c>where</c> requirements, written on an associated
///     type (<c>associatedtype Iter: Iterator where Iter.Element == Element</c>)
///     or on the protocol: they hold of every conformer, and of every type the
///     protocol constrains, read through it.
/// </summary>
public sealed class ProtocolRequirementTests {
    const string Protocols = """
                             protocol Source {
                                 associatedtype Element
                                 mutating fn next() -> Element?
                             }
                             protocol Feed {
                                 associatedtype Element
                                 associatedtype Iter: Source where Iter.Element == Element
                                 fn start() -> Iter
                             }
                             protocol CountedFeed: Feed {
                                 var count: Int { get }
                             }
                             struct Counter: Source {
                                 alias Element = Int
                                 var i: Int
                                 mutating fn next() -> Int? { return none }
                             }
                             struct Words: Source {
                                 alias Element = String
                                 mutating fn next() -> String? { return none }
                             }
                             """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{Protocols}\n{source}")
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
    public void AGenericParametersIteratorGivesItsElements() =>
        AssertClean(
            """
            fn first<F: Feed>(_ feed: F) -> F.Element? {
                var iterator = feed.start()
                return iterator.next()
            }
            """
        );

    [Fact]
    public void WithoutTheRequirementTheyAreDifferentTypes() {
        var errors = ErrorsIn(
            """
            protocol LooseFeed {
                associatedtype Element
                associatedtype Iter: Source
                fn start() -> Iter
            }
            fn first<F: LooseFeed>(_ feed: F) -> F.Element? {
                var iterator = feed.start()
                return iterator.next()
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_TypeMismatch);
    }

    [Fact]
    public void ARequirementOnTheProtocolSaysTheSame() =>
        AssertClean(
            """
            protocol OtherFeed where Iter.Element == Element {
                associatedtype Element
                associatedtype Iter: Source
                fn start() -> Iter
            }
            fn first<F: OtherFeed>(_ feed: F) -> F.Element? {
                var iterator = feed.start()
                return iterator.next()
            }
            """
        );

    [Fact]
    public void ARefiningProtocolCarriesTheRequirement() =>
        AssertClean(
            """
            fn first<F: CountedFeed>(_ feed: F) -> F.Element? {
                var iterator = feed.start()
                let element: F.Element? = iterator.next()
                return element
            }
            """
        );

    [Fact]
    public void ItCombinesWithTheFunctionsOwnRequirements() =>
        AssertClean(
            """
            fn total<F: Feed>(_ feed: F) -> Int where F.Element == Int {
                var sum = 0
                var iterator = feed.start()
                while let x = iterator.next() {
                    sum = sum + x
                }
                return sum
            }
            """
        );

    [Fact]
    public void InsideAProtocolExtensionItHoldsAsWritten() =>
        AssertClean(
            """
            extension Feed {
                fn first() -> Element? {
                    var iterator = start()
                    return iterator.next()
                }
            }
            """
        );

    [Fact]
    public void AConformersElementIsInferredFromItsIterator() =>
        AssertClean(
            """
            struct Three: Feed {
                fn start() -> Counter { return Counter { i: 0 } }
            }
            fn probe() -> Int {
                let element: Three.Element = 3
                return element
            }
            """
        );

    [Fact]
    public void AConformerWhoseElementIsNotItsIteratorsDoesNotConform() {
        var errors = ErrorsIn(
            """
            struct Mixed: Feed {
                alias Element = Int
                fn start() -> Words { return Words() }
            }
            """
        );
        var error = Assert.Single(errors);
        Assert.Equal(ErrorCode.ERR_DoesNotConform, error.Code);
        Assert.Contains("'Iter.Element == Element': here they are 'String' and 'Int'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConformanceRequirementOnTheProtocolIsChecked() {
        var errors = ErrorsIn(
            """
            protocol Named { fn name() -> String }
            protocol NamedFeed {
                associatedtype Iter: Source where Iter.Element: Named
                fn start() -> Iter
            }
            struct Bad: NamedFeed {
                fn start() -> Counter { return Counter { i: 0 } }
            }
            """
        );
        var error = Assert.Single(errors);
        Assert.Equal(ErrorCode.ERR_DoesNotConform, error.Code);
        Assert.Contains("'Int' does not conform to 'Named'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConformanceRequirementGivesTheMembersThroughAParameter() =>
        AssertClean(
            """
            protocol Named { fn name() -> String }
            protocol NamedFeed {
                associatedtype Iter: Source where Iter.Element: Named
                fn start() -> Iter
            }
            fn firstName<F: NamedFeed>(_ feed: F) -> String {
                var iterator = feed.start()
                guard let first = iterator.next() else { return "" }
                return first.name()
            }
            """
        );

    [Fact]
    public void AnExtensionsWhereClauseBringsTheProtocolsRequirements() =>
        AssertClean(
            """
            extension Feed where Element: Feed {
                fn firstOfFirst() -> Element.Element? {
                    var outer = start()
                    guard let inner = outer.next() else { return none }
                    var iterator = inner.start()
                    return iterator.next()
                }
            }
            """
        );

    [Fact]
    public void ARecursiveRequirementEnds() =>
        AssertClean(
            """
            protocol Sliced {
                associatedtype Element
                associatedtype Slice: Sliced where Slice.Element == Element
                fn slice() -> Slice
                fn first() -> Element
            }
            fn deep<S: Sliced>(_ s: S) -> S.Element {
                return s.slice().slice().slice().first()
            }
            """
        );
}
