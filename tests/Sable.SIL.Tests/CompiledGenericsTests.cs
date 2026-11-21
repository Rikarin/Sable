namespace Sable.SIL.Tests;

/// <summary>
///     A library's generic code compiled once into its archive (#297 stage 7,
///     #182): the library's unit compiles every generic function of its files
///     that can be compiled once and records which; a unit lowered against it
///     calls those there, without binding or lowering their bodies, and an
///     optimised one copies in the inlinable ones from the library's
///     interface to specialise.
/// </summary>
public sealed class CompiledGenericsTests {
    const string Program = """
        module App

        fn main() {
            var numbers: Int[>..] = [3, 1, 2]
            numbers.append(4)
            let names: [String: Int] = ["a": 1]
            print("\(numbers.contains(2)) \(names["a"] ?? 0) \(numbers.map { $0 * 2 })")
        }
        """;

    static Compilation Compile() {
        var compilation = Compilation.Create([SyntaxTree.Parse(new SourceText(Program, "app.sb"))]);
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        return compilation;
    }

    /// <summary>The core's unit lowered as a library's archive is, and what it recorded of its generic code.</summary>
    static (SilModule Module, SilUnitInfo Info) LowerCore(Compilation compilation) {
        var (module, diagnostics) = SilGen.LowerUnit(compilation, new SilUnit(CoreLibrary.Contains) { CompilesGenerics = true }, SilProgram.Index(compilation));
        Assert.Empty(diagnostics);
        Assert.Empty(SilVerifier.Verify(module));
        return (module, module.Unit!);
    }

    static SilModule LowerProgram(Compilation compilation, params CompiledGenerics[] libraries) {
        var (module, diagnostics) = SilGen.LowerUnit(compilation, new SilUnit(t => !CoreLibrary.Contains(t)) { Libraries = libraries }, SilProgram.Index(compilation));
        Assert.Empty(diagnostics);
        Assert.Empty(SilVerifier.Verify(module));
        return module;
    }

    static IEnumerable<string> CoreGenerics(SilModule module) =>
        module.Functions.Select(static f => f.Name).Where(static n => n.StartsWith("Sable.Core.", StringComparison.Ordinal) && n.EndsWith("$generic", StringComparison.Ordinal));

    [Fact]
    public void ALibrarysUnitCompilesEveryGenericFunctionItCanOnceAndDefinesItForEveryUnit() {
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        var (module, info) = LowerCore(Compile());

        // Not only what its own code calls: the array's append, which the core never calls on its own.
        Assert.Contains(info.Generics.Keys, static n => n.Contains(".append", StringComparison.Ordinal));
        Assert.All(info.Generics.Keys, name => {
            Assert.NotNull(module.Find(name + "$generic"));
            Assert.Contains(name + "$generic", info.Owned);
        });
        Assert.Subset(info.Generics.Keys.ToHashSet(), info.Inlinable.ToHashSet());
        Assert.NotEmpty(info.Inlinable);
    }

    [Fact]
    public void AUnitLoweredAgainstTheLibraryCallsItsGenericCodeRatherThanLoweringItAgain() {
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        var compilation = Compile();
        var (_, info) = LowerCore(compilation);

        // Alone, the program lowers the core's generic functions it calls.
        Assert.NotEmpty(CoreGenerics(LowerProgram(compilation)));

        // Against the library, it declares them, to call in the archive.
        var against = LowerProgram(compilation, new CompiledGenerics(CoreLibrary.Contains, info.Generics, info.Inlinable));
        Assert.Empty(CoreGenerics(against));
        var called = against.Unit!.External.Keys.Where(static n => n.EndsWith("$generic", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(called);
        Assert.All(called, name => Assert.True(info.Generics.ContainsKey(name[..^"$generic".Length]), name));
    }

    [Fact]
    public void ASpecialisedBuildRecordsNoGenericCode() {
        using var _ = SilGen.UseGenerics(GenericsMode.Specialised);
        var (_, info) = LowerCore(Compile());
        Assert.Empty(info.Generics);
        Assert.Empty(info.Inlinable);
    }

    [Fact]
    public void AnOptimisedUnitCopiesInTheLibrarysInlinableCodeAndSpecialisesIt() {
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        var compilation = Compile();
        var (core, info) = LowerCore(compilation);
        var face = LibraryInterface.Of(compilation, CoreLibrary.ModuleName, CoreLibrary.Contains, "test-compiler", "settings", LibraryInterface.InlinableSil(core, info), info);

        // The interface carries the inlinable code alone, and the core's own functions are not in it.
        var carried = SilParser.Parse(face.SilText);
        Assert.All(info.Inlinable, name => Assert.NotNull(carried.Find(name + "$generic")));
        Assert.DoesNotContain(carried.Functions, f => info.Owned.Contains(f.Name) && !info.Inlinable.Contains(f.Name[..^"$generic".Length]));

        var program = LowerProgram(compilation, face.CompiledGenerics());
        var copied = face.LinkInlinable(program);
        Assert.True(copied > 0);
        Assert.Empty(SilVerifier.Verify(program));
        SilPassManager.Run(program, [new Specialise()]);
        Assert.Contains(program.Functions, static f => f.Name.Contains("$specialised$", StringComparison.Ordinal));
    }

    [Fact]
    public void TheInterfaceRecordsTheGenericCodeAndReadsItBack() {
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        var compilation = Compile();
        var (core, info) = LowerCore(compilation);
        var face = LibraryInterface.Of(compilation, CoreLibrary.ModuleName, CoreLibrary.Contains, "test-compiler", "settings", LibraryInterface.InlinableSil(core, info), info);
        var read = LibraryInterface.Read(face.Write(), "test-compiler");
        Assert.NotNull(read);
        Assert.Equal(info.Generics, read.Generics);
        Assert.Equal(info.Inlinable, read.Inlinable);
        Assert.Equal(face.Write(), read.Write());
    }
}
