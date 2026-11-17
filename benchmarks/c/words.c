// Manually-managed port of benchmarks/words.sab (see README.md).
//
// Strings are one heap object each (literals static); arrays are a header
// and an element buffer (two mallocs; the empty array is NULL), growing 4,
// 8, 16, ... The dictionary is what Sable's [String: Int] is: an array of
// (key, value) entries searched linearly — get and set each search — and it
// owns its keys. Interpolation concatenates piece by piece, freeing each
// intermediate, as Sable's lowering does. ranked() shares its input and
// copies it only on its first write, as Sable's copy-on-write array does.
// Int arithmetic and array indexing are checked.

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static _Noreturn void trap(const char *message) {
    fflush(stdout);
    fprintf(stderr, "fatal error: %s\n", message);
    abort();
}

static inline int64_t add_i(int64_t a, int64_t b) {
    int64_t r;
    if (__builtin_add_overflow(a, b, &r)) trap("integer overflow");
    return r;
}

static inline int64_t sub_i(int64_t a, int64_t b) {
    int64_t r;
    if (__builtin_sub_overflow(a, b, &r)) trap("integer overflow");
    return r;
}

static inline int64_t mul_i(int64_t a, int64_t b) {
    int64_t r;
    if (__builtin_mul_overflow(a, b, &r)) trap("integer overflow");
    return r;
}

static void *xmalloc(size_t size) {
    void *p = malloc(size);
    if (p == NULL) trap("out of memory");
    return p;
}

// ---- Strings ---------------------------------------------------------------

typedef struct String {
    int64_t length;
    int64_t count; // Unicode scalars
    char bytes[];
} String;

static int64_t count_scalars(const char *bytes, int64_t length) {
    int64_t count = 0;
    for (int64_t i = 0; i < length; i++) {
        if (((unsigned char)bytes[i] & 0xC0) != 0x80) count++;
    }
    return count;
}

static String *string_make(const char *bytes, int64_t length) {
    String *s = xmalloc(sizeof(String) + (size_t)length);
    memcpy(s->bytes, bytes, (size_t)length);
    s->length = length;
    s->count = count_scalars(bytes, length);
    return s;
}

static String *string_concat(const String *a, const String *b) {
    String *s = xmalloc(sizeof(String) + (size_t)(a->length + b->length));
    memcpy(s->bytes, a->bytes, (size_t)a->length);
    memcpy(s->bytes + a->length, b->bytes, (size_t)b->length);
    s->length = a->length + b->length;
    s->count = a->count + b->count;
    return s;
}

static String *string_from_int(int64_t value) {
    char text[32];
    int n = snprintf(text, sizeof text, "%lld", (long long)value);
    return string_make(text, n);
}

static bool string_equal(const String *a, const String *b) {
    return a->length == b->length && memcmp(a->bytes, b->bytes, (size_t)a->length) == 0;
}

