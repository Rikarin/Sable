// Port of parse-int.sb, with strtoll.
#include <errno.h>
#include "../../support/c/checked.h"

int main(void) {
    enum { COUNT = 200000 };
    static char texts[COUNT][32];
    int64_t x = 12345;
    for (int64_t i = 0; i < COUNT; i++) {
        x = next_random(x);
        snprintf(texts[i], sizeof texts[i], "%lld", (long long)(i % 2 == 0 ? x : -x));
    }
    int64_t total = 0;
    for (int round = 0; round < 5; round++) {
        for (int64_t i = 0; i < COUNT; i++) {
            errno = 0;
            char *end;
            long long value = strtoll(texts[i], &end, 10);
            if (errno != 0 || *end != 0) trap("not a number");
            total = add_i(total, value);
        }
    }
    printf("%lld\n", (long long)total);
    return 0;
}
