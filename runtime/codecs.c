// What Sable.Text's Base64 and Hex ask of the runtime: whole blocks of their
// texts, written and read with the processor's byte shuffles and its widest
// registers.
//
// Both are written in Sable, portably, and that code is what runs wherever
// this file has nothing better: on a processor without the instructions, on
// WASI, and in the interpreter. Each kernel here does as many whole blocks as
// its instructions take and says how many bytes of the source that was;
// decoding stops before the first block with a byte that is no digit. What
// is left — the last few bytes, and saying what was wrong and where — is
// the portable code's. A kernel reads only the source's bytes and writes
// only the room it is told of.

enum {
    SABLE_CODEC_BASE64_ENCODE = 1,
    SABLE_CODEC_BASE64_DECODE = 2,
    SABLE_CODEC_HEX_ENCODE = 3,
    SABLE_CODEC_HEX_DECODE = 4,
    // With any of them: Base64's URL-safe alphabet, or Hex's capitals.
    SABLE_CODEC_OTHER_DIGITS = 8,
};

#if defined(__aarch64__) || defined(_M_ARM64)
#define SABLE_CODEC_NEON 1
#include <arm_neon.h>
#elif (defined(__x86_64__) || defined(_M_X64)) && (defined(__clang__) || defined(__GNUC__))
#define SABLE_CODEC_X64 1
#include <cpuid.h>
#include <immintrin.h>
#define SABLE_CODEC_TARGET_SSSE3 __attribute__((target("ssse3")))
#define SABLE_CODEC_TARGET_AVX2 __attribute__((target("avx2")))
#endif

#if defined(SABLE_CODEC_NEON)

// The 64 digits, as four registers a lookup indexes as one.
static uint8x16x4_t base64_digits(bool url) {
    static const uint8_t digits[65] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    uint8x16x4_t table = vld1q_u8_x4(digits);
    if (url) {
        table.val[3] = vsetq_lane_u8('-', table.val[3], 14);
        table.val[3] = vsetq_lane_u8('_', table.val[3], 15);
    }

    return table;
}

static int64_t base64_encode(const uint8_t *from, int64_t count, uint8_t *to, int64_t room, bool url) {
    const uint8x16x4_t digits = base64_digits(url);
    const uint8x16_t six = vdupq_n_u8(0x3F);
    int64_t i = 0;
    for (; count - i >= 48 && room >= 64; i += 48, to += 64, room -= 64) {
        uint8x16x3_t bytes = vld3q_u8(from + i);
        uint8x16x4_t values;
        values.val[0] = vshrq_n_u8(bytes.val[0], 2);
        values.val[1] = vandq_u8(vorrq_u8(vshlq_n_u8(bytes.val[0], 4), vshrq_n_u8(bytes.val[1], 4)), six);
        values.val[2] = vandq_u8(vorrq_u8(vshlq_n_u8(bytes.val[1], 2), vshrq_n_u8(bytes.val[2], 6)), six);
        values.val[3] = vandq_u8(bytes.val[2], six);
        for (int k = 0; k < 4; k++) {
            values.val[k] = vqtbl4q_u8(digits, values.val[k]);
        }

        vst4q_u8(to, values);
    }

    return i;
}

