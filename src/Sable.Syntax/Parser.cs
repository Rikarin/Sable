using System.Collections.Immutable;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>
///     A hand-written recursive-descent parser.
/// </summary>
/// <remarks>
///     <para>
///         Hand-written rather than generated, for the reason given in
///         docs/04-implementation-plan.md §0.4: error recovery and incremental
///         reparsing both need hand control.
///     </para>
///     <para>
///         Error recovery never drops text. A token that cannot be used is
///         folded into <see cref="SyntaxKind.SkippedTokensTrivia" /> on the next token
///         it consumes, and a token that is required but absent is inserted as a
///         zero-width missing token. Both keep the full-fidelity round-trip
///         invariant.
///     </para>
///     <para>
///         This milestone parses types and expressions. Declarations,
///         statements, patterns, closures and structured string interpolation are not
///         implemented yet; an interpolated string is a single literal token whose
///         holes are available through <see cref="SyntaxToken.Parts" />.
///     </para>
/// </remarks>
internal sealed class Parser {
    readonly ImmutableArray<GreenToken> _tokens;

    /// <summary>
    ///     Tokens the parser split in place, such as the <c>&gt;&gt;</c> that
    ///     closes two generic argument lists. Splitting is rare, so an overlay
    ///     costs far less than copying the whole stream to make it mutable.
    /// </summary>
    Dictionary<int, GreenToken>? _split;

    /// <summary>Absolute offset of the token at <see cref="_index" />.</summary>
    int _tokenPosition;

    readonly ParseOptions _options;
    readonly IncrementalReuse? _reuse;
    readonly DiagnosticBag _diagnostics = new();
    int _index;

    /// <summary>Full text of tokens skipped by error recovery, not yet re-attached.</summary>
    readonly List<GreenTrivia> _skipped = [];

    /// <summary>
    ///     True while parsing an expression whose end is marked by '{' — an `if`
    ///     condition, a `for` sequence, a `match` subject. In that position a brace
    ///     opens the statement's body, so it may not be read as a record literal or
    ///     a trailing closure. Parentheses and brackets clear it again.
    /// </summary>
    bool _noBrace;

    /// <summary>
    ///     True while parsing an expression that <c>=&gt;</c> terminates — a match
    ///     arm's <c>where</c> clause. There a trailing <c>ident =&gt;</c> is the arm
    ///     separator, not a one-parameter closure.
    /// </summary>
    bool _noArrow;

    /// <summary>Inside brackets both suppressions lift again.</summary>
    T WithBracesAllowed<T>(Func<T> parse) {
        var savedBrace = _noBrace;
        var savedArrow = _noArrow;
        _noBrace = false;
        _noArrow = false;
        try {
            return parse();
        } finally {
            _noBrace = savedBrace;
            _noArrow = savedArrow;
        }
    }

    T WithArrowSuppressed<T>(Func<T> parse) {
        var savedBrace = _noBrace;
        var savedArrow = _noArrow;
        _noBrace = true;
        _noArrow = true;
        try {
            return parse();
        } finally {
            _noBrace = savedBrace;
            _noArrow = savedArrow;
        }
    }

    T WithBracesSuppressed<T>(Func<T> parse) {
        var saved = _noBrace;
        _noBrace = true;
        try {
            return parse();
        } finally {
            _noBrace = saved;
        }
    }

    Parser(SourceText source, ParseOptions options)
        : this(
            options,
            Lexer.TokenizeGreen(source, options, out var lexDiagnostics, out _),
            lexDiagnostics,
            null
        ) { }

    Parser(
        ParseOptions options,
        ImmutableArray<GreenToken> tokens,
        IReadOnlyList<Diagnostic> diagnostics,
        IncrementalReuse? reuse
    ) {
        _options = options;
        _reuse = reuse;
        _tokens = tokens;
        _diagnostics.AddRange(diagnostics);
    }

    /// <summary>
    ///     The lowest and highest token indices read since the current member
    ///     began; see <see cref="ParseMembers" />.
    /// </summary>
    int _lowestRead = int.MaxValue;

    int _highestRead = -1;

    /// <summary>
    ///     For each member parsed, the old token indices its parse read, by
    ///     node: an incremental parse may reuse the member only if every one
    ///     of them is unchanged (<see cref="IncrementalReuse" />).
    /// </summary>
    readonly Dictionary<GreenNode, TokenExtent> _memberExtents = new(ReferenceEqualityComparer.Instance);

    void NoteRead(int index) {
        // A lookahead that has given up reads only its own way out.
        if (_gaveUp) {
            return;
        }

        if (index < _lowestRead) {
            _lowestRead = index;
        }

        if (index > _highestRead) {
            _highestRead = index;
        }
    }

    /// <summary>The token as lexed, ignoring any split, noted as read.</summary>
    GreenToken LexedTokenAt(int index) {
        NoteRead(index);
        return _tokens[index];
    }

    GreenToken TokenAt(int index) {
        NoteRead(index);
        if (_split is not null && _split.TryGetValue(index, out var replaced)) {
            return replaced;
        }

        return _tokens[index];
    }

    public static SyntaxTree ParseExpression(SourceText source, ParseOptions options) {
        var parser = new Parser(source, options);
        return parser.Finish(source, parser.ParseExpression(), "expression");
    }

    public static SyntaxTree ParseType(SourceText source, ParseOptions options) {
        var parser = new Parser(source, options);
        return parser.Finish(source, parser.ParseType(), "type");
    }

    public static SyntaxTree ParseStatement(SourceText source, ParseOptions options) {
        var parser = new Parser(source, options);
        return parser.Finish(source, parser.ParseStatement(), "statement");
    }

    public static SyntaxTree ParseCompilationUnit(SourceText source, ParseOptions options) {
        var tokens = Lexer.TokenizeGreen(source, options, out var diagnostics, out var hasDirectives);

        return ParseCompilationUnitFrom(source, options, tokens, diagnostics, hasDirectives, null);
    }

    /// <summary>Parses an already-lexed token stream, optionally reusing old nodes.</summary>
    internal static SyntaxTree ParseCompilationUnitFrom(
        SourceText source,
        ParseOptions options,
        ImmutableArray<GreenToken> tokens,
        IReadOnlyList<Diagnostic> diagnostics,
        bool hasDirectives,
        IncrementalReuse? reuse
    ) {
        var parser = new Parser(options, tokens, diagnostics, reuse);
        var unit = parser.ParseCompilationUnit();

        return new SyntaxTree(
            source,
            unit.CreateRed(null, 0),
            default,
            parser._diagnostics.ToList(),
            options,
            tokens,
            hasDirectives,
            diagnostics,
            parser._memberExtents
        );
    }

    /// <summary>
    ///     Consumes anything left over, so that the root plus the end-of-file token
    ///     still account for every character in the file.
    /// </summary>
    SyntaxTree Finish(SourceText source, GreenNode root, string what) {
        if (Current.Kind != SyntaxKind.EndOfFile) {
            ReportUnexpected(
                ErrorCode.ERR_UnexpectedTrailingTokens,
                $"unexpected '{DescribeCurrent()}' after the end of the {what}"
            );
            while (Current.Kind != SyntaxKind.EndOfFile) {
                SkipCurrentToken();
            }
        }

        var eof = EatToken();
        var node = root.CreateRed(null, 0);
        return new(
            source,
            node,
            new(eof, null, root.FullWidth),
            _diagnostics.ToList(),
            _options
        );
    }

    // -----------------------------------------------------------------------
    // Token stream
    // -----------------------------------------------------------------------

    GreenToken Current => TokenAt(_index);
    GreenToken Peek(int offset) => TokenAt(Math.Min(_index + offset, _tokens.Length - 1));

    TextSpan CurrentSpan => new(_tokenPosition + Current.LeadingTriviaWidth, Current.Text.Length);

    bool At(SyntaxKind kind) => Current.Kind == kind;

    GreenToken EatToken() {
        var token = TokenAt(_index);
        if (_index < _tokens.Length - 1) {
            _index++;
            _tokenPosition += token.FullWidth;
            return AttachSkipped(token);
        }

        // The end of file cannot advance, so eating it twice would put its
        // trivia in the tree twice. Whoever eats it first gets it; anyone
        // after gets a zero-width stand-in, and the text stays exact.
        if (_endOfFileEaten) {
            return CreateMissing(SyntaxKind.EndOfFile);
        }

        _endOfFileEaten = true;
        return AttachSkipped(token);
    }

    /// <summary>True once the end-of-file token is in the tree.</summary>
    bool _endOfFileEaten;

    /// <summary>
    ///     How deeply expressions, types, patterns and statements may nest.
    /// </summary>
    /// <remarks>
    ///     Recursive descent spends stack per level, and a stack overflow kills
    ///     the process rather than throwing — no IDE can survive a file of five
    ///     thousand parentheses otherwise. Real code never comes close.
    /// </remarks>
    const int MaxNesting = 256;

    int _nesting;
    bool _reportedTooDeep;

    /// <summary>How many times the parse has reached the nesting limit, so that a member knows whether its own did.</summary>
    int _tooDeepCount;

    static GreenNode MissingExpression() => new IdentifierNameGreen(CreateMissing(SyntaxKind.IdentifierToken));

    /// <summary>
    ///     True when the token before the current one ends a statement, per
    ///     the newline rule of spec/lexical.md §4, so the current token starts
    ///     a new one and nothing may extend the expression before it.
    /// </summary>
    bool AtStatementStart => _index > 0 && TokenAt(_index - 1).TerminatesStatement;

    /// <summary>At the nesting limit: skips this level's text and says so.</summary>
    bool TooDeep() {
        GiveUpIfWrong();
        _deepest = Math.Max(_deepest, _nesting);
        if (_nesting < MaxNesting) {
            return false;
        }

        SkipTooDeep();
        return true;
    }

    /// <summary>
    ///     Past the nesting limit: reports it once, then skips to the bracket
    ///     that closes the group this level sits in. The skipped text rides on
    ///     the next token as trivia, so nothing is lost, and every outer level
    ///     then finds its own closing bracket and unwinds normally.
    /// </summary>
    void SkipTooDeep() {
        _tooDeepCount++;
        if (!_reportedTooDeep) {
            _reportedTooDeep = true;
            ReportUnexpected(
                ErrorCode.ERR_NestedTooDeeply,
                $"this is nested more than {MaxNesting} levels deep; the rest of it is not parsed"
            );
        }

        var depth = 0;
        while (!At(SyntaxKind.EndOfFile)) {
            switch (Current.Kind) {
                case SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenBraceToken:
                    depth++;
                    break;

                case SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken:
                    if (depth == 0) {
                        return;
                    }

                    depth--;
                    break;
            }

            SkipCurrentToken();
        }
    }

    // -----------------------------------------------------------------------
    // Lookahead by parsing
    // -----------------------------------------------------------------------

    /// <summary>A question about the text ahead that is answered by parsing it.</summary>
    enum LookaheadKind {
        /// <summary>Whether a compound type is written here, <see cref="AtCompoundTypeExpression" />; any error says no.</summary>
        CompoundType,

        /// <summary>Whether a <c>where</c> item is a constraint, <see cref="ParseWhereItem" />; errors do not matter.</summary>
        WhereConstraint
    }

    /// <summary>What an answer depends on besides the tokens and the nesting limit.</summary>
    readonly record struct LookaheadKey(LookaheadKind Kind, int Index, bool NoBrace, bool NoArrow);

    /// <param name="Lowest">The lowest index of a token the answer depends on.</param>
    /// <param name="Highest">The highest.</param>
    /// <param name="Nesting">How deeply nested the question was asked.</param>
    /// <param name="Deepest">How deeply nested its parse went.</param>
    /// <param name="TooDeep">Whether that reached the limit.</param>
    /// <param name="Member">The member being parsed (<see cref="_member" />).</param>
    readonly record struct LookaheadAnswer(bool Yes, int Lowest, int Highest, int Nesting, int Deepest, bool TooDeep, int Member) {
        /// <summary>
        ///     Whether the answer stands at another nesting: where the limit is
        ///     not reached it would be the same. Where it was, the question is
        ///     not asked again in the same member: a question is first asked
        ///     deep within other lookaheads, and asking again at each of the
        ///     nestings it then has would take a time that grows with their
        ///     number. Across members it is, so that what one member makes of
        ///     its tokens does not depend on another's lookahead.
        /// </summary>
        public bool HoldsAt(int nesting, int member) =>
            TooDeep
                ? nesting == Nesting || member == Member
                : Deepest + (nesting - Nesting) < MaxNesting;
    }

    /// <summary>
    ///     The answers so far. What a lookahead parses can ask the same
    ///     question of the text within it, and that text is then parsed again
    ///     — for an outer lookahead, or for real — and asks once more: without
    ///     this, each level of nesting doubles the work.
    /// </summary>
    readonly Dictionary<LookaheadKey, LookaheadAnswer> _answers = [];

    /// <summary>The splits made within a lookahead, with what each replaced.</summary>
    readonly List<(int Index, GreenToken? Before)> _splitLog = [];

    /// <summary>The innermost member being parsed, counted in the order they began, and how many have.</summary>
    int _member;

    int _memberCount;

    /// <summary>How many lookaheads this parse is within.</summary>
    int _lookahead;

    /// <summary>The deepest nesting since the innermost lookahead began.</summary>
    int _deepest;

    /// <summary>The suppressed error count past which the innermost lookahead's answer is no, or -1 if errors do not matter to it.</summary>
    int _errorLimit = -1;

    /// <summary>Whether the innermost lookahead has met an error, and is only unwinding.</summary>
    bool _gaveUp;

    /// <summary>
    ///     Ends a lookahead that an error has already answered, by moving it to
    ///     the end of the file, where every production stops.
    /// </summary>
    void GiveUpIfWrong() {
        if (_errorLimit >= 0 && !_gaveUp && _diagnostics.SuppressedErrors > _errorLimit) {
            _gaveUp = true;
            _index = _tokens.Length - 1;
        }
    }

    /// <summary>
    ///     Answers a question by parsing what is ahead, and puts everything
    ///     back as it was. The answer must be a function of the tokens, of
    ///     <see cref="LookaheadKey" /> and of the nesting limit alone, so that
    ///     it is the same whenever it is asked again: in a full parse and in an
    ///     incremental one too.
    /// </summary>
    bool Lookahead(LookaheadKind kind, Func<Parser, bool> parse) {
        // The second half of a split token is not what the stream says is here.
        var remembered = !_gaveUp && (_split is null || !_split.ContainsKey(_index));
        var key = new LookaheadKey(kind, _index, _noBrace, _noArrow);
        if (remembered && _answers.TryGetValue(key, out var known) && known.HoldsAt(_nesting, _member)) {
            NoteRead(known.Lowest);
            NoteRead(known.Highest);
            _deepest = Math.Max(_deepest, known.Deepest + (_nesting - known.Nesting));
            _tooDeepCount += known.TooDeep ? 1 : 0;
            return known.Yes;
        }

        var index = _index;
        var tokenPosition = _tokenPosition;
        var splits = _splitLog.Count;
        // A copy, not a count: the parse may attach the pending skipped text
        // to a token it then throws away, which empties the list, and
        // truncating would not bring that text back.
        var skipped = _skipped.ToArray();
        var endOfFileEaten = _endOfFileEaten;
        var statementEnd = _statementEndReportedAt;
        var reportedTooDeep = _reportedTooDeep;
        var tooDeepCount = _tooDeepCount;
        var errors = _diagnostics.SuppressedErrors;
        var errorLimit = _errorLimit;
        var gaveUp = _gaveUp;
        var (deepest, lowest, highest) = (_deepest, _lowestRead, _highestRead);

        _statementEndReportedAt = -1;
        _reportedTooDeep = false;
        _errorLimit = kind == LookaheadKind.CompoundType ? errors : -1;
        _deepest = _nesting;
        _lowestRead = int.MaxValue;
        _highestRead = -1;
        _lookahead++;

        // The frames of a lookahead come on top of those of the levels it parses.
        _nesting++;

        using (_diagnostics.Suppress()) {
            try {
                var yes = parse(this) && (_errorLimit < 0 || _diagnostics.SuppressedErrors == errors);
                if (remembered) {
                    _answers[key] = new(
                        yes,
                        Math.Min(_lowestRead, index),
                        Math.Max(_highestRead, index),
                        _nesting - 1,
                        _deepest,
                        _tooDeepCount != tooDeepCount,
                        _member
                    );
                }

                return yes;
            } finally {
                _lookahead--;
                _nesting--;
                _index = index;
                _tokenPosition = tokenPosition;
                for (var i = _splitLog.Count - 1; i >= splits; i--) {
                    var (at, before) = _splitLog[i];
                    if (before is null) {
                        _split!.Remove(at);
                    } else {
                        _split![at] = before;
                    }
                }

                _splitLog.RemoveRange(splits, _splitLog.Count - splits);
                _skipped.Clear();
                _skipped.AddRange(skipped);
                _endOfFileEaten = endOfFileEaten;
                _statementEndReportedAt = statementEnd;
                _reportedTooDeep = reportedTooDeep;
                _diagnostics.ForgetSuppressedErrors(errors);
                _errorLimit = errorLimit;
                _gaveUp = gaveUp;
                _deepest = Math.Max(deepest, _deepest);
                _lowestRead = Math.Min(lowest, _lowestRead);
                _highestRead = Math.Max(highest, _highestRead);
            }
        }
    }

    /// <summary>Consumes the expected token, or inserts a zero-width missing one.</summary>
    GreenToken EatToken(SyntaxKind kind) {
        if (Current.Kind == kind) {
            return EatToken();
        }

        var expected = SyntaxFacts.GetText(kind) ?? kind.ToString();
        _diagnostics.Error(
            ErrorCode.ERR_ExpectedToken,
            CurrentSpan,
            $"expected '{expected}' but found '{DescribeCurrent()}'"
        );
        return CreateMissing(kind);
    }

    GreenToken? TryEatToken(SyntaxKind kind) => Current.Kind == kind ? EatToken() : null;

    static GreenToken CreateMissing(SyntaxKind kind) => new(kind, string.Empty, [], [], isMissing: true);

    string DescribeCurrent() => Current.Kind == SyntaxKind.EndOfFile ? "end of file" : Current.Text;

    /// <summary>Moves the current token's text into the pending skipped-trivia buffer.</summary>
    void SkipCurrentToken() {
        var token = TokenAt(_index);
        _skipped.Add(new(SyntaxKind.SkippedTokensTrivia, token.ToFullString()));
        if (_index < _tokens.Length - 1) {
            _index++;
            _tokenPosition += token.FullWidth;
        }
    }

    /// <summary>Prepends any pending skipped text to a token's leading trivia.</summary>
    GreenToken AttachSkipped(GreenToken token) {
        if (_skipped.Count == 0) {
            return token;
        }

        var leading = ImmutableArray.CreateBuilder<GreenTrivia>(_skipped.Count + token.Leading.Length);
        leading.AddRange(_skipped);
        leading.AddRange(token.Leading);
        _skipped.Clear();
        return token.WithTrivia(leading.ToImmutable(), token.Trailing);
    }

    void ReportUnexpected(ErrorCode code, string message) => _diagnostics.Error(code, CurrentSpan, message);

    /// <summary>
    ///     Splits the current token after its first character, returning the first
    ///     half and leaving the remainder in the stream.
    /// </summary>
    /// <remarks>
    ///     The lexer takes the longest match, which is wrong at two points in type
    ///     syntax: <c>List&lt;List&lt;Int&gt;&gt;</c> ends on one <c>&gt;&gt;</c>
    ///     token, and <c>Int??</c> ends on one <c>??</c>. Splitting here keeps the
    ///     lexer context-free and the round-trip exact, because the two halves
    ///     carry the original token's leading and trailing trivia respectively.
    /// </remarks>
    GreenToken SplitLeadingCharacter(SyntaxKind firstKind, SyntaxKind remainderKind) {
        var token = TokenAt(_index);
        var first = new GreenToken(firstKind, token.Text[..1], token.Leading, []);
        var remainder = new GreenToken(
            remainderKind,
            token.Text[1..],
            [],
            token.Trailing,
            terminatesStatement: token.TerminatesStatement
        );

        if (_lookahead > 0) {
            _splitLog.Add((_index, _split is not null && _split.TryGetValue(_index, out var before) ? before : null));
        }

        (_split ??= [])[_index] = remainder;
        _tokenPosition += token.LeadingTriviaWidth + 1;
        return AttachSkipped(first);
    }

