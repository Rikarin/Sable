namespace Sable.SIL.Tests;

/// <summary>
///     Each rule of <see cref="SilVerifier" />, broken on purpose by a small
///     hand-written function, and the parser's errors.
/// </summary>
public sealed class VerifierTests {
    static void AssertRejected(string sil, string complaint) {
        var problems = SilVerifier.Verify(SilParser.Parse(sil));
        Assert.True(
            problems.Any(p => p.Contains(complaint, StringComparison.Ordinal)),
            $"expected a complaint containing \"{complaint}\", got:\n{string.Join("\n", problems)}"
        );
    }

    [Fact]
    public void AValidFunctionVerifies() =>
        Assert.Empty(
            SilVerifier.Verify(
                SilParser.Parse(
                    """
                    sil @inc : $(Int) -> Int {
                    bb0(%0 : $Int):
                      %1 = integer_literal $Int, 1
                      %2 = builtin "add_Int"(%0, %1) : $Int
                      return %2
                    }
                    """
                )
            )
        );

    [Fact]
    public void ABlockMustEndInATerminator() =>
        AssertRejected(
            """
            sil @f : $() -> Int {
            bb0:
              %0 = integer_literal $Int, 1
            }
            """,
            "does not end in a terminator"
        );

    [Fact]
    public void ATerminatorMustBeLast() =>
        AssertRejected(
            """
            sil @f : $() -> () {
            bb0:
              return
              return
            }
            """,
            "before its end"
        );

    [Fact]
    public void TheEntryBlockTakesTheParameters() =>
        AssertRejected(
            """
            sil @f : $(Int) -> () {
            bb0:
              return
            }
            """,
            "the entry block's arguments must be the parameters"
        );

    [Fact]
    public void ADefinitionMustDominateItsUses() =>
        AssertRejected(
            """
            sil @f : $(Bool) -> Int {
            bb0(%0 : $Bool):
              cond_br %0, bb1, bb2
            bb1:
              %1 = integer_literal $Int, 1
              br bb2
            bb2:
              return %1
            }
            """,
            "does not dominate"
        );

    [Fact]
    public void ABuiltinChecksItsOperands() =>
        AssertRejected(
            """
            sil @f : $(Int, Bool) -> Int {
            bb0(%0 : $Int, %1 : $Bool):
              %2 = builtin "add_Int"(%0, %1) : $Int
              return %2
            }
            """,
            "takes (Int, Int)"
        );

    [Fact]
    public void AnUnknownBuiltinIsRejected() =>
        AssertRejected(
            """
            sil @f : $(Int) -> Int {
            bb0(%0 : $Int):
              %1 = builtin "frobnicate_Int"(%0) : $Int
              return %1
            }
            """,
            "no builtin named"
        );

    [Fact]
    public void AReturnMatchesTheSignature() =>
        AssertRejected(
            """
            sil @f : $() -> Int {
            bb0:
              %0 = integer_literal $Bool, 1
              return %0
            }
            """,
            "returns Bool from a function returning Int"
        );

    [Fact]
    public void ABranchPassesWhatTheTargetTakes() =>
        AssertRejected(
            """
            sil @f : $() -> Int {
            bb0:
              br bb1
            bb1(%0 : $Int):
              return %0
            }
            """,
            "branch passes () to a block taking (Int)"
        );

    [Fact]
    public void ACondBranchNeedsABool() =>
        AssertRejected(
            """
            sil @f : $(Int) -> () {
            bb0(%0 : $Int):
              cond_br %0, bb1, bb1
            bb1:
              return
            }
            """,
            "cond_br needs a Bool"
        );

    [Fact]
    public void AStoreMatchesItsAddress() =>
        AssertRejected(
            """
            sil @f : $() -> () {
            bb0:
              %0 = alloc_stack $Int
              %1 = integer_literal $Bool, 0
              store %1 to [init] %0
              dealloc_stack %0
              return
            }
            """,
            "store of Bool to *Int"
        );

