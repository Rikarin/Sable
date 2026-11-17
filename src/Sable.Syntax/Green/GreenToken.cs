using System.Collections.Immutable;
using System.Text;

namespace Sable.Syntax.Green;

/// <summary>A green trivia node: width and text only.</summary>
internal sealed class GreenTrivia(SyntaxKind kind, string text)
    : GreenNode(kind, text.Length) {
    /// <summary>
    ///     Shared instances for the trivia that dominates real source: a line
    ///     break, and indentation up to 32 columns. Green nodes are immutable and
    ///     position-independent, so one instance can appear anywhere in any tree.
    /// </summary>
    static readonly GreenTrivia[] Spaces = CreateSpaces();

    static readonly GreenTrivia[] Tabs = CreateTabs();

    public static readonly GreenTrivia LineFeed =
        new(SyntaxKind.NewLineTrivia, "\n") { IsShared = true };

    public static readonly GreenTrivia CarriageReturnLineFeed =
        new(SyntaxKind.NewLineTrivia, "\r\n") { IsShared = true };

    public static readonly GreenTrivia CarriageReturn =
        new(SyntaxKind.NewLineTrivia, "\r") { IsShared = true };

    static GreenTrivia[] CreateSpaces() {
        var result = new GreenTrivia[33];
        for (var i = 1; i < result.Length; i++) {
            result[i] = new(SyntaxKind.WhitespaceTrivia, new(' ', i)) { IsShared = true };
        }

        return result;
    }

    static GreenTrivia[] CreateTabs() {
        var result = new GreenTrivia[9];
        for (var i = 1; i < result.Length; i++) {
            result[i] = new(SyntaxKind.WhitespaceTrivia, new('\t', i)) { IsShared = true };
        }

        return result;
    }

    /// <summary>
    ///     True for the shared instances above. Only tokens whose trivia is all
    ///     shared can be cached, because the cache compares trivia by reference.
    /// </summary>
    public bool IsShared { get; private init; }

    /// <summary>Returns a shared instance when the text is common, else a new one.</summary>
    public static GreenTrivia Create(SyntaxKind kind, ReadOnlySpan<char> text) {
        switch (kind) {
            case SyntaxKind.NewLineTrivia when text.Length == 1 && text[0] == '\n':
                return LineFeed;
            case SyntaxKind.NewLineTrivia when text.Length == 1 && text[0] == '\r':
                return CarriageReturn;
            case SyntaxKind.NewLineTrivia when text.Length == 2:
                return CarriageReturnLineFeed;

            case SyntaxKind.WhitespaceTrivia when text.Length < Spaces.Length: {
                var allSpaces = true;
                var allTabs = text.Length < Tabs.Length;
                foreach (var c in text) {
                    if (c != ' ') {
                        allSpaces = false;
                    }

                    if (c != '\t') {
                        allTabs = false;
                    }
                }

                if (allSpaces) {
                    return Spaces[text.Length];
                }

                if (allTabs) {
                    return Tabs[text.Length];
                }

                break;
            }
        }

        return new(kind, text.ToString());
    }

    public string Text { get; } = text;

    public override bool IsTrivia => true;
    public override int LeadingTriviaWidth => 0;
    public override int TrailingTriviaWidth => 0;

    public override void WriteTo(StringBuilder builder, bool leading, bool trailing) => builder.Append(Text);
}

/// <summary>
///     A green token: its text, decoded value, and attached trivia, all without
///     absolute positions.
/// </summary>
internal sealed class GreenToken : GreenNode {
    public string Text { get; }
    public object? Value { get; }

    /// <summary>
    ///     Segments of an interpolated string. Spans are relative to the start of
    ///     the token's text; the red facade offsets them to absolute positions.
    /// </summary>
    public ImmutableArray<StringPart> Parts { get; }

    public ImmutableArray<GreenTrivia> Leading { get; }
    public ImmutableArray<GreenTrivia> Trailing { get; }

    /// <summary>True when a statement break occurs immediately after this token.</summary>
    public bool TerminatesStatement { get; }

    /// <summary>True when this token was synthesised by error recovery.</summary>
    public bool IsMissing { get; }

    public GreenToken(
        SyntaxKind kind,
        string text,
        ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing,
        object? value = null,
        ImmutableArray<StringPart> parts = default,
        bool terminatesStatement = false,
        bool isMissing = false
    )
        : base(kind, 0) {
        Text = text;
        Value = value;
        Parts = parts.IsDefault ? [] : parts;
        Leading = leading;
        Trailing = trailing;
        TerminatesStatement = terminatesStatement;
        IsMissing = isMissing;

        var width = text.Length;
        foreach (var t in leading) {
            width += t.FullWidth;
        }

        foreach (var t in trailing) {
            width += t.FullWidth;
        }

        FullWidth = width;
    }

