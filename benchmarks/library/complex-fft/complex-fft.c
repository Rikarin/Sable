// Port of complex-fft.sb, with C's complex numbers.
#include <complex.h>
#include <math.h>
#include "../../support/c/checked.h"

// glibc leaves it out for a clang it does not know.
#ifndef CMPLX
#define CMPLX(x, y) __builtin_complex((double)(x), (double)(y))
#endif

#pragma STDC FP_CONTRACT OFF

enum { N = 65536 };

static size_t at(int64_t i, int64_t count) {
    if (i < 0 || i >= count) trap("index out of bounds");
    return (size_t)i;
}

static void transform(double complex *values, int64_t n, const double complex *roots) {
    int64_t j = 0;
    for (int64_t i = 1; i < n; i++) {
        int64_t bit = n >> 1;
        while (j & bit) {
            j ^= bit;
            bit >>= 1;
        }
        j ^= bit;
        if (i < j) {
            double complex t = values[at(i, n)];
            values[at(i, n)] = values[at(j, n)];
            values[at(j, n)] = t;
        }
    }
    for (int64_t length = 2; length <= n; length = mul_i(length, 2)) {
        int64_t half = length / 2, step = n / length;
        for (int64_t start = 0; start < n; start = add_i(start, length)) {
            for (int64_t k = 0; k < half; k++) {
                int64_t low = add_i(start, k), high = add_i(low, half);
                double complex even = values[at(low, n)];
                double complex odd = values[at(high, n)] * roots[at(mul_i(k, step), n / 2)];
                values[at(low, n)] = even + odd;
                values[at(high, n)] = even - odd;
            }
        }
    }
}

int main(void) {
    double complex *signal = xrealloc(NULL, N * sizeof *signal), *values = xrealloc(NULL, N * sizeof *values);
    double complex *forward = xrealloc(NULL, N / 2 * sizeof *forward), *inverse = xrealloc(NULL, N / 2 * sizeof *inverse);
    for (int64_t i = 0; i < N; i++) {
        values[i] = signal[i] = CMPLX((double)(i * 7919 % 1009) / 1009.0, (double)(i * 104729 % 757) / 757.0);
    }
    for (int64_t i = 0; i < N / 2; i++) {
        double angle = -2.0 * 3.14159265358979323846 * (double)i / N;
        forward[i] = CMPLX(cos(angle), sin(angle));
        inverse[i] = conj(forward[i]);
    }
    double peak = 0.0;
    for (int round = 0; round < 16; round++) {
        transform(values, N, forward);
        peak = cabs(values[0]);
        transform(values, N, inverse);
        for (int64_t i = 0; i < N; i++) {
            values[i] /= (double)N;
        }
    }
    double error = 0.0;
    for (int64_t i = 0; i < N; i++) {
        error += cabs(values[i] - signal[i]);
    }
    free(signal);
    free(values);
    free(forward);
    free(inverse);
    printf("%.3f %s\n", peak, error < 0.000001 ? "true" : "false");
    return 0;
}
