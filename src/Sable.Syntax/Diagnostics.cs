using System.Collections;
using System.Globalization;

namespace Sable.Syntax;

/// <summary>
///     Diagnostic identifiers. Ranges are allocated per docs/04-implementation-plan.md §0.1:
///     S0001-0999 lexical, S1000-1999 syntactic, S2000-2999 name resolution,
///     S3000-3999 types, S4000-4999 ownership, S5000-5999 concurrency,
///     S6000-6999 patterns, S7000-7999 codegen, S8000-8999 linking, S9000+ internal.
/// </summary>
public enum ErrorCode {
    None = 0,

    // ---- Lexical (S0001-S0999) -------------------------------------------
    ERR_UnexpectedCharacter = 1,
    ERR_UnterminatedBlockComment = 2,
    ERR_UnterminatedString = 3,
    ERR_UnterminatedChar = 4,
    ERR_UnterminatedRawString = 5,
    ERR_EmptyCharLiteral = 6,
    ERR_CharLiteralTooLong = 7,
    ERR_InvalidEscapeSequence = 8,
    ERR_InvalidUnicodeEscape = 9,
    ERR_UnicodeScalarOutOfRange = 10,
    ERR_NumericLiteralNoDigits = 11,
    ERR_LeadingUnderscoreInNumber = 12,
    ERR_TrailingUnderscoreInNumber = 13,
    ERR_IntegerLiteralOverflow = 14,
    ERR_InvalidFloatLiteral = 15,
    ERR_HexFloatNeedsExponent = 16,

    // 17 was "a float needs digits on both sides". It cannot happen: by
    // spec/lexical.md, '1.' is the integer 1 followed by '.', which is what
    // makes 'x.0' and '1..<5' lex unambiguously.
    ERR_ByteOrderMarkNotAllowed = 18,
    ERR_UnterminatedEscapedIdentifier = 19,
    ERR_EmptyEscapedIdentifier = 20,
    ERR_UnterminatedInterpolation = 21,
    ERR_InvalidDigitForBase = 22,
    ERR_MultilineDelimiterMustBeOnOwnLine = 23,
    ERR_InsufficientIndentation = 24,
    ERR_UnknownDirective = 25,
    ERR_UnmatchedDirective = 26,
    ERR_UnterminatedConditional = 27,
    ERR_BadDirectiveCondition = 28,
    ERR_UserError = 29,
    WRN_UserWarning = 30,

    // ---- Syntactic (S1000-S1999) -----------------------------------------
    // 1000 was "unexpected token". Recovery never needs it: a token that
    // cannot start what is expected is reported by whatever expected it.
    ERR_ExpectedToken = 1001,
    ERR_ExpectedExpression = 1002,
    ERR_ExpectedType = 1003,
    ERR_ExpectedIdentifier = 1004,
    ERR_UnexpectedTrailingTokens = 1005,
    ERR_ExpectedPattern = 1006,

    // 1007 was "expected a statement". Anything may start one, so the
    // parser never has to say this.
    ERR_ExpectedDeclaration = 1008,
    ERR_NestedTooDeeply = 1009,
    ERR_ExpectedStatementEnd = 1010,
    ERR_BraceOnNewLine = 1011,
    ERR_TypealiasIsAlias = 1012,

    // ---- Name resolution (S2000-S2999) -----------------------------------
    ERR_DuplicateDeclaration = 2000,
    ERR_TypeNotFound = 2001,
    ERR_NameNotFound = 2002,
    ERR_AmbiguousName = 2003,
    ERR_NotAType = 2004,
    ERR_CircularAlias = 2005,
    ERR_ModuleNotFound = 2006,
    ERR_NoModuleDeclaration = 2007,

