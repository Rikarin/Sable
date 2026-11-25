using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Where a symbol read from a library's interface is declared in the
///     library's source: the file, and the declaration's place and kind in it.
///     The file is parsed when the declaration is first asked for.
/// </summary>
internal sealed record SymbolSource(SyntaxTree Tree, int Start, int Length, SyntaxKind Kind) {
    /// <summary>The place a declaration is at in its file.</summary>
    public static SymbolSource Of(SyntaxTree tree, SyntaxNode declaration) =>
        new(tree, declaration.Span.Start, declaration.Span.Length, declaration.Kind);

    /// <summary>The declaration, in the parsed file.</summary>
    public SyntaxNode Find() {
        var span = new TextSpan(Start, Length);
        var node = Tree.Root;
        while (true) {
            if (node.Kind == Kind && node.Span == span) {
                return node;
            }

            SyntaxNode? inside = null;
            foreach (var child in node.ChildNodes()) {
                if (child.Span.Start <= Start && child.Span.End >= span.End) {
                    inside = child;
                    break;
                }
            }

            node = inside ?? throw new InvalidOperationException($"'{Tree.Text.FileName}' has no {Kind} at {span}");
        }
    }
}
