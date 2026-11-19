// The Sable runtime: strings — UTF-8 text, its scalars, and text built from values
// (and joined, in arrays.c, which it needs).
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Strings -----------------------------------------------------------

// UTF-8 bytes, their number, and the number of Unicode scalars they hold —
// equal for ASCII, which makes indexing by scalar a matter of indexing bytes.
typedef struct SableString {
    SableObject header;
    int64_t length;
    int64_t count;
    char bytes[];
} SableString;

const SableMetadata sable_string_metadata = { NULL, "String", 0, NULL, NULL, 0, _Alignof(SableObject), NULL, NULL };

// Sixteen bytes as one value, with clang's portable vector types: each
// machine compiles them to its own SIMD (SSE2, NEON). See Searching, below.
typedef uint8_t sable_bytes16 __attribute__((vector_size(16)));
typedef int8_t sable_mask16 __attribute__((vector_size(16)));

static inline sable_bytes16 load16(const char *p) {
    sable_bytes16 v;
    memcpy(&v, p, sizeof v);
    return v;
}

static inline bool any16(sable_mask16 mask) {
    uint64_t words[2];
    memcpy(words, &mask, sizeof words);
    return (words[0] | words[1]) != 0;
}

static int64_t count_scalars(const char *bytes, int64_t length) {
    int64_t count = 0;
    for (int64_t i = 0; i < length; i++) {
        if (((unsigned char)bytes[i] & 0xC0) != 0x80) {
            count++;
        }
    }

    return count;
}

// A string of 'length' bytes still to be written, holding 'count' scalars:
// the bytes are not zeroed first, as every caller writes them all.
static SableString *new_string(int64_t length, int64_t count) {
    SableString *string = alloc_object(&sable_string_metadata, (int64_t)sizeof(SableString) + length, false);
    string->length = length;
    string->count = count;
    return string;
}

// A string of bytes whose scalars are known to number 'count'.
static SableString *make_counted(const char *bytes, int64_t length, int64_t count) {
    SableString *string = new_string(length, count);
    memcpy(string->bytes, bytes, (size_t)length);
    return string;
}

static SableString *make_string(const char *bytes, int64_t length) {
    return make_counted(bytes, length, count_scalars(bytes, length));
}

static SableString *make_cstring(const char *text) {
    return make_string(text, (int64_t)strlen(text));
}

// The length of the well-formed UTF-8 sequence starting at bytes[i], or 0 if
// none does: no overlong forms, no surrogates, nothing past U+10FFFF.
static int utf8_sequence(const uint8_t *bytes, int64_t i, int64_t length) {
    uint8_t b = bytes[i];
    int need = b < 0x80 ? 0 : b >= 0xC2 && b <= 0xDF ? 1 : b >= 0xE0 && b <= 0xEF ? 2 : b >= 0xF0 && b <= 0xF4 ? 3 : -1;
    if (need < 0 || i + need >= length) {
        return 0;
    }

    for (int k = 1; k <= need; k++) {
        uint8_t c = bytes[i + k];
        uint8_t low = 0x80, high = 0xBF;
        if (k == 1 && b == 0xE0) low = 0xA0;
        if (k == 1 && b == 0xED) high = 0x9F;
        if (k == 1 && b == 0xF0) low = 0x90;
        if (k == 1 && b == 0xF4) high = 0x8F;
        if (c < low || c > high) {
            return 0;
        }
    }

    return need + 1;
}

// Bytes from outside the program — a file, a line, an argument, the
// environment — as a string. A String holds well-formed UTF-8, which its
// scalar count and indexing rely on, so each byte of an ill-formed sequence
// becomes U+FFFD, as inline layout's text does. Well-formed input, the usual
// case, is copied as it is.
static SableString *make_text(const char *text, int64_t length) {
    const uint8_t *bytes = (const uint8_t *)text;
    int64_t count = 0, size = 0;
    for (int64_t i = 0; i < length;) {
        // Sixteen ASCII bytes at once, the usual run: each its own scalar.
        if (i + 16 <= length && !any16((sable_mask16)(load16(text + i) & (uint8_t)0x80))) {
            i += 16;
            count += 16;
            size += 16;
            continue;
        }

        int width = utf8_sequence(bytes, i, length);
        size += width == 0 ? 3 : width;
        i += width == 0 ? 1 : width;
        count++;
    }

    if (size == length) {
        return make_counted(text, length, count);
    }

    SableString *string = new_string(size, count);
    char *out = string->bytes;
    for (int64_t i = 0; i < length;) {
        int width = utf8_sequence(bytes, i, length);
        if (width == 0) {
            memcpy(out, "\xEF\xBF\xBD", 3);
            out += 3;
            i++;
        } else {
            memcpy(out, text + i, (size_t)width);
            out += width;
            i += width;
        }
    }

    return string;
}

