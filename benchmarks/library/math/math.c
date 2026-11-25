// Port of math.sb.
#include <math.h>
#include "../../support/c/bench.h"
#include "../../support/c/checked.h"

static void run(void) {
    double total = 0.0;
    for (int i = 1; i < 500001; i++) {
        double x = i / 1000.0;
        total += sin(x) + cos(x) + log(x) + exp(-x) + atan2(x, 3.0) + hypot(x, 2.0);
    }
    bench_printf("%.4f\n", total);
}

BENCH_MAIN(run)
