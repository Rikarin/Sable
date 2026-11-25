// Port of mutex.sb: four threads and a pthread mutex.
#include <pthread.h>
#include "../../support/c/bench.h"
#include "../../support/c/checked.h"

static pthread_mutex_t gate = PTHREAD_MUTEX_INITIALIZER;
static int64_t count;

static void *work(void *unused) {
    (void)unused;
    for (int i = 0; i < 250000; i++) {
        pthread_mutex_lock(&gate);
        count = add_i(count, 1);
        pthread_mutex_unlock(&gate);
    }
    return NULL;
}

static void run(void) {
    count = 0;
    pthread_t threads[4];
    for (int t = 0; t < 4; t++) {
        if (pthread_create(&threads[t], NULL, work, NULL) != 0) trap("pthread_create");
    }
    for (int t = 0; t < 4; t++) pthread_join(threads[t], NULL);
    bench_printf("%lld\n", (long long)count);
}

BENCH_MAIN(run)
