using System.Globalization;
using System.Text;
using System.Xml;
using Sable.SIL;

namespace Sable.Driver;

/// <summary>
///     A run's coverage as Cobertura's XML (<c>sable test --cobertura</c>), which
///     GitLab, Azure Pipelines and ReportGenerator read: a package per
///     directory, a class per file, its lines with how often each ran and how
///     many of its decisions' outcomes happened.
/// </summary>
internal static class CoberturaReport {
    /// <param name="directory">What the files' names are from.</param>
    public static void Write(Stream stream, string directory, IReadOnlyDictionary<string, SortedDictionary<int, long>> files, IReadOnlyDictionary<string, IReadOnlyList<CoverageBranch>> branches) {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), NewLineChars = "\n" });
        writer.WriteStartDocument();
        writer.WriteDocType("coverage", null, "http://cobertura.sourceforge.net/xml/coverage-04.dtd", null);
        writer.WriteStartElement("coverage");
        Counts(writer, [..files], branches, totals: true);
        writer.WriteAttributeString("version", "1");
        writer.WriteAttributeString("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("sources");
        writer.WriteElementString("source", directory);
        writer.WriteEndElement();
        writer.WriteStartElement("packages");
        foreach (var package in files.GroupBy(f => DirectoryOf(Name(f.Key, directory))).OrderBy(static g => g.Key, StringComparer.Ordinal)) {
            writer.WriteStartElement("package");
            writer.WriteAttributeString("name", package.Key);
            Counts(writer, [..package], branches);
            writer.WriteAttributeString("complexity", "0");
            writer.WriteStartElement("classes");
            foreach (var file in package) {
                var name = Name(file.Key, directory);
                writer.WriteStartElement("class");
                writer.WriteAttributeString("name", Path.GetFileNameWithoutExtension(name));
                writer.WriteAttributeString("filename", name);
                Counts(writer, [file], branches);
                writer.WriteAttributeString("complexity", "0");
                writer.WriteElementString("methods", "");
                writer.WriteStartElement("lines");
                var decisions = (branches.GetValueOrDefault(file.Key) ?? []).ToLookup(static d => d.Line);
                foreach (var (line, count) in file.Value) {
                    writer.WriteStartElement("line");
                    writer.WriteAttributeString("number", line.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("hits", count.ToString(CultureInfo.InvariantCulture));
                    var outcomes = decisions[line].Sum(static d => d.Outcomes.Count);
                    writer.WriteAttributeString("branch", outcomes > 0 ? "true" : "false");
                    if (outcomes > 0) {
                        var happened = decisions[line].Sum(static d => d.Outcomes.Count(static o => o.Count > 0));
                        writer.WriteAttributeString("condition-coverage", string.Create(CultureInfo.InvariantCulture, $"{100 * happened / outcomes}% ({happened}/{outcomes})"));
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    static void Counts(XmlWriter writer, IReadOnlyList<KeyValuePair<string, SortedDictionary<int, long>>> files, IReadOnlyDictionary<string, IReadOnlyList<CoverageBranch>> branches, bool totals = false) {
        var lines = files.Sum(static f => f.Value.Count);
        var ran = files.Sum(static f => f.Value.Values.Count(static c => c > 0));
        var outcomes = files.SelectMany(f => branches.GetValueOrDefault(f.Key) ?? []).SelectMany(static d => d.Outcomes).ToList();
        var happened = outcomes.Count(static o => o.Count > 0);
        writer.WriteAttributeString("line-rate", Rate(ran, lines));
        writer.WriteAttributeString("branch-rate", Rate(happened, outcomes.Count));
        if (totals) {
            foreach (var (name, count) in new[] { ("lines-covered", ran), ("lines-valid", lines), ("branches-covered", happened), ("branches-valid", outcomes.Count), ("complexity", 0) }) {
                writer.WriteAttributeString(name, count.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    static string Rate(int part, int whole) => (whole == 0 ? 1 : (double)part / whole).ToString("0.####", CultureInfo.InvariantCulture);

    static string Name(string file, string directory) => (Path.IsPathRooted(file) ? Path.GetRelativePath(directory, file) : file).Replace('\\', '/');

    static string DirectoryOf(string file) => file.LastIndexOf('/') is var slash and >= 0 ? file[..slash] : ".";
}
