namespace Sable.CodeGen.Tests;

/// <summary>
///     Casts of an existential to a type code compiled once knows only by its
///     metadata, or to an instance of a generic protocol; and a generic type
///     without its arguments as a value (#81, #297): with generics compiled
///     once and specialised, native and interpreted.
/// </summary>
public sealed partial class OpenedTypeTests {
    const string Casting = """
        protocol Named { fn n() -> String }
        protocol Repo<T> { fn made() -> T? }
        extension Int: Named { fn n() -> String { return "int" } }
        open class Animal { init() {} }
        final class Dog: Animal, Named { override init() { super.init() } fn n() -> String { return "dog" } }
        final class Sql<T>: Repo<T> { init() {} fn made() -> T? { return none } }
        final class IntRepo: Repo<Int> { init() {} fn made() -> Int? { return 3 } }
        struct Big { let a: String, let b: Int, let c: String }
        struct Pair<A> { let a: A }
        struct Order {}
        fn cast<T>(_ t: T.Type, _ x: any Any) -> T? { return x as? T }
        fn force<T>(_ t: T.Type, _ x: any Any) -> T { return x as! T }
        fn pair<T>(_ t: T.Type, _ x: any Any) -> Pair<T>? { return x as? Pair<T> }
        """;

    [Fact]
    public void AnExistentialIsCastToATypeParameter() => Prints(
        """
        3 - false false - int false false true -
        -1 s false false - - false false true -
        -1 - true true - dog false false true -
        -1 - false false c - false false true -
        -1 - false false - - true false true -
        3 - false false - int false false true -
        -1 - false false - - false false true q
        s

        """,
        Casting,
        """
            let xs: (any Any)[] = [3, "s", Dog(), Big { a: "a", b: 2, c: "c" }, Sql<Int>(), 3, Pair { a: "q" }]
            for x in xs {
                let i = cast(Int.self, x)
                let s = cast(String.self, x)
                let a = cast(Animal.self, x)
                let d = cast(Dog.self, x)
                let b = cast(Big.self, x)
                let n = cast((any Named).self, x)
                let r = cast((any Repo<Int>).self, x)
                let q = cast((any Repo<String>).self, x)
                let y = cast((any Any).self, x)
                let p = pair(String.self, x)
                print("\(i ?? -1) \(s ?? "-") \(a != none) \(d != none) \(b?.c ?? "-") \(n?.n() ?? "-") \(r != none) \(q != none) \(y != none) \(p?.a ?? "-")")
            }
            print(force(String.self, xs[1]))
        """
    );

