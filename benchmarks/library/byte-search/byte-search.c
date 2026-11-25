// Port of byte-search.sb, with memchr, and a loop to count.
#include "../../support/c/bench.h"
#include "../../support/c/checked.h"

enum { COUNT = 1048576 };

static unsigned char *buffer;

static void setup(void) {
    buffer = xrealloc(NULL, COUNT);
    for (int i = 0; i < COUNT; i++) buffer[i] = (unsigned char)(i % 97);
    buffer[1048570] = 200;
}

static void run(void) {
    int64_t total = 0;
    for (int round = 0; round < 1000; round++) {
        const unsigned char *bytes = opaque(buffer);
        const unsigned char *at = memchr(bytes, 200, COUNT);
        int64_t count = 0;
        for (int i = 0; i < COUNT; i++) count += bytes[i] == 42;
        total = add_i(add_i(total, at - bytes), count);
    }
    bench_printf("%lld\n", (long long)total);
}

BENCH_MAIN_WITH(run, setup)
