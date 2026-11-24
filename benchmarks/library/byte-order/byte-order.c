// Port of byte-order.sb, for a little-endian machine.
#include "../../support/c/checked.h"

int main(void) {
    enum { COUNT = 1048576 };
    unsigned char *bytes = xrealloc(NULL, COUNT);
    uint64_t total = 0;
    for (uint32_t round = 0; round < 50; round++) {
        for (uint32_t at = 0; at + 12 <= COUNT; at += 16) {
            uint32_t first = __builtin_bswap32(at + round);
            uint64_t second = (uint64_t)at * 3;
            memcpy(bytes + at, &first, 4);
            memcpy(bytes + at + 4, &second, 8);
        }
        for (uint32_t at = 0; at + 12 <= COUNT; at += 16) {
            uint32_t first;
            uint64_t second;
            memcpy(&first, bytes + at, 4);
            memcpy(&second, bytes + at + 4, 8);
            total = (total + __builtin_bswap32(first) + second) % 1000000007;
        }
    }
    printf("%llu\n", (unsigned long long)total);
    free(bytes);
    return 0;
}
