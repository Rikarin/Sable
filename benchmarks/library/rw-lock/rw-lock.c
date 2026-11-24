// Port of rw-lock.sb: four threads and a pthread read-write lock.
#include <pthread.h>
#include "../../support/c/checked.h"

static pthread_rwlock_t gate = PTHREAD_RWLOCK_INITIALIZER;
static int64_t count;

static void *work(void *argument) {
    int64_t task = (intptr_t)argument, sum = 0;
    for (int64_t i = 0; i < 250000; i++) {
        if (i % 100 == task) {
            pthread_rwlock_wrlock(&gate);
            count = add_i(count, 1);
        } else {
            pthread_rwlock_rdlock(&gate);
            sum = add_i(sum, count % 2);
        }
        pthread_rwlock_unlock(&gate);
    }
    opaque(&sum);
    return NULL;
}

int main(void) {
    pthread_t threads[4];
    for (intptr_t t = 0; t < 4; t++) {
        if (pthread_create(&threads[t], NULL, work, (void *)t) != 0) trap("pthread_create");
    }
    for (int t = 0; t < 4; t++) pthread_join(threads[t], NULL);
    printf("%lld\n", (long long)count);
    return 0;
}
