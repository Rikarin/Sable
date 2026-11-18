namespace Sable.Symbols.Tests;

public sealed class GenericsAndConformanceTests {
    static Compilation Compile(string source) => Compilation.Create("module M\n" + source);

    static IReadOnlyList<Diagnostic> Errors(string source) => [
        ..Compile(source)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = Errors(source);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message} {e.Span}"))
        );
    }

    /// <summary>The type of the last binding's initialiser in 'probe'.</summary>
    static string TypeOfLast(string source) {
        var compilation = Compile(source);
        // The functions it calls are signatures alone: what inference needs.
        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error && d.Code != ErrorCode.ERR_MissingBody)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => e.Message)));

        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);

        var last = tree.Root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .Last()
            .Declarator.Initializer!.Value;

        return model.GetTypeOf(last)!.ToDisplayString();
    }

    // ---- Substitution ------------------------------------------------------

    [Fact]
    public void AConstructedTypesFieldIsSubstituted() =>
        Assert.Equal(
            "Int",
            TypeOfLast(
                """
                struct Box<T> { var item: T }
                fn probe(_ b: Box<Int>) {
                    let v = b.item
                }
                """
            )
        );

    [Fact]
    public void NestedSubstitutionWorks() =>
        Assert.Equal(
            "Box<String>",
            TypeOfLast(
                """
                struct Box<T> { var item: T }
                fn probe(_ b: Box<Box<String>>) {
                    let v = b.item
                }
                """
            )
        );

    [Fact]
    public void SubstitutionReachesIntoStructuralTypes() =>
        Assert.Equal(
            "Int[]",
            TypeOfLast(
                """
                struct Holder<T> { var items: T[] }
                fn probe(_ h: Holder<Int>) {
                    let v = h.items
                }
                """
            )
        );

    [Fact]
    public void AMethodsReturnTypeIsSubstituted() =>
        Assert.Equal(
            "Int",
            TypeOfLast(
                """
                struct Box<T> {
                    var item: T
                    fn get() -> T { return item }
                }
                fn probe(_ b: Box<Int>) {
                    let v = b.get()
                }
                """
            )
        );

    [Fact]
    public void AMethodsParameterTypeIsSubstituted() {
        AssertClean(
            """
            struct Box<T> {
                fn put(_ value: T) { }
            }
            fn probe(_ b: Box<Int>) {
                b.put(1)
            }
            """
        );

        Assert.Contains(
            Errors(
                """
                struct Box<T> {
                    fn put(_ value: T) { }
                }
                fn probe(_ b: Box<Int>) {
                    b.put("s")
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );
    }

    // ---- Inference ---------------------------------------------------------

    [Fact]
    public void InfersFromAnArgument() =>
        Assert.Equal(
            "Int",
            TypeOfLast(
                """
                fn identity<T>(_ value: T) -> T { return value }
                fn probe() {
                    let v = identity(1)
                }
                """
            )
        );

    [Fact]
    public void InfersFromAnArrayArgument() =>
        Assert.Equal(
            "String",
            TypeOfLast(
                """
                fn first<T>(_ items: T[]) -> T
                fn probe() {
                    let xs = ["a", "b"]
                    let v = first(xs)
                }
                """
            )
        );

    [Fact]
    public void InfersSeveralParameters() =>
        Assert.Equal(
            "(Int, String)",
            TypeOfLast(
                """
                fn pair<A, B>(_ a: A, _ b: B) -> (A, B)
                fn probe() {
                    let v = pair(1, "s")
                }
                """
            )
        );

    [Fact]
    public void InfersThroughAConstructedType() =>
        Assert.Equal(
            "Int",
            TypeOfLast(
                """
                struct Box<T> { }
                fn unwrap<T>(_ box: Box<T>) -> T
                fn probe(_ b: Box<Int>) {
                    let v = unwrap(b)
                }
                """
            )
        );

    [Fact]
    public void InconsistentInferenceIsRejected() =>
        Assert.Contains(
            Errors(
                """
                fn same<T>(_ a: T, _ b: T) -> T
                fn probe() {
                    let v = same(1, "s")
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );

    [Fact]
    public void AnUninferableParameterIsReported() =>
        Assert.Contains(
            Errors(
                """
                fn make<T>() -> T
                fn probe() {
                    let v = make()
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_CannotInferTypeArgument
        );

    // ---- Conformance -------------------------------------------------------

    [Fact]
    public void AConformingTypeIsAccepted() =>
        AssertClean(
            """
            protocol Drawable {
                fn draw() -> Int
                var size: Int { get }
            }
            struct Circle: Drawable {
                fn draw() -> Int { return 1 }
                var size: Int { get => 2 }
            }
            """
        );

    [Fact]
    public void AMissingRequirementIsReported() {
        var error = Assert.Single(
            Errors(
                """
                protocol Drawable { fn draw() -> Int }
                struct Circle: Drawable { }
                """
            ),
            static e => e.Code == ErrorCode.ERR_DoesNotConform
        );

        Assert.Contains("draw", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMismatchedSignatureIsReported() =>
        Assert.Contains(
            Errors(
                """
                protocol Drawable { fn draw() -> Int }
                struct Circle: Drawable {
                    fn draw() -> String { return "s" }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_RequirementSignatureMismatch
        );

    [Fact]
    public void AMissingPropertyIsReported() =>
        Assert.Contains(
            Errors(
                """
                protocol Sized { var size: Int { get } }
                struct Box: Sized { }
                """
            ),
            static e => e.Code == ErrorCode.ERR_DoesNotConform
        );

    [Fact]
    public void ADefaultImplementationIsNotARequirement() =>
        AssertClean(
            """
            protocol Named {
                fn name() -> String
                fn describe() -> String { return name() }
            }
            struct Thing: Named {
                fn name() -> String { return "thing" }
            }
            """
        );

    [Fact]
    public void SelfInARequirementMeansTheConformingType() =>
        AssertClean(
            """
            protocol Copyable {
                fn copy() -> Self
            }
            struct Point: Copyable {
                fn copy() -> Point { return self }
            }
            """
        );

    [Fact]
    public void AnInheritedMemberSatisfiesARequirement() =>
        AssertClean(
            """
            protocol Drawable { fn draw() -> Int }
            class Base {
                fn draw() -> Int { return 1 }
            }
            class Derived: Base, Drawable { }
            """
        );

    [Fact]
    public void ConformingToSomethingThatIsNotAProtocolIsReported() =>
        Assert.Contains(
            Errors(
                """
                struct A { }
                struct B: A { }
                """
            ),
            static e => e.Code == ErrorCode.ERR_NotAProtocol
        );

    [Fact]
    public void TwoBaseClassesAreReported() =>
        Assert.Contains(
            Errors(
                """
                class A { }
                class B { }
                class C: A, B { }
                """
            ),
            static e => e.Code == ErrorCode.ERR_MultipleBaseClasses
        );

    [Fact]
    public void CircularInheritanceIsReported() =>
        Assert.Contains(
            Errors(
                """
                class A: B { }
                class B: A { }
                """
            ),
            static e => e.Code == ErrorCode.ERR_CircularInheritance
        );

    [Fact]
    public void AnEnumsUnderlyingTypeIsNotInheritance() => AssertClean("enum Status: U8 { Pending = 0, Active = 1 }");

    [Fact]
    public void AnUpcastIsAllowedWhereTheBaseIsExpected() =>
        AssertClean(
            """
            class Base { }
            class Derived: Base { }
            fn take(_ b: Base) { }
            fn probe(_ d: Derived) {
                take(d)
            }
            """
        );

    // ---- Composed constraints ----------------------------------------------

    [Fact]
    public void AnOperatorRequirementIsFoundThroughAComposition() =>
        AssertClean(
            """
            protocol Addable { operator + (a: Self, b: Self) -> Self }
            protocol Zero { static fn zero() -> Self }
            fn two<T: Addable & Zero>(_ a: T) -> T => a + T.zero()
            """
        );

    [Fact]
    public void ACompositionInAWhereClauseIsTwoConstraints() =>
        AssertClean(
            """
            protocol Addable { operator + (a: Self, b: Self) -> Self }
            protocol Zero { static fn zero() -> Self }
            fn two<T>(_ a: T) -> T where T: Addable & Zero => a + T.zero()
            """
        );

    [Fact]
    public void AnAssociatedTypesCompositionIsTwoConstraints() =>
        AssertClean(
            """
            protocol Addable { operator + (a: Self, b: Self) -> Self }
            protocol Zero { static fn zero() -> Self }
            protocol Holder {
                associatedtype Item: Addable & Zero
                fn item() -> Item
            }
            fn twice<H: Holder>(_ h: H) -> H.Item => h.item() + h.item()
            """
        );

    // ---- Initialiser requirements ------------------------------------------

    [Fact]
    public void AnInitialiserRequirementIsCalledThroughAParameter() =>
        Assert.Equal(
            "T",
            TypeOfLast(
                """
                protocol Makeable { init(seed: Int) }
                fn probe<T: Makeable>(_ witness: T) {
                    let v = T(seed: 4)
                }
                """
            )
        );

    [Fact]
    public void AnInitialiserRequirementChecksItsArguments() =>
        Assert.Contains(
            Errors(
                """
                protocol Makeable { init(seed: Int) }
                fn make<T: Makeable>() -> T => T(seed: "four")
                """
            ),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );

    [Fact]
    public void AParameterWithoutAnInitialiserRequirementIsNotCallable() =>
        Assert.Contains(
            Errors(
                """
                protocol Plain { fn f() -> Int }
                fn make<T: Plain>() -> T => T(seed: 4)
                """
            ),
            static e => e.Code == ErrorCode.ERR_NotCallable && e.Message.Contains("requires an initialiser")
        );

    [Fact]
    public void AProtocolCannotBeInitialised() =>
        Assert.Contains(
            Errors(
                """
                protocol Makeable { init(seed: Int) }
                fn make() => Makeable(seed: 4)
                """
            ),
            static e => e.Code == ErrorCode.ERR_CannotInstantiate
        );

    [Fact]
    public void AnInitialiserRequirementIsMetByTheMemberwiseInitialiser() =>
        AssertClean(
            """
            protocol Makeable { init(seed: Int) }
            struct Thing: Makeable { var seed: Int }
            """
        );

    [Fact]
    public void AMissingInitialiserRequirementIsReported() =>
        Assert.Contains(
            Errors(
                """
                protocol Makeable { init(seed: Int) }
                struct Thing: Makeable { var name: String }
                """
            ),
            static e => e.Code == ErrorCode.ERR_RequirementSignatureMismatch && e.Message.Contains("'init(seed: Int)'")
        );
}
