using System.Collections.Frozen;

namespace Sable.Syntax;

/// <summary>
/// Static facts about tokens: the keyword table, canonical punctuation text,
/// and the predicates that implement the statement-terminator rule of
/// spec/lexical.md §4.
/// </summary>
public static class SyntaxFacts
{
    private static readonly FrozenDictionary<string, SyntaxKind> Keywords =
        new Dictionary<string, SyntaxKind>(StringComparer.Ordinal)
        {
            // Reserved
            ["abstract"] = SyntaxKind.AbstractKeyword,
            ["actor"] = SyntaxKind.ActorKeyword,
            ["alias"] = SyntaxKind.AliasKeyword,
            ["any"] = SyntaxKind.AnyKeyword,
            ["as"] = SyntaxKind.AsKeyword,
            ["assert"] = SyntaxKind.AssertKeyword,
            ["async"] = SyntaxKind.AsyncKeyword,
            ["await"] = SyntaxKind.AwaitKeyword,
            ["borrowing"] = SyntaxKind.BorrowingKeyword,
            ["break"] = SyntaxKind.BreakKeyword,
            ["case"] = SyntaxKind.CaseKeyword,
            ["catch"] = SyntaxKind.CatchKeyword,
            ["class"] = SyntaxKind.ClassKeyword,
            ["const"] = SyntaxKind.ConstKeyword,
            ["consuming"] = SyntaxKind.ConsumingKeyword,
            ["continue"] = SyntaxKind.ContinueKeyword,
            ["default"] = SyntaxKind.DefaultKeyword,
            ["defer"] = SyntaxKind.DeferKeyword,
            ["deinit"] = SyntaxKind.DeinitKeyword,
            ["do"] = SyntaxKind.DoKeyword,
            ["else"] = SyntaxKind.ElseKeyword,
            ["enum"] = SyntaxKind.EnumKeyword,
            ["extension"] = SyntaxKind.ExtensionKeyword,
            ["fallthrough"] = SyntaxKind.FallthroughKeyword,
            ["false"] = SyntaxKind.FalseKeyword,
            ["final"] = SyntaxKind.FinalKeyword,
            ["fn"] = SyntaxKind.FnKeyword,
            ["for"] = SyntaxKind.ForKeyword,
            ["guard"] = SyntaxKind.GuardKeyword,
            ["if"] = SyntaxKind.IfKeyword,
            ["import"] = SyntaxKind.ImportKeyword,
            ["in"] = SyntaxKind.InKeyword,
            ["init"] = SyntaxKind.InitKeyword,
            ["inout"] = SyntaxKind.InoutKeyword,
            ["internal"] = SyntaxKind.InternalKeyword,
            ["is"] = SyntaxKind.IsKeyword,
            ["lazy"] = SyntaxKind.LazyKeyword,
            ["let"] = SyntaxKind.LetKeyword,
            ["match"] = SyntaxKind.MatchKeyword,
            ["module"] = SyntaxKind.ModuleKeyword,
            ["move"] = SyntaxKind.MoveKeyword,
            ["mutating"] = SyntaxKind.MutatingKeyword,
            ["none"] = SyntaxKind.NoneKeyword,
            ["nonisolated"] = SyntaxKind.NonisolatedKeyword,
            ["nonmutating"] = SyntaxKind.NonmutatingKeyword,
            ["open"] = SyntaxKind.OpenKeyword,
            ["operator"] = SyntaxKind.OperatorKeyword,
            ["out"] = SyntaxKind.OutKeyword,
            ["override"] = SyntaxKind.OverrideKeyword,
            ["package"] = SyntaxKind.PackageKeyword,
            ["postfix"] = SyntaxKind.PostfixKeyword,
            ["precondition"] = SyntaxKind.PreconditionKeyword,
            ["prefix"] = SyntaxKind.PrefixKeyword,
            ["private"] = SyntaxKind.PrivateKeyword,
            ["protocol"] = SyntaxKind.ProtocolKeyword,
            ["public"] = SyntaxKind.PublicKeyword,
            ["region"] = SyntaxKind.RegionKeyword,
            ["repeat"] = SyntaxKind.RepeatKeyword,
            ["required"] = SyntaxKind.RequiredKeyword,
            ["rethrows"] = SyntaxKind.RethrowsKeyword,
            ["return"] = SyntaxKind.ReturnKeyword,
            ["sealed"] = SyntaxKind.SealedKeyword,
            ["self"] = SyntaxKind.SelfKeyword,
            ["Self"] = SyntaxKind.CapitalSelfKeyword,
            ["some"] = SyntaxKind.SomeKeyword,
            ["static"] = SyntaxKind.StaticKeyword,
            ["struct"] = SyntaxKind.StructKeyword,
            ["subscript"] = SyntaxKind.SubscriptKeyword,
            ["super"] = SyntaxKind.SuperKeyword,
            ["switch"] = SyntaxKind.SwitchKeyword,
            ["throw"] = SyntaxKind.ThrowKeyword,
            ["throws"] = SyntaxKind.ThrowsKeyword,
            ["true"] = SyntaxKind.TrueKeyword,
            ["try"] = SyntaxKind.TryKeyword,
            ["typealias"] = SyntaxKind.TypealiasKeyword,
            ["union"] = SyntaxKind.UnionKeyword,
            ["unowned"] = SyntaxKind.UnownedKeyword,
            ["unsafe"] = SyntaxKind.UnsafeKeyword,
            ["using"] = SyntaxKind.UsingKeyword,
            ["var"] = SyntaxKind.VarKeyword,
            ["weak"] = SyntaxKind.WeakKeyword,
            ["where"] = SyntaxKind.WhereKeyword,
            ["while"] = SyntaxKind.WhileKeyword,

            // Contextual
            ["associatedtype"] = SyntaxKind.AssociatedtypeKeyword,
            ["comptime"] = SyntaxKind.ComptimeKeyword,
            ["convenience"] = SyntaxKind.ConvenienceKeyword,
            ["didSet"] = SyntaxKind.DidSetKeyword,
            ["get"] = SyntaxKind.GetKeyword,
            ["newValue"] = SyntaxKind.NewValueKeyword,
            ["oldValue"] = SyntaxKind.OldValueKeyword,
            ["set"] = SyntaxKind.SetKeyword,
            ["unchecked"] = SyntaxKind.UncheckedKeyword,
            ["willSet"] = SyntaxKind.WillSetKeyword,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> FutureReserved =
        new[] { "macro", "yield", "trait", "impl", "become", "effect", "handler" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Maps an identifier spelling to its keyword kind, or Identifier.</summary>
    public static SyntaxKind KeywordKind(string text) =>
        Keywords.TryGetValue(text, out var kind) ? kind : SyntaxKind.IdentifierToken;

    public static bool IsFutureReserved(string text) => FutureReserved.Contains(text);

    public static bool IsKeyword(SyntaxKind kind) =>
        IsReservedKeyword(kind) || IsContextualKeyword(kind);

    public static bool IsReservedKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.AbstractKeyword && kind <= SyntaxKind.WhileKeyword;

    public static bool IsContextualKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.AssociatedtypeKeyword && kind <= SyntaxKind.UncheckedKeyword;

    public static bool IsLiteral(SyntaxKind kind) =>
        kind is SyntaxKind.IntegerLiteralToken
            or SyntaxKind.FloatLiteralToken
            or SyntaxKind.CharLiteralToken
            or SyntaxKind.StringLiteralToken
            or SyntaxKind.MultilineStringLiteralToken
            or SyntaxKind.RawStringLiteralToken
            or SyntaxKind.TrueKeyword
            or SyntaxKind.FalseKeyword
            or SyntaxKind.NoneKeyword;

    public static bool IsTrivia(SyntaxKind kind) =>
        kind >= SyntaxKind.WhitespaceTrivia && kind <= SyntaxKind.DisabledTextTrivia;

    // -----------------------------------------------------------------------
    // Statement-terminator rule — spec/lexical.md §4
    // -----------------------------------------------------------------------

    /// <summary>
    /// True when a statement may legally end with this token, which is the
    /// precondition for inserting a terminator at the following newline.
    /// </summary>
    /// <remarks>
    /// Deliberate inclusions with known trade-offs, documented in
    /// spec/lexical.md §4: <c>&gt;</c> (so <c>var x: List&lt;Int&gt;</c> ends a
    /// line) and <c>?</c> (so <c>var x: Int?</c> does). A multi-line expression
    /// that would end a line on either must be parenthesised.
    /// </remarks>
    public static bool CanEndStatement(SyntaxKind kind) => kind switch
    {
        SyntaxKind.IdentifierToken => true,
        SyntaxKind.IntegerLiteralToken => true,
        SyntaxKind.FloatLiteralToken => true,
        SyntaxKind.CharLiteralToken => true,
        SyntaxKind.StringLiteralToken => true,
        SyntaxKind.MultilineStringLiteralToken => true,
        SyntaxKind.RawStringLiteralToken => true,

        SyntaxKind.CloseParenToken => true,
        SyntaxKind.CloseBracketToken => true,
        SyntaxKind.CloseBraceToken => true,

        SyntaxKind.QuestionToken => true,
        SyntaxKind.BangToken => true,
        SyntaxKind.GreaterToken => true,
        SyntaxKind.DotDotDotToken => true,

        SyntaxKind.SelfKeyword => true,
        SyntaxKind.CapitalSelfKeyword => true,
        SyntaxKind.SuperKeyword => true,
        SyntaxKind.TrueKeyword => true,
        SyntaxKind.FalseKeyword => true,
        SyntaxKind.NoneKeyword => true,
        SyntaxKind.GetKeyword => true,
        SyntaxKind.SetKeyword => true,

        // Jump keywords terminate even when bare; see AlwaysTerminates.
        SyntaxKind.ReturnKeyword => true,
        SyntaxKind.BreakKeyword => true,
        SyntaxKind.ContinueKeyword => true,
        SyntaxKind.ThrowKeyword => true,
        SyntaxKind.FallthroughKeyword => true,

        _ => false,
    };

    /// <summary>
    /// Jump keywords terminate at a newline unconditionally, so that a bare
    /// <c>return</c> can never silently swallow the next line. This is the
    /// guard against JavaScript's automatic-semicolon-insertion hazard.
    /// </summary>
    public static bool AlwaysTerminates(SyntaxKind kind) => kind switch
    {
        SyntaxKind.ReturnKeyword => true,
        SyntaxKind.BreakKeyword => true,
        SyntaxKind.ContinueKeyword => true,
        SyntaxKind.ThrowKeyword => true,
        SyntaxKind.FallthroughKeyword => true,
        _ => false,
    };

    /// <summary>
    /// True when a token appearing first on a line continues the previous
    /// statement rather than starting a new one.
    /// </summary>
    /// <remarks>
    /// <c>{</c> is deliberately absent: an opening brace must appear on the
    /// same line as its header, because allowing it to start a line would be
    /// ambiguous with a trailing closure.
    /// </remarks>
    public static bool ContinuesStatement(SyntaxKind kind) => kind switch
    {
        SyntaxKind.DotToken => true,
        SyntaxKind.QuestionDotToken => true,
        SyntaxKind.ColonToken => true,
        SyntaxKind.FatArrowToken => true,
        SyntaxKind.ArrowToken => true,
        SyntaxKind.CommaToken => true,
        SyntaxKind.CloseParenToken => true,
        SyntaxKind.CloseBracketToken => true,

        SyntaxKind.ElseKeyword => true,
        SyntaxKind.CatchKeyword => true,
        SyntaxKind.WhileKeyword => true,
        SyntaxKind.WhereKeyword => true,

        _ => IsBinaryOperator(kind),
    };

    public static bool IsBinaryOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken
            or SyntaxKind.SlashToken or SyntaxKind.PercentToken => true,
        SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.CaretToken
            or SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken => true,
        SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken
            or SyntaxKind.QuestionQuestionToken => true,
        SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken
            or SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.BangEqualsEqualsToken => true,
        SyntaxKind.LessToken or SyntaxKind.LessEqualsToken
            or SyntaxKind.GreaterEqualsToken => true,
        SyntaxKind.DotDotToken or SyntaxKind.DotDotLessToken
            or SyntaxKind.DotDotDotToken => true,
        SyntaxKind.AsKeyword or SyntaxKind.IsKeyword => true,
        _ => IsAssignmentOperator(kind),
    };

    public static bool IsAssignmentOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.EqualsToken or SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken
            or SyntaxKind.StarEqualsToken or SyntaxKind.SlashEqualsToken
            or SyntaxKind.PercentEqualsToken or SyntaxKind.AmpersandEqualsToken
            or SyntaxKind.PipeEqualsToken or SyntaxKind.CaretEqualsToken
            or SyntaxKind.LessLessEqualsToken or SyntaxKind.GreaterGreaterEqualsToken
            or SyntaxKind.QuestionQuestionEqualsToken => true,
        _ => false,
    };

