using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>
/// A node in the public syntax tree: a thin, lazily created facade over an
/// immutable <see cref="GreenNode"/>, adding a parent pointer and an absolute
/// position.
/// </summary>
/// <remarks>
/// Red nodes are created on demand as the tree is walked and cached in the
/// parent, so an unvisited subtree costs nothing beyond its green nodes.
/// </remarks>
public abstract class SyntaxNode
{
    internal GreenNode Green { get; }

    /// <summary>The containing node, or null for the root.</summary>
    public SyntaxNode? Parent { get; }

    /// <summary>Absolute start of this node including its leading trivia.</summary>
    public int Position { get; }

    private protected SyntaxNode(GreenNode green, SyntaxNode? parent, int position)
    {
        Green = green ?? throw new ArgumentNullException(nameof(green));
        Parent = parent;
        Position = position;
    }

    public SyntaxKind Kind => Green.Kind;

    /// <summary>Span including leading and trailing trivia.</summary>
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>Span of the node's own text, excluding outer trivia.</summary>
    public TextSpan Span => new(Position + Green.LeadingTriviaWidth, Green.Width);

    public int FullWidth => Green.FullWidth;
    public int Width => Green.Width;

    /// <summary>True when every token beneath this node was synthesised.</summary>
    public bool IsMissing
    {
        get
        {
            foreach (var token in DescendantTokens())
                if (!token.IsMissing) return false;
            return true;
        }
    }

    // -----------------------------------------------------------------------
    // Slot access, used by generated node classes
    // -----------------------------------------------------------------------

    /// <summary>Absolute position of the green slot at <paramref name="index"/>.</summary>
    private protected int GetChildPosition(int index)
    {
        var offset = Position;
        for (var i = 0; i < index; i++)
            offset += Green.GetSlot(i)?.FullWidth ?? 0;
        return offset;
    }

    /// <summary>Materialises and caches the red node for a child slot.</summary>
    private protected T GetRed<T>(ref T? field, int index) where T : SyntaxNode
    {
        var result = field;
        if (result is null)
        {
            var green = Green.GetSlot(index)
                ?? throw new InvalidOperationException(
                    $"{Kind} slot {index} is empty but was accessed as a required child");
            result = (T)green.CreateRed(this, GetChildPosition(index));
            Interlocked.CompareExchange(ref field, result, null);
            result = field!;
        }
        return result;
    }

    /// <summary>Materialises and caches an optional child slot.</summary>
    private protected T? GetRedOptional<T>(ref T? field, int index) where T : SyntaxNode
    {
        var green = Green.GetSlot(index);
        if (green is null) return null;

        var result = field;
        if (result is null)
        {
            result = (T)green.CreateRed(this, GetChildPosition(index));
            Interlocked.CompareExchange(ref field, result, null);
            result = field!;
        }
        return result;
    }

    private protected SyntaxToken GetToken(int index)
    {
        var green = Green.GetSlot(index);
        return green is GreenToken token
            ? new SyntaxToken(token, this, GetChildPosition(index))
            : default;
    }

    private protected SyntaxTokenList GetTokenList(int index) =>
        new(Green.GetSlot(index) as GreenList, this, GetChildPosition(index));

    private protected SyntaxList<T> GetList<T>(int index) where T : SyntaxNode =>
        new(Green.GetSlot(index) as GreenList, this, GetChildPosition(index));

    private protected SeparatedSyntaxList<T> GetSeparatedList<T>(int index) where T : SyntaxNode =>
        new(Green.GetSlot(index) as GreenList, this, GetChildPosition(index));

    /// <summary>Returns the cached red node for a slot, or null if it is a token or empty.</summary>
    internal abstract SyntaxNode? GetNodeSlot(int index);

    /// <summary>Dispatches to the visitor method for this node's concrete type.</summary>
    public abstract void Accept(SyntaxVisitor visitor);

    // -----------------------------------------------------------------------
    // Traversal
    // -----------------------------------------------------------------------

    public IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        var offset = Position;
        for (var i = 0; i < Green.SlotCount; i++)
        {
            var slot = Green.GetSlot(i);
            if (slot is null) continue;

            if (slot is GreenList list)
            {
                var itemOffset = offset;
                foreach (var child in list.Children)
                {
                    yield return child is GreenToken token
                        ? new SyntaxNodeOrToken(new SyntaxToken(token, this, itemOffset))
                        : new SyntaxNodeOrToken(child.CreateRed(this, itemOffset));
                    itemOffset += child.FullWidth;
                }
            }
            else if (slot is GreenToken token)
            {
                yield return new SyntaxNodeOrToken(new SyntaxToken(token, this, offset));
            }
            else
            {
                var node = GetNodeSlot(i) ?? slot.CreateRed(this, offset);
                yield return new SyntaxNodeOrToken(node);
            }

            offset += slot.FullWidth;
        }
    }

    public IEnumerable<SyntaxNode> ChildNodes()
    {
        foreach (var child in ChildNodesAndTokens())
            if (child.AsNode() is { } node) yield return node;
    }

    /// <summary>Every node beneath this one, in source order, excluding itself.</summary>
    public IEnumerable<SyntaxNode> DescendantNodes()
    {
        foreach (var child in ChildNodes())
        {
            yield return child;
            foreach (var descendant in child.DescendantNodes()) yield return descendant;
        }
    }

    /// <summary>This node and every node beneath it, in source order.</summary>
    public IEnumerable<SyntaxNode> DescendantNodesAndSelf()
    {
        yield return this;
        foreach (var descendant in DescendantNodes()) yield return descendant;
    }

    /// <summary>Every token beneath this node, in source order.</summary>
    public IEnumerable<SyntaxToken> DescendantTokens()
    {
        foreach (var child in ChildNodesAndTokens())
        {
            if (child.IsToken) yield return child.AsToken();
            else foreach (var token in child.AsNode()!.DescendantTokens()) yield return token;
        }
    }

    public SyntaxToken GetFirstToken()
    {
        foreach (var token in DescendantTokens()) return token;
        return default;
    }

    public SyntaxToken GetLastToken()
    {
        var last = default(SyntaxToken);
        foreach (var token in DescendantTokens()) last = token;
        return last;
    }

    /// <summary>The innermost node whose span contains <paramref name="position"/>.</summary>
    public SyntaxNode? FindNode(int position)
    {
        if (!FullSpan.Contains(position)) return null;
        foreach (var child in ChildNodes())
        {
            var found = child.FindNode(position);
            if (found is not null) return found;
        }
        return this;
    }

    /// <summary>The node's text, excluding leading and trailing trivia.</summary>
    public override string ToString() => Green.ToString();

    /// <summary>The node's text including all trivia; round-trips exactly.</summary>
    public string ToFullString() => Green.ToFullString();
}
