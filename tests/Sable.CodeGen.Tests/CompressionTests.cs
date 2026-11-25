using System.IO.Compression;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.IO.Compression, whose codecs read and write memory unchecked: the
///     interpreter, which checks every access, compresses, decompresses and
///     refuses damaged data as native code does, with no access out of its
///     memory. The library's own tests (tests/lib/Sable.IO.Compression) say more.
/// </summary>
[Native]
public sealed class CompressionTests {
    const string Support = """
                           struct Walk {
                               var state: U64
                               mutating fn next(_ below: Int) -> Int {
                                   state = state ^ (state << 13)
                                   state = state ^ (state >> 7)
                                   state = state ^ (state << 17)
                                   return Int(state % U64(below))
                               }
                           }

                           fn sample(_ count: Int, _ seed: U64) -> U8[>..] {
                               var walk = Walk(state: seed)
                               var bytes: U8[>..] = []
                               while bytes.count < count {
                                   if bytes.count > 40 && walk.next(3) > 0 {
                                       let back = 1 + walk.next(bytes.count)
                                       var left = 3 + walk.next(60)
                                       while left > 0 && bytes.count < count {
                                           bytes.append(bytes[bytes.count - back])
                                           left -= 1
                                       }
                                   } else {
                                       bytes.append(U8(walk.next(walk.next(2) == 0 ? 256 : 4)))
                                   }
                               }
                               return bytes
                           }

                           fn sum(_ bytes: U8[>..]) -> Int {
                               var sum = 0
                               for byte in bytes {
                                   sum = (sum * 31 + Int(byte)) % 1_000_003
                               }
                               return sum
                           }

                           fn damaged(_ bytes: U8[>..], _ seed: U64) -> U8[>..] {
                               var walk = Walk(state: seed)
                               var changed = bytes
                               let at = walk.next(changed.count)
                               changed[at] = changed[at] ^ U8(1 << walk.next(8))
                               if walk.next(4) == 0 {
                                   changed.removeLast(walk.next(changed.count))
                               }
                               return changed
                           }
                           """;

    static string Program(string body) => $$"""
        module C
        import Sable.IO
        import Sable.IO.Compression
        {{Support}}
        @main
        fn main() {
        {{body}}
        }
        """;

    /// <summary>The same in the interpreter and natively, by the processor's instructions and without.</summary>
    static string Output(string body) {
        var source = Program(body);
        var output = AssertAgree(source, stepLimit: 400_000_000).Stdout;
        if (CanBuild) {
            Assert.Equal(output, BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_PORTABLE"] = "1" }).Stdout);
        }

        return output;
    }

    [Fact]
    public void DeflateStaysInItsMemory() =>
        Assert.Equal(
            "whole true\npieces true\nrefused 12\n",
            Output(
                """
                var whole = true
                for level in [0, 1, 2, 5, 8, 9] {
                    let bytes = sample(700 + level * 211, U64(level) + 7)
                    whole = whole && (try! Gzip.decompress(Gzip.compress(bytes, level: level))) == bytes
                }
                print("whole \(whole)")
                let bytes = sample(3000, 99)
                var packed = ByteBuffer()
                let encoder = ZlibEncoder(level: 6)
                encoder.compress(bytes[..<1000], into: &packed)
                encoder.flush(into: &packed)
                encoder.compress(bytes[1000...], into: &packed)
                encoder.finish(into: &packed)
                let decoder = ZlibDecoder()
                var back = ByteBuffer()
                var at = 0
                while at < packed.count {
                    let end = at + 200 < packed.count ? at + 200 : packed.count
                    let step = try! decoder.decompress(packed.bytes[at..<end], into: &back, limit: 300)
                    at += step.consumed
                }
                while !decoder.isFinished {
                    _ = try! decoder.decompress([], into: &back, limit: 300)
                }
                print("pieces \(back.bytes == bytes)")
                var refused = 0
                var seed: U64 = 1
                while seed < 13 {
                    do {
                        _ = try Zlib.decompress(damaged(Zlib.compress(sample(400, seed), level: Int(seed) % 10), seed))
                    } catch {
                        refused += 1
                    }
                    seed += 1
                }
                print("refused \(refused)")
                """
            )
        );

    [Fact]
    public void ZstdStaysInItsMemory() =>
        Assert.Equal(
            "whole true\npieces true\nrefused 12\n",
            Output(
                """
                var whole = true
                for level in [1, 3, 5, 19] {
                    let bytes = sample(700 + level * 31, U64(level + 7))
                    whole = whole && (try! Zstd.decompress(Zstd.compress(bytes, level: level))) == bytes
                }
                print("whole \(whole)")
                let bytes = sample(2500, 98)
                let known = try! ZstdDictionary(bytes[..<400])
                var packed = ByteBuffer()
                let encoder = ZstdEncoder(level: 5, dictionary: known)
                encoder.compress(bytes[..<1000], into: &packed)
                encoder.flush(into: &packed)
                encoder.compress(bytes[1000...], into: &packed)
                encoder.finish(into: &packed)
                let decoder = ZstdDecoder(limits: DecompressionLimits(), dictionary: known)
                var back = ByteBuffer()
                var at = 0
                while at < packed.count {
                    let end = at + 200 < packed.count ? at + 200 : packed.count
                    let step = try! decoder.decompress(packed.bytes[at..<end], into: &back, limit: 300)
                    at += step.consumed
                }
                while !decoder.isFinished {
                    _ = try! decoder.decompress([], into: &back, limit: 300)
                }
                print("pieces \(back.bytes == bytes)")
                var refused = 0
                var seed: U64 = 1
                while seed < 13 {
                    do {
                        _ = try Zstd.decompress(damaged(Zstd.compress(sample(400, seed), level: 1 + Int(seed) % 6), seed))
                    } catch {
                        refused += 1
                    }
                    seed += 1
                }
                print("refused \(refused)")
                """
            )
        );

    [Fact]
    public void DeflateIsWhatDotNetReadsAndReadsWhatItWrites() {
        var bytes = new byte[5000];
        for (var i = 0; i < bytes.Length; i++) {
            bytes[i] = (byte)(i * i / 13);
        }

        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, true)) {
            gzip.Write(bytes);
        }

        var hex = Convert.ToHexStringLower(packed.ToArray());
        var output = Output(
            $$"""
              var packed: U8[>..] = []
              var high = 0
              var isHigh = true
              for digit in "{{hex}}".utf8Bytes() {
                  let value = Int(digit) < 0x3A ? Int(digit) - 0x30 : Int(digit) - 0x57
                  if isHigh {
                      high = value
                  } else {
                      packed.append(U8(high * 16 + value))
                  }
                  isHigh = !isHigh
              }
              let bytes = try! Gzip.decompress(packed)
              print("\(bytes.count) \(sum(bytes))")
              for byte in Gzip.compress(bytes) {
                  print("\(byte)")
              }
              """
        );
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sum = bytes.Aggregate(0, static (sum, b) => (sum * 31 + b) % 1_000_003);
        Assert.Equal($"5000 {sum}", lines[0]);
        using var unpacked = new GZipStream(new MemoryStream([..lines.Skip(1).Select(byte.Parse)]), CompressionMode.Decompress);
        using var back = new MemoryStream();
        unpacked.CopyTo(back);
        Assert.Equal(bytes, back.ToArray());
    }
}
