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
#elif defined(__x86_64__) || defined(_M_X64)
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

#endif

// What 'kind' names, in its low eight bits; a sponge's rate, in bytes, is
// in the bits above.
enum {
    SABLE_CRYPTO_SHA256 = 1,   // a U32[8], blocks of 64 bytes
    SABLE_CRYPTO_SHA512 = 2,   // a U64[8], blocks of 128 bytes
    SABLE_CRYPTO_KECCAK = 3,   // a U64[25], blocks of the rate, each absorbed and permuted
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

#endif

#if defined(SABLE_CRYPTO_ARM64)

static const uint64_t sha512_constants[80] __attribute__((aligned(16))) = {
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
    // Every 64-bit ARM Apple has made has SHA-256's.
    return 1 | (apple_feature("hw.optional.arm.FEAT_SHA512") ? 2 : 0) | (apple_feature("hw.optional.arm.FEAT_SHA3") ? 4 : 0);
#elif defined(__linux__)
    // HWCAP_SHA2, HWCAP_SHA512 and HWCAP_SHA3, which older headers lack.
    unsigned long has = getauxval(AT_HWCAP);
    return ((has >> 6 & 1) ? 1 : 0) | ((has >> 21 & 1) ? 2 : 0) | ((has >> 17 & 1) ? 4 : 0);
#elif defined(_WIN32)
    // PF_ARM_V8_CRYPTO_INSTRUCTIONS_AVAILABLE, PF_ARM_SHA512_INSTRUCTIONS_AVAILABLE
    // and PF_ARM_SHA3_INSTRUCTIONS_AVAILABLE, which older headers lack.
    return (IsProcessorFeaturePresent(30) ? 1 : 0) | (IsProcessorFeaturePresent(65) ? 2 : 0) | (IsProcessorFeaturePresent(64) ? 4 : 0);
#else
    return 0;
#endif
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

// A lane to a register's low half; the high halves are not used.
SABLE_TARGET_SHA3 static void keccak_blocks(uint64_t *state, const uint8_t *data, int64_t count, int64_t rate) {
    uint64x2_t a[25];
    for (int i = 0; i < 25; i++) {
        a[i] = vcombine_u64(vld1_u64(state + i), vdup_n_u64(0));
    }

    for (; count >= rate; count -= rate, data += rate) {
        for (int i = 0; i < rate / 8; i++) {
            a[i] = veorq_u64(a[i], vcombine_u64(vreinterpret_u64_u8(vld1_u8(data + 8 * i)), vdup_n_u64(0)));
        }

        for (int round = 0; round < 24; round++) {
            uint64x2_t c0 = veor3q_u64(veor3q_u64(a[0], a[5], a[10]), a[15], a[20]);
            uint64x2_t c1 = veor3q_u64(veor3q_u64(a[1], a[6], a[11]), a[16], a[21]);
            uint64x2_t c2 = veor3q_u64(veor3q_u64(a[2], a[7], a[12]), a[17], a[22]);
            uint64x2_t c3 = veor3q_u64(veor3q_u64(a[3], a[8], a[13]), a[18], a[23]);
            uint64x2_t c4 = veor3q_u64(veor3q_u64(a[4], a[9], a[14]), a[19], a[24]);
            uint64x2_t d0 = vrax1q_u64(c4, c1);
            uint64x2_t d1 = vrax1q_u64(c0, c2);
            uint64x2_t d2 = vrax1q_u64(c1, c3);
            uint64x2_t d3 = vrax1q_u64(c2, c4);
            uint64x2_t d4 = vrax1q_u64(c3, c0);

            // Rho and pi: each lane, with theta's column, rotated left and moved.
            uint64x2_t b0 = veorq_u64(a[0], d0);
            uint64x2_t b1 = vxarq_u64(a[6], d1, 64 - 44);
            uint64x2_t b2 = vxarq_u64(a[12], d2, 64 - 43);
            uint64x2_t b3 = vxarq_u64(a[18], d3, 64 - 21);
            uint64x2_t b4 = vxarq_u64(a[24], d4, 64 - 14);
            uint64x2_t b5 = vxarq_u64(a[3], d3, 64 - 28);
            uint64x2_t b6 = vxarq_u64(a[9], d4, 64 - 20);
            uint64x2_t b7 = vxarq_u64(a[10], d0, 64 - 3);
            uint64x2_t b8 = vxarq_u64(a[16], d1, 64 - 45);
            uint64x2_t b9 = vxarq_u64(a[22], d2, 64 - 61);
            uint64x2_t b10 = vxarq_u64(a[1], d1, 64 - 1);
            uint64x2_t b11 = vxarq_u64(a[7], d2, 64 - 6);
            uint64x2_t b12 = vxarq_u64(a[13], d3, 64 - 25);
            uint64x2_t b13 = vxarq_u64(a[19], d4, 64 - 8);
            uint64x2_t b14 = vxarq_u64(a[20], d0, 64 - 18);
            uint64x2_t b15 = vxarq_u64(a[4], d4, 64 - 27);
            uint64x2_t b16 = vxarq_u64(a[5], d0, 64 - 36);
            uint64x2_t b17 = vxarq_u64(a[11], d1, 64 - 10);
            uint64x2_t b18 = vxarq_u64(a[17], d2, 64 - 15);
            uint64x2_t b19 = vxarq_u64(a[23], d3, 64 - 56);
            uint64x2_t b20 = vxarq_u64(a[2], d2, 64 - 62);
            uint64x2_t b21 = vxarq_u64(a[8], d3, 64 - 55);
            uint64x2_t b22 = vxarq_u64(a[14], d4, 64 - 39);
            uint64x2_t b23 = vxarq_u64(a[15], d0, 64 - 41);
            uint64x2_t b24 = vxarq_u64(a[21], d1, 64 - 2);

            // Chi, and iota into the first lane.
            a[0] = veorq_u64(vbcaxq_u64(b0, b2, b1), vdupq_n_u64(keccak_constants[round]));
            a[1] = vbcaxq_u64(b1, b3, b2);
            a[2] = vbcaxq_u64(b2, b4, b3);
            a[3] = vbcaxq_u64(b3, b0, b4);
            a[4] = vbcaxq_u64(b4, b1, b0);
            a[5] = vbcaxq_u64(b5, b7, b6);
            a[6] = vbcaxq_u64(b6, b8, b7);
            a[7] = vbcaxq_u64(b7, b9, b8);
            a[8] = vbcaxq_u64(b8, b5, b9);
            a[9] = vbcaxq_u64(b9, b6, b5);
            a[10] = vbcaxq_u64(b10, b12, b11);
            a[11] = vbcaxq_u64(b11, b13, b12);
            a[12] = vbcaxq_u64(b12, b14, b13);
            a[13] = vbcaxq_u64(b13, b10, b14);
            a[14] = vbcaxq_u64(b14, b11, b10);
            a[15] = vbcaxq_u64(b15, b17, b16);
            a[16] = vbcaxq_u64(b16, b18, b17);
            a[17] = vbcaxq_u64(b17, b19, b18);
            a[18] = vbcaxq_u64(b18, b15, b19);
            a[19] = vbcaxq_u64(b19, b16, b15);
            a[20] = vbcaxq_u64(b20, b22, b21);
            a[21] = vbcaxq_u64(b21, b23, b22);
            a[22] = vbcaxq_u64(b22, b24, b23);
            a[23] = vbcaxq_u64(b23, b20, b24);
            a[24] = vbcaxq_u64(b24, b21, b20);
        }
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
        return 0;
    }

    bool vectors = (c >> 9 & 1) && (c >> 19 & 1);   // SSSE3 and SSE4.1
    return vectors && __get_cpuid_count(7, 0, &a, &b, &c, &d) && (b >> 29 & 1) ? 1 : 0;
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

#endif

// ---- Choosing ------------------------------------------------------------------

#if defined(SABLE_CRYPTO_ARM64) || defined(SABLE_CRYPTO_X64)

// What the processor has, found once and kept: bit 0 SHA-256, bit 1 SHA-512,
// bit 2 Keccak, bit 7 that it was looked for. Two threads that look at once
// find the same. SABLE_CRYPTO_PORTABLE=1 says it has nothing, so that the
// tests can run the portable code where the kernels would.
static int crypto_available(void) {
    static int known;
    int features = __atomic_load_n(&known, __ATOMIC_RELAXED);
    if (features == 0) {
        const char *portable = getenv("SABLE_CRYPTO_PORTABLE");
        features = 128 | (portable != NULL && strcmp(portable, "1") == 0 ? 0 : crypto_features());
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
        case SABLE_CRYPTO_SHA256:
            if (available & 1) {
                sha256_blocks(state, data, count);
                return true;
            }
            break;
#if defined(SABLE_CRYPTO_ARM64)
        case SABLE_CRYPTO_SHA512:
            if (available & 2) {
                sha512_blocks(state, data, count);
                return true;
            }
            break;
        case SABLE_CRYPTO_KECCAK:
            if (available & 4) {
                keccak_blocks(state, data, count, kind >> 8);
                return true;
            }
            break;
#endif
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
