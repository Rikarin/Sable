namespace Sable.Syntax;

/// <summary>
///     What a file says before its declarations: the module it is in, and the
///     modules it imports, as written — <c>Sable.Core</c>.
/// </summary>
/// <param name="Module">The module the file declares, or null when it declares none.</param>
/// <param name="Imports">The modules it imports, in order.</param>
public sealed record SourceHeader(string? Module, IReadOnlyList<string> Imports) {
    static readonly SourceHeader None = new(null, []);

    /// <summary>A parsed file's header.</summary>
    public static SourceHeader Of(SyntaxNode root) =>
        root is CompilationUnitSyntax unit
            ? new(unit.Module?.Name.ToString().Trim(), [..unit.Imports.Select(static i => i.Name.ToString().Trim())])
            : None;

    /// <summary>
    ///     The header read off the text without parsing it, or null where the
    ///     lines before the first declaration do not plainly say: where one of
    ///     them is anything but blank, a line comment, or a whole
    ///     <c>module</c> or <c>import</c> declaration of a dotted name.
    /// </summary>
    public static SourceHeader? Scan(string text) {
        string? module = null;
        var imports = new List<string>();
        for (var at = 0; at < text.Length;) {
            var end = text.IndexOf('\n', at);
            var line = text.AsSpan(at, (end < 0 ? text.Length : end) - at).Trim();
            at = end < 0 ? text.Length : end + 1;
            if (line.IsEmpty || line.StartsWith("//")) {
                continue;
            }

            if (Declaration(line, "module") is { } declared) {
                // One, and before any import.
                if (module is not null || imports.Count > 0 || !IsDottedName(declared)) {
                    return null;
                }

                module = declared;
            } else if (Declaration(line, "import") is { } imported) {
                if (!IsDottedName(imported)) {
                    return null;
                }

                imports.Add(imported);
            } else if (line[0] is '#' or '/' || module is null) {
                return null;
            } else {
                break;
            }
        }

        return new(module, imports);
    }

    static string? Declaration(ReadOnlySpan<char> line, string keyword) {
        if (!line.StartsWith(keyword) || line.Length <= keyword.Length || line[keyword.Length] is not (' ' or '\t')) {
            return null;
        }

        var rest = line[keyword.Length..].Trim();
        var comment = rest.IndexOf("//");
        return (comment < 0 ? rest : rest[..comment].TrimEnd()).ToString();
    }

    static bool IsDottedName(string name) =>
        name.Split('.').All(static part => part.Length > 0 && !char.IsDigit(part[0]) && part.All(static c => char.IsLetterOrDigit(c) || c == '_'));
}