static int64_t base64_decode(const uint8_t *from, int64_t count, uint8_t *to, int64_t room, bool url) {
    // What each ASCII byte is worth, in two halves; 0xFF for no digit.
    uint8_t values[128];
    memset(values, 0xFF, sizeof values);
    for (int k = 0; k < 26; k++) {
        values['A' + k] = (uint8_t)k;
        values['a' + k] = (uint8_t)(26 + k);
    }

    for (int k = 0; k < 10; k++) {
        values['0' + k] = (uint8_t)(52 + k);
    }

    values[url ? '-' : '+'] = 62;
    values[url ? '_' : '/'] = 63;
    const uint8x16x4_t low = vld1q_u8_x4(values), high = vld1q_u8_x4(values + 64);
    const uint8x16_t half = vdupq_n_u8(0x40);
    int64_t i = 0;
    for (; count - i >= 64 && room >= 48; i += 64, to += 48, room -= 48) {
        uint8x16x4_t text = vld4q_u8(from + i);
        uint8x16_t wrong = vdupq_n_u8(0);
        for (int k = 0; k < 4; k++) {
            // A byte past 0x7F is in neither half, and kept as too large.
            uint8x16_t value = vqtbx4q_u8(vqtbl4q_u8(low, text.val[k]), high, veorq_u8(text.val[k], half));
            wrong = vorrq_u8(wrong, vorrq_u8(value, vandq_u8(text.val[k], vdupq_n_u8(0x80))));
            text.val[k] = value;
        }

        if (vmaxvq_u8(wrong) >= 64) {
            break;
        }

        uint8x16x3_t bytes;
        bytes.val[0] = vorrq_u8(vshlq_n_u8(text.val[0], 2), vshrq_n_u8(text.val[1], 4));
        bytes.val[1] = vorrq_u8(vshlq_n_u8(text.val[1], 4), vshrq_n_u8(text.val[2], 2));
        bytes.val[2] = vorrq_u8(vshlq_n_u8(text.val[2], 6), text.val[3]);
        vst3q_u8(to, bytes);
    }

    return i;
}

static int64_t hex_encode(const uint8_t *from, int64_t count, uint8_t *to, int64_t room, bool upper) {
    const uint8x16_t digits = vld1q_u8((const uint8_t *)(upper ? "0123456789ABCDEF" : "0123456789abcdef"));
    int64_t i = 0;
    for (; count - i >= 16 && room >= 32; i += 16, to += 32, room -= 32) {
        uint8x16_t bytes = vld1q_u8(from + i);
        uint8x16x2_t halves;
        halves.val[0] = vqtbl1q_u8(digits, vshrq_n_u8(bytes, 4));
        halves.val[1] = vqtbl1q_u8(digits, vandq_u8(bytes, vdupq_n_u8(0x0F)));
        vst2q_u8(to, halves);
    }

    return i;
}

static int64_t hex_decode(const uint8_t *from, int64_t count, uint8_t *to, int64_t room) {
    int64_t i = 0;
    for (; count - i >= 32 && room >= 16; i += 32, to += 16, room -= 16) {
        uint8x16x2_t text = vld2q_u8(from + i);
        uint8x16_t wrong = vdupq_n_u8(0);
        for (int k = 0; k < 2; k++) {
            uint8x16_t decimal = vsubq_u8(text.val[k], vdupq_n_u8('0'));
            uint8x16_t letter = vsubq_u8(vorrq_u8(text.val[k], vdupq_n_u8(0x20)), vdupq_n_u8('a'));
            uint8x16_t isDecimal = vcltq_u8(decimal, vdupq_n_u8(10));
            uint8x16_t isLetter = vcltq_u8(letter, vdupq_n_u8(6));
            wrong = vorrq_u8(wrong, vmvnq_u8(vorrq_u8(isDecimal, isLetter)));
            text.val[k] = vbslq_u8(isDecimal, decimal, vaddq_u8(letter, vdupq_n_u8(10)));
        }

        if (vmaxvq_u8(wrong) != 0) {
            break;
        }

        vst1q_u8(to, vorrq_u8(vshlq_n_u8(text.val[0], 4), text.val[1]));
    }

    return i;
}

#elif defined(SABLE_CODEC_X64)

// 1 for SSSE3, 2 for AVX2 too, its registers kept by the system.
static int codec_features(void) {
    unsigned a, b, c, d;
    if (!__get_cpuid(1, &a, &b, &c, &d) || !(c >> 9 & 1)) {
        return 0;
    }

    if (!(c >> 27 & 1) || !(c >> 28 & 1)) {
        return 1;
    }

    unsigned low, high;
    __asm__ __volatile__("xgetbv" : "=a"(low), "=d"(high) : "c"(0));
    (void)high;
    return (low & 6) == 6 && __get_cpuid_count(7, 0, &a, &b, &c, &d) && (b >> 5 & 1) ? 2 : 1;
}

