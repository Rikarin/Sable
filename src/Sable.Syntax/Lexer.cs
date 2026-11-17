using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>
/// Converts source text into a token stream with full-fidelity trivia.
/// </summary>
/// <remarks>
/// <para>Implements spec/lexical.md. The round-trip invariant — concatenating
/// <see cref="SyntaxToken.ToFullString"/> over every token reproduces the source
/// exactly — is enforced by the test suite and must never be broken.</para>
/// <para>Conditional-compilation directives (<c>#if</c> and friends) are lexed
/// as <c>#</c> plus an identifier here. Turning inactive regions into
/// <see cref="SyntaxKind.DisabledTextTrivia"/> is a separate pass that runs
/// after the parser milestone.</para>
/// </remarks>
public sealed class Lexer
{
    private readonly SourceText _source;
    private readonly ParseOptions _options;
    private readonly DiagnosticBag _diagnostics = new();
    private int _position;

    /// <summary>Nesting depth of <c>(</c> and <c>[</c>; suppresses terminators.</summary>
    private int _bracketDepth;

    /// <summary>Start of the current token's text, for relative part spans.</summary>
    private int _tokenStart;

    private Lexer(SourceText source, ParseOptions options)
    {
        _source = source;
        _options = options;
    }

    public static ImmutableArray<SyntaxToken> Tokenize(
        SourceText source,
        out IReadOnlyList<Diagnostic> diagnostics)
        => Tokenize(source, ParseOptions.Default, out diagnostics);

    public static ImmutableArray<SyntaxToken> Tokenize(
        SourceText source,
        ParseOptions options,
        out IReadOnlyList<Diagnostic> diagnostics)
    {
        var lexer = new Lexer(source, options);
        var tokens = lexer.LexAll();
        diagnostics = lexer._diagnostics.ToList();
        return tokens;
    }

    public static ImmutableArray<SyntaxToken> Tokenize(string text, string fileName = "<test>") =>
        Tokenize(new SourceText(text, fileName), out _);

    // -----------------------------------------------------------------------

    private char Current => Peek(0);
    private char Next => Peek(1);
    private bool AtEnd => _position >= _source.Length;

    private char Peek(int offset)
    {
        var index = _position + offset;
        return index < _source.Length ? _source[index] : '\0';
    }

    private bool Matches(string text)
    {
        if (_position + text.Length > _source.Length) return false;
        for (var i = 0; i < text.Length; i++)
            if (_source[_position + i] != text[i]) return false;
        return true;
    }

    private string TextBetween(int start, int end) =>
        _source.Text[start..end];

    // -----------------------------------------------------------------------

    private ImmutableArray<SyntaxToken> LexAll()
    {
        if (_source.Length > 0 && _source[0] == '﻿')
        {
            _diagnostics.Error(ErrorCode.ERR_ByteOrderMarkNotAllowed, new TextSpan(0, 1),
                "byte order mark is not allowed; save the file as UTF-8 without a BOM");
            _position = 1;
        }

        var green = new List<GreenToken>();
        while (true)
        {
            var token = LexToken();
            green.Add(token);
            if (token.Kind == SyntaxKind.EndOfFile) break;
        }

        if (_conditionals.Count > 0)
        {
            _diagnostics.Error(ErrorCode.ERR_UnterminatedConditional,
                new TextSpan(Math.Max(0, _source.Length - 1), Math.Min(1, _source.Length)),
                $"{_conditionals.Count} conditional-compilation directive(s) "
                + "were never closed with '#endif'");
        }

        MarkStatementTerminators(green);

        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>(green.Count);
        var position = 0;
        foreach (var token in green)
        {
            tokens.Add(new SyntaxToken(token, parent: null, position));
            position += token.FullWidth;
        }
        return tokens.MoveToImmutable();
    }

    private GreenToken LexToken()
    {
        var leading = LexTrivia(isTrailing: false);

        _tokenStart = _position;
        var start = _position;
        var (kind, value, parts) = ScanToken();
        var text = TextBetween(start, _position);

        var trailing = LexTrivia(isTrailing: true);

        return new GreenToken(kind, text, leading, trailing, value, parts);
    }

    // -----------------------------------------------------------------------
    // Trivia
    // -----------------------------------------------------------------------

