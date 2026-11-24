namespace Sable.SIL.Tests;

/// <summary>
///     The specialisation pass (#297 stage 5): generic code lowered once,
///     unspecialised, and copied by the optimiser for the types it is called
///     with where they are known — each copy with nothing opaque left in it,
///     running as the specialised lowering's code does.
/// </summary>
public sealed class SpecialiseTests {
    static SilModule Lower(string source, GenericsMode mode = GenericsMode.Unspecialised) {
        var compilation = Compilation.Create($"module M\n{source}");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        SilModule module;
        IReadOnlyList<Diagnostic> diagnostics;
        using (SilGen.UseGenerics(mode)) {
            (module, diagnostics) = SilGen.Lower(compilation);
        }

        Assert.Empty(diagnostics);
        Assert.Empty(SilVerifier.Verify(module));
        return module;
    }

    /// <summary>Lowered unspecialised, then the passes the level runs, which verify after each.</summary>
    static SilModule Optimised(string source, string level = "2") {
        var module = Lower(source);
        SilPassManager.RemoveUnreachable(module);
        SilPassManager.Run(module, SilPassManager.ForLevel(level));
        return module;
    }

    /// <summary>Lowered unspecialised, then specialised and nothing else.</summary>
    static SilModule Specialised(string source) {
        var module = Lower(source);
        SilPassManager.RemoveUnreachable(module);
        SilPassManager.Run(module, [new Specialise()]);
        return module;
    }

    static string Run(SilModule module) {
        var interpreter = new SilInterpreter(module);
        interpreter.Run("M.main");
        Assert.Equal(0, interpreter.LiveObjects);
        return interpreter.Output.ToString();
    }

    static List<SilInstruction> Instructions(SilFunction function) => [..function.Blocks.SelectMany(static b => b.Instructions)];

    /// <summary>What a copy must not have left: anything that asks a type's metadata or a witness table, or holds a value opaquely.</summary>
    static void AssertNothingGeneric(SilFunction function) {
        Assert.False(function.IsUnspecialised, function.Name);
        Assert.False(SilType.MentionsOpaque(function.Type), function.Type.ToString());
        foreach (var instruction in Instructions(function)) {
            Assert.False(instruction is MetadataInst, $"{function.Name}: {SilPrinter.Print(function)}");
            Assert.False(instruction is BuiltinInst { Name: var name }
                         && (name.StartsWith("opaque", StringComparison.Ordinal) || name.StartsWith("witness_", StringComparison.Ordinal)
                                                                                 || name.StartsWith("metadata_", StringComparison.Ordinal)
                                                                                 || name.StartsWith("value_witness_", StringComparison.Ordinal)),
                $"{function.Name}: {SilPrinter.Print(function)}");
            Assert.False(instruction.HasResult && SilType.MentionsOpaque(instruction.Type), $"{function.Name}: {instruction.Opcode} : {instruction.Type}");
        }

        foreach (var argument in function.Blocks.SelectMany(static b => b.Arguments)) {
            Assert.False(SilType.MentionsOpaque(argument.Type), argument.Type.ToString());
        }
    }

    const string Shapes = """
                          protocol Shape {
                              fn area() -> Int
                              fn name() -> String
                          }

                          struct Square: Shape {
                              var side: Int
                              fn area() -> Int => side * side
                              fn name() -> String => "square"
                          }

                          class Circle: Shape {
                              var r: Int
                              init(r: Int) { self.r = r }
                              fn area() -> Int => 3 * r * r
                              fn name() -> String => "circle"
                          }

                          fn describe<S: Shape>(_ shape: S) -> String {
                              return shape.name() + " \(shape.area())"
                          }

                          fn twice<S: Shape>(_ shape: S) -> String {
                              return describe(shape) + ", " + describe(shape)
                          }

                          fn pick<T>(_ a: T, _ b: T, _ first: Bool) -> T {
                              if first {
                                  return a
                              }
                              return b
                          }

                          fn largest<T: Comparable>(_ xs: T[]) -> T {
                              var best = xs[0]
                              for x in xs {
                                  if best < x {
                                      best = x
                                  }
                              }
                              return best
                          }

                          fn main() {
                              print(describe(Square { side: 4 }))
                              print(twice(Circle(r: 2)))
                              print(pick(1, 2, false))
                              print(pick("a", "b", true))
                              print(largest([3, 9, 2]))
                              print(largest(["pear", "apple", "quince"]))
                          }
                          """;