    /// <summary>The canonical source text of a fixed-text token, or null.</summary>
    public static string? GetText(SyntaxKind kind) => kind switch
    {
        SyntaxKind.OpenParenToken => "(",
        SyntaxKind.CloseParenToken => ")",
        SyntaxKind.OpenBracketToken => "[",
        SyntaxKind.CloseBracketToken => "]",
        SyntaxKind.OpenBraceToken => "{",
        SyntaxKind.CloseBraceToken => "}",
        SyntaxKind.CommaToken => ",",
        SyntaxKind.SemicolonToken => ";",
        SyntaxKind.ColonToken => ":",
        SyntaxKind.DotToken => ".",
        SyntaxKind.QuestionToken => "?",
        SyntaxKind.QuestionDotToken => "?.",
        SyntaxKind.DotDotToken => "..",
        SyntaxKind.DotDotLessToken => "..<",
        SyntaxKind.DotDotDotToken => "...",
        SyntaxKind.ArrowToken => "->",
        SyntaxKind.FatArrowToken => "=>",
        SyntaxKind.AtToken => "@",
        SyntaxKind.HashToken => "#",
        SyntaxKind.UnderscoreToken => "_",
        SyntaxKind.BackslashToken => "\\",
        SyntaxKind.DollarToken => "$",
        SyntaxKind.PlusToken => "+",
        SyntaxKind.MinusToken => "-",
        SyntaxKind.StarToken => "*",
        SyntaxKind.SlashToken => "/",
        SyntaxKind.PercentToken => "%",
        SyntaxKind.AmpersandToken => "&",
        SyntaxKind.PipeToken => "|",
        SyntaxKind.CaretToken => "^",
        SyntaxKind.TildeToken => "~",
        SyntaxKind.BangToken => "!",
        SyntaxKind.LessLessToken => "<<",
        SyntaxKind.GreaterGreaterToken => ">>",
        SyntaxKind.AmpersandAmpersandToken => "&&",
        SyntaxKind.PipePipeToken => "||",
        SyntaxKind.QuestionQuestionToken => "??",
        SyntaxKind.EqualsToken => "=",
        SyntaxKind.EqualsEqualsToken => "==",
        SyntaxKind.BangEqualsToken => "!=",
        SyntaxKind.EqualsEqualsEqualsToken => "===",
        SyntaxKind.BangEqualsEqualsToken => "!==",
        SyntaxKind.LessToken => "<",
        SyntaxKind.LessEqualsToken => "<=",
        SyntaxKind.GreaterToken => ">",
        SyntaxKind.GreaterEqualsToken => ">=",
        SyntaxKind.PlusEqualsToken => "+=",
        SyntaxKind.MinusEqualsToken => "-=",
        SyntaxKind.StarEqualsToken => "*=",
        SyntaxKind.SlashEqualsToken => "/=",
        SyntaxKind.PercentEqualsToken => "%=",
        SyntaxKind.AmpersandEqualsToken => "&=",
        SyntaxKind.PipeEqualsToken => "|=",
        SyntaxKind.CaretEqualsToken => "^=",
        SyntaxKind.LessLessEqualsToken => "<<=",
        SyntaxKind.GreaterGreaterEqualsToken => ">>=",
        SyntaxKind.QuestionQuestionEqualsToken => "??=",
        _ => IsKeyword(kind) ? KeywordText(kind) : null,
    };

    private static string? KeywordText(SyntaxKind kind)
    {
        foreach (var (text, k) in Keywords)
            if (k == kind) return text;
        return null;
    }

    /// <summary>XID_Start, approximated by Unicode letter categories plus '_'.</summary>
    public static bool IsIdentifierStart(char c) =>
        c == '_' || char.IsLetter(c);

    /// <summary>XID_Continue, approximated by letter/digit categories plus '_'.</summary>
    public static bool IsIdentifierContinue(char c) =>
        c == '_' || char.IsLetterOrDigit(c);
}
