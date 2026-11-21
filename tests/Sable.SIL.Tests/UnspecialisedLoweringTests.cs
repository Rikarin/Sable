namespace Sable.SIL.Tests;

/// <summary>
///     Unspecialised generic code (#297): a generic function compiled once,
///     taking its values by address, metadata and witness tables, as lowering writes it, as
///     SIL text reads it back, and as the interpreter runs it — with the same
///     answers as the specialised program.
/// </summary>
public sealed class UnspecialisedLoweringTests {
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

    static string Run(SilModule module) {
        var interpreter = new SilInterpreter(module);
        interpreter.Run("M.main");
        Assert.Equal(0, interpreter.LiveObjects);
        return interpreter.Output.ToString();
    }

    static List<SilInstruction> Instructions(SilFunction function) => [..function.Blocks.SelectMany(static b => b.Instructions)];

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

                          fn main() {
                              print(describe(Square { side: 4 }))
                              print(twice(Circle(r: 2)))
                          }
                          """;

    /// <summary>A generic function used with two types is lowered once, and nothing is specialised for either.</summary>
    [Fact]
    public void AGenericFunctionIsLoweredOnce() {
        var module = Lower(Shapes);
        Assert.Single(module.Functions, static f => f.Name.StartsWith("M.describe", StringComparison.Ordinal));
        Assert.NotNull(module.Find("M.describe$generic"));
        Assert.DoesNotContain(module.Functions, static f => f.Name.Contains("$of$", StringComparison.Ordinal) && f.Name.StartsWith("M.", StringComparison.Ordinal));

        var specialised = Lower(Shapes, GenericsMode.Specialised);
        Assert.Contains(specialised.Functions, static f => f.Name.StartsWith("M.describe$of$M.Square", StringComparison.Ordinal));
        Assert.Contains(specialised.Functions, static f => f.Name.StartsWith("M.describe$of$M.Circle", StringComparison.Ordinal));
        Assert.DoesNotContain(specialised.Functions, static f => f.Name.EndsWith("$generic", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The calling convention: a value of a type parameter's type is
    ///     opaque, borrowed where its caller holds it, and after the
    ///     function's own parameters come each type parameter's metadata and
    ///     its witness tables. A requirement is called through the table's
    ///     generic entry, given the value and Self's metadata; a call from
    ///     code that knows the type passes the address of a slot holding the
    ///     value — no allocation — the constant metadata and the table made
    ///     for the type, referred to as a constant.
    /// </summary>
    [Fact]
    public void TheGenericCallingConventionTakesValuesByAddressMetadataAndWitnessTables() {
        var module = Lower(Shapes);
        var describe = module.Find("M.describe$generic")!;
        Assert.Equal("(@opaque(S), Metadata, WitnessTable) -> String", describe.Type.ToString());
        var body = Instructions(describe);
        Assert.Contains(body, static i => i is BuiltinInst { Name: "witness_generic:0" });
        Assert.Contains(body, static i => i is BuiltinInst { Name: "witness_generic:1" });
        Assert.DoesNotContain(body, static i => i is FunctionRefInst { Function: var f } && f.StartsWith("M.Square", StringComparison.Ordinal));

        // 'twice' passes its own box, metadata and table on.
        var twice = Instructions(module.Find("M.twice$generic")!);
        var call = twice.OfType<ApplyInst>().First(static a => a.Callee is FunctionRefInst { Function: "M.describe$generic" });
        Assert.All(call.Arguments, static a => Assert.IsType<SilArgument>(a));

        var main = Instructions(module.Find("M.main")!);
        Assert.DoesNotContain(main, static i => i is AllocRefInst);
        Assert.Contains(main, static i => i is BuiltinInst { Name: "opaque_cast", Operands: [AllocStackInst { ElementType: StructType { Name: "M.Square" } }] });
        Assert.Contains(main, static i => i is MetadataInst { Described: StructType { Name: "M.Square" } });
        Assert.Contains(main, static i => i is BuiltinInst { Name: "witness_table_known:M.Shape" });

        // Each conformance's table has the protocol's requirements in the generic convention, in order.
        var table = module.FindWitnessTable(module.FindType("M.Square")!, "M.Shape")!;
        Assert.Equal(["area", "name"], table.GenericEntries.Select(static e => e.Requirement));
        Assert.Equal("(@opaque(Self), Metadata) -> Int", table.GenericEntries[0].Type!.ToString());
        Assert.NotNull(module.Find(table.GenericEntries[1].Function!));
    }

    [Fact]
    public void UnspecialisedCodeRoundTripsThroughTextAndRunsAsSpecialisedCodeDoes() {
        var module = Lower(Shapes);
        var text = SilPrinter.Print(module);
        Assert.Contains("generic \"area\": @", text, StringComparison.Ordinal);
        var parsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(parsed));
        Assert.Empty(SilVerifier.Verify(parsed));
        Assert.Equal("square 16\ncircle 12, circle 12\n", Run(parsed));
        Assert.Equal(Run(Lower(Shapes, GenericsMode.Specialised)), Run(module));
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
    ///     A static requirement through a <c>T.Type</c> takes the metatype's
    ///     value as Self's metadata; a result of <c>Self</c> is written where
    ///     the caller says; <c>T.self == Int.self</c> is asked of the metadata
    ///     at run time; <c>type(of:)</c> asks the metadata, or an object; a
    ///     value is written out by its type's describer.
    /// </summary>
    [Fact]
    public void StaticRequirementsMetatypesAndDescriptionsAreAskedAtRunTime() {
        var module = Lower(Metatypes);
        var make = module.Find("M.make$generic")!;
        Assert.Equal("(Metadata, *@opaque(T), Metadata, WitnessTable) -> ()", make.Type.ToString());
        var zero = Instructions(make).OfType<ApplyInst>().Single();
        Assert.Same(make.Entry.Arguments[2], zero.Arguments.Last());

        Assert.NotNull(module.Find("M.sum$generic"));
        Assert.Contains(Instructions(module.Find("M.isInt$generic")!), static i => i is BuiltinInst { Name: "metadata_equal" });
        Assert.Contains(Instructions(module.Find("M.kind$generic")!), static i => i is BuiltinInst { Name: "opaque_type" });
        Assert.Contains(Instructions(module.Find("M.show$generic")!), static i => i is BuiltinInst { Name: "generic_describe" });
        Assert.Contains("M.Meters", module.Describers.Keys);

        var table = module.FindWitnessTable(module.FindType("M.Meters")!, "M.Zero")!;
        Assert.Equal("(*@opaque(Self), Metadata) -> ()", table.GenericEntries[0].Type!.ToString());
        Assert.Equal("(@opaque(Self), @opaque(Self), *@opaque(Self), Metadata) -> ()", table.GenericEntries[1].Type!.ToString());

        const string expected = "0\n5\ntrue\nfalse\nM.Meters\n<7><seven><Meters(value: 7)>\n";
        Assert.Equal(expected, Run(module));
        Assert.Equal(expected, Run(Lower(Metatypes, GenericsMode.Specialised)));
        Assert.Equal(expected, Run(SilParser.Parse(SilPrinter.Print(module))));
    }

    /// <summary>
    ///     A function whose type parameter is used in a way unspecialised
    ///     code cannot serve yet — a closure over one returned — is still
    ///     specialised, for each type it is used with, in a program that is
    ///     otherwise unspecialised.
    /// </summary>
    [Fact]
    public void WhatUnspecialisedCodeCannotServeIsStillSpecialised() {
        var module = Lower("""
                           fn first<T>(_ value: T) -> () -> T { return { value } }

