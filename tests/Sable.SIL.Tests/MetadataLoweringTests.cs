namespace Sable.SIL.Tests;

/// <summary>
///     Type metadata in SIL (docs/06 §6.2, #297): <c>metadata $T</c> and the
///     builtins over it, as lowering writes them, as text reads them back, and
///     as the interpreter runs them; and the switch that has every type's
///     metadata carry field descriptors only for a program that reads them.
/// </summary>
public sealed class MetadataLoweringTests {
    static SilModule Lower(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.Empty(diagnostics);
        Assert.Empty(SilVerifier.Verify(module));
        return module;
    }

    const string Source = """
                          struct Point {
                              var x: Int
                              var name: String
                          }

                          fn main() {
                              let m: Type = Point.self
                              print(m.size)
                              print(m.name)
                              let p = Point { x: 1, name: "a" }
                              print(m.copy(p).name)
                          }
                          """;

    [Fact]
    public void ATypesMetadataIsAnInstructionOfItsOwn() {
        var module = Lower(Source);
        var main = module.Find("M.main")!.Blocks.SelectMany(static b => b.Instructions).ToList();
        Assert.Contains(main, static i => i is MetadataInst { Described: StructType { Name: "M.Point" } });
        Assert.Contains(main, static i => i is BuiltinInst { Name: "metadata_size" });
        Assert.Contains(main, static i => i is BuiltinInst { Name: "value_witness_copy" });
    }

