using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A program compiled against Sable.Core's interface (<c>.sbi</c>, #182) —
///     the core's bodies not checked again, its code taken from the
///     interface's SIL rather than lowered — says and does exactly what the
///     program compiled whole from source does: the same diagnostics, the
///     same SIL, the same output.
/// </summary>
public sealed class InterfaceCompileTests {
    /// <summary>The core's interface, written and read back as a build would keep it: built once for the suite.</summary>
    static readonly Lazy<LibraryInterface> Core = new(static () => {
        var built = Pipeline.CoreInterface(ParseOptions.Default);
        return LibraryInterface.Read(built.Write(), Pipeline.CompilerIdentity) ?? throw new InvalidOperationException("the core's interface did not read back");
    });

    static SyntaxTree Parse(string source) => SyntaxTree.Parse(new SourceText(source, "test.sb"));

    /// <summary>
    ///     The functions a module's program reaches — from its entry point,
    ///     its exports and what its types name (vtables, deinits) — each as
    ///     SIL text, sorted. Compiled whole, a program lowers only the
    ///     library code it reaches; against an interface it links in all of
    ///     the library's, of which it reaches the same. The interface's SIL
    ///     was printed and read back, which numbers values and blocks afresh,
    ///     so the whole program's is printed and read back too, and the two
    ///     are compared as SIL, not by the order lowering made them in.
    /// </summary>
    static List<string> Sil(SilModule module) {
        var text = SilPrinter.Print(SilParser.Parse(SilPrinter.Print(module)));
        var functions = new Dictionary<string, string>(StringComparer.Ordinal);
        var roots = new List<string> { module.EntryPoint! };
        foreach (var chunk in text.Split("\n\n")) {
            if (chunk.StartsWith("sil @", StringComparison.Ordinal)) {
                functions[chunk[5..chunk.IndexOf(' ', 5)]] = chunk.TrimEnd();
            } else {
                roots.AddRange(References(chunk));
            }
        }

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var waiting = new Stack<string>(roots);
        while (waiting.TryPop(out var name)) {
            if (reached.Add(name) && functions.TryGetValue(name, out var body)) {
                foreach (var referenced in References(body)) {
                    waiting.Push(referenced);
                }
            }
        }

        return [..reached.Where(functions.ContainsKey).Select(n => functions[n]).Order(StringComparer.Ordinal)];

        static IEnumerable<string> References(string sil) =>
            System.Text.RegularExpressions.Regex.Matches(sil, @"@([^\s:(),\[\]{}""]+)").Select(static m => m.Groups[1].Value);
    }

    static string Run(SilModule module) {
        var interpreter = new SilInterpreter(module, 10_000_000);
        interpreter.Run(module.EntryPoint!);
        return interpreter.Output.ToString();
    }

    static readonly string[] Sources = [
        "module M\n\nfn main() {\n    print(\"hi \\(1 + 2)\")\n}\n",
        """
        module M

        @derive(Equatable)
        struct Point {
            var x: Int
            var y: Int
            fn sum() -> Int => x + y
        }

        class Counter {
            var count = 0
            fn bump() { count += 1 }
        }

        let origin = Point(x: 0, y: 0)

        @main
        fn main() {
            var points: Point[>..] = [Point(x: 1, y: 2), Point(x: 3, y: 4)]
            points.append(origin)
            let total = points.map { $0.sum() }.reduce(0) { $0 + $1 }
            let counter = Counter()
            for _ in 0..<3 { counter.bump() }
            var names: [String: Int] = [:]
            names["a"] = 1
            print("\(total) \(counter.count) \(points.contains(origin)) \(names["a"] ?? 0)")
            let text = "Sable"
            print("\(text.uppercased()) \(text.count)")
        }
        """,
        """
        module M

        union Shape {
            Circle(radius: Int)
            Square(side: Int)
        }

        fn area(_ shape: Shape) -> Int {
            return match shape {
                case .Circle(let r) => 3 * r * r
                case .Square(let s) => s * s
            }
        }

        fn main() {
            let shapes = [Shape.Circle(radius: 2), Shape.Square(side: 3)]
            var sum = 0
            for shape in shapes { sum += area(shape) }
            let maybe: Int? = shapes.isEmpty ? none : sum
            print(maybe ?? -1)
        }
        """,
    ];

    public static readonly TheoryData<string> Programs = new(Sources);

    [Theory]
    [MemberData(nameof(Programs))]
    public void AProgramCompiledAgainstTheCoresInterfaceIsTheProgramCompiledWhole(string source) {
        var whole = Pipeline.Compile([Parse(source)]);
        var against = Pipeline.CompileAgainst([Parse(source)], [Core.Value]);
        Assert.True(whole.Succeeded, string.Join("\n", whole.Errors));
        Assert.True(against.Succeeded, string.Join("\n", against.Errors));
        Assert.Equal(whole.Warnings, against.Warnings);
        Assert.Equal(Sil(whole.Sil!), Sil(against.Sil!));
        Assert.Equal(Run(whole.Sil!), Run(against.Sil!));
    }

    /// <summary>
    ///     Optimised, it does the same. Its SIL may differ where lowering the
    ///     whole program used what only the whole program shows — an
    ///     existential's call made directly when no type the program declares
    ///     answers it otherwise — which the library's code, lowered as a unit
    ///     of its own (docs/05 §5.4), leaves to dispatch.
    /// </summary>
    [Fact]
    public void AnOptimisedProgramDoesTheSame() {
        var source = Sources[1];
        var whole = Pipeline.Compile([Parse(source)], optimised: true);
        var against = Pipeline.CompileAgainst([Parse(source)], [Core.Value], optimised: true);
        Assert.True(against.Succeeded, string.Join("\n", against.Errors));
        Assert.Equal(Run(whole.Sil!), Run(against.Sil!));
    }

    [Theory]
    [InlineData("module M\nfn main() {\n    let x: Int = true\n}\n")]
    [InlineData("module M\nfn main() {\n    let unused = 1\n    print(missing)\n}\n")]
    [InlineData("module M\n@noncopy struct H { var v: Int }\nfn take(_ h: consuming H) {}\nfn main() {\n    let h = H(v: 1)\n    take(h)\n    take(h)\n}\n")]
    public void ItSaysWhatTheWholeProgramSays(string source) {
        var whole = Pipeline.Compile([Parse(source)]);
        var against = Pipeline.CompileAgainst([Parse(source)], [Core.Value]);
        Assert.False(against.Succeeded);
        Assert.NotEmpty(against.Errors);
        Assert.Equal(whole.Errors, against.Errors);
        Assert.Equal(whole.Warnings, against.Warnings);
    }

    [Fact]
    public void AnInterfaceForAnotherProfileIsRefused() {
        var program = SyntaxTree.Parse(new SourceText("module M\nfn main() { }\n", "test.sb"), Pipeline.ParseOptionsFor(optimise: true));
        var result = Pipeline.CompileAgainst([program], [Core.Value]);
        Assert.False(result.Succeeded);
        Assert.Contains("cannot compile against the interface of 'Sable.Core'", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void TheInterfacesSymbolsAreTheCoresOwn() {
        var modules = Core.Value.ReadSymbols();
        var core = Assert.Single(modules);
        Assert.Equal(CoreLibrary.ModuleName, core.Name);
        Assert.NotNull(core.GetType("String"));
        Assert.Equal(Core.Value.SymbolBytes, SymbolInterface.Write(modules));
    }
}
