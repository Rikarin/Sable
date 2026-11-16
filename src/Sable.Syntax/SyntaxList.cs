using System.Collections;
using Sable.Syntax.Green;

namespace Sable.Syntax;

/// <summary>A list of sibling nodes of the same kind.</summary>
public readonly struct SyntaxList<T> : IReadOnlyList<T> where T : SyntaxNode
{
    private readonly GreenList? _green;
    private readonly SyntaxNode? _parent;
    private readonly int _position;

    internal SyntaxList(GreenList? green, SyntaxNode? parent, int position)
    {
        _green = green;
        _parent = parent;
        _position = position;
    }

    public int Count => _green?.SlotCount ?? 0;

    public T this[int index]
    {
        get
        {
            if (_green is null) throw new ArgumentOutOfRangeException(nameof(index));
            var offset = _position;
            for (var i = 0; i < index; i++) offset += _green.Children[i].FullWidth;
            return (T)_green.Children[index].CreateRed(_parent, offset);
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        if (_green is null) yield break;
        var offset = _position;
        foreach (var child in _green.Children)
        {
            yield return (T)child.CreateRed(_parent, offset);
            offset += child.FullWidth;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A list of nodes interleaved with separator tokens. Items occupy the even
/// green slots and separators the odd ones, so a list of <c>n</c> items with a
/// trailing separator has <c>2n</c> slots and one without has <c>2n - 1</c>.
/// </summary>
public readonly struct SeparatedSyntaxList<T> : IReadOnlyList<T> where T : SyntaxNode
{
    private readonly GreenList? _green;
    private readonly SyntaxNode? _parent;
    private readonly int _position;

    internal SeparatedSyntaxList(GreenList? green, SyntaxNode? parent, int position)
    {
        _green = green;
        _parent = parent;
        _position = position;
    }

    private int SlotCount => _green?.SlotCount ?? 0;

    public int Count => (SlotCount + 1) / 2;
    public int SeparatorCount => SlotCount / 2;

    public T this[int index]
    {
        get
        {
            if (_green is null) throw new ArgumentOutOfRangeException(nameof(index));
            return (T)_green.Children[index * 2].CreateRed(_parent, OffsetOfSlot(index * 2));
        }
    }

    public SyntaxToken GetSeparator(int index)
    {
        if (_green is null) return default;
        var slot = index * 2 + 1;
        if (slot >= SlotCount) return default;
        return new SyntaxToken((GreenToken)_green.Children[slot], _parent, OffsetOfSlot(slot));
    }

    /// <summary>True when a separator follows the final item.</summary>
    public bool HasTrailingSeparator => SlotCount > 0 && SlotCount % 2 == 0;

    private int OffsetOfSlot(int slot)
    {
        var offset = _position;
        for (var i = 0; i < slot; i++) offset += _green!.Children[i].FullWidth;
        return offset;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Items and separators together, in source order.</summary>
    public IEnumerable<SyntaxNodeOrToken> GetWithSeparators()
    {
        if (_green is null) yield break;
        var offset = _position;
        for (var i = 0; i < SlotCount; i++)
        {
            var child = _green.Children[i];
            yield return child is GreenToken token
                ? new SyntaxNodeOrToken(new SyntaxToken(token, _parent, offset))
                : new SyntaxNodeOrToken(child.CreateRed(_parent, offset));
            offset += child.FullWidth;
        }
    }
}
