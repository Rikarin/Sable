using System.Collections;
using System.Collections.Immutable;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>
///     A span of non-token source text attached to an adjacent token: whitespace,
///     line breaks, comments, the shebang line, and inactive <c>#if</c> regions.
/// </summary>
/// <remarks>
///     Trivia is retained rather than discarded so that the full-fidelity
///     round-trip invariant holds: concatenating <see cref="SyntaxToken.ToFullString" />
///     over every token of a file reproduces that file byte for byte.
/// </remarks>
public readonly struct SyntaxTrivia : IEquatable<SyntaxTrivia> {
    readonly GreenTrivia? _green;

    internal SyntaxTrivia(GreenTrivia green, int position) {
        _green = green;
        Position = position;
    }

    internal GreenTrivia? Green => _green;

    public SyntaxKind Kind => _green?.Kind ?? SyntaxKind.None;
    public string Text => _green?.Text ?? string.Empty;
    public int Position { get; }
    public int Width => _green?.FullWidth ?? 0;
    public TextSpan Span => new(Position, Width);

    public bool IsComment =>
        Kind is SyntaxKind.LineCommentTrivia
            or SyntaxKind.BlockCommentTrivia
            or SyntaxKind.DocCommentTrivia
            or SyntaxKind.ModuleDocCommentTrivia;

    public bool IsDocumentation =>
        Kind is SyntaxKind.DocCommentTrivia
            or SyntaxKind.ModuleDocCommentTrivia;

    public bool IsWhitespaceOrNewLine =>
        Kind is SyntaxKind.WhitespaceTrivia
            or SyntaxKind.NewLineTrivia;

    public override string ToString() => Text;

    public bool Equals(SyntaxTrivia other) => ReferenceEquals(_green, other._green) && Position == other.Position;

    public override bool Equals(object? obj) => obj is SyntaxTrivia other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_green, Position);
    public static bool operator ==(SyntaxTrivia left, SyntaxTrivia right) => left.Equals(right);
    public static bool operator !=(SyntaxTrivia left, SyntaxTrivia right) => !left.Equals(right);
}

/// <summary>An indexable list of trivia with absolute positions.</summary>
public readonly struct SyntaxTriviaList : IReadOnlyList<SyntaxTrivia> {
    readonly ImmutableArray<GreenTrivia> _green;
    readonly int _position;

    internal SyntaxTriviaList(ImmutableArray<GreenTrivia> green, int position) {
        _green = green;
        _position = position;
    }

    public int Count => _green.IsDefault ? 0 : _green.Length;

    public SyntaxTrivia this[int index] {
        get {
            var offset = _position;
            for (var i = 0; i < index; i++) {
                offset += _green[i].FullWidth;
            }

            return new SyntaxTrivia(_green[index], offset);
        }
    }

    public IEnumerator<SyntaxTrivia> GetEnumerator() {
        if (_green.IsDefault) {
            yield break;
        }

        var offset = _position;
        foreach (var trivia in _green) {
            yield return new SyntaxTrivia(trivia, offset);
            offset += trivia.FullWidth;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int Width {
        get {
            if (_green.IsDefault) {
                return 0;
            }

            var width = 0;
            foreach (var trivia in _green) {
                width += trivia.FullWidth;
            }

            return width;
        }
    }

    public bool Any(SyntaxKind kind) {
        if (_green.IsDefault) {
            return false;
        }

        foreach (var trivia in _green) {
            if (trivia.Kind == kind) {
                return true;
            }
        }

        return false;
    }

    public override string ToString() {
        if (_green.IsDefault) {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var trivia in _green) {
            builder.Append(trivia.Text);
        }

        return builder.ToString();
    }
}
