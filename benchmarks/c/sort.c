// Manually-managed port of benchmarks/sort.sab (see README.md).
//
// The generic merge sort is instantiated twice, for Int and for String, as
// Sable monomorphises it; the ordering is a function pointer. Arrays are a
// header and an element buffer (two mallocs; the empty array is NULL),
// slices are copies, appends grow the buffer 4, 8, 16, ... — all as in
// Sable — and every array is freed where its Sable counterpart dies.
// Strings are one heap object each; the sorted String arrays borrow the
// labels' strings, which are freed once, at exit. Int arithmetic and array
// indexing are checked.

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

static String *string_concat(const char *a, int64_t a_length, int64_t a_count, const String *b) {
    String *s = xmalloc(sizeof(String) + (size_t)(a_length + b->length));
    memcpy(s->bytes, a, (size_t)a_length);
    memcpy(s->bytes + a_length, b->bytes, (size_t)b->length);
    s->length = a_length + b->length;
    s->count = a_count + b->count;
    return s;
}

static String *string_from_int(int64_t value) {
    char text[32];
    int n = snprintf(text, sizeof text, "%lld", (long long)value);
    return string_make(text, n);
}

// ---- Arrays and the sort ---------------------------------------------------

#define DEFINE_SORT(Name, T)                                                         \
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
    static inline T Name##_get(const Name *array, int64_t index) {                   \
        if (array == NULL || index < 0 || index >= array->count) trap("array index out of range"); \
        return array->elements[index];                                               \
    }                                                                                \
    static Name *Name##_slice(const Name *array, int64_t from, int64_t to) {         \
        int64_t count = Name##_count(array);                                         \
        if (from < 0 || to > count || from > to) trap("array slice out of range");   \
        if (from == to) return NULL;                                                 \
        Name *copy = xmalloc(sizeof(Name));                                          \
        copy->count = copy->capacity = to - from;                                    \
        copy->elements = xmalloc((size_t)copy->capacity * sizeof(T));                \
        memcpy(copy->elements, array->elements + from, (size_t)copy->count * sizeof(T)); \
        return copy;                                                                 \
    }                                                                                \
    static void Name##_free(Name *array) {                                           \
        if (array != NULL) {                                                         \
            free(array->elements);                                                   \
            free(array);                                                             \
        }                                                                            \
    }                                                                                \
    static Name *Name##_sorted(const Name *items, bool (*before)(T, T)) {            \
        if (Name##_count(items) <= 1) {                                              \
            Name *copy = NULL;                                                       \
            for (int64_t k = 0; k < Name##_count(items); k++) {                      \
                Name##_append(&copy, Name##_get(items, k));                          \
            }                                                                        \
            return copy;                                                             \
        }                                                                            \
        int64_t middle = Name##_count(items) / 2;                                    \
        Name *slice = Name##_slice(items, 0, middle);                                \
        Name *left = Name##_sorted(slice, before);                                   \
        Name##_free(slice);                                                          \
        slice = Name##_slice(items, middle, Name##_count(items));                    \
        Name *right = Name##_sorted(slice, before);                                  \
        Name##_free(slice);                                                          \
                                                                                     \
        Name *result = NULL;                                                         \
        int64_t i = 0;                                                               \
        int64_t j = 0;                                                               \
        while (i < Name##_count(left) && j < Name##_count(right)) {                  \
            if (before(Name##_get(right, j), Name##_get(left, i))) {                 \
                Name##_append(&result, Name##_get(right, j));                        \
                j = add_i(j, 1);                                                     \
            } else {                                                                 \
                Name##_append(&result, Name##_get(left, i));                         \
                i = add_i(i, 1);                                                     \
            }                                                                        \
        }                                                                            \
        while (i < Name##_count(left)) {                                             \
            Name##_append(&result, Name##_get(left, i));                             \
            i = add_i(i, 1);                                                         \
        }                                                                            \
        while (j < Name##_count(right)) {                                            \
            Name##_append(&result, Name##_get(right, j));                            \
            j = add_i(j, 1);                                                         \
        }                                                                            \
        Name##_free(left);                                                           \
        Name##_free(right);                                                          \
        return result;                                                               \
    }

DEFINE_SORT(IntArray, int64_t)
DEFINE_SORT(StringArray, String *)

static bool int_before(int64_t a, int64_t b) { return a < b; }
static bool string_before(String *a, String *b) { return a->count < b->count; }

int main(void) {
    IntArray *numbers = NULL;
    int64_t seed = 42;
    int64_t i = 0;
    while (i < 200000) {
        seed = add_i(mul_i(seed, 1103515245), 12345) % 2147483648;
        IntArray_append(&numbers, seed % 1000000);
        i = add_i(i, 1);
    }

    IntArray *slice = IntArray_slice(numbers, 0, IntArray_count(numbers));
    IntArray *ascending = IntArray_sorted(slice, int_before);
    IntArray_free(slice);

    StringArray *labels = NULL;
    i = 0;
    while (i < 20000) {
        String *number = string_from_int(IntArray_get(ascending, mul_i(i, 10)) % 997);
        StringArray_append(&labels, string_concat("item-", 5, 5, number));
        free(number);
        i = add_i(i, 1);
    }
    StringArray *labelSlice = StringArray_slice(labels, 0, StringArray_count(labels));
    StringArray *byLabel = StringArray_sorted(labelSlice, string_before);
    StringArray_free(labelSlice);

    printf("%lld\n", (long long)IntArray_get(ascending, 0));
    printf("%lld\n", (long long)IntArray_get(ascending, 199999));
    String *first = StringArray_get(byLabel, 0);
    String *last = StringArray_get(byLabel, 19999);
    fwrite(first->bytes, 1, (size_t)first->length, stdout);
    fputc('\n', stdout);
    fwrite(last->bytes, 1, (size_t)last->length, stdout);
    fputc('\n', stdout);

    IntArray_free(numbers);
    IntArray_free(ascending);
    StringArray_free(byLabel);
    for (int64_t k = 0; k < labels->count; k++) {
        free(labels->elements[k]);
    }
    StringArray_free(labels);
    return 0;
}
