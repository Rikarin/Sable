namespace Sable.SIL.Tests;

/// <summary>
///     Opening a <c>Type</c> in SIL (SYNTAX.md "Opening a type", #297 stage
///     6): the metadata the value holds and the registry's tables
///     (<c>witness_table_required</c>) given to the callee compiled once — in
///     a specialised build too — a result of the opened type written to a
///     buffer the metadata sizes (<c>opaque_alloc</c>) and boxed by its
///     type's boxer (<c>opaque_box</c>); as text reads it back and as the
///     interpreter runs it.
/// </summary>
public sealed class OpenedTypeLoweringTests {
    static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) TryLower(string source, GenericsMode mode) {
        using var _ = SilGen.UseGenerics(mode);
        var compilation = Compilation.Create($"module M\n{source}");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var (module, diagnostics) = SilGen.Lower(compilation);
        return (module, [..diagnostics]);
    }

    static SilModule Lower(string source, GenericsMode mode) {
        var (module, diagnostics) = TryLower(source, mode);
        Assert.Empty(diagnostics);
        Assert.Empty(SilVerifier.Verify(module));
        return module;
    }

    const string Source = """
                          protocol Zero {
                              static fn zero() -> Self
                              fn name() -> String
                          }

                          struct Meters: Zero {
                              var value: Int
                              static fn zero() -> Meters => Meters { value: 0 }
                              fn name() -> String => "\(value)m"
                          }

                          struct Label: Zero {
                              var text: String
                              static fn zero() -> Label => Label { text: "none" }
                              fn name() -> String => text
                          }

                          fn make<T: Zero>(_ type: T.Type) -> T => T.zero()

                          fn main() {
                              let types: Type[] = [Meters.self, Label.self]
                              for t in types {
                                  print(make(t).name())
                              }

                              print(make(Meters.self).name())
                          }
                          """;

    [Theory]
    [InlineData(GenericsMode.Unspecialised)]
    [InlineData(GenericsMode.Specialised)]
    public void AnOpenedCallFindsItsTablesInTheRegistryAndBoxesItsResult(GenericsMode mode) {
        var module = Lower(Source, mode);
        var main = module.Find("M.main")!.Blocks.SelectMany(static b => b.Instructions).ToList();
        Assert.Contains(main, static i => i is BuiltinInst { Name: "witness_table_required:M.Zero" });
        Assert.Contains(main, static i => i is BuiltinInst { Name: "opaque_alloc" });
        Assert.Contains(main, static i => i is BuiltinInst { Name: "opaque_box" });
        Assert.Contains(main, static i => i is FunctionRefInst { Function: "M.make$generic" });

        // Each conformer can be boxed, and its table has the entries generic code calls.
        Assert.Contains("M.Meters", module.Boxers.Keys);
        Assert.Contains("M.Label", module.Boxers.Keys);
        Assert.NotEmpty(module.FindWitnessTable(module.FindType("M.Label")!, "M.Zero")!.GenericEntries);
    }

    /// <summary>A specialised build calls the function compiled once only where a call opens a Type: where the type is known, the copy for it.</summary>
    [Fact]
    public void ASpecialisedBuildCompilesOnceOnlyWhatACallOpens() {
        var module = Lower(Source, GenericsMode.Specialised);
        var main = module.Find("M.main")!.Blocks.SelectMany(static b => b.Instructions).OfType<FunctionRefInst>().Select(static f => f.Function).ToList();
        Assert.Contains("M.make$generic", main);
        Assert.Contains(main, static n => n.StartsWith("M.make<", StringComparison.Ordinal) || n.StartsWith("M.make$", StringComparison.Ordinal) && n != "M.make$generic");
    }

    [Theory]
    [InlineData(GenericsMode.Unspecialised)]
    [InlineData(GenericsMode.Specialised)]
    public void AnOpenedCallRoundTripsThroughTextAndRuns(GenericsMode mode) {
        var text = SilPrinter.Print(Lower(Source, mode));
        Assert.Contains("sil_boxer $M.Meters = @$boxer$M.Meters", text, StringComparison.Ordinal);
        var parsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(parsed));
        var interpreter = new SilInterpreter(parsed);
        interpreter.Run("M.main");
        Assert.Equal("0m\nnone\n0m\n", interpreter.Output.ToString());
        Assert.Equal(0, interpreter.LiveObjects);
    }

    /// <summary>A callee that is copied for each type cannot be given one known only at run time: S3164 says why.</summary>
    [Theory]
    [InlineData(GenericsMode.Unspecialised)]
    [InlineData(GenericsMode.Specialised)]
    public void ACalleeThatCannotBeCompiledOnceIsAnError(GenericsMode mode) {
        var (_, diagnostics) = TryLower(
            """
            protocol Zero {
                static fn zero() -> Self
            }

            struct Meters: Zero {
                var value: Int
                static fn zero() -> Meters => Meters { value: 0 }
            }

            fn debug<T: Zero>(_ type: T.Type) -> String => "\(T.zero():?)"

            fn main() {
                let t: Type = Meters.self
                print(debug(t))
            }
            """,
            mode
        );
        var error = Assert.Single(diagnostics, static d => d.Code == ErrorCode.ERR_OpenedCalleeSpecialised);
        Assert.Contains("'debug' cannot be called with a type known only at run time", error.Message, StringComparison.Ordinal);
    }
}
