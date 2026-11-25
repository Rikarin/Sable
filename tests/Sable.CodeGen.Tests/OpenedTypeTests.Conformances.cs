namespace Sable.CodeGen.Tests;

/// <summary>
///     The conformances of types known only as the program runs (#297), as
///     the calls that open them find them.
/// </summary>
public sealed partial class OpenedTypeTests {
    /// <summary>A requirement a generic type leaves to the protocol's default is in the table of its instances made at run time, too.</summary>
    [Fact]
    public void AConformanceLeavesARequirementToItsDefault() => Prints(
        """
        ["none"] a Test.Box<Test.Meters> of 7 true 7
        ["none"] a Test.Cell<Test.Meters> of 4 true 4
        ["none"] a Test.Box<Test.Cell<Test.Meters>> of 7 true 7

        """,
        """
        protocol Describes {
            static fn parts() -> String[>..]
            fn title() -> String
            var size: Int { get }
            fn both(_ other: Self) -> Bool
            static fn make() -> Self
        }
        extension Describes {
            static fn parts() -> String[>..] => ["none"]
            fn title() -> String => "a \(Self.self) of \(size)"
            var size: Int => 7
            fn both(_ other: Self) -> Bool => other.size == size
        }
        struct Box<T> { let item: Int }
        extension Box: Describes {
            static fn make() -> Box<T> => Box<T> { item: 3 }
        }
        final class Cell<T> {
            let item: Int
            init(_ i: Int) { item = i }
        }
        extension Cell: Describes {
            static fn make() -> Cell<T> => Cell<T>(4)
            var size: Int => item
        }
        fn boxOf<T>(_ t: T.Type) -> Type => Box<T>.self
        fn cellOf<T>(_ t: T.Type) -> Type => Cell<T>.self
        fn show<T: Describes>(_ t: T.Type) -> String {
            let x = T.make()
            return "\(T.parts()) \(x.title()) \(x.both(x)) \(x.size)"
        }
        struct Meters { }
        """,
        """
            let t: Type = Meters.self
            print(show(boxOf(t)))
            print(show(cellOf(t)))
            print(show(boxOf(cellOf(t))))
        """
    );

    /// <summary>An instance the program names is opened as what it conforms to, though nothing boxes or casts one.</summary>
    [Fact]
    public void AnInstanceNamedOnlyAsAValueIsOpened() => Prints(
        "Int\nSable.Core.Range<Int>\nTest.Crate<Sable.Core.Range<Int>>\n",
        """
        fn equatable<T: Equatable>(_ type: T.Type) -> String => "\(type)"
        struct Crate<T> { let item: T }
        extension Crate: Equatable where T: Equatable {
            static operator ==(a: Self, b: Self) -> Bool => a.item == b.item
        }
        """,
        """
            let types: Type[>..] = [Int.self, Range<Int>.self, Crate<Range<Int>>.self]
            for t in types { print(equatable(t)) }
        """
    );
}
