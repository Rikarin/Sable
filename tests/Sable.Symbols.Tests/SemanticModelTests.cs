namespace Sable.Symbols.Tests;

/// <summary>
///     The semantic model as a product: what it answers for the tools that read
///     it, rather than what the binder happens to compute.
/// </summary>
public sealed class SemanticModelTests {
    static (SemanticModel Model, SyntaxTree Tree) Compile(string source) {
        var compilation = Compilation.Create("module M\n" + source);
        var tree = compilation.SyntaxTrees[0];
        return (compilation.GetSemanticModel(tree), tree);
    }

    static StatementSyntax NthStatement(SyntaxTree tree, int index) =>
        tree.Root.DescendantNodes().OfType<BlockSyntax>().First().Statements[index];

    static IReadOnlyList<string> Names(IEnumerable<Symbol> symbols) =>
        [..symbols.Select(static s => s.Name).Order(StringComparer.Ordinal)];

    // ---- Every body is bound, not just 'fn' --------------------------------

    [Fact]
    public void AnInitializerBodyIsChecked() {
        var (model, _) = Compile(
            """
            struct Point {
                var x: Int
                init(_ n: Int) { self.x = "no" }
            }
            """
        );
        Assert.Contains(model.GetDiagnostics(), static d => d.Code == ErrorCode.ERR_TypeMismatch);
    }

    [Fact]
    public void AnOperatorBodyIsChecked() {
        var (model, _) = Compile(
            """
            struct Money {
                var cents: I64
                operator + (a: Money, b: Money) -> Money { return 1 }
            }
            """
        );
        Assert.Contains(model.GetDiagnostics(), static d => d.Code == ErrorCode.ERR_TypeMismatch);
    }

    [Fact]
    public void AnAccessorBodyIsChecked() {
        var (model, _) = Compile(
            """
            struct Temperature {
                var celsius: F64
                var label: String { get => celsius }
            }
            """
        );
        Assert.Contains(model.GetDiagnostics(), static d => d.Code == ErrorCode.ERR_TypeMismatch);
    }

