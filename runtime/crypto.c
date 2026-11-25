// What Sable.Crypto asks of the runtime: the compression functions the
// processor has instructions for, and zeroing that is not optimised away.
// (Random bytes are random.c's.)
//
// The hashes are written in Sable, portably, and that code is what runs
// wherever this file has nothing better: on a processor without the
// instructions, on WASI, and in the interpreter. Each kernel here does
// exactly what the portable block loop does to the same state, so whichever
// runs the digest is the same. None branches or indexes on the data: only
// on its length.

#if defined(__aarch64__) || defined(_M_ARM64)
// Compilers that give a function instructions its file is not compiled for.
#if defined(__ARM_FEATURE_SHA512) || (defined(__clang_major__) && __clang_major__ >= 16) || (!defined(__clang__) && defined(__GNUC__) && __GNUC__ >= 9)
#define SABLE_CRYPTO_ARM64 1
#endif
#elif defined(SABLE_X64)
#define SABLE_CRYPTO_X64 1
#endif

// Every round written out, so that the schedule's words stay in registers.
#if defined(__clang__)
#define SABLE_UNROLL _Pragma("clang loop unroll(full)")
#else
#define SABLE_UNROLL _Pragma("GCC unroll 64")
#endif

#if defined(SABLE_CRYPTO_ARM64)

#include <arm_neon.h>

#if defined(__APPLE__)
#include <sys/sysctl.h>
#elif defined(__linux__)
#include <sys/auxv.h>
#endif

// SHA-1's instructions are with SHA-256's.
#if defined(__clang__)
#define SABLE_TARGET_SHA2 __attribute__((target("sha2")))
#define SABLE_TARGET_SHA3 __attribute__((target("sha3")))
#else
#define SABLE_TARGET_SHA2 __attribute__((target("+sha2")))
#define SABLE_TARGET_SHA3 __attribute__((target("arch=armv8.2-a+sha3")))
#endif

#elif defined(SABLE_CRYPTO_X64)

#include <cpuid.h>
#include <immintrin.h>

#define SABLE_TARGET_SHANI __attribute__((target("sha,sse4.1,ssse3")))
#define SABLE_TARGET_BMI __attribute__((target("bmi,bmi2")))

#endif

// What 'kind' names, in its low eight bits; a sponge's rate, in bytes, is
// in the bits above.
enum {
    SABLE_CRYPTO_SHA256 = 1,   // a U32[8], blocks of 64 bytes
    SABLE_CRYPTO_SHA512 = 2,   // a U64[8], blocks of 128 bytes
    SABLE_CRYPTO_KECCAK = 3,   // a U64[25], blocks of the rate, each absorbed and permuted
    SABLE_CRYPTO_SHA1 = 4,     // a U32[5], blocks of 64 bytes
};

// What the processor has kernels for.
enum {
    CRYPTO_SHA256 = 1,
    CRYPTO_SHA512 = 2,
    CRYPTO_KECCAK = 4,
    CRYPTO_SHA1 = 8,
    CRYPTO_WIDE = 16,   // of SHA-512's and Keccak's, the AVX-512 ones
    CRYPTO_ASKED = 128,
    CRYPTO_AES = 256,          // AES, and the carryless multiplication GHASH is done by
    CRYPTO_VAES = 512,         // those on two blocks at once
    CRYPTO_VAES_WIDE = 1024,   // and on four
    CRYPTO_VECTORS = 2048,     // what ChaCha20 and Poly1305 need, which every one has
    CRYPTO_AVX2 = 4096,
};

#if defined(SABLE_CRYPTO_ARM64) || defined(SABLE_CRYPTO_X64)

static const uint32_t sha256_constants[64] __attribute__((aligned(16))) = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
};

static const uint64_t sha512_constants[80] __attribute__((aligned(32))) = {
    0x428a2f98d728ae22, 0x7137449123ef65cd, 0xb5c0fbcfec4d3b2f, 0xe9b5dba58189dbbc, 0x3956c25bf348b538, 0x59f111f1b605d019, 0x923f82a4af194f9b, 0xab1c5ed5da6d8118,
    0xd807aa98a3030242, 0x12835b0145706fbe, 0x243185be4ee4b28c, 0x550c7dc3d5ffb4e2, 0x72be5d74f27b896f, 0x80deb1fe3b1696b1, 0x9bdc06a725c71235, 0xc19bf174cf692694,
    0xe49b69c19ef14ad2, 0xefbe4786384f25e3, 0x0fc19dc68b8cd5b5, 0x240ca1cc77ac9c65, 0x2de92c6f592b0275, 0x4a7484aa6ea6e483, 0x5cb0a9dcbd41fbd4, 0x76f988da831153b5,
    0x983e5152ee66dfab, 0xa831c66d2db43210, 0xb00327c898fb213f, 0xbf597fc7beef0ee4, 0xc6e00bf33da88fc2, 0xd5a79147930aa725, 0x06ca6351e003826f, 0x142929670a0e6e70,
    0x27b70a8546d22ffc, 0x2e1b21385c26c926, 0x4d2c6dfc5ac42aed, 0x53380d139d95b3df, 0x650a73548baf63de, 0x766a0abb3c77b2a8, 0x81c2c92e47edaee6, 0x92722c851482353b,
    0xa2bfe8a14cf10364, 0xa81a664bbc423001, 0xc24b8b70d0f89791, 0xc76c51a30654be30, 0xd192e819d6ef5218, 0xd69906245565a910, 0xf40e35855771202a, 0x106aa07032bbd1b8,
    0x19a4c116b8d2d0c8, 0x1e376c085141ab53, 0x2748774cdf8eeb99, 0x34b0bcb5e19b48a8, 0x391c0cb3c5c95a63, 0x4ed8aa4ae3418acb, 0x5b9cca4f7763e373, 0x682e6ff3d6b2b8a3,
    0x748f82ee5defb2fc, 0x78a5636f43172f60, 0x84c87814a1f0ab72, 0x8cc702081a6439ec, 0x90befffa23631e28, 0xa4506cebde82bde9, 0xbef9a3f7b2c67915, 0xc67178f2e372532b,
    0xca273eceea26619c, 0xd186b8c721c0c207, 0xeada7dd6cde0eb1e, 0xf57d4f7fee6ed178, 0x06f067aa72176fba, 0x0a637dc5a2c898a6, 0x113f9804bef90dae, 0x1b710b35131c471b,
    0x28db77f523047d84, 0x32caab7b40c72493, 0x3c9ebe0a15c9bebc, 0x431d67c49c100d4c, 0x4cc5d4becb3e42b6, 0x597f299cfc657e2a, 0x5fcb6fab3ad6faec, 0x6c44198c4a475817,
};

