namespace Sable.Syntax;

/// <summary>
/// Every lexical and syntactic element kind. Values are grouped into stable
/// ranges so that <see cref="SyntaxFacts"/> can answer category questions with
/// a range check rather than a switch.
/// </summary>
/// <remarks>
/// Ranges:
/// <list type="bullet">
///   <item>0000-0099  control and trivia</item>
///   <item>0100-0199  literals and identifiers</item>
///   <item>0200-0399  punctuation and operators</item>
///   <item>0400-0599  reserved keywords</item>
///   <item>0600-0699  contextual keywords</item>
///   <item>0700-0799  directives</item>
///   <item>1000+      syntax nodes (added in the parser milestone)</item>
/// </list>
/// </remarks>
public enum SyntaxKind : ushort
{
    None = 0,
    EndOfFile = 1,
    BadToken = 2,

    // ---- Trivia -----------------------------------------------------------
    WhitespaceTrivia = 10,
    NewLineTrivia = 11,
    LineCommentTrivia = 12,
    BlockCommentTrivia = 13,
    DocCommentTrivia = 14,          // /// and /** */
    ModuleDocCommentTrivia = 15,    // //!
    ShebangTrivia = 16,
    DisabledTextTrivia = 17,

    // ---- Literals and identifiers ----------------------------------------
    IdentifierToken = 100,
    IntegerLiteralToken = 101,
    FloatLiteralToken = 102,
    CharLiteralToken = 103,
    StringLiteralToken = 104,
    MultilineStringLiteralToken = 105,
    RawStringLiteralToken = 106,

    // ---- Punctuation ------------------------------------------------------
    OpenParenToken = 200,           // (
    CloseParenToken = 201,          // )
    OpenBracketToken = 202,         // [
    CloseBracketToken = 203,        // ]
    OpenBraceToken = 204,           // {
    CloseBraceToken = 205,          // }
    CommaToken = 206,               // ,
    SemicolonToken = 207,           // ;
    ColonToken = 208,               // :
    DotToken = 209,                 // .
    QuestionToken = 210,            // ?
    QuestionDotToken = 211,         // ?.
    DotDotToken = 212,              // ..
    DotDotLessToken = 213,          // ..<
    DotDotDotToken = 214,           // ...
    ArrowToken = 215,               // ->
    FatArrowToken = 216,            // =>
    AtToken = 217,                  // @
    HashToken = 218,                // #
    UnderscoreToken = 219,          // _
    BackslashToken = 220,           // \
    DollarToken = 221,              // $  (closure shorthand $0, $1)

    // ---- Operators --------------------------------------------------------
    PlusToken = 240,                // +
    MinusToken = 241,               // -
    StarToken = 242,                // *
    SlashToken = 243,               // /
    PercentToken = 244,             // %
    AmpersandToken = 245,           // &
    PipeToken = 246,                // |
    CaretToken = 247,               // ^
    TildeToken = 248,               // ~
    BangToken = 249,                // !
    LessLessToken = 250,            // <<
    GreaterGreaterToken = 251,      // >>
    AmpersandAmpersandToken = 252,  // &&
    PipePipeToken = 253,            // ||
    QuestionQuestionToken = 254,    // ??

    EqualsToken = 260,              // =
    EqualsEqualsToken = 261,        // ==
    BangEqualsToken = 262,          // !=
    EqualsEqualsEqualsToken = 263,  // ===
    BangEqualsEqualsToken = 264,    // !==
    LessToken = 265,                // <
    LessEqualsToken = 266,          // <=
    GreaterToken = 267,             // >
    GreaterEqualsToken = 268,       // >=

