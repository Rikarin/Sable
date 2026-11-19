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
    int[] OldPositions,
    int[] ReflaggedIndices) {
    /// <summary>The new index at which the reused suffix begins.</summary>
    public int NewSuffixStartIndex => Tokens.Length - ReusedSuffixCount;
}

/// <summary>
///     Re-lexes only the damaged part of a file, splicing the old token stream
///     back in on both sides.
/// </summary>
/// <remarks>
///     <para>
///         Green tokens carry no position, so a token after the edit is reused
///         as-is: nothing in it has to change. That is the whole reason the tree is
///         split into green and red halves.
///     </para>
///     <para>
///         Two conservative rules keep this sound. The prefix stops two tokens
///         short of the edit, because an edit can merge with its neighbours (typing a
///         second <c>/</c> turns two tokens into a comment), and further back
///         still when it ends in a run of <c>#</c>s that a raw string could
///         open with. And the suffix is only
///         reused once the new lexer lands exactly on an old token boundary past the
///         edit, which means the two streams have resynchronised.
///     </para>
/// </remarks>
internal static class IncrementalLexer {
    /// <summary>Tokens kept before the edit must end this many tokens early.</summary>
    const int PrefixSafetyMargin = 2;

    public static IncrementalLexResult Lex(
        SourceText newText,
        ParseOptions options,
        ImmutableArray<GreenToken> oldTokens,
        IReadOnlyList<Diagnostic> oldDiagnostics,
        bool oldHadDirectives,
        TextChange change
    ) {
        // Directives make the lexer stateful across tokens: how text lexes
        // depends on the #if branches open before it. A lexer started after
        // the reused prefix would start with none open, and resynchronising
        // is not simply a matter of matching positions. Those files re-lex,
        // from the start.
        var canReuseSuffix = !oldHadDirectives;

        var oldPositions = ComputePositions(oldTokens);
        var prefixCount = oldHadDirectives ? 0 : FindReusablePrefix(oldTokens, oldPositions, change.Span.Start);
        var prefixEnd = prefixCount == 0 ? 0 : oldPositions[prefixCount];

        var lexer = new Lexer(newText, options, prefixEnd);

        // Only the damaged middle is lexed; the two ends are copied.
        var middle = new List<GreenToken>(32);

        var changeEndNew = change.Span.Start + change.NewText.Length;
        var reusedSuffix = 0;
        var oldSuffixStart = oldTokens.Length;

        while (true) {
            var positionBefore = lexer.Position;

            if (canReuseSuffix && positionBefore > changeEndNew) {
                var oldIndex = FindTokenStartingAt(oldPositions, positionBefore - change.Delta, prefixCount);

                if (oldIndex >= 0 && lexer.IsResyncSafe) {
                    reusedSuffix = oldTokens.Length - oldIndex;
                    oldSuffixStart = oldIndex;
                    break;
                }
            }

            var token = lexer.LexNextToken();
            middle.Add(token);

            if (token.Kind == SyntaxKind.EndOfFile) {
                lexer.ReportUnterminatedConditionals();
                break;
            }
        }

        var array = new GreenToken[prefixCount + middle.Count + reusedSuffix];
        oldTokens.CopyTo(0, array, 0, prefixCount);
        middle.CopyTo(array, prefixCount);
        if (reusedSuffix > 0) {
            oldTokens.CopyTo(oldSuffixStart, array, prefixCount + middle.Count, reusedSuffix);
        }

        Lexer.MarkTerminators(array);

        // The copied tokens whose statement-ending flag the edit changed, in
        // new-stream indices. Re-marking replaces exactly those, so a
        // reference comparison finds them; a member spanning one cannot be
        // reused, and this lets reuse ask with a binary search.
        var reflagged = new List<int>();
        for (var i = 0; i < prefixCount; i++) {
            if (!ReferenceEquals(array[i], oldTokens[i])) {
                reflagged.Add(i);
            }
        }

        var newSuffixStart = prefixCount + middle.Count;
        for (var i = 0; i < reusedSuffix; i++) {
            if (!ReferenceEquals(array[newSuffixStart + i], oldTokens[oldSuffixStart + i])) {
                reflagged.Add(newSuffixStart + i);
            }
        }

        var tokens = System.Runtime.InteropServices.ImmutableCollectionsMarshal
            .AsImmutableArray(array);

        var diagnostics = MergeDiagnostics(
            oldDiagnostics,
            lexer.Diagnostics,
            prefixEnd,
            oldSuffixStart < oldTokens.Length ? oldPositions[oldSuffixStart] : int.MaxValue,
            change.Delta
        );

        return new(tokens, diagnostics, prefixCount, reusedSuffix, oldSuffixStart, oldPositions, [..reflagged]);
    }

