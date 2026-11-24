// What the C ports here share: Sable's checks (integer overflow and array
// bounds trap) and a growable array of int64_t.

#ifndef CHECKED_H
#define CHECKED_H

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static _Noreturn inline void trap(const char *message) {
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

static inline int64_t next_random(int64_t x) {
    return add_i(mul_i(x, 1103515245), 12345) % 2147483648;
}

static inline void *xrealloc(void *p, size_t size) {
    p = realloc(p, size);
    if (p == NULL) trap("out of memory");
    return p;
}

typedef struct Ints {
    int64_t count, capacity;
    int64_t *elements;
} Ints;

static inline void ints_reserve(Ints *a) {
    if (a->count == a->capacity) {
        a->capacity = a->capacity < 4 ? 4 : a->capacity * 2;
        a->elements = xrealloc(a->elements, (size_t)a->capacity * sizeof(int64_t));
    }
}

static inline void ints_append(Ints *a, int64_t value) {
    ints_reserve(a);
    a->elements[a->count++] = value;
}

static inline int64_t *ints_at(const Ints *a, int64_t index) {
    if ((uint64_t)index >= (uint64_t)a->count) trap("array index out of range");
    return &a->elements[index];
}

static inline void ints_insert(Ints *a, int64_t value, int64_t index) {
    if ((uint64_t)index > (uint64_t)a->count) trap("array index out of range");
    ints_reserve(a);
    memmove(a->elements + index + 1, a->elements + index, (size_t)(a->count - index) * sizeof(int64_t));
    a->elements[index] = value;
    a->count++;
}

static inline int64_t ints_remove(Ints *a, int64_t index) {
    int64_t value = *ints_at(a, index);
    a->count--;
    memmove(a->elements + index, a->elements + index + 1, (size_t)(a->count - index) * sizeof(int64_t));
    return value;
}

// Keeps the optimiser from carrying one round's answer over to the next.
static inline const void *opaque(const void *p) {
    __asm__ volatile("" : "+r"(p) : : "memory");
    return p;
}

#endif
