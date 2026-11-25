// What the C ports share: the measuring, as Sable's Bencher does it
// (../../README.md, "How it is measured"). A port has 'static void work(void)',
// which does the work and says what it computed with 'bench_printf'; and 'BENCH_MAIN(work)', or BENCH_MAIN_WITH(work, setup), whose
// 'static void setup(void)' makes what it needs. Run without arguments, the program
// does the work once and prints what it computed; with '--measure', it prints
// 'check' and the samples, as support/dotnet/Program.cs does.

#ifndef BENCH_H
#define BENCH_H

#include <math.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/time.h>
#include <time.h>

static char bench_check[4096];
static size_t bench_length;

// Adds to what this run computed.
__attribute__((format(printf, 1, 2))) static inline void bench_printf(const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    bench_length += (size_t)vsnprintf(bench_check + bench_length, sizeof bench_check - bench_length, format, arguments);
    va_end(arguments);
}

// The same of bytes, as fwrite has them.
static inline void bench_write(const void *bytes, size_t length) {
    length = length < sizeof bench_check - 1 - bench_length ? length : sizeof bench_check - 1 - bench_length;
    memcpy(bench_check + bench_length, bytes, length);
    bench_length += length;
    bench_check[bench_length] = 0;
}

static double bench_clock(clockid_t clock) {
    struct timespec now;
    clock_gettime(clock, &now);
    return (double)now.tv_sec * 1e9 + (double)now.tv_nsec;
}

// This process's processor time, and that of the processes it waited for.
static double bench_processor(void) {
    struct rusage usage;
    getrusage(RUSAGE_CHILDREN, &usage);
    return bench_clock(CLOCK_PROCESS_CPUTIME_ID) + (double)(usage.ru_utime.tv_sec + usage.ru_stime.tv_sec) * 1e9
        + (double)(usage.ru_utime.tv_usec + usage.ru_stime.tv_usec) * 1e3;
}

typedef struct BenchSample {
    long long iterations;
    double wall, processor;
} BenchSample;

static BenchSample bench_iterate(void (*volatile work)(void), long long iterations) {
    BenchSample sample = { iterations, 0, 0 };
    double used = bench_processor(), begun = bench_clock(CLOCK_MONOTONIC);
    for (long long i = 0; i < iterations; i++) {
        bench_length = 0;
        work();
    }

    sample.wall = bench_clock(CLOCK_MONOTONIC) - begun;
    sample.processor = bench_processor() - used;
    return sample;
}

static double bench_option(int argc, char **argv, const char *name, double otherwise) {
    for (int i = 1; i + 1 < argc; i++) {
        if (strcmp(argv[i], name) == 0) {
            return atof(argv[i + 1]);
        }
    }

    return otherwise;
}

static long long bench_choose(double sample, double per) {
    long long count = (long long)ceil(sample / per);
    return count < 1 ? 1 : count;
}

static int bench_main(int argc, char **argv, void (*work)(void)) {
    if (argc == 1) {
        work();
        printf("%s", bench_check);
        return 0;
    }

    double warm_up = bench_option(argc, argv, "--warm-up-ms", 100) * 1e6;
    double length = bench_option(argc, argv, "--sample-ms", 10) * 1e6;
    int samples = (int)bench_option(argc, argv, "--samples", 20);
    double budget = bench_option(argc, argv, "--budget-ms", 3000) * 1e6;

    // The pilot.
    long long iterations = 1;
    double warmed = 0;
    BenchSample sample;
    for (;;) {
        sample = bench_iterate(work, iterations);
        warmed += sample.wall;
        if (sample.wall * 8 >= length) {
            break;
        }

        iterations *= 2;
    }

    iterations = bench_choose(length, sample.wall / (double)iterations);

    // The warm-up.
    double last = 0;
    int runs = 0, rises = 0;
    while (warmed < warm_up || (rises < 2 && runs < 50 && warmed < 3 * warm_up)) {
        sample = bench_iterate(work, iterations);
        warmed += sample.wall;
        runs++;
        double per = sample.wall / (double)iterations;
        rises += runs > 1 && per >= last;
        last = per;
        iterations = bench_choose(length, per);
    }

    BenchSample *taken = malloc((size_t)samples * sizeof *taken);
    if (taken == NULL) {
        return 1;
    }

    int count = 0;
    for (double measured = 0; count < samples && (count < 5 || measured < budget); count++) {
        taken[count] = bench_iterate(work, iterations);
        measured += taken[count].wall;
    }

    printf("check\t");
    for (const char *c = bench_check; *c != 0; c++) {
        switch (*c) {
            case '\n': printf("\\n"); break;
            case '\t': printf("\\t"); break;
            case '\\': printf("\\\\"); break;
            default: putchar(*c);
        }
    }

    putchar('\n');
    for (int i = 0; i < count; i++) {
        printf("sample\t%lld\t%f\t%f\n", taken[i].iterations, taken[i].wall, taken[i].processor);
    }

    free(taken);
    return 0;
}

#define BENCH_MAIN(work) \
    int main(int argc, char **argv) { return bench_main(argc, argv, work); }

#define BENCH_MAIN_WITH(work, setup) \
    int main(int argc, char **argv) { \
        setup(); \
        return bench_main(argc, argv, work); \
    }

#endif
