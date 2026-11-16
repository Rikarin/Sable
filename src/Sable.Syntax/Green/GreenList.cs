using System.Collections.Immutable;

namespace Sable.Syntax.Green;

/// <summary>
/// A green list of child nodes, used for both plain lists and separated lists
/// (where separators occupy the odd slots).
/// </summary>
internal sealed class GreenList : GreenNode
{
    public static readonly GreenList Empty = new([]);

    private readonly ImmutableArray<GreenNode> _children;

    private GreenList(ImmutableArray<GreenNode> children)
        : base(SyntaxKind.None, 0)
    {
        _children = children;
        var width = 0;
        foreach (var child in children) width += child.FullWidth;
        FullWidth = width;
    }

    public static GreenList Create(ImmutableArray<GreenNode> children) =>
        children.IsDefaultOrEmpty ? Empty : new GreenList(children);

    public static GreenList Create(IEnumerable<GreenNode> children) =>
        Create([.. children]);

    public override bool IsList => true;
    public override int SlotCount => _children.Length;
    public override GreenNode? GetSlot(int index) => _children[index];

    public ImmutableArray<GreenNode> Children => _children;
}
