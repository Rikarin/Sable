using System.IO.Hashing;

// Port of hashing.sb, over System.IO.Hashing (a NuGet package, as
// System.Numerics.Tensors is).
[Benchmark("library/hashing", "crc32", 1_048_576, 200)]
[Benchmark("library/hashing", "crc32", 256, 100_000)]
[Benchmark("library/hashing", "crc32", 16, 100_000)]
[Benchmark("library/hashing", "crc32c", 1_048_576, 200)]
[Benchmark("library/hashing", "crc64", 1_048_576, 200)]
[Benchmark("library/hashing", "crc64", 256, 100_000)]
[Benchmark("library/hashing", "crc64", 16, 100_000)]
[Benchmark("library/hashing", "crc64nvme", 1_048_576, 200)]
[Benchmark("library/hashing", "adler32", 1_048_576, 200)]
[Benchmark("library/hashing", "adler32", 256, 100_000)]
[Benchmark("library/hashing", "adler32", 16, 100_000)]
[Benchmark("library/hashing", "xxh32", 1_048_576, 200)]
[Benchmark("library/hashing", "xxh32", 256, 100_000)]
[Benchmark("library/hashing", "xxh32", 16, 100_000)]
[Benchmark("library/hashing", "xxh64", 1_048_576, 200)]
[Benchmark("library/hashing", "xxh64", 256, 100_000)]
[Benchmark("library/hashing", "xxh64", 16, 100_000)]
[Benchmark("library/hashing", "xxh3", 1_048_576, 200)]
[Benchmark("library/hashing", "xxh3", 256, 100_000)]
[Benchmark("library/hashing", "xxh3", 16, 100_000)]
[Benchmark("library/hashing", "xxh128", 1_048_576, 200)]
[Benchmark("library/hashing", "xxh128", 256, 100_000)]
[Benchmark("library/hashing", "xxh128", 16, 100_000)]
static class LibraryHashing {
    public static Func<string> Setup(string name, int size, int rounds) {
        var input = Sample(size);
        return () => Run(name, input, rounds);
    }

    static readonly string[] Names = ["crc32", "crc32c", "crc64", "crc64nvme", "adler32", "xxh32", "xxh64", "xxh3", "xxh128"];

    static ulong Once(int hash, byte[] bytes) =>
        hash switch {
            0 => Crc32.HashToUInt32(bytes),
            1 => Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes),
            2 => Crc64.HashToUInt64(bytes),
            3 => Crc64.HashToUInt64(Crc64ParameterSet.Nvme, bytes),
            4 => Adler32.HashToUInt32(bytes),
            5 => XxHash32.HashToUInt32(bytes),
            6 => XxHash64.HashToUInt64(bytes),
            7 => XxHash3.HashToUInt64(bytes),
            _ => (ulong)XxHash128.HashToUInt128(bytes),
        };

    static string Run(string name, byte[] input, int rounds) {
        checked {
            var hash = Array.IndexOf(Names, name);
            var bytes = (byte[])input.Clone();
            ulong total = 0;
            for (var round = 0; round < rounds; round++) {
                var value = Once(hash, bytes);
                total = unchecked(total + value);
                bytes[round % bytes.Length] = (byte)(value & 0xFF);
            }

            return $"{total}";
        }
    }

    static byte[] Sample(int count) {
        var bytes = new byte[count];
        ulong state = 1;
        for (var i = 0; i < count; i++) {
            state = unchecked(state * 6364136223846793005 + 1442695040888963407);
            bytes[i] = (byte)(state >> 56);
        }

        return bytes;
    }
}