    /// <summary>
    ///     A call whose types are known calls a copy made for them: its
    ///     values the types they are, no metadata or tables passed, the
    ///     requirement it called through a table its witness, called
    ///     directly, and the generic function gone once nothing calls it.
    /// </summary>
    [Fact]
    public void ACallWithKnownTypesCallsACopyWithNothingGenericLeft() {
        var module = Specialised(Shapes);
        var describe = module.Find("M.describe$specialised$M.Square")!;
        Assert.Equal("(M.Square) -> String", describe.Type.ToString());
        AssertNothingGeneric(describe);
        var called = Instructions(describe).OfType<FunctionRefInst>().Select(static r => r.Function).ToList();
        Assert.Contains(called, static f => f.StartsWith("$generic$witness$M.Square$M.Shape$0$area$specialised", StringComparison.Ordinal));
        AssertNothingGeneric(module.Find(called.First(static f => f.Contains("$area$", StringComparison.Ordinal)))!);

        // A class's copy, and 'twice' calling the same copy of 'describe' it would.
        AssertNothingGeneric(module.Find("M.twice$specialised$M.Circle")!);
        Assert.Contains(Instructions(module.Find("M.twice$specialised$M.Circle")!), static i => i is FunctionRefInst { Function: "M.describe$specialised$M.Circle" });

        var main = Instructions(module.Find("M.main")!);
        Assert.DoesNotContain(main, static i => i is FunctionRefInst { Function: var f } && f.EndsWith("$generic", StringComparison.Ordinal));
        Assert.DoesNotContain(main, static i => i is BuiltinInst { Name: var name } && name.StartsWith("opaque", StringComparison.Ordinal));
        Assert.DoesNotContain(module.Functions, static f => f.Name.StartsWith("M.", StringComparison.Ordinal) && f.IsUnspecialised);
    }

    /// <summary>
    ///     A result the generic function writes where its caller says is
    ///     returned by the copy, and a loop calling a requirement calls the
    ///     conformance's own operator.
    /// </summary>
    [Fact]
    public void AResultWrittenWhereTheCallerSaysIsReturned() {
        var module = Specialised(Shapes);
        Assert.Equal("(Int, Int, Bool) -> Int", module.Find("M.pick$specialised$Int")!.Type.ToString());
        Assert.Equal("(String, String, Bool) -> String", module.Find("M.pick$specialised$String")!.Type.ToString());
        var largest = module.Find("M.largest$specialised$Int")!;
        Assert.Equal("([Int]) -> Int", largest.Type.ToString());
        AssertNothingGeneric(largest);
        AssertNothingGeneric(module.Find("M.largest$specialised$String")!);
    }

    /// <summary>The program the copies make prints what the specialised lowering's does, specialised alone and with every pass after.</summary>
    [Fact]
    public void SpecialisedCodeRunsAsTheSpecialisedLoweringDoes() {
        const string expected = "square 16\ncircle 12, circle 12\n2\na\n9\nquince\n";
        Assert.Equal(expected, Run(Lower(Shapes, GenericsMode.Specialised)));
        Assert.Equal(expected, Run(Lower(Shapes)));
        Assert.Equal(expected, Run(Specialised(Shapes)));
        foreach (var level in new[] { "2", "s" }) {
            var module = Optimised(Shapes, level);
            Assert.Equal(expected, Run(module));
            Assert.DoesNotContain(module.Functions, static f => f.Name.StartsWith("M.", StringComparison.Ordinal) && f.IsUnspecialised);
        }

        // After the inliner, main calls nothing generic and asks no metadata.
        var main = Instructions(Optimised(Shapes).Find("M.main")!);
        Assert.DoesNotContain(main, static i => i is MetadataInst or BuiltinInst { Name: "witness_table_known:M.Shape" });
    }

    const string Metatypes = """
                             protocol Zero {
                                 static fn zero() -> Self
                                 operator + (a: Self, b: Self) -> Self
                             }

                             struct Meters: Zero {
                                 var value: Int
                                 static fn zero() -> Meters => Meters { value: 0 }
                                 operator + (a: Meters, b: Meters) -> Meters => Meters { value: a.value + b.value }
                             }

                             fn make<T: Zero>(_ type: T.Type) -> T { return type.zero() }

                             fn sum<T: Zero>(_ a: T, _ b: T) -> T { return a + b + T.zero() }

                             fn isInt<T>(_ value: T) -> Bool { return T.self == Int.self }

                             fn kind<T>(_ value: T) -> Type { return type(of: value) }

                             fn show<T>(_ value: T) -> String { return "<\(value)>" }

                             fn main() {
                                 print(make(Meters.self).value)
                                 print(sum(Meters { value: 2 }, Meters { value: 3 }).value)
                                 print(isInt(1))
                                 print(isInt("one"))
                                 print(kind(Meters { value: 1 }))
                                 print(show(7) + show("seven") + show(Meters { value: 7 }))
                             }
                             """;