static const uint64_t keccak_constants[24] = {
    0x0000000000000001, 0x0000000000008082, 0x800000000000808a, 0x8000000080008000, 0x000000000000808b, 0x0000000080000001,
    0x8000000080008081, 0x8000000000008009, 0x000000000000008a, 0x0000000000000088, 0x0000000080008009, 0x000000008000000a,
    0x000000008000808b, 0x800000000000008b, 0x8000000000008089, 0x8000000000008003, 0x8000000000008002, 0x8000000000000080,
    0x000000000000800a, 0x800000008000000a, 0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
};

// Keccak-f[1600] on the 25 lanes of 'a', in the instructions the processor
// has for its steps: theta's columns, and what each adds to its neighbours
// (THETA declares 'd0' to 'd4'); rho and pi, a lane with theta's column
// rotated left and moved; chi; and iota into the first lane.
#define SABLE_KECCAK_ROUNDS(LANE, XOR, XOR3, THETA, MOVED, CHI, CONSTANT) \
    for (int round = 0; round < 24; round++) { \
        LANE c0 = XOR3(XOR3(a[0], a[5], a[10]), a[15], a[20]); \
        LANE c1 = XOR3(XOR3(a[1], a[6], a[11]), a[16], a[21]); \
        LANE c2 = XOR3(XOR3(a[2], a[7], a[12]), a[17], a[22]); \
        LANE c3 = XOR3(XOR3(a[3], a[8], a[13]), a[18], a[23]); \
        LANE c4 = XOR3(XOR3(a[4], a[9], a[14]), a[19], a[24]); \
        THETA; \
        LANE b0 = MOVED(a[0], 0, 0), b1 = MOVED(a[6], 1, 44), b2 = MOVED(a[12], 2, 43), b3 = MOVED(a[18], 3, 21), b4 = MOVED(a[24], 4, 14); \
        LANE b5 = MOVED(a[3], 3, 28), b6 = MOVED(a[9], 4, 20), b7 = MOVED(a[10], 0, 3), b8 = MOVED(a[16], 1, 45), b9 = MOVED(a[22], 2, 61); \
        LANE b10 = MOVED(a[1], 1, 1), b11 = MOVED(a[7], 2, 6), b12 = MOVED(a[13], 3, 25), b13 = MOVED(a[19], 4, 8), b14 = MOVED(a[20], 0, 18); \
        LANE b15 = MOVED(a[4], 4, 27), b16 = MOVED(a[5], 0, 36), b17 = MOVED(a[11], 1, 10), b18 = MOVED(a[17], 2, 15), b19 = MOVED(a[23], 3, 56); \
        LANE b20 = MOVED(a[2], 2, 62), b21 = MOVED(a[8], 3, 55), b22 = MOVED(a[14], 4, 39), b23 = MOVED(a[15], 0, 41), b24 = MOVED(a[21], 1, 2); \
        a[0] = XOR(CHI(b0, b1, b2), CONSTANT(keccak_constants + round)); \
        a[1] = CHI(b1, b2, b3), a[2] = CHI(b2, b3, b4), a[3] = CHI(b3, b4, b0), a[4] = CHI(b4, b0, b1); \
        a[5] = CHI(b5, b6, b7), a[6] = CHI(b6, b7, b8), a[7] = CHI(b7, b8, b9), a[8] = CHI(b8, b9, b5), a[9] = CHI(b9, b5, b6); \
        a[10] = CHI(b10, b11, b12), a[11] = CHI(b11, b12, b13), a[12] = CHI(b12, b13, b14), a[13] = CHI(b13, b14, b10), a[14] = CHI(b14, b10, b11); \
        a[15] = CHI(b15, b16, b17), a[16] = CHI(b16, b17, b18), a[17] = CHI(b17, b18, b19), a[18] = CHI(b18, b19, b15), a[19] = CHI(b19, b15, b16); \
        a[20] = CHI(b20, b21, b22), a[21] = CHI(b21, b22, b23), a[22] = CHI(b22, b23, b24), a[23] = CHI(b23, b24, b20), a[24] = CHI(b24, b20, b21); \
    }

