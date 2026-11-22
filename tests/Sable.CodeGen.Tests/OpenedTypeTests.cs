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
    static Execution Runs(string declarations, string body) {
        var program = Program(declarations, body);
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
