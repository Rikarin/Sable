using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

// Port of json-write.sb, with Utf8JsonWriter, which escapes no more than
// Sable's does and checks the structure no more than a release build.
[Benchmark("library/json-write")]
static class LibraryJsonWrite {
    static (int, int) Write(bool indented, int round) {
        checked {
            var buffer = new ArrayBufferWriter<byte>();
            var options = new JsonWriterOptions {
                Indented = indented,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                SkipValidation = true,
                NewLine = "\n"
            };
            using var writer = new Utf8JsonWriter(buffer, options);
            writer.WriteStartArray();
            for (var i = 0; i < 30000; i++) {
                writer.WriteStartObject();
                writer.WritePropertyName("id");
                writer.WriteNumberValue(i + round);
                writer.WritePropertyName("name");
                writer.WriteStringValue("item number seven");
                writer.WritePropertyName("price");
                writer.WriteNumberValue(i % 1000 + 0.25);
                writer.WritePropertyName("tags");
                writer.WriteStartArray();
                writer.WriteStringValue("a");
                writer.WriteStringValue("b\n");
                writer.WriteBooleanValue(true);
                writer.WriteNullValue();
                writer.WriteEndArray();
                writer.WritePropertyName("note");
                writer.WriteStringValue("a longer text, as a description is");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.Flush();
            return (buffer.WrittenCount, buffer.WrittenSpan[buffer.WrittenCount / 2]);
        }
    }

    public static string Run() {
        checked {
            long total = 0;
            long middle = 0;
            for (var round = 0; round < 10; round++) {
                var (compactCount, compactByte) = Write(false, round);
                var (prettyCount, prettyByte) = Write(true, round);
                total += compactCount + prettyCount;
                middle += compactByte + prettyByte;
            }

            return $"{total} {middle}";
        }
    }
}