#endif

#if defined(SABLE_CRYPTO_ARM64)

static const uint32_t sha1_constants[4] = {0x5a827999, 0x6ed9eba1, 0x8f1bbcdc, 0xca62c1d6};

// ---- What the processor has --------------------------------------------------

#if defined(__APPLE__)
static bool apple_feature(const char *name) {
    int value = 0;
    size_t size = sizeof value;
    return sysctlbyname(name, &value, &size, NULL, 0) == 0 && value != 0;
}
#endif

static int crypto_features(void) {
#if defined(__APPLE__)
    // Every 64-bit ARM Apple has made has SHA-1's and SHA-256's. Keccak's are
    // used only here: Arm's own processors (Neoverse) do them all on one of
    // their vector units, at half the portable code's speed.
    return CRYPTO_VECTORS | CRYPTO_AES | CRYPTO_SHA1 | CRYPTO_SHA256 | (apple_feature("hw.optional.arm.FEAT_SHA512") ? CRYPTO_SHA512 : 0)
        | (apple_feature("hw.optional.arm.FEAT_SHA3") ? CRYPTO_KECCAK : 0);
#elif defined(__linux__)
    // HWCAP_AES, HWCAP_PMULL, HWCAP_SHA1, HWCAP_SHA2 and HWCAP_SHA512, which
    // older headers lack.
    unsigned long has = getauxval(AT_HWCAP);
    return CRYPTO_VECTORS | ((has >> 3 & 3) == 3 ? CRYPTO_AES : 0)
        | ((has >> 5 & 1) ? CRYPTO_SHA1 : 0) | ((has >> 6 & 1) ? CRYPTO_SHA256 : 0) | ((has >> 21 & 1) ? CRYPTO_SHA512 : 0);
#elif defined(_WIN32)
    // PF_ARM_V8_CRYPTO_INSTRUCTIONS_AVAILABLE and
    // PF_ARM_SHA512_INSTRUCTIONS_AVAILABLE, which older headers lack.
    return CRYPTO_VECTORS | (IsProcessorFeaturePresent(30) ? CRYPTO_AES | CRYPTO_SHA1 | CRYPTO_SHA256 : 0) | (IsProcessorFeaturePresent(65) ? CRYPTO_SHA512 : 0);
#else
    return CRYPTO_VECTORS;
#endif
}

// ---- SHA-1, by ARMv8's instructions --------------------------------------------

SABLE_TARGET_SHA2 static void sha1_blocks(uint32_t *state, const uint8_t *data, int64_t count) {
    uint32x4_t abcd = vld1q_u32(state);
    uint32_t e = state[4];
    for (; count >= 64; count -= 64, data += 64) {
        uint32x4_t abcd_before = abcd;
        uint32_t e_before = e;
        uint32x4_t w[4];
        SABLE_UNROLL
        for (int i = 0; i < 4; i++) {
            w[i] = vreinterpretq_u32_u8(vrev32q_u8(vld1q_u8(data + 16 * i)));
        }

        // Four rounds a step, the message's schedule four words at a time.
        SABLE_UNROLL
        for (int i = 0; i < 20; i++) {
            if (i >= 4) {
                w[i & 3] = vsha1su1q_u32(vsha1su0q_u32(w[i & 3], w[(i + 1) & 3], w[(i + 2) & 3]), w[(i + 3) & 3]);
            }

            uint32x4_t wk = vaddq_u32(w[i & 3], vdupq_n_u32(sha1_constants[i / 5]));
            uint32_t next = vsha1h_u32(vgetq_lane_u32(abcd, 0));
            abcd = i < 5 ? vsha1cq_u32(abcd, e, wk) : i >= 10 && i < 15 ? vsha1mq_u32(abcd, e, wk) : vsha1pq_u32(abcd, e, wk);
            e = next;
        }

        abcd = vaddq_u32(abcd, abcd_before);
        e += e_before;
    }

    vst1q_u32(state, abcd);
    state[4] = e;
}

// ---- SHA-256, by ARMv8's instructions ----------------------------------------

SABLE_TARGET_SHA2 static void sha256_blocks(uint32_t *state, const uint8_t *data, int64_t count) {
    uint32x4_t abcd = vld1q_u32(state);
    uint32x4_t efgh = vld1q_u32(state + 4);
    for (; count >= 64; count -= 64, data += 64) {
        uint32x4_t abcd_before = abcd;
        uint32x4_t efgh_before = efgh;
        uint32x4_t w[4];
        SABLE_UNROLL
        for (int i = 0; i < 4; i++) {
            w[i] = vreinterpretq_u32_u8(vrev32q_u8(vld1q_u8(data + 16 * i)));
        }

        // Four rounds a step, the message's schedule four words at a time.
        SABLE_UNROLL
        for (int i = 0; i < 16; i++) {
            if (i >= 4) {
                w[i & 3] = vsha256su1q_u32(vsha256su0q_u32(w[i & 3], w[(i + 1) & 3]), w[(i + 2) & 3], w[(i + 3) & 3]);
            }

            uint32x4_t wk = vaddq_u32(w[i & 3], vld1q_u32(sha256_constants + 4 * i));
            uint32x4_t next = vsha256hq_u32(abcd, efgh, wk);
            efgh = vsha256h2q_u32(efgh, abcd, wk);
            abcd = next;
        }

        abcd = vaddq_u32(abcd, abcd_before);
        efgh = vaddq_u32(efgh, efgh_before);
    }

    vst1q_u32(state, abcd);
    vst1q_u32(state + 4, efgh);
}

