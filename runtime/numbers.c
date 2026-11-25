// Numbers and decimal text: the shortest digits a float is written as, the
// float a decimal names, and an integer's digits. What strings.c makes strings
// of, and prints.

#include "number_tables.h"

// ---- Integers ---------------------------------------------------------------

static const char digit_pairs[201] =
    "00010203040506070809101112131415161718192021222324252627282930313233343536373839404142434445464748495051525354555657585960616263646566676869"
    "707172737475767778798081828384858687888990919293949596979899";

// The digits of a number, two at a time, ending before 'end'; where they start.
static inline char *write_digits(uint64_t value, char *end) {
    while (value >= 100) {
        uint64_t rest = value / 100;
        end -= 2;
        memcpy(end, digit_pairs + 2 * (value - rest * 100), 2);
        value = rest;
    }

    if (value >= 10) {
        end -= 2;
        memcpy(end, digit_pairs + 2 * value, 2);
    } else {
        *--end = (char)('0' + value);
    }

    return end;
}

// A number below 10^19 is as long as its bits say, give or take one digit.
static inline int count_digits(uint64_t value) {
    static const uint64_t powers[] = {
        0, 10, 100, 1000, 10000, 100000, 1000000, 10000000, 100000000, 1000000000, 10000000000, 100000000000,
        1000000000000, 10000000000000, 100000000000000, 1000000000000000, 10000000000000000, 100000000000000000,
        1000000000000000000, 10000000000000000000u,
    };
    int bits = 64 - __builtin_clzll(value | 1);
    int estimate = bits * 1233 >> 12;
    return estimate + (value >= powers[estimate]);
}

// ---- Writing floats ---------------------------------------------------------

// A finite positive float as decimal: digits * 10^exponent.
typedef struct {
    uint64_t digits;
    int32_t exponent;
} Decimal;

// Both floors, for every exponent of ten or two that a double has.
static inline int32_t floor_log2_power_of_ten(int32_t e) {
    return (int32_t)(((int64_t)e * 1741647) >> 19);
}

static inline int32_t floor_log10_power_of_two(int32_t e) {
    return (int32_t)(((int64_t)e * 1262611) >> 22);
}

// The high 64 bits of a 128 by 64-bit product, its last bit set if what is
// below is more than the rounding of the power can account for.
static inline uint64_t round_to_odd(const uint64_t power[2], uint64_t factor) {
    unsigned __int128 low = (unsigned __int128)power[1] * factor;
    unsigned __int128 high = (unsigned __int128)power[0] * factor + (uint64_t)(low >> 64);
    return (uint64_t)(high >> 64) | ((uint64_t)high > 1);
}

// The shortest decimal between the midpoints to a float's neighbours, the
// nearest to the float where there are more, for a float significand * 2^exponent
// that is no power of two, so that its neighbours are equally far: R. Giulietti's
// Schubfach ("The Schubfach way to render doubles", 2020). With k the floor of
// log10 of the float's spacing, one at most of the multiples of 10^(k + 1) is
// between the midpoints, and one at least of 10^k's. The midpoints are too when
// the significand is even, as text at one reads back as the even neighbour.
//
// For a power of two the nearest decimal of the fewest digits can be outside
// the midpoints on the nearer side, where Sable writes one digit more
// rather than a decimal that is not the nearest: see format_f64.
static Decimal shortest_decimal(uint64_t significand, int32_t exponent) {
    uint64_t odd = significand & 1;
    int32_t k = floor_log10_power_of_two(exponent);
    int32_t shift = exponent + floor_log2_power_of_ten(-k) + 1;
    const uint64_t *power = powers_of_ten[-k - POWER_LEAST];

    // Four times the value and its midpoints, over 10^k.
    uint64_t four = significand << 2;
    uint64_t lower = round_to_odd(power, (four - 2) << shift);
    uint64_t value = round_to_odd(power, four << shift);
    uint64_t upper = round_to_odd(power, (four + 2) << shift);
    lower += odd;
    upper -= odd;
    uint64_t s = value >> 2;
    uint64_t ten = s / 10;
    bool ten_down = lower <= 40 * ten, ten_up = 40 * ten + 40 <= upper;
    if (s >= 10 && ten_down != ten_up) {
        Decimal decimal = { ten + ten_up, k + 1 };
        return decimal;
    }

    bool down = lower <= 4 * s, up = 4 * s + 4 <= upper;
    if (down == up) {
        uint64_t middle = 4 * s + 2;
        up = value > middle || (value == middle && (s & 1) != 0);
    }

    Decimal decimal = { s + up, k };
    return decimal;
}

