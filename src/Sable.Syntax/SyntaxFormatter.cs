using System.Text;

namespace Sable.Syntax;

/// <summary>
///     Renders a syntax tree as canonically formatted source.
/// </summary>
/// <remarks>
///     <para>
///         There are no options, by design: one style, settled once. The
///         formatter walks the tree rather than the token stream, because almost every
///         hard spacing question — is this <c>&lt;</c> a generic bracket or a
///         comparison, is this <c>-</c> unary or binary, is this <c>:</c> a type
///         annotation or the middle of a conditional — is answered for free by the
///         node a token belongs to.
///     </para>
///     <para>
///         Two properties are tested and must hold: formatting is idempotent,
///         and formatting never changes the tree. Comments and the author's blank
///         lines survive; everything else about the whitespace is decided here.
///     </para>
/// </remarks>
public static class SyntaxFormatter {
    const string IndentUnit = "    ";

    public static string Format(SyntaxTree tree) {
        ArgumentNullException.ThrowIfNull(tree);

        var writer = new Writer();
        writer.WriteNode(tree.Root, tree.Root.Kind);

        // A compilation unit owns its end-of-file token, so writing it again
        // here would emit its trivia twice.
        if (tree.Root is not CompilationUnitSyntax && !tree.EndOfFileToken.IsNone) {
            writer.WriteToken(tree.EndOfFileToken, null);
        }

        return writer.Finish();
    }

    /// <summary>Formats source text directly.</summary>
    public static string Format(string text, ParseOptions? options = null) =>
        Format(SyntaxTree.Parse(new(text), options ?? ParseOptions.Default));

    private sealed class Writer {
        readonly StringBuilder _builder = new();
        int _indent;
        int _pendingLines;
        bool _pendingSpace;
        bool _started;

        SyntaxKind _previousKind = SyntaxKind.None;
        SyntaxKind _previousParent = SyntaxKind.None;

        /// <summary>
        ///     True when the previous token's trailing trivia ended its line.
        ///     Trailing trivia stops at the first line break, so a blank line
        ///     between two declarations is one newline on each side.
        /// </summary>
        bool _previousEndedLine;

        public string Finish() {
            // Exactly one trailing newline.
            while (_builder.Length > 0 && _builder[^1] is ' ' or '\n') {
                _builder.Length--;
            }

            if (_builder.Length > 0) {
                _builder.Append('\n');
            }

            return _builder.ToString();
        }

        public void WriteNode(SyntaxNode node, SyntaxKind parentKind) {
            var kind = node.Kind;

            var startsLine = StartsOnItsOwnLine(node, parentKind);
            if (startsLine && !InsideInlineBrace) {
                BreakLine(BlankLinesBefore(node));
            }

            // A line the formatter starts is a unit for kept breaks: a
            // continuation inside it is indented until it ends.
            var line = startsLine ? new Group(SyntaxKind.None, _tokens) : null;
            if (line is not null) {
                _groups.Push(line);
            }

            if (node is MatchExpressionSyntax) {
                _matches.Push([]);
            }

            foreach (var child in node.ChildNodesAndTokens()) {
                if (child.IsToken) {
                    WriteToken(child.AsToken(), node);
                } else {
                    WriteNode(child.AsNode()!, kind);
                }
            }

            if (node is MatchArmSyntax && _matches.Count > 0 && _matches.Peek() is [.., { End: < 0 } arm]) {
                arm.End = _builder.Length;
            }

            if (node is MatchExpressionSyntax) {
                AlignArrows(_matches.Pop());
            }

            if (line is not null) {
                // Whatever a malformed tree left open closes with its line.
                while (_groups.Count > 0) {
                    var open = _groups.Pop();
                    Release(open);
                    if (ReferenceEquals(open, line)) {
                        break;
                    }
                }
            }

            if (EndsItsLine(node, parentKind) && !InsideInlineBrace) {
                BreakLine(0);
            }
        }

