namespace Sable.Symbols.Tests;

/// <summary>
///     <c>@derive(Injectable)</c> (#81): what it makes of an initialiser, and
///     the initialisers it refuses. The library's own tests run what it makes.
/// </summary>
public sealed class InjectableTests {
    /// <summary>As much of Sable.DependencyInjection as the members it makes use.</summary>
    const string Library = """
        module Sable.DependencyInjection
        public struct Keyed: Attribute { public let key: String
            public init(_ key: String) { self.key = key } }
        public struct Inject: Attribute { public init() { } }
        public enum DependencyKind { Required, Optional, All, Deferred }
        public struct ServiceDependency {
            public init(_ type: Type, key: String? = none, kind: DependencyKind = DependencyKind.Required) { }
        }
        public final class ServiceScope {
            public fn resolve<T>(_ type: T.Type, key: String? = none) -> T { fatalError("") }
            public fn resolve<T>(_ type: T.Type, key: String? = none, otherwise fallback: () -> T) -> T { return fallback() }
            public fn find<T>(_ type: T.Type, key: String? = none) -> T? { return none }
            public fn resolveAll<T>(_ type: T.Type, key: String? = none) -> T[>..] { return [] }
            public fn factory<T>(_ type: T.Type, key: String? = none) -> () -> T { fatalError("") }
        }
        public protocol Injectable {
            static fn create(from services: ServiceScope) throws -> Self
            static fn dependencies() -> ServiceDependency[>..]
        }
        """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create(Library, "module M\nimport Sable.DependencyInjection\n" + source)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static string Refused(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 1, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        Assert.Equal(ErrorCode.ERR_CannotDerive, errors[0].Code);
        return errors[0].Message;
    }

    [Fact]
    public void AClassAStructAndAnActorDeriveIt() =>
        AssertClean(
            """
            @derive(Injectable) final class A { init() { } }
            @derive(Injectable) struct B { let a: A, let n: Int = 3 }
            @derive(Injectable) actor C { init(a: A, b: B) { } }
            @derive(Injectable) final class D<T> {
                init(a: A?, all: B[], make: @escaping () -> B, @Keyed("k") text: String, scope: ServiceScope) { }
            }
            fn all(_ scope: ServiceScope) throws -> Int {
                let _ = try A.create(from: scope)
                let d: D<Int> = try D<Int>.create(from: scope)
                return B.dependencies().count + D<Int>.dependencies().count
            }
            """
        );

    [Fact]
    public void OfSeveralInitialisersOneIsMarked() {
        Assert.Contains("has 2 initialisers; mark the one", Refused("@derive(Injectable) final class A { init() { }\n init(n: Int) { } }"), StringComparison.Ordinal);
        AssertClean("@derive(Injectable) final class A { init() { }\n @Inject init(n: Int) { } }");
    }

    [Fact]
    public void AnInitialiserWithNothingToGiveItIsRefused() {
        Assert.Contains("a place to change", Refused("@derive(Injectable) final class A { init(n: inout Int) { } }"), StringComparison.Ordinal);
        Assert.Contains("any number of values", Refused("@derive(Injectable) final class A { init(n: Int...) { } }"), StringComparison.Ordinal);
        Assert.Contains("a function that takes nothing", Refused("@derive(Injectable) final class A { init(n: @escaping (Int) -> Int) { } }"), StringComparison.Ordinal);
        Assert.Contains("an async initialiser", Refused("@derive(Injectable) final class A { async init() { } }"), StringComparison.Ordinal);
        Assert.Contains("type parameters of its own", Refused("@derive(Injectable) final class A { init<T>(t: T) { } }"), StringComparison.Ordinal);
        Assert.Contains(ErrorsIn("let k: String = \"k\"\n@derive(Injectable) final class A { init(@Keyed(k) t: String) { } }"), static e => e.Message.Contains("as a string literal", StringComparison.Ordinal));
    }

    [Fact]
    public void AUnionAndAnEnumDoNotDeriveIt() {
        Assert.Contains("classes, actors and structs", Refused("@derive(Injectable) union U { A, B(Int) }"), StringComparison.Ordinal);
        Assert.Contains("classes, actors and structs", Refused("@derive(Injectable) enum E { A, B }"), StringComparison.Ordinal);
    }

    [Fact]
    public void ItNeedsItsModule() {
        var errors = Compilation.Create("module M\n@derive(Injectable) final class A { init() { } }").GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Contains(errors, static e => e.Message.Contains("import Sable.DependencyInjection", StringComparison.Ordinal));
    }

    [Fact]
    public void AGenericTypeBareIsAGenericType() {
        AssertClean("struct Box<T> { let t: T }\nprotocol P<T> { }\nfn f() -> GenericType[] { return [Box.self, P.self] }");
        AssertClean("fn f() -> Type[] { return [Int.self, String.self] }\nfn g(_ types: Type[>..]) { }\nfn h() { g([Int.self]) }");
    }
}
