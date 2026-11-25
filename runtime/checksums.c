// CRCs by carry-less multiplication, for Sable.IO.Hashing's Crc32 and Crc64:
// x86-64's PCLMULQDQ and AArch64's PMULL. The library's portable code runs
// wherever this has neither, and in the interpreter, and gives the same.
//
// Sixty-four bytes at a time are folded into four lanes of sixteen: each lane
// times x to the 512 (mod the polynomial), worked out as its two halves times
// two constants, is added to the lane sixty-four bytes on (Intel's "Fast CRC
// Computation for Generic Polynomials Using PCLMULQDQ"). The four are then
// folded into one, and that over any sixteen bytes more. Those sixteen bytes,
// given to the CRC from a state of zero, give what the whole would have, and
// tables of eight bytes at a time finish them. One kernel does every
// polynomial: a reflected CRC's bytes are a little-endian number, the other's
// a big-endian one.
//
// tools/conformance/hashing/constants.py works the constants out and checks
// the folding against a CRC computed a bit at a time.

// What 'kind' names, as Sable.IO.Hashing numbers them.
enum {
    CRC_32 = 0,          // IEEE 802.3's, reflected
    CRC_32C = 1,         // Castagnoli's, reflected
    CRC_64_NVME = 2,     // NVM Express's, reflected
    CRC_64_ECMA = 3,     // ECMA-182's, not reflected
};

// For each kind, what a lane's low half and its high half are multiplied by to
// fold it sixty-four bytes on, then sixteen: x^575, x^511, x^191 and x^127 mod
// the polynomial, bit-reversed into 64 bits, for a reflected CRC, whose first
// bytes are the low half; x^512, x^576, x^128 and x^192, aligned to the top,
// for ECMA-182's, whose first bytes are the high half.
static const uint64_t crc_constants[4][4] = {
    { 0x653d982200000000, 0xcad38e8f00000000, 0x65673b4600000000, 0x9ba54c6f00000000 },
    { 0x1c19243b00000000, 0x75bba45b00000000, 0x3743f7bd00000000, 0x3171d43000000000 },
    { 0x0c32cdb31e18a84a, 0x62242240ace5045a, 0xeadc41fd2ba3d420, 0x21e9761e252621ac },
    { 0x5f6843ca540df020, 0xddf4b6981205b83f, 0x05f5c3c7eb52fab6, 0x4eb938a7d257740e },
};

#if defined(__aarch64__) && !defined(__wasi__)

#include <arm_acle.h>
#include <arm_neon.h>

#if defined(__APPLE__)
// Every one of theirs has it.
static bool checksum_has_pmull(void) {
    return true;
}
#elif defined(__linux__)
#include <sys/auxv.h>
static bool checksum_has_pmull(void) {
    // HWCAP_PMULL and HWCAP_CRC32.
    return (getauxval(AT_HWCAP) & (1 << 4)) != 0 && (getauxval(AT_HWCAP) & (1 << 7)) != 0;
}
#elif defined(_WIN32)
static bool checksum_has_pmull(void) {
    // PF_ARM_V8_CRYPTO_INSTRUCTIONS_AVAILABLE and PF_ARM_V8_CRC32_INSTRUCTIONS_AVAILABLE.
    return IsProcessorFeaturePresent(30) != 0 && IsProcessorFeaturePresent(31) != 0;
}
#else
static bool checksum_has_pmull(void) {
    return false;
}
#endif

#if defined(__clang__)
#define SABLE_TARGET_PMULL __attribute__((target("aes,crc")))
#else
#define SABLE_TARGET_PMULL __attribute__((target("+crypto+crc")))
#endif

#define SABLE_CRC_HERE 1

typedef uint64x2_t crc_lane;