static void print_string(const String *s) {
    fwrite(s->bytes, 1, (size_t)s->length, stdout);
    fputc('\n', stdout);
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

// Static literals, laid out as heap strings are.
#define LITERAL(name, text)                                                          \
    static const struct { int64_t length, count; char bytes[sizeof(text)]; } name##_storage = \
        { sizeof(text) - 1, sizeof(text) - 1, text };                                \
    static String *const name = (String *)&name##_storage;

LITERAL(lit_alpha, "alpha")
LITERAL(lit_beta, "beta")
LITERAL(lit_gamma, "gamma")
LITERAL(lit_delta, "delta")
LITERAL(lit_epsilon, "epsilon")
LITERAL(lit_zeta, "zeta")
LITERAL(lit_eta, "eta")
LITERAL(lit_theta, "theta")
LITERAL(lit_iota, "iota")
LITERAL(lit_kappa, "kappa")
LITERAL(lit_space, " ")

// ---- Arrays ----------------------------------------------------------------

#define DEFINE_ARRAY(Name, T)                                                        \
    typedef struct Name {                                                            \
        int64_t count, capacity;                                                     \
        T *elements;                                                                 \
    } Name;                                                                          \
    static void Name##_append(Name **slot, T value) {                                \
        Name *array = *slot;                                                         \
        if (array == NULL) {                                                         \
            array = xmalloc(sizeof(Name));                                           \
            array->count = array->capacity = 0;                                      \
            array->elements = NULL;                                                  \
            *slot = array;                                                           \
        }                                                                            \
        if (array->count == array->capacity) {                                       \
            array->capacity = array->capacity < 4 ? 4 : array->capacity * 2;         \
            array->elements = realloc(array->elements, (size_t)array->capacity * sizeof(T)); \
            if (array->elements == NULL) trap("out of memory");                      \
        }                                                                            \
        array->elements[array->count++] = value;                                     \
    }                                                                                \
    static inline int64_t Name##_count(const Name *array) {                          \
        return array == NULL ? 0 : array->count;                                     \
    }                                                                                \
    static inline T *Name##_at(const Name *array, int64_t index) {                   \
        if (array == NULL || index < 0 || index >= array->count) trap("array index out of range"); \
        return &array->elements[index];                                              \
    }                                                                                \
    __attribute__((unused)) static Name *Name##_copy(const Name *array) {                                    \
        Name *copy = xmalloc(sizeof(Name));                                          \
        copy->count = copy->capacity = array->count;                                 \
        copy->elements = xmalloc((size_t)copy->capacity * sizeof(T));                \
        memcpy(copy->elements, array->elements, (size_t)copy->count * sizeof(T));   \
        return copy;                                                                 \
    }                                                                                \
    static void Name##_free(Name *array) {                                           \
        if (array != NULL) {                                                         \
            free(array->elements);                                                   \
            free(array);                                                             \
        }                                                                            \
    }

typedef struct Entry {
    String *key;
    int64_t value;
} Entry;

typedef struct Count {
    String *word;
    int64_t count;
} Count;

DEFINE_ARRAY(StringArray, String *)
DEFINE_ARRAY(EntryArray, Entry)
DEFINE_ARRAY(CountArray, Count)

// ---- The dictionary: entries, searched linearly ------------------------------

static bool dictionary_get(const EntryArray *entries, const String *key, int64_t *value) {
    for (int64_t i = 0; i < EntryArray_count(entries); i = add_i(i, 1)) {
        const Entry *entry = EntryArray_at(entries, i);
        if (string_equal(entry->key, key)) {
            *value = entry->value;
            return true;
        }
    }
    return false;
}

// Returns true when the key was taken in (a new entry owns it); false when
// an entry already had an equal key, which it keeps.
static bool dictionary_set(EntryArray **entries, String *key, int64_t value) {
    for (int64_t i = 0; i < EntryArray_count(*entries); i = add_i(i, 1)) {
        Entry *entry = EntryArray_at(*entries, i);
        if (string_equal(entry->key, key)) {
            entry->value = value;
            return false;
        }
    }
    EntryArray_append(entries, (Entry){ key, value });
    return true;
}

// ---- The program -------------------------------------------------------------

static StringArray *corpus(int64_t lines) {
    StringArray *words = NULL;
    StringArray_append(&words, lit_alpha);
    StringArray_append(&words, lit_beta);
    StringArray_append(&words, lit_gamma);
    StringArray_append(&words, lit_delta);
    StringArray_append(&words, lit_epsilon);
    StringArray_append(&words, lit_zeta);
    StringArray_append(&words, lit_eta);
    StringArray_append(&words, lit_theta);
    StringArray_append(&words, lit_iota);
    StringArray_append(&words, lit_kappa);

    StringArray *result = NULL;
    int64_t i = 0;
    while (i < lines) {
        String *a = *StringArray_at(words, i % 10);
        String *b = *StringArray_at(words, add_i(mul_i(i, 7), 3) % 10);
        String *c = *StringArray_at(words, add_i(mul_i(i, 13), 5) % 10);
        String *s1 = string_concat(a, lit_space);
        String *s2 = string_concat(s1, b);
        free(s1);
        String *s3 = string_concat(s2, lit_space);
        free(s2);
        String *s4 = string_concat(s3, c);
        free(s3);
        String *s5 = string_concat(s4, lit_space);
        free(s4);
        String *line = string_concat(s5, a);
        free(s5);
        StringArray_append(&result, line);
        i = add_i(i, 1);
    }
    StringArray_free(words); // its strings are static
    return result;
}