// ---- SHA-512, by ARMv8.2's instructions --------------------------------------

SABLE_TARGET_SHA3 static void sha512_blocks(uint64_t *state, const uint8_t *data, int64_t count) {
    uint64x2_t ab = vld1q_u64(state);
    uint64x2_t cd = vld1q_u64(state + 2);
    uint64x2_t ef = vld1q_u64(state + 4);
    uint64x2_t gh = vld1q_u64(state + 6);
    for (; count >= 128; count -= 128, data += 128) {
        uint64x2_t ab_before = ab, cd_before = cd, ef_before = ef, gh_before = gh;
        uint64x2_t w[8];
        SABLE_UNROLL
        for (int i = 0; i < 8; i++) {
            w[i] = vreinterpretq_u64_u8(vrev64q_u8(vld1q_u8(data + 16 * i)));
        }

        // Two rounds a step.
        SABLE_UNROLL
        for (int i = 0; i < 40; i++) {
            if (i >= 8) {
                w[i & 7] = vsha512su1q_u64(vsha512su0q_u64(w[i & 7], w[(i + 1) & 7]), w[(i + 7) & 7], vextq_u64(w[(i + 4) & 7], w[(i + 5) & 7], 1));
            }

            uint64x2_t wk = vaddq_u64(w[i & 7], vld1q_u64(sha512_constants + 2 * i));
            uint64x2_t sum = vaddq_u64(vextq_u64(wk, wk, 1), gh);
            uint64x2_t half = vsha512hq_u64(sum, vextq_u64(ef, gh, 1), vextq_u64(cd, ef, 1));
            uint64x2_t next = vsha512h2q_u64(half, cd, ab);
            gh = ef;
            ef = vaddq_u64(cd, half);
            cd = ab;
            ab = next;
        }

        ab = vaddq_u64(ab, ab_before);
        cd = vaddq_u64(cd, cd_before);
        ef = vaddq_u64(ef, ef_before);
        gh = vaddq_u64(gh, gh_before);
    }

    vst1q_u64(state, ab);
    vst1q_u64(state + 2, cd);
    vst1q_u64(state + 4, ef);
    vst1q_u64(state + 6, gh);
}

// ---- Keccak-f[1600], by ARMv8.2's instructions ---------------------------------

#define SABLE_LANE(words) vcombine_u64(vld1_u64(words), vdup_n_u64(0))
#define SABLE_THETA \
    uint64x2_t d0 = vrax1q_u64(c4, c1), d1 = vrax1q_u64(c0, c2), d2 = vrax1q_u64(c1, c3), d3 = vrax1q_u64(c2, c4), d4 = vrax1q_u64(c3, c0)
#define SABLE_MOVED(x, column, n) vxarq_u64(x, d##column, (64 - (n)) & 63)
#define SABLE_CHI(x, y, z) vbcaxq_u64(x, z, y)

// A lane to a register's low half; the high halves are not used.
SABLE_TARGET_SHA3 static void keccak_blocks(uint64_t *state, const uint8_t *data, int64_t count, int64_t rate) {
    uint64x2_t a[25];
    for (int i = 0; i < 25; i++) {
        a[i] = SABLE_LANE(state + i);
    }

    for (; count >= rate; count -= rate, data += rate) {
        for (int i = 0; i < rate / 8; i++) {
            a[i] = veorq_u64(a[i], vcombine_u64(vreinterpret_u64_u8(vld1_u8(data + 8 * i)), vdup_n_u64(0)));
        }

        SABLE_KECCAK_ROUNDS(uint64x2_t, veorq_u64, veor3q_u64, SABLE_THETA, SABLE_MOVED, SABLE_CHI, SABLE_LANE)
    }

    for (int i = 0; i < 25; i++) {
        vst1_u64(state + i, vget_low_u64(a[i]));
    }
}

#elif defined(SABLE_CRYPTO_X64)

// ---- What the processor has --------------------------------------------------

static int crypto_features(void) {
    unsigned int a, b, c, d;
    if (!__get_cpuid(1, &a, &b, &c, &d)) {
        return CRYPTO_VECTORS;
    }

    bool vectors = (c >> 9 & 1) && (c >> 19 & 1);   // SSSE3 and SSE4.1
    int features = CRYPTO_VECTORS | (vectors && (c >> 25 & 1) && (c >> 1 & 1) ? CRYPTO_AES : 0);
    if (!__get_cpuid_count(7, 0, &a, &b, &c, &d)) {
        b = c = 0;
    }

    features |= vectors && (b >> 29 & 1) ? CRYPTO_SHA1 | CRYPTO_SHA256 : 0;
    bool wide = (processor_known() & PROCESSOR_AVX512) != 0;
    if (processor_has_avx2()) {
        features |= CRYPTO_SHA512 | CRYPTO_KECCAK | CRYPTO_AVX2;
        if ((features & CRYPTO_AES) && (c >> 9 & 1) && (c >> 10 & 1)) {   // VAES and VPCLMULQDQ
            features |= CRYPTO_VAES | (wide && (b >> 30 & 1) ? CRYPTO_VAES_WIDE : 0);   // AVX-512's BW
        }
    }

    return features | (wide ? CRYPTO_WIDE : 0);
}