        public void WriteToken(SyntaxToken token, SyntaxNode? parent) {
            var parentKind = parent?.Kind ?? SyntaxKind.None;

            if (token.IsMissing) {
                WriteTrivia(token.LeadingTrivia, true);
                return;
            }

            // A line break the author made inside an expression, where the
            // formatter would not make one, is kept: gofmt's rule, since
            // there is no line width here to decide it instead.
            var brokenBefore = _started && (_previousEndedLine || token.LeadingTrivia.Any(SyntaxKind.NewLineTrivia));
            var closesGroup = Closer(token.Kind) && _groups.Count > 0 && _groups.Peek().Closer == token.Kind
                && (token.Kind != SyntaxKind.CloseBraceToken || (_braces.Count > 0 && !_braces.Peek().OpensBody));
            var closesBody = token.Kind == SyntaxKind.CloseBraceToken && _braces.Count > 0 && _braces.Peek().OpensBody;
            if (brokenBefore && !_structuralBreak && !closesGroup && !closesBody && !ContinuesTheLineBefore(token.Kind, parentKind)) {
                KeepBreak(token.Kind);
            }

            WriteTrivia(token.LeadingTrivia, true);

            if (closesGroup) {
                Release(_groups.Pop());
                if (brokenBefore && !_structuralBreak) {
                    BreakLine(0, structural: false);
                }
            }

            if (token.Kind == SyntaxKind.CloseBraceToken && _braces.Count > 0) {
                var brace = _braces.Pop();
                if (brace.OpensBody) {
                    Outdent();

                    // Nothing was written inside, so keep '{ }' on one line.
                    if (_builder.Length == brace.Mark) {
                        _pendingLines = 0;
                        _pendingSpace = true;
                    } else {
                        BreakLine(0);
                    }
                }
            }

            // '} else {' and '} catch {' read better than a break between.
            if (ContinuesTheLineBefore(token.Kind, parentKind) && _pendingLines > 0) {
                _pendingLines = 0;
                _pendingSpace = true;
            }

            if (_started && NeedsSpace(token.Kind, parentKind)) {
                _pendingSpace = true;
            }

            Write(token.Text);
            _tokens++;

            if (parent is MatchArmSyntax arm && _matches.Count > 0) {
                var offset = _builder.Length - token.Text.Length;
                if (token == arm.CaseOrDefaultKeyword) {
                    _matches.Peek().Add(new(offset));
                } else if (token == arm.ArrowToken && _matches.Peek() is [.., var written]) {
                    written.Arrow = offset;
                }
            }

            _previousKind = token.Kind;
            _previousParent = parentKind;

            // Trailing trivia first, so a comment after '{' stays on its line.
            // The mark is taken before it: a body holding only a comment is
            // not empty, and its '}' must not land after a line comment.
            var mark = _builder.Length;
            WriteTrivia(token.TrailingTrivia, false);

            if (token.Kind == SyntaxKind.OpenBraceToken) {
                var opensBody = OpensABody(parent);
                _braces.Push(new(opensBody, mark, !opensBody && parent is not null && IsTypeDeclaration(parent)));
                if (opensBody) {
                    // A body is indented from its line, not from a
                    // continuation of it: 'if a &&\n    b {' closes at 'if'.
                    if (_groups.Count > 0 && _groups.Peek() is { Continued: true, ContinuedAt: not SyntaxKind.CatchKeyword } continued) {
                        continued.Continued = false;
                        continued.Indents--;
                        Outdent();
                    }

                    Indent();

                    // '{ a, b in' keeps its header on the brace's line; the
                    // body starts after 'in'.
                    if (parent is not BraceClosureExpressionSyntax { Header: not null }) {
                        BreakLine(0);
                    }
                } else {
                    _groups.Push(new(SyntaxKind.CloseBraceToken, _tokens));
                }
            } else if (token.Kind == SyntaxKind.InKeyword && parent is ClosureHeaderSyntax { Parent: BraceClosureExpressionSyntax closure } && OpensABody(closure)) {
                BreakLine(0);
            } else if (token.Kind is SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken) {
                _groups.Push(new(token.Kind == SyntaxKind.OpenParenToken ? SyntaxKind.CloseParenToken : SyntaxKind.CloseBracketToken, _tokens));
            }

            _previousEndedLine = token.TrailingTrivia.Any(SyntaxKind.NewLineTrivia);

            // In a type's body written on one line, a ';' separates members
            // that stay on that line: 'struct Q { var x: Int; var y: Int }'.
            if (token.TerminatesStatement
                && token.Kind != SyntaxKind.EndOfFile
                && token.Kind != SyntaxKind.OpenBraceToken
                && !(_braces.Count > 0 && _braces.Peek().OneLineMembers)) {
                BreakLine(0);
            }
        }

        // -------------------------------------------------------------------
        // Alignment
        // -------------------------------------------------------------------

        /// <summary>Where a match arm, its '=>' and its end were written (-1 until they are).</summary>
        private sealed class Arm(int start) {
            public int Start { get; } = start;
            public int Arrow { get; set; } = -1;
            public int End { get; set; } = -1;
        }

        /// <summary>Where the output's line holding <paramref name="offset" /> starts.</summary>
        int LineStart(int offset) {
            while (offset > 0 && _builder[offset - 1] != '\n') {
                offset--;
            }

            return offset;
        }

