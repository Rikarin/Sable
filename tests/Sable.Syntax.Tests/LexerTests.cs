using System.Text;

namespace Sable.Syntax.Tests;

public sealed class LexerTests
{
    // ---- Identifiers and keywords -----------------------------------------

    [Theory]
    [InlineData("foo")]
    [InlineData("_private")]
    [InlineData("camelCase42")]
    [InlineData("ünïcödé")]
    [InlineData("日本語")]
    public void LexesIdentifiers(string text)
    {
        var token = TestHelpers.LexSingle(text);
        Assert.Equal(SyntaxKind.IdentifierToken, token.Kind);
        Assert.Equal(text, token.Text);
    }

    [Theory]
    [InlineData("fn", SyntaxKind.FnKeyword)]
    [InlineData("let", SyntaxKind.LetKeyword)]
    [InlineData("var", SyntaxKind.VarKeyword)]
    [InlineData("class", SyntaxKind.ClassKeyword)]
    [InlineData("struct", SyntaxKind.StructKeyword)]
    [InlineData("protocol", SyntaxKind.ProtocolKeyword)]
    [InlineData("union", SyntaxKind.UnionKeyword)]
    [InlineData("region", SyntaxKind.RegionKeyword)]
    [InlineData("some", SyntaxKind.SomeKeyword)]
    [InlineData("any", SyntaxKind.AnyKeyword)]
    [InlineData("inout", SyntaxKind.InoutKeyword)]
    [InlineData("consuming", SyntaxKind.ConsumingKeyword)]
    [InlineData("borrowing", SyntaxKind.BorrowingKeyword)]
    [InlineData("self", SyntaxKind.SelfKeyword)]
    [InlineData("Self", SyntaxKind.CapitalSelfKeyword)]
    [InlineData("none", SyntaxKind.NoneKeyword)]
    [InlineData("get", SyntaxKind.GetKeyword)]
    [InlineData("willSet", SyntaxKind.WillSetKeyword)]
    [InlineData("associatedtype", SyntaxKind.AssociatedtypeKeyword)]
    public void LexesKeywords(string text, SyntaxKind expected)
    {
        var token = TestHelpers.LexSingle(text);
        Assert.Equal(expected, token.Kind);
    }

    [Fact]
    public void BareUnderscoreIsTheWildcardTokenNotAnIdentifier()
    {
        Assert.Equal(SyntaxKind.UnderscoreToken, TestHelpers.LexSingle("_").Kind);
        Assert.Equal(SyntaxKind.IdentifierToken, TestHelpers.LexSingle("_x").Kind);
        Assert.Equal(SyntaxKind.IdentifierToken, TestHelpers.LexSingle("__").Kind);
    }

    [Fact]
    public void SelfAndCapitalSelfAreDistinct()
    {
        var kinds = TestHelpers.Kinds("self Self");
        Assert.Equal([SyntaxKind.SelfKeyword, SyntaxKind.CapitalSelfKeyword], kinds);
    }

    [Fact]
    public void ContextualKeywordsAreClassifiedSeparately()
    {
        Assert.True(SyntaxFacts.IsReservedKeyword(SyntaxKind.FnKeyword));
        Assert.False(SyntaxFacts.IsContextualKeyword(SyntaxKind.FnKeyword));
        Assert.True(SyntaxFacts.IsContextualKeyword(SyntaxKind.GetKeyword));
        Assert.False(SyntaxFacts.IsReservedKeyword(SyntaxKind.GetKeyword));
    }

    [Fact]
    public void BooleanKeywordsCarryTheirValue()
    {
        Assert.Equal(true, TestHelpers.LexSingle("true").Value);
        Assert.Equal(false, TestHelpers.LexSingle("false").Value);
    }

    [Fact]
    public void EscapedIdentifierIsAnIdentifier()
    {
        var token = TestHelpers.LexSingle("`class`");
        Assert.Equal(SyntaxKind.IdentifierToken, token.Kind);
        Assert.Equal("`class`", token.Text);
        Assert.Equal("class", token.Value);
    }

