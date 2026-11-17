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
        string Captured = "",
        int Through = -1) {
        public override string ToString() => Body.Replace("\n", " ⏎ ", StringComparison.Ordinal);
    }

    const string Preamble = """
                            struct Point { var x: Int, var y: Int }
                            fn use(_ n: Int) -> Int { return n }
                            fn pair() -> (Int, Int) { return (1, 2) }
                            union Oops { Bad }
                            fn risky() throws(Oops) -> Int { return 1 }
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

            // ---- Flow precision: what crosses the boundary ------------------
            // A value written before the region and overwritten inside it
            // before any read does not flow in; one overwritten after it
            // before any read does not flow out.
            data.Add(new("var a = 0\na = 5\nlet b = a", 2, "b", "a", "b", "a"));
            data.Add(new("var a = 0\nif n > 0 {\n    a = 1\n    use(a)\n}", 1, Read: "n,a", Written: "a", In: "n"));
            data.Add(new("var a = 0\nif n > 0 {\n    a = 1\n}\nlet b = a", 1, Read: "n", Written: "a", In: "n", Out: "a"));
            data.Add(new("var a = 0\nif n > 0 {\n    a = 1\n}\nuse(a)", 2, Read: "a", In: "a"));
            data.Add(new("var a = 0\na = n\na = 2\nuse(a)", 1, Read: "n", Written: "a", In: "n"));
            data.Add(new("var a = 0\na = n\na = 2\nuse(a)", 2, Written: "a", Out: "a"));
            data.Add(new("var a = n\nlet b = a\na = 3\nuse(a)", 1, "b", "a", "b", "a"));
            data.Add(new("var a = n\nlet b = a\na = 3\nuse(a)", 2, Written: "a", Out: "a"));
            data.Add(new("var a = 0\na = n\nif n > 0 {\n    a = 1\n} else {\n    a = 2\n}\nuse(a)", 1, Read: "n", Written: "a", In: "n"));
            data.Add(new("var a = 0\na = n\nif n > 0 {\n    a = 1\n}\nuse(a)", 1, Read: "n", Written: "a", In: "n", Out: "a"));
            data.Add(new("var a = 0\nif n > 0 {\n    a = 1\n} else {\n    a = 2\n}\nuse(a)", 1, Read: "n", Written: "a", In: "n", Out: "a"));
            data.Add(new("var a = 0\nif n > 0 {\n    a = 1\n} else {\n    a = 2\n}\nuse(a)", 2, Read: "a", In: "a"));
            data.Add(new("var a = 0\nif n > 0 {\n    a = 1\n    return a\n}\nuse(a)", 1, Read: "n,a", Written: "a", In: "n"));
            data.Add(new("var a = 0\nif n > 0 {\n    return a\n}\na = 1\nuse(a)", 1, Read: "n,a", In: "n,a"));

            // ---- Flow precision: loops ----------------------------------------
            data.Add(new("var a = 0\nfor x in xs {\n    use(a)\n    a = x\n}", 1, "x", "xs,a,x", "x,a", "xs,a"));
            data.Add(new("var i = 0\nwhile i < n {\n    i = i + 1\n}\nuse(i)", 1, Read: "i,n", Written: "i", In: "i,n", Out: "i"));
            data.Add(new("var i = 0\nrepeat {\n    i = i + 1\n} while i < n\nuse(i)", 1, Read: "i,n", Written: "i", In: "i,n", Out: "i"));
            data.Add(new("var total = 0\nfor x in xs {\n    total += x\n}\nuse(total)", 1, "x", "xs,total,x", "x,total", "xs,total", "total"));
            data.Add(new("var found = 0\nfor x in xs {\n    if x == n {\n        found = x\n        break\n    }\n}\nuse(found)", 1, "x", "xs,x,n", "x,found", "xs,n", "found"));
            data.Add(new("for x in xs where x > n {\n    use(x)\n}", 0, "x", "xs,x,n", "x", "xs,n"));
            data.Add(new("var a = 0\nwhile n > a {\n    a = a + 1\n}", 1, Read: "n,a", Written: "a", In: "n,a"));
            data.Add(new("var last = 0\nfor x in xs {\n    last = x\n}\nuse(last)", 1, "x", "xs,x", "x,last", "xs", "last"));
            data.Add(new("var a = 0\nfor x in xs {\n    a = x\n}\na = 1\nuse(a)", 1, "x", "xs,x", "x,a", "xs"));

            // ---- Flow precision: guard, if let, match ---------------------------
            data.Add(new("guard n > 0 else { return 1 }\nuse(n)", 0, Read: "n", In: "n"));
            data.Add(new("let o: Int? = n\nguard let v = o else { return 1 }\nuse(v)", 1, "v", "o", "v", "o", "v"));
            data.Add(new("let o: Int? = n\nif let v = o {\n    use(v)\n}", 1, "v", "o,v", "v", "o"));
            data.Add(new("var a = 0\nlet b = n > 0 ? a : n", 1, "b", "n,a", "b", "n,a"));
            data.Add(new("var a = 0\nmatch n {\n    case 0 => { a = 1 }\n    default => { a = 2 }\n}\nuse(a)", 1, Read: "n", Written: "a", In: "n", Out: "a"));
            data.Add(new("var a = 0\nmatch n {\n    case 0 => { a = 1 }\n    default => { }\n}\nuse(a)", 2, Read: "a", In: "a"));
            data.Add(new("match n {\n    case let k where k > 0 => { use(k) }\n    default => { }\n}", 0, "k", "n,k", "k", "n"));
            data.Add(new("let o: Int? = none\nlet v = o ?? n", 1, "v", "o,n", "v", "o,n"));

            // ---- Flow precision: errors ------------------------------------------
            data.Add(new("var a = 0\ndo {\n    a = try risky()\n} catch {\n    a = 2\n}\nuse(a)", 1, Written: "a", Out: "a"));
            data.Add(new("var a = 0\ndo {\n    let t = try risky()\n    a = t\n} catch {\n    use(a)\n}", 1, "t", "t,a", "t,a", "a"));
            data.Add(new("let v = try risky() catch { n }", 0, "v", "n", "v", "n"));
            data.Add(new("var a = n\ndefer { use(a) }\na = 1", 1, Read: "a", In: "a"));

            // ---- Flow precision: destructuring and fields -------------------------
            data.Add(new("let (a, b) = pair()\nuse(a)", 0, "a,b", Written: "a,b", Out: "a"));
            data.Add(new("let { x, y } = p\nuse(x)", 0, "x,y", "p", "x,y", "p", "x"));
            data.Add(new("var q = p\nq.x = n\nuse(q.y)", 1, Read: "n", Written: "q", In: "n", Out: "q"));
            data.Add(new("var q = p\nq.x = n\nq = p\nuse(q.y)", 1, Read: "n", Written: "q", In: "n"));

            // ---- Flow precision: closures ------------------------------------------
            data.Add(new("var a = 0\nlet f = () => { a = n }\nf()\nuse(a)", 1, "f", "n", "f,a", "n", "f,a", "a,n"));
            data.Add(new("let a = n\nlet f = () => a + 1\nuse(f())", 1, "f", "a", "f", "a", "f", "a"));
            data.Add(new("let f = (k: Int) => k + n\nuse(f(1))", 0, "f,k", "k,n", "f,k", "n", "f", "n"));
            data.Add(new("fn twice(_ k: Int) -> Int { return k * 2 }\nuse(twice(n))", 0, Read: "k", In: ""));

            // ---- Statement ranges ------------------------------------------------
            data.Add(new("var a = 0\na = n\nlet b = a\nuse(b)", 1, "b", "n,a", "a,b", "n", "b", Through: 2));
            data.Add(new("var a = n\nlet b = a + 1\na = b\nuse(a)", 1, "b", "a,b", "b,a", "a", "a", Through: 2));
            data.Add(new("var a = 0\nvar b = 0\na = n\nb = a\nuse(a)\nuse(b)", 2, Read: "n,a", Written: "a,b", In: "n", Out: "a,b", Through: 3));

            // ---- Nothing at all -----------------------------------------------------
            data.Add(new("let a = 1", 0, "a", Written: "a"));
            data.Add(new("use(1)", 0));
            data.Add(new("return 0", 0));

            return data;
        }
    }

    /// <summary>
    ///     Straight-line programs, generated, with the answer worked out by an
    ///     oracle that knows nothing of the compiler.
    /// </summary>
    /// <remarks>
    ///     Four variables are declared, then a random run of statements reads
    ///     and writes them, and a random range of those statements is the
    ///     region. With no branches the answer is a scan: a variable flows in
    ///     when a read inside sees a write from before the region — the
    ///     latest write before it is outside — and flows out when a read after
    ///     the region sees a write from inside it. The cases are fixed by the
    ///     seed, so a failure always reproduces.
    /// </remarks>
    public static TheoryData<Case> GeneratedCases {
        get {
            var data = new TheoryData<Case>();
            var random = new Random(20261122);
            string[] names = ["v0", "v1", "v2", "v3"];

            while (data.Count < 195) {
                // Each statement: what it reads, in order, then what it writes.
                var statements = new List<(string Text, string[] Reads, string? Write)>();
                var length = random.Next(3, 9);
                for (var i = 0; i < length; i++) {
                    var target = names[random.Next(names.Length)];
                    var a = names[random.Next(names.Length)];
                    var b = names[random.Next(names.Length)];
                    statements.Add(random.Next(5) switch {
                        0 => ($"{target} = {a} + {b}", [a, b], target),
                        1 => ($"{target} = n", ["n"], target),
                        2 => ($"use({a})", [a], null),
                        3 => ($"{target} += {a}", [a, target], target),
                        _ => ($"{target} = {a}", [a], target),
                    });
                }

                var first = random.Next(length);
                var last = random.Next(first, Math.Min(length, first + 3));

                var lines = names.Select(static name => $"var {name} = 0").Concat(statements.Select(static s => s.Text));

                // The oracle: where each variable was last written, by index.
                // The declarations count as writes before everything.
                var lastWrite = names.ToDictionary(static name => name, static _ => -1);
                var read = new List<string>();
                var written = new List<string>();
                var flowsIn = new HashSet<string>();
                var flowsOut = new HashSet<string>();

                for (var i = 0; i < length; i++) {
                    var inside = i >= first && i <= last;
                    foreach (var name in statements[i].Reads) {
                        if (name == "n") {
                            if (inside) {
                                read.Add(name);
                                flowsIn.Add(name);
                            }

                            continue;
                        }

                        var writer = lastWrite[name];
                        var writerInside = writer >= first && writer <= last;
                        if (inside) {
                            read.Add(name);
                            if (!writerInside) {
                                flowsIn.Add(name);
                            }
                        } else if (i > last && writerInside) {
                            flowsOut.Add(name);
                        }
                    }

                    if (statements[i].Write is { } target) {
                        lastWrite[target] = i;
                        if (inside) {
                            written.Add(target);
                        }
                    }
                }

                data.Add(
                    new(
                        string.Join("\n", lines),
                        names.Length + first,
                        Read: string.Join(",", read.Distinct()),
                        Written: string.Join(",", written.Distinct()),
                        In: string.Join(",", flowsIn),
                        Out: string.Join(",", flowsOut),
                        Through: names.Length + last
                    )
                );
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GeneratedCases))]
    public void AGeneratedStraightLineCaseFlowsAsTheOracleSays(Case testCase) => TheAnalysisSaysWhatTheCaseClaims(testCase);

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

        var last = testCase.Through < 0 ? testCase.Statement : testCase.Through;
        var flow = model.AnalyzeDataFlow(body.Statements[testCase.Statement], body.Statements[last]);
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