    public override bool IsToken => true;

    public override int LeadingTriviaWidth {
        get {
            var width = 0;
            foreach (var t in Leading) {
                width += t.FullWidth;
            }

            return width;
        }
    }

    public override int TrailingTriviaWidth {
        get {
            var width = 0;
            foreach (var t in Trailing) {
                width += t.FullWidth;
            }

            return width;
        }
    }

    public GreenToken WithTerminatesStatement(bool value) =>
        value == TerminatesStatement
            ? this
            : GreenTokenCache.Create(Kind, Text, Leading, Trailing, Value, Parts, value, IsMissing);

    public GreenToken WithTrivia(
        ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing
    ) =>
        new(Kind, Text, leading, trailing, Value, Parts, TerminatesStatement, IsMissing);

    public override void WriteTo(StringBuilder builder, bool leading, bool trailing) {
        if (leading) {
            foreach (var t in Leading) {
                builder.Append(t.Text);
            }
        }

        builder.Append(Text);

        if (trailing) {
            foreach (var t in Trailing) {
                builder.Append(t.Text);
            }
        }
    }

    public override string ToString() => Text;
}

/// <summary>
///     A per-thread cache of green tokens, keyed by kind, text and trivia.
/// </summary>
/// <remarks>
///     Real source repeats the same (token, surrounding whitespace) pairs
///     relentlessly — a <c>{</c> after one space, a newline then four spaces then
///     an identifier. Because green tokens are immutable and carry no position,
///     one instance serves every occurrence, which removes most of the lexer's
///     allocations. Roslyn's <c>SyntaxNodeCache</c> does the same thing.
/// </remarks>
internal static class GreenTokenCache {
    const int SizeBits = 16;
    const int Size = 1 << SizeBits;

    [ThreadStatic]
    static GreenToken?[]? _entries;

    public static GreenToken Create(
        SyntaxKind kind,
        string text,
        ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing,
        object? value = null,
        ImmutableArray<StringPart> parts = default,
        bool terminatesStatement = false,
        bool isMissing = false
    ) {
        if (!IsCacheable(value, parts, leading, trailing)) {
            return new(kind, text, leading, trailing, value, parts, terminatesStatement, isMissing);
        }

        var entries = _entries ??= new GreenToken?[Size];
        var index = Hash(kind, text, leading, trailing, terminatesStatement) & (Size - 1);

        var cached = entries[index];
        if (cached is not null
            && cached.Kind == kind
            && ReferenceEquals(cached.Text, text)
            && cached.TerminatesStatement == terminatesStatement
            && cached.IsMissing == isMissing
            && SameTrivia(cached.Leading, leading)
            && SameTrivia(cached.Trailing, trailing)) {
            return cached;
        }

        var created = new GreenToken(kind, text, leading, trailing, value, parts, terminatesStatement, isMissing);
        entries[index] = created;
        return created;
    }

    static bool IsCacheable(
        object? value,
        ImmutableArray<StringPart> parts,
        ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing
    ) {
        // A decoded value or interpolation segments make the token unique.
        if (value is not null || !parts.IsDefaultOrEmpty) {
            return false;
        }

        return IsCacheableTrivia(leading) && IsCacheableTrivia(trailing);
    }

    static bool IsCacheableTrivia(ImmutableArray<GreenTrivia> trivia) {
        if (trivia.IsDefaultOrEmpty) {
            return true;
        }

        if (trivia.Length > 2) {
            return false;
        }

        foreach (var item in trivia) {
            if (!item.IsShared) {
                return false;
            }
        }

        return true;
    }

    static bool SameTrivia(
        ImmutableArray<GreenTrivia> left,
        ImmutableArray<GreenTrivia> right
    ) {
        if (left.Length != right.Length) {
            return false;
        }

        for (var i = 0; i < left.Length; i++) {
            if (!ReferenceEquals(left[i], right[i])) {
                return false;
            }
        }

        return true;
    }

    static int Hash(
        SyntaxKind kind,
        string text,
        ImmutableArray<GreenTrivia> leading,
        ImmutableArray<GreenTrivia> trailing,
        bool terminatesStatement
    ) {
        var hash = (int)kind * 397;
        hash = (hash * 31) ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(text);
        hash = (hash * 31) ^ (terminatesStatement ? 1 : 0);

        if (!leading.IsDefaultOrEmpty) {
            foreach (var item in leading) {
                hash = (hash * 31)
                    ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
            }
        }

        if (!trailing.IsDefaultOrEmpty) {
            foreach (var item in trailing) {
                hash = (hash * 29)
                    ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
            }
        }

        return hash & int.MaxValue;
    }
}
