namespace Sable.Syntax.Tests;

public sealed class ExpressionParserTests {
    static string Parse(string source) {
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
    [InlineData("a &+ b &* c", "(Binary (IdentifierName a) &+ (Binary (IdentifierName b) &* (IdentifierName c)))")]
    [InlineData("a &- b + c", "(Binary (Binary (IdentifierName a) &- (IdentifierName b)) + (IdentifierName c))")]
    [InlineData("a & -b", "(Binary (IdentifierName a) & (PrefixUnary - (IdentifierName b)))")]
    public void BindsOperatorsByPrecedence(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void CoalesceIsRightAssociative() =>
        Assert.Equal(
            "(Binary (IdentifierName a) ?? (Binary (IdentifierName b) ?? (IdentifierName c)))",
            Parse("a ?? b ?? c")
        );

    [Fact]
    public void AssignmentIsRightAssociative() =>
        Assert.Equal(
            "(Assignment (IdentifierName a) = (Assignment (IdentifierName b) = (IdentifierName c)))",
            Parse("a = b = c")
        );

    [Fact]
    public void AssignmentIsLooserThanEverythingElse() =>
        Assert.Equal(
            "(Assignment (IdentifierName a) += (Binary (IdentifierName b) + (IdentifierName c)))",
            Parse("a += b + c")
        );

    [Fact]
    public void ConditionalBindsLooserThanCoalesce() =>
        Assert.Equal(
            "(Conditional (Binary (IdentifierName a) ?? (IdentifierName b)) ? (IdentifierName c) (IdentifierName d))",
            Parse("a ?? b ? c : d")
        );

    [Theory]
    [InlineData("n > 1 ? [1] : [2]")]
    [InlineData("n > 1 ? (a) : b")]
    [InlineData("flag ? [x] : []")]
    [InlineData("a.count ? (b + c) : d")]
    public void AQuestionMarkWithSpaceBeforeItIsTheConditional(string source) =>
        Assert.StartsWith("(Conditional", Parse(source));

    [Theory]
    [InlineData("a?[0]", "ElementAccess")]
    [InlineData("f?()", "Invocation")]
    [InlineData("a.items?[i] ?? 0", "ElementAccess")]
    public void AQuestionMarkAgainstWhatItChainsFromIndexesOrCalls(string source, string node) {
        var dump = Parse(source);
        Assert.DoesNotContain("Conditional", dump);
        Assert.Contains(node, dump);
    }

    [Fact]
    public void ConditionalIsRightAssociative() =>
        Assert.Equal(
            "(Conditional (IdentifierName a) ? (IdentifierName b) "
            + "(Conditional (IdentifierName c) ? (IdentifierName d) (IdentifierName e)))",
            Parse("a ? b : c ? d : e")
        );

    // ---- Unary and postfix ------------------------------------------------

    [Theory]
    [InlineData("-a * b", "(Binary (PrefixUnary - (IdentifierName a)) * (IdentifierName b))")]
    [InlineData("!a && b", "(Binary (PrefixUnary ! (IdentifierName a)) && (IdentifierName b))")]
    [InlineData("~a", "(PrefixUnary ~ (IdentifierName a))")]
    [InlineData("&a", "(PrefixUnary & (IdentifierName a))")]
    [InlineData("await f()", "(PrefixUnary await (Invocation (IdentifierName f) (ArgumentList)))")]
    [InlineData("move b", "(PrefixUnary move (IdentifierName b))")]
    [InlineData("--a", "(PrefixUnary - (PrefixUnary - (IdentifierName a)))")]
    public void ParsesPrefixOperators(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void ForceUnwrapIsPostfix() => Assert.Equal("(PostfixUnary (IdentifierName a) !)", Parse("a!"));

    [Fact]
    public void ForceUnwrapChainsWithMemberAccess() =>
        Assert.Equal(
            "(MemberAccess (PostfixUnary (IdentifierName a) !) . (IdentifierName b))",
            Parse("a!.b")
        );

    [Fact]
    public void NotEqualsIsNotAForceUnwrap() =>
        Assert.Equal(
            "(Binary (IdentifierName a) != (IdentifierName b))",
            Parse("a != b")
        );

    // ---- Member access, calls, indexing -----------------------------------

    [Theory]
    [InlineData(
        "a.b.c",
        "(MemberAccess (MemberAccess (IdentifierName a) . (IdentifierName b)) . (IdentifierName c))"
    )]
    [InlineData(
        "a?.b",
        "(MemberAccess (IdentifierName a) ?. (IdentifierName b))"
    )]
    [InlineData(
        ".Circle",
        "(ImplicitMemberAccess . (IdentifierName Circle))"
    )]
    [InlineData(
        "a[1]",
        "(ElementAccess (IdentifierName a) (ArgumentList (Argument (Literal 1))))"
    )]
    [InlineData(
        "a?[1]",
        "(ElementAccess (IdentifierName a) ? (ArgumentList (Argument (Literal 1))))"
    )]
    [InlineData(
        "f?(x)",
        "(Invocation (IdentifierName f) ? (ArgumentList (Argument (IdentifierName x))))"
    )]
    public void ParsesPostfixChains(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void ParsesLabelledAndModifiedArguments() =>
        Assert.Equal(
            "(Invocation (IdentifierName relocate) (ArgumentList "
            + "(Argument from (IdentifierName a)) "
            + "(Argument to & (IdentifierName b)) "
            + "(Argument out (IdentifierName c))))",
            Parse("relocate(from: a, to: &b, out c)")
        );

    [Fact]
    public void MovePrefixKeywordShadowsACallToAFunctionNamedMove() {
        // 'move' is a prefix operator, so 'move(x)' is a move of a parenthesised
        // expression, not a call. Naming a function 'move' is not possible.
        Assert.Equal(
            "(PrefixUnary move (Parenthesized (IdentifierName x)))",
            Parse("move(x)")
        );
    }

    [Fact]
    public void ParsesTrailingCommaInArgumentList() =>
        Assert.Equal(
            "(Invocation (IdentifierName f) (ArgumentList (Argument (Literal 1)) (Argument (Literal 2))))",
            Parse("f(1, 2,)")
        );

    [Fact]
    public void ParsesImplicitMemberCall() =>
        Assert.Equal(
            "(Invocation (ImplicitMemberAccess . (IdentifierName Circle)) "
            + "(ArgumentList (Argument radius (Literal 1))))",
            Parse(".Circle(radius: 1)")
        );

    // ---- Casts and type tests ---------------------------------------------

    [Theory]
    [InlineData("x as Int", "(Cast (IdentifierName x) as (SimpleNameType Int))")]
    [InlineData("x as? Widget", "(Cast (IdentifierName x) as ? (SimpleNameType Widget))")]
    [InlineData("x as! Widget", "(Cast (IdentifierName x) as ! (SimpleNameType Widget))")]
    [InlineData("x unchecked as I32", "(Cast (IdentifierName x) unchecked as (SimpleNameType I32))")]
    [InlineData("x is Drawable", "(TypeTest (IdentifierName x) is (SimpleNameType Drawable))")]
    public void ParsesCastsAndTests(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void CastBindsTighterThanMultiplication() =>
        Assert.Equal(
            "(Binary (Cast (IdentifierName a) as (SimpleNameType F64)) * (IdentifierName b))",
            Parse("a as F64 * b")
        );

    // ---- Ranges -----------------------------------------------------------

    [Theory]
    [InlineData("0..<10", "(Range (Literal 0) ..< (Literal 10))")]
    [InlineData("0...10", "(Range (Literal 0) ... (Literal 10))")]
    [InlineData("..<10", "(Range ..< (Literal 10))")]
    [InlineData("5...", "(Range (Literal 5) ...)")]
    [InlineData("a + 1 ..< b", "(Range (Binary (IdentifierName a) + (Literal 1)) ..< (IdentifierName b))")]
    public void ParsesRanges(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void RangeIsTighterThanComparison() =>
        Assert.Equal(
            "(Binary (Range (Literal 0) ..< (Literal 5)) < (IdentifierName n))",
            Parse("0..<5 < n")
        );

    // ---- Collection and tuple literals ------------------------------------

    [Theory]
    [InlineData("[1, 2, 3]", "(Array (Literal 1) (Literal 2) (Literal 3))")]
    [InlineData("[]", "(Array)")]
    [InlineData("[:]", "(Dictionary)")]
    [InlineData("""["a": 1]""", """(Dictionary (KeyValuePair (Literal "a") (Literal 1)))""")]
    [InlineData("(1, 2)", "(Tuple (TupleElement (Literal 1)) (TupleElement (Literal 2)))")]
    [InlineData("(q: 1, r: 2)", "(Tuple (TupleElement q (Literal 1)) (TupleElement r (Literal 2)))")]
    [InlineData("(a)", "(Parenthesized (IdentifierName a))")]
    [InlineData("()", "(Tuple)")]
    public void ParsesLiteralsAndGroups(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void ParenthesesOverridePrecedence() =>
        Assert.Equal(
            "(Binary (Parenthesized (Binary (Literal 1) + (Literal 2))) * (Literal 3))",
            Parse("(1 + 2) * 3")
        );

    [Fact]
    public void ParsesRecordLiteral() =>
        Assert.Equal(
            "(Record (SimpleNameType Point) (FieldInitializer x (Literal 1)) "
            + "(FieldInitializer y (Literal 2)))",
            Parse("Point { x: 1, y: 2 }")
        );

    [Fact]
    public void RecordLiteralsHaveNoFieldShorthand() {
        // 'Point { x }' would be indistinguishable from a closure returning x,
        // so a record field always carries its value. See the brace rule in
        // Parser.LooksLikeRecordLiteral.
        var tree = SyntaxTree.ParseExpression("Point { x, y }");
        Assert.True(tree.HasErrors);
        Assert.Equal("Point { x, y }", tree.ToFullString());
    }

    [Fact]
    public void ParsesQualifiedRecordLiteral() =>
        Assert.Equal(
            "(Record (QualifiedName (SimpleNameType Geo) . (SimpleNameType Point)) "
            + "(FieldInitializer x (Literal 1)))",
            Parse("Geo.Point { x: 1 }")
        );

    [Fact]
    public void AnIdentifierNotFollowedByABraceIsJustAName() => Assert.Equal("(IdentifierName Point)", Parse("Point"));

    // ---- try, generics, builtins ------------------------------------------

    [Theory]
    [InlineData("try f()", "(Try try (Invocation (IdentifierName f) (ArgumentList)))")]
    [InlineData("try? f()", "(Try try ? (Invocation (IdentifierName f) (ArgumentList)))")]
    [InlineData("try! f()", "(Try try ! (Invocation (IdentifierName f) (ArgumentList)))")]
    public void ParsesTryForms(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void ParsesExplicitGenericArguments() =>
        Assert.Equal(
            "(Invocation (GenericName max (TypeArgumentList < (SimpleNameType Int) >)) "
            + "(ArgumentList (Argument (IdentifierName a)) (Argument (IdentifierName b))))",
            Parse("max<Int>(a, b)")
        );

    /// <summary>The '>' of an array kind, '[>..]', closes no type argument list.</summary>
    [Theory]
    [InlineData("Map<String, String[>..]>()")]
    [InlineData("Map<Int[..64], Int[>..<]>()")]
    public void ArrayKindsInsideTypeArgumentsStayGeneric(string source) =>
        Assert.StartsWith("(Invocation (GenericName Map (TypeArgumentList <", Parse(source), StringComparison.Ordinal);

    [Fact]
    public void LessThanIsComparisonWhenItIsNotATypeArgumentList() =>
        Assert.Equal(
            "(Binary (Binary (IdentifierName a) < (IdentifierName b)) > (IdentifierName c))",
            Parse("a < b > c")
        );

    [Fact]
    public void ParsesCompilerBuiltins() => Assert.Equal("(Builtin # file)", Parse("#file"));

    [Fact]
    public void ANestedTupleIndexIsTakenApart() {
        // '0.1' lexes as a float literal, because by spec/lexical.md §6 that
        // is what it is; after a '.' it is two indices.
        Assert.Equal(
            "(MemberAccess (MemberAccess (IdentifierName p) . (IdentifierName 0)) "
            + ". (IdentifierName 1))",
            Parse("p.0.1")
        );
    }

    [Fact]
    public void ANestedTupleIndexRoundTrips() {
        var tree = SyntaxTree.ParseExpression("p . 0.1 ");
        Assert.Equal("p . 0.1 ", tree.Root.ToFullString());
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
    }

    [Fact]
    public void AShorthandParametersTupleElementIsTakenApart() {
        var tree = SyntaxTree.ParseExpression("$0.1.2 ");
        Assert.Equal("$0.1.2 ", tree.Root.ToFullString());
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        var outer = Assert.IsType<MemberAccessExpressionSyntax>(tree.Root);
        var inner = Assert.IsType<MemberAccessExpressionSyntax>(outer.Expression);
        Assert.IsType<ImplicitParameterExpressionSyntax>(inner.Expression);
    }

    [Fact]
    public void AFloatLiteralIsStillAFloatLiteral() => Assert.Equal("(Literal 0.1)", Parse("0.1"));

    [Fact]
    public void ParsesSelfAndSuper() {
        Assert.Equal("(MemberAccess (Self self) . (IdentifierName x))", Parse("self.x"));
        Assert.Equal("(MemberAccess (Super super) . (IdentifierName init))", Parse("super.init"));
    }

    [Fact]
    public void InterpolatedStringHolesAreRealExpressions() {
        var tree = SyntaxTree.ParseExpression("\"a \\(x + 1) b\"");
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());

        var interpolated = Assert.IsType<InterpolatedStringExpressionSyntax>(tree.Root);
        Assert.Equal(3, interpolated.Contents.Count);

        Assert.Equal("a ", Assert.IsType<InterpolatedTextSyntax>(interpolated.Contents[0]).TextToken.Text);

        var hole = Assert.IsType<InterpolationSyntax>(interpolated.Contents[1]);
        Assert.Equal(
            "(Binary (IdentifierName x) + (Literal 1))",
            TreeDump.Dump(hole.Expression)
        );

        Assert.Equal("\"a \\(x + 1) b\"", tree.ToFullString());
    }

    [Fact]
    public void AStringWithoutHolesStaysALiteral() {
        var tree = SyntaxTree.ParseExpression("\"plain\"");
        Assert.IsType<LiteralExpressionSyntax>(tree.Root);
    }

    // ---- A dense, realistic expression ------------------------------------

    [Fact]
    public void ParsesADenseExpression() {
        const string source = "user?.address?.city ?? config.defaults[\"city\"] as? String ?? \"unknown\"";
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        Assert.Equal(SyntaxKind.BinaryExpression, tree.Root.Kind);
    }

    // ---- Metatypes, per spec/SYNTAX.md §4 ---------------------------------

    /// <remarks>
    ///     A compound type in expression position is only ever followed by
    ///     <c>.self</c>, which is what tells the parser to read a type there at
    ///     all. Everything else with the same shape stays what it was.
    /// </remarks>
    [Theory]
    [InlineData(
        "Point.self",
        "(MemberAccess (IdentifierName Point) . (IdentifierName self))"
    )]
    [InlineData(
        "Point?.self",
        "(MemberAccess (Type (OptionalType (SimpleNameType Point) ?)) . (IdentifierName self))"
    )]
    [InlineData(
        "Point[].self",
        "(MemberAccess (Type (FixedSizeArrayType (SimpleNameType Point))) . (IdentifierName self))"
    )]
    [InlineData(
        "Token[>..].self",
        "(MemberAccess (Type (GrowArrayType (SimpleNameType Token) > ..)) "
        + ". (IdentifierName self))"
    )]
    [InlineData(
        "(Int, String).self",
        "(MemberAccess (Type (TupleType (TupleTypeElement (SimpleNameType Int)) "
        + "(TupleTypeElement (SimpleNameType String)))) . (IdentifierName self))"
    )]
    public void ParsesMetatypes(string source, string expected) => Assert.Equal(expected, Parse(source));

    // ---- A collection type called, for its initialiser (#272) -------------

    [Theory]
    [InlineData("[String: Int]()", "DictionaryType")]
    [InlineData("Int[>..]()", "GrowArrayType")]
    [InlineData("Int[>..<]()", "GrowArrayType")]
    [InlineData("[Int: Point[>..]]()", "DictionaryType")]
    public void ACollectionTypeIsCalledForItsInitialiser(string source, string type) {
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        Assert.StartsWith($"(Invocation (Type ({type} ", Parse(source), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xs[0]")]
    [InlineData("p?.x")]
    [InlineData("n > 0 ? n : 0")]
    [InlineData("a.b.c")]
    [InlineData("f(x).y")]
    [InlineData("xs[0] ?? 1")]
    [InlineData("xs[8]()")]
    [InlineData("[f][0]()")]
    [InlineData("[a: b][a]")]
    [InlineData("[\"k\": 1](x)")]
    [InlineData("[weak self] (x: Int) => x")]
    public void TheMetatypeLookaheadLeavesEverythingElseAlone(string source) {
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        Assert.DoesNotContain("(Type ", TreeDump.Dump(tree), StringComparison.Ordinal);
    }
}