    /// <summary>
    ///     A static requirement, an operator of Self's and its result
    ///     returned; a comparison of metadata decided as the copy is made; a
    ///     value's type a constant for a struct; a value written out by its
    ///     type's describer, the describer's copy for the type, called directly.
    /// </summary>
    [Fact]
    public void RequirementsMetatypesAndComparisonsOfTypesAreSpecialised() {
        var module = Specialised(Metatypes);
        Assert.Equal("(Metadata) -> M.Meters", module.Find("M.make$specialised$M.Meters")!.Type.ToString());
        AssertNothingGeneric(module.Find("M.sum$specialised$M.Meters")!);
        var isInt = module.Find("M.isInt$specialised$Int")!;
        Assert.Contains(Instructions(isInt), static i => i is IntegerLiteralInst { Value: 1 } literal && literal.Type == SilType.Bool);
        Assert.Contains(Instructions(module.Find("M.isInt$specialised$String")!), static i => i is IntegerLiteralInst { Value: 0 } literal && literal.Type == SilType.Bool);
        Assert.Contains(Instructions(module.Find("M.kind$specialised$M.Meters")!), static i => i is MetadataInst { Described: StructType { Name: "M.Meters" } });
        var show = module.Find("M.show$specialised$M.Meters")!;
        AssertNothingGeneric(show);
        Assert.Contains(Instructions(show), static i => i is FunctionRefInst { Function: var f } && f.StartsWith("$describe$M.Meters$specialised", StringComparison.Ordinal));
        Assert.Null(module.Find("M.show$generic"));

        const string expected = "0\n5\ntrue\nfalse\nM.Meters\n<7><seven><Meters(value: 7)>\n";
        Assert.Equal(expected, Run(module));
        Assert.Equal(expected, Run(Optimised(Metatypes)));
    }

    const string Dictionaries = """
                                fn main() {
                                    var counts: [String: Int] = [:]
                                    for word in ["a", "b", "a", "c", "a", "b"] {
                                        counts[word] = (counts[word] ?? 0) + 1
                                    }
                                    print(counts["a"] ?? 0)
                                    print(counts["b"] ?? 0)
                                    print(counts["z"] ?? 0)
                                    var squares: [Int: Int] = [:]
                                    for i in 0..<100 {
                                        squares[i] = i * i
                                    }
                                    squares.remove(3)
                                    print(squares[9] ?? -1)
                                    print(squares[3] ?? -1)
                                    print(squares.count)
                                }
                                """;

    /// <summary>
    ///     The library's generic code — a dictionary's, compiled once — is
    ///     copied for each key and value type the program uses, its struct
    ///     the program's own instance of it, and nothing of it is left
    ///     generic.
    /// </summary>
    [Fact]
    public void TheLibrarysGenericCodeIsSpecialisedForTheProgramsTypes() {
        var module = Specialised(Dictionaries);
        Assert.Contains(module.Functions, static f => f.Name.StartsWith("Sable.Core.Dictionary.", StringComparison.Ordinal) && f.Name.EndsWith("$specialised$String$Int", StringComparison.Ordinal));
        Assert.Contains(module.Functions, static f => f.Name.StartsWith("Sable.Core.Dictionary.", StringComparison.Ordinal) && f.Name.EndsWith("$specialised$Int$Int", StringComparison.Ordinal));
        foreach (var copy in module.Functions.Where(static f => f.Name.Contains("$specialised$", StringComparison.Ordinal))) {
            AssertNothingGeneric(copy);
        }

        Assert.DoesNotContain(module.Functions, static f => f.Name.StartsWith("Sable.Core.Dictionary.", StringComparison.Ordinal) && f.IsUnspecialised);
        const string expected = "3\n2\n0\n81\n-1\n99\n";
        Assert.Equal(expected, Run(Lower(Dictionaries, GenericsMode.Specialised)));
        Assert.Equal(expected, Run(module));
        Assert.Equal(expected, Run(Optimised(Dictionaries)));
    }