    [Fact]
    public void AForcedCastToATypeParameterTraps() {
        var execution = Runs(Casting, "    let x: any Any = 3\n    print(force(String.self, x))");
        Assert.NotEqual(0, execution.ExitCode);
        Assert.Contains("cast to 'T' failed", execution.Stderr.Replace("cast to 'String' failed", "cast to 'T' failed", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AGenericTypesInstanceIsCastToAGenericProtocols() => Prints(
        "order no no int 3\n",
        Casting,
        """
            let x: any Any = Sql<Order>()
            let y: any Any = IntRepo()
            let a = x as? any Repo<Order>
            let b = x as? any Repo<Int>
            let c = y as? any Repo<Order>
            let d = y as? any Repo<Int>
            print("\(a == none ? "no" : "order") \(b == none ? "no" : "int") \(c == none ? "no" : "order") \(d == none ? "no" : "int \(d!.made()!)")")
        """
    );

    [Fact]
    public void AValueOfAKnownTypeIsCastToAProtocolItConformsTo() => Prints(
        "int - dog -\n",
        Casting + """

            fn named<T>(_ value: T) -> String {
                if let n = value as? any Named {
                    return n.n()
                }
                return "-"
            }
            """,
        """
            print("\(named(3)) \(named("s")) \(named(Dog())) \(named(Big { a: "", b: 1, c: "" }))")
        """
    );

    [Fact]
    public void AnErrorIsCastToItsType() => Prints(
        "bad 4 none\n",
        """
        struct Bad: Error { let n: Int }
        struct Worse: Error { }
        fn fail(_ worse: Bool) throws {
            if worse {
                throw Worse { }
            }
            throw Bad { n: 4 }
        }
        fn said(_ worse: Bool) -> String {
            do {
                try fail(worse)
            } catch {
                let e: any Error = error
                if let bad = e as? Bad {
                    return "bad \(bad.n)"
                }
                return "none"
            }
            return "-"
        }
        """,
        """
            print("\(said(false)) \(said(true))")
        """
    );

    [Fact]
    public void AGenericTypeIsAValue() => Prints(
        """
        Test.Box 1
        any Test.Source<Int> 1 [Int]
        Test.Box<Int> true true
        true true
        Test.Two<Int, String> Test.Either<Int, String>
        true false true

        """,
        """
        protocol Source<T> { fn next() -> T? }
        protocol Named { fn n() -> String }
        extension Int: Named { fn n() -> String { return "int" } }
        final class Box<T: Named>: Source<T> {
            init() {}
            fn next() -> T? { return none }
        }
        struct Two<A, B> { let a: A, let b: B }
        union Either<A, B> { L(A), R(B) }
        """,
        """
            let box = Box.self
            print("\(box.name) \(box.parameterCount)")
            let source: Type = (any Source<Int>).self
            let arguments = Source.self.arguments(of: source)!
            print("\(source.name) \(source.argumentCount) [\(arguments[0])]")
            let made = box.instantiate(arguments)!
            print("\(made) \(made == Box<Int>.self) \(box.arguments(of: made)! == arguments)")
            print("\(box.instantiate([String.self]) == none) \(Source.self.instantiate(arguments) == none)")
            print("\(Two.self.instantiate([Int.self, String.self])!) \(Either.self.instantiate([Int.self, String.self])!)")
            print("\(Two.self == Two.self) \(Two.self == Either.self) \(Either.self.arguments(of: made) == none)")
        """
    );

    [Fact]
    public void AGenericClassHoldsOneOfAnotherOfItself() => Prints(
        "log of Test.Repo<Test.Order>\n",
        """
        struct Order {}
        final class Log<T> {
            init() {}
            fn who() -> String { return "log of \(T.self)" }
        }
        protocol Made { static fn make() -> Self
            fn who() -> String }
        final class Repo<T>: Made {
            let log: Log<Repo<T>>
            init(_ log: Log<Repo<T>>) { self.log = log }
            static fn make() -> Self { return Self(Log<Repo<T>>()) }
            fn who() -> String { return log.who() }
        }
        fn made<T: Made>(_ t: T.Type) -> T { return T.make() }
        """,
        """
            print(made(Repo.self.instantiate([Order.self])!).who())
        """
    );

    [Fact]
    public void ATypeIsCastToAnExistentialMetatype() => Prints(
        """
        Test.Meters Test.Meters 8
        0m 0m false
        Test.Plain has none
        0ft 0ft true
        0ft 0ft true
        Test.Meters Test.Feet
        true true
        0m

        """,
        """
        protocol Zero {
            static fn zero() -> Self
            fn text() -> String
        }
        protocol Sub: Zero { }
        struct Meters: Zero {
            let n: Int
            static fn zero() -> Self { return Meters { n: 0 } }
            fn text() -> String { return "\(n)m" }
        }
        struct Feet: Sub {
            let n: Int
            static fn zero() -> Self { return Feet { n: 0 } }
            fn text() -> String { return "\(n)ft" }
        }
        struct Plain { }
        fn make<T: Zero>(_ t: T.Type) -> T { return T.zero() }
        fn keep(_ t: any Zero.Type) -> any Zero { return make(t) }
        """,
        """
            let z: any Zero.Type = Meters.self
            print("\(z) \(z.name) \(z.size)")
            let s: any Sub.Type = Feet.self
            let up: any Zero.Type = s
            let types: Type[] = [Meters.self, Plain.self, Feet.self, up]
            for t in types {
                if let c = t as? any Zero.Type {
                    print("\(make(c).text()) \(keep(c).text()) \(c is any Sub.Type)")
                } else {
                    print("\(t) has none")
                }
            }
            let list: (any Zero.Type)[] = [Meters.self, Feet.self]
            print("\(list[0].name) \(list[1])")
            let f = types[0] as! any Zero.Type
            let ty: Type = f
            print("\(ty == Meters.self) \(ty == list[0])")
            var d: [String: any Zero.Type] = [:]
            d["a"] = Meters.self
            print(make(d["a"]!).text())
        """
    );

    [Fact]
    public void AForcedCastToAnExistentialMetatypeTraps() {
        var execution = Runs("protocol Zero { }\nstruct Plain { }", "    let t: Type = Plain.self\n    let z = t as! any Zero.Type\n    print(z.name)");
        Assert.NotEqual(0, execution.ExitCode);
        Assert.Contains("cast to 'any Zero.Type' failed", execution.Stderr, StringComparison.Ordinal);
    }
}