                           fn pass<T>(_ value: T) -> T { return value }

                           fn main() {
                               print(first(1)())
                               print(first("a")())
                               print(pass(first(3)()))
                           }
                           """);
        Assert.NotNull(module.Find("M.pass$generic"));
        Assert.Null(module.Find("M.first$generic"));
        Assert.Equal(2, module.Functions.Count(static f => f.Name.StartsWith("M.first$of$", StringComparison.Ordinal) && !f.Name.Contains("$closure", StringComparison.Ordinal)));
        Assert.Equal("1\na\n3\n", Run(module));
    }

    /// <summary>
    ///     A closure over a type parameter's values is called in the generic
    ///     convention: its value of the type parameter's borrowed where the
    ///     caller holds it, its result written where the caller says. One
    ///     made where the types are known is given through a thunk that
    ///     converts; one made in unspecialised code is compiled with it, and
    ///     finds its metadata, its tables and what it captures in its context.
    /// </summary>
    const string Closures = """
                            fn apply<T, U>(_ value: T, _ change: (T) -> U) -> U { return change(value) }

                            fn same<T: Equatable>(_ items: T[], _ wanted: T) -> Bool[>..] {
                                var result: Bool[>..] = []
                                for item in items {
                                    result.append(apply(item, { $0 == wanted }))
                                }
                                return result
                            }