static StringArray *split(const String *s, int32_t separator) {
    StringArray *parts = NULL;
    int64_t start = 0;
    for (int64_t i = 0; i <= s->length;) {
        int64_t width = 1;
        if (i == s->length || decode(s->bytes + i, &width) == separator) {
            StringArray_append(&parts, string_make(s->bytes + start, i - start));
            start = i + width;
        }
        i += width;
    }
    return parts;
}

// Sorts a shared array: copied on the first write, as Sable's is.
static CountArray *ranked(CountArray *items, bool (*before)(Count, Count)) {
    CountArray *sorted = items;
    int64_t i = 1;
    while (i < CountArray_count(sorted)) {
        int64_t j = i;
        while (j > 0 && before(*CountArray_at(sorted, j), *CountArray_at(sorted, sub_i(j, 1)))) {
            if (sorted == items) {
                CountArray_at(items, j); // the bounds check of the write
                sorted = CountArray_copy(items);
            }
            Count held = *CountArray_at(sorted, j);
            *CountArray_at(sorted, j) = *CountArray_at(sorted, sub_i(j, 1));
            *CountArray_at(sorted, sub_i(j, 1)) = held;
            j = sub_i(j, 1);
        }
        i = add_i(i, 1);
    }
    return sorted;
}

static bool by_count(Count a, Count b) { return a.count > b.count; }

int main(void) {
    EntryArray *counts = NULL;
    StringArray *seen = NULL; // borrows the dictionary's keys
    int64_t round = 0;
    while (round < 20) {
        StringArray *lines = corpus(5000);
        for (int64_t l = 0; l < StringArray_count(lines); l++) {
            StringArray *words = split(*StringArray_at(lines, l), ' ');
            for (int64_t w = 0; w < StringArray_count(words); w++) {
                String *word = *StringArray_at(words, w);
                int64_t value;
                if (!dictionary_get(counts, word, &value)) {
                    StringArray_append(&seen, word);
                }
                int64_t current = dictionary_get(counts, word, &value) ? value : 0;
                if (dictionary_set(&counts, word, add_i(current, 1))) {
                    words->elements[w] = NULL; // the dictionary owns it now
                }
            }
            for (int64_t w = 0; w < StringArray_count(words); w++) {
                free(words->elements[w]);
            }
            StringArray_free(words);
        }
        for (int64_t l = 0; l < StringArray_count(lines); l++) {
            free(lines->elements[l]);
        }
        StringArray_free(lines);
        round = add_i(round, 1);
    }

    CountArray *items = NULL;
    for (int64_t k = 0; k < StringArray_count(seen); k++) {
        String *word = *StringArray_at(seen, k);
        int64_t value;
        CountArray_append(&items, (Count){ word, dictionary_get(counts, word, &value) ? value : 0 });
    }

    CountArray *order = ranked(items, by_count);
    for (int64_t k = 0; k < CountArray_count(order); k++) {
        Count item = *CountArray_at(order, k);
        String *s1 = string_concat(item.word, lit_space);
        String *number = string_from_int(item.count);
        String *text = string_concat(s1, number);
        free(number);
        free(s1);
        print_string(text);
        free(text);
    }

    if (order != items) CountArray_free(order);
    CountArray_free(items);
    StringArray_free(seen);
    for (int64_t k = 0; k < EntryArray_count(counts); k++) {
        free(counts->elements[k].key);
    }
    EntryArray_free(counts);
    return 0;
}
