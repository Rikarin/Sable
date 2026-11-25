// Port of complex-mandelbrot.sb, with C's complex numbers.
#include <complex.h>
#include "../../support/c/bench.h"
#include "../../support/c/checked.h"

// glibc leaves it out for a clang it does not know.
#ifndef CMPLX
#define CMPLX(x, y) __builtin_complex((double)(x), (double)(y))
#endif

#pragma STDC FP_CONTRACT OFF

static void run(void) {
    int64_t rounds = 0;
    for (int row = 0; row < 480; row++) {
        for (int column = 0; column < 640; column++) {
            double complex c = CMPLX(column / 256.0 - 2.0, row / 192.0 - 1.25);
            double complex z = 0.0;
            int64_t count = 0;
            while (count < 200 && creal(z) * creal(z) + cimag(z) * cimag(z) <= 4.0) {
                z = z * z + c;
                count = add_i(count, 1);
            }
            rounds = add_i(rounds, count);
        }
    }
    double complex w = 1.0, total = 0.0;
    for (int i = 0; i < 500000; i++) {
        w = CMPLX(1.0, 0.5) / (w + CMPLX(2.0, i % 16 / 16.0));
        total += w;
    }
    bench_printf("%lld %.3f %.3f\n", (long long)rounds, creal(total), cimag(total));
}

BENCH_MAIN(run)