        /// <summary>The arms of each match being written, innermost on top.</summary>
        readonly Stack<List<Arm>> _matches = new();

        /// <summary>
        ///     Lines up the '=>' of a match's arms, as gofmt lines up the values
        ///     of consecutive lines: each run of arms written one to a line,
        ///     with no blank line or comment between them, has its arrows in
        ///     one column, the one its longest pattern needs.
        /// </summary>
        /// <remarks>
        ///     Imposed, not kept from the source, so that where the author put
        ///     spaces never matters. A blank line or a comment ends a run, which
        ///     is how an author keeps a long pattern from pushing the others
        ///     out. The arms are already written, so their arrows are moved in
        ///     place, last first so the earlier offsets still hold; a match
        ///     nested in an arm was aligned when it ended, before anything
        ///     after it was recorded.
        /// </remarks>
        void AlignArrows(List<Arm> arms) {
            bool OnOneLine(Arm arm) {
                if (arm.Arrow < 0 || arm.End < arm.Arrow) {
                    return false;
                }

                for (var i = arm.Start; i < arm.End; i++) {
                    if (_builder[i] == '\n') {
                        return false;
                    }
                }

                return true;
            }

            var runs = new List<List<Arm>>();
            List<Arm>? run = null;
            foreach (var arm in arms) {
                if (!OnOneLine(arm)) {
                    run = null;
                    continue;
                }

                // The next line of the run starts right where this one ended.
                var follows = run is not null && run[^1].End + 1 == LineStart(arm.Start) && _builder[run[^1].End] == '\n';
                if (!follows) {
                    run = [];
                    runs.Add(run);
                }

                run!.Add(arm);
            }

            var moves = new List<(int Offset, int Spaces)>();
            foreach (var group in runs.Where(static r => r.Count > 1)) {
                var column = group.Max(a => a.Arrow - a.Start);
                moves.AddRange(group.Select(a => (a.Arrow, column - (a.Arrow - a.Start))));
            }

            foreach (var (offset, spaces) in moves.OrderByDescending(static m => m.Offset)) {
                if (spaces > 0) {
                    _builder.Insert(offset, " ", spaces);
                }
            }
        }

        // -------------------------------------------------------------------
        // Trivia
        // -------------------------------------------------------------------

        void WriteTrivia(SyntaxTriviaList trivia, bool leading) {
            if (trivia.Count == 0) {
                return;
            }

            // When the token before ended its line, the first newline here
            // already makes a blank one.
            var sawNewLine = leading && _previousEndedLine;
            var blankLines = 0;
            var wroteLine = false;

            for (var index = 0; index < trivia.Count; index++) {
                var item = trivia[index];
                var previous = index > 0 ? trivia[index - 1].Kind : SyntaxKind.None;
                switch (item.Kind) {
                    case SyntaxKind.NewLineTrivia:
                        if (sawNewLine) {
                            blankLines++;
                        }

                        sawNewLine = true;
                        continue;

                    case SyntaxKind.WhitespaceTrivia:
                        continue;

                    case SyntaxKind.LineCommentTrivia:
                    case SyntaxKind.DocCommentTrivia:
                    case SyntaxKind.ModuleDocCommentTrivia:
                    case SyntaxKind.ShebangTrivia:
                    case SyntaxKind.DirectiveTrivia:
                        if (leading && (sawNewLine || !_started)) {
                            BreakLine(Math.Min(blankLines, 1), structural: false);
                        } else {
                            _pendingSpace = true;
                        }

                        Write(item.Text.TrimEnd());
                        BreakLine(0, structural: false);
                        wroteLine = true;

                        // The newline that ends this comment is its own, not a
                        // blank line before whatever follows.
                        sawNewLine = false;
                        blankLines = 0;
                        continue;

                    case SyntaxKind.BlockCommentTrivia:
                        if (leading && sawNewLine) {
                            BreakLine(Math.Min(blankLines, 1), structural: false);
                        } else {
                            _pendingSpace = true;
                        }

                        Write(item.Text);
                        if (leading && sawNewLine) {
                            BreakLine(0, structural: false);
                            wroteLine = true;
                        }

                        blankLines = 0;
                        continue;

                    case SyntaxKind.DisabledTextTrivia:
                        // An inactive region, a line at a time, verbatim and
                        // with its own indentation. A line with nothing on it
                        // either ends the directive before it or is a blank
                        // line the author left.
                        if (item.Text.AsSpan().Trim().IsEmpty) {
                            if (previous != SyntaxKind.DirectiveTrivia && _started) {
                                _pendingLines++;
                            }
                        } else {
                            BreakLine(0, structural: false);
                            WriteVerbatim(item.Text, indent: false);
                        }

                        wroteLine = true;

                        sawNewLine = false;
                        blankLines = 0;
                        continue;

                    case SyntaxKind.SkippedTokensTrivia:
                        // Text the parser could not use: emitted verbatim so
                        // nothing is ever lost.
                        if (leading) {
                            BreakLine(0, structural: false);
                        }

                        WriteVerbatim(item.Text);
                        continue;
                }
            }

            // A blank line between a comment and what it precedes stays.
            if (leading && wroteLine && blankLines > 0) {
                BreakLine(1, structural: false);
            }
        }

