#!/usr/bin/env python3
"""runtime/checksums.c's CRC folding constants, worked out and checked.

    python3 -I tools/conformance/hashing/constants.py

Prints, for each CRC, the four constants checksums.c's table has, and checks
the folding it does with them (four lanes of sixteen bytes, sixty-four bytes a
step, then sixteen; what is left given to the CRC from a state of zero) against
the CRC computed a bit at a time, at lengths from 64 to some thousands. Exits
with 1 if any differs.

A reflected CRC's lane is its sixteen bytes as a little-endian number, whose
bit i is the coefficient of x^(127 - i); its low half is the first eight bytes.
Moving a lane D bits on is multiplying it by x^D modulo the polynomial: the
high half (in the polynomial's terms) by x^(D + 64), the low by x^D. A carry-less
product of two bit-reversed 64-bit numbers is the bit-reversed 127-bit product,
one bit short of a lane, so each constant is one power of x less, reversed into
64 bits. ECMA-182's CRC is not reflected: its lane is the bytes as a big-endian
number, whose high half comes first, and its constants are x^(D + 64) and x^D,
aligned to the top of 64 bits as its register is.
"""

import random
import sys

M64 = (1 << 64) - 1

# name: polynomial, width, reflected, initial value, final xor, check value
CRCS = {
    "CRC_32": (0x04C11DB7, 32, True, 0xFFFFFFFF, 0xFFFFFFFF, 0xCBF43926),
    "CRC_32C": (0x1EDC6F41, 32, True, 0xFFFFFFFF, 0xFFFFFFFF, 0xE3069283),
    "CRC_64_NVME": (0xAD93D23594C93659, 64, True, M64, M64, 0xAE8B14860A799888),
    "CRC_64_ECMA": (0x42F0E1EBA9EA3693, 64, False, 0, 0, 0x6C40DF5F0B497347),
}


def clmul(a, b):
    product = 0
    while b:
        if b & 1:
            product ^= a
        a <<= 1
        b >>= 1
    return product


def x_to_the(n, polynomial, width):
    """x^n modulo x^width + the polynomial, as a number of width bits."""
    value = 1 << n
    full = 1 << width | polynomial
    while value.bit_length() > width:
        value ^= full << (value.bit_length() - 1 - width)
    return value


def reverse(value, bits):
    return int(format(value, f"0{bits}b")[::-1], 2)


def constants(polynomial, width, reflected):
    """What a lane's low half and high half are multiplied by, to move it 64
    bytes on, then 16."""
    if reflected:
        k = lambda n: reverse(x_to_the(n, polynomial, width), 64)
        return [k(575), k(511), k(191), k(127)]
    k = lambda n: x_to_the(n, polynomial, width) << (64 - width)
    return [k(512), k(576), k(128), k(192)]


def crc_bits(data, polynomial, width, reflected, initial, final):
    """The CRC a bit at a time."""
    crc = initial
    mask = (1 << width) - 1
    for byte in data:
        if reflected:
            crc ^= byte
            for _ in range(8):
                crc = crc >> 1 ^ reverse(polynomial, width) if crc & 1 else crc >> 1
        else:
            crc ^= byte << (width - 8)
            for _ in range(8):
                crc = (crc << 1 ^ polynomial) & mask if crc >> (width - 1) & 1 else crc << 1 & mask
    return crc ^ final


def folded(data, polynomial, width, reflected, state):
    """What checksums.c does: the sixteen bytes that stand for the whole
    sixty-four and more, a multiple of sixteen, and how many bytes they are."""
    order = "little" if reflected else "big"
    k = constants(polynomial, width, reflected)

    def lane(at):
        return int.from_bytes(data[at:at + 16], order)

    def fold(value, low, high, following):
        return clmul(value & M64, low) ^ clmul(value >> 64, high) ^ following

    lanes = [lane(16 * i) for i in range(4)]
    lanes[0] ^= state if reflected else state << (128 - width)
    at = 64
    while len(data) - at >= 64:
        lanes = [fold(lanes[i], k[0], k[1], lane(at + 16 * i)) for i in range(4)]
        at += 64
    value = lanes[0]
    for i in range(1, 4):
        value = fold(value, k[2], k[3], lanes[i])
    while len(data) - at >= 16:
        value = fold(value, k[2], k[3], lane(at))
        at += 16
    return value.to_bytes(16, order), at


def main():
    random_bytes = random.Random(69)
    failed = 0
    for name, (polynomial, width, reflected, initial, final, check) in CRCS.items():
        print(f"{name}: " + ", ".join(f"0x{c:016x}" for c in constants(polynomial, width, reflected)))
        if crc_bits(b"123456789", polynomial, width, reflected, initial, final) != check:
            print(f"  FAIL: the check value")
            failed += 1
        for count in [64, 80, 112, 128, 200, 1000, 4144, 4160]:
            data = bytes(random_bytes.randrange(256) for _ in range(count))
            rest, at = folded(data, polynomial, width, reflected, initial)
            # The register given back is the CRC before its final xor.
            got = crc_bits(rest + data[at:], polynomial, width, reflected, 0, final)
            if got != crc_bits(data, polynomial, width, reflected, initial, final):
                print(f"  FAIL: folded over {count} bytes")
                failed += 1
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