// Each three bytes of twelve made four six-bit values, a byte each: the
// bytes put where a multiplication can move each value's bits to its place.
// (Muła and Lemire, "Faster Base64 Encoding and Decoding using AVX2
// Instructions", 2018.) The digit a value is written as is it and what its
// run of digits is ahead of it, found by which run that is.
SABLE_CODEC_TARGET_SSSE3 static int64_t base64_encode_ssse3(const uint8_t *from, int64_t count, uint8_t *to, int64_t room, bool url) {
    const __m128i order = _mm_setr_epi8(1, 0, 2, 1, 4, 3, 5, 4, 7, 6, 8, 7, 10, 9, 11, 10);
    const char ahead62 = (char)(url ? '-' - 62 : '+' - 62), ahead63 = (char)(url ? '_' - 63 : '/' - 63);
    const __m128i ahead = _mm_setr_epi8('a' - 26, '0' - 52, '0' - 52, '0' - 52, '0' - 52, '0' - 52, '0' - 52, '0' - 52,
                                        '0' - 52, '0' - 52, '0' - 52, ahead62, ahead63, 'A', 0, 0);
    int64_t i = 0;
    for (; count - i >= 16 && room >= 16; i += 12, to += 16, room -= 16) {
        __m128i bytes = _mm_shuffle_epi8(_mm_loadu_si128((const __m128i *)(from + i)), order);
        __m128i odd = _mm_mulhi_epu16(_mm_and_si128(bytes, _mm_set1_epi32(0x0FC0FC00)), _mm_set1_epi32(0x04000040));
        __m128i even = _mm_mullo_epi16(_mm_and_si128(bytes, _mm_set1_epi32(0x003F03F0)), _mm_set1_epi32(0x01000010));
        __m128i values = _mm_or_si128(odd, even);
        __m128i run = _mm_subs_epu8(values, _mm_set1_epi8(51));
        run = _mm_or_si128(run, _mm_and_si128(_mm_cmpgt_epi8(_mm_set1_epi8(26), values), _mm_set1_epi8(13)));
        _mm_storeu_si128((__m128i *)to, _mm_add_epi8(_mm_shuffle_epi8(ahead, run), values));
    }

    return i;
}

SABLE_CODEC_TARGET_AVX2 static int64_t base64_encode_avx2(const uint8_t *from, int64_t count, uint8_t *to, int64_t room, bool url) {
    const __m256i order = _mm256_broadcastsi128_si256(_mm_setr_epi8(1, 0, 2, 1, 4, 3, 5, 4, 7, 6, 8, 7, 10, 9, 11, 10));
    const char ahead62 = (char)(url ? '-' - 62 : '+' - 62), ahead63 = (char)(url ? '_' - 63 : '/' - 63);
    const __m256i ahead = _mm256_broadcastsi128_si256(
        _mm_setr_epi8('a' - 26, '0' - 52, '0' - 52, '0' - 52, '0' - 52, '0' - 52, '0' - 52, '0' - 52,
                      '0' - 52, '0' - 52, '0' - 52, ahead62, ahead63, 'A', 0, 0));
    int64_t i = 0;
    for (; count - i >= 28 && room >= 32; i += 24, to += 32, room -= 32) {
        __m256i bytes = _mm256_inserti128_si256(_mm256_castsi128_si256(_mm_loadu_si128((const __m128i *)(from + i))),
                                                _mm_loadu_si128((const __m128i *)(from + i + 12)), 1);
        bytes = _mm256_shuffle_epi8(bytes, order);
        __m256i odd = _mm256_mulhi_epu16(_mm256_and_si256(bytes, _mm256_set1_epi32(0x0FC0FC00)), _mm256_set1_epi32(0x04000040));
        __m256i even = _mm256_mullo_epi16(_mm256_and_si256(bytes, _mm256_set1_epi32(0x003F03F0)), _mm256_set1_epi32(0x01000010));
        __m256i values = _mm256_or_si256(odd, even);
        __m256i run = _mm256_subs_epu8(values, _mm256_set1_epi8(51));
        run = _mm256_or_si256(run, _mm256_and_si256(_mm256_cmpgt_epi8(_mm256_set1_epi8(26), values), _mm256_set1_epi8(13)));
        _mm256_storeu_si256((__m256i *)to, _mm256_add_epi8(_mm256_shuffle_epi8(ahead, run), values));
    }

    return i;
}