    private ImmutableArray<GreenTrivia> LexTrivia(bool isTrailing)
    {
        var builder = ImmutableArray.CreateBuilder<GreenTrivia>();

        while (!AtEnd)
        {
            var start = _position;

            if (IsDirectiveAhead())
            {
                ScanDirective(builder);
                continue;
            }

            // Inside an inactive region nothing is a token, so whole lines are
            // taken as disabled text until a directive reopens it.
            if (!IsActive)
            {
                ScanDisabledLine();
                builder.Add(Trivia(SyntaxKind.DisabledTextTrivia, start));
                continue;
            }

            if (_position == 0 && Matches("#!"))
            {
                while (!AtEnd && Current is not ('\n' or '\r')) _position++;
                builder.Add(Trivia(SyntaxKind.ShebangTrivia, start));
                continue;
            }

            if (Current is ' ' or '\t' or '\v' or '\f')
            {
                while (Current is ' ' or '\t' or '\v' or '\f') _position++;
                builder.Add(Trivia(SyntaxKind.WhitespaceTrivia, start));
                continue;
            }

            if (Current is '\r' or '\n')
            {
                if (Current == '\r' && Next == '\n') _position += 2;
                else _position++;
                builder.Add(Trivia(SyntaxKind.NewLineTrivia, start));

                // Roslyn's rule: trailing trivia ends at the first line break.
                if (isTrailing) break;
                continue;
            }

            if (Current == '/' && Next == '/')
            {
                var kind = SyntaxKind.LineCommentTrivia;
                if (Peek(2) == '/' && Peek(3) != '/') kind = SyntaxKind.DocCommentTrivia;
                else if (Peek(2) == '!') kind = SyntaxKind.ModuleDocCommentTrivia;

                while (!AtEnd && Current is not ('\n' or '\r')) _position++;
                builder.Add(Trivia(kind, start));
                continue;
            }

            if (Current == '/' && Next == '*')
            {
                var kind = Peek(2) == '*' && Peek(3) != '/'
                    ? SyntaxKind.DocCommentTrivia
                    : SyntaxKind.BlockCommentTrivia;
                ScanBlockComment(start);
                builder.Add(Trivia(kind, start));
                continue;
            }

            break;
        }

        return builder.ToImmutable();
    }

    private GreenTrivia Trivia(SyntaxKind kind, int start) =>
        new(kind, TextBetween(start, _position));

    /// <summary>Scans a block comment, honouring nesting per spec/lexical.md §8.</summary>
    private void ScanBlockComment(int start)
    {
        _position += 2;
        var depth = 1;
        while (depth > 0)
        {
            if (AtEnd)
            {
                _diagnostics.Error(ErrorCode.ERR_UnterminatedBlockComment, new TextSpan(start, 2),
                    "unterminated block comment");
                return;
            }
            if (Current == '/' && Next == '*') { depth++; _position += 2; }
            else if (Current == '*' && Next == '/') { depth--; _position += 2; }
            else _position++;
        }
    }

    // -----------------------------------------------------------------------
    // Tokens
    // -----------------------------------------------------------------------

    private (SyntaxKind Kind, object? Value, ImmutableArray<StringPart> Parts) ScanToken()
    {
        if (AtEnd) return (SyntaxKind.EndOfFile, null, default);

        var c = Current;

        if (c == '`') return ScanEscapedIdentifier();
        if (SyntaxFacts.IsIdentifierStart(c)) return ScanIdentifierOrKeyword();
        if (char.IsAsciiDigit(c)) return ScanNumber();
        if (c == '\'') return ScanCharLiteral();
        if (c == '"') return ScanStringLiteral();
        if (c == '#' && (Peek(1) == '"' || IsRawStringStart())) return ScanRawString();

        return (ScanPunctuation(), null, default);
    }

    private bool IsRawStringStart()
    {
        var i = _position;
        while (i < _source.Length && _source[i] == '#') i++;
        return i < _source.Length && _source[i] == '"';
    }

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanIdentifierOrKeyword()
    {
        var start = _position;
        while (!AtEnd && SyntaxFacts.IsIdentifierContinue(Current)) _position++;

        var text = TextBetween(start, _position);
        var normalized = text.IsNormalized(NormalizationForm.FormC)
            ? text
            : text.Normalize(NormalizationForm.FormC);

        // A lone '_' is the wildcard, not an identifier (spec/lexical.md §3).
        if (normalized == "_") return (SyntaxKind.UnderscoreToken, null, default);

        var kind = SyntaxFacts.KeywordKind(normalized);
        object? value = kind switch
        {
            SyntaxKind.TrueKeyword => true,
            SyntaxKind.FalseKeyword => false,
            _ => null,
        };

        if (kind == SyntaxKind.IdentifierToken && SyntaxFacts.IsFutureReserved(normalized))
        {
            _diagnostics.Add(Diagnostic.Warning(
                ErrorCode.None, new TextSpan(start, _position - start),
                $"'{normalized}' is reserved for future use and may become a keyword"));
        }

        return (kind, value, default);
    }

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanEscapedIdentifier()
    {
        var start = _position;
        _position++; // opening backtick

        var contentStart = _position;
        while (!AtEnd && Current != '`' && Current is not ('\n' or '\r')) _position++;

        if (Current != '`')
        {
            _diagnostics.Error(ErrorCode.ERR_UnterminatedEscapedIdentifier,
                new TextSpan(start, _position - start),
                "unterminated escaped identifier; expected a closing '`'");
            return (SyntaxKind.IdentifierToken, null, default);
        }

        var content = TextBetween(contentStart, _position);
        _position++; // closing backtick

        if (content.Length == 0)
        {
            _diagnostics.Error(ErrorCode.ERR_EmptyEscapedIdentifier,
                new TextSpan(start, _position - start),
                "escaped identifier cannot be empty");
        }

        return (SyntaxKind.IdentifierToken, content, default);
    }

