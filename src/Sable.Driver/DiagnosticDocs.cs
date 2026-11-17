using System.Globalization;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>
///     The long-form documentation of the diagnostics, for <c>sable explain</c>:
///     one Markdown file per code under <c>docs/diagnostics/</c>, embedded in
///     the driver. Each has a title line, an explanation, and usually an
///     erroneous example and its fix, as <c>```sable</c> blocks under
///     <c>## Example</c> and <c>## Fix</c>; a test compiles both.
/// </summary>
public static class DiagnosticDocs {
    const string Prefix = "diagnostics/";

    static readonly Lazy<IReadOnlyDictionary<string, string>> Pages = new(Load);

    static IReadOnlyDictionary<string, string> Load() {
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(DiagnosticDocs).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(static n => n.StartsWith(Prefix, StringComparison.Ordinal))) {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            pages[Path.GetFileNameWithoutExtension(resource[Prefix.Length..])] = reader.ReadToEnd();
        }

        return pages;
    }

    /// <summary>The codes that have a page, in order.</summary>
    public static IReadOnlyList<string> Codes => [..Pages.Value.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    ///     Reads what a user typed as a code: <c>S3004</c>, <c>s3004</c>,
    ///     <c>3004</c> or <c>E3004</c> all mean <c>S3004</c>.
    /// </summary>
    public static string? Normalise(string text) {
        var digits = text.Trim();
        if (digits.Length > 0 && char.IsLetter(digits[0])) {
            digits = digits[1..];
        }

        return digits.Length is > 0 and <= 4 && digits.All(char.IsAsciiDigit)
            ? "S" + int.Parse(digits, CultureInfo.InvariantCulture).ToString("D4", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>The page for a code, such as <c>S3004</c>, if it has one.</summary>
    public static string? Page(string code) => Pages.Value.GetValueOrDefault(code);

    /// <summary>The compiler's own name for a code, if it is one the compiler has.</summary>
    public static ErrorCode? Known(string code) {
        if (code.Length != 5 || !int.TryParse(code.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
            return null;
        }

        return Enum.IsDefined(typeof(ErrorCode), number) && number != 0 ? (ErrorCode)number : null;
    }

    /// <summary>A page's first line, without its <c>#</c> and code: the diagnostic in a few words.</summary>
    public static string Title(string page) {
        var line = page.TrimStart().Split('\n')[0].TrimStart('#', ' ').Trim();
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? line[(colon + 1)..].Trim() : line;
    }

    /// <summary>The <c>```sable</c> block under a page's <c>## <paramref name="heading" /></c>, if it has one.</summary>
    public static string? Example(string page, string heading) {
        var lines = page.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inSection = false;
        for (var i = 0; i < lines.Length; i++) {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal)) {
                inSection = lines[i][3..].Trim().Equals(heading, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection && lines[i].Trim() == "```sable") {
                var end = Array.FindIndex(lines, i + 1, static l => l.Trim() == "```");
                return end < 0 ? null : string.Join('\n', lines[(i + 1)..end]) + "\n";
            }
        }

        return null;
    }
}
