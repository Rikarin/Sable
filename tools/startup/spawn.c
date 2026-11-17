// Start-to-exit time of a program: posix_spawn to waitpid, 1,000 runs, with
// standard output to /dev/null. The number docs/04 §2.5's "startup time
// < 2 ms" is measured with — a shell or Python loop adds its own spawn cost.
//
//     clang -O2 tools/startup/spawn.c -o spawn && ./spawn ./hello

#include <fcntl.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/wait.h>
#include <time.h>

extern char **environ;

static int compare(const void *a, const void *b) {
    double x = *(const double *)a, y = *(const double *)b;
    return x < y ? -1 : x > y;
}

int main(int argc, char **argv) {
    if (argc != 2) {
        fprintf(stderr, "usage: spawn <program>\n");
        return 2;
    }

    enum { RUNS = 1000 };
    static double times[RUNS];
    posix_spawn_file_actions_t actions;
    posix_spawn_file_actions_init(&actions);
    posix_spawn_file_actions_addopen(&actions, 1, "/dev/null", O_WRONLY, 0);

    for (int i = 0; i < RUNS; i++) {
        struct timespec start, end;
        char *arguments[] = { argv[1], NULL };
        pid_t pid;
        int status;

        clock_gettime(CLOCK_MONOTONIC, &start);
        if (posix_spawn(&pid, argv[1], &actions, NULL, arguments, environ) != 0) {
            perror("posix_spawn");
            return 1;
        }

        waitpid(pid, &status, 0);
        clock_gettime(CLOCK_MONOTONIC, &end);
        times[i] = (end.tv_sec - start.tv_sec) * 1e3 + (end.tv_nsec - start.tv_nsec) / 1e6;
    }

    qsort(times, RUNS, sizeof times[0], compare);
    printf("%s: median %.3f ms, p10 %.3f ms, p90 %.3f ms\n", argv[1], times[RUNS / 2], times[RUNS / 10], times[RUNS * 9 / 10]);
    return 0;
}