    [Fact]
    public void MetadataRoundTripsThroughTextAndRuns() {
        var text = SilPrinter.Print(Lower(Source));
        Assert.Contains("metadata $M.Point", text, StringComparison.Ordinal);
        var parsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(parsed));
        var interpreter = new SilInterpreter(parsed);
        interpreter.Run("M.main");
        Assert.Equal("16\nM.Point\na\n", interpreter.Output.ToString());
        Assert.Equal(0, interpreter.LiveObjects);
    }

    /// <summary>A program that never asks for fields leaves them out of every type's metadata but a class's, which the leak report names.</summary>
    [Fact]
    public void FieldDescriptorsAreCarriedOnlyWhenAProgramReadsThem() {
        Assert.False(Lower(Source).FieldDescriptors);
        Assert.True(Lower("""
                          struct Point {
                              var x: Int
                          }

                          fn main() {
                              print(Point.self.fieldCount)
                          }
                          """).FieldDescriptors);

        var point = new StructType("M.Point");
        point.Fields.Add(("x", SilType.Int));
        var described = TypeMetadata.Of(point);
        Assert.Empty(described.FieldsIn(new SilModule()));
        Assert.Single(described.FieldsIn(new SilModule { FieldDescriptors = true }));
        var node = new ClassType("M.Node");
        node.Fields.Add(("value", SilType.Int));
        Assert.Equal(16, Assert.Single(TypeMetadata.Of(node).FieldsIn(new SilModule())).Offset);
    }

    /// <summary>The layout metadata reports, by kind: TypeLayout's, which the code generator lays values out by.</summary>
    [Theory]
    [InlineData("Int", MetadataKind.Scalar, 8, 8, 8)]
    [InlineData("Bool", MetadataKind.Scalar, 1, 1, 1)]
    [InlineData("String", MetadataKind.String, 8, 8, 8)]
    [InlineData("(Int, Bool)", MetadataKind.Tuple, 16, 8, 16)]
    [InlineData("[Int]", MetadataKind.Array, 8, 8, 8)]
    [InlineData("@thick (Int) -> Int", MetadataKind.Function, 16, 8, 16)]
    [InlineData("Metadata", MetadataKind.Metatype, 8, 8, 8)]
    public void LayoutByKind(string type, MetadataKind kind, int size, int alignment, int stride) {
        var module = SilParser.Parse($"sil @f : $() -> () {{\nbb0:\n  %0 = metadata ${type}\n  return\n}}\n");
        var described = TypeMetadata.Of(((MetadataInst)module.Find("f")!.Blocks[0].Instructions[0]).Described);
        Assert.Equal((kind, size, alignment, stride), (described.Kind, described.Size, described.Alignment, described.Stride));
    }

    const string Tabled = """
                          protocol Named {
                              fn name() -> String
                          }

                          protocol Holder {
                              associatedtype Item: Named
                              fn count() -> Int
                          }

                          struct Tag: Named {
                              var text: String
                              fn name() -> String => text
                          }

                          struct Shelf: Holder {
                              alias Item = Tag
                              var n: Int
                              fn count() -> Int => n
                          }

                          fn main() {
                              let h: any Holder = Shelf { n: 2 }
                              if let named = h as? any Named {
                                  print(named.name())
                              } else {
                                  print(h.count())
                              }
                          }
                          """;

    /// <summary>
    ///     A conformance an existential needs has a witness table: its
    ///     requirements' witnesses in slot order, its associated types as the
    ///     conformance binds them and what each must conform to; a cast to a
    ///     protocol looks it up in the registry rather than trying each type.
    /// </summary>
    [Fact]
    public void AConformanceHasAWitnessTableWithItsAssociatedTypes() {
        var module = Lower(Tabled);
        var table = module.FindWitnessTable(module.FindType("M.Shelf")!, "M.Holder")!;
        Assert.Equal("count", Assert.Single(table.Entries).Requirement);
        Assert.Equal(("Item", "M.Tag"), (Assert.Single(table.AssociatedTypes).Name, table.AssociatedTypes[0].Type.ToString()));
        Assert.Equal((0, "M.Named"), Assert.Single(table.AssociatedConformances));
        Assert.NotNull(module.FindWitnessTable(module.FindType("M.Tag")!, "M.Named"));
        Assert.Contains(("M.Holder", table), module.ConformancesOf(module.FindType("M.Shelf")!));

        var cast = module.Functions.Single(static f => f.Name.StartsWith("$cast$to$any$M.Named", StringComparison.Ordinal));
        var builtins = cast.Blocks.SelectMany(static b => b.Instructions).OfType<BuiltinInst>().Select(static b => b.Name).ToList();
        Assert.Contains("box_metadata", builtins);
        Assert.Contains("witness_table:M.Named", builtins);
        Assert.DoesNotContain(builtins, static b => b.StartsWith("class_is:", StringComparison.Ordinal));
    }

    [Fact]
    public void WitnessTablesRoundTripThroughTextAndRun() {
        var text = SilPrinter.Print(Lower(Tabled));
        Assert.Contains("sil_witness_table $M.Shelf : \"M.Holder\" {", text, StringComparison.Ordinal);
        Assert.Contains("  associated \"Item\": $M.Tag\n  conforms 0: \"M.Named\"\n", text, StringComparison.Ordinal);
        var parsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(parsed));
        var interpreter = new SilInterpreter(parsed);
        interpreter.Run("M.main");
        Assert.Equal("2\n", interpreter.Output.ToString());
        Assert.Equal(0, interpreter.LiveObjects);
    }

    /// <summary>A witness table's witnesses are kept while its conformer can be made or boxed, and dropped with it.</summary>
    [Fact]
    public void AWitnessTablesWitnessesAreReachedWithItsConformer() {
        var module = Lower(Tabled);
        var reached = SilPassManager.Reachable(module)!;
        var shelf = module.FindWitnessTable(module.FindType("M.Shelf")!, "M.Holder")!;
        Assert.Contains(shelf.Entries[0].Function, reached);
        var tag = module.FindWitnessTable(module.FindType("M.Tag")!, "M.Named")!;
        Assert.DoesNotContain(tag.Entries[0].Function, reached);
    }

    const string Static = """
                          protocol Zero {
                              static fn zero() -> Self
                          }

                          struct Meters: Zero {
                              var value: Int
                              static fn zero() -> Meters { return Meters { value: 0 } }
                          }

                          fn make<T: Zero>(_ type: T.Type) -> T { return type.zero() }

                          fn isInt<T>(_ value: T) -> Bool { return T.self == Int.self }

                          fn main() {
                              print(make(Meters.self).value)
                              print(isInt(1))
                              print(isInt("one"))
                          }
                          """;

    /// <summary>
    ///     A metatype whose type is known — every one, in code specialised for
    ///     its types — costs nothing at run time (SYNTAX.md "Metatypes"): a
    ///     static member reached through a <c>T.Type</c> is called directly,
    ///     without reading the value, and <c>T.self == Int.self</c> is a
    ///     constant in each instance. The value is the type's metadata, a
    ///     constant that nothing reads.
    /// </summary>
    [Fact]
    public void StaticMetatypeCodeHasNoRuntimeCost() {
        var module = Lower(Static);
        var instructions = module.Functions.Where(static f => f.Name.Contains("make", StringComparison.Ordinal) || f.Name.Contains("isInt", StringComparison.Ordinal))
            .SelectMany(static f => f.Blocks).SelectMany(static b => b.Instructions).ToList();
        Assert.NotEmpty(instructions);
        Assert.DoesNotContain(instructions, static i => i is MetadataInst || i is BuiltinInst builtin && builtin.Name.Contains("metadata", StringComparison.Ordinal));

        var make = module.Functions.First(static f => f.Name.Contains("make", StringComparison.Ordinal) && f.Blocks[0].Arguments.Any(static a => a.Type == SilType.Metadata));
        var parameter = make.Blocks[0].Arguments.Single(static a => a.Type == SilType.Metadata);
        Assert.DoesNotContain(make.Blocks.SelectMany(static b => b.Instructions), i => i.Operands.Contains(parameter));

        var interpreter = new SilInterpreter(module);
        interpreter.Run("M.main");
        Assert.Equal("0\ntrue\nfalse\n", interpreter.Output.ToString());
    }

    const string Dynamic = """
                           protocol Named {
                               fn name() -> String
                           }

                           class Animal {}

                           class Dog: Animal, Named {
                               fn name() -> String => "dog"
                           }

                           struct Point {
                               var x: Int
                           }

                           fn main() {
                               let animal: Animal = Dog()
                               let named: any Named = Dog()
                               print(type(of: animal))
                               print(type(of: named))
                               print(type(of: Point { x: 1 }))
                               print(Dog.self.isSubtype(of: (any Named).self))
                               print(Animal.self.isInstance(animal))
                           }
                           """;

    /// <summary>
    ///     <c>type(of:)</c> reads an object's own class from its header
    ///     (<c>object_metadata</c>), and what an existential's box holds
    ///     (<c>box_metadata</c>); any other value's type is a constant.
    /// </summary>
    [Fact]
    public void TypeOfReadsAnObjectsClassAndAnExistentialsBox() {
        var module = Lower(Dynamic);
        var main = module.Find("M.main")!.Blocks.SelectMany(static b => b.Instructions).ToList();
        Assert.Contains(main, static i => i is BuiltinInst { Name: "object_metadata" });
        Assert.Contains(main, static i => i is BuiltinInst { Name: "box_metadata" });
        Assert.Contains(main, static i => i is MetadataInst { Described: StructType { Name: "M.Point" } });

        var text = SilPrinter.Print(module);
        Assert.Equal(text, SilPrinter.Print(SilParser.Parse(text)));
        var interpreter = new SilInterpreter(module);
        interpreter.Run("M.main");
        Assert.Equal("M.Dog\nM.Dog\nM.Point\ntrue\ntrue\n", interpreter.Output.ToString());
        Assert.Equal(0, interpreter.LiveObjects);
    }
}
