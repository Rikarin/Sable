// Port of utf8-validate.sb.
[Benchmark("library/utf8-validate")]
static class LibraryUtf8Validate {
    public static Func<string> Setup() {
        checked {
            var ascii = new System.Text.StringBuilder();
            var mixed = new System.Text.StringBuilder();
            for (var i = 0; i < 16384; i++) {
                ascii.Append("The quick brown fox jumps over the lazy dog, again. ");
                mixed.Append("Grüße, мир, 世界 😀 and plain words too. ");
            }

            var asciiBytes = System.Text.Encoding.UTF8.GetBytes(ascii.ToString());
            var mixedBytes = System.Text.Encoding.UTF8.GetBytes(mixed.ToString());
            return () => Run(asciiBytes, mixedBytes);
        }
    }

    static string Run(byte[] asciiBytes, byte[] mixedBytes) {
        checked {
            long valid = 0;
            for (var round = 0; round < 400; round++) {
                if (System.Text.Unicode.Utf8.IsValid(asciiBytes)) {
                    valid++;
                }

                if (System.Text.Unicode.Utf8.IsValid(mixedBytes)) {
                    valid++;
                }
            }

            return $"{valid} {asciiBytes.Length} {mixedBytes.Length}";
        }
    }
}
