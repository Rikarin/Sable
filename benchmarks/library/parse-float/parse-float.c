// Port of parse-float.sb, with strtod.
#include "../../support/c/bench.h"
#include "../../support/c/checked.h"

enum { COUNT = 200000 };

static char texts[COUNT][32];

static void setup(void) {
    int64_t x = 12345;
    for (int64_t i = 0; i < COUNT; i++) {
        int64_t whole = next_random(x);
        x = next_random(whole);
        snprintf(texts[i], sizeof texts[i], "%lld.%lld", (long long)(whole % 100000), (long long)x);
    }
}

static void run(void) {
    double total = 0.0;
    for (int round = 0; round < 5; round++) {
        for (int64_t i = 0; i < COUNT; i++) {
            char *end;
            total += strtod(texts[i], &end);
            if (*end != 0) trap("not a number");
        }
    }
    bench_printf("%.3f\n", total);
}

BENCH_MAIN_WITH(run, setup)
