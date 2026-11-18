namespace Sable.Syntax;

/// <summary>
///     Every lexical and syntactic element kind. Values are grouped into stable
///     ranges so that <see cref="SyntaxFacts" /> can answer category questions with
///     a range check rather than a switch.
/// </summary>
/// <remarks>
///     Ranges:
///     <list type="bullet">
///         <item>0000-0099  control and trivia</item>
///         <item>0100-0199  literals and identifiers</item>
///         <item>0200-0399  punctuation and operators</item>
///         <item>0400-0599  reserved keywords</item>
///         <item>0600-0699  contextual keywords</item>
///         <item>0700-0799  directives</item>
///         <item>1000+      syntax nodes (added in the parser milestone)</item>
///     </list>
/// </remarks>
public enum SyntaxKind : ushort {
    None = 0,
    EndOfFile = 1,
    BadToken = 2,

    // ---- Trivia -----------------------------------------------------------
    WhitespaceTrivia = 10,
    NewLineTrivia = 11,
    LineCommentTrivia = 12,
    BlockCommentTrivia = 13,
    DocCommentTrivia = 14, // /// and /** */
    ModuleDocCommentTrivia = 15, // //!
    ShebangTrivia = 16,
    DisabledTextTrivia = 17,
    SkippedTokensTrivia = 18,
    DirectiveTrivia = 19,

    // ---- Literals and identifiers ----------------------------------------
    IdentifierToken = 100,
    IntegerLiteralToken = 101,
    FloatLiteralToken = 102,
    CharLiteralToken = 103,
    StringLiteralToken = 104,
    MultilineStringLiteralToken = 105,
    RawStringLiteralToken = 106,

    // Pieces an interpolated string is rebuilt from by the parser.
    InterpolatedStringStartToken = 107,
    InterpolatedTextToken = 108,
    InterpolationStartToken = 109,
    InterpolationEndToken = 110,
    InterpolatedStringEndToken = 111,

    // ---- Punctuation ------------------------------------------------------
    OpenParenToken = 200, // (
    CloseParenToken = 201, // )
    OpenBracketToken = 202, // [
    CloseBracketToken = 203, // ]
    OpenBraceToken = 204, // {
    CloseBraceToken = 205, // }
    CommaToken = 206, // ,
    SemicolonToken = 207, // ;
    ColonToken = 208, // :
    DotToken = 209, // .
    QuestionToken = 210, // ?
    QuestionDotToken = 211, // ?.
    DotDotToken = 212, // ..
    DotDotLessToken = 213, // ..<
    DotDotDotToken = 214, // ...
    ArrowToken = 215, // ->
    FatArrowToken = 216, // =>
    AtToken = 217, // @
    HashToken = 218, // #
    UnderscoreToken = 219, // _
    BackslashToken = 220, // \
    DollarToken = 221, // $  (closure shorthand $0, $1)

    // ---- Operators --------------------------------------------------------
    PlusToken = 240, // +
    MinusToken = 241, // -
    StarToken = 242, // *
    SlashToken = 243, // /
    PercentToken = 244, // %
    AmpersandToken = 245, // &
    PipeToken = 246, // |
    CaretToken = 247, // ^
    TildeToken = 248, // ~
    BangToken = 249, // !
    LessLessToken = 250, // <<
    GreaterGreaterToken = 251, // >>
    AmpersandAmpersandToken = 252, // &&
    PipePipeToken = 253, // ||
    QuestionQuestionToken = 254, // ??

    EqualsToken = 260, // =
    EqualsEqualsToken = 261, // ==
    BangEqualsToken = 262, // !=
    EqualsEqualsEqualsToken = 263, // ===
    BangEqualsEqualsToken = 264, // !==
    LessToken = 265, // <
    LessEqualsToken = 266, // <=
    GreaterToken = 267, // >
    GreaterEqualsToken = 268, // >=

    PlusEqualsToken = 280, // +=
    MinusEqualsToken = 281, // -=
    StarEqualsToken = 282, // *=
    SlashEqualsToken = 283, // /=
    PercentEqualsToken = 284, // %=
    AmpersandEqualsToken = 285, // &=
    PipeEqualsToken = 286, // |=
    CaretEqualsToken = 287, // ^=
    LessLessEqualsToken = 288, // <<=
    GreaterGreaterEqualsToken = 289, // >>=
    QuestionQuestionEqualsToken = 290, // ??=

    // ---- Reserved keywords (spec/lexical.md §5) ---------------------------
    AbstractKeyword = 400,
    ActorKeyword = 401,
    AliasKeyword = 402,
    AnyKeyword = 403,
    AsKeyword = 404,
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
    SelfKeyword = 462, // self
    CapitalSelfKeyword = 463, // Self
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
    RefKeyword = 483, // out of alphabetical order; the range check is numeric

    // ---- Contextual keywords ---------------------------------------------
    AssociatedtypeKeyword = 600,
    ConvenienceKeyword = 601,
    DidSetKeyword = 602,
    GetKeyword = 603,
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

