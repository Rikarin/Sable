namespace Sable.Syntax.Tests;

public sealed class ExpressionParserTests
{
    private static string Parse(string source)
    {
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        return TreeDump.Dump(tree);
    }

    // ---- Precedence, per spec/SYNTAX.md §16 -------------------------------

    [Theory]
    [InlineData("1 + 2 * 3", "(Binary (Literal 1) + (Binary (Literal 2) * (Literal 3)))")]
    [InlineData("1 * 2 + 3", "(Binary (Binary (Literal 1) * (Literal 2)) + (Literal 3))")]
    [InlineData("1 - 2 - 3", "(Binary (Binary (Literal 1) - (Literal 2)) - (Literal 3))")]
    [InlineData("a << 1 + 2", "(Binary (IdentifierName a) << (Binary (Literal 1) + (Literal 2)))")]
    [InlineData("a < b == c", "(Binary (Binary (IdentifierName a) < (IdentifierName b)) == (IdentifierName c))")]
    [InlineData("a & b == c", "(Binary (IdentifierName a) & (Binary (IdentifierName b) == (IdentifierName c)))")]
    [InlineData("a | b ^ c", "(Binary (IdentifierName a) | (Binary (IdentifierName b) ^ (IdentifierName c)))")]
    [InlineData("a && b | c", "(Binary (IdentifierName a) && (Binary (IdentifierName b) | (IdentifierName c)))")]
    [InlineData("a || b && c", "(Binary (IdentifierName a) || (Binary (IdentifierName b) && (IdentifierName c)))")]
    public void BindsOperatorsByPrecedence(string source, string expected) =>
        Assert.Equal(expected, Parse(source));

    [Fact]
    public void CoalesceIsRightAssociative() =>
        Assert.Equal(
            "(Binary (IdentifierName a) ?? (Binary (IdentifierName b) ?? (IdentifierName c)))",
            Parse("a ?? b ?? c"));

    [Fact]
    public void AssignmentIsRightAssociative() =>
        Assert.Equal(
            "(Assignment (IdentifierName a) = (Assignment (IdentifierName b) = (IdentifierName c)))",
            Parse("a = b = c"));

    [Fact]
    public void AssignmentIsLooserThanEverythingElse() =>
        Assert.Equal(
            "(Assignment (IdentifierName a) += (Binary (IdentifierName b) + (IdentifierName c)))",
            Parse("a += b + c"));

    [Fact]
    public void ConditionalBindsLooserThanCoalesce() =>
        Assert.Equal(
            "(Conditional (Binary (IdentifierName a) ?? (IdentifierName b)) ? (IdentifierName c) (IdentifierName d))",
            Parse("a ?? b ? c : d"));

    [Fact]
    public void ConditionalIsRightAssociative() =>
        Assert.Equal(
            "(Conditional (IdentifierName a) ? (IdentifierName b) "
            + "(Conditional (IdentifierName c) ? (IdentifierName d) (IdentifierName e)))",
            Parse("a ? b : c ? d : e"));

    // ---- Unary and postfix ------------------------------------------------

    [Theory]
    [InlineData("-a * b", "(Binary (PrefixUnary - (IdentifierName a)) * (IdentifierName b))")]
    [InlineData("!a && b", "(Binary (PrefixUnary ! (IdentifierName a)) && (IdentifierName b))")]
    [InlineData("~a", "(PrefixUnary ~ (IdentifierName a))")]
    [InlineData("&a", "(PrefixUnary & (IdentifierName a))")]
    [InlineData("await f()", "(PrefixUnary await (Invocation (IdentifierName f) (ArgumentList)))")]
    [InlineData("move b", "(PrefixUnary move (IdentifierName b))")]
    [InlineData("--a", "(PrefixUnary - (PrefixUnary - (IdentifierName a)))")]
    public void ParsesPrefixOperators(string source, string expected) =>
        Assert.Equal(expected, Parse(source));

    [Fact]
    public void ForceUnwrapIsPostfix() =>
        Assert.Equal("(PostfixUnary (IdentifierName a) !)", Parse("a!"));

    [Fact]
    public void ForceUnwrapChainsWithMemberAccess() =>
        Assert.Equal(
            "(MemberAccess (PostfixUnary (IdentifierName a) !) . (IdentifierName b))",
            Parse("a!.b"));

