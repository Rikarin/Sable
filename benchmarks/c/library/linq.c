// Port of benchmarks/versus-dotnet/linq.sb. C has no lazy chains: both sums
// are the hand-written loop, which is what the chain should match.
#include "checked.h"

static int64_t sum(const Ints *values, int64_t modulus) {
    int64_t total = 0;
    for (int64_t i = 0; i < values->count; i++) {
        int64_t value = values->elements[i];
        if (value % modulus != 0) total = add_i(total, mul_i(value, 2));
    }
    return total;
}

int main(void) {
    Ints values = {0};
    for (int64_t i = 0; i < 10000000; i++) ints_append(&values, i);
    int64_t chained = 0, looped = 0;
    for (int64_t round = 0; round < 5; round++) chained = add_i(chained, sum(opaque(&values), round + 3));
    for (int64_t round = 0; round < 5; round++) looped = add_i(looped, sum(opaque(&values), round + 3));
    printf("%lld %lld\n", (long long)chained, (long long)looped);
    free(values.elements);
    return 0;
}
