using System.Collections.Immutable;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>
/// A hand-written recursive-descent parser.
/// </summary>
/// <remarks>
/// <para>Hand-written rather than generated, for the reason given in
/// docs/04-implementation-plan.md §0.4: error recovery and incremental
/// reparsing both need hand control.</para>
/// <para>Error recovery never drops text. A token that cannot be used is
/// folded into <see cref="SyntaxKind.SkippedTokensTrivia"/> on the next token
/// it consumes, and a token that is required but absent is inserted as a
/// zero-width missing token. Both keep the full-fidelity round-trip
/// invariant.</para>
/// <para>This milestone parses types and expressions. Declarations,
/// statements, patterns, closures and structured string interpolation are not
/// implemented yet; an interpolated string is a single literal token whose
/// holes are available through <see cref="SyntaxToken.Parts"/>.</para>
/// </remarks>
internal sealed class Parser
{
    private readonly List<GreenToken> _tokens;
    private readonly List<int> _positions;
    private readonly DiagnosticBag _diagnostics = new();
    private int _index;

    /// <summary>Full text of tokens skipped by error recovery, not yet re-attached.</summary>
    private readonly List<GreenTrivia> _skipped = [];

    private Parser(SourceText source)
    {
        var tokens = Lexer.Tokenize(source, out var lexDiagnostics);
        _diagnostics.AddRange(lexDiagnostics);

        _tokens = new List<GreenToken>(tokens.Length);
        _positions = new List<int>(tokens.Length);
        foreach (var token in tokens)
        {
            _tokens.Add(token.Green!);
            _positions.Add(token.Position);
        }
    }

    public static SyntaxTree ParseExpression(SourceText source)
    {
        var parser = new Parser(source);
        return parser.Finish(source, parser.ParseExpression(), "expression");
    }

    public static SyntaxTree ParseType(SourceText source)
    {
        var parser = new Parser(source);
        return parser.Finish(source, parser.ParseType(), "type");
    }

    /// <summary>
    /// Consumes anything left over, so that the root plus the end-of-file token
    /// still account for every character in the file.
    /// </summary>
    private SyntaxTree Finish(SourceText source, GreenNode root, string what)
    {
        if (Current.Kind != SyntaxKind.EndOfFile)
        {
            ReportUnexpected(ErrorCode.ERR_UnexpectedTrailingTokens,
                $"unexpected '{DescribeCurrent()}' after the end of the {what}");
            while (Current.Kind != SyntaxKind.EndOfFile) SkipCurrentToken();
        }

        var eof = EatToken();
        var node = root.CreateRed(null, 0);
        return new SyntaxTree(source, node,
            new SyntaxToken(eof, null, root.FullWidth), _diagnostics.ToList());
    }

    // -----------------------------------------------------------------------
    // Token stream
    // -----------------------------------------------------------------------

    private GreenToken Current => _tokens[_index];
    private GreenToken Peek(int offset) =>
        _tokens[Math.Min(_index + offset, _tokens.Count - 1)];

    private TextSpan CurrentSpan =>
        new(_positions[_index] + Current.LeadingTriviaWidth, Current.Text.Length);

    private bool At(SyntaxKind kind) => Current.Kind == kind;

    private GreenToken EatToken()
    {
        var token = _tokens[_index];
        if (_index < _tokens.Count - 1) _index++;
        return AttachSkipped(token);
    }

    /// <summary>Consumes the expected token, or inserts a zero-width missing one.</summary>
    private GreenToken EatToken(SyntaxKind kind)
    {
        if (Current.Kind == kind) return EatToken();

        var expected = SyntaxFacts.GetText(kind) ?? kind.ToString();
        _diagnostics.Error(ErrorCode.ERR_ExpectedToken, CurrentSpan,
            $"expected '{expected}' but found '{DescribeCurrent()}'");
        return CreateMissing(kind);
    }

    private GreenToken? TryEatToken(SyntaxKind kind) =>
        Current.Kind == kind ? EatToken() : null;

    private static GreenToken CreateMissing(SyntaxKind kind) =>
        new(kind, string.Empty, [], [], isMissing: true);

    private string DescribeCurrent() =>
        Current.Kind == SyntaxKind.EndOfFile ? "end of file" : Current.Text;

    /// <summary>Moves the current token's text into the pending skipped-trivia buffer.</summary>
    private void SkipCurrentToken()
    {
        var token = _tokens[_index];
        _skipped.Add(new GreenTrivia(SyntaxKind.SkippedTokensTrivia, token.ToFullString()));
        if (_index < _tokens.Count - 1) _index++;
    }

    /// <summary>Prepends any pending skipped text to a token's leading trivia.</summary>
    private GreenToken AttachSkipped(GreenToken token)
    {
        if (_skipped.Count == 0) return token;

        var leading = ImmutableArray.CreateBuilder<GreenTrivia>(_skipped.Count + token.Leading.Length);
        leading.AddRange(_skipped);
        leading.AddRange(token.Leading);
        _skipped.Clear();
        return token.WithTrivia(leading.ToImmutable(), token.Trailing);
    }