    PlusEqualsToken = 280,          // +=
    MinusEqualsToken = 281,         // -=
    StarEqualsToken = 282,          // *=
    SlashEqualsToken = 283,         // /=
    PercentEqualsToken = 284,       // %=
    AmpersandEqualsToken = 285,     // &=
    PipeEqualsToken = 286,          // |=
    CaretEqualsToken = 287,         // ^=
    LessLessEqualsToken = 288,      // <<=
    GreaterGreaterEqualsToken = 289,// >>=
    QuestionQuestionEqualsToken = 290, // ??=

    // ---- Reserved keywords (spec/lexical.md §5) ---------------------------
    AbstractKeyword = 400,
    ActorKeyword = 401,
    AliasKeyword = 402,
    AnyKeyword = 403,
    AsKeyword = 404,
    AssertKeyword = 405,
    AsyncKeyword = 406,
    AwaitKeyword = 407,
    BorrowingKeyword = 408,
    BreakKeyword = 409,
    CaseKeyword = 410,
    CatchKeyword = 411,
    ClassKeyword = 412,
    ConstKeyword = 413,
    ConsumingKeyword = 414,
    ContinueKeyword = 415,
    DefaultKeyword = 416,
    DeferKeyword = 417,
    DeinitKeyword = 418,
    DoKeyword = 419,
    ElseKeyword = 420,
    EnumKeyword = 421,
    ExtensionKeyword = 422,
    FallthroughKeyword = 423,
    FalseKeyword = 424,
    FinalKeyword = 425,
    FnKeyword = 426,
    ForKeyword = 427,
    GuardKeyword = 428,
    IfKeyword = 429,
    ImportKeyword = 430,
    InKeyword = 431,
    InitKeyword = 432,
    InoutKeyword = 433,
    InternalKeyword = 434,
    IsKeyword = 435,
    LazyKeyword = 436,
    LetKeyword = 437,
    MatchKeyword = 438,
    ModuleKeyword = 439,
    MoveKeyword = 440,
    MutatingKeyword = 441,
    NoneKeyword = 442,
    NonisolatedKeyword = 443,
    NonmutatingKeyword = 444,
    OpenKeyword = 445,
    OperatorKeyword = 446,
    OutKeyword = 447,
    OverrideKeyword = 448,
    PackageKeyword = 449,
    PostfixKeyword = 450,
    PreconditionKeyword = 451,
    PrefixKeyword = 452,
    PrivateKeyword = 453,
    ProtocolKeyword = 454,
    PublicKeyword = 455,
    RegionKeyword = 456,
    RepeatKeyword = 457,
    RequiredKeyword = 458,
    RethrowsKeyword = 459,
    ReturnKeyword = 460,
    SealedKeyword = 461,
    SelfKeyword = 462,          // self
    CapitalSelfKeyword = 463,   // Self
    SomeKeyword = 464,
    StaticKeyword = 465,
    StructKeyword = 466,
    SubscriptKeyword = 467,
    SuperKeyword = 468,
    SwitchKeyword = 469,
    ThrowKeyword = 470,
    ThrowsKeyword = 471,
    TrueKeyword = 472,
    TryKeyword = 473,
    TypealiasKeyword = 474,
    UnionKeyword = 475,
    UnownedKeyword = 476,
    UnsafeKeyword = 477,
    UsingKeyword = 478,
    VarKeyword = 479,
    WeakKeyword = 480,
    WhereKeyword = 481,
    WhileKeyword = 482,

    // ---- Contextual keywords ---------------------------------------------
    AssociatedtypeKeyword = 600,
    ConvenienceKeyword = 601,
    DidSetKeyword = 602,
    GetKeyword = 603,
    NewValueKeyword = 604,
    OldValueKeyword = 605,
    SetKeyword = 606,
    WillSetKeyword = 607,
    ComptimeKeyword = 608,
    UncheckedKeyword = 609,

    // ---- Directives -------------------------------------------------------
    HashIfDirective = 700,
    HashElseIfDirective = 701,
    HashElseDirective = 702,
    HashEndIfDirective = 703,
    HashErrorDirective = 704,
    HashWarningDirective = 705,
}
