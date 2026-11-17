using System.Text;

namespace Sable.Syntax;

/// <summary>
/// Renders a syntax tree as canonically formatted source.
/// </summary>
/// <remarks>
/// <para>There are no options, by design: one style, settled once. The
/// formatter walks the tree rather than the token stream, because almost every
/// hard spacing question — is this <c>&lt;</c> a generic bracket or a
/// comparison, is this <c>-</c> unary or binary, is this <c>:</c> a type
/// annotation or the middle of a conditional — is answered for free by the
/// node a token belongs to.</para>
/// <para>Two properties are tested and must hold: formatting is idempotent,
/// and formatting never changes the tree. Comments and the author's blank
/// lines survive; everything else about the whitespace is decided here.</para>
/// </remarks>
public static class SyntaxFormatter
{
    private const string IndentUnit = "    ";

    public static string Format(SyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var writer = new Writer();
        writer.WriteNode(tree.Root, tree.Root.Kind);

        // A compilation unit owns its end-of-file token, so writing it again
        // here would emit its trivia twice.
        if (tree.Root is not CompilationUnitSyntax && !tree.EndOfFileToken.IsNone)
            writer.WriteToken(tree.EndOfFileToken, null);

        return writer.Finish();
    }

    /// <summary>Formats source text directly.</summary>
    public static string Format(string text, ParseOptions? options = null) =>
        Format(SyntaxTree.Parse(new SourceText(text), options ?? ParseOptions.Default));

    private sealed class Writer
    {
        private readonly StringBuilder _builder = new();
        private int _indent;
        private int _pendingLines;
        private bool _pendingSpace;
        private bool _started;

        private SyntaxKind _previousKind = SyntaxKind.None;
        private SyntaxKind _previousParent = SyntaxKind.None;

        /// <summary>
        /// True when the previous token's trailing trivia ended its line.
        /// Trailing trivia stops at the first line break, so a blank line
        /// between two declarations is one newline on each side.
        /// </summary>
        private bool _previousEndedLine;

        public string Finish()
        {
            // Exactly one trailing newline.
            while (_builder.Length > 0 && _builder[^1] is ' ' or '\n') _builder.Length--;
            if (_builder.Length > 0) _builder.Append('\n');
            return _builder.ToString();
        }

        public void WriteNode(SyntaxNode node, SyntaxKind parentKind)
        {
            var kind = node.Kind;

            if (StartsOnItsOwnLine(node, parentKind) && !InsideInlineBrace)
                BreakLine(BlankLinesBefore(node));

            foreach (var child in node.ChildNodesAndTokens())
            {
                if (child.IsToken) WriteToken(child.AsToken(), node);
                else WriteNode(child.AsNode()!, kind);
            }

            if (EndsItsLine(node, parentKind)) BreakLine(0);
        }

        public void WriteToken(SyntaxToken token, SyntaxNode? parent)
        {
            var parentKind = parent?.Kind ?? SyntaxKind.None;

            if (token.IsMissing)
            {
                WriteTrivia(token.LeadingTrivia, leading: true);
                return;
            }

            WriteTrivia(token.LeadingTrivia, leading: true);

            if (token.Kind == SyntaxKind.CloseBraceToken && _braces.Count > 0)
            {
                var brace = _braces.Pop();
                if (brace.OpensBody)
                {
                    Outdent();

                    // Nothing was written inside, so keep '{ }' on one line.
                    if (_builder.Length == brace.Mark)
                    {
                        _pendingLines = 0;
                        _pendingSpace = true;
                    }
                    else
                    {
                        BreakLine(0);
                    }
                }
            }

            // '} else {' and '} catch {' read better than a break between.
            if (ContinuesTheLineBefore(token.Kind, parentKind) && _pendingLines > 0)
            {
                _pendingLines = 0;
                _pendingSpace = true;
            }

            if (_started && NeedsSpace(token.Kind, parentKind)) _pendingSpace = true;

            Write(token.Text);

            _previousKind = token.Kind;
            _previousParent = parentKind;

            // Trailing trivia first, so a comment after '{' stays on its line.
            WriteTrivia(token.TrailingTrivia, leading: false);

            if (token.Kind == SyntaxKind.OpenBraceToken)
            {
                var opensBody = OpensABody(parent);
                _braces.Push(new OpenBrace(opensBody, _builder.Length));
                if (opensBody)
                {
                    Indent();
                    BreakLine(0);
                }
            }
            _previousEndedLine = token.TrailingTrivia.Any(SyntaxKind.NewLineTrivia);

            if (token.TerminatesStatement && token.Kind != SyntaxKind.EndOfFile
                && token.Kind != SyntaxKind.OpenBraceToken)
            {
                BreakLine(0);
            }
        }

