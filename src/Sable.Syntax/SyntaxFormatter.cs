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

            foreach (var child in node.ChildNodesAndTokens()) {
                if (child.IsToken) {
                    WriteToken(child.AsToken(), node);
                } else {
                    WriteNode(child.AsNode()!, kind);
                }
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

            if (EndsItsLine(node, parentKind)) {
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

            _previousKind = token.Kind;
            _previousParent = parentKind;

            // Trailing trivia first, so a comment after '{' stays on its line.
            // The mark is taken before it: a body holding only a comment is
            // not empty, and its '}' must not land after a line comment.
            var mark = _builder.Length;
            WriteTrivia(token.TrailingTrivia, false);

            if (token.Kind == SyntaxKind.OpenBraceToken) {
                var opensBody = OpensABody(parent);
                _braces.Push(new(opensBody, mark));
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

            if (token.TerminatesStatement
                && token.Kind != SyntaxKind.EndOfFile
                && token.Kind != SyntaxKind.OpenBraceToken) {
                BreakLine(0);
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
        private readonly record struct OpenBrace(bool OpensBody, int Mark);

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
            (node is StatementSyntax && node.Kind != SyntaxKind.Block && parentKind is not (SyntaxKind.ElseClause or SyntaxKind.LabeledStatement))
            || IsMember(node)
            || node.Kind is SyntaxKind.ImportDeclaration
                or SyntaxKind.ModuleDeclaration
                or SyntaxKind.Accessor
                or SyntaxKind.MatchArm
                or SyntaxKind.MatchCaseBlock
                or SyntaxKind.SwitchCase
            || (node.Kind == SyntaxKind.Attribute && IsMemberContext(parentKind));

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
                StructDeclarationSyntax
                    or ClassDeclarationSyntax
                    or ActorDeclarationSyntax
                    or ProtocolDeclarationSyntax
                    or UnionDeclarationSyntax
                    or EnumDeclarationSyntax
                    or ExtensionDeclarationSyntax => true,
                MatchExpressionSyntax or SwitchStatementSyntax => true,

                AccessorListSyntax accessors => accessors.Accessors.Any(static a => a.Body is not null || a.ExpressionBody is not null)
                    && !(WrittenOnOneLine(accessors) && accessors.Accessors.All(static a => a.Body is null || IsOneLineBody(a.Body))),

                BraceClosureExpressionSyntax closure =>
                    closure.Header is not null
                    || closure.Statements.Count > 1
                    || closure.Statements.Any(static statement => statement is not ExpressionStatementSyntax),

                _ => false,
            };

        /// <summary>
        ///     A function's, initialiser's, operator's or accessor's body, or a
        ///     block inside an expression or a match arm, or a guard's exit, of
        ///     one statement with no block of its own, written on one line:
        ///     <c>guard let x = y else { return none }</c>. Other statements'
        ///     blocks — 'if', 'for', 'while' — always take their own lines.
        /// </summary>
        static bool IsOneLineBody(BlockSyntax block) =>
            block.Parent is FunctionDeclarationSyntax
                or InitDeclarationSyntax
                or DeinitDeclarationSyntax
                or OperatorDeclarationSyntax
                or AccessorSyntax
                or ExpressionSyntax
                or MatchArmSyntax
                or GuardStatementSyntax
            && block.Statements.Count == 1
            && block.Statements[0] is not DeclarationStatementSyntax
            && !block.Statements[0].DescendantNodes().Any(static n => n is BlockSyntax or BraceClosureExpressionSyntax or MatchExpressionSyntax)
            && WrittenOnOneLine(block);

        /// <summary>No line break or comment anywhere inside the node, up to its last token.</summary>
        static bool WrittenOnOneLine(SyntaxNode node) {
            var last = node.GetLastToken();
            foreach (var token in node.DescendantTokens()) {
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

                SyntaxKind.AmpersandToken => parentKind is SyntaxKind.ReferenceType
                    or SyntaxKind.Argument
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
                    or SyntaxKind.TypealiasDeclaration
                    or SyntaxKind.AssociatedTypeDeclaration => true,
                SyntaxKind.UnionCaseDeclaration or SyntaxKind.EnumCaseDeclaration => true,
                _ => false,
            };
    }
}