// A byte's value is it and what its run of digits is behind it, where the
// comparisons with the runs' bounds say which that is; one in no run is no
// digit. (Signed comparisons: a byte past 0x7F is below every run.) Four
// values are then put together as three bytes by two multiply-and-adds.
SABLE_CODEC_TARGET_SSSE3 static int64_t base64_decode_ssse3(const uint8_t *from, int64_t count, uint8_t *to, int64_t room, bool url) {
    const __m128i digit62 = _mm_set1_epi8(url ? '-' : '+'), digit63 = _mm_set1_epi8(url ? '_' : '/');
    const __m128i order = _mm_setr_epi8(2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, -1, -1, -1, -1);
    int64_t i = 0;
    for (; count - i >= 16 && room >= 16; i += 16, to += 12, room -= 12) {
        __m128i text = _mm_loadu_si128((const __m128i *)(from + i));
        __m128i upper = _mm_and_si128(_mm_cmpgt_epi8(text, _mm_set1_epi8('A' - 1)), _mm_cmpgt_epi8(_mm_set1_epi8('Z' + 1), text));
        __m128i lower = _mm_and_si128(_mm_cmpgt_epi8(text, _mm_set1_epi8('a' - 1)), _mm_cmpgt_epi8(_mm_set1_epi8('z' + 1), text));
        __m128i decimal = _mm_and_si128(_mm_cmpgt_epi8(text, _mm_set1_epi8('0' - 1)), _mm_cmpgt_epi8(_mm_set1_epi8('9' + 1), text));
        __m128i is62 = _mm_cmpeq_epi8(text, digit62), is63 = _mm_cmpeq_epi8(text, digit63);
        __m128i behind = _mm_or_si128(_mm_or_si128(_mm_and_si128(upper, _mm_set1_epi8(-'A')), _mm_and_si128(lower, _mm_set1_epi8(26 - 'a'))),
                                      _mm_and_si128(decimal, _mm_set1_epi8(52 - '0')));
        behind = _mm_or_si128(behind, _mm_or_si128(_mm_and_si128(is62, _mm_sub_epi8(_mm_set1_epi8(62), digit62)),
                                                   _mm_and_si128(is63, _mm_sub_epi8(_mm_set1_epi8(63), digit63))));
        __m128i known = _mm_or_si128(_mm_or_si128(_mm_or_si128(upper, lower), decimal), _mm_or_si128(is62, is63));
        if (_mm_movemask_epi8(known) != 0xFFFF) {
            break;
        }

        __m128i values = _mm_add_epi8(text, behind);
        __m128i pairs = _mm_maddubs_epi16(values, _mm_set1_epi32(0x01400140));
        __m128i groups = _mm_madd_epi16(pairs, _mm_set1_epi32(0x00011000));
        _mm_storeu_si128((__m128i *)to, _mm_shuffle_epi8(groups, order));
    }

    return i;
}

