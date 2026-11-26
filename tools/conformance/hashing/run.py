#!/usr/bin/env python3
"""Sable.IO.Hashing against xxHash's sanity vectors and the CRC catalogue.

    python3 -I tools/conformance/hashing/run.py <data>

<data>/sanity_test_vectors.h is xxHash's tests/sanity_test_vectors.h (README.md
says which). Its XXH32, XXH64, XXH3 and XXH128 vectors, of every length of the
sanity buffer up to 4160 with a seed of zero and another, are asked of the
library at once and in pieces; those with a secret of their own are skipped,
as the library, like .NET's, takes a seed alone. The CRCs and Adler-32 are
held, over the same buffer at each length to 4160 and some longer, to a CRC
computed here a bit at a time from the catalogue's parameters (Greg Cook's
CRC RevEng catalogue: CRC-32/ISO-HDLC, CRC-32/ISCSI, CRC-64/ECMA-182 as .NET
has it, CRC-64/NVME), each first checked against its check value; and to
Python's zlib for CRC-32 and Adler-32."""

import pathlib
import re
import sys
import zlib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, ask, build

M64 = (1 << 64) - 1

# name: polynomial, width, reflected, initial value, final xor, check value
CRCS = {
    "crc32": (0x04C11DB7, 32, True, 0xFFFFFFFF, 0xFFFFFFFF, 0xCBF43926),
    "crc32c": (0x1EDC6F41, 32, True, 0xFFFFFFFF, 0xFFFFFFFF, 0xE3069283),
    "crc64": (0x42F0E1EBA9EA3693, 64, False, 0, 0, 0x6C40DF5F0B497347),
    "crc64nvme": (0xAD93D23594C93659, 64, True, M64, M64, 0xAE8B14860A799888),
}


def sanity_buffer(count):
    generator = 2654435761
    out = bytearray()
    for _ in range(count):
        out.append(generator >> 56)
        generator = generator * 11400714785074694797 & M64
    return bytes(out)


def crc_tables(polynomial, width, reflected):
    table = []
    top = 1 << (width - 1)
    mask = (1 << width) - 1
    reversed_polynomial = int(format(polynomial, f"0{width}b")[::-1], 2)
    for byte in range(256):
        if reflected:
            crc = byte
            for _ in range(8):
                crc = crc >> 1 ^ reversed_polynomial if crc & 1 else crc >> 1
        else:
            crc = byte << (width - 8)
            for _ in range(8):
                crc = (crc << 1 ^ polynomial) & mask if crc & top else crc << 1 & mask
        table.append(crc)
    return table


def crc(name, data, cache={}):
    polynomial, width, reflected, initial, final, _ = CRCS[name]
    if name not in cache:
        cache[name] = crc_tables(polynomial, width, reflected)
    table = cache[name]
    value = initial
    if reflected:
        for byte in data:
            value = table[(value ^ byte) & 0xFF] ^ value >> 8
    else:
        for byte in data:
            value = table[(value >> (width - 8) ^ byte) & 0xFF] ^ value << 8 & ((1 << width) - 1)
    return value ^ final


def vectors(text, name):
    """The rows of one of the header's tables: (length, seed, result)."""
    start = text.index(f"XSUM_{name}_testdata[] = {{")
    end = text.index("};", start)
    rows = []
    for row in re.finditer(r"\{\s*(\d+),\s*0x([0-9A-Fa-f]+)U?L*,\s*(.+?)\s*\},\s*/\*", text[start:end]):
        length, seed, result = int(row.group(1)), int(row.group(2), 16), row.group(3)
        words = [int(word, 16) for word in re.findall(r"0x([0-9A-Fa-f]+)", result)]
        # XXH128's are { low64, high64 }.
        value = words[0] if len(words) == 1 else words[1] << 64 | words[0]
        rows.append((length, seed, value))
    return rows


def main():
    data = pathlib.Path(sys.argv[1])
    text = (data / "sanity_test_vectors.h").read_text()
    program = build(pathlib.Path(__file__).with_name("answer.sb"))
    tally = Tally()

    for name in CRCS:
        tally.check(name, crc(name, b"123456789") == CRCS[name][5], f"{name}'s check value, computed here")

    questions = []
    expected = []
    for name, table in (("xxh32", "XXH32"), ("xxh64", "XXH64"), ("xxh3", "XXH3"), ("xxh128", "XXH128")):
        rows = vectors(text, table)
        for length, seed, value in rows:
            for piece in (0, 1, 61):
                questions.append(f"{name} {seed} {length} {piece}")
                expected.append((name, value, f"length {length}, seed {seed:#x}, pieces from {piece}"))
        if name in ("xxh3", "xxh128"):
            for _ in vectors(text, table + "_withSecret"):
                tally.skip(name, "a secret of its own")

    buffer = sanity_buffer(70_000)
    lengths = list(range(0, 4161)) + [8191, 8192, 8193, 65_536, 70_000]
    for name in (*CRCS, "adler32"):
        for length in lengths:
            part = buffer[:length]
            value = zlib.adler32(part) if name == "adler32" else crc(name, part)
            for piece in (0, 1, 61) if length % 97 == 0 or length > 4096 else (0,):
                questions.append(f"{name} 0 {length} {piece}")
                expected.append((name, value, f"length {length}, pieces from {piece}"))
            if name == "crc32" and length % 7 == 0:
                tally.check("crc32", zlib.crc32(part) == value, f"zlib's CRC-32 of {length} bytes")

    answers = ask(program, questions)
    for (name, value, describe), answer in zip(expected, answers):
        tally.check(name, answer == format(value, "x"), f"{describe}: {answer}, not {value:x}")
    tally.finish()


if __name__ == "__main__":
    main()
