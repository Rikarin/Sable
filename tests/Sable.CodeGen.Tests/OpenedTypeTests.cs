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

        // And compiled in units, each mode: in CI, as building each program twice more is slow.
        if (CanBuild && NativeMode.All) {
            foreach (var mode in (GenericsMode[])[GenericsMode.Unspecialised, GenericsMode.Specialised]) {
                using (SilGen.UseGenerics(mode)) {
                    var separate = BuildSeparatelyAndRun(program);
                    Assert.Equal((mode, specialised.Stdout, specialised.ExitCode), (mode, separate.Stdout, separate.ExitCode));
                }
            }
        }

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
            box of meters
            box of box of meters
            cell of Test.Meters
            cell of Test.Plain
            false

            """,
            """
            protocol Kind {
                static fn kind() -> String
            }

            extension Meters: Kind {
                static fn kind() -> String => "meters"
            }

            extension Box: Kind where T: Kind {
                static fn kind() -> String => "box of \(T.kind())"
            }

            extension Cell: Kind {
                static fn kind() -> String => "cell of \(T.self)"
            }

            fn kindOf<T: Kind>(_ type: T.Type) -> String => T.kind()

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

            // Opened as a constrained T: the registry finds each instance's template table, whose entry reads T from its metadata.
            let meters = types["meters"]!
            print(kindOf(boxOf(meters)))
            print(kindOf(boxOf(boxOf(meters))))
            print(kindOf(cellOf(meters)))
            print(kindOf(cellOf(types["plain"]!)))
            print(boxOf(types["plain"]!).isSubtype(of: (any Kind).self))
            """
        );

    /// <summary>
    ///     The arguments of a call that opens existentials are evaluated in
    ///     their order, those between the existentials too, and each is let go
    ///     when the callee, or a later argument, throws.
    /// </summary>
    [Fact]
    public void AnOpenedCallsArgumentsAreEvaluatedInOrder() =>
        Prints(
            """
            mk a
            order bx
            mk c
            abxc
            order ok
            mk long text one
            mk long text two
            long text twook
            long text twook!
            order bad
            mk long text one
            mk long text two
            caught Bad(why: "long text twobad")
            mk first long text
            caught Bad(why: "stop now")
            mk ext
            order +
            ext+

            """,
            """
            struct Bad: Error {
                var why: String
            }

            protocol P {
                fn name() -> String
                fn copy(_ tag: String) throws -> Self
            }

            struct A: P {
                var text: String
                fn name() -> String => text
                fn copy(_ tag: String) throws -> A {
                    if tag == "bad" {
                        throw Bad { why: text + tag }
                    }

                    return A { text: text + tag }
                }
            }

            extension P {
                fn renamed(_ tag: String) -> String => name() + tag
                fn itself() throws -> Self => try copy("!")
            }

            fn mk(_ text: String) -> any P {
                print("mk \(text)")
                return A { text: text }
            }

            fn order(_ text: String) -> String {
                print("order \(text)")
                return text
            }

            fn failing(_ text: String) throws -> String {
                throw Bad { why: text }
            }

            fn pair<X: P, Y: P>(_ x: X, _ tag: String, _ y: Y) -> String => x.name() + tag + y.name()

            fn keep<X: P, Y: P>(_ tag: String, _ x: X, _ y: Y) throws -> Y => try y.copy(tag)
            """,
            """
                print(pair(mk("a"), order("b" + "x"), mk("c")))
                for tag in ["ok", "bad"] {
                    do {
                        let kept = try keep(order(tag + ""), mk("long text one"), mk("long text two"))
                        print(kept.name())
                        print(try kept.itself().name())
                    } catch {
                        print("caught \(error)")
                    }
                }

                do {
                    print(pair(mk("first long text"), try failing("stop now"), mk("never")))
                } catch {
                    print("caught \(error)")
                }

                print(mk("ext").renamed(order("+")))
            """
        );

    /// <summary>
    ///     A value of a generic type made only at run time: its type, opened
    ///     as a constrained T, makes one through its template table, which
    ///     reads the arguments from the type's metadata; the result is boxed
    ///     in a class made for it, is cast to another protocol its
    ///     declaration conforms it to and to the type itself, copied, and
    ///     changed on its own.
    /// </summary>
    [Fact]
    public void AValueOfAGenericTypeMadeAtRunTimeIsBoxedAndCast() =>
        Prints(
            """
            box of meters a long label 7 7 tagged box(m7)
            box of box of meters a long label 7 7 tagged box(box(m7))
            cell of meters a long label 7 8 tagged cell m7
            box of plain 0 0 no tag
            a box of 7
            caught 7

            """,
            """
            struct Bad: Error {
                var n: Int
            }

            protocol Zero {
                static fn zero() -> Self
                var label: String { get }
                fn count() -> Int
                mutating fn bump()
                fn check() throws -> Self
            }

            protocol Tagged {
                fn tag() -> String
            }

            struct Meters: Zero, Tagged {
                var value: Int
                var text: String
                var label: String { get => "meters \(text)" }
                static fn zero() -> Meters => Meters { value: 7, text: "a long label" }
                fn count() -> Int => value
                mutating fn bump() {
                    value = value + 1
                }

                fn check() throws -> Meters {
                    throw Bad { n: value }
                }

                fn tag() -> String => "m\(value)"
            }

            struct Plain: Zero {
                var label: String { get => "plain" }
                static fn zero() -> Plain => Plain {}
                fn count() -> Int => 0
                mutating fn bump() {}
                fn check() throws -> Plain => self
            }

            struct Box<T> {
                var item: T
            }

            extension Box: Zero where T: Zero {
                var label: String { get => "box of \(item.label)" }
                static fn zero() -> Box<T> => Box { item: T.zero() }
                fn count() -> Int => item.count()
                mutating fn bump() {
                    item.bump()
                }

                fn check() throws -> Box<T> => Box { item: try item.check() }
            }

            extension Box: Tagged where T: Tagged {
                fn tag() -> String => "box(\(item.tag()))"
            }

            final class Cell<T> {
                var item: T
                var bumps = 0
                init(item: T) {
                    self.item = item
                }
            }

            extension Cell: Zero where T: Zero {
                var label: String { get => "cell of \(item.label)" }
                static fn zero() -> Cell<T> => Cell(item: T.zero())
                fn count() -> Int => item.count() + bumps
                fn bump() {
                    bumps = bumps + 1
                }

                fn check() throws -> Cell<T> => self
            }

            extension Cell: Tagged where T: Tagged {
                fn tag() -> String => "cell \(item.tag())"
            }

            fn boxOf<T>(_ type: T.Type) -> Type => Box<T>.self

            fn cellOf<T>(_ type: T.Type) -> Type => Cell<T>.self

            fn make<T: Zero>(_ type: T.Type) -> T => T.zero()
            """,
            """
                let types: [String: Type] = ["meters": Meters.self, "plain": Plain.self]
                let meters = types["meters"]!
                for t in [boxOf(meters), boxOf(boxOf(meters)), cellOf(meters), boxOf(types["plain"]!)] {
                    let made = make(t)
                    var copy = made
                    let before = made.count()
                    copy.bump()
                    if let tagged = made as? any Tagged {
                        print("\(made.label) \(before) \(made.count()) tagged \(tagged.tag())")
                    } else {
                        print("\(made.label) \(before) \(made.count()) no tag")
                    }
                }

                let made = make(boxOf(meters))
                if let box = made as? Box<Meters> {
                    print("a box of \(box.item.value)")
                }

                do {
                    let _ = try made.check()
                } catch let bad as Bad {
                    print("caught \(bad.n)")
                } catch {
                    print("other")
                }
            """
        );

    /// <summary>
    ///     A conformance with associated types, of an instance made at run
    ///     time: its table is the instance's own, with the metadata its
    ///     conformance's template gives for it — the type argument, another
    ///     instance, an associated type of the argument — and what they
    ///     conform to is found; through a refined protocol too.
    /// </summary>
    [Fact]
    public void AGenericTypeMadeAtRunTimeHasItsAssociatedTypes() => Prints(
        "a box: Test.Meters first 7m 2\na box: Test.Label first seven 2\n"
        + "a box: Test.Box<Test.Meters> first box of 7m 2\n"
        + "pair of Test.Box<Test.Meters> and Int\n"
        + "cell Test.Meters\n",
        """
        protocol Named {
            static fn made() -> Self
            fn name() -> String
        }

        struct Meters: Named {
            var value: Int
            static fn made() -> Meters => Meters { value: 7 }
            fn name() -> String => "\(value)m"
        }

        struct Label: Named {
            var text: String
            static fn made() -> Label => Label { text: "seven" }
            fn name() -> String => text
        }

        protocol Steps {
            associatedtype Item: Named
            mutating fn step() -> Item?
        }

        protocol Countable {
            static fn count() -> Int
        }

        protocol Holder: Countable {
            associatedtype Item: Named
            associatedtype Walk: Steps where Walk.Item == Item
            static fn whole() -> Self
            fn item() -> Item
            fn walk() -> Walk
        }

        struct BoxSteps<T: Named>: Steps {
            alias Item = T
            var left: T?
            mutating fn step() -> T? {
                let given = left
                left = none
                return given
            }
        }

        struct Box<T: Named> {
            var value: T
            var seen: Int
        }

        extension Box: Countable {
            static fn count() -> Int => 2
        }

        extension Box: Holder {
            alias Item = T
            alias Walk = BoxSteps<T>
            static fn whole() -> Box<T> => Box(value: T.made(), seen: 0)
            fn item() -> T => value
            fn walk() -> BoxSteps<T> => BoxSteps(left: value)
        }

        extension Box: Named {
            static fn made() -> Box<T> => Box(value: T.made(), seen: 1)
            fn name() -> String => "box of \(value.name())"
        }

        protocol Paired {
            associatedtype First
            associatedtype Second
        }

        extension Box: Paired {
            alias First = Box<T>
            alias Second = Int
        }

        class Cell<T> {
            var held: T?
            init() {
                held = none
            }
        }

        protocol Element {
            associatedtype Value
        }

        extension Cell: Element {
            alias Value = T
        }

        fn boxOf<T: Named>(_ type: T.Type) -> Type => Box<T>.self

        fn cellOf<T: Named>(_ type: T.Type) -> Type => Cell<T>.self

        fn describe<H: Holder>(_ type: H.Type) -> String {
            var walk = H.whole().walk()
            var steps = 0
            var first = "nothing"
            while let item = walk.step() {
                steps = steps + 1
                first = item.name()
            }

            let t: Type = H.Item.self
            return "a box: \(t) first \(first) \(steps + H.count() - 1)"
        }

        fn parts<P: Paired>(_ type: P.Type) -> String {
            let a: Type = P.First.self
            let b: Type = P.Second.self
            return "pair of \(a) and \(b)"
        }

        fn value<E: Element>(_ type: E.Type) -> String {
            let v: Type = E.Value.self
            return "cell \(v)"
        }
        """,
        """
            let types: [String: Type] = ["meters": Meters.self, "label": Label.self]
            print(describe(boxOf(types["meters"]!)))
            print(describe(boxOf(types["label"]!)))
            print(describe(boxOf(boxOf(types["meters"]!))))
            print(parts(boxOf(types["meters"]!)))
            print(value(cellOf(types["meters"]!)))
        """
    );

    /// <summary>A conformance a template cannot be made whole for has none: opening the type traps, as one that does not conform does.</summary>
    [Fact]
    public void AGenericTypeMadeAtRunTimeWithNoTableTraps() {
        var execution = Runs(
            """
            protocol Takes {
                associatedtype Items: Sequence
                static fn name() -> String
            }

            struct Plain {}

            struct Box<T>: Takes {
                alias Items = T[]
                var item: T
                static fn name() -> String => "box"
            }

            fn boxOf<T>(_ type: T.Type) -> Type => Box<T>.self

            fn name<T: Takes>(_ type: T.Type) -> String => T.name()
            """,
            """
                let t: Type = Plain.self
                print(name(boxOf(t)))
            """
        );
        Assert.NotEqual(0, execution.ExitCode);
        Assert.Contains("'Test.Box<Test.Plain>' does not conform to 'Test.Takes'", execution.Stderr, StringComparison.Ordinal);
    }
}
