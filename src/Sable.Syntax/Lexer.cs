using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>
///     Converts source text into a token stream with full-fidelity trivia.
/// </summary>
/// <remarks>
///     <para>
///         Implements spec/lexical.md. The round-trip invariant — concatenating
///         <see cref="SyntaxToken.ToFullString" /> over every token reproduces the source
///         exactly — is enforced by the test suite and must never be broken.
///     </para>
///     <para>
///         Conditional-compilation directives (<c>#if</c> and friends) are lexed
///         as <c>#</c> plus an identifier here. Turning inactive regions into
///         <see cref="SyntaxKind.DisabledTextTrivia" /> is a separate pass that runs
///         after the parser milestone.
///     </para>
/// </remarks>
public sealed class Lexer {
    readonly SourceText _source;
    readonly ParseOptions _options;
    readonly DiagnosticBag _diagnostics = new();
    int _position;

    /// <summary>Nesting depth of <c>(</c> and <c>[</c>; suppresses terminators.</summary>
    int _bracketDepth;

    /// <summary>Start of the current token's text, for relative part spans.</summary>
    int _tokenStart;

    Lexer(SourceText source, ParseOptions options) {
        _source = source;
        _options = options;
    }

    /// <summary>Creates a lexer positioned mid-file, for incremental re-lexing.</summary>
    internal Lexer(SourceText source, ParseOptions options, int startPosition) {
        _source = source;
        _options = options;
        _position = startPosition;
    }

    /// <summary>The offset the next token will be read from.</summary>
    internal int Position => _position;

    internal IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.ToList();

    /// <summary>
    ///     True when no conditional-compilation region is open, which is the
    ///     condition for the incremental lexer to splice old tokens back in.
    /// </summary>
    internal bool IsResyncSafe => _conditionals.Count == 0;

    internal GreenToken LexNextToken() => LexToken();

    /// <summary>
    ///     Reports conditionals left open at end of file. The all-at-once path
    ///     does this inline; the incremental path calls it when it reaches the
    ///     end rather than resynchronising.
    /// </summary>
    internal void ReportUnterminatedConditionals() {
        if (_conditionals.Count == 0) {
            return;
        }

        _diagnostics.Error(
            ErrorCode.ERR_UnterminatedConditional,
            new(Math.Max(0, _source.Length - 1), Math.Min(1, _source.Length)),
            $"{_conditionals.Count} conditional-compilation directive(s) "
            + "were never closed with '#endif'"
        );
    }

    /// <summary>Applies the newline rule to an already-assembled token run.</summary>
    internal static void MarkTerminators(Span<GreenToken> tokens) => MarkStatementTerminators(tokens);

    public static ImmutableArray<SyntaxToken> Tokenize(
        SourceText source,
        out IReadOnlyList<Diagnostic> diagnostics
    ) =>
        Tokenize(source, ParseOptions.Default, out diagnostics);

    public static ImmutableArray<SyntaxToken> Tokenize(
        SourceText source,
        ParseOptions options,
        out IReadOnlyList<Diagnostic> diagnostics
    ) {
        var lexer = new Lexer(source, options);
        var tokens = lexer.LexAll();
        diagnostics = lexer._diagnostics.ToList();
        return tokens;
    }

    public static ImmutableArray<SyntaxToken> Tokenize(string text, string fileName = "<test>") =>
        Tokenize(new(text, fileName), out _);

    /// <summary>
    ///     Lexes to green tokens, which is what the parser and the incremental
    ///     path both work on.
    /// </summary>
    internal static ImmutableArray<GreenToken> TokenizeGreen(
        SourceText source,
        ParseOptions options,
        out IReadOnlyList<Diagnostic> diagnostics,
        out bool hasDirectives
    ) {
        var lexer = new Lexer(source, options);
        var tokens = lexer.LexAllGreen();
        diagnostics = lexer._diagnostics.ToList();
        hasDirectives = lexer._sawDirective;
        return tokens;
    }

    bool _sawDirective;

    // -----------------------------------------------------------------------

    char Current => Peek(0);
    char Next => Peek(1);
    bool AtEnd => _position >= _source.Length;

    char Peek(int offset) {
        var index = _position + offset;
        return index < _source.Length ? _source[index] : '\0';
    }

    bool Matches(string text) {
        if (_position + text.Length > _source.Length) {
            return false;
        }

        for (var i = 0; i < text.Length; i++) {
            if (_source[_position + i] != text[i]) {
                return false;
            }
        }

        return true;
    }

    string TextBetween(int start, int end) => Intern(_source.Text.AsSpan(start, end - start));

    /// <summary>
    ///     Interns identifier and literal text. Source repeats the same names over
    ///     and over, and reference-equal text is what lets the token cache compare
    ///     cheaply.
    /// </summary>
    readonly Dictionary<string, string> _internTable = new(StringComparer.Ordinal);

    string Intern(ReadOnlySpan<char> text) {
        if (text.Length == 0) {
            return string.Empty;
        }

        if (text.Length > 64) {
            return text.ToString();
        }

        var lookup = _internTable.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(text, out var existing)) {
            return existing;
        }