// The lane times the constants' powers of x, added to the next: the low half
// by the first constant, the high by the second.
SABLE_TARGET_PMULL static inline crc_lane crc_fold(crc_lane x, poly64x2_t by, crc_lane next) {
    poly128_t low = vmull_p64((poly64_t)vgetq_lane_u64(x, 0), vgetq_lane_p64(by, 0));
    poly128_t high = vmull_high_p64(vreinterpretq_p64_u64(x), by);
    return veorq_u64(veorq_u64(vreinterpretq_u64_p128(low), vreinterpretq_u64_p128(high)), next);
}

SABLE_TARGET_PMULL static inline crc_lane crc_load(const uint8_t *data, bool reflected) {
    uint8x16_t bytes = vld1q_u8(data);
    if (!reflected) {
        bytes = vrev64q_u8(bytes);
        bytes = vextq_u8(bytes, bytes, 8);
    }
    return vreinterpretq_u64_u8(bytes);
}

SABLE_TARGET_PMULL static inline void crc_store(uint8_t *out, crc_lane x, bool reflected) {
    uint8x16_t bytes = vreinterpretq_u8_u64(x);
    if (!reflected) {
        bytes = vrev64q_u8(bytes);
        bytes = vextq_u8(bytes, bytes, 8);
    }
    vst1q_u8(out, bytes);
}

SABLE_TARGET_PMULL static inline poly64x2_t crc_pair(uint64_t low, uint64_t high) {
    return vcombine_p64(vcreate_p64(low), vcreate_p64(high));
}

SABLE_TARGET_PMULL static inline crc_lane crc_state(uint64_t state, bool reflected) {
    return reflected ? vcombine_u64(vcreate_u64(state), vcreate_u64(0)) : vcombine_u64(vcreate_u64(0), vcreate_u64(state));
}

#define crc_xor veorq_u64

// CRC-32 and CRC-32C have instructions of their own, eight bytes at a time.
SABLE_TARGET_PMULL static inline bool crc_by_instruction(int64_t kind, uint64_t *state, const uint8_t *data, int64_t count) {
    if (kind != CRC_32 && kind != CRC_32C) {
        return false;
    }

    uint32_t crc = (uint32_t)*state;
    uint64_t word;
    if (kind == CRC_32) {
        for (; count >= 8; count -= 8, data += 8) {
            memcpy(&word, data, 8);
            crc = __crc32d(crc, word);
        }
        for (; count > 0; count--, data++) {
            crc = __crc32b(crc, *data);
        }
    } else {
        for (; count >= 8; count -= 8, data += 8) {
            memcpy(&word, data, 8);
            crc = __crc32cd(crc, word);
        }
        for (; count > 0; count--, data++) {
            crc = __crc32cb(crc, *data);
        }
    }

    *state = crc;
    return true;
}

#elif defined(SABLE_X64)

#define SABLE_CRC_HERE 1
#define SABLE_TARGET_PMULL __attribute__((target("pclmul,ssse3,sse4.2")))

static bool checksum_has_pmull(void) {
    unsigned int a, b, c, d;
    // PCLMULQDQ, SSSE3 and SSE4.2.
    return __get_cpuid(1, &a, &b, &c, &d) && (c >> 1 & 1) && (c >> 9 & 1) && (c >> 20 & 1);
}

typedef __m128i crc_lane;
typedef __m128i poly64x2_t;

SABLE_TARGET_PMULL static inline crc_lane crc_fold(crc_lane x, __m128i by, crc_lane next) {
    return _mm_xor_si128(_mm_xor_si128(_mm_clmulepi64_si128(x, by, 0x00), _mm_clmulepi64_si128(x, by, 0x11)), next);
}

SABLE_TARGET_PMULL static inline crc_lane crc_load(const uint8_t *data, bool reflected) {
    __m128i bytes = _mm_loadu_si128((const __m128i *)data);
    return reflected ? bytes : _mm_shuffle_epi8(bytes, _mm_set_epi8(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15));
}

SABLE_TARGET_PMULL static inline void crc_store(uint8_t *out, crc_lane x, bool reflected) {
    _mm_storeu_si128((__m128i *)out, reflected ? x : _mm_shuffle_epi8(x, _mm_set_epi8(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15)));
}