// Whether a finite float other than zero is written as a decimal of its own
// digits: an integer below 2^53, between whose neighbours there is no shorter
// decimal but its own without its trailing zeros, as they are at most one away.
static inline bool small_integer(double magnitude, Decimal *decimal) {
    if (magnitude >= 9007199254740992.0 || (double)(uint64_t)magnitude != magnitude) {
        return false;
    }

    decimal->digits = (uint64_t)magnitude;
    decimal->exponent = 0;
    return true;
}

// A decimal laid out the way Python's repr does: plain decimal with a point
// for exponents -4 through 15 (100.0, 4.0, -0.0, 0.0001), and scientific
// outside them (1e+20, 1.5e-07). The point keeps a whole float from reading as
// an integer. The interpreter formats the same way. How many bytes.
static int layout_float(bool negative, Decimal decimal, char *text) {
    uint64_t n = decimal.digits;
    int32_t exponent = decimal.exponent;
    while (n >= 10 && n % 10 == 0) {
        n /= 10;
        exponent++;
    }

    char *out = text;
    if (negative) {
        *out++ = '-';
    }

    int count = count_digits(n);
    // The exponent of the first digit.
    int first = count - 1 + exponent;
    if (first < -4 || first >= 16) {
        // The digits a place on, and then the first one back over the point.
        write_digits(n, out + 1 + count);
        out[0] = out[1];
        if (count > 1) {
            out[1] = '.';
            out += count + 1;
        } else {
            out++;
        }

        *out++ = 'e';
        *out++ = first < 0 ? '-' : '+';
        int size = first < 0 ? -first : first;
        if (size >= 100) {
            *out++ = (char)('0' + size / 100);
            size %= 100;
        }

        memcpy(out, digit_pairs + 2 * size, 2);
        return (int)(out + 2 - text);
    }

    if (first < 0) {
        memcpy(out, "0.0000", 6);
        out += 1 - first;
        write_digits(n, out + count);
        return (int)(out + count - text);
    }

    if (exponent >= 0) {
        write_digits(n, out + count);
        out += count;
        memset(out, '0', (size_t)exponent);
        out += exponent;
        memcpy(out, ".0", 2);
        return (int)(out + 2 - text);
    }

    // The digits a place on, and then those before the point back over it.
    write_digits(n, out + 1 + count);
    memmove(out, out + 1, (size_t)first + 1);
    out[first + 1] = '.';
    return (int)(out + 1 + count - text);
}

// The decimal of printf's %.Ne text, its sign aside.
static Decimal decimal_of_scientific(const char *text) {
    Decimal decimal = { 0, 0 };
    bool fraction = false;
    for (; *text != 'e' && *text != '\0'; text++) {
        if (*text >= '0' && *text <= '9') {
            decimal.digits = decimal.digits * 10 + (uint64_t)(*text - '0');
            decimal.exponent -= fraction;
        } else if (*text != '-') {
            // The point.
            fraction = true;
        }
    }

    decimal.exponent += *text == 'e' ? atoi(text + 1) : 0;
    return decimal;
}

// NaN, an infinity or a zero, which have no digits to find; 0 for another.
static int format_special(double value, char *text) {
    const char *special;
    if (isnan(value)) {
        special = "nan";
    } else if (value == 0) {
        special = signbit(value) ? "-0.0" : "0.0";
    } else if (isinf(value)) {
        special = value > 0 ? "inf" : "-inf";
    } else {
        return 0;
    }

    size_t length = strlen(special);
    memcpy(text, special, length);
    return (int)length;
}

// What a float's text fits in: a sign, 17 digits, a point and an exponent.
enum { FLOAT_TEXT = 32 };