// ---- SHA-1, by the SHA extensions ----------------------------------------------

SABLE_TARGET_SHANI static void sha1_blocks(uint32_t *state, const uint8_t *data, int64_t count) {
    const __m128i big = _mm_set_epi64x(0x0001020304050607ll, 0x08090a0b0c0d0e0fll);

    // The instructions keep A in a register's highest word.
    __m128i abcd = _mm_shuffle_epi32(_mm_loadu_si128((const __m128i *)state), 0x1B);
    __m128i e = _mm_set_epi32((int)state[4], 0, 0, 0);
    for (; count >= 64; count -= 64, data += 64) {
        __m128i abcd_before = abcd;
        __m128i e_before = e;
        __m128i w[4];
        SABLE_UNROLL
        for (int i = 0; i < 4; i++) {
            w[i] = _mm_shuffle_epi8(_mm_loadu_si128((const __m128i *)(data + 16 * i)), big);
        }

        // Four rounds a step, the message's schedule four words at a time.
        // 'e' is E itself at first, and then A of the step before, which is
        // rotated to make it.
        SABLE_UNROLL
        for (int i = 0; i < 20; i++) {
            if (i >= 4) {
                w[i & 3] = _mm_sha1msg2_epu32(_mm_xor_si128(_mm_sha1msg1_epu32(w[i & 3], w[(i + 1) & 3]), w[(i + 2) & 3]), w[(i + 3) & 3]);
            }

            __m128i we = i == 0 ? _mm_add_epi32(e, w[0]) : _mm_sha1nexte_epu32(e, w[i & 3]);
            e = abcd;
            switch (i / 5) {
                case 0: abcd = _mm_sha1rnds4_epu32(abcd, we, 0); break;
                case 1: abcd = _mm_sha1rnds4_epu32(abcd, we, 1); break;
                case 2: abcd = _mm_sha1rnds4_epu32(abcd, we, 2); break;
                default: abcd = _mm_sha1rnds4_epu32(abcd, we, 3); break;
            }
        }

        e = _mm_sha1nexte_epu32(e, e_before);
        abcd = _mm_add_epi32(abcd, abcd_before);
    }

    _mm_storeu_si128((__m128i *)state, _mm_shuffle_epi32(abcd, 0x1B));
    state[4] = (uint32_t)_mm_extract_epi32(e, 3);
}

// ---- SHA-256, by the SHA extensions ------------------------------------------

SABLE_TARGET_SHANI static void sha256_blocks(uint32_t *state, const uint8_t *data, int64_t count) {
    const __m128i big = _mm_set_epi64x(0x0c0d0e0f08090a0bll, 0x0405060700010203ll);

    // The instructions keep the state as ABEF and CDGH.
    __m128i low = _mm_shuffle_epi32(_mm_loadu_si128((const __m128i *)state), 0xB1);
    __m128i high = _mm_shuffle_epi32(_mm_loadu_si128((const __m128i *)(state + 4)), 0x1B);
    __m128i abef = _mm_alignr_epi8(low, high, 8);
    __m128i cdgh = _mm_blend_epi16(high, low, 0xF0);
    for (; count >= 64; count -= 64, data += 64) {
        __m128i abef_before = abef;
        __m128i cdgh_before = cdgh;
        __m128i w[4];
        SABLE_UNROLL
        for (int i = 0; i < 4; i++) {
            w[i] = _mm_shuffle_epi8(_mm_loadu_si128((const __m128i *)(data + 16 * i)), big);
        }

        // Four rounds a step, the message's schedule four words at a time.
        SABLE_UNROLL
        for (int i = 0; i < 16; i++) {
            if (i >= 4) {
                __m128i seven = _mm_alignr_epi8(w[(i + 3) & 3], w[(i + 2) & 3], 4);
                __m128i sum = _mm_add_epi32(_mm_sha256msg1_epu32(w[i & 3], w[(i + 1) & 3]), seven);
                w[i & 3] = _mm_sha256msg2_epu32(sum, w[(i + 3) & 3]);
            }

            __m128i wk = _mm_add_epi32(w[i & 3], _mm_load_si128((const __m128i *)(sha256_constants + 4 * i)));
            cdgh = _mm_sha256rnds2_epu32(cdgh, abef, wk);
            abef = _mm_sha256rnds2_epu32(abef, cdgh, _mm_shuffle_epi32(wk, 0x0E));
        }

        abef = _mm_add_epi32(abef, abef_before);
        cdgh = _mm_add_epi32(cdgh, cdgh_before);
    }

    low = _mm_shuffle_epi32(abef, 0x1B);
    high = _mm_shuffle_epi32(cdgh, 0xB1);
    _mm_storeu_si128((__m128i *)state, _mm_blend_epi16(low, high, 0xF0));
    _mm_storeu_si128((__m128i *)(state + 4), _mm_alignr_epi8(high, low, 8));
}

