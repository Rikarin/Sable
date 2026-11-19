using System.Collections.Immutable;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>Which member list a node came out of.</summary>
internal enum MemberContext {
    /// <summary>A compilation unit or an ordinary type body.</summary>
    Declaration,

    /// <summary>A union body, where a bare identifier is a case.</summary>
    Union,

    /// <summary>An enum body, where a bare identifier is a case.</summary>
    Enum
}

/// <summary>The first and last token indices a member's parse read.</summary>
internal readonly record struct TokenExtent(int Lowest, int Highest);

/// <summary>
///     The green nodes an incremental parse may reuse, keyed by the token index
///     they start at in the new stream.
/// </summary>
/// <remarks>
///     <para>
///         Reuse is only offered for whole members. A member's parse depends on
///         nothing but the tokens it read, so while every one of those is unchanged,
///         dropping its old green node straight into the new tree gives exactly what
///         reparsing it would have given, for free. Those are mostly its own, but
///         not only: deciding whether <c>e&lt;r</c> names a record's type can look for
///         its <c>&gt;</c> past the member's closing brace and into the next member,
///         and a statement looks back at the token before it. The parser notes the tokens each member read
///         (<see cref="TokenExtent" />), and those are what must be unchanged.
///     </para>
///     <para>
///         The context is recorded because a union body and a type body read a
///         bare identifier differently; a node is only offered back to the same kind
///         of member list it came from.
///     </para>
/// </remarks>
internal sealed class IncrementalReuse {
    readonly Dictionary<int, Entry> _entries = [];

    /// <param name="Behind">How many tokens before its first the member's parse read.</param>
    /// <param name="Ahead">How many tokens past its first the member's parse read.</param>
    private readonly record struct Entry(GreenNode Node, int TokenCount, MemberContext Context, int Behind, int Ahead);

    public int Count => _entries.Count;

    /// <summary>
    ///     The member to reuse at a token index, with the tokens its parse read
    ///     as counts from that index: <paramref name="behind" /> before it and
    ///     <paramref name="ahead" /> after it.
    /// </summary>
    public bool TryGet(
        int tokenIndex,
        MemberContext context,
        out GreenNode node,
        out int tokenCount,
        out int behind,
        out int ahead
    ) {
        if (_entries.TryGetValue(tokenIndex, out var entry) && entry.Context == context) {
            node = entry.Node;
            tokenCount = entry.TokenCount;
            behind = entry.Behind;
            ahead = entry.Ahead;
            return true;
        }

        node = null!;
        tokenCount = 0;
        behind = 0;
        ahead = 0;
        return false;
    }

    /// <summary>
    ///     Collects the members of an old tree that survive an edit, translating
    ///     their old token indices into new ones.
    /// </summary>
    int _cursor;

    int[] _diagnosticPositions = [];

    ImmutableArray<GreenToken> _oldTokens;
    IReadOnlyDictionary<GreenNode, TokenExtent> _extents = null!;
    int _newTokenCount;
    int[] _reflagged = [];

    public static IncrementalReuse Build(
        GreenNode oldRoot,
        ImmutableArray<GreenToken> oldTokens,
        IReadOnlyDictionary<GreenNode, TokenExtent> oldExtents,
        IReadOnlyList<Diagnostic> oldDiagnostics,
        in IncrementalLexResult lexResult
    ) {
        var reuse = new IncrementalReuse {
            _oldTokens = oldTokens,
            _extents = oldExtents,
            _newTokenCount = lexResult.Tokens.Length,
            _reflagged = lexResult.ReflaggedIndices,
            // Members that produced a diagnostic are never reused, so that the
            // parser's diagnostics can simply be regenerated.
            _diagnosticPositions = [
                ..oldDiagnostics
                    .Select(static d => d.Span.Start)
                    .Order()
            ]
        };

        reuse.Collect(
            oldRoot,
            0,
            MemberContext.Declaration,
            lexResult.OldPositions,
            lexResult.ReusedPrefixCount,
            lexResult.OldSuffixStartIndex,
            lexResult.NewSuffixStartIndex
        );

        return reuse;
    }

    /// <summary>
    ///     Moves the cursor to a position and returns its token index, or -1 when
    ///     no token starts exactly there. The traversal is in source order, so the
    ///     cursor only ever moves forward.
    /// </summary>
    int Advance(int[] positions, int position) {
        while (_cursor < positions.Length && positions[_cursor] < position) {
            _cursor++;
        }

        return _cursor < positions.Length && positions[_cursor] == position ? _cursor : -1;
    }

    void Collect(
        GreenNode node,
        int position,
        MemberContext context,
        int[] positions,
        int prefixCount,
        int oldSuffixStart,
        int newSuffixStart
    ) {
        var offset = position;

        for (var i = 0; i < node.SlotCount; i++) {
            var slot = node.GetSlot(i);
            if (slot is null) {
                continue;
            }

            if (slot is GreenList list && IsMemberList(list)) {
                var childOffset = offset;
                var childContext = ContextOf(node.Kind);

                foreach (var child in list.Children) {
                    var startIndex = Advance(positions, childOffset);

                    // Nested members are recorded while the cursor is inside
                    // this child, before it moves on to the child's end.
                    Collect(
                        child,
                        childOffset,
                        childContext,
                        positions,
                        prefixCount,
                        oldSuffixStart,
                        newSuffixStart
                    );

                    var endIndex = Advance(positions, childOffset + child.FullWidth);

                    Record(
                        child,
                        childOffset,
                        startIndex,
                        endIndex,
                        childContext,
                        prefixCount,
                        oldSuffixStart,
                        newSuffixStart
                    );

                    childOffset += child.FullWidth;
                }
            } else if (IsDeclarationLike(slot.Kind)) {
                Collect(
                    slot,
                    offset,
                    context,
                    positions,
                    prefixCount,
                    oldSuffixStart,
                    newSuffixStart
                );
            }

            offset += slot.FullWidth;
        }
    }