    [Fact]
    public void StackAllocationsAreFreedInReverse() =>
        AssertRejected(
            """
            sil @f : $() -> () {
            bb0:
              %0 = alloc_stack $Int
              %1 = alloc_stack $Int
              dealloc_stack %0
              dealloc_stack %1
              return
            }
            """,
            "does not free the most recent"
        );

    [Fact]
    public void NothingIsLiveAtAReturn() =>
        AssertRejected(
            """
            sil @f : $() -> () {
            bb0:
              %0 = alloc_stack $Int
              return
            }
            """,
            "still live"
        );

    [Fact]
    public void PathsMustAgreeOnTheStack() =>
        AssertRejected(
            """
            sil @f : $(Bool) -> () {
            bb0(%0 : $Bool):
              cond_br %0, bb1, bb2
            bb1:
              %1 = alloc_stack $Int
              br bb2
            bb2:
              return
            }
            """,
            "different stack allocations"
        );

    [Fact]
    public void ACallMatchesTheCallee() =>
        AssertRejected(
            """
            sil @g : $(Int) -> Int {
            bb0(%0 : $Int):
              return %0
            }

            sil @f : $() -> Int {
            bb0:
              %0 = function_ref @g : $(Int) -> Int
              %1 = integer_literal $Bool, 1
              %2 = apply %0(%1) : $Int
              return %2
            }
            """,
            "apply passes (Bool)"
        );

    [Fact]
    public void AFunctionRefMustNameAFunction() =>
        AssertRejected(
            """
            sil @f : $() -> Int {
            bb0:
              %0 = function_ref @missing : $() -> Int
              %1 = apply %0() : $Int
              return %1
            }
            """,
            "does not define"
        );

    // ---- The parser ---------------------------------------------------------

    [Theory]
    [InlineData("sil @f : $() -> () {\nbb0:\n  frobnicate\n}", "unknown instruction")]
    [InlineData("sil @f : $() -> Int {\nbb0:\n  return %7\n}", "%7 is used but never defined")]
    [InlineData("sil @f : $() -> () {\nbb0:\n  br bb9\n}", "bb9 is used but never defined")]
    [InlineData("sil @f : $() -> () {\nbb0:\n  %0 = alloc_stack $Bogus\n}", "unknown type")]
    [InlineData("sil @f : $() -> () {\nbb0:\n  %0 = return\n}", "produces no value")]
    public void MalformedTextIsReportedWithItsLine(string sil, string complaint) {
        var error = Assert.Throws<SilParseException>(() => SilParser.Parse(sil));
        Assert.Contains(complaint, error.Message, StringComparison.Ordinal);
        Assert.True(error.Line >= 1);
    }

    [Fact]
    public void AUseMayComeBeforeItsDefinitionInTheText() {
        // bb1 is written first but runs second: %1 is defined in bb2, which
        // dominates it.
        var module = SilParser.Parse(
            """
            sil @f : $() -> Int {
            bb0:
              br bb2
            bb1:
              return %0
            bb2:
              %0 = integer_literal $Int, 5
              br bb1
            }
            """
        );

        Assert.Empty(SilVerifier.Verify(module));
        Assert.Equal(5L, new SilInterpreter(module).Run("f"));
    }

    // ---- The .sil corpus ----------------------------------------------------

    public static TheoryData<string> CorpusFiles {
        get {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(Path.Combine(RepositoryRoot, "tests", "sil"), "*.sil")
                         .Order(StringComparer.Ordinal)) {
                data.Add(Path.GetFileName(file));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void ACorpusFileParsesVerifiesAndRoundTrips(string fileName) {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot, "tests", "sil", fileName));
        var module = SilParser.Parse(text);
        Assert.Empty(SilVerifier.Verify(module));

        var printed = SilPrinter.Print(module);
        Assert.Equal(printed, SilPrinter.Print(SilParser.Parse(printed)));
    }

    static string RepositoryRoot {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("cannot find the repository root");
        }
    }
}
