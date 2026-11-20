using System.Text;

namespace Sable.Syntax.Green;

/// <summary>
///     The internal, immutable, position-independent half of the syntax tree.
/// </summary>
/// <remarks>
///     <para>
///         A green node knows its width but not its absolute position, which is
///         what makes it shareable between tree versions: an incremental reparse can
///         reuse every green node outside the damaged span, and only the thin red
///         facades above them are rebuilt.
///     </para>
///     <para>
///         Green nodes are never exposed from the public API. Consumers see the
///         red facades — <see cref="SyntaxNode" />, <see cref="SyntaxToken" /> and
///         <see cref="SyntaxTrivia" /> — which add a parent pointer and an absolute
///         position.
///     </para>
/// </remarks>
internal abstract class GreenNode(SyntaxKind kind, int fullWidth) {
    public SyntaxKind Kind { get; } = kind;

    /// <summary>Width including leading and trailing trivia.</summary>
    public int FullWidth { get; protected set; } = fullWidth;

    public virtual int SlotCount => 0;
    public virtual GreenNode? GetSlot(int index) => null;

    public virtual bool IsToken => false;
    public virtual bool IsTrivia => false;
    public virtual bool IsList => false;

    /// <summary>Width of the leading trivia of the first token beneath this node.</summary>
    public virtual int LeadingTriviaWidth {
        get {
            if (FullWidth == 0) {
                return 0;
            }

            var first = FirstTerminal();
            return first?.LeadingTriviaWidth ?? 0;
        }
    }

    /// <summary>Width of the trailing trivia of the last token beneath this node.</summary>
    public virtual int TrailingTriviaWidth {
        get {
            if (FullWidth == 0) {
                return 0;
            }

            var last = LastTerminal();
            return last?.TrailingTriviaWidth ?? 0;
        }
    }

    /// <summary>Width excluding leading and trailing trivia.</summary>
    public int Width => FullWidth - LeadingTriviaWidth - TrailingTriviaWidth;

    public GreenNode? FirstTerminal() {
        var node = this;
        while (!node.IsToken) {
            GreenNode? next = null;
            for (var i = 0; i < node.SlotCount; i++) {
                var slot = node.GetSlot(i);
                if (slot is not null && slot.FullWidth > 0) {
                    next = slot;
                    break;
                }
            }

            if (next is null) {
                return null;
            }

            node = next;
        }

        return node;
    }

    public GreenNode? LastTerminal() {
        var node = this;
        while (!node.IsToken) {
            GreenNode? next = null;
            for (var i = node.SlotCount - 1; i >= 0; i--) {
                var slot = node.GetSlot(i);
                if (slot is not null && slot.FullWidth > 0) {
                    next = slot;
                    break;
                }
            }

            if (next is null) {
                return null;
            }

            node = next;
        }

        return node;
    }

    /// <summary>Appends this node's text, optionally including the outer trivia.</summary>
    /// <remarks>
    ///     The node's own leading and trailing trivia are those of its first
    ///     and last slots that hold any text: an absent or empty slot at either
    ///     end (an attribute's missing argument list) does not make the
    ///     trivia before it the node's inside.
    /// </remarks>
    public virtual void WriteTo(StringBuilder builder, bool leading, bool trailing) {
        var first = 0;
        while (first < SlotCount && GetSlot(first) is not { FullWidth: > 0 }) {
            first++;
        }

        var last = SlotCount - 1;
        while (last > first && GetSlot(last) is not { FullWidth: > 0 }) {
            last--;
        }

        for (var i = first; i <= last; i++) {
            var slot = GetSlot(i);
            slot?.WriteTo(builder, leading || i > first, trailing || i < last);
        }
    }

    public string ToFullString() {
        var builder = new StringBuilder(FullWidth);
        WriteTo(builder, true, true);
        return builder.ToString();
    }

    public override string ToString() {
        var builder = new StringBuilder(Width);
        WriteTo(builder, false, false);
        return builder.ToString();
    }

    /// <summary>Creates the red facade for this node. Tokens and trivia never do this.</summary>
    public virtual SyntaxNode CreateRed(SyntaxNode? parent, int position) =>
        throw new InvalidOperationException($"{Kind} has no red node representation");
}