        // -------------------------------------------------------------------
        // Output primitives
        // -------------------------------------------------------------------

        void Write(string text) {
            if (text.Length == 0) {
                return;
            }

            Flush();
            _builder.Append(text);
            _started = true;
            _structuralBreak = false;
        }

        void WriteVerbatim(string text, bool indent = true) {
            if (text.Length == 0) {
                return;
            }

            _pendingSpace = false;
            Flush(indent);
            _builder.Append(text.TrimEnd('\n', '\r'));
            _started = true;
            _structuralBreak = false;
            BreakLine(0, structural: false);
        }

        void Flush(bool indent = true) {
            if (_pendingLines > 0) {
                for (var i = 0; i < _pendingLines; i++) {
                    _builder.Append('\n');
                }

                _pendingLines = 0;
                _pendingSpace = false;

                for (var i = 0; indent && i < _indent; i++) {
                    _builder.Append(IndentUnit);
                }
            } else if (_pendingSpace) {
                if (_builder.Length > 0 && _builder[^1] is not (' ' or '\n')) {
                    _builder.Append(' ');
                }

                _pendingSpace = false;
            }
        }

        /// <param name="structural">
        ///     False for a break only trivia or the author asked for; a
        ///     structural one — a statement's, a member's, a body's — is the
        ///     formatter's own, and a kept break never adds to it.
        /// </param>
        void BreakLine(int blankLines, bool structural = true) {
            if (!_started) {
                return;
            }

            _pendingSpace = false;
            _pendingLines = Math.Max(_pendingLines, 1 + blankLines);
            _structuralBreak |= structural;
        }

        /// <summary>True when the break about to be written is the formatter's own.</summary>
        bool _structuralBreak;

        /// <summary>Tokens written so far: a group knows whether anything follows its opener.</summary>
        int _tokens;

        /// <summary>
        ///     An open bracket, or a line the formatter started, and the
        ///     indentation the author's breaks inside it have added.
        /// </summary>
        private sealed class Group(SyntaxKind closer, int openedAt) {
            /// <summary>The token that closes it; none for a line.</summary>
            public SyntaxKind Closer { get; } = closer;

            /// <summary>The token count when it opened: a break right after the opener breaks the group.</summary>
            public int OpenedAt { get; } = openedAt;

            /// <summary>Broken right after its opener: one item per line, indented.</summary>
            public bool Broken { get; set; }

            /// <summary>Broken somewhere in the middle: the rest of it indented once.</summary>
            public bool Continued { get; set; }

            /// <summary>The token the continuation began at.</summary>
            public SyntaxKind ContinuedAt { get; set; }

            public int Indents { get; set; }
        }

        readonly Stack<Group> _groups = new();

        static bool Closer(SyntaxKind kind) =>
            kind is SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken;

        /// <summary>Keeps a break the author made, indenting what follows it.</summary>
        void KeepBreak(SyntaxKind at) {
            if (_groups.Count > 0) {
                var group = _groups.Peek();
                if (group.Closer != SyntaxKind.None && !group.Broken && _tokens == group.OpenedAt) {
                    group.Broken = true;
                    group.Indents++;
                    Indent();
                } else if (!group.Broken && !group.Continued) {
                    group.Continued = true;
                    group.ContinuedAt = at;
                    group.Indents++;
                    Indent();
                }
            }

            BreakLine(0, structural: false);
        }

        /// <summary>Takes back the indentation a group added, as it closes.</summary>
        void Release(Group group) {
            for (var i = 0; i < group.Indents; i++) {
                Outdent();
            }

            group.Indents = 0;
        }

        void Indent() => _indent++;
        void Outdent() => _indent = Math.Max(0, _indent - 1);

        /// <summary>
        ///     An open brace: whether it introduced an indented body, and how much
        ///     output existed at the time, so an empty body can stay on one line.
        /// </summary>
        private readonly record struct OpenBrace(bool OpensBody, int Mark, bool OneLineMembers = false);

        readonly Stack<OpenBrace> _braces = new();

