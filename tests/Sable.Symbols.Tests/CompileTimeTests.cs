namespace Sable.Symbols.Tests;

/// <summary>
///     Compile-time evaluation, as the binder sees it (SYNTAX.md §21): what a
///     constant's type is when it is not written, and what code that runs in
///     the compiler may do (S3220). Computing the values is lowering's, and
///     tested there.
/// </summary>
public sealed class CompileTimeTests {
    static Compilation Compile(string source) => Compilation.Create($"module M\n{source}");

    static List<Diagnostic> ErrorsIn(string source) =>
        [..Compile(source).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static Diagnostic AssertReports(ErrorCode code, string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count > 0 && errors.TrueForAll(e => e.Code == code), string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        return errors[0];
    }

    static TypeSymbol TypeOf(string source, string constant) {
        var compilation = Compile(source);
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var field = compilation.SyntaxTrees.SelectMany(static t => t.Root.DescendantNodes().OfType<ConstDeclarationSyntax>())
            .Single(d => d.Identifier.Text == constant);
        return ((FieldSymbol)compilation.GetDeclaredSymbol(field)!).Type;
    }

    // ---- A constant's type, left out ----------------------------------------

    [Theory]
    [InlineData("const Pi = 3.14", "F64")]
    [InlineData("const Answer = 42", "Int")]
    [InlineData("const Name = \"sable\"", "String")]
    [InlineData("const Negative = -1", "Int")]
    public void ALiteralGivesItsType(string declaration, string type) =>
        Assert.Equal(type, TypeOf(declaration, declaration.Split(' ')[1]).ToDisplayString());

    [Fact]
    public void ACallGivesWhatItReturns() =>
        Assert.Equal("Int", TypeOf("const fn factorial(_ n: Int) -> Int => n <= 1 ? 1 : n * factorial(n - 1)\nconst Table = factorial(5)", "Table").ToDisplayString());

    [Fact]
    public void ArithmeticOnAnotherConstantGivesItsType() =>
        Assert.Equal("U8", TypeOf("const Width: U8 = 4\nconst Area = Width * 2", "Area").ToDisplayString());

    [Fact]
    public void AComparisonIsABool() =>
        Assert.Equal("Bool", TypeOf("const Wide = Platform.pointerWidth == 64", "Wide").ToDisplayString());

    [Fact]
    public void ARingOfConstantsHasNoTypeToFind() =>
        Assert.Contains(ErrorsIn("const A = B\nconst B = A"), static e => e.Code == ErrorCode.ERR_TypeNotFound);

    // ---- What may run in the compiler ---------------------------------------

    [Fact]
    public void AConstantCannotCallAnOrdinaryFunction() {
        var error = AssertReports(
            ErrorCode.ERR_NotConstant,
            """
            fn notConst() -> Int {
                print("side effect")
                return 3
            }
            const X: Int = notConst()
            """
        );
        Assert.Contains("'notConst' is not one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConstantCanCallAConstFn() =>
        AssertClean(
            """
            const fn square(_ n: Int) -> Int => n * n
            const Area: Int = square(4)
            """
        );

    [Fact]
    public void AConstFnCannotPrint() =>
        AssertReports(
            ErrorCode.ERR_NotConstant,
            """
            const fn noisy(_ n: Int) -> Int {
                print(n)
                return n
            }
            """
        );

    [Fact]
    public void AConstFnCannotUseTheRuntime() =>
        AssertReports(ErrorCode.ERR_NotConstant, "const fn now() -> Int => Runtime.wallClockNanoseconds()");

    [Fact]
    public void AConstFnCanUseTheCoresPureMembers() =>
        AssertClean(
            """
            const fn shout(_ s: String) -> String => s.trimmed() + "!"
            const fn count(_ s: String) -> Int => s.count
            """
        );

    [Fact]
    public void AConstantCannotReadAGlobalSetAtRunTime() =>
        AssertReports(
            ErrorCode.ERR_NotConstant,
            """
            static let Base = 10
            const Derived: Int = Base + 1
            """
        );

    [Fact]
    public void AConstantCanReadAnotherConstant() =>
        AssertClean(
            """
            const Base: Int = 10
            const Derived: Int = Base + 1
            """
        );

    [Fact]
    public void ACallToAMemberwiseInitialiserIsAllowed() =>
        AssertClean(
            """
            struct Point {
                x: Int
                y: Int
            }
            const Origin: Point = Point(x: 0, y: 0)
            """
        );

    [Fact]
    public void AComptimeBlockInABodyCannotSeeTheFunctionsLocals() {
        var error = AssertReports(
            ErrorCode.ERR_NotConstant,
            """
            fn f(_ n: Int) {
                comptime {
                    let m = n + 1
                }
            }
            """
        );
        Assert.Contains("cannot see 'n'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AComptimeBlockSeesWhatItDeclares() =>
        AssertClean(
            """
            const fn twice(_ n: Int) -> Int => n * 2
            fn f() {
                comptime {
                    let m = twice(3)
                    if m != 6 {
                        compileError("twice is broken")
                    }
                }
            }
            """
        );

    [Fact]
    public void ATopLevelComptimeBlockBinds() =>
        AssertClean(
            """
            comptime {
                if Platform.pointerWidth != 64 {
                    compileError("64-bit only")
                }
            }
            """
        );

    [Fact]
    public void ATopLevelComptimeBlockIsCheckedToo() =>
        AssertReports(ErrorCode.ERR_NotConstant, "comptime {\n    print(\"hello\")\n}");

    [Fact]
    public void CompileErrorTakesALiteral() =>
        AssertReports(
            ErrorCode.ERR_NotConstant,
            """
            comptime {
                let message = "no"
                compileError(message)
            }
            """
        );

    [Fact]
    public void ALiteralThatDoesNotFitItsTypeIsTheBindersToReport() =>
        AssertReports(ErrorCode.ERR_IntegerDoesNotFit, "const D: U8 = 300");

    [Fact]
    public void PlainArithmeticThatOverflowsIsReportedAsItIsBound() =>
        AssertReports(ErrorCode.ERR_ConstantOverflow, "const Big: Int = 9223372036854775807 + 1");

    [Fact]
    public void ALiteralThatFitsIsClean() => AssertClean("const D: U8 = 255\nconst E: I8 = -128");
}