static SableString *make_ctext(const char *text) {
    return make_text(text, (int64_t)strlen(text));
}

// ---- Scalar positions
//
// A String is indexed by scalar, and a UTF-8 scalar is one to four bytes, so
// the byte offset of scalar i is found by counting. Counting from the start
// every time makes the usual loop — at(0), at(1), at(2)... over a string
// that is not ASCII — quadratic. So the runtime remembers where it last
// was in a few strings, and finds the next position by stepping from the
// nearest of that, the start and the end. One task runs at a time, so the
// table needs no lock; a string whose memory is given back is forgotten,
// since the memory may become another string.

enum { CURSORS = 8 };

static struct {
    const SableString *string;
    int64_t index;
    int64_t offset;
} cursors[CURSORS];

static unsigned cursor_slot(const void *string) {
    // Objects are 8-byte aligned at least: the low bits say nothing.
    return (unsigned)((((uintptr_t)string >> 3) * 0x9E3779B97F4A7C15ull) >> 61);
}

static void forget_string(const SableObject *object) {
    unsigned slot = cursor_slot(object);
    if ((const void *)cursors[slot].string == (const void *)object) {
        cursors[slot].string = NULL;
    }
}

static bool is_continuation(char byte) {
    return ((unsigned char)byte & 0xC0) == 0x80;
}

// The byte offset of scalar 'index', from 0 to the scalar count; the count
// gives the length.
static int64_t offset_of(const SableString *s, int64_t index) {
    if (s->count == s->length) {
        return index;
    }

    if (index >= s->count) {
        return s->length;
    }

    // The nearest known position: the start, the end, or where this string was last read.
    int64_t at = 0, offset = 0;
    if (s->count - index < index) {
        at = s->count;
        offset = s->length;
    }

    unsigned slot = cursor_slot(s);
    if (cursors[slot].string == s) {
        int64_t distance = cursors[slot].index > index ? cursors[slot].index - index : index - cursors[slot].index;
        int64_t best = at > index ? at - index : index - at;
        if (distance < best) {
            at = cursors[slot].index;
            offset = cursors[slot].offset;
        }
    }

    while (at < index) {
        offset++;
        while (is_continuation(s->bytes[offset])) {
            offset++;
        }
        at++;
    }

    while (at > index) {
        offset--;
        while (is_continuation(s->bytes[offset])) {
            offset--;
        }
        at--;
    }

    cursors[slot].string = s;
    cursors[slot].index = index;
    cursors[slot].offset = offset;
    return offset;
}

static int32_t decode(const char *bytes, int64_t *width) {
    const unsigned char *b = (const unsigned char *)bytes;
    if (b[0] < 0x80) {
        *width = 1;
        return b[0];
    }

    if (b[0] < 0xE0) {
        *width = 2;
        return ((b[0] & 0x1F) << 6) | (b[1] & 0x3F);
    }

    if (b[0] < 0xF0) {
        *width = 3;
        return ((b[0] & 0x0F) << 12) | ((b[1] & 0x3F) << 6) | (b[2] & 0x3F);
    }

    *width = 4;
    return ((b[0] & 0x07) << 18) | ((b[1] & 0x3F) << 12) | ((b[2] & 0x3F) << 6) | (b[3] & 0x3F);
}

