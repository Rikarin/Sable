using System.Text;
using System.Text.RegularExpressions;

namespace Sable.Driver.Packages;

/// <summary>
///     A manifest's <c>include</c> and <c>exclude</c> patterns, matched
///     against a package's files by their path relative to it, with '/'.
///     <c>*</c> is any run of characters within a name, <c>?</c> one
///     character, and <c>**</c> any number of directories. A pattern with
///     no '/' matches a name at any depth (<c>*.png</c>); one with a '/' is
///     relative to the package (<c>docs/*.md</c>, a leading '/' allowed). A
///     pattern that matches a directory matches everything under it.
/// </summary>
public static class FileGlob {
    /// <summary>Why <paramref name="pattern" /> is not one; null when it is.</summary>
    public static string? Problem(string pattern) =>
        pattern.Trim('/').Length == 0 ? "a pattern cannot be empty"
        : pattern.Contains('\\') ? "a pattern separates directories with '/'"
        : pattern.Split('/').Contains("..") ? "a pattern names files inside the package, and cannot go up with '..'"
        : Path.IsPathRooted(pattern) && !pattern.StartsWith('/') ? "a pattern is relative to the package"
        : null;

    /// <summary>Whether any of <paramref name="patterns" /> matches <paramref name="path" /> or a directory it is in.</summary>
    public static bool MatchesAny(IReadOnlyList<string> patterns, string path) =>
        patterns.Any(p => Matches(p, path));

    /// <summary>Whether <paramref name="pattern" /> matches <paramref name="path" /> or a directory it is in.</summary>
    public static bool Matches(string pattern, string path) {
        var regex = ToRegex(pattern);
        var segments = path.Split('/');
        for (var count = 1; count <= segments.Length; count++) {
            if (regex.IsMatch(string.Join('/', segments.Take(count)))) {
                return true;
            }
        }

        return false;
    }

    static Regex ToRegex(string pattern) {
        var anchored = pattern.TrimEnd('/').Contains('/');
        var text = pattern.Trim('/');
        var regex = new StringBuilder(anchored ? "^" : "^(.*/)?");
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*') {
                // '**/' is any number of directories, none included; '**' at
                // the end, anything.
                if (i + 2 < text.Length && text[i + 2] == '/') {
                    regex.Append("(.*/)?");
                    i += 2;
                } else {
                    regex.Append(".*");
                    i++;
                }
            } else if (c == '*') {
                regex.Append("[^/]*");
            } else if (c == '?') {
                regex.Append("[^/]");
            } else {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant);
    }
}
