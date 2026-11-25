// A megabyte to hex and back, forty times.
[Benchmark("library/hex")]
static class LibraryHex {
    public static Func<string> Setup() {
        checked {
            var bytes = CodecSample.Bytes();
            return () => Run(bytes);
        }
    }

    static string Run(byte[] bytes) {
        checked {
            long digits = 0;
            long check = 0;
            for (var round = 0; round < 40; round++) {
                var text = Convert.ToHexString(bytes);
                digits += text.Length;
                var decoded = Convert.FromHexString(text);
                check += decoded[round * 7919 % decoded.Length];
            }

            return $"{digits} {check}";
        }
    }
}
