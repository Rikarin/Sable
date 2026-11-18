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

    [Fact]
    public void InfersFromANamedFunctionPassedAsAClosure() =>
        Assert.Equal(
            "String[>..]",
            TypeOfLast(
                """
                fn map<T, U>(_ xs: T[>..], _ f: (T) -> U) -> U[>..]
                fn show(_ x: Int) -> String => "\(x)"
                fn probe(_ xs: Int[>..]) {
                    let v = map(xs, show)
                }
                """
            )
        );

    [Fact]
    public void AClosureThatCannotThrowMakesATypedErrorNever() =>
        Assert.Equal(
            "Int",
            TypeOfLast(
                """
                fn apply<T, E>(_ f: () throws(E) -> T) throws(E) -> T
                fn probe() {
                    let v = apply { 7 }
                }
                """
            )
        );

    [Fact]
    public void AClosureThatThrowsGivesATypedErrorItsType() {
        var errors = Errors(
            """
            union IoError { Denied }
            fn apply<T, E>(_ f: () throws(E) -> T) throws(E) -> T
            fn risky() throws(IoError) -> Int
            fn bare() -> Int => apply { try risky() }
            fn caught() throws(IoError) -> Int => try apply { try risky() }
            """
        ).Where(static e => e.Code != ErrorCode.ERR_MissingBody).ToList();
        var error = Assert.Single(errors);
        Assert.Equal(ErrorCode.ERR_MustUseTry, error.Code);
    }

    [Fact]
    public void AFloatLiteralWinsOverAnIntegerOne() =>
        Assert.Equal(
            "F64",
            TypeOfLast(
                """
                fn pick<T>(_ a: T, _ b: T) -> T
                fn probe() {
                    let v = pick(1, 2.5)
                }
                """
            )
        );

    [Fact]
    public void AnotherArgumentWinsOverALiteral() =>
        Assert.Equal(
            "I8",
            TypeOfLast(
                """
                fn pick<T>(_ a: T, _ b: T) -> T
                fn probe(_ small: I8) {
                    let v = pick(1, small)
                }
                """
            )
        );

    [Fact]
    public void AnIntegerAndAFloatLiteralAddAsFloats() =>
        Assert.Equal(
            "F64",
            TypeOfLast(
                """
                fn probe() {
                    let v = 1 + 2.0
                }
                """
            )
        );

    [Fact]
    public void TheContextWinsOverALiteral() =>
        AssertClean(
            """
            fn wrap<T>(_ x: T) -> T[>..] => [x]
            fn pick<T>(_ a: T, _ b: T) -> T => a
            fn probe() -> Int {
                let r: I8[>..] = wrap(3)
                let n: Int? = pick(1, none)
                return r.count + (n ?? 0)
            }
            """
        );

    [Fact]
    public void TheContextIsIgnoredWhereItBreaksAConstraint() =>
        Assert.Equal(
            "Int",
            TypeOfLast(
                """
                protocol Numeric { }
                extension Int: Numeric { }
                fn twice<T: Numeric>(_ x: T) -> T
                fn probe() {
                    let n: Int? = twice(3)
                    let v = twice(3)
                }
                """
            )
        );

    [Fact]
    public void AStaticMemberOfABareGenericTypeInfersItsParameters() =>
        Assert.Equal(
            "Box<Int>",
            TypeOfLast(
                """
                struct Box<T> {
                    var v: T
                    static fn make(_ x: T) -> Box<T> => Box(v: x)
                }
                fn probe() {
                    let v = Box.make(4)
                }
                """
            )
        );

    [Fact]
    public void AnAssociatedTypeIsInferredFromAWitness() =>
        Assert.Equal(
            "Int",
            TypeOfLast(
                """
                protocol Bag {
                    associatedtype Item
                    fn get() -> Item
                }
                struct IntBag: Bag { fn get() -> Int => 3 }
                fn takesBag<B: Bag>(_ b: B) -> B.Item => b.get()
                fn probe() {
                    let v = takesBag(IntBag())
                }
                """
            )
        );

    [Fact]
    public void AnAssociatedTypeIsInferredFromAPropertyWitness() =>
        AssertClean(
            """
            protocol Holder {
                associatedtype Value
                var value: Value { get }
            }
            struct Named: Holder { var value: String }
            fn read<H: Holder>(_ h: H) -> H.Value => h.value
            fn probe() -> String => read(Named(value: "n"))
            """
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

    // ---- Generic protocols and variance -------------------------------------

    const string Animals = """
                           class Animal { }
                           class Dog: Animal { }
                           protocol Producer<out T> { fn next() -> T? }
                           protocol Consumer<in T> { fn accept(_ x: T) }
                           protocol Source<T> { fn next() -> T? }
                           struct DogProducer: Producer<Dog> { fn next() -> Dog? => none }
                           struct AnimalConsumer: Consumer<Animal> { fn accept(_ x: Animal) { } }
                           struct DogSource: Source<Dog> { fn next() -> Dog? => none }
                           """;

    [Fact]
    public void AConformanceToAGenericProtocolReadsItsArguments() =>
        AssertClean(
            """
            protocol Source<T> { fn next() -> T? }
            struct Ones: Source<Int> { fn next() -> Int? => 1 }
            fn first<S: Source<Int>>(_ s: S) -> Int => s.next() ?? 0
            fn probe() -> Int => first(Ones())
            """
        );

    [Fact]
    public void AWrongWitnessForAGenericProtocolIsReported() =>
        Assert.Contains(
            Errors(
                """
                protocol Source<T> { fn next() -> T? }
                struct Ones: Source<Int> { fn next() -> String? => none }
                """
            ),
            static e => e.Code == ErrorCode.ERR_RequirementSignatureMismatch
        );

    [Fact]
    public void AGenericConformerConformsWithItsOwnArguments() =>
        AssertClean(
            """
            protocol Source<T> { fn next() -> T? }
            protocol Counted<U>: Source<U> { fn count() -> Int }
            struct Rep<E>: Counted<E> {
                var value: E
                fn next() -> E? => value
                fn count() -> Int => 1
            }
            fn first<S: Source<Int>>(_ s: S) -> Int => s.next() ?? 0
            fn probe() -> Int => first(Rep(value: 5))
            fn firstOf(_ s: any Counted<String>) -> String => s.next() ?? ""
            """
        );

    [Fact]
    public void AGenericConformerWithOtherArgumentsDoesNotConform() =>
        Assert.Contains(
            Errors(
                """
                protocol Source<T> { fn next() -> T? }
                struct Rep<E>: Source<E> { var value: E; fn next() -> E? => value }
                fn first<S: Source<Int>>(_ s: S) -> Int => s.next() ?? 0
                fn probe() -> Int => first(Rep(value: "five"))
                """
            ),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );

    [Fact]
    public void AnOutParametersExistentialIsCovariant() =>
        AssertClean(
            $$"""
              {{Animals}}
              fn probe() {
                  let p: any Producer<Animal> = DogProducer()
              }
              """
        );

    [Fact]
    public void AnInParametersExistentialIsContravariant() =>
        AssertClean(
            $$"""
              {{Animals}}
              fn probe() {
                  let c: any Consumer<Dog> = AnimalConsumer()
              }
              """
        );

    [Theory]
    [InlineData("let p: any Producer<Dog> = AnimalProducer()")]
    [InlineData("let c: any Consumer<Animal> = DogConsumer()")]
    [InlineData("let s: any Source<Animal> = DogSource()")]
    public void VarianceGoesOnlyTheWayItIsDeclared(string declaration) =>
        Assert.Contains(
            Errors(
                $$"""
                  {{Animals}}
                  struct AnimalProducer: Producer<Animal> { fn next() -> Animal? => none }
                  struct DogConsumer: Consumer<Dog> { fn accept(_ x: Dog) { } }
                  fn probe() {
                      {{declaration}}
                  }
                  """
            ),
            static e => e.Code == ErrorCode.ERR_TypeMismatch
        );

    [Fact]
    public void AGenericConstraintIsInvariantEvenForAnOutParameter() =>
        Assert.Contains(
            Errors(
                $$"""
                  {{Animals}}
                  fn take<P: Producer<Animal>>(_ p: P) { }
                  fn probe() {
                      take(DogProducer())
                  }
                  """
            ),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );
}