static int encode(int32_t scalar, char *out) {
    if (scalar < 0x80) {
        out[0] = (char)scalar;
        return 1;
    }

    if (scalar < 0x800) {
        out[0] = (char)(0xC0 | (scalar >> 6));
        out[1] = (char)(0x80 | (scalar & 0x3F));
        return 2;
    }

    if (scalar < 0x10000) {
        out[0] = (char)(0xE0 | (scalar >> 12));
        out[1] = (char)(0x80 | ((scalar >> 6) & 0x3F));
        out[2] = (char)(0x80 | (scalar & 0x3F));
        return 3;
    }

    out[0] = (char)(0xF0 | (scalar >> 18));
    out[1] = (char)(0x80 | ((scalar >> 12) & 0x3F));
    out[2] = (char)(0x80 | ((scalar >> 6) & 0x3F));
    out[3] = (char)(0x80 | (scalar & 0x3F));
    return 4;
}

void sable_print_string(const SableString *s) {
    fwrite(s->bytes, 1, (size_t)s->length, stdout);
    fputc('\n', stdout);
}

// Byte-for-byte equality: strings compare by content, not identity.
bool sable_string_equal(const SableString *a, const SableString *b) {
    return a->length == b->length && memcmp(a->bytes, b->bytes, (size_t)a->length) == 0;
}

// The pieces end to end, in one allocation: an interpolation, or 'a + b + c',
// rather than a string for every '+'.
SableString *sable_string_concat_many(int64_t n, SableString *const *pieces) {
    int64_t length = 0, count = 0;
    for (int64_t i = 0; i < n; i++) {
        length += pieces[i]->length;
        count += pieces[i]->count;
    }

    SableString *s = new_string(length, count);
    char *end = s->bytes;
    for (int64_t i = 0; i < n; i++) {
        memcpy(end, pieces[i]->bytes, (size_t)pieces[i]->length);
        end += pieces[i]->length;
    }

    return s;
}

// A hash of the bytes, eight at a time, never negative: what Sable.Core's
// String.hash() gives. The interpreter computes exactly this
// (SilInterpreter.StringHash), so the two print the same.
int64_t sable_string_hash(const SableString *s) {
    const uint64_t multiplier = 0x9E3779B97F4A7C15ull;
    const unsigned char *bytes = (const unsigned char *)s->bytes;
    uint64_t h = 0x243F6A8885A308D3ull ^ (uint64_t)s->length;
    int64_t i = 0;
    for (; i + 8 <= s->length; i += 8) {
        uint64_t word = 0;
        for (int b = 0; b < 8; b++) {
            word |= (uint64_t)bytes[i + b] << (8 * b);
        }

        h = (h ^ word) * multiplier;
        h ^= h >> 29;
    }

    if (i < s->length) {
        uint64_t tail = 0;
        for (int shift = 0; i < s->length; i++, shift += 8) {
            tail |= (uint64_t)bytes[i] << shift;
        }

        h = (h ^ tail) * multiplier;
        h ^= h >> 29;
    }

    h ^= h >> 32;
    h *= 0xD6E8FEB86659FD93ull;
    h ^= h >> 32;
    return (int64_t)(h & INT64_MAX);
}

// Eight bytes from 'p' as one number whose order is the bytes' order: the
// first byte the most significant.
static inline uint64_t load_ordered(const char *p) {
    uint64_t word;
    memcpy(&word, p, sizeof word);
#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ == __ORDER_LITTLE_ENDIAN__
    word = __builtin_bswap64(word);
#endif
    return word;
}

// -1, 0 or 1 as 'a' orders before, with or after 'b': byte by byte, which
// for UTF-8 is scalar by scalar, a prefix first. Eight bytes are compared at
// a time, as numbers whose order is the bytes' (a sort's keys are mostly
// short and the byte loop's branches mispredict); the last eight end at the
// shorter's end, reaching back over bytes already found equal or, when it is
// shorter than eight, into the 'count' field before the bytes, which the
// shift then drops.
int64_t sable_string_compare(const SableString *a, const SableString *b) {
    int64_t shorter = a->length < b->length ? a->length : b->length;
    if (shorter > 0) {
        int64_t i = 0;
        for (; i + 8 < shorter; i += 8) {
            uint64_t x = load_ordered(a->bytes + i), y = load_ordered(b->bytes + i);
            if (x != y) {
                return x < y ? -1 : 1;
            }
        }

        unsigned drop = (unsigned)(8 - (shorter - i)) * 8;
        uint64_t x = load_ordered(a->bytes + shorter - 8), y = load_ordered(b->bytes + shorter - 8);
        if (drop != 0) {
            x <<= drop;
            y <<= drop;
        }
        if (x != y) {
            return x < y ? -1 : 1;
        }
    }

    return a->length < b->length ? -1 : a->length > b->length ? 1 : 0;
}

