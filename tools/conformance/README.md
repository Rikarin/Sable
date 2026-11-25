# Conformance suites

The official test data for what the library implements, run against it. The
data is not in the repository: download it, unpack each suite in a directory of
its own, and give that directory to the suite's script, from a built tree
(`dotnet build Sable.slnx`; `SABLE` may name another compiler's command).

```bash
python3 -I tools/conformance/crypto/run.py data/
python3 -I tools/conformance/ciphers/run.py data/
python3 -I tools/conformance/toml/run.py data/toml-test
python3 -I tools/conformance/yaml/run.py data/yaml-test-suite
python3 -I tools/conformance/net/run.py data/iana
python3 -I tools/conformance/random/run.py data/practrand/RNG_test --size 8GB
```

A script builds the Sable program beside it (`answer.sb`, `stream.sb`), which
only answers, and compares the answers with the data. It prints what failed and
how many cases of each part passed, failed and were skipped, and why; it exits
with 1 if any failed. Each suite's directory has only what this says of it, and
nothing in it is run but PractRand.

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