SABLE_TARGET_PMULL static inline __m128i crc_pair(uint64_t low, uint64_t high) {
    return _mm_set_epi64x((long long)high, (long long)low);
}

SABLE_TARGET_PMULL static inline crc_lane crc_state(uint64_t state, bool reflected) {
    return reflected ? _mm_set_epi64x(0, (long long)state) : _mm_set_epi64x((long long)state, 0);
}

#define crc_xor _mm_xor_si128

// CRC-32C has an instruction of its own, eight bytes at a time (SSE4.2's).
SABLE_TARGET_PMULL static inline bool crc_by_instruction(int64_t kind, uint64_t *state, const uint8_t *data, int64_t count) {
    if (kind != CRC_32C) {
        return false;
    }

    uint64_t crc = (uint32_t)*state;
    uint64_t word;
    for (; count >= 8; count -= 8, data += 8) {
        memcpy(&word, data, 8);
        crc = _mm_crc32_u64(crc, word);
    }
    for (; count > 0; count--, data++) {
        crc = _mm_crc32_u8((uint32_t)crc, *data);
    }

    *state = crc;
    return true;
}

#endif

#if defined(SABLE_CRC_HERE)

// Each kind's tables for eight bytes at a time, made when it is first folded:
// table[k][i] is what byte i does to the CRC with k bytes after it.
static uint64_t crc_tables[4][8][256];
static int crc_tables_made[4];

static void crc_make_tables(int64_t kind) {
    static const uint64_t polynomials[4] = { 0xEDB88320, 0x82F63B78, 0x9A6C9329AC4BC9B5, 0x42F0E1EBA9EA3693 };
    uint64_t polynomial = polynomials[kind];
    uint64_t (*table)[256] = crc_tables[kind];
    for (int i = 0; i < 256; i++) {
        uint64_t crc;
        if (kind == CRC_64_ECMA) {
            crc = (uint64_t)i << 56;
            for (int bit = 0; bit < 8; bit++) {
                crc = crc >> 63 ? crc << 1 ^ polynomial : crc << 1;
            }
        } else {
            crc = (uint64_t)i;
            for (int bit = 0; bit < 8; bit++) {
                crc = crc & 1 ? crc >> 1 ^ polynomial : crc >> 1;
            }
        }
        table[0][i] = crc;
    }

    for (int k = 1; k < 8; k++) {
        for (int i = 0; i < 256; i++) {
            uint64_t before = table[k - 1][i];
            table[k][i] = kind == CRC_64_ECMA ? before << 8 ^ table[0][before >> 56] : before >> 8 ^ table[0][before & 0xFF];
        }
    }
}

// The state after the bytes at an address, by the instructions where the kind
// has them and eight bytes a step by tables where it has not.
SABLE_TARGET_PMULL static uint64_t crc_tail(int64_t kind, uint64_t state, const uint8_t *data, int64_t count) {
    if (crc_by_instruction(kind, &state, data, count)) {
        return state;
    }

    if (!__atomic_load_n(&crc_tables_made[kind], __ATOMIC_ACQUIRE)) {
        // Two threads that make them at once make the same.
        crc_make_tables(kind);
        __atomic_store_n(&crc_tables_made[kind], 1, __ATOMIC_RELEASE);
    }

    uint64_t (*t)[256] = crc_tables[kind];
    uint64_t crc = state;
    uint64_t word;
    if (kind == CRC_64_ECMA) {
        for (; count >= 8; count -= 8, data += 8) {
            memcpy(&word, data, 8);
            crc ^= __builtin_bswap64(word);
            crc = t[7][crc >> 56] ^ t[6][crc >> 48 & 0xFF] ^ t[5][crc >> 40 & 0xFF] ^ t[4][crc >> 32 & 0xFF]
                ^ t[3][crc >> 24 & 0xFF] ^ t[2][crc >> 16 & 0xFF] ^ t[1][crc >> 8 & 0xFF] ^ t[0][crc & 0xFF];
        }
        for (; count > 0; count--, data++) {
            crc = crc << 8 ^ t[0][(crc >> 56 ^ *data) & 0xFF];
        }
    } else {
        for (; count >= 8; count -= 8, data += 8) {
            memcpy(&word, data, 8);
            crc ^= word;
            crc = t[7][crc & 0xFF] ^ t[6][crc >> 8 & 0xFF] ^ t[5][crc >> 16 & 0xFF] ^ t[4][crc >> 24 & 0xFF]
                ^ t[3][crc >> 32 & 0xFF] ^ t[2][crc >> 40 & 0xFF] ^ t[1][crc >> 48 & 0xFF] ^ t[0][crc >> 56];
        }
        for (; count > 0; count--, data++) {
            crc = crc >> 8 ^ t[0][(crc ^ *data) & 0xFF];
        }
    }

    return crc;
}

