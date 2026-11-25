// Port of format-int.sb, with snprintf.
#include "../../support/c/bench.h"
#include "../../support/c/checked.h"

static void run(void) {
    int64_t x = 12345, characters = 0;
    for (int64_t i = 0; i < 1000000; i++) {
        x = next_random(x);
        char square[32], number[32];
        characters = add_i(characters, snprintf(square, sizeof square, "%lld", (long long)mul_i(x, x)));
        characters = add_i(characters, snprintf(number, sizeof number, "%lld", (long long)i));
    }
    bench_printf("%lld\n", (long long)characters);
}

BENCH_MAIN(run)
