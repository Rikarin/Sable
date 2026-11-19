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

    /// <summary>A slice is read and written in place, as an array is, and viewed from one; its count never changes.</summary>
    [Fact]
    public void ASliceIsReadAndWrittenButNeverGrown() {
        Assert.Empty(
            SilVerifier.Verify(
                SilParser.Parse(
                    """
                    sil @f : $([Int], Int) -> Int {
                    bb0(%0 : $[Int], %1 : $Int):
                      %2 = builtin "slice_of"(%0) : $[Int:]
                      %3 = builtin "array_slice"(%2, %1, %1) : $[Int:]
                      %4 = builtin "array_count"(%3) : $Int
                      %5 = builtin "array_get"(%2, %4) : $Int
                      destroy_value %3
                      destroy_value %2
                      return %5
                    }
                    """
                )
            )
        );

        AssertRejected(
            """
            sil @f : $([Int:], Int) -> () {
            bb0(%0 : $[Int:], %1 : $Int):
              %2 = alloc_stack $[Int:]
              %3 = copy_value %0
              store %3 to [init] %2
              builtin "array_append"(%2, %1) : $()
              destroy_addr %2
              dealloc_stack %2
              return
            }
            """,
            "builtin \"array_append\" cannot take (*[Int:], Int)"
        );

        AssertRejected(
            """
            sil @f : $([Int:]) -> [Int:] {
            bb0(%0 : $[Int:]):
              %1 = builtin "slice_of"(%0) : $[Int:]
              return %1
            }
            """,
            "builtin \"slice_of\" cannot take ([Int:])"
        );
    }

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

    // ---- Borrows and the new reference kinds --------------------------------

    [Fact]
    public void StorageCannotBeWrittenWhileItIsBorrowed() =>
        AssertRejected(
            """
            sil @f : $(String) -> () {
            bb0(%0 : $String):
              %1 = alloc_stack $String
              %2 = copy_value %0
              store %2 to [assign] %1
              %3 = load_borrow %1
              %4 = copy_value %0
              store %4 to [assign] %1
              end_borrow %3
              destroy_addr %1
              dealloc_stack %1
              return
            }
            """,
            "'store' writes storage that %3 (load_borrow) borrows"
        );

    [Fact]
    public void ABorrowMustEndOnEveryPath() =>
        AssertRejected(
            """
            sil @f : $(*String) -> () {
            bb0(%0 : $*String):
              %1 = load_borrow %0
              return
            }
            """,
            "never consumed: %1 (load_borrow)"
        );

    [Fact]
    public void OwnedParametersWeakAndUnownedStorageRoundTrip() {
        const string text = """
                            sil_class M.Node { next: $@sil_weak Optional$M.Node, parent: $@sil_unowned M.Node }
                            sil_enum Optional$M.Node { Some($M.Node), None }

                            sil @M.Node.link : $(@owned M.Node, M.Node) -> () {
                            bb0(%0 : $M.Node, %1 : $M.Node):
                              %2 = ref_element_addr %0, #next
                              %3 = load_weak %2
                              store_weak %3 to %2
                              destroy_value %3
                              %4 = ref_element_addr %0, #parent
                              store_unowned %1 to %4
                              %5 = load_unowned %4
                              destroy_value %5
                              destroy_value %0
                              return
                            }

                            """;
        var module = SilParser.Parse(text);
        Assert.Empty(SilVerifier.Verify(module));
        Assert.Equal(SilPrinter.Print(module), SilPrinter.Print(SilParser.Parse(SilPrinter.Print(module))));
        Assert.Contains("(@owned M.Node, M.Node) -> ()", SilPrinter.Print(module), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOwnedParameterMustBeConsumed() =>
        AssertRejected(
            """
            sil @f : $(@owned String) -> () {
            bb0(%0 : $String):
              return
            }
            """,
            "never consumed: %0 (argument)"
        );

    // ---- Blind spots: unreachable blocks, accesses, objects, vtables --------

    [Fact]
    public void ABlockNothingReachesIsRejected() =>
        AssertRejected(
            """
            sil @f : $() -> () {
            bb0:
              return
            bb1:
              %0 = builtin "add_Int"(%0, %0) : $Int
              return
            }
            """,
            "bb1: cannot be reached from the entry block"
        );

    const string Node = """
                        sil_class M.Node { value: $Int, next: $Optional$M.Node }
                        sil_enum Optional$M.Node { Some($M.Node), None }

                        """;

    [Fact]
    public void AnAccessMustEndBeforeTheFunctionDoes() =>
        AssertRejected(
            Node + """
                   sil @f : $(M.Node) -> () {
                   bb0(%0 : $M.Node):
                     %1 = ref_element_addr %0, #value
                     builtin "access_begin_modify"(%1) : $()
                     return
                   }
                   """,
            "leaves the function with 1 access(es) still in progress"
        );

    [Fact]
    public void AnAccessEndNeedsAnAccessInProgress() =>
        AssertRejected(
            Node + """
                   sil @f : $(M.Node) -> () {
                   bb0(%0 : $M.Node):
                     %1 = ref_element_addr %0, #value
                     builtin "access_end"(%1) : $()
                     return
                   }
                   """,
            "access_end ends an access that is not in progress"
        );

    [Fact]
    public void PathsMustAgreeOnTheAccessesInProgress() =>
        AssertRejected(
            Node + """
                   sil @f : $(M.Node, Bool) -> () {
                   bb0(%0 : $M.Node, %1 : $Bool):
                     %2 = ref_element_addr %0, #value
                     builtin "access_begin_read"(%2) : $()
                     cond_br %1, bb1, bb2
                   bb1:
                     builtin "access_end"(%2) : $()
                     br bb2
                   bb2:
                     return
                   }
                   """,
            "is entered with different accesses in progress on different paths"
        );

    [Fact]
    public void ANewObjectsReferencesAreStoredBeforeItIsUsed() =>
        AssertRejected(
            Node + """
                   sil @make : $() -> M.Node {
                   bb0:
                     %0 = alloc_ref $M.Node
                     %1 = integer_literal $Int, 1
                     %2 = ref_element_addr %0, #value
                     store %1 to [init] %2
                     return %0
                   }
                   """,
            "'return' uses %0 (alloc_ref $M.Node) before its field #next is stored"
        );

    [Fact]
    public void AFieldIsNotReadBeforeItIsStored() =>
        AssertRejected(
            Node + """
                   sil @make : $() -> M.Node {
                   bb0:
                     %0 = alloc_ref $M.Node
                     %1 = ref_element_addr %0, #next
                     %2 = load [copy] %1
                     store %2 to [init] %1
                     return %0
                   }
                   """,
            "'load' uses %0 (alloc_ref $M.Node) before its field #next is stored"
        );

    // The bug this check exists for: an initialiser that forgot a field, its
    // object handed back zeroed.
    const string Initialiser = """
                               sil @M.Node.init$initialize : $(M.Node, Int, @owned Optional$M.Node) -> () {
                               bb0(%0 : $M.Node, %1 : $Int, %2 : $Optional$M.Node):
                                 %3 = ref_element_addr %0, #value
                                 store %1 to [assign] %3
                                 %4 = ref_element_addr %0, #next
                                 cond_br %5, bb1, bb2
                               bb1:
                                 store %2 to [assign] %4
                                 br bb3
                               bb2:
                                 STORE_OR_NOT
                                 br bb3
                               bb3:
                                 return
                               }

                               sil @M.Node.init : $(Int, @owned Optional$M.Node) -> M.Node {
                               bb0(%0 : $Int, %1 : $Optional$M.Node):
                                 %2 = alloc_ref $M.Node
                                 %3 = function_ref @M.Node.init$initialize : $(M.Node, Int, @owned Optional$M.Node) -> ()
                                 apply %3(%2, %0, %1) : $()
                                 return %2
                               }
                               """;

    [Fact]
    public void AnInitialiserThatStoresEveryFieldOnEveryPathVerifies() {
        var text = Node + Initialiser.Replace("cond_br %5", "%5 = builtin \"cmp_eq_Int\"(%1, %1) : $Bool\n  cond_br %5")
            .Replace("STORE_OR_NOT", "store %2 to [assign] %4");
        Assert.Empty(SilVerifier.Verify(SilParser.Parse(text)));
    }

    [Fact]
    public void AnInitialiserThatMissesAFieldOnOnePathIsCaughtWhereTheObjectIsUsed() =>
        AssertRejected(
            Node + Initialiser.Replace("cond_br %5", "%5 = builtin \"cmp_eq_Int\"(%1, %1) : $Bool\n  cond_br %5")
                .Replace("STORE_OR_NOT", "destroy_value %2"),
            "@M.Node.init bb0: 'return' uses %2 (alloc_ref $M.Node) before its field #next is stored"
        );

    [Fact]
    public void AVTableNamesFunctionsThatExist() {
        var problems = SilVerifier.Verify(
            SilParser.Parse(
                """
                sil_class M.Shape { } vtable { area: @M.Shape.area }

                sil @f : $() -> () {
                bb0:
                  return
                }
                """
            )
        );
        Assert.Contains(problems, p => p.Contains("vtable slot #area names @M.Shape.area, which the module does not define", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOverrideHasItsSlotsSignature() {
        var problems = SilVerifier.Verify(
            SilParser.Parse(
                """
                sil_class M.Shape { } vtable { area: @M.Shape.area }
                sil_class M.Circle : M.Shape { } vtable { area: @M.Circle.area }

                sil @M.Shape.area : $(M.Shape) -> F64 {
                bb0(%0 : $M.Shape):
                  %1 = float_literal $F64, 0.0
                  return %1
                }

                sil @M.Circle.area : $(M.Circle) -> Int {
                bb0(%0 : $M.Circle):
                  %1 = integer_literal $Int, 0
                  return %1
                }
                """
            )
        );
        Assert.Contains(problems, p => p.Contains("M.Circle: vtable slot #area names @M.Circle.area : (M.Circle) -> Int, which does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void AVTableKeepsItsSuperclasssSlots() {
        var problems = SilVerifier.Verify(
            SilParser.Parse(
                """
                sil_class M.Shape { } vtable { area: @M.Shape.area }
                sil_class M.Circle : M.Shape { }

                sil @M.Shape.area : $(M.Shape) -> F64 {
                bb0(%0 : $M.Shape):
                  %1 = float_literal $F64, 0.0
                  return %1
                }
                """
            )
        );
        Assert.Contains(problems, p => p.Contains("M.Circle: vtable slot 0 must be #area, as in M.Shape", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeinitTakesItsType() {
        var problems = SilVerifier.Verify(
            SilParser.Parse(
                """
                sil_class M.File { } deinit @M.File.deinit
                sil_class M.Other { } deinit @M.Missing

                sil @M.File.deinit : $(Int) -> () {
                bb0(%0 : $Int):
                  return
                }
                """
            )
        );
        Assert.Contains(problems, p => p.Contains("M.File: its deinit @M.File.deinit is (Int) -> (), not (M.File) -> ()", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("M.Other: its deinit @M.Missing is not a function the module defines", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The dominators, worked out on random control flow, are those of the
    ///     definition: a block dominates another when, without it, the other
    ///     cannot be reached from the entry. Unreachable blocks have none.
    /// </summary>
    [Fact]
    public void DominatorsAreThoseOfTheDefinition() {
        var random = new Random(7);
        for (var trial = 0; trial < 300; trial++) {
            var function = new SilFunction("f", new FunctionType([SilType.Bool], SilType.Unit));
            var count = random.Next(1, 25);
            var blocks = Enumerable.Range(0, count).Select(_ => function.AddBlock()).ToList();
            var condition = blocks[0].AddArgument(SilType.Bool);
            foreach (var block in blocks) {
                // Never back to the entry, which the verifier forbids.
                SilBlock Target() => blocks[random.Next(1, count)];
                block.Append<SilInstruction>(count == 1 || random.Next(5) == 0 ? new ReturnInst(null)
                    : random.Next(2) == 0 ? new BranchInst(Target())
                    : new CondBranchInst(condition, Target(), [], Target(), []));
            }

            var dominators = SilVerifier.Dominators(function);
            var reachable = Reached(blocks[0], null);
            Assert.Equal(reachable.Count, dominators.Count);
            foreach (var block in reachable) {
                var expected = blocks.Where(d => d == block || !Reached(blocks[0], d).Contains(block)).ToHashSet();
                Assert.True(expected.SetEquals(dominators[block]), $"trial {trial}: bb{blocks.IndexOf(block)}'s dominators");
            }
        }

        static HashSet<SilBlock> Reached(SilBlock entry, SilBlock? without) {
            var seen = new HashSet<SilBlock>();
            var pending = new Stack<SilBlock>([entry]);
            while (pending.Count > 0) {
                var block = pending.Pop();
                if (block != without && seen.Add(block)) {
                    foreach (var successor in block.Successors) {
                        pending.Push(successor);
                    }
                }
            }

            return seen;
        }
    }
}