    private void ReportUnexpected(ErrorCode code, string message) =>
        _diagnostics.Error(code, CurrentSpan, message);

    /// <summary>
    /// Splits the current token after its first character, returning the first
    /// half and leaving the remainder in the stream.
    /// </summary>
    /// <remarks>
    /// The lexer takes the longest match, which is wrong at two points in type
    /// syntax: <c>List&lt;List&lt;Int&gt;&gt;</c> ends on one <c>&gt;&gt;</c>
    /// token, and <c>Int??</c> ends on one <c>??</c>. Splitting here keeps the
    /// lexer context-free and the round-trip exact, because the two halves
    /// carry the original token's leading and trailing trivia respectively.
    /// </remarks>
    private GreenToken SplitLeadingCharacter(SyntaxKind firstKind, SyntaxKind remainderKind)
    {
        var token = _tokens[_index];
        var first = new GreenToken(firstKind, token.Text[..1], token.Leading, []);
        var remainder = new GreenToken(remainderKind, token.Text[1..], [], token.Trailing);

        _tokens[_index] = remainder;
        _positions[_index] += token.LeadingTriviaWidth + 1;
        return AttachSkipped(first);
    }

    /// <summary>Consumes a <c>&gt;</c>, splitting <c>&gt;&gt;</c> if necessary.</summary>
    private GreenToken EatGreaterThan() => Current.Kind switch
    {
        SyntaxKind.GreaterToken => EatToken(),
        SyntaxKind.GreaterGreaterToken =>
            SplitLeadingCharacter(SyntaxKind.GreaterToken, SyntaxKind.GreaterToken),
        SyntaxKind.GreaterGreaterEqualsToken =>
            SplitLeadingCharacter(SyntaxKind.GreaterToken, SyntaxKind.GreaterEqualsToken),
        _ => EatToken(SyntaxKind.GreaterToken),
    };

    // =======================================================================
    // Expressions
    // =======================================================================

    /// <summary>Parses an expression. Precedence follows spec/SYNTAX.md §16.</summary>
    public GreenNode ParseExpression() => ParseAssignment();

    private GreenNode ParseAssignment()
    {
        var left = ParseTernary();
        if (!SyntaxFacts.IsAssignmentOperator(Current.Kind)) return left;

        var op = EatToken();
        var right = ParseAssignment();           // right-associative
        return new AssignmentExpressionGreen(left, op, right);
    }

    private GreenNode ParseTernary()
    {
        var condition = ParseCoalesce();
        if (!At(SyntaxKind.QuestionToken)) return condition;

        var question = EatToken();
        var whenTrue = ParseExpression();
        var colon = EatToken(SyntaxKind.ColonToken);
        var whenFalse = ParseTernary();
        return new ConditionalExpressionGreen(condition, question, whenTrue, colon, whenFalse);
    }

    private GreenNode ParseCoalesce()
    {
        var left = ParseLogicalOr();
        if (!At(SyntaxKind.QuestionQuestionToken)) return left;

        var op = EatToken();
        var right = ParseCoalesce();             // right-associative
        return new BinaryExpressionGreen(left, op, right);
    }

    private GreenNode ParseLogicalOr() =>
        ParseLeftAssociative(ParseLogicalAnd, SyntaxKind.PipePipeToken);

    private GreenNode ParseLogicalAnd() =>
        ParseLeftAssociative(ParseBitOr, SyntaxKind.AmpersandAmpersandToken);

    private GreenNode ParseBitOr() =>
        ParseLeftAssociative(ParseBitXor, SyntaxKind.PipeToken);

    private GreenNode ParseBitXor() =>
        ParseLeftAssociative(ParseBitAnd, SyntaxKind.CaretToken);

    private GreenNode ParseBitAnd() =>
        ParseLeftAssociative(ParseEquality, SyntaxKind.AmpersandToken);

    private GreenNode ParseEquality() => ParseLeftAssociative(
        ParseRelational,
        SyntaxKind.EqualsEqualsToken, SyntaxKind.BangEqualsToken,
        SyntaxKind.EqualsEqualsEqualsToken, SyntaxKind.BangEqualsEqualsToken);

    private GreenNode ParseRelational() => ParseLeftAssociative(
        ParseRange,
        SyntaxKind.LessToken, SyntaxKind.LessEqualsToken,
        SyntaxKind.GreaterToken, SyntaxKind.GreaterEqualsToken);

    /// <summary>Ranges are non-associative and may omit either operand.</summary>
    private GreenNode ParseRange()
    {
        if (IsRangeOperator(Current.Kind))
        {
            var prefixOp = EatToken();
            var prefixRight = ParseShift();
            return new RangeExpressionGreen(null, prefixOp, prefixRight);
        }

        var left = ParseShift();
        if (!IsRangeOperator(Current.Kind)) return left;

        var op = EatToken();
        var right = CanStartExpression(Current.Kind) ? ParseShift() : null;
        return new RangeExpressionGreen(left, op, right);
    }

    private static bool IsRangeOperator(SyntaxKind kind) =>
        kind is SyntaxKind.DotDotLessToken or SyntaxKind.DotDotDotToken;

