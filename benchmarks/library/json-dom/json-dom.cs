using System.Text.Json.Nodes;

// Port of json-dom.sb, with JsonNode.
[Benchmark("library/json-dom")]
static class LibraryJsonDom {
    public static string Run() {
        checked {
            var parts = new List<string>();
            for (var i = 0; i < 30000; i++) {
                parts.Add($"{{\"id\": {i}, \"name\": \"item {i}\", \"price\": {i % 1000}.25, \"tags\": [\"a\", \"b\\n\", true, null]}}");
            }

            var text = "[" + string.Join(", ", parts) + "]";
            var total = 0.0;
            long count = 0;
            for (var round = 0; round < 3; round++) {
                var document = JsonNode.Parse(text)!;
                foreach (var item in document.AsArray()) {
                    total += (double)item!["id"]! + (double)item["price"]!;
                    count += item["tags"]!.AsArray().Count + ((string)item["name"]!).Length;
                }
            }

            return $"{count} {total:F2}";
        }
    }
}
