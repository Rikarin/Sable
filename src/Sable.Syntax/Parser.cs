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
    private readonly ImmutableArray<GreenToken> _tokens;

    /// <summary>
    /// Tokens the parser split in place, such as the <c>&gt;&gt;</c> that
    /// closes two generic argument lists. Splitting is rare, so an overlay
    /// costs far less than copying the whole stream to make it mutable.
    /// </summary>
    private Dictionary<int, GreenToken>? _split;

    /// <summary>Absolute offset of the token at <see cref="_index"/>.</summary>
    private int _tokenPosition;
    private readonly ParseOptions _options;
    private readonly IncrementalReuse? _reuse;
    private readonly DiagnosticBag _diagnostics = new();
    private int _index;

    /// <summary>Full text of tokens skipped by error recovery, not yet re-attached.</summary>
    private readonly List<GreenTrivia> _skipped = [];

    /// <summary>
    /// True while parsing an expression whose end is marked by '{' — an `if`
    /// condition, a `for` sequence, a `match` subject. In that position a brace
    /// opens the statement's body, so it may not be read as a record literal or
    /// a trailing closure. Parentheses and brackets clear it again.
    /// </summary>
    private bool _noBrace;

    /// <summary>
    /// True while parsing an expression that <c>=&gt;</c> terminates — a match
    /// arm's <c>where</c> clause. There a trailing <c>ident =&gt;</c> is the arm
    /// separator, not a one-parameter closure.
    /// </summary>
    private bool _noArrow;

    /// <summary>Inside brackets both suppressions lift again.</summary>
    private T WithBracesAllowed<T>(Func<T> parse)
    {
        var savedBrace = _noBrace;
        var savedArrow = _noArrow;
        _noBrace = false;
        _noArrow = false;
        try { return parse(); }
        finally { _noBrace = savedBrace; _noArrow = savedArrow; }
    }

    private T WithArrowSuppressed<T>(Func<T> parse)
    {
        var savedBrace = _noBrace;
        var savedArrow = _noArrow;
        _noBrace = true;
        _noArrow = true;
        try { return parse(); }
        finally { _noBrace = savedBrace; _noArrow = savedArrow; }
    }

    private T WithBracesSuppressed<T>(Func<T> parse)
    {
        var saved = _noBrace;
        _noBrace = true;
        try { return parse(); }
        finally { _noBrace = saved; }
    }

    private Parser(SourceText source, ParseOptions options)
        : this(options,
               Lexer.TokenizeGreen(source, options, out var lexDiagnostics, out _),
               lexDiagnostics,
               reuse: null)
    {
    }

    private Parser(
        ParseOptions options,
        ImmutableArray<GreenToken> tokens,
        IReadOnlyList<Diagnostic> diagnostics,
        IncrementalReuse? reuse)
    {
        _options = options;
        _reuse = reuse;
        _tokens = tokens;
        _diagnostics.AddRange(diagnostics);
    }

    private GreenToken TokenAt(int index)
    {
        if (_split is not null && _split.TryGetValue(index, out var replaced)) return replaced;
        return _tokens[index];
    }

    public static SyntaxTree ParseExpression(SourceText source, ParseOptions options)
    {
        var parser = new Parser(source, options);
        return parser.Finish(source, parser.ParseExpression(), "expression");
    }

    public static SyntaxTree ParseType(SourceText source, ParseOptions options)
    {
        var parser = new Parser(source, options);
        return parser.Finish(source, parser.ParseType(), "type");
    }

    public static SyntaxTree ParseStatement(SourceText source, ParseOptions options)
    {
        var parser = new Parser(source, options);
        return parser.Finish(source, parser.ParseStatement(), "statement");
    }

    public static SyntaxTree ParseCompilationUnit(SourceText source, ParseOptions options)
    {
        var tokens = Lexer.TokenizeGreen(
            source, options, out var diagnostics, out var hasDirectives);

        return ParseCompilationUnitFrom(
            source, options, tokens, diagnostics, hasDirectives, reuse: null);
    }

    /// <summary>Parses an already-lexed token stream, optionally reusing old nodes.</summary>
    internal static SyntaxTree ParseCompilationUnitFrom(
        SourceText source,
        ParseOptions options,
        ImmutableArray<GreenToken> tokens,
        IReadOnlyList<Diagnostic> diagnostics,
        bool hasDirectives,
        IncrementalReuse? reuse)
    {
        var parser = new Parser(options, tokens, diagnostics, reuse);
        var unit = parser.ParseCompilationUnit();

        return new SyntaxTree(source, unit.CreateRed(null, 0), default,
            parser._diagnostics.ToList(), options, tokens, hasDirectives);
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
        return new SyntaxTree(source, node, new SyntaxToken(eof, null, root.FullWidth),
            _diagnostics.ToList(), _options);
    }

    // -----------------------------------------------------------------------
    // Token stream
    // -----------------------------------------------------------------------

    private GreenToken Current => TokenAt(_index);
    private GreenToken Peek(int offset) =>
        TokenAt(Math.Min(_index + offset, _tokens.Length - 1));

    private TextSpan CurrentSpan =>
        new(_tokenPosition + Current.LeadingTriviaWidth, Current.Text.Length);

    private bool At(SyntaxKind kind) => Current.Kind == kind;

    private GreenToken EatToken()
    {
        var token = TokenAt(_index);
        if (_index < _tokens.Length - 1)
        {
            _index++;
            _tokenPosition += token.FullWidth;
        }
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
        var token = TokenAt(_index);
        _skipped.Add(new GreenTrivia(SyntaxKind.SkippedTokensTrivia, token.ToFullString()));
        if (_index < _tokens.Length - 1)
        {
            _index++;
            _tokenPosition += token.FullWidth;
        }
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
        var token = TokenAt(_index);
        var first = new GreenToken(firstKind, token.Text[..1], token.Leading, []);
        var remainder = new GreenToken(remainderKind, token.Text[1..], [], token.Trailing);

        (_split ??= [])[_index] = remainder;
        _tokenPosition += token.LeadingTriviaWidth + 1;
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
        // 'List<Int>=x' with no spaces lexes the close bracket into '>='.
        SyntaxKind.GreaterEqualsToken =>
            SplitLeadingCharacter(SyntaxKind.GreaterToken, SyntaxKind.EqualsToken),
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
            // 'try' covers the whole expression it is written on, as in
            // Swift: one 'try' can stand for several calls. It stops short of
            // assignment, so 'x = try f()' still assigns.
            var operand = ParseTernary();

            GreenToken? catchKeyword = null;
            GreenNode? catchBody = null;
            if (At(SyntaxKind.CatchKeyword))
            {
                catchKeyword = EatToken();
                catchBody = At(SyntaxKind.OpenBraceToken) ? ParseBlock() : ParseExpression();
            }

            return new TryExpressionGreen(
                tryKeyword, marker, operand, catchKeyword, catchBody);
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

                    // 'pair.0.1' lexes '0.1' as a float literal, because by
                    // spec/lexical.md §6 that is what it is. Here it is two
                    // indices, so the token is taken apart.
                    if (IsTupleIndexPath(Current)) 
                    {
                        expression = ParseTupleIndexPath(expression, op);
                        continue;
                    }

                    var name = ParseSimpleName(memberContext: true);
                    expression = new MemberAccessExpressionGreen(expression, op, name);
                    continue;
                }

                case SyntaxKind.OpenParenToken:
                {
                    var arguments = ParseArgumentList(
                        SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken);
                    var trailing = TryParseTrailingClosure();
                    expression = new InvocationExpressionGreen(
                        expression, null, arguments, trailing);
                    continue;
                }

                // 'items.filter { ... }' is a call whose only argument is the
                // closure; it has no parenthesised argument list.
                case SyntaxKind.OpenBraceToken when CanTakeTrailingClosure():
                {
                    var closure = ParseBraceClosure(null);
                    expression = new InvocationExpressionGreen(expression, null, null, closure);
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
                    expression = new InvocationExpressionGreen(
                        expression, question, arguments, TryParseTrailingClosure());
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
    /// A member name after '.' may be any keyword: `super.init`, `Type.self`,
    /// `File.open`, `x.default`. The binder decides whether the name means
    /// anything; the parser only has to not reject it.
    /// </summary>
    private GreenToken EatMemberName()
    {
        // 'pair.0' names a tuple element by position; the token is the index.
        if (Current.Kind == SyntaxKind.IntegerLiteralToken) return EatToken();

        return Current.IsKeywordToken() ? EatToken() : EatIdentifier();
    }

    /// <summary>True for a float literal that is really a path of indices.</summary>
    private static bool IsTupleIndexPath(GreenToken token)
    {
        if (token.Kind != SyntaxKind.FloatLiteralToken) return false;

        var text = token.Text;
        var sawDigit = false;

        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c)) { sawDigit = true; continue; }
            if (c == '.' && sawDigit) continue;
            return false;
        }

        return text.Contains('.', StringComparison.Ordinal) && !text.EndsWith('.');
    }

    /// <summary>
    /// Turns <c>.0.1</c> into two member accesses, keeping the text exactly.
    /// </summary>
    /// <remarks>
    /// The float token's leading trivia goes on the first index and its
    /// trailing trivia on the last, with zero-width dots between, so
    /// concatenating the tree still reproduces the source.
    /// </remarks>
    private GreenNode ParseTupleIndexPath(GreenNode expression, GreenToken firstDot)
    {
        var token = EatToken();
        var parts = token.Text.Split('.');

        var dot = firstDot;
        for (var i = 0; i < parts.Length; i++)
        {
            var leading = i == 0 ? token.Leading : [];
            var trailing = i == parts.Length - 1 ? token.Trailing : [];

            var index = new GreenToken(
                SyntaxKind.IntegerLiteralToken, parts[i], leading, trailing);

            expression = new MemberAccessExpressionGreen(
                expression, dot, new IdentifierNameGreen(index));

            if (i + 1 < parts.Length)
                dot = new GreenToken(SyntaxKind.DotToken, ".", [], []);
        }

        return expression;
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
            case SyntaxKind.StringLiteralToken when HasInterpolation(Current):
            case SyntaxKind.MultilineStringLiteralToken when HasInterpolation(Current):
                return ParseInterpolatedString();

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

            // 'self' and 'Self' both stand for a value here; the token says
            // which, and 'Self.self' is how a metatype is written.
            case SyntaxKind.SelfKeyword:
            case SyntaxKind.CapitalSelfKeyword:
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

            case SyntaxKind.MatchKeyword:
                return ParseMatchExpression();

            case SyntaxKind.OpenBraceToken:
                return ParseBraceClosure(null);

            case SyntaxKind.DollarToken:
            {
                var dollar = EatToken();
                return new ImplicitParameterExpressionGreen(
                    dollar, EatToken(SyntaxKind.IntegerLiteralToken));
            }

            case SyntaxKind.OpenBracketToken when LooksLikeCaptureList():
                return ParseCaptureListClosure();

            case SyntaxKind.OpenParenToken when !_noArrow && LooksLikeArrowClosure():
                return ParseArrowClosure(null);

            case SyntaxKind.IdentifierToken
                when !_noArrow && Peek(1).Kind == SyntaxKind.FatArrowToken:
                return ParseArrowClosure(null);

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

    /// <summary>
    /// True when <c>ident ('.' ident)* '{'</c> is followed by something only a
    /// record literal can contain.
    /// </summary>
    /// <remarks>
    /// <c>Point { x: 1 }</c> and <c>items.filter { $0.isActive }</c> have the
    /// same shape up to the brace, so the contents decide: a record literal is
    /// empty or starts <c>ident :</c>. That is why record literals have no
    /// field shorthand — <c>Point { x }</c> would be indistinguishable from a
    /// closure returning <c>x</c>.
    /// </remarks>
    private bool LooksLikeRecordLiteral()
    {
        if (_noBrace) return false;

        var offset = 0;
        while (true)
        {
            if (Peek(offset).Kind != SyntaxKind.IdentifierToken) return false;
            offset++;

            // 'Box<Int> { ... }' names a constructed type.
            if (Peek(offset).Kind == SyntaxKind.LessToken)
            {
                var skipped = SkipBalancedAngles(offset);
                if (skipped < 0) return false;
                offset = skipped;
            }

            if (Peek(offset).Kind == SyntaxKind.OpenBraceToken)
            {
                return Peek(offset + 1).Kind == SyntaxKind.CloseBraceToken
                    || (Peek(offset + 1).Kind == SyntaxKind.IdentifierToken
                        && Peek(offset + 2).Kind == SyntaxKind.ColonToken);
            }

            if (Peek(offset).Kind != SyntaxKind.DotToken) return false;
            offset++;
        }
    }

    /// <summary>
    /// Returns the offset just past a balanced angle-bracket group, or -1 when
    /// it is not balanced within a reasonable distance.
    /// </summary>
    private int SkipBalancedAngles(int offset)
    {
        var depth = 0;
        for (var i = offset; i < offset + 64; i++)
        {
            switch (Peek(i).Kind)
            {
                case SyntaxKind.LessToken: depth++; break;
                case SyntaxKind.GreaterToken: depth--; break;
                case SyntaxKind.GreaterGreaterToken: depth -= 2; break;
                case SyntaxKind.EndOfFile:
                case SyntaxKind.OpenBraceToken:
                case SyntaxKind.SemicolonToken:
                    return -1;
            }

            if (depth <= 0) return i + 1;
        }
        return -1;
    }

    private GreenNode ParseFieldInitializer()
    {
        var name = EatIdentifier();
        var colon = EatToken(SyntaxKind.ColonToken);
        return new FieldInitializerGreen(name, colon, ParseExpression());
    }

    private GreenNode ParseParenthesizedOrTuple() =>
        WithBracesAllowed(ParseParenthesizedOrTupleCore);

    private GreenNode ParseParenthesizedOrTupleCore()
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

    /// <summary>
    /// True when the current token starts a <c>label:</c> prefix. Keywords are
    /// allowed, so <c>parseExpr(tokens, in: arena)</c> reads as intended.
    /// </summary>
    private bool IsArgumentLabel() =>
        (Current.Kind == SyntaxKind.IdentifierToken || Current.IsKeywordToken())
        && Peek(1).Kind == SyntaxKind.ColonToken;

    private GreenNode ParseArrayOrDictionary() =>
        WithBracesAllowed(ParseArrayOrDictionaryCore);

    private GreenNode ParseArrayOrDictionaryCore()
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

    private GreenNode ParseArgumentList(SyntaxKind openKind, SyntaxKind closeKind) =>
        WithBracesAllowed<GreenNode>(() =>
        {
            var open = EatToken(openKind);
            var arguments = ParseSeparatedList(closeKind, ParseArgument);
            var close = EatToken(closeKind);
            return new ArgumentListGreen(open, arguments, close);
        });

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

    private static bool HasInterpolation(GreenToken token)
    {
        foreach (var part in token.Parts)
            if (part.Kind == StringPartKind.Interpolation) return true;
        return false;
    }

    private static bool CanStartExpression(SyntaxKind kind) => kind switch
    {
        SyntaxKind.IdentifierToken => true,
        SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken
            or SyntaxKind.CharLiteralToken or SyntaxKind.StringLiteralToken
            or SyntaxKind.MultilineStringLiteralToken
            or SyntaxKind.RawStringLiteralToken => true,
        SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NoneKeyword => true,
        SyntaxKind.SelfKeyword or SyntaxKind.CapitalSelfKeyword
            or SyntaxKind.SuperKeyword => true,
        SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken => true,
        SyntaxKind.DotToken or SyntaxKind.HashToken => true,
        SyntaxKind.MinusToken or SyntaxKind.PlusToken or SyntaxKind.BangToken
            or SyntaxKind.TildeToken or SyntaxKind.AmpersandToken => true,
        SyntaxKind.AwaitKeyword or SyntaxKind.MoveKeyword or SyntaxKind.TryKeyword => true,
        SyntaxKind.MatchKeyword or SyntaxKind.CapitalSelfKeyword => true,
        SyntaxKind.OpenBraceToken or SyntaxKind.DollarToken => true,
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
        while (At(SyntaxKind.AmpersandToken) && CanFollowCompositionAmpersand(Peek(1).Kind))
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
                case SyntaxKind.AmpersandToken when !CanFollowCompositionAmpersand(Peek(1).Kind):
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
                if (!At(SyntaxKind.IdentifierToken) && !SyntaxFacts.IsKeyword(Current.Kind))
                {
                    _diagnostics.Error(ErrorCode.ERR_ExpectedType, CurrentSpan,
                        $"expected a type but found '{DescribeCurrent()}'");
                    return new SimpleNameTypeGreen(
                        CreateMissing(SyntaxKind.IdentifierToken), null);
                }

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

    private bool LooksLikeRecordLiteralStart() => LooksLikeRecordLiteral();

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
            or SyntaxKind.GreaterEqualsToken
            or SyntaxKind.GreaterGreaterEqualsToken;

    /// <summary>
    /// What may follow the <c>&amp;</c> of a protocol composition.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="CanStartType"/>: only a named type can be a
    /// protocol, so <c>Document&amp;[&gt;..]</c> is a grow-array of references
    /// rather than a composition with a dictionary type.
    /// </remarks>
    private static bool CanFollowCompositionAmpersand(SyntaxKind kind) => kind switch
    {
        SyntaxKind.IdentifierToken or SyntaxKind.CapitalSelfKeyword => true,
        SyntaxKind.SomeKeyword or SyntaxKind.AnyKeyword => true,
        SyntaxKind.OpenParenToken => true,
        _ => false,
    };

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

                if (_index >= _tokens.Length - 1) return false;
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
    // =======================================================================
    // Closures
    // =======================================================================

    /// <summary>A brace right after an argument list is the call's last argument.</summary>
    private GreenNode? TryParseTrailingClosure() =>
        CanTakeTrailingClosure() ? ParseBraceClosure(null) : null;

    private bool CanTakeTrailingClosure() =>
        !_noBrace && At(SyntaxKind.OpenBraceToken) && !IsAccessorBlockAhead();

    /// <summary>
    /// True when the brace at the current token opens a property's accessors.
    /// </summary>
    /// <remarks>
    /// `var tracked: Int = 0 { willSet { ... } }` has an initialiser and then
    /// an accessor block, so the brace must not be read as a trailing closure
    /// on the initialiser. Swift decides the same way: by the first token
    /// inside the brace. None of these keywords is a valid expression, so the
    /// test is safe to apply everywhere.
    /// </remarks>
    private bool IsAccessorBlockAhead() =>
        Peek(1).Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
            or SyntaxKind.WillSetKeyword or SyntaxKind.DidSetKeyword;

    /// <summary>
    /// True for a closure capture list: <c>[weak self] { ... }</c>.
    /// </summary>
    /// <remarks>
    /// Both ends have to be checked. An array literal can also be followed by
    /// an identifier on the next line, so the token after the closing bracket
    /// must be something only a closure can start.
    /// </remarks>
    private bool LooksLikeCaptureList()
    {
        if (Peek(1).Kind is not (SyntaxKind.WeakKeyword or SyntaxKind.UnownedKeyword
            or SyntaxKind.SelfKeyword or SyntaxKind.IdentifierToken))
        {
            return false;
        }

        var depth = 0;
        for (var offset = 0; offset < 64; offset++)
        {
            var kind = Peek(offset).Kind;
            if (kind == SyntaxKind.OpenBracketToken) depth++;
            else if (kind == SyntaxKind.CloseBracketToken)
            {
                depth--;
                if (depth != 0) continue;

                return Peek(offset + 1).Kind switch
                {
                    SyntaxKind.OpenBraceToken => true,
                    SyntaxKind.OpenParenToken => true,
                    SyntaxKind.IdentifierToken =>
                        Peek(offset + 2).Kind == SyntaxKind.FatArrowToken,
                    _ => false,
                };
            }
            else if (kind is SyntaxKind.EndOfFile or SyntaxKind.OpenBraceToken) return false;
        }
        return false;
    }

    /// <summary>Scans for a balanced <c>)</c> followed by <c>=&gt;</c>.</summary>
    private bool LooksLikeArrowClosure()
    {
        var offset = 0;
        var depth = 0;
        while (offset < 256)
        {
            var kind = Peek(offset).Kind;
            if (kind is SyntaxKind.OpenParenToken) depth++;
            else if (kind is SyntaxKind.CloseParenToken)
            {
                depth--;
                if (depth == 0)
                {
                    // '=>' is the body; '->' is a written return type, and no
                    // expression can follow a parenthesised one with an arrow.
                    return Peek(offset + 1).Kind
                        is SyntaxKind.FatArrowToken or SyntaxKind.ArrowToken;
                }
            }
            else if (kind is SyntaxKind.EndOfFile) return false;
            offset++;
        }
        return false;
    }

    private GreenNode ParseCaptureListClosure()
    {
        var captures = ParseCaptureList();
        if (At(SyntaxKind.OpenBraceToken)) return ParseBraceClosure(captures);
        return ParseArrowClosure(captures);
    }

    private GreenNode ParseCaptureList() => WithBracesAllowed<GreenNode>(() =>
    {
        var open = EatToken(SyntaxKind.OpenBracketToken);
        var items = ParseSeparatedList(SyntaxKind.CloseBracketToken, ParseCaptureItem);
        var close = EatToken(SyntaxKind.CloseBracketToken);
        return new CaptureListGreen(open, items, close);
    });

    private GreenNode ParseCaptureItem()
    {
        var specifier = Current.Kind is SyntaxKind.WeakKeyword or SyntaxKind.UnownedKeyword
            ? EatToken()
            : null;

        var identifier = At(SyntaxKind.SelfKeyword) ? EatToken() : EatIdentifier();
        var equals = TryEatToken(SyntaxKind.EqualsToken);
        var value = equals is null ? null : ParseExpression();
        return new CaptureItemGreen(specifier, identifier, equals, value);
    }

    private GreenNode ParseArrowClosure(GreenNode? captures)
    {
        var signature = ParseClosureSignature();
        var arrow = EatToken(SyntaxKind.FatArrowToken);
        var body = At(SyntaxKind.OpenBraceToken)
            ? ParseBlock()
            : WithBracesAllowed(ParseExpression);
        return new ClosureExpressionGreen(captures, signature, arrow, body);
    }

    private GreenNode ParseClosureSignature()
    {
        if (!At(SyntaxKind.OpenParenToken))
            return new ClosureSignatureGreen(
                null, GreenList.Create([new ClosureParameterGreen(EatIdentifier(), null, null)]),
                null, null, null);

        return WithBracesAllowed<GreenNode>(() =>
        {
            var open = EatToken(SyntaxKind.OpenParenToken);
            var parameters = ParseSeparatedList(SyntaxKind.CloseParenToken, ParseClosureParameter);
            var close = EatToken(SyntaxKind.CloseParenToken);

            GreenToken? arrow = null;
            GreenNode? returnType = null;
            if (At(SyntaxKind.ArrowToken))
            {
                arrow = EatToken();
                returnType = ParseType();
            }
            return new ClosureSignatureGreen(open, parameters, close, arrow, returnType);
        });
    }

    private GreenNode ParseClosureParameter()
    {
        var identifier = EatIdentifier();
        var colon = TryEatToken(SyntaxKind.ColonToken);
        var type = colon is null ? null : ParseType();
        return new ClosureParameterGreen(identifier, colon, type);
    }

    private GreenNode ParseBraceClosure(GreenNode? captures) => WithBracesAllowed<GreenNode>(() =>
    {
        var open = EatToken(SyntaxKind.OpenBraceToken);
        var header = HasClosureHeader() ? ParseClosureHeader() : null;
        var statements = ParseStatements(SyntaxKind.CloseBraceToken);
        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new BraceClosureExpressionGreen(captures, open, header, statements, close);
    });

    /// <summary>
    /// True when the tokens after <c>{</c> form a parameter list ending in
    /// <c>in</c>.
    /// </summary>
    /// <remarks>
    /// The scan stops at the first token that cannot appear in a header, so
    /// <c>{ for x in xs { } }</c> is not mistaken for one: <c>for</c> ends the
    /// scan before the <c>in</c> is reached.
    /// </remarks>
    private bool HasClosureHeader()
    {
        // The opening brace has already been consumed, so the scan starts at
        // the current token.
        for (var offset = 0; offset < 64; offset++)
        {
            switch (Peek(offset).Kind)
            {
                case SyntaxKind.InKeyword:
                    return true;

                case SyntaxKind.IdentifierToken:
                case SyntaxKind.UnderscoreToken:
                case SyntaxKind.CommaToken:
                case SyntaxKind.ColonToken:
                case SyntaxKind.ArrowToken:
                case SyntaxKind.OpenParenToken:
                case SyntaxKind.CloseParenToken:
                case SyntaxKind.DotToken:
                case SyntaxKind.LessToken:
                case SyntaxKind.GreaterToken:
                case SyntaxKind.GreaterGreaterToken:
                case SyntaxKind.QuestionToken:
                case SyntaxKind.BangToken:
                case SyntaxKind.AmpersandToken:
                case SyntaxKind.PipeToken:
                case SyntaxKind.OpenBracketToken:
                case SyntaxKind.CloseBracketToken:
                case SyntaxKind.DotDotToken:
                case SyntaxKind.SomeKeyword:
                case SyntaxKind.AnyKeyword:
                case SyntaxKind.RefKeyword:
                case SyntaxKind.CapitalSelfKeyword:
                    continue;

                default:
                    return false;
            }
        }
        return false;
    }

    private GreenNode ParseClosureHeader()
    {
        var open = TryEatToken(SyntaxKind.OpenParenToken);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (!At(SyntaxKind.InKeyword) && !At(SyntaxKind.ArrowToken)
               && !At(SyntaxKind.CloseParenToken) && !At(SyntaxKind.EndOfFile))
        {
            items.Add(ParseClosureParameter());
            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) break;
            items.Add(comma);
        }

        var close = open is null ? null : TryEatToken(SyntaxKind.CloseParenToken);

        GreenToken? arrow = null;
        GreenNode? returnType = null;
        if (At(SyntaxKind.ArrowToken))
        {
            arrow = EatToken();
            returnType = ParseType();
        }

        var inKeyword = EatToken(SyntaxKind.InKeyword);
        return new ClosureHeaderGreen(
            open, GreenList.Create(items.ToImmutable()), close, arrow, returnType, inKeyword);
    }

    // =======================================================================
    // match
    // =======================================================================

    private GreenNode ParseMatchExpression()
    {
        var matchKeyword = EatToken(SyntaxKind.MatchKeyword);
        var subject = WithBracesSuppressed(ParseExpression);
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var arms = ImmutableArray.CreateBuilder<GreenNode>();
        while (!At(SyntaxKind.CloseBraceToken) && !At(SyntaxKind.EndOfFile))
        {
            var before = _index;
            arms.Add(ParseMatchArm());
            if (_index == before) SkipCurrentToken();
        }

        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new MatchExpressionGreen(
            matchKeyword, subject, open, GreenList.Create(arms.ToImmutable()), close);
    }

    private GreenNode ParseMatchArm()
    {
        GreenToken keyword;
        GreenList patterns = GreenList.Empty;

        if (At(SyntaxKind.DefaultKeyword))
        {
            keyword = EatToken();
        }
        else
        {
            keyword = EatToken(SyntaxKind.CaseKeyword);
            var items = ImmutableArray.CreateBuilder<GreenNode>();
            while (true)
            {
                items.Add(WithBracesSuppressed(ParsePattern));
                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) break;
                items.Add(comma);
                if (At(SyntaxKind.EndOfFile)) break;
            }
            patterns = GreenList.Create(items.ToImmutable());
        }

        var whereClause = At(SyntaxKind.WhereKeyword)
            ? new PatternWhereClauseGreen(EatToken(), WithArrowSuppressed(ParseExpression))
            : null;

        if (At(SyntaxKind.ColonToken))
        {
            var colon = EatToken();
            var statements = ParseStatements(SyntaxKind.CaseKeyword, SyntaxKind.DefaultKeyword,
                SyntaxKind.CloseBraceToken);
            return new MatchCaseBlockGreen(keyword, patterns, whereClause, colon, statements);
        }

        var arrow = EatToken(SyntaxKind.FatArrowToken);
        var body = At(SyntaxKind.OpenBraceToken) ? ParseBlock() : ParseExpression();
        return new MatchArmGreen(keyword, patterns, whereClause, arrow, body);
    }

    // =======================================================================
    // Patterns
    // =======================================================================

    /// <summary>Parses a pattern. Forms follow spec/SYNTAX.md §15.</summary>
    public GreenNode ParsePattern()
    {
        var left = ParsePatternWithRange();
        while (At(SyntaxKind.PipeToken))
        {
            var pipe = EatToken();
            left = new OrPatternGreen(left, pipe, ParsePatternWithRange());
        }
        return left;
    }

    private GreenNode ParsePatternWithRange()
    {
        if (IsRangeOperator(Current.Kind))
        {
            var prefixOp = EatToken();
            return new RangePatternGreen(null, prefixOp, ParsePrimaryPattern());
        }

        var pattern = ParsePrimaryPattern();
        if (!IsRangeOperator(Current.Kind)) return pattern;

        var op = EatToken();
        var right = CanStartPattern(Current.Kind) ? ParsePrimaryPattern() : null;
        return new RangePatternGreen(pattern, op, right);
    }

    private GreenNode ParsePrimaryPattern()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.UnderscoreToken:
                return new WildcardPatternGreen(EatToken());

            case SyntaxKind.LetKeyword:
            case SyntaxKind.VarKeyword:
            {
                var keyword = EatToken();
                var identifier = At(SyntaxKind.SelfKeyword) ? EatToken() : EatIdentifier();
                if (!At(SyntaxKind.AsKeyword)) return new BindingPatternGreen(keyword, identifier);

                var asKeyword = EatToken();
                return new BindingTypeTestPatternGreen(
                    keyword, identifier, asKeyword, ParseType());
            }

            case SyntaxKind.IsKeyword:
                return new TypeTestPatternGreen(EatToken(), ParseType());

            case SyntaxKind.DotToken:
            {
                var dot = EatToken();
                var name = EatMemberName();
                var arguments = At(SyntaxKind.OpenParenToken) ? ParsePatternArgumentList() : null;
                return new UnionCasePatternGreen(null, dot, name, arguments);
            }

            case SyntaxKind.OpenParenToken:
                return ParseTupleOrParenthesizedPattern();

            case SyntaxKind.OpenBracketToken:
                return ParseArrayPattern();

            case SyntaxKind.OpenBraceToken:
                return ParseRecordPattern(null);

            case SyntaxKind.MinusToken:
            {
                var minus = EatToken();
                return new LiteralPatternGreen(minus, EatNumericLiteral());
            }

            case SyntaxKind.IntegerLiteralToken:
            case SyntaxKind.FloatLiteralToken:
            case SyntaxKind.CharLiteralToken:
            case SyntaxKind.StringLiteralToken:
            case SyntaxKind.MultilineStringLiteralToken:
            case SyntaxKind.RawStringLiteralToken:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NoneKeyword:
                return new LiteralPatternGreen(null, EatToken());

            case SyntaxKind.IdentifierToken:
                return ParseNamedPattern();

            default:
                _diagnostics.Error(ErrorCode.ERR_ExpectedPattern, CurrentSpan,
                    $"expected a pattern but found '{DescribeCurrent()}'");
                return new BindingPatternGreen(null, CreateMissing(SyntaxKind.IdentifierToken));
        }
    }

    private GreenToken EatNumericLiteral()
    {
        if (Current.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken)
            return EatToken();

        _diagnostics.Error(ErrorCode.ERR_ExpectedPattern, CurrentSpan,
            "a negated pattern must be a numeric literal");
        return CreateMissing(SyntaxKind.IntegerLiteralToken);
    }

    /// <summary>
    /// An identifier starts a binding, a qualified union case, or a record
    /// pattern; the dots and the brace decide which.
    /// </summary>
    private GreenNode ParseNamedPattern()
    {
        var segments = CountNameSegments();

        if (Peek(segments * 2 - 1).Kind == SyntaxKind.OpenBraceToken)
            return ParseRecordPattern(ParseTypeNameSegments(segments));

        if (segments == 1)
            return new BindingPatternGreen(null, EatToken());

        // The last segment is the case name; everything before it qualifies it.
        var qualifier = ParseTypeNameSegments(segments - 1);
        var dot = EatToken(SyntaxKind.DotToken);
        var name = EatMemberName();
        var arguments = At(SyntaxKind.OpenParenToken) ? ParsePatternArgumentList() : null;
        return new UnionCasePatternGreen(qualifier, dot, name, arguments);
    }

    /// <summary>Counts the identifiers in an <c>ident ('.' ident)*</c> run.</summary>
    private int CountNameSegments()
    {
        var count = 1;
        var offset = 1;
        while (Peek(offset).Kind == SyntaxKind.DotToken
               && Peek(offset + 1).Kind == SyntaxKind.IdentifierToken)
        {
            count++;
            offset += 2;
        }
        return count;
    }

    private GreenNode ParseTypeNameSegments(int count)
    {
        GreenNode type = new SimpleNameTypeGreen(EatIdentifier(), null);
        for (var i = 1; i < count; i++)
        {
            var dot = EatToken(SyntaxKind.DotToken);
            type = new QualifiedNameGreen(type, dot, new SimpleNameTypeGreen(EatIdentifier(), null));
        }
        return type;
    }

    private GreenNode ParseRecordPattern(GreenNode? type) => WithBracesAllowed<GreenNode>(() =>
    {
        var open = EatToken(SyntaxKind.OpenBraceToken);
        var fields = ParseSeparatedList(SyntaxKind.CloseBraceToken, ParseFieldPattern);
        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new RecordPatternGreen(type, open, fields, close);
    });

    private GreenNode ParseFieldPattern()
    {
        var name = EatIdentifier();
        var colon = TryEatToken(SyntaxKind.ColonToken);
        var pattern = colon is null ? null : ParsePattern();
        return new FieldPatternGreen(name, colon, pattern);
    }

    private GreenNode ParseTupleOrParenthesizedPattern() => WithBracesAllowed<GreenNode>(() =>
    {
        var open = EatToken(SyntaxKind.OpenParenToken);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        var simple = !At(SyntaxKind.CloseParenToken);
        GreenNode? only = null;

        while (!At(SyntaxKind.CloseParenToken) && !At(SyntaxKind.EndOfFile))
        {
            var element = ParsePattern();
            only ??= element;
            items.Add(element);

            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) break;
            items.Add(comma);
            simple = false;
        }

        var close = EatToken(SyntaxKind.CloseParenToken);

        return simple && items.Count == 1 && only is not null
            ? new ParenthesizedPatternGreen(open, only, close)
            : new TuplePatternGreen(open, GreenList.Create(items.ToImmutable()), close);
    });

    private GreenNode ParseArrayPattern() => WithBracesAllowed<GreenNode>(() =>
    {
        var open = EatToken(SyntaxKind.OpenBracketToken);
        var elements = ParseSeparatedList(SyntaxKind.CloseBracketToken, ParseArrayPatternElement);
        var close = EatToken(SyntaxKind.CloseBracketToken);
        return new ArrayPatternGreen(open, elements, close);
    });

    /// <summary>
    /// Inside an array pattern <c>...</c> is the rest element, not a range.
    /// </summary>
    private GreenNode ParseArrayPatternElement()
    {
        if (!At(SyntaxKind.DotDotDotToken)) return ParsePattern();

        var dots = EatToken();
        var letKeyword = TryEatToken(SyntaxKind.LetKeyword);
        var identifier = At(SyntaxKind.IdentifierToken) ? EatToken() : null;
        return new RestPatternGreen(dots, letKeyword, identifier);
    }

    private GreenNode ParsePatternArgumentList() => WithBracesAllowed<GreenNode>(() =>
    {
        var open = EatToken(SyntaxKind.OpenParenToken);
        var arguments = ParseSeparatedList(SyntaxKind.CloseParenToken, ParseSubPattern);
        var close = EatToken(SyntaxKind.CloseParenToken);
        return new PatternArgumentListGreen(open, arguments, close);
    });

    private GreenNode ParseSubPattern()
    {
        GreenToken? name = null;
        GreenToken? colon = null;
        if (IsArgumentLabel())
        {
            name = EatToken();
            colon = EatToken(SyntaxKind.ColonToken);
        }
        return new SubPatternGreen(name, colon, ParsePattern());
    }

    private static bool CanStartPattern(SyntaxKind kind) => kind switch
    {
        SyntaxKind.IdentifierToken or SyntaxKind.UnderscoreToken => true,
        SyntaxKind.LetKeyword or SyntaxKind.VarKeyword or SyntaxKind.IsKeyword => true,
        SyntaxKind.DotToken or SyntaxKind.MinusToken => true,
        SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken
            or SyntaxKind.OpenBraceToken => true,
        SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken
            or SyntaxKind.CharLiteralToken or SyntaxKind.StringLiteralToken
            or SyntaxKind.MultilineStringLiteralToken
            or SyntaxKind.RawStringLiteralToken => true,
        SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NoneKeyword => true,
        _ => false,
    };

    // =======================================================================
    // Statements
    // =======================================================================

    public GreenNode ParseBlock() => WithBracesAllowed<GreenNode>(() =>
    {
        var open = EatToken(SyntaxKind.OpenBraceToken);
        var statements = ParseStatements(SyntaxKind.CloseBraceToken);
        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new BlockGreen(open, statements, close);
    });

    /// <summary>
    /// Parses statements until one of <paramref name="terminators"/>, always
    /// making progress so that malformed input cannot spin.
    /// </summary>
    private GreenList ParseStatements(params SyntaxKind[] terminators)
    {
        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (!At(SyntaxKind.EndOfFile) && Array.IndexOf(terminators, Current.Kind) < 0)
        {
            var before = _index;
            items.Add(ParseStatement());
            if (_index == before) SkipCurrentToken();
        }
        return GreenList.Create(items.ToImmutable());
    }

    public GreenNode ParseStatement()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.OpenBraceToken:
                return ParseBlock();

            case SyntaxKind.SemicolonToken:
                return new EmptyStatementGreen(EatToken());

            case SyntaxKind.LetKeyword:
            case SyntaxKind.VarKeyword:
                return ParseLocalDeclaration(null, null);

            case SyntaxKind.UsingKeyword:
                return ParseLocalDeclaration(null, EatToken());

            // 'async let x = f()' starts a child task; see SYNTAX.md §19.
            case SyntaxKind.AsyncKeyword when Peek(1).Kind is SyntaxKind.LetKeyword
                or SyntaxKind.VarKeyword:
                return ParseLocalDeclaration(EatToken(), null);

            case SyntaxKind.IfKeyword:
                return ParseIfStatement();

            case SyntaxKind.GuardKeyword:
            {
                var guardKeyword = EatToken();
                var conditions = WithBracesSuppressed(ParseConditionList);
                var elseKeyword = EatToken(SyntaxKind.ElseKeyword);
                return new GuardStatementGreen(guardKeyword, conditions, elseKeyword, ParseBlock());
            }

            case SyntaxKind.WhileKeyword:
            {
                var whileKeyword = EatToken();
                var conditions = WithBracesSuppressed(ParseConditionList);
                return new WhileStatementGreen(whileKeyword, conditions, ParseBlock());
            }

            case SyntaxKind.RepeatKeyword:
            {
                var repeatKeyword = EatToken();
                var body = ParseBlock();
                var whileKeyword = EatToken(SyntaxKind.WhileKeyword);
                var condition = ParseExpression();
                return new RepeatStatementGreen(repeatKeyword, body, whileKeyword, condition,
                    TryEatToken(SyntaxKind.SemicolonToken));
            }

            case SyntaxKind.ForKeyword:
                return ParseForStatement();

            case SyntaxKind.SwitchKeyword:
                return ParseSwitchStatement();

            case SyntaxKind.DoKeyword:
                return ParseDoStatement();

            case SyntaxKind.RegionKeyword:
                return ParseRegionStatement();

            case SyntaxKind.DeferKeyword:
                return new DeferStatementGreen(EatToken(), ParseBlock());

            case SyntaxKind.UnsafeKeyword:
                return new UnsafeStatementGreen(EatToken(), ParseBlock());

            case SyntaxKind.ComptimeKeyword:
                return new ComptimeStatementGreen(EatToken(), ParseBlock());

            case SyntaxKind.ReturnKeyword:
            {
                var returnKeyword = EatToken();
                var value = !returnKeyword.TerminatesStatement && CanStartExpression(Current.Kind)
                    ? ParseExpression()
                    : null;
                return new ReturnStatementGreen(returnKeyword, value,
                    TryEatToken(SyntaxKind.SemicolonToken));
            }

            case SyntaxKind.ThrowKeyword:
            {
                var throwKeyword = EatToken();
                return new ThrowStatementGreen(throwKeyword, ParseExpression(),
                    TryEatToken(SyntaxKind.SemicolonToken));
            }

            case SyntaxKind.BreakKeyword:
            {
                var breakKeyword = EatToken();
                var label = !breakKeyword.TerminatesStatement && At(SyntaxKind.IdentifierToken)
                    ? EatToken()
                    : null;
                return new BreakStatementGreen(breakKeyword, label,
                    TryEatToken(SyntaxKind.SemicolonToken));
            }

            case SyntaxKind.ContinueKeyword:
            {
                var continueKeyword = EatToken();
                var label = !continueKeyword.TerminatesStatement && At(SyntaxKind.IdentifierToken)
                    ? EatToken()
                    : null;
                return new ContinueStatementGreen(continueKeyword, label,
                    TryEatToken(SyntaxKind.SemicolonToken));
            }

            case SyntaxKind.FallthroughKeyword:
                return new FallthroughStatementGreen(
                    EatToken(), TryEatToken(SyntaxKind.SemicolonToken));

            // A declaration used as a statement: a nested function or type.
            case SyntaxKind.FnKeyword:
            case SyntaxKind.StructKeyword:
            case SyntaxKind.ClassKeyword:
            case SyntaxKind.ActorKeyword:
            case SyntaxKind.ProtocolKeyword:
            case SyntaxKind.UnionKeyword:
            case SyntaxKind.EnumKeyword:
            case SyntaxKind.ExtensionKeyword:
            case SyntaxKind.AliasKeyword:
            case SyntaxKind.TypealiasKeyword:
            case SyntaxKind.ConstKeyword:
            case SyntaxKind.AtToken:
                return new DeclarationStatementGreen(ParseMemberDeclaration(GreenList.Empty));

            case SyntaxKind.IdentifierToken when Peek(1).Kind == SyntaxKind.ColonToken
                && IsLabelTarget(Peek(2).Kind):
            {
                var label = EatToken();
                var colon = EatToken(SyntaxKind.ColonToken);
                return new LabeledStatementGreen(label, colon, ParseStatement());
            }

            default:
            {
                var expression = ParseExpression();
                return new ExpressionStatementGreen(
                    expression, TryEatToken(SyntaxKind.SemicolonToken));
            }
        }
    }

    private static bool IsLabelTarget(SyntaxKind kind) =>
        kind is SyntaxKind.ForKeyword or SyntaxKind.WhileKeyword
            or SyntaxKind.RepeatKeyword or SyntaxKind.OpenBraceToken;

    private GreenNode ParseLocalDeclaration(GreenToken? asyncKeyword, GreenToken? usingKeyword)
    {
        var keyword = Current.Kind is SyntaxKind.LetKeyword or SyntaxKind.VarKeyword
            ? EatToken()
            : EatToken(SyntaxKind.LetKeyword);

        var declarator = ParseVariableDeclarator();
        return new LocalDeclarationStatementGreen(asyncKeyword, usingKeyword, keyword,
            declarator, TryEatToken(SyntaxKind.SemicolonToken));
    }

    private GreenNode ParseVariableDeclarator()
    {
        var pattern = ParseBindingTarget();

        GreenNode? annotation = null;
        if (At(SyntaxKind.ColonToken))
        {
            var colon = EatToken();
            annotation = new TypeAnnotationGreen(colon, ParseType());
        }

        GreenNode? initializer = null;
        if (At(SyntaxKind.EqualsToken))
        {
            var equals = EatToken();
            initializer = new EqualsValueClauseGreen(equals, ParseExpression());
        }

        return new VariableDeclaratorGreen(pattern, annotation, initializer);
    }

    /// <summary>
    /// The name side of a binding: a plain identifier, or a tuple or record
    /// pattern when destructuring.
    /// </summary>
    private GreenNode ParseBindingTarget() => Current.Kind switch
    {
        SyntaxKind.OpenParenToken => ParseTupleOrParenthesizedPattern(),
        SyntaxKind.OpenBraceToken => ParseRecordPattern(null),
        SyntaxKind.UnderscoreToken => new WildcardPatternGreen(EatToken()),
        _ => new BindingPatternGreen(null, EatIdentifier()),
    };

    private GreenNode ParseIfStatement()
    {
        var ifKeyword = EatToken(SyntaxKind.IfKeyword);
        var conditions = WithBracesSuppressed(ParseConditionList);
        var body = ParseBlock();

        GreenNode? elseClause = null;
        if (At(SyntaxKind.ElseKeyword))
        {
            var elseKeyword = EatToken();
            var elseBody = At(SyntaxKind.IfKeyword) ? ParseIfStatement() : ParseBlock();
            elseClause = new ElseClauseGreen(elseKeyword, elseBody);
        }

        return new IfStatementGreen(ifKeyword, conditions, body, elseClause);
    }

    private GreenList ParseConditionList()
    {
        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (true)
        {
            items.Add(ParseCondition());
            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) break;
            items.Add(comma);
            if (At(SyntaxKind.EndOfFile) || At(SyntaxKind.OpenBraceToken)) break;
        }
        return GreenList.Create(items.ToImmutable());
    }

    private GreenNode ParseCondition()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.LetKeyword:
            case SyntaxKind.VarKeyword:
            {
                var keyword = EatToken();
                var pattern = At(SyntaxKind.SelfKeyword)
                    ? new BindingPatternGreen(null, EatToken())
                    : ParseBindingTarget();

                GreenNode? annotation = null;
                if (At(SyntaxKind.ColonToken))
                {
                    var colon = EatToken();
                    annotation = new TypeAnnotationGreen(colon, ParseType());
                }

                GreenNode? initializer = null;
                if (At(SyntaxKind.EqualsToken))
                {
                    var equals = EatToken();
                    initializer = new EqualsValueClauseGreen(equals, ParseExpression());
                }

                return new OptionalBindingConditionGreen(keyword,
                    new VariableDeclaratorGreen(pattern, annotation, initializer));
            }

            case SyntaxKind.CaseKeyword:
            {
                var caseKeyword = EatToken();
                var pattern = ParsePattern();
                var equals = EatToken(SyntaxKind.EqualsToken);
                return new CasePatternConditionGreen(caseKeyword, pattern, equals, ParseExpression());
            }

            default:
                return new ExpressionConditionGreen(ParseExpression());
        }
    }

    private GreenNode ParseForStatement()
    {
        var forKeyword = EatToken(SyntaxKind.ForKeyword);
        var awaitKeyword = TryEatToken(SyntaxKind.AwaitKeyword);
        var pattern = WithBracesSuppressed(ParseBindingTarget);
        var inKeyword = EatToken(SyntaxKind.InKeyword);
        var sequence = WithBracesSuppressed(ParseExpression);

        GreenNode? whereClause = null;
        if (At(SyntaxKind.WhereKeyword))
        {
            var whereKeyword = EatToken();
            whereClause = new ForWhereClauseGreen(
                whereKeyword, WithBracesSuppressed(ParseExpression));
        }

        return new ForStatementGreen(
            forKeyword, awaitKeyword, pattern, inKeyword, sequence, whereClause, ParseBlock());
    }

    private GreenNode ParseSwitchStatement()
    {
        var switchKeyword = EatToken(SyntaxKind.SwitchKeyword);
        var subject = WithBracesSuppressed(ParseExpression);
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var cases = ImmutableArray.CreateBuilder<GreenNode>();
        while (!At(SyntaxKind.CloseBraceToken) && !At(SyntaxKind.EndOfFile))
        {
            var before = _index;
            cases.Add(ParseSwitchCase());
            if (_index == before) SkipCurrentToken();
        }

        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new SwitchStatementGreen(
            switchKeyword, subject, open, GreenList.Create(cases.ToImmutable()), close);
    }

    private GreenNode ParseSwitchCase()
    {
        GreenToken keyword;
        GreenList values = GreenList.Empty;

        if (At(SyntaxKind.DefaultKeyword))
        {
            keyword = EatToken();
        }
        else
        {
            keyword = EatToken(SyntaxKind.CaseKeyword);
            var items = ImmutableArray.CreateBuilder<GreenNode>();
            while (true)
            {
                items.Add(WithBracesSuppressed(ParseExpression));
                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) break;
                items.Add(comma);
                if (At(SyntaxKind.EndOfFile)) break;
            }
            values = GreenList.Create(items.ToImmutable());
        }

        var colon = EatToken(SyntaxKind.ColonToken);
        var statements = ParseStatements(
            SyntaxKind.CaseKeyword, SyntaxKind.DefaultKeyword, SyntaxKind.CloseBraceToken);
        return new SwitchCaseGreen(keyword, values, colon, statements);
    }

    private GreenNode ParseDoStatement()
    {
        var doKeyword = EatToken(SyntaxKind.DoKeyword);
        var body = ParseBlock();

        var catches = ImmutableArray.CreateBuilder<GreenNode>();
        while (At(SyntaxKind.CatchKeyword))
        {
            var catchKeyword = EatToken();
            var pattern = At(SyntaxKind.OpenBraceToken)
                ? null
                : WithBracesSuppressed(ParsePattern);

            GreenNode? whereClause = null;
            if (At(SyntaxKind.WhereKeyword))
            {
                var whereKeyword = EatToken();
                whereClause = new PatternWhereClauseGreen(
                    whereKeyword, WithBracesSuppressed(ParseExpression));
            }

            catches.Add(new CatchClauseGreen(catchKeyword, pattern, whereClause, ParseBlock()));
        }

        return new DoStatementGreen(doKeyword, body, GreenList.Create(catches.ToImmutable()));
    }

    private GreenNode ParseRegionStatement()
    {
        var regionKeyword = EatToken(SyntaxKind.RegionKeyword);
        var identifier = At(SyntaxKind.IdentifierToken) ? EatToken() : null;

        GreenToken? equals = null;
        GreenNode? source = null;
        if (At(SyntaxKind.EqualsToken))
        {
            equals = EatToken();
            source = WithBracesSuppressed(ParseExpression);
        }

        return new RegionStatementGreen(regionKeyword, identifier, equals, source, ParseBlock());
    }

    // =======================================================================
    // Declarations
    // =======================================================================

    /// <summary>Parses a whole source file.</summary>
    public GreenNode ParseCompilationUnit()
    {
        var attributes = ParseAttributes();

        GreenNode? module = null;
        if (At(SyntaxKind.ModuleKeyword))
        {
            var moduleKeyword = EatToken();
            module = new ModuleDeclarationGreen(attributes, moduleKeyword, ParseTypeName());
            attributes = GreenList.Empty;
        }

        var imports = ImmutableArray.CreateBuilder<GreenNode>();
        while (attributes.SlotCount == 0 && AtImportDeclaration())
            imports.Add(ParseImportDeclaration());

        var members = ParseMembers(
            attributes, ParseMemberDeclaration, SyntaxKind.EndOfFile, MemberContext.Declaration);
        var eof = EatToken();

        return new CompilationUnitGreen(
            module, GreenList.Create(imports.ToImmutable()), members, eof);
    }

    private bool AtImportDeclaration() =>
        At(SyntaxKind.ImportKeyword)
        || (At(SyntaxKind.PublicKeyword) && Peek(1).Kind == SyntaxKind.ImportKeyword);

    private GreenNode ParseImportDeclaration()
    {
        var publicKeyword = TryEatToken(SyntaxKind.PublicKeyword);
        var importKeyword = EatToken(SyntaxKind.ImportKeyword);
        var name = ParseTypeName();

        GreenNode? items = null;
        if (At(SyntaxKind.DotToken) && Peek(1).Kind == SyntaxKind.OpenBraceToken)
        {
            var dot = EatToken();
            var open = EatToken(SyntaxKind.OpenBraceToken);
            var list = ParseSeparatedList(SyntaxKind.CloseBraceToken, ParseImportItem);
            var close = EatToken(SyntaxKind.CloseBraceToken);
            items = new ImportItemListGreen(dot, open, list, close);
        }

        GreenToken? asKeyword = null;
        GreenToken? alias = null;
        if (items is null && At(SyntaxKind.AsKeyword))
        {
            asKeyword = EatToken();
            alias = EatIdentifier();
        }

        return new ImportDeclarationGreen(
            publicKeyword, importKeyword, name, asKeyword, alias, items);
    }

    private GreenNode ParseImportItem()
    {
        var name = At(SyntaxKind.StarToken) ? EatToken() : EatIdentifier();
        var asKeyword = TryEatToken(SyntaxKind.AsKeyword);
        var alias = asKeyword is null ? null : EatIdentifier();
        return new ImportItemGreen(name, asKeyword, alias);
    }

    private GreenList ParseAttributes()
    {
        if (!At(SyntaxKind.AtToken)) return GreenList.Empty;

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (At(SyntaxKind.AtToken))
        {
            var at = EatToken();
            var name = ParseTypeName();
            var arguments = At(SyntaxKind.OpenParenToken)
                ? ParseArgumentList(SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken)
                : null;
            items.Add(new AttributeGreen(at, name, arguments));
        }
        return GreenList.Create(items.ToImmutable());
    }

    private GreenList ParseModifiers()
    {
        if (!IsModifier(Current.Kind) && !IsConstFunctionModifier()) return GreenList.Empty;

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (IsModifier(Current.Kind) || IsConstFunctionModifier())
        {
            items.Add(EatToken());

            // 'private(set)' and 'unowned(safe)' carry a parenthesised argument.
            if (At(SyntaxKind.OpenParenToken) && Peek(2).Kind == SyntaxKind.CloseParenToken)
            {
                items.Add(EatToken());
                items.Add(EatToken());
                items.Add(EatToken());
            }
        }
        return GreenList.Create(items.ToImmutable());
    }

    /// <summary>
    /// <c>const</c> declares a compile-time constant on its own, but modifies
    /// a function in <c>const fn</c>.
    /// </summary>
    private bool IsConstFunctionModifier() =>
        At(SyntaxKind.ConstKeyword) && Peek(1).Kind == SyntaxKind.FnKeyword;

    private static bool IsModifier(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PublicKeyword or SyntaxKind.PackageKeyword
            or SyntaxKind.InternalKeyword or SyntaxKind.PrivateKeyword => true,
        SyntaxKind.StaticKeyword or SyntaxKind.OpenKeyword or SyntaxKind.SealedKeyword
            or SyntaxKind.AbstractKeyword or SyntaxKind.FinalKeyword
            or SyntaxKind.OverrideKeyword or SyntaxKind.RequiredKeyword
            or SyntaxKind.ConvenienceKeyword => true,
        SyntaxKind.MutatingKeyword or SyntaxKind.NonmutatingKeyword
            or SyntaxKind.LazyKeyword or SyntaxKind.NonisolatedKeyword => true,
        SyntaxKind.WeakKeyword or SyntaxKind.UnownedKeyword => true,
        SyntaxKind.AsyncKeyword => true,
        _ => false,
    };

    /// <summary>
    /// Parses members until <paramref name="terminator"/>, always making
    /// progress. <paramref name="leading"/> carries attributes already read.
    /// </summary>
    private GreenList ParseMembers(
        GreenList leading,
        Func<GreenList, GreenNode> parseMember,
        SyntaxKind terminator,
        MemberContext context)
    {
        var items = ImmutableArray.CreateBuilder<GreenNode>();
        var pending = leading;

        while (!At(terminator) && !At(SyntaxKind.EndOfFile))
        {
            // An untouched member from the previous tree can be dropped
            // straight in: its green node is position-independent.
            if (pending.SlotCount == 0 && _skipped.Count == 0 && _reuse is not null
                && _reuse.TryGet(_index, context, out var reused, out var tokenCount)
                && _index + tokenCount < _tokens.Length)
            {
                items.Add(reused);
                _index += tokenCount;
                _tokenPosition += reused.FullWidth;
                continue;
            }

            var before = _index;
            items.Add(parseMember(pending));
            pending = GreenList.Empty;
            if (_index == before) SkipCurrentToken();
        }

        if (pending.SlotCount > 0)
            items.Add(new IncompleteDeclarationGreen(pending, GreenList.Empty));

        return GreenList.Create(items.ToImmutable());
    }

    private GreenNode ParseMemberDeclaration(GreenList leadingAttributes)
    {
        var attributes = leadingAttributes.SlotCount > 0 ? leadingAttributes : ParseAttributes();
        var modifiers = ParseModifiers();

        switch (Current.Kind)
        {
            case SyntaxKind.FnKeyword:
                return ParseFunctionDeclaration(attributes, modifiers);

            case SyntaxKind.StructKeyword:
            case SyntaxKind.ClassKeyword:
            case SyntaxKind.ActorKeyword:
            case SyntaxKind.ProtocolKeyword:
                return ParseTypeDeclaration(attributes, modifiers);

            case SyntaxKind.UnionKeyword:
                return ParseUnionDeclaration(attributes, modifiers);

            case SyntaxKind.EnumKeyword:
                return ParseEnumDeclaration(attributes, modifiers);

            case SyntaxKind.ExtensionKeyword:
                return ParseExtensionDeclaration(attributes, modifiers);

            case SyntaxKind.AliasKeyword:
            {
                var aliasKeyword = EatToken();
                var identifier = EatIdentifier();
                var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
                var equals = EatToken(SyntaxKind.EqualsToken);
                return new AliasDeclarationGreen(attributes, modifiers, aliasKeyword,
                    identifier, typeParameters, equals, ParseType());
            }

            case SyntaxKind.TypealiasKeyword:
            {
                var keyword = EatToken();
                var identifier = EatIdentifier();
                var equals = EatToken(SyntaxKind.EqualsToken);
                return new TypealiasDeclarationGreen(
                    attributes, modifiers, keyword, identifier, equals, ParseType());
            }

            case SyntaxKind.AssociatedtypeKeyword:
            {
                var keyword = EatToken();
                var identifier = EatIdentifier();
                var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
                var equals = TryEatToken(SyntaxKind.EqualsToken);
                var defaultType = equals is null ? null : ParseType();
                return new AssociatedTypeDeclarationGreen(attributes, modifiers, keyword,
                    identifier, baseTypes, equals, defaultType);
            }

            case SyntaxKind.ConstKeyword:
            {
                var keyword = EatToken();
                var identifier = EatIdentifier();
                GreenNode? annotation = null;
                if (At(SyntaxKind.ColonToken))
                {
                    var colon = EatToken();
                    annotation = new TypeAnnotationGreen(colon, ParseType());
                }
                var equals = EatToken(SyntaxKind.EqualsToken);
                return new ConstDeclarationGreen(attributes, modifiers, keyword, identifier,
                    annotation, new EqualsValueClauseGreen(equals, ParseExpression()));
            }

            case SyntaxKind.LetKeyword:
            case SyntaxKind.VarKeyword:
                return ParseFieldDeclaration(attributes, modifiers, EatToken());

            // The terse field form inside a type body: 'x: F32' declares an
            // immutable stored field; 'var' is written when it is mutable.
            case SyntaxKind.IdentifierToken when Peek(1).Kind == SyntaxKind.ColonToken:
                return ParseFieldDeclaration(attributes, modifiers, null);

            case SyntaxKind.InitKeyword:
            {
                var keyword = EatToken();
                var question = TryEatToken(SyntaxKind.QuestionToken);
                var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
                var parameters = ParseParameterList();
                var throwsSpec = ParseThrowsSpecOrNull();
                var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;
                var body = At(SyntaxKind.OpenBraceToken) ? ParseBlock() : null;
                return new InitDeclarationGreen(attributes, modifiers, keyword, question,
                    typeParameters, parameters, throwsSpec, whereClause, body);
            }

            case SyntaxKind.DeinitKeyword:
                return new DeinitDeclarationGreen(
                    attributes, modifiers, EatToken(), ParseBlock());

            case SyntaxKind.SubscriptKeyword:
                return ParseSubscriptDeclaration(attributes, modifiers);

            case SyntaxKind.OperatorKeyword:
            case SyntaxKind.PrefixKeyword:
            case SyntaxKind.PostfixKeyword:
                return ParseOperatorDeclaration(attributes, modifiers);

            default:
                if (attributes.SlotCount > 0 || modifiers.SlotCount > 0)
                    return new IncompleteDeclarationGreen(attributes, modifiers);

                _diagnostics.Error(ErrorCode.ERR_ExpectedDeclaration, CurrentSpan,
                    $"expected a declaration but found '{DescribeCurrent()}'");
                return new IncompleteDeclarationGreen(GreenList.Empty, GreenList.Empty);
        }
    }

    private GreenNode ParseFieldDeclaration(
        GreenList attributes, GreenList modifiers, GreenToken? keyword)
    {
        var declarator = ParseVariableDeclarator();
        var accessors = At(SyntaxKind.OpenBraceToken) ? ParseAccessorList() : null;

        // One-line type bodies separate their fields with commas.
        var separator = Current.Kind is SyntaxKind.CommaToken or SyntaxKind.SemicolonToken
            ? EatToken()
            : null;

        return new FieldDeclarationGreen(
            attributes, modifiers, keyword, declarator, accessors, separator);
    }

    private GreenNode ParseFunctionDeclaration(GreenList attributes, GreenList modifiers)
    {
        var fnKeyword = EatToken(SyntaxKind.FnKeyword);

        GreenNode? explicitProtocol = null;
        GreenToken? dot = null;
        if (At(SyntaxKind.IdentifierToken) && Peek(1).Kind == SyntaxKind.DotToken)
        {
            explicitProtocol = ParseTypeNameSegments(CountNameSegments() - 1);
            dot = EatToken(SyntaxKind.DotToken);
        }

        var identifier = EatMemberName();
        var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
        var parameters = ParseParameterList();

        var asyncKeyword = TryEatToken(SyntaxKind.AsyncKeyword);
        var throwsSpec = ParseThrowsSpecOrNull();
        asyncKeyword ??= TryEatToken(SyntaxKind.AsyncKeyword);

        var returnClause = At(SyntaxKind.ArrowToken) ? ParseReturnClause() : null;
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;

        GreenNode? body = null;
        GreenToken? arrow = null;
        GreenNode? expressionBody = null;

        if (At(SyntaxKind.OpenBraceToken)) body = ParseBlock();
        else if (At(SyntaxKind.FatArrowToken))
        {
            arrow = EatToken();
            expressionBody = ParseExpression();
        }

        return new FunctionDeclarationGreen(attributes, modifiers, fnKeyword, explicitProtocol,
            dot, identifier, typeParameters, parameters, asyncKeyword, throwsSpec, returnClause,
            whereClause, body, arrow, expressionBody);
    }

    private GreenNode ParseReturnClause()
    {
        var arrow = EatToken(SyntaxKind.ArrowToken);
        return new ReturnClauseGreen(arrow, ParseType());
    }

    private GreenNode ParseParameterList(
        SyntaxKind openKind = SyntaxKind.OpenParenToken,
        SyntaxKind closeKind = SyntaxKind.CloseParenToken) =>
        WithBracesAllowed<GreenNode>(() =>
        {
            var open = EatToken(openKind);
            var parameters = ParseSeparatedList(closeKind, ParseParameter);
            var close = EatToken(closeKind);
            return new ParameterListGreen(open, parameters, close);
        });

    private GreenNode ParseParameter()
    {
        var attributes = ParseAttributes();

        var modifierItems = ImmutableArray.CreateBuilder<GreenNode>();
        while (IsParameterModifier(Current.Kind)) modifierItems.Add(EatToken());
        var modifiers = GreenList.Create(modifierItems.ToImmutable());

        // 'from source: T' has an external label; 'source: T' does not. The
        // label may be a keyword — 'fn first(where p: ...)' reads naturally.
        GreenToken? label = null;
        if ((Current.Kind is SyntaxKind.IdentifierToken or SyntaxKind.UnderscoreToken
             || Current.IsKeywordToken())
            && Peek(1).Kind == SyntaxKind.IdentifierToken)
        {
            label = EatToken();
        }

        var identifier = At(SyntaxKind.UnderscoreToken) ? EatToken() : EatIdentifier();
        var colon = EatToken(SyntaxKind.ColonToken);

        // 'into buf: inout U8[]' puts the modifier after the colon, which is
        // the form that reads well once a parameter has an external label.
        var typeModifier = IsParameterModifier(Current.Kind) ? EatToken() : null;

        var type = ParseType();
        var ellipsis = TryEatToken(SyntaxKind.DotDotDotToken);

        GreenNode? defaultValue = null;
        if (At(SyntaxKind.EqualsToken))
        {
            var equals = EatToken();
            defaultValue = new EqualsValueClauseGreen(equals, ParseExpression());
        }

        return new ParameterGreen(attributes, modifiers, label, identifier, colon,
            typeModifier, type, ellipsis, defaultValue);
    }

    private static bool IsParameterModifier(SyntaxKind kind) => kind switch
    {
        SyntaxKind.InoutKeyword or SyntaxKind.OutKeyword or SyntaxKind.ConsumingKeyword
            or SyntaxKind.BorrowingKeyword or SyntaxKind.RefKeyword
            or SyntaxKind.InKeyword => true,
        _ => false,
    };

    private GreenNode ParseGenericParameterList() => WithBracesAllowed<GreenNode>(() =>
    {
        var less = EatToken(SyntaxKind.LessToken);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (!IsTypeArgumentListEnd() && !At(SyntaxKind.EndOfFile))
        {
            items.Add(ParseGenericParameter());
            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) break;
            items.Add(comma);
        }

        var greater = EatGreaterThan();
        return new GenericParameterListGreen(
            less, GreenList.Create(items.ToImmutable()), greater);
    });

    private GreenNode ParseGenericParameter()
    {
        var attributes = ParseAttributes();
        var variance = Current.Kind is SyntaxKind.OutKeyword or SyntaxKind.InKeyword
            ? EatToken()
            : null;

        var identifier = EatIdentifier();
        var colon = TryEatToken(SyntaxKind.ColonToken);
        var constraint = colon is null ? null : ParseIntersectionType();
        var equals = TryEatToken(SyntaxKind.EqualsToken);
        var defaultType = equals is null ? null : ParseType();

        return new GenericParameterGreen(
            attributes, variance, identifier, colon, constraint, equals, defaultType);
    }

    private GreenNode ParseWhereClause()
    {
        var whereKeyword = EatToken(SyntaxKind.WhereKeyword);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (true)
        {
            items.Add(ParseWhereConstraint());
            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) break;
            items.Add(comma);
            if (At(SyntaxKind.EndOfFile) || At(SyntaxKind.OpenBraceToken)) break;
        }

        return new WhereClauseGreen(whereKeyword, GreenList.Create(items.ToImmutable()));
    }

    private GreenNode ParseWhereConstraint()
    {
        var left = WithBracesSuppressed(() => ParsePostfixType());
        var op = Current.Kind switch
        {
            SyntaxKind.ColonToken => EatToken(),
            SyntaxKind.EqualsEqualsToken => EatToken(),
            _ => EatToken(SyntaxKind.ColonToken),
        };
        var right = WithBracesSuppressed(() => ParseIntersectionType());
        return new WhereConstraintGreen(left, op, right);
    }

    private GreenNode ParseBaseTypeList()
    {
        var colon = EatToken(SyntaxKind.ColonToken);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (true)
        {
            items.Add(WithBracesSuppressed(() => ParseIntersectionType()));
            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) break;
            items.Add(comma);
            if (At(SyntaxKind.EndOfFile) || At(SyntaxKind.OpenBraceToken)) break;
        }

        return new BaseTypeListGreen(colon, GreenList.Create(items.ToImmutable()));
    }

    private (GreenToken Keyword, GreenToken Identifier, GreenNode? TypeParameters,
        GreenNode? BaseTypes, GreenNode? WhereClause, GreenToken Open) ParseTypeHeader()
    {
        var keyword = EatToken();
        var identifier = EatIdentifier();
        var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
        var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;
        var open = EatToken(SyntaxKind.OpenBraceToken);
        return (keyword, identifier, typeParameters, baseTypes, whereClause, open);
    }

    private GreenNode ParseTypeDeclaration(GreenList attributes, GreenList modifiers)
    {
        var kind = Current.Kind;
        var header = ParseTypeHeader();
        var members = ParseMembers(GreenList.Empty, ParseMemberDeclaration,
            SyntaxKind.CloseBraceToken, MemberContext.Declaration);
        var close = EatToken(SyntaxKind.CloseBraceToken);

        return kind switch
        {
            SyntaxKind.StructKeyword => new StructDeclarationGreen(attributes, modifiers,
                header.Keyword, header.Identifier, header.TypeParameters, header.BaseTypes,
                header.WhereClause, header.Open, members, close),
            SyntaxKind.ClassKeyword => new ClassDeclarationGreen(attributes, modifiers,
                header.Keyword, header.Identifier, header.TypeParameters, header.BaseTypes,
                header.WhereClause, header.Open, members, close),
            SyntaxKind.ActorKeyword => new ActorDeclarationGreen(attributes, modifiers,
                header.Keyword, header.Identifier, header.TypeParameters, header.BaseTypes,
                header.WhereClause, header.Open, members, close),
            _ => new ProtocolDeclarationGreen(attributes, modifiers,
                header.Keyword, header.Identifier, header.TypeParameters, header.BaseTypes,
                header.WhereClause, header.Open, members, close),
        };
    }

    private GreenNode ParseUnionDeclaration(GreenList attributes, GreenList modifiers)
    {
        var keyword = EatToken(SyntaxKind.UnionKeyword);
        var identifier = EatIdentifier();
        // 'union Node..' marks a variant-sized union.
        var variableSize = TryEatToken(SyntaxKind.DotDotToken);
        var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
        var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var members = ParseMembers(GreenList.Empty, ParseUnionMember,
            SyntaxKind.CloseBraceToken, MemberContext.Union);
        var close = EatToken(SyntaxKind.CloseBraceToken);

        return new UnionDeclarationGreen(attributes, modifiers, keyword, identifier,
            variableSize, typeParameters, baseTypes, whereClause, open, members, close);
    }

    /// <summary>A union body holds cases first, then ordinary members.</summary>
    private GreenNode ParseUnionMember(GreenList leadingAttributes)
    {
        var attributes = leadingAttributes.SlotCount > 0 ? leadingAttributes : ParseAttributes();

        if (At(SyntaxKind.IdentifierToken))
        {
            var identifier = EatToken();
            GreenNode? payload = null;
            if (At(SyntaxKind.OpenParenToken))
            {
                var open = EatToken();
                var fields = ParseSeparatedList(SyntaxKind.CloseParenToken, ParseUnionCaseField);
                var close = EatToken(SyntaxKind.CloseParenToken);
                payload = new UnionCasePayloadListGreen(open, fields, close);
            }
            var comma = TryEatToken(SyntaxKind.CommaToken);
            return new UnionCaseDeclarationGreen(attributes, identifier, payload, comma);
        }

        return ParseMemberDeclaration(attributes);
    }

    private GreenNode ParseUnionCaseField()
    {
        GreenToken? name = null;
        GreenToken? colon = null;
        if (IsArgumentLabel())
        {
            name = EatToken();
            colon = EatToken(SyntaxKind.ColonToken);
        }
        return new UnionCaseFieldGreen(name, colon, ParseType());
    }

    private GreenNode ParseEnumDeclaration(GreenList attributes, GreenList modifiers)
    {
        var keyword = EatToken(SyntaxKind.EnumKeyword);
        var identifier = EatIdentifier();
        var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var members = ParseMembers(GreenList.Empty, ParseEnumMember,
            SyntaxKind.CloseBraceToken, MemberContext.Enum);
        var close = EatToken(SyntaxKind.CloseBraceToken);

        return new EnumDeclarationGreen(attributes, modifiers, keyword, identifier,
            baseTypes, open, members, close);
    }

    private GreenNode ParseEnumMember(GreenList leadingAttributes)
    {
        var attributes = leadingAttributes.SlotCount > 0 ? leadingAttributes : ParseAttributes();

        if (!At(SyntaxKind.IdentifierToken)) return ParseMemberDeclaration(attributes);

        var identifier = EatToken();
        var equals = TryEatToken(SyntaxKind.EqualsToken);
        var value = equals is null ? null : ParseExpression();
        var comma = TryEatToken(SyntaxKind.CommaToken);
        return new EnumCaseDeclarationGreen(attributes, identifier, equals, value, comma);
    }

    private GreenNode ParseExtensionDeclaration(GreenList attributes, GreenList modifiers)
    {
        var keyword = EatToken(SyntaxKind.ExtensionKeyword);
        var type = WithBracesSuppressed(() => ParsePostfixType());
        var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var members = ParseMembers(GreenList.Empty, ParseMemberDeclaration,
            SyntaxKind.CloseBraceToken, MemberContext.Declaration);
        var close = EatToken(SyntaxKind.CloseBraceToken);

        return new ExtensionDeclarationGreen(attributes, modifiers, keyword, type,
            baseTypes, whereClause, open, members, close);
    }

    private GreenNode ParseSubscriptDeclaration(GreenList attributes, GreenList modifiers)
    {
        var keyword = EatToken(SyntaxKind.SubscriptKeyword);
        var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
        var parameters = ParseParameterList();
        var returnClause = ParseReturnClause();
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;

        GreenNode? accessors = null;
        GreenToken? arrow = null;
        GreenNode? expressionBody = null;

        if (At(SyntaxKind.OpenBraceToken)) accessors = ParseAccessorList();
        else if (At(SyntaxKind.FatArrowToken))
        {
            arrow = EatToken();
            expressionBody = ParseExpression();
        }

        return new SubscriptDeclarationGreen(attributes, modifiers, keyword, typeParameters,
            parameters, returnClause, whereClause, accessors, arrow, expressionBody);
    }

    private GreenNode ParseOperatorDeclaration(GreenList attributes, GreenList modifiers)
    {
        var fixity = Current.Kind is SyntaxKind.PrefixKeyword or SyntaxKind.PostfixKeyword
            ? EatToken()
            : null;

        var operatorKeyword = EatToken(SyntaxKind.OperatorKeyword);
        var operatorToken = EatToken();

        // '[]' and '()' are written as two tokens.
        var second = operatorToken.Kind switch
        {
            SyntaxKind.OpenBracketToken => EatToken(SyntaxKind.CloseBracketToken),
            SyntaxKind.OpenParenToken => EatToken(SyntaxKind.CloseParenToken),
            _ => null,
        };

        var parameters = ParseParameterList();
        var returnClause = ParseReturnClause();

        GreenNode? body = null;
        GreenToken? arrow = null;
        GreenNode? expressionBody = null;

        if (At(SyntaxKind.OpenBraceToken)) body = ParseBlock();
        else if (At(SyntaxKind.FatArrowToken))
        {
            arrow = EatToken();
            expressionBody = ParseExpression();
        }

        return new OperatorDeclarationGreen(attributes, modifiers, fixity, operatorKeyword,
            operatorToken, second, parameters, returnClause, body, arrow, expressionBody);
    }

    private GreenNode ParseAccessorList() => WithBracesAllowed<GreenNode>(() =>
    {
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (!At(SyntaxKind.CloseBraceToken) && !At(SyntaxKind.EndOfFile))
        {
            var before = _index;
            items.Add(ParseAccessor());
            if (_index == before) SkipCurrentToken();
        }

        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new AccessorListGreen(open, GreenList.Create(items.ToImmutable()), close);
    });

    private GreenNode ParseAccessor()
    {
        var attributes = ParseAttributes();
        var modifiers = ParseModifiers();

        var keyword = Current.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
            or SyntaxKind.WillSetKeyword or SyntaxKind.DidSetKeyword
            ? EatToken()
            : EatToken(SyntaxKind.GetKeyword);

        GreenToken? open = null;
        GreenToken? parameterName = null;
        GreenToken? close = null;
        if (At(SyntaxKind.OpenParenToken))
        {
            open = EatToken();
            parameterName = EatIdentifier();
            close = EatToken(SyntaxKind.CloseParenToken);
        }

        GreenNode? body = null;
        GreenToken? arrow = null;
        GreenNode? expressionBody = null;

        if (At(SyntaxKind.OpenBraceToken)) body = ParseBlock();
        else if (At(SyntaxKind.FatArrowToken))
        {
            arrow = EatToken();
            expressionBody = ParseExpression();
        }

        return new AccessorGreen(attributes, modifiers, keyword, open, parameterName, close,
            body, arrow, expressionBody);
    }

    // =======================================================================
    // Interpolated strings
    // =======================================================================

    /// <summary>
    /// Rebuilds a string literal token whose text contains <c>\(...)</c> holes
    /// into a node whose holes are real expressions.
    /// </summary>
    /// <remarks>
    /// <para>The lexer keeps an interpolated string as one token, because the
    /// holes nest arbitrarily and tracking that in the lexer would make it
    /// stateful. The parser takes the token apart again here, slicing the
    /// original characters so that the pieces tile the token exactly, and
    /// re-entering itself for each hole.</para>
    /// <para>Diagnostics from inside a hole are shifted into file
    /// coordinates, so they point where the reader is looking.</para>
    /// </remarks>
    private GreenNode ParseInterpolatedString()
    {
        var token = Current;
        var tokenTextStart = _tokenPosition + token.LeadingTriviaWidth;
        var parts = token.Parts;
        var text = token.Text;

        EatToken();

        var firstRaw = parts[0].Span.Start;
        var lastRaw = parts[^1].Span.End;

        var open = new GreenToken(
            SyntaxKind.InterpolatedStringStartToken, text[..firstRaw], token.Leading, []);

        var contents = ImmutableArray.CreateBuilder<GreenNode>(parts.Length);

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var raw = text[part.Span.Start..part.Span.End];

            if (part.Kind == StringPartKind.Literal)
            {
                contents.Add(new InterpolatedTextGreen(new GreenToken(
                    SyntaxKind.InterpolatedTextToken, raw, [], [], part.Text)));
                continue;
            }

            contents.Add(ParseInterpolationHole(part, raw, tokenTextStart));
        }

        var close = new GreenToken(
            SyntaxKind.InterpolatedStringEndToken, text[lastRaw..], [], token.Trailing,
            terminatesStatement: token.TerminatesStatement);

        return new InterpolatedStringExpressionGreen(
            open, GreenList.Create(contents.ToImmutable()), close);
    }

    private GreenNode ParseInterpolationHole(StringPart part, string raw, int tokenTextStart)
    {
        // raw is '\(' + inner + ')'.
        var openText = raw[..2];
        var inner = part.Text;

        var holeStart = tokenTextStart + part.Span.Start + openText.Length;

        var source = new SourceText(inner, "<interpolation>");
        var inner_parser = new Parser(source, _options);
        var expression = inner_parser.ParseExpression();

        foreach (var diagnostic in inner_parser._diagnostics)
        {
            _diagnostics.Add(diagnostic with
            {
                Span = new TextSpan(diagnostic.Span.Start + holeStart, diagnostic.Span.Length),
            });
        }

        // Whatever the expression did not cover rides on the closing paren, so
        // the hole still tiles.
        var covered = expression.FullWidth;
        var leftover = covered < inner.Length ? inner[covered..] : string.Empty;

        ImmutableArray<GreenTrivia> closeLeading = leftover.Length == 0
            ? []
            : [new GreenTrivia(
                leftover.Trim().Length == 0
                    ? SyntaxKind.WhitespaceTrivia
                    : SyntaxKind.SkippedTokensTrivia,
                leftover)];

        var open = new GreenToken(SyntaxKind.InterpolationStartToken, openText, [], []);
        var close = new GreenToken(
            SyntaxKind.InterpolationEndToken, raw[^1..], closeLeading, []);

        return new InterpolationGreen(open, expression, close);
    }

}

internal static class ParserExtensions
{
    public static bool IsContextualKeywordToken(this GreenToken token) =>
        SyntaxFacts.IsContextualKeyword(token.Kind);

    public static bool IsKeywordToken(this GreenToken token) =>
        SyntaxFacts.IsKeyword(token.Kind);
}
