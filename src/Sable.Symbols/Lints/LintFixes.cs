using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>Applying the fixes diagnostics carry to a file's text: what <c>sable fix</c> does with each file.</summary>
public static class LintFixes {
    /// <summary>
    ///     The text with each diagnostic's first fix applied, and how many
    ///     were. A fix whose edits overlap one already taken is left for the
    ///     next round: by then the file has been checked again, and the
    ///     second fix may no longer be wanted, or may be different.
    /// </summary>
    public static (string Text, int Applied) Apply(string text, IEnumerable<Diagnostic> diagnostics) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var taken = new List<TextChange>();
        var applied = 0;
        foreach (var fix in diagnostics.Where(static d => !d.Fixes.IsEmpty).OrderBy(static d => d.Span.Start).Select(static d => d.Fixes[0])) {
            if (fix.Changes.Any(change => change.Span.End > text.Length || taken.Exists(other => Overlap(change.Span, other.Span)))) {
                continue;
            }

            taken.AddRange(fix.Changes);
            applied++;
        }

        // From the end, so each edit's span still means what it meant.
        foreach (var change in taken.OrderByDescending(static c => c.Span.Start).ThenByDescending(static c => c.Span.End)) {
            text = string.Concat(text.AsSpan(0, change.Span.Start), change.NewText, text.AsSpan(change.Span.End));
        }

        return (text, applied);
    }

    /// <summary>
    ///     Whether two edits touch: they share text, or both insert at one
    ///     point (whose order would be a guess), or one inserts inside the
    ///     other's span.
    /// </summary>
    static bool Overlap(TextSpan a, TextSpan b) =>
        a.Start == b.Start || (a.Start < b.End && b.Start < a.End);
}