void sable_secure_zero(void *bytes, int64_t count);

// ---- SHA-512, by AVX2 or AVX-512, and BMI2 -------------------------------------

#define SABLE_ROTATE(x, n) ((x) >> (n) | (x) << (64 - (n)))

// Keeps a sum as it is written: the compiler would sooner add in an order
// that makes each round wait longer for the one before.
#define SABLE_SUMMED(x) __asm__("" : "+r"(x))

// A round, given its message word with its constant. The words take each
// other's names from one to the next rather than move; 'bc' is b ^ c, which
// was the round before's a ^ b.
#define SABLE_SHA512_ROUND(a, b, c, d, e, f, g, h, word) \
    do { \
        uint64_t ab = a ^ b; \
        h += (word); \
        SABLE_SUMMED(h); \
        h += (e & f) + (~e & g); \
        SABLE_SUMMED(h); \
        h += SABLE_ROTATE(e, 14) ^ SABLE_ROTATE(e, 18) ^ SABLE_ROTATE(e, 41); \
        SABLE_SUMMED(h); \
        d += h; \
        bc = (bc & ab) ^ b; \
        h += bc + (SABLE_ROTATE(a, 28) ^ SABLE_ROTATE(a, 34) ^ SABLE_ROTATE(a, 39)); \
        bc = ab; \
    } while (0)

// Sixteen rounds, STEP before each two of them. The words are two in four of
// those at 'wk', one block's of a pair.
#define SABLE_SHA512_ROUNDS(STEP) \
    STEP(0) SABLE_SHA512_ROUND(a, b, c, d, e, f, g, h, wk[0]); SABLE_SHA512_ROUND(h, a, b, c, d, e, f, g, wk[1]); \
    STEP(1) SABLE_SHA512_ROUND(g, h, a, b, c, d, e, f, wk[4]); SABLE_SHA512_ROUND(f, g, h, a, b, c, d, e, wk[5]); \
    STEP(2) SABLE_SHA512_ROUND(e, f, g, h, a, b, c, d, wk[8]); SABLE_SHA512_ROUND(d, e, f, g, h, a, b, c, wk[9]); \
    STEP(3) SABLE_SHA512_ROUND(c, d, e, f, g, h, a, b, wk[12]); SABLE_SHA512_ROUND(b, c, d, e, f, g, h, a, wk[13]); \
    STEP(4) SABLE_SHA512_ROUND(a, b, c, d, e, f, g, h, wk[16]); SABLE_SHA512_ROUND(h, a, b, c, d, e, f, g, wk[17]); \
    STEP(5) SABLE_SHA512_ROUND(g, h, a, b, c, d, e, f, wk[20]); SABLE_SHA512_ROUND(f, g, h, a, b, c, d, e, wk[21]); \
    STEP(6) SABLE_SHA512_ROUND(e, f, g, h, a, b, c, d, wk[24]); SABLE_SHA512_ROUND(d, e, f, g, h, a, b, c, wk[25]); \
    STEP(7) SABLE_SHA512_ROUND(c, d, e, f, g, h, a, b, wk[28]); SABLE_SHA512_ROUND(b, c, d, e, f, g, h, a, wk[29]);

#define SABLE_SHA512_LOAD \
    uint64_t a = state[0], b = state[1], c = state[2], d = state[3]; \
    uint64_t e = state[4], f = state[5], g = state[6], h = state[7]; \
    uint64_t bc = b ^ c

#define SABLE_SHA512_ADD \
    state[0] += a, state[1] += b, state[2] += c, state[3] += d; \
    state[4] += e, state[5] += f, state[6] += g, state[7] += h

#define SABLE_NO_STEP(i)

// The words sixteen rounds on, two of each block of the pair.
#define SABLE_SHA512_STEP(i) \
    { \
        __m256i one = _mm256_alignr_epi8(w[(i + 1) & 7], w[i], 8); \
        __m256i nine = _mm256_alignr_epi8(w[(i + 5) & 7], w[(i + 4) & 7], 8); \
        __m256i sum = _mm256_add_epi64(_mm256_add_epi64(w[i], nine), SABLE_SIGMA(one, 1, 8, 7)); \
        w[i] = _mm256_add_epi64(sum, SABLE_SIGMA(w[(i + 7) & 7], 19, 61, 6)); \
        __m256i k = _mm256_broadcastsi128_si256(_mm_load_si128((const __m128i *)(sha512_constants + t + 16 + 2 * i))); \
        _mm256_store_si256((__m256i *)(wk + 32 + 4 * i), _mm256_add_epi64(w[i], k)); \
    }

