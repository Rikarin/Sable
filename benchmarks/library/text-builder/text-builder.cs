// A million small appends to a StringBuilder, built once; then edits in the
// middle, a removal at the front and a replacement throughout.
[Benchmark("library/text-builder")]
static class LibraryTextBuilder {
    public static string Run() {
        checked {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < 1000000; i++) {
                builder.Append("item ");
                builder.Append(i);
                builder.Append(';');
                if (i % 10 == 0) {
                    builder.Append('\n');
                }
            }

            var built = builder.ToString();
            for (var j = 0; j < 20; j++) {
                builder.Insert(builder.Length / 2, "<>");
            }

            builder.Remove(0, 5);
            builder.Replace("item", "it");
            var edited = builder.ToString();
            return $"{built.Length} {edited.Length}";
        }
    }
}
