// Port of atomic.sb: four threads.
#include <pthread.h>
#include <stdatomic.h>
#include "../../support/c/checked.h"

static _Atomic int64_t count;

static void *work(void *unused) {
    (void)unused;
    for (int i = 0; i < 500000; i++) atomic_fetch_add(&count, 1);
    for (int i = 0; i < 500000; i++) {
        int64_t seen = atomic_load(&count);
        while (!atomic_compare_exchange_strong(&count, &seen, add_i(seen, 1))) {
        }
    }
    return NULL;
}

int main(void) {
    pthread_t threads[4];
    for (intptr_t t = 0; t < 4; t++) {
        if (pthread_create(&threads[t], NULL, work, (void *)t) != 0) trap("pthread_create");
    }
    for (int t = 0; t < 4; t++) pthread_join(threads[t], NULL);
    printf("%lld\n", (long long)atomic_load(&count));
    return 0;
}