SABLE_CODEC_TARGET_AVX2 static int64_t base64_decode_avx2(const uint8_t *from, int64_t count, uint8_t *to, int64_t room, bool url) {
    const __m256i digit62 = _mm256_set1_epi8(url ? '-' : '+'), digit63 = _mm256_set1_epi8(url ? '_' : '/');
    const __m256i order = _mm256_broadcastsi128_si256(_mm_setr_epi8(2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, -1, -1, -1, -1));
    const __m256i together = _mm256_setr_epi32(0, 1, 2, 4, 5, 6, 7, 7);
    int64_t i = 0;
    for (; count - i >= 32 && room >= 32; i += 32, to += 24, room -= 24) {
        __m256i text = _mm256_loadu_si256((const __m256i *)(from + i));
        __m256i upper = _mm256_and_si256(_mm256_cmpgt_epi8(text, _mm256_set1_epi8('A' - 1)), _mm256_cmpgt_epi8(_mm256_set1_epi8('Z' + 1), text));
        __m256i lower = _mm256_and_si256(_mm256_cmpgt_epi8(text, _mm256_set1_epi8('a' - 1)), _mm256_cmpgt_epi8(_mm256_set1_epi8('z' + 1), text));
        __m256i decimal = _mm256_and_si256(_mm256_cmpgt_epi8(text, _mm256_set1_epi8('0' - 1)), _mm256_cmpgt_epi8(_mm256_set1_epi8('9' + 1), text));
        __m256i is62 = _mm256_cmpeq_epi8(text, digit62), is63 = _mm256_cmpeq_epi8(text, digit63);
        __m256i behind = _mm256_or_si256(_mm256_or_si256(_mm256_and_si256(upper, _mm256_set1_epi8(-'A')), _mm256_and_si256(lower, _mm256_set1_epi8(26 - 'a'))),
                                         _mm256_and_si256(decimal, _mm256_set1_epi8(52 - '0')));
        behind = _mm256_or_si256(behind, _mm256_or_si256(_mm256_and_si256(is62, _mm256_sub_epi8(_mm256_set1_epi8(62), digit62)),
                                                         _mm256_and_si256(is63, _mm256_sub_epi8(_mm256_set1_epi8(63), digit63))));
        __m256i known = _mm256_or_si256(_mm256_or_si256(_mm256_or_si256(upper, lower), decimal), _mm256_or_si256(is62, is63));
        if (_mm256_movemask_epi8(known) != -1) {
            break;
        }

        __m256i values = _mm256_add_epi8(text, behind);
        __m256i pairs = _mm256_maddubs_epi16(values, _mm256_set1_epi32(0x01400140));
        __m256i groups = _mm256_madd_epi16(pairs, _mm256_set1_epi32(0x00011000));
        __m256i bytes = _mm256_permutevar8x32_epi32(_mm256_shuffle_epi8(groups, order), together);
        _mm256_storeu_si256((__m256i *)to, bytes);
    }

    return i;
}

SABLE_CODEC_TARGET_SSSE3 static int64_t hex_encode_ssse3(const uint8_t *from, int64_t count, uint8_t *to, int64_t room, bool upper) {
    const __m128i digits = _mm_loadu_si128((const __m128i *)(const void *)(upper ? "0123456789ABCDEF" : "0123456789abcdef"));
    const __m128i four = _mm_set1_epi8(0x0F);
    int64_t i = 0;
    for (; count - i >= 16 && room >= 32; i += 16, to += 32, room -= 32) {
        __m128i bytes = _mm_loadu_si128((const __m128i *)(from + i));
        __m128i high = _mm_shuffle_epi8(digits, _mm_and_si128(_mm_srli_epi16(bytes, 4), four));
        __m128i low = _mm_shuffle_epi8(digits, _mm_and_si128(bytes, four));
        _mm_storeu_si128((__m128i *)to, _mm_unpacklo_epi8(high, low));
        _mm_storeu_si128((__m128i *)(to + 16), _mm_unpackhi_epi8(high, low));
    }

    return i;
}

