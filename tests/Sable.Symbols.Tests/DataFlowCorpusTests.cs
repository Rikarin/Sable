namespace Sable.Symbols.Tests;

/// <summary>
///     A table of statements and what <see cref="SemanticModel.AnalyzeDataFlow" />
///     should say about each, per the exit criterion in docs/04 §1.4.
/// </summary>
/// <remarks>
///     Each row is a function body. The statement under analysis is marked with
///     a <c>//!</c> comment; everything else is context. The expectations are
///     written as comma-separated names, in no particular order.
/// </remarks>
public sealed class DataFlowCorpusTests {
    /// <summary>One case: a body, which statement to analyse, and the answer.</summary>
    public sealed record Case(
        string Body,
        int Statement,
        string Declared = "",
        string Read = "",
        string Written = "",
        string In = "",
        string Out = "",
        string Captured = "") {
        public override string ToString() => Body.Replace("\n", " ⏎ ", StringComparison.Ordinal);
    }

    const string Preamble = """
                            struct Point { var x: Int, var y: Int }
                            fn use(_ n: Int) -> Int { return n }
                            fn pair() -> (Int, Int) { return (1, 2) }
                            """;

    public static TheoryData<Case> Cases {
        get {
            var data = new TheoryData<Case>();

            // ---- Declarations ----------------------------------------------
            data.Add(new("let a = 1\nlet b = 2", 0, "a", Written: "a"));
            data.Add(
                new(
                    "let a = 1\nlet b = a",
                    1,
                    "b",
                    "a",
                    "b",
                    "a"
                )
            );
            data.Add(new("var a: Int\na = 1\nlet b = a", 0, "a"));
            data.Add(
                new(
                    "var a: Int\na = 1\nlet b = a",
                    1,
                    Written: "a",
                    Out: "a"
                )
            );
            data.Add(
                new(
                    "let a = n + 1",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );

            // ---- Assignments ------------------------------------------------
            data.Add(new("var a = 0\na = n", 1, Read: "n", Written: "a", In: "n"));
            data.Add(
                new(
                    "var a = 0\na = a + 1",
                    1,
                    Read: "a",
                    Written: "a",
                    In: "a"
                )
            );
            data.Add(
                new(
                    "var a = 0\na += n",
                    1,
                    Read: "a,n",
                    Written: "a",
                    In: "a,n"
                )
            );
            data.Add(
                new(
                    "var a = 0\na = n\nlet b = a",
                    1,
                    Read: "n",
                    Written: "a",
                    In: "n",
                    Out: "a"
                )
            );

            // ---- Calls -------------------------------------------------------
            data.Add(
                new(
                    "let a = use(n)",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = use(use(n))",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );

            // ---- Members and tuples -------------------------------------------
            data.Add(
                new(
                    "let a = p.x",
                    0,
                    "a",
                    "p",
                    "a",
                    "p"
                )
            );
            data.Add(new("let a = pair().0", 0, "a", Written: "a"));

            // ---- Branches -----------------------------------------------------
            data.Add(
                new(
                    "var a = 0\nif n > 0 { a = 1 }",
                    1,
                    Read: "n",
                    Written: "a",
                    In: "n"
                )
            );
            data.Add(
                new(
                    "var a = 0\nif n > 0 { a = 1 } else { a = 2 }\nlet b = a",
                    1,
                    Read: "n",
                    Written: "a",
                    In: "n",
                    Out: "a"
                )
            );
            data.Add(
                new(
                    "var a = 0\nif a > 0 { a = 1 }",
                    1,
                    Read: "a",
                    Written: "a",
                    In: "a"
                )
            );

            // ---- Loops ---------------------------------------------------------
            data.Add(
                new(
                    "var total = 0\nwhile total < n { total = total + 1 }",
                    1,
                    Read: "total,n",
                    Written: "total",
                    In: "total,n"
                )
            );
            data.Add(
                new(
                    "var total = 0\nfor i in xs { total = total + i }",
                    1,
                    "i",
                    "xs,total,i",
                    "total,i",
                    "xs,total"
                )
            );

            // ---- Closures -------------------------------------------------------
            data.Add(
                new(
                    "let f = () => { return n }",
                    0,
                    "f",
                    "n",
                    "f",
                    "n",
                    Captured: "n"
                )
            );
            data.Add(
                new(
                    "var a = 0\nlet f = () => { a = 1 }",
                    1,
                    "f",
                    Written: "f,a",
                    In: string.Empty,
                    Captured: "a"
                )
            );
            data.Add(
                new(
                    "let f = [copied = n] () => { return copied }",
                    0,
                    "f,copied",
                    "n,copied",
                    "f,copied",
                    "n",
                    Captured: "copied"
                )
            );

            // ---- Matches ----------------------------------------------------------
            data.Add(
                new(
                    "var a = 0\nmatch n { case 1 => { a = 1 } default => { a = 2 } }",
                    1,
                    Read: "n",
                    Written: "a",
                    In: "n"
                )
            );
            data.Add(
                new(
                    "let a = match n { case 1 => n default => 0 }",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );

            // ---- Operators ---------------------------------------------------------
            data.Add(new("let a = n + n", 0, "a", "n", "a", "n"));
            data.Add(new("let a = -n", 0, "a", "n", "a", "n"));
            data.Add(
                new(
                    "let a = !(n > 0)",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = n > 0 && n < 9",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = n > 0 ? n : 0",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = n as I64",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = n is Int",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );

            // ---- Compound shapes ----------------------------------------------------
            data.Add(
                new(
                    "let a = (n, n)",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = [n, n]",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = [\"k\": n]",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = Point { x: n, y: n }",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = 0..<n",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );
            data.Add(
                new(
                    "let a = \"v: \\(n)\"",
                    0,
                    "a",
                    "n",
                    "a",
                    "n"
                )
            );

            // ---- Reading through things --------------------------------------------
            data.Add(
                new(
                    "let a = p.x + p.y",
                    0,
                    "a",
                    "p",
                    "a",
                    "p"
                )
            );
            data.Add(
                new(
                    "let a = xs[n]",
                    0,
                    "a",
                    "xs,n",
                    "a",
                    "xs,n"
                )
            );
            data.Add(
                new(
                    "var q = p\nq.x = n",
                    1,
                    Read: "n",
                    Written: "q",
                    In: "n"
                )
            );

            // ---- Several statements at once ----------------------------------------
            data.Add(
                new(
                    "var a = 0\nvar b = 0\na = b",
                    2,
                    Read: "b",
                    Written: "a",
                    In: "b"
                )
            );
            data.Add(
                new(
                    "var a = 0\nvar b = 0\nb = a\nlet c = b",
                    2,
                    Read: "a",
                    Written: "b",
                    In: "a",
                    Out: "b"
                )
            );
            data.Add(
                new(
                    "var a = 0\nif n > 0 { a = 1 } else { a = 2 }",
                    1,
                    Read: "n",
                    Written: "a",
                    In: "n"
                )
            );

            // ---- Loops in more shapes ----------------------------------------------
            data.Add(
                new(
                    "var a = 0\nfor i in 0..<n { a = a + i }",
                    1,
                    "i",
                    "n,a,i",
                    "a,i",
                    "n,a"
                )
            );
            data.Add(
                new(
                    "var a = 0\nfor i in xs where i > 0 { a = a + i }",
                    1,
                    "i",
                    "xs,a,i",
                    "a,i",
                    "xs,a"
                )
            );
            data.Add(
                new(
                    "var a = 0\nrepeat { a = a + 1 } while a < n",
                    1,
                    Read: "a,n",
                    Written: "a",
                    In: "a,n"
                )
            );
            data.Add(
                new(
                    "var a = 0\nwhile a < n { a = a + 1 }\nlet b = a",
                    1,
                    Read: "a,n",
                    Written: "a",
                    In: "a,n",
                    Out: "a"
                )
            );

            // ---- Conditions that bind ----------------------------------------------
            data.Add(
                new(
                    "let m: Int? = n\nif let v = m { use(v) }",
                    1,
                    "v",
                    "m,v",
                    "v",
                    "m"
                )
            );
            data.Add(
                new(
                    "let m: Int? = n\nguard let v = m else { return 0 }",
                    1,
                    "v",
                    "m",
                    "v",
                    "m"
                )
            );

            // ---- Patterns -----------------------------------------------------------
            data.Add(
                new(
                    "match n { case let k => { use(k) } }",
                    0,
                    "k",
                    "n,k",
                    "k",
                    "n"
                )
            );
            data.Add(
                new(
                    "let t = pair()\nmatch t { case (let a, let b) => { use(a) } }",
                    1,
                    "a,b",
                    "t,a",
                    "a,b",
                    "t"
                )
            );

            // ---- Closures -----------------------------------------------------------
            data.Add(
                new(
                    "let f = (k: Int) => { return k + n }",
                    0,
                    "f,k",
                    "n,k",
                    "f,k",
                    "n",
                    Captured: "n"
                )
            );
            data.Add(
                new(
                    "let f = () => { let inner = n\nreturn inner }",
                    0,
                    "f,inner",
                    "n,inner",
                    "f,inner",
                    "n",
                    Captured: "n"
                )
            );
            data.Add(
                new(
                    "var a = 0\nlet f = () => { return a + n }",
                    1,
                    "f",
                    "a,n",
                    "f",
                    "a,n",
                    Captured: "a,n"
                )
            );

            // ---- Local functions ------------------------------------------------------
            data.Add(new("fn inner() -> Int { return n }", 0, Read: "n", In: "n"));

            // ---- Defer and do ----------------------------------------------------------
            data.Add(new("var a = 0\ndefer { a = 1 }", 1, Written: "a"));
            data.Add(new("var a = 0\ndo { a = 1 } catch { a = 2 }", 1, Written: "a"));

            // ---- Nothing at all -----------------------------------------------------
            data.Add(new("let a = 1", 0, "a", Written: "a"));
            data.Add(new("use(1)", 0));
            data.Add(new("return 0", 0));

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheAnalysisSaysWhatTheCaseClaims(Case testCase) {
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
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );

        var body = tree.Root.DescendantNodes()
            .OfType<FunctionDeclarationSyntax>()
            .First(static f => f.Identifier.Text == "probe")
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

    static void AssertSame(
        string expected,
        IEnumerable<Symbol> actual,
        string what
    ) {
        var wanted = expected
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToList();

        var got = actual.Select(static s => s.Name).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            wanted.SequenceEqual(got, StringComparer.Ordinal),
            $"{what}: expected [{string.Join(", ", wanted)}] "
            + $"but found [{string.Join(", ", got)}]"
        );
    }
}
