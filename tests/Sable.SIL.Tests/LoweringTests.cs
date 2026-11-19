namespace Sable.SIL.Tests;

/// <summary>
///     Real Sable programs, lowered to SIL, verified, printed and parsed back,
///     and run — so a test checks what the lowered code computes, not only
///     what it looks like.
/// </summary>
public sealed class LoweringTests {
    /// <summary>Lowers a module, insisting on no diagnostics and verifier-clean SIL that round-trips.</summary>
    static SilModule Lower(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));

        var problems = SilVerifier.Verify(module);
        Assert.True(problems.Count == 0, string.Join("\n", problems) + "\n\n" + SilPrinter.Print(module));

        // Printing, parsing and printing again must give the same text, and
        // what is parsed must verify too.
        var text = SilPrinter.Print(module);
        var reparsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(reparsed));
        Assert.Empty(SilVerifier.Verify(reparsed));

        // The tests run the promoted form: if promotion changed what a
        // program computes, they say so. It must verify and round-trip too.
        SilPassManager.Run(reparsed, SilPassManager.Default);
        var promoted = SilPrinter.Print(reparsed);
        Assert.Equal(promoted, SilPrinter.Print(SilParser.Parse(promoted)));

        return reparsed;
    }

    static object? Run(SilModule module, string function, params object[] arguments) =>
        new SilInterpreter(module).Run($"M.{function}", arguments);

    // ---- Arithmetic and calls ----------------------------------------------

    [Fact]
    public void AddsTwoNumbers() =>
        Assert.Equal(5L, Run(Lower("fn add(_ a: Int, _ b: Int) -> Int { return a + b }"), "add", 2L, 3L));

    [Fact]
    public void RecursionComputesFibonacci() {
        var module = Lower(
            """
            fn fib(_ n: Int) -> Int {
                if n < 2 { return n }
                return fib(n - 1) + fib(n - 2)
            }
            """
        );

        Assert.Equal(6765L, Run(module, "fib", 20L));
    }

    [Fact]
    public void ALoopComputesTheGreatestCommonDivisor() {
        var module = Lower(
            """
            fn gcd(_ a: Int, _ b: Int) -> Int {
                var x = a
                var y = b
                while y != 0 {
                    let t = x % y
                    x = y
                    y = t
                }
                return x
            }
            """
        );

        Assert.Equal(6L, Run(module, "gcd", 48L, 18L));
        Assert.Equal(1L, Run(module, "gcd", 17L, 5L));
    }

    /// <summary>
    ///     An enum without '==' of its own is compared case by case, in blocks
    ///     made as the condition is; in an 'else if', once promoted, the second
    ///     comparison switches on a block argument of a block printed after it.
    ///     The text must still read back (Lower round-trips it).
    /// </summary>
    [Fact]
    public void AnEnumComparedInAnElseIfReadsBackOncePromoted() {
        var module = Lower(
            """
            enum Side {
                Automatic
                Left
                Center
                Right
            }

            fn before(_ chosen: Side, _ numeric: Bool, _ missing: Int) -> Int {
                let side = chosen == .Automatic ? (numeric ? Side.Right : Side.Left) : chosen
                var count = 0
                if side == .Right {
                    count = missing
                } else if side == .Center {
                    count = missing / 2
                }
                return count
            }
            """
        );

        Assert.Equal(6L, Run(module, "before", new SilInterpreter.EnumValue(0, null), true, 6L));
    }

    [Fact]
    public void CompoundAssignmentAndRepeat() {
        var module = Lower(
            """
            fn factorial(_ n: Int) -> Int {
                var result = 1
                var i = 1
                repeat {
                    result *= i
                    i += 1
                } while i <= n
                return result
            }
            """
        );

        Assert.Equal(2_432_902_008_176_640_000L, Run(module, "factorial", 20L));
    }

    [Fact]
    public void CoalescingAssignmentStoresAnOptional() {
        // 'x ??= 5' computes 'x ?? 5', an Int, and stores it back as an Int?.
        var module = Lower(
            """
            fn fill(_ start: Int, _ empty: Bool) -> Int {
                var x: Int? = start
                if empty { x = none }
                x ??= 5
                return x ?? 0
            }
            """
        );

        Assert.Equal(5L, Run(module, "fill", 3L, true));
        Assert.Equal(3L, Run(module, "fill", 3L, false));
    }

    [Fact]
    public void IntegerOverflowTraps() {
        var module = Lower(
            """
            fn factorial(_ n: Int) -> Int {
                if n <= 1 { return 1 }
                return n * factorial(n - 1)
            }
            """
        );

        Assert.Equal("integer overflow", Assert.Throws<SilTrap>(() => Run(module, "factorial", 21L)).Message);
    }

    [Fact]
    public void DivisionByZeroTraps() {
        var module = Lower("fn divide(_ a: Int, _ b: Int) -> Int { return a / b }");
        Assert.Equal("division by zero", Assert.Throws<SilTrap>(() => Run(module, "divide", 1L, 0L)).Message);
    }

    [Fact]
    public void FloatingPointArithmetic() {
        var module = Lower(
            """
            fn mean(_ a: F64, _ b: F64) -> F64 {
                let two: F64 = 2
                return (a + b) / two
            }
            """
        );

        Assert.Equal(2.5, Run(module, "mean", 2.0, 3.0));
    }

    // ---- Control flow ------------------------------------------------------

    [Fact]
    public void ShortCircuitingSkipsTheRightSide() {
        // With n = 0 the right side would divide by zero; '&&' must not run it.
        var module = Lower(
            """
            fn safe(_ n: Int) -> Bool {
                return n != 0 && 10 / n > 1
            }
            """
        );

        Assert.Equal(false, Run(module, "safe", 0L));
        Assert.Equal(true, Run(module, "safe", 2L));
    }

    [Fact]
    public void OrAndNotAsValues() {
        var module = Lower("fn either(_ a: Bool, _ b: Bool) -> Bool { return !(a || b) || (a && b) }");
        Assert.Equal(true, Run(module, "either", false, false));
        Assert.Equal(false, Run(module, "either", true, false));
        Assert.Equal(true, Run(module, "either", true, true));
    }

    [Fact]
    public void GuardReturnsEarly() {
        var module = Lower(
            """
            fn sign(_ n: Int) -> Int {
                guard n != 0 else { return 0 }
                return n > 0 ? 1 : -1
            }
            """
        );

        Assert.Equal(0L, Run(module, "sign", 0L));
        Assert.Equal(1L, Run(module, "sign", 7L));
        Assert.Equal(-1L, Run(module, "sign", -7L));
    }

    [Fact]
    public void ElseIfChains() {
        var module = Lower(
            """
            fn grade(_ score: Int) -> Int {
                if score >= 90 {
                    return 4
                } else if score >= 80 {
                    return 3
                } else if score >= 70 {
                    return 2
                }
                return 0
            }
            """
        );

        Assert.Equal(4L, Run(module, "grade", 95L));
        Assert.Equal(3L, Run(module, "grade", 85L));
        Assert.Equal(0L, Run(module, "grade", 10L));
    }

    [Fact]
    public void ALabelledBreakLeavesTheOuterLoop() {
        // The first pair (i, j) with i * j == target, encoded as i * 100 + j.
        var module = Lower(
            """
            fn find(_ target: Int) -> Int {
                var found = 0
                var i = 1
                outer: while i < 10 {
                    var j = 1
                    while j < 10 {
                        if i * j == target {
                            found = i * 100 + j
                            break outer
                        }
                        j += 1
                    }
                    i += 1
                }
                return found
            }
            """
        );

        // (3, 8) comes first. Without the labelled break the outer loop would
        // go on and overwrite it with (4, 6), giving 406.
        Assert.Equal(308L, Run(module, "find", 24L));
        Assert.Equal(0L, Run(module, "find", 97L));
    }

    [Fact]
    public void ContinueSkipsToTheNextIteration() {
        var module = Lower(
            """
            fn sumOdd(_ n: Int) -> Int {
                var sum = 0
                var i = 0
                while i < n {
                    i += 1
                    if i % 2 == 0 { continue }
                    sum += i
                }
                return sum
            }
            """
        );

        Assert.Equal(25L, Run(module, "sumOdd", 10L));
    }

    [Fact]
    public void CollatzStepsAcrossFunctions() {
        var module = Lower(
            """
            fn next(_ n: Int) -> Int {
                return n % 2 == 0 ? n / 2 : 3 * n + 1
            }

            fn steps(_ start: Int) -> Int {
                var n = start
                var count = 0
                while n != 1 {
                    n = next(n)
                    count += 1
                }
                return count
            }
            """
        );

        Assert.Equal(111L, Run(module, "steps", 27L));
    }

    [Fact]
    public void AFunctionReturningNothing() {
        var module = Lower(
            """
            fn nothing(_ n: Int) {
                var x = n
                x += 1
            }
            fn caller() -> Int {
                nothing(1)
                return 7
            }
            """
        );

        Assert.Null(Run(module, "nothing", 1L));
        Assert.Equal(7L, Run(module, "caller"));
    }

    // Found by the compiler fuzzer (SABLE_FUZZ_SEED=429585833): binding what
    // a function returning nothing gives back stored a value defined nowhere.
    [Fact]
    public void WhatAFunctionReturningNothingGivesIsAValue() {
        var module = Lower(
            """
            fn nothing() {}
            fn caller() -> Int {
                let bound = nothing()
                let pair = (nothing(), 1)
                let list = [nothing(), nothing()]
                let chosen = if pair.1 > 0 { nothing() } else { nothing() }
                let later = { nothing() }
                let called = later()
                let kept = { bound }
                let again = kept()
                return pair.1 + list.count
            }
            """
        );

        Assert.Equal(3L, Run(module, "caller"));
    }

    [Fact]
    public void OverloadsGetDistinctNames() {
        var module = Lower(
            """
            fn twice(_ n: Int) -> Int { return n * 2 }
            fn twice(_ x: F64) -> F64 { return x + x }
            """
        );

        Assert.NotNull(module.Find("M.twice$Int"));
        Assert.NotNull(module.Find("M.twice$F64"));
    }

    // ---- The text -------------------------------------------------------------

    [Fact]
    public void TheTextOfASmallFunction() {
        var module = Lower("fn max(_ a: Int, _ b: Int) -> Int { return a > b ? a : b }");
        Assert.Equal(
            """
            sil @M.max : $(Int, Int) -> Int loc "file0.sb":2 {
            bb0(%0 : $Int var "a", %1 : $Int var "b"):
              %2 = builtin "cmp_gt_Int"(%0, %1) : $Bool loc 2:44
              cond_br %2, bb1, bb2 loc 2:37
            bb1:
              br bb3(%0) loc 2:37
            bb2:
              br bb3(%1) loc 2:37
            bb3(%3 : $Int):
              br bb4(%3) loc 2:37
            bb4(%4 : $Int):
              return %4
            }

            """.ReplaceLineEndings("\n"),
            SilPrinter.Print(module.Find("M.max")!)
        );
    }

    /// <summary>
    ///     '@packed' and '@align(n)' are flags of the SIL struct, which the
    ///     emitter lays out by; printed, parsed back, and of no matter to
    ///     what the interpreter computes.
    /// </summary>
    [Fact]
    public void PackedAndAlignedStructsCarryTheirLayout() {
        var module = Lower(
            """
            @packed struct Header { var tag: U8, var length: U32 }
            @packed @align(4) struct Word { var a: U8, var b: U16 }
            @align(16) struct Vec { var x: F32, var y: F32 }
            fn sum() -> Int {
                var h = Header(tag: 1, length: 2)
                h.length += 3
                let v = Vec(x: 1.0, y: 2.0)
                let w = Word(a: 4, b: 5)
                return Int(h.tag) + Int(h.length) + Int(v.y) + Int(w.b)
            }
            """
        );

        var text = SilPrinter.Print(module);
        Assert.Contains("sil_struct M.Header @packed { tag: $U8, length: $U32 }", text, StringComparison.Ordinal);
        Assert.Contains("sil_struct M.Word @packed @align(4) { a: $U8, b: $U16 }", text, StringComparison.Ordinal);
        Assert.Contains("sil_struct M.Vec @align(16) { x: $F32, y: $F32 }", text, StringComparison.Ordinal);
        Assert.Equal(13L, Run(module, "sum"));
    }

    // ---- What is not lowered yet --------------------------------------------

    [Fact]
    public void AnUnsupportedConstructIsReportedByName() {
        var compilation = Compilation.Create(
            """
            module M
            @noncopy struct Token { var id: Int }
            struct Point { var x: Token[] }
            fn origin(_ p: Point) -> Int { return 0 }
            """
        );

        var (module, diagnostics) = SilGen.Lower(compilation);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ErrorCode.ERR_NotLoweredYet, diagnostic.Code);
        Assert.Contains("Point", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(SilVerifier.Verify(module));
    }

    [Fact]
    public void AFunctionThatCannotBeLoweredStillVerifies() {
        var compilation = Compilation.Create(
            """
            module M
            fn label(_ n: Int) -> Int {
                let table = [(n, n): n]
                return n
            }
            """
        );

        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.Contains(diagnostics, static d => d.Code == ErrorCode.ERR_NotLoweredYet);
        Assert.Empty(SilVerifier.Verify(module));
    }
    [Fact]
    public void AnOperatorTakingAReferenceIsPassedTheOperandsAddress() {
        var module = Lower(
            """
            struct Money {
                var cents: Int
                operator + (a: Money, b: Money&) -> Money {
                    return Money { cents: a.cents + b.cents }
                }
                operator < (a: Money&, b: Money) -> Bool {
                    return a.cents < b.cents
                }
                prefix operator - (m: Money&) -> Money {
                    return Money { cents: 0 - m.cents }
                }
            }
            fn add(_ x: Int, _ y: Int) -> Int {
                let a = Money { cents: x }
                let b = Money { cents: y }
                return (a + b).cents
            }
            fn addParameters(_ a: Money, _ b: Money) -> Money {
                return a + b
            }
            fn negate(_ x: Int) -> Int {
                let m = Money { cents: x }
                return (-m).cents
            }
            fn greater(_ x: Int, _ y: Int) -> Bool {
                let a = Money { cents: x }
                let b = Money { cents: y }
                return a > b
            }
            fn atMost(_ x: Int, _ y: Int) -> Bool {
                let a = Money { cents: x }
                let b = Money { cents: y }
                return a <= b
            }
            """
        );

        Assert.Equal(7L, Run(module, "add", 3L, 4L));
        Assert.Equal(-5L, Run(module, "negate", 5L));
        Assert.Equal(true, Run(module, "greater", 4L, 3L));
        Assert.Equal(false, Run(module, "greater", 3L, 4L));
        Assert.Equal(true, Run(module, "atMost", 3L, 3L));
        Assert.Equal(false, Run(module, "atMost", 4L, 3L));
    }
}