    private GreenNode ParseShift() => ParseLeftAssociative(
        ParseAdditive, SyntaxKind.LessLessToken, SyntaxKind.GreaterGreaterToken);

    private GreenNode ParseAdditive() => ParseLeftAssociative(
        ParseMultiplicative, SyntaxKind.PlusToken, SyntaxKind.MinusToken);

    private GreenNode ParseMultiplicative() => ParseLeftAssociative(
        ParseUnary, SyntaxKind.StarToken, SyntaxKind.SlashToken, SyntaxKind.PercentToken);

    private GreenNode ParseLeftAssociative(
        Func<GreenNode> operand, params SyntaxKind[] operators)
    {
        var left = operand();
        while (Array.IndexOf(operators, Current.Kind) >= 0)
        {
            var op = EatToken();
            var right = operand();
            left = new BinaryExpressionGreen(left, op, right);
        }
        return left;
    }

    private GreenNode ParseUnary()
    {
        if (At(SyntaxKind.TryKeyword))
        {
            var tryKeyword = EatToken();
            var marker = Current.Kind is SyntaxKind.QuestionToken or SyntaxKind.BangToken
                ? EatToken()
                : null;
            return new TryExpressionGreen(tryKeyword, marker, ParseUnary());
        }

        if (IsPrefixOperator(Current.Kind))
        {
            var op = EatToken();
            return new PrefixUnaryExpressionGreen(op, ParseUnary());
        }

        return ParsePostfix(ParsePrimary());
    }