    // ---- Type nodes (1000-1099) -------------------------------------------
    QualifiedName = 1000,
    SimpleNameType = 1001,
    TypeArgumentList = 1002,
    OptionalType = 1003,
    ImplicitlyUnwrappedType = 1004,
    ReferenceType = 1005,
    RelativeReferenceSpec = 1006,
    FixedSizeArrayType = 1007,
    InlineArrayType = 1008,
    GrowArrayType = 1009,
    SliceType = 1010,
    DictionaryType = 1011,
    TupleType = 1012,
    TupleTypeElement = 1013,
    FunctionType = 1014,
    ThrowsSpec = 1015,
    OpaqueType = 1016,
    ExistentialType = 1017,
    RefType = 1018,
    CompositionType = 1019,
    UnionType = 1020,
    SelfType = 1021,
    ParenthesizedType = 1022,

    // ---- Expression nodes (1100-1199) -------------------------------------
    LiteralExpression = 1100,
    IdentifierName = 1101,
    GenericName = 1102,
    SelfExpression = 1103,
    SuperExpression = 1104,
    MemberAccessExpression = 1105,
    ImplicitMemberAccessExpression = 1106,
    InvocationExpression = 1107,
    ArgumentList = 1108,
    Argument = 1109,
    ElementAccessExpression = 1110,
    PrefixUnaryExpression = 1111,
    PostfixUnaryExpression = 1112,
    TryExpression = 1113,
    BinaryExpression = 1114,
    AssignmentExpression = 1115,
    ConditionalExpression = 1116,
    RangeExpression = 1117,
    CastExpression = 1118,
    TypeTestExpression = 1119,
    TupleExpression = 1120,
    TupleElement = 1121,
    ParenthesizedExpression = 1122,
    ArrayExpression = 1123,
    DictionaryExpression = 1124,
    KeyValuePair = 1125,
    RecordExpression = 1126,
    FieldInitializer = 1127,
    BuiltinExpression = 1128,
    ClosureExpression = 1129,
    BraceClosureExpression = 1130,
    ClosureSignature = 1131,
    ClosureParameter = 1132,
    ClosureHeader = 1133,
    CaptureList = 1134,
    CaptureItem = 1135,
    MatchExpression = 1136,
    MatchArm = 1137,
    MatchCaseBlock = 1138,
    PatternWhereClause = 1139,
    ImplicitParameterExpression = 1140,
    InterpolatedStringExpression = 1141,
    InterpolatedText = 1142,
    Interpolation = 1143,
    TypeExpression = 1144,

    // ---- Pattern nodes (1200-1299) ----------------------------------------
    WildcardPattern = 1200,
    BindingPattern = 1201,
    LiteralPattern = 1202,
    RangePattern = 1203,
    TuplePattern = 1204,
    UnionCasePattern = 1205,
    PatternArgumentList = 1206,
    SubPattern = 1207,
    RecordPattern = 1208,
    FieldPattern = 1209,
    ArrayPattern = 1210,
    RestPattern = 1211,
    TypeTestPattern = 1212,
    BindingTypeTestPattern = 1213,
    OrPattern = 1214,
    ParenthesizedPattern = 1215,

    // ---- Statement nodes (1300-1399) --------------------------------------
    Block = 1300,
    ExpressionStatement = 1301,
    LocalDeclarationStatement = 1302,
    VariableDeclarator = 1303,
    TypeAnnotation = 1304,
    EqualsValueClause = 1305,
    IfStatement = 1306,
    ElseClause = 1307,
    ExpressionCondition = 1308,
    OptionalBindingCondition = 1309,
    CasePatternCondition = 1310,
    GuardStatement = 1311,
    WhileStatement = 1312,
    RepeatStatement = 1313,
    ForStatement = 1314,
    ForWhereClause = 1315,
    SwitchStatement = 1316,
    SwitchCase = 1317,
    DoStatement = 1318,
    CatchClause = 1319,
    RegionStatement = 1320,
    UsingStatement = 1321,
    DeferStatement = 1322,
    ReturnStatement = 1323,
    ThrowStatement = 1324,
    BreakStatement = 1325,
    ContinueStatement = 1326,
    FallthroughStatement = 1327,
    UnsafeStatement = 1328,
    ComptimeStatement = 1329,
    LabeledStatement = 1330,
    EmptyStatement = 1331,
    DeclarationStatement = 1332,

    // ---- Declaration nodes (1400-1499) ------------------------------------
    CompilationUnit = 1400,
    ModuleDeclaration = 1401,
    ImportDeclaration = 1402,
    ImportItemList = 1403,
    ImportItem = 1404,
    Attribute = 1405,
    FunctionDeclaration = 1406,
    ParameterList = 1407,
    Parameter = 1408,
    ReturnClause = 1409,
    GenericParameterList = 1410,
    GenericParameter = 1411,
    WhereClause = 1412,
    WhereConstraint = 1413,
    BaseTypeList = 1414,
    StructDeclaration = 1415,
    ClassDeclaration = 1416,
    ActorDeclaration = 1417,
    ProtocolDeclaration = 1418,
    UnionDeclaration = 1419,
    EnumDeclaration = 1420,
    ExtensionDeclaration = 1421,
    AliasDeclaration = 1422,
    AssociatedTypeDeclaration = 1424,
    FieldDeclaration = 1425,
    AccessorList = 1426,
    Accessor = 1427,
    ConstDeclaration = 1428,
    InitDeclaration = 1429,
    DeinitDeclaration = 1430,
    SubscriptDeclaration = 1431,
    OperatorDeclaration = 1432,
    UnionCaseDeclaration = 1433,
    UnionCasePayloadList = 1434,
    UnionCaseField = 1435,
    EnumCaseDeclaration = 1436,
    IncompleteDeclaration = 1437,
    ComptimeDeclaration = 1438
}