// The schedules of two blocks at once, one in each half of the registers (the
// same one in both, for a block that is left), and meanwhile the first one's
// rounds: each of those waits on the one before, which leaves the processor
// room to schedule in. The second one's words are kept for after. What is
// hashed may be a secret, so the words are zeroed.
#define SABLE_SHA512_KERNEL(name, TARGET) \
    TARGET static void name##_rounds(uint64_t *state, const uint64_t *wk) { \
        SABLE_SHA512_LOAD; \
        for (int t = 0; t < 80; t += 16, wk += 32) { \
            SABLE_SHA512_ROUNDS(SABLE_NO_STEP) \
        } \
        SABLE_SHA512_ADD; \
    } \
    \
    TARGET static void name##_first(uint64_t *state, uint64_t *wk, const uint8_t *data, const uint8_t *next) { \
        const __m256i big = _mm256_broadcastsi128_si256(_mm_set_epi64x(0x08090a0b0c0d0e0fll, 0x0001020304050607ll)); \
        __m256i w[8]; \
        SABLE_UNROLL \
        for (int i = 0; i < 8; i++) { \
            __m256i pair = _mm256_castsi128_si256(_mm_loadu_si128((const __m128i *)(data + 16 * i))); \
            pair = _mm256_inserti128_si256(pair, _mm_loadu_si128((const __m128i *)(next + 16 * i)), 1); \
            w[i] = _mm256_shuffle_epi8(pair, big); \
            __m256i k = _mm256_broadcastsi128_si256(_mm_load_si128((const __m128i *)(sha512_constants + 2 * i))); \
            _mm256_store_si256((__m256i *)(wk + 4 * i), _mm256_add_epi64(w[i], k)); \
        } \
        \
        SABLE_SHA512_LOAD; \
        for (int t = 0; t < 64; t += 16, wk += 32) { \
            SABLE_SHA512_ROUNDS(SABLE_SHA512_STEP) \
        } \
        SABLE_SHA512_ROUNDS(SABLE_NO_STEP) \
        SABLE_SHA512_ADD; \
    } \
    \
    static void name(uint64_t *state, const uint8_t *data, int64_t count) { \
        uint64_t words[160] __attribute__((aligned(32))); \
        for (; count >= 256; count -= 256, data += 256) { \
            name##_first(state, words, data, data + 128); \
            name##_rounds(state, words + 2); \
        } \
        if (count >= 128) { \
            name##_first(state, words, data, data); \
        } \
        sable_secure_zero(words, sizeof words); \
    }

#define SABLE_ROTATE_LANES(x, n) _mm256_or_si256(_mm256_srli_epi64(x, n), _mm256_slli_epi64(x, 64 - (n)))
#define SABLE_SIGMA(x, p, q, r) _mm256_xor_si256(_mm256_xor_si256(SABLE_ROTATE_LANES(x, p), SABLE_ROTATE_LANES(x, q)), _mm256_srli_epi64(x, r))

SABLE_SHA512_KERNEL(sha512_blocks, SABLE_TARGET_AVX2)

#undef SABLE_SIGMA
#define SABLE_SIGMA(x, p, q, r) _mm256_ternarylogic_epi64(_mm256_ror_epi64(x, p), _mm256_ror_epi64(x, q), _mm256_srli_epi64(x, r), 0x96)

SABLE_SHA512_KERNEL(sha512_blocks_wide, SABLE_TARGET_AVX512)

// ---- Keccak-f[1600], by BMI's instructions -------------------------------------

#define SABLE_XOR(x, y) ((x) ^ (y))
#define SABLE_XOR3(x, y, z) ((x) ^ (y) ^ (z))
#define SABLE_LEFT(x, n) ((x) << (n) | (x) >> ((64 - (n)) & 63))
#define SABLE_THETA \
    uint64_t d0 = c4 ^ SABLE_LEFT(c1, 1), d1 = c0 ^ SABLE_LEFT(c2, 1), d2 = c1 ^ SABLE_LEFT(c3, 1); \
    uint64_t d3 = c2 ^ SABLE_LEFT(c4, 1), d4 = c3 ^ SABLE_LEFT(c0, 1)
#define SABLE_MOVED(x, column, n) SABLE_LEFT((x) ^ d##column, n)
#define SABLE_CHI(x, y, z) ((x) ^ (~(y) & (z)))
#define SABLE_CONSTANT(word) (*(word))

SABLE_TARGET_BMI static void keccak_blocks(uint64_t *state, const uint8_t *data, int64_t count, int64_t rate) {
    uint64_t a[25];
    memcpy(a, state, sizeof a);
    for (; count >= rate; count -= rate, data += rate) {
        for (int i = 0; i < rate / 8; i++) {
            uint64_t word;
            memcpy(&word, data + 8 * i, 8);
            a[i] ^= word;
        }

        SABLE_KECCAK_ROUNDS(uint64_t, SABLE_XOR, SABLE_XOR3, SABLE_THETA, SABLE_MOVED, SABLE_CHI, SABLE_CONSTANT)
    }

    memcpy(state, a, sizeof a);
}

#undef SABLE_THETA
#undef SABLE_MOVED
#undef SABLE_CHI

// ---- Keccak-f[1600], by AVX-512's, a lane to a register's low half ---------------

// What theta adds to a lane is two columns, one of them rotated ('d' here),
// all three put together at once.
#define SABLE_LANE(words) _mm_loadl_epi64((const __m128i *)(words))
#define SABLE_LANES_XOR3(x, y, z) _mm_ternarylogic_epi64(x, y, z, 0x96)
#define SABLE_THETA \
    __m128i d0 = _mm_rol_epi64(c1, 1), d1 = _mm_rol_epi64(c2, 1), d2 = _mm_rol_epi64(c3, 1); \
    __m128i d3 = _mm_rol_epi64(c4, 1), d4 = _mm_rol_epi64(c0, 1)