        // -------------------------------------------------------------------
        // Trivia
        // -------------------------------------------------------------------

        private void WriteTrivia(SyntaxTriviaList trivia, bool leading)
        {
            if (trivia.Count == 0) return;

            var sawNewLine = false;
            var blankLines = 0;

            foreach (var item in trivia)
            {
                switch (item.Kind)
                {
                    case SyntaxKind.NewLineTrivia:
                        if (sawNewLine) blankLines++;
                        sawNewLine = true;
                        continue;

                    case SyntaxKind.WhitespaceTrivia:
                        continue;

                    case SyntaxKind.LineCommentTrivia:
                    case SyntaxKind.DocCommentTrivia:
                    case SyntaxKind.ModuleDocCommentTrivia:
                    case SyntaxKind.ShebangTrivia:
                    case SyntaxKind.DirectiveTrivia:
                        if (leading && (sawNewLine || !_started))
                        {
                            BreakLine(Math.Min(blankLines, 1));
                        }
                        else
                        {
                            _pendingSpace = true;
                        }

                        Write(item.Text.TrimEnd());
                        BreakLine(0);

                        // The newline that ends this comment is its own, not a
                        // blank line before whatever follows.
                        sawNewLine = false;
                        blankLines = 0;
                        continue;

                    case SyntaxKind.BlockCommentTrivia:
                        if (leading && sawNewLine) BreakLine(Math.Min(blankLines, 1));
                        else _pendingSpace = true;

                        Write(item.Text);
                        if (leading && sawNewLine) BreakLine(0);
                        blankLines = 0;
                        continue;

                    case SyntaxKind.SkippedTokensTrivia:
                    case SyntaxKind.DisabledTextTrivia:
                        // Text the parser could not use, and inactive regions:
                        // emitted verbatim so nothing is ever lost.
                        if (leading) BreakLine(0);
                        WriteVerbatim(item.Text);
                        continue;
                }
            }
        }

        // -------------------------------------------------------------------
        // Output primitives
        // -------------------------------------------------------------------

        private void Write(string text)
        {
            if (text.Length == 0) return;

            Flush();
            _builder.Append(text);
            _started = true;
        }

        private void WriteVerbatim(string text)
        {
            if (text.Length == 0) return;

            _pendingSpace = false;
            Flush();
            _builder.Append(text.TrimEnd('\n', '\r'));
            _started = true;
            BreakLine(0);
        }

        private void Flush()
        {
            if (_pendingLines > 0)
            {
                for (var i = 0; i < _pendingLines; i++) _builder.Append('\n');
                _pendingLines = 0;
                _pendingSpace = false;

                for (var i = 0; i < _indent; i++) _builder.Append(IndentUnit);
            }
            else if (_pendingSpace)
            {
                if (_builder.Length > 0 && _builder[^1] is not (' ' or '\n'))
                    _builder.Append(' ');
                _pendingSpace = false;
            }
        }

        private void BreakLine(int blankLines)
        {
            if (!_started) return;
            _pendingSpace = false;
            _pendingLines = Math.Max(_pendingLines, 1 + blankLines);
        }

        private void Indent() => _indent++;
        private void Outdent() => _indent = Math.Max(0, _indent - 1);

        /// <summary>
        /// An open brace: whether it introduced an indented body, and how much
        /// output existed at the time, so an empty body can stay on one line.
        /// </summary>
        private readonly record struct OpenBrace(bool OpensBody, int Mark);

        private readonly Stack<OpenBrace> _braces = new();

        /// <summary>
        /// True when the innermost open brace stayed on one line, such as a
        /// short accessor list; nothing inside it breaks.
        /// </summary>
        private bool InsideInlineBrace => _braces.Count > 0 && !_braces.Peek().OpensBody;

        /// <summary>Keywords that belong on the same line as the brace before them.</summary>
        private static bool ContinuesTheLineBefore(SyntaxKind kind, SyntaxKind parentKind) =>
            (kind == SyntaxKind.ElseKeyword && parentKind == SyntaxKind.ElseClause)
            || (kind == SyntaxKind.CatchKeyword && parentKind == SyntaxKind.CatchClause)
            || (kind == SyntaxKind.WhileKeyword && parentKind == SyntaxKind.RepeatStatement);

        // -------------------------------------------------------------------
        // Layout rules
        // -------------------------------------------------------------------

