namespace Sable.Symbols.Tests;

/// <summary>
///     Extensions: what they may add, where their members end up, and what names
///     they can introduce. Plus the initialiser a struct gets for free.
/// </summary>
public sealed class ExtensionTests {
    static Compilation Compile(string source) => Compilation.Create("module M\n" + source);

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compile(source)
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

    const string Point = """
                         struct Point { var x: Int, var y: Int }
                         """;

    // ---- Where the members land -------------------------------------------

    [Fact]
    public void AnExtensionMethodIsAMemberOfTheExtendedType() =>
        AssertClean(
            $$"""
              {{Point}}
              extension Point { fn sum() -> Int { return x + y } }
              fn probe(_ p: Point) -> Int { return p.sum() }
              """
        );

    [Fact]
    public void AnExtensionMayComeBeforeTheDeclaration() =>
        AssertClean(
            $$"""
              extension Point { fn sum() -> Int { return x + y } }
              {{Point}}
              fn probe(_ p: Point) -> Int { return p.sum() }
              """
        );

    [Fact]
    public void AnExtensionMayAddAComputedProperty() =>
        AssertClean(
            $$"""
              {{Point}}
              extension Point { var isOrigin: Bool { get => x == 0 && y == 0 } }
              fn probe(_ p: Point) -> Bool { return p.isOrigin }
              """
        );

    [Fact]
    public void AnExtensionMayAddAStaticMember() =>
        AssertClean(
            $$"""
              {{Point}}
              extension Point { static fn zero() -> Point { return Point { x: 0, y: 0 } } }
              fn probe() -> Point { return Point.zero() }
              """
        );

    [Fact]
    public void AnExtensionMayAddAnOperator() =>
        AssertClean(
            $$"""
              {{Point}}
              extension Point {
                  operator + (a: Point, b: Point) -> Point {
                      return Point { x: a.x + b.x, y: a.y + b.y }
                  }
              }
              fn probe(_ a: Point, _ b: Point) -> Point { return a + b }
              """
        );

    // ---- What they may not add --------------------------------------------

    [Fact]
    public void AnExtensionCannotAddAStoredProperty() {
        var errors = ErrorsIn(
            $$"""
              {{Point}}
              extension Point { var z: Int }
              """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_ExtensionStoredProperty);
    }

    [Fact]
    public void AnExtensionCannotAddAnInitializer() {
        var errors = ErrorsIn(
            $$"""
              {{Point}}
              extension Point { init(_ n: Int) { } }
              """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_ExtensionInitializer);
    }

    [Fact]
    public void OnlyANamedTypeCanBeExtended() {
        var errors = ErrorsIn("extension (Int, Int) { fn f() { } }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_CannotExtend);
    }

    // ---- Conformance and generics -----------------------------------------

    [Fact]
    public void AnExtensionMayAddAConformance() =>
        AssertClean(
            $$"""
              protocol Named { fn name() -> String }
              {{Point}}
              extension Point: Named { fn name() -> String { return "p" } }
              fn probe(_ value: some Named) -> String { return value.name() }
              fn use(_ p: Point) -> String { return probe(p) }
              """
        );

    [Fact]
    public void AnUnsatisfiedRetroactiveConformanceIsReported() {
        var errors = ErrorsIn(
            $$"""
              protocol Named { fn name() -> String }
              {{Point}}
              extension Point: Named { }
              """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_DoesNotConform);
    }

    // ---- Coherence ---------------------------------------------------------

    static Compilation ThreeModules(string third) =>
        Compilation.Create(
            [
                SyntaxTree.Parse(
                    new(
                        """
                        module Types
                        public struct Point { public var x: Int }
                        """,
                        "types.sb"
                    )
                ),
                SyntaxTree.Parse(
                    new(
                        """
                        module Protocols
                        public protocol Named { fn name() -> String }
                        """,
                        "protocols.sb"
                    )
                ),
                SyntaxTree.Parse(new(third, "third.sb"))
            ]
        );

    [Fact]
    public void AConformanceMayBeDeclaredInTheTypesModule() {
        var compilation = ThreeModules(
            """
            module Types
            import Protocols
            extension Point: Named { fn name() -> String { return "p" } }
            """
        );

        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static d => d.Code == ErrorCode.ERR_IncoherentConformance
        );
    }