// ---- Searching
//
// Sixteen bytes at a time (sable_bytes16, above), a scalar loop after for
// what is left. A UTF-8 needle found byte for byte starts at a
// scalar's first byte, and matches scalar for scalar, so a byte search is a
// scalar search; the interpreter searches by scalar and finds the same place.

// Where the needle first occurs in the haystack, or null. Each block of
// sixteen candidate starts is tested at once on the needle's first and last
// bytes, which rarely both match by chance; only a candidate that passes is
// compared whole (Muła's "generic SIMD" search).
static const char *find_bytes(const char *haystack, int64_t length, const char *needle, int64_t size) {
    if (size > length) {
        return NULL;
    }

    if (size == 1) {
        return memchr(haystack, needle[0], (size_t)length);
    }

    int64_t i = 0;
    sable_bytes16 first = (sable_bytes16){ 0 } + (uint8_t)needle[0];
    sable_bytes16 last = (sable_bytes16){ 0 } + (uint8_t)needle[size - 1];
    for (; i + size - 1 + 16 <= length; i += 16) {
        sable_mask16 both = (sable_mask16)((load16(haystack + i) == first) & (load16(haystack + i + size - 1) == last));
        if (!any16(both)) {
            continue;
        }

        for (int k = 0; k < 16; k++) {
            if (both[k] && memcmp(haystack + i + k + 1, needle + 1, (size_t)(size - 2)) == 0) {
                return haystack + i + k;
            }
        }
    }

    for (; i + size <= length; i++) {
        if (haystack[i] == needle[0] && memcmp(haystack + i, needle, (size_t)size) == 0) {
            return haystack + i;
        }
    }

    return NULL;
}

// Where 'part' next occurs in 'text' at or after scalar 'from' (taken as 0
// when negative): its scalar index, or -1. Sable.Text's find, contains,
// replacing, splitting and occurrences are written over it.
int64_t sable_string_find(const SableString *text, const SableString *part, int64_t from) {
    if (from < 0) {
        from = 0;
    }

    if (from > text->count) {
        return -1;
    }

    if (part->length == 0) {
        return from;
    }

    int64_t start = offset_of(text, from);
    const char *found = find_bytes(text->bytes + start, text->length - start, part->bytes, part->length);
    if (found == NULL) {
        return -1;
    }

    int64_t offset = found - text->bytes;
    return text->count == text->length ? offset : from + count_scalars(text->bytes + start, offset - start);
}

SableString *sable_string_concat(const SableString *a, const SableString *b) {
    SableString *s = new_string(a->length + b->length, a->count + b->count);
    memcpy(s->bytes, a->bytes, (size_t)a->length);
    memcpy(s->bytes + a->length, b->bytes, (size_t)b->length);
    return s;
}

int64_t sable_string_count(const SableString *s) {
    return s->count;
}

int32_t sable_string_at(const SableString *s, int64_t index) {
    if (index < 0 || index >= s->count) {
        trap_here("string index out of range");
    }

    int64_t width;
    return decode(s->bytes + offset_of(s, index), &width);
}

SableString *sable_string_slice(const SableString *s, int64_t from, int64_t to) {
    if (from < 0 || to > s->count || from > to) {
        trap_here("string slice out of range");
    }

    // Of an ASCII string, scalars are bytes: nothing to count.
    if (s->count == s->length) {
        return make_counted(s->bytes + from, to - from, to - from);
    }

    int64_t start = offset_of(s, from);
    int64_t end = offset_of(s, to);
    return make_string(s->bytes + start, end - start);
}

// A cursor over a string's scalars is a byte offset: the next scalar starts
// there, and the one after it past that scalar's bytes. A loop over a string
// is then linear, where indexing by scalar would count from the start.
int64_t sable_string_cursor_end(const SableString *s) {
    return s->length;
}

int32_t sable_string_cursor_scalar(const SableString *s, int64_t cursor) {
    int64_t width;
    return decode(s->bytes + cursor, &width);
}

