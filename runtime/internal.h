// The Sable runtime: what every part of it shares — the C library, and the
// few things each part may need before the part that defines them.
// sable_runtime.c includes it first.

#ifndef SABLE_RUNTIME_INTERNAL_H
#define SABLE_RUNTIME_INTERNAL_H

// glibc declares dladdr (for a trap's backtrace) only for GNU sources.
#define _GNU_SOURCE

// The Microsoft C runtime calls fopen, strdup and the like deprecated; they
// are the C library's, and used as such.
#define _CRT_SECURE_NO_WARNINGS
#define _CRT_NONSTDC_NO_DEPRECATE

#include <errno.h>
#include <math.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
// The Microsoft C runtime's names for holding a stream across calls.
#define flockfile _lock_file
#define funlockfile _unlock_file
#endif

// Stops the program, saying why (traps.c).
_Noreturn void sable_trap(const char *message);

// Bytes gathered as they come — a process's output — in memory the owner frees.
typedef struct {
    char *bytes;
    size_t length;
    size_t capacity;
} Buffer;

static inline void buffer_add(Buffer *buffer, const char *bytes, size_t length) {
    if (buffer->length + length > buffer->capacity) {
        size_t capacity = buffer->capacity == 0 ? 4096 : buffer->capacity;
        while (capacity < buffer->length + length) {
            capacity *= 2;
        }

        char *grown = realloc(buffer->bytes, capacity);
        if (grown == NULL) {
            sable_trap("out of memory");
        }

        buffer->bytes = grown;
        buffer->capacity = capacity;
    }

    memcpy(buffer->bytes + buffer->length, bytes, length);
    buffer->length += length;
}

#endif
