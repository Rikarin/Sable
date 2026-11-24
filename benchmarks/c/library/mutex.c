// Port of benchmarks/versus-dotnet/mutex.sb: four threads and a pthread mutex.
#include <pthread.h>
#include "checked.h"

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

int main(void) {
    pthread_t threads[4];
    for (int t = 0; t < 4; t++) {
        if (pthread_create(&threads[t], NULL, work, NULL) != 0) trap("pthread_create");
    }
    for (int t = 0; t < 4; t++) pthread_join(threads[t], NULL);
    printf("%lld\n", (long long)count);
    return 0;
}
