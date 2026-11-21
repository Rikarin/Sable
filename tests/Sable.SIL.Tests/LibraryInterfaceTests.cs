using System.Text;

namespace Sable.SIL.Tests;

/// <summary>
///     A library unit's interface (<c>.sbi</c>, #182): its bound symbols and
///     its SIL, written and read back the same, refused when another compiler
///     wrote it or it is not whole, and refused for a program whose library
///     files differ from the ones it was built from.
/// </summary>
public sealed class LibraryInterfaceTests {
    const string Compiler = "test-compiler";

    static readonly Lazy<(Compilation Compilation, LibraryInterface Interface)> Core = new(static () => {
        var compilation = Compilation.Create([]);
        var (built, errors) = LibraryInterface.Build(compilation, CoreLibrary.ModuleName, CoreLibrary.Contains, Compiler);
        Assert.True(built is not null, string.Join("\n", errors.Select(static e => $"{e.Diagnostic.Id}: {e.Diagnostic.Message}")));
        return (compilation, built!);
    });

    /// <summary>Every declaration of a module, one line each, with what binding reads of it: how two symbol graphs are compared.</summary>
    static List<string> Describe(IEnumerable<ModuleSymbol> modules) {
        var lines = new List<string>();
        foreach (var module in modules.OrderBy(static m => m.Name, StringComparer.Ordinal)) {
            foreach (var member in module.Members) {
                Visit(member, "");
            }
        }

        return lines;

        void Visit(Symbol symbol, string indent) {
            var attributes = string.Join(" ", symbol.Attributes.Select(static a => a.ToString()));
            var line = symbol switch {
                NamedTypeSymbol type =>
                    $"{type.TypeKind} {type.Name}<{string.Join(", ", type.TypeParameters.Select(static p => $"{p.Name}: {string.Join(" & ", p.Constraints.Select(static c => c.ToDisplayString()))}"))}>"
                    + $" : {string.Join(", ", type.BaseTypes.Select(static b => b.ToDisplayString()))}"
                    + $" assoc [{string.Join(", ", type.AssociatedTypes.Select(static a => a.Name))}] predicates [{string.Join(", ", type.ValuePredicates.Select(static p => p.Text))}]"
                    + $" extensions {type.Extensions.Length}",
                FunctionSymbol function =>
                    $"{function.FunctionKind} {function}{(function.ThrownType is { } thrown ? $" throws {thrown.ToDisplayString()}" : "")}"
                    + $" labels ({string.Join(", ", function.Parameters.Select(static p => p.Label ?? "_"))})"
                    + $"{(function.IsStatic ? " static" : "")}{(function.IsMutating ? " mutating" : "")}{(function.HasBody ? " body" : "")}{(function.IsSynthesized ? " synthesised" : "")}"
                    + $"{(function.IsNonisolated ? " nonisolated" : "")} in {function.ContainingSymbol?.Kind}",
                FieldSymbol field => $"field {field.Name}: {field.Type.ToDisplayString()}{(field.IsStatic ? " static" : "")}{(field.HasSetter ? " set" : "")}",
                AliasSymbol alias => $"alias {alias.Name} = {alias.Type.ToDisplayString()}",
                UnionCaseSymbol unionCase => $"case {unionCase.Name}({string.Join(", ", unionCase.Payload.Select(static p => p.Type.ToDisplayString()))})",
                EnumCaseSymbol enumCase => $"case {enumCase.Name} = {enumCase.RawValue}",
                _ => $"{symbol.Kind} {symbol.Name}",
            };
            lines.Add($"{indent}{line} {symbol.DeclaredAccessibility} {attributes}");
            if (symbol is NamedTypeSymbol named) {
                foreach (var member in named.Members) {
                    Visit(member, indent + "  ");
                }
            }
        }
    }

    [Fact]
    public void TheCoresSymbolsReadBackAsTheyWereWritten() {
        var (_, core) = Core.Value;
        var modules = core.ReadSymbols();
        Assert.Equal(CoreLibrary.ModuleName, Assert.Single(modules).Name);
        Assert.Equal(core.SymbolBytes, SymbolInterface.Write(modules));
    }

