using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Opening a <c>Type</c> to call a generic function (SYNTAX.md "Opening a
///     type", docs/06 §6.2, #297 stage 6): a <c>Type</c> given for a
///     <c>T.Type</c> binds T to the type it holds at run time, the callee runs
///     compiled once with its metadata and the registry's tables, and a result
///     of T comes back as the existential of T's protocols. Each program is
///     built with generics compiled once and specialised, and each runs
///     natively and in the interpreter, leak-checked; all four must agree.
/// </summary>
[Native]
public sealed class OpenedTypeTests {
    static Execution Runs(string declarations, string body) => RunsBoth(Program(declarations, body));

    /// <summary>A program whose main is async, run as <see cref="Runs" /> runs one.</summary>
    static void PrintsAsync(string expected, string declarations, string body) =>
        Assert.Equal(expected, RunsBoth($"module Test\n{declarations}\n@main\nfn main() async {{\n{body}\n}}\n").Stdout);

    static Execution RunsBoth(string program) {
        Execution unspecialised;
        using (SilGen.UseGenerics(GenericsMode.Unspecialised)) {
            unspecialised = AssertAgree(program);
        }

        Execution specialised;
        using (SilGen.UseGenerics(GenericsMode.Specialised)) {
            specialised = AssertAgree(program);
        }

        Assert.Equal(specialised.Stdout, unspecialised.Stdout);
        Assert.Equal(specialised.ExitCode, unspecialised.ExitCode);
        return specialised;
    }

    static void Prints(string expected, string declarations, string body) => Assert.Equal(expected, Runs(declarations, body).Stdout);

    const string Declarations = """
        protocol Zero {
            static fn zero() -> Self
        }

        protocol Named {
            fn name() -> String
        }

        struct Meters: Zero, Named {
            var value: Int
            static fn zero() -> Meters => Meters { value: 0 }
            fn name() -> String => "\(value)m"
        }

        struct Label: Zero, Named {
            var text: String
            static fn zero() -> Label => Label { text: "none" }
            fn name() -> String => text
        }

        struct Plain {}

        struct Box<T> {
            var item: T
        }

        struct Refused: Error {}

        fn make<T: Zero & Named>(_ type: T.Type) -> T => T.zero()

        fn describe<T: Zero & Named>(_ type: T.Type) -> String => "\(T.self): \(T.zero().name())"

        fn typeName<T>(_ type: T.Type) -> String => "\(T.self)"

        fn same<T>(_ type: T.Type) -> T.Type => type

        fn arrayOf<T>(_ type: T.Type) -> Type => T[].self

        fn optionalOf<T>(_ type: T.Type) -> Type => T?.self

        fn boxOf<T>(_ type: T.Type) -> Type => Box<T>.self

        fn count<T: Zero>(_ type: T.Type, _ n: Int) -> Int {
            var made: T[>..] = []
            for _ in 0..<n {
                made.append(T.zero())
            }

            return made.count
        }

        fn maybe<T: Zero & Named>(_ type: T.Type, failing: Bool) throws -> T {
            if failing {
                throw Refused()
            }

            return T.zero()
        }
        """;

    [Fact]
    public void AGenericFunctionIsCalledWithTheTypesOfADictionary() =>
        Prints(
            """
            0m
            none

            """,
            Declarations,
            """
            let types: [String: Type] = ["meters": Meters.self, "label": Label.self]
            for key in ["meters", "label"] {
                let made = make(types[key]!)
                print(made.name())
            }
            """
        );

    [Fact]
    public void AnUnconstrainedTypeParameterIsOpenedAndItsMetatypeComesBackAsAType() =>
        Prints(
            """
            Test.Plain
            true
            Int?

            """,
            Declarations,
            """
            let t: Type = Plain.self
            print(typeName(t))
            print(same(t) == Plain.self)
            print(typeName(Int?.self as Type))
            """
        );

    [Fact]
    public void AnExistentialsDynamicTypeIsOpened() =>
        Prints(
            """
            Test.Label: none
            Test.Meters
            0m

            """,
            Declarations,
            """
            let held: any Named = Label { text: "held" }
            print(describe(type(of: held)))
            let other: any Named = Meters { value: 9 }
            let made = make(type(of: other))
            print(type(of: made))
            print(made.name())
            """
        );

    [Fact]
    public void AResultOfTheOpenedTypeIsErasedToTheExistentialOfItsProtocols() =>
        Prints(
            """
            0m
            true
            none

            """,
            Declarations,
            """
            let made: any Zero & Named = make(Meters.self as Type)
            print(made.name())
            let back = made as? Meters
            print(back != none)
            var all: (any Zero & Named)[>..] = []
            all.append(make(Label.self as Type))
            print(all[0].name())
            """
        );

    [Fact]
    public void ATypeThatDoesNotConformTraps() {
        var run = Runs(
            Declarations,
            """
            print("before")
            let t: Type = Plain.self
            let made = make(t)
            print(made.name())
            """
        );
        Assert.Equal("before\n", run.Stdout);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("'Test.Plain' does not conform to 'Test.", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeIsTestedBeforeItIsOpened() =>
        Prints(
            """
            Test.Meters: 0m
            Test.Plain is not one

            """,
            Declarations,
            """
            for t in [Meters.self as Type, Plain.self] {
                if t.isSubtype(of: (any Zero & Named).self) {
                    print(describe(t))
                } else {
                    print("\(t) is not one")
                }
            }
            """
        );

    [Fact]
    public void GenericTypesAreMadeFromATypeKnownAtRunTime() =>
        Prints(
            """
            [Test.Label] Test.Label? Test.Box<Test.Label>
            true true true
            3

            """,
            Declarations,
            """
            let t: Type = Label.self
            print("\(arrayOf(t)) \(optionalOf(t)) \(boxOf(t))")
            print("\(arrayOf(t) == Label[].self) \(optionalOf(t) == Label?.self) \(boxOf(t) == Box<Label>.self)")
            print(count(t, 3))
            """
        );

    /// <summary>
    ///     What a container keyed by runtime types (#81's ServiceCollection)
    ///     does: build each registered type's service, a class's object among
    ///     them, keep it as an existential under its Type, and make the type of
    ///     a generic service for an entity known only at run time — from
    ///     generic code too, which opens what it is given.
    /// </summary>
    [Fact]
    public void AContainerKeyedByTypeBuildsEachService() =>
        Prints(
            """
            clock 0
            config db://
            Test.Repository<Test.Config> true
            2
            true

            """,
            """
            protocol Service {
                static fn make() -> Self
                fn describe() -> String
            }

            final class Clock: Service {
                var ticks: Int = 0
                init() {}
                static fn make() -> Clock => Clock()
                fn describe() -> String => "clock \(ticks)"
            }

            struct Config: Service {
                var url: String
                static fn make() -> Config => Config { url: "db://" }
                fn describe() -> String => "config \(url)"
            }

            struct Repository<Entity> {
                var name: String
            }

            fn build<T: Service>(_ type: T.Type) -> T => T.make()

            fn repositoryType<T>(_ entity: T.Type) -> Type => Repository<T>.self

            fn described<U>(_ given: U, _ types: Type[]) -> Int {
                var n = 0
                for t in types {
                    if build(t).describe().count > 0 {
                        n += 1
                    }
                }

                return n
            }
            """,
            """
            let wanted: Type[] = [Clock.self, Config.self]
            var services: [Type: any Service] = [:]
            for t in wanted {
                services[t] = build(t)
            }

            for t in wanted {
                print(services[t]!.describe())
            }

            let repository = repositoryType(Config.self as Type)
            print("\(repository) \(repository == Repository<Config>.self)")
            print(described(1, wanted))
            print(services[Clock.self]! as? Clock != none)
            """
        );

    /// <summary>
    ///     A factory that makes a generic class for the type it opens: the
    ///     class's initialiser is compiled once, its object made from the
    ///     class's metadata for that type, made at run time.
    /// </summary>
    [Fact]
    public void AnOpenedCallMakesAGenericClassForTheType() =>
        Prints(
            """
            Test.SqlRepository<Test.Config> 2
            Test.SqlRepository<Test.User> 2
            Test.SqlRepository<Int> 2

            """,
            """
            struct Config {
                var url: String
            }

            final class User {
                var name: String = "ann"
            }

            class SqlRepository<Entity> {
                var rows: Int
                var last: Entity?

                init() {
                    rows = 0
                    last = none
                }

                fn insert() {
                    rows += 1
                }
            }

            fn describe<T>(_ entity: T.Type) -> String {
                let repository = SqlRepository<T>()
                repository.insert()
                repository.insert()
                return "\(SqlRepository<T>.self) \(repository.rows)"
            }
            """,
            """
            let types: Type[] = [Config.self, User.self, Int.self]
            for t in types {
                print(describe(t))
            }
            """
        );

    [Fact]
    public void AnOpenedCallThatThrowsLetsItsResultsBufferGo() =>
        Prints(
            """
            caught
            none

            """,
            Declarations,
            """
            let t: Type = Label.self
            do {
                let made = try maybe(t, failing: true)
                print(made.name())
            } catch {
                print("caught")
            }

            if let made = try? maybe(t, failing: false) {
                print(made.name())
            }
            """
        );
    /// <summary>
    ///     A requirement whose result is Self or an associated type, called
    ///     through <c>any P</c> (#299, SE-0352): the existential is opened, the
    ///     requirement's entry writes the result to a buffer its type's
    ///     metadata sizes, and the result is boxed again — as <c>any Shape</c>
    ///     for Self, as the existential of an associated type's protocols for
    ///     one of those (its metadata read from the conformance's table). So
    ///     are a property of Self, a requirement that throws, one that changes
    ///     the value, and an extension member giving Self; and the value given
    ///     back is its type's, as a cast finds.
    /// </summary>
    [Fact]
    public void ARequirementGivingSelfOrAnAssociatedTypeIsCalledThroughAnExistential() =>
        Prints(
            """
            sq4 sq8 2cm sq2 sq2 sq3
            a square of 4
            sq3 again
            circle abab circle abababab 3cm circle ab! circle ab! circle ab!
            sq4 tagged
            caught Refused(side: 2)

            """,
            """
            protocol Named {
                fn name() -> String
            }

            protocol Shape {
                associatedtype Unit: Named
                fn doubled() -> Self
                fn name() -> String
                fn unit() -> Unit
                var me: Self { get }
                mutating fn grown() -> Self
                fn again() throws -> Self
            }

            protocol Tagged {
                fn tag() -> String
            }

            extension Shape {
                fn same() -> Self => self
            }

            struct Refused: Error {
                var side: Int
            }

            struct Cm: Named {
                var n: Int
                fn name() -> String => "\(n)cm"
            }

            struct Sq: Shape, Tagged {
                var side: Int
                fn doubled() -> Sq => Sq { side: side * 2 }
                fn name() -> String => "sq\(side)"
                fn unit() -> Cm => Cm { n: side }
                var me: Sq { get => self }
                mutating fn grown() -> Sq {
                    side = side + 1
                    return self
                }

                fn again() throws -> Sq {
                    if side == 2 {
                        throw Refused { side: side }
                    }

                    return self
                }

                fn tag() -> String => "tagged"
            }

            final class Circle: Shape {
                var r: String
                init(r: String) {
                    self.r = r
                }

                fn doubled() -> Circle => Circle(r: r + r)
                fn name() -> String => "circle \(r)"
                fn unit() -> Cm => Cm { n: r.count }
                var me: Circle { get => self }
                fn grown() -> Circle {
                    r = r + "!"
                    return self
                }

                fn again() throws -> Circle => self
            }
            """,
            """
                let shapes: (any Shape)[] = [Sq { side: 2 }, Circle(r: "ab")]
                for s in shapes {
                    let d = s.doubled()
                    var g = s
                    let grown = g.grown()
                    print("\(d.name()) \(d.doubled().name()) \(s.unit().name()) \(s.me.name()) \(s.same().name()) \(grown.name())")
                    if let sq = d as? Sq {
                        print("a square of \(sq.side)")
                        if let again = try? grown.again() {
                            print("\(again.name()) again")
                        }
                    }
                }

                let both: any Shape & Tagged = Sq { side: 2 }
                let twice = both.doubled()
                print("\(twice.name()) \(twice.tag())")
                do {
                    let _ = try both.again()
                } catch {
                    print("caught \(error)")
                }
            """
        );

    /// <summary>
    ///     Generic code given several existentials opens each for its own
    ///     type parameter (#299, SE-0352): the arguments evaluated in their
    ///     order, each value's address, metadata and tables given, a result of
    ///     one of the types boxed again, a requirement that takes Self
    ///     callable on each. A type parameter two parameters name, or one a
    ///     parameter names inside an optional, is not opened: the callee's copy
    ///     for the existential runs, as before.
    /// </summary>
    [Fact]
    public void SeveralExistentialsAreOpenedEachForItsOwnTypeParameter() =>
        Prints(
            """
            first a3 second b
            true
            a3 true b true
            kept b
            same type? false
            maybe a3

            """,
            """
            protocol P {
                fn same(_ other: Self) -> Bool
                fn name() -> String
            }

            protocol Q {
                fn label() -> String
            }

            struct A: P {
                var n: Int
                fn same(_ other: A) -> Bool => n == other.n
                fn name() -> String => "a\(n)"
            }

            struct B: P, Q {
                fn same(_ other: B) -> Bool => true
                fn name() -> String => "b"
                fn label() -> String => "b"
            }

            fn order(_ text: String) -> String {
                print(text)
                return text
            }

            fn pair<X: P, Y: Q>(_ x: X, _ tag: String, _ y: Y) -> String => "\(x.name()) \(x.same(x)) \(y.label()) \(tag)"

            fn second<X: P, Y: P>(_ x: X, _ y: Y) -> Y => y

            fn both<T: P>(_ a: T, _ b: T) -> Bool => a.name() == b.name()

            fn maybe<T: P>(_ x: T?) -> String => x?.name() ?? "none"
            """,
            """
                let a: any P = A { n: 3 }
                let b: any P & Q = B()
                let q: any Q = B()
                print("first \(a.name()) second \(b.label())")
                print(pair(a, order("true") == "true" ? "true" : "false", q))
                print("kept \(second(a, b).name())")
                let other: any P = B()
                print("same type? \(both(a, other))")
                let held: (any P)? = a
                print("maybe \(maybe(held))")
            """
        );

    /// <summary>
    ///     An async requirement called through <c>any P</c> (#299): its
    ///     table's entry is an async function in the generic convention, the
    ///     value's address in the box and the metadata given it, and the call
    ///     awaits it as any async call — one that suspends part-way, one that
    ///     throws, one that changes the value (its box made the existential's
    ///     own first), and one that never suspends, which stays async since
    ///     its table holds it.
    /// </summary>
    [Fact]
    public void AnAsyncRequirementIsAwaitedThroughAnExistential() =>
        PrintsAsync(
            """
            local 0 now;remote 12 #1;remote 25 #1;local 38 now;
            remote 2 #2
            refused 3
            counter 2

            """,
            """
            struct Refused: Error {
                var n: Int
            }

            protocol Fetcher {
                fn fetch(_ n: Int) async -> String
                fn check(_ n: Int) async throws -> Int
                mutating fn bump() async -> Int
            }

            struct Local: Fetcher {
                var label: String
                var count = 0
                fn fetch(_ n: Int) async -> String => "\(label) \(n) now"
                fn check(_ n: Int) async throws -> Int => n
                mutating fn bump() async -> Int {
                    await Task.yield()
                    count = count + 1
                    return count
                }
            }

            final class Remote: Fetcher {
                var count = 0
                fn fetch(_ n: Int) async -> String {
                    await Task.yield()
                    count = count + 1
                    return "remote \(n) #\(count)"
                }

                fn check(_ n: Int) async throws -> Int {
                    await Task.yield()
                    if n % 2 == 1 {
                        throw Refused { n: n }
                    }

                    return n
                }

                fn bump() async -> Int => count
            }

            fn all(_ fetchers: (any Fetcher)[]) async -> String {
                var text = ""
                for f in fetchers {
                    text = text + (await f.fetch(text.count)) + ";"
                }

                return text
            }
            """,
            """
                let remote = Remote()
                let fetchers: (any Fetcher)[] = [Local { label: "local" }, remote, Remote(), Local { label: "local" }]
                print(await all(fetchers))
                let again: any Fetcher = remote
                print(await again.fetch(2))
                do {
                    print(try await again.check(3))
                } catch let refused as Refused {
                    print("refused \(refused.n)")
                } catch {
                    print("other")
                }

                var counter: any Fetcher = Local { label: "c" }
                var copy = counter
                let _ = await counter.bump()
                let bumped = await counter.bump()
                let unchanged = await copy.bump()
                print("counter \(bumped + unchanged - 1)")
            """
        );

    /// <summary>
    ///     A generic struct and a generic class made only at run time, for a
    ///     type opened from a dictionary, have the conformance records their
    ///     declarations give them: the class's always, the struct's
    ///     conditional one where its argument conforms — an argument made at
    ///     run time too among them — as an instance the program names has.
    /// </summary>
    [Fact]
    public void AGenericTypeMadeAtRunTimeHasItsDeclarationsConformances() =>
        Prints(
            """
            Test.Box<Test.Meters> true true
            Test.Box<Test.Box<Test.Meters>> true true
            Test.Cell<Test.Meters> true false
            Test.Box<Test.Plain> false true
            Test.Box<Test.Box<Test.Plain>> false true
            Test.Cell<Test.Plain> true false
            true false

            """,
            """
            protocol Zero {
                static fn zero() -> Self
            }

            protocol P {
                fn tag() -> String
            }

            struct Meters: Zero, P {
                var value: Int
                static fn zero() -> Meters => Meters { value: 0 }
                fn tag() -> String => "m\(value)"
            }

            struct Plain: Zero {
                static fn zero() -> Plain => Plain {}
            }

            struct Box<T> {
                var item: T
            }

            extension Box: Zero where T: Zero {
                static fn zero() -> Box<T> => Box { item: T.zero() }
            }

            extension Box: P where T: P {
                fn tag() -> String => "box(\(item.tag()))"
            }

            final class Cell<T>: P {
                var item: T
                init(item: T) {
                    self.item = item
                }

                fn tag() -> String => "cell"
            }

            fn boxOf<T>(_ type: T.Type) -> Type => Box<T>.self

            fn cellOf<T>(_ type: T.Type) -> Type => Cell<T>.self
            """,
            """
            let types: [String: Type] = ["meters": Meters.self, "plain": Plain.self]
            for key in ["meters", "plain"] {
                let t = types[key]!
                for made in [boxOf(t), boxOf(boxOf(t)), cellOf(t)] {
                    print("\(made) \(made.isSubtype(of: (any P).self)) \(made.isSubtype(of: (any Zero).self))")
                }
            }

            let named: Type[] = [Box<Meters>.self, Box<Plain>.self]
            print("\(named[0].isSubtype(of: (any P).self)) \(named[1].isSubtype(of: (any P).self))")
            """
        );
}
