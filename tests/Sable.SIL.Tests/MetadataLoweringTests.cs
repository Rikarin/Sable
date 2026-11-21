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
                              let m = TypeMetadata.of(Point.self)
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
                              print(TypeMetadata.of(Point.self).fieldCount)
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
}