    // -----------------------------------------------------------------------
    // Numeric literals
    // -----------------------------------------------------------------------

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanNumber()
    {
        var start = _position;

        if (Current == '0' && (Next is 'x' or 'X')) return ScanRadixOrHexFloat(start, 16);
        if (Current == '0' && (Next is 'o' or 'O')) return ScanRadix(start, 8);
        if (Current == '0' && (Next is 'b' or 'B')) return ScanRadix(start, 2);

        return ScanDecimal(start);
    }

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanDecimal(int start)
    {
        ScanDigitRun(10);

        var isFloat = false;

        // A '.' is part of the literal only when a digit follows, so that
        // '1..<5' and 'tuple.0' lex correctly (spec/lexical.md §6).
        if (Current == '.' && char.IsAsciiDigit(Next))
        {
            isFloat = true;
            _position++;
            ScanDigitRun(10);
        }

        if (Current is 'e' or 'E')
        {
            var save = _position;
            _position++;
            if (Current is '+' or '-') _position++;
            if (char.IsAsciiDigit(Current))
            {
                isFloat = true;
                ScanDigitRun(10);
            }
            else
            {
                _position = save; // not an exponent: leave it to the next token
            }
        }

        var text = TextBetween(start, _position);
        var span = new TextSpan(start, _position - start);
        CheckUnderscores(text, span);

        var cleaned = text.Replace("_", "", StringComparison.Ordinal);

        if (isFloat)
        {
            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                _diagnostics.Error(ErrorCode.ERR_InvalidFloatLiteral, span,
                    $"'{text}' is not a valid floating-point literal");
                d = 0;
            }
            return (SyntaxKind.FloatLiteralToken, d, default);
        }