    private static bool IsPrefixOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.MinusToken or SyntaxKind.PlusToken or SyntaxKind.BangToken
            or SyntaxKind.TildeToken or SyntaxKind.AmpersandToken => true,
        SyntaxKind.AwaitKeyword or SyntaxKind.MoveKeyword => true,
        _ => false,
    };

    private GreenNode ParsePostfix(GreenNode expression)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case SyntaxKind.DotToken:
                case SyntaxKind.QuestionDotToken:
                {
                    var op = EatToken();
                    var name = ParseSimpleName(memberContext: true);
                    expression = new MemberAccessExpressionGreen(expression, op, name);
                    continue;
                }

                case SyntaxKind.OpenParenToken:
                {
                    var arguments = ParseArgumentList(
                        SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken);
                    expression = new InvocationExpressionGreen(expression, null, arguments);
                    continue;
                }

                case SyntaxKind.OpenBracketToken:
                {
                    var arguments = ParseArgumentList(
                        SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken);
                    expression = new ElementAccessExpressionGreen(expression, null, arguments);
                    continue;
                }

                // '?(' and '?[' are optional-chained calls and indexes; a bare
                // '?' is left for the conditional operator.
                case SyntaxKind.QuestionToken when Peek(1).Kind == SyntaxKind.OpenParenToken:
                {
                    var question = EatToken();
                    var arguments = ParseArgumentList(
                        SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken);
                    expression = new InvocationExpressionGreen(expression, question, arguments);
                    continue;
                }

                case SyntaxKind.QuestionToken when Peek(1).Kind == SyntaxKind.OpenBracketToken:
                {
                    var question = EatToken();
                    var arguments = ParseArgumentList(
                        SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken);
                    expression = new ElementAccessExpressionGreen(expression, question, arguments);
                    continue;
                }

                case SyntaxKind.BangToken:
                {
                    var bang = EatToken();
                    expression = new PostfixUnaryExpressionGreen(expression, bang);
                    continue;
                }

                case SyntaxKind.AsKeyword:
                {
                    var asKeyword = EatToken();
                    var marker = Current.Kind is SyntaxKind.QuestionToken or SyntaxKind.BangToken
                        ? EatToken()
                        : null;
                    expression = new CastExpressionGreen(
                        expression, null, asKeyword, marker, ParseType(inExpression: true));
                    continue;
                }

                case SyntaxKind.UncheckedKeyword when Peek(1).Kind == SyntaxKind.AsKeyword:
                {
                    var unchecked_ = EatToken();
                    var asKeyword = EatToken();
                    expression = new CastExpressionGreen(
                        expression, unchecked_, asKeyword, null, ParseType(inExpression: true));
                    continue;
                }

                case SyntaxKind.IsKeyword:
                {
                    var isKeyword = EatToken();
                    expression = new TypeTestExpressionGreen(
                        expression, isKeyword, ParseType(inExpression: true));
                    continue;
                }

                default:
                    return expression;
            }
        }
    }

    private GreenNode ParseSimpleName(bool memberContext = false)
    {
        var identifier = memberContext ? EatMemberName() : EatIdentifier();
        if (At(SyntaxKind.LessToken) && TryScanTypeArgumentList())
            return new GenericNameGreen(identifier, ParseTypeArgumentList());

        return new IdentifierNameGreen(identifier);
    }

    /// <summary>
    /// A member name after '.' may be a keyword: `super.init`, `Type.self`,
    /// `x.default`. The binder decides whether the name means anything.
    /// </summary>
    private GreenToken EatMemberName()
    {
        if (Current.Kind is SyntaxKind.InitKeyword or SyntaxKind.DeinitKeyword
            or SyntaxKind.SelfKeyword or SyntaxKind.CapitalSelfKeyword
            or SyntaxKind.DefaultKeyword)
        {
            return EatToken();
        }
        return EatIdentifier();
    }

    private GreenToken EatIdentifier()
    {
        if (At(SyntaxKind.IdentifierToken)) return EatToken();

        // A contextual keyword is a perfectly good member name.
        if (Current.IsContextualKeywordToken()) return EatToken();

        _diagnostics.Error(ErrorCode.ERR_ExpectedIdentifier, CurrentSpan,
            $"expected an identifier but found '{DescribeCurrent()}'");
        return CreateMissing(SyntaxKind.IdentifierToken);
    }

    private GreenNode ParsePrimary()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.IntegerLiteralToken:
            case SyntaxKind.FloatLiteralToken:
            case SyntaxKind.CharLiteralToken:
            case SyntaxKind.StringLiteralToken:
            case SyntaxKind.MultilineStringLiteralToken:
            case SyntaxKind.RawStringLiteralToken:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NoneKeyword:
                return new LiteralExpressionGreen(EatToken());

            case SyntaxKind.SelfKeyword:
                return new SelfExpressionGreen(EatToken());

            case SyntaxKind.SuperKeyword:
                return new SuperExpressionGreen(EatToken());

            case SyntaxKind.HashToken:
            {
                var hash = EatToken();
                return new BuiltinExpressionGreen(hash, EatIdentifier());
            }

            case SyntaxKind.DotToken:
            {
                var dot = EatToken();
                return new ImplicitMemberAccessExpressionGreen(
                    dot, ParseSimpleName(memberContext: true));
            }

            case SyntaxKind.OpenParenToken:
                return ParseParenthesizedOrTuple();

            case SyntaxKind.OpenBracketToken:
                return ParseArrayOrDictionary();

            case SyntaxKind.IdentifierToken:
                return ParseIdentifierOrRecord();

            default:
                _diagnostics.Error(ErrorCode.ERR_ExpectedExpression, CurrentSpan,
                    $"expected an expression but found '{DescribeCurrent()}'");
                return new IdentifierNameGreen(CreateMissing(SyntaxKind.IdentifierToken));
        }
    }

    /// <summary>
    /// Distinguishes a record literal (<c>Point { x: 1 }</c>) from a plain name.
    /// </summary>
    /// <remarks>
    /// Decided by pure lookahead over <c>ident ('.' ident)* '{'</c>, so nothing
    /// is parsed twice and no token split has to be undone. Once statements
    /// exist this will also need a "no record literal here" context flag, for
    /// the same reason Swift needs one before a trailing closure.
    /// </remarks>
    private GreenNode ParseIdentifierOrRecord()
    {
        if (!LooksLikeRecordLiteral()) return ParseSimpleName();

        var type = ParseTypeName();
        var open = EatToken(SyntaxKind.OpenBraceToken);
        var fields = ParseSeparatedList(SyntaxKind.CloseBraceToken, ParseFieldInitializer);
        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new RecordExpressionGreen(type, open, fields, close);
    }

    private bool LooksLikeRecordLiteral()
    {
        var offset = 0;
        while (true)
        {
            if (Peek(offset).Kind != SyntaxKind.IdentifierToken) return false;
            offset++;

            if (Peek(offset).Kind == SyntaxKind.OpenBraceToken) return true;
            if (Peek(offset).Kind != SyntaxKind.DotToken) return false;
            offset++;
        }
    }

    private GreenNode ParseFieldInitializer()
    {
        var name = EatIdentifier();
        var colon = TryEatToken(SyntaxKind.ColonToken);
        var value = colon is null ? null : ParseExpression();
        return new FieldInitializerGreen(name, colon, value);
    }

    private GreenNode ParseParenthesizedOrTuple()
    {
        var open = EatToken();

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        var simple = !At(SyntaxKind.CloseParenToken);   // '()' is the empty tuple
        GreenNode? onlyInner = null;

        if (!At(SyntaxKind.CloseParenToken))
        {
            while (true)
            {
                var element = ParseTupleElement();
                if (element.Labelled) simple = false;
                onlyInner ??= element.Inner;
                items.Add(element.Node);

                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) break;
                items.Add(comma);
                simple = false;
                if (At(SyntaxKind.CloseParenToken)) break;
            }
        }

        var close = EatToken(SyntaxKind.CloseParenToken);

        if (simple && items.Count == 1 && onlyInner is not null)
            return new ParenthesizedExpressionGreen(open, onlyInner, close);

        return new TupleExpressionGreen(open, GreenList.Create(items.ToImmutable()), close);
    }

    private (GreenNode Node, bool Labelled, GreenNode Inner) ParseTupleElement()
    {
        GreenToken? name = null;
        GreenToken? colon = null;

        if (IsArgumentLabel())
        {
            name = EatToken();
            colon = EatToken(SyntaxKind.ColonToken);
        }

        var expression = ParseExpression();
        return (new TupleElementGreen(name, colon, expression), name is not null, expression);
    }

    /// <summary>True when the current token starts a <c>label:</c> prefix.</summary>
    private bool IsArgumentLabel() =>
        (Current.Kind == SyntaxKind.IdentifierToken || Current.IsContextualKeywordToken())
        && Peek(1).Kind == SyntaxKind.ColonToken;

    private GreenNode ParseArrayOrDictionary()
    {
        var open = EatToken();

        // '[:]' is the empty dictionary.
        if (At(SyntaxKind.ColonToken) && Peek(1).Kind == SyntaxKind.CloseBracketToken)
        {
            var emptyColon = EatToken();
            return new DictionaryExpressionGreen(
                open, emptyColon, GreenList.Empty, EatToken(SyntaxKind.CloseBracketToken));
        }

        if (At(SyntaxKind.CloseBracketToken))
        {
            return new ArrayExpressionGreen(
                open, GreenList.Empty, EatToken(SyntaxKind.CloseBracketToken));
        }

        var first = ParseExpression();

        if (At(SyntaxKind.ColonToken))
        {
            var colon = EatToken();
            var value = ParseExpression();

            var entries = ImmutableArray.CreateBuilder<GreenNode>();
            entries.Add(new KeyValuePairGreen(first, colon, value));

            while (TryEatToken(SyntaxKind.CommaToken) is { } comma)
            {
                entries.Add(comma);
                if (At(SyntaxKind.CloseBracketToken)) break;
                entries.Add(ParseKeyValuePair());
            }

            return new DictionaryExpressionGreen(
                open, null, GreenList.Create(entries.ToImmutable()),
                EatToken(SyntaxKind.CloseBracketToken));
        }

        var elements = ImmutableArray.CreateBuilder<GreenNode>();
        elements.Add(first);
        while (TryEatToken(SyntaxKind.CommaToken) is { } comma)
        {
            elements.Add(comma);
            if (At(SyntaxKind.CloseBracketToken)) break;
            elements.Add(ParseExpression());
        }

        return new ArrayExpressionGreen(
            open, GreenList.Create(elements.ToImmutable()),
            EatToken(SyntaxKind.CloseBracketToken));
    }

    private GreenNode ParseKeyValuePair()
    {
        var key = ParseExpression();
        var colon = EatToken(SyntaxKind.ColonToken);
        return new KeyValuePairGreen(key, colon, ParseExpression());
    }

    private GreenNode ParseArgumentList(SyntaxKind openKind, SyntaxKind closeKind)
    {
        var open = EatToken(openKind);
        var arguments = ParseSeparatedList(closeKind, ParseArgument);
        var close = EatToken(closeKind);
        return new ArgumentListGreen(open, arguments, close);
    }

    private GreenNode ParseArgument()
    {
        GreenToken? name = null;
        GreenToken? colon = null;

        if (IsArgumentLabel())
        {
            name = EatToken();
            colon = EatToken(SyntaxKind.ColonToken);
        }

        var modifier = Current.Kind is SyntaxKind.AmpersandToken
            or SyntaxKind.OutKeyword or SyntaxKind.MoveKeyword
            ? EatToken()
            : null;

        return new ArgumentGreen(name, colon, modifier, ParseExpression());
    }

    /// <summary>
    /// Parses a comma-separated list terminated by <paramref name="closeKind"/>,
    /// allowing a trailing separator.
    /// </summary>
    private GreenList ParseSeparatedList(SyntaxKind closeKind, Func<GreenNode> parseItem)
    {
        if (At(closeKind)) return GreenList.Empty;

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (true)
        {
            items.Add(parseItem());

            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) break;

            items.Add(comma);
            if (At(closeKind)) break;

            // Nothing consumed and nothing to consume: bail rather than spin.
            if (At(SyntaxKind.EndOfFile)) break;
        }
        return GreenList.Create(items.ToImmutable());
    }

    private static bool CanStartExpression(SyntaxKind kind) => kind switch
    {
        SyntaxKind.IdentifierToken => true,
        SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken
            or SyntaxKind.CharLiteralToken or SyntaxKind.StringLiteralToken
            or SyntaxKind.MultilineStringLiteralToken
            or SyntaxKind.RawStringLiteralToken => true,
        SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NoneKeyword => true,
        SyntaxKind.SelfKeyword or SyntaxKind.SuperKeyword => true,
        SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken => true,
        SyntaxKind.DotToken or SyntaxKind.HashToken => true,
        SyntaxKind.MinusToken or SyntaxKind.PlusToken or SyntaxKind.BangToken
            or SyntaxKind.TildeToken or SyntaxKind.AmpersandToken => true,
        SyntaxKind.AwaitKeyword or SyntaxKind.MoveKeyword or SyntaxKind.TryKeyword => true,
        _ => false,
    };

    // =======================================================================
    // Types
    // =======================================================================

    /// <summary>Parses a type. Structure follows spec/grammar.ebnf §4.</summary>
    /// <param name="inExpression">
    /// True for the type after <c>as</c> or <c>is</c>, where a spaced <c>?</c>
    /// belongs to a conditional expression rather than to the type. See
    /// <see cref="IsOptionalSuffix"/>.
    /// </param>
    public GreenNode ParseType(bool inExpression = false)
    {
        var left = ParseIntersectionType(inExpression);
        while (At(SyntaxKind.PipeToken))
        {
            var pipe = EatToken();
            left = new UnionTypeGreen(left, pipe, ParseIntersectionType(inExpression));
        }
        return left;
    }

    /// <summary>
    /// Decides whether a <c>?</c> or <c>??</c> continues the type or starts an
    /// operator.
    /// </summary>
    /// <remarks>
    /// <para>The ambiguity is real: in <c>x as? T ?? fallback</c> the <c>??</c>
    /// is the coalescing operator, while in <c>Int??</c> it is two optional
    /// markers. Swift resolves the same case by whitespace and so does
    /// Sable — a question mark that continues a type is written tight against
    /// it.</para>
    /// <para>A <c>??</c> always requires no preceding space, because a type
    /// is never followed by a spaced <c>??</c> that belongs to it. A single
    /// <c>?</c> only requires it inside an expression, so that declarations
    /// may still be written <c>var x: Int ?</c>.</para>
    /// </remarks>
    private bool IsOptionalSuffix(bool inExpression) => Current.Kind switch
    {
        SyntaxKind.QuestionToken => !inExpression || !HasSpaceBeforeCurrent,
        SyntaxKind.QuestionQuestionToken => !HasSpaceBeforeCurrent,
        _ => false,
    };

    /// <summary>
    /// True when trivia separates the current token from the previous one.
    /// </summary>
    /// <remarks>
    /// Trailing trivia attaches to the preceding token, so the space in
    /// <c>T ??</c> lives on <c>T</c>, not on the <c>??</c>. Both sides have to
    /// be checked.
    /// </remarks>
    private bool HasSpaceBeforeCurrent =>
        Current.LeadingTriviaWidth > 0
        || (_index > 0 && _tokens[_index - 1].TrailingTriviaWidth > 0);

    private GreenNode ParseIntersectionType(bool inExpression = false)
    {
        var left = ParsePostfixType(inExpression);

        // A '&' that is followed by something which can start a type is protocol
        // composition; otherwise ParsePostfixType already took it as a reference
        // suffix, so reaching here means composition.
        while (At(SyntaxKind.AmpersandToken) && CanStartType(Peek(1).Kind))
        {
            var ampersand = EatToken();
            left = new CompositionTypeGreen(left, ampersand, ParsePostfixType(inExpression));
        }
        return left;
    }

    private GreenNode ParsePostfixType(bool inExpression = false)
    {
        var type = ParsePrimaryType();

        while (true)
        {
            switch (Current.Kind)
            {
                case SyntaxKind.QuestionToken when IsOptionalSuffix(inExpression):
                    type = new OptionalTypeGreen(type, EatToken());
                    continue;

                // 'Int??' is an optional of an optional; the lexer gave us one
                // token for the two question marks.
                case SyntaxKind.QuestionQuestionToken when IsOptionalSuffix(inExpression):
                    type = new OptionalTypeGreen(type, SplitLeadingCharacter(
                        SyntaxKind.QuestionToken, SyntaxKind.QuestionToken));
                    continue;

                case SyntaxKind.BangToken:
                    type = new ImplicitlyUnwrappedTypeGreen(type, EatToken());
                    continue;

                // '&' is a reference suffix unless a type follows, in which case
                // it is protocol composition and belongs to the caller.
                case SyntaxKind.AmpersandToken when !CanStartType(Peek(1).Kind):
                {
                    var ampersand = EatToken();
                    var spec = At(SyntaxKind.LessToken) ? ParseRelativeReferenceSpec() : null;
                    var question = TryEatToken(SyntaxKind.QuestionToken);
                    type = new ReferenceTypeGreen(type, ampersand, spec, question);
                    continue;
                }

                case SyntaxKind.OpenBracketToken:
                    type = ParseArraySuffix(type);
                    continue;

                case SyntaxKind.DotToken:
                {
                    var dot = EatToken();
                    type = new QualifiedNameGreen(type, dot, ParseSimpleNameType());
                    continue;
                }

                default:
                    return type;
            }
        }
    }

    private GreenNode ParseRelativeReferenceSpec()
    {
        var less = EatToken(SyntaxKind.LessToken);
        var width = EatIdentifier();
        var inKeyword = TryEatToken(SyntaxKind.InKeyword);
        var pool = inKeyword is null ? null : EatIdentifier();
        var greater = EatGreaterThan();
        return new RelativeReferenceSpecGreen(less, width, inKeyword, pool, greater);
    }

    private GreenNode ParseArraySuffix(GreenNode element)
    {
        var open = EatToken(SyntaxKind.OpenBracketToken);

        // T[]
        if (At(SyntaxKind.CloseBracketToken))
            return new FixedSizeArrayTypeGreen(element, open, null, EatToken());

        // T[:]
        if (At(SyntaxKind.ColonToken))
        {
            var colon = EatToken();
            return new SliceTypeGreen(element, open, colon,
                EatToken(SyntaxKind.CloseBracketToken));
        }

        // T[..] and T[..k]
        if (At(SyntaxKind.DotDotToken))
        {
            var dotDot = EatToken();
            var capacity = At(SyntaxKind.CloseBracketToken) ? null : ParseExpression();
            return new InlineArrayTypeGreen(element, open, dotDot, capacity,
                EatToken(SyntaxKind.CloseBracketToken));
        }

        // T[>..] and T[>..<] — the shrink form lexes '..<' as one token.
        if (At(SyntaxKind.GreaterToken))
        {
            var greater = EatToken();
            var range = Current.Kind is SyntaxKind.DotDotToken or SyntaxKind.DotDotLessToken
                ? EatToken()
                : EatToken(SyntaxKind.DotDotToken);
            return new GrowArrayTypeGreen(element, open, greater, range,
                EatToken(SyntaxKind.CloseBracketToken));
        }

        // T[k]
        var size = ParseExpression();
        return new FixedSizeArrayTypeGreen(element, open, size,
            EatToken(SyntaxKind.CloseBracketToken));
    }

    private GreenNode ParsePrimaryType()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.CapitalSelfKeyword:
                return new SelfTypeGreen(EatToken());

            case SyntaxKind.SomeKeyword:
            {
                var someKeyword = EatToken();
                return new OpaqueTypeGreen(someKeyword, ParseIntersectionType());
            }

            case SyntaxKind.AnyKeyword:
            {
                var anyKeyword = EatToken();
                return new ExistentialTypeGreen(anyKeyword, ParseIntersectionType());
            }

            case SyntaxKind.RefKeyword:
                return new RefTypeGreen(EatToken(), ParsePostfixType());

            case SyntaxKind.AsyncKeyword:
            {
                var asyncKeyword = EatToken();
                return ParseFunctionTypeTail(asyncKeyword);
            }

            case SyntaxKind.OpenParenToken:
                return ParseParenthesizedOrTupleOrFunctionType(null);

            case SyntaxKind.OpenBracketToken:
                return ParseDictionaryType();

            default:
                return ParseSimpleNameType();
        }
    }

    private GreenNode ParseFunctionTypeTail(GreenToken? asyncKeyword)
    {
        var group = ParseParenGroupType();
        var throwsSpec = ParseThrowsSpecOrNull();
        var arrow = EatToken(SyntaxKind.ArrowToken);
        return new FunctionTypeGreen(asyncKeyword, group.AsTuple(), throwsSpec, arrow, ParseType());
    }

    /// <summary>
    /// A parenthesised group is a function parameter list, a tuple type, or
    /// just a parenthesised type. The decision needs the token after the
    /// closing paren, so the group is parsed once and shaped afterwards.
    /// </summary>
    private GreenNode ParseParenthesizedOrTupleOrFunctionType(GreenToken? asyncKeyword)
    {
        var group = ParseParenGroupType();

        if (At(SyntaxKind.ArrowToken) || At(SyntaxKind.ThrowsKeyword)
            || At(SyntaxKind.RethrowsKeyword))
        {
            var throwsSpec = ParseThrowsSpecOrNull();
            var arrow = EatToken(SyntaxKind.ArrowToken);
            return new FunctionTypeGreen(
                asyncKeyword, group.AsTuple(), throwsSpec, arrow, ParseType());
        }

        return group.Simple && group.Inner is not null
            ? new ParenthesizedTypeGreen(group.Open, group.Inner, group.Close)
            : group.AsTuple();
    }

    private readonly record struct ParenGroupType(
        GreenToken Open, GreenList Elements, GreenToken Close, bool Simple, GreenNode? Inner)
    {
        public GreenNode AsTuple() => new TupleTypeGreen(Open, Elements, Close);
    }

    private ParenGroupType ParseParenGroupType()
    {
        var open = EatToken(SyntaxKind.OpenParenToken);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        var simple = !At(SyntaxKind.CloseParenToken);
        GreenNode? inner = null;

        if (!At(SyntaxKind.CloseParenToken))
        {
            while (true)
            {
                GreenToken? name = null;
                GreenToken? colon = null;
                if (IsArgumentLabel())
                {
                    name = EatToken();
                    colon = EatToken(SyntaxKind.ColonToken);
                    simple = false;
                }

                var type = ParseType();
                inner ??= type;
                items.Add(new TupleTypeElementGreen(name, colon, type));

                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) break;
                items.Add(comma);
                simple = false;
                if (At(SyntaxKind.CloseParenToken) || At(SyntaxKind.EndOfFile)) break;
            }
        }

        var close = EatToken(SyntaxKind.CloseParenToken);
        return new ParenGroupType(
            open, GreenList.Create(items.ToImmutable()), close, simple, inner);
    }

    private GreenNode? ParseThrowsSpecOrNull()
    {
        if (At(SyntaxKind.RethrowsKeyword))
            return new ThrowsSpecGreen(EatToken(), null, null, null);

        if (!At(SyntaxKind.ThrowsKeyword)) return null;

        var throwsKeyword = EatToken();
        if (!At(SyntaxKind.OpenParenToken))
            return new ThrowsSpecGreen(throwsKeyword, null, null, null);

        var open = EatToken();
        var errorType = ParseType();
        var close = EatToken(SyntaxKind.CloseParenToken);
        return new ThrowsSpecGreen(throwsKeyword, open, errorType, close);
    }

    private GreenNode ParseDictionaryType()
    {
        var open = EatToken(SyntaxKind.OpenBracketToken);
        var key = ParseType();
        var colon = EatToken(SyntaxKind.ColonToken);
        var value = ParseType();
        var close = EatToken(SyntaxKind.CloseBracketToken);
        return new DictionaryTypeGreen(open, key, colon, value, close);
    }

    private GreenNode ParseSimpleNameType()
    {
        var identifier = EatIdentifier();
        var arguments = At(SyntaxKind.LessToken) ? ParseTypeArgumentList() : null;
        return new SimpleNameTypeGreen(identifier, arguments);
    }

    /// <summary>Parses a dotted type name without any suffixes.</summary>
    private GreenNode ParseTypeName()
    {
        GreenNode type = ParseSimpleNameType();
        while (At(SyntaxKind.DotToken) && Peek(1).Kind == SyntaxKind.IdentifierToken)
        {
            var dot = EatToken();
            type = new QualifiedNameGreen(type, dot, ParseSimpleNameType());
        }
        return type;
    }

    private GreenNode ParseTypeArgumentList()
    {
        var less = EatToken(SyntaxKind.LessToken);
        var arguments = ImmutableArray.CreateBuilder<GreenNode>();

        if (!IsTypeArgumentListEnd())
        {
            while (true)
            {
                arguments.Add(ParseType());
                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) break;
                arguments.Add(comma);
                if (IsTypeArgumentListEnd() || At(SyntaxKind.EndOfFile)) break;
            }
        }

        var greater = EatGreaterThan();
        return new TypeArgumentListGreen(
            less, GreenList.Create(arguments.ToImmutable()), greater);
    }

    private bool IsTypeArgumentListEnd() =>
        Current.Kind is SyntaxKind.GreaterToken
            or SyntaxKind.GreaterGreaterToken
            or SyntaxKind.GreaterGreaterEqualsToken;

    private static bool CanStartType(SyntaxKind kind) => kind switch
    {
        SyntaxKind.IdentifierToken => true,
        SyntaxKind.CapitalSelfKeyword => true,
        SyntaxKind.SomeKeyword or SyntaxKind.AnyKeyword or SyntaxKind.RefKeyword => true,
        SyntaxKind.AsyncKeyword => true,
        SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken => true,
        _ => false,
    };

    /// <summary>
    /// Decides whether a <c>&lt;</c> in expression position opens a type
    /// argument list or is a comparison, by scanning for a balanced
    /// <c>&gt;</c> followed by a token that may follow a generic name. This is
    /// the C# rule, and it has the same pathological cases.
    /// </summary>
    private bool TryScanTypeArgumentList()
    {
        var start = _index;
        var depth = 0;

        try
        {
            while (true)
            {
                var kind = _tokens[_index].Kind;

                switch (kind)
                {
                    case SyntaxKind.LessToken:
                        depth++;
                        break;

                    case SyntaxKind.GreaterToken:
                        depth--;
                        if (depth == 0) return IsAfterTypeArgumentList(Peek(1).Kind);
                        break;

                    case SyntaxKind.GreaterGreaterToken:
                        depth -= 2;
                        if (depth <= 0) return IsAfterTypeArgumentList(Peek(1).Kind);
                        break;

                    case SyntaxKind.IdentifierToken:
                    case SyntaxKind.DotToken:
                    case SyntaxKind.CommaToken:
                    case SyntaxKind.QuestionToken:
                    case SyntaxKind.AmpersandToken:
                    case SyntaxKind.PipeToken:
                    case SyntaxKind.OpenBracketToken:
                    case SyntaxKind.CloseBracketToken:
                    case SyntaxKind.ColonToken:
                    case SyntaxKind.SomeKeyword:
                    case SyntaxKind.AnyKeyword:
                    case SyntaxKind.RefKeyword:
                    case SyntaxKind.CapitalSelfKeyword:
                        break;

                    default:
                        return false;   // not plausible as a type argument list
                }

                if (_index >= _tokens.Count - 1) return false;
                _index++;
            }
        }
        finally
        {
            _index = start;
        }
    }

    private static bool IsAfterTypeArgumentList(SyntaxKind kind) => kind switch
    {
        SyntaxKind.OpenParenToken => true,
        SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken
            or SyntaxKind.CloseBraceToken => true,
        SyntaxKind.CommaToken or SyntaxKind.SemicolonToken or SyntaxKind.ColonToken => true,
        SyntaxKind.DotToken => true,
        SyntaxKind.EndOfFile => true,
        _ => false,
    };
}

internal static class ParserExtensions
{
    public static bool IsContextualKeywordToken(this GreenToken token) =>
        SyntaxFacts.IsContextualKeyword(token.Kind);
}
