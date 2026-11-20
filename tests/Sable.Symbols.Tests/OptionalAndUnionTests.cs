namespace Sable.Symbols.Tests;

/// <summary>
///     Optionals and structural unions as the type checker sees them:
///     promotion through a literal or a union (docs/03 §3.7), union
///     normalisation (docs/03 §3.1), chains through optional members.
/// </summary>
public sealed class OptionalAndUnionTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    // ---- Promotion (#145) ---------------------------------------------------

    [Fact]
    public void AnArrayLiteralPromotesToAnOptionalArray() =>
        AssertClean(
            """
            fn f() {
                let x: Int[>..]? = [9]
                let y: U8[]? = [1, 2]
            }
            """
        );

    [Fact]
    public void AUnionMemberPromotesToAnOptionalUnion() =>
        AssertClean(
            """
            fn take(_ u: (Int | String)?) {}
            fn f() {
                let u: (Int | String)? = "x"
                take(3)
                take("y")
            }
            """
        );

    [Fact]
    public void AnOptionalUnionIsShownWithItsParentheses() {
        var error = Assert.Single(ErrorsIn("fn f() { let u: (Int | String)? = 1.5 }"));
        Assert.Contains("'(Int | String)?'", error.Message);
    }

    [Fact]
    public void AnArrayOfAUnionIsShownWithItsParentheses() {
        var error = Assert.Single(ErrorsIn("fn f() { let u: (Int | String)[] = 1 }"));
        Assert.Contains("'(Int | String)[]'", error.Message);
    }

    // ---- Normalisation (#146) -----------------------------------------------

    static string ParameterType(string written) {
        var compilation = Compilation.Create($"module M\nprotocol P {{}}\nfn probe(_ v: {written}) {{ }}");
        var probe = compilation.GetModule("M")!.Members.OfType<FunctionSymbol>().Single(static f => f.Name == "probe");
        return probe.Parameters[0].Type.ToDisplayString();
    }

    [Theory]
    [InlineData("Int | Never", "Int")]
    [InlineData("Never | String | Int", "Int | String")]
    [InlineData("Int | Int?", "Int?")]
    [InlineData("Int | String?", "(Int | String)?")]
    [InlineData("Int | String | None", "(Int | String)?")]
    [InlineData("Int | None", "Int?")]
    [InlineData("(Int | String) | (String | Bool)", "Bool | Int | String")]
    public void AUnionIsNormalised(string written, string normalised) =>
        Assert.Equal(normalised, ParameterType(written));

    [Fact]
    public void AnOpenExistentialIsNotAUnionCase() {
        var error = Assert.Single(ErrorsIn("protocol P {}\nfn f(_ v: Int | any P) {}"));
        Assert.Equal(ErrorCode.ERR_OpenExistentialInUnion, error.Code);
    }

    [Fact]
    public void AnyErrorIsAUnionCase() => AssertClean("struct Oops {}\nfn f(_ v: Oops | any Error) {}");

    /// <summary>
    ///     Error is Sable.Core's, with requirements: 'any Error' reaches them,
    ///     and is an 'any Debug'; a conformer needs write none of them.
    /// </summary>
    [Fact]
    public void AnyErrorReachesErrorsRequirements() {
        var compilation = Compilation.Create("module M\nunion Boom: Error { Bang }");
        Assert.Equal(CoreLibrary.ModuleName, compilation.Core.Error.ContainingSymbol?.Name);
        Assert.Contains(compilation.Core.Error.GetMembers("source"), static m => m is FieldSymbol);

        AssertClean(
            """
            union Boom: Error { Bang }
            fn f(_ e: any Error) -> String {
                let d: any Debug = e
                let s: (any Error)? = e.source
                return e.description() + d.debugDescription() + Boom.Bang.description()
            }
            fn g() throws { throw Boom.Bang }
            """
        );
    }

    [Fact]
    public void ADeclaredNoneIsThatType() =>
        AssertClean(
            """
            struct None {}
            fn f(_ v: Int | None) -> Int {
                if v is None { return 0 }
                return 1
            }
            """
        );

    [Fact]
    public void ComparingAnOptionalUnionToNoneNarrowsIt() =>
        AssertClean(
            """
            fn f(_ x: Int | String | None) -> Int {
                if x != none {
                    let y: Int | String = x
                    return 1
                }
                return 0
            }
            fn g() -> Int { return f(none) + f(3) + f("s") }
            """
        );

    // ---- Recursive aliases (#146) -------------------------------------------

    [Fact]
    public void AUnionAliasMayMentionItselfInsideItsCases() =>
        AssertClean(
            """
            alias Json = Bool | F64 | String | Json[] | [String: Json]
            fn size(_ j: Json) -> Int {
                return match j {
                    case let b as Bool => 1
                    case let f as F64 => 1
                    case let s as String => s.count
                    case let xs as Json[] => xs.count
                    case let d as [String: Json] => d.count
                }
            }
            fn make() -> Json {
                let leaf: Json = "abc"
                let list: Json = [leaf, true, 1.5, "x"]
                return ["a": list, "b": false]
            }
            """
        );

    [Fact]
    public void ARecursiveUnionIsCheckedForExhaustiveness() {
        var error = Assert.Single(
            ErrorsIn(
                """
                alias Tree = Int | Tree[]
                fn f(_ t: Tree) -> Int {
                    return match t {
                        case let n as Int => n
                    }
                }
                """
            )
        );
        Assert.Equal(ErrorCode.ERR_MatchNotExhaustive, error.Code);
        Assert.Contains("Tree[]", error.Message);
    }

    [Theory]
    [InlineData("alias A = A | Int")]
    [InlineData("alias A = Int | A?")]
    [InlineData("alias A = A[]")]
    [InlineData("alias A = B\nalias B = A")]
    public void AnAliasThatHoldsItselfDirectlyIsCircular(string source) =>
        Assert.Contains(ErrorsIn(source), static e => e.Code == ErrorCode.ERR_CircularAlias);

    // ---- Narrowing after an early exit (#146) -------------------------------

    [Fact]
    public void AnIfThatLeavesNarrowsWhatFollows() =>
        AssertClean(
            """
            struct Vec3 { var x: Int }
            fn render(_ v: Int | String | Vec3) -> String {
                if v is Int { return "int" }
                let rest: String | Vec3 = v
                if v is String { return v }
                return "\(v.x)"
            }
            fn other(_ v: Int | String) -> String {
                if !(v is String) { return "" } else { }
                return v
            }
            """
        );

    [Fact]
    public void AnIfThatFallsThroughNarrowsNothing() {
        var error = Assert.Single(
            ErrorsIn(
                """
                fn f(_ v: Int | String) -> String {
                    if v is Int { }
                    return v
                }
                """
            )
        );
        Assert.Equal(ErrorCode.ERR_TypeMismatch, error.Code);
    }

    [Fact]
    public void ANarrowingEndsWithItsBlock() {
        var error = Assert.Single(
            ErrorsIn(
                """
                fn f(_ v: Int | String, _ c: Bool) -> String {
                    if c {
                        if v is Int { return "" }
                    }
                    return v
                }
                """
            )
        );
        Assert.Equal(ErrorCode.ERR_TypeMismatch, error.Code);
    }

    [Fact]
    public void AnAssignmentEndsTheNarrowing() {
        var error = Assert.Single(
            ErrorsIn(
                """
                fn f(_ start: Int | String) -> String {
                    var v = start
                    if v is Int { return "" }
                    v = 3
                    return v
                }
                """
            )
        );
        Assert.Equal(ErrorCode.ERR_TypeMismatch, error.Code);
    }

    // ---- Chains and T! (#152) -----------------------------------------------

    [Fact]
    public void AChainThroughAnOptionalMemberGivesOneOptional() =>
        AssertClean(
            """
            struct Address { var city: String }
            struct User { var address: Address? }
            fn city(_ user: User?) -> String {
                let reached: Address? = user?.address
                return user?.address?.city ?? "unknown"
            }
            """
        );

    [Fact]
    public void ATupleElementReachedThroughAnOptionalIsOptional() =>
        AssertClean(
            """
            fn first(_ pair: (String, Int)?) -> String {
                let count: Int? = pair?.1
                return pair?.0 ?? "none"
            }
            fn inner(_ pair: (Int?, Int)?) -> Int? {
                return pair?.0
            }
            """
        );

    [Fact]
    public void AStoreThroughAChainTakesTheFieldsType() {
        var error = Assert.Single(
            ErrorsIn(
                """
                class Node { var v: Int, var next: Node?, init() { v = 0; next = none } }
                fn f(_ a: Node) {
                    a.next?.v = 9
                    a.next?.v = "nine"
                }
                """
            )
        );
        Assert.Equal(ErrorCode.ERR_TypeMismatch, error.Code);
        Assert.Contains("expected 'Int'", error.Message);
    }

    [Fact]
    public void AStoreThroughAChainNeedsAMutableField() =>
        Assert.Contains(
            ErrorsIn(
                """
                class Node { let v: Int, var next: Node?, init() { v = 0; next = none } }
                fn f(_ a: Node) { a.next?.v = 9 }
                """
            ),
            static e => e.Code == ErrorCode.ERR_NotAssignable
        );

    [Fact]
    public void AnImplicitlyUnwrappedOptionalIsShownAsWritten() {
        var error = Assert.Single(ErrorsIn("fn f(_ x: Int!) { let s: String = x }"));
        Assert.Contains("'Int!'", error.Message);
    }

    [Fact]
    public void AnImplicitlyUnwrappedOptionalIsAnOptionalToo() =>
        AssertClean(
            """
            fn f(_ x: Int!) -> Int? {
                let y: Int? = x
                if let z = x { return z }
                return x ?? 0
            }
            """
        );
}