    /// <summary>Prefix sums of token widths; entry i is where token i starts.</summary>
    static int[] ComputePositions(ImmutableArray<GreenToken> tokens) {
        var positions = new int[tokens.Length + 1];
        var offset = 0;
        for (var i = 0; i < tokens.Length; i++) {
            positions[i] = offset;
            offset += tokens[i].FullWidth;
        }

        positions[tokens.Length] = offset;
        return positions;
    }

    static int FindReusablePrefix(
        ImmutableArray<GreenToken> tokens,
        int[] positions,
        int changeStart
    ) {
        var count = 0;
        while (count < tokens.Length && positions[count + 1] < changeStart) {
            count++;
        }

        count = Math.Max(0, count - PrefixSafetyMargin);

        // A raw string opens with any number of '#'s, and whether a '#' is
        // one of them depends on what follows the whole run: a '"' typed after
        // '######' turns six tokens into one. So a run of adjacent '#'s is
        // never split; the prefix ends before it, however long it is.
        while (count > 0 && AreAdjacentHashes(tokens[count - 1], tokens[count])) {
            count--;
        }

        return count;
    }

    /// <summary>A '#' followed, with nothing between, by another.</summary>
    static bool AreAdjacentHashes(GreenToken token, GreenToken next) =>
        token.Kind == SyntaxKind.HashToken
        && next.Kind == SyntaxKind.HashToken
        && token.TrailingTriviaWidth == 0
        && next.LeadingTriviaWidth == 0;

    /// <summary>Index of the old token starting exactly at a position, or -1.</summary>
    static int FindTokenStartingAt(int[] positions, int position, int lowerBound) {
        if (position < 0) {
            return -1;
        }

        var low = lowerBound;
        var high = positions.Length - 2;
        while (low <= high) {
            var middle = (low + high) / 2;
            var value = positions[middle];
            if (value == position) {
                return middle;
            }

            if (value < position) {
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Keeps the lexical diagnostics of the regions that were not re-lexed,
    ///     shifting the ones after the edit.
    /// </summary>
    /// <remarks>
    ///     Only lexical ones. The parser runs over the whole spliced stream, so it
    ///     re-reports its own diagnostics for everything it did not reuse — and a
    ///     member it did reuse is never one that had a parser diagnostic, which is
    ///     what <see cref="IncrementalReuse" /> guarantees.
    /// </remarks>
    static IReadOnlyList<Diagnostic> MergeDiagnostics(
        IReadOnlyList<Diagnostic> old,
        IReadOnlyList<Diagnostic> fresh,
        int prefixEnd,
        int oldSuffixStart,
        int delta
    ) {
        var merged = new List<Diagnostic>(fresh);

        foreach (var diagnostic in old) {
            if (!IsLexical(diagnostic)) {
                continue;
            }

            if (diagnostic.Span.End <= prefixEnd) {
                merged.Add(diagnostic);
            } else if (diagnostic.Span.Start >= oldSuffixStart) {
                merged.Add(diagnostic with { Span = new(diagnostic.Span.Start + delta, diagnostic.Span.Length) });
            }
        }

        return merged;
    }

    /// <summary>Lexical diagnostics occupy S0001-S0999; see ErrorCode.</summary>
    static bool IsLexical(Diagnostic diagnostic) => (int)diagnostic.Code < 1000;
}