    [Fact]
    public void AConformanceMayBeDeclaredInTheProtocolsModule() {
        var compilation = ThreeModules(
            """
            module Protocols
            import Types
            extension Point: Named { fn name() -> String { return "p" } }
            """
        );

        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static d => d.Code == ErrorCode.ERR_IncoherentConformance
        );
    }

    [Fact]
    public void AConformanceMayNotBeDeclaredInAThirdModule() {
        var compilation = ThreeModules(
            """
            module Elsewhere
            import Types
            import Protocols
            extension Point: Named { fn name() -> String { return "p" } }
            """
        );

        Assert.Contains(
            compilation.GetAllDiagnostics(),
            static d => d.Code == ErrorCode.ERR_IncoherentConformance
        );
    }

    [Fact]
    public void AnExtensionMayRenameTheTypeParameters() =>
        AssertClean(
            """
            struct Box<Item> { var item: Item }
            extension Box<T> { fn held() -> T { return item } }
            fn probe(_ b: Box<Int>) -> Int { return b.held() }
            """
        );

    // ---- Memberwise initialisers ------------------------------------------

    [Fact]
    public void AStructGetsAMemberwiseInitializer() =>
        AssertClean(
            $$"""
              {{Point}}
              fn probe() -> Point { return Point(x: 1, y: 2) }
              """
        );

    [Fact]
    public void ADeclaredInitializerReplacesTheMemberwiseOne() {
        var errors = ErrorsIn(
            """
            struct Point {
                var x: Int
                var y: Int
                init(_ n: Int) { self.x = n; self.y = n }
            }
            fn probe() -> Point { return Point(x: 1, y: 2) }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NoMatchingOverload);
    }

    [Fact]
    public void TheMemberwiseInitializerChecksItsArgumentTypes() {
        var errors = ErrorsIn(
            $$"""
              {{Point}}
              fn probe() -> Point { return Point(x: "a", y: 2) }
              """
        );
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void AMemberwiseInitializerMayLeaveOutDefaultedFields() =>
        AssertClean(
            """
            struct D { var a: Int = 1; var b: Int = 5 }
            struct E { var x: Int; var y: Int = 9; var z: String = "z" }
            fn probe() -> Int {
                let d = D()
                let e = D(b: 2)
                let f = E(x: 3, z: "q")
                return d.a + e.b + f.y
            }
            """
        );

    [Fact]
    public void AMemberwiseInitializerStillNeedsTheFieldsWithoutDefaults() {
        var errors = ErrorsIn(
            """
            struct E { var x: Int; var y: Int = 9 }
            fn probe() -> E { return E(y: 2) }
            """
        );
        Assert.NotEmpty(errors);
    }

    // ---- Lookup through what stands in for a type -------------------------

    [Fact]
    public void AClassInheritsItsBaseMembers() =>
        AssertClean(
            """
            open class Animal { fn speak() -> String { return "" } }
            class Dog: Animal { }
            fn probe(_ d: Dog) -> String { return d.speak() }
            """
        );

    [Fact]
    public void ATypeParameterHasItsConstraintsMembers() =>
        AssertClean(
            """
            protocol Named { fn name() -> String }
            fn probe<T: Named>(_ value: T) -> String { return value.name() }
            """
        );

    [Fact]
    public void AnExistentialHasItsProtocolsMembers() =>
        AssertClean(
            """
            protocol Named { fn name() -> String }
            fn probe(_ value: any Named) -> String { return value.name() }
            """
        );
}
