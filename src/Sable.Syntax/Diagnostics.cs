using System.Collections;
using System.Collections.Immutable;
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
    ERR_ModuleInTwoPackages = 2090,

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
    ERR_ExposesHiddenType = 3063,
    ERR_OverrideSignatureMismatch = 3065,
    ERR_InvalidCast = 3066,
    ERR_UnknownDerivation = 3070,
    ERR_CannotDerive = 3071,
    ERR_DerivedFieldDoesNotConform = 3072,
    ERR_NotReflectableKind = 3080,
    ERR_BuiltinOutsideCore = 3081,
    ERR_MalformedBuiltin = 3082,
    ERR_MalformedArrayExtension = 3083,
    ERR_EndIsNoElement = 3090,
    ERR_HalfOpenRangeWithoutEnd = 3091,
    ERR_UnknownAttribute = 3092,
    ERR_AttributeDoesNotApply = 3093,
    ERR_MalformedAttribute = 3094,
    WRN_Deprecated = 3095,
    ERR_NotAvailable = 3096,
    ERR_NotDeeplyImmutable = 3097,
    ERR_NotSupportedYet = 3099,

    // '@test', '@bench' and '@fuzz' (docs/05 §5.1): what the harness can call.
    ERR_TestTakesParameters = 3100,
    ERR_TestIsGeneric = 3101,
    ERR_TestReturnsValue = 3102,
    ERR_TestIsInstanceMethod = 3103,
    ERR_FuzzSignature = 3104,
    ERR_BenchParameters = 3105,
    ERR_TestAndBench = 3106,
    ERR_TestAttributeArguments = 3107,
    ERR_TestIsLocal = 3108,
    ERR_TestNotReachable = 3109,
    ERR_BenchThrows = 3110,
    ERR_BackedPropertyNeedsInitializer = 3111,
    ERR_MissingBody = 3112,
    ERR_LazyProperty = 3113,
    ERR_IfValueNeedsElse = 3130,
    ERR_PropertyObservers = 3140,
    ERR_ConvenienceMustDelegate = 3141,
    ERR_EnumCaseValue = 3142,
    ERR_MissingSuperInit = 3143,
    ERR_OpenExistentialInUnion = 3150,
    ERR_ConstGenericPredicate = 3160,
    ERR_ValueGenericArgument = 3420,
    ERR_ConstGenericPredicateFails = 3421,
    ERR_SelfRequirementOnExistential = 3161,

    // Ownership, escape and isolation checks (S3200-S3208).
    ERR_IntegerDoesNotFit = 3200,
    ERR_ConstantOverflow = 3201,
    ERR_OutNotAssigned = 3202,
    ERR_MisplacedSuppression = 3203,
    ERR_NoncopyableTypeArgument = 3204,
    ERR_Escapes = 3205,
    ERR_ActorStateFromOutside = 3206,
    ERR_NotSendable = 3207,
    ERR_MisplacedInOut = 3208,

    // A type declared ': Sendable' that holds something that is not (S3410).
    ERR_DeclaredSendableIsNot = 3410,

    // Globals and statics whose initial values need each other (S3411).
    ERR_GlobalInitializationCycle = 3411,

    // A default value that leaves itself off a call it makes, so would be
    // put in without end (S3412).
    ERR_DefaultValueCycle = 3412,

    // ---- Compile-time evaluation (S3220-S3222): 'const', 'const fn' and
    // 'comptime' (SYNTAX.md §21).
    ERR_NotConstant = 3220,
    ERR_ConstantEvaluationFailed = 3221,
    ERR_CompileError = 3222,

    // ---- C interop (S3223-S3229): '@extern' and '@export' (SYNTAX.md §24).
    ERR_MalformedForeign = 3223,
    ERR_NotACType = 3224,

    // ---- Source generators (S3230-S3239): docs/08.
    ERR_GeneratorRule = 3230,
    ERR_GeneratorFailed = 3231,
    ERR_GeneratorReported = 3232,
    WRN_GeneratorReported = 3233,
    ERR_GeneratedFileInvalid = 3234,
    ERR_AsyncLetCaptured = 3235,
    ERR_TaskCaptureNotSendable = 3236,
    ERR_IsolatedWitness = 3237,
    ERR_IsolatedStateInSendableClosure = 3238,

    // ---- Lints (S3300-S3399): reported by Sable.Symbols' Lints, which
    // 'sable check' runs, never by compiling. Their severity is configured.
    LNT_UnusedLocal = 3300,
    LNT_UnusedParameter = 3301,
    LNT_UnusedImport = 3302,
    LNT_VarNeverMutated = 3303,
    LNT_ShadowedParameter = 3304,
    LNT_UnusedResult = 3308,
    LNT_ShadowedLocal = 3390,
    LNT_SelfAssignment = 3391,
    LNT_UnusedPrivate = 3392,
    LNT_ConstantArithmetic = 3394,

    // A lint a plugin adds (docs/05 §5.7), and one that failed as it ran.
    LNT_PluginLint = 3397,
    LNT_PluginFailed = 3398,
    LNT_UnknownLint = 3399,

    // ---- Reflection and the runtime's profiles (S3450-S3459) ---------------
    ERR_ReflectionDenied = 3450,

    // ---- Memory (S3460-S3469): what ARC cannot free.
    WRN_StrongReferenceCycle = 3460,

    // ---- Unsafe code (S3470-S3479): SYNTAX.md §23.
    ERR_UnsafeCallOutsideUnsafe = 3470,
    ERR_UnsafeNotAFunction = 3471,
    ERR_UnsafeDenied = 3472,
    ERR_YieldOutsideGenerator = 3480,
    ERR_YieldInDefer = 3481,
    ERR_ReturnValueInGenerator = 3482,
    ERR_InoutParameterInGenerator = 3483,
    ERR_AlignTooLarge = 3510,
    ERR_AttributeArgumentNotConstant = 3511,
    ERR_NotPolymorphic = 3512,
    ERR_DuplicateAttribute = 3513,
    ERR_InvalidEntryPoint = 3514,

    // ---- Format specs in interpolation (S3660-S3669) -------------------------
    ERR_FormatSpecMalformed = 3660,
    ERR_FormatKindNotForType = 3661,
    ERR_FormatOptionNotForType = 3662,
    ERR_FormatSpecNeedsFormattable = 3663,
    ERR_ClosureRethrows = 3664,

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
    ERR_FallthroughOutOfPlace = 6020,

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

    /// <summary>
    ///     Edits that would fix it, each complete on its own, in the
    ///     diagnostic's file: what <c>sable fix</c> applies and an editor
    ///     offers as a quick fix. Most diagnostics have none.
    /// </summary>
    public ImmutableArray<DiagnosticFix> Fixes { get; init; } = [];

    /// <summary>
    ///     The other places it is about — the earlier declaration a duplicate
    ///     clashes with, the other access of a conflict, the requirement a
    ///     member fails to match — each with a word on what is there. An
    ///     editor shows them beside the diagnostic, as links.
    /// </summary>
    public ImmutableArray<DiagnosticLocation> Related { get; init; } = [];

    /// <summary>
    ///     A node of the file the span is in, when whoever reported it knew
    ///     one. A span is an offset into one file, and several files' offsets
    ///     overlap: a diagnostic reported away from its file's own pass — a
    ///     late check, a lazily bound signature — is placed by this.
    /// </summary>
    public SyntaxNode? Anchor { get; init; }

    /// <summary>The same diagnostic, in the file of <paramref name="node" />, unless it already knows its own.</summary>
    public Diagnostic AnchoredAt(SyntaxNode? node) =>
        Anchor is not null || node is null ? this : this with { Anchor = node };

    /// <summary>The same diagnostic, saying another place is part of it.</summary>
    public Diagnostic WithRelated(DiagnosticLocation? location) =>
        location is null ? this : this with { Related = Related.Add(location) };

    // Two reports of the same thing are equal whatever fixes they carry:
    // the fixes are advice about the diagnostic, not part of what it says.
    public bool Equals(Diagnostic? other) =>
        other is not null && Code == other.Code && Severity == other.Severity && Span == other.Span && Message == other.Message;

    public override int GetHashCode() => HashCode.Combine(Code, Severity, Span, Message);

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

