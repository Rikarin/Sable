// The Sable runtime: the small amount of native code a compiled program
// links against (docs/06 §6.1).
//
// Writing a line, stopping the way the language says a program stops, and the
// heap: counted objects, strings and copy-on-write arrays. Every counted
// object starts with the header of docs/06 §6.2. Counts are not atomic yet:
// there is one thread until the concurrency runtime arrives.

#include <errno.h>
#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- Objects -----------------------------------------------------------

// What a counted object is: how to destroy what it holds, and, for an
// array, how its elements are copied and destroyed. The compiler emits one
// per class and per array element type.
typedef struct SableMetadata {
    void (*destroy)(void *object); // destroys the contents; not the memory
    const char *name;
    int64_t element_size;
    void (*copy_element)(void *destination, const void *source); // null: copy the bits
    void (*destroy_element)(void *element);                       // null: nothing to do
} SableMetadata;

typedef struct SableObject {
    const SableMetadata *type;
    uint32_t strong;
    uint32_t flags;
} SableObject;

enum {
    // Never counted, never freed: string literals live in read-only memory.
    SABLE_IMMORTAL = 1u,
};

_Noreturn void sable_trap(const char *message);

static int64_t live_objects;

// With SABLE_CHECK_LEAKS set, a program that ends with objects still alive
// says how many: tests run every program this way.
__attribute__((destructor)) static void sable_report_leaks(void) {
    if (live_objects != 0 && getenv("SABLE_CHECK_LEAKS") != NULL) {
        fflush(stdout);
        fprintf(stderr, "sable: %lld object(s) leaked\n", (long long)live_objects);
    }
}

// A new object, its count one and the rest of it zero: every field empty.
void *sable_alloc(const SableMetadata *type, int64_t size) {
    SableObject *object = calloc(1, (size_t)size);
    if (object == NULL) {
        sable_trap("out of memory");
    }

    object->type = type;
    object->strong = 1;
    live_objects++;
    return object;
}

void sable_retain(void *pointer) {
    SableObject *object = pointer;
    if (object != NULL && !(object->flags & SABLE_IMMORTAL)) {
        object->strong++;
    }
}

void sable_release(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL || (object->flags & SABLE_IMMORTAL)) {
        return;
    }

    if (--object->strong > 0) {
        return;
    }

    // The deinit, and the destruction of the fields, see the object alive.
    object->strong = 1;
    if (object->type->destroy != NULL) {
        object->type->destroy(object);
    }

    if (object->strong != 1) {
        sable_trap("an object outlived its deinit");
    }

    live_objects--;
    free(object);
}

// ---- Stopping and writing -------------------------------------------

// Overflow, division by zero, an unreachable point reached: not catchable,
// not unwound. Say why, then stop (docs/06 §6.6).
_Noreturn void sable_trap(const char *message) {
    fflush(stdout);
    fprintf(stderr, "fatal error: %s\n", message);
    abort();
}

// ---- Strings -----------------------------------------------------------

// UTF-8 bytes, their number, and the number of Unicode scalars they hold —
// equal for ASCII, which makes indexing by scalar a matter of indexing bytes.
typedef struct SableString {
    SableObject header;
    int64_t length;
    int64_t count;
    char bytes[];
} SableString;

const SableMetadata sable_string_metadata = { NULL, "String", 0, NULL, NULL };

static int64_t count_scalars(const char *bytes, int64_t length) {
    int64_t count = 0;
    for (int64_t i = 0; i < length; i++) {
        if (((unsigned char)bytes[i] & 0xC0) != 0x80) {
            count++;
        }
    }

    return count;
}

static SableString *make_string(const char *bytes, int64_t length) {
    SableString *string = sable_alloc(&sable_string_metadata, (int64_t)sizeof(SableString) + length);
    memcpy(string->bytes, bytes, (size_t)length);
    string->length = length;
    string->count = count_scalars(bytes, length);
    return string;
}

static SableString *make_cstring(const char *text) {
    return make_string(text, (int64_t)strlen(text));
}