                            fn main() {
                                print(apply(1, { x in x * 2 }))
                                print(apply("a", { x in x + "b" }))
                                print(same(["a", "b", "a"], "a"))
                            }
                            """;

    [Fact]
    public void ClosuresOverATypeParametersValuesTakeTheGenericConvention() {
        var module = Lower(Closures);
        Assert.Equal("(@opaque(T), @thick (@opaque(T), *@opaque(U)) -> (), *@opaque(U), Metadata, Metadata) -> ()", module.Find("M.apply$generic")!.Type.ToString());
        Assert.Contains(module.Functions, static f => f.Name.StartsWith("$reabstract$", StringComparison.Ordinal));
        Assert.Contains(module.Functions, static f => f.Name.StartsWith("M.same$generic$closure", StringComparison.Ordinal) && f.IsUnspecialised);
        const string expected = "2\nab\n[true, false, true]\n";
        Assert.Equal(expected, Run(module));
        Assert.Equal(expected, Run(Lower(Closures, GenericsMode.Specialised)));
        Assert.Equal(expected, Run(SilParser.Parse(SilPrinter.Print(module))));
    }

    /// <summary>
    ///     A requirement that changes its value is given the address of the
    ///     generic code's own copy, which it changes where it is: the
    ///     caller's value is left as it was.
    /// </summary>
    [Fact]
    public void ARequirementThatChangesItsValueChangesItWhereItIs() {
        var module = Lower("""
                           protocol Counter {
                               mutating fn bump()
                               fn count() -> Int
                           }

                           struct Clicks: Counter {
                               var n: Int
                               mutating fn bump() { n = n + 1 }
                               fn count() -> Int => n
                           }

                           fn bumped<C: Counter>(_ counter: C) -> C {
                               var copy = counter
                               copy.bump()
                               copy.bump()
                               return copy
                           }

                           fn main() {
                               let start = Clicks { n: 1 }
                               let after = bumped(start)
                               print(start.count())
                               print(after.count())
                           }
                           """);
        Assert.NotNull(module.Find("M.bumped$generic"));
        var table = module.FindWitnessTable(module.FindType("M.Clicks")!, "M.Counter")!;
        Assert.Equal("(*@opaque(Self), Metadata) -> ()", table.GenericEntries[0].Type!.ToString());
        Assert.Contains(Instructions(module.Find(table.GenericEntries[0].Function!)!), static i => i is BuiltinInst { Name: "opaque_cast" });
        Assert.Equal("1\n3\n", Run(module));
    }

    /// <summary>Each requirement has a witness of its own, though two operators' names are written alike in SIL.</summary>
    [Fact]
    public void OperatorsOfOneProtocolHaveWitnessesOfTheirOwn() {
        var module = Lower("""
                           fn sum<T: AdditiveArithmetic>(_ a: T, _ b: T) -> T { return a + b }

                           fn difference<T: AdditiveArithmetic>(_ a: T, _ b: T) -> T { return a - b }

