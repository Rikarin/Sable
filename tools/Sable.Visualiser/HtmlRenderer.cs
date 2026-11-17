using System.Reflection;
using System.Text.Json;

namespace Sable.Visualiser;

/// <summary>
///     Renders the tree as one self-contained HTML page: the source beside a
///     collapsible tree, with a property pane, and selection that goes both ways.
/// </summary>
/// <remarks>
///     The data is embedded as JSON in compact positional arrays, because a large
///     file has tens of thousands of entries and the page is meant to open from
///     disk with nothing else. The serializer's default encoder escapes
///     <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c>, so source text cannot close the
///     script element it sits in.
/// </remarks>
public static class HtmlRenderer {
    const string Placeholder = "/*__SABLE_DATA__*/null";

    public static string Render(VisualDocument document) {
        var data = new {
            file = document.FileName,
            source = document.Source,
            semantics = document.HasSemantics,
            root = Encode(document.Root),
            diagnostics = document.Diagnostics.Select(static d => new object[] {
                    d.Id, d.Severity.ToString(), d.Span.Start, d.Span.End, d.Message
                }
            )
        };

        var json = JsonSerializer.Serialize(data);
        return Template().Replace(Placeholder, json, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>
    ///         [category, kind, start, end, fullStart, fullEnd, text, missing,
    ///         type, symbol, symbolKind, children]
    ///     </c>
    /// </summary>
    static object?[] Encode(VisualItem item) => [
        (int)item.Category,
        item.Kind.ToString(),
        item.Span.Start,
        item.Span.End,
        item.FullSpan.Start,
        item.FullSpan.End,
        item.Text,
        item.IsMissing ? 1 : 0,
        item.Type,
        item.Symbol,
        item.SymbolKind,
        item.Children.Select(Encode).ToArray()
    ];

    static string Template() {
        using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Sable.Visualiser.Page.html")
            ?? throw new InvalidOperationException("the page template is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
