using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Integers read from and written to bytes at an offset (Sable.Core's
///     Array+Binary.sb, under ByteBuffer and ByteReader): one range check and
///     one unaligned load or store natively, a byte at a time in the
///     interpreter, and the same answers and the same traps either way.
/// </summary>
public sealed class BinaryDataTests {
    [Fact]
    public void ReadsAndWritesAgreeAtEveryWidthAndOffset() =>
        Assert.Equal(
            "513\n2057\n1029\n84148994\n101124105\n578437695752307201\n144964032628459529\n"
            + "1 255 254 253 252 251 250 249 248 9\n-2\n-16777217\n-72339069014967816\n",
            AssertAgree(
                Program(
                    "",
                    """
                        var bytes: U8[>..] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 9]
                        print(bytes.readU16(at: 0, endian: .Little))
                        print(bytes.readU16(at: 7, endian: .Big))
                        print(bytes.slice(from: 3, to: 5).readU16(at: 0, endian: .Big))
                        print(bytes.readU32(at: 1, endian: .Little))
                        print(bytes.readU32(at: 5, endian: .Big))
                        print(bytes.readU64(at: 0, endian: .Little))
                        print(bytes.readU64(at: 1, endian: .Big))
                        bytes.writeU64(0xF8F9_FAFB_FCFD_FEFF, at: 1, endian: .Little)
                        var text = ""
                        for b in bytes {
                            text = text + "\(b) "
                        }
                        print(text.trimmed())
                        bytes.writeU16(0xFFFE, at: 0, endian: .Big)
                        bytes.writeU32(0xFEFF_FFFF, at: 2, endian: .Big)
                        print(bytes.readI16(at: 0, endian: .Big))
                        print(bytes.readI32(at: 2, endian: .Big))
                        print(bytes.readI64(at: 1, endian: .Big))
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("let b: U8[>..] = [1, 2, 3]\nprint(b.readU32(at: 0, endian: .Little))")]
    [InlineData("let b: U8[>..] = [1, 2, 3, 4]\nprint(b.readU16(at: -1, endian: .Little))")]
    [InlineData("let b: U8[>..] = [1, 2, 3, 4]\nprint(b.readU16(at: 9223372036854775807, endian: .Little))")]
    [InlineData("let b: U8[>..] = [1, 2, 3, 4, 5, 6, 7, 8, 9]\nprint(b.slice(from: 2, to: 9).readU64(at: 0, endian: .Little))")]
    [InlineData("var b: U8[>..] = [1, 2, 3]\nb.writeU32(7, at: 0, endian: .Big)")]
    [InlineData("var b: U8[>..] = [1, 2, 3]\nb.writeU16(7, at: -1, endian: .Big)")]
    [InlineData("var b: U8[>..] = []\nb.writeU64(7, at: 9223372036854775806, endian: .Big)")]
    public void AnyByteOutsideTheBytesTraps(string body) {
        var execution = AssertAgree(Program("", body));
        Assert.Contains("array index out of range", execution.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadIsOneUnalignedLoadAndAWriteOneStore() {
        var result = Pipeline.Compile(
            new SourceText(
                Program(
                    "",
                    """
                        var bytes: U8[>..] = [1, 2, 3, 4, 5, 6, 7, 8]
                        print(bytes.readU32(at: Runtime.argumentCount(), endian: .Big))
                        bytes.writeU64(9, at: 0, endian: .Little)
                        print(bytes.readU16(at: 0, endian: .Little))
                    """
                ),
                "test.sb"
            )
        );
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var ir = result.LlvmIr!;
        Assert.Matches(@"load i32, ptr %\S+, align 1", ir);
        Assert.Matches(@"store i64 %\S+, ptr %\S+, align 1", ir);
        Assert.Contains("@llvm.bswap.i32(", ir, StringComparison.Ordinal);
    }
}
