using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.IO.Hashing, whose hashes read their bytes by address unchecked: the
///     interpreter, which checks every access and has none of the runtime's
///     CRC kernels, gives what native code gives, by the processor's
///     instructions and without them (SABLE_PORTABLE=1). The values are xxHash's
///     (Python's xxhash 3.7), zlib's and a CRC computed a bit at a time; the
///     library's own tests (tests/lib/Sable.IO.Hashing) say more.
/// </summary>
[Native]
public sealed class HashingTests {
    const string Program = """
        module H

        import Sable.IO.Hashing

        fn buffer(_ count: Int) -> U8[>..] {
            var bytes = U8[>..](repeating: 0, count: count)
            var generator: U64 = 2654435761
            var i = 0
            while i < count {
                bytes[i] = U8(generator >> 56)
                generator = generator &* 11400714785074694797
                i += 1
            }
            return bytes
        }

        fn pieces<H: NonCryptographicHash>(_ hash: H, _ bytes: U8[:]) -> H {
            var hash = hash
            var at = 0
            var size = 1
            while at < bytes.count {
                let end = at + size < bytes.count ? at + size : bytes.count
                hash.update(bytes[at..<end])
                at = end
                size = size * 5 % 97 + 1
            }
            return hash
        }

        @main
        fn main() {
            let all = buffer(1100)
            for count in [0, 3, 17, 65, 241, 1025] {
                let bytes = all[..<count]
                print("\(count) \(Crc32.hash(bytes):x) \(Crc32.hash(bytes, polynomial: .Castagnoli):x) \(Crc64.hash(bytes):x) \(Crc64.hash(bytes, polynomial: .Nvme):x) \(Adler32.hash(bytes):x)")
                print("  \(XxHash32.hash(bytes):x) \(XxHash64.hash(bytes):x) \(XxHash3.hash(bytes):x) \(XxHash128.hash(bytes):x)")
                print("  \(XxHash64.hash(bytes, seed: 9):x) \(XxHash3.hash(bytes, seed: 9):x) \(XxHash128.hash(bytes, seed: 9):x)")
                let same = pieces(Crc64(.Nvme), bytes).value == Crc64.hash(bytes, polynomial: .Nvme)
                    && pieces(Crc32(), bytes).value == Crc32.hash(bytes)
                    && pieces(XxHash3(seed: 9), bytes).value == XxHash3.hash(bytes, seed: 9)
                    && pieces(XxHash128(), bytes).value == XxHash128.hash(bytes)
                    && pieces(XxHash32(seed: 9), bytes).value == XxHash32.hash(bytes, seed: 9)
                    && pieces(Adler32(), bytes).value == Adler32.hash(bytes)
                print("  in pieces \(same)")
            }
        }
        """;

    [Fact]
    public void EveryHashIsTheSameEveryWay() {
        var output = AssertAgree(Program, stepLimit: 400_000_000).Stdout;
        if (CanBuild) {
            Assert.Equal(output, BuildAndRun(Program, environment: new Dictionary<string, string> { ["SABLE_PORTABLE"] = "1" }).Stdout);
        }

        Assert.Equal(Expected, output);
    }

    const string Expected = """
        0 0 0 0 0 1
          2cc5d05 ef46db3751d8e999 2d06800538d394c2 99aa06d3014798d86001c324468d497f
          d5dd67ec9a0b4dfa 8e1aeea8bc928d25 57ef5b9098e6f2454be3b2f64c897e92
          in pieces true
        3 69cd14ac f46028f1 b64ec7a0e3831b18 4d0cc47e2755b1a1 13900e5
          c23884f5 ff7e1959cb50794a 54247382a8d6b94d 20efc49ff02422ea54247382a8d6b94d
          584e0c71b1405225 40d2e549a2176a53 d2fea4886e37cdc240d2e549a2176a53
          in pieces true
        17 869af775 c650da6f 9bdeee6ed2681193 61c4af37130ed7cc 3c4807c9
          89fdc23e d39a2d051a30c2c 796f5acd3a60f862 955fa78643ed3669abbc12d11973d7db
          f1f94fdf636fe839 d625a6614468f7a0 e36c580753b9b37e760a362a1eda3ce4
          in pieces true
        65 3fbb4189 d31204ce a0e6a29caba7ad72 cde3e057fe635689 415d21ad
          16992b3d de0f20dc2631af7a fd81aac4bebc3883 6c074d65e54db85afe2f650fa500ec6e
          c291048ebd4ef043 572a639ac5c3be32 574e7d2361b5a54b6cd0299909d0839e
          in pieces true
        241 1a13aa3 2fd8cfd5 85b2256224c36b1b 3c3ee8f1aa8f586f 33aa7f2b
          e5f7c54d 95d76c8b4d8fc4d6 c5a639ecd2030e5e 99a80ecf0ecfc647c5a639ecd2030e5e
          eebd1c6b8278333 92b7df4087b65e6b e4b2f3bc1924050f92b7df4087b65e6b
          in pieces true
        1025 dc7a87ef 42f57ac2 ea5b3f7da84cf1a1 4a18c6a45a684fb f4c1056f
          75b3b8a1 847fa6006d7c2ac0 d870c0fa13211c6a fd3ee4fe7f2954c6d870c0fa13211c6a
          2a7443bf9cf6aeb1 52d9aac65b755de7 9a994b50157ba63152d9aac65b755de7
          in pieces true

        """;
}