    const string Lazy = """
                        fn main() {
                            var values: Int[>..] = []
                            for i in 0..<20 {
                                values.append(i)
                            }
                            let modulus = 3
                            print(values.lazy.filter { $0 % modulus != 0 }.map { $0 * 2 }.reduce(0) { $0 + $1 })
                        }
                        """;

    /// <summary>
    ///     A closure given to generic code is reabstracted to its convention
    ///     where it is made: one that returns a type parameter's value then
    ///     writes it where it is told. A copy is given the closure itself,
    ///     returning its result, and the reabstraction goes. A requirement
    ///     whose error type is Never boxes no error.
    /// </summary>
    [Fact]
    public void AClosureGivenToGenericCodeIsGivenToTheCopyAsItWasMade() {
        // As an optimised build lowers it: the lazy views keep the closures as they are made.
        using var optimised = SilGen.UseOptimised(true);
        var module = Specialised(Lazy);
        var reduce = module.Functions.Single(static f => f.Name.StartsWith("Sable.Core.Sequence.reduce", StringComparison.Ordinal) && f.Name.Contains("$specialised$", StringComparison.Ordinal));
        AssertNothingGeneric(reduce);
        Assert.Contains(reduce.Type.Parameters, static p => p.ToString() == "@thick (Int, Int) throws(any$Error) -> Int");
        var main = Instructions(module.Find("M.main")!);
        Assert.DoesNotContain(main, static i => i is FunctionRefInst { Function: var f } && f.StartsWith("$reabstract$", StringComparison.Ordinal));
        Assert.DoesNotContain(module.Functions, static f => f.Name.StartsWith("Sable.Core.Sequence.reduce", StringComparison.Ordinal) && f.IsUnspecialised);

        const string expected = "254\n";
        Assert.Equal(expected, Run(Lower(Lazy, GenericsMode.Specialised)));
        Assert.Equal(expected, Run(module));
        Assert.Equal(expected, Run(Optimised(Lazy)));
    }

    /// <summary>
    ///     A closure a lazy view keeps, lowered as an unoptimised build does:
    ///     in the one convention every instance keeps it in, which the copies
    ///     for the types convert from.
    /// </summary>
    [Fact]
    public void AClosureAGenericTypeKeepsIsConvertedInTheCopies() {
        var module = Specialised(Lazy);
        Assert.Equal("254\n", Run(module));
    }

    const string Predicates = """
                              fn holds<T>(_ value: T, _ test: (T) -> Bool) -> Bool { return test(value) }

                              fn main() {
                                  let limit = 3
                                  print(holds(5) { $0 > limit })
                                  print(holds("ab") { $0.count > limit })
                              }
                              """;

    /// <summary>
    ///     A closure reabstracted to take a type parameter's value where it
    ///     is: given to the copy with its reabstraction copied for the types,
    ///     which takes the value as it is.
    /// </summary>
    [Fact]
    public void AReabstractionIsCopiedForTheTypesOfTheCopyItIsGivenTo() {
        var module = Specialised(Predicates);
        var holds = module.Find("M.holds$specialised$Int")!;
        AssertNothingGeneric(holds);
        Assert.Equal("(Int, @thick (Int) -> Bool) -> Bool", holds.Type.ToString());
        var main = Instructions(module.Find("M.main")!);
        Assert.DoesNotContain(main, static i => i is FunctionRefInst { Type: var type } && SilType.MentionsOpaque(type));
        Assert.Contains(main, static i => i is FunctionRefInst { Function: var f } && f.StartsWith("$reabstract$", StringComparison.Ordinal) && f.Contains("$specialised$", StringComparison.Ordinal));

        const string expected = "true\nfalse\n";
        Assert.Equal(expected, Run(Lower(Predicates, GenericsMode.Specialised)));
        Assert.Equal(expected, Run(module));
        Assert.Equal(expected, Run(Optimised(Predicates)));
    }

    /// <summary>A module lowered specialised has nothing for the pass to do.</summary>
    [Fact]
    public void AModuleWithNothingGenericIsLeftAsItIs() {
        var module = Lower(Shapes, GenericsMode.Specialised);
        var before = SilPrinter.Print(module);
        SilPassManager.Run(module, [new Specialise()]);
        Assert.Equal(before, SilPrinter.Print(module));
    }
}