    [Fact]
    public void ASetterSeesNewValue() {
        var (model, _) = Compile(
            """
            struct Box {
                var stored: Int
                var value: Int {
                    get => stored
                    set { stored = newValue }
                }
            }
            """
        );
        Assert.DoesNotContain(model.GetDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AFieldInitializerIsChecked() {
        var (model, _) = Compile(
            """
            struct Point { var x: Int = "no" }
            """
        );
        Assert.Contains(model.GetDiagnostics(), static d => d.Code == ErrorCode.ERR_TypeMismatch);
    }

    [Fact]
    public void SuperReachesTheBaseClass() {
        var (model, _) = Compile(
            """
            open class Node {
                var value: Int
                init(value: Int) { self.value = value }
                open fn describe() -> String { return "node" }
            }
            class Leaf: Node {
                init(value: Int) { super.init(value: value) }
                override fn describe() -> String { return super.describe() }
            }
            """
        );
        var errors = model.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    // ---- Data flow ---------------------------------------------------------

    [Fact]
    public void DataFlowReportsWhatAStatementReadsAndWrites() {
        var (model, tree) = Compile(
            """
            fn probe(_ a: Int, _ b: Int) -> Int {
                var total = 0
                total = a + b
                return total
            }
            """
        );

        var flow = model.AnalyzeDataFlow(NthStatement(tree, 1));

        Assert.True(flow.Succeeded);
        Assert.Equal(["a", "b"], Names(flow.ReadInside));
        Assert.Equal(["total"], Names(flow.WrittenInside));
        Assert.Empty(flow.VariablesDeclared);
    }

    [Fact]
    public void DataFlowReportsWhatCrossesTheBoundary() {
        var (model, tree) = Compile(
            """
            fn probe(_ a: Int) -> Int {
                var total = 0
                total = a + 1
                return total
            }
            """
        );

        var flow = model.AnalyzeDataFlow(NthStatement(tree, 1));

        Assert.Equal(["a"], Names(flow.DataFlowsIn));
        Assert.Equal(["total"], Names(flow.DataFlowsOut));
    }

    [Fact]
    public void AValueWrittenAndNeverReadDoesNotFlowOut() {
        var (model, tree) = Compile(
            """
            fn probe(_ a: Int) -> Int {
                var unused = 0
                unused = a
                return a
            }
            """
        );

        var flow = model.AnalyzeDataFlow(NthStatement(tree, 1));
        Assert.Empty(flow.DataFlowsOut);
    }

    [Fact]
    public void ADeclarationIsReportedAsDeclared() {
        var (model, tree) = Compile(
            """
            fn probe(_ a: Int) -> Int {
                let doubled = a * 2
                return doubled
            }
            """
        );

        var flow = model.AnalyzeDataFlow(NthStatement(tree, 0));
        Assert.Equal(["doubled"], Names(flow.VariablesDeclared));
        Assert.Equal(["a"], Names(flow.DataFlowsIn));
    }

    [Fact]
    public void AClosureReportsWhatItCaptures() {
        var (model, tree) = Compile(
            """
            fn probe(_ a: Int) -> Int {
                let f = () => { return a }
                return f()
            }
            """
        );

        var flow = model.AnalyzeDataFlow(NthStatement(tree, 0));
        Assert.Equal(["a"], Names(flow.Captured));
    }

    // ---- Control flow ------------------------------------------------------

    [Fact]
    public void ControlFlowFindsTheReturns() {
        var (model, tree) = Compile(
            """
            fn probe(_ a: Int) -> Int {
                if a > 0 {
                    return 1
                } else {
                    return 2
                }
            }
            """
        );

        var flow = model.AnalyzeControlFlow(NthStatement(tree, 0));

        Assert.True(flow.Succeeded);
        Assert.Equal(2, flow.ReturnStatements.Length);
        Assert.False(flow.EndPointIsReachable);
    }

    [Fact]
    public void AStatementThatFallsThroughHasAReachableEnd() {
        var (model, tree) = Compile(
            """
            fn probe(_ a: Int) -> Int {
                if a > 0 {
                    return 1
                }
                return 2
            }
            """
        );

        var flow = model.AnalyzeControlFlow(NthStatement(tree, 0));
        Assert.True(flow.EndPointIsReachable);
        Assert.Single(flow.ReturnStatements);
    }

    [Fact]
    public void ABreakInsideTheRegionDoesNotLeaveIt() {
        // The break ends the loop, and the loop is the region: control goes
        // on past it normally, so nothing leaves early.
        var (model, tree) = Compile(
            """
            fn probe(_ items: Int[]) -> Int {
                for i in items {
                    break
                }
                return 0
            }
            """
        );

        var flow = model.AnalyzeControlFlow(NthStatement(tree, 0));
        Assert.Empty(flow.ExitPoints);
        Assert.Empty(flow.ReturnStatements);
        Assert.True(flow.EndPointIsReachable);
    }

    [Fact]
    public void ABreakToALoopOutsideTheRegionIsAnExitPoint() {
        var (model, tree) = Compile(
            """
            fn probe(_ items: Int[]) -> Int {
                outer: for i in items {
                    for j in items {
                        break outer
                    }
                }
                return 0
            }
            """
        );

        var inner = tree.Root.DescendantNodes().OfType<ForStatementSyntax>().Last();
        var flow = model.AnalyzeControlFlow(inner);
        Assert.IsType<BoundBreak>(Assert.Single(flow.ExitPoints));

        // 'items' may be empty, so the inner loop can also just finish.
        Assert.True(flow.EndPointIsReachable);
    }

    // ---- The step budget ---------------------------------------------------

    [Fact]
    public void AnExpressionOverTheBudgetIsReportedRatherThanBound() {
        var source = "module M\nfn probe() -> Int { return "
            + string.Join(" + ", Enumerable.Repeat("1", 50))
            + " }";

        var compilation = Compilation.Create(source)
            .WithBindOptions(new() { StepBudget = 10 });

        Assert.Contains(
            compilation.GetAllDiagnostics(),
            static d => d.Code == ErrorCode.ERR_ExpressionTooComplex
        );
    }

    [Fact]
    public void TheBudgetIsReportedOncePerStatement() {
        var source = "module M\nfn probe() -> Int { return "
            + string.Join(" + ", Enumerable.Repeat("1", 50))
            + " }";

        var compilation = Compilation.Create(source)
            .WithBindOptions(new() { StepBudget = 10 });

        Assert.Single(
            compilation.GetAllDiagnostics(),
            static d => d.Code == ErrorCode.ERR_ExpressionTooComplex
        );
    }

    [Fact]
    public void TheDefaultBudgetIsGenerous() {
        var source = "module M\nfn probe() -> Int { return "
            + string.Join(" + ", Enumerable.Repeat("1", 500))
            + " }";

        Assert.DoesNotContain(
            Compilation.Create(source).GetAllDiagnostics(),
            static d => d.Code == ErrorCode.ERR_ExpressionTooComplex
        );
    }

    /// <summary>
    ///     Offsets overlap between files: a body's error says which file it is
    ///     in, so that a driver does not place one file's error in another.
    /// </summary>
    [Fact]
    public void ADiagnosticKnowsItsFile() {
        var program = SyntaxTree.Parse(new("module P\nfn main() {\n    let a = 1\n    let b = 2\n}\n", "p.sab"));
        var other = SyntaxTree.Parse(new("module Q\nfn f() -> Int { return true }\n", "q.sab"));

        var found = Compilation.Create([program, other])
            .GetAllDiagnosticsByFile()
            .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.NotEmpty(found);
        Assert.All(found, d => Assert.Same(other, d.Tree));
    }
}
