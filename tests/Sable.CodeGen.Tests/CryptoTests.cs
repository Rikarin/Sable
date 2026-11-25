using System.Security.Cryptography;
using System.Text;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Crypto: the interpreter, which runs its portable code, native code
///     that runs the processor's own instructions, and native code told it has
///     none, all give the digests .NET does; and what must survive the
///     optimiser does. The library's own tests (tests/lib/Sable.Crypto) say more.
/// </summary>
[Native]
public sealed class CryptoTests {
    static readonly IReadOnlyDictionary<string, string> Portable = new Dictionary<string, string> { ["SABLE_CRYPTO_PORTABLE"] = "1" };

    /// <summary>The AVX2 kernels, where the processor has AVX-512 too.</summary>
    static readonly IReadOnlyDictionary<string, string> Narrow = new Dictionary<string, string> { ["SABLE_CRYPTO_NARROW"] = "1" };

    const string Message = """
                           fn message(_ count: Int) -> U8[>..] {
                               var bytes: U8[>..] = []
                               var i = 0
                               while i < count {
                                   bytes.append(U8((i * 7 + 3) & 0xFF))
                                   i += 1
                               }
                               return bytes
                           }
                           """;

    static byte[] Bytes(int count) => [..Enumerable.Range(0, count).Select(static i => (byte)(i * 7 + 3))];

    static string Program(string body) => $$"""
        module C
        import Sable.Crypto
        {{Message}}
        @main
        fn main() {
        {{body}}
        }
        """;

    static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>The same program with the kernels and without them.</summary>
    static void Prints(string expected, string source) {
        Assert.Equal(expected, AssertAgree(source, stepLimit: 100_000_000).Stdout);
        if (CanBuild) {
            Assert.Equal(expected, BuildAndRun(source, environment: Portable).Stdout);
            Assert.Equal(expected, BuildAndRun(source, environment: Narrow).Stdout);
        }
    }

    [Fact]
    public void EveryWayOfHashingGivesWhatDotNetDoes() {
        int[] lengths = [0, 1, 55, 56, 63, 64, 65, 111, 112, 127, 128, 129, 300, 511, 512, 640, 1000];
        var expected = new StringBuilder();
        var body = new StringBuilder();
        foreach (var length in lengths) {
            var bytes = Bytes(length);
            expected.Append(Hex(SHA256.HashData(bytes))).Append('\n').Append(Hex(SHA384.HashData(bytes))).Append('\n')
                .Append(Hex(SHA512.HashData(bytes))).Append('\n').Append(Hex(MD5.HashData(bytes))).Append('\n').Append(Hex(SHA1.HashData(bytes))).Append('\n');
            foreach (var function in (string[])["Sha256", "Sha384", "Sha512", "Insecure.Md5", "Insecure.Sha1"]) {
                body.Append($"    print(\"\\({function}.hash(message({length})))\")\n");
            }
        }

        Prints(expected.ToString(), Program(body.ToString()));
    }

    /// <summary>FIPS 202's, SP 800-185's and RFC 7693's, and BLAKE3's, since .NET has those of them only where its system does.</summary>
    [Fact]
    public void TheSpongesAndTheBlakesGiveTheirStandardsVectors() =>
        Prints(
            """
            3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532
            b751850b1a57168a5693cd924b6b096e08f621827444f70d884f5d0240d2712e10e116e9192af3c91a7ec57647e3934057340b4cf408d5a56592f8274eec53f0
            5881092dd818bf5cf8a3
            e5780b0d3ea6f7d3a429c5706aa43a00fadbd7d49628839e3187243f456ee14e
            ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923
            6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85

            """,
            Program(
                """
                    print("\(Sha3_256.hash(utf8: "abc"))")
                    print("\(Sha3_512.hash(utf8: "abc"))")
                    print("\(Digest(Shake128.hash(utf8: "abc", count: 10)))")
                    var key: U8[>..] = []
                    var i = 0x40
                    while i < 0x60 {
                        key.append(U8(i))
                        i += 1
                    }
                    print("\(Kmac128.authenticationCode(for: [0, 1, 2, 3], using: SymmetricKey(key)))")
                    print("\(Blake2b.hash(utf8: "abc"))")
                    print("\(Blake3.hash(utf8: "abc"))")
                """
            )
        );

