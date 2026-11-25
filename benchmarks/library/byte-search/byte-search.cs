// Port of byte-search.sb.
[Benchmark("library/byte-search")]
static class LibraryByteSearch {
    public static Func<string> Setup() {
        checked {
            var bytes = new byte[1048576];
            for (var i = 0; i < bytes.Length; i++) {
                bytes[i] = (byte)(i % 97);
            }

            bytes[1048570] = 200;
            return () => Run(bytes);
        }
    }

    static string Run(byte[] bytes) {
        checked {
            long total = 0;
            for (var round = 0; round < 1000; round++) {
                total += bytes.AsSpan().IndexOf((byte)200);
                total += bytes.AsSpan().Count((byte)42);
            }

            return total.ToString();
        }
    }
}