        var created = text.ToString();
        _internTable[created] = created;
        return created;
    }

    // -----------------------------------------------------------------------

    ImmutableArray<GreenToken> LexAllGreen() {
        var green = LexAllGreenCore();
        return [..green];
    }

    List<GreenToken> LexAllGreenCore() {
        if (_source.Length > 0 && _source[0] == '\uFEFF') {
            _diagnostics.Error(
                ErrorCode.ERR_ByteOrderMarkNotAllowed,
                new(0, 1),
                "byte order mark is not allowed; save the file as UTF-8 without a BOM"
            );
        }

        var green = new List<GreenToken>();
        while (true) {
            var token = LexToken();
            green.Add(token);
            if (token.Kind == SyntaxKind.EndOfFile) {
                break;
            }
        }

        if (_conditionals.Count > 0) {
            _diagnostics.Error(
                ErrorCode.ERR_UnterminatedConditional,
                new(Math.Max(0, _source.Length - 1), Math.Min(1, _source.Length)),
                $"{_conditionals.Count} conditional-compilation directive(s) "
                + "were never closed with '#endif'"
            );
        }

        MarkStatementTerminators(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(green));
        return green;
    }

    ImmutableArray<SyntaxToken> LexAll() {
        var green = LexAllGreenCore();
        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>(green.Count);
        var position = 0;
        foreach (var token in green) {
            tokens.Add(new(token, null, position));
            position += token.FullWidth;
        }

        return tokens.MoveToImmutable();
    }

    GreenToken LexToken() {
        var leading = LexTrivia(false);

        _tokenStart = _position;
        var start = _position;
        var (kind, value, parts) = ScanToken();

        // Punctuation and keywords always have the same spelling, so the
        // canonical string can be shared instead of cut from the source.
        var canonical = SyntaxFacts.GetText(kind);
        var text = canonical is not null && canonical.Length == _position - start
            ? canonical
            : TextBetween(start, _position);

        var trailing = LexTrivia(true);

        return GreenTokenCache.Create(kind, text, leading, trailing, value, parts);
    }

    // -----------------------------------------------------------------------
    // Trivia
    // -----------------------------------------------------------------------

    readonly List<GreenTrivia> _triviaBuffer = [];

    ImmutableArray<GreenTrivia> LexTrivia(bool isTrailing) {
        var builder = _triviaBuffer;
        builder.Clear();

        while (!AtEnd) {
            var start = _position;

            if (IsDirectiveAhead()) {
                ScanDirective(builder);
                continue;
            }

            // Inside an inactive region nothing is a token, so whole lines are
            // taken as disabled text until a directive reopens it.
            if (!IsActive) {
                // A directive may be indented, here as in active code: the
                // indentation is whitespace, and the directive comes next.
                var indented = _position;
                while (indented < _source.Length && _source[indented] is ' ' or '\t') {
                    indented++;
                }

                if (indented > _position && indented < _source.Length && _source[indented] == '#' && IsAtLineStart(_position)) {
                    var save = _position;
                    _position = indented;
                    var directive = IsDirectiveAhead();
                    _position = save;
                    if (directive) {
                        _position = indented;
                        builder.Add(Trivia(SyntaxKind.WhitespaceTrivia, start));
                        continue;
                    }
                }

                ScanDisabledLine();
                builder.Add(Trivia(SyntaxKind.DisabledTextTrivia, start));
                continue;
            }

            // A byte order mark is an error, reported once for the file; it is
            // kept as skipped text on the first token, so that the tree still
            // gives back every character of the file.
            if (_position == 0 && Current == '\uFEFF') {
                _position = 1;
                builder.Add(Trivia(SyntaxKind.SkippedTokensTrivia, start));
                continue;
            }

            if (_position == 0 && Matches("#!")) {
                while (!AtEnd && Current is not ('\n' or '\r')) {
                    _position++;
                }

                builder.Add(Trivia(SyntaxKind.ShebangTrivia, start));
                continue;
            }

            if (Current is ' ' or '\t' or '\v' or '\f') {
                while (Current is ' ' or '\t' or '\v' or '\f') {
                    _position++;
                }

                builder.Add(Trivia(SyntaxKind.WhitespaceTrivia, start));
                continue;
            }

            if (Current is '\r' or '\n') {
                if (Current == '\r' && Next == '\n') {
                    _position += 2;
                } else {
                    _position++;
                }

                builder.Add(Trivia(SyntaxKind.NewLineTrivia, start));

                // Roslyn's rule: trailing trivia ends at the first line break.
                if (isTrailing) {
                    break;
                }

                continue;
            }

            if (Current == '/' && Next == '/') {
                var kind = SyntaxKind.LineCommentTrivia;
                if (Peek(2) == '/' && Peek(3) != '/') {
                    kind = SyntaxKind.DocCommentTrivia;
                } else if (Peek(2) == '!') {
                    kind = SyntaxKind.ModuleDocCommentTrivia;
                }

                while (!AtEnd && Current is not ('\n' or '\r')) {
                    _position++;
                }

                builder.Add(Trivia(kind, start));
                continue;
            }

            if (Current == '/' && Next == '*') {
                var kind = Peek(2) == '*' && Peek(3) != '/'
                    ? SyntaxKind.DocCommentTrivia
                    : SyntaxKind.BlockCommentTrivia;
                ScanBlockComment(start);
                builder.Add(Trivia(kind, start));
                continue;
            }

            break;
        }

        return builder.Count == 0 ? [] : [..builder];
    }

    GreenTrivia Trivia(SyntaxKind kind, int start) =>
        GreenTrivia.Create(kind, _source.Text.AsSpan(start, _position - start));

    /// <summary>Scans a block comment, honouring nesting per spec/lexical.md §8.</summary>
    void ScanBlockComment(int start) {
        _position += 2;
        var depth = 1;
        while (depth > 0) {
            if (AtEnd) {
                _diagnostics.Error(
                    ErrorCode.ERR_UnterminatedBlockComment,
                    new(start, 2),
                    "unterminated block comment"
                );
                return;
            }

            if (Current == '/' && Next == '*') {
                depth++;
                _position += 2;
            } else if (Current == '*' && Next == '/') {
                depth--;
                _position += 2;
            } else {
                _position++;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Tokens
    // -----------------------------------------------------------------------

    (SyntaxKind Kind, object? Value, ImmutableArray<StringPart> Parts) ScanToken() {
        if (AtEnd) {
            return (SyntaxKind.EndOfFile, null, default);
        }

        var c = Current;

        if (c == '`') {
            return ScanEscapedIdentifier();
        }

        if (SyntaxFacts.IsIdentifierStart(c)) {
            return ScanIdentifierOrKeyword();
        }

        if (char.IsAsciiDigit(c)) {
            return ScanNumber();
        }

        if (c == '\'') {
            return ScanCharLiteral();
        }

        if (c == '"') {
            return ScanStringLiteral();
        }

        if (c == '#' && (Peek(1) == '"' || IsRawStringStart())) {
            return ScanRawString();
        }

        return (ScanPunctuation(), null, default);
    }

    bool IsRawStringStart() {
        var i = _position;
        while (i < _source.Length && _source[i] == '#') {
            i++;
        }

        return i < _source.Length && _source[i] == '"';
    }

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanIdentifierOrKeyword() {
        var start = _position;
        while (!AtEnd && SyntaxFacts.IsIdentifierContinue(Current)) {
            _position++;
        }

        var text = TextBetween(start, _position);
        var normalized = text.IsNormalized(NormalizationForm.FormC)
            ? text
            : text.Normalize(NormalizationForm.FormC);

        // A lone '_' is the wildcard, not an identifier (spec/lexical.md §3).
        if (normalized == "_") {
            return (SyntaxKind.UnderscoreToken, null, default);
        }

        var kind = SyntaxFacts.KeywordKind(normalized);
        object? value = kind switch {
            SyntaxKind.TrueKeyword => true,
            SyntaxKind.FalseKeyword => false,
            _ => null,
        };

        if (kind == SyntaxKind.IdentifierToken && SyntaxFacts.IsFutureReserved(normalized)) {
            _diagnostics.Add(
                Diagnostic.Warning(
                    ErrorCode.None,
                    new(start, _position - start),
                    $"'{normalized}' is reserved for future use and may become a keyword"
                )
            );
        }

        return (kind, value, default);
    }

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanEscapedIdentifier() {
        var start = _position;
        _position++; // opening backtick

        var contentStart = _position;
        while (!AtEnd && Current != '`' && Current is not ('\n' or '\r')) {
            _position++;
        }

        if (Current != '`') {
            _diagnostics.Error(
                ErrorCode.ERR_UnterminatedEscapedIdentifier,
                new(start, _position - start),
                "unterminated escaped identifier; expected a closing '`'"
            );
            return (SyntaxKind.IdentifierToken, null, default);
        }

        var content = TextBetween(contentStart, _position);
        _position++; // closing backtick

        if (content.Length == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_EmptyEscapedIdentifier,
                new(start, _position - start),
                "escaped identifier cannot be empty"
            );
        }

        return (SyntaxKind.IdentifierToken, content, default);
    }

    // -----------------------------------------------------------------------
    // Numeric literals
    // -----------------------------------------------------------------------

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanNumber() {
        var start = _position;

        if (Current == '0' && Next is 'x' or 'X') {
            return ScanRadixOrHexFloat(start, 16);
        }

        if (Current == '0' && Next is 'o' or 'O') {
            return ScanRadix(start, 8);
        }

        if (Current == '0' && Next is 'b' or 'B') {
            return ScanRadix(start, 2);
        }

        return ScanDecimal(start);
    }

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanDecimal(int start) {
        ScanDigitRun(10);

        var isFloat = false;

        // A '.' is part of the literal only when a digit follows, so that
        // '1..<5' and 'tuple.0' lex correctly (spec/lexical.md §6).
        if (Current == '.' && char.IsAsciiDigit(Next)) {
            isFloat = true;
            _position++;
            ScanDigitRun(10);
        }

        if (Current is 'e' or 'E') {
            var save = _position;
            _position++;
            if (Current is '+' or '-') {
                _position++;
            }

            if (char.IsAsciiDigit(Current)) {
                isFloat = true;
                ScanDigitRun(10);
            } else {
                _position = save; // not an exponent: leave it to the next token
            }
        }

        var text = TextBetween(start, _position);
        var span = new TextSpan(start, _position - start);
        CheckUnderscores(text, span);

        CheckNoTrailingDigits(start, 10);

        var cleaned = text.Replace("_", string.Empty, StringComparison.Ordinal);

        if (isFloat) {
            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) {
                _diagnostics.Error(
                    ErrorCode.ERR_InvalidFloatLiteral,
                    span,
                    $"'{text}' is not a valid floating-point literal"
                );
                d = 0;
            }

            return (SyntaxKind.FloatLiteralToken, d, default);
        }

        // A literal is a ulong when it fits one, and a UInt128 when only the
        // 128-bit types can hold it.
        if (ulong.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out var u)) {
            return (SyntaxKind.IntegerLiteralToken, u, default);
        }

        if (UInt128.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out var wide)) {
            return (SyntaxKind.IntegerLiteralToken, wide, default);
        }

        _diagnostics.Error(
            ErrorCode.ERR_IntegerLiteralOverflow,
            span,
            $"integer literal '{text}' does not fit in 128 bits"
        );
        return (SyntaxKind.IntegerLiteralToken, 0UL, default);
    }

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanRadix(int start, int radix) {
        _position += 2; // 0o / 0b
        var digitsStart = _position;
        ScanDigitRun(radix);

        var span = new TextSpan(start, _position - start);
        var digits = TextBetween(digitsStart, _position);

        if (digits.Length == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_NumericLiteralNoDigits,
                span,
                $"expected at least one base-{radix} digit after '{TextBetween(start, digitsStart)}'"
            );
            return (SyntaxKind.IntegerLiteralToken, 0UL, default);
        }

        CheckUnderscores(TextBetween(start, _position), span);
        CheckNoTrailingDigits(start, radix);
        return (SyntaxKind.IntegerLiteralToken, ParseRadix(digits, radix, span), default);
    }

    /// <summary>
    ///     Reports a literal that runs straight into letters or digits.
    /// </summary>
    /// <remarks>
    ///     The grammar in spec/lexical.md §6 stops a literal at the first
    ///     character it cannot use, so <c>0b12</c> would lex as <c>0b1</c>
    ///     followed by <c>2</c>, and <c>1.0f</c> as <c>1.0</c> followed by a name.
    ///     Since suffixes are not supported, neither of those is ever what was
    ///     meant, and saying so beats silently agreeing with it.
    /// </remarks>
    void CheckNoTrailingDigits(int start, int radix) {
        if (AtEnd || !char.IsAsciiLetterOrDigit(Current)) {
            return;
        }

        var bad = Current;
        while (!AtEnd && (char.IsAsciiLetterOrDigit(Current) || Current == '_')) {
            _position++;
        }

        var code = char.IsAsciiDigit(bad) && radix != 10
            ? ErrorCode.ERR_InvalidDigitForBase
            : ErrorCode.ERR_InvalidFloatLiteral;

        _diagnostics.Error(
            code,
            new(start, _position - start),
            char.IsAsciiDigit(bad) && radix != 10
                ? $"'{bad}' is not a valid base-{radix} digit"
                : $"'{TextBetween(start, _position)}' is not a valid numeric literal; "
                + "suffixes are not supported, so write a type annotation or 'as'"
        );
    }

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanRadixOrHexFloat(int start, int radix) {
        _position += 2; // 0x
        var digitsStart = _position;
        ScanDigitRun(radix);
        var intDigits = TextBetween(digitsStart, _position);

        var isFloat = false;
        var fracDigits = string.Empty;

        if (Current == '.' && IsRadixDigit(Next, 16)) {
            isFloat = true;
            _position++;
            var fracStart = _position;
            ScanDigitRun(16);
            fracDigits = TextBetween(fracStart, _position);
        }

        if (Current is 'p' or 'P') {
            isFloat = true;
            _position++;
            if (Current is '+' or '-') {
                _position++;
            }

            var expStart = _position;
            ScanDigitRun(10);
            if (_position == expStart) {
                _diagnostics.Error(
                    ErrorCode.ERR_HexFloatNeedsExponent,
                    new(start, _position - start),
                    "hexadecimal floating-point literal requires digits after 'p'"
                );
            }
        } else if (isFloat) {
            _diagnostics.Error(
                ErrorCode.ERR_HexFloatNeedsExponent,
                new(start, _position - start),
                "hexadecimal floating-point literal requires a binary exponent, e.g. '0x1.8p3'"
            );
        }

        var span = new TextSpan(start, _position - start);
        var text = TextBetween(start, _position);
        CheckUnderscores(text, span);

        if (intDigits.Length == 0 && fracDigits.Length == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_NumericLiteralNoDigits,
                span,
                "expected at least one hexadecimal digit after '0x'"
            );
            return (SyntaxKind.IntegerLiteralToken, 0UL, default);
        }

        if (isFloat) {
            return (SyntaxKind.FloatLiteralToken, ParseHexFloat(text, span), default);
        }

        return (SyntaxKind.IntegerLiteralToken, ParseRadix(intDigits, 16, span), default);
    }

    void ScanDigitRun(int radix) {
        while (!AtEnd && (Current == '_' || IsRadixDigit(Current, radix))) {
            _position++;
        }
    }

    static bool IsRadixDigit(char c, int radix) =>
        radix switch {
            2 => c is '0' or '1',
            8 => c is >= '0' and <= '7',
            10 => char.IsAsciiDigit(c),
            16 => char.IsAsciiHexDigit(c),
            _ => false,
        };

    void CheckUnderscores(string text, TextSpan span) {
        if (text.EndsWith('_')) {
            _diagnostics.Error(
                ErrorCode.ERR_TrailingUnderscoreInNumber,
                span,
                "numeric literal may not end with '_'"
            );
        }

        // "Underscores are separators and may not lead" (spec/lexical.md §6).
        // A decimal literal cannot start with one — that is an identifier —
        // but '0x_1' can, and the digits after a '.' or an 'e' can too.
        if (LeadsWithUnderscore(text)) {
            _diagnostics.Error(
                ErrorCode.ERR_LeadingUnderscoreInNumber,
                span,
                "'_' is a separator in a numeric literal, so it may not lead a digit run"
            );
        }
    }

    static bool LeadsWithUnderscore(string text) {
        // '0x', '0o' and '0b' are the literal's prefix, not a digit run.
        var start = text.Length > 1 && text[0] == '0'
            && text[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B'
                ? 2
                : 0;

        for (var i = start; i < text.Length; i++) {
            if (text[i] != '_') {
                continue;
            }

            // An underscore leads when what precedes it is not a digit: the
            // start of the literal, or a '.', 'e' or prefix just before it.
            if (i == start || !char.IsAsciiHexDigit(text[i - 1])) {
                return true;
            }
        }

        return false;
    }

    object ParseRadix(string digits, int radix, TextSpan span) {
        UInt128 value = 0;
        foreach (var c in digits) {
            if (c == '_') {
                continue;
            }

            if (!IsRadixDigit(c, radix)) {
                _diagnostics.Error(
                    ErrorCode.ERR_InvalidDigitForBase,
                    span,
                    $"'{c}' is not a valid base-{radix} digit"
                );
                return 0UL;
            }

            var digit = (UInt128)Convert.ToInt32(c.ToString(), 16);
            if (value > (UInt128.MaxValue - digit) / (UInt128)radix) {
                _diagnostics.Error(
                    ErrorCode.ERR_IntegerLiteralOverflow,
                    span,
                    "integer literal does not fit in 128 bits"
                );
                return 0UL;
            }

            value = value * (UInt128)radix + digit;
        }

        return value <= ulong.MaxValue ? (object)(ulong)value : value;
    }

    double ParseHexFloat(string text, TextSpan span) {
        var s = text.Replace("_", string.Empty, StringComparison.Ordinal)[2..]; // drop 0x
        var pIndex = s.IndexOfAny(['p', 'P']);
        var exponent = 0;
        if (pIndex >= 0) {
            var expText = s[(pIndex + 1)..];
            if (!int.TryParse(expText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) {
                _diagnostics.Error(
                    ErrorCode.ERR_InvalidFloatLiteral,
                    span,
                    $"'{text}' has an invalid binary exponent"
                );
                return 0;
            }

            s = s[..pIndex];
        }

        var dotIndex = s.IndexOf('.', StringComparison.Ordinal);
        var fracDigits = 0;
        if (dotIndex >= 0) {
            fracDigits = s.Length - dotIndex - 1;
            s = s.Remove(dotIndex, 1);
        }

        double mantissa = 0;
        foreach (var c in s) {
            if (!char.IsAsciiHexDigit(c)) {
                _diagnostics.Error(
                    ErrorCode.ERR_InvalidFloatLiteral,
                    span,
                    $"'{text}' is not a valid hexadecimal floating-point literal"
                );
                return 0;
            }

            mantissa = mantissa * 16 + Convert.ToInt32(c.ToString(), 16);
        }

        return mantissa * Math.Pow(2, exponent - 4 * fracDigits);
    }

    // -----------------------------------------------------------------------
    // Character literals
    // -----------------------------------------------------------------------

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanCharLiteral() {
        var start = _position;
        _position++; // opening quote

        var builder = new StringBuilder();
        while (!AtEnd && Current != '\'' && Current is not ('\n' or '\r')) {
            if (Current == '\\') {
                ScanEscape(builder);
            } else {
                builder.Append(Current);
                _position++;
            }
        }

        if (Current != '\'') {
            _diagnostics.Error(
                ErrorCode.ERR_UnterminatedChar,
                new(start, _position - start),
                "unterminated character literal"
            );
            return (SyntaxKind.CharLiteralToken, null, default);
        }

        _position++; // closing quote

        var span = new TextSpan(start, _position - start);
        var content = builder.ToString();

        if (content.Length == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_EmptyCharLiteral,
                span,
                "character literal cannot be empty"
            );
            return (SyntaxKind.CharLiteralToken, null, default);
        }

        var runes = content.EnumerateRunes();
        Rune? first = null;
        var count = 0;
        foreach (var r in runes) {
            first ??= r;
            count++;
        }

        if (count > 1) {
            _diagnostics.Error(
                ErrorCode.ERR_CharLiteralTooLong,
                span,
                $"character literal must hold exactly one Unicode scalar value, but holds {count}"
            );
        }

        return (SyntaxKind.CharLiteralToken, first, default);
    }

    /// <summary>Consumes one escape sequence and appends its cooked value.</summary>
    void ScanEscape(StringBuilder builder) {
        var start = _position;
        _position++; // backslash

        // A backslash at end of file has nothing to escape.
        if (AtEnd) {
            _diagnostics.Error(
                ErrorCode.ERR_InvalidEscapeSequence,
                new(start, 1),
                "a backslash must be followed by an escape character"
            );
            return;
        }

        switch (Current) {
            case '0':
                builder.Append('\0');
                _position++;
                return;
            case '\\':
                builder.Append('\\');
                _position++;
                return;
            case 't':
                builder.Append('\t');
                _position++;
                return;
            case 'n':
                builder.Append('\n');
                _position++;
                return;
            case 'r':
                builder.Append('\r');
                _position++;
                return;
            case '"':
                builder.Append('"');
                _position++;
                return;
            case '\'':
                builder.Append('\'');
                _position++;
                return;
            case 'u':
                ScanUnicodeEscape(builder, start);
                return;
            default:
                _diagnostics.Error(
                    ErrorCode.ERR_InvalidEscapeSequence,
                    new(start, 2),
                    $"unrecognised escape sequence '\\{Current}'"
                );
                builder.Append(Current);
                _position++;
                return;
        }
    }

    void ScanUnicodeEscape(StringBuilder builder, int start) {
        _position++; // 'u'
        if (Current != '{') {
            _diagnostics.Error(
                ErrorCode.ERR_InvalidUnicodeEscape,
                new(start, 2),
                """unicode escape must be written '\u{...}'"""
            );
            return;
        }

        _position++; // '{'

        var digitsStart = _position;
        while (!AtEnd && char.IsAsciiHexDigit(Current)) {
            _position++;
        }

        var digits = TextBetween(digitsStart, _position);

        if (Current != '}') {
            _diagnostics.Error(
                ErrorCode.ERR_InvalidUnicodeEscape,
                new(start, _position - start),
                "unterminated unicode escape; expected '}'"
            );
            return;
        }

        _position++; // '}'

        var span = new TextSpan(start, _position - start);
        if (digits.Length is 0 or > 6) {
            _diagnostics.Error(
                ErrorCode.ERR_InvalidUnicodeEscape,
                span,
                "unicode escape requires between 1 and 6 hexadecimal digits"
            );
            return;
        }

        var scalar = int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (!Rune.IsValid(scalar)) {
            _diagnostics.Error(
                ErrorCode.ERR_UnicodeScalarOutOfRange,
                span,
                $"U+{scalar:X} is not a valid Unicode scalar value"
            );
            return;
        }

        builder.Append(new Rune(scalar).ToString());
    }

    // -----------------------------------------------------------------------
    // String literals
    // -----------------------------------------------------------------------

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanStringLiteral() =>
        Matches("\"\"\"") ? ScanMultilineString() : ScanSimpleString();

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanSimpleString() {
        var start = _position;
        _position++; // opening quote

        var parts = ImmutableArray.CreateBuilder<StringPart>();
        var builder = new StringBuilder();
        var rawStart = _position;

        while (true) {
            if (AtEnd || Current is '\n' or '\r') {
                _diagnostics.Error(
                    ErrorCode.ERR_UnterminatedString,
                    new(start, _position - start),
                    """"unterminated string literal; use """ for a multi-line string""""
                );
                break;
            }

            if (Current == '"') {
                FlushLiteral(parts, builder, rawStart);
                _position++;
                break;
            }

            if (Current == '\\' && Next == '(') {
                FlushLiteral(parts, builder, rawStart);
                ScanInterpolation(parts);
                rawStart = _position;
                continue;
            }

            if (Current == '\\') {
                ScanEscape(builder);
                continue;
            }

            builder.Append(Current);
            _position++;
        }

        FlushLiteral(parts, builder, rawStart);
        return Finish(SyntaxKind.StringLiteralToken, parts);
    }

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanMultilineString() {
        var start = _position;
        _position += 3; // opening """

        // The opening delimiter must be followed by a line break.
        var afterOpen = _position;
        while (Current is ' ' or '\t') {
            _position++;
        }

        if (Current is '\n' or '\r') {
            if (Current == '\r' && Next == '\n') {
                _position += 2;
            } else {
                _position++;
            }
        } else {
            _diagnostics.Error(
                ErrorCode.ERR_MultilineDelimiterMustBeOnOwnLine,
                new(start, 3),
                "a multi-line string must begin on the line after its opening \"\"\""
            );
            _position = afterOpen;
        }

        var parts = ImmutableArray.CreateBuilder<StringPart>();
        var builder = new StringBuilder();
        var terminated = false;
        var closingIndent = 0;
        var rawStart = _position;

        // Where each line of source starts in the value: the part it falls
        // in and the offset there. Dedenting works from these rather than
        // from the value's '\n's, which an escape can write and a joined
        // line has none of.
        var lineStarts = new List<(int Part, int Offset)> { (0, 0) };

        while (!AtEnd) {
            // A closing delimiter is the first non-whitespace text on its line.
            if (Matches("\"\"\"") && IsAtLineStartIgnoringWhitespace(_position, out closingIndent)) {
                FlushLiteral(parts, builder, rawStart);
                _position += 3;
                terminated = true;
                break;
            }

            if (Current == '\\' && Next == '(') {
                FlushLiteral(parts, builder, rawStart);
                ScanInterpolation(parts);
                rawStart = _position;
                continue;
            }

            if (Current == '\\') {
                // A backslash immediately before a line break joins the lines.
                if (Next is '\n' or '\r') {
                    _position++;
                    if (Current == '\r' && Next == '\n') {
                        _position += 2;
                    } else {
                        _position++;
                    }

                    // The joined line is still a line of the literal: it
                    // loses the closing delimiter's indentation like the rest.
                    lineStarts.Add((parts.Count, builder.Length));
                    continue;
                }

                ScanEscape(builder);
                continue;
            }

            builder.Append(Current);
            _position++;
            if (builder[^1] == '\n') {
                lineStarts.Add((parts.Count, builder.Length));
            }
        }

        if (!terminated) {
            _diagnostics.Error(
                ErrorCode.ERR_UnterminatedString,
                new(start, 3),
                "unterminated multi-line string literal"
            );
            FlushLiteral(parts, builder, rawStart);
        }

        var dedented = Dedent(parts, lineStarts, closingIndent, new(start, _position - start));
        return Finish(SyntaxKind.MultilineStringLiteralToken, dedented);
    }

    /// <summary>
    ///     True when only whitespace separates <paramref name="position" /> from the
    ///     start of its line; reports that whitespace width as the indentation.
    /// </summary>
    bool IsAtLineStartIgnoringWhitespace(int position, out int indent) {
        indent = 0;
        var i = position - 1;
        while (i >= 0 && _source[i] is ' ' or '\t') {
            i--;
            indent++;
        }

        return i < 0 || _source[i] is '\n' or '\r';
    }

    /// <summary>
    ///     Strips the closing delimiter's indentation from every line, and removes
    ///     the final line break that precedes the closing delimiter.
    /// </summary>
    /// <param name="lineStarts">Where each line of source starts: a part, and an offset in its text.</param>
    ImmutableArray<StringPart>.Builder Dedent(
        ImmutableArray<StringPart>.Builder parts,
        List<(int Part, int Offset)> lineStarts,
        int indent,
        TextSpan span
    ) {
        if (parts.Count == 0) {
            return parts;
        }

        // Last line first, so that cutting one leaves the offsets of the lines
        // before it where they were. The closing delimiter's own line loses
        // its indentation here too, leaving only the line break before it.
        for (var s = lineStarts.Count - 1; s >= 0 && indent > 0; s--) {
            var (index, offset) = lineStarts[s];
            if (index >= parts.Count) {
                continue;
            }

            var next = s + 1 < lineStarts.Count ? lineStarts[s + 1] : (Part: parts.Count, Offset: 0);
            var blank = IsBlankLine(parts, (index, offset), next);

            // A line that opens with an interpolation has no indentation.
            var text = parts[index].Kind == StringPartKind.Literal ? parts[index].Text : "";
            var removed = 0;
            while (removed < indent && offset + removed < text.Length && text[offset + removed] is ' ' or '\t') {
                removed++;
            }

            if (removed < indent && !blank) {
                _diagnostics.Error(
                    ErrorCode.ERR_InsufficientIndentation,
                    span,
                    $"line is indented less than the closing delimiter (expected {indent} columns)"
                );
            }

            if (removed > 0) {
                parts[index] = parts[index] with { Text = text.Remove(offset, removed) };
            }
        }

        // Drop the line break and indentation immediately before the delimiter.
        var last = parts[^1];
        if (last.Kind == StringPartKind.Literal) {
            var t = last.Text;
            var cut = t.Length;
            var seen = 0;
            while (cut > 0 && t[cut - 1] is ' ' or '\t' && seen < indent) {
                cut--;
                seen++;
            }

            if (cut > 0 && t[cut - 1] == '\n') {
                cut--;
                if (cut > 0 && t[cut - 1] == '\r') {
                    cut--;
                }
            }

            parts[^1] = last with { Text = t[..cut] };
        }

        return parts;
    }

    /// <summary>
    ///     Whether the value between two line starts is only whitespace: no
    ///     interpolation, and nothing else, so it need not be indented as far
    ///     as the closing delimiter.
    /// </summary>
    static bool IsBlankLine(
        ImmutableArray<StringPart>.Builder parts,
        (int Part, int Offset) from,
        (int Part, int Offset) to
    ) {
        for (var p = from.Part; p <= to.Part && p < parts.Count; p++) {
            var start = p == from.Part ? from.Offset : 0;
            var end = p == to.Part ? to.Offset : parts[p].Text.Length;
            if (start >= end && p == to.Part) {
                break;
            }

            if (parts[p].Kind != StringPartKind.Literal) {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(parts[p].Text[start..end])) {
                return false;
            }
        }

        return true;
    }

    (SyntaxKind, object?, ImmutableArray<StringPart>) ScanRawString() {
        var start = _position;
        var hashes = 0;
        while (Current == '#') {
            hashes++;
            _position++;
        }

        var isMultiline = Matches("\"\"\"");
        var openLength = isMultiline ? 3 : 1;
        _position += openLength;

        var closing = new string('"', openLength) + new string('#', hashes);
        var contentStart = _position;

        while (!AtEnd && !Matches(closing)) {
            if (!isMultiline && Current is '\n' or '\r') {
                break;
            }

            _position++;
        }

        if (!Matches(closing)) {
            _diagnostics.Error(
                ErrorCode.ERR_UnterminatedRawString,
                new(start, _position - start),
                $"unterminated raw string literal; expected '{closing}'"
            );
            var unterminated = TextBetween(contentStart, _position);
            return (SyntaxKind.RawStringLiteralToken, unterminated, default);
        }

        var content = TextBetween(contentStart, _position);
        _position += closing.Length;

        return (SyntaxKind.RawStringLiteralToken, content, default);
    }

    /// <summary>
    ///     Captures a <c>\(expr)</c> hole. The inner source is recorded verbatim and
    ///     re-entered by the parser; nested strings and parentheses are skipped here
    ///     only well enough to find the matching close paren.
    /// </summary>
    void ScanInterpolation(ImmutableArray<StringPart>.Builder parts) {
        var start = _position;
        _position += 2; // '\' '('
        var innerStart = _position;

        var depth = 1;
        while (!AtEnd && depth > 0) {
            var c = Current;
            if (c == '(') {
                depth++;
                _position++;
            } else if (c == ')') {
                depth--;
                _position++;
            } else if (c == '"') {
                SkipNestedString();
            } else if (c == '\'') {
                SkipNestedChar();
            } else if (c is '\n' or '\r') {
                break;
            } else {
                _position++;
            }
        }

        if (depth != 0) {
            _diagnostics.Error(
                ErrorCode.ERR_UnterminatedInterpolation,
                new(start, Math.Max(2, _position - start)),
                "unterminated string interpolation; expected ')'"
            );

            // Still a part: without one the '\(' and what follows it would
            // belong to nothing, and the token's text would not tile.
            parts.Add(
                new(
                    StringPartKind.Interpolation,
                    TextBetween(innerStart, _position),
                    TextSpan.FromBounds(start - _tokenStart, _position - _tokenStart)
                )
            );
            return;
        }

        var innerEnd = _position - 1; // exclude the closing paren
        parts.Add(
            new(
                StringPartKind.Interpolation,
                TextBetween(innerStart, innerEnd),
                // The whole hole, '\(' and ')' included, so that the parts tile
                // the token's text and the parser can rebuild it exactly.
                TextSpan.FromBounds(start - _tokenStart, _position - _tokenStart)
            )
        );
    }

    void SkipNestedString() {
        if (Matches("\"\"\"")) {
            _position += 3;
            while (!AtEnd && !Matches("\"\"\"")) {
                _position++;
            }

            if (Matches("\"\"\"")) {
                _position += 3;
            }

            return;
        }

        _position++; // opening quote
        while (!AtEnd && Current != '"' && Current is not ('\n' or '\r')) {
            _position += Current == '\\' ? 2 : 1;
        }

        _position = Math.Min(_position, _source.Length);
        if (!AtEnd && Current == '"') {
            _position++;
        }
    }

    void SkipNestedChar() {
        _position++;
        while (!AtEnd && Current != '\'' && Current is not ('\n' or '\r')) {
            _position += Current == '\\' ? 2 : 1;
        }

        _position = Math.Min(_position, _source.Length);
        if (!AtEnd && Current == '\'') {
            _position++;
        }
    }

    /// <summary>
    ///     Ends a run of literal text. The raw span is recorded as well as the
    ///     cooked text, because the parser rebuilds an interpolated string from
    ///     the original characters and the pieces have to tile the token exactly.
    /// </summary>
    void FlushLiteral(
        ImmutableArray<StringPart>.Builder parts,
        StringBuilder builder,
        int rawStart
    ) {
        if (builder.Length == 0) {
            return;
        }

        parts.Add(
            new(
                StringPartKind.Literal,
                builder.ToString(),
                TextSpan.FromBounds(rawStart - _tokenStart, _position - _tokenStart)
            )
        );

        builder.Clear();
    }

    static (SyntaxKind, object?, ImmutableArray<StringPart>) Finish(
        SyntaxKind kind,
        ImmutableArray<StringPart>.Builder parts
    ) {
        var array = parts.ToImmutable();
        var hasHole = false;
        foreach (var p in array) {
            if (p.Kind == StringPartKind.Interpolation) {
                hasHole = true;
                break;
            }
        }

        // A string with no holes carries its cooked value directly.
        object? value = null;
        if (!hasHole) {
            value = array.Length switch {
                0 => string.Empty,
                1 => array[0].Text,
                _ => string.Concat(array.Select(static p => p.Text)),
            };
        }

        return (kind, value, array);
    }

    // -----------------------------------------------------------------------
    // Punctuation
    // -----------------------------------------------------------------------

    SyntaxKind ScanPunctuation() {
        var c = Current;
        _position++;

        switch (c) {
            case '(':
                _bracketDepth++;
                return SyntaxKind.OpenParenToken;
            case ')':
                if (_bracketDepth > 0) {
                    _bracketDepth--;
                }

                return SyntaxKind.CloseParenToken;
            case '[':
                _bracketDepth++;
                return SyntaxKind.OpenBracketToken;
            case ']':
                if (_bracketDepth > 0) {
                    _bracketDepth--;
                }

                return SyntaxKind.CloseBracketToken;
            case '{': return SyntaxKind.OpenBraceToken;
            case '}': return SyntaxKind.CloseBraceToken;
            case ',': return SyntaxKind.CommaToken;
            case ';': return SyntaxKind.SemicolonToken;
            case ':': return SyntaxKind.ColonToken;
            case '@': return SyntaxKind.AtToken;
            case '#': return SyntaxKind.HashToken;
            case '~': return SyntaxKind.TildeToken;
            case '\\': return SyntaxKind.BackslashToken;
            case '$': return SyntaxKind.DollarToken;

            case '.':
                if (Current == '.' && Next == '.') {
                    _position += 2;
                    return SyntaxKind.DotDotDotToken;
                }

                if (Current == '.' && Next == '<') {
                    _position += 2;
                    return SyntaxKind.DotDotLessToken;
                }

                if (Current == '.') {
                    _position++;
                    return SyntaxKind.DotDotToken;
                }

                return SyntaxKind.DotToken;

            case '?':
                if (Current == '.') {
                    _position++;
                    return SyntaxKind.QuestionDotToken;
                }

                if (Current == '?' && Next == '=') {
                    _position += 2;
                    return SyntaxKind.QuestionQuestionEqualsToken;
                }

                if (Current == '?') {
                    _position++;
                    return SyntaxKind.QuestionQuestionToken;
                }

                return SyntaxKind.QuestionToken;

            case '+':
                if (Current == '=') {
                    _position++;
                    return SyntaxKind.PlusEqualsToken;
                }

                return SyntaxKind.PlusToken;

            case '-':
                if (Current == '>') {
                    _position++;
                    return SyntaxKind.ArrowToken;
                }

                if (Current == '=') {
                    _position++;
                    return SyntaxKind.MinusEqualsToken;
                }

                return SyntaxKind.MinusToken;

            case '*':
                if (Current == '=') {
                    _position++;
                    return SyntaxKind.StarEqualsToken;
                }

                return SyntaxKind.StarToken;

            case '/':
                if (Current == '=') {
                    _position++;
                    return SyntaxKind.SlashEqualsToken;
                }

                return SyntaxKind.SlashToken;

            case '%':
                if (Current == '=') {
                    _position++;
                    return SyntaxKind.PercentEqualsToken;
                }

                return SyntaxKind.PercentToken;

            case '^':
                if (Current == '=') {
                    _position++;
                    return SyntaxKind.CaretEqualsToken;
                }

                return SyntaxKind.CaretToken;

            case '&':
                if (Current == '&') {
                    _position++;
                    return SyntaxKind.AmpersandAmpersandToken;
                }

                // '&+', '&-' and '&*': the wrapping operators, and their
                // compound assignments.
                if (Current is '+' or '-' or '*') {
                    var wrapping = Current;
                    _position++;
                    if (Current == '=') {
                        _position++;
                        return wrapping switch {
                            '+' => SyntaxKind.AmpersandPlusEqualsToken,
                            '-' => SyntaxKind.AmpersandMinusEqualsToken,
                            _ => SyntaxKind.AmpersandStarEqualsToken,
                        };
                    }

                    return wrapping switch {
                        '+' => SyntaxKind.AmpersandPlusToken,
                        '-' => SyntaxKind.AmpersandMinusToken,
                        _ => SyntaxKind.AmpersandStarToken,
                    };
                }

                if (Current == '=') {
                    _position++;
                    return SyntaxKind.AmpersandEqualsToken;
                }

                return SyntaxKind.AmpersandToken;

            case '|':
                if (Current == '|') {
                    _position++;
                    return SyntaxKind.PipePipeToken;
                }

                if (Current == '=') {
                    _position++;
                    return SyntaxKind.PipeEqualsToken;
                }

                return SyntaxKind.PipeToken;

            case '=':
                if (Current == '=' && Next == '=') {
                    _position += 2;
                    return SyntaxKind.EqualsEqualsEqualsToken;
                }

                if (Current == '=') {
                    _position++;
                    return SyntaxKind.EqualsEqualsToken;
                }

                if (Current == '>') {
                    _position++;
                    return SyntaxKind.FatArrowToken;
                }

                return SyntaxKind.EqualsToken;

            case '!':
                if (Current == '=' && Next == '=') {
                    _position += 2;
                    return SyntaxKind.BangEqualsEqualsToken;
                }

                if (Current == '=') {
                    _position++;
                    return SyntaxKind.BangEqualsToken;
                }

                return SyntaxKind.BangToken;

            case '<':
                if (Current == '<' && Next == '=') {
                    _position += 2;
                    return SyntaxKind.LessLessEqualsToken;
                }

                if (Current == '<') {
                    _position++;
                    return SyntaxKind.LessLessToken;
                }

                if (Current == '=') {
                    _position++;
                    return SyntaxKind.LessEqualsToken;
                }

                return SyntaxKind.LessToken;

            case '>':
                // '>>' is produced here, but the parser may split it when closing
                // nested generic argument lists, exactly as Roslyn does.
                if (Current == '>' && Next == '=') {
                    _position += 2;
                    return SyntaxKind.GreaterGreaterEqualsToken;
                }

                if (Current == '>') {
                    _position++;
                    return SyntaxKind.GreaterGreaterToken;
                }

                if (Current == '=') {
                    _position++;
                    return SyntaxKind.GreaterEqualsToken;
                }

                return SyntaxKind.GreaterToken;

            default:
                _diagnostics.Error(
                    ErrorCode.ERR_UnexpectedCharacter,
                    new(_position - 1, 1),
                    $"unexpected character '{c}' (U+{(int)c:X4})"
                );
                return SyntaxKind.BadToken;
        }
    }

    // -----------------------------------------------------------------------
    // Statement terminators — spec/lexical.md §4
    // -----------------------------------------------------------------------

    static void MarkStatementTerminators(Span<GreenToken> tokens) {
        // Rule 3 counts unbalanced ( and [ — but a '{' inside them opens a
        // body of statements, as in 'xs.map({ x in … })', where newlines end
        // statements again. So each brace starts a fresh count, and its '}'
        // puts the outer one back.
        var depth = 0;
        var outer = new Stack<int>();

        // Every token is assigned, not just the ones that terminate: an
        // incremental lex copies old tokens in, and a flag that was true
        // before the edit may have to become false.
        for (var i = 0; i < tokens.Length; i++) {
            var token = tokens[i];
            tokens[i] = token.WithTerminatesStatement(Terminates(tokens, i, ref depth, outer));
        }

        if (tokens.Length > 0) {
            tokens[^1] = tokens[^1].WithTerminatesStatement(true);
        }
    }

    static bool Terminates(Span<GreenToken> tokens, int i, ref int depth, Stack<int> outer) {
        var token = tokens[i];

        switch (token.Kind) {
            case SyntaxKind.OpenParenToken:
            case SyntaxKind.OpenBracketToken:
                depth++;
                break;
            case SyntaxKind.CloseParenToken:
            case SyntaxKind.CloseBracketToken:
                if (depth > 0) {
                    depth--;
                }

                break;
            case SyntaxKind.OpenBraceToken:
                outer.Push(depth);
                depth = 0;
                break;
            case SyntaxKind.CloseBraceToken:
                depth = outer.Count > 0 ? outer.Pop() : 0;
                break;
            case SyntaxKind.SemicolonToken:
                return true;
        }

        if (i + 1 >= tokens.Length) {
            return false;
        }

        var next = tokens[i + 1];
        var separatedByNewLine = HasNewLine(token.Trailing) || HasNewLine(next.Leading);
        if (!separatedByNewLine) {
            return false;
        }

        // Rule 3: inside unbalanced ( or [, newlines never terminate.
        if (depth > 0) {
            return false;
        }

        // Jump keywords terminate unconditionally, guarding against the
        // JavaScript automatic-semicolon-insertion hazard.
        if (SyntaxFacts.AlwaysTerminates(token.Kind)) {
            return true;
        }

        // Rule 1: the statement must be able to end here.
        if (!SyntaxFacts.CanEndStatement(token.Kind)) {
            return false;
        }

        // Rule 2: the next line must not open with a continuation token.
        // 'while' also starts a statement of its own, so it continues
        // only the 'repeat { }' its '}' closes.
        return !(SyntaxFacts.ContinuesStatement(next.Kind)
            && (next.Kind != SyntaxKind.WhileKeyword || token.Kind == SyntaxKind.CloseBraceToken));
    }

    static bool HasNewLine(ImmutableArray<GreenTrivia> trivia) {
        foreach (var t in trivia) {
            if (t.Kind == SyntaxKind.NewLineTrivia) {
                return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Conditional compilation — spec/lexical.md §9
    // -----------------------------------------------------------------------

    /// <summary>The state of one open <c>#if</c>.</summary>
    private enum BranchState {
        /// <summary>This branch's tokens are being lexed.</summary>
        Taking,

        /// <summary>This branch is inactive but a later one may still be taken.</summary>
        Skipping,

        /// <summary>A branch was already taken, so every later one is inactive.</summary>
        Done
    }

    readonly List<BranchState> _conditionals = [];

    /// <summary>True when no enclosing conditional is excluding this text.</summary>
    bool IsActive {
        get {
            foreach (var state in _conditionals) {
                if (state != BranchState.Taking) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>True when every conditional below the innermost one is taking.</summary>
    bool IsEnclosingActive {
        get {
            for (var i = 0; i < _conditionals.Count - 1; i++) {
                if (_conditionals[i] != BranchState.Taking) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>True when only whitespace separates the position from its line start.</summary>
    bool IsAtLineStart(int position) {
        var i = position - 1;
        while (i >= 0 && _source[i] is ' ' or '\t') {
            i--;
        }

        return i < 0 || _source[i] is '\n' or '\r';
    }

    bool IsDirectiveAhead() {
        if (Current != '#' || !IsAtLineStart(_position)) {
            return false;
        }

        var i = _position + 1;
        var start = i;
        while (i < _source.Length && SyntaxFacts.IsIdentifierContinue(_source[i])) {
            i++;
        }

        // Per spec/lexical.md §9 a directive is first on its line, and the
        // name does not have to be one we know: an unrecognised directive is
        // reported as one rather than falling through to the parser as a
        // stray '#'. The builtins are the exception, so that an expression
        // may still begin a line with '#file'.
        var name = TextBetween(start, i);
        return name.Length > 0
            && name is not
            ("file" or "line" or "column" or "function" or "module");
    }

    /// <summary>Consumes a directive line and applies it to the conditional stack.</summary>
    void ScanDirective(List<GreenTrivia> builder) {
        var start = _position;
        _position++; // '#'

        var nameStart = _position;
        while (!AtEnd && SyntaxFacts.IsIdentifierContinue(Current)) {
            _position++;
        }

        var name = TextBetween(nameStart, _position);

        var argumentStart = _position;
        while (!AtEnd && Current is not ('\n' or '\r')) {
            _position++;
        }

        var argument = TextBetween(argumentStart, _position).Trim();

        var span = new TextSpan(start, _position - start);
        ApplyDirective(name, argument, span);

        _sawDirective = true;
        builder.Add(Trivia(SyntaxKind.DirectiveTrivia, start));
    }

    void ApplyDirective(string name, string argument, TextSpan span) {
        switch (name) {
            case "if":
                _conditionals.Add(BranchState.Skipping); // placeholder for nesting
                _conditionals[^1] = IsEnclosingActive
                    ? EvaluateCondition(argument, span) ? BranchState.Taking : BranchState.Skipping
                    : BranchState.Done;
                return;

            case "elseif":
                if (!RequireOpenConditional("#elseif", span)) {
                    return;
                }

                _conditionals[^1] = _conditionals[^1] switch {
                    BranchState.Taking => BranchState.Done,
                    BranchState.Done => BranchState.Done,
                    _ => IsEnclosingActive && EvaluateCondition(argument, span)
                        ? BranchState.Taking
                        : BranchState.Skipping,
                };
                return;

            case "else":
                if (!RequireOpenConditional("#else", span)) {
                    return;
                }

                _conditionals[^1] = _conditionals[^1] switch {
                    BranchState.Skipping when IsEnclosingActive => BranchState.Taking,

                    _ => BranchState.Done,
                };
                return;

            case "endif":
                if (!RequireOpenConditional("#endif", span)) {
                    return;
                }

                _conditionals.RemoveAt(_conditionals.Count - 1);
                return;

            case "error":
                if (IsActive) {
                    _diagnostics.Error(ErrorCode.ERR_UserError, span, DirectiveMessage(argument));
                }

                return;

            case "warning":
                if (IsActive) {
                    _diagnostics.Add(Diagnostic.Warning(ErrorCode.WRN_UserWarning, span, DirectiveMessage(argument)));
                }

                return;

            default:
                _diagnostics.Error(
                    ErrorCode.ERR_UnknownDirective,
                    span,
                    $"unknown directive '#{name}'"
                );
                return;
        }
    }

    bool RequireOpenConditional(string directive, TextSpan span) {
        if (_conditionals.Count > 0) {
            return true;
        }

        _diagnostics.Error(
            ErrorCode.ERR_UnmatchedDirective,
            span,
            $"'{directive}' has no matching '#if'"
        );
        return false;
    }

    bool EvaluateCondition(string condition, TextSpan span) {
        if (condition.Length == 0) {
            _diagnostics.Error(
                ErrorCode.ERR_BadDirectiveCondition,
                span,
                "expected a condition after the directive"
            );
            return false;
        }

        var value = DirectiveCondition.Evaluate(condition, _options, out var error);
        if (error is not null) {
            _diagnostics.Error(ErrorCode.ERR_BadDirectiveCondition, span, error);
        }

        return value;
    }

    /// <summary>Unwraps the <c>("...")</c> of a <c>#error</c> or <c>#warning</c>.</summary>
    static string DirectiveMessage(string argument) {
        var text = argument.Trim();
        if (text.StartsWith('(') && text.EndsWith(')')) {
            text = text[1..^1].Trim();
        }

        if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"')) {
            text = text[1..^1];
        }

        return text.Length == 0 ? "(no message)" : text;
    }

    /// <summary>Consumes one line of an inactive region as disabled text.</summary>
    void ScanDisabledLine() {
        while (!AtEnd && Current is not ('\n' or '\r')) {
            _position++;
        }

        if (AtEnd) {
            return;
        }

        if (Current == '\r' && Next == '\n') {
            _position += 2;
        } else {
            _position++;
        }
    }
}