// The byte offset of scalar 'index'.
static int64_t offset_of(const SableString *s, int64_t index) {
    if (s->count == s->length) {
        return index;
    }

    int64_t seen = -1;
    for (int64_t i = 0; i < s->length; i++) {
        if (((unsigned char)s->bytes[i] & 0xC0) != 0x80 && ++seen == index) {
            return i;
        }
    }

    return s->length;
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

SableString *sable_string_concat(const SableString *a, const SableString *b) {
    SableString *s = sable_alloc(&sable_string_metadata, (int64_t)sizeof(SableString) + a->length + b->length);
    memcpy(s->bytes, a->bytes, (size_t)a->length);
    memcpy(s->bytes + a->length, b->bytes, (size_t)b->length);
    s->length = a->length + b->length;
    s->count = a->count + b->count;
    return s;
}

int64_t sable_string_count(const SableString *s) {
    return s->count;
}

int32_t sable_string_at(const SableString *s, int64_t index) {
    if (index < 0 || index >= s->count) {
        sable_trap("string index out of range");
    }

    int64_t width;
    return decode(s->bytes + offset_of(s, index), &width);
}

SableString *sable_string_slice(const SableString *s, int64_t from, int64_t to) {
    if (from < 0 || to > s->count || from > to) {
        sable_trap("string slice out of range");
    }

    int64_t start = offset_of(s, from);
    int64_t end = offset_of(s, to);
    return make_string(s->bytes + start, end - start);
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

SableString *sable_string_from_int(int64_t value) {
    char text[32];
    snprintf(text, sizeof text, "%lld", (long long)value);
    return make_cstring(text);
}

// %.17g round-trips every double; the interpreter formats the same way.
static void format_f64(double value, char *text, size_t size) {
    if (isnan(value)) {
        snprintf(text, size, "nan");
    } else {
        snprintf(text, size, "%.17g", value);
    }
}

SableString *sable_string_from_f64(double value) {
    char text[40];
    format_f64(value, text, sizeof text);
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
// infinities, NaN, hex or surrounding space, which strtod would also take.
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
    *out = strtod(text, NULL);
    free(text);
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

void sable_print_f64(double value) {
    char text[40];
    format_f64(value, text, sizeof text);
    puts(text);
}

void sable_print_bool(bool value) {
    fputs(value ? "true\n" : "false\n", stdout);
}

// ---- Arrays ------------------------------------------------------------

// A counted buffer of elements, copied before it is changed while shared.
// The empty array is no buffer: a null pointer.
typedef struct SableArray {
    SableObject header;
    int64_t count;
    int64_t capacity;
    char *elements;
} SableArray;

void sable_array_destroy(void *pointer) {
    SableArray *array = pointer;
    const SableMetadata *type = array->header.type;
    if (type->destroy_element != NULL) {
        for (int64_t i = 0; i < array->count; i++) {
            type->destroy_element(array->elements + i * type->element_size);
        }
    }

    free(array->elements);
}

int64_t sable_array_count(const SableArray *array) {
    return array == NULL ? 0 : array->count;
}

void *sable_array_element(const SableArray *array, int64_t index) {
    if (array == NULL || index < 0 || index >= array->count) {
        sable_trap("array index out of range");
    }

    return array->elements + index * array->header.type->element_size;
}

// The array at '*slot', made the only reference to its buffer, so that it
// can be changed: a fresh one for the empty array, a copy of a shared one.
static SableArray *unique(SableArray **slot, const SableMetadata *type, int64_t room) {
    SableArray *array = *slot;
    if (array != NULL && array->header.strong == 1) {
        return array;
    }

    SableArray *copy = sable_alloc(type, (int64_t)sizeof(SableArray));
    if (array != NULL) {
        int64_t size = type->element_size;
        copy->capacity = array->count + room;
        copy->elements = malloc((size_t)(copy->capacity * size));
        for (int64_t i = 0; i < array->count; i++) {
            if (type->copy_element != NULL) {
                type->copy_element(copy->elements + i * size, array->elements + i * size);
            } else {
                memcpy(copy->elements + i * size, array->elements + i * size, (size_t)size);
            }
        }

        copy->count = array->count;
        sable_release(array);
    }

    *slot = copy;
    return copy;
}

// Room for one more element at the end; the caller stores it.
void *sable_array_append_slot(SableArray **slot, const SableMetadata *type) {
    SableArray *array = unique(slot, type, 1);
    if (array->count == array->capacity) {
        array->capacity = array->capacity < 4 ? 4 : array->capacity * 2;
        array->elements = realloc(array->elements, (size_t)(array->capacity * type->element_size));
        if (array->elements == NULL) {
            sable_trap("out of memory");
        }
    }

    return array->elements + array->count++ * type->element_size;
}

void *sable_array_mutable_element(SableArray **slot, const SableMetadata *type, int64_t index) {
    if (*slot == NULL || index < 0 || index >= (*slot)->count) {
        sable_trap("array index out of range");
    }

    SableArray *array = unique(slot, type, 0);
    return array->elements + index * type->element_size;
}

// The last element, moved out: the caller loads it, and owns it.
void *sable_array_remove_last(SableArray **slot, const SableMetadata *type) {
    if (*slot == NULL || (*slot)->count == 0) {
        sable_trap("removeLast of an empty array");
    }

    SableArray *array = unique(slot, type, 0);
    return array->elements + --array->count * type->element_size;
}

static void copy_string_element(void *destination, const void *source) {
    memcpy(destination, source, sizeof(void *));
    sable_retain(*(void *const *)source);
}

static void destroy_string_element(void *element) {
    sable_release(*(void **)element);
}

static const SableMetadata string_array_metadata = {
    sable_array_destroy, "[String]", sizeof(void *), copy_string_element, destroy_string_element,
};

SableArray *sable_string_split(const SableString *s, int32_t separator) {
    SableArray *parts = NULL;
    int64_t start = 0;
    for (int64_t i = 0; i <= s->length;) {
        int64_t width = 1;
        if (i == s->length || decode(s->bytes + i, &width) == separator) {
            SableString *part = make_string(s->bytes + start, i - start);
            *(SableString **)sable_array_append_slot(&parts, &string_array_metadata) = part;
            start = i + width;
        }

        i += width;
    }

    return parts;
}
