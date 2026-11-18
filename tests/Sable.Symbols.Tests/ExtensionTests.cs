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

    // ---- Constrained extensions -------------------------------------------

    const string Describable = """
                               protocol Describable { fn describe() -> String }
                               struct Cat: Describable { fn describe() -> String => "cat" }
                               struct Box<T> { var v: T }
                               """;

    [Fact]
    public void AConstrainedExtensionsBodySeesItsConformanceRequirement() =>
        AssertClean(
            $$"""
              {{Describable}}
              extension Box where T: Describable {
                  fn describe() -> String => "box of " + v.describe()
              }
              fn probe(_ b: Box<Cat>) -> String => b.describe()
              """
        );

    [Fact]
    public void AConstrainedExtensionsBodySeesItsSameTypeRequirement() =>
        AssertClean(
            """
            struct Wrapper<T> { var v: T }
            extension Wrapper where T == Int {
                fn doubled() -> Int => v * 2
                fn quadrupled() -> Int => doubled() * 2
            }
            fn probe(_ w: Wrapper<Int>) -> Int => w.doubled()
            """
        );

    [Fact]
    public void ARenamedParameterCarriesTheRequirement() =>
        AssertClean(
            $$"""
              {{Describable}}
              extension Box<U> where U: Describable {
                  fn describe() -> String => v.describe()
              }
              """
        );

    [Fact]
    public void AConstrainedExtensionsRequirementDoesNotLeakIntoTheType() {
        var errors = ErrorsIn(
            $$"""
              {{Describable}}
              extension Box where T: Describable { fn describe() -> String => v.describe() }
              extension Box { fn other() -> String => v.describe() }
              """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NameNotFound && e.Message.Contains("'describe'"));
    }

    [Fact]
    public void AConstrainedMemberIsNotThereWhereTheRequirementFails() {
        var errors = ErrorsIn(
            """
            struct Wrapper<T> { var v: T }
            extension Wrapper where T == Int { fn doubled() -> Int => v * 2 }
            fn probe(_ w: Wrapper<String>) -> Int => w.doubled()
            """
        );
        var error = Assert.Single(errors);
        Assert.Equal(ErrorCode.ERR_DoesNotConform, error.Code);
        Assert.Contains("'Wrapper<String>' has 'doubled' only where 'T == Int'", error.Message);
    }

    [Fact]
    public void AConstrainedMemberIsNotThereThroughSelfOutsideTheExtension() {
        var errors = ErrorsIn(
            """
            struct Wrapper<T> {
                var v: T
                fn tryIt() -> Int => doubled()
            }
            extension Wrapper where T == Int { fn doubled() -> Int => v * 2 }
            """
        );
        var error = Assert.Single(errors);
        Assert.Equal(ErrorCode.ERR_DoesNotConform, error.Code);
        Assert.Contains("only where 'T == Int'", error.Message);
    }

    [Fact]
    public void AConditionalConformanceHoldsWhereItsRequirementDoes() =>
        AssertClean(
            $$"""
              {{Describable}}
              extension Box: Describable where T: Describable {
                  fn describe() -> String => "box of " + v.describe()
              }
              fn show<D: Describable>(_ d: D) -> String => d.describe()
              fn probe() -> String => show(Box(v: Box(v: Cat())))
              """
        );

    [Fact]
    public void AConditionalConformanceFailsWhereItsRequirementDoes() {
        var errors = ErrorsIn(
            $$"""
              {{Describable}}
              extension Box: Describable where T: Describable {
                  fn describe() -> String => "box of " + v.describe()
              }
              fn show<D: Describable>(_ d: D) -> String => d.describe()
              fn probe() -> String => show(Box(v: 3))
              """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NoMatchingOverload && e.Message.Contains("'Box<Int>', which is not 'Describable'"));
    }

    [Fact]
    public void AConditionalConformanceThroughAGenericParameterNeedsItsRequirement() {
        var errors = ErrorsIn(
            $$"""
              {{Describable}}
              extension Box: Describable where T: Describable {
                  fn describe() -> String => v.describe()
              }
              fn show<D: Describable>(_ d: D) -> String => d.describe()
              fn good<T: Describable>(_ t: T) -> String => show(Box(v: t))
              fn bad<T>(_ t: T) -> String => show(Box(v: t))
              """
        );
        var error = Assert.Single(errors);
        Assert.Equal(ErrorCode.ERR_NoMatchingOverload, error.Code);
    }

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