// A float is written as its shortest digits that read back as it, the nearest
// to it of as many digits: 0.1, not 0.10000000000000001. How many bytes.
static int format_f64(double value, char *text) {
    int special = format_special(value, text);
    if (special != 0) {
        return special;
    }

    uint64_t bits;
    memcpy(&bits, &value, sizeof bits);
    uint64_t fraction = bits & 0xfffffffffffffu;
    int32_t exponent = (int32_t)(bits >> 52 & 0x7ff);
    double magnitude = fabs(value);
    Decimal decimal;
    if (small_integer(magnitude, &decimal)) {
        return layout_float(value < 0, decimal, text);
    }

    // A power of two is nearer to the neighbour below it than to the one above.
    if (fraction == 0 && exponent > 1) {
        char scientific[FLOAT_TEXT];
        for (int precision = 1; precision <= 17; precision++) {
            snprintf(scientific, sizeof scientific, "%.*e", precision - 1, magnitude);
            if (precision == 17 || strtod(scientific, NULL) == magnitude) {
                break;
            }
        }

        return layout_float(value < 0, decimal_of_scientific(scientific), text);
    }

    if (exponent != 0) {
        fraction |= 0x10000000000000u;
    } else {
        exponent = 1;
    }

    return layout_float(value < 0, shortest_decimal(fraction, exponent - 1075), text);
}

// F32 the same way, against a float: 1.1, not 1.10000002.
static int format_f32(float value, char *text) {
    int special = format_special(value, text);
    if (special != 0) {
        return special;
    }

    uint32_t bits;
    memcpy(&bits, &value, sizeof bits);
    uint32_t fraction = bits & 0x7fffffu;
    int32_t exponent = (int32_t)(bits >> 23 & 0xff);
    float magnitude = fabsf(value);
    Decimal decimal;
    if (magnitude < 16777216.0f && small_integer(magnitude, &decimal)) {
        return layout_float(value < 0, decimal, text);
    }

    if (fraction == 0 && exponent > 1) {
        char scientific[FLOAT_TEXT];
        for (int precision = 1; precision <= 9; precision++) {
            snprintf(scientific, sizeof scientific, "%.*e", precision - 1, (double)magnitude);
            if (precision == 9 || strtof(scientific, NULL) == magnitude) {
                break;
            }
        }

        return layout_float(value < 0, decimal_of_scientific(scientific), text);
    }

    if (exponent != 0) {
        fraction |= 0x800000u;
    } else {
        exponent = 1;
    }

    return layout_float(value < 0, shortest_decimal(fraction, exponent - 150), text);
}

// ---- Reading floats ---------------------------------------------------------

// What a decimal number's text says: its first 19 significant digits, and the
// power of ten they are times; more digits, if not zeros, make it 'inexact'.
typedef struct {
    uint64_t digits;
    int64_t exponent;
    bool negative;
    bool inexact;
} DecimalText;

// Whether eight bytes are all digits: with 0x30 off it and with 0x46 on it, a
// digit's byte keeps its high bit clear.
static inline bool eight_digits(uint64_t word) {
    return (((word + 0x4646464646464646u) | (word - 0x3030303030303030u)) & 0x8080808080808080u) == 0;
}

// The number eight digits write, the first of them in the lowest byte: pairs
// put together, then pairs of pairs, then those.
static inline uint32_t eight_digits_value(uint64_t word) {
    word -= 0x3030303030303030u;
    word = word * 10 + (word >> 8);
    const uint64_t mask = 0x000000ff000000ffu;
    return (uint32_t)(((word & mask) * 0x000f424000000064u + (word >> 16 & mask) * 0x0000271000000001u) >> 32);
}

// A run of digits after the 'digits' before it, modulo 2^64; where it ends.
static inline const char *scan_digits(const char *p, const char *end, uint64_t *digits) {
    uint64_t value = *digits;
#if __BYTE_ORDER__ == __ORDER_LITTLE_ENDIAN__
    while (end - p >= 8) {
        uint64_t word;
        memcpy(&word, p, sizeof word);
        if (!eight_digits(word)) {
            break;
        }

        value = value * 100000000 + eight_digits_value(word);
        p += 8;
    }
#endif
    while (p < end && *p >= '0' && *p <= '9') {
        value = value * 10 + (uint64_t)(*p++ - '0');
    }

    *digits = value;
    return p;
}