SABLE_CODEC_TARGET_AVX2 static int64_t hex_encode_avx2(const uint8_t *from, int64_t count, uint8_t *to, int64_t room, bool upper) {
    const __m256i digits = _mm256_broadcastsi128_si256(_mm_loadu_si128((const __m128i *)(const void *)(upper ? "0123456789ABCDEF" : "0123456789abcdef")));
    const __m256i four = _mm256_set1_epi8(0x0F);
    int64_t i = 0;
    for (; count - i >= 32 && room >= 64; i += 32, to += 64, room -= 64) {
        // The halves' middle quarters changed about, as a half is what is
        // put side by side with another.
        __m256i bytes = _mm256_permute4x64_epi64(_mm256_loadu_si256((const __m256i *)(from + i)), 0xD8);
        __m256i high = _mm256_shuffle_epi8(digits, _mm256_and_si256(_mm256_srli_epi16(bytes, 4), four));
        __m256i low = _mm256_shuffle_epi8(digits, _mm256_and_si256(bytes, four));
        _mm256_storeu_si256((__m256i *)to, _mm256_unpacklo_epi8(high, low));
        _mm256_storeu_si256((__m256i *)(to + 32), _mm256_unpackhi_epi8(high, low));
    }

    return i;
}

SABLE_CODEC_TARGET_SSSE3 static int64_t hex_decode_ssse3(const uint8_t *from, int64_t count, uint8_t *to, int64_t room) {
    int64_t i = 0;
    for (; count - i >= 32 && room >= 16; i += 32, to += 16, room -= 16) {
        __m128i pairs[2];
        int known = 0xFFFF;
        for (int k = 0; k < 2; k++) {
            __m128i text = _mm_loadu_si128((const __m128i *)(from + i + 16 * k));
            __m128i folded = _mm_or_si128(text, _mm_set1_epi8(0x20));
            __m128i decimal = _mm_and_si128(_mm_cmpgt_epi8(text, _mm_set1_epi8('0' - 1)), _mm_cmpgt_epi8(_mm_set1_epi8('9' + 1), text));
            __m128i letter = _mm_and_si128(_mm_cmpgt_epi8(folded, _mm_set1_epi8('a' - 1)), _mm_cmpgt_epi8(_mm_set1_epi8('f' + 1), folded));
            known &= _mm_movemask_epi8(_mm_or_si128(decimal, letter));
            __m128i values = _mm_add_epi8(_mm_and_si128(text, _mm_set1_epi8(0x0F)), _mm_and_si128(letter, _mm_set1_epi8(9)));
            pairs[k] = _mm_maddubs_epi16(values, _mm_set1_epi16(0x0110));
        }

        if (known != 0xFFFF) {
            break;
        }

        _mm_storeu_si128((__m128i *)to, _mm_packus_epi16(pairs[0], pairs[1]));
    }

    return i;
}

SABLE_CODEC_TARGET_AVX2 static int64_t hex_decode_avx2(const uint8_t *from, int64_t count, uint8_t *to, int64_t room) {
    int64_t i = 0;
    for (; count - i >= 64 && room >= 32; i += 64, to += 32, room -= 32) {
        __m256i pairs[2];
        int known = -1;
        for (int k = 0; k < 2; k++) {
            __m256i text = _mm256_loadu_si256((const __m256i *)(from + i + 32 * k));
            __m256i folded = _mm256_or_si256(text, _mm256_set1_epi8(0x20));
            __m256i decimal = _mm256_and_si256(_mm256_cmpgt_epi8(text, _mm256_set1_epi8('0' - 1)), _mm256_cmpgt_epi8(_mm256_set1_epi8('9' + 1), text));
            __m256i letter = _mm256_and_si256(_mm256_cmpgt_epi8(folded, _mm256_set1_epi8('a' - 1)), _mm256_cmpgt_epi8(_mm256_set1_epi8('f' + 1), folded));
            known &= _mm256_movemask_epi8(_mm256_or_si256(decimal, letter));
            __m256i values = _mm256_add_epi8(_mm256_and_si256(text, _mm256_set1_epi8(0x0F)), _mm256_and_si256(letter, _mm256_set1_epi8(9)));
            pairs[k] = _mm256_maddubs_epi16(values, _mm256_set1_epi16(0x0110));
        }

        if (known != -1) {
            break;
        }

        __m256i bytes = _mm256_packus_epi16(pairs[0], pairs[1]);
        _mm256_storeu_si256((__m256i *)to, _mm256_permute4x64_epi64(bytes, 0xD8));
    }

    return i;
}