    /// <summary>Consumes a <c>&gt;</c>, splitting <c>&gt;&gt;</c> if necessary.</summary>
    GreenToken EatGreaterThan() =>
        Current.Kind switch {
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

    GreenNode ParseAssignment() {
        var left = ParseTernary();
        if (!SyntaxFacts.IsAssignmentOperator(Current.Kind) || AtStatementStart) {
            return left;
        }

        var op = EatToken();
        var right = ParseAssignment(); // right-associative
        return new AssignmentExpressionGreen(left, op, right);
    }

    GreenNode ParseTernary() {
        var condition = ParseCoalesce();
        if (!At(SyntaxKind.QuestionToken) || AtStatementStart) {
            return condition;
        }

        var question = EatToken();
        var whenTrue = ParseExpression();
        var colon = EatToken(SyntaxKind.ColonToken);
        var whenFalse = ParseTernary();
        return new ConditionalExpressionGreen(condition, question, whenTrue, colon, whenFalse);
    }

    GreenNode ParseCoalesce() {
        var left = ParseLogicalOr();
        if (!At(SyntaxKind.QuestionQuestionToken) || AtStatementStart) {
            return left;
        }

        var op = EatToken();
        var right = ParseCoalesce(); // right-associative
        return new BinaryExpressionGreen(left, op, right);
    }

    /// <summary>
    ///     The left-associative binary precedence levels, loosest first, per
    ///     spec/SYNTAX.md §16. Ranges sit between relational and shift, and are
    ///     parsed by <see cref="ParseRange" /> since they do not associate.
    /// </summary>
    enum BinaryLevel {
        LogicalOr,
        LogicalAnd,
        BitOr,
        BitXor,
        BitAnd,
        Equality,
        Relational,
        Shift,
        Additive,
        Multiplicative,
        None,
    }

    static BinaryLevel LevelOf(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.PipePipeToken => BinaryLevel.LogicalOr,
            SyntaxKind.AmpersandAmpersandToken => BinaryLevel.LogicalAnd,
            SyntaxKind.PipeToken => BinaryLevel.BitOr,
            SyntaxKind.CaretToken => BinaryLevel.BitXor,
            SyntaxKind.AmpersandToken => BinaryLevel.BitAnd,
            SyntaxKind.EqualsEqualsToken
                or SyntaxKind.BangEqualsToken
                or SyntaxKind.EqualsEqualsEqualsToken
                or SyntaxKind.BangEqualsEqualsToken => BinaryLevel.Equality,
            SyntaxKind.LessToken
                or SyntaxKind.LessEqualsToken
                or SyntaxKind.GreaterToken
                or SyntaxKind.GreaterEqualsToken => BinaryLevel.Relational,
            SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken => BinaryLevel.Shift,
            SyntaxKind.PlusToken or SyntaxKind.MinusToken
                or SyntaxKind.AmpersandPlusToken or SyntaxKind.AmpersandMinusToken => BinaryLevel.Additive,
            SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken
                or SyntaxKind.AmpersandStarToken => BinaryLevel.Multiplicative,
            _ => BinaryLevel.None,
        };

    GreenNode ParseLogicalOr() => ParseBinary(BinaryLevel.LogicalOr);

    GreenNode ParseShift() => ParseBinary(BinaryLevel.Shift);

    /// <summary>
    ///     One left-associative level: operands from the next tighter level,
    ///     joined by this level's operators.
    /// </summary>
    /// <remarks>
    ///     One method for every level, dispatched on an enum, rather than a
    ///     method per level that hands a delegate and an operator array to a
    ///     shared helper: that allocated both for every operand of every
    ///     expression, on the parser's hottest path.
    /// </remarks>
    GreenNode ParseBinary(BinaryLevel level) {
        var left = ParseOperand(level);
        while (LevelOf(Current.Kind) == level && !AtStatementStart) {
            var op = EatToken();
            var right = ParseOperand(level);
            left = new BinaryExpressionGreen(left, op, right);
        }

        return left;
    }

    GreenNode ParseOperand(BinaryLevel level) =>
        level switch {
            BinaryLevel.Relational => ParseRange(),
            BinaryLevel.Multiplicative => ParseUnary(),
            _ => ParseBinary(level + 1),
        };

    /// <summary>Ranges are non-associative and may omit either operand.</summary>
    GreenNode ParseRange() {
        if (IsRangeOperator(Current.Kind)) {
            var prefixOp = EatToken();
            var prefixRight = ParseShift();
            return new RangeExpressionGreen(null, prefixOp, prefixRight);
        }

        var left = ParseShift();
        if (!IsRangeOperator(Current.Kind) || AtStatementStart) {
            return left;
        }

        // Where a brace opens the statement's body, 'for i in 3... {' is a
        // range with no upper bound, not one bounded by a closure.
        var op = EatToken();
        var right = CanStartExpression(Current.Kind)
            && !AtStatementStart
            && !(_noBrace && At(SyntaxKind.OpenBraceToken))
                ? ParseShift()
                : null;
        return new RangeExpressionGreen(left, op, right);
    }

    static bool IsRangeOperator(SyntaxKind kind) => kind is SyntaxKind.DotDotLessToken or SyntaxKind.DotDotDotToken;

    GreenNode ParseUnary() {
        if (TooDeep()) {
            return MissingExpression();
        }

        _nesting++;
        try {
            return ParseUnaryCore();
        } finally {
            _nesting--;
        }
    }

    GreenNode ParseUnaryCore() {
        if (At(SyntaxKind.TryKeyword)) {
            var tryKeyword = EatToken();
            // 'try! f()' stops at an error; 'try !f()' is the negation of what
            // 'try f()' gives, so a '!' is the keyword's only against it.
            var marker = Current.Kind is SyntaxKind.QuestionToken || At(SyntaxKind.BangToken) && !HasSpaceBeforeCurrent
                ? EatToken()
                : null;
            // 'try' covers the whole expression it is written on, as in
            // Swift: one 'try' can stand for several calls. It stops short of
            // assignment, so 'x = try f()' still assigns.
            var operand = ParseTernary();

            GreenToken? catchKeyword = null;
            GreenNode? catchBody = null;
            if (At(SyntaxKind.CatchKeyword)) {
                catchKeyword = EatToken();
                catchBody = At(SyntaxKind.OpenBraceToken) ? ParseBlock() : ParseExpression();
            }

            return new TryExpressionGreen(tryKeyword, marker, operand, catchKeyword, catchBody);
        }

        if (IsPrefixOperator(Current.Kind)) {
            var op = EatToken();
            return new PrefixUnaryExpressionGreen(op, ParseUnary());
        }

        return ParsePostfix(ParsePrimary());
    }

    static bool IsPrefixOperator(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.MinusToken
                or SyntaxKind.PlusToken
                or SyntaxKind.BangToken
                or SyntaxKind.TildeToken
                or SyntaxKind.CaretToken
                or SyntaxKind.AmpersandToken => true,
            SyntaxKind.AwaitKeyword or SyntaxKind.MoveKeyword => true,
            _ => false,
        };

    GreenNode ParsePostfix(GreenNode expression) {
        while (true) {
            // A new line that starts a new statement: '(1, 2)' or '[0]' there
            // is not a call or a subscript of what came before.
            if (AtStatementStart) {
                return expression;
            }

            switch (Current.Kind) {
                case SyntaxKind.DotToken:
                case SyntaxKind.QuestionDotToken: {
                    var op = EatToken();

                    // 'pair.0.1' lexes '0.1' as a float literal, because by
                    // spec/lexical.md §6 that is what it is. Here it is two
                    // indices, so the token is taken apart.
                    if (IsTupleIndexPath(Current)) {
                        expression = ParseTupleIndexPath(expression, op);
                        continue;
                    }

                    var name = ParseSimpleName(true);
                    expression = new MemberAccessExpressionGreen(expression, op, name);
                    continue;
                }

                case SyntaxKind.OpenParenToken: {
                    var arguments = ParseArgumentList(SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken);
                    var trailing = TryParseTrailingClosure();
                    expression = new InvocationExpressionGreen(expression, null, arguments, trailing, ParseLabeledClosures(trailing));
                    continue;
                }

                // 'items.filter { ... }' is a call whose only argument is the
                // closure; it has no parenthesised argument list.
                case SyntaxKind.OpenBraceToken when CanTakeTrailingClosure(): {
                    var closure = ParseBraceClosure(null);
                    expression = new InvocationExpressionGreen(expression, null, null, closure, ParseLabeledClosures(closure));
                    continue;
                }

                case SyntaxKind.OpenBracketToken: {
                    var arguments = ParseArgumentList(SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken);
                    expression = new ElementAccessExpressionGreen(expression, null, arguments);
                    continue;
                }

                // '?(' and '?[' are optional-chained calls and indexes, written
                // against what they chain from; a bare '?', or one with space
                // before it — 'n > 1 ? [1] : [2]' — is the conditional operator.
                case SyntaxKind.QuestionToken when Peek(1).Kind == SyntaxKind.OpenParenToken && !HasSpaceBeforeCurrent: {
                    var question = EatToken();
                    var arguments = ParseArgumentList(SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken);
                    var trailing = TryParseTrailingClosure();
                    expression = new InvocationExpressionGreen(
                        expression,
                        question,
                        arguments,
                        trailing,
                        ParseLabeledClosures(trailing)
                    );
                    continue;
                }

                case SyntaxKind.QuestionToken when Peek(1).Kind == SyntaxKind.OpenBracketToken && !HasSpaceBeforeCurrent: {
                    var question = EatToken();
                    var arguments = ParseArgumentList(SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken);
                    expression = new ElementAccessExpressionGreen(expression, question, arguments);
                    continue;
                }

                case SyntaxKind.BangToken: {
                    var bang = EatToken();
                    expression = new PostfixUnaryExpressionGreen(expression, bang);
                    continue;
                }

                case SyntaxKind.AsKeyword: {
                    var asKeyword = EatToken();
                    var marker = Current.Kind is SyntaxKind.QuestionToken or SyntaxKind.BangToken
                        ? EatToken()
                        : null;
                    expression = new CastExpressionGreen(
                        expression,
                        null,
                        asKeyword,
                        marker,
                        ParseType(true)
                    );
                    continue;
                }

                case SyntaxKind.UncheckedKeyword when Peek(1).Kind == SyntaxKind.AsKeyword: {
                    var unchecked_ = EatToken();
                    var asKeyword = EatToken();
                    expression = new CastExpressionGreen(
                        expression,
                        unchecked_,
                        asKeyword,
                        null,
                        ParseType(true)
                    );
                    continue;
                }

                case SyntaxKind.IsKeyword: {
                    var isKeyword = EatToken();
                    expression = new TypeTestExpressionGreen(expression, isKeyword, ParseType(true));
                    continue;
                }

                default:
                    return expression;
            }
        }
    }

    GreenNode ParseSimpleName(bool memberContext = false) {
        var identifier = memberContext ? EatMemberName() : EatIdentifier();
        if (At(SyntaxKind.LessToken) && TryScanTypeArgumentList()) {
            return new GenericNameGreen(identifier, ParseTypeArgumentList());
        }

        return new IdentifierNameGreen(identifier);
    }

    /// <summary>
    ///     A member name after '.' may be any keyword: `super.init`, `Type.self`,
    ///     `File.open`, `x.default`. The binder decides whether the name means
    ///     anything; the parser only has to not reject it.
    /// </summary>
    GreenToken EatMemberName() {
        // 'pair.0' names a tuple element by position; the token is the index.
        if (Current.Kind == SyntaxKind.IntegerLiteralToken) {
            return EatToken();
        }

        // A keyword that begins a statement, first on its line, is the next
        // statement rather than a member: 'p.' left unfinished above 'let b'
        // must not swallow the 'let'.
        if (StartsStatement(Current.Kind) && HasNewLineBeforeCurrent) {
            _diagnostics.Error(
                ErrorCode.ERR_ExpectedIdentifier,
                CurrentSpan,
                $"expected a member name but found '{DescribeCurrent()}'"
            );
            return CreateMissing(SyntaxKind.IdentifierToken);
        }

        return Current.IsKeywordToken() ? EatToken() : EatIdentifier();
    }

    bool HasNewLineBeforeCurrent =>
        Current.Leading.Any(static t => t.Kind == SyntaxKind.NewLineTrivia)
        || (_index > 0 && LexedTokenAt(_index - 1).Trailing.Any(static t => t.Kind == SyntaxKind.NewLineTrivia));

    static bool StartsStatement(SyntaxKind kind) =>
        kind is SyntaxKind.LetKeyword or SyntaxKind.VarKeyword or SyntaxKind.IfKeyword
            or SyntaxKind.GuardKeyword or SyntaxKind.WhileKeyword or SyntaxKind.RepeatKeyword
            or SyntaxKind.ForKeyword or SyntaxKind.ReturnKeyword or SyntaxKind.ThrowKeyword or SyntaxKind.YieldKeyword
            or SyntaxKind.BreakKeyword or SyntaxKind.ContinueKeyword or SyntaxKind.DoKeyword
            or SyntaxKind.DeferKeyword or SyntaxKind.FnKeyword;

    /// <summary>True for a float literal that is really a path of indices.</summary>
    static bool IsTupleIndexPath(GreenToken token) {
        if (token.Kind != SyntaxKind.FloatLiteralToken) {
            return false;
        }

        var text = token.Text;
        var sawDigit = false;

        foreach (var c in text) {
            if (char.IsAsciiDigit(c)) {
                sawDigit = true;
                continue;
            }

            if (c == '.' && sawDigit) {
                continue;
            }

            return false;
        }

        return text.Contains('.', StringComparison.Ordinal) && !text.EndsWith('.');
    }

    /// <summary>A backslash outside a string starts a key path where a member, or the root's type, comes after it.</summary>
    bool KeyPathFollows() => Peek(1).Kind is SyntaxKind.DotToken or SyntaxKind.IdentifierToken;

    /// <summary><c>\.address?.city</c>, <c>\Customer.name</c>: member accesses of the root, which is all of a key path.</summary>
    GreenNode ParseKeyPath() {
        var backslash = EatToken();
        GreenNode body = new KeyPathRootExpressionGreen(backslash, At(SyntaxKind.DotToken) ? null : ParseSimpleNameType());
        if (!At(SyntaxKind.DotToken)) {
            return new KeyPathExpressionGreen(new MemberAccessExpressionGreen(body, EatToken(SyntaxKind.DotToken), ParseSimpleName(true)));
        }

        while (Current.Kind is SyntaxKind.DotToken or SyntaxKind.QuestionDotToken && !AtStatementStart) {
            var op = EatToken();
            body = IsTupleIndexPath(Current)
                ? ParseTupleIndexPath(body, op)
                : new MemberAccessExpressionGreen(body, op, ParseSimpleName(true));
        }

        return new KeyPathExpressionGreen(body);
    }

    /// <summary>
    ///     Turns <c>.0.1</c> into two member accesses, keeping the text exactly.
    /// </summary>
    /// <remarks>
    ///     The float token's leading trivia goes on the first index and its
    ///     trailing trivia on the last, with zero-width dots between, so
    ///     concatenating the tree still reproduces the source.
    /// </remarks>
    GreenNode ParseTupleIndexPath(GreenNode expression, GreenToken firstDot) {
        var token = EatToken();
        var parts = token.Text.Split('.');

        var dot = firstDot;
        for (var i = 0; i < parts.Length; i++) {
            var leading = i == 0 ? token.Leading : [];
            var trailing = i == parts.Length - 1 ? token.Trailing : [];

            var index = new GreenToken(SyntaxKind.IntegerLiteralToken, parts[i], leading, trailing);

            expression = new MemberAccessExpressionGreen(expression, dot, new IdentifierNameGreen(index));

            if (i + 1 < parts.Length) {
                dot = new GreenToken(SyntaxKind.DotToken, ".", [], []);
            }
        }

        return expression;
    }

    GreenToken EatIdentifier() {
        if (At(SyntaxKind.IdentifierToken)) {
            return EatToken();
        }

        // A contextual keyword is a perfectly good member name.
        if (Current.IsContextualKeywordToken()) {
            return EatToken();
        }

        _diagnostics.Error(
            ErrorCode.ERR_ExpectedIdentifier,
            CurrentSpan,
            $"expected an identifier but found '{DescribeCurrent()}'"
        );
        return CreateMissing(SyntaxKind.IdentifierToken);
    }

    /// <summary>
    ///     Tokens that can appear inside a type, used to decide cheaply whether a
    ///     speculative type parse is worth attempting at all.
    /// </summary>
    static bool CanAppearInType(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.IdentifierToken or SyntaxKind.CapitalSelfKeyword => true,
            SyntaxKind.DotToken or SyntaxKind.CommaToken or SyntaxKind.ColonToken => true,
            SyntaxKind.OpenParenToken or SyntaxKind.CloseParenToken => true,
            SyntaxKind.OpenBracketToken or SyntaxKind.CloseBracketToken => true,
            SyntaxKind.LessToken or SyntaxKind.GreaterToken => true,
            SyntaxKind.QuestionToken or SyntaxKind.QuestionQuestionToken => true,
            SyntaxKind.QuestionDotToken => true, // 'Int?.self' lexes the '?.' whole
            SyntaxKind.PipeToken or SyntaxKind.AmpersandToken or SyntaxKind.BangToken => true,
            SyntaxKind.ArrowToken or SyntaxKind.DotDotToken or SyntaxKind.DotDotLessToken => true,
            SyntaxKind.GreaterGreaterToken or SyntaxKind.DotDotDotToken => true,
            SyntaxKind.UnderscoreToken or SyntaxKind.StarToken => true,
            SyntaxKind.SomeKeyword or SyntaxKind.AnyKeyword or SyntaxKind.RefKeyword => true,
            SyntaxKind.AsyncKeyword
                or SyntaxKind.ThrowsKeyword
                or SyntaxKind.RethrowsKeyword => true,
            SyntaxKind.SelfKeyword => true,
            _ => false,
        };

    /// <summary>
    ///     True when a compound type written where an expression goes starts
    ///     here: one followed by <c>.self</c>, or a collection type called, as
    ///     <c>[String: Int]()</c> and <c>Int[&gt;..]()</c> call its initialiser.
    /// </summary>
    /// <remarks>
    ///     A simple name is left alone: <c>T.self</c> already works as a member
    ///     access on a name that happens to be a type, and routing it through here
    ///     would change which diagnostic a non-type name produces. What this is
    ///     for is <c>Token[&gt;..].self</c>, <c>Int?.self</c> and
    ///     <c>(Int) -&gt; Bool.self</c>, none of which parse as expressions.
    ///     A called type is only a dictionary type or a growable array's,
    ///     whose brackets no expression could fill: <c>xs[8]()</c> stays an
    ///     index called.
    /// </remarks>
    bool AtCompoundTypeExpression() {
        if (!CanAppearInType(Current.Kind)) {
            return false;
        }

        // 'T.self' and 'self.self': the member-access path has those.
        if (Peek(1).Kind == SyntaxKind.DotToken
            && Peek(2).Kind == SyntaxKind.SelfKeyword) {
            return false;
        }

        // A bounded scan for '.self', or for the ']' of a collection type
        // with '(' after it, over tokens a type could contain, so the
        // speculative parse below is only ever reached for something shaped
        // like one.
        var found = false;
        var growArray = false;
        for (var offset = 0; offset < MetatypeScanLimit; offset++) {
            var kind = Peek(offset).Kind;

            // '?.' counts: 'Int?.self' lexes the two characters as one token,
            // and the type parser splits it again.
            if (kind is SyntaxKind.DotToken or SyntaxKind.QuestionDotToken
                && Peek(offset + 1).Kind == SyntaxKind.SelfKeyword) {
                found = true;
                break;
            }

            growArray |= kind == SyntaxKind.OpenBracketToken && Peek(offset + 1).Kind == SyntaxKind.GreaterToken;
            if (kind == SyntaxKind.CloseBracketToken
                && Peek(offset + 1).Kind == SyntaxKind.OpenParenToken
                && (growArray || Current.Kind == SyntaxKind.OpenBracketToken)) {
                found = true;
                break;
            }

            if (!CanAppearInType(kind)) {
                return false;
            }
        }

        if (!found) {
            return false;
        }

        // A type must be one as written: '[missing] (x: Int) => x' is a
        // capture list, and '[Dog.self, Cat.self]' an array literal, read as a
        // type only by error recovery.
        return Lookahead(
            LookaheadKind.CompoundType,
            static parser => {
                var type = parser.ParseType();
                return parser.At(SyntaxKind.DotToken) && parser.Peek(1).Kind == SyntaxKind.SelfKeyword
                    || parser.At(SyntaxKind.OpenParenToken) && type.Kind is SyntaxKind.DictionaryType or SyntaxKind.GrowArrayType;
            }
        );
    }

    /// <summary>How far ahead to look for the <c>.self</c> of a metatype.</summary>
    const int MetatypeScanLimit = 64;

    GreenNode ParsePrimary() {
        if (AtCompoundTypeExpression()) {
            return new TypeExpressionGreen(ParseType());
        }

        switch (Current.Kind) {
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

            // '_ = f()' drops what it is given; '_' is no expression elsewhere.
            case SyntaxKind.UnderscoreToken when Peek(1).Kind == SyntaxKind.EqualsToken:
                return new DiscardExpressionGreen(EatToken());

            // 'self' and 'Self' both stand for a value here; the token says
            // which, and 'Self.self' is how a metatype is written.
            case SyntaxKind.SelfKeyword:
            case SyntaxKind.CapitalSelfKeyword:
                return new SelfExpressionGreen(EatToken());

            case SyntaxKind.SuperKeyword:
                return new SuperExpressionGreen(EatToken());

            case SyntaxKind.HashToken: {
                var hash = EatToken();

                // '#module' is a builtin though 'module' is a keyword (spec/lexical.md §9).
                return new BuiltinExpressionGreen(hash, At(SyntaxKind.ModuleKeyword) ? EatToken() : EatIdentifier());
            }

            case SyntaxKind.DotToken: {
                var dot = EatToken();
                return new ImplicitMemberAccessExpressionGreen(dot, ParseSimpleName(true));
            }

            case SyntaxKind.BackslashToken when KeyPathFollows():
                return ParseKeyPath();

            case SyntaxKind.MatchKeyword:
                return ParseMatchExpression();

            // 'let s = if x > 3 { "big" } else { "small" }': where a value is
            // wanted, an 'if' gives one. At the start of a statement it is
            // the statement, which the statement parser sees first.
            case SyntaxKind.IfKeyword:
                return new IfExpressionGreen(ParseIfStatement());

            // 'case .Gap => throw e', 'x ?? throw e': where a value is wanted,
            // a 'throw' leaves instead of giving one. It takes the whole
            // expression after it, as the statement does.
            case SyntaxKind.ThrowKeyword: {
                var throwKeyword = EatToken();
                return new ThrowExpressionGreen(throwKeyword, ParseExpression());
            }

            // Where a '{' opens the body — after 'if p.x ==' with its right
            // operand missing — it is the body, not a closure for an operand;
            // read as one, the body went with it and the '{' after went missing.
            case SyntaxKind.OpenBraceToken when !_noBrace:
                return ParseBraceClosure(null);

            case SyntaxKind.DollarToken: {
                var dollar = EatToken();

                // '$0.1' lexes '0.1' as a float literal: it is the first
                // parameter, then its element 1, so the token is taken apart.
                if (IsTupleIndexPath(Current)) {
                    var path = EatToken();
                    var parts = path.Text.Split('.');
                    var first = new GreenToken(SyntaxKind.IntegerLiteralToken, parts[0], path.Leading, parts.Length == 1 ? path.Trailing : []);
                    GreenNode expression = new ImplicitParameterExpressionGreen(dollar, first);
                    for (var i = 1; i < parts.Length; i++) {
                        var index = new GreenToken(SyntaxKind.IntegerLiteralToken, parts[i], [], i == parts.Length - 1 ? path.Trailing : []);
                        var dot = new GreenToken(SyntaxKind.DotToken, ".", [], []);
                        expression = new MemberAccessExpressionGreen(expression, dot, new IdentifierNameGreen(index));
                    }

                    return expression;
                }

                return new ImplicitParameterExpressionGreen(dollar, EatToken(SyntaxKind.IntegerLiteralToken));
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
                _diagnostics.Error(
                    ErrorCode.ERR_ExpectedExpression,
                    CurrentSpan,
                    $"expected an expression but found '{DescribeCurrent()}'"
                );
                return new IdentifierNameGreen(CreateMissing(SyntaxKind.IdentifierToken));
        }
    }

    /// <summary>
    ///     Distinguishes a record literal (<c>Point { x: 1 }</c>) from a plain name.
    /// </summary>
    /// <remarks>
    ///     Decided by pure lookahead over <c>ident ('.' ident)* '{'</c>, so nothing
    ///     is parsed twice and no token split has to be undone. Once statements
    ///     exist this will also need a "no record literal here" context flag, for
    ///     the same reason Swift needs one before a trailing closure.
    /// </remarks>
    GreenNode ParseIdentifierOrRecord() {
        if (!LooksLikeRecordLiteral()) {
            return ParseSimpleName();
        }

        var type = ParseTypeName();
        var open = EatToken(SyntaxKind.OpenBraceToken);
        var fields = ParseSeparatedList(SyntaxKind.CloseBraceToken, ParseFieldInitializer);
        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new RecordExpressionGreen(type, open, fields, close);
    }

    /// <summary>
    ///     True when <c>ident ('.' ident)* '{'</c> is followed by something only a
    ///     record literal can contain.
    /// </summary>
    /// <remarks>
    ///     <c>Point { x: 1 }</c> and <c>items.filter { $0.isActive }</c> have the
    ///     same shape up to the brace, so the contents decide: a record literal is
    ///     empty or starts <c>ident :</c>. That is why record literals have no
    ///     field shorthand — <c>Point { x }</c> would be indistinguishable from a
    ///     closure returning <c>x</c>.
    /// </remarks>
    bool LooksLikeRecordLiteral() {
        if (_noBrace) {
            return false;
        }

        var offset = 0;
        while (true) {
            if (Peek(offset).Kind != SyntaxKind.IdentifierToken) {
                return false;
            }

            offset++;

            // 'Box<Int> { ... }' names a constructed type.
            if (Peek(offset).Kind == SyntaxKind.LessToken) {
                var skipped = SkipBalancedAngles(offset);
                if (skipped < 0) {
                    return false;
                }

                offset = skipped;
            }

            if (Peek(offset).Kind == SyntaxKind.OpenBraceToken) {
                return Peek(offset + 1).Kind == SyntaxKind.CloseBraceToken
                    || (Peek(offset + 1).Kind == SyntaxKind.IdentifierToken
                        && Peek(offset + 2).Kind == SyntaxKind.ColonToken);
            }

            if (Peek(offset).Kind != SyntaxKind.DotToken) {
                return false;
            }

            offset++;
        }
    }

    /// <summary>
    ///     Returns the offset just past a balanced angle-bracket group, or -1 when
    ///     it is not balanced within a reasonable distance.
    /// </summary>
    int SkipBalancedAngles(int offset) {
        var depth = 0;
        for (var i = offset; i < offset + 64; i++) {
            switch (Peek(i).Kind) {
                case SyntaxKind.LessToken: depth++; break;
                case SyntaxKind.GreaterToken: depth--; break;
                case SyntaxKind.GreaterGreaterToken: depth -= 2; break;
                case SyntaxKind.EndOfFile:
                case SyntaxKind.OpenBraceToken:
                case SyntaxKind.SemicolonToken:
                    return -1;
            }

            if (depth <= 0) {
                return i + 1;
            }
        }

        return -1;
    }

    GreenNode ParseFieldInitializer() {
        var name = EatIdentifier();
        var colon = EatToken(SyntaxKind.ColonToken);
        return new FieldInitializerGreen(name, colon, ParseExpression());
    }

    GreenNode ParseParenthesizedOrTuple() => WithBracesAllowed(ParseParenthesizedOrTupleCore);

    GreenNode ParseParenthesizedOrTupleCore() {
        var open = EatToken();

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        var simple = !At(SyntaxKind.CloseParenToken); // '()' is the empty tuple
        GreenNode? onlyInner = null;

        if (!At(SyntaxKind.CloseParenToken)) {
            while (true) {
                var element = ParseTupleElement();
                if (element.Labelled) {
                    simple = false;
                }

                onlyInner ??= element.Inner;
                items.Add(element.Node);

                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) {
                    break;
                }

                items.Add(comma);
                simple = false;
                if (At(SyntaxKind.CloseParenToken)) {
                    break;
                }
            }
        }

        var close = EatToken(SyntaxKind.CloseParenToken);

        if (simple && items.Count == 1 && onlyInner is not null) {
            return new ParenthesizedExpressionGreen(open, onlyInner, close);
        }

        return new TupleExpressionGreen(open, GreenList.Create(items.ToImmutable()), close);
    }

    (GreenNode Node, bool Labelled, GreenNode Inner) ParseTupleElement() {
        GreenToken? name = null;
        GreenToken? colon = null;

        if (IsArgumentLabel()) {
            name = EatToken();
            colon = EatToken(SyntaxKind.ColonToken);
        }

        var expression = ParseExpression();
        return (new TupleElementGreen(name, colon, expression), name is not null, expression);
    }

    /// <summary>
    ///     True when the current token starts a <c>label:</c> prefix. Keywords are
    ///     allowed, so <c>parseExpr(tokens, in: arena)</c> reads as intended.
    /// </summary>
    bool IsArgumentLabel() =>
        (Current.Kind == SyntaxKind.IdentifierToken || Current.IsKeywordToken())
        && Peek(1).Kind == SyntaxKind.ColonToken;

    GreenNode ParseArrayOrDictionary() => WithBracesAllowed(ParseArrayOrDictionaryCore);

    GreenNode ParseArrayOrDictionaryCore() {
        var open = EatToken();

        // '[:]' is the empty dictionary.
        if (At(SyntaxKind.ColonToken) && Peek(1).Kind == SyntaxKind.CloseBracketToken) {
            var emptyColon = EatToken();
            return new DictionaryExpressionGreen(
                open,
                emptyColon,
                GreenList.Empty,
                EatToken(SyntaxKind.CloseBracketToken)
            );
        }

        if (At(SyntaxKind.CloseBracketToken)) {
            return new ArrayExpressionGreen(open, GreenList.Empty, EatToken(SyntaxKind.CloseBracketToken));
        }

        var first = ParseExpression();

        if (At(SyntaxKind.ColonToken)) {
            var colon = EatToken();
            var value = ParseExpression();

            var entries = ImmutableArray.CreateBuilder<GreenNode>();
            entries.Add(new KeyValuePairGreen(first, colon, value));

            while (TryEatToken(SyntaxKind.CommaToken) is { } comma) {
                entries.Add(comma);
                if (At(SyntaxKind.CloseBracketToken)) {
                    break;
                }

                entries.Add(ParseKeyValuePair());
            }

            return new DictionaryExpressionGreen(
                open,
                null,
                GreenList.Create(entries.ToImmutable()),
                EatToken(SyntaxKind.CloseBracketToken)
            );
        }

        var elements = ImmutableArray.CreateBuilder<GreenNode>();
        elements.Add(first);
        while (TryEatToken(SyntaxKind.CommaToken) is { } comma) {
            elements.Add(comma);
            if (At(SyntaxKind.CloseBracketToken)) {
                break;
            }

            elements.Add(ParseExpression());
        }

        return new ArrayExpressionGreen(
            open,
            GreenList.Create(elements.ToImmutable()),
            EatToken(SyntaxKind.CloseBracketToken)
        );
    }

    GreenNode ParseKeyValuePair() {
        var key = ParseExpression();
        var colon = EatToken(SyntaxKind.ColonToken);
        return new KeyValuePairGreen(key, colon, ParseExpression());
    }

    GreenNode ParseArgumentList(SyntaxKind openKind, SyntaxKind closeKind) =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(openKind);
                var arguments = ParseSeparatedList(closeKind, ParseArgument);
                var close = EatToken(closeKind);
                return new ArgumentListGreen(open, arguments, close);
            }
        );

    GreenNode ParseArgument() {
        GreenToken? name = null;
        GreenToken? colon = null;

        if (IsArgumentLabel()) {
            name = EatToken();
            colon = EatToken(SyntaxKind.ColonToken);
        }

        var modifier = Current.Kind is SyntaxKind.AmpersandToken
            or SyntaxKind.OutKeyword
            or SyntaxKind.MoveKeyword
            ? EatToken()
            : null;

        return new ArgumentGreen(name, colon, modifier, ParseExpression());
    }

    /// <summary>
    ///     Parses a comma-separated list terminated by <paramref name="closeKind" />,
    ///     allowing a trailing separator.
    /// </summary>
    GreenList ParseSeparatedList(SyntaxKind closeKind, Func<GreenNode> parseItem) {
        if (At(closeKind)) {
            return GreenList.Empty;
        }

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (true) {
            items.Add(parseItem());

            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) {
                break;
            }

            items.Add(comma);
            if (At(closeKind)) {
                break;
            }

            // Nothing consumed and nothing to consume: bail rather than spin.
            if (At(SyntaxKind.EndOfFile)) {
                break;
            }
        }

        return GreenList.Create(items.ToImmutable());
    }

    static bool HasInterpolation(GreenToken token) {
        foreach (var part in token.Parts) {
            if (part.Kind == StringPartKind.Interpolation) {
                return true;
            }
        }

        return false;
    }

    static bool CanStartExpression(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.IdentifierToken => true,
            SyntaxKind.IntegerLiteralToken
                or SyntaxKind.FloatLiteralToken
                or SyntaxKind.CharLiteralToken
                or SyntaxKind.StringLiteralToken
                or SyntaxKind.MultilineStringLiteralToken
                or SyntaxKind.RawStringLiteralToken => true,
            SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NoneKeyword => true,
            SyntaxKind.SelfKeyword
                or SyntaxKind.CapitalSelfKeyword
                or SyntaxKind.SuperKeyword => true,
            SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken => true,
            SyntaxKind.DotToken or SyntaxKind.HashToken => true,
            SyntaxKind.MinusToken
                or SyntaxKind.PlusToken
                or SyntaxKind.BangToken
                or SyntaxKind.TildeToken
                or SyntaxKind.CaretToken
                or SyntaxKind.AmpersandToken => true,
            SyntaxKind.AwaitKeyword or SyntaxKind.MoveKeyword or SyntaxKind.TryKeyword => true,
            SyntaxKind.MatchKeyword or SyntaxKind.IfKeyword or SyntaxKind.CapitalSelfKeyword => true,
            SyntaxKind.OpenBraceToken or SyntaxKind.DollarToken or SyntaxKind.BackslashToken => true,
            _ => false,
        };

    // =======================================================================
    // Types
    // =======================================================================

    /// <summary>Parses a type. Structure follows spec/grammar.ebnf §4.</summary>
    /// <param name="inExpression">
    ///     True for the type after <c>as</c> or <c>is</c>, where a spaced <c>?</c>
    ///     belongs to a conditional expression rather than to the type. See
    ///     <see cref="IsOptionalSuffix" />.
    /// </param>
    public GreenNode ParseType(bool inExpression = false) {
        var left = ParseIntersectionType(inExpression);
        while (At(SyntaxKind.PipeToken)) {
            var pipe = EatToken();
            left = new UnionTypeGreen(left, pipe, ParseIntersectionType(inExpression));
        }

        return left;
    }

    /// <summary>
    ///     Decides whether a <c>?</c> or <c>??</c> continues the type or starts an
    ///     operator.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The ambiguity is real: in <c>x as? T ?? fallback</c> the <c>??</c>
    ///         is the coalescing operator, while in <c>Int??</c> it is two optional
    ///         markers. Swift resolves the same case by whitespace and so does
    ///         Sable — a question mark that continues a type is written tight against
    ///         it.
    ///     </para>
    ///     <para>
    ///         A <c>??</c> always requires no preceding space, because a type
    ///         is never followed by a spaced <c>??</c> that belongs to it. A single
    ///         <c>?</c> only requires it inside an expression, so that declarations
    ///         may still be written <c>var x: Int ?</c>.
    ///     </para>
    /// </remarks>
    bool IsOptionalSuffix(bool inExpression) =>
        Current.Kind switch {
            SyntaxKind.QuestionToken => !inExpression || !HasSpaceBeforeCurrent,
            SyntaxKind.QuestionQuestionToken => !HasSpaceBeforeCurrent,
            _ => false,
        };

    /// <summary>
    ///     True when trivia separates the current token from the previous one.
    /// </summary>
    /// <remarks>
    ///     Trailing trivia attaches to the preceding token, so the space in
    ///     <c>T ??</c> lives on <c>T</c>, not on the <c>??</c>. Both sides have to
    ///     be checked.
    /// </remarks>
    bool HasSpaceBeforeCurrent =>
        Current.LeadingTriviaWidth > 0
        || (_index > 0 && LexedTokenAt(_index - 1).TrailingTriviaWidth > 0);

    GreenNode ParseIntersectionType(bool inExpression = false) {
        var left = ParsePostfixType(inExpression);

        // A '&' that is followed by something which can start a type is protocol
        // composition; otherwise ParsePostfixType already took it as a reference
        // suffix, so reaching here means composition.
        while (At(SyntaxKind.AmpersandToken) && CanFollowCompositionAmpersand(Peek(1).Kind)) {
            var ampersand = EatToken();
            left = new CompositionTypeGreen(left, ampersand, ParsePostfixType(inExpression));
        }

        return left;
    }

    GreenNode ParsePostfixType(bool inExpression = false) {
        var type = ParsePrimaryType();

        while (true) {
            switch (Current.Kind) {
                case SyntaxKind.QuestionToken when IsOptionalSuffix(inExpression):
                    type = new OptionalTypeGreen(type, EatToken());
                    continue;

                // 'Int??' is an optional of an optional; the lexer gave us one
                // token for the two question marks.
                case SyntaxKind.QuestionQuestionToken when IsOptionalSuffix(inExpression):
                    type = new OptionalTypeGreen(
                        type,
                        SplitLeadingCharacter(SyntaxKind.QuestionToken, SyntaxKind.QuestionToken)
                    );
                    continue;

                // 'Int?.self' and 'Int?.Type' lex '?.' whole, and here the
                // '?' belongs to the type while the '.' introduces the
                // metatype, as a value or as a type.
                case SyntaxKind.QuestionDotToken
                    when Peek(1).Kind == SyntaxKind.SelfKeyword
                         || Peek(1) is { Kind: SyntaxKind.IdentifierToken, Text: "Type" }:
                    type = new OptionalTypeGreen(
                        type,
                        SplitLeadingCharacter(SyntaxKind.QuestionToken, SyntaxKind.DotToken)
                    );
                    continue;

                case SyntaxKind.BangToken:
                    type = new ImplicitlyUnwrappedTypeGreen(type, EatToken());
                    continue;

                // '&' is a reference suffix unless a type follows, in which case
                // it is protocol composition and belongs to the caller.
                case SyntaxKind.AmpersandToken when !CanFollowCompositionAmpersand(Peek(1).Kind): {
                    var ampersand = EatToken();
                    var spec = At(SyntaxKind.LessToken) ? ParseRelativeReferenceSpec() : null;
                    var question = TryEatToken(SyntaxKind.QuestionToken);
                    type = new ReferenceTypeGreen(type, ampersand, spec, question);
                    continue;
                }

                case SyntaxKind.OpenBracketToken:
                    type = ParseArraySuffix(type);
                    continue;

                // '.self' is the metatype of the type so far, not a member
                // of it: no type has a member called 'self'.
                case SyntaxKind.DotToken when Peek(1).Kind != SyntaxKind.SelfKeyword: {
                    var dot = EatToken();
                    type = new QualifiedNameGreen(type, dot, ParseSimpleNameType());
                    continue;
                }

                default:
                    return type;
            }
        }
    }

    GreenNode ParseRelativeReferenceSpec() {
        var less = EatToken(SyntaxKind.LessToken);
        var width = EatIdentifier();
        var inKeyword = TryEatToken(SyntaxKind.InKeyword);
        var pool = inKeyword is null ? null : EatIdentifier();
        var greater = EatGreaterThan();
        return new RelativeReferenceSpecGreen(less, width, inKeyword, pool, greater);
    }

    GreenNode ParseArraySuffix(GreenNode element) {
        var open = EatToken(SyntaxKind.OpenBracketToken);

        // T[]
        if (At(SyntaxKind.CloseBracketToken)) {
            return new FixedSizeArrayTypeGreen(element, open, null, EatToken());
        }

        // T[:]
        if (At(SyntaxKind.ColonToken)) {
            var colon = EatToken();
            return new SliceTypeGreen(
                element,
                open,
                colon,
                EatToken(SyntaxKind.CloseBracketToken)
            );
        }

        // T[..] and T[..k]
        if (At(SyntaxKind.DotDotToken)) {
            var dotDot = EatToken();
            var capacity = At(SyntaxKind.CloseBracketToken) ? null : ParseExpression();
            return new InlineArrayTypeGreen(
                element,
                open,
                dotDot,
                capacity,
                EatToken(SyntaxKind.CloseBracketToken)
            );
        }

        // T[>..] and T[>..<] — the shrink form lexes '..<' as one token.
        if (At(SyntaxKind.GreaterToken)) {
            var greater = EatToken();
            var range = Current.Kind is SyntaxKind.DotDotToken or SyntaxKind.DotDotLessToken
                ? EatToken()
                : EatToken(SyntaxKind.DotDotToken);
            return new GrowArrayTypeGreen(
                element,
                open,
                greater,
                range,
                EatToken(SyntaxKind.CloseBracketToken)
            );
        }

        // T[k]
        var size = ParseExpression();
        return new FixedSizeArrayTypeGreen(
            element,
            open,
            size,
            EatToken(SyntaxKind.CloseBracketToken)
        );
    }

    GreenNode ParsePrimaryType() {
        if (TooDeep()) {
            return new SimpleNameTypeGreen(CreateMissing(SyntaxKind.IdentifierToken), null);
        }

        _nesting++;
        try {
            return ParsePrimaryTypeCore();
        } finally {
            _nesting--;
        }
    }

    GreenNode ParsePrimaryTypeCore() {
        switch (Current.Kind) {
            case SyntaxKind.CapitalSelfKeyword:
                return new SelfTypeGreen(EatToken());

            case SyntaxKind.SomeKeyword: {
                var someKeyword = EatToken();
                return new OpaqueTypeGreen(someKeyword, ParseIntersectionType());
            }

            case SyntaxKind.AnyKeyword: {
                var anyKeyword = EatToken();
                return new ExistentialTypeGreen(anyKeyword, ParseIntersectionType());
            }

            // 'ref T' is not Sable: a reference is written after its type,
            // 'T&'. The word stays reserved so that it says so, and what
            // follows reads as the reference it meant.
            case SyntaxKind.RefKeyword: {
                var keyword = RemovedKeyword();
                return ReadAsReference(ParsePostfixType(), keyword, "a reference is written after its type");
            }

            // '~Copyable': parsed wherever a type is, and bound only as a constraint.
            case SyntaxKind.TildeToken:
                return new SuppressedTypeGreen(EatToken(), ParsePostfixType());

            // 'region' names the region type too: 'fn f(in r: region)' (docs/02 §2.8).
            case SyntaxKind.RegionKeyword:
                return new SimpleNameTypeGreen(EatToken(), null);

            case SyntaxKind.AsyncKeyword: {
                var asyncKeyword = EatToken();
                return ParseFunctionTypeTail(asyncKeyword);
            }

            case SyntaxKind.OpenParenToken:
                return ParseParenthesizedOrTupleOrFunctionType(null);

            case SyntaxKind.OpenBracketToken:
                return ParseDictionaryType();

            default:
                if (!At(SyntaxKind.IdentifierToken) && !SyntaxFacts.IsKeyword(Current.Kind)) {
                    _diagnostics.Error(
                        ErrorCode.ERR_ExpectedType,
                        CurrentSpan,
                        $"expected a type but found '{DescribeCurrent()}'"
                    );
                    return new SimpleNameTypeGreen(CreateMissing(SyntaxKind.IdentifierToken), null);
                }

                return ParseSimpleNameType();
        }
    }

    GreenNode ParseFunctionTypeTail(GreenToken? asyncKeyword) {
        var group = ParseParenGroupType();
        var throwsSpec = ParseThrowsSpecOrNull();
        var arrow = EatToken(SyntaxKind.ArrowToken);
        return new FunctionTypeGreen(asyncKeyword, group.AsTuple(), throwsSpec, arrow, ParseType());
    }

    /// <summary>
    ///     A parenthesised group is a function parameter list, a tuple type, or
    ///     just a parenthesised type. The decision needs the token after the
    ///     closing paren, so the group is parsed once and shaped afterwards.
    /// </summary>
    GreenNode ParseParenthesizedOrTupleOrFunctionType(GreenToken? asyncKeyword) {
        var group = ParseParenGroupType();

        if (At(SyntaxKind.ArrowToken) || At(SyntaxKind.ThrowsKeyword)
            || At(SyntaxKind.RethrowsKeyword)) {
            var throwsSpec = ParseThrowsSpecOrNull();
            var arrow = EatToken(SyntaxKind.ArrowToken);
            return new FunctionTypeGreen(asyncKeyword, group.AsTuple(), throwsSpec, arrow, ParseType());
        }

        return group.Simple && group.Inner is not null
            ? new ParenthesizedTypeGreen(group.Open, group.Inner, group.Close)
            : group.AsTuple();
    }

    private readonly record struct ParenGroupType(
        GreenToken Open,
        GreenList Elements,
        GreenToken Close,
        bool Simple,
        GreenNode? Inner) {
        public GreenNode AsTuple() => new TupleTypeGreen(Open, Elements, Close);
    }

    ParenGroupType ParseParenGroupType() {
        var open = EatToken(SyntaxKind.OpenParenToken);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        var simple = !At(SyntaxKind.CloseParenToken);
        GreenNode? inner = null;

        if (!At(SyntaxKind.CloseParenToken)) {
            while (true) {
                GreenToken? name = null;
                GreenToken? colon = null;
                if (IsArgumentLabel()) {
                    name = EatToken();
                    colon = EatToken(SyntaxKind.ColonToken);
                    simple = false;
                }

                // '(inout Int) -> ()': a function type's parameter it may
                // change in place. A tuple's element cannot be, which the
                // binder says.
                var modifier = TryEatToken(SyntaxKind.InoutKeyword);
                if (modifier is not null) {
                    simple = false;
                }

                // '(@escaping () -> Int) -> ()': a function type's parameter
                // it may keep. A tuple's element cannot be, which the binder says.
                GreenNode type;
                if (At(SyntaxKind.AtToken)) {
                    type = new AttributedTypeGreen(ParseAttributes(onType: true), ParseType());
                    simple = false;
                } else {
                    type = ParseType();
                }

                inner ??= type;
                items.Add(new TupleTypeElementGreen(name, colon, modifier, type));

                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) {
                    break;
                }

                items.Add(comma);
                simple = false;
                if (At(SyntaxKind.CloseParenToken) || At(SyntaxKind.EndOfFile)) {
                    break;
                }
            }
        }

        var close = EatToken(SyntaxKind.CloseParenToken);
        return new(open, GreenList.Create(items.ToImmutable()), close, simple, inner);
    }

    GreenNode? ParseThrowsSpecOrNull() {
        // 'rethrows(E)': what the closures throw, and E as well.
        if (!At(SyntaxKind.ThrowsKeyword) && !At(SyntaxKind.RethrowsKeyword)) {
            return null;
        }

        var throwsKeyword = EatToken();
        if (!At(SyntaxKind.OpenParenToken)) {
            return new ThrowsSpecGreen(throwsKeyword, null, null, null);
        }

        var open = EatToken();
        var errorType = ParseType();
        var close = EatToken(SyntaxKind.CloseParenToken);
        return new ThrowsSpecGreen(throwsKeyword, open, errorType, close);
    }

    GreenNode ParseDictionaryType() {
        var open = EatToken(SyntaxKind.OpenBracketToken);
        var key = ParseType();
        var colon = EatToken(SyntaxKind.ColonToken);
        var value = ParseType();
        var close = EatToken(SyntaxKind.CloseBracketToken);
        return new DictionaryTypeGreen(open, key, colon, value, close);
    }

    GreenNode ParseSimpleNameType() {
        var identifier = EatIdentifier();
        var arguments = At(SyntaxKind.LessToken) ? ParseTypeArgumentList() : null;
        return new SimpleNameTypeGreen(identifier, arguments);
    }

    /// <summary>Parses a dotted type name without any suffixes.</summary>
    GreenNode ParseTypeName() {
        var type = ParseSimpleNameType();
        while (At(SyntaxKind.DotToken) && Peek(1).Kind == SyntaxKind.IdentifierToken) {
            var dot = EatToken();
            type = new QualifiedNameGreen(type, dot, ParseSimpleNameType());
        }

        return type;
    }

    bool LooksLikeRecordLiteralStart() => LooksLikeRecordLiteral();

    GreenNode ParseTypeArgumentList() {
        var less = EatToken(SyntaxKind.LessToken);
        var arguments = ImmutableArray.CreateBuilder<GreenNode>();

        if (!IsTypeArgumentListEnd()) {
            while (true) {
                // 'Buf<Int, 4>': a value for a value generic parameter.
                arguments.Add(At(SyntaxKind.IntegerLiteralToken) ? new LiteralTypeGreen(EatToken()) : ParseType());
                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) {
                    break;
                }

                arguments.Add(comma);
                if (IsTypeArgumentListEnd() || At(SyntaxKind.EndOfFile)) {
                    break;
                }
            }
        }

        var greater = EatGreaterThan();
        return new TypeArgumentListGreen(less, GreenList.Create(arguments.ToImmutable()), greater);
    }

    bool IsTypeArgumentListEnd() =>
        Current.Kind is SyntaxKind.GreaterToken
            or SyntaxKind.GreaterGreaterToken
            or SyntaxKind.GreaterEqualsToken
            or SyntaxKind.GreaterGreaterEqualsToken;

    /// <summary>
    ///     What may follow the <c>&amp;</c> of a protocol composition.
    /// </summary>
    /// <remarks>
    ///     Narrower than <see cref="CanStartType" />: only a named type can be a
    ///     protocol, so <c>Document&amp;[&gt;..]</c> is a grow-array of references
    ///     rather than a composition with a dictionary type.
    /// </remarks>
    static bool CanFollowCompositionAmpersand(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.IdentifierToken or SyntaxKind.CapitalSelfKeyword => true,
            SyntaxKind.SomeKeyword or SyntaxKind.AnyKeyword => true,
            SyntaxKind.OpenParenToken => true,
            SyntaxKind.TildeToken => true,
            _ => false,
        };

    static bool CanStartType(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.IdentifierToken => true,
            SyntaxKind.CapitalSelfKeyword => true,
            SyntaxKind.SomeKeyword or SyntaxKind.AnyKeyword or SyntaxKind.RefKeyword => true,
            SyntaxKind.AsyncKeyword => true,
            SyntaxKind.RegionKeyword => true,
            SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken => true,
            _ => false,
        };

    /// <summary>
    ///     Decides whether a <c>&lt;</c> in expression position opens a type
    ///     argument list or is a comparison, by scanning for a balanced
    ///     <c>&gt;</c> followed by a token that may follow a generic name. This is
    ///     the C# rule, and it has the same pathological cases.
    /// </summary>
    bool TryScanTypeArgumentList() {
        var start = _index;
        var depth = 0;

        // Inside an array type's brackets, '>' and '..' spell its kind —
        // 'String[>..]' — and close nothing.
        var brackets = 0;

        // Parentheses are a function type's, 'Box<(Int) -> Bool>', or a
        // tuple's, 'Box<(Int, Int)>': with neither an arrow after them nor a
        // comma in them, 'a < (b) > (c)' compares. Each is whether what it
        // holds could be such a type so far, and how deep in '<' it opened.
        var parens = new Stack<(bool IsType, int Depth)>();

        void Holds() {
            if (parens.TryPop(out var open)) {
                parens.Push((true, open.Depth));
            }
        }

        try {
            while (true) {
                var kind = LexedTokenAt(_index).Kind;

                if (brackets > 0
                    && kind is SyntaxKind.GreaterToken
                        or SyntaxKind.DotDotToken
                        or SyntaxKind.DotDotLessToken
                        or SyntaxKind.IntegerLiteralToken) {
                    _index++;
                    continue;
                }

                switch (kind) {
                    case SyntaxKind.LessToken:
                        depth++;
                        break;

                    case SyntaxKind.OpenBracketToken:
                        brackets++;
                        break;

                    case SyntaxKind.CloseBracketToken:
                        brackets--;
                        break;

                    case SyntaxKind.GreaterToken:
                        depth--;
                        if (depth == 0) {
                            return parens.Count == 0 && IsAfterTypeArgumentList(Peek(1).Kind);
                        }

                        break;

                    case SyntaxKind.GreaterGreaterToken:
                        depth -= 2;
                        if (depth <= 0) {
                            return parens.Count == 0 && IsAfterTypeArgumentList(Peek(1).Kind);
                        }

                        break;

                    case SyntaxKind.OpenParenToken:
                        parens.Push((Peek(1).Kind is SyntaxKind.CloseParenToken, depth));
                        break;

                    case SyntaxKind.CommaToken when parens.TryPeek(out var tuple) && tuple.Depth == depth:
                        Holds();
                        break;

                    case SyntaxKind.CloseParenToken:
                        if (parens.Count == 0 || !parens.Pop().IsType && Peek(1).Kind is not SyntaxKind.ArrowToken) {
                            return false;
                        }

                        break;

                    // What is in parentheses with an arrow in it is a type: '((Int) -> Bool)?'.
                    case SyntaxKind.ArrowToken when Peek(-1).Kind is SyntaxKind.CloseParenToken:
                        Holds();
                        break;

                    // 'Buf<Int, 4>': a value given to a value generic parameter.
                    case SyntaxKind.IntegerLiteralToken when Peek(-1).Kind is SyntaxKind.LessToken or SyntaxKind.CommaToken
                                                             && (!parens.TryPeek(out var around) || around.Depth < depth):
                    case SyntaxKind.IdentifierToken:
                    case SyntaxKind.DotToken:
                    case SyntaxKind.CommaToken:
                    case SyntaxKind.QuestionToken:
                    case SyntaxKind.AmpersandToken:
                    case SyntaxKind.PipeToken:
                    case SyntaxKind.ColonToken:
                    case SyntaxKind.RefKeyword:
                    case SyntaxKind.CapitalSelfKeyword:
                        break;

                    // Nor does an expression begin with 'any': '(any P)?'.
                    case SyntaxKind.SomeKeyword:
                    case SyntaxKind.AnyKeyword:
                        Holds();
                        break;

                    default:
                        return false; // not plausible as a type argument list
                }

                if (_index >= _tokens.Length - 1) {
                    return false;
                }

                _index++;
            }
        } finally {
            _index = start;
        }
    }

    static bool IsAfterTypeArgumentList(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.OpenParenToken => true,
            SyntaxKind.CloseParenToken
                or SyntaxKind.CloseBracketToken
                or SyntaxKind.CloseBraceToken => true,
            SyntaxKind.CommaToken or SyntaxKind.SemicolonToken or SyntaxKind.ColonToken => true,
            SyntaxKind.DotToken => true,

            // 'Gen<Int> { … }': nothing compares with a block.
            SyntaxKind.OpenBraceToken => true,
            SyntaxKind.EndOfFile => true,
            _ => false,
        };

    // =======================================================================
    // Closures
    // =======================================================================

    /// <summary>A brace right after an argument list is the call's last argument.</summary>
    GreenNode? TryParseTrailingClosure() => CanTakeTrailingClosure() ? ParseBraceClosure(null) : null;

    /// <summary>
    ///     <c>both { 1 } onFail: { 2 }</c>: after a trailing closure, more of
    ///     them, each labelled for the parameter it is — on the line the one
    ///     before ends on, as a new line starts a new statement.
    /// </summary>
    GreenList ParseLabeledClosures(GreenNode? trailing) {
        if (trailing is null) {
            return GreenList.Empty;
        }

        var closures = ImmutableArray.CreateBuilder<GreenNode>();
        while (At(SyntaxKind.IdentifierToken) && Peek(1).Kind == SyntaxKind.ColonToken && Peek(2).Kind == SyntaxKind.OpenBraceToken
               && !_noBrace && !AtStatementStart) {
            var name = EatToken();
            var colon = EatToken();
            closures.Add(new LabeledClosureGreen(name, colon, ParseBraceClosure(null)));
        }

        return GreenList.Create(closures.ToImmutable());
    }

    /// <remarks>
    ///     A brace on a new line is never a trailing closure: the line before
    ///     it ended a statement, so the brace starts something else.
    /// </remarks>
    bool CanTakeTrailingClosure() =>
        !_noBrace && At(SyntaxKind.OpenBraceToken) && !AtStatementStart && !IsAccessorBlockAhead();

    /// <summary>
    ///     True when the brace at the current token opens a property's accessors.
    /// </summary>
    /// <remarks>
    ///     `var tracked: Int = 0 { willSet { ... } }` has an initialiser and then
    ///     an accessor block, so the brace must not be read as a trailing closure
    ///     on the initialiser. Swift decides the same way: by the first token
    ///     inside the brace. None of these keywords is a valid expression, so the
    ///     test is safe to apply everywhere.
    /// </remarks>
    bool IsAccessorBlockAhead() =>
        Peek(1).Kind is SyntaxKind.GetKeyword
            or SyntaxKind.SetKeyword
            or SyntaxKind.WillSetKeyword
            or SyntaxKind.DidSetKeyword;

    /// <summary>
    ///     True for a closure capture list: <c>[weak self] { ... }</c>.
    /// </summary>
    /// <remarks>
    ///     Both ends have to be checked. An array literal can also be followed by
    ///     an identifier on the next line, so the token after the closing bracket
    ///     must be something only a closure can start.
    /// </remarks>
    bool LooksLikeCaptureList() {
        if (Peek(1).Kind is not (SyntaxKind.WeakKeyword
            or SyntaxKind.UnownedKeyword
            or SyntaxKind.SelfKeyword
            or SyntaxKind.IdentifierToken)) {
            return false;
        }

        var depth = 0;
        for (var offset = 0; offset < 64; offset++) {
            var kind = Peek(offset).Kind;
            if (kind == SyntaxKind.OpenBracketToken) {
                depth++;
            } else if (kind == SyntaxKind.CloseBracketToken) {
                depth--;
                if (depth != 0) {
                    continue;
                }

                // Where a '{' opens a statement's body — 'if case [let a, ...]
                // = [x, y] {' — the brackets before it are an array, not the
                // captures of a closure that brace would start.
                return Peek(offset + 1).Kind switch {
                    SyntaxKind.OpenBraceToken => !_noBrace,
                    SyntaxKind.OpenParenToken => true,
                    SyntaxKind.IdentifierToken =>
                        Peek(offset + 2).Kind == SyntaxKind.FatArrowToken,
                    _ => false,
                };
            } else if (kind is SyntaxKind.EndOfFile or SyntaxKind.OpenBraceToken) {
                return false;
            }
        }

        return false;
    }

    /// <summary>Scans for a balanced <c>)</c> followed by <c>=&gt;</c>.</summary>
    bool LooksLikeArrowClosure() {
        var offset = 0;
        var depth = 0;
        while (offset < 256) {
            var kind = Peek(offset).Kind;
            if (kind is SyntaxKind.OpenParenToken) {
                depth++;
            } else if (kind is SyntaxKind.CloseParenToken) {
                depth--;
                if (depth == 0) {
                    // '=>' is the body; '->' is a written return type, and
                    // 'throws' what it throws: no expression can follow a
                    // parenthesised one with either.
                    return Peek(offset + 1).Kind
                        is SyntaxKind.FatArrowToken
                        or SyntaxKind.ArrowToken
                        or SyntaxKind.ThrowsKeyword
                        or SyntaxKind.RethrowsKeyword;
                }
            } else if (kind is SyntaxKind.EndOfFile) {
                return false;
            }

            offset++;
        }

        return false;
    }

    GreenNode ParseCaptureListClosure() {
        var captures = ParseCaptureList();
        if (At(SyntaxKind.OpenBraceToken)) {
            return ParseBraceClosure(captures);
        }

        return ParseArrowClosure(captures);
    }

    GreenNode ParseCaptureList() =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(SyntaxKind.OpenBracketToken);
                var items = ParseSeparatedList(SyntaxKind.CloseBracketToken, ParseCaptureItem);
                var close = EatToken(SyntaxKind.CloseBracketToken);
                return new CaptureListGreen(open, items, close);
            }
        );

    GreenNode ParseCaptureItem() {
        var specifier = Current.Kind is SyntaxKind.WeakKeyword or SyntaxKind.UnownedKeyword
            ? EatToken()
            : null;

        var identifier = At(SyntaxKind.SelfKeyword) ? EatToken() : EatIdentifier();
        var equals = TryEatToken(SyntaxKind.EqualsToken);
        var value = equals is null ? null : ParseExpression();
        return new CaptureItemGreen(specifier, identifier, equals, value);
    }

    GreenNode ParseArrowClosure(GreenNode? captures) {
        var signature = ParseClosureSignature();
        var arrow = EatToken(SyntaxKind.FatArrowToken);
        var body = At(SyntaxKind.OpenBraceToken)
            ? ParseBlock()
            : WithBracesAllowed(ParseExpression);
        return new ClosureExpressionGreen(captures, signature, arrow, body);
    }

    GreenNode ParseClosureSignature() {
        if (!At(SyntaxKind.OpenParenToken)) {
            return new ClosureSignatureGreen(
                null,
                GreenList.Create([new ClosureParameterGreen(EatIdentifier(), null, null, null)]),
                null,
                null,
                null,
                null
            );
        }

        return WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(SyntaxKind.OpenParenToken);
                var parameters = ParseSeparatedList(SyntaxKind.CloseParenToken, ParseClosureParameter);
                var close = EatToken(SyntaxKind.CloseParenToken);
                var throwsSpec = ParseThrowsSpecOrNull();

                GreenToken? arrow = null;
                GreenNode? returnType = null;
                if (At(SyntaxKind.ArrowToken)) {
                    arrow = EatToken();
                    returnType = ParseType();
                }

                return new ClosureSignatureGreen(open, parameters, close, throwsSpec, arrow, returnType);
            }
        );
    }

    GreenNode ParseClosureParameter() {
        // '_', of a value the closure is given and does not use.
        var identifier = At(SyntaxKind.UnderscoreToken) ? EatToken() : EatIdentifier();
        var colon = TryEatToken(SyntaxKind.ColonToken);
        var modifier = colon is null ? null : TryEatToken(SyntaxKind.InoutKeyword);
        var type = colon is null ? null : At(SyntaxKind.AtToken) ? new AttributedTypeGreen(ParseAttributes(onType: true), ParseType()) : ParseType();
        return new ClosureParameterGreen(identifier, colon, modifier, type);
    }

    GreenNode ParseBraceClosure(GreenNode? captures) =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(SyntaxKind.OpenBraceToken);
                var header = HasClosureHeader() ? ParseClosureHeader() : null;
                var statements = ParseStatements(SyntaxKind.CloseBraceToken);
                var close = EatToken(SyntaxKind.CloseBraceToken);
                return new BraceClosureExpressionGreen(captures, open, header, statements, close);
            }
        );

    /// <summary>
    ///     True when the tokens after <c>{</c> form a parameter list ending in
    ///     <c>in</c>.
    /// </summary>
    /// <remarks>
    ///     The scan stops at the first token that cannot appear in a header, so
    ///     <c>{ for x in xs { } }</c> is not mistaken for one: <c>for</c> ends the
    ///     scan before the <c>in</c> is reached.
    /// </remarks>
    bool HasClosureHeader() {
        // The opening brace has already been consumed, so the scan starts at
        // the current token.
        for (var offset = 0; offset < 64; offset++) {
            switch (Peek(offset).Kind) {
                case SyntaxKind.InKeyword:
                    // 'in:' is a label, of a call the body starts with.
                    return Peek(offset + 1).Kind != SyntaxKind.ColonToken;

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
                case SyntaxKind.InoutKeyword:
                case SyntaxKind.CapitalSelfKeyword:
                case SyntaxKind.ThrowsKeyword:
                case SyntaxKind.RethrowsKeyword:
                // '(f: @escaping () -> Int) in'.
                case SyntaxKind.AtToken:
                // An array's length, 'U8[16]', or a value argument, 'Buf<Int, 4>'.
                case SyntaxKind.IntegerLiteralToken when Peek(offset - 1).Kind is SyntaxKind.OpenBracketToken or SyntaxKind.DotDotToken or SyntaxKind.CommaToken:
                    continue;

                default:
                    return false;
            }
        }

        return false;
    }

    GreenNode ParseClosureHeader() {
        var open = TryEatToken(SyntaxKind.OpenParenToken);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (!At(SyntaxKind.InKeyword)
               && !At(SyntaxKind.ArrowToken)
               && !At(SyntaxKind.ThrowsKeyword)
               && !At(SyntaxKind.RethrowsKeyword)
               && !At(SyntaxKind.CloseParenToken)
               && !At(SyntaxKind.EndOfFile)) {
            items.Add(ParseClosureParameter());
            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) {
                break;
            }

            items.Add(comma);
        }

        var close = open is null ? null : TryEatToken(SyntaxKind.CloseParenToken);
        var throwsSpec = ParseThrowsSpecOrNull();

        GreenToken? arrow = null;
        GreenNode? returnType = null;
        if (At(SyntaxKind.ArrowToken)) {
            arrow = EatToken();
            returnType = ParseType();
        }

        var inKeyword = EatToken(SyntaxKind.InKeyword);
        return new ClosureHeaderGreen(open, GreenList.Create(items.ToImmutable()), close, throwsSpec, arrow, returnType, inKeyword);
    }

    // =======================================================================
    // match
    // =======================================================================

    GreenNode ParseMatchExpression() {
        var matchKeyword = EatToken(SyntaxKind.MatchKeyword);
        var subject = WithBracesSuppressed(ParseExpression);
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var arms = ImmutableArray.CreateBuilder<GreenNode>();
        while (!At(SyntaxKind.CloseBraceToken) && !At(SyntaxKind.EndOfFile)) {
            var before = _index;
            arms.Add(ParseMatchArm());
            if (_index == before) {
                SkipCurrentToken();
            }
        }

        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new MatchExpressionGreen(matchKeyword, subject, open, GreenList.Create(arms.ToImmutable()), close);
    }

    GreenNode ParseMatchArm() {
        GreenToken keyword;
        var patterns = GreenList.Empty;

        if (At(SyntaxKind.DefaultKeyword)) {
            keyword = EatToken();
        } else {
            keyword = EatToken(SyntaxKind.CaseKeyword);
            var items = ImmutableArray.CreateBuilder<GreenNode>();
            while (true) {
                items.Add(WithBracesSuppressed(ParsePattern));
                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) {
                    break;
                }

                items.Add(comma);
                if (At(SyntaxKind.EndOfFile)) {
                    break;
                }
            }

            patterns = GreenList.Create(items.ToImmutable());
        }

        var whereClause = At(SyntaxKind.WhereKeyword)
            ? new PatternWhereClauseGreen(EatToken(), WithArrowSuppressed(ParseExpression))
            : null;

        if (At(SyntaxKind.ColonToken)) {
            var colon = EatToken();
            var statements = ParseStatements(
                SyntaxKind.CaseKeyword,
                SyntaxKind.DefaultKeyword,
                SyntaxKind.CloseBraceToken
            );
            return new MatchCaseBlockGreen(keyword, patterns, whereClause, colon, statements);
        }

        var arrow = EatToken(SyntaxKind.FatArrowToken);
        // '=> return r', '=> break', '=> continue': a statement that leaves,
        // as 'throw' does, is an arm's body as a block of it alone would be.
        var body = At(SyntaxKind.OpenBraceToken) ? ParseBlock()
            : At(SyntaxKind.ReturnKeyword) || At(SyntaxKind.BreakKeyword) || At(SyntaxKind.ContinueKeyword)
                ? new BlockGreen(CreateMissing(SyntaxKind.OpenBraceToken), GreenList.Create([ParseStatement()]), CreateMissing(SyntaxKind.CloseBraceToken))
                : ParseExpression();
        return new MatchArmGreen(keyword, patterns, whereClause, arrow, body);
    }

    // =======================================================================
    // Patterns
    // =======================================================================

    /// <summary>Parses a pattern. Forms follow spec/SYNTAX.md §15.</summary>
    public GreenNode ParsePattern() {
        var left = ParsePatternWithRange();
        while (At(SyntaxKind.PipeToken)) {
            var pipe = EatToken();
            left = new OrPatternGreen(left, pipe, ParsePatternWithRange());
        }

        return left;
    }

    GreenNode ParsePatternWithRange() {
        if (IsRangeOperator(Current.Kind)) {
            var prefixOp = EatToken();
            return new RangePatternGreen(null, prefixOp, ParsePrimaryPattern());
        }

        var pattern = ParsePrimaryPattern();
        if (!IsRangeOperator(Current.Kind)) {
            return pattern;
        }

        var op = EatToken();
        var right = CanStartPattern(Current.Kind) ? ParsePrimaryPattern() : null;

        // '5...' runs to the end of the type; '5..<' would leave out a bound
        // it never names.
        if (right is null && op.Kind == SyntaxKind.DotDotLessToken) {
            _diagnostics.Error(
                ErrorCode.ERR_ExpectedPattern,
                CurrentSpan,
                "a half-open range needs an upper bound: write '5...' for one that runs to the end"
            );
        }

        return new RangePatternGreen(pattern, op, right);
    }

    GreenNode ParsePrimaryPattern() {
        if (TooDeep()) {
            return new BindingPatternGreen(null, CreateMissing(SyntaxKind.IdentifierToken));
        }

        _nesting++;
        try {
            // 'let v?' is '.Some(let v)': the pattern, matched against what
            // an optional holds.
            var pattern = ParsePrimaryPatternCore();
            while (At(SyntaxKind.QuestionToken)) {
                pattern = new OptionalPatternGreen(pattern, EatToken());
            }

            return pattern;
        } finally {
            _nesting--;
        }
    }

    GreenNode ParsePrimaryPatternCore() {
        switch (Current.Kind) {
            case SyntaxKind.UnderscoreToken:
                return new WildcardPatternGreen(EatToken());

            case SyntaxKind.LetKeyword:
            case SyntaxKind.VarKeyword: {
                var keyword = EatToken();
                var identifier = At(SyntaxKind.SelfKeyword) ? EatToken() : EatIdentifier();
                if (!At(SyntaxKind.AsKeyword)) {
                    return new BindingPatternGreen(keyword, identifier);
                }

                var asKeyword = EatToken();
                return new BindingTypeTestPatternGreen(keyword, identifier, asKeyword, ParseType());
            }

            case SyntaxKind.IsKeyword:
                return new TypeTestPatternGreen(EatToken(), ParseType());

            case SyntaxKind.DotToken: {
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

            case SyntaxKind.MinusToken: {
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
                _diagnostics.Error(
                    ErrorCode.ERR_ExpectedPattern,
                    CurrentSpan,
                    $"expected a pattern but found '{DescribeCurrent()}'"
                );
                return new BindingPatternGreen(null, CreateMissing(SyntaxKind.IdentifierToken));
        }
    }

    GreenToken EatNumericLiteral() {
        if (Current.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken) {
            return EatToken();
        }

        _diagnostics.Error(
            ErrorCode.ERR_ExpectedPattern,
            CurrentSpan,
            "a negated pattern must be a numeric literal"
        );
        return CreateMissing(SyntaxKind.IntegerLiteralToken);
    }

    /// <summary>
    ///     An identifier starts a binding, a qualified union case, or a record
    ///     pattern; the dots and the brace decide which.
    /// </summary>
    GreenNode ParseNamedPattern() {
        var segments = CountNameSegments();

        if (Peek(segments * 2 - 1).Kind == SyntaxKind.OpenBraceToken) {
            return ParseRecordPattern(ParseTypeNameSegments(segments));
        }

        if (segments == 1) {
            return new BindingPatternGreen(null, EatToken());
        }

        // The last segment is the case name; everything before it qualifies it.
        var qualifier = ParseTypeNameSegments(segments - 1);
        var dot = EatToken(SyntaxKind.DotToken);
        var name = EatMemberName();
        var arguments = At(SyntaxKind.OpenParenToken) ? ParsePatternArgumentList() : null;
        return new UnionCasePatternGreen(qualifier, dot, name, arguments);
    }

    /// <summary>Counts the identifiers in an <c>ident ('.' ident)*</c> run.</summary>
    int CountNameSegments() {
        var count = 1;
        var offset = 1;
        while (Peek(offset).Kind == SyntaxKind.DotToken
               && Peek(offset + 1).Kind == SyntaxKind.IdentifierToken) {
            count++;
            offset += 2;
        }

        return count;
    }

    GreenNode ParseTypeNameSegments(int count) {
        GreenNode type = new SimpleNameTypeGreen(EatIdentifier(), null);
        for (var i = 1; i < count; i++) {
            var dot = EatToken(SyntaxKind.DotToken);
            type = new QualifiedNameGreen(type, dot, new SimpleNameTypeGreen(EatIdentifier(), null));
        }

        return type;
    }

    GreenNode ParseRecordPattern(GreenNode? type) =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(SyntaxKind.OpenBraceToken);
                var fields = ParseSeparatedList(SyntaxKind.CloseBraceToken, ParseFieldPattern);
                var close = EatToken(SyntaxKind.CloseBraceToken);
                return new RecordPatternGreen(type, open, fields, close);
            }
        );

    GreenNode ParseFieldPattern() {
        var name = EatIdentifier();
        var colon = TryEatToken(SyntaxKind.ColonToken);
        var pattern = colon is null ? null : ParsePattern();
        return new FieldPatternGreen(name, colon, pattern);
    }

    GreenNode ParseTupleOrParenthesizedPattern() =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(SyntaxKind.OpenParenToken);

                var items = ImmutableArray.CreateBuilder<GreenNode>();
                var simple = !At(SyntaxKind.CloseParenToken);
                GreenNode? only = null;

                while (!At(SyntaxKind.CloseParenToken) && !At(SyntaxKind.EndOfFile)) {
                    var element = ParsePattern();
                    only ??= element;
                    items.Add(element);

                    var comma = TryEatToken(SyntaxKind.CommaToken);
                    if (comma is null) {
                        break;
                    }

                    items.Add(comma);
                    simple = false;
                }

                var close = EatToken(SyntaxKind.CloseParenToken);

                return simple && items.Count == 1 && only is not null
                    ? new ParenthesizedPatternGreen(open, only, close)
                    : new TuplePatternGreen(open, GreenList.Create(items.ToImmutable()), close);
            }
        );

    GreenNode ParseArrayPattern() =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(SyntaxKind.OpenBracketToken);
                var elements = ParseSeparatedList(SyntaxKind.CloseBracketToken, ParseArrayPatternElement);
                var close = EatToken(SyntaxKind.CloseBracketToken);
                return new ArrayPatternGreen(open, elements, close);
            }
        );

    /// <summary>
    ///     Inside an array pattern <c>...</c> is the rest element, not a range.
    /// </summary>
    GreenNode ParseArrayPatternElement() {
        if (!At(SyntaxKind.DotDotDotToken)) {
            return ParsePattern();
        }

        var dots = EatToken();
        var letKeyword = TryEatToken(SyntaxKind.LetKeyword);
        var identifier = At(SyntaxKind.IdentifierToken) ? EatToken() : null;
        return new RestPatternGreen(dots, letKeyword, identifier);
    }

    GreenNode ParsePatternArgumentList() =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(SyntaxKind.OpenParenToken);
                var arguments = ParseSeparatedList(SyntaxKind.CloseParenToken, ParseSubPattern);
                var close = EatToken(SyntaxKind.CloseParenToken);
                return new PatternArgumentListGreen(open, arguments, close);
            }
        );

    GreenNode ParseSubPattern() {
        GreenToken? name = null;
        GreenToken? colon = null;
        if (IsArgumentLabel()) {
            name = EatToken();
            colon = EatToken(SyntaxKind.ColonToken);
        }

        return new SubPatternGreen(name, colon, ParsePattern());
    }

    static bool CanStartPattern(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.IdentifierToken or SyntaxKind.UnderscoreToken => true,
            SyntaxKind.LetKeyword or SyntaxKind.VarKeyword or SyntaxKind.IsKeyword => true,
            SyntaxKind.DotToken or SyntaxKind.MinusToken => true,
            SyntaxKind.OpenParenToken
                or SyntaxKind.OpenBracketToken
                or SyntaxKind.OpenBraceToken => true,
            SyntaxKind.IntegerLiteralToken
                or SyntaxKind.FloatLiteralToken
                or SyntaxKind.CharLiteralToken
                or SyntaxKind.StringLiteralToken
                or SyntaxKind.MultilineStringLiteralToken
                or SyntaxKind.RawStringLiteralToken => true,
            SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NoneKeyword => true,
            _ => false,
        };

    // =======================================================================
    // Statements
    // =======================================================================

    /// <summary>Parses the body of a statement or declaration.</summary>
    /// <remarks>
    ///     A body's <c>{</c> must be on its header's line (spec/lexical.md §4):
    ///     one that starts a line would read as a trailing closure or a block of
    ///     its own. It is still taken as the body, so the rest parses sensibly.
    /// </remarks>
    public GreenNode ParseBlock() {
        if (At(SyntaxKind.OpenBraceToken) && AtStatementStart) {
            ReportUnexpected(
                ErrorCode.ERR_BraceOnNewLine,
                "'{' must be on the same line as what it belongs to"
            );
        }

        return ParseBlockCore();
    }

    GreenNode ParseBlockCore() =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(SyntaxKind.OpenBraceToken);
                var statements = ParseStatements(SyntaxKind.CloseBraceToken);
                var close = EatToken(SyntaxKind.CloseBraceToken);
                return new BlockGreen(open, statements, close);
            }
        );

    /// <summary>
    ///     Parses statements until one of <paramref name="terminators" />, always
    ///     making progress so that malformed input cannot spin.
    /// </summary>
    GreenList ParseStatements(params SyntaxKind[] terminators) {
        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (!At(SyntaxKind.EndOfFile) && Array.IndexOf(terminators, Current.Kind) < 0) {
            var before = _index;
            var reported = _diagnostics.Count;
            items.Add(ParseStatement());

            // Nothing could start here. The rest of the line is skipped with
            // it: parsed as a statement of its own, the '?' of 'let x?' made a
            // conditional out of missing operands, and every missing piece
            // was another error, in the parser and again in the binder.
            if (_index == before) {
                do {
                    SkipCurrentToken();
                } while (!AtStatementStart
                         && !At(SyntaxKind.EndOfFile)
                         && !At(SyntaxKind.SemicolonToken)
                         && Array.IndexOf(terminators, Current.Kind) < 0);
                continue;
            }

            // Two statements on one line need a ';' between them. After a '}'
            // the next statement may follow on the same line, as in one-line
            // bodies, so only a statement that ends on something else counts.
            // A statement that already reported an error usually stopped
            // early, and saying so twice helps nobody.
            if (!AtStatementStart
                && _diagnostics.Count == reported
                && !At(SyntaxKind.BadToken)
                && !At(SyntaxKind.EndOfFile)
                && !At(SyntaxKind.SemicolonToken)
                && Array.IndexOf(terminators, Current.Kind) < 0
                && TokenAt(_index - 1).Kind is not (SyntaxKind.CloseBraceToken or SyntaxKind.SemicolonToken)) {
                ReportUnexpected(
                    ErrorCode.ERR_ExpectedStatementEnd,
                    $"expected a new line or ';' before '{DescribeCurrent()}'"
                );
                _statementEndReportedAt = _index;
            }
        }

        return GreenList.Create(items.ToImmutable());
    }

    public GreenNode ParseStatement() {
        if (TooDeep()) {
            return new ExpressionStatementGreen(MissingExpression(), null);
        }

        _nesting++;
        try {
            return ParseStatementCore();
        } finally {
            _nesting--;
        }
    }

    GreenNode ParseStatementCore() {
        switch (Current.Kind) {
            // A block on its own is a statement, wherever it starts.
            case SyntaxKind.OpenBraceToken:
                return ParseBlockCore();

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

            case SyntaxKind.GuardKeyword: {
                var guardKeyword = EatToken();
                var conditions = WithBracesSuppressed(ParseConditionList);
                var elseKeyword = EatToken(SyntaxKind.ElseKeyword);
                return new GuardStatementGreen(guardKeyword, conditions, elseKeyword, ParseBlock());
            }

            case SyntaxKind.WhileKeyword: {
                var whileKeyword = EatToken();
                var conditions = WithBracesSuppressed(ParseConditionList);
                return new WhileStatementGreen(whileKeyword, conditions, ParseBlock());
            }

            case SyntaxKind.RepeatKeyword: {
                var repeatKeyword = EatToken();
                var body = ParseBlock();
                var whileKeyword = EatToken(SyntaxKind.WhileKeyword);
                var condition = ParseExpression();
                return new RepeatStatementGreen(
                    repeatKeyword,
                    body,
                    whileKeyword,
                    condition,
                    TryEatToken(SyntaxKind.SemicolonToken)
                );
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

            // 'unsafe fn' in a body: a local function the caller must vouch for.
            case SyntaxKind.UnsafeKeyword when IsUnsafeFunctionModifier():
                return new DeclarationStatementGreen(ParseMemberDeclaration(GreenList.Empty));

            case SyntaxKind.UnsafeKeyword:
                return new UnsafeStatementGreen(EatToken(), ParseBlock());

            case SyntaxKind.ComptimeKeyword:
                return new ComptimeStatementGreen(EatToken(), ParseBlock());

            case SyntaxKind.ReturnKeyword: {
                var returnKeyword = EatToken();

                // 'return ..<n' returns a range open at the start.
                var value = !returnKeyword.TerminatesStatement && (CanStartExpression(Current.Kind) || IsRangeOperator(Current.Kind))
                    ? ParseExpression()
                    : null;
                return new ReturnStatementGreen(
                    returnKeyword,
                    value,
                    TryEatToken(SyntaxKind.SemicolonToken)
                );
            }

            case SyntaxKind.ThrowKeyword: {
                var throwKeyword = EatToken();
                return new ThrowStatementGreen(
                    throwKeyword,
                    ParseExpression(),
                    TryEatToken(SyntaxKind.SemicolonToken)
                );
            }

            case SyntaxKind.YieldKeyword: {
                var yieldKeyword = EatToken();
                return new YieldStatementGreen(
                    yieldKeyword,
                    ParseExpression(),
                    TryEatToken(SyntaxKind.SemicolonToken)
                );
            }

            case SyntaxKind.BreakKeyword: {
                var breakKeyword = EatToken();
                var label = !breakKeyword.TerminatesStatement && At(SyntaxKind.IdentifierToken)
                    ? EatToken()
                    : null;
                return new BreakStatementGreen(
                    breakKeyword,
                    label,
                    TryEatToken(SyntaxKind.SemicolonToken)
                );
            }

            case SyntaxKind.ContinueKeyword: {
                var continueKeyword = EatToken();
                var label = !continueKeyword.TerminatesStatement && At(SyntaxKind.IdentifierToken)
                    ? EatToken()
                    : null;
                return new ContinueStatementGreen(
                    continueKeyword,
                    label,
                    TryEatToken(SyntaxKind.SemicolonToken)
                );
            }

            case SyntaxKind.FallthroughKeyword:
                return new FallthroughStatementGreen(EatToken(), TryEatToken(SyntaxKind.SemicolonToken));

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
                && IsLabelTarget(Peek(2).Kind): {
                var label = EatToken();
                var colon = EatToken(SyntaxKind.ColonToken);
                return new LabeledStatementGreen(label, colon, ParseStatement());
            }

            default: {
                // What cannot start an expression cannot start a statement
                // either; this consumes nothing, and the statement list skips
                // the line. Straight after "expected a new line or ';'", that
                // is the one error to give.
                if (!CanStartExpressionStatement()) {
                    if (_statementEndReportedAt != _index) {
                        ReportUnexpected(
                            ErrorCode.ERR_ExpectedExpression,
                            $"expected an expression but found '{DescribeCurrent()}'"
                        );
                    }

                    return new ExpressionStatementGreen(MissingExpression(), null);
                }

                var expression = ParseExpression();
                return new ExpressionStatementGreen(expression, TryEatToken(SyntaxKind.SemicolonToken));
            }
        }
    }

    /// <summary>Where the last "expected a new line or ';'" was reported, so the next statement does not report it again.</summary>
    int _statementEndReportedAt = -1;

    /// <summary>
    ///     True when an expression statement can begin at the current token:
    ///     whatever <see cref="ParseUnaryCore" /> and <see cref="ParsePrimary" />
    ///     accept first, which includes a metatype written as a type.
    /// </summary>
    bool CanStartExpressionStatement() =>
        CanStartExpression(Current.Kind)
        || At(SyntaxKind.UnderscoreToken) && Peek(1).Kind == SyntaxKind.EqualsToken
        || IsPrefixOperator(Current.Kind)
        || IsRangeOperator(Current.Kind)
        || AtCompoundTypeExpression();

    static bool IsLabelTarget(SyntaxKind kind) =>
        kind is SyntaxKind.ForKeyword
            or SyntaxKind.WhileKeyword
            or SyntaxKind.RepeatKeyword
            or SyntaxKind.OpenBraceToken;

    GreenNode ParseLocalDeclaration(GreenToken? asyncKeyword, GreenToken? usingKeyword) {
        var keyword = Current.Kind is SyntaxKind.LetKeyword or SyntaxKind.VarKeyword
            ? EatToken()
            : EatToken(SyntaxKind.LetKeyword);

        var declarator = ParseVariableDeclarator();
        return new LocalDeclarationStatementGreen(
            asyncKeyword,
            usingKeyword,
            keyword,
            declarator,
            TryEatToken(SyntaxKind.SemicolonToken)
        );
    }

    GreenNode ParseVariableDeclarator() {
        var pattern = ParseBindingTarget();

        GreenNode? annotation = null;
        if (At(SyntaxKind.ColonToken)) {
            var colon = EatToken();
            annotation = new TypeAnnotationGreen(colon, ParseType());
        }

        GreenNode? initializer = null;
        if (At(SyntaxKind.EqualsToken)) {
            var equals = EatToken();
            initializer = new EqualsValueClauseGreen(equals, ParseExpression());
        }

        return new VariableDeclaratorGreen(pattern, annotation, initializer);
    }

    /// <summary>
    ///     The name side of a binding: a plain identifier, or a tuple or record
    ///     pattern when destructuring.
    /// </summary>
    GreenNode ParseBindingTarget() =>
        Current.Kind switch {
            SyntaxKind.OpenParenToken => ParseTupleOrParenthesizedPattern(),
            SyntaxKind.OpenBraceToken => ParseRecordPattern(null),
            SyntaxKind.UnderscoreToken => new WildcardPatternGreen(EatToken()),
            _ => new BindingPatternGreen(null, EatIdentifier()),
        };

    GreenNode ParseIfStatement() {
        var ifKeyword = EatToken(SyntaxKind.IfKeyword);
        var conditions = WithBracesSuppressed(ParseConditionList);
        var body = ParseBlock();

        GreenNode? elseClause = null;
        if (At(SyntaxKind.ElseKeyword)) {
            var elseKeyword = EatToken();
            var elseBody = At(SyntaxKind.IfKeyword) ? ParseIfStatement() : ParseBlock();
            elseClause = new ElseClauseGreen(elseKeyword, elseBody);
        }

        return new IfStatementGreen(ifKeyword, conditions, body, elseClause);
    }

    GreenList ParseConditionList() {
        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (true) {
            items.Add(ParseCondition());
            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) {
                break;
            }

            items.Add(comma);
            if (At(SyntaxKind.EndOfFile) || At(SyntaxKind.OpenBraceToken)) {
                break;
            }
        }

        return GreenList.Create(items.ToImmutable());
    }

    GreenNode ParseCondition() {
        switch (Current.Kind) {
            case SyntaxKind.LetKeyword:
            case SyntaxKind.VarKeyword: {
                var keyword = EatToken();
                var pattern = At(SyntaxKind.SelfKeyword)
                    ? new BindingPatternGreen(null, EatToken())
                    : ParseBindingTarget();

                // 'if let v? = o' is the pattern 'v?' where 'let' already
                // unwraps: said once, rather than as everything after it.
                if (At(SyntaxKind.QuestionToken)) {
                    ReportUnexpected(
                        ErrorCode.ERR_ExpectedToken,
                        "'if let' unwraps the optional already: write 'if let v = o', or 'if case let v? = o'"
                    );
                    SkipCurrentToken();
                }

                GreenNode? annotation = null;
                if (At(SyntaxKind.ColonToken)) {
                    var colon = EatToken();
                    annotation = new TypeAnnotationGreen(colon, ParseType());
                }

                GreenNode? initializer = null;
                if (At(SyntaxKind.EqualsToken)) {
                    var equals = EatToken();
                    initializer = new EqualsValueClauseGreen(equals, ParseExpression());
                }

                return new OptionalBindingConditionGreen(
                    keyword,
                    new VariableDeclaratorGreen(pattern, annotation, initializer)
                );
            }

            case SyntaxKind.CaseKeyword: {
                var caseKeyword = EatToken();
                var pattern = ParsePattern();
                var equals = EatToken(SyntaxKind.EqualsToken);
                return new CasePatternConditionGreen(caseKeyword, pattern, equals, ParseExpression());
            }

            default:
                return new ExpressionConditionGreen(ParseExpression());
        }
    }

    GreenNode ParseForStatement() {
        var forKeyword = EatToken(SyntaxKind.ForKeyword);
        var tryKeyword = TryEatToken(SyntaxKind.TryKeyword);
        var awaitKeyword = TryEatToken(SyntaxKind.AwaitKeyword);
        var pattern = WithBracesSuppressed(ParseBindingTarget);
        var inKeyword = EatToken(SyntaxKind.InKeyword);
        var sequence = WithBracesSuppressed(ParseExpression);

        GreenNode? whereClause = null;
        if (At(SyntaxKind.WhereKeyword)) {
            var whereKeyword = EatToken();
            whereClause = new ForWhereClauseGreen(whereKeyword, WithBracesSuppressed(ParseExpression));
        }

        return new ForStatementGreen(forKeyword, tryKeyword, awaitKeyword, pattern, inKeyword, sequence, whereClause, ParseBlock());
    }

    GreenNode ParseSwitchStatement() {
        var switchKeyword = EatToken(SyntaxKind.SwitchKeyword);
        var subject = WithBracesSuppressed(ParseExpression);
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var cases = ImmutableArray.CreateBuilder<GreenNode>();
        while (!At(SyntaxKind.CloseBraceToken) && !At(SyntaxKind.EndOfFile)) {
            var before = _index;
            cases.Add(ParseSwitchCase());
            if (_index == before) {
                SkipCurrentToken();
            }
        }

        var close = EatToken(SyntaxKind.CloseBraceToken);
        return new SwitchStatementGreen(switchKeyword, subject, open, GreenList.Create(cases.ToImmutable()), close);
    }

    GreenNode ParseSwitchCase() {
        GreenToken keyword;
        var values = GreenList.Empty;

        if (At(SyntaxKind.DefaultKeyword)) {
            keyword = EatToken();
        } else {
            keyword = EatToken(SyntaxKind.CaseKeyword);
            var items = ImmutableArray.CreateBuilder<GreenNode>();
            while (true) {
                items.Add(WithBracesSuppressed(ParseExpression));
                var comma = TryEatToken(SyntaxKind.CommaToken);
                if (comma is null) {
                    break;
                }

                items.Add(comma);
                if (At(SyntaxKind.EndOfFile)) {
                    break;
                }
            }

            values = GreenList.Create(items.ToImmutable());
        }

        var colon = EatToken(SyntaxKind.ColonToken);
        var statements = ParseStatements(SyntaxKind.CaseKeyword, SyntaxKind.DefaultKeyword, SyntaxKind.CloseBraceToken);
        return new SwitchCaseGreen(keyword, values, colon, statements);
    }

    GreenNode ParseDoStatement() {
        var doKeyword = EatToken(SyntaxKind.DoKeyword);
        var body = ParseBlock();

        var catches = ImmutableArray.CreateBuilder<GreenNode>();
        while (At(SyntaxKind.CatchKeyword)) {
            var catchKeyword = EatToken();
            var pattern = At(SyntaxKind.OpenBraceToken)
                ? null
                : WithBracesSuppressed(ParsePattern);

            GreenNode? whereClause = null;
            if (At(SyntaxKind.WhereKeyword)) {
                var whereKeyword = EatToken();
                whereClause = new PatternWhereClauseGreen(whereKeyword, WithBracesSuppressed(ParseExpression));
            }

            catches.Add(new CatchClauseGreen(catchKeyword, pattern, whereClause, ParseBlock()));
        }

        return new DoStatementGreen(doKeyword, body, GreenList.Create(catches.ToImmutable()));
    }

    GreenNode ParseRegionStatement() {
        var regionKeyword = EatToken(SyntaxKind.RegionKeyword);
        var identifier = At(SyntaxKind.IdentifierToken) ? EatToken() : null;

        GreenToken? equals = null;
        GreenNode? source = null;
        if (At(SyntaxKind.EqualsToken)) {
            equals = EatToken();
            source = WithBracesSuppressed(ParseExpression);
        }

        return new RegionStatementGreen(regionKeyword, identifier, equals, source, ParseBlock());
    }

    // =======================================================================
    // Declarations
    // =======================================================================

    /// <summary>Parses a whole source file.</summary>
    public GreenNode ParseCompilationUnit() {
        var attributes = ParseAttributes();

        GreenNode? module = null;
        if (At(SyntaxKind.ModuleKeyword)) {
            var moduleKeyword = EatToken();
            module = new ModuleDeclarationGreen(attributes, moduleKeyword, ParseTypeName());
            attributes = GreenList.Empty;
        }

        var imports = ImmutableArray.CreateBuilder<GreenNode>();
        while (attributes.SlotCount == 0 && AtImportDeclaration()) {
            imports.Add(ParseImportDeclaration());
        }

        var members = ParseMembers(attributes, ParseMemberDeclaration, SyntaxKind.EndOfFile, MemberContext.Declaration);
        var eof = EatToken();

        return new CompilationUnitGreen(module, GreenList.Create(imports.ToImmutable()), members, eof);
    }

    bool AtImportDeclaration() =>
        At(SyntaxKind.ImportKeyword)
        || (At(SyntaxKind.PublicKeyword) && Peek(1).Kind == SyntaxKind.ImportKeyword);

    GreenNode ParseImportDeclaration() {
        var publicKeyword = TryEatToken(SyntaxKind.PublicKeyword);
        var importKeyword = EatToken(SyntaxKind.ImportKeyword);
        var name = ParseTypeName();

        GreenNode? items = null;
        if (At(SyntaxKind.DotToken) && Peek(1).Kind == SyntaxKind.OpenBraceToken) {
            var dot = EatToken();
            var open = EatToken(SyntaxKind.OpenBraceToken);
            var list = ParseSeparatedList(SyntaxKind.CloseBraceToken, ParseImportItem);
            var close = EatToken(SyntaxKind.CloseBraceToken);
            items = new ImportItemListGreen(dot, open, list, close);
        }

        GreenToken? asKeyword = null;
        GreenToken? alias = null;
        if (items is null && At(SyntaxKind.AsKeyword)) {
            asKeyword = EatToken();
            alias = EatIdentifier();
        }

        return new ImportDeclarationGreen(publicKeyword, importKeyword, name, asKeyword, alias, items);
    }

    GreenNode ParseImportItem() {
        var name = At(SyntaxKind.StarToken) ? EatToken() : EatIdentifier();
        var asKeyword = TryEatToken(SyntaxKind.AsKeyword);
        var alias = asKeyword is null ? null : EatIdentifier();
        return new ImportItemGreen(name, asKeyword, alias);
    }

    /// <param name="onType">
    ///     Attributes written on a type, '@escaping () -> Int', take no
    ///     arguments: the parenthesis after one is the type's.
    /// </param>
    GreenList ParseAttributes(bool onType = false) {
        if (!At(SyntaxKind.AtToken)) {
            return GreenList.Empty;
        }

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (At(SyntaxKind.AtToken)) {
            items.Add(ParseAttribute(onType, out _));
        }

        return GreenList.Create(items.ToImmutable());
    }

    GreenNode ParseAttribute(bool onType, out string name) {
        var at = EatToken();
        var typeName = ParseTypeName();
        name = typeName.ToFullString().Trim();
        var arguments = At(SyntaxKind.OpenParenToken) && !onType
            ? ParseArgumentList(SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken)
            : null;
        return new AttributeGreen(at, typeName, arguments);
    }

    /// <summary>
    ///     The attributes that say what a parameter's closure is, and so are
    ///     written on its type, as Swift has them: <c>f: @escaping () -&gt; Int</c>.
    /// </summary>
    static bool IsTypeAttribute(string name) => name is "escaping" or "autoclosure" or "Sendable";

    GreenList ParseModifiers() {
        if (!IsModifier(Current.Kind) && !IsConstFunctionModifier() && !IsUnsafeFunctionModifier()) {
            return GreenList.Empty;
        }

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (IsModifier(Current.Kind) || IsConstFunctionModifier() || IsUnsafeFunctionModifier()) {
            items.Add(EatToken());

            // 'private(set)' and 'unowned(safe)' carry a parenthesised argument.
            if (At(SyntaxKind.OpenParenToken) && Peek(2).Kind == SyntaxKind.CloseParenToken) {
                items.Add(EatToken());
                items.Add(EatToken());
                items.Add(EatToken());
            }
        }

        return GreenList.Create(items.ToImmutable());
    }

    /// <summary>
    ///     <c>const</c> declares a compile-time constant on its own, but modifies
    ///     a function in <c>const fn</c>, and an initialiser in <c>const init</c>.
    /// </summary>
    bool IsConstFunctionModifier() => At(SyntaxKind.ConstKeyword) && Peek(1).Kind is SyntaxKind.FnKeyword or SyntaxKind.InitKeyword;

    /// <summary>
    ///     <c>unsafe</c> starts a block on its own, but modifies a function in
    ///     <c>unsafe fn</c>, <c>unsafe static fn</c>, <c>unsafe init</c> and the
    ///     like: where a function, an initialiser or another modifier follows it, not a brace.
    /// </summary>
    bool IsUnsafeFunctionModifier() =>
        At(SyntaxKind.UnsafeKeyword)
        && (Peek(1).Kind is SyntaxKind.FnKeyword or SyntaxKind.InitKeyword
            || IsModifier(Peek(1).Kind)
            || Peek(1).Kind == SyntaxKind.ConstKeyword && Peek(2).Kind == SyntaxKind.FnKeyword);

    static bool IsModifier(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.PublicKeyword
                or SyntaxKind.PackageKeyword
                or SyntaxKind.InternalKeyword
                or SyntaxKind.PrivateKeyword => true,
            SyntaxKind.StaticKeyword
                or SyntaxKind.OpenKeyword
                or SyntaxKind.SealedKeyword
                or SyntaxKind.AbstractKeyword
                or SyntaxKind.FinalKeyword
                or SyntaxKind.OverrideKeyword
                or SyntaxKind.RequiredKeyword
                or SyntaxKind.ConvenienceKeyword => true,
            SyntaxKind.MutatingKeyword
                or SyntaxKind.NonmutatingKeyword
                or SyntaxKind.LazyKeyword
                or SyntaxKind.NonisolatedKeyword => true,
            SyntaxKind.WeakKeyword or SyntaxKind.UnownedKeyword => true,
            SyntaxKind.AsyncKeyword => true,
            _ => false,
        };

    /// <summary>
    ///     Parses members until <paramref name="terminator" />, always making
    ///     progress. <paramref name="leading" /> carries attributes already read.
    /// </summary>
    GreenList ParseMembers(
        GreenList leading,
        Func<GreenList, GreenNode> parseMember,
        SyntaxKind terminator,
        MemberContext context
    ) {
        var items = ImmutableArray.CreateBuilder<GreenNode>();
        var pending = leading;

        while (!At(terminator) && !At(SyntaxKind.EndOfFile)) {
            // Whether this member's parse starts from nothing but tokens:
            // attributes already read, text skipped after a member before it
            // made no progress, or the second half of a split token are all
            // handed over from what came before, and what the member makes of
            // its tokens can depend on them. The leftover attributes, say,
            // make the member list skip the next token silently, where a
            // member that read the same attributes itself would not.
            var clean = pending.SlotCount == 0
                && _skipped.Count == 0
                && (_split is null || !_split.ContainsKey(_index));

            // An untouched member from the previous tree can be dropped
            // straight in: its green node is position-independent. Only one
            // parsed from the same clean start, at the same nesting, is offered.
            if (clean
                && _reuse is not null
                && _reuse.TryGet(_index, context, _nesting, out var reused, out var tokenCount, out var behind, out var ahead)
                && _index + tokenCount < _tokens.Length) {
                // The old parse read these tokens, which are the same ones,
                // so this parse would have read them too.
                var translated = new TokenExtent(_index - behind, _index + ahead, _nesting);
                NoteRead(translated.Lowest);
                NoteRead(translated.Highest);
                _memberExtents[reused] = translated;
                items.Add(reused);
                _index += tokenCount;
                _tokenPosition += reused.FullWidth;
                continue;
            }

            // What a member's parse makes of its tokens can depend on tokens
            // outside it: lookahead that runs past its end, or a look back at
            // the token before it. So the reads are noted, member by member
            // (the enclosing member's include its members'), and kept for the
            // next incremental parse.
            // A member that did not start clean, or that reached the nesting
            // limit (reported only the first time, so whether it says so
            // depends on every member before it), has no note, and is never
            // reused. Nor may anything else carried from member to member
            // reach into it: the statement-end error's position is the
            // member's own.
            var outerLowest = _lowestRead;
            var outerHighest = _highestRead;
            var outerStatementEnd = _statementEndReportedAt;
            var tooDeepBefore = _tooDeepCount;
            _lowestRead = int.MaxValue;
            _highestRead = -1;
            _statementEndReportedAt = -1;

            var before = _index;
            var outerMember = _member;
            _member = ++_memberCount;
            var member = parseMember(pending);
            _member = outerMember;
            items.Add(member);
            if (clean && _tooDeepCount == tooDeepBefore && _highestRead >= 0) {
                _memberExtents[member] = new(_lowestRead, _highestRead, _nesting);
            }

            _lowestRead = Math.Min(outerLowest, _lowestRead);
            _highestRead = Math.Max(outerHighest, _highestRead);
            _statementEndReportedAt = outerStatementEnd;

            pending = GreenList.Empty;
            if (_index == before) {
                SkipCurrentToken();
            }
        }

        if (pending.SlotCount > 0) {
            items.Add(new IncompleteDeclarationGreen(pending, GreenList.Empty));
        }

        return GreenList.Create(items.ToImmutable());
    }

    GreenNode ParseMemberDeclaration(GreenList leadingAttributes) {
        var attributes = leadingAttributes.SlotCount > 0 ? leadingAttributes : ParseAttributes();
        var modifiers = ParseModifiers();

        switch (Current.Kind) {
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

            case SyntaxKind.AliasKeyword: {
                var aliasKeyword = EatToken();
                var identifier = EatIdentifier();
                var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
                var equals = EatToken(SyntaxKind.EqualsToken);
                return new AliasDeclarationGreen(
                    attributes,
                    modifiers,
                    aliasKeyword,
                    identifier,
                    typeParameters,
                    equals,
                    ParseType()
                );
            }

            // Swift's 'typealias' is Sable's 'alias', protocol requirements'
            // included; the word stays reserved so that it says so, and what
            // follows parses as the alias it meant.
            case SyntaxKind.TypealiasKeyword: {
                ReportUnexpected(ErrorCode.ERR_TypealiasIsAlias, "Sable writes 'typealias' as 'alias': 'alias Element = Int'");
                var keyword = EatToken();
                var identifier = EatIdentifier();
                var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
                var equals = EatToken(SyntaxKind.EqualsToken);
                return new AliasDeclarationGreen(attributes, modifiers, keyword, identifier, typeParameters, equals, ParseType());
            }

            case SyntaxKind.AssociatedtypeKeyword: {
                var keyword = EatToken();
                var identifier = EatIdentifier();
                var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
                var equals = TryEatToken(SyntaxKind.EqualsToken);
                var defaultType = equals is null ? null : ParseType();

                // 'associatedtype Iter: Iterator where Iter.Element == Element':
                // what the protocol asks of the type beyond its conformances.
                var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;
                return new AssociatedTypeDeclarationGreen(
                    attributes,
                    modifiers,
                    keyword,
                    identifier,
                    baseTypes,
                    equals,
                    defaultType,
                    whereClause
                );
            }

            // 'comptime { ... }' among declarations: run as the program is compiled.
            case SyntaxKind.ComptimeKeyword:
                return new ComptimeDeclarationGreen(attributes, modifiers, EatToken(), ParseBlock());

            case SyntaxKind.ConstKeyword: {
                var keyword = EatToken();
                var identifier = EatIdentifier();
                GreenNode? annotation = null;
                if (At(SyntaxKind.ColonToken)) {
                    var colon = EatToken();
                    annotation = new TypeAnnotationGreen(colon, ParseType());
                }

                var equals = EatToken(SyntaxKind.EqualsToken);
                return new ConstDeclarationGreen(
                    attributes,
                    modifiers,
                    keyword,
                    identifier,
                    annotation,
                    new EqualsValueClauseGreen(equals, ParseExpression())
                );
            }

            case SyntaxKind.LetKeyword:
            case SyntaxKind.VarKeyword:
                return ParseFieldDeclaration(attributes, modifiers, EatToken());

            // The terse field form inside a type body: 'x: F32' declares an
            // immutable stored field; 'var' is written when it is mutable.
            case SyntaxKind.IdentifierToken when Peek(1).Kind == SyntaxKind.ColonToken:
                return ParseFieldDeclaration(attributes, modifiers, null);

            case SyntaxKind.InitKeyword: {
                var keyword = EatToken();
                var question = TryEatToken(SyntaxKind.QuestionToken);
                var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
                var parameters = ParseParameterList();
                var throwsSpec = ParseThrowsSpecOrNull();
                var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;
                var body = At(SyntaxKind.OpenBraceToken) ? ParseBlock() : null;
                return new InitDeclarationGreen(
                    attributes,
                    modifiers,
                    keyword,
                    question,
                    typeParameters,
                    parameters,
                    throwsSpec,
                    whereClause,
                    body
                );
            }

            case SyntaxKind.DeinitKeyword:
                return new DeinitDeclarationGreen(attributes, modifiers, EatToken(), ParseBlock());

            case SyntaxKind.SubscriptKeyword:
                return ParseSubscriptDeclaration(attributes, modifiers);

            case SyntaxKind.OperatorKeyword:
            case SyntaxKind.PrefixKeyword:
            case SyntaxKind.PostfixKeyword:
                return ParseOperatorDeclaration(attributes, modifiers);

            default:
                if (attributes.SlotCount > 0 || modifiers.SlotCount > 0) {
                    return new IncompleteDeclarationGreen(attributes, modifiers);
                }

                _diagnostics.Error(
                    ErrorCode.ERR_ExpectedDeclaration,
                    CurrentSpan,
                    $"expected a declaration but found '{DescribeCurrent()}'"
                );
                return new IncompleteDeclarationGreen(GreenList.Empty, GreenList.Empty);
        }
    }

    GreenNode ParseFieldDeclaration(
        GreenList attributes,
        GreenList modifiers,
        GreenToken? keyword
    ) {
        var declarator = ParseVariableDeclarator();
        var accessors = At(SyntaxKind.OpenBraceToken) ? ParseAccessorList()
            : At(SyntaxKind.FatArrowToken) ? ParseExpressionBodiedProperty()
            : null;

        // One-line type bodies separate their fields with commas.
        var separator = Current.Kind is SyntaxKind.CommaToken or SyntaxKind.SemicolonToken
            ? EatToken()
            : null;

        return new FieldDeclarationGreen(attributes, modifiers, keyword, declarator, accessors, separator);
    }

    GreenNode ParseFunctionDeclaration(GreenList attributes, GreenList modifiers) {
        var fnKeyword = EatToken(SyntaxKind.FnKeyword);

        GreenNode? explicitProtocol = null;
        GreenToken? dot = null;
        if (At(SyntaxKind.IdentifierToken) && Peek(1).Kind == SyntaxKind.DotToken) {
            explicitProtocol = ParseTypeNameSegments(CountNameSegments() - 1);
            dot = EatToken(SyntaxKind.DotToken);
        }

        var identifier = EatMemberName();
        var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
        var parameters = ParseParameterList();

        var asyncKeyword = TryEatToken(SyntaxKind.AsyncKeyword);
        var throwsSpec = ParseThrowsSpecOrNull();

        // 'throws async' has the effects the wrong way round. The tree has
        // one place for 'async', before 'throws', so a late one is kept as
        // skipped text rather than moved — moving it would change the source.
        if (throwsSpec is not null && At(SyntaxKind.AsyncKeyword) && !HasNewLineBeforeCurrent) {
            ReportUnexpected(ErrorCode.ERR_ExpectedToken, "'async' goes before 'throws', not after it");
            SkipCurrentToken();
        }

        var returnClause = At(SyntaxKind.ArrowToken) ? ParseReturnClause() : null;
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;

        GreenNode? body = null;
        GreenToken? arrow = null;
        GreenNode? expressionBody = null;

        if (At(SyntaxKind.OpenBraceToken)) {
            body = ParseBlock();
        } else if (At(SyntaxKind.FatArrowToken)) {
            arrow = EatToken();
            expressionBody = ParseExpression();
        }

        return new FunctionDeclarationGreen(
            attributes,
            modifiers,
            fnKeyword,
            explicitProtocol,
            dot,
            identifier,
            typeParameters,
            parameters,
            asyncKeyword,
            throwsSpec,
            returnClause,
            whereClause,
            body,
            arrow,
            expressionBody
        );
    }

    GreenNode ParseReturnClause() {
        var arrow = EatToken(SyntaxKind.ArrowToken);
        return new ReturnClauseGreen(arrow, ParseType());
    }

    GreenNode ParseParameterList(
        SyntaxKind openKind = SyntaxKind.OpenParenToken,
        SyntaxKind closeKind = SyntaxKind.CloseParenToken
    ) =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(openKind);
                var parameters = ParseSeparatedList(closeKind, ParseParameter);
                var close = EatToken(closeKind);
                return new ParameterListGreen(open, parameters, close);
            }
        );

    GreenNode ParseParameter() {
        // '@escaping f: () -> Int' is not Sable: what a closure parameter may
        // do with its closure is part of its type, 'f: @escaping () -> Int'.
        // It is read as written, with the error and the fix that moves it.
        var attributeItems = ImmutableArray.CreateBuilder<GreenNode>();
        var misplaced = new List<(string Name, TextSpan Span)>();
        while (At(SyntaxKind.AtToken)) {
            var start = CurrentSpan.Start;
            attributeItems.Add(ParseAttribute(onType: false, out var name));
            if (IsTypeAttribute(name)) {
                misplaced.Add((name, TextSpan.FromBounds(start, _tokenPosition)));
            }
        }

        var attributes = GreenList.Create(attributeItems.ToImmutable());

        // 'fn build(in r: region)' is labelled 'in', as the spec calls it —
        // 'build(in: arena)' — not an unlabelled parameter with the 'in'
        // modifier: that modifier only borrows, as every parameter does, so
        // reading the word as the label loses nothing. The modifier can still
        // be written after the colon ('r: in region') or before a label
        // ('in _ r: region').
        //
        // 'ref a: Node' and 'borrowing h: FileHandle' are not Sable: a
        // parameter that refers to its argument says so in its type,
        // 'a: Node&'. The words stay reserved so that they say so, and the
        // parameter reads as the one it meant.
        //
        // Every such word is skipped, however many are written: the error
        // names the first, and its fix deletes them all.
        var modifierItems = ImmutableArray.CreateBuilder<GreenNode>();
        var removed = ImmutableArray.CreateBuilder<(SyntaxKind Kind, TextSpan Span)>();
        while (true) {
            if (IsRemovedParameterModifier(Current.Kind)) {
                removed.Add(RemovedKeyword());
            } else if (IsParameterModifier(Current.Kind) && !IsInLabel()) {
                modifierItems.Add(EatToken());
            } else {
                break;
            }
        }

        var modifiers = GreenList.Create(modifierItems.ToImmutable());

        // 'from source: T' has an external label; 'source: T' does not. The
        // label may be a keyword — 'fn first(where p: ...)' reads naturally.
        GreenToken? label = null;
        if ((Current.Kind is SyntaxKind.IdentifierToken or SyntaxKind.UnderscoreToken
                || Current.IsKeywordToken())
            && Peek(1).Kind == SyntaxKind.IdentifierToken) {
            label = EatToken();
        }

        var identifier = At(SyntaxKind.UnderscoreToken) ? EatToken() : EatIdentifier();
        var colon = EatToken(SyntaxKind.ColonToken);

        // 'into buf: inout U8[]' puts the modifier after the colon, which is
        // the form that reads well once a parameter has an external label.
        // 'h: borrowing FileHandle' is not Sable either; 'a: ref Node' is a
        // type, and says so as one.
        while (At(SyntaxKind.BorrowingKeyword)) {
            removed.Add(RemovedKeyword());
        }

        var typeModifier = IsParameterModifier(Current.Kind) ? EatToken() : null;

        var typeStart = CurrentSpan.Start;
        var type = At(SyntaxKind.AtToken) ? new AttributedTypeGreen(ParseAttributes(onType: true), ParseType()) : ParseType();
        if (misplaced.Count > 0) {
            var written = string.Concat(misplaced.Select(static m => "@" + m.Name + " "));
            var typeText = type.ToFullString().Substring(type.LeadingTriviaWidth, type.Width);
            _diagnostics.Add(
                Diagnostic.Error(
                    ErrorCode.ERR_AttributeOnParameterType,
                    new TextSpan(misplaced[0].Span.Start, misplaced[0].Name.Length + 1),
                    $"'@{misplaced[0].Name}' is part of the parameter's type, and written on it: '{identifier.Text}: {written}{typeText}'"
                ) with {
                    Fixes = [
                        new(
                            $"Write '{identifier.Text}: {written}{typeText}'",
                            [..misplaced.Select(static m => new TextChange(m.Span, "")), new TextChange(new TextSpan(typeStart, 0), written)]
                        ),
                    ],
                }
            );
        }

        if (removed.Count > 0) {
            var keyword = removed[0];
            type = ReadAsReference(
                type,
                keyword,
                keyword.Kind == SyntaxKind.BorrowingKeyword
                    ? "a parameter taken by reference says so in its type (a class or @noncopy one is, without it)"
                    : "a parameter that refers to its argument says so in its type",
                identifier.Text + ": ",
                removed.Skip(1).Select(static other => other.Span)
            );
        }
        var ellipsis = TryEatToken(SyntaxKind.DotDotDotToken);

        GreenNode? defaultValue = null;
        if (At(SyntaxKind.EqualsToken)) {
            var equals = EatToken();
            defaultValue = new EqualsValueClauseGreen(equals, ParseExpression());
        }

        return new ParameterGreen(
            attributes,
            modifiers,
            label,
            identifier,
            colon,
            typeModifier,
            type,
            ellipsis,
            defaultValue
        );
    }

    /// <summary>Whether a parameter starts 'in name:', where 'in' is the external label.</summary>
    bool IsInLabel() =>
        At(SyntaxKind.InKeyword)
        && Peek(1).Kind == SyntaxKind.IdentifierToken
        && Peek(2).Kind == SyntaxKind.ColonToken;

    static bool IsParameterModifier(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.InoutKeyword
                or SyntaxKind.OutKeyword
                or SyntaxKind.ConsumingKeyword
                or SyntaxKind.InKeyword => true,
            _ => false,
        };

    /// <summary>'ref' and 'borrowing' before a parameter: reserved, to say what Sable writes instead.</summary>
    static bool IsRemovedParameterModifier(SyntaxKind kind) =>
        kind is SyntaxKind.RefKeyword or SyntaxKind.BorrowingKeyword;

    /// <summary>
    ///     Skips 'ref' or 'borrowing', returning where it was with the
    ///     whitespace after it, which the fix deletes with it.
    /// </summary>
    (SyntaxKind Kind, TextSpan Span) RemovedKeyword() {
        var kind = Current.Kind;
        var span = new TextSpan(CurrentSpan.Start, Current.Text.Length + Current.TrailingTriviaWidth);
        SkipCurrentToken();
        return (kind, span);
    }

    /// <summary>
    ///     A type written with 'ref' or 'borrowing' before it, read as the
    ///     reference it meant, 'T&amp;', with the error that says so and the
    ///     fix that writes it.
    /// </summary>
    GreenNode ReadAsReference(
        GreenNode type,
        (SyntaxKind Kind, TextSpan Span) keyword,
        string why,
        string parameter = "",
        IEnumerable<TextSpan>? others = null
    ) {
        var text = type.ToFullString().Substring(type.LeadingTriviaWidth, type.Width);
        var end = _tokenPosition - type.TrailingTriviaWidth;
        var word = keyword.Kind == SyntaxKind.RefKeyword ? "ref" : "borrowing";
        var diagnostic = Diagnostic.Error(
            ErrorCode.ERR_RefIsReferenceType,
            new TextSpan(keyword.Span.Start, word.Length),
            $"'{word}' is not Sable: {why}, '{parameter}{text}&'"
        ) with {
            Fixes = [
                new(
                    $"Write '{text}&'",
                    [
                        new TextChange(keyword.Span, ""),
                        ..(others ?? []).Select(static span => new TextChange(span, "")),
                        new TextChange(new TextSpan(end, 0), "&"),
                    ]
                ),
            ],
        };
        _diagnostics.Add(diagnostic);

        return type is ReferenceTypeGreen || type.Width == 0
            ? type
            : new ReferenceTypeGreen(type, CreateMissing(SyntaxKind.AmpersandToken), null, null);
    }

    GreenNode ParseGenericParameterList() =>
        WithBracesAllowed<GreenNode>(() => {
                var less = EatToken(SyntaxKind.LessToken);

                var items = ImmutableArray.CreateBuilder<GreenNode>();
                while (!IsTypeArgumentListEnd() && !At(SyntaxKind.EndOfFile)) {
                    items.Add(ParseGenericParameter());
                    var comma = TryEatToken(SyntaxKind.CommaToken);
                    if (comma is null) {
                        break;
                    }

                    items.Add(comma);
                }

                var greater = EatGreaterThan();
                return new GenericParameterListGreen(less, GreenList.Create(items.ToImmutable()), greater);
            }
        );

    GreenNode ParseGenericParameter() {
        var attributes = ParseAttributes();
        var variance = Current.Kind is SyntaxKind.OutKeyword or SyntaxKind.InKeyword
            ? EatToken()
            : null;

        var identifier = EatIdentifier();
        var colon = TryEatToken(SyntaxKind.ColonToken);
        var constraint = colon is null ? null : ParseIntersectionType();
        var equals = TryEatToken(SyntaxKind.EqualsToken);
        var defaultType = equals is null ? null : ParseType();

        return new GenericParameterGreen(attributes, variance, identifier, colon, constraint, equals, defaultType);
    }

    GreenNode ParseWhereClause() {
        var whereKeyword = EatToken(SyntaxKind.WhereKeyword);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (true) {
            items.Add(ParseWhereItem());
            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) {
                break;
            }

            items.Add(comma);
            if (At(SyntaxKind.EndOfFile) || At(SyntaxKind.OpenBraceToken)) {
                break;
            }
        }

        return new WhereClauseGreen(whereKeyword, GreenList.Create(items.ToImmutable()));
    }

    /// <summary>
    ///     Parses one item of a <c>where</c> clause: <c>T: P</c>,
    ///     <c>S.Element == T</c>, or a const-generic predicate such as
    ///     <c>N &gt; 0</c>.
    /// </summary>
    /// <remarks>
    ///     The two shapes share a first token, so a speculative parse of a type
    ///     decides: a type followed by <c>:</c> or <c>==</c> is a constraint, and
    ///     anything else is an expression.
    /// </remarks>
    GreenNode ParseWhereItem() {
        var isConstraint = Lookahead(
            LookaheadKind.WhereConstraint,
            static parser => {
                parser.WithBracesSuppressed(() => parser.ParsePostfixType());
                return parser.Current.Kind is SyntaxKind.ColonToken or SyntaxKind.EqualsEqualsToken;
            }
        );

        return isConstraint
            ? ParseWhereConstraint()
            : new WherePredicateGreen(WithBracesSuppressed(ParseExpression));
    }

    GreenNode ParseWhereConstraint() {
        var left = WithBracesSuppressed(() => ParsePostfixType());
        var op = Current.Kind switch {
            SyntaxKind.ColonToken => EatToken(),
            SyntaxKind.EqualsEqualsToken => EatToken(),
            _ => EatToken(SyntaxKind.ColonToken),
        };
        var right = WithBracesSuppressed(() => ParseIntersectionType());
        return new WhereConstraintGreen(left, op, right);
    }

    GreenNode ParseBaseTypeList() {
        var colon = EatToken(SyntaxKind.ColonToken);

        var items = ImmutableArray.CreateBuilder<GreenNode>();
        while (true) {
            items.Add(WithBracesSuppressed(() => ParseIntersectionType()));
            var comma = TryEatToken(SyntaxKind.CommaToken);
            if (comma is null) {
                break;
            }

            items.Add(comma);
            if (At(SyntaxKind.EndOfFile) || At(SyntaxKind.OpenBraceToken)) {
                break;
            }
        }

        return new BaseTypeListGreen(colon, GreenList.Create(items.ToImmutable()));
    }

    (GreenToken Keyword, GreenToken Identifier, GreenNode? TypeParameters,
        GreenNode? BaseTypes, GreenNode? WhereClause, GreenToken Open) ParseTypeHeader() {
        var keyword = EatToken();
        var identifier = EatIdentifier();
        var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
        var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;
        var open = EatToken(SyntaxKind.OpenBraceToken);
        return (keyword, identifier, typeParameters, baseTypes, whereClause, open);
    }

    GreenNode ParseTypeDeclaration(GreenList attributes, GreenList modifiers) {
        var kind = Current.Kind;
        var header = ParseTypeHeader();
        var members = ParseMembers(
            GreenList.Empty,
            ParseMemberDeclaration,
            SyntaxKind.CloseBraceToken,
            MemberContext.Declaration
        );
        var close = EatToken(SyntaxKind.CloseBraceToken);

        return kind switch {
            SyntaxKind.StructKeyword => new StructDeclarationGreen(
                attributes,
                modifiers,
                header.Keyword,
                header.Identifier,
                header.TypeParameters,
                header.BaseTypes,
                header.WhereClause,
                header.Open,
                members,
                close
            ),
            SyntaxKind.ClassKeyword => new ClassDeclarationGreen(
                attributes,
                modifiers,
                header.Keyword,
                header.Identifier,
                header.TypeParameters,
                header.BaseTypes,
                header.WhereClause,
                header.Open,
                members,
                close
            ),
            SyntaxKind.ActorKeyword => new ActorDeclarationGreen(
                attributes,
                modifiers,
                header.Keyword,
                header.Identifier,
                header.TypeParameters,
                header.BaseTypes,
                header.WhereClause,
                header.Open,
                members,
                close
            ),
            _ => new ProtocolDeclarationGreen(
                attributes,
                modifiers,
                header.Keyword,
                header.Identifier,
                header.TypeParameters,
                header.BaseTypes,
                header.WhereClause,
                header.Open,
                members,
                close
            ),
        };
    }

    GreenNode ParseUnionDeclaration(GreenList attributes, GreenList modifiers) {
        var keyword = EatToken(SyntaxKind.UnionKeyword);
        var identifier = EatIdentifier();
        // 'union Node..' marks a variant-sized union.
        var variableSize = TryEatToken(SyntaxKind.DotDotToken);
        var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
        var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var members = ParseMembers(
            GreenList.Empty,
            ParseUnionMember,
            SyntaxKind.CloseBraceToken,
            MemberContext.Union
        );
        var close = EatToken(SyntaxKind.CloseBraceToken);

        return new UnionDeclarationGreen(
            attributes,
            modifiers,
            keyword,
            identifier,
            variableSize,
            typeParameters,
            baseTypes,
            whereClause,
            open,
            members,
            close
        );
    }

    /// <summary>A union body holds cases first, then ordinary members.</summary>
    GreenNode ParseUnionMember(GreenList leadingAttributes) {
        var attributes = leadingAttributes.SlotCount > 0 ? leadingAttributes : ParseAttributes();

        if (At(SyntaxKind.IdentifierToken)) {
            var identifier = EatToken();
            GreenNode? payload = null;
            if (At(SyntaxKind.OpenParenToken)) {
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

    GreenNode ParseUnionCaseField() {
        GreenToken? name = null;
        GreenToken? colon = null;
        if (IsArgumentLabel()) {
            name = EatToken();
            colon = EatToken(SyntaxKind.ColonToken);
        }

        return new UnionCaseFieldGreen(name, colon, ParseType());
    }

    GreenNode ParseEnumDeclaration(GreenList attributes, GreenList modifiers) {
        var keyword = EatToken(SyntaxKind.EnumKeyword);
        var identifier = EatIdentifier();
        var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var members = ParseMembers(
            GreenList.Empty,
            ParseEnumMember,
            SyntaxKind.CloseBraceToken,
            MemberContext.Enum
        );
        var close = EatToken(SyntaxKind.CloseBraceToken);

        return new EnumDeclarationGreen(
            attributes,
            modifiers,
            keyword,
            identifier,
            baseTypes,
            open,
            members,
            close
        );
    }

    GreenNode ParseEnumMember(GreenList leadingAttributes) {
        var attributes = leadingAttributes.SlotCount > 0 ? leadingAttributes : ParseAttributes();

        if (!At(SyntaxKind.IdentifierToken)) {
            return ParseMemberDeclaration(attributes);
        }

        var identifier = EatToken();
        var equals = TryEatToken(SyntaxKind.EqualsToken);
        var value = equals is null ? null : ParseExpression();
        var comma = TryEatToken(SyntaxKind.CommaToken);
        return new EnumCaseDeclarationGreen(attributes, identifier, equals, value, comma);
    }

    GreenNode ParseExtensionDeclaration(GreenList attributes, GreenList modifiers) {
        var keyword = EatToken(SyntaxKind.ExtensionKeyword);

        // 'extension<Element> Element[>..]': parameters for a type that has no
        // name to write them after — every array of a kind.
        var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
        var type = WithBracesSuppressed(() => ParsePostfixType());
        var baseTypes = At(SyntaxKind.ColonToken) ? ParseBaseTypeList() : null;
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;
        var open = EatToken(SyntaxKind.OpenBraceToken);

        var members = ParseMembers(
            GreenList.Empty,
            ParseMemberDeclaration,
            SyntaxKind.CloseBraceToken,
            MemberContext.Declaration
        );
        var close = EatToken(SyntaxKind.CloseBraceToken);

        return new ExtensionDeclarationGreen(
            attributes,
            modifiers,
            keyword,
            typeParameters,
            type,
            baseTypes,
            whereClause,
            open,
            members,
            close
        );
    }

    GreenNode ParseSubscriptDeclaration(GreenList attributes, GreenList modifiers) {
        var keyword = EatToken(SyntaxKind.SubscriptKeyword);
        var typeParameters = At(SyntaxKind.LessToken) ? ParseGenericParameterList() : null;
        var parameters = ParseParameterList();
        var returnClause = ParseReturnClause();
        var whereClause = At(SyntaxKind.WhereKeyword) ? ParseWhereClause() : null;

        GreenNode? accessors = null;
        GreenToken? arrow = null;
        GreenNode? expressionBody = null;

        if (At(SyntaxKind.OpenBraceToken)) {
            accessors = ParseAccessorList();
        } else if (At(SyntaxKind.FatArrowToken)) {
            arrow = EatToken();
            expressionBody = ParseExpression();
        }

        return new SubscriptDeclarationGreen(
            attributes,
            modifiers,
            keyword,
            typeParameters,
            parameters,
            returnClause,
            whereClause,
            accessors,
            arrow,
            expressionBody
        );
    }

    GreenNode ParseOperatorDeclaration(GreenList attributes, GreenList modifiers) {
        var fixity = Current.Kind is SyntaxKind.PrefixKeyword or SyntaxKind.PostfixKeyword
            ? EatToken()
            : null;

        var operatorKeyword = EatToken(SyntaxKind.OperatorKeyword);

        // Anything can be the operator being declared, except what plainly ends
        // the declaration.
        GreenToken operatorToken;
        if (Current.Kind is SyntaxKind.EndOfFile
            or SyntaxKind.OpenBraceToken
            or SyntaxKind.CloseBraceToken
            or SyntaxKind.SemicolonToken) {
            _diagnostics.Error(
                ErrorCode.ERR_ExpectedToken,
                CurrentSpan,
                $"expected the operator being declared but found '{DescribeCurrent()}'"
            );
            operatorToken = CreateMissing(SyntaxKind.PlusToken);
        } else {
            operatorToken = EatToken();
        }

        // '[]' and '()' are written as two tokens.
        var second = operatorToken.Kind switch {
            SyntaxKind.OpenBracketToken => EatToken(SyntaxKind.CloseBracketToken),
            SyntaxKind.OpenParenToken => EatToken(SyntaxKind.CloseParenToken),
            _ => null,
        };

        var parameters = ParseParameterList();
        var returnClause = ParseReturnClause();

        GreenNode? body = null;
        GreenToken? arrow = null;
        GreenNode? expressionBody = null;

        if (At(SyntaxKind.OpenBraceToken)) {
            body = ParseBlock();
        } else if (At(SyntaxKind.FatArrowToken)) {
            arrow = EatToken();
            expressionBody = ParseExpression();
        }

        return new OperatorDeclarationGreen(
            attributes,
            modifiers,
            fixity,
            operatorKeyword,
            operatorToken,
            second,
            parameters,
            returnClause,
            body,
            arrow,
            expressionBody
        );
    }

    /// <summary>
    ///     <c>var area: F64 => width * height</c>: a getter alone, written
    ///     without one. It parses as <c>{ get => width * height }</c> does, its
    ///     braces and keyword there but empty, so that everything past the
    ///     parser sees the getter it means.
    /// </summary>
    GreenNode ParseExpressionBodiedProperty() {
        var arrow = EatToken();
        var expression = ParseExpression();
        var getter = new AccessorGreen(
            GreenList.Empty,
            GreenList.Empty,
            CreateMissing(SyntaxKind.GetKeyword),
            null,
            null,
            null,
            null,
            arrow,
            expression
        );
        return new AccessorListGreen(
            CreateMissing(SyntaxKind.OpenBraceToken),
            GreenList.Create([getter]),
            CreateMissing(SyntaxKind.CloseBraceToken)
        );
    }

    GreenNode ParseAccessorList() =>
        WithBracesAllowed<GreenNode>(() => {
                var open = EatToken(SyntaxKind.OpenBraceToken);

                var items = ImmutableArray.CreateBuilder<GreenNode>();
                while (!At(SyntaxKind.CloseBraceToken) && !At(SyntaxKind.EndOfFile)) {
                    var before = _index;
                    items.Add(ParseAccessor());
                    if (_index == before) {
                        SkipCurrentToken();
                    }
                }

                var close = EatToken(SyntaxKind.CloseBraceToken);
                return new AccessorListGreen(open, GreenList.Create(items.ToImmutable()), close);
            }
        );

    GreenNode ParseAccessor() {
        var attributes = ParseAttributes();
        var modifiers = ParseModifiers();

        var keyword = Current.Kind is SyntaxKind.GetKeyword
            or SyntaxKind.SetKeyword
            or SyntaxKind.WillSetKeyword
            or SyntaxKind.DidSetKeyword
            ? EatToken()
            : EatToken(SyntaxKind.GetKeyword);

        GreenToken? open = null;
        GreenToken? parameterName = null;
        GreenToken? close = null;
        if (At(SyntaxKind.OpenParenToken)) {
            open = EatToken();
            parameterName = EatIdentifier();
            close = EatToken(SyntaxKind.CloseParenToken);
        }

        GreenNode? body = null;
        GreenToken? arrow = null;
        GreenNode? expressionBody = null;

        if (At(SyntaxKind.OpenBraceToken)) {
            body = ParseBlock();
        } else if (At(SyntaxKind.FatArrowToken)) {
            arrow = EatToken();
            expressionBody = ParseExpression();
        }

        return new AccessorGreen(
            attributes,
            modifiers,
            keyword,
            open,
            parameterName,
            close,
            body,
            arrow,
            expressionBody
        );
    }

    // =======================================================================
    // Interpolated strings
    // =======================================================================

    /// <summary>
    ///     Rebuilds a string literal token whose text contains <c>\(...)</c> holes
    ///     into a node whose holes are real expressions.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The lexer keeps an interpolated string as one token, because the
    ///         holes nest arbitrarily and tracking that in the lexer would make it
    ///         stateful. The parser takes the token apart again here, slicing the
    ///         original characters so that the pieces tile the token exactly, and
    ///         re-entering itself for each hole.
    ///     </para>
    ///     <para>
    ///         Diagnostics from inside a hole are shifted into file
    ///         coordinates, so they point where the reader is looking.
    ///     </para>
    /// </remarks>
    GreenNode ParseInterpolatedString() {
        var token = Current;
        var tokenTextStart = _tokenPosition + token.LeadingTriviaWidth;
        var parts = token.Parts;
        var text = token.Text;

        // The eaten token, not 'token': eating attaches any pending skipped
        // text to its leading trivia, and that text must reach the tree.
        var eaten = EatToken();

        var firstRaw = parts[0].Span.Start;
        var lastRaw = parts[^1].Span.End;

        var open = new GreenToken(SyntaxKind.InterpolatedStringStartToken, text[..firstRaw], eaten.Leading, []);

        var contents = ImmutableArray.CreateBuilder<GreenNode>(parts.Length);

        for (var i = 0; i < parts.Length; i++) {
            var part = parts[i];
            var raw = text[part.Span.Start..part.Span.End];

            if (part.Kind == StringPartKind.Literal) {
                contents.Add(
                    new InterpolatedTextGreen(new GreenToken(SyntaxKind.InterpolatedTextToken, raw, [], [], part.Text))
                );
                continue;
            }

            contents.Add(ParseInterpolationHole(part, raw, tokenTextStart));
        }

        var close = new GreenToken(
            SyntaxKind.InterpolatedStringEndToken,
            text[lastRaw..],
            [],
            token.Trailing,
            terminatesStatement: token.TerminatesStatement
        );

        return new InterpolatedStringExpressionGreen(open, GreenList.Create(contents.ToImmutable()), close);
    }

    GreenNode ParseInterpolationHole(StringPart part, string raw, int tokenTextStart) {
        // raw is '\(' + inner + ')', except that an unterminated hole has no
        // ')'. The inner text is read off raw rather than the part, so the
        // pieces always add back up to exactly what the source said.
        var openText = raw[..Math.Min(2, raw.Length)];
        var terminated = raw.Length > openText.Length && raw[^1] == ')';
        var inner = raw[openText.Length..(terminated ? raw.Length - 1 : raw.Length)];

        var holeStart = tokenTextStart + part.Span.Start + openText.Length;

        var source = new SourceText(inner, "<interpolation>");
        var inner_parser = new Parser(source, _options);
        var expression = inner_parser.ParseExpression();

        // A colon after the expression starts its format spec, '\(x:08x)',
        // which runs to the closing paren. The spec is text, not tokens — '>8'
        // or '.2' would lex as operators and numbers — so it is one token,
        // which the binder reads. A ternary's colon is the expression's, so
        // '\(a ? b : c)' has no spec.
        var covered = expression.FullWidth;
        GreenToken? format = null;
        if (covered < inner.Length && inner[covered] == ':') {
            var spec = inner[covered..];
            format = new GreenToken(SyntaxKind.InterpolationFormatToken, spec, [], [], spec[1..]);
            covered = inner.Length;
        }

        // An unterminated hole is already reported, and what it swallowed up
        // to the end of the line is rarely an expression; its own complaints
        // would only repeat the one that matters. Nor is the lexer's opinion
        // of a format spec's text one: the binder reads that.
        if (terminated) {
            foreach (var diagnostic in inner_parser._diagnostics) {
                if (format is not null && diagnostic.Span.Start >= expression.FullWidth) {
                    continue;
                }

                _diagnostics.Add(diagnostic with { Span = new(diagnostic.Span.Start + holeStart, diagnostic.Span.Length) });
            }
        }

        // Whatever the expression did not cover rides on the closing paren, so
        // the hole still tiles.
        var leftover = covered < inner.Length ? inner[covered..] : string.Empty;

        ImmutableArray<GreenTrivia> closeLeading = leftover.Length == 0
            ? []
            : [
                new GreenTrivia(
                    leftover.Trim().Length == 0
                        ? SyntaxKind.WhitespaceTrivia
                        : SyntaxKind.SkippedTokensTrivia,
                    leftover
                )
            ];

        var open = new GreenToken(SyntaxKind.InterpolationStartToken, openText, [], []);
        var close = new GreenToken(
            SyntaxKind.InterpolationEndToken,
            terminated ? ")" : string.Empty,
            closeLeading,
            [],
            isMissing: !terminated
        );

        return new InterpolationGreen(open, expression, format, close);
    }
}

internal static class ParserExtensions {
    public static bool IsContextualKeywordToken(this GreenToken token) => SyntaxFacts.IsContextualKeyword(token.Kind);

    public static bool IsKeywordToken(this GreenToken token) => SyntaxFacts.IsKeyword(token.Kind);
}