    [Fact]
    public void NotEqualsIsNotAForceUnwrap() =>
        Assert.Equal(
            "(Binary (IdentifierName a) != (IdentifierName b))",
            Parse("a != b"));

    // ---- Member access, calls, indexing -----------------------------------

    [Theory]
    [InlineData("a.b.c",
        "(MemberAccess (MemberAccess (IdentifierName a) . (IdentifierName b)) . (IdentifierName c))")]
    [InlineData("a?.b",
        "(MemberAccess (IdentifierName a) ?. (IdentifierName b))")]
    [InlineData(".Circle",
        "(ImplicitMemberAccess . (IdentifierName Circle))")]
    [InlineData("a[1]",
        "(ElementAccess (IdentifierName a) (ArgumentList (Argument (Literal 1))))")]
    [InlineData("a?[1]",
        "(ElementAccess (IdentifierName a) ? (ArgumentList (Argument (Literal 1))))")]
    [InlineData("f?(x)",
        "(Invocation (IdentifierName f) ? (ArgumentList (Argument (IdentifierName x))))")]
    public void ParsesPostfixChains(string source, string expected) =>
        Assert.Equal(expected, Parse(source));

    [Fact]
    public void ParsesLabelledAndModifiedArguments() =>
        Assert.Equal(
            "(Invocation (IdentifierName relocate) (ArgumentList "
            + "(Argument from (IdentifierName a)) "
            + "(Argument to & (IdentifierName b)) "
            + "(Argument out (IdentifierName c))))",
            Parse("relocate(from: a, to: &b, out c)"));

    [Fact]
    public void MovePrefixKeywordShadowsACallToAFunctionNamedMove()
    {
        // 'move' is a prefix operator, so 'move(x)' is a move of a parenthesised
        // expression, not a call. Naming a function 'move' is not possible.
        Assert.Equal(
            "(PrefixUnary move (Parenthesized (IdentifierName x)))",
            Parse("move(x)"));
    }

    [Fact]
    public void ParsesTrailingCommaInArgumentList() =>
        Assert.Equal(
            "(Invocation (IdentifierName f) (ArgumentList (Argument (Literal 1)) (Argument (Literal 2))))",
            Parse("f(1, 2,)"));

    [Fact]
    public void ParsesImplicitMemberCall() =>
        Assert.Equal(
            "(Invocation (ImplicitMemberAccess . (IdentifierName Circle)) "
            + "(ArgumentList (Argument radius (Literal 1))))",
            Parse(".Circle(radius: 1)"));

    // ---- Casts and type tests ---------------------------------------------

    [Theory]
    [InlineData("x as Int", "(Cast (IdentifierName x) as (SimpleNameType Int))")]
    [InlineData("x as? Widget", "(Cast (IdentifierName x) as ? (SimpleNameType Widget))")]
    [InlineData("x as! Widget", "(Cast (IdentifierName x) as ! (SimpleNameType Widget))")]
    [InlineData("x unchecked as I32", "(Cast (IdentifierName x) unchecked as (SimpleNameType I32))")]
    [InlineData("x is Drawable", "(TypeTest (IdentifierName x) is (SimpleNameType Drawable))")]
    public void ParsesCastsAndTests(string source, string expected) =>
        Assert.Equal(expected, Parse(source));

    [Fact]
    public void CastBindsTighterThanMultiplication() =>
        Assert.Equal(
            "(Binary (Cast (IdentifierName a) as (SimpleNameType F64)) * (IdentifierName b))",
            Parse("a as F64 * b"));

    // ---- Ranges -----------------------------------------------------------

    [Theory]
    [InlineData("0..<10", "(Range (Literal 0) ..< (Literal 10))")]
    [InlineData("0...10", "(Range (Literal 0) ... (Literal 10))")]
    [InlineData("..<10", "(Range ..< (Literal 10))")]
    [InlineData("5...", "(Range (Literal 5) ...)")]
    [InlineData("a + 1 ..< b", "(Range (Binary (IdentifierName a) + (Literal 1)) ..< (IdentifierName b))")]
    public void ParsesRanges(string source, string expected) =>
        Assert.Equal(expected, Parse(source));

    [Fact]
    public void RangeIsTighterThanComparison() =>
        Assert.Equal(
            "(Binary (Range (Literal 0) ..< (Literal 5)) < (IdentifierName n))",
            Parse("0..<5 < n"));

    // ---- Collection and tuple literals ------------------------------------