#define SABLE_BEFORE_0 c4
#define SABLE_BEFORE_1 c0
#define SABLE_BEFORE_2 c1
#define SABLE_BEFORE_3 c2
#define SABLE_BEFORE_4 c3
#define SABLE_MOVED(x, column, n) _mm_rol_epi64(SABLE_LANES_XOR3(x, SABLE_BEFORE_##column, d##column), n)
#define SABLE_CHI(x, y, z) _mm_ternarylogic_epi64(x, y, z, 0xD2)

SABLE_TARGET_AVX512 static void keccak_blocks_wide(uint64_t *state, const uint8_t *data, int64_t count, int64_t rate) {
    __m128i a[25];
    for (int i = 0; i < 25; i++) {
        a[i] = SABLE_LANE(state + i);
    }

    for (; count >= rate; count -= rate, data += rate) {
        for (int i = 0; i < rate / 8; i++) {
            a[i] = _mm_xor_si128(a[i], SABLE_LANE(data + 8 * i));
        }

        SABLE_KECCAK_ROUNDS(__m128i, _mm_xor_si128, SABLE_LANES_XOR3, SABLE_THETA, SABLE_MOVED, SABLE_CHI, SABLE_LANE)
    }

    for (int i = 0; i < 25; i++) {
        _mm_storel_epi64((__m128i *)(state + i), a[i]);
    }
}

#endif

// ---- Choosing ------------------------------------------------------------------

#if defined(SABLE_CRYPTO_ARM64) || defined(SABLE_CRYPTO_X64)

// What the processor has kernels for, found once and kept. Two threads that
// look at once find the same. SABLE_CRYPTO_PORTABLE=1 says it has none, so
// that the tests can run the portable code where the kernels would;
// SABLE_CRYPTO_NARROW=1 that it has no AVX-512, so that they can run AVX2's,
// and SABLE_CRYPTO_NARROW=2 that the ciphers have no AVX2 either.
static int crypto_available(void) {
    static int known;
    int features = __atomic_load_n(&known, __ATOMIC_RELAXED);
    if (features == 0) {
        const char *portable = getenv("SABLE_CRYPTO_PORTABLE");
        const char *narrow = getenv("SABLE_CRYPTO_NARROW");
        features = CRYPTO_ASKED | (portable != NULL && strcmp(portable, "1") == 0 ? 0 : crypto_features());
        if (narrow != NULL && (strcmp(narrow, "1") == 0 || strcmp(narrow, "2") == 0)) {
            features &= ~(CRYPTO_WIDE | CRYPTO_VAES_WIDE | (narrow[0] == '2' ? CRYPTO_VAES | CRYPTO_AVX2 : 0));
        }

        __atomic_store_n(&known, features, __ATOMIC_RELAXED);
    }

    return features;
}

#endif

// 'count' bytes, a whole number of blocks, worked into the state at
// 'state'; false, with nothing done, when there is no kernel for it here.
static bool crypto_blocks(int64_t kind, void *state, const uint8_t *data, int64_t count) {
#if defined(SABLE_CRYPTO_ARM64) || defined(SABLE_CRYPTO_X64)
    int available = crypto_available();
    switch (kind & 255) {
        case SABLE_CRYPTO_SHA1:
            if (available & CRYPTO_SHA1) {
                sha1_blocks(state, data, count);
                return true;
            }
            break;
        case SABLE_CRYPTO_SHA256:
            if (available & CRYPTO_SHA256) {
                sha256_blocks(state, data, count);
                return true;
            }
            break;
        case SABLE_CRYPTO_SHA512:
#if defined(SABLE_CRYPTO_X64)
            if (available & CRYPTO_WIDE) {
                sha512_blocks_wide(state, data, count);
                return true;
            }
#endif
            if (available & CRYPTO_SHA512) {
                sha512_blocks(state, data, count);
                return true;
            }
            break;
        case SABLE_CRYPTO_KECCAK:
#if defined(SABLE_CRYPTO_X64)
            if (available & CRYPTO_WIDE) {
                keccak_blocks_wide(state, data, count, kind >> 8);
                return true;
            }
#endif
            if (available & CRYPTO_KECCAK) {
                keccak_blocks(state, data, count, kind >> 8);
                return true;
            }
            break;
    }
#else
    (void)kind;
    (void)state;
    (void)data;
    (void)count;
#endif
    return false;
}

// The blocks of a slice of bytes (its buffer, where it starts in it, and how many).
bool sable_crypto_blocks(int64_t kind, void *state, const SableArray *buffer, int64_t start, int64_t count) {
    return crypto_blocks(kind, state, slice_bytes(buffer, start), count);
}

// The blocks at an address.
bool sable_crypto_blocks_raw(int64_t kind, void *state, const void *data, int64_t count) {
    return crypto_blocks(kind, state, data, count);
}

// ---- Zeroing --------------------------------------------------------------------

// The bytes set to zero whether or not anything reads them after: the
// assembly may, for all the compiler (link-time optimisation included)
// knows, so the store to memory about to be freed is kept.
void sable_secure_zero(void *bytes, int64_t count) {
    if (count > 0) {
        memset(bytes, 0, (size_t)count);
        __asm__ __volatile__("" : : "r"(bytes) : "memory");
    }
}
