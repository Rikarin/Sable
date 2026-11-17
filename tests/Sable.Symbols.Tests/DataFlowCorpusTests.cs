using Sable.Symbols;

namespace Sable.Symbols.Tests;

/// <summary>
/// A table of statements and what <see cref="SemanticModel.AnalyzeDataFlow"/>
/// should say about each, per the exit criterion in docs/04 §1.4.
/// </summary>
/// <remarks>
/// Each row is a function body. The statement under analysis is marked with
/// a <c>//!</c> comment; everything else is context. The expectations are
/// written as comma-separated names, in no particular order.
/// </remarks>
public sealed class DataFlowCorpusTests
{
    /// <summary>One case: a body, which statement to analyse, and the answer.</summary>
    public sealed record Case(
        string Body,
        int Statement,
        string Declared = "",
        string Read = "",
        string Written = "",
        string In = "",
        string Out = "",
        string Captured = "")
    {
        public override string ToString() =>
            Body.Replace("\n", " ⏎ ", StringComparison.Ordinal);
    }

    private const string Preamble = """
        struct Point { var x: Int, var y: Int }
        fn use(_ n: Int) -> Int { return n }
        fn pair() -> (Int, Int) { return (1, 2) }
        """;

    public static TheoryData<Case> Cases
    {
        get
        {
            var data = new TheoryData<Case>();

            // ---- Declarations ----------------------------------------------
            data.Add(new Case("let a = 1\nlet b = 2", 0, Declared: "a", Written: "a"));
            data.Add(new Case("let a = 1\nlet b = a", 1,
                Declared: "b", Read: "a", Written: "b", In: "a"));
            data.Add(new Case("var a: Int\na = 1\nlet b = a", 0, Declared: "a"));
            data.Add(new Case("var a: Int\na = 1\nlet b = a", 1,
                Written: "a", Out: "a"));
            data.Add(new Case("let a = n + 1", 0,
                Declared: "a", Read: "n", Written: "a", In: "n"));

            // ---- Assignments ------------------------------------------------
            data.Add(new Case("var a = 0\na = n", 1, Read: "n", Written: "a", In: "n"));
            data.Add(new Case("var a = 0\na = a + 1", 1,
                Read: "a", Written: "a", In: "a"));
            data.Add(new Case("var a = 0\na += n", 1,
                Read: "a,n", Written: "a", In: "a,n"));
            data.Add(new Case("var a = 0\na = n\nlet b = a", 1,
                Read: "n", Written: "a", In: "n", Out: "a"));

            // ---- Calls -------------------------------------------------------
            data.Add(new Case("let a = use(n)", 0,
                Declared: "a", Read: "n", Written: "a", In: "n"));
            data.Add(new Case("let a = use(use(n))", 0,
                Declared: "a", Read: "n", Written: "a", In: "n"));

            // ---- Members and tuples -------------------------------------------
            data.Add(new Case("let a = p.x", 0,
                Declared: "a", Read: "p", Written: "a", In: "p"));
            data.Add(new Case("let a = pair().0", 0, Declared: "a", Written: "a"));

            // ---- Branches -----------------------------------------------------
            data.Add(new Case("var a = 0\nif n > 0 { a = 1 }", 1,
                Read: "n", Written: "a", In: "n"));
            data.Add(new Case("var a = 0\nif n > 0 { a = 1 } else { a = 2 }\nlet b = a", 1,
                Read: "n", Written: "a", In: "n", Out: "a"));
            data.Add(new Case("var a = 0\nif a > 0 { a = 1 }", 1,
                Read: "a", Written: "a", In: "a"));

            // ---- Loops ---------------------------------------------------------
            data.Add(new Case("var total = 0\nwhile total < n { total = total + 1 }", 1,
                Read: "total,n", Written: "total", In: "total,n"));
            data.Add(new Case("var total = 0\nfor i in xs { total = total + i }", 1,
                Declared: "i", Read: "xs,total,i", Written: "total", In: "xs,total"));

            // ---- Closures -------------------------------------------------------
            data.Add(new Case("let f = () => { return n }", 0,
                Declared: "f", Read: "n", Written: "f", In: "n", Captured: "n"));
            data.Add(new Case("var a = 0\nlet f = () => { a = 1 }", 1,
                Declared: "f", Written: "f,a", In: "", Captured: "a"));
            data.Add(new Case("let f = [copied = n] () => { return copied }", 0,
                Declared: "f,copied", Read: "n,copied", Written: "f,copied",
                In: "n", Captured: "copied"));

            // ---- Matches ----------------------------------------------------------
            data.Add(new Case("var a = 0\nmatch n { case 1 => { a = 1 } default => { a = 2 } }", 1,
                Read: "n", Written: "a", In: "n"));
            data.Add(new Case("let a = match n { case 1 => n default => 0 }", 0,
                Declared: "a", Read: "n", Written: "a", In: "n"));

            // ---- Nothing at all -----------------------------------------------------
            data.Add(new Case("let a = 1", 0, Declared: "a", Written: "a"));
            data.Add(new Case("use(1)", 0));

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheAnalysisSaysWhatTheCaseClaims(Case testCase)
    {
        var source = $$"""
            module M
            {{Preamble}}
            fn probe(_ n: Int, _ p: Point, _ xs: Int[]) -> Int {
            {{testCase.Body}}
            return 0
            }
            """;

        var compilation = Compilation.Create(source);
        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);

        var errors = compilation.GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message}")));

        var body = tree.Root.DescendantNodes()
            .OfType<FunctionDeclarationSyntax>()
            .First(f => f.Identifier.Text == "probe")
            .Body!;

        var flow = model.AnalyzeDataFlow(body.Statements[testCase.Statement]);
        Assert.True(flow.Succeeded);

        AssertSame(testCase.Declared, flow.VariablesDeclared, "declared");
        AssertSame(testCase.Read, flow.ReadInside, "read");
        AssertSame(testCase.Written, flow.WrittenInside, "written");
        AssertSame(testCase.In, flow.DataFlowsIn, "flows in");
        AssertSame(testCase.Out, flow.DataFlowsOut, "flows out");
        AssertSame(testCase.Captured, flow.Captured, "captured");
    }

    private static void AssertSame(
        string expected, IEnumerable<Symbol> actual, string what)
    {
        var wanted = expected
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToList();

        var got = actual.Select(s => s.Name).Order(StringComparer.Ordinal).ToList();

        Assert.True(wanted.SequenceEqual(got, StringComparer.Ordinal),
            $"{what}: expected [{string.Join(", ", wanted)}] "
            + $"but found [{string.Join(", ", got)}]");
    }
}
