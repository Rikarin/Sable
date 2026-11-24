// Port of format-int.sb, with snprintf.
#include "../../support/c/checked.h"

int main(void) {
    int64_t x = 12345, characters = 0;
    for (int64_t i = 0; i < 1000000; i++) {
        x = next_random(x);
        char square[32], number[32];
        characters = add_i(characters, snprintf(square, sizeof square, "%lld", (long long)mul_i(x, x)));
        characters = add_i(characters, snprintf(number, sizeof number, "%lld", (long long)i));
    }
    printf("%lld\n", (long long)characters);
    return 0;
}
