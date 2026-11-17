using System.Collections.Immutable;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>The outcome of an incremental lex: which old tokens survived.</summary>
internal readonly record struct IncrementalLexResult(
    ImmutableArray<GreenToken> Tokens,
    IReadOnlyList<Diagnostic> Diagnostics,
    int ReusedPrefixCount,
    int ReusedSuffixCount,
    int OldSuffixStartIndex,
    int[] OldPositions)
{
    /// <summary>The new index at which the reused suffix begins.</summary>
    public int NewSuffixStartIndex => Tokens.Length - ReusedSuffixCount;
}

/// <summary>
/// Re-lexes only the damaged part of a file, splicing the old token stream
/// back in on both sides.
/// </summary>
/// <remarks>
/// <para>Green tokens carry no position, so a token after the edit is reused
/// as-is: nothing in it has to change. That is the whole reason the tree is
/// split into green and red halves.</para>
/// <para>Two conservative rules keep this sound. The prefix stops two tokens
/// short of the edit, because an edit can merge with its neighbours (typing a
/// second <c>/</c> turns two tokens into a comment). And the suffix is only
/// reused once the new lexer lands exactly on an old token boundary past the
/// edit, which means the two streams have resynchronised.</para>
/// </remarks>
internal static class IncrementalLexer
{
    /// <summary>Tokens kept before the edit must end this many tokens early.</summary>
    private const int PrefixSafetyMargin = 2;

    public static IncrementalLexResult Lex(
        SourceText newText,
        ParseOptions options,
        ImmutableArray<GreenToken> oldTokens,
        IReadOnlyList<Diagnostic> oldDiagnostics,
        bool oldHadDirectives,
        TextChange change)
    {
        // Directives make the lexer stateful across tokens, so resynchronising
        // is not simply a matter of matching positions. Those files re-lex.
        var canReuseSuffix = !oldHadDirectives;

        var oldPositions = ComputePositions(oldTokens);
        var prefixCount = FindReusablePrefix(oldTokens, oldPositions, change.Span.Start);
        var prefixEnd = prefixCount == 0 ? 0 : oldPositions[prefixCount];

        var lexer = new Lexer(newText, options, prefixEnd);

        // Only the damaged middle is lexed; the two ends are copied.
        var middle = new List<GreenToken>(32);

        var changeEndNew = change.Span.Start + change.NewText.Length;
        var reusedSuffix = 0;
        var oldSuffixStart = oldTokens.Length;

        while (true)
        {
            var positionBefore = lexer.Position;

            if (canReuseSuffix && positionBefore > changeEndNew)
            {
                var oldIndex = FindTokenStartingAt(
                    oldPositions, positionBefore - change.Delta, prefixCount);

                if (oldIndex >= 0 && lexer.IsResyncSafe)
                {
                    reusedSuffix = oldTokens.Length - oldIndex;
                    oldSuffixStart = oldIndex;
                    break;
                }
            }

            var token = lexer.LexNextToken();
            middle.Add(token);

            if (token.Kind == SyntaxKind.EndOfFile)
            {
                lexer.ReportUnterminatedConditionals();
                break;
            }
        }

        var array = new GreenToken[prefixCount + middle.Count + reusedSuffix];
        oldTokens.CopyTo(0, array, 0, prefixCount);
        middle.CopyTo(array, prefixCount);
        if (reusedSuffix > 0)
            oldTokens.CopyTo(oldSuffixStart, array, prefixCount + middle.Count, reusedSuffix);

        Lexer.MarkTerminators(array);
        var tokens = System.Runtime.InteropServices.ImmutableCollectionsMarshal
            .AsImmutableArray(array);

        var diagnostics = MergeDiagnostics(
            oldDiagnostics, lexer.Diagnostics, prefixEnd,
            oldSuffixStart < oldTokens.Length ? oldPositions[oldSuffixStart] : int.MaxValue,
            change.Delta);

        return new IncrementalLexResult(
            tokens, diagnostics, prefixCount, reusedSuffix, oldSuffixStart, oldPositions);
    }

    /// <summary>Prefix sums of token widths; entry i is where token i starts.</summary>
    private static int[] ComputePositions(ImmutableArray<GreenToken> tokens)
    {
        var positions = new int[tokens.Length + 1];
        var offset = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            positions[i] = offset;
            offset += tokens[i].FullWidth;
        }
        positions[tokens.Length] = offset;
        return positions;
    }

    private static int FindReusablePrefix(
        ImmutableArray<GreenToken> tokens, int[] positions, int changeStart)
    {
        var count = 0;
        while (count < tokens.Length && positions[count + 1] < changeStart) count++;
        return Math.Max(0, count - PrefixSafetyMargin);
    }

    /// <summary>Index of the old token starting exactly at a position, or -1.</summary>
    private static int FindTokenStartingAt(int[] positions, int position, int lowerBound)
    {
        if (position < 0) return -1;

        var low = lowerBound;
        var high = positions.Length - 2;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var value = positions[middle];
            if (value == position) return middle;
            if (value < position) low = middle + 1; else high = middle - 1;
        }
        return -1;
    }

    /// <summary>
    /// Keeps the diagnostics of the regions that were not re-lexed, shifting
    /// the ones after the edit.
    /// </summary>
    private static IReadOnlyList<Diagnostic> MergeDiagnostics(
        IReadOnlyList<Diagnostic> old,
        IReadOnlyList<Diagnostic> fresh,
        int prefixEnd,
        int oldSuffixStart,
        int delta)
    {
        var merged = new List<Diagnostic>();

        foreach (var diagnostic in old)
            if (diagnostic.Span.End <= prefixEnd) merged.Add(diagnostic);

        merged.AddRange(fresh);

        foreach (var diagnostic in old)
        {
            if (diagnostic.Span.Start < oldSuffixStart) continue;
            merged.Add(diagnostic with
            {
                Span = new TextSpan(diagnostic.Span.Start + delta, diagnostic.Span.Length),
            });
        }

        merged.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        return merged;
    }
}
