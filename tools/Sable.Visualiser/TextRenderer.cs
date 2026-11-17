using System.Text;
using Sable.Syntax;

namespace Sable.Visualiser;

/// <summary>Renders the tree as indented text, for a terminal.</summary>
public static class TextRenderer
{
    public static string Render(VisualDocument document, bool trivia = false)
    {
        var builder = new StringBuilder();
        Write(builder, document.Root, 0, trivia);

        if (document.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            foreach (var diagnostic in document.Diagnostics)
            {
                builder.Append(diagnostic.Id).Append(' ')
                    .Append(Format(diagnostic.Span)).Append(' ')
                    .AppendLine(diagnostic.Message);
            }
        }

        return builder.ToString();
    }

    private static void Write(StringBuilder builder, VisualItem item, int depth, bool trivia)
    {
        if (item.Category == VisualCategory.Trivia && !trivia) return;

        builder.Append(' ', depth * 2);
        builder.Append(item.Kind);
        builder.Append(' ').Append(Format(item.Span));

        if (item.Text is { } text) builder.Append(' ').Append(Quote(text));
        if (item.IsMissing) builder.Append(" <missing>");
        if (item.Type is { } type) builder.Append("  : ").Append(type);
        if (item.Symbol is { } symbol) builder.Append("  -> ").Append(item.SymbolKind)
            .Append(' ').Append(symbol);

        builder.AppendLine();

        foreach (var child in item.Children) Write(builder, child, depth + 1, trivia);
    }

    internal static string Format(TextSpan span) => $"[{span.Start}..{span.End})";

    private static string Quote(string text)
    {
        var escaped = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
