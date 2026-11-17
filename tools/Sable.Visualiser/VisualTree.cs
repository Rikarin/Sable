using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Visualiser;

/// <summary>What one entry in the visualiser is: a node, a token or a trivia.</summary>
public enum VisualCategory { Node, Token, Trivia }

/// <summary>
/// One entry in the tree the visualiser shows, with everything the property
/// pane needs already worked out, so both renderers read the same thing.
/// </summary>
public sealed record VisualItem(
    VisualCategory Category,
    SyntaxKind Kind,
    TextSpan Span,
    TextSpan FullSpan,
    string? Text,
    bool IsMissing,
    string? Type,
    string? Symbol,
    string? SymbolKind,
    IReadOnlyList<VisualItem> Children);

/// <summary>Everything about one file the visualiser shows.</summary>
public sealed record VisualDocument(
    string FileName,
    string Source,
    VisualItem Root,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool HasSemantics);

/// <summary>
/// Builds the visualiser's view of a file: the full-fidelity tree, and, when
/// asked, what the semantic model says about each node.
/// </summary>
/// <remarks>
/// This is the equivalent of Roslyn's Syntax Visualiser that docs/04 §0.5
/// asks for. Every node, token and trivia is kept, including missing tokens
/// and skipped text, because the point of the tool is to see exactly what
/// the parser made of the source — broken source above all.
/// </remarks>
public static class VisualTree
{
    public static VisualDocument Build(SourceText source, bool semantics = true)
    {
        var tree = SyntaxTree.Parse(source);

        SemanticModel? model = null;
        IReadOnlyList<Diagnostic> diagnostics = tree.Diagnostics;

        if (semantics)
        {
            var compilation = Compilation.Create([tree]);
            model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
            diagnostics = compilation.GetAllDiagnostics();
        }

        return new VisualDocument(
            source.FileName, source.Text, Node(tree.Root, model), diagnostics, semantics);
    }

    private static VisualItem Node(SyntaxNode node, SemanticModel? model)
    {
        var children = new List<VisualItem>();
        foreach (var child in node.ChildNodesAndTokens())
        {
            children.Add(child.IsNode
                ? Node(child.AsNode()!, model)
                : Token(child.AsToken()));
        }

        var (type, symbol, symbolKind) = Semantics(node, model);

        return new VisualItem(
            VisualCategory.Node, node.Kind, node.Span, node.FullSpan, null,
            node.IsMissing, type, symbol, symbolKind, children);
    }

    private static VisualItem Token(SyntaxToken token)
    {
        var children = new List<VisualItem>();
        foreach (var trivia in token.LeadingTrivia) children.Add(Trivia(trivia));
        foreach (var trivia in token.TrailingTrivia) children.Add(Trivia(trivia));

        return new VisualItem(
            VisualCategory.Token, token.Kind, token.Span, token.FullSpan, token.Text,
            token.IsMissing, null, null, null, children);
    }

    private static VisualItem Trivia(SyntaxTrivia trivia) => new(
        VisualCategory.Trivia, trivia.Kind, trivia.Span, trivia.Span, trivia.Text,
        false, null, null, null, []);

    /// <summary>
    /// What the semantic model says about a node. A failure is shown rather
    /// than hidden: a visualiser that stops on the code it is meant to explain
    /// is no use, and one that hides a compiler crash is worse.
    /// </summary>
    private static (string? Type, string? Symbol, string? SymbolKind) Semantics(
        SyntaxNode node, SemanticModel? model)
    {
        if (model is null) return (null, null, null);

        try
        {
            var type = node is ExpressionSyntax expression
                ? model.GetTypeOf(expression)?.ToDisplayString()
                : null;

            var symbol = model.GetSymbolOf(node);
            return (type, symbol?.ToString(), symbol?.Kind.ToString());
        }
        catch (Exception e)
        {
            return ($"<{e.GetType().Name}: {e.Message}>", null, null);
        }
    }
}
