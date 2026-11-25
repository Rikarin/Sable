using System.Collections.Concurrent;
using Sable.Driver;
using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A program compiled against the standard library's declarations as its
///     interface has them (<see cref="LibrarySymbols" />, #182) — none of the
///     library's files declared, and those parsed that something asks for —
///     says what it does with them declared from source: the same diagnostics,
///     the same SIL.
/// </summary>
public sealed class LibrarySymbolsTests {
    static readonly ConcurrentDictionary<string, Lazy<LibrarySymbols>> Made = new(StringComparer.Ordinal);

    /// <summary>The symbols of the library a program imports, written and read back as a build would keep them.</summary>
    static LibrarySymbols SymbolsFor(SyntaxTree program) {
        var library = Library.For(program);
        return Made.GetOrAdd(string.Join(" ", library.Select(static t => t.Text.FileName)), _ => new(() => {
            var made = LibrarySymbols.Of(library, program.Options, out var why);
            Assert.True(made is not null, $"the library's declarations have no symbols: it has {why}");
            var read = LibrarySymbols.Read(made.Write(), made.Trees);
            Assert.NotNull(read);
            return read;
        })).Value;
    }

    static Compilation Against(SyntaxTree program) => Compilation.Create([program, ..Library.For(program)], library: SymbolsFor(program));

    static Compilation FromSource(SyntaxTree program) => Compilation.Create([program, ..Library.For(program)]);

    static SyntaxTree Parse(string source) => SyntaxTree.Parse(new SourceText(source, "test.sb"));

    /// <summary>What compiling says and makes: the errors and the program's other diagnostics, and its unit's SIL, external functions and layouts.</summary>
    static List<string> Compiled(Compilation compilation, SyntaxTree program) {
        var said = new List<string>();
        foreach (var (diagnostic, tree) in compilation.GetAllDiagnosticsByFile(t => ReferenceEquals(t, program))) {
            // The library's own warnings, which only its declarations give, are no program's.
            if (diagnostic.Severity != DiagnosticSeverity.Error && !ReferenceEquals(tree, program)) {
                continue;
            }

            var related = string.Concat(diagnostic.Related.Select(p => $" ({p.Message} at {p.Span} of {(p.Anchor is { } anchor ? compilation.TreeOf(anchor)?.Text.FileName : null)})"));
            said.Add($"{tree?.Text.FileName} {diagnostic.Severity} {diagnostic.Id} {diagnostic.Span}: {diagnostic.Message}{related}");
        }

        if (said.Any(static s => s.Contains(" Error ", StringComparison.Ordinal))) {
            return said;
        }

        var unit = new SilUnit(t => ReferenceEquals(t, program));
        var (module, lowering) = SilGen.LowerUnitByFile(compilation, unit, SilProgram.Index(compilation));
        said.AddRange(lowering.Select(static d => $"{d.Tree?.Text.FileName} {d.Diagnostic.Id} {d.Diagnostic.Span}: {d.Diagnostic.Message}"));
        said.AddRange(module.Unit!.Abi.Select(static p => $"abi {p.Key} {p.Value}"));
        said.AddRange(module.Unit.External.OrderBy(static e => e.Key, StringComparer.Ordinal).Select(static e => $"external {e.Key} {e.Value}"));
        said.AddRange(module.Unit.Owned.Order(StringComparer.Ordinal).Select(static o => $"owned {o}"));
        said.AddRange(SilPrinter.Print(module).Split("\n\n").Order(StringComparer.Ordinal));
        return said;
    }

    static void Same(string source, bool againstInterface = true, string name = "test.sb") {
        var program = SyntaxTree.Parse(new SourceText(source, name));
        var against = Against(program);
        Assert.Equal(againstInterface, against.IsAgainstInterface);
        Assert.Equal(Compiled(FromSource(program), program), Compiled(against, program));
    }

    static string Root {
        get {
            var directory = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(directory, "Sable.slnx"))) {
                directory = Path.GetDirectoryName(directory)!;
            }

