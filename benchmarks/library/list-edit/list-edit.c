// Port of list-edit.sb.
#include "../../support/c/checked.h"

int main(void) {
    int64_t total = 0;
    for (int64_t round = 0; round < 3; round++) {
        Ints list = {0};
        for (int64_t i = 0; i < 1000000; i++) ints_append(&list, i ^ round);
        for (int64_t i = 0; i < list.count; i++) total = add_i(total, *ints_at(&list, i) % 7);
        list.count = 20000;
        for (int64_t i = 0; i < 2000; i++) ints_insert(&list, i, list.count / 2);
        for (int64_t i = 0; i < 2000; i++) total = add_i(total, ints_remove(&list, list.count / 3));
        int64_t kept = 0;
        for (int64_t i = 0; i < list.count; i++) {
            int64_t value = list.elements[i];
            if (value % 2 != 0) list.elements[kept++] = value;
        }
        list.count = kept;
        total = add_i(add_i(total, list.count), *ints_at(&list, list.count / 2));
        free(list.elements);
    }
    printf("%lld\n", (long long)total);
    return 0;
}
