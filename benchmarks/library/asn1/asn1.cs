using System.Formats.Asn1;

// Twenty thousand certificates of X.509's shape written and each read back.
[Benchmark("library/asn1")]
static class LibraryAsn1 {
    const string DomainComponent = "0.9.2342.19200300.100.1.25";
    const string CommonName = "2.5.4.3";
    const string Sha256WithRsa = "1.2.840.113549.1.1.11";
    const string Rsa = "1.2.840.113549.1.1.1";
    const string KeyUsage = "2.5.29.15";
    const string BasicConstraints = "2.5.29.19";

    static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0);
    static readonly Asn1Tag Context3 = new(TagClass.ContextSpecific, 3);

    [Flags]
    enum Usage : ushort {
        None = 0,
        KeyCertSign = 0x20,
        CrlSign = 0x40,
    }

    public static string Run() {
        var modulus = new byte[256];
        for (var i = 0; i < 256; i++) {
            modulus[i] = (byte)(i * 7 + 193);
        }

        var from = new DateTimeOffset(2024, 4, 30, 14, 25, 34, TimeSpan.Zero);
        var to = new DateTimeOffset(2025, 4, 30, 14, 25, 34, TimeSpan.Zero);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        long size = 0, sum = 0, check = 0;
        for (var i = 0; i < 20000; i++) {
            writer.Reset();
            WriteCertificate(writer, i, modulus, from, to);
            var bytes = writer.Encode();
            size += bytes.Length;
            check = checked(check * 31 + bytes[i % bytes.Length]) % 1000003;
            var reader = new AsnReader(bytes, AsnEncodingRules.DER);
            sum += ReadCertificate(reader);
            reader.ThrowIfNotEmpty();
        }

        return $"{size} {sum} {check}";
    }

    static void WriteName(AsnWriter writer, string common) {
        writer.PushSequence();
        foreach (var part in (string[])["com", "example"]) {
            writer.PushSetOf();
            writer.PushSequence();
            writer.WriteObjectIdentifier(DomainComponent);
            writer.WriteCharacterString(UniversalTagNumber.IA5String, part);
            writer.PopSequence();
            writer.PopSetOf();
        }

        writer.PushSetOf();
        writer.PushSequence();
        writer.WriteObjectIdentifier(CommonName);
        writer.WriteCharacterString(UniversalTagNumber.UTF8String, common);
        writer.PopSequence();
        writer.PopSetOf();
        writer.PopSequence();
    }

    static void WriteAlgorithm(AsnWriter writer, string id) {
        writer.PushSequence();
        writer.WriteObjectIdentifier(id);
        writer.WriteNull();
        writer.PopSequence();
    }

    static void WriteCertificate(AsnWriter writer, int serial, byte[] modulus, DateTimeOffset from, DateTimeOffset to) {
        writer.PushSequence();
        writer.PushSequence(Context0);
        writer.WriteInteger(2);
        writer.PopSequence(Context0);
        writer.WriteInteger(serial);
        WriteAlgorithm(writer, Sha256WithRsa);
        WriteName(writer, "Example CA");
        writer.PushSequence();
        writer.WriteUtcTime(from);
        writer.WriteUtcTime(to);
        writer.PopSequence();
        WriteName(writer, $"host-{serial}");
        writer.PushSequence();
        WriteAlgorithm(writer, Rsa);
        writer.PushOctetString();
        writer.PushSequence();
        writer.WriteIntegerUnsigned(modulus);
        writer.WriteInteger(65537);
        writer.PopSequence();
        writer.PopOctetString();
        writer.PopSequence();
        writer.PushSequence(Context3);
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteObjectIdentifier(KeyUsage);
        writer.WriteBoolean(true);
        writer.PushOctetString();
        writer.WriteNamedBitList(Usage.KeyCertSign | Usage.CrlSign);
        writer.PopOctetString();
        writer.PopSequence();
        writer.PushSequence();
        writer.WriteObjectIdentifier(BasicConstraints);
        writer.WriteBoolean(true);
        writer.PushOctetString();
        writer.PushSequence();
        writer.WriteBoolean(true);
        writer.PopSequence();
        writer.PopOctetString();
        writer.PopSequence();
        writer.PopSequence();
        writer.PopSequence(Context3);
        writer.PopSequence();
    }

    static int ReadName(AsnReader reader) {
        var length = 0;
        var names = reader.ReadSequence();
        while (names.HasData) {
            var set = names.ReadSetOf();
            var attribute = set.ReadSequence();
            var id = attribute.ReadObjectIdentifier();
            length += attribute.ReadCharacterString(id == CommonName ? UniversalTagNumber.UTF8String : UniversalTagNumber.IA5String).Length;
            attribute.ThrowIfNotEmpty();
            set.ThrowIfNotEmpty();
        }

        return length;
    }

    static long Number(AsnReader reader) =>
        reader.TryReadInt64(out var value) ? value : throw new AsnContentException();

    static long ReadCertificate(AsnReader reader) {
        long sum = 0;
        var fields = reader.ReadSequence();
        sum += Number(fields.ReadSequence(Context0));
        sum += Number(fields);
        var algorithm = fields.ReadSequence();
        sum += algorithm.ReadObjectIdentifier() == Sha256WithRsa ? 1 : 0;
        algorithm.ReadNull();
        sum += ReadName(fields);
        var validity = fields.ReadSequence();
        sum += validity.ReadUtcTime().Year;
        sum += validity.ReadUtcTime().Year;
        sum += ReadName(fields);
        var key = fields.ReadSequence();
        key.ReadEncodedValue();
        var numbers = new AsnReader(key.TryReadPrimitiveOctetString(out var contents) ? contents : throw new AsnContentException(), AsnEncodingRules.DER).ReadSequence();
        sum += numbers.ReadIntegerBytes().Length;
        sum += Number(numbers);
        var extensions = fields.ReadSequence(Context3).ReadSequence();
        while (extensions.HasData) {
            var one = extensions.ReadSequence();
            var id = one.ReadObjectIdentifier();
            sum += one.ReadBoolean() ? 1 : 0;
            var value = new AsnReader(one.TryReadPrimitiveOctetString(out var octets) ? octets : throw new AsnContentException(), AsnEncodingRules.DER);
            if (id == KeyUsage) {
                sum += (long)value.ReadNamedBitListValue<Usage>();
            } else {
                sum += value.ReadSequence().ReadBoolean() ? 1 : 0;
            }
        }

        fields.ThrowIfNotEmpty();
        return sum;
    }
}