    bool HasDiagnosticIn(int start, int end) {
        if (_diagnosticPositions.Length == 0) {
            return false;
        }

        var index = Array.BinarySearch(_diagnosticPositions, start);
        if (index < 0) {
            index = ~index;
        }

        return index < _diagnosticPositions.Length && _diagnosticPositions[index] < end;
    }

    void Record(
        GreenNode child,
        int childPosition,
        int startIndex,
        int endIndex,
        MemberContext context,
        int prefixCount,
        int oldSuffixStart,
        int newSuffixStart
    ) {
        if (child.FullWidth == 0) {
            return;
        }

        if (startIndex < 0 || endIndex < 0 || endIndex <= startIndex) {
            return;
        }

        // A parse error is often reported at the token after the member that
        // caused it — 'expected X but found Y' points at Y. So the window runs
        // to the end of that next token's text, inclusive: a member whose
        // parse may have produced such an error is reparsed, and says it again.
        var windowEnd = childPosition + child.FullWidth;
        if (endIndex < _oldTokens.Length) {
            windowEnd += _oldTokens[endIndex].LeadingTriviaWidth + _oldTokens[endIndex].Width;
        }

        if (HasDiagnosticIn(childPosition, windowEnd + 1)) {
            return;
        }

        // A member with no note of what it read (none should be without
        // one) is not known to be safe.
        if (!_extents.TryGetValue(child, out var extent)) {
            return;
        }

        var tokenCount = endIndex - startIndex;

        // Every token the parse read, its own and the one after it at least.
        var lowest = Math.Min(extent.Lowest, startIndex);
        var highest = Math.Max(extent.Highest, endIndex);
        var behind = startIndex - lowest;
        var ahead = highest - startIndex;

        // Entirely inside the untouched prefix: indices are unchanged.
        if (highest < prefixCount) {
            if (SameTokens(lowest, highest)) {
                _entries[startIndex] = new(child, tokenCount, context, behind, ahead);
            }

            return;
        }

        // Entirely inside the reused suffix: shift into new-stream indices.
        if (lowest >= oldSuffixStart) {
            var shift = newSuffixStart - oldSuffixStart;
            if (SameTokens(lowest + shift, highest + shift)) {
                _entries[startIndex + shift] = new(child, tokenCount, context, behind, ahead);
            }
        }
    }

    /// <summary>
    ///     True when no token from <paramref name="first" /> to
    ///     <paramref name="last" />, new-stream indices, had its flag changed:
    ///     the copied tokens there are the very ones the member was parsed from.
    /// </summary>
    /// <remarks>
    ///     Text alone is not enough. Whether a token ends a statement depends on
    ///     the line after it and on brackets far before it (spec/lexical.md §4),
    ///     and an edit elsewhere can change that. The incremental lexer lists
    ///     the copied tokens whose flag changed; none may be among those the
    ///     member's parse read, nor be the token after it, since a member ends
    ///     where its last token's flag and the next token say it does.
    /// </remarks>
    bool SameTokens(int first, int last) {
        if (last >= _newTokenCount) {
            last = _newTokenCount - 1;
        }

        var index = Array.BinarySearch(_reflagged, first);
        if (index < 0) {
            index = ~index;
        }

        return index >= _reflagged.Length || _reflagged[index] > last;
    }

    static bool IsMemberList(GreenList list) {
        if (list.SlotCount == 0) {
            return false;
        }

        var first = list.Children[0];
        return IsMemberKind(first.Kind);
    }

    /// <summary>
    ///     The kinds that actually appear in a member list. Listed explicitly
    ///     rather than by range, because the declaration range also holds helper
    ///     nodes — attributes, parameter lists, where clauses — and an attribute
    ///     list must never look like a member list.
    /// </summary>
    static bool IsMemberKind(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.FunctionDeclaration
                or SyntaxKind.FieldDeclaration
                or SyntaxKind.ConstDeclaration
                or SyntaxKind.ComptimeDeclaration
                or SyntaxKind.InitDeclaration
                or SyntaxKind.DeinitDeclaration
                or SyntaxKind.SubscriptDeclaration
                or SyntaxKind.OperatorDeclaration => true,
            SyntaxKind.StructDeclaration
                or SyntaxKind.ClassDeclaration
                or SyntaxKind.ActorDeclaration
                or SyntaxKind.ProtocolDeclaration
                or SyntaxKind.UnionDeclaration
                or SyntaxKind.EnumDeclaration
                or SyntaxKind.ExtensionDeclaration => true,
            SyntaxKind.AliasDeclaration
                or SyntaxKind.AssociatedTypeDeclaration => true,
            SyntaxKind.UnionCaseDeclaration or SyntaxKind.EnumCaseDeclaration => true,
            SyntaxKind.IncompleteDeclaration => true,
            _ => false,
        };

    /// <summary>Declaration nodes are the only ones worth descending into.</summary>
    static bool IsDeclarationLike(SyntaxKind kind) => kind is SyntaxKind.CompilationUnit || IsMemberKind(kind);

    static MemberContext ContextOf(SyntaxKind parentKind) =>
        parentKind switch {
            SyntaxKind.UnionDeclaration => MemberContext.Union,
            SyntaxKind.EnumDeclaration => MemberContext.Enum,
            _ => MemberContext.Declaration,
        };
}
