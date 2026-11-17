namespace Sable.Syntax;

/// <summary>A tree child, which is either a node or a token.</summary>
public readonly struct SyntaxNodeOrToken : IEquatable<SyntaxNodeOrToken> {
    readonly SyntaxNode? _node;
    readonly SyntaxToken _token;

    internal SyntaxNodeOrToken(SyntaxNode node) {
        _node = node;
        _token = default;
    }

    internal SyntaxNodeOrToken(SyntaxToken token) {
        _node = null;
        _token = token;
    }

    public bool IsNode => _node is not null;
    public bool IsToken => _node is null;

    public SyntaxNode? AsNode() => _node;
    public SyntaxToken AsToken() => _token;

    public SyntaxKind Kind => _node?.Kind ?? _token.Kind;
    public TextSpan Span => _node?.Span ?? _token.Span;
    public TextSpan FullSpan => _node?.FullSpan ?? _token.FullSpan;
    public int Position => _node?.Position ?? _token.Position;

    public string ToFullString() => _node?.ToFullString() ?? _token.ToFullString();
    public override string ToString() => _node?.ToString() ?? _token.ToString();

    public static implicit operator SyntaxNodeOrToken(SyntaxNode node) => new(node);
    public static implicit operator SyntaxNodeOrToken(SyntaxToken token) => new(token);

    public bool Equals(SyntaxNodeOrToken other) => ReferenceEquals(_node, other._node) && _token == other._token;

    public override bool Equals(object? obj) => obj is SyntaxNodeOrToken other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_node, _token);
    public static bool operator ==(SyntaxNodeOrToken l, SyntaxNodeOrToken r) => l.Equals(r);
    public static bool operator !=(SyntaxNodeOrToken l, SyntaxNodeOrToken r) => !l.Equals(r);
}
