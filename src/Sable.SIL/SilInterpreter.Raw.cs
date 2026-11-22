using System.Numerics;
using System.Text;

namespace Sable.SIL;

/// <summary>
///     Raw memory (<c>RawPtr</c>, <c>NativeMemory</c>), interpreted: blocks at
///     made-up addresses, each its bytes and the values put in it. A scalar —
///     an integer, a float, a Bool, a Char — is written as its bytes, little
///     endian, as natively, so memory filled one way may be read another; any
///     other value is kept as itself at its offset, and read back only as the
///     type it was put there as. What natively would be undefined is a trap
///     here: an address in no block, a block freed twice, a read of bytes no
///     one wrote, a misaligned access.
/// </summary>
public sealed partial class SilInterpreter {
    /// <summary>A block of raw memory: where it starts, its bytes, which have been written, and the values in it by offset.</summary>
    sealed class RawBlock(long start, int size) {
        public long Start { get; } = start;
        public byte[] Bytes { get; } = new byte[size];
        public bool[] Written { get; } = new bool[size];
        public SortedDictionary<int, (SilType Type, object? Value)> Values { get; } = [];
        public bool Freed { get; set; }
    }

    /// <summary>The blocks allocated so far, by where each starts.</summary>
    readonly SortedList<long, RawBlock> _rawBlocks = [];

    /// <summary>Where the next block may start: clear of zero, so no block is at a null pointer.</summary>
    long _rawNext = 0x1_0000;

    object? RawBuiltin(BuiltinInst builtin, object?[] operands) {
        switch (builtin.Name) {
            case "raw_alloc": {
                var (size, alignment) = ((long)operands[0]!, (long)operands[1]!);
                if (size < 0 || size > int.MaxValue) {
                    throw new SilTrap($"cannot allocate {size} bytes");
                }

                if (alignment <= 0 || (alignment & (alignment - 1)) != 0) {
                    throw new SilTrap($"alignment {alignment} is not a power of two");
                }

                var start = (_rawNext + alignment - 1) / alignment * alignment;
                _rawNext = start + Math.Max(size, 1) + 16;
                _rawBlocks.Add(start, new RawBlock(start, (int)size));
                return start;
            }

            case "raw_free": {
                var address = (long)operands[0]!;
                if (address == 0) {
                    return null;
                }

                if (!_rawBlocks.TryGetValue(address, out var block)) {
                    throw new SilTrap($"freeing 0x{address:x}, which no allocation starts at");
                }

                if (block.Freed) {
                    throw new SilTrap($"freeing 0x{address:x} twice");
                }

                block.Freed = true;
                block.Values.Clear();
                return null;
            }

            case "raw_copy": {
                var (to, from, count) = ((long)operands[0]!, (long)operands[1]!, (long)operands[2]!);
                if (count < 0) {
                    throw new SilTrap($"copying {count} bytes");
                }

                if (count == 0) {
                    return null;
                }

                var (source, sourceAt) = RawRange(from, count);
                var (target, targetAt) = RawRange(to, count);
                var moved = source.Values.Where(v => v.Key >= sourceAt && v.Key + TypeLayout.Size(v.Value.Type) <= sourceAt + count).ToList();
                var bytes = source.Bytes[sourceAt..(sourceAt + (int)count)];
                var written = source.Written[sourceAt..(sourceAt + (int)count)];
                Forget(target, targetAt, (int)count);
                bytes.CopyTo(target.Bytes, targetAt);
                written.CopyTo(target.Written, targetAt);
                foreach (var (offset, value) in moved) {
                    target.Values[offset - sourceAt + targetAt] = value;
                }

                return null;
            }

            case "raw_fill": {
                var (to, value, count) = ((long)operands[0]!, (long)operands[1]!, (long)operands[2]!);
                if (count < 0) {
                    throw new SilTrap($"filling {count} bytes");
                }

                if (count == 0) {
                    return null;
                }

                var (block, at) = RawRange(to, count);
                Forget(block, at, (int)count);
                Array.Fill(block.Bytes, (byte)value, at, (int)count);
                Array.Fill(block.Written, true, at, (int)count);
                return null;
            }

            case "raw_init" or "raw_init_unaligned": {
                var type = builtin.Operands[1].Type;
                var size = TypeLayout.Size(type);
                var (block, at) = RawAccess((long)operands[0]!, type, size, aligned: builtin.Name == "raw_init");
                Forget(block, at, size);
                if (Encode(type, operands[1]) is { } bytes) {
                    bytes.CopyTo(block.Bytes, at);
                    Array.Fill(block.Written, true, at, size);
                } else {
                    block.Values[at] = (type, operands[1]);
                }

                return null;
            }

            case "raw_load" or "raw_load_unaligned" or "raw_take": {
                var type = builtin.Type;
                var size = TypeLayout.Size(type);
                var (block, at) = RawAccess((long)operands[0]!, type, size, aligned: builtin.Name != "raw_load_unaligned");
                if (IsRawScalar(type)) {
                    if (!block.Written.AsSpan(at, size).ToArray().All(static w => w)) {
                        throw new SilTrap($"reading a {type} at 0x{(long)operands[0]!:x}, where nothing was written");
                    }

                    return Decode(type, block.Bytes.AsSpan(at, size));
                }

                if (!block.Values.TryGetValue(at, out var held) || held.Type != type) {
                    throw new SilTrap($"reading a {type} at 0x{(long)operands[0]!:x}, which holds none");
                }

                if (builtin.Name == "raw_take") {
                    block.Values.Remove(at);
                } else {
                    Retain(type, held.Value);
                }

                return held.Value;
            }
        }