#endif

static int64_t codec_run(int64_t kind, const uint8_t *from, int64_t count, uint8_t *to, int64_t room) {
#if defined(SABLE_CODEC_NEON) || defined(SABLE_CODEC_X64)
    bool other = (kind & SABLE_CODEC_OTHER_DIGITS) != 0;
#if defined(SABLE_CODEC_NEON)
    switch (kind & 7) {
        case SABLE_CODEC_BASE64_ENCODE:
            return base64_encode(from, count, to, room, other);
        case SABLE_CODEC_BASE64_DECODE:
            return base64_decode(from, count, to, room, other);
        case SABLE_CODEC_HEX_ENCODE:
            return hex_encode(from, count, to, room, other);
        case SABLE_CODEC_HEX_DECODE:
            return hex_decode(from, count, to, room);
    }
#else
    static int known = -1;
    int features = __atomic_load_n(&known, __ATOMIC_RELAXED);
    if (features < 0) {
        features = codec_features();
        __atomic_store_n(&known, features, __ATOMIC_RELAXED);
    }

    if (features == 0) {
        return 0;
    }

    bool wide = features > 1;
    switch (kind & 7) {
        case SABLE_CODEC_BASE64_ENCODE:
            return wide ? base64_encode_avx2(from, count, to, room, other) : base64_encode_ssse3(from, count, to, room, other);
        case SABLE_CODEC_BASE64_DECODE:
            return wide ? base64_decode_avx2(from, count, to, room, other) : base64_decode_ssse3(from, count, to, room, other);
        case SABLE_CODEC_HEX_ENCODE:
            return wide ? hex_encode_avx2(from, count, to, room, other) : hex_encode_ssse3(from, count, to, room, other);
        case SABLE_CODEC_HEX_DECODE:
            return wide ? hex_decode_avx2(from, count, to, room) : hex_decode_ssse3(from, count, to, room);
    }
#endif
#endif
    (void)kind;
    (void)from;
    (void)count;
    (void)to;
    (void)room;
    return 0;
}

// The source a slice of bytes, or a range of a string's.
int64_t sable_codec_bytes(int64_t kind, const SableArray *buffer, int64_t start, int64_t count, uint8_t *to, int64_t room) {
    return count <= 0 || room <= 0 ? 0 : codec_run(kind, slice_bytes(buffer, start), count, to, room);
}

int64_t sable_codec_string(int64_t kind, const SableString *text, int64_t start, int64_t end, uint8_t *to, int64_t room) {
    if (start < 0 || end > text->length || start >= end || room <= 0) {
        return 0;
    }

    return codec_run(kind, (const uint8_t *)text->bytes + start, end - start, to, room);
}

// ---- UTF-8 and UTF-16 ----------------------------------------------------
//
// As above: whole runs of one put in the other, as many as there is room
// for, the rest being the portable code's. Sixteen units at a time while
// they are ASCII, and then a scalar at a time.