        /// <summary>Blank lines the author left before a node, capped at one.</summary>
        private int BlankLinesBefore(SyntaxNode node)
        {
            if (!IsMember(node) && node is not StatementSyntax
                && node.Kind is not (SyntaxKind.ImportDeclaration
                    or SyntaxKind.ModuleDeclaration or SyntaxKind.Accessor
                    or SyntaxKind.MatchArm or SyntaxKind.MatchCaseBlock
                    or SyntaxKind.SwitchCase))
            {
                return 0;
            }

            var first = node.GetFirstToken();
            if (first.IsNone) return 0;

            var newLines = _previousEndedLine ? 1 : 0;
            foreach (var trivia in first.LeadingTrivia)
            {
                if (trivia.Kind == SyntaxKind.NewLineTrivia) newLines++;
                else if (trivia.IsComment) break;
            }
            return newLines >= 2 ? 1 : 0;
        }

        private static bool StartsOnItsOwnLine(SyntaxNode node, SyntaxKind parentKind) =>
            (node is StatementSyntax && node.Kind != SyntaxKind.Block)
            || IsMember(node)
            || node.Kind is SyntaxKind.ImportDeclaration or SyntaxKind.ModuleDeclaration
                or SyntaxKind.Accessor or SyntaxKind.MatchArm or SyntaxKind.MatchCaseBlock
                or SyntaxKind.SwitchCase
            || (node.Kind == SyntaxKind.Attribute && IsMemberContext(parentKind));

        private static bool EndsItsLine(SyntaxNode node, SyntaxKind parentKind) =>
            IsMember(node)
            || node.Kind is SyntaxKind.ImportDeclaration or SyntaxKind.ModuleDeclaration
            || (node.Kind == SyntaxKind.Attribute && IsMemberContext(parentKind));

        /// <summary>
        /// A member is a declaration that is not the module header or an
        /// import, both of which have their own layout.
        /// </summary>
        private static bool IsMember(SyntaxNode node) =>
            node is DeclarationSyntax
            && node.Kind is not (SyntaxKind.ModuleDeclaration or SyntaxKind.ImportDeclaration);

        private static bool IsMemberContext(SyntaxKind kind) => IsMemberKind(kind);

        /// <summary>True when a brace opens an indented, line-per-item body.</summary>
        /// <remarks>
        /// A short accessor list or a one-expression closure reads better on
        /// one line: <c>var x: Int { get }</c> and <c>xs.filter { $0 &gt; 1 }</c>.
        /// Anything holding statements gets the indented form.
        /// </remarks>
        private static bool OpensABody(SyntaxNode? parent) => parent switch
        {
            BlockSyntax => true,
            StructDeclarationSyntax or ClassDeclarationSyntax or ActorDeclarationSyntax
                or ProtocolDeclarationSyntax or UnionDeclarationSyntax
                or EnumDeclarationSyntax or ExtensionDeclarationSyntax => true,
            MatchExpressionSyntax or SwitchStatementSyntax => true,

            AccessorListSyntax accessors => accessors.Accessors.Any(a => a.Body is not null),

            BraceClosureExpressionSyntax closure =>
                closure.Header is not null
                || closure.Statements.Count > 1
                || closure.Statements.Any(statement => statement is not ExpressionStatementSyntax),

            _ => false,
        };

        private bool NeedsSpace(SyntaxKind kind, SyntaxKind parentKind)
        {
            if (_previousKind == SyntaxKind.None) return false;

            // A modifier's parenthesised argument: 'private(set)'.
            if (kind == SyntaxKind.OpenParenToken && IsModifierKeyword(_previousKind))
                return false;

            if (NoSpaceBefore(kind, parentKind)) return false;
            if (NoSpaceAfter(_previousKind, _previousParent)) return false;
            return true;
        }

        private static bool IsModifierKeyword(SyntaxKind kind) => kind switch
        {
            SyntaxKind.PublicKeyword or SyntaxKind.PackageKeyword
                or SyntaxKind.InternalKeyword or SyntaxKind.PrivateKeyword => true,
            SyntaxKind.WeakKeyword or SyntaxKind.UnownedKeyword => true,
            _ => false,
        };

