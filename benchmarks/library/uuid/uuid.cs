// Port of uuid.sb, with Guid.
[Benchmark("library/uuid")]
static class LibraryUuid {
    public static string Run() {
        checked {
            long total = 0;
            for (ulong round = 0; round < 3; round++) {
                for (ulong i = 0; i < 300000; i++) {
                    var id = Make(0x119108F752D14320 + i, 0x1BACF847DB4148A8 + round);
                    var text = id.ToString();
                    var back = Guid.Parse(text);
                    if (back == id) { total += text.Length; }
                }
            }

            return total.ToString();
        }
    }

    static Guid Make(ulong high, ulong low) {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, high);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], low);
        return new Guid(bytes, bigEndian: true);
    }
}
