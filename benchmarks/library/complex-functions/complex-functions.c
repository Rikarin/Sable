// Port of complex-functions.sb, with <complex.h>.
#include <complex.h>
#include <math.h>
#include "../../support/c/checked.h"

// glibc leaves it out for a clang it does not know.
#ifndef CMPLX
#define CMPLX(x, y) __builtin_complex((double)(x), (double)(y))
#endif

int main(void) {
    double complex total = 0.0;
    double magnitudes = 0.0;
    for (int row = 0; row < 480; row++) {
        for (int column = 0; column < 150; column++) {
            double complex z = CMPLX(column / 60.0 - 1.246, row / 180.0 - 1.329);
            double complex w = CMPLX(column / 150.0 + 0.5, 0.25);
            total = total + cexp(z) + clog(z) + csqrt(z) + cpow(z, w);
            total = total + csin(z) + ccos(z) + csinh(z) + ccosh(z);
            total = total + ctan(z) + ctanh(z);
            total = total + casin(z) + cacos(z) + catan(z);
            magnitudes += cabs(z) + carg(z);
            total += CMPLX(cabs(z) * cos(carg(z)), cabs(z) * sin(carg(z)));
        }
    }
    printf("%.2f %.2f %.2f\n", creal(total), cimag(total), magnitudes);
    return 0;
}