            return directory;
        }
    }

    public static TheoryData<string> Programs() {
        var data = new TheoryData<string>();
        foreach (var directory in (string[])["examples", "tests/programs", "tests/corpus"]) {
            foreach (var file in Directory.GetFiles(Path.Combine(Root, directory), "*.sb").Order(StringComparer.Ordinal)) {
                data.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Programs))]
    public void AProgramCompilesAsFromSource(string file) {
        var text = File.ReadAllText(Path.Combine(Root, file));
        var program = SyntaxTree.Parse(new SourceText(text, Path.GetFileName(file)));
        Assert.Equal(Compiled(FromSource(program), program), Compiled(Against(program), program));
    }

    [Theory]
    [InlineData("module M\n\nfn main() {\n    let name = \"world\"\n    print(\"hi \\(name) \\(1 + 2)\")\n}\n")]

    // A default argument, a default that is the call's place, an autoclosure, a constant of the library's.
    [InlineData("module M\n\nfn main() {\n    assert(1 < 2, \"fine\")\n    precondition(Platform.pointerWidth == 64)\n    print([1, 2, 3].sorted(by: { $0 > $1 }).map { \"\\($0)\" }.joined(separator: \", \"))\n}\n")]

    // Generic instances, subscripts, properties, a derived member, an optional's members.
    [InlineData("module M\n\n@derive(Hashable, Debug)\nstruct Point { x: Int; y: Int }\n\nfn main() {\n    var seen: [Point: Int] = [:]\n    seen[Point(x: 1, y: 2)] = 3\n    let found = seen[Point(x: 1, y: 2)]\n    print(found.map { $0 + 1 } ?? 0, seen.count, \"abc\"[..].count, (1..<4).reversed().count)\n    let set: Set<Int> = [1, 2]\n    print(set.contains(1), [1, 2][0..<1], Ordering.less == .greater)\n}\n")]

    // Errors that name the library: its declarations' places, its private and internal members.
    [InlineData("module M\n\nfn main() {\n    let s = \"abc\"\n    s.nothing()\n    let n: Int = s\n    print(s.count(of: 3), [1].append(\"x\"))\n    Int(1, 2, 3)\n    switch Ordering.less {\n        case .less: {}\n    }\n}\n")]

    // A class of the library's, an actor, a task, a protocol conformed to.
    [InlineData("module M\n\nstruct Count: Sequence, Iterator {\n    var at: Int = 0\n    mutating fn next() -> Int? {\n        self.at += 1\n        return self.at < 4 ? self.at : none\n    }\n}\n\nactor Counter {\n    var n: Int = 0\n    fn bump() -> Int {\n        self.n += 1\n        return self.n\n    }\n}\n\n@main\nfn run() async {\n    let counter = Counter()\n    let task = Task { await counter.bump() }\n    print(await task.value)\n    for i in Count() { print(i) }\n}\n")]

    // Generators, and a generic function over the library's protocols.
    [InlineData("module M\n\nfn evens(below n: Int) -> some Sequence<Int> {\n    for i in 0..<n where i % 2 == 0 { yield i }\n}\n\nfn total<S: Sequence>(_ s: S) -> Int where S.Element == Int {\n    var sum = 0\n    for x in s { sum += x }\n    return sum\n}\n\nfn main() {\n    print(total(evens(below: 10)), total([1, 2, 3]))\n}\n")]

    // Key paths, and a derived Validatable's rules, the library's methods and extensions.
    [InlineData("module M\n\nimport Sable.Validation\n\n@derive(Validatable)\nstruct Point {\n    @range(1...) @lessThan(\\.y)\n    var x: Int\n    var y: Int?\n    @notEmpty @email\n    var name: String\n}\n\nfn main() {\n    var rules = Point.validationRules()\n    rules.field(\\.name).maxLength(3)\n    let all = [Point(x: 1, y: none, name: \"a\")]\n    print(all.map(\\.name)[0])\n}\n")]
    public void ASmallProgramCompilesAsFromSource(string source) => Same(source);

    /// <summary>A program extending the library's own types is compiled with them declared from source.</summary>
    [Theory]
    [InlineData("module M\n\nextension Int {\n    fn twice() -> Int => self * 2\n}\n\nfn main() { print(2.twice()) }\n")]
    [InlineData("module M\n\nextension<T> T[:] {\n    fn second() -> T? => self.count > 1 ? self[1] : none\n}\n\nfn main() { print([1, 2].second() ?? 0) }\n")]
    [InlineData("module M\n\nprotocol Named { fn name() -> String }\n\nextension String: Named {\n    fn name() -> String => self\n}\n\nfn main() { print(\"a\".name()) }\n")]
    public void AProgramExtendingTheLibraryIsCompiledFromSource(string source) => Same(source, againstInterface: false);

    [Fact]
    public void AProgramExtendingItsOwnTypesIsNot() =>
        Same("module M\n\nstruct Box { n: Int }\n\nextension Box: Equatable {\n    static fn ==(a: Box, b: Box) -> Bool => a.n == b.n\n}\n\nfn main() { print(Box(n: 1) == Box(n: 1)) }\n".Replace("static fn ==(a: Box, b: Box)", "operator ==(a: Box, b: Box)", StringComparison.Ordinal));

    [Fact]
    public void AProgramBringingAFileOfTheLibraryIsCompiledFromSource() {
        var program = SyntaxTree.Parse(new SourceText("module Sable.Core\n\npublic enum Bit { zero; one }\n", "lib/Sable.Core/Ordering.sb"));
        Assert.False(Compilation.Create([program], library: SymbolsFor(Parse(""))).IsAgainstInterface);
        var beside = Parse("module Sable.Core\n\npublic enum Bit { zero; one }\n");
        Assert.False(Compilation.Create([beside], library: SymbolsFor(Parse(""))).IsAgainstInterface);
    }

    [Fact]
    public void TheWholeLibraryHasSymbolsAndItsBodiesBindAgainstThem() {
        var everything = Parse(string.Concat(Library.ModuleNames.Select(static m => $"import {m}\n")));
        var compilation = Against(everything);
        Assert.True(compilation.IsAgainstInterface);

        // Every body of the library, bound from its file in its file's scope
        // with the interface's symbols around it: as clean as from source.
        Assert.Equal(
            [],
            compilation.GetAllDiagnosticsByFile().Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(static d => $"{d.Tree?.Text.FileName}:{d.Diagnostic.Span.Start} {d.Diagnostic.Id} {d.Diagnostic.Message}")
        );
    }

    [Fact]
    public void EveryDeclarationIsItsFilesOwn() {
        var program = Parse("module M\n");
        var compilation = Against(program);
        var source = FromSource(program);
        source.GetDiagnostics();
        foreach (var tree in compilation.SyntaxTrees.Where(compilation.IsFromInterface)) {
            var declared = tree.Root.DescendantNodes().Where(n => source.GetDeclaredSymbol(n) is not null).ToList();
            foreach (var node in declared) {
                Assert.Equal($"{node.Kind} {source.GetDeclaredSymbol(node)}", $"{node.Kind} {compilation.GetDeclaredSymbol(node)}");
            }

            foreach (var symbol in compilation.InterfaceDeclarations(tree)) {
                Assert.Same(symbol, compilation.GetDeclaredSymbol(symbol.DeclaringSyntax[0]));
                Assert.Same(tree, compilation.TreeOf(symbol.DeclaringSyntax[0]));
            }

            Assert.Equal(source.GetFileScope(tree).Imports.Select(static m => m.Name), compilation.GetFileScope(tree).Imports.Select(static m => m.Name));
        }
    }

    [Fact]
    public void DamagedSymbolsAreNotRead() {
        var symbols = SymbolsFor(Parse(""));
        var bytes = symbols.Write();
        Assert.Null(LibrarySymbols.Read(bytes[..(bytes.Length / 2)], symbols.Trees));
        Assert.Null(LibrarySymbols.Read([], symbols.Trees));
        var changed = (byte[])bytes.Clone();
        changed[bytes.Length / 2] ^= 0xFF;
        Assert.Null(LibrarySymbols.Read(changed, symbols.Trees));
        Assert.Null(LibrarySymbols.Read(bytes, [..symbols.Trees.Skip(1)]));
        Assert.Equal(bytes, LibrarySymbols.Read(bytes, symbols.Trees)!.Write());
    }
}