int64_t sable_string_cursor_next(const SableString *s, int64_t cursor) {
    int64_t width;
    decode(s->bytes + cursor, &width);
    return cursor + width;
}

int64_t sable_string_index_of(const SableString *s, int32_t scalar) {
    int64_t index = 0;
    for (int64_t i = 0; i < s->length; index++) {
        int64_t width;
        if (decode(s->bytes + i, &width) == scalar) {
            return index;
        }

        i += width;
    }

    return -1;
}

bool sable_string_contains(const SableString *s, int32_t scalar) {
    return sable_string_index_of(s, scalar) >= 0;
}

// The whitespace the runtime knows: ASCII's.
static bool is_space(int c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f';
}

SableString *sable_string_trimmed(const SableString *s) {
    int64_t start = 0;
    int64_t end = s->length;
    while (start < end && is_space(s->bytes[start])) {
        start++;
    }

    while (end > start && is_space(s->bytes[end - 1])) {
        end--;
    }

    return make_string(s->bytes + start, end - start);
}

SableString *sable_string_from_char(int32_t scalar) {
    char bytes[4];
    return make_string(bytes, encode(scalar, bytes));
}

// Digits written backwards into a buffer, not through snprintf: an
// interpolated number is common, and the format machinery is most of its cost.
SableString *sable_string_from_int(int64_t value) {
    char text[24];
    char *end = text + sizeof text, *start = end;
    uint64_t magnitude = value < 0 ? 0 - (uint64_t)value : (uint64_t)value;
    do {
        *--start = (char)('0' + magnitude % 10);
        magnitude /= 10;
    } while (magnitude != 0);

    if (value < 0) {
        *--start = '-';
    }

    return make_counted(start, end - start, end - start);
}

// A float is written as its shortest round-tripping digits — 0.1, not
// 0.10000000000000001 — laid out the way Python's repr does: plain decimal
// with a point for exponents -4 through 15 (100.0, 4.0, -0.0, 0.0001), and
// scientific outside them (1e+20, 1.5e-07). The point keeps a whole float
// from reading as an integer. The interpreter formats the same way.
//
// 'scientific' is printf's %.Ne of the value, whose digits are already the
// shortest that round-trip; this only moves the point.
static void layout_float(const char *scientific, char *text, size_t size) {
    const char *p = scientific;
    bool negative = *p == '-';
    if (negative) {
        p++;
    }

    char digits[32];
    int count = 0;
    for (; *p != 'e' && *p != '\0'; p++) {
        if (*p != '.' && count < (int)sizeof digits - 1) {
            digits[count++] = *p;
        }
    }

    int exponent = *p == 'e' ? atoi(p + 1) : 0;

    // %.Ne pads with zeros the shortest digits never need.
    while (count > 1 && digits[count - 1] == '0') {
        count--;
    }

    digits[count] = '\0';

    char *out = text, *limit = text + size - 1;
#define PUT(c) do { if (out < limit) *out++ = (c); } while (0)
    if (negative) {
        PUT('-');
    }

    if (exponent < -4 || exponent >= 16) {
        PUT(digits[0]);
        if (count > 1) {
            PUT('.');
            for (int i = 1; i < count; i++) {
                PUT(digits[i]);
            }
        }

        char tail[8];
        snprintf(tail, sizeof tail, "e%c%02d", exponent < 0 ? '-' : '+', exponent < 0 ? -exponent : exponent);
        for (char *t = tail; *t != '\0'; t++) {
            PUT(*t);
        }
    } else if (exponent < 0) {
        PUT('0');
        PUT('.');
        for (int i = -1; i > exponent; i--) {
            PUT('0');
        }

        for (int i = 0; i < count; i++) {
            PUT(digits[i]);
        }
    } else {
        for (int i = 0; i <= exponent; i++) {
            PUT(i < count ? digits[i] : '0');
        }

        PUT('.');
        if (count > exponent + 1) {
            for (int i = exponent + 1; i < count; i++) {
                PUT(digits[i]);
            }
        } else {
            PUT('0');
        }
    }
#undef PUT

    *out = '\0';
}

