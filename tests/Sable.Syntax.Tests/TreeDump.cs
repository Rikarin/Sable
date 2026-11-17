using System.Text;

namespace Sable.Syntax.Tests;

/// <summary>
///     Renders a tree as a compact S-expression, so precedence and shape
///     assertions read as one line instead of a chain of casts.
/// </summary>
/// <remarks>
///     Grouping punctuation is omitted; operators, identifiers, literals and
///     keywords are kept, because those are what the assertions are about.
/// </remarks>
internal static class TreeDump {
    public static string Dump(SyntaxNode node) {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    public static string Dump(SyntaxTree tree) => Dump(tree.Root);

    static void Write(StringBuilder builder, SyntaxNode node) {
        builder.Append('(').Append(KindName(node.Kind));

        foreach (var child in node.ChildNodesAndTokens()) {
            if (child.IsToken) {
                var token = child.AsToken();
                if (IsNoise(token.Kind)) {
                    continue;
                }

                builder.Append(' ');
                builder.Append(token.IsMissing ? "<missing>" : token.Text);
            } else {
                builder.Append(' ');
                Write(builder, child.AsNode()!);
            }
        }

        builder.Append(')');
    }

    /// <summary>Drops the "Expression"/"Type" noise so dumps stay readable.</summary>
    static string KindName(SyntaxKind kind) {
        var name = kind.ToString();
        if (name.EndsWith("Expression", StringComparison.Ordinal) && name.Length > "Expression".Length) {
            return name[..^"Expression".Length];
        }

        return name;
    }

    static bool IsNoise(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.OpenParenToken or SyntaxKind.CloseParenToken => true,
            SyntaxKind.OpenBracketToken or SyntaxKind.CloseBracketToken => true,
            SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken => true,
            SyntaxKind.CommaToken or SyntaxKind.ColonToken => true,
            SyntaxKind.EndOfFile => true,
            _ => false,
        };
}