    [Fact]
    public void FutureReservedWordWarns()
    {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("let macro = 1");
        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    // ---- Punctuation and operators ----------------------------------------

    [Theory]
    [InlineData("+", SyntaxKind.PlusToken)]
    [InlineData("-", SyntaxKind.MinusToken)]
    [InlineData("->", SyntaxKind.ArrowToken)]
    [InlineData("=>", SyntaxKind.FatArrowToken)]
    [InlineData("=", SyntaxKind.EqualsToken)]
    [InlineData("==", SyntaxKind.EqualsEqualsToken)]
    [InlineData("===", SyntaxKind.EqualsEqualsEqualsToken)]
    [InlineData("!=", SyntaxKind.BangEqualsToken)]
    [InlineData("!==", SyntaxKind.BangEqualsEqualsToken)]
    [InlineData("<", SyntaxKind.LessToken)]
    [InlineData("<=", SyntaxKind.LessEqualsToken)]
    [InlineData("<<", SyntaxKind.LessLessToken)]
    [InlineData("<<=", SyntaxKind.LessLessEqualsToken)]
    [InlineData(">>", SyntaxKind.GreaterGreaterToken)]
    [InlineData(">>=", SyntaxKind.GreaterGreaterEqualsToken)]
    [InlineData("??", SyntaxKind.QuestionQuestionToken)]
    [InlineData("??=", SyntaxKind.QuestionQuestionEqualsToken)]
    [InlineData("?.", SyntaxKind.QuestionDotToken)]
    [InlineData("?", SyntaxKind.QuestionToken)]
    [InlineData("..", SyntaxKind.DotDotToken)]
    [InlineData("..<", SyntaxKind.DotDotLessToken)]
    [InlineData("...", SyntaxKind.DotDotDotToken)]
    [InlineData(".", SyntaxKind.DotToken)]
    [InlineData("&&", SyntaxKind.AmpersandAmpersandToken)]
    [InlineData("&", SyntaxKind.AmpersandToken)]
    [InlineData("||", SyntaxKind.PipePipeToken)]
    [InlineData("@", SyntaxKind.AtToken)]
    [InlineData("#", SyntaxKind.HashToken)]
    [InlineData("~", SyntaxKind.TildeToken)]
    [InlineData("$", SyntaxKind.DollarToken)]
    public void LexesOperatorsWithLongestMatch(string text, SyntaxKind expected)
    {
        var token = TestHelpers.LexSingle(text);
        Assert.Equal(expected, token.Kind);
        Assert.Equal(text, token.Text);
    }

    [Fact]
    public void RangeOperatorsDoNotStealDigits()
    {
        // '1..<5' must not lex '1.' as a float.
        Assert.Equal(
            [SyntaxKind.IntegerLiteralToken, SyntaxKind.DotDotLessToken, SyntaxKind.IntegerLiteralToken],
            TestHelpers.Kinds("1..<5"));

        Assert.Equal(
            [SyntaxKind.IntegerLiteralToken, SyntaxKind.DotDotDotToken, SyntaxKind.IntegerLiteralToken],
            TestHelpers.Kinds("1...5"));
    }

    [Fact]
    public void TupleMemberAccessLexesAsDotThenInteger()
    {
        Assert.Equal(
            [SyntaxKind.IdentifierToken, SyntaxKind.DotToken, SyntaxKind.IntegerLiteralToken],
            TestHelpers.Kinds("pair.0"));
    }

    [Fact]
    public void EveryFixedTextTokenRoundTripsThroughSyntaxFacts()
    {
        foreach (SyntaxKind kind in Enum.GetValues<SyntaxKind>())
        {
            var text = SyntaxFacts.GetText(kind);
            if (text is null) continue;
            var token = TestHelpers.LexSingle(text);
            Assert.True(token.Kind == kind,
                $"'{text}' lexed as {token.Kind} but SyntaxFacts.GetText says {kind}");
        }
    }

    // ---- Trivia -----------------------------------------------------------

    [Fact]
    public void LeadingAndTrailingTriviaAttachAsRoslynDoes()
    {
        var tokens = TestHelpers.Lex("  let  \n  x");

        var let = tokens[0];
        Assert.Equal(SyntaxKind.LetKeyword, let.Kind);
        Assert.Equal(SyntaxKind.WhitespaceTrivia, Assert.Single(let.LeadingTrivia).Kind);

        // Trailing trivia ends at (and includes) the first line break.
        Assert.Equal(
            [SyntaxKind.WhitespaceTrivia, SyntaxKind.NewLineTrivia],
            let.TrailingTrivia.Select(t => t.Kind));

        var x = tokens[1];
        Assert.Equal(SyntaxKind.WhitespaceTrivia, Assert.Single(x.LeadingTrivia).Kind);
    }

    [Theory]
    [InlineData("// line", SyntaxKind.LineCommentTrivia)]
    [InlineData("/// doc", SyntaxKind.DocCommentTrivia)]
    [InlineData("//// not doc", SyntaxKind.LineCommentTrivia)]
    [InlineData("//! module doc", SyntaxKind.ModuleDocCommentTrivia)]
    [InlineData("/* block */", SyntaxKind.BlockCommentTrivia)]
    [InlineData("/** block doc */", SyntaxKind.DocCommentTrivia)]
    [InlineData("/**/", SyntaxKind.BlockCommentTrivia)]
    public void ClassifiesComments(string comment, SyntaxKind expected)
    {
        var tokens = TestHelpers.LexAll(comment + "\nx");
        var trivia = tokens[0].LeadingTrivia;
        Assert.Equal(expected, trivia[0].Kind);
    }

    [Fact]
    public void BlockCommentsNest()
    {
        var tokens = TestHelpers.LexAll("/* a /* b */ c */ x");
        var trivia = Assert.Single(tokens[0].LeadingTrivia.Where(t => t.IsComment));
        Assert.Equal("/* a /* b */ c */", trivia.Text);
        Assert.Equal(SyntaxKind.IdentifierToken, tokens[0].Kind);
    }

    [Fact]
    public void ShebangIsTriviaOnlyOnTheFirstLine()
    {
        var first = TestHelpers.LexAll("#!/usr/bin/env sable\nfn main() { }");
        Assert.Equal(SyntaxKind.ShebangTrivia, first[0].LeadingTrivia[0].Kind);

        // Not on line 1: '#' is an ordinary token.
        var later = TestHelpers.Lex("x\n#!foo");
        Assert.Contains(later, t => t.Kind == SyntaxKind.HashToken);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void RecognisesAllLineTerminators(string source)
    {
        var tokens = TestHelpers.Lex(source);
        Assert.Equal(2, tokens.Length);
        Assert.Contains(tokens[0].TrailingTrivia, t => t.Kind == SyntaxKind.NewLineTrivia);
    }

    // ---- Spans ------------------------------------------------------------

    [Fact]
    public void SpanCoversTokenTextAndFullSpanCoversTrivia()
    {
        var tokens = TestHelpers.Lex("  abc  ");
        var token = tokens[0];
        Assert.Equal(new TextSpan(2, 3), token.Span);
        Assert.Equal(new TextSpan(0, 7), token.FullSpan);
    }

    [Fact]
    public void EndOfFileTokenIsAlwaysLastAndEmpty()
    {
        var tokens = TestHelpers.LexAll("let x = 1\n");
        var eof = tokens[^1];
        Assert.Equal(SyntaxKind.EndOfFile, eof.Kind);
        Assert.Equal(string.Empty, eof.Text);
        Assert.Equal(0, eof.Width);
    }

    [Fact]
    public void ByteOrderMarkIsRejected()
    {
        var (_, diagnostics) = TestHelpers.LexWithDiagnostics("﻿let x = 1");
        Assert.Contains(diagnostics, d => d.Code == ErrorCode.ERR_ByteOrderMarkNotAllowed);
    }

    [Fact]
    public void UnexpectedCharacterProducesBadTokenAndDiagnostic()
    {
        var (tokens, diagnostics) = TestHelpers.LexWithDiagnostics("let x = §");
        Assert.Contains(tokens, t => t.Kind == SyntaxKind.BadToken);
        Assert.Contains(diagnostics, d => d.Code == ErrorCode.ERR_UnexpectedCharacter);
    }

    [Fact]
    public void DiagnosticFormatsWithCaret()
    {
        var source = new SourceText("let x = §\n", "test.sab");
        Lexer.Tokenize(source, out var diagnostics);
        var formatted = diagnostics[0].Format(source);
        Assert.Contains("S0001", formatted, StringComparison.Ordinal);
        Assert.Contains("test.sab:1:9", formatted, StringComparison.Ordinal);
        Assert.Contains("^", formatted, StringComparison.Ordinal);
    }

    // ---- A realistic declaration ------------------------------------------

    [Fact]
    public void LexesARepresentativeDeclaration()
    {
        const string source = "public fn add(_ a: Int, b: Int = 0) throws(MathError) -> Int => a + b";
        var kinds = TestHelpers.Kinds(source);

        Assert.Equal(
        [
            SyntaxKind.PublicKeyword, SyntaxKind.FnKeyword, SyntaxKind.IdentifierToken,
            SyntaxKind.OpenParenToken,
            SyntaxKind.UnderscoreToken, SyntaxKind.IdentifierToken, SyntaxKind.ColonToken,
            SyntaxKind.IdentifierToken, SyntaxKind.CommaToken,
            SyntaxKind.IdentifierToken, SyntaxKind.ColonToken, SyntaxKind.IdentifierToken,
            SyntaxKind.EqualsToken, SyntaxKind.IntegerLiteralToken,
            SyntaxKind.CloseParenToken,
            SyntaxKind.ThrowsKeyword, SyntaxKind.OpenParenToken, SyntaxKind.IdentifierToken,
            SyntaxKind.CloseParenToken,
            SyntaxKind.ArrowToken, SyntaxKind.IdentifierToken,
            SyntaxKind.FatArrowToken,
            SyntaxKind.IdentifierToken, SyntaxKind.PlusToken, SyntaxKind.IdentifierToken,
        ], kinds);
    }

    [Fact]
    public void LexesRelativeReferenceType()
    {
        Assert.Equal(
        [
            SyntaxKind.IdentifierToken, SyntaxKind.AmpersandToken, SyntaxKind.LessToken,
            SyntaxKind.IdentifierToken, SyntaxKind.InKeyword, SyntaxKind.IdentifierToken,
            SyntaxKind.GreaterToken, SyntaxKind.QuestionToken,
        ], TestHelpers.Kinds("Order&<u32 in pool>?"));
    }

    [Fact]
    public void LexesGrowOnlyArraySuffix()
    {
        Assert.Equal(
        [
            SyntaxKind.IdentifierToken, SyntaxKind.OpenBracketToken, SyntaxKind.GreaterToken,
            SyntaxKind.DotDotToken, SyntaxKind.CloseBracketToken,
        ], TestHelpers.Kinds("Line[>..]"));
    }

    [Fact]
    public void LexesVariantSizedUnionHeader()
    {
        Assert.Equal(
        [
            SyntaxKind.UnionKeyword, SyntaxKind.IdentifierToken, SyntaxKind.DotDotToken,
            SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken,
        ], TestHelpers.Kinds("union Node.. { }"));
    }

    [Fact]
    public void LexesLargeInputWithoutQuadraticBlowup()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
            builder.Append("let x").Append(i).Append(" = ").Append(i).Append(" + 1\n");

        var source = new SourceText(builder.ToString());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tokens = Lexer.Tokenize(source, out var diagnostics);
        watch.Stop();

        Assert.Empty(diagnostics);
        Assert.True(tokens.Length > 100_000);
        Assert.True(watch.ElapsedMilliseconds < 3000,
            $"lexing 20k lines took {watch.ElapsedMilliseconds} ms");
    }
}