                           fn main() {
                               print(sum(10, 3))
                               print(difference(10, 3))
                               print(difference(1.0, 0.25))
                           }
                           """);
        Assert.NotNull(module.Find("M.difference$generic"));
        var table = module.FindWitnessTable(SilType.Int, "Sable.Core.AdditiveArithmetic")!;
        var witnesses = table.GenericEntries.Select(static e => e.Function).OfType<string>().ToList();
        Assert.Equal(witnesses.Count, witnesses.Distinct().Count());
        Assert.Equal("13\n7\n0.75\n", Run(module));
    }

    /// <summary>
    ///     An array of a type parameter's values is an array of opaque values,
    ///     passed as any array is; an array of one passed as a type argument —
    ///     polymorphic recursion — has its metadata made at run time.
    /// </summary>
    [Fact]
    public void AnArrayOfATypeParametersValuesIsABufferOfThem() {
        var module = Lower("""
                           fn twice<T>(_ value: T) -> T[>..] { return [value, value] }

                           fn nest<T>(_ value: T, _ depth: Int) -> Int {
                               if depth == 0 {
                                   return 0
                               }
                               return 1 + nest([value], depth - 1)
                           }

                           fn main() {
                               print(twice("a").count)
                               print(nest(1, 3))
                           }
                           """);
        Assert.Equal("(@opaque(T), Metadata) -> [@opaque(T)]", module.Find("M.twice$generic")!.Type.ToString());
        Assert.Contains(Instructions(module.Find("M.nest$generic")!), static i => i is BuiltinInst { Name: "metadata_array" });
        Assert.Equal("2\n3\n", Run(module));
        Assert.Equal("2\n3\n", Run(SilParser.Parse(SilPrinter.Print(module))));
    }

    /// <summary>
    ///     A function that only hands its parameters to a requirement stays
    ///     specialised, its copies the witness called directly; an enum's
    ///     generic '==' compares cases, as code that knows the enum does; and
    ///     a trivial result written again where a loop's call left the last
    ///     one is no store over a live value.
    /// </summary>
    [Fact]
    public void ForwardersStaySpecialisedAndAnEnumsEqualityIsItsCases() {
        var module = Lower("""
                           enum Suit: Equatable { Hearts, Spades }

                           protocol Doubling {
                               static fn twice(_ x: Self) -> Self
                           }

                           struct Count: Doubling {
                               var n: Int
                               static fn twice(_ x: Count) -> Count => Count { n: x.n * 2 }
                           }

                           fn twice<T: Doubling>(_ x: T) -> T => T.twice(x)

                           fn same<T: Equatable>(_ a: T, _ b: T) -> Bool { return a == b }

                           fn pass<T>(_ value: T) -> T { return value }

                           fn main() {
                               print(twice(Count { n: 2 }).n)
                               print(same(Suit.Hearts, Suit.Hearts))
                               print(same(Suit.Hearts, Suit.Spades))
                               var total = 0
                               for i in 0..<3 {
                                   total = total + pass(i)
                               }
                               print(total)
                           }
                           """);
        Assert.Null(module.Find("M.twice$generic"));
        Assert.NotNull(module.Find("M.same$generic"));
        Assert.Equal("4\ntrue\nfalse\n3\n", Run(module));
    }

    /// <summary>
    ///     An optional or a tuple of a type parameter's values is held as the
    ///     value itself is, by address, and returned through one; as a type
    ///     argument its metadata is made at run time from its parts'.
    /// </summary>
    [Fact]
    public void AnOptionalOrATupleOfATypeParametersValuesIsHeldByAddress() {
        var module = Lower("""
                           fn firstOf<T>(_ items: T[]) -> T? {
                               if items.count == 0 {
                                   return none
                               }
                               return items[0]
                           }

                           fn swapped<T, U>(_ pair: (T, U)) -> (U, T) => (pair.1, pair.0)

                           fn named<T>(_ value: T) -> String => "\(type(of: value))"

                           fn wrappedName<T>(_ value: T) -> String => named(firstOf([value]))

