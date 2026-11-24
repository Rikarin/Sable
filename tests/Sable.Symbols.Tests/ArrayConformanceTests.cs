namespace Sable.Symbols.Tests;

/// <summary>
///     Arrays conforming to protocols, by an array extension that names them
///     (<c>extension&lt;Element&gt; Element[:]: Sequence</c>), and what goes with
///     a conformance anywhere: protocol extension members, overloads across
///     protocols, and <c>Self</c> in a protocol's own extension.
/// </summary>
public sealed class ArrayConformanceTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{source}")
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
    public void EveryArrayIsASequenceOfItsElements() =>
        AssertClean(
            """
            fn total<S: Sequence>(_ values: S) -> Int where S.Element == Int, S.Failure == Never {
                var sum = 0
                for value in values {
                    sum += value
                }
                return sum
            }
            fn probe(_ grow: Int[>..], _ fixed: Int[], _ slice: Int[:]) -> Int {
                return total(grow) + total(fixed) + total(slice)
            }
            """
        );

    [Fact]
    public void AnArraysAssociatedTypesAreItsExtensions() =>
        AssertClean(
            """
            fn firstOf<C: RandomAccessCollection>(_ items: C) -> C.Element? {
                return items.startIndex < items.endIndex ? items[items.startIndex] : none
            }
            fn probe(_ words: String[>..]) -> String {
                let index: Int = words.startIndex
                return (firstOf(words) ?? "") + "\(index)"
            }
            """
        );

    [Fact]
    public void AConformanceOfItsOwnIsCheckedAndUsed() =>
        AssertClean(
            """
            protocol Sized { fn size() -> Int }
            extension<Element> Element[>..]: Sized {
                fn size() -> Int { return self.count }
            }
            fn measure<T: Sized>(_ value: T) -> Int { return value.size() }
            fn probe(_ values: Int[>..]) -> Int { return measure(values) }
            """
        );

    [Fact]
    public void AnArrayOfAnotherKindDoesNotConform() {
        var errors = ErrorsIn(
            """
            protocol Sized { fn size() -> Int }
            extension<Element> Element[>..]: Sized {
                fn size() -> Int { return self.count }
            }
            fn measure<T: Sized>(_ value: T) -> Int { return value.size() }
            fn probe(_ values: Int[]) -> Int { return measure(values) }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NoMatchingOverload);
    }

    [Fact]
    public void AConstrainedExtensionConformsOnlyTheArraysItsElementMeets() {
        const string Declarations = """
                                    protocol Shown { fn shown() -> String }
                                    extension<Element: Equatable> Element[>..]: Shown {
                                        fn shown() -> String { return "\(self.count)" }
                                    }
                                    fn show<T: Shown>(_ value: T) -> String { return value.shown() }
                                    struct Opaque { }
                                    """;
        AssertClean($"{Declarations}\nfn probe(_ values: Int[>..]) -> String {{ return show(values) }}");
        Assert.Contains(
            ErrorsIn($"{Declarations}\nfn probe(_ values: Opaque[>..]) -> String {{ return show(values) }}"),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );
    }

    [Fact]
    public void AMissingWitnessIsReported() {
        var errors = ErrorsIn(
            """
            protocol Sized { fn size() -> Int }
            extension<Element> Element[>..]: Sized { }
            """
        );
        var error = Assert.Single(errors);
        Assert.Equal(ErrorCode.ERR_DoesNotConform, error.Code);
        Assert.Contains("missing 'fn size() -> Int'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWitnessAnotherArrayExtensionDeclaresMeetsIt() =>
        AssertClean(
            """
            protocol Counted { var count: Int { get } }
            extension<Element> Element[>..]: Counted { }
            """
        );

    [Fact]
    public void AnArraysIndexingMeetsASubscriptByAnInt() {
        AssertClean(
            """
            protocol Indexed {
                associatedtype Item
                subscript(position: Int) -> Item { get }
            }
            extension<Element> Element[>..]: Indexed { alias Item = Element }
            """
        );

        var errors = ErrorsIn(
            """
            protocol Keyed {
                subscript(key: String) -> Int { get }
            }
            extension Int[>..]: Keyed { }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_RequirementSignatureMismatch && e.Message.Contains("takes an 'Int'", StringComparison.Ordinal));
    }

    [Fact]
    public void AProtocolExtensionsMembersAreAnArrays() =>
        AssertClean(
            """
            extension Sequence where Failure == Never {
                fn doubledCount() -> Int {
                    var n = 0
                    for _ in self {
                        n += 2
                    }
                    return n
                }
            }
            fn probe(_ values: Int[>..]) -> Int { return values.doubledCount() }
            """
        );

    [Fact]
    public void ATypesOwnMethodBeatsAProtocolExtensionsWithTheSameFit() {
        const string Declarations = """
                                    protocol Bag { fn size() -> Int }
                                    extension Bag {
                                        fn holds(_ value: Int) -> String { return "any bag" }
                                    }
                                    extension<Element> Element[>..]: Bag {
                                        fn size() -> Int { return self.count }
                                    }
                                    struct Sack: Bag {
                                        fn size() -> Int { return 0 }
                                        fn holds(value: Int) -> Bool { return false }
                                    }
                                    """;

        // The array's own 'contains(element:)' and the protocol's
        // 'holds(_:)' fit an unlabelled argument alike; the array's, or the
        // struct's own, is the one, with nothing ambiguous.
        AssertClean(
            $$"""
              {{Declarations}}
              extension Bag {
                  fn contains(_ value: Int) -> String { return "any bag" }
              }
              fn probe(_ values: Int[>..], _ sack: Sack) -> Bool {
                  return values.contains(3) || sack.holds(3)
              }
              """
        );
    }

    [Fact]
    public void AnArrayOfOneElementTypesOwnMemberBeatsTheGenericOne() =>
        // 'extension U8[:]' over 'extension<Element> Element[:]': a byte
        // array's own search is chosen where both fit, with nothing ambiguous;
        // generic code, knowing only Element, still calls the generic one.
        AssertClean(
            """
            extension<Element: Equatable> Element[:] {
                fn probe(_ value: Element) -> Int { return 1 }
            }
            extension U8[:] {
                fn probe(_ value: U8) -> Bool { return true }
            }
            fn bytes(_ values: U8[>..]) -> Bool {
                return values.probe(2)
            }
            fn numbers(_ values: Int[>..]) -> Int {
                return values.probe(2)
            }
            fn anything<T: Equatable>(_ values: T[:], _ value: T) -> Int {
                return values.probe(value)
            }
            """
        );

    [Fact]
    public void AnArrayMemberThatAsksMoreOfItsElementBeatsOneThatAsksLess() =>
        // SIMDScalar refines Comparable, and so Equatable: its member is
        // chosen for an Int's array, a refinement's over both, the generic
        // one where the element is only Equatable.
        AssertClean(
            """
            protocol Marked: SIMDScalar { }
            extension I8: Marked { }
            extension<Element: Equatable> Element[:] {
                fn probe(_ value: Element) -> Int { return 1 }
            }
            extension<Element: SIMDScalar> Element[:] {
                fn probe(_ value: Element) -> Bool { return true }
            }
            extension<Element: Marked> Element[:] {
                fn probe(_ value: Element) -> String { return "marked" }
            }
            fn words(_ values: String[>..]) -> Int { return values.probe("a") }
            fn numbers(_ values: Int[>..]) -> Bool { return values.probe(2) }
            fn marked(_ values: I8[>..]) -> String { return values.probe(2) }
            fn anything<T: Equatable>(_ values: T[:], _ value: T) -> Int { return values.probe(value) }
            fn anyLanes<T: SIMDScalar>(_ values: T[:], _ value: T) -> Bool { return values.probe(value) }
            """
        );

    [Fact]
    public void AnArraysOwnMemberThatTakesASliceBeatsAProtocolsThatTakesAnySequence() =>
        // Viewing the argument as a slice is no reason to take it as any
        // sequence: the array's own copies its elements at once. What is not
        // an array still goes to the protocol's.
        AssertClean(
            """
            extension<Element> Element[>..] {
                fn taking(_ other: Element[:]) -> Int { return 1 }
            }
            extension RangeReplaceableCollection {
                fn taking<S: Sequence>(_ other: S) -> Bool where S.Element == Element { return true }
            }
            fn grown(_ xs: Int[>..], _ ys: Int[>..]) -> Int { return xs.taking(ys) }
            fn literal(_ xs: Int[>..]) -> Int { return xs.taking([1, 2]) }
            fn bytes(_ xs: U8[>..]) -> Int { return xs.taking([1, 2]) }
            fn empty(_ xs: Int[>..]) -> Int { return xs.taking([]) }
            fn slice(_ xs: Int[>..], _ ys: Int[:]) -> Int { return xs.taking(ys) }
            fn range(_ xs: Int[>..]) -> Bool { return xs.taking(0..<3) }
            """
        );

    [Fact]
    public void OverloadsFromSeveralProtocolsAreOneSet() =>
        AssertClean(
            """
            protocol Base { var size: Int { get } }
            protocol Refined: Base { }
            extension Base {
                var first: Int => 1
                fn first(where wanted: Int) -> Int { return wanted }
                fn take(_ n: Int) -> Int { return n }
            }
            extension Refined {
                fn take(while wanted: Int) -> Int { return wanted }
            }
            struct Thing: Refined {
                var size: Int => 3
                fn size(over n: Int) -> Int { return n }
            }
            fn probe(_ thing: Thing) -> Int {
                // A property and a method of one name; a method of a refined
                // protocol and one of the protocol it refines; a type's own
                // property and a protocol's method.
                return thing.first + thing.first(where: 2) + thing.take(3) + thing.take(while: 4) + thing.size + thing.size(over: 5)
            }
            """
        );

    [Fact]
    public void SelfInAProtocolsExtensionIsItsSelf() =>
        AssertClean(
            """
            protocol Shape { fn area() -> Int }
            struct Pair<A: Shape, B: Shape> {
                let first: A
                let second: B
            }
            extension Shape {
                fn paired<Other: Shape>(with other: Other) -> Pair<Self, Other> {
                    return Pair<Self, Other> { first: self, second: other }
                }
            }
            """
        );

    [Fact]
    public void AGenericProtocolsStaticRequirementGivesTheParameter() =>
        AssertClean(
            """
            protocol MakeOf<E> { static fn make(_ e: E) -> Self }
            struct A: MakeOf<Int> {
                let v: Int
                static fn make(_ e: Int) -> A { return A { v: e } }
            }
            fn made<T: MakeOf<Int>>() -> T { return T.make(5) }
            fn probe() -> Int {
                let a: A = made()
                return a.v
            }
            """
        );

    [Fact]
    public void AnEscapedPropertyNameIsTheNameWithoutTheBackticks() =>
        AssertClean(
            """
            struct Box {
                var `lazy`: Int => 3
            }
            fn probe(_ box: Box) -> Int { return box.lazy + box.`lazy` }
            """
        );

    /// <summary>
    ///     Every kind of array, and a slice, is Equatable, Hashable and
    ///     Comparable where its element is (Sable.Core's Array+Comparison.sb),
    ///     and the operators work on arrays of any kind written out.
    /// </summary>
    [Fact]
    public void ArraysAreEquatableHashableAndComparableWhereTheirElementsAre() =>
        AssertClean(
            """
            fn same<T: Equatable>(_ a: T, _ b: T) -> Bool { return a == b }
            fn first<T: Comparable>(_ a: T, _ b: T) -> Bool { return a < b }
            fn key<T: Hashable>(_ a: T) -> Int { return a.hashValue }
            fn probe(_ grow: String[>..], _ fixed: Int[3], _ slice: Int[:], _ nested: Int[>..][>..]) -> Bool {
                let ordered = grow < ["b"] && fixed >= [1, 2, 3] && slice <= slice
                return same(grow, grow) && same(fixed, fixed) && first(slice, slice) && same(nested, nested)
                    && key(grow) == key(nested) && ordered
            }
            """
        );

    [Fact]
    public void AnArrayOfElementsWithNoEqualityIsNotEquatable() {
        var errors = ErrorsIn(
            """
            struct Opaque { var n: Int }
            fn same<T: Equatable>(_ a: T, _ b: T) -> Bool { return a == b }
            fn probe(_ xs: Opaque[>..]) -> Bool { return same(xs, xs) }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NoMatchingOverload);
    }

    [Fact]
    public void AnArrayOfElementsWithNoOrderHasNoLessThan() {
        var errors = ErrorsIn(
            """
            fn probe(_ xs: Bool[>..]) -> Bool { return xs < xs }
            """
        );
        Assert.NotEmpty(errors);
    }
}
