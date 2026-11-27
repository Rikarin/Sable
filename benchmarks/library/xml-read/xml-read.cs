using System.Globalization;
using System.Text;
using System.Xml;

// Port of xml-read.sb, with System.Xml's XmlReader. It reports an empty
// element as one node, Sable as two, so the document has none; white space
// is Whitespace to it and Text to Sable, so both count as text.
[Benchmark("library/xml-read")]
static class LibraryXmlRead {
    static (long, long, double) Read(byte[] bytes) {
        checked {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            long nodes = 0;
            long sum = 0;
            var price = 0.0;
            while (reader.Read()) {
                switch (reader.NodeType) {
                    case XmlNodeType.Element:
                        nodes++;
                        if (reader.LocalName == "item") {
                            sum += long.Parse(reader.GetAttribute("id")!, CultureInfo.InvariantCulture);
                            price += double.Parse(reader.GetAttribute("price")!, CultureInfo.InvariantCulture);
                        }
                        break;
                    case XmlNodeType.EndElement:
                        nodes++;
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        nodes++;
                        sum += Encoding.UTF8.GetByteCount(reader.Value);
                        break;
                }
            }

            return (nodes, sum, price);
        }
    }

    static byte[] Document(string inner) {
        var parts = new StringBuilder();
        for (var i = 0; i < 20000; i++) {
            parts.Append($"{inner}<item id=\"{i}\" price=\"{i % 1000}.25\">{inner}<name>item number {i}</name>" +
                         $"{inner}<tags><tag>a</tag><tag>b &amp; c</tag></tags>{inner}<note>a longer text, as a description is</note>{inner}</item>");
        }

        return Encoding.UTF8.GetBytes("<items>" + parts + "</items>");
    }

    public static Func<string> Setup() {
        var compact = Document("");
        var pretty = Document("\n        ");
        return () => Run(compact, pretty);
    }

    static string Run(byte[] compact, byte[] pretty) {
        var lines = new List<string>();
        for (var round = 0; round < 3; round++) {
            var (nodes, sum, price) = Read(compact);
            var (others, sums, prices) = Read(pretty);
            lines.Add($"{nodes + others} {sum + sums} {price + prices:F2}");
        }

        return string.Join("\n", lines);
    }
}