    // ---- Types (S3000-S3999) ---------------------------------------------
    ERR_WrongTypeArgumentCount = 3000,
    ERR_TypeArgumentsOnNonGeneric = 3001,
    ERR_MissingTypeArguments = 3002,
    ERR_VariableSizeNotAllowed = 3003,
    ERR_TypeMismatch = 3004,
    ERR_CannotInferType = 3005,
    ERR_NotCallable = 3006,
    ERR_NoMatchingOverload = 3007,
    ERR_AmbiguousOverload = 3008,
    ERR_OperatorNotDefined = 3009,
    ERR_NotAssignable = 3010,
    ERR_NotOptional = 3011,
    ERR_NotIndexable = 3012,
    ERR_NotIterable = 3013,
    ERR_BranchTypesDiffer = 3014,
    ERR_InconsistentArrayElements = 3015,
    ERR_ReturnValueRequired = 3016,
    ERR_ReturnValueNotAllowed = 3017,
    ERR_NoEnclosingLoop = 3018,
    ERR_SelfOutsideType = 3019,
    ERR_NotAValue = 3020,
    ERR_NoContextualType = 3021,
    ERR_CannotInferTypeArgument = 3022,
    ERR_DoesNotConform = 3023,
    ERR_NotAProtocol = 3024,
    ERR_MultipleBaseClasses = 3025,
    ERR_CircularInheritance = 3026,
    ERR_RequirementSignatureMismatch = 3027,
    ERR_MissingFieldInitializer = 3028,
    ERR_ExtensionStoredProperty = 3029,
    ERR_ExtensionInitializer = 3030,
    ERR_CannotExtend = 3031,
    ERR_MustUseTry = 3032,
    ERR_ThrowsNotDeclared = 3033,
    ERR_MustUseAwait = 3034,
    ERR_AwaitOutsideAsync = 3035,
    WRN_NothingThrowsHere = 3036,
    ERR_ExpressionTooComplex = 3038,
    ERR_MemberAccessMismatch = 3039,
    ERR_NotAccessible = 3040,
    ERR_NotMutable = 3041,
    ERR_MissingOverride = 3042,
    ERR_NothingToOverride = 3043,
    ERR_NotOpen = 3044,
    ERR_CannotInstantiate = 3045,
    ERR_IncoherentConformance = 3046,
    ERR_VarianceViolation = 3047,
    ERR_RethrowsWithoutThrowingParameter = 3048,
    ERR_RethrowsThrowsItsOwn = 3049,
    ERR_NotAllowedInBody = 3050,
    ERR_OpaqueReturnTypesDiffer = 3051,
    ERR_InfiniteSize = 3052,
    ERR_DuplicateConformance = 3053,
    ERR_ErrorTypeDoesNotFit = 3054,
    ERR_WeakNeedsOptionalClass = 3055,
    ERR_UnownedNeedsClass = 3056,
    ERR_UsedAfterMove = 3057,
    ERR_CopyOfNoncopyable = 3058,
    ERR_PartialMove = 3059,
    ERR_ReferenceNeedsPlace = 3060,
    ERR_ReferenceOutlivesRoot = 3061,
    ERR_OverlappingAccess = 3062,
    ERR_UnknownDerivation = 3070,
    ERR_CannotDerive = 3071,
    ERR_DerivedFieldDoesNotConform = 3072,
    ERR_NotReflectableKind = 3080,
    ERR_BuiltinOutsideCore = 3081,
    ERR_MalformedBuiltin = 3082,
    ERR_MalformedArrayExtension = 3083,
    ERR_NotSupportedYet = 3099,

    // '@test' and '@bench' (docs/05 §5.1): what the harness can call.
    ERR_TestTakesParameters = 3100,
    ERR_TestIsGeneric = 3101,
    ERR_TestReturnsValue = 3102,
    ERR_TestIsInstanceMethod = 3103,
    ERR_TestIsAsync = 3104,
    ERR_BenchParameters = 3105,
    ERR_TestAndBench = 3106,
    ERR_TestAttributeArguments = 3107,
    ERR_TestIsLocal = 3108,
    ERR_TestNotReachable = 3109,
    ERR_BenchThrows = 3110,
    ERR_BackedPropertyNeedsInitializer = 3111,
    ERR_MissingBody = 3112,
    ERR_LazyProperty = 3113,

    // ---- Lints (S3300-S3399): reported by Sable.Symbols' Lints, which
    // 'sable check' runs, never by compiling. Their severity is configured.
    LNT_UnusedLocal = 3300,
    LNT_UnusedParameter = 3301,
    LNT_UnusedImport = 3302,
    LNT_VarNeverMutated = 3303,
    LNT_ShadowedParameter = 3304,
    LNT_UnknownLint = 3399,

