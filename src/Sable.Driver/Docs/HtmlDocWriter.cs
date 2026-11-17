using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Sable.Driver.Docs;

/// <summary>
///     Writes a <see cref="DocSet" /> as static HTML: an index, a page per
///     module and a page per type, with the names in signatures and in
///     <c>`code`</c> spans linked to their types. One small stylesheet, inline;
///     the only script is the index's filter box.
/// </summary>
public static partial class HtmlDocWriter {
    /// <summary>The files to write, by name relative to the output directory.</summary>
    public static IReadOnlyDictionary<string, string> Render(DocSet set) {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal) {
            ["index.html"] = Index(set),
        };

        foreach (var module in set.Modules) {
            files[PageOf(module.Name)] = ModulePage(set, module);
            foreach (var type in module.Types.SelectMany(Flatten)) {
                files[PageOf(type.Id)] = TypePage(set, type);
            }
        }

        return files;
    }

    /// <summary>A type and the types nested in it, outermost first.</summary>
    static IEnumerable<DocEntry> Flatten(DocEntry type) =>
        [type, ..type.Members.Where(static m => m.IsType).SelectMany(Flatten)];

    public static string PageOf(string id) => id.Replace('/', '.') + ".html";

    /// <summary>Where an entry is documented: its type's page, or its module's, at its anchor; a type is its own page.</summary>
    public static string Href(DocEntry entry) =>
        entry.IsType ? PageOf(entry.Id)
        : entry.Parent is { } parent ? $"{PageOf(parent.Id)}#{entry.Anchor}"
        : $"{PageOf(entry.Module)}#{Anchor(entry)}";

    /// <summary>A module-level entry's anchor; an extension member's carries its type, so two types' 'count' differ.</summary>
    static string Anchor(DocEntry entry) {
        var local = entry.Id[(entry.Module.Length + 1)..];
        return DocEntry.Sanitise(local.Replace('/', '-'));
    }

    // ---- Pages -------------------------------------------------------------

    static string Index(DocSet set) {
        var body = new StringBuilder();
        body.Append("<h1>API documentation</h1>\n");
        body.Append("<input id=\"filter\" type=\"search\" placeholder=\"Filter\" autofocus>\n");
        body.Append("<h2>Modules</h2>\n<ul class=\"list\">\n");
        foreach (var module in set.Modules) {
            body.Append($"<li><a href=\"{PageOf(module.Name)}\">{E(module.Name)}</a> {Summary(module.Doc)}</li>\n");
        }

        body.Append("</ul>\n<h2>Declarations</h2>\n<ul class=\"list\" id=\"all\">\n");
        var all = set.Entries.Values
            .Where(static e => e.IsType || e.Parent is null)
            .OrderBy(static e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static e => e.Id, StringComparer.Ordinal);
        foreach (var entry in all) {
            body.Append($"<li data-name=\"{E(entry.Name.ToLowerInvariant())}\"><span class=\"kind\">{E(entry.Kind)}</span> <a href=\"{Href(entry)}\">{E(entry.Name)}</a> <span class=\"where\">{E(entry.Module)}</span></li>\n");
        }

        body.Append("</ul>\n");
        body.Append("""
            <script>
            document.getElementById('filter').addEventListener('input', e => {
              const q = e.target.value.toLowerCase();
              for (const li of document.querySelectorAll('#all li')) li.hidden = !li.dataset.name.includes(q);
            });
            </script>

            """);
        return Page("API documentation", body.ToString(), []);
    }

    static string ModulePage(DocSet set, DocModule module) {
        var body = new StringBuilder();
        body.Append($"<h1><span class=\"kind\">module</span> {E(module.Name)}</h1>\n");
        body.Append(Markdown(set, module.Doc, module.Name));

        Section(body, set, "Types", module.Types, summaryOnly: true);
        Section(body, set, "Functions", module.Functions, summaryOnly: false);
        Section(body, set, "Values", module.Values, summaryOnly: false);

        foreach (var (type, members) in module.Extensions) {
            var target = set.ResolveType(type.Name, type.ContainingModule?.Name ?? "");
            var name = target is null ? E(type.Name) : $"<a href=\"{Href(target)}\">{E(type.Name)}</a>";
            body.Append($"<h2>Extensions to {name}</h2>\n");
            foreach (var member in members) {
                Declaration(body, set, member, Anchor(member));
            }
        }

        return Page(module.Name, body.ToString(), [("index", "index.html")]);
    }

    static string TypePage(DocSet set, DocEntry type) {
        var body = new StringBuilder();
        body.Append($"<h1><span class=\"kind\">{E(type.Kind)}</span> {E(type.Name)}</h1>\n");
        body.Append($"<pre class=\"sig\">{Signature(set, type)}</pre>\n");
        body.Append(Markdown(set, type.Doc, type.Module));

        var extenders = set.Modules
            .Where(m => m.Extensions.Any(x => x.Type.Name == type.Name && set.ResolveType(type.Name, x.Type.ContainingModule?.Name ?? "") == type))
            .Select(static m => $"<a href=\"{PageOf(m.Name)}\">{E(m.Name)}</a>")
            .ToList();
        if (extenders.Count > 0) {
            body.Append($"<p class=\"where\">Extended in {string.Join(", ", extenders)}.</p>\n");
        }

        var groups = new (string Title, Func<DocEntry, bool> Holds)[] {
            ("Nested types", static m => m.IsType),
            ("Cases", static m => m.Kind == "case"),
            ("Associated types", static m => m.Kind == "associatedtype"),
            ("Initialisers", static m => m.Kind == "init"),
            ("Properties", static m => m.Kind is "var" or "let"),
            ("Methods", static m => m.Kind is "fn" or "subscript"),
            ("Operators", static m => m.Kind == "operator"),
            ("Aliases", static m => m.Kind == "alias"),
        };
        foreach (var (title, holds) in groups) {
            Section(body, set, title, [..type.Members.Where(holds)], summaryOnly: title == "Nested types");
        }

        var crumbs = new List<(string, string)> { ("index", "index.html"), (type.Module, PageOf(type.Module)) };
        for (var parent = type.Parent; parent is not null; parent = parent.Parent) {
            crumbs.Insert(2, (parent.Name, PageOf(parent.Id)));
        }

        return Page($"{type.Name} — {type.Module}", body.ToString(), crumbs);
    }

    static void Section(StringBuilder body, DocSet set, string title, IReadOnlyList<DocEntry> entries, bool summaryOnly) {
        if (entries.Count == 0) {
            return;
        }

        body.Append($"<h2>{E(title)}</h2>\n");
        if (summaryOnly) {
            body.Append("<ul class=\"list\">\n");
            foreach (var entry in entries) {
                body.Append($"<li><span class=\"kind\">{E(entry.Kind)}</span> <a href=\"{Href(entry)}\">{E(entry.Name)}</a> {Summary(entry.Doc)}</li>\n");
            }

            body.Append("</ul>\n");
            return;
        }

        foreach (var entry in entries) {
            Declaration(body, set, entry, entry.Parent is null ? Anchor(entry) : entry.Anchor);
        }
    }

    static void Declaration(StringBuilder body, DocSet set, DocEntry entry, string anchor) {
        body.Append($"<section class=\"decl\" id=\"{E(anchor)}\">\n");
        body.Append($"<pre class=\"sig\"><a class=\"self\" href=\"#{E(anchor)}\">§</a> {Signature(set, entry)}</pre>\n");
        body.Append(Markdown(set, entry.Doc, entry.Module));
        body.Append("</section>\n");
    }

    // ---- Text --------------------------------------------------------------

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    /// <summary>A signature, escaped, with each name of a documented type linked to it.</summary>
    static string Signature(DocSet set, DocEntry entry) {
        var text = new StringBuilder();
        var last = 0;
        foreach (Match match in Identifier().Matches(entry.Signature)) {
            text.Append(E(entry.Signature[last..match.Index]));
            last = match.Index + match.Length;

            var name = match.Value;
            var isDeclaredName = entry.IsType && name == entry.Name && match.Index == entry.Signature.IndexOf(" " + name, StringComparison.Ordinal) + 1;
            text.Append(!isDeclaredName && !entry.TypeParameters.Contains(name) && set.ResolveType(name, entry.Module) is { } target && IsTypePosition(entry.Signature, match.Index)
                ? $"<a href=\"{Href(target)}\">{E(name)}</a>"
                : E(name));
        }

        text.Append(E(entry.Signature[last..]));
        return text.ToString();
    }

    /// <summary>True when the name at an offset is where a type is written: after ':', '->', '<', ',', '(', '[', '&', '=' or 'throws('.</summary>
    static bool IsTypePosition(string signature, int at) {
        var i = at - 1;
        while (i >= 0 && signature[i] == ' ') {
            i--;
        }

        if (i < 0) {
            return false;
        }

        // A parameter's name follows its label, or starts the list: 'label name: T'.
        if (signature[i] is '(' or ',' && NextNonSpace(signature, at) == ':') {
            return false;
        }

        return signature[i] is ':' or '>' or '<' or ',' or '(' or '[' or '&' or '=' or '|' || (i >= 1 && signature[(i - 1)..(i + 1)] == "->");
    }

    static char NextNonSpace(string text, int at) {
        var i = at;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) {
            i++;
        }

        while (i < text.Length && text[i] == ' ') {
            i++;
        }

        return i < text.Length ? text[i] : '\0';
    }

    /// <summary>A doc comment's first sentence, for a list.</summary>
    static string Summary(string doc) {
        if (doc.Length == 0) {
            return "";
        }

        var paragraph = doc.Split("\n\n")[0].Replace('\n', ' ');
        var end = paragraph.IndexOf(". ", StringComparison.Ordinal);
        var sentence = end < 0 ? paragraph : paragraph[..(end + 1)];
        return $"<span class=\"summary\">— {InlineCode(sentence)}</span>";
    }

    /// <summary>
    ///     The little Markdown doc comments use: paragraphs, <c>```</c>
    ///     blocks, <c>- </c> lists and <c>`code`</c>, the last linked when
    ///     it names a documented type.
    /// </summary>
    static string Markdown(DocSet set, string doc, string module) {
        if (doc.Length == 0) {
            return "";
        }

        var html = new StringBuilder("<div class=\"doc\">\n");
        var lines = doc.Split('\n');
        var paragraph = new List<string>();
        var inList = false;

        void FlushParagraph() {
            if (paragraph.Count > 0) {
                html.Append("<p>").Append(Inline(set, string.Join(' ', paragraph), module)).Append("</p>\n");
                paragraph.Clear();
            }
        }

        void CloseList() {
            if (inList) {
                html.Append("</ul>\n");
                inList = false;
            }
        }

        for (var i = 0; i < lines.Length; i++) {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) {
                FlushParagraph();
                CloseList();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) {
                    code.Add(lines[i]);
                }

                html.Append("<pre class=\"code\">").Append(E(string.Join('\n', code))).Append("</pre>\n");
            } else if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal)) {
                FlushParagraph();
                if (!inList) {
                    html.Append("<ul>\n");
                    inList = true;
                }

                html.Append("<li>").Append(Inline(set, line.TrimStart()[2..], module)).Append("</li>\n");
            } else if (line.Trim().Length == 0) {
                FlushParagraph();
                CloseList();
            } else if (inList && line.StartsWith("  ", StringComparison.Ordinal)) {
                // A list item's continuation line.
                html.Length -= "</li>\n".Length;
                html.Append(' ').Append(Inline(set, line.Trim(), module)).Append("</li>\n");
            } else {
                CloseList();
                paragraph.Add(line.Trim());
            }
        }

        FlushParagraph();
        CloseList();
        return html.Append("</div>\n").ToString();
    }

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex CodeSpan();

    static string Inline(DocSet set, string text, string module) {
        var html = new StringBuilder();
        var last = 0;
        foreach (Match match in CodeSpan().Matches(text)) {
            html.Append(E(text[last..match.Index]));
            last = match.Index + match.Length;
            var code = match.Groups[1].Value;
            html.Append(set.ResolveType(code, module) is { } target
                ? $"<a href=\"{Href(target)}\"><code>{E(code)}</code></a>"
                : $"<code>{E(code)}</code>");
        }

        return html.Append(E(text[last..])).ToString();
    }

    static string InlineCode(string text) => CodeSpan().Replace(E(text), "<code>$1</code>");

    static string E(string text) => WebUtility.HtmlEncode(text);

    static string Page(string title, string body, IReadOnlyList<(string Name, string Href)> crumbs) {
        var nav = crumbs.Count == 0
            ? ""
            : "<nav>" + string.Join(" / ", crumbs.Select(static c => $"<a href=\"{c.Href}\">{E(c.Name)}</a>")) + "</nav>\n";
        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(title)}</title>
            <style>
            {Stylesheet}
            </style>
            </head>
            <body>
            <main>
            {nav}{body}</main>
            </body>
            </html>

            """;
    }

    const string Stylesheet = """
        :root { --fg: #1d1f21; --muted: #6a737d; --bg: #fff; --code: #f4f5f7; --link: #0b5cad; --rule: #e1e4e8; }
        @media (prefers-color-scheme: dark) { :root { --fg: #dcdfe4; --muted: #8b949e; --bg: #16181c; --code: #22262c; --link: #6cb6ff; --rule: #30363d; } }
        body { background: var(--bg); color: var(--fg); font: 16px/1.55 system-ui, sans-serif; margin: 0; }
        main { max-width: 52rem; margin: 0 auto; padding: 1.5rem 1rem 4rem; }
        a { color: var(--link); text-decoration: none; } a:hover { text-decoration: underline; }
        nav { color: var(--muted); font-size: .9rem; margin-bottom: 1rem; }
        h1 { font-size: 1.7rem; margin: .2rem 0 1rem; } h2 { font-size: 1.2rem; border-bottom: 1px solid var(--rule); padding-bottom: .2rem; margin-top: 2rem; }
        .kind { color: var(--muted); font-weight: normal; font-size: .85em; }
        .where, .summary { color: var(--muted); }
        pre, code { font: .9rem/1.45 ui-monospace, SFMono-Regular, Menlo, monospace; }
        code { background: var(--code); padding: .05rem .25rem; border-radius: 3px; }
        pre.sig, pre.code { background: var(--code); padding: .6rem .8rem; border-radius: 4px; overflow-x: auto; white-space: pre-wrap; }
        .decl { margin: 1.2rem 0; } .decl .doc { margin-left: .8rem; }
        a.self { color: var(--muted); }
        ul.list { list-style: none; padding: 0; } ul.list li { margin: .25rem 0; }
        input[type=search] { width: 100%; padding: .4rem .6rem; font-size: 1rem; border: 1px solid var(--rule); border-radius: 4px; background: var(--bg); color: var(--fg); }
        """;
}