        return null;
    }

    /// <summary>The block a run of bytes is in, and where in it the run starts: trapping where the run is not all in one live block.</summary>
    (RawBlock Block, int At) RawRange(long address, long count) {
        var index = BlockAt(address);
        if (index < 0) {
            throw new SilTrap(address == 0 ? "a null pointer used" : $"0x{address:x} is in no allocation");
        }

        var block = _rawBlocks.Values[index];
        if (block.Freed) {
            throw new SilTrap($"0x{address:x} used after it was freed");
        }

        var at = address - block.Start;
        if (at + count > block.Bytes.Length) {
            throw new SilTrap($"{count} bytes at 0x{address:x} run past the end of its allocation of {block.Bytes.Length}");
        }

        return (block, (int)at);
    }

    (RawBlock Block, int At) RawAccess(long address, SilType type, int size, bool aligned) {
        if (aligned && address % TypeLayout.Align(type) != 0) {
            throw new SilTrap($"a {type} at 0x{address:x}, which is not aligned to {TypeLayout.Align(type)}");
        }

        return RawRange(address, size);
    }

    /// <summary>The index of the last block starting at or before the address, or -1.</summary>
    int BlockAt(long address) {
        var starts = _rawBlocks.Keys;
        int low = 0, high = starts.Count - 1, found = -1;
        while (low <= high) {
            var middle = (low + high) / 2;
            if (starts[middle] <= address) {
                found = middle;
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return found;
    }

    /// <summary>The values a run of bytes overlaps, forgotten: overwritten, as natively, without being destroyed.</summary>
    static void Forget(RawBlock block, int at, int count) {
        foreach (var offset in block.Values.Keys.Where(o => o < at + count && o + Math.Max(TypeLayout.Size(block.Values[o].Type), 1) > at).ToList()) {
            block.Values.Remove(offset);
        }

        Array.Fill(block.Written, false, at, count);
    }

    static bool IsRawScalar(SilType type) =>
        SilType.IntegerInfo(type) is not null || type == SilType.F64 || type == SilType.F32 || type == SilType.F16
        || type == SilType.BF16 || type == SilType.Bool || type == SilType.Char;

    /// <summary>A scalar's bytes, little endian, as natively; null for any other value.</summary>
    static byte[]? Encode(SilType type, object? value) {
        if (SilType.IntegerInfo(type) is var (bits, _)) {
            var bytes = new byte[bits / 8];
            var unsigned = Wrapped(Big(value), bits, false);
            for (var i = 0; i < bytes.Length; i++) {
                bytes[i] = (byte)(unsigned >> (8 * i) & 0xFF);
            }

            return bytes;
        }

        return value switch {
            double real when type == SilType.F64 => BitConverter.GetBytes(BitConverter.DoubleToUInt64Bits(real)),
            float real when type == SilType.F32 => BitConverter.GetBytes(BitConverter.SingleToUInt32Bits(real)),
            Half real when type == SilType.F16 => BitConverter.GetBytes(BitConverter.HalfToUInt16Bits(real)),
            BFloat16 real when type == SilType.BF16 => BitConverter.GetBytes(real.Bits),
            bool flag when type == SilType.Bool => [flag ? (byte)1 : (byte)0],
            Rune character when type == SilType.Char => BitConverter.GetBytes(character.Value),
            _ => null,
        };
    }

    /// <summary>A scalar read from its bytes.</summary>
    static object Decode(SilType type, ReadOnlySpan<byte> bytes) {
        if (SilType.IntegerInfo(type) is var (bits, signed)) {
            var unsigned = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
            return NarrowBig(Wrapped(unsigned, bits, signed), type);
        }

        if (type == SilType.F64) {
            return BitConverter.UInt64BitsToDouble(BitConverter.ToUInt64(bytes));
        }

        if (type == SilType.F32) {
            return BitConverter.UInt32BitsToSingle(BitConverter.ToUInt32(bytes));
        }

        if (type == SilType.F16) {
            return BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes));
        }

        if (type == SilType.BF16) {
            return new BFloat16(BitConverter.ToUInt16(bytes));
        }

        if (type == SilType.Bool) {
            return bytes[0] != 0;
        }

        var code = BitConverter.ToInt32(bytes);
        return Rune.IsValid(code) ? new Rune(code) : throw new SilTrap($"0x{code:x} is no character");
    }
}
