// CRC-32 by the processor's own instructions, for Sable.IO.Compression: gzip's
// and zip's is checked of every byte they decompress. The library's portable
// code runs wherever this has none, and in the interpreter, and gives the same.

#if defined(__aarch64__) && !defined(__wasi__)

#if defined(__APPLE__)
// Every one of theirs has the instructions.
static bool checksum_has_crc(void) {
    return true;
}
#elif defined(__linux__)
#include <sys/auxv.h>
static bool checksum_has_crc(void) {
    return (getauxval(AT_HWCAP) & (1 << 7)) != 0;
}
#elif defined(_WIN32)
static bool checksum_has_crc(void) {
    return IsProcessorFeaturePresent(31) != 0;   // PF_ARM_V8_CRC32_INSTRUCTIONS_AVAILABLE
}
#else
static bool checksum_has_crc(void) {
    return false;
}
#endif

#if defined(__clang__)
#define SABLE_TARGET_CRC __attribute__((target("crc")))
#else
#define SABLE_TARGET_CRC __attribute__((target("+crc")))
#endif

#define SABLE_CRC_HERE 1

SABLE_TARGET_CRC static uint32_t crc32_kernel(uint32_t crc, const uint8_t *data, int64_t count) {
    for (; count >= 8; count -= 8, data += 8) {
        uint64_t word;
        memcpy(&word, data, 8);
        __asm__("crc32x %w0, %w0, %x1" : "+r"(crc) : "r"(word));
    }

    for (; count > 0; count--, data++) {
        __asm__("crc32b %w0, %w0, %w1" : "+r"(crc) : "r"((uint32_t)*data));
    }

    return crc;
}

#elif defined(SABLE_X64)

#define SABLE_CRC_HERE 1
#define SABLE_TARGET_CRC __attribute__((target("pclmul,sse4.1")))

static bool checksum_has_crc(void) {
    unsigned int a, b, c, d;
    // PCLMULQDQ and SSE4.1.
    return __get_cpuid(1, &a, &b, &c, &d) && (c >> 1 & 1) && (c >> 19 & 1);
}

// Sixteen bytes' remainder worked into the sixteen 'by' stands for further on
// (Intel's "Fast CRC Computation for Generic Polynomials Using PCLMULQDQ").
SABLE_TARGET_CRC static inline __m128i crc32_fold(__m128i x, __m128i by, __m128i next) {
    return _mm_xor_si128(_mm_xor_si128(_mm_clmulepi64_si128(x, by, 0x00), _mm_clmulepi64_si128(x, by, 0x11)), next);
}

// At least sixty-four bytes, a multiple of sixteen.
SABLE_TARGET_CRC static uint32_t crc32_kernel(uint32_t crc, const uint8_t *data, int64_t count) {
    const __m128i by64 = _mm_set_epi64x(0x01c6e41596, 0x0154442bd4);
    const __m128i by16 = _mm_set_epi64x(0x00ccaa009e, 0x01751997d0);
    const __m128i by8 = _mm_set_epi64x(0, 0x0163cd6124);
    const __m128i polynomial = _mm_set_epi64x(0x01f7011641, 0x01db710641);
    const __m128i low = _mm_set_epi32(0, -1, 0, -1);

#define SABLE_CRC_LOAD(i) _mm_loadu_si128((const __m128i *)(data + 16 * (i)))
    __m128i x0 = _mm_xor_si128(SABLE_CRC_LOAD(0), _mm_cvtsi32_si128((int)crc));
    __m128i x1 = SABLE_CRC_LOAD(1), x2 = SABLE_CRC_LOAD(2), x3 = SABLE_CRC_LOAD(3);
    for (data += 64, count -= 64; count >= 64; data += 64, count -= 64) {
        x0 = crc32_fold(x0, by64, SABLE_CRC_LOAD(0));
        x1 = crc32_fold(x1, by64, SABLE_CRC_LOAD(1));
        x2 = crc32_fold(x2, by64, SABLE_CRC_LOAD(2));
        x3 = crc32_fold(x3, by64, SABLE_CRC_LOAD(3));
    }

    x0 = crc32_fold(x0, by16, x1);
    x0 = crc32_fold(x0, by16, x2);
    x0 = crc32_fold(x0, by16, x3);
    for (; count >= 16; data += 16, count -= 16) {
        x0 = crc32_fold(x0, by16, SABLE_CRC_LOAD(0));
    }
#undef SABLE_CRC_LOAD

    // 128 bits to 64, those to 32, by Barrett's reduction.
    __m128i t = _mm_clmulepi64_si128(x0, by16, 0x10);
    x0 = _mm_xor_si128(_mm_srli_si128(x0, 8), t);
    t = _mm_srli_si128(x0, 4);
    x0 = _mm_xor_si128(_mm_clmulepi64_si128(_mm_and_si128(x0, low), by8, 0x00), t);
    t = _mm_clmulepi64_si128(_mm_and_si128(x0, low), polynomial, 0x10);
    t = _mm_clmulepi64_si128(_mm_and_si128(t, low), polynomial, 0x00);
    return (uint32_t)_mm_extract_epi32(_mm_xor_si128(x0, t), 1);
}

#endif

// The checksum's state after the bytes at an address: after how many of them
// is the result's high half, and the state its low. None where the processor
// has no instructions for it, or SABLE_PORTABLE=1 says so.
uint64_t sable_crc32(uint64_t crc, const void *data, int64_t count) {
#if defined(SABLE_CRC_HERE)
    static int known;
    int has = __atomic_load_n(&known, __ATOMIC_RELAXED);
    if (has == 0) {
        const char *portable = getenv("SABLE_PORTABLE");
        has = portable != NULL && strcmp(portable, "1") == 0 ? 1 : checksum_has_crc() ? 2 : 1;
        __atomic_store_n(&known, has, __ATOMIC_RELAXED);
    }

    count = count > 0x7FFFFFF0 ? 0x7FFFFFF0 : count & ~(int64_t)15;
    if (has == 2 && count >= 64) {
        return (uint64_t)count << 32 | crc32_kernel((uint32_t)crc, data, count);
    }
#endif
    (void)data;
    (void)count;
    return crc;
}
