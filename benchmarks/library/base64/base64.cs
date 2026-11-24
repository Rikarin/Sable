// A megabyte to Base64 and back, as a string and as ASCII bytes, forty times.
[Benchmark("library/base64")]
static class LibraryBase64 {
    public static string Run() {
        checked {
            var bytes = CodecSample.Bytes();
            long digits = 0;
            long check = 0;
            for (var round = 0; round < 40; round++) {
                var text = Convert.ToBase64String(bytes);
                digits += text.Length;
                var decoded = Convert.FromBase64String(text);
                check += decoded[round * 7919 % decoded.Length];

                var buffer = new byte[System.Buffers.Text.Base64.GetMaxEncodedToUtf8Length(bytes.Length)];
                System.Buffers.Text.Base64.EncodeToUtf8(bytes, buffer, out _, out var written);
                digits += written;
                var raw = new byte[System.Buffers.Text.Base64.GetMaxDecodedFromUtf8Length(written)];
                System.Buffers.Text.Base64.DecodeFromUtf8(buffer.AsSpan(0, written), raw, out _, out var length);
                check += raw[round * 104729 % length];
            }

            return $"{digits} {check}";
        }
    }
}
