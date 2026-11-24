static class CodecSample {
    // A megabyte of bytes the Sable side makes the same way.
    public static byte[] Bytes() {
        checked {
            var bytes = new byte[1048576];
            long state = 1;
            for (var i = 0; i < bytes.Length; i++) {
                state = (state * 75 + 74) % 65537;
                bytes[i] = (byte)(state & 0xFF);
            }

            return bytes;
        }
    }
}
