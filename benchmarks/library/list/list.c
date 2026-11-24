// Port of list.sb.
#include "../../support/c/checked.h"

int main(void) {
    int64_t total = 0;
    for (int64_t round = 0; round < 3; round++) {
        Ints values = {0};
        for (int64_t i = 0; i < 10000000; i++) ints_append(&values, i ^ round);
        for (int64_t i = 0; i < values.count; i++) total = add_i(total, values.elements[i]);
        for (int64_t i = 0; i < values.count; i++) total = add_i(total, *ints_at(&values, i) % 7);
        while (values.count > 5000000) {
            total = sub_i(total, *ints_at(&values, values.count - 1));
            values.count--;
        }
        total = add_i(total, values.count);
        free(values.elements);
    }
    printf("%lld\n", (long long)total);
    return 0;
}
