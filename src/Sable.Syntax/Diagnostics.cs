using System.Collections;
using System.Globalization;

namespace Sable.Syntax;

/// <summary>
/// Diagnostic identifiers. Ranges are allocated per docs/04-implementation-plan.md §0.1:
/// S0001-0999 lexical, S1000-1999 syntactic, S2000-2999 name resolution,
/// S3000-3999 types, S4000-4999 ownership, S5000-5999 concurrency,
/// S6000-6999 patterns, S7000-7999 codegen, S8000-8999 linking, S9000+ internal.
/// </summary>
public enum ErrorCode
{
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
    ERR_FloatNeedsDigitsBothSides = 17,
    ERR_ByteOrderMarkNotAllowed = 18,
    ERR_UnterminatedEscapedIdentifier = 19,
    ERR_EmptyEscapedIdentifier = 20,
    ERR_UnterminatedInterpolation = 21,
    ERR_InvalidDigitForBase = 22,
    ERR_MultilineDelimiterMustBeOnOwnLine = 23,
    ERR_InsufficientIndentation = 24,
}

public enum DiagnosticSeverity { Hidden, Info, Warning, Error }

/// <summary>
/// A structured diagnostic. Carries data, not a pre-formatted string, so the CLI
/// and the language server can render the same object differently.
/// </summary>
public sealed record Diagnostic(
    ErrorCode Code,
    DiagnosticSeverity Severity,
    TextSpan Span,
    string Message)
{
    public string Id => "S" + ((int)Code).ToString("D4", CultureInfo.InvariantCulture);

    public static Diagnostic Error(ErrorCode code, TextSpan span, string message) =>
        new(code, DiagnosticSeverity.Error, span, message);

    public static Diagnostic Warning(ErrorCode code, TextSpan span, string message) =>
        new(code, DiagnosticSeverity.Warning, span, message);

    public override string ToString() => $"{Id}: {Message} {Span}";

    /// <summary>Renders a rustc/Roslyn-style caret diagnostic against its source.</summary>
    public string Format(SourceText source)
    {
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
public sealed class DiagnosticBag : IReadOnlyCollection<Diagnostic>
{
    private readonly List<Diagnostic> _items = [];

    public int Count => _items.Count;
    public bool HasErrors => _items.Exists(d => d.Severity == DiagnosticSeverity.Error);

    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);

    public void Error(ErrorCode code, TextSpan span, string message) =>
        _items.Add(Diagnostic.Error(code, span, message));

    public void Error(ErrorCode code, int start, int length, string message) =>
        _items.Add(Diagnostic.Error(code, new TextSpan(start, length), message));

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

    public IReadOnlyList<Diagnostic> ToList() => _items.AsReadOnly();

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