    [Theory]
    [InlineData("[1, 2, 3]", "(Array (Literal 1) (Literal 2) (Literal 3))")]
    [InlineData("[]", "(Array)")]
    [InlineData("[:]", "(Dictionary)")]
    [InlineData("[\"a\": 1]", "(Dictionary (KeyValuePair (Literal \"a\") (Literal 1)))")]
    [InlineData("(1, 2)", "(Tuple (TupleElement (Literal 1)) (TupleElement (Literal 2)))")]
    [InlineData("(q: 1, r: 2)", "(Tuple (TupleElement q (Literal 1)) (TupleElement r (Literal 2)))")]
    [InlineData("(a)", "(Parenthesized (IdentifierName a))")]
    [InlineData("()", "(Tuple)")]
    public void ParsesLiteralsAndGroups(string source, string expected) =>
        Assert.Equal(expected, Parse(source));

    [Fact]
    public void ParenthesesOverridePrecedence() =>
        Assert.Equal(
            "(Binary (Parenthesized (Binary (Literal 1) + (Literal 2))) * (Literal 3))",
            Parse("(1 + 2) * 3"));

    [Fact]
    public void ParsesRecordLiteral() =>
        Assert.Equal(
            "(Record (SimpleNameType Point) (FieldInitializer x (Literal 1)) "
            + "(FieldInitializer y (Literal 2)))",
            Parse("Point { x: 1, y: 2 }"));

    [Fact]
    public void ParsesRecordLiteralShorthandField() =>
        Assert.Equal(
            "(Record (SimpleNameType Point) (FieldInitializer x) (FieldInitializer y))",
            Parse("Point { x, y }"));

    [Fact]
    public void ParsesQualifiedRecordLiteral() =>
        Assert.Equal(
            "(Record (QualifiedName (SimpleNameType Geo) . (SimpleNameType Point)) "
            + "(FieldInitializer x (Literal 1)))",
            Parse("Geo.Point { x: 1 }"));

    [Fact]
    public void AnIdentifierNotFollowedByABraceIsJustAName() =>
        Assert.Equal("(IdentifierName Point)", Parse("Point"));

    // ---- try, generics, builtins ------------------------------------------

    [Theory]
    [InlineData("try f()", "(Try try (Invocation (IdentifierName f) (ArgumentList)))")]
    [InlineData("try? f()", "(Try try ? (Invocation (IdentifierName f) (ArgumentList)))")]
    [InlineData("try! f()", "(Try try ! (Invocation (IdentifierName f) (ArgumentList)))")]
    public void ParsesTryForms(string source, string expected) =>
        Assert.Equal(expected, Parse(source));

    [Fact]
    public void ParsesExplicitGenericArguments() =>
        Assert.Equal(
            "(Invocation (GenericName max (TypeArgumentList < (SimpleNameType Int) >)) "
            + "(ArgumentList (Argument (IdentifierName a)) (Argument (IdentifierName b))))",
            Parse("max<Int>(a, b)"));

    [Fact]
    public void LessThanIsComparisonWhenItIsNotATypeArgumentList() =>
        Assert.Equal(
            "(Binary (Binary (IdentifierName a) < (IdentifierName b)) > (IdentifierName c))",
            Parse("a < b > c"));

    [Fact]
    public void ParsesCompilerBuiltins() =>
        Assert.Equal("(Builtin # file)", Parse("#file"));

    [Fact]
    public void ParsesSelfAndSuper()
    {
        Assert.Equal("(MemberAccess (Self self) . (IdentifierName x))", Parse("self.x"));
        Assert.Equal("(MemberAccess (Super super) . (IdentifierName init))", Parse("super.init"));
    }

    [Fact]
    public void InterpolatedStringIsOneLiteralTokenWithAccessibleHoles()
    {
        var tree = SyntaxTree.ParseExpression("\"a \\(x + 1) b\"");
        var literal = Assert.IsType<LiteralExpressionSyntax>(tree.Root);
        Assert.Equal(3, literal.Token.Parts.Length);
        Assert.Equal("x + 1", literal.Token.Parts[1].Text);
    }

    // ---- A dense, realistic expression ------------------------------------

    [Fact]
    public void ParsesADenseExpression()
    {
        const string source = "user?.address?.city ?? config.defaults[\"city\"] as? String ?? \"unknown\"";
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        Assert.Equal(SyntaxKind.BinaryExpression, tree.Root.Kind);
    }
}
