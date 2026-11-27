# Conformance suites

The official test data for what the library implements, run against it. The
data is not in the repository: download it, unpack each suite in a directory of
its own, and give that directory to the suite's script, from a built tree
(`dotnet build Sable.slnx`; `SABLE` may name another compiler's command).

```bash
python3 -I tools/conformance/crypto/run.py data/
python3 -I tools/conformance/ciphers/run.py data/
python3 -I tools/conformance/publickey/run.py data/wycheproof
python3 -I tools/conformance/x509/run.py data/limbo.json
python3 -I tools/conformance/toml/run.py data/toml-test
python3 -I tools/conformance/yaml/run.py data/yaml-test-suite
python3 -I tools/conformance/xml/run.py data/xmlconf
python3 -I tools/conformance/json/run.py data/JSONTestSuite
python3 -I tools/conformance/compression/run.py data/compression [corpus file ...]
python3 -I tools/conformance/hashing/run.py data/xxhash
python3 -I tools/conformance/net/run.py data/iana
python3 -I tools/conformance/random/run.py data/practrand/RNG_test --size 8GB
```

A script builds the Sable program beside it (`answer.sb`, `stream.sb`), which
only answers, and compares the answers with the data. It prints what failed and
how many cases of each part passed, failed and were skipped, and why; it exits
with 1 if any failed. Each suite's directory has only what this says of it, and
nothing in it is run but PractRand.

## geometry: System.Numerics

No data to download: `geometry/reference.cs` prints what .NET's System.Numerics
makes of some inputs, as the table that `tests/lib/Sable.Math.Geometry/DotNetTests.sb`
compares with, which is in the repository. Its head says how to make it again.

## crypto: NIST's CAVP and ACVP, and Wycheproof

`data/cavp/`, unzipped: the byte-oriented vectors of
https://csrc.nist.gov/projects/cryptographic-algorithm-validation-program —
`shs/shabytetestvectors.zip` (SHA-256 of the file `929ef80b…f4c9b8`),
`sha3/sha-3bytetestvectors.zip` (`cd07701a…8d8fc8c`),
`sha3/shakebytetestvectors.zip` (`debfebc3…6de4c7`) and
`mac/hmactestvectors.zip` (`418c3837…ae4bd`).

`data/acvp/<name>.json`, each `gen-val/json-files/<name>/internalProjection.json`
of https://github.com/usnistgov/ACVP-Server at `975de31eb83d87039ec88934fdc47d8c312b892d`:
HMAC-{SHA-1,SHA2-256,SHA2-384,SHA2-512,SHA3-256,SHA3-384,SHA3-512}-{1.0,2.0},
SHA2-{256,384,512}-1.0, SHA3-{256,384,512}-2.0, SHAKE-{128,256}-{1.0,FIPS202},
KMAC-{128,256}-1.0, KDA-HKDF-Sp800-56C{r1,r2}, PBKDF-1.0.

NIST's are works of the United States government, in the public domain there.

`data/wycheproof/`, https://github.com/C2SP/wycheproof at
`65ca494727a52944c204441c860b039e6786b99b`: Apache-2.0.

Messages, keys and outputs that are not of whole bytes are skipped: the library
takes bytes. So are SHA-224, SHA3-224 and SHA-512/t, which it has not.
ChaCha20-Poly1305's vectors test the ChaCha generator's keystream. Neither
source has vectors for BLAKE2b, BLAKE3, Argon2id, scrypt or MD5.

## ciphers: NIST's CAVP and ACVP, and Wycheproof

As for crypto, and in the same directories or others. Of CAVP, unzipped each
in a directory of its own: `mac/gcmtestvectors.zip` (SHA-256 `f9fc479e…26a023`),
`mac/ccmtestvectors.zip` (`0e3c9676…aa673aa`), `mac/kwtestvectors.zip`
(`04a4a82e…80dfdf3`), `aes/KAT_AES.zip` (`a203b16c…098eb60`),
`aes/aesmmt.zip` (`12d1616f…cbaf0`) and `aes/aesmct.zip` (`6a2a72c0…571426b`).
Of ACVP, at the same commit, ACVP-AES-{GCM,CCM,CBC,CTR,ECB,CFB8,CFB128,KW,KWP}-1.0.
Of Wycheproof, AES-GCM, GMAC, CCM, CBC, key wrap with padding and without,
ChaCha20-Poly1305 and XChaCha20-Poly1305.

Skipped are what the library has not: GCM's tags of fewer than 12 bytes, key
wrap by the inverse cipher, CFB1 and OFB, counters that are not a sequence, and
what is not of whole bytes. Every vector is sealed and opened into another
array and in place, and what does not open must leave nothing but zeros. With
`SABLE_CRYPTO_PORTABLE=1` the same is asked of the portable code.

## publickey: Wycheproof

The same `data/wycheproof/`: ECDSA and ECDH on P-256, P-384 and P-521, Ed25519,
X25519, RSA signatures (PSS and PKCS #1 v1.5) and the keys of each in PEM, DER
and JWK. What is valid must be taken and what is invalid refused; what is
"acceptable" may be either. What the library has nothing for is counted as
skipped, with what it is.

## x509: x509-limbo

`data/limbo.json`, https://github.com/C2SP/x509-limbo's `limbo.json`
(SHA-256 `56799cef…9654fca`, 9807 cases): Apache-2.0. Each part is run by the
profile it is of: `rfc5280`, `crl`, `pathlen` and `pathological` by
`CertificateProfile.Rfc5280`, `webpki` by `.WebPki`, and `bettertls`, `cve` and
`online`, whose certificates are made by neither's rules, by `.Compatible`;
`--profile` runs all by one.

## compression: brotli's and zstd's own, and zlib

`data/compression/zstd/`, the archive of https://github.com/facebook/zstd v1.5.7
unpacked (`v1.5.7.tar.gz`, SHA-256 `37d72845…012ee3`): its golden frames, files
and dictionaries; BSD. `data/compression/brotli/tests/testdata/`, that directory
of https://github.com/google/brotli v1.0.9, file by file, as its archives leave
it out: MIT. Python's zlib is the third. Corpus files (Silesia's, say) are
compressed at every level and read by the `gzip`, `zstd` and `brotli` commands
that are there, and theirs here.

## hashing: xxHash's sanity vectors, and the CRC catalogue

`data/xxhash/sanity_test_vectors.h`, `tests/sanity_test_vectors.h` of
https://github.com/Cyan4973/xxHash at `2ec67caeb18ec15ae60c0853174331089acddfc0`
(SHA-256 `e81b5f30…589af7`): BSD 2-clause. Its XXH32, XXH64, XXH3 and XXH128
vectors, every length to 4160 of its sanity buffer with a seed of zero and
another, are asked at once and in pieces of three patterns of sizes; those
with a secret of their own are skipped, as the library, as .NET's, takes a
seed alone. CRC-32 (ISO-HDLC), CRC-32C (ISCSI), CRC-64 (ECMA-182, as .NET's
`Crc64`) and CRC-64 (NVME) are held at each length to 4160 and some longer
to one the script computes a bit at a time from Greg Cook's catalogue of
parametrised CRC algorithms, each first held to its check value; CRC-32 and
Adler-32 also to Python's zlib. Run it again with `SABLE_PORTABLE=1` for the
CRCs' portable code.

`tools/conformance/hashing/constants.py` works out `runtime/checksums.c`'s
folding constants and checks the folding, for each polynomial, against a CRC
computed a bit at a time; it needs no data.

## toml: toml-test

https://github.com/toml-lang/toml-test v2.2.0
(`ce08da1ddb075d1c7596d663c7fcba9a2ae02c5c`), MIT. The library reads TOML
1.0.0, so what `--version 1.1.0` adds fails, by design. The bytes are checked
to be UTF-8 (`String.fromUtf8`) before they are given to `Toml.parse`, which
takes text: `File.read` would make U+FFFD of what is not.

## yaml: yaml-test-suite

https://github.com/yaml/yaml-test-suite, the `data` branch at
`6ad3d2c62885d82fc349026c136ef560838fdf3d`, MIT. `run.py` lists the files that
are not as the suite has them, by design, under `KNOWN`: the same key twice in
a mapping is read as events and makes no nodes.

## xml: the W3C XML Conformance Test Suite

https://www.w3.org/XML/Test/xmlts20130923.tar.gz (SHA-256
`9b61db9f…6cf6fc1f`), W3C Software Notice and License; its directory
`xmlconf`. Every case of XML 1.0 (fifth edition) and Namespaces 1.0 that
reads no external entity: a `valid` or `invalid` document must be
well-formed (the reader does not validate) and read the same once it is
written again, and a `not-wf` one must be an error. XML 1.1, editions before
the fifth, the optional `error` cases and cases that need an external
entity or subset read are skipped, by their reason. At this writing 1727
pass, none fail and 858 are skipped.

