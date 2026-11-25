using Sable.Driver;
using Sable.Driver.Generators;
using Sable.Driver.Packages;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.DependencyInjection's generators (docs/06, "Dependency
///     injection"): what <c>@container</c> says at compile time is wrong with
///     a graph, where and by which way; and fragments of other packages. What
///     a container that compiles does is tests/lib's to test.
/// </summary>
public sealed class DependencyInjectionTests {
    /// <summary>The errors of one file with the library's generators' output, the first line of each with its place: <c>7:10 …</c>.</summary>
    static string Errors(string source) {
        var errors = Pipeline.Check([SyntaxTree.Parse(new SourceText("module App\n\nimport Sable.Concurrency\nimport Sable.DependencyInjection\n\n" + source, "app.sb"))]);
        return string.Join("\n", errors.Select(static e => {
            var lines = e.Split('\n');
            var place = lines[1].Trim().Replace("--> app.sb:", "", StringComparison.Ordinal);
            return $"{place} {lines[0][(lines[0].IndexOf(": ", StringComparison.Ordinal) + 2)..]}";
        }));
    }

    [Fact]
    public void AGraphWithNothingWrongCompiles() =>
        Assert.Equal("", Errors("""
            @injectable(.Scoped)
            final class Service { init(name: String) { } }

            @container
            struct Services {
                @singleton fn name() -> String { return "a" }
            }

            fn use() -> Service {
                let provider = Services.build()
                return provider.withScope { scope in scope.resolve(Service.self) }
            }
            """));

    [Fact]
    public void AMissingRegistrationIsSaidWithTheWayToIt() =>
        Assert.Equal(
            "13:10 container: OrderService → OrderRepository → Database: nothing provides Database",
            Errors("""
                final class Database { init() { } }

                protocol OrderRepository { }

                @injectable(.Scoped, as: OrderRepository.self)
                final class SqlOrderRepository: OrderRepository {
                    // The parameter is where it is said.
                    init(db: Database) { }
                }

                @injectable(.Scoped)
                final class OrderService { init(repository: any OrderRepository) { } }

                @container
                struct Services { }
                """));

    [Fact]
    public void ACaptiveDependencyIsSaid() =>
        Assert.Equal(
            "10:22 container: Cache → Reader → Session: a singleton would hold a scoped service",
            Errors("""
                @injectable(.Scoped)
                final class Session { init() { } }

                @injectable(.Transient)
                struct Reader { init(session: Session) { } }

                @immutable
                @injectable(.Singleton)
                final class Cache { init(reader: Lazy<Reader>) { } }

                @container
                struct Services { }
                """));