        /// <summary>
        ///     True when the innermost open brace stayed on one line, such as a
        ///     short accessor list; nothing inside it breaks.
        /// </summary>
        bool InsideInlineBrace => _braces.Count > 0 && !_braces.Peek().OpensBody;

        /// <summary>Keywords that belong on the same line as the brace before them.</summary>
        static bool ContinuesTheLineBefore(SyntaxKind kind, SyntaxKind parentKind) =>
            (kind == SyntaxKind.ElseKeyword && parentKind == SyntaxKind.ElseClause)
            || (kind == SyntaxKind.CatchKeyword && parentKind == SyntaxKind.CatchClause)
            || (kind == SyntaxKind.WhileKeyword && parentKind == SyntaxKind.RepeatStatement);

        // -------------------------------------------------------------------
        // Layout rules
        // -------------------------------------------------------------------

        /// <summary>Blank lines the author left before a node, capped at one.</summary>
        int BlankLinesBefore(SyntaxNode node) {
            if (!IsMember(node)
                && node is not StatementSyntax
                && node.Kind is not (SyntaxKind.ImportDeclaration
                    or SyntaxKind.ModuleDeclaration
                    or SyntaxKind.Accessor
                    or SyntaxKind.MatchArm
                    or SyntaxKind.MatchCaseBlock
                    or SyntaxKind.SwitchCase)) {
                return 0;
            }

            var first = node.GetFirstToken();
            if (first.IsNone) {
                return 0;
            }

            var newLines = _previousEndedLine ? 1 : 0;
            foreach (var trivia in first.LeadingTrivia) {
                if (trivia.Kind == SyntaxKind.NewLineTrivia) {
                    newLines++;
                } else if (trivia.IsComment || trivia.Kind is SyntaxKind.DirectiveTrivia or SyntaxKind.DisabledTextTrivia) {
                    // Whatever comes after that is the trivia's to place.
                    break;
                }
            }

            return newLines >= 2 ? 1 : 0;
        }

        static bool StartsOnItsOwnLine(SyntaxNode node, SyntaxKind parentKind) =>
            // 'else if' is one line: the 'if' continues the 'else'.
            // An 'if' that gives a value stays where the value goes: 'let s = if c {'.
            (node is StatementSyntax && node.Kind != SyntaxKind.Block
                                     && parentKind is not (SyntaxKind.ElseClause or SyntaxKind.LabeledStatement or SyntaxKind.IfExpression))
            || IsMember(node)
            || (node.Kind == SyntaxKind.Accessor && !IsPropertyArrow(node))
            || node.Kind is SyntaxKind.ImportDeclaration
                or SyntaxKind.ModuleDeclaration
                or SyntaxKind.MatchArm
                or SyntaxKind.MatchCaseBlock
                or SyntaxKind.SwitchCase
            || (node.Kind == SyntaxKind.Attribute && IsMemberContext(parentKind));

        /// <summary><c>var area: F64 => w * h</c>: a getter written without its keyword and braces, which stays on its property's line.</summary>
        static bool IsPropertyArrow(SyntaxNode node) => node is AccessorSyntax { Keyword.IsMissing: true };

        static bool EndsItsLine(SyntaxNode node, SyntaxKind parentKind) =>
            IsMember(node)
            || node.Kind is SyntaxKind.ImportDeclaration or SyntaxKind.ModuleDeclaration
            || (node.Kind == SyntaxKind.Attribute && IsMemberContext(parentKind));

        /// <summary>
        ///     A member is a declaration that is not the module header or an
        ///     import, both of which have their own layout.
        /// </summary>
        static bool IsMember(SyntaxNode node) =>
            node is DeclarationSyntax
            && node.Kind is not (SyntaxKind.ModuleDeclaration or SyntaxKind.ImportDeclaration);

        static bool IsMemberContext(SyntaxKind kind) => IsMemberKind(kind);