// A decimal number — sign, digits, fraction, exponent — and nothing else: no
// infinities, NaN, hex or surrounding space. Whether that is what the text is.
static bool scan_decimal(const char *p, int64_t length, DecimalText *out) {
    const char *end = p + length;
    out->negative = false;
    if (p < end && (*p == '+' || *p == '-')) {
        out->negative = *p == '-';
        p++;
    }

    uint64_t digits = 0;
    const char *start = p;
    p = scan_digits(p, end, &digits);
    int64_t count = p - start, places = 0;
    if (p < end && *p == '.') {
        const char *point = ++p;
        p = scan_digits(p, end, &digits);
        places = p - point;
        count += places;
    }

    if (count == 0) {
        return false;
    }

    const char *mantissa_end = p;
    int64_t exponent = 0;
    if (p < end && (*p == 'e' || *p == 'E')) {
        p++;
        bool negative = p < end && *p == '-';
        if (p < end && (*p == '+' || *p == '-')) {
            p++;
        }

        if (p == end || *p < '0' || *p > '9') {
            return false;
        }

        for (; p < end && *p >= '0' && *p <= '9'; p++) {
            // Further than the length of any text could bring back.
            if (exponent < INT64_MAX / 20) {
                exponent = exponent * 10 + (*p - '0');
            }
        }

        exponent = negative ? -exponent : exponent;
    }

    if (p != end) {
        return false;
    }

    out->inexact = false;
    out->exponent = exponent - places;
    if (count > 19) {
        // Leading zeros are no digits; past nineteen of the rest, the digits
        // are only counted.
        const char *q = start;
        while (q < mantissa_end && (*q == '0' || *q == '.')) {
            q++;
        }

        digits = 0;
        int taken = 0;
        int64_t dropped = 0;
        for (; q < mantissa_end; q++) {
            if (*q == '.') {
                continue;
            }

            if (taken < 19) {
                digits = digits * 10 + (uint64_t)(*q - '0');
                taken++;
            } else {
                dropped++;
                out->inexact |= *q != '0';
            }
        }

        out->exponent += dropped;
    }

    out->digits = digits;
    return true;
}

// A binary format, as read_decimal needs it.
typedef struct {
    int fraction_bits;
    int bias;
    int infinite;            // the exponent field of an infinity
    int least, most;         // the powers of ten below which 19 digits are zero, and past which one is infinite
    int tie_least, tie_most; // those with which 64 bits can be halfway between two floats
} FloatFormat;

static const FloatFormat f64_format = { 52, 1023, 0x7ff, -342, 308, -4, 23 };
static const FloatFormat f32_format = { 23, 127, 0xff, -64, 38, -17, 10 };

// The bits of the float nearest digits * 10^exponent, ties to even: D. Lemire,
// "Number Parsing at a Gigabyte per Second" (2021), after M. Eisel. The digits,
// shifted up to 64 bits, are multiplied by the first 64 bits of the power of
// ten, and by the next 64 where those leave the float's bits uncertain. False
// where all 128 do.
static inline bool read_decimal(uint64_t digits, int64_t exponent, const FloatFormat *format, uint64_t *out) {
    if (digits == 0 || exponent < format->least) {
        *out = 0;
        return true;
    }

    uint64_t infinity = (uint64_t)format->infinite << format->fraction_bits;
    if (exponent > format->most) {
        *out = infinity;
        return true;
    }

    int32_t power = (int32_t)exponent;
    int zeros = __builtin_clzll(digits);
    digits <<= zeros;

    // The table's powers are rounded up; the proof has them rounded down, but
    // for the exact ones and their reciprocals.
    const uint64_t *entry = powers_of_ten[power - POWER_LEAST];
    uint64_t high_power = entry[0], low_power = entry[1];
    bool rounded = power < -27 || power > 55;
    if (rounded) {
        high_power -= low_power == 0;
        low_power--;
    }

    unsigned __int128 product = (unsigned __int128)digits * high_power;
    uint64_t high = (uint64_t)(product >> 64), low = (uint64_t)product;
    uint64_t mask = UINT64_MAX >> (format->fraction_bits + 3);
    if ((high & mask) == mask) {
        uint64_t more = (uint64_t)(((unsigned __int128)digits * low_power) >> 64);
        low += more;
        high += low < more;
    }

    if (rounded && low == UINT64_MAX) {
        return false;
    }

    int upper = (int)(high >> 63);
    int dropped = upper + 64 - format->fraction_bits - 3;
    uint64_t fraction = high >> dropped;
    int32_t binary = floor_log2_power_of_ten(power) + 63 + upper - zeros + format->bias;
    if (binary <= 0) {
        // Subnormal, or rounded up to the least normal.
        int shift = 1 - binary;
        if (shift >= 64) {
            *out = 0;
            return true;
        }

        fraction >>= shift;
        fraction += fraction & 1;
        *out = fraction >> 1;
        return true;
    }

    // Exactly halfway, nothing being below the bit that says so: to even.
    if (low <= 1 && power >= format->tie_least && power <= format->tie_most && (fraction & 3) == 1 && fraction << dropped == high) {
        fraction &= ~(uint64_t)1;
    }

    fraction += fraction & 1;
    fraction >>= 1;
    if (fraction >= (uint64_t)2 << format->fraction_bits) {
        fraction >>= 1;
        binary++;
    }

    if (binary >= format->infinite) {
        *out = infinity;
        return true;
    }

    *out = (fraction & ~((uint64_t)1 << format->fraction_bits)) | (uint64_t)binary << format->fraction_bits;
    return true;
}