    [Fact]
    public void TheCoresSymbolsReadBackSayWhatTheBoundOnesSay() {
        var (compilation, core) = Core.Value;
        var expected = Describe([compilation.GetModule(CoreLibrary.ModuleName)!]);
        var actual = Describe(core.ReadSymbols());
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void SymbolsReadBackHaveNoSyntax() {
        var module = Assert.Single(Core.Value.Interface.ReadSymbols());
        var all = module.Members.Concat(module.Members.OfType<NamedTypeSymbol>().SelectMany(static t => t.Members)).ToList();
        Assert.All(all, static symbol => Assert.True(symbol.DeclaringSyntax.IsEmpty));
        Assert.Contains(all, static symbol => !symbol.IsSynthesized);
        Assert.Contains(all, static symbol => symbol is FunctionSymbol { HasBody: true });
    }

    [Fact]
    public void AProgramsSymbolsNamingTheCoresReadBackAgainstIt() {
        var compilation = Compilation.Create([SyntaxTree.Parse(new SourceText(
            "module App\n\npublic struct Box<T: Equatable> {\n    public var items: T[]\n    public fn first() -> T? => none\n}\n\nextension Int {\n    fn twice() -> Int => self * 2\n}\n",
            "app.sb"))]);
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var app = compilation.GetModule("App")!;
        var bytes = SymbolInterface.Write([app], compilation.Modules);

        // The core it names is the one read back from the core's interface.
        var core = Core.Value.Interface.ReadSymbols();
        var read = SymbolInterface.Read(bytes, core);
        Assert.Equal(bytes, SymbolInterface.Write(read, core));
        var box = Assert.IsType<NamedTypeSymbol>(Assert.Single(read).GetMembers("Box").Single());
        var first = box.GetMembers("first").OfType<FunctionSymbol>().Single();
        Assert.Equal("T?", first.ReturnType.ToDisplayString());
        var equatable = Assert.IsType<NamedTypeSymbol>(Assert.Single(box.TypeParameters).Constraints.Single());
        Assert.Same(core[0].GetType("Equatable"), equatable.OriginalDefinition);

        // The extension of the core's Int is the program's, and is added to it.
        var integer = core[0].GetType("Int")!;
        Assert.Contains(integer.Members, static m => m is FunctionSymbol { Name: "twice" });
    }

    [Fact]
    public void ReadingWithoutTheModulesItNamesSaysSo() {
        var compilation = Compilation.Create([SyntaxTree.Parse(new SourceText("module App\npublic fn name() -> String { \"a\" }\n", "app.sb"))]);
        var bytes = SymbolInterface.Write([compilation.GetModule("App")!]);
        var failure = Assert.Throws<InvalidDataException>(() => SymbolInterface.Read(bytes));
        Assert.Contains("Sable.Core", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileReadsBackWhole() {
        var core = Core.Value.Interface;
        var read = LibraryInterface.Read(core.Write(), Compiler);
        Assert.NotNull(read);
        Assert.Equal(core.Unit, read.Unit);
        Assert.Equal(core.Settings, read.Settings);
        Assert.Equal(core.Sources, read.Sources);
        Assert.Equal(core.Modules, read.Modules);
        Assert.Equal(core.Extensions, read.Extensions);
        Assert.Equal(core.SymbolBytes, read.SymbolBytes);
        Assert.Equal(core.SilText, read.SilText);
        Assert.Equal(core.Write(), read.Write());
    }

    [Fact]
    public void TheSilReadsBackAsItWasLowered() {
        var core = Core.Value.Interface;
        var module = core.ReadSil();
        Assert.Equal(core.SilText, SilPrinter.Print(module, keepBlockOrder: true));
        Assert.Empty(SilVerifier.Verify(module));
        Assert.NotEmpty(module.Functions);
    }

    [Fact]
    public void AnotherCompilersInterfaceIsNotRead() =>
        Assert.Null(LibraryInterface.Read(Core.Value.Interface.Write(), "another-compiler"));

    [Fact]
    public void AnotherVersionIsNotRead() {
        var bytes = Core.Value.Interface.Write();
        BitConverter.GetBytes(LibraryInterface.Version + 1).CopyTo(bytes, 4);
        Assert.Null(LibraryInterface.Read(bytes, Compiler));
    }

    [Fact]
    public void AFileCutShortOrChangedIsNotRead() {
        var bytes = Core.Value.Interface.Write();
        Assert.Null(LibraryInterface.Read(bytes.AsSpan(0, bytes.Length / 2), Compiler));
        Assert.Null(LibraryInterface.Read([], Compiler));
        var changed = (byte[])bytes.Clone();
        changed[bytes.Length / 2] ^= 0xFF;
        Assert.Null(LibraryInterface.Read(changed, Compiler));
    }

    /// <summary>A bounded run of what the fuzz target does: any bytes are refused or read, never thrown on.</summary>
    [Fact]
    public void DamagedSymbolsAreRefusedNotThrownOn() {
        var bytes = Core.Value.Interface.SymbolBytes;
        var random = new Random(182);
        for (var i = 0; i < 200; i++) {
            var damaged = (byte[])bytes.Clone();
            for (var j = 0; j < 1 + i % 4; j++) {
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            }

            var cut = i % 3 == 0 ? damaged.AsSpan(0, random.Next(damaged.Length)) : damaged;
            try {
                SymbolInterface.Read(cut);
            } catch (InvalidDataException) {
                // Refused, as it should be.
            }
        }
    }

    [Fact]
    public void AProgramWithOtherLibraryFilesIsRefused() {
        var core = Core.Value.Interface;
        Assert.Null(core.Mismatch(Compilation.Create([]), core.Settings));
        Assert.Contains("built for", core.Mismatch(Compilation.Create([]), "other-target")!, StringComparison.Ordinal);

        // A program bringing its own copy of a core file is compiled with that file instead.
        var own = SyntaxTree.Parse(new SourceText("module Sable.Core\n\npublic fn extra() -> Int { 1 }\n", "lib/Sable.Core/Bool.sb"));
        var why = core.Mismatch(Compilation.Create([own]), core.Settings);
        Assert.Contains("Bool.sb", why!, StringComparison.Ordinal);
    }

    [Fact]
    public void AProgramExtendingTheLibraryOtherwiseIsRefused() {
        var core = Core.Value.Interface;
        var program = Compilation.Create([SyntaxTree.Parse(new SourceText("module App\nextension Int {\n    fn twice() -> Int => self * 2\n}\n", "app.sb"))]);
        Assert.Contains("App: Sable.Core.Int.twice", core.Mismatch(program, core.Settings)!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdentityIsTheBuildsOwn() {
        var identity = LibraryInterface.IdentityOf(LibraryInterface.FrontEnd);
        Assert.Equal(64, identity.Length);
        Assert.Equal(identity, LibraryInterface.IdentityOf(LibraryInterface.FrontEnd.Reverse()));
        Assert.NotEqual(identity, LibraryInterface.IdentityOf([typeof(LibraryInterfaceTests).Assembly]));
        Assert.Equal(identity, Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(identity)));
    }
}