## json: JSONTestSuite

https://github.com/nst/JSONTestSuite at
`1ef36fa01286573e846ac449e8683f8833c5b26a`, MIT; `test_parsing` of it. Each
document is read whole, in pieces of some sizes, as a tree and as a decoder,
which must agree, and what is accepted is written, printed and read again.
`run.py` lists under `DECISIONS` what is made of the `i_` documents, which RFC
8259 leaves open: any number of its grammar is a token, though an F64 is not
made of one past them all; a lone surrogate's escape, ill-formed UTF-8, UTF-16
and a byte order mark are refused; arrays and objects nest 512 deep.

## net: IANA's registries

As CSV, from https://www.iana.org/assignments/, which puts no restriction on
their use: `iana-ipv4-special-registry/iana-ipv4-special-registry-1.csv` and
`iana-ipv6-special-registry/iana-ipv6-special-registry-1.csv` (both as of
2025-10-09), and `service-names-port-numbers/service-names-port-numbers.csv`.
`lib/Sable.Net/IpAddress+Classification.sb` says which it was last compared
with.

## random: PractRand

https://pracrand.sourceforge.net, PractRand 0.96 (the zip's SHA-256
`e4caf7fda98b2c597bbda3b576753cf5a0f6047aab837c82be370ab798a672e1`), CC0. Only
its source, compiled as its `doc/installation.txt` says:

```bash
clang++ -O2 -std=c++14 -w -Iinclude -Itools src/*.cpp src/RNGs/*.cpp src/RNGs/other/*.cpp tools/RNG_test.cpp -o RNG_test -pthread
```

On ARM it includes an x86 header for `__rdtsc`, which only names a seed here:
give it an `x86intrin.h` with a function of that name in a directory of its own,
first among the `-I`s.