/// <summary>
///     A place a diagnostic points at besides its own span. A span is an
///     offset into one file, so it comes with a node of that file, which
///     whoever shows it finds the file by; with none, it is in the
///     diagnostic's own file.
/// </summary>
public sealed record DiagnosticLocation(SyntaxNode? Anchor, TextSpan Span, string Message) {
    /// <summary>A declaration's name, or the node itself when it has none.</summary>
    public static DiagnosticLocation? At(SyntaxNode? node, string message) => node is null ? null : new(node, node.Span, message);

    public static DiagnosticLocation? At(SyntaxToken token, string message) =>
        token.IsNone || token.Parent is not { } parent ? null : new(parent, token.Span, message);
}

/// <summary>A machine-applicable fix: what it does, in a few words, and the edits that do it.</summary>
public sealed record DiagnosticFix(string Title, ImmutableArray<TextChange> Changes);

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

    /// <summary>
    ///     The node of the file being reported about, if any: a diagnostic
    ///     added without an anchor of its own takes this one.
    /// </summary>
    public SyntaxNode? Anchor { get; private set; }

    /// <summary>
    ///     Anchors what is added, until the returned handle is disposed, to
    ///     <paramref name="node" />'s file. With none, the anchor stays as it is.
    /// </summary>
    public Anchoring AnchoredAt(SyntaxNode? node) => new(this, node);

    public readonly struct Anchoring : IDisposable {
        readonly DiagnosticBag _bag;
        readonly SyntaxNode? _previous;

        internal Anchoring(DiagnosticBag bag, SyntaxNode? node) {
            _bag = bag;
            _previous = bag.Anchor;
            bag.Anchor = node ?? bag.Anchor;
        }

        public void Dispose() => _bag.Anchor = _previous;
    }

    /// <summary>How many errors were held back while suppressed: whether a speculative parse went wrong.</summary>
    public int SuppressedErrors { get; private set; }

    public void Add(Diagnostic diagnostic) {
        if (_suppressed == 0) {
            _items.Add(diagnostic.AnchoredAt(Anchor));
        } else if (diagnostic.Severity == DiagnosticSeverity.Error) {
            SuppressedErrors++;
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