        /// <summary>True when a brace opens an indented, line-per-item body.</summary>
        /// <remarks>
        ///     A short accessor list or a one-expression closure reads better on
        ///     one line: <c>var x: Int { get }</c> and <c>xs.filter { $0 &gt; 1 }</c>.
        ///     Anything holding statements gets the indented form — except a
        ///     declaration's body of one simple statement that its author wrote
        ///     on one line, as gofmt keeps it: <c>fn hash() -&gt; Int { return self }</c>.
        /// </remarks>
        static bool OpensABody(SyntaxNode? parent) =>
            parent switch {
                BlockSyntax block => !IsOneLineBody(block),
                not null when IsTypeDeclaration(parent) => !IsOneLineTypeBody(parent),
                MatchExpressionSyntax or SwitchStatementSyntax => true,

                AccessorListSyntax { OpenBraceToken.IsMissing: true } => false,
                AccessorListSyntax accessors => accessors.Accessors.Any(static a => a.Body is not null || a.ExpressionBody is not null)
                    && !(WrittenOnOneLine(accessors) && accessors.Accessors.All(static a => a.Body is null || IsOneLineBody(a.Body))),

                // '{ x in x * 2 }' written on one line stays there, as one
                // without a header does.
                BraceClosureExpressionSyntax closure =>
                    closure.Statements.Count > 1
                    || closure.Statements.Any(static statement => statement is not ExpressionStatementSyntax)
                    || (closure.Header is not null && !(closure.Statements.All(static s => IsSimple(s)) && WrittenOnOneLine(closure))),

                _ => false,
            };

        /// <summary>
        ///     A function's, initialiser's, operator's or accessor's body, or a
        ///     block inside an expression or a match arm, or a guard's exit, of
        ///     one statement with no block of its own, written on one line:
        ///     <c>guard let x = y else { return none }</c>; and the blocks of an
        ///     <c>if</c>/<c>else</c> chain written on one line, each of them
        ///     such a body. A loop's block always takes its own lines.
        /// </summary>
        static bool IsOneLineBody(BlockSyntax block) =>
            block.Parent switch {
                FunctionDeclarationSyntax
                    or InitDeclarationSyntax
                    or DeinitDeclarationSyntax
                    or OperatorDeclarationSyntax
                    or AccessorSyntax
                    or ExpressionSyntax
                    or MatchArmSyntax
                    or GuardStatementSyntax => IsSimpleBlock(block) && WrittenOnOneLine(block),

                // 'if a { return 1 } else { return 2 }': the whole chain on
                // one line as written, each block of it one simple statement.
                IfStatementSyntax or ElseClauseSyntax => IsOneLineIf(block)
                                                         || (IsIfValueBranch(block) && IsSimpleBlock(block) && WrittenOnOneLine(block)),
                _ => false,
            };

        /// <summary>A branch of an 'if' that gives a value, which reads as a conditional does: 'if c { a } else { b }'.</summary>
        static bool IsIfValueBranch(BlockSyntax block) {
            for (var node = block.Parent; node is IfStatementSyntax or ElseClauseSyntax; node = node.Parent) {
                if (node.Parent is IfExpressionSyntax) {
                    return true;
                }
            }

            return false;
        }

        static bool IsSimpleBlock(BlockSyntax block) => block.Statements.Count == 1 && IsSimple(block.Statements[0]);

        /// <summary>A statement with no declaration and no braces of its own.</summary>
        static bool IsSimple(StatementSyntax statement) =>
            statement is not DeclarationStatementSyntax
            && !statement.DescendantNodes().Any(static n => n is BlockSyntax or BraceClosureExpressionSyntax or MatchExpressionSyntax);