    [Fact]
    public void CodesAndDerivedKeysAreWhatDotNetGives() {
        var bytes = Bytes(100);
        var expected = Hex(HMACSHA256.HashData(Bytes(70), bytes)) + "\n"
            + Hex(HMACSHA512.HashData(Bytes(130), bytes)) + "\n"
            + Hex(HKDF.DeriveKey(HashAlgorithmName.SHA256, Bytes(20), 70, Bytes(5), Bytes(6))) + "\n"
            + Hex(Rfc2898DeriveBytes.Pbkdf2(Bytes(9), Bytes(16), 5, HashAlgorithmName.SHA256, 40)) + "\n"
            + Hex(Rfc2898DeriveBytes.Pbkdf2(Bytes(9), Bytes(16), 5, HashAlgorithmName.SHA512, 40)) + "\n";
        Prints(
            expected,
            Program(
                """
                    print("\(Hmac<Sha256>.authenticationCode(for: message(100), using: SymmetricKey(message(70))))")
                    print("\(Hmac<Sha512>.authenticationCode(for: message(100), using: SymmetricKey(message(130))))")
                    print("\(Digest((try! Hkdf<Sha256>.deriveKey(from: SymmetricKey(message(20)), salt: message(5), info: message(6), byteCount: 70)).bytes()))")
                    print("\(Digest((try! Pbkdf2<Sha256>.deriveKey(from: SymmetricKey(message(9)), salt: message(16), iterations: 5, byteCount: 40)).bytes()))")
                    print("\(Digest((try! Pbkdf2<Sha512>.deriveKey(from: SymmetricKey(message(9)), salt: message(16), iterations: 5, byteCount: 40)).bytes()))")
                """
            )
        );
    }

    /// <summary>RFC 9106's case, and RFC 7914's smallest.</summary>
    [Fact]
    public void Argon2idAndScryptGiveTheirStandardsVectors() =>
        Prints(
            """
            0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659
            77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906

            """,
            Program(
                """
                    let argon2 = try! Argon2id(memoryKiB: 32, iterations: 3, parallelism: 4)
                    let key = try! argon2.deriveKey(
                        from: SymmetricKey(U8[>..](repeating: 1, count: 32)),
                        salt: U8[>..](repeating: 2, count: 16),
                        byteCount: 32,
                        secret: SymmetricKey(U8[>..](repeating: 3, count: 8)),
                        associatedData: U8[>..](repeating: 4, count: 12)
                    )
                    print("\(Digest(key.bytes()))")
                    let scrypt = try! Scrypt(cost: 16, blockSize: 1, parallelism: 1)
                    print("\(Digest((try! scrypt.deriveKey(from: SymmetricKey([]), salt: [], byteCount: 64)).bytes()))")
                """
            )
        );

    [Fact]
    public void RandomBytesAndZeroedOnesAreTheArraysOwn() =>
        Prints(
            "1000 true 0 0 3\n",
            Program(
                """
                    var bytes = SecureRandom.bytes(1000)
                    let before = bytes
                    SecureRandom.fill(&bytes)
                    let differs = bytes != before
                    let count = bytes.count
                    secureZero(&bytes)
                    var part = message(4)
                    secureZero(&part[1..<3])
                    print("\(count) \(differs) \(bytes[0] | bytes[999]) \(part[1] | part[2]) \(part[0])")
                """
            )
        );

    /// <summary>A secret zeroed as the last thing done with it: the call is there in what LLVM optimises, and opaque to it.</summary>
    [Fact]
    public void ZeroingWhatIsNotReadAgainIsKept() {
        var result = Pipeline.Compile(
            new SourceText(
                Program("    var secret = SecureRandom.bytes(32)\n    print(\"\\(secret.count)\")\n    secureZero(&secret)"), "test.sb"),
            optimised: true
        );
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("call void @sable_secure_zero(", result.LlvmIr!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     .NET's RSA signature, checked natively and interpreted: the public-key algorithm whose arithmetic is
    ///     short enough to interpret.
    /// </summary>
    [Fact]
    public void ASignatureIsCheckedTheSameInterpreted() {
        using var key = RSA.Create(1024);
        var modulus = key.ExportParameters(false).Modulus!;
        var signature = key.SignData(Bytes(50), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var source = Program($$"""
                let key = try! RsaPublicKey(modulus: [{{string.Join(", ", modulus)}}], exponent: 65537)
                var signature: U8[>..] = [{{string.Join(", ", signature)}}]
                print(key.isValid(signature, for: message(50), padding: .Pkcs1(.Sha256)))
                signature[50] = signature[50] ^ 1
                print(key.isValid(signature, for: message(50), padding: .Pkcs1(.Sha256)))
            """);
        Assert.Equal("true\nfalse\n", AssertAgree(source).Stdout);
    }
}