        if (!ulong.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out var u))
        {
            _diagnostics.Error(ErrorCode.ERR_IntegerLiteralOverflow, span,
                $"integer literal '{text}' does not fit in 64 bits");
            u = 0;
        }
        return (SyntaxKind.IntegerLiteralToken, u, default);
    }

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanRadix(int start, int radix)
    {
        _position += 2; // 0o / 0b
        var digitsStart = _position;
        ScanDigitRun(radix);

        var span = new TextSpan(start, _position - start);
        var digits = TextBetween(digitsStart, _position);

        if (digits.Length == 0)
        {
            _diagnostics.Error(ErrorCode.ERR_NumericLiteralNoDigits, span,
                $"expected at least one base-{radix} digit after '{TextBetween(start, digitsStart)}'");
            return (SyntaxKind.IntegerLiteralToken, 0UL, default);
        }

        CheckUnderscores(TextBetween(start, _position), span);
        return (SyntaxKind.IntegerLiteralToken, ParseRadix(digits, radix, span), default);
    }

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanRadixOrHexFloat(int start, int radix)
    {
        _position += 2; // 0x
        var digitsStart = _position;
        ScanDigitRun(radix);
        var intDigits = TextBetween(digitsStart, _position);

        var isFloat = false;
        var fracDigits = "";

        if (Current == '.' && IsRadixDigit(Next, 16))
        {
            isFloat = true;
            _position++;
            var fracStart = _position;
            ScanDigitRun(16);
            fracDigits = TextBetween(fracStart, _position);
        }

        if (Current is 'p' or 'P')
        {
            isFloat = true;
            _position++;
            if (Current is '+' or '-') _position++;
            var expStart = _position;
            ScanDigitRun(10);
            if (_position == expStart)
            {
                _diagnostics.Error(ErrorCode.ERR_HexFloatNeedsExponent,
                    new TextSpan(start, _position - start),
                    "hexadecimal floating-point literal requires digits after 'p'");
            }
        }
        else if (isFloat)
        {
            _diagnostics.Error(ErrorCode.ERR_HexFloatNeedsExponent,
                new TextSpan(start, _position - start),
                "hexadecimal floating-point literal requires a binary exponent, e.g. '0x1.8p3'");
        }

        var span = new TextSpan(start, _position - start);
        var text = TextBetween(start, _position);
        CheckUnderscores(text, span);

        if (intDigits.Length == 0 && fracDigits.Length == 0)
        {
            _diagnostics.Error(ErrorCode.ERR_NumericLiteralNoDigits, span,
                "expected at least one hexadecimal digit after '0x'");
            return (SyntaxKind.IntegerLiteralToken, 0UL, default);
        }

        if (isFloat) return (SyntaxKind.FloatLiteralToken, ParseHexFloat(text, span), default);

        return (SyntaxKind.IntegerLiteralToken, ParseRadix(intDigits, 16, span), default);
    }

    private void ScanDigitRun(int radix)
    {
        while (!AtEnd && (Current == '_' || IsRadixDigit(Current, radix))) _position++;
    }

    private static bool IsRadixDigit(char c, int radix) => radix switch
    {
        2 => c is '0' or '1',
        8 => c is >= '0' and <= '7',
        10 => char.IsAsciiDigit(c),
        16 => char.IsAsciiHexDigit(c),
        _ => false,
    };

    private void CheckUnderscores(string text, TextSpan span)
    {
        if (text.EndsWith('_'))
        {
            _diagnostics.Error(ErrorCode.ERR_TrailingUnderscoreInNumber, span,
                "numeric literal may not end with '_'");
        }
    }

    private ulong ParseRadix(string digits, int radix, TextSpan span)
    {
        ulong value = 0;
        foreach (var c in digits)
        {
            if (c == '_') continue;
            if (!IsRadixDigit(c, radix))
            {
                _diagnostics.Error(ErrorCode.ERR_InvalidDigitForBase, span,
                    $"'{c}' is not a valid base-{radix} digit");
                return 0;
            }
            var digit = (ulong)Convert.ToInt32(c.ToString(), 16);
            if (value > (ulong.MaxValue - digit) / (ulong)radix)
            {
                _diagnostics.Error(ErrorCode.ERR_IntegerLiteralOverflow, span,
                    "integer literal does not fit in 64 bits");
                return 0;
            }
            value = value * (ulong)radix + digit;
        }
        return value;
    }

    private double ParseHexFloat(string text, TextSpan span)
    {
        var s = text.Replace("_", "", StringComparison.Ordinal)[2..]; // drop 0x
        var pIndex = s.IndexOfAny(['p', 'P']);
        var exponent = 0;
        if (pIndex >= 0)
        {
            var expText = s[(pIndex + 1)..];
            if (!int.TryParse(expText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            {
                _diagnostics.Error(ErrorCode.ERR_InvalidFloatLiteral, span,
                    $"'{text}' has an invalid binary exponent");
                return 0;
            }
            s = s[..pIndex];
        }

        var dotIndex = s.IndexOf('.', StringComparison.Ordinal);
        var fracDigits = 0;
        if (dotIndex >= 0)
        {
            fracDigits = s.Length - dotIndex - 1;
            s = s.Remove(dotIndex, 1);
        }

        double mantissa = 0;
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                _diagnostics.Error(ErrorCode.ERR_InvalidFloatLiteral, span,
                    $"'{text}' is not a valid hexadecimal floating-point literal");
                return 0;
            }
            mantissa = mantissa * 16 + Convert.ToInt32(c.ToString(), 16);
        }

        return mantissa * Math.Pow(2, exponent - 4 * fracDigits);
    }

    // -----------------------------------------------------------------------
    // Character literals
    // -----------------------------------------------------------------------

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanCharLiteral()
    {
        var start = _position;
        _position++; // opening quote

        var builder = new StringBuilder();
        while (!AtEnd && Current != '\'' && Current is not ('\n' or '\r'))
        {
            if (Current == '\\') ScanEscape(builder);
            else { builder.Append(Current); _position++; }
        }

        if (Current != '\'')
        {
            _diagnostics.Error(ErrorCode.ERR_UnterminatedChar,
                new TextSpan(start, _position - start),
                "unterminated character literal");
            return (SyntaxKind.CharLiteralToken, null, default);
        }
        _position++; // closing quote

        var span = new TextSpan(start, _position - start);
        var content = builder.ToString();

        if (content.Length == 0)
        {
            _diagnostics.Error(ErrorCode.ERR_EmptyCharLiteral, span,
                "character literal cannot be empty");
            return (SyntaxKind.CharLiteralToken, null, default);
        }

        var runes = content.EnumerateRunes();
        Rune? first = null;
        var count = 0;
        foreach (var r in runes) { first ??= r; count++; }

        if (count > 1)
        {
            _diagnostics.Error(ErrorCode.ERR_CharLiteralTooLong, span,
                $"character literal must hold exactly one Unicode scalar value, but holds {count}");
        }

        return (SyntaxKind.CharLiteralToken, first, default);
    }

    /// <summary>Consumes one escape sequence and appends its cooked value.</summary>
    private void ScanEscape(StringBuilder builder)
    {
        var start = _position;
        _position++; // backslash

        switch (Current)
        {
            case '0': builder.Append('\0'); _position++; return;
            case '\\': builder.Append('\\'); _position++; return;
            case 't': builder.Append('\t'); _position++; return;
            case 'n': builder.Append('\n'); _position++; return;
            case 'r': builder.Append('\r'); _position++; return;
            case '"': builder.Append('"'); _position++; return;
            case '\'': builder.Append('\''); _position++; return;
            case 'u': ScanUnicodeEscape(builder, start); return;
            default:
                _diagnostics.Error(ErrorCode.ERR_InvalidEscapeSequence,
                    new TextSpan(start, 2),
                    $"unrecognised escape sequence '\\{Current}'");
                builder.Append(Current);
                _position++;
                return;
        }
    }

    private void ScanUnicodeEscape(StringBuilder builder, int start)
    {
        _position++; // 'u'
        if (Current != '{')
        {
            _diagnostics.Error(ErrorCode.ERR_InvalidUnicodeEscape, new TextSpan(start, 2),
                "unicode escape must be written '\\u{...}'");
            return;
        }
        _position++; // '{'

        var digitsStart = _position;
        while (!AtEnd && char.IsAsciiHexDigit(Current)) _position++;
        var digits = TextBetween(digitsStart, _position);

        if (Current != '}')
        {
            _diagnostics.Error(ErrorCode.ERR_InvalidUnicodeEscape,
                new TextSpan(start, _position - start),
                "unterminated unicode escape; expected '}'");
            return;
        }
        _position++; // '}'

        var span = new TextSpan(start, _position - start);
        if (digits.Length is 0 or > 6)
        {
            _diagnostics.Error(ErrorCode.ERR_InvalidUnicodeEscape, span,
                "unicode escape requires between 1 and 6 hexadecimal digits");
            return;
        }

        var scalar = int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (!Rune.IsValid(scalar))
        {
            _diagnostics.Error(ErrorCode.ERR_UnicodeScalarOutOfRange, span,
                $"U+{scalar:X} is not a valid Unicode scalar value");
            return;
        }

        builder.Append(new Rune(scalar).ToString());
    }

    // -----------------------------------------------------------------------
    // String literals
    // -----------------------------------------------------------------------

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanStringLiteral() =>
        Matches("\"\"\"") ? ScanMultilineString() : ScanSimpleString();

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanSimpleString()
    {
        var start = _position;
        _position++; // opening quote

        var parts = ImmutableArray.CreateBuilder<StringPart>();
        var builder = new StringBuilder();

        while (true)
        {
            if (AtEnd || Current is '\n' or '\r')
            {
                _diagnostics.Error(ErrorCode.ERR_UnterminatedString,
                    new TextSpan(start, _position - start),
                    "unterminated string literal; use \"\"\" for a multi-line string");
                break;
            }

            if (Current == '"') { _position++; break; }

            if (Current == '\\' && Next == '(')
            {
                FlushLiteral(parts, builder);
                ScanInterpolation(parts);
                continue;
            }

            if (Current == '\\') { ScanEscape(builder); continue; }

            builder.Append(Current);
            _position++;
        }

        FlushLiteral(parts, builder);
        return Finish(SyntaxKind.StringLiteralToken, parts);
    }

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanMultilineString()
    {
        var start = _position;
        _position += 3; // opening """

        // The opening delimiter must be followed by a line break.
        var afterOpen = _position;
        while (Current is ' ' or '\t') _position++;
        if (Current is '\n' or '\r')
        {
            if (Current == '\r' && Next == '\n') _position += 2; else _position++;
        }
        else
        {
            _diagnostics.Error(ErrorCode.ERR_MultilineDelimiterMustBeOnOwnLine,
                new TextSpan(start, 3),
                "a multi-line string must begin on the line after its opening \"\"\"");
            _position = afterOpen;
        }

        var parts = ImmutableArray.CreateBuilder<StringPart>();
        var builder = new StringBuilder();
        var terminated = false;
        var closingIndent = 0;

        while (!AtEnd)
        {
            // A closing delimiter is the first non-whitespace text on its line.
            if (Matches("\"\"\"") && IsAtLineStartIgnoringWhitespace(_position, out closingIndent))
            {
                _position += 3;
                terminated = true;
                break;
            }

            if (Current == '\\' && Next == '(')
            {
                FlushLiteral(parts, builder);
                ScanInterpolation(parts);
                continue;
            }

            if (Current == '\\')
            {
                // A backslash immediately before a line break joins the lines.
                if (Next is '\n' or '\r')
                {
                    _position++;
                    if (Current == '\r' && Next == '\n') _position += 2; else _position++;
                    continue;
                }
                ScanEscape(builder);
                continue;
            }

            builder.Append(Current);
            _position++;
        }

        if (!terminated)
        {
            _diagnostics.Error(ErrorCode.ERR_UnterminatedString, new TextSpan(start, 3),
                "unterminated multi-line string literal");
        }

        FlushLiteral(parts, builder);

        var dedented = Dedent(parts, closingIndent, new TextSpan(start, _position - start));
        return Finish(SyntaxKind.MultilineStringLiteralToken, dedented);
    }

    /// <summary>
    /// True when only whitespace separates <paramref name="position"/> from the
    /// start of its line; reports that whitespace width as the indentation.
    /// </summary>
    private bool IsAtLineStartIgnoringWhitespace(int position, out int indent)
    {
        indent = 0;
        var i = position - 1;
        while (i >= 0 && _source[i] is ' ' or '\t') { i--; indent++; }
        return i < 0 || _source[i] is '\n' or '\r';
    }

    /// <summary>
    /// Strips the closing delimiter's indentation from every line, and removes
    /// the final line break that precedes the closing delimiter.
    /// </summary>
    private ImmutableArray<StringPart>.Builder Dedent(
        ImmutableArray<StringPart>.Builder parts, int indent, TextSpan span)
    {
        if (parts.Count == 0) return parts;

        // Drop the line break and indentation immediately before the delimiter.
        var last = parts[^1];
        if (last.Kind == StringPartKind.Literal)
        {
            var t = last.Text;
            var cut = t.Length;
            var seen = 0;
            while (cut > 0 && t[cut - 1] is ' ' or '\t' && seen < indent) { cut--; seen++; }
            if (cut > 0 && t[cut - 1] == '\n')
            {
                cut--;
                if (cut > 0 && t[cut - 1] == '\r') cut--;
            }
            parts[^1] = last with { Text = t[..cut] };
        }

        if (indent == 0) return parts;

        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Kind != StringPartKind.Literal) continue;

            var lines = parts[i].Text.Split('\n');
            var isFirst = i == 0;
            for (var l = 0; l < lines.Length; l++)
            {
                // Only line-initial text is dedented; the first segment of the
                // first part already begins at a line start.
                if (l == 0 && !isFirst) continue;

                var line = lines[l];
                var removed = 0;
                while (removed < indent && removed < line.Length && line[removed] is ' ' or '\t')
                    removed++;

                if (removed < indent && line.Trim().Length > 0)
                {
                    _diagnostics.Error(ErrorCode.ERR_InsufficientIndentation, span,
                        $"line is indented less than the closing delimiter (expected {indent} columns)");
                }
                lines[l] = line[removed..];
            }
            parts[i] = parts[i] with { Text = string.Join('\n', lines) };
        }

        return parts;
    }

    private (SyntaxKind, object?, ImmutableArray<StringPart>) ScanRawString()
    {
        var start = _position;
        var hashes = 0;
        while (Current == '#') { hashes++; _position++; }

        var isMultiline = Matches("\"\"\"");
        var openLength = isMultiline ? 3 : 1;
        _position += openLength;

        var closing = new string('"', openLength) + new string('#', hashes);
        var contentStart = _position;

        while (!AtEnd && !Matches(closing))
        {
            if (!isMultiline && Current is '\n' or '\r') break;
            _position++;
        }

        if (!Matches(closing))
        {
            _diagnostics.Error(ErrorCode.ERR_UnterminatedRawString,
                new TextSpan(start, _position - start),
                $"unterminated raw string literal; expected '{closing}'");
            var unterminated = TextBetween(contentStart, _position);
            return (SyntaxKind.RawStringLiteralToken, unterminated, default);
        }

        var content = TextBetween(contentStart, _position);
        _position += closing.Length;

        return (SyntaxKind.RawStringLiteralToken, content, default);
    }

    /// <summary>
    /// Captures a <c>\(expr)</c> hole. The inner source is recorded verbatim and
    /// re-entered by the parser; nested strings and parentheses are skipped here
    /// only well enough to find the matching close paren.
    /// </summary>
    private void ScanInterpolation(ImmutableArray<StringPart>.Builder parts)
    {
        var start = _position;
        _position += 2; // '\' '('
        var innerStart = _position;

        var depth = 1;
        while (!AtEnd && depth > 0)
        {
            var c = Current;
            if (c == '(') { depth++; _position++; }
            else if (c == ')') { depth--; _position++; }
            else if (c == '"') SkipNestedString();
            else if (c == '\'') SkipNestedChar();
            else if (c is '\n' or '\r') break;
            else _position++;
        }

        if (depth != 0)
        {
            _diagnostics.Error(ErrorCode.ERR_UnterminatedInterpolation,
                new TextSpan(start, Math.Max(2, _position - start)),
                "unterminated string interpolation; expected ')'");
            return;
        }

        var innerEnd = _position - 1; // exclude the closing paren
        parts.Add(new StringPart(
            StringPartKind.Interpolation,
            TextBetween(innerStart, innerEnd),
            // Relative to the token's text; SyntaxToken.Parts makes it absolute.
            TextSpan.FromBounds(innerStart - _tokenStart, innerEnd - _tokenStart)));
    }

    private void SkipNestedString()
    {
        if (Matches("\"\"\""))
        {
            _position += 3;
            while (!AtEnd && !Matches("\"\"\"")) _position++;
            if (Matches("\"\"\"")) _position += 3;
            return;
        }

        _position++; // opening quote
        while (!AtEnd && Current != '"' && Current is not ('\n' or '\r'))
        {
            if (Current == '\\') _position += 2; else _position++;
        }
        if (Current == '"') _position++;
    }

    private void SkipNestedChar()
    {
        _position++;
        while (!AtEnd && Current != '\'' && Current is not ('\n' or '\r'))
        {
            if (Current == '\\') _position += 2; else _position++;
        }
        if (Current == '\'') _position++;
    }

    private static void FlushLiteral(
        ImmutableArray<StringPart>.Builder parts, StringBuilder builder)
    {
        if (builder.Length == 0) return;
        parts.Add(new StringPart(StringPartKind.Literal, builder.ToString(), default));
        builder.Clear();
    }

    private static (SyntaxKind, object?, ImmutableArray<StringPart>) Finish(
        SyntaxKind kind, ImmutableArray<StringPart>.Builder parts)
    {
        var array = parts.ToImmutable();
        var hasHole = false;
        foreach (var p in array)
            if (p.Kind == StringPartKind.Interpolation) { hasHole = true; break; }

        // A string with no holes carries its cooked value directly.
        object? value = null;
        if (!hasHole)
        {
            value = array.Length switch
            {
                0 => string.Empty,
                1 => array[0].Text,
                _ => string.Concat(array.Select(p => p.Text)),
            };
        }

        return (kind, value, array);
    }

    // -----------------------------------------------------------------------
    // Punctuation
    // -----------------------------------------------------------------------

    private SyntaxKind ScanPunctuation()
    {
        var c = Current;
        _position++;

        switch (c)
        {
            case '(': _bracketDepth++; return SyntaxKind.OpenParenToken;
            case ')': if (_bracketDepth > 0) _bracketDepth--; return SyntaxKind.CloseParenToken;
            case '[': _bracketDepth++; return SyntaxKind.OpenBracketToken;
            case ']': if (_bracketDepth > 0) _bracketDepth--; return SyntaxKind.CloseBracketToken;
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
                if (Current == '.' && Next == '.') { _position += 2; return SyntaxKind.DotDotDotToken; }
                if (Current == '.' && Next == '<') { _position += 2; return SyntaxKind.DotDotLessToken; }
                if (Current == '.') { _position++; return SyntaxKind.DotDotToken; }
                return SyntaxKind.DotToken;

            case '?':
                if (Current == '.') { _position++; return SyntaxKind.QuestionDotToken; }
                if (Current == '?' && Next == '=') { _position += 2; return SyntaxKind.QuestionQuestionEqualsToken; }
                if (Current == '?') { _position++; return SyntaxKind.QuestionQuestionToken; }
                return SyntaxKind.QuestionToken;

            case '+':
                if (Current == '=') { _position++; return SyntaxKind.PlusEqualsToken; }
                return SyntaxKind.PlusToken;

            case '-':
                if (Current == '>') { _position++; return SyntaxKind.ArrowToken; }
                if (Current == '=') { _position++; return SyntaxKind.MinusEqualsToken; }
                return SyntaxKind.MinusToken;

            case '*':
                if (Current == '=') { _position++; return SyntaxKind.StarEqualsToken; }
                return SyntaxKind.StarToken;

            case '/':
                if (Current == '=') { _position++; return SyntaxKind.SlashEqualsToken; }
                return SyntaxKind.SlashToken;

            case '%':
                if (Current == '=') { _position++; return SyntaxKind.PercentEqualsToken; }
                return SyntaxKind.PercentToken;

            case '^':
                if (Current == '=') { _position++; return SyntaxKind.CaretEqualsToken; }
                return SyntaxKind.CaretToken;

            case '&':
                if (Current == '&') { _position++; return SyntaxKind.AmpersandAmpersandToken; }
                if (Current == '=') { _position++; return SyntaxKind.AmpersandEqualsToken; }
                return SyntaxKind.AmpersandToken;

            case '|':
                if (Current == '|') { _position++; return SyntaxKind.PipePipeToken; }
                if (Current == '=') { _position++; return SyntaxKind.PipeEqualsToken; }
                return SyntaxKind.PipeToken;

            case '=':
                if (Current == '=' && Next == '=') { _position += 2; return SyntaxKind.EqualsEqualsEqualsToken; }
                if (Current == '=') { _position++; return SyntaxKind.EqualsEqualsToken; }
                if (Current == '>') { _position++; return SyntaxKind.FatArrowToken; }
                return SyntaxKind.EqualsToken;

            case '!':
                if (Current == '=' && Next == '=') { _position += 2; return SyntaxKind.BangEqualsEqualsToken; }
                if (Current == '=') { _position++; return SyntaxKind.BangEqualsToken; }
                return SyntaxKind.BangToken;

            case '<':
                if (Current == '<' && Next == '=') { _position += 2; return SyntaxKind.LessLessEqualsToken; }
                if (Current == '<') { _position++; return SyntaxKind.LessLessToken; }
                if (Current == '=') { _position++; return SyntaxKind.LessEqualsToken; }
                return SyntaxKind.LessToken;

            case '>':
                // '>>' is produced here, but the parser may split it when closing
                // nested generic argument lists, exactly as Roslyn does.
                if (Current == '>' && Next == '=') { _position += 2; return SyntaxKind.GreaterGreaterEqualsToken; }
                if (Current == '>') { _position++; return SyntaxKind.GreaterGreaterToken; }
                if (Current == '=') { _position++; return SyntaxKind.GreaterEqualsToken; }
                return SyntaxKind.GreaterToken;

            default:
                _diagnostics.Error(ErrorCode.ERR_UnexpectedCharacter,
                    new TextSpan(_position - 1, 1),
                    $"unexpected character '{c}' (U+{(int)c:X4})");
                return SyntaxKind.BadToken;
        }
    }

    // -----------------------------------------------------------------------
    // Statement terminators — spec/lexical.md §4
    // -----------------------------------------------------------------------

    private static void MarkStatementTerminators(List<GreenToken> tokens)
    {
        var depth = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            switch (token.Kind)
            {
                case SyntaxKind.OpenParenToken:
                case SyntaxKind.OpenBracketToken:
                    depth++;
                    break;
                case SyntaxKind.CloseParenToken:
                case SyntaxKind.CloseBracketToken:
                    if (depth > 0) depth--;
                    break;
                case SyntaxKind.SemicolonToken:
                    tokens[i] = token.WithTerminatesStatement(true);
                    continue;
            }

            if (i + 1 >= tokens.Count) continue;

            var next = tokens[i + 1];
            var separatedByNewLine = HasNewLine(token.Trailing) || HasNewLine(next.Leading);
            if (!separatedByNewLine) continue;

            // Rule 3: inside unbalanced ( or [, newlines never terminate.
            if (depth > 0) continue;

            // Jump keywords terminate unconditionally, guarding against the
            // JavaScript automatic-semicolon-insertion hazard.
            if (SyntaxFacts.AlwaysTerminates(token.Kind))
            {
                tokens[i] = token.WithTerminatesStatement(true);
                continue;
            }

            // Rule 1: the statement must be able to end here.
            if (!SyntaxFacts.CanEndStatement(token.Kind)) continue;

            // Rule 2: the next line must not open with a continuation token.
            if (SyntaxFacts.ContinuesStatement(next.Kind)) continue;

            tokens[i] = token.WithTerminatesStatement(true);
        }

        if (tokens.Count > 0) tokens[^1] = tokens[^1].WithTerminatesStatement(true);
    }

    private static bool HasNewLine(ImmutableArray<GreenTrivia> trivia)
    {
        foreach (var t in trivia)
            if (t.Kind == SyntaxKind.NewLineTrivia) return true;
        return false;
    }

    // -----------------------------------------------------------------------
    // Conditional compilation — spec/lexical.md §9
    // -----------------------------------------------------------------------

    /// <summary>The state of one open <c>#if</c>.</summary>
    private enum BranchState
    {
        /// <summary>This branch's tokens are being lexed.</summary>
        Taking,

        /// <summary>This branch is inactive but a later one may still be taken.</summary>
        Skipping,

        /// <summary>A branch was already taken, so every later one is inactive.</summary>
        Done,
    }

    private readonly List<BranchState> _conditionals = [];

    /// <summary>True when no enclosing conditional is excluding this text.</summary>
    private bool IsActive
    {
        get
        {
            foreach (var state in _conditionals)
                if (state != BranchState.Taking) return false;
            return true;
        }
    }

    /// <summary>True when every conditional below the innermost one is taking.</summary>
    private bool IsEnclosingActive
    {
        get
        {
            for (var i = 0; i < _conditionals.Count - 1; i++)
                if (_conditionals[i] != BranchState.Taking) return false;
            return true;
        }
    }

    /// <summary>True when only whitespace separates the position from its line start.</summary>
    private bool IsAtLineStart(int position)
    {
        var i = position - 1;
        while (i >= 0 && _source[i] is ' ' or '\t') i--;
        return i < 0 || _source[i] is '\n' or '\r';
    }

    private bool IsDirectiveAhead()
    {
        if (Current != '#' || !IsAtLineStart(_position)) return false;

        var i = _position + 1;
        var start = i;
        while (i < _source.Length && SyntaxFacts.IsIdentifierContinue(_source[i])) i++;

        return TextBetween(start, i) is
            "if" or "elseif" or "else" or "endif" or "error" or "warning";
    }

    /// <summary>Consumes a directive line and applies it to the conditional stack.</summary>
    private void ScanDirective(ImmutableArray<GreenTrivia>.Builder builder)
    {
        var start = _position;
        _position++; // '#'

        var nameStart = _position;
        while (!AtEnd && SyntaxFacts.IsIdentifierContinue(Current)) _position++;
        var name = TextBetween(nameStart, _position);

        var argumentStart = _position;
        while (!AtEnd && Current is not ('\n' or '\r')) _position++;
        var argument = TextBetween(argumentStart, _position).Trim();

        var span = new TextSpan(start, _position - start);
        ApplyDirective(name, argument, span);

        builder.Add(Trivia(SyntaxKind.DirectiveTrivia, start));
    }

    private void ApplyDirective(string name, string argument, TextSpan span)
    {
        switch (name)
        {
            case "if":
                _conditionals.Add(BranchState.Skipping);   // placeholder for nesting
                _conditionals[^1] = IsEnclosingActive
                    ? (EvaluateCondition(argument, span) ? BranchState.Taking : BranchState.Skipping)
                    : BranchState.Done;
                return;

            case "elseif":
                if (!RequireOpenConditional("#elseif", span)) return;
                _conditionals[^1] = _conditionals[^1] switch
                {
                    BranchState.Taking => BranchState.Done,
                    BranchState.Done => BranchState.Done,
                    _ => IsEnclosingActive && EvaluateCondition(argument, span)
                        ? BranchState.Taking
                        : BranchState.Skipping,
                };
                return;

            case "else":
                if (!RequireOpenConditional("#else", span)) return;
                _conditionals[^1] = _conditionals[^1] switch
                {
                    BranchState.Skipping when IsEnclosingActive => BranchState.Taking,
                    BranchState.Skipping => BranchState.Done,
                    _ => BranchState.Done,
                };
                return;

            case "endif":
                if (!RequireOpenConditional("#endif", span)) return;
                _conditionals.RemoveAt(_conditionals.Count - 1);
                return;

            case "error":
                if (IsActive)
                    _diagnostics.Error(ErrorCode.ERR_UserError, span, DirectiveMessage(argument));
                return;

            case "warning":
                if (IsActive)
                {
                    _diagnostics.Add(Diagnostic.Warning(
                        ErrorCode.WRN_UserWarning, span, DirectiveMessage(argument)));
                }
                return;

            default:
                _diagnostics.Error(ErrorCode.ERR_UnknownDirective, span,
                    $"unknown directive '#{name}'");
                return;
        }
    }

    private bool RequireOpenConditional(string directive, TextSpan span)
    {
        if (_conditionals.Count > 0) return true;

        _diagnostics.Error(ErrorCode.ERR_UnmatchedDirective, span,
            $"'{directive}' has no matching '#if'");
        return false;
    }

    private bool EvaluateCondition(string condition, TextSpan span)
    {
        if (condition.Length == 0)
        {
            _diagnostics.Error(ErrorCode.ERR_BadDirectiveCondition, span,
                "expected a condition after the directive");
            return false;
        }

        var value = DirectiveCondition.Evaluate(condition, _options, out var error);
        if (error is not null)
            _diagnostics.Error(ErrorCode.ERR_BadDirectiveCondition, span, error);

        return value;
    }

    /// <summary>Unwraps the <c>("...")</c> of a <c>#error</c> or <c>#warning</c>.</summary>
    private static string DirectiveMessage(string argument)
    {
        var text = argument.Trim();
        if (text.StartsWith('(') && text.EndsWith(')')) text = text[1..^1].Trim();
        if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"')) text = text[1..^1];
        return text.Length == 0 ? "(no message)" : text;
    }

    /// <summary>Consumes one line of an inactive region as disabled text.</summary>
    private void ScanDisabledLine()
    {
        while (!AtEnd && Current is not ('\n' or '\r')) _position++;
        if (AtEnd) return;
        if (Current == '\r' && Next == '\n') _position += 2; else _position++;
    }
}
