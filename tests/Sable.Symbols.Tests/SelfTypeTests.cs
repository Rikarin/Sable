namespace Sable.Symbols.Tests;

/// <summary>
///     <c>Self</c> in a protocol: the conforming type, wherever the member is
///     reached from.
/// </summary>
public sealed class SelfTypeTests {
    const string Comparable = """
                              protocol Comparable {
                                  fn lessThan(_ other: Self) -> Bool
                              }
                              struct Version: Comparable {
                                  var major: Int
                                  fn lessThan(_ other: Version) -> Bool { return major < other.major }
                              }
                              """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{Comparable}\n{source}")
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

    [Fact]
    public void AConformerSatisfiesSelfWithItself() =>
        AssertClean(
            """
            fn probe(_ a: Version, _ b: Version) -> Bool { return a.lessThan(b) }
            """
        );

    [Fact]
    public void SelfIsTheTypeParameterThroughAConstraint() =>
        AssertClean(
            """
            fn probe<T: Comparable>(_ a: T, _ b: T) -> Bool { return a.lessThan(b) }
            """
        );

    [Fact]
    public void SelfIsTheOpaqueTypeThroughSome() =>
        AssertClean(
            """
            fn probe(_ a: some Comparable, _ b: Version) -> Bool { return a.lessThan(a) }
            """
        );

    [Fact]
    public void ADifferentTypeDoesNotSatisfySelf() {
        var errors = ErrorsIn(
            """
            struct Other { var n: Int }
            fn probe<T: Comparable>(_ a: T, _ b: Other) -> Bool { return a.lessThan(b) }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NoMatchingOverload);
    }

    [Fact]
    public void AConformerWhoseSignatureUsesTheProtocolDoesNotMatch() {
        var errors = ErrorsIn(
            """
            struct Wrong: Comparable {
                fn lessThan(_ other: Int) -> Bool { return true }
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_RequirementSignatureMismatch);
    }

    // ---- Generic unions ----------------------------------------------------

    const string Tree = """
                        union Tree<Value> {
                            Leaf
                            Node(value: Value, left: Tree<Value>, right: Tree<Value>)
                        }
                        """;

    [Fact]
    public void AGenericUnionTakesItsArgumentsFromThePayload() {
        var source = $"module M\n{Tree}\nfn probe() -> Tree<Int> "
            + "{ return Tree.Node(value: 1, left: Tree.Leaf, right: Tree.Leaf) }";

        var errors = Compilation.Create(source)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    [Fact]
    public void AMismatchedPayloadIsStillReported() {
        var source = $"module M\n{Tree}\nfn probe() -> Tree<Int> "
            + """{ return Tree.Node(value: "x", left: Tree.Leaf, right: Tree.Leaf) }""";

        Assert.Contains(
            Compilation.Create(source).GetAllDiagnostics(),
            static d => d.Code == ErrorCode.ERR_TypeMismatch
        );
    }

    [Fact]
    public void SelfInAGenericTypeIsTheTypeWithItsOwnParameters() {
        var source = """
                     module M
                     struct Nest<T> {
                         var inner: T
                         fn wrap() -> Nest<Nest<T>> { return Nest<Nest<T>> { inner: self } }
                         fn me() -> Self { return self }
                         fn same(_ other: Nest<T>) -> Self { return other }
                     }
                     """;
        var errors = Compilation.Create(source).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    /// <summary>
    ///     In a protocol's extension, <c>self</c> is a Self, which has what the
    ///     protocol requires: its operators, its static members through
    ///     <c>Self</c>, and its prefix operators — as a T constrained to the
    ///     protocol does.
    /// </summary>
    [Fact]
    public void InAProtocolsExtensionSelfHasWhatTheProtocolRequires() =>
        AssertClean(
            """
            protocol Scaled {
                static var unit: Self { get }
                operator * (lhs: Self, rhs: Self) -> Self
                prefix operator - (operand: Self) -> Self
            }
            extension Scaled {
                fn squared() -> Self { return self * self }
                fn negatedUnit() -> Self { return -Self.unit }
                static var twice: Self => Self.unit * Self.unit
            }
            fn probe<T: Scaled>(_ x: T) -> T { return -x.squared() * T.twice }
            """
        );

    /// <summary>
    ///     A property in a protocol's extension named as one of the
    ///     protocol's requirements is its default, not a second declaration.
    /// </summary>
    [Fact]
    public void APropertyInTheExtensionIsTheRequirementsDefault() =>
        AssertClean(
            """
            protocol Sized {
                var size: Int { get }
            }
            extension Sized {
                var size: Int => 0
            }
            struct Empty: Sized { }
            fn probe(_ e: Empty) -> Int { return e.size }
            """
        );

    /// <summary>
    ///     A static and an instance member of one name are told apart by how
    ///     they are reached: Int's static 'max', and the 'max(other:)' its
    ///     protocols give its values.
    /// </summary>
    [Fact]
    public void StaticAndInstanceMembersOfOneNameAreToldApartByTheirReceiver() {
        AssertClean("fn probe(_ x: Int) -> Int { return x.max(other: Int.max) }");
        var errors = ErrorsIn("fn probe() -> Int { return Int.abs() }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_MemberAccessMismatch);
    }
}
