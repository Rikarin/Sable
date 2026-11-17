namespace Sable.Syntax.Tests;

public sealed class StatementParserTests {
    static string Parse(string source) {
        var tree = SyntaxTree.ParseStatement(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        return TreeDump.Dump(tree);
    }

    static SyntaxNode ParseRoot(string source) {
        var tree = SyntaxTree.ParseStatement(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        return tree.Root;
    }

    // ---- Declarations and simple statements -------------------------------

    [Fact]
    public void ParsesLocalBinding() =>
        Assert.Equal(
            "(LocalDeclarationStatement let (VariableDeclarator (BindingPattern x) "
            + "(EqualsValueClause = (Literal 1))))",
            Parse("let x = 1")
        );

    [Fact]
    public void ParsesLocalBindingWithTypeAnnotation() =>
        Assert.Equal(
            "(LocalDeclarationStatement var (VariableDeclarator (BindingPattern total) "
            + "(TypeAnnotation (SimpleNameType I64))))",
            Parse("var total: I64")
        );

    [Fact]
    public void ParsesTupleDestructuring() =>
        Assert.Equal(
            "(LocalDeclarationStatement let (VariableDeclarator "
            + "(TuplePattern (BindingPattern a) (BindingPattern b)) "
            + "(EqualsValueClause = (IdentifierName pair))))",
            Parse("let (a, b) = pair")
        );

    [Fact]
    public void ParsesRecordDestructuringWithoutATypeName() =>
        Assert.Equal(
            "(LocalDeclarationStatement let (VariableDeclarator "
            + "(RecordPattern (FieldPattern name) (FieldPattern age)) "
            + "(EqualsValueClause = (IdentifierName person))))",
            Parse("let { name, age } = person")
        );

    [Fact]
    public void ParsesUsingBinding() =>
        Assert.Equal(
            "(LocalDeclarationStatement using let (VariableDeclarator (BindingPattern f) "
            + "(EqualsValueClause = (Invocation (MemberAccess (IdentifierName File) . "
            + "(IdentifierName open)) (ArgumentList (Argument (IdentifierName path)))))))",
            Parse("using let f = File.open(path)")
        );

    [Theory]
    [InlineData("return", SyntaxKind.ReturnStatement)]
    [InlineData("return x", SyntaxKind.ReturnStatement)]
    [InlineData("throw .NotFound(id)", SyntaxKind.ThrowStatement)]
    [InlineData("break", SyntaxKind.BreakStatement)]
    [InlineData("break outer", SyntaxKind.BreakStatement)]
    [InlineData("continue outer", SyntaxKind.ContinueStatement)]
    [InlineData("fallthrough", SyntaxKind.FallthroughStatement)]
    [InlineData(";", SyntaxKind.EmptyStatement)]
    [InlineData("{ }", SyntaxKind.Block)]
    [InlineData("defer { close() }", SyntaxKind.DeferStatement)]
    [InlineData("unsafe { p.store(0) }", SyntaxKind.UnsafeStatement)]
    [InlineData("comptime { check() }", SyntaxKind.ComptimeStatement)]
    [InlineData("f(1)", SyntaxKind.ExpressionStatement)]
    public void ParsesSimpleStatements(string source, SyntaxKind expected) =>
        Assert.Equal(expected, ParseRoot(source).Kind);

    [Fact]
    public void BareReturnDoesNotSwallowTheNextLine() {
        var tree = SyntaxTree.ParseStatement("{\n    return\n    f()\n}");
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());

        var block = Assert.IsType<BlockSyntax>(tree.Root);
        Assert.Equal(2, block.Statements.Count);
        Assert.Equal(SyntaxKind.ReturnStatement, block.Statements[0].Kind);
        Assert.Null(Assert.IsType<ReturnStatementSyntax>(block.Statements[0]).Expression);
    }

    // ---- Control flow -----------------------------------------------------

    [Fact]
    public void ParsesIfElseChain() {
        var root = ParseRoot("if x > 0 { a() } else if x < 0 { b() } else { c() }");
        var ifStatement = Assert.IsType<IfStatementSyntax>(root);
        var elseClause = Assert.IsType<ElseClauseSyntax>(ifStatement.ElseClause);
        Assert.Equal(SyntaxKind.IfStatement, elseClause.Body.Kind);
    }

    [Fact]
    public void ParsesOptionalBindingCondition() =>
        Assert.Equal(
            "(IfStatement if (OptionalBindingCondition let (VariableDeclarator "
            + "(BindingPattern user) (EqualsValueClause = (Invocation (IdentifierName find) "
            + "(ArgumentList (Argument (IdentifierName id))))))) (Block))",
            Parse("if let user = find(id) { }")
        );

    [Fact]
    public void ParsesMultipleConditions() {
        var root = ParseRoot("if let a = p, let b = q, a.id == b.id { }");
        var ifStatement = Assert.IsType<IfStatementSyntax>(root);
        Assert.Equal(3, ifStatement.Conditions.Count);
        Assert.Equal(SyntaxKind.OptionalBindingCondition, ifStatement.Conditions[0].Kind);
        Assert.Equal(SyntaxKind.ExpressionCondition, ifStatement.Conditions[2].Kind);
    }

    [Fact]
    public void ParsesCasePatternCondition() =>
        Assert.Equal(
            "(IfStatement if (CasePatternCondition case (UnionCasePattern . Circle "
            + "(PatternArgumentList (SubPattern (BindingPattern let r)))) = (IdentifierName shape)) "
            + "(Block))",
            Parse("if case .Circle(let r) = shape { }")
        );

    [Fact]
    public void ParsesGuardWithSelfShorthand() {
        var root = ParseRoot("guard let self else { return 0 }");
        Assert.Equal(SyntaxKind.GuardStatement, root.Kind);
    }

    [Theory]
    [InlineData("while i < 10 { i += 1 }", SyntaxKind.WhileStatement)]
    [InlineData("repeat { f() } while x", SyntaxKind.RepeatStatement)]
    [InlineData("for i in 0..<10 { }", SyntaxKind.ForStatement)]
    [InlineData("for (k, v) in dict { }", SyntaxKind.ForStatement)]
    [InlineData("for item in xs where item.isActive { }", SyntaxKind.ForStatement)]
    [InlineData("for await r in group { }", SyntaxKind.ForStatement)]
    [InlineData("outer: for i in rows { }", SyntaxKind.LabeledStatement)]
    public void ParsesLoops(string source, SyntaxKind expected) => Assert.Equal(expected, ParseRoot(source).Kind);

    [Fact]
    public void ParsesForWithWhereClause() {
        var root = ParseRoot("for item in xs where item.isActive { }");
        var forStatement = Assert.IsType<ForStatementSyntax>(root);
        Assert.NotNull(forStatement.WhereClause);
        Assert.True(forStatement.AwaitKeyword.IsNone);
    }

    [Fact]
    public void ParsesDoCatch() {
        var root = ParseRoot("do { try f() } catch .NotFound(let p) { log(p) } catch { fail() }");
        var doStatement = Assert.IsType<DoStatementSyntax>(root);
        Assert.Equal(2, doStatement.Catches.Count);
        Assert.NotNull(doStatement.Catches[0].Pattern);
        Assert.Null(doStatement.Catches[1].Pattern);
    }

    [Fact]
    public void ParsesSwitchWithFallthrough() {
        var root = ParseRoot("switch code { case 200, 201: ok(); case 301: fallthrough; default: fail() }");
        var switchStatement = Assert.IsType<SwitchStatementSyntax>(root);
        Assert.Equal(3, switchStatement.Cases.Count);
        Assert.Equal(2, switchStatement.Cases[0].Values.Count);
    }

    [Fact]
    public void ParsesRegionStatements() {
        Assert.Equal(SyntaxKind.RegionStatement, ParseRoot("region arena { f() }").Kind);

        var scoped = Assert.IsType<RegionStatementSyntax>(ParseRoot("region frame = frameArena { f() }"));
        Assert.NotNull(scoped.Source);
    }

    // ---- match ------------------------------------------------------------

    [Fact]
    public void ParsesMatchWithArrowArms() {
        var root = ParseRoot("match shape { case .Circle(let r) => area(r) case .Empty => 0 default => 1 }");
        var statement = Assert.IsType<ExpressionStatementSyntax>(root);
        var match = Assert.IsType<MatchExpressionSyntax>(statement.Expression);
        Assert.Equal(3, match.Arms.Count);
    }

    [Fact]
    public void ParsesMatchArmWithGuard() {
        var root = ParseRoot("match s { case .Poly(let p) where p.count < 3 => 0 }");
        var match = (MatchExpressionSyntax)((ExpressionStatementSyntax)root).Expression;
        var arm = Assert.IsType<MatchArmSyntax>(match.Arms[0]);
        Assert.NotNull(arm.WhereClause);
    }

    [Fact]
    public void ParsesMatchArmWithBlockBody() {
        var root = ParseRoot("match x { case 1 => { f(); g() } }");
        var match = (MatchExpressionSyntax)((ExpressionStatementSyntax)root).Expression;
        var arm = Assert.IsType<MatchArmSyntax>(match.Arms[0]);
        Assert.Equal(SyntaxKind.Block, arm.Body.Kind);
    }

    [Fact]
    public void ParsesMatchColonForm() {
        var root = ParseRoot("match x { case 1: f(); default: g() }");
        var match = (MatchExpressionSyntax)((ExpressionStatementSyntax)root).Expression;
        Assert.All(match.Arms, static arm => Assert.Equal(SyntaxKind.MatchCaseBlock, arm.Kind));
    }

    [Fact]
    public void ParsesMatchAsAnInitialiser() {
        var root = ParseRoot("let area = match shape { case .Rect(let w, let h) => w * h }");
        var declaration = Assert.IsType<LocalDeclarationStatementSyntax>(root);
        Assert.Equal(
            SyntaxKind.MatchExpression,
            declaration.Declarator.Initializer!.Value.Kind
        );
    }

    // ---- Patterns ---------------------------------------------------------

    static string ParsePattern(string pattern) {
        var source = $"match x {{ case {pattern} => 0 }}";
        var tree = SyntaxTree.ParseStatement(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());

        var match = (MatchExpressionSyntax)((ExpressionStatementSyntax)tree.Root).Expression;
        var arm = (MatchArmSyntax)match.Arms[0];
        return TreeDump.Dump(arm.Patterns[0]);
    }

    [Theory]
    [InlineData("_", "(WildcardPattern _)")]
    [InlineData("42", "(LiteralPattern 42)")]
    [InlineData("-1", "(LiteralPattern - 1)")]
    [InlineData("\"a\"", """(LiteralPattern "a")""")]
    [InlineData("none", "(LiteralPattern none)")]
    [InlineData("let v", "(BindingPattern let v)")]
    [InlineData("1...9", "(RangePattern (LiteralPattern 1) ... (LiteralPattern 9))")]
    [InlineData("..<10", "(RangePattern ..< (LiteralPattern 10))")]
    [InlineData("is Drawable", "(TypeTestPattern is (SimpleNameType Drawable))")]
    [InlineData(
        "let s as String",
        "(BindingTypeTestPattern let s as (SimpleNameType String))"
    )]
    [InlineData(
        "(let a, let b)",
        "(TuplePattern (BindingPattern let a) (BindingPattern let b))"
    )]
    [InlineData("(0, _)", "(TuplePattern (LiteralPattern 0) (WildcardPattern _))")]
    [InlineData(
        ".Circle(let r)",
        "(UnionCasePattern . Circle (PatternArgumentList (SubPattern (BindingPattern let r))))"
    )]
    [InlineData(
        ".Rect(width: let w, _)",
        "(UnionCasePattern . Rect (PatternArgumentList (SubPattern width (BindingPattern let w)) "
        + "(SubPattern (WildcardPattern _))))"
    )]
    [InlineData(
        "Shape.Empty",
        "(UnionCasePattern (SimpleNameType Shape) . Empty)"
    )]
    [InlineData(
        "Point { x: 0, y: let y }",
        "(RecordPattern (SimpleNameType Point) (FieldPattern x (LiteralPattern 0)) "
        + "(FieldPattern y (BindingPattern let y)))"
    )]
    [InlineData(
        "[first, _, last]",
        "(ArrayPattern (BindingPattern first) (WildcardPattern _) (BindingPattern last))"
    )]
    [InlineData(
        "[let head, ...let tail]",
        "(ArrayPattern (BindingPattern let head) (RestPattern ... let tail))"
    )]
    [InlineData(
        ".Some(.Ok(let v))",
        "(UnionCasePattern . Some (PatternArgumentList (SubPattern (UnionCasePattern . Ok "
        + "(PatternArgumentList (SubPattern (BindingPattern let v)))))))"
    )]
    public void ParsesPatternForms(string pattern, string expected) => Assert.Equal(expected, ParsePattern(pattern));

    [Fact]
    public void ParsesOrPatterns() =>
        Assert.Equal(
            """(OrPattern (OrPattern (LiteralPattern "a") | (LiteralPattern "b")) """
            + """| (LiteralPattern "c"))""",
            ParsePattern("\"a\" | \"b\" | \"c\"")
        );

    [Fact]
    public void ParsesAlternativePatternsSeparatedByCommas() {
        var source = "match n { case 1, 2, 3 => true }";
        var tree = SyntaxTree.ParseStatement(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());

        var match = (MatchExpressionSyntax)((ExpressionStatementSyntax)tree.Root).Expression;
        Assert.Equal(3, ((MatchArmSyntax)match.Arms[0]).Patterns.Count);
    }

    // ---- Closures ---------------------------------------------------------

    [Fact]
    public void ParsesArrowClosureWithOneParameter() =>
        Assert.Equal(
            "(ExpressionStatement (Invocation (MemberAccess (IdentifierName xs) . "
            + "(IdentifierName map)) (ArgumentList (Argument (Closure (ClosureSignature "
            + "(ClosureParameter x)) => (Binary (IdentifierName x) * (Literal 2)))))))",
            Parse("xs.map(x => x * 2)")
        );

    [Fact]
    public void ParsesArrowClosureWithParenthesizedParameters() {
        var root = ParseRoot("let add = (a: Int, b: Int) => a + b");
        var declaration = Assert.IsType<LocalDeclarationStatementSyntax>(root);
        var closure = Assert.IsType<ClosureExpressionSyntax>(declaration.Declarator.Initializer!.Value);
        Assert.Equal(2, closure.Signature.Parameters.Count);
    }

    [Fact]
    public void ParsesArrowClosureWithBlockBody() {
        var root = ParseRoot("let noop = () => { }");
        var declaration = Assert.IsType<LocalDeclarationStatementSyntax>(root);
        var closure = Assert.IsType<ClosureExpressionSyntax>(declaration.Declarator.Initializer!.Value);
        Assert.Equal(SyntaxKind.Block, closure.Body.Kind);
    }

    [Fact]
    public void ParsesTrailingClosureWithImplicitParameter() {
        var root = ParseRoot("xs.filter { $0.isActive }");
        var invocation = Assert.IsType<InvocationExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(root).Expression
        );
        Assert.Null(invocation.ArgumentList);
        Assert.NotNull(invocation.TrailingClosure);
    }

