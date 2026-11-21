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
    ///     code cannot serve yet — in an array — is still specialised, for
    ///     each type it is used with, in a program that is otherwise unspecialised.
    /// </summary>
    [Fact]
    public void WhatUnspecialisedCodeCannotServeIsStillSpecialised() {
        var module = Lower("""
                           fn first<T>(_ items: T[]) -> T { return items[0] }

                           fn pass<T>(_ value: T) -> T { return value }

                           fn main() {
                               print(first([1, 2]))
                               print(first(["a"]))
                               print(pass(first([3])))
                           }
                           """);
        Assert.NotNull(module.Find("M.pass$generic"));
        Assert.Null(module.Find("M.first$generic"));
        Assert.Equal(2, module.Functions.Count(static f => f.Name.StartsWith("M.first$of$", StringComparison.Ordinal)));
        Assert.Equal("1\na\n3\n", Run(module));
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
}
