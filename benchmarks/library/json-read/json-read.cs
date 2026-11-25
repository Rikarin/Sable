using System.Text;
using System.Text.Json;

// Port of json-read.sb, with Utf8JsonReader.
[Benchmark("library/json-read")]
static class LibraryJsonRead {
    static (long, long, double) Read(ReadOnlySpan<byte> bytes) {
        checked {
            var reader = new Utf8JsonReader(bytes);
            long tokens = 0;
            long sum = 0;
            var price = 0.0;
            var isPrice = false;
            while (reader.Read()) {
                tokens++;
                if (reader.TokenType == JsonTokenType.PropertyName) {
                    isPrice = reader.ValueTextEquals("price"u8);
                } else if (reader.TokenType == JsonTokenType.Number) {
                    if (isPrice) {
                        price += reader.GetDouble();
                    } else {
                        sum += reader.GetInt64();
                    }
                } else if (reader.TokenType == JsonTokenType.String) {
                    sum += reader.ValueSpan.Length;
                }
            }

            return (tokens, sum, price);
        }
    }

    static byte[] Document(string inner) {
        var parts = new List<string>();
        for (var i = 0; i < 30000; i++) {
            parts.Add($"{inner}{{{inner}\"id\":{i},{inner}\"name\":\"item number {i}\",{inner}\"price\":{i % 1000}.25," +
                      $"{inner}\"tags\":[{inner}\"a\",{inner}\"b\\n\",{inner}true,{inner}null],{inner}\"note\":\"a longer text, as a description is\"}}");
        }

        return Encoding.UTF8.GetBytes("[" + string.Join(",", parts) + "]");
    }

    public static Func<string> Setup() {
        var compact = Document("");
        var pretty = Document("\n        ");
        return () => Run(compact, pretty);
    }

    static string Run(byte[] compact, byte[] pretty) {
        var lines = new List<string>();
        for (var round = 0; round < 10; round++) {
            var (tokens, sum, price) = Read(compact);
            var (others, sums, prices) = Read(pretty);
            lines.Add($"{tokens + others} {sum + sums} {price + prices:F2}");
        }

        return string.Join("\n", lines);
    }
}
