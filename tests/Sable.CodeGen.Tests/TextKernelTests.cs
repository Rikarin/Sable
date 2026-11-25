using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     What the library has the runtime do natively, by kernels the
///     interpreter has none of, or writes in place, comes to the same in the
///     interpreter, whose library code does it all.
/// </summary>
[Native]
public sealed class TextKernelTests {
    static string Run(string imports, string body) => AssertAgree($"module Main\n\n{imports}\n\n@main\nfn main() throws {{\n{body}\n}}\n").Stdout;

    [Fact]
    public void CodecsAgree() =>
        Assert.Equal(
            "true true true true\ntrue DecodeError.InvalidCharacter(at: 70, byte: 33)\n",
            Run("import Sable.Text", """
                var bytes: U8[>..] = []
                for i in 0..<150 {
                    bytes.append(((i * 75 + 74) % 257) unchecked as U8)
                }
                let base64 = Base64.encode(bytes)
                let url = Base64.encode(bytes, alphabet: .UrlSafe, padding: false)
                let hex = Hex.encode(bytes, uppercase: true)
                let decoded = try Base64.decode(base64)
                let decodedUrl = try Base64.decode(url, alphabet: .UrlSafe)
                let decodedHex = try Hex.decode(hex)
                print("\(decoded == bytes) \(decodedUrl == bytes) \(decodedHex == bytes) \(base64.utf8Count == 200)")
                let wrong = base64[..<base64.index(atScalar: 70)].toString() + "!" + base64[base64.index(atScalar: 71)...].toString()
                var said = ""
                do {
                    let _ = try Base64.decode(wrong)
                } catch {
                    said = "\(error)"
                }
                print("\(hex.hasPrefix("4A95")) \(said)")
            """)
        );

    [Fact]
    public void TranscodingAndFormsAgree() =>
        Assert.Equal(
            "39 true true\nvietnamese: ệ AI 가 1 | é true\nΣΊΣΥΦΟΣ ΣΣ| σίσυφος σς| straße SS\n",
            Run("import Sable.Text", """
                let text = "Grüße, мир, 世界 😀 and plain words too. "
                var units: U16[>..] = []
                let (status, _) = Utf8.toUtf16(text.utf8Bytes(), into: &units)
                var back: U8[>..] = []
                let _ = Utf8.fromUtf16(units, into: &back)
                print("\(units.count) \(status == .Done) \(back == text.utf8Bytes())")
                let forms = "vietnamese: e\u{302}\u{323} \u{FF21}\u{FF29} \u{1100}\u{1161} \u{B9}".normalized(.NFKC)
                let apart = "é".normalized(.NFD)
                print("\(forms) | \(apart.normalized(.NFC)) \(apart.count == 2)")
                print("\("Σίσυφος σς".uppercased())| \("ΣΊΣΥΦΟΣ ΣΣ".lowercased())| \("STRAßE".lowercased()) \("ß".uppercased())")
            """)
        );

    [Fact]
    public void UuidsAndUrlsAgree() =>
        Assert.Equal(
            "6ba7b810-9dad-11d1-80b4-00c04fd430c8 6ba7b8109dad11d180b400c04fd430c8 true ParseError.InvalidDigit(at: 9)\n"
            + "https://us%20er@example.com:8443/a/d%20e/?q=%C3%BC#top example.com 8443 /a/d%20e/\n"
            + "a%20b%2F%C3%BC%E2%9C%93 a b/ü✓ a+b%2F%C3%BC\n",
            Run("import Sable.Net", """
                let id = try UUID.parse("{6BA7B810-9DAD-11D1-80B4-00C04FD430C8}")
                let again = try UUID.parse("urn:uuid:\(id)")
                var said = ""
                do {
                    let _ = try UUID.parse("6ba7b810-Gdad-11d1-80b4-00c04fd430c8")
                } catch {
                    said = "\(error)"
                }
                print("\(id) \(id.compactString()) \(again == id) \(said)")
                let url = try Url.parse("HTTPS://us er@Example.com:8443/a/./b/../d e/%2e?q=ü#top")
                print("\(url.href) \(url.hostname) \(url.portText) \(url.path)")
                let encoded = PercentEncoding.encode("a b/ü✓", .Component)
                print("\(encoded) \(PercentEncoding.decodeText(encoded)) \(PercentEncoding.encode("a b/ü", .FormUrlencoded, spaceAsPlus: true))")
            """)
        );
}