static bool format_special(double value, char *text, size_t size) {
    if (isnan(value)) {
        snprintf(text, size, "nan");
        return true;
    }

    if (isinf(value)) {
        snprintf(text, size, value > 0 ? "inf" : "-inf");
        return true;
    }

    return false;
}

static void format_f64(double value, char *text, size_t size) {
    if (format_special(value, text, size)) {
        return;
    }

    char scientific[40];
    for (int precision = 1; precision <= 17; precision++) {
        snprintf(scientific, sizeof scientific, "%.*e", precision - 1, value);
        if (precision == 17 || strtod(scientific, NULL) == value) {
            break;
        }
    }

    layout_float(scientific, text, size);
}

SableString *sable_string_from_f64(double value) {
    char text[40];
    format_f64(value, text, sizeof text);
    return make_cstring(text);
}

// A finite double's digits at a precision, correctly rounded from its exact
// binary value, as printf writes them: 'digits' after the point, in
// scientific form (%.*e, "1.250e+03") or fixed (%.*f, "1250.000"). The
// numeric protocols' formatting is built on these (FloatingPoint+Text.sb);
// a narrower float comes widened, which is exact. The precision is held to
// what a double's exact expansion needs, 1074 places after the point, and
// the buffer is as long as that is.
static SableString *format_f64_at(const char *format, double value, int64_t digits) {
    int precision = digits < 0 ? 0 : digits > 1100 ? 1100 : (int)digits;
    char text[1500];
    snprintf(text, sizeof text, format, precision, value);
    return make_cstring(text);
}

SableString *sable_string_scientific_f64(double value, int64_t digits) {
    return format_f64_at("%.*e", value, digits);
}

SableString *sable_string_fixed_f64(double value, int64_t digits) {
    return format_f64_at("%.*f", value, digits);
}

// F32 the same way, against a float: 1.1, not 1.10000002.
static void format_f32(float value, char *text, size_t size) {
    if (format_special(value, text, size)) {
        return;
    }

    char scientific[40];
    for (int precision = 1; precision <= 9; precision++) {
        snprintf(scientific, sizeof scientific, "%.*e", precision - 1, (double)value);
        if (precision == 9 || strtof(scientific, NULL) == value) {
            break;
        }
    }

    layout_float(scientific, text, size);
}

SableString *sable_string_from_f32(float value) {
    char text[40];
    format_f32(value, text, sizeof text);
    return make_cstring(text);
}

// F16 the same way, against a half: 0.1, not 0.0999755859375. It comes as
// the float it widens to exactly; the digits are the fewest that round back
// to it as a half, and five always do.
static void format_f16(float value, char *text, size_t size) {
    if (format_special(value, text, size)) {
        return;
    }

    char scientific[40];
    for (int precision = 1; precision <= 5; precision++) {
        snprintf(scientific, sizeof scientific, "%.*e", precision - 1, (double)value);
        if (precision == 5 || (float)(_Float16)strtof(scientific, NULL) == value) {
            break;
        }
    }

    layout_float(scientific, text, size);
}

SableString *sable_string_from_f16(float value) {
    char text[40];
    format_f16(value, text, sizeof text);
    return make_cstring(text);
}

// A 128-bit integer arrives as its two halves, low first, and is written in
// decimal from the magnitude up.
static void format_128(uint64_t low, uint64_t high, bool is_signed, char *text, size_t size) {
    unsigned __int128 magnitude = ((unsigned __int128)high << 64) | low;
    bool negative = is_signed && (int64_t)high < 0;
    if (negative) {
        magnitude = 0 - magnitude;
    }

    char digits[48];
    char *end = digits + sizeof digits, *start = end;
    do {
        *--start = (char)('0' + (int)(magnitude % 10));
        magnitude /= 10;
    } while (magnitude != 0);

    if (negative) {
        *--start = '-';
    }

    snprintf(text, size, "%.*s", (int)(end - start), start);
}

SableString *sable_string_from_i128(uint64_t low, uint64_t high) {
    char text[48];
    format_128(low, high, true, text, sizeof text);
    return make_cstring(text);
}

SableString *sable_string_from_u128(uint64_t low, uint64_t high) {
    char text[48];
    format_128(low, high, false, text, sizeof text);
    return make_cstring(text);
}