    // ---- Patterns and flow (S6000-S6999) ---------------------------------
    ERR_PatternTypeMismatch = 6000,
    ERR_UnknownUnionCase = 6001,
    ERR_WrongPayloadCount = 6002,
    ERR_MatchNotExhaustive = 6003,
    ERR_NotAllPathsReturn = 6004,
    WRN_UnreachableCode = 6005,
    WRN_PatternAlreadyCovered = 6006,
    ERR_MatchArmTypesDiffer = 6007,
    ERR_UseBeforeAssignment = 6008,
    ERR_RefutablePatternInDeclaration = 6009,

    // ---- Code generation (S7000-S7999) ---------------------------------
    ERR_NotLoweredYet = 7000,
    ERR_UnknownBuiltin = 7001,
    ERR_BuiltinSignatureMismatch = 7002,
    ERR_EndlessInstantiation = 7003,
}

public enum DiagnosticSeverity {
    Hidden,
    Info,
    Warning,
    Error
}

/// <summary>
///     A structured diagnostic. Carries data, not a pre-formatted string, so the CLI
///     and the language server can render the same object differently.
/// </summary>
public sealed record Diagnostic(
    ErrorCode Code,
    DiagnosticSeverity Severity,
    TextSpan Span,
    string Message) {
    public string Id => "S" + ((int)Code).ToString("D4", CultureInfo.InvariantCulture);

    public static Diagnostic Error(ErrorCode code, TextSpan span, string message) =>
        new(code, DiagnosticSeverity.Error, span, message);

    public static Diagnostic Warning(ErrorCode code, TextSpan span, string message) =>
        new(code, DiagnosticSeverity.Warning, span, message);

    public override string ToString() => $"{Id}: {Message} {Span}";

    /// <summary>Renders a rustc/Roslyn-style caret diagnostic against its source.</summary>
    public string Format(SourceText source) {
        var pos = source.GetLinePosition(Span.Start);
        var lineText = source.GetLineText(pos.Line);
        var gutter = pos.Line.ToString(CultureInfo.InvariantCulture);
        var pad = new string(' ', gutter.Length);
        var caretPad = new string(' ', Math.Max(0, pos.Column - 1));
        var caret = new string('^', Math.Max(1, Math.Min(Span.Length, lineText.Length - pos.Column + 1)));
        var level = Severity == DiagnosticSeverity.Error ? "error" : Severity.ToString().ToLowerInvariant();

        return $"""
                {level}[{Id}]: {Message}
                {pad}--> {source.FileName}:{pos.Line}:{pos.Column}
                {pad} |
                {gutter} | {lineText}
                {pad} | {caretPad}{caret}
                """;
    }
}

/// <summary>A mutable, ordered collection of diagnostics produced during a compilation step.</summary>
public sealed class DiagnosticBag : IReadOnlyCollection<Diagnostic> {
    readonly List<Diagnostic> _items = [];
    int _suppressed;

    public int Count => _items.Count;
    public bool HasErrors => _items.Exists(static d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>
    ///     Stops recording until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    ///     Bidirectional inference sometimes binds an expression to find out what
    ///     it is, then binds it again in the position it turned out to have. The
    ///     first pass is a question, not a claim, so its complaints are not the
    ///     user's problem.
    /// </remarks>
    public Suppression Suppress() => new(this);

    public readonly struct Suppression : IDisposable {
        readonly DiagnosticBag _bag;

        internal Suppression(DiagnosticBag bag) {
            _bag = bag;
            bag._suppressed++;
        }

        public void Dispose() => _bag._suppressed--;
    }

    public void Add(Diagnostic diagnostic) {
        if (_suppressed == 0) {
            _items.Add(diagnostic);
        }
    }

    public void Error(ErrorCode code, TextSpan span, string message) => Add(Diagnostic.Error(code, span, message));

    public void Error(ErrorCode code, int start, int length, string message) =>
        Add(Diagnostic.Error(code, new(start, length), message));

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

    public IReadOnlyList<Diagnostic> ToList() => _items.AsReadOnly();

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