        private static bool NoSpaceBefore(SyntaxKind kind, SyntaxKind parentKind) => kind switch
        {
            // Nothing inside an interpolated string takes added whitespace.
            SyntaxKind.InterpolatedTextToken or SyntaxKind.InterpolationStartToken
                or SyntaxKind.InterpolationEndToken
                or SyntaxKind.InterpolatedStringEndToken => true,

            SyntaxKind.CommaToken or SyntaxKind.SemicolonToken => true,
            SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken => true,
            SyntaxKind.DotToken => parentKind is not
                (SyntaxKind.ImplicitMemberAccessExpression or SyntaxKind.UnionCasePattern),
            SyntaxKind.QuestionDotToken => true,

            SyntaxKind.ColonToken => parentKind != SyntaxKind.ConditionalExpression,

            SyntaxKind.QuestionToken => parentKind is SyntaxKind.OptionalType
                or SyntaxKind.ReferenceType or SyntaxKind.CastExpression
                or SyntaxKind.TryExpression or SyntaxKind.InitDeclaration
                or SyntaxKind.InvocationExpression or SyntaxKind.ElementAccessExpression,

            SyntaxKind.BangToken => parentKind is SyntaxKind.PostfixUnaryExpression
                or SyntaxKind.ImplicitlyUnwrappedType or SyntaxKind.CastExpression
                or SyntaxKind.TryExpression,

            SyntaxKind.OpenParenToken => parentKind is SyntaxKind.ArgumentList
                or SyntaxKind.ParameterList or SyntaxKind.PatternArgumentList
                or SyntaxKind.UnionCasePayloadList or SyntaxKind.ThrowsSpec
                or SyntaxKind.Attribute or SyntaxKind.Accessor,

            SyntaxKind.OpenBracketToken => parentKind is SyntaxKind.ElementAccessExpression
                or SyntaxKind.ArgumentList or SyntaxKind.FixedSizeArrayType
                or SyntaxKind.InlineArrayType or SyntaxKind.GrowArrayType
                or SyntaxKind.SliceType,

            SyntaxKind.LessToken or SyntaxKind.GreaterToken =>
                parentKind is SyntaxKind.TypeArgumentList or SyntaxKind.GenericParameterList
                    or SyntaxKind.RelativeReferenceSpec or SyntaxKind.GrowArrayType,

            SyntaxKind.DotDotToken or SyntaxKind.DotDotLessToken
                or SyntaxKind.DotDotDotToken => true,

            SyntaxKind.AmpersandToken => parentKind is SyntaxKind.ReferenceType
                or SyntaxKind.Argument or SyntaxKind.PrefixUnaryExpression,

            _ => false,
        };

        private static bool NoSpaceAfter(SyntaxKind kind, SyntaxKind parentKind) => kind switch
        {
            SyntaxKind.InterpolatedStringStartToken or SyntaxKind.InterpolatedTextToken
                or SyntaxKind.InterpolationStartToken
                or SyntaxKind.InterpolationEndToken => true,

            SyntaxKind.DotToken or SyntaxKind.QuestionDotToken => true,
            SyntaxKind.AtToken or SyntaxKind.HashToken or SyntaxKind.DollarToken => true,

            SyntaxKind.OpenParenToken => parentKind is not (SyntaxKind.ConditionalExpression),
            SyntaxKind.OpenBracketToken => true,

            SyntaxKind.LessToken => parentKind is SyntaxKind.TypeArgumentList
                or SyntaxKind.GenericParameterList or SyntaxKind.RelativeReferenceSpec,

            SyntaxKind.DotDotToken => parentKind != SyntaxKind.UnionDeclaration,
            SyntaxKind.DotDotLessToken => true,
            SyntaxKind.DotDotDotToken => parentKind is SyntaxKind.RangeExpression
                or SyntaxKind.RangePattern,

            SyntaxKind.AmpersandToken or SyntaxKind.MinusToken or SyntaxKind.PlusToken
                or SyntaxKind.BangToken or SyntaxKind.TildeToken =>
                parentKind is SyntaxKind.PrefixUnaryExpression or SyntaxKind.Argument,

            SyntaxKind.QuestionToken => parentKind is SyntaxKind.InvocationExpression
                or SyntaxKind.ElementAccessExpression or SyntaxKind.TryExpression
                or SyntaxKind.CastExpression,

            SyntaxKind.GreaterToken => parentKind is SyntaxKind.GrowArrayType,

            _ => false,
        };

        private static bool IsMemberKind(SyntaxKind kind) => kind switch
        {
            SyntaxKind.FunctionDeclaration or SyntaxKind.FieldDeclaration
                or SyntaxKind.ConstDeclaration or SyntaxKind.InitDeclaration
                or SyntaxKind.DeinitDeclaration or SyntaxKind.SubscriptDeclaration
                or SyntaxKind.OperatorDeclaration => true,
            SyntaxKind.StructDeclaration or SyntaxKind.ClassDeclaration
                or SyntaxKind.ActorDeclaration or SyntaxKind.ProtocolDeclaration
                or SyntaxKind.UnionDeclaration or SyntaxKind.EnumDeclaration
                or SyntaxKind.ExtensionDeclaration => true,
            SyntaxKind.AliasDeclaration or SyntaxKind.TypealiasDeclaration
                or SyntaxKind.AssociatedTypeDeclaration => true,
            SyntaxKind.UnionCaseDeclaration or SyntaxKind.EnumCaseDeclaration => true,
            _ => false,
        };
    }
}