SableString *sable_string_from_uint(uint64_t value) {
    char text[32];
    snprintf(text, sizeof text, "%llu", (unsigned long long)value);
    return make_cstring(text);
}

SableString *sable_string_from_bool(bool value) {
    return make_cstring(value ? "true" : "false");
}

// An optional sign and decimal digits, and nothing else.
bool sable_string_to_int(const SableString *s, int64_t *out) {
    char text[32];
    int64_t i = s->length > 0 && (s->bytes[0] == '+' || s->bytes[0] == '-') ? 1 : 0;
    if (i >= s->length || s->length >= (int64_t)sizeof text) {
        return false;
    }

    for (int64_t j = i; j < s->length; j++) {
        if (s->bytes[j] < '0' || s->bytes[j] > '9') {
            return false;
        }
    }

    memcpy(text, s->bytes, (size_t)s->length);
    text[s->length] = '\0';
    errno = 0;
    long long value = strtoll(text, NULL, 10);
    if (errno == ERANGE) {
        return false;
    }

    *out = value;
    return true;
}

// A decimal number — sign, digits, fraction, exponent — and nothing else: no
// infinities, NaN, hex or surrounding space, which strtod would also take —
// that is finite as a double.
bool sable_string_to_f64(const SableString *s, double *out) {
    const char *b = s->bytes;
    int64_t n = s->length, i = 0, digits = 0;
    if (i < n && (b[i] == '+' || b[i] == '-')) {
        i++;
    }

    while (i < n && b[i] >= '0' && b[i] <= '9') {
        i++;
        digits++;
    }

    if (i < n && b[i] == '.') {
        i++;
        while (i < n && b[i] >= '0' && b[i] <= '9') {
            i++;
            digits++;
        }
    }

    if (digits == 0) {
        return false;
    }

    if (i < n && (b[i] == 'e' || b[i] == 'E')) {
        i++;
        if (i < n && (b[i] == '+' || b[i] == '-')) {
            i++;
        }

        int64_t exponent = 0;
        while (i < n && b[i] >= '0' && b[i] <= '9') {
            i++;
            exponent++;
        }

        if (exponent == 0) {
            return false;
        }
    }

    if (i != n) {
        return false;
    }

    char *text = malloc((size_t)n + 1);
    memcpy(text, b, (size_t)n);
    text[n] = '\0';
    double value = strtod(text, NULL);
    free(text);

    // Too large for a double does not fit, as for sable_string_to_int.
    if (isinf(value)) {
        return false;
    }

    *out = value;
    return true;
}

int64_t sable_char_to_int(int32_t c) {
    return c;
}

// The scalar for a code point, if it is one: not a surrogate, not past U+10FFFF.
bool sable_char_from_int(int64_t code, int32_t *out) {
    if (code < 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) {
        *out = 0;
        return false;
    }

    *out = (int32_t)code;
    return true;
}

bool sable_char_is_digit(int32_t c) {
    return c >= '0' && c <= '9';
}

bool sable_char_is_letter(int32_t c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}

bool sable_char_is_whitespace(int32_t c) {
    return is_space(c);
}

void sable_print_int(int64_t value) {
    printf("%lld\n", (long long)value);
}

void sable_print_uint(uint64_t value) {
    printf("%llu\n", (unsigned long long)value);
}

void sable_print_i128(uint64_t low, uint64_t high) {
    char text[48];
    format_128(low, high, true, text, sizeof text);
    puts(text);
}

void sable_print_u128(uint64_t low, uint64_t high) {
    char text[48];
    format_128(low, high, false, text, sizeof text);
    puts(text);
}

void sable_print_f16(float value) {
    char text[40];
    format_f16(value, text, sizeof text);
    puts(text);
}

void sable_print_char(int32_t scalar) {
    char bytes[5];
    int length = encode(scalar, bytes);
    bytes[length] = '\n';
    fwrite(bytes, 1, (size_t)length + 1, stdout);
}

void sable_print_f32(float value) {
    char text[40];
    format_f32(value, text, sizeof text);
    puts(text);
}

void sable_print_f64(double value) {
    char text[40];
    format_f64(value, text, sizeof text);
    puts(text);
}

void sable_print_bool(bool value) {
    fputs(value ? "true\n" : "false\n", stdout);
}