    [Fact]
    public void ACycleIsSaidUnlessALazyOrAFactoryIsOnIt() {
        const string source = """
            @injectable(.Scoped)
            final class A { init(b: B) { } }

            @injectable(.Scoped)
            final class B { init(c: C) { } }

            @injectable(.Scoped)
            final class C { init(a: A) { } }

            @container
            struct Services { }
            """;
        Assert.Equal("13:22 container: A → B → C → A: a service needs itself, and nothing on the way is a Lazy or a factory", Errors(source));
        Assert.Equal("", Errors(source.Replace("a: A", "a: Lazy<A>", StringComparison.Ordinal)));
        Assert.Equal("", Errors(source.Replace("a: A", "a: @escaping () -> A", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnAmbiguousRegistrationIsSaidWhereOneIsAskedFor() {
        const string source = """
            protocol Clock { }

            @injectable(.Transient, as: Clock.self)
            struct One: Clock { }

            @container
            struct Services {
                @transient fn other() -> any Clock { return One() }
                @transient fn timer(clock: any Clock) -> Int { return 1 }
            }
            """;
        Assert.Equal("14:25 container: Int → Clock: 2 are registered where one is asked for: One and other()", Errors(source));
        Assert.Equal("", Errors(source.Replace("clock: any Clock", "clock: (any Clock)[]", StringComparison.Ordinal)));
    }

    [Fact]
    public void OnlyASingletonsMakingMayThrowOrWait() =>
        Assert.Equal(
            """
            8:19 container: Int is made by what may throw, and only a singleton may be, which the container makes as it is built: 'resolve' never throws, and never waits
            9:22 container: String is made by waiting, and only a singleton may be, which the container makes as it is built: 'resolve' never throws, and never waits
            """.ReplaceLineEndings("\n"),
            Errors("""
                @container
                struct Services {
                    @transient fn number() throws -> Int { return 1 }
                    @scoped async fn text() -> String { return "" }
                }
                """));

    [Fact]
    public void AskingForWhatAContainerDoesNotProvideDoesNotCompile() {
        const string source = """
            @injectable(.Scoped)
            final class Session { init() { } }

            @container
            struct Services { }

            fn use() {
                let provider = Services.build()
                _ = provider.resolve(Session.self)
            }
            """;
        // A scoped service is a scope's to give, not the provider's.
        Assert.Equal("14:9 'resolve' cannot be called with these arguments: it requires 'Self: Provides<Session>', and 'ServicesProvider' is not", Errors(source));
        Assert.Equal("", Errors(source.Replace("_ = provider.resolve(Session.self)", "provider.withScope { scope in _ = scope.resolve(Session.self) }", StringComparison.Ordinal)));
        Assert.Contains("it requires 'Self: Provides<Int>'", Errors(source.Replace("_ = provider.resolve(Session.self)", "provider.withScope { scope in _ = scope.resolve(Int.self) }", StringComparison.Ordinal)));
    }

    [Fact]
    public void BuildHasTheEffectsOfItsSingletons() {
        const string source = """
            @container
            struct Services {
                @singleton EFFECTS
            }

            fn use() { _ = Services.build() }
            """;
        Assert.Equal("", Errors(source.Replace("EFFECTS", "fn number() -> Int { return 1 }", StringComparison.Ordinal)));
        Assert.Contains("'build' can throw", Errors(source.Replace("EFFECTS", "fn number() throws -> Int { return 1 }", StringComparison.Ordinal)));
        Assert.Contains("'build' is async", Errors(source.Replace("EFFECTS", "async fn number() -> Int { return 1 }", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASingletonIsSendable() =>
        Assert.Equal("7:13 container: Open is a singleton, which any thread may use, and is not Sendable", Errors("""
            @injectable(.Singleton)
            final class Open { var count: Int = 0
                init() { } }

            @container
            struct Services { }
            """));

    [Fact]
    public void AContainerIsSendable() =>
        Assert.Contains("a container is shared by whoever uses its provider, so it is Sendable", Errors("""
            final class State { var count: Int = 0 }

            @container
            struct Services { let state: State }
            """));

    [Theory]
    [InlineData("@injectable(.Forever)\nstruct S { }", "'@injectable' says how long a service lives: '.Singleton', '.Scoped' or '.Transient', not '.Forever'")]
    [InlineData("protocol P { }\n@injectable(.Scoped, as: P.self)\nstruct S { }", "'S' is not a 'P'")]
    [InlineData("@injectable(.Scoped, named: \"s\")\nstruct S { }", "'@injectable' takes a lifetime, 'as:' and 'key:', not 'named:'")]
    [InlineData("@injectable\nenum S { One }", "a container makes a class, an actor or a struct, by its initialiser; 'S' is none of them")]
    [InlineData("@injectable\nfinal class S { init() { }\n init(a: Int) { } }", "'S' has 2 initialisers; mark the one a container is to use '@inject'")]
    [InlineData("@injectable\nfinal class S { init(f: @escaping (Int) -> Int) { } }", "its parameter 'f' is a '(Int) -> Int'; a container gives a function that takes nothing, and neither throws nor waits")]
    [InlineData("let k = \"k\"\n@injectable\nfinal class S { init(@keyed(k) a: Int) { } }", "its parameter 'a' says '@keyed' of something other than a key: a string literal")]
    [InlineData("@injectable(.Scoped)\nfinal class S { async init() { } }", "'S' has an initialiser that waits, so it can only be a singleton, which is made as its provider is built")]
    [InlineData("@container\nenum C { One }", "a container is a struct or a class, and is not generic; 'C' is not one")]
    [InlineData("@container\nstruct C { @include(Nowhere.Services.self) @singleton fn a() -> Int { return 1 } }", "'@include(Nowhere.Services.self)' names no package's services")]
    [InlineData("@container\nstruct C { @keyed(\"a\") fn a() -> Int { return 1 } }", "'a' says '@keyed' but not how long what it makes lives")]
    [InlineData("@container\nstruct C { @singleton fn a() { } }", "'a' gives nothing to register")]
    [InlineData("@container\nstruct C { @transient fn a(scope: ServiceScope) -> Int { return 1 } }", "a container made by '@container' has no ServiceScope to give")]
    public void WhatCannotBeInjectedIsSaid(string source, string message) => Assert.Contains(message, Errors(source));

    [Fact]
    public void AnOpenGenericIsClosedWhereItsConstraintsAreMet() =>
        Assert.Equal(
            "13:41 container: Asks → Store<Other>: nothing provides Store<Other>",
            Errors("""
                protocol Entity { }
                struct Known: Entity { }
                struct Other { }

                @injectable(.Scoped)
                final class Store<Item: Entity> { init() { } }
                @injectable(.Scoped)
                struct Asks { init(known: Store<Known>, other: Store<Other>) { } }

                @container
                struct Services { }
                """));

    [Fact]
    public void AMethodsWhereSelfIsAskedOfEachCall() {
        // 'where Self: P' of one method asks nothing of another.
        var errors = Pipeline.Check([SyntaxTree.Parse(new SourceText("""
            module App

            protocol Gives<T> { fn give(_ type: T.Type) -> T }
            protocol Box { }
            extension Box {
                fn open<T>(_ type: T.Type) -> T where Self: Gives<T> { return give(type) }
                fn shut() -> Int { return 1 }
            }
            struct Plain: Box { }
            struct Both: Box, Gives<Int>, Gives<String> {
                fn give(_ type: Int.Type) -> Int { return 1 }
                fn give(_ type: String.Type) -> String { return "s" }
            }
            fn use() {
                _ = Plain().shut()
                let i: Int = Both().open(Int.self)
                let s: String = Both().open(String.self)
                _ = (i, s)
                _ = Plain().open(Int.self)
            }
            """, "app.sb"))]);
        var error = Assert.Single(errors);
        Assert.Contains("it requires 'Self: Gives<Int>', and 'Plain' is not", error);
    }

    [Fact]
    public void ATypeMayConformToAProtocolOfSeveralTypes() =>
        Assert.Equal("1 s\n1\n", Programs.AssertAgree("""
            module App

            protocol Gives<T> { fn give(_ type: T.Type) -> T }
            protocol Box { }
            extension Box {
                fn open<T>(_ type: T.Type) -> T where Self: Gives<T> { return give(type) }
            }
            struct Both: Box, Gives<Int>, Gives<String> {
                fn give(_ type: Int.Type) -> Int { return 1 }
                fn give(_ type: String.Type) -> String { return "s" }
            }
            fn number<B: Gives<Int>>(_ b: B) -> Int { return b.give(Int.self) }
            fn main() {
                print("\(Both().open(Int.self)) \(Both().open(String.self))")
                print(number(Both()))
            }
            """).Stdout);

    [Fact]
    public void NorIsItAnExistentialOfOneOfThem() =>
        // One table would be all of them: an error, not the wrong member.
        Assert.Contains("through an existential yet", Assert.Single(Pipeline.Compile(new SourceText("""
            module App

            protocol Takes<T> { fn take(_ t: T) -> String }
            struct Both: Takes<Int>, Takes<String> {
                fn take(_ t: Int) -> String { return "int" }
                fn take(_ t: String) -> String { return "string" }
            }
            fn main() {
                let s: any Takes<String> = Both()
                print(s.take("a"))
            }
            """, "app.sb")).Errors));

    [Fact]
    public void AnArrayThatGrewIsCastToOneThatIsDone() =>
        Assert.Equal("9 1 3\n4\n", Programs.AssertAgree("""
            module App

            fn done<T>(_ xs: T[>..]) -> T[] { return xs as T[] }
            fn main() {
                var xs: Int[>..] = [1, 2]
                xs.append(3)
                var a: Int[] = xs as Int[]
                a[0] = 9
                print("\(a[0]) \(xs[0]) \(done(xs).count)")
                var more = a as Int[>..]
                more.append(1)
                print(more.count)
            }
            """).Stdout);

    [Fact]
    public void TheCompilerKnowsTheLibraryGeneratorsAttributes() =>
        Assert.Equal(
            LibraryGenerators.All.Select(static g => $"{g.Module}: {string.Join(",", g.Set.Attributes.Types.Order(StringComparer.Ordinal))}; {string.Join(",", g.Set.Attributes.Members.Distinct().Order(StringComparer.Ordinal))}"),
            Compilation.LibraryGeneratorAttributes.Select(static g => $"{g.Module}: {string.Join(",", g.Types.Order(StringComparer.Ordinal))}; {string.Join(",", g.Members.Order(StringComparer.Ordinal))}"));

    [Fact]
    public void TheAttributesAreKnownOnlyWhereTheModuleIsImported() {
        var errors = Pipeline.Check([SyntaxTree.Parse(new SourceText("module App\n\n@injectable\nstruct S { }\n", "app.sb"))]);
        Assert.Contains("injectable", Assert.Single(errors));
    }

    [Fact]
    public void TheEditorHasWhatIsWrittenForAFileInNoPackage() {
        var tree = SyntaxTree.Parse(new SourceText("module App\n\nimport Sable.DependencyInjection\n\n@injectable\nstruct S { }\n\n@container\nstruct C { @singleton fn a(b: Int) -> String { return \"\" } }\n", "app.sb"));
        var generated = LibraryGenerators.ForEditor([tree]);
        Assert.Contains("extension S: Injectable", Assert.Single(generated.Trees).Text.Text);
        var (diagnostic, at) = Assert.Single(generated.Diagnostics);
        Assert.Same(tree, at);
        Assert.Contains("String → Int: nothing provides Int", diagnostic.Message);
    }

    // ---- Packages ---------------------------------------------------------------

    const string Payments = """
        module Payments

        import Sable.DependencyInjection

        public protocol Gateway: Sendable { fn name() -> String }

        // Kept to itself: made through the package's fragment.
        @injectable(.Singleton, as: Gateway.self)
        struct CardGateway: Gateway {
            let bank: any Bank
            fn name() -> String { return "cards at \(bank.name())" }
        }

        public protocol Bank: Sendable { fn name() -> String }

        @immutable
        @injectable(.Singleton)
        public final class Ledger<Entry> {
            public init() { }
            public fn name() -> String { return "ledger of \(Entry.self)" }
        }

        @injectable(.Transient)
        struct Secret { }

        @injectable(.Transient)
        struct Keeper { let secret: Secret }
        """;

    const string App = """
        module App

        import Payments
        import Sable.DependencyInjection

        struct Coin { }

        struct Vault: Bank { fn name() -> String { return "the vault" } }

        @injectable(.Scoped)
        final class Till {
            let gateway: any Gateway
            let ledger: Ledger<Coin>
            init(gateway: any Gateway, ledger: Ledger<Coin>) {
                self.gateway = gateway
                self.ledger = ledger
            }
        }

        @container
        struct AppServices {
            @include(Payments.Services.self)
            @singleton fn bank() -> any Bank { return Vault() }
        }

        fn show(_ till: Till) -> String { return "\(till.gateway.name()), \(till.ledger.name())" }

        @main
        fn main() {
            print(AppServices.build().withScope { scope in show(scope.resolve(Till.self)) })
            var services = ServiceCollection()
            services.add(AppServices.self)
            print((try! services.build()).withScope { scope in show(try! scope.resolve(Till.self)) })
        }
        """;

    static int Check(string app, string payments, out string errors, PackageFixture fixture) {
        fixture.Package("payments", "payments", "1.0.0", "", ("src/lib.sb", payments));
        fixture.Package("app", "app", "1.0.0", "[dependencies]\npayments = { path = \"../payments\" }\n", ("src/main.sb", app));
        var code = PackageBuilder.Check(fixture.Load("app"), fixture.Environment, new());
        errors = fixture.Errors.ToString();
        return code;
    }

    [Fact]
    public void AContainerIncludesAnotherPackagesFragment() {
        using var fixture = new PackageFixture();
        Assert.True(Check(App, Payments, out var errors, fixture) == 0, errors);
        var fragment = File.ReadAllText(Path.Combine(fixture.Load("app").TargetDirectory, "generated", "payments", "container", "Services.sb"));
        Assert.Contains("public struct Services: StandardModule", fragment);
        Assert.Contains("public static fn makeCardGateway(bank: any Bank) -> any Gateway", fragment);
        Assert.DoesNotContain("makeKeeper", fragment);

        if (PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new()) is [var binary]) {
            Assert.Equal(("cards at the vault, ledger of App.Coin\ncards at the vault, ledger of App.Coin\n", 0), PackageFixture.Execute(binary.Path));
        }
    }

    [Fact]
    public void AFragmentsGraphIsCheckedWithTheContainers() {
        using var fixture = new PackageFixture();
        Assert.Equal(1, Check(App.Replace("    @singleton fn bank() -> any Bank { return Vault() }\n", "    @singleton fn other() -> Int { return 1 }\n", StringComparison.Ordinal), Payments, out var errors, fixture));
        // Where the container is: the dependency's file is not this package's to change.
        Assert.Contains("container: Till → Gateway → Bank: nothing provides Bank", errors);
        Assert.Contains("src/main.sb:21:8", errors);
    }

    [Fact]
    public void WhatAPackageKeepsToItselfIsNotIncluded() {
        using var fixture = new PackageFixture();
        var payments = Payments.Replace("public protocol Gateway", "protocol Gateway", StringComparison.Ordinal);
        var app = App.Replace("let gateway: any Gateway", "let gateway: Int", StringComparison.Ordinal).Replace("gateway: any Gateway,", "gateway: Int,", StringComparison.Ordinal).Replace("till.gateway.name()", "till.gateway", StringComparison.Ordinal);
        Assert.Equal(1, Check(app, payments, out var errors, fixture));
        Assert.Contains("container: Till → Int: nothing provides Int", errors);
    }
}
