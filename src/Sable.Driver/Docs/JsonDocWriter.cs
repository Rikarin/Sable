using System.Text;
using System.Text.Json;

namespace Sable.Driver.Docs;

/// <summary>
///     Writes a <see cref="DocSet" /> as a symbol graph: every documented
///     declaration as a node with its kind, signature and comment, and the
///     edges between them — <c>memberOf</c>, <c>conformsTo</c> (a type's
///     supertypes) and <c>extends</c> (an extension member's type). For tools:
///     search indexes, other renderers, API diffing.
/// </summary>
public static class JsonDocWriter {
    public static string Render(DocSet set) {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new() { Indented = true })) {
            json.WriteStartObject();
            json.WriteNumber("formatVersion", 1);

            json.WriteStartArray("modules");
            foreach (var module in set.Modules) {
                json.WriteStartObject();
                json.WriteString("name", module.Name);
                json.WriteString("doc", module.Doc);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("symbols");
            foreach (var entry in set.Entries.Values.OrderBy(static e => e.Id, StringComparer.Ordinal)) {
                json.WriteStartObject();
                json.WriteString("id", entry.Id);
                json.WriteString("kind", entry.Kind);
                json.WriteString("name", entry.Name);
                json.WriteString("module", entry.Module);
                json.WriteString("signature", entry.Signature);
                json.WriteString("doc", entry.Doc);
                json.WriteString("href", HtmlDocWriter.Href(entry));
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("relationships");
            foreach (var entry in set.Entries.Values.OrderBy(static e => e.Id, StringComparer.Ordinal)) {
                if (entry.Parent is { } parent) {
                    Edge(json, "memberOf", entry.Id, parent.Id);
                }

                foreach (var supertype in entry.Supertypes) {
                    var name = supertype.Split('<')[0];
                    Edge(json, "conformsTo", entry.Id, set.ResolveType(name, entry.Module)?.Id ?? supertype);
                }
            }

            foreach (var module in set.Modules) {
                foreach (var (type, members) in module.Extensions) {
                    var target = set.ResolveType(type.Name, type.ContainingModule?.Name ?? "")?.Id ?? type.QualifiedName;
                    foreach (var member in members) {
                        Edge(json, "extends", member.Id, target);
                    }
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    static void Edge(Utf8JsonWriter json, string kind, string source, string target) {
        json.WriteStartObject();
        json.WriteString("kind", kind);
        json.WriteString("source", source);
        json.WriteString("target", target);
        json.WriteEndObject();
    }
}