    [Fact]
    public void ParsesTrailingClosureWithNamedParameters() {
        var root = ParseRoot("xs.sorted { a, b in a.score > b.score }");
        var invocation = Assert.IsType<InvocationExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(root).Expression
        );
        var closure = Assert.IsType<BraceClosureExpressionSyntax>(invocation.TrailingClosure);
        Assert.NotNull(closure.Header);
        Assert.Equal(2, closure.Header!.Parameters.Count);
    }

    [Fact]
    public void ParsesTrailingClosureAfterAnArgumentList() {
        var root = ParseRoot("withLock(mutex) { counter += 1 }");
        var invocation = Assert.IsType<InvocationExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(root).Expression
        );
        Assert.NotNull(invocation.ArgumentList);
        Assert.NotNull(invocation.TrailingClosure);
    }

    [Fact]
    public void ParsesCaptureList() {
        var root = ParseRoot("let c = [weak self, count = localCount] (x: Int) => x");
        var declaration = Assert.IsType<LocalDeclarationStatementSyntax>(root);
        var closure = Assert.IsType<ClosureExpressionSyntax>(declaration.Declarator.Initializer!.Value);
        Assert.Equal(2, closure.CaptureList!.Items.Count);
        Assert.Equal("weak", closure.CaptureList.Items[0].SpecifierToken.Text);
    }

    [Fact]
    public void ParsesClosureHeaderWithReturnType() {
        var root = ParseRoot("let f = { a: Int -> Int in a }");
        var declaration = Assert.IsType<LocalDeclarationStatementSyntax>(root);
        var closure = Assert.IsType<BraceClosureExpressionSyntax>(declaration.Declarator.Initializer!.Value);
        Assert.NotNull(closure.Header!.ReturnType);
    }

    // ---- The brace ambiguity ----------------------------------------------

    [Fact]
    public void AConditionsBraceOpensTheBodyNotAClosure() {
        var root = ParseRoot("if x { f() }");
        var ifStatement = Assert.IsType<IfStatementSyntax>(root);
        Assert.Single(ifStatement.Conditions);
        Assert.Equal(SyntaxKind.ExpressionCondition, ifStatement.Conditions[0].Kind);
    }

    [Fact]
    public void AForSequencesBraceOpensTheBody() {
        var root = ParseRoot("for x in xs { f(x) }");
        var forStatement = Assert.IsType<ForStatementSyntax>(root);
        Assert.Equal(SyntaxKind.IdentifierName, forStatement.Sequence.Kind);
    }

    [Fact]
    public void BracesAreAllowedAgainInsideParentheses() {
        var root = ParseRoot("if f(Point { x: 1 }) { }");
        var ifStatement = Assert.IsType<IfStatementSyntax>(root);
        var condition = Assert.IsType<ExpressionConditionSyntax>(ifStatement.Conditions[0]);
        var invocation = Assert.IsType<InvocationExpressionSyntax>(condition.Expression);
        Assert.Equal(
            SyntaxKind.RecordExpression,
            invocation.ArgumentList!.Arguments[0].Expression.Kind
        );
    }

    [Fact]
    public void ARecordLiteralIsDistinguishedFromATrailingClosureByItsContents() {
        Assert.Equal(
            SyntaxKind.RecordExpression,
            Assert.IsType<ExpressionStatementSyntax>(ParseRoot("Point { x: 1 }")).Expression.Kind
        );

        Assert.Equal(
            SyntaxKind.InvocationExpression,
            Assert.IsType<ExpressionStatementSyntax>(ParseRoot("Point { f() }")).Expression.Kind
        );

        // An empty brace after a bare name is an empty record literal.
        Assert.Equal(
            SyntaxKind.RecordExpression,
            Assert.IsType<ExpressionStatementSyntax>(ParseRoot("Point { }")).Expression.Kind
        );
    }

    [Fact]
    public void AForInsideAClosureIsNotMistakenForAClosureHeader() {
        var root = ParseRoot("run { for x in xs { f(x) } }");
        var invocation = Assert.IsType<InvocationExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(root).Expression
        );
        var closure = Assert.IsType<BraceClosureExpressionSyntax>(invocation.TrailingClosure);
        Assert.Null(closure.Header);
        Assert.Equal(SyntaxKind.ForStatement, closure.Statements[0].Kind);
    }

    // ---- A realistic body ---------------------------------------------------

    [Fact]
    public void ParsesARealisticFunctionBody() {
        const string source = """
                              {
                                  frameArena.reset()
                                  region frame = frameArena {
                                      let visible: Document&[>..] = frame.alloc()
                                      for d in documents where d.isVisible {
                                          visible.append(&d)
                                      }
                                      for d in visible {
                                          d.draw(dt)
                                      }
                                  }

                                  guard let owner = self.owner else { return }

                                  match owner.state {
                                      case .Idle => owner.wake()
                                      case .Busy(let since) where since > limit => owner.defer()
                                      default => { }
                                  }

                                  defer { frameArena.reset() }
                              }
                              """;

        var tree = SyntaxTree.ParseStatement(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());

        var block = Assert.IsType<BlockSyntax>(tree.Root);
        Assert.Equal(5, block.Statements.Count);
    }
}
