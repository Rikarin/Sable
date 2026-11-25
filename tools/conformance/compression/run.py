#!/usr/bin/env python3
"""Sable.IO.Compression against the formats' own test data.

    python3 -I tools/conformance/compression/run.py <data> [corpus file ...]

<data> has brotli's and zstd's sources, unpacked (README.md says which). Every
brotli stream of its tests is what its file has; zstd's golden frames are
decompressed, or refused, as they should be, and its other golden files are
compressed and compared; and Python's zlib and Sable's deflate read each other,
by every strategy, window and level. The corpus files, if any are given, are
compressed at each level and read by the gzip, zstd and brotli commands, where
there are such, and what those make is read here."""

import pathlib
import shutil
import subprocess
import sys
import tempfile
import zlib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, ask, build

LEVELS = {"gzip": range(0, 10), "zstd": [-5, -1, *range(1, 20)], "brotli": range(0, 12)}
READERS = {"gzip": ["gzip", "-dc"], "zstd": ["zstd", "-dcq"], "brotli": ["brotli", "-dc"]}
WRITERS = {
    "gzip": lambda level: ["gzip", "-c", f"-{max(level, 1)}"],
    "zstd": lambda level: ["zstd", "-cq", f"-{level}" if level > 0 else f"--fast={-level}"],
    "brotli": lambda level: ["brotli", "-c", "-q", str(level)],
}


def samples():
    """Bytes that zlib compresses in each of its ways."""
    state = 1
    noise = bytearray()
    while len(noise) < 100_000:
        state = state * 6364136223846793005 + 1442695040888963407 & (1 << 64) - 1
        noise.append(state >> 56)
    text = " ".join(str(i * i % 9973) for i in range(40_000)).encode()
    return {
        "empty": b"", "one": b"x", "noise": bytes(noise), "zeros": bytes(200_000), "text": text,
        "mixed": text[:50_000] + bytes(noise[:50_000]) + bytes(60_000) + text[20_000:90_000],
        "far": (text[:32_768] + bytes(noise[:40])) * 4,
    }


def main():
    data = pathlib.Path(sys.argv[1])
    program = build(pathlib.Path(__file__).with_name("answer.sb"))
    tally = Tally()
    with tempfile.TemporaryDirectory(prefix="sable-compression-") as directory:
        scratch = pathlib.Path(directory)
        made = scratch / "made"

        def question(*parts):
            return ask(program, ["\t".join(str(part) for part in parts)])[0]

        # Brotli's own streams.
        for packed in sorted(data.glob("brotli*/**/tests/testdata/*.compressed*")):
            expected = packed.with_name(packed.name.split(".compressed")[0])
            reply = question("decompress", "brotli", packed, made)
            tally.check("brotli streams", not reply.startswith(("error", "other")) and made.read_bytes() == expected.read_bytes(), f"{packed.name}: {reply}")

        # Zstd's golden frames and files.
        tests = next(iter(sorted(data.glob("zstd*/**/tests/golden-decompression"))), None)
        if tests is None:
            tally.skip("zstd golden", "not there")
        else:
            tests = tests.parent
            for packed in sorted((tests / "golden-decompression").glob("*.zst")):
                reply = question("decompress", "zstd", packed, made)
                right = reply.isdigit()
                if right and shutil.which("zstd"):
                    right = subprocess.run(["zstd", "-dcq", str(packed)], stdout=subprocess.PIPE).stdout == made.read_bytes()
                tally.check("zstd frames", right, f"{packed.name}: {reply}")
            for packed in sorted((tests / "golden-decompression-errors").glob("*.zst")):
                reply = question("decompress", "zstd", packed, made)
                tally.check("zstd errors", reply.startswith("error"), f"{packed.name}: {reply}")
            for plain in sorted((tests / "golden-compression").iterdir()):
                for level in LEVELS["zstd"]:
                    reply = question("compress", "zstd", plain, made, level)
                    right = reply.isdigit()
                    if right and shutil.which("zstd"):
                        right = subprocess.run(["zstd", "-dcq", str(made)], stdout=subprocess.PIPE).stdout == plain.read_bytes()
                    tally.check("zstd files", right, f"{plain.name} at {level}: {reply}")
                for dictionary in sorted((tests / "golden-dictionaries").iterdir()):
                    reply = question("dictionary", "zstd", dictionary, plain)
                    tally.check("zstd dictionaries", reply.isdigit(), f"{dictionary.name}: {reply}")

        # Python's zlib.
        plain = scratch / "plain"
        for name, content in samples().items():
            plain.write_bytes(content)
            for strategy in (zlib.Z_DEFAULT_STRATEGY, zlib.Z_FILTERED, zlib.Z_HUFFMAN_ONLY, zlib.Z_RLE, zlib.Z_FIXED):
                for bits in (9, 12, 15):
                    for level in (0, 1, 6, 9):
                        for codec, window in (("deflate", -bits), ("zlib", bits), ("gzip", bits + 16)):
                            packer = zlib.compressobj(level, zlib.DEFLATED, window, 1 + level % 9, strategy)
                            packed = packer.compress(content[:len(content) // 2]) + packer.flush(zlib.Z_SYNC_FLUSH) + packer.compress(content[len(content) // 2:]) + packer.flush()
                            made.write_bytes(packed)
                            reply = question("decompress", codec, made, scratch / "back")
                            tally.check(f"zlib to {codec}", reply.isdigit() and (scratch / "back").read_bytes() == content, f"{name}, {strategy}, {bits}, {level}: {reply}")
            for codec, window in (("deflate", -15), ("zlib", 15), ("gzip", 31)):
                for level in range(10):
                    reply = question("compress", codec, plain, made, level)
                    tally.check(f"{codec} to zlib", reply.isdigit() and zlib.decompress(made.read_bytes(), window) == content, f"{name} at {level}: {reply}")

        # The commands.
        for path in sys.argv[2:]:
            content = pathlib.Path(path).read_bytes()
            for codec, levels in LEVELS.items():
                if shutil.which(codec) is None:
                    tally.skip(codec, "no such command")
                    continue
                for level in levels:
                    reply = question("compress", codec, path, made, level)
                    read = subprocess.run([*READERS[codec], str(made)] if codec != "gzip" else ["gzip", "-dc"], stdin=made.open("rb"), stdout=subprocess.PIPE).stdout
                    tally.check(f"{codec} command reads", read == content, f"{path} at {level}: {reply}")
                    with made.open("wb") as target:
                        subprocess.run([*WRITERS[codec](level), path], stdout=target, check=True)
                    reply = question("decompress", codec, made, scratch / "back")
                    tally.check(f"{codec} command writes", reply.isdigit() and (scratch / "back").read_bytes() == content, f"{path} at {level}: {reply}")
    tally.finish()


main()