// At least sixty-four bytes, a multiple of sixteen, folded to sixteen at 'out'.
SABLE_TARGET_PMULL static void crc_fold_all(int64_t kind, uint64_t state, const uint8_t *data, int64_t count, uint8_t *out) {
    const uint64_t *k = crc_constants[kind];
    bool reflected = kind != CRC_64_ECMA;
    poly64x2_t by64 = crc_pair(k[0], k[1]);
    poly64x2_t by16 = crc_pair(k[2], k[3]);

    crc_lane x0 = crc_xor(crc_load(data, reflected), crc_state(state, reflected));
    crc_lane x1 = crc_load(data + 16, reflected);
    crc_lane x2 = crc_load(data + 32, reflected);
    crc_lane x3 = crc_load(data + 48, reflected);
    for (data += 64, count -= 64; count >= 64; data += 64, count -= 64) {
        x0 = crc_fold(x0, by64, crc_load(data, reflected));
        x1 = crc_fold(x1, by64, crc_load(data + 16, reflected));
        x2 = crc_fold(x2, by64, crc_load(data + 32, reflected));
        x3 = crc_fold(x3, by64, crc_load(data + 48, reflected));
    }

    x0 = crc_fold(x0, by16, x1);
    x0 = crc_fold(x0, by16, x2);
    x0 = crc_fold(x0, by16, x3);
    for (; count >= 16; data += 16, count -= 16) {
        x0 = crc_fold(x0, by16, crc_load(data, reflected));
    }

    crc_store(out, x0, reflected);
}

#endif

// Whether sable_crc_update can be called: whether the processor has the
// instructions, and SABLE_PORTABLE=1 does not say to leave them.
bool sable_crc_available(void) {
#if defined(SABLE_CRC_HERE)
    static int known;
    int has = __atomic_load_n(&known, __ATOMIC_RELAXED);
    if (has == 0) {
        const char *portable = getenv("SABLE_PORTABLE");
        has = portable != NULL && strcmp(portable, "1") == 0 ? 1 : checksum_has_pmull() ? 2 : 1;
        __atomic_store_n(&known, has, __ATOMIC_RELAXED);
    }

    return has == 2;
#else
    return false;
#endif
}

// The CRC's state (not yet complemented) after the bytes at an address, where
// sable_crc_available says so: what can be, folded, and what is left, and the
// sixteen bytes the folding leaves, from a state of zero, by instructions or
// tables.
uint64_t sable_crc_update(int64_t kind, uint64_t state, const void *data, int64_t count) {
#if defined(SABLE_CRC_HERE)
    const uint8_t *at = data;
    if (count >= 64) {
        int64_t whole = count & ~(int64_t)15;
        uint8_t rest[16];
        crc_fold_all(kind, state, at, whole, rest);
        state = crc_tail(kind, 0, rest, 16);
        at += whole;
        count -= whole;
    }

    return crc_tail(kind, state, at, count);
#else
    (void)kind;
    (void)data;
    (void)count;
    return state;
#endif
}
