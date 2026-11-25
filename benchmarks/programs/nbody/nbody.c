// Manually-managed port of nbody.sb (see ../README.md).
//
// The bodies are a growable array — a header and an element buffer, built
// by appending as Sable's array literal is — indexed with bounds checks.
// Floating point is evaluated exactly as written (no FMA contraction, as
// Sable emits none), and printed the way Sable prints an F64: the shortest
// %.Ng that reads back as the same double.

#include "../../support/c/bench.h"
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#pragma STDC FP_CONTRACT OFF

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

typedef struct Body {
    double x, y, z, vx, vy, vz, mass;
} Body;

typedef struct BodyArray {
    int64_t count, capacity;
    Body *elements;
} BodyArray;

static void append(BodyArray **slot, Body value) {
    BodyArray *array = *slot;
    if (array == NULL) {
        array = malloc(sizeof(BodyArray));
        if (array == NULL) trap("out of memory");
        array->count = array->capacity = 0;
        array->elements = NULL;
        *slot = array;
    }
    if (array->count == array->capacity) {
        array->capacity = array->capacity < 4 ? 4 : array->capacity * 2;
        array->elements = realloc(array->elements, (size_t)array->capacity * sizeof(Body));
        if (array->elements == NULL) trap("out of memory");
    }
    array->elements[array->count++] = value;
}

static inline int64_t count_of(const BodyArray *array) {
    return array == NULL ? 0 : array->count;
}

static inline Body *at(const BodyArray *array, int64_t index) {
    if (array == NULL || index < 0 || index >= array->count) trap("array index out of range");
    return &array->elements[index];
}

static void format_f64(double value, char *text, size_t size) {
    if (isnan(value)) {
        snprintf(text, size, "nan");
        return;
    }
    for (int precision = 1; precision < 17; precision++) {
        snprintf(text, size, "%.*g", precision, value);
        if (strtod(text, NULL) == value) return;
    }
    snprintf(text, size, "%.17g", value);
}

static void print_f64(double value) {
    char text[40];
    format_f64(value, text, sizeof text);
    bench_printf("%s\n", text);
}

static double sqrtApprox(double v) {
    double guess = v / 2.0 + 0.5;
    int64_t k = 0;
    while (k < 12) {
        guess = 0.5 * (guess + v / guess);
        k = add_i(k, 1);
    }
    return guess;
}

static double energy(const BodyArray *bodies) {
    double e = 0.0;
    int64_t i = 0;
    while (i < count_of(bodies)) {
        Body b = *at(bodies, i);
        e = e + 0.5 * b.mass * (b.vx * b.vx + b.vy * b.vy + b.vz * b.vz);
        int64_t j = add_i(i, 1);
        while (j < count_of(bodies)) {
            Body c = *at(bodies, j);
            double dx = b.x - c.x;
            double dy = b.y - c.y;
            double dz = b.z - c.z;
            e = e - b.mass * c.mass / sqrtApprox(dx * dx + dy * dy + dz * dz);
            j = add_i(j, 1);
        }
        i = add_i(i, 1);
    }
    return e;
}

static void advance(BodyArray *bodies, double dt) {
    int64_t n = count_of(bodies);
    int64_t i = 0;
    while (i < n) {
        Body b = *at(bodies, i);
        int64_t j = add_i(i, 1);
        while (j < n) {
            Body c = *at(bodies, j);
            double dx = b.x - c.x;
            double dy = b.y - c.y;
            double dz = b.z - c.z;
            double d2 = dx * dx + dy * dy + dz * dz;
            double mag = dt / (d2 * sqrtApprox(d2));
            b.vx = b.vx - dx * c.mass * mag;
            b.vy = b.vy - dy * c.mass * mag;
            b.vz = b.vz - dz * c.mass * mag;
            c.vx = c.vx + dx * b.mass * mag;
            c.vy = c.vy + dy * b.mass * mag;
            c.vz = c.vz + dz * b.mass * mag;
            *at(bodies, j) = c;
            j = add_i(j, 1);
        }
        b.x = b.x + dt * b.vx;
        b.y = b.y + dt * b.vy;
        b.z = b.z + dt * b.vz;
        *at(bodies, i) = b;
        i = add_i(i, 1);
    }
}

static void program(void) {
    BodyArray *bodies = NULL;
    append(&bodies, (Body){ 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 39.47 });
    append(&bodies, (Body){ 4.84, -1.16, -0.10, 0.60, 2.81, -0.02, 0.037 });
    append(&bodies, (Body){ 8.34, 4.12, -0.40, -1.01, 1.82, 0.008, 0.011 });
    append(&bodies, (Body){ 12.89, -15.11, -0.22, 1.08, 0.86, -0.01, 0.0017 });
    append(&bodies, (Body){ 15.37, -25.91, 0.17, 0.97, 0.59, -0.03, 0.002 });

    print_f64(energy(bodies));
    int64_t step = 0;
    while (step < 200000) {
        advance(bodies, 0.01);
        step = add_i(step, 1);
    }
    print_f64(energy(bodies));

    free(bodies->elements);
    free(bodies);
}

BENCH_MAIN(program)
