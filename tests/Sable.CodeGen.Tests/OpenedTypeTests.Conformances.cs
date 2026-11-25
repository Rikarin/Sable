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

    /// <summary>What an extension of a generic protocol adds, used on an existential of an instance of it.</summary>
    [Fact]
    public void AnExtensionsMemberIsCalledThroughAnInstanceOfAGenericProtocol() => Prints(
        """
        ok positive 4
        bad
        n any Test.Validator<Int>
        label
        [1, 2]
        at 3 label
        true positive 5
        ok positive 6
        2 0
        ok held
        ["s", "t"]

        """,
        """
        protocol Validator<T> {
            fn rules(_ x: T) -> String
            fn check(_ x: T) -> Bool
        }
        extension Validator {
            fn validate(_ x: T) -> String => check(x) ? "ok \(rules(x))" : "bad"
            fn named(_ n: String) -> String => "\(n) \(Self.self)"
            var label: String => "label"
            subscript(i: Int) -> String => "at \(i) \(label)"
            fn generic<U>(_ u: U, _ x: T) -> String => "\(u) \(rules(x))"
            fn passes(_ x: T) -> String => use(self, x)
            fn both(_ x: T, _ y: T) -> T[>..] => check(x) ? [x, y] : [y]
        }
        struct Positive: Validator<Int> {
            fn rules(_ x: Int) -> String => "positive \(x)"
            fn check(_ x: Int) -> Bool => x > 0
        }
        struct Holds<T>: Validator<T> {
            let text: String
            fn rules(_ x: T) -> String => "\(text)"
            fn check(_ x: T) -> Bool => true
        }

        fn use<V: Validator<X>, X>(_ v: V, _ x: X) -> String => v.validate(x)
        protocol Grows<T> { mutating fn grow(_ t: T)
          fn count() -> Int }
        extension Grows { mutating fn growTwice(_ t: T) { grow(t)
          grow(t) } }
        struct Bag: Grows<Int> { var items: Int[>..] = []
          mutating fn grow(_ t: Int) { items.append(t) }
          fn count() -> Int => items.count }
        """,
        """
            let v: any Validator<Int> = Positive()
            print(v.validate(4))
            print(v.validate(-4))
            print(v.named("n"))
            print(v.label)
            print("\(v.both(1, 2))")
            print(v[3])
            print(v.generic(true, 5))
            print(v.passes(6))
            var g: any Grows<Int> = Bag()
            let before = g
            g.growTwice(4)
            print("\(g.count()) \(before.count())")
            let all: (any Validator<String>)[>..] = [Holds<String> { text: "held" }]
            for v in all { print(v.validate("s")) }
            for v in all { print("\(v.both("s", "t"))") }
        """
    );

    /// <summary>A requirement generic over its own parameter throws that parameter's associated type, on a type opened.</summary>
    [Fact]
    public void ARequirementThrowsItsOwnParametersError() => Prints(
        """
        always a
        found a
        always x
        missing x

        """,
        """
        protocol Resolver {
            associatedtype Failure: Error
            fn get(_ name: String) throws(Failure) -> String
        }
        struct Missing: Error { let name: String }
        struct Always: Resolver {
            alias Failure = Never
            fn get(_ name: String) -> String => "always \(name)"
        }
        struct Never1: Resolver {
            alias Failure = Missing
            fn get(_ name: String) throws(Missing) -> String {
                if name == "x" { throw Missing { name: name } }
                return "found \(name)"
            }
        }

        protocol Injectable {
            static fn make<R: Resolver>(_ r: R) throws(R.Failure) -> Self
            fn show() -> String
        }
        struct A: Injectable {
            let s: String
            static fn make<R: Resolver>(_ r: R) throws(R.Failure) -> A => A { s: try r.get("a") }
            fn show() -> String => s
        }
        struct X: Injectable {
            let s: String
            static fn make<R: Resolver>(_ r: R) throws(R.Failure) -> X => X { s: try r.get("x") }
            fn show() -> String => s
        }

        fn make<T: Injectable, R: Resolver>(_ t: T.Type, _ r: R) throws(R.Failure) -> T => try T.make(r)
        """,
        """
            let ts: Type[>..] = [A.self, X.self]
            for t in ts {
                print(make(t, Always()).show())
                do {
                    print(try make(t, Never1()).show())
                } catch {
                    print("missing \(error.name)")
                }
            }
        """
    );

    /// <summary>A type that conforms to several instances of a generic protocol, as an existential of each.</summary>
    [Fact]
    public void ATypeConformsToSeveralInstancesOfAProtocol() => Prints(
        """
        int 5;
        string s2;
        int 5;int 5;
        int 4;
        string z3;
        i4;btrue;
        string cast9;
        int 10;
        false

        """,
        """
        protocol Handles<E> {
            fn handle(_ e: E) -> String
        }
        extension Handles {
            fn twice(_ e: E) -> String => handle(e) + handle(e)
        }
        struct Both: Handles<Int>, Handles<String> {
            let n: Int
            fn handle(_ e: Int) -> String => "int \(e + n);"
            fn handle(_ e: String) -> String => "string \(e)\(n);"
        }
        final class Many: Handles<Int>, Handles<Bool> {
            fn handle(_ e: Int) -> String => "i\(e);"
            fn handle(_ e: Bool) -> String => "b\(e);"
            init() { }
        }
        fn use<H: Handles<Int>>(_ h: H) -> String => h.handle(1)
        fn useS<H: Handles<String>>(_ h: H) -> String => h.handle("z")
        """,
        """
            let a: any Handles<Int> = Both { n: 1 }
            let b: any Handles<String> = Both { n: 2 }
            print(a.handle(4))
            print(b.handle("s"))
            print(a.twice(4))
            print(use(Both { n: 3 }))
            print(useS(Both { n: 3 }))
            let c: any Handles<Int> = Many()
            let d: any Handles<Bool> = Many()
            print(c.handle(4) + d.handle(true))
            let x: any Any = Both { n: 9 }
            if let h = x as? any Handles<String> { print(h.handle("cast")) }
            if let h = x as? any Handles<Int> { print(h.handle(1)) }
            print("\(x is any Handles<Bool>)")
        """
    );

    /// <summary>A protocol's static members, used on an any P.Type.</summary>
    [Fact]
    public void StaticMembersOfAnExistentialMetatype() => Prints(
        """
        A
        A!
        3
        1
        B
        B!
        4
        2
        A

        """,
        """
        protocol Zero {
            static fn zero() -> Self
            static fn title() -> String
            static var unit: Int { get }
            fn n() -> Int
        }
        extension Zero {
            static fn loud() -> String => title() + "!"
        }
        struct A: Zero {
            static fn zero() -> A => A()
            static fn title() -> String => "A"
            static var unit: Int => 3
            fn n() -> Int => 1
        }
        final class B: Zero {
            static fn zero() -> B => B()
            static fn title() -> String => "B"
            static var unit: Int => 4
            fn n() -> Int => 2
            init() { }
        }
        """,
        """
            let ts: (any Zero.Type)[>..] = [A.self, B.self]
            for t in ts {
                print(t.title())
                print(t.loud())
                print("\(t.unit)")
                print("\(t.zero().n())")
            }
            let t0: Type = A.self
            if let z = t0 as? any Zero.Type { print(z.title()) }
        """
    );

    /// <summary>Those that throw, wait, are generic, and give back Self in each way that can be erased; and of a generic type made at run time.</summary>
    [Fact]
    public void StaticMembersGiveBackWhatCallsDo() => PrintsAsync(
        """
        3 -1 -2 5 [0, 1] 2 0 4 A4
        true 2 0
        7
        3 1 1 1 [6] 2 6 2 B4
        true 2 6
        8
        3 1 1 1 [3] 2 3 2 B4
        true 2 3
        8

        """,
        """
        struct Bad: Error { let why: String }
        protocol Zero {
            static fn parse(_ s: String) throws(Bad) -> Self
            static fn maybe(_ n: Int) -> Self?
            static fn many(_ n: Int) -> Self[>..]
            static fn wait() async -> Int
            static fn convert<U: Formattable>(_ u: U) -> String
            fn n() -> Int
        }
        extension Zero {
            static var twice: Int => 2 * many(1).count
            static fn first(_ n: Int) -> Self => many(n)[0]
            static fn each(_ f: (Int) -> Int) -> Int => f(many(3).count)
        }
        struct A: Zero {
            let v: Int
            static fn parse(_ s: String) throws(Bad) -> A {
                if s == "x" { throw Bad { why: "x" } }
                return A { v: s.count }
            }
            static fn maybe(_ n: Int) -> A? => n > 0 ? A { v: n } : none
            static fn many(_ n: Int) -> A[>..] {
                var all: A[>..] = []
                for i in 0..<n { all.append(A { v: i }) }
                return all
            }
            static fn wait() async -> Int => 7
            static fn convert<U: Formattable>(_ u: U) -> String => "A\(u)"
            fn n() -> Int => v
        }
        final class B<T>: Zero {
            let t: String
            init(_ t: String) { self.t = t }
            static fn parse(_ s: String) throws(Bad) -> B<T> => B<T>(s)
            static fn maybe(_ n: Int) -> B<T>? => B<T>("\(n)")
            static fn many(_ n: Int) -> B<T>[>..] => [B<T>("\(T.self)")]
            static fn wait() async -> Int => 8
            static fn convert<U: Formattable>(_ u: U) -> String => "B\(u)"
            fn n() -> Int => t.count
        }
        fn bOf<T>(_ t: T.Type) -> Type => B<T>.self

        fn show(_ t: any Zero.Type) -> String {
            let made = (try? t.parse("abc"))?.n() ?? -1
            let bad = (try? t.parse("x"))?.n() ?? -1
            return "\(made) \(bad) \(t.maybe(0)?.n() ?? -2) \(t.maybe(5)?.n() ?? -2) \(t.many(2).map({ $0.n() })) \(t.twice) \(t.first(1).n()) \(t.each({ $0 + 1 })) \(t.convert(4))"
        }
        fn generic<T>(_ x: T, _ t: any Zero.Type) -> String => "\(x) \(t.twice) \(t.first(1).n())"
        """,
        """
            let i: Type = Int.self
            let ts: Type[>..] = [A.self, B<String>.self, bOf(i)]
            for t in ts {
                let z = t as! any Zero.Type
                print(show(z))
                print(generic(true, z))
                print("\(await z.wait())")
            }
        """
    );

    /// <summary>Arrays, slices and optionals, named and made as the program runs, conform as their declarations say, and are opened as that.</summary>
    [Fact]
    public void ArraysSlicesAndOptionalsConformAsTheyAreMade() => Prints(
        """
        Int: true true true true
        String: true true true true
        Test.Suit: false false false false
        Test.Plain: false false false false
        [Int]: true true true true
        [Test.Plain]: false false false false
        Int?: true true true true
        Test.Plain?: false false false false
        [Int:]: true true true true
        Test.Crate<Int>: true true false false
        Test.Crate<Test.Plain>: false false false false
        (Int, Int): false false false false
        [[Int]]: true true true true
        [Int]: true true true true
        [Test.Plain]: false false false false
        Int?: true true true true
        Test.Crate<Int>: true true false false
        (Int, Int): false false false false
        [String]: true true true true
        [String]
        [String]
        [String]
        [String]
        [String]: true true true true
        [String]
        [String]
        [String]
        [String]
        [String:]: true true true true
        [String:]
        [String:]
        [String:]
        [String:]
        String?: true true true true
        String?
        String?
        String?
        String?
        Test.Crate<String>: true true false false
        Test.Crate<String>
        Test.Crate<String>
        Test.Cell<String>: true false false false
        Test.Cell<String>
        (String, Int): false false false false
        [Test.Crate<String>]: true true false false
        [Test.Crate<String>]
        [Test.Crate<String>]
        Test.Crate<[String]>: true true false false
        Test.Crate<[String]>
        Test.Crate<[String]>
        [String]?: true true true true
        [String]?
        [String]?
        [String]?
        [String]?
        [String?]: true true true true
        [String?]
        [String?]
        [String?]
        [String?]
        [Test.Plain]: false false false false
        [Test.Plain]: false false false false
        [Test.Plain:]: false false false false
        Test.Plain?: false false false false
        Test.Crate<Test.Plain>: false false false false
        Test.Cell<Test.Plain>: false false false false
        (Test.Plain, Int): false false false false
        [Test.Crate<Test.Plain>]: false false false false
        Test.Crate<[Test.Plain]>: false false false false
        [Test.Plain]?: false false false false
        [Test.Plain?]: false false false false
        [Sable.Core.Range<Int>]: true false false true
        [Sable.Core.Range<Int>]
        [Sable.Core.Range<Int>]
        [Sable.Core.Range<Int>]: true false false true
        [Sable.Core.Range<Int>]
        [Sable.Core.Range<Int>]
        [Sable.Core.Range<Int>:]: true false false true
        [Sable.Core.Range<Int>:]
        [Sable.Core.Range<Int>:]
        Sable.Core.Range<Int>?: true false false true
        Sable.Core.Range<Int>?
        Sable.Core.Range<Int>?
        Test.Crate<Sable.Core.Range<Int>>: true false false false
        Test.Crate<Sable.Core.Range<Int>>
        Test.Cell<Sable.Core.Range<Int>>: true false false false
        Test.Cell<Sable.Core.Range<Int>>
        (Sable.Core.Range<Int>, Int): false false false false
        [Test.Crate<Sable.Core.Range<Int>>]: true false false false
        [Test.Crate<Sable.Core.Range<Int>>]
        Test.Crate<[Sable.Core.Range<Int>]>: true false false false
        Test.Crate<[Sable.Core.Range<Int>]>
        [Sable.Core.Range<Int>]?: true false false true
        [Sable.Core.Range<Int>]?
        [Sable.Core.Range<Int>]?
        [Sable.Core.Range<Int>?]: true false false true
        [Sable.Core.Range<Int>?]
        [Sable.Core.Range<Int>?]

        """,
        """
        struct Plain { }
        struct Crate<T> { let item: T }
        extension Crate: Equatable where T: Equatable {
            static operator ==(a: Self, b: Self) -> Bool => a.item == b.item
        }
        extension Crate: Hashable where T: Hashable {
            fn hash(into hasher: inout Hasher) { item.hash(into: &hasher) }
        }
        enum Suit { Hearts, Spades }
        union Either<A, B> { First(a: A), Second(b: B) }
        final class Cell<T> {
            let item: T
            init(_ t: T) { item = t }
        }
        extension Cell: Equatable where T: Equatable {
            static operator ==(a: Cell<T>, b: Cell<T>) -> Bool => a.item == b.item
        }

        fn subtypes(_ t: Type) -> String =>
            "\(t): \(t.isSubtype(of: (any Equatable).self)) \(t.isSubtype(of: (any Hashable).self)) \(t.isSubtype(of: (any Comparable).self)) \(t.isSubtype(of: (any Debug).self))"
        fn casts(_ x: any Any) -> String =>
            "\(type(of: x)): \(x is any Equatable) \(x is any Hashable) \(x is any Comparable) \((x as? any Debug) != none)"
        fn equatable<T: Equatable>(_ type: T.Type) -> String => "\(type)"
        fn hashable<T: Hashable>(_ type: T.Type) -> String => "\(type)"
        fn comparable<T: Comparable>(_ type: T.Type) -> String => "\(type)"
        fn debug<T: Debug>(_ type: T.Type) -> String => "\(type)"

        fn arrayOf<T>(_ t: T.Type) -> Type => T[].self
        fn growable<T>(_ t: T.Type) -> Type => T[>..].self
        fn sliceOf<T>(_ t: T.Type) -> Type => T[:].self
        fn optionalOf<T>(_ t: T.Type) -> Type => Optional<T>.self
        fn crateOf<T>(_ t: T.Type) -> Type => Crate<T>.self
        fn cellOf<T>(_ t: T.Type) -> Type => Cell<T>.self
        fn pairOf<T>(_ t: T.Type) -> Type => (T, Int).self
        """,
        """
            let types: Type[>..] = [Int.self, String.self, Suit.self, Plain.self, Int[].self, Plain[].self, Int?.self, Plain?.self, Int[:].self, Crate<Int>.self, Crate<Plain>.self, (Int, Int).self, Int[][].self]
            for t in types { print(subtypes(t)) }
            print(casts([1, 2]))
            print(casts([Plain()]))
            let o: Int? = 3
            print(casts(o as any Any))
            print(casts(Crate<Int> { item: 4 }))
            print(casts((1, 2)))
            let s: Type = String.self
            let p: Type = Plain.self
            let r: Type = Range<Int>.self
            for t in [s, p, r] {
                for u in [arrayOf(t), growable(t), sliceOf(t), optionalOf(t), crateOf(t), cellOf(t), pairOf(t), arrayOf(crateOf(t)), crateOf(arrayOf(t)), optionalOf(arrayOf(t)), arrayOf(optionalOf(t))] {
                    print(subtypes(u))
                    if u.isSubtype(of: (any Equatable).self) { print(equatable(u)) }
                    if u.isSubtype(of: (any Hashable).self) { print(hashable(u)) }
                    if u.isSubtype(of: (any Comparable).self) { print(comparable(u)) }
                    if u.isSubtype(of: (any Debug).self) { print(debug(u)) }
                }
            }
        """
    );

    /// <summary>And what they conform by is called: the library's, and a program's own.</summary>
    [Fact]
    public void WhatArraysAndOptionalsConformByIsCalled() => Prints(
        """
        false true [<a>, <b>]
        run of Test.Label true 0
        true [<a>, <a>] true
        <a> maybe Test.Label true
        false

        """,
        """
        protocol Counted {
            fn counted() -> Int
            static fn empty() -> Self
        }
        extension<E> E[>..]: Counted {
            fn counted() -> Int => count
            static fn empty() -> E[>..] => []
        }
        protocol Sized {
            static fn title() -> String
            fn same(_ other: Self) -> Bool
        }
        extension<E: Equatable> E[:]: Sized {
            static fn title() -> String => "run of \(E.self)"
            fn same(_ other: E[:]) -> Bool => self == other
        }
        protocol Holds {
            associatedtype Item: Debug
            fn pick() -> Item
        }
        extension Optional: Sized where Wrapped: Equatable {
            static fn title() -> String => "maybe \(Wrapped.self)"
            fn same(_ other: Wrapped?) -> Bool => self == other
        }
        struct Label: Equatable, Debug {
            let text: String
            static operator ==(a: Label, b: Label) -> Bool => a.text == b.text
            fn debugDescription() -> String => "<\(text)>"
        }

        fn equal<T: Equatable>(_ type: T.Type, _ x: any Any, _ y: any Any) -> Bool => (x as! T) == (y as! T)
        fn debug<T: Debug>(_ type: T.Type, _ x: any Any) -> String => (x as! T).debugDescription()
        fn title<T: Sized>(_ type: T.Type, _ x: any Any) -> String => "\(T.title()) \((x as! T).same(x as! T))"
        fn counted<T: Counted>(_ type: T.Type) -> Int => T.empty().counted()
        fn arrayOf<T>(_ t: T.Type) -> Type => T[>..].self
        fn optionalOf<T>(_ t: T.Type) -> Type => Optional<T>.self
        """,
        """
            let t: Type = Label.self
            let l = Label { text: "a" }
            let xs: any Any = [l, Label { text: "b" }]
            let ys: any Any = [l, Label { text: "c" }]
            print("\(equal(arrayOf(t), xs, ys)) \(equal(arrayOf(t), xs, xs)) \(debug(arrayOf(t), xs))")
            print("\(title(arrayOf(t), xs)) \(counted(arrayOf(t)))")
            let o: Label? = l
            let made = [l, l]
            print("\(made as any Any is any Equatable) \((made as any Any as? any Debug)?.debugDescription() ?? "none") \((made as any Any as? any Sized) != none)")
            print("\((o as any Any as? any Debug)?.debugDescription() ?? "none") \(title(optionalOf(t), o as any Any))")
            let range: Type = Range<Int>.self
            print("\(equal(arrayOf(range), [1..<2, 3..<4] as any Any, [1..<2] as any Any))")
        """
    );

    /// <summary>A requirement's own 'some P' parameters, on a type opened, an existential and a generic type made at run time.</summary>
    [Fact]
    public void ARequirementTakesSomeP() => Prints(
        """
        always a 1 x 1 s
        missing
        always a
        always String x 1 String s 1 String
        missing
        always String
        always Int x 1 Int s 1 Int
        missing
        always Int

        """,
        """
        protocol Resolver {
            fn get(_ name: String) throws -> String
        }
        struct Missing: Error { }
        struct Always: Resolver {
            fn get(_ name: String) -> String => "always \(name)"
        }
        final class Never1: Resolver {
            init() { }
            fn get(_ name: String) throws -> String { throw Missing() }
        }
        protocol Injectable {
            static fn make(_ r: some Resolver) throws -> Self
            fn show() -> String
            fn both(_ a: some Formattable, _ b: some Debug) -> String
        }
        struct A: Injectable {
            let s: String
            static fn make(_ r: some Resolver) throws -> A => A { s: try r.get("a") }
            fn show() -> String => s
            fn both(_ a: some Formattable, _ b: some Debug) -> String => "\(a) \(b)"
        }
        final class Logger<T>: Injectable {
            let s: String
            init(_ s: String) { self.s = s }
            static fn make(_ r: some Resolver) throws -> Logger<T> => Logger<T>(try r.get("\(T.self)"))
            fn show() -> String => s
            fn both(_ a: some Formattable, _ b: some Debug) -> String => "\(b) \(a) \(T.self)"
        }
        fn make<T: Injectable>(_ t: T.Type, _ r: some Resolver) throws -> T => try T.make(r)
        fn loggerOf<T>(_ t: T.Type) -> Type => Logger<T>.self
        fn both<T: Injectable>(_ x: T) -> String => x.both(1, "s")
        """,
        """
            let int: Type = Int.self
            let ts: Type[>..] = [A.self, Logger<String>.self, loggerOf(int)]
            for t in ts {
                let made = try! make(t, Always())
                print("\(made.show()) \(made.both(1, "x")) \(both(made))")
                print((try? make(t, Never1()))?.show() ?? "missing")
                let z = t as! any Injectable.Type
                print((try! z.make(Always())).show())
            }
        """
    );
}