        static bool IsOneLineIf(BlockSyntax block) {
            SyntaxNode top = block.Parent!;
            while (top.Parent is IfStatementSyntax or ElseClauseSyntax) {
                top = top.Parent;
            }

            if (top is not IfStatementSyntax chain || !WrittenOnOneLine(chain, from: SyntaxKind.IfKeyword)) {
                return false;
            }

            for (StatementSyntax? link = chain; link is not null;) {
                switch (link) {
                    case IfStatementSyntax conditional when IsSimpleBlock(conditional.Body):
                        link = conditional.ElseClause?.Body;
                        break;
                    case BlockSyntax last when IsSimpleBlock(last):
                        link = null;
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        static bool IsTypeDeclaration(SyntaxNode node) =>
            node is StructDeclarationSyntax
                or ClassDeclarationSyntax
                or ActorDeclarationSyntax
                or ProtocolDeclarationSyntax
                or UnionDeclarationSyntax
                or EnumDeclarationSyntax
                or ExtensionDeclarationSyntax;

        /// <summary>
        ///     A type's body its author wrote on one line, of members with no
        ///     body of their own: <c>struct P { var x: Int }</c>,
        ///     <c>enum Dir { north, south }</c>, <c>protocol Shape { fn area() -&gt; F64 }</c>.
        ///     It stays on one line, as gofmt keeps a short struct type.
        /// </summary>
        static bool IsOneLineTypeBody(SyntaxNode declaration) {
            var inside = false;
            var members = 0;
            foreach (var child in declaration.ChildNodesAndTokens()) {
                if (child.IsToken) {
                    var token = child.AsToken();
                    if (token.Kind == SyntaxKind.OpenBraceToken) {
                        inside = true;
                    } else if (token.Kind == SyntaxKind.CloseBraceToken) {
                        break;
                    }

                    continue;
                }

                var member = child.AsNode()!;
                if (!inside) {
                    continue;
                }

                members++;
                if (member.DescendantNodesAndSelf().Any(static n =>
                        (n is BlockSyntax block && !IsOneLineBody(block)) || n is BraceClosureExpressionSyntax or MatchExpressionSyntax or AttributeSyntax
                        || IsTypeDeclaration(n)
                        || n is AccessorSyntax { ExpressionBody: not null })) {
                    return false;
                }
            }

            // From its '{': what comes before — attributes, a doc comment, a
            // long header — is the declaration's, not its body's.
            return members > 0 && WrittenOnOneLine(declaration, from: SyntaxKind.OpenBraceToken);
        }

        /// <summary>
        ///     No line break or comment anywhere inside the node, up to its
        ///     last token; from its first token of kind <paramref name="from" />
        ///     when one is given, and ignoring what comes before that token.
        /// </summary>
        static bool WrittenOnOneLine(SyntaxNode node, SyntaxKind from = SyntaxKind.None) {
            var last = node.GetLastToken();
            var started = from == SyntaxKind.None;
            foreach (var token in node.DescendantTokens()) {
                if (!started) {
                    if (token.Kind != from) {
                        continue;
                    }

                    started = true;
                    if (token.TrailingTrivia.Any(static t => t.Kind == SyntaxKind.NewLineTrivia || t.IsComment)) {
                        return false;
                    }

                    continue;
                }

                if (token.IsMissing) {
                    return false;
                }

                if (token.LeadingTrivia.Any(static t => t.Kind == SyntaxKind.NewLineTrivia || t.IsComment || t.Kind is SyntaxKind.DirectiveTrivia or SyntaxKind.DisabledTextTrivia)) {
                    return false;
                }

                if (token != last && token.TrailingTrivia.Any(static t => t.Kind == SyntaxKind.NewLineTrivia || t.IsComment)) {
                    return false;
                }
            }

            return true;
        }

        bool NeedsSpace(SyntaxKind kind, SyntaxKind parentKind) {
            if (_previousKind == SyntaxKind.None) {
                return false;
            }

            // A modifier's parenthesised argument: 'private(set)'.
            if (kind == SyntaxKind.OpenParenToken && IsModifierKeyword(_previousKind)) {
                return false;
            }

            // An operator's name stands apart from its parameters: 'operator == (lhs: T, rhs: T)'.
            if (kind == SyntaxKind.OpenParenToken && _previousParent == SyntaxKind.OperatorDeclaration) {
                return true;
            }

            if (NoSpaceBefore(kind, parentKind)) {
                return false;
            }

            if (NoSpaceAfter(_previousKind, _previousParent)) {
                return false;
            }

            return true;
        }

        static bool IsModifierKeyword(SyntaxKind kind) =>
            kind switch {
                SyntaxKind.PublicKeyword
                    or SyntaxKind.PackageKeyword
                    or SyntaxKind.InternalKeyword
                    or SyntaxKind.PrivateKeyword => true,
                SyntaxKind.WeakKeyword or SyntaxKind.UnownedKeyword => true,
                _ => false,
            };

        static bool NoSpaceBefore(SyntaxKind kind, SyntaxKind parentKind) =>
            kind switch {
                // Nothing inside an interpolated string takes added whitespace.
                SyntaxKind.InterpolatedTextToken
                    or SyntaxKind.InterpolationStartToken
                    or SyntaxKind.InterpolationEndToken
                    or SyntaxKind.InterpolatedStringEndToken => true,

                SyntaxKind.CommaToken or SyntaxKind.SemicolonToken => true,
                SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken => true,
                SyntaxKind.DotToken => parentKind is not
                    (SyntaxKind.ImplicitMemberAccessExpression or SyntaxKind.UnionCasePattern),
                SyntaxKind.QuestionDotToken => true,

                SyntaxKind.ColonToken => parentKind != SyntaxKind.ConditionalExpression,

                SyntaxKind.QuestionToken => parentKind is SyntaxKind.OptionalType
                    or SyntaxKind.OptionalPattern
                    or SyntaxKind.ReferenceType
                    or SyntaxKind.CastExpression
                    or SyntaxKind.TryExpression
                    or SyntaxKind.InitDeclaration
                    or SyntaxKind.InvocationExpression
                    or SyntaxKind.ElementAccessExpression,

                SyntaxKind.BangToken => parentKind is SyntaxKind.PostfixUnaryExpression
                    or SyntaxKind.ImplicitlyUnwrappedType
                    or SyntaxKind.CastExpression
                    or SyntaxKind.TryExpression,

                SyntaxKind.OpenParenToken => parentKind is SyntaxKind.ArgumentList
                    or SyntaxKind.ParameterList
                    or SyntaxKind.PatternArgumentList
                    or SyntaxKind.UnionCasePayloadList
                    or SyntaxKind.ThrowsSpec
                    or SyntaxKind.Attribute
                    or SyntaxKind.Accessor,

                SyntaxKind.OpenBracketToken => parentKind is SyntaxKind.ElementAccessExpression
                    or SyntaxKind.ArgumentList
                    or SyntaxKind.FixedSizeArrayType
                    or SyntaxKind.InlineArrayType
                    or SyntaxKind.GrowArrayType
                    or SyntaxKind.SliceType,

                SyntaxKind.LessToken or SyntaxKind.GreaterToken =>
                    parentKind is SyntaxKind.TypeArgumentList
                        or SyntaxKind.GenericParameterList
                        or SyntaxKind.RelativeReferenceSpec
                        or SyntaxKind.GrowArrayType,

                SyntaxKind.DotDotToken
                    or SyntaxKind.DotDotLessToken
                    or SyntaxKind.DotDotDotToken => true,

                // '&x' as an argument follows '(' with no space, as anything
                // does, but a comma or a label's colon with one: 'f(a, &b)'.
                SyntaxKind.AmpersandToken => parentKind is SyntaxKind.ReferenceType
                    or SyntaxKind.PrefixUnaryExpression,

                _ => false,
            };

        static bool NoSpaceAfter(SyntaxKind kind, SyntaxKind parentKind) =>
            kind switch {
                SyntaxKind.InterpolatedStringStartToken
                    or SyntaxKind.InterpolatedTextToken
                    or SyntaxKind.InterpolationStartToken
                    or SyntaxKind.InterpolationEndToken => true,

                SyntaxKind.DotToken or SyntaxKind.QuestionDotToken => true,
                SyntaxKind.AtToken or SyntaxKind.HashToken or SyntaxKind.DollarToken => true,

                SyntaxKind.OpenParenToken => parentKind is not SyntaxKind.ConditionalExpression,
                SyntaxKind.OpenBracketToken => true,

                SyntaxKind.LessToken => parentKind is SyntaxKind.TypeArgumentList
                    or SyntaxKind.GenericParameterList
                    or SyntaxKind.RelativeReferenceSpec,

                SyntaxKind.DotDotToken => parentKind != SyntaxKind.UnionDeclaration,
                SyntaxKind.DotDotLessToken => true,
                SyntaxKind.DotDotDotToken => parentKind is SyntaxKind.RangeExpression
                    or SyntaxKind.RangePattern,

                SyntaxKind.AmpersandToken
                    or SyntaxKind.MinusToken
                    or SyntaxKind.PlusToken
                    or SyntaxKind.BangToken
                    or SyntaxKind.TildeToken =>
                    parentKind is SyntaxKind.PrefixUnaryExpression or SyntaxKind.Argument or SyntaxKind.LiteralPattern,

                // 'f()?.x' and 'xs[0]?.y'; but 'try? f()' and 'x as? T' take a space.
                SyntaxKind.QuestionToken => parentKind is SyntaxKind.InvocationExpression
                    or SyntaxKind.ElementAccessExpression,

                SyntaxKind.GreaterToken => parentKind is SyntaxKind.GrowArrayType,

                _ => false,
            };

        static bool IsMemberKind(SyntaxKind kind) =>
            kind switch {
                SyntaxKind.FunctionDeclaration
                    or SyntaxKind.FieldDeclaration
                    or SyntaxKind.ConstDeclaration
                    or SyntaxKind.ComptimeDeclaration
                    or SyntaxKind.InitDeclaration
                    or SyntaxKind.DeinitDeclaration
                    or SyntaxKind.SubscriptDeclaration
                    or SyntaxKind.OperatorDeclaration => true,
                SyntaxKind.StructDeclaration
                    or SyntaxKind.ClassDeclaration
                    or SyntaxKind.ActorDeclaration
                    or SyntaxKind.ProtocolDeclaration
                    or SyntaxKind.UnionDeclaration
                    or SyntaxKind.EnumDeclaration
                    or SyntaxKind.ExtensionDeclaration => true,
                SyntaxKind.AliasDeclaration
                    or SyntaxKind.AssociatedTypeDeclaration => true,
                SyntaxKind.UnionCaseDeclaration or SyntaxKind.EnumCaseDeclaration => true,
                _ => false,
            };
    }
}
