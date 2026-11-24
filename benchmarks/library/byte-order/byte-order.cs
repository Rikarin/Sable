using System.Buffers.Binary;

// Port of byte-order.sb.
[Benchmark("library/byte-order")]
static class LibraryByteOrder {
    public static string Run() {
        checked {
            var bytes = new byte[1048576];
            ulong total = 0;
            for (var round = 0; round < 50; round++) {
                for (var at = 0; at < bytes.Length; at += 16) {
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)(at + round));
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 4), (ulong)(at * 3L));
                }

                for (var at = 0; at < bytes.Length; at += 16) {
                    total = (total + BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at)) + BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 4))) % 1000000007;
                }
            }

            return total.ToString();
        }
    }
}