// The UTF-16 of well-formed UTF-8, with a unit's room for each byte: how many units.
int64_t sable_utf8_to_utf16(const SableArray *buffer, int64_t start, int64_t count, uint16_t *to, int64_t room) {
    if (count <= 0 || room < count) {
        return 0;
    }

    const uint8_t *from = slice_bytes(buffer, start);
    const uint8_t *end = from + count;
    uint16_t *first = to;
    while (from < end) {
        const uint8_t *until = end;
        if (end - from >= 16) {
            sable_bytes16 block = load16((const char *)from);
            if (!any16((sable_mask16)(block & 0x80))) {
                typedef uint16_t units16 __attribute__((vector_size(32)));
                units16 wide = __builtin_convertvector(block, units16);
                memcpy(to, &wide, sizeof wide);
                from += 16;
                to += 16;
                continue;
            }

            until = from + 16;
        }

        // The scalars that start in the block. Each is a branch, and not
        // the length of a run of them: text alternates, in ways a processor
        // foresees, and what it must wait for a length it cannot.
        while (from < until) {
            uint32_t lead = from[0];
            if (lead < 0x80) {
                *to++ = (uint16_t)lead;
                from += 1;
            } else if (lead < 0xE0) {
                *to++ = (uint16_t)((lead & 0x1F) << 6 | (from[1] & 0x3F));
                from += 2;
            } else if (lead < 0xF0) {
                *to++ = (uint16_t)((lead & 0x0F) << 12 | (from[1] & 0x3F) << 6 | (from[2] & 0x3F));
                from += 3;
            } else {
                uint32_t above = ((lead & 0x07) << 18 | (uint32_t)(from[1] & 0x3F) << 12 | (uint32_t)(from[2] & 0x3F) << 6 | (from[3] & 0x3F)) - 0x10000;
                to[0] = (uint16_t)(0xD800 | above >> 10);
                to[1] = (uint16_t)(0xDC00 | (above & 0x3FF));
                from += 4;
                to += 2;
            }
        }
    }

    return to - first;
}

// The UTF-8 of UTF-16, with room for three bytes a unit and sixteen more, as
// far as it is well-formed: how many units were read in the high 32 bits,
// and bytes written in the low, so at most 2^31 / 3 units at a time.
int64_t sable_utf16_to_utf8(const SableArray *buffer, int64_t start, int64_t count, uint8_t *to, int64_t room) {
    if (count <= 0 || count > INT32_MAX / 3 - 16 || room < 3 * count + 16) {
        return 0;
    }

    const uint16_t *from = (const uint16_t *)(const void *)buffer->elements + start;
    const uint16_t *begin = from, *end = from + count;
    uint8_t *first = to;
    while (from < end) {
        const uint16_t *until = end;
        if (end - from >= 8) {
            typedef uint16_t units8 __attribute__((vector_size(16)));
            typedef uint8_t bytes8 __attribute__((vector_size(8)));
            units8 block;
            memcpy(&block, from, sizeof block);
            if (!any16((sable_mask16)(block & 0xFF80))) {
                bytes8 narrow = __builtin_convertvector(block, bytes8);
                memcpy(to, &narrow, sizeof narrow);
                from += 8;
                to += 8;
                continue;
            }

            until = from + 8;
        }

        while (from < until) {
            uint32_t unit = *from;
            if (unit < 0x80) {
                *to++ = (uint8_t)unit;
                from++;
            } else if (unit < 0x800) {
                to[0] = (uint8_t)(0xC0 | unit >> 6);
                to[1] = (uint8_t)(0x80 | (unit & 0x3F));
                to += 2;
                from++;
            } else if (unit - 0xD800 >= 0x800) {
                to[0] = (uint8_t)(0xE0 | unit >> 12);
                to[1] = (uint8_t)(0x80 | (unit >> 6 & 0x3F));
                to[2] = (uint8_t)(0x80 | (unit & 0x3F));
                to += 3;
                from++;
            } else {
                if (unit >= 0xDC00 || end - from < 2 || (from[1] & 0xFC00) != 0xDC00) {
                    return (int64_t)((uint64_t)(from - begin) << 32 | (uint64_t)(to - first));
                }

                uint32_t code = 0x10000 + ((unit - 0xD800) << 10 | (from[1] - 0xDC00u));
                to[0] = (uint8_t)(0xF0 | code >> 18);
                to[1] = (uint8_t)(0x80 | (code >> 12 & 0x3F));
                to[2] = (uint8_t)(0x80 | (code >> 6 & 0x3F));
                to[3] = (uint8_t)(0x80 | (code & 0x3F));
                to += 4;
                from += 2;
            }
        }
    }

    return (int64_t)((uint64_t)(from - begin) << 32 | (uint64_t)(to - first));
}