// The same for text whose digits may go on past those: certain where they, and
// one more in the last place, are nearest to one float.
static inline bool read_decimal_text(const DecimalText *text, const FloatFormat *format, uint64_t *out) {
    uint64_t next;
    return read_decimal(text->digits, text->exponent, format, out)
        && (!text->inexact || (read_decimal(text->digits + 1, text->exponent, format, &next) && next == *out));
}

// The text as the C library wants it, for what read_decimal_text cannot tell:
// in the buffer where it fits.
static char *terminated(const char *text, int64_t length, char *buffer, size_t size) {
    char *copy = (size_t)length < size ? buffer : malloc((size_t)length + 1);
    memcpy(copy, text, (size_t)length);
    copy[length] = '\0';
    return copy;
}

// A decimal number that is finite as a double, rounded to the nearest one.
static bool parse_f64(const char *text, int64_t length, double *out) {
    DecimalText decimal;
    if (!scan_decimal(text, length, &decimal)) {
        return false;
    }

    // Both are exact, and one operation rounds once (W. Clinger, 1990).
    static const double powers[] = {
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
        1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
    };
    if (!decimal.inexact && decimal.digits <= (uint64_t)1 << 53 && decimal.exponent >= -22 && decimal.exponent <= 22) {
        double value = (double)decimal.digits;
        value = decimal.exponent < 0 ? value / powers[-decimal.exponent] : value * powers[decimal.exponent];
        *out = decimal.negative ? -value : value;
        return true;
    }

    uint64_t bits;
    double value;
    if (read_decimal_text(&decimal, &f64_format, &bits)) {
        memcpy(&value, &bits, sizeof value);
        value = decimal.negative ? -value : value;
    } else {
        char buffer[256];
        char *copy = terminated(text, length, buffer, sizeof buffer);
        value = strtod(copy, NULL);
        if (copy != buffer) {
            free(copy);
        }
    }

    // Too large for a double does not fit, as for sable_string_to_int.
    if (isinf(value)) {
        return false;
    }

    *out = value;
    return true;
}

// The same for a float, rounded once, from the decimal digits to the nearest
// float: through a double it would be rounded twice, and a decimal just past
// a midpoint between two floats can land on the midpoint as a double and
// then go the wrong way.
static bool parse_f32(const char *text, int64_t length, float *out) {
    DecimalText decimal;
    if (!scan_decimal(text, length, &decimal)) {
        return false;
    }

    static const float powers[] = { 1e0f, 1e1f, 1e2f, 1e3f, 1e4f, 1e5f, 1e6f, 1e7f, 1e8f, 1e9f, 1e10f };
    if (!decimal.inexact && decimal.digits <= (uint64_t)1 << 24 && decimal.exponent >= -10 && decimal.exponent <= 10) {
        float value = (float)decimal.digits;
        value = decimal.exponent < 0 ? value / powers[-decimal.exponent] : value * powers[decimal.exponent];
        *out = decimal.negative ? -value : value;
        return true;
    }

    uint64_t bits;
    float value;
    if (read_decimal_text(&decimal, &f32_format, &bits)) {
        uint32_t half = (uint32_t)bits;
        memcpy(&value, &half, sizeof value);
        value = decimal.negative ? -value : value;
    } else {
        char buffer[256];
        char *copy = terminated(text, length, buffer, sizeof buffer);
        value = strtof(copy, NULL);
        if (copy != buffer) {
            free(copy);
        }
    }

    if (isinf(value)) {
        return false;
    }

    *out = value;
    return true;
}
