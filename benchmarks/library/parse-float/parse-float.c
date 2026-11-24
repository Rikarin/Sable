// Port of parse-float.sb, with strtod.
#include "../../support/c/checked.h"

int main(void) {
    enum { COUNT = 200000 };
    static char texts[COUNT][32];
    int64_t x = 12345;
    for (int64_t i = 0; i < COUNT; i++) {
        int64_t whole = next_random(x);
        x = next_random(whole);
        snprintf(texts[i], sizeof texts[i], "%lld.%lld", (long long)(whole % 100000), (long long)x);
    }
    double total = 0.0;
    for (int round = 0; round < 5; round++) {
        for (int64_t i = 0; i < COUNT; i++) {
            char *end;
            total += strtod(texts[i], &end);
            if (*end != 0) trap("not a number");
        }
    }
    printf("%.3f\n", total);
    return 0;
}