                           fn main() {
                               print(firstOf([3]) ?? 0)
                               print(swapped((1, "a")).0)
                               print(wrappedName("s"))
                           }
                           """);
        Assert.Equal("([@opaque(T)], *Optional$$opaque$T$, Metadata) -> ()", module.Find("M.firstOf$generic")!.Type.ToString());
        Assert.Equal("((@opaque(T), @opaque(U)), *(@opaque(U), @opaque(T)), Metadata, Metadata) -> ()", module.Find("M.swapped$generic")!.Type.ToString());
        Assert.Contains(Instructions(module.Find("M.wrappedName$generic")!), static i => i is MetadataInst { Described: EnumType { Name: "Optional$$opaque$T$" } });
        Assert.Equal("3\na\nString?\n", Run(module));
        Assert.Equal("3\na\nString?\n", Run(SilParser.Parse(SilPrinter.Print(module))));
    }

    const string Iteration = """
                             enum Stop: Error {
                                 Early
                             }

                             struct Countdown: Sequence {
                                 let from: Int
                                 fn makeIterator() -> CountdownIterator => CountdownIterator { left: from }
                             }

                             struct CountdownIterator: Iterator {
                                 var left: Int
                                 mutating fn next() throws(Stop) -> Int? {
                                     if left == 0 {
                                         throw Stop.Early
                                     }
                                     left -= 1
                                     return left
                                 }
                             }

                             fn firstOf<S: Sequence>(_ s: S) throws(S.Failure) -> S.Element? {
                                 var it = s.makeIterator()
                                 return try it.next()
                             }

                             fn joined<S: Sequence>(_ s: S) -> String where S.Failure == Never {
                                 var text = ""
                                 for x in s {
                                     text += "\(x);"
                                 }
                                 return text
                             }

                             fn main() {
                                 print(try! firstOf(Countdown(from: 2)) ?? -1)
                                 print(joined(1...3))
                                 do {
                                     let _ = try firstOf(Countdown(from: 0))
                                 } catch {
                                     print("stopped")
                                 }
                             }
                             """;

    /// <summary>
    ///     An associated type read through a type parameter, <c>S.Element</c>,
    ///     is opaque too: its metadata is found where the function starts, from
    ///     the table of its base's conformance the function is given, or from
    ///     the registry (<c>associated(...)</c>, which SIL text keeps); an
    ///     error of one, <c>throws(S.Failure)</c>, is written where the caller
    ///     says, and the address is thrown.
    /// </summary>
    [Fact]
    public void AssociatedTypesAreFoundFromTheTablesAndTheirErrorsWrittenWhereTheCallerSays() {
        var module = Lower(Iteration);
        var firstOf = module.Find("M.firstOf$generic")!;
        Assert.Equal("(@opaque(S), *Optional$$opaque$S.Element$, *@opaque(S.Failure), Metadata, WitnessTable) throws(*@opaque(S.Failure)) -> ()", firstOf.Type.ToString());
        Assert.Contains(firstOf.AssociatedMetadata, static a => a is { Name: "S.Iter", Base: "S", Protocol: "Sable.Core.Sequence", Table: "$witnesses.S.Sequence" });
        Assert.Contains(Instructions(firstOf), static i => i is BuiltinInst { Name: "opaque_metadata:S.Iter" });
        Assert.NotNull(module.Find("M.joined$generic"));

        var table = module.FindWitnessTable(module.FindType("M.CountdownIterator")!, "Sable.Core.Iterator")!;
        Assert.Equal("(*@opaque(Self), *Optional$$opaque$Self.Element$, *@opaque(Self.Failure), Metadata) throws(*@opaque(Self.Failure)) -> ()", table.GenericEntries[0].Type!.ToString());

        const string expected = "1\n1;2;3;\nstopped\n";
        Assert.Equal(expected, Run(module));
        Assert.Equal(expected, Run(Lower(Iteration, GenericsMode.Specialised)));
        var text = SilPrinter.Print(module);
        Assert.Contains("associated(\"S.Iter\", \"S\", \"Sable.Core.Sequence\", 2, \"$witnesses.S.Sequence\")", text, StringComparison.Ordinal);
        var parsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(parsed));
        Assert.Equal(expected, Run(parsed));
    }
}
