/*
 * The C side of inline_layout.sb's coverage-guided harness: libFuzzer's
 * entry point, which pushes each input into the Sable program a byte at a
 * time (only integers cross to C) and asks fromBytes for its verdict.
 * tools/fuzz/coverage.sh builds it with the program's LLVM IR, its @main
 * renamed out of libFuzzer's way, and the runtime:
 *
 *     clang -fsanitize=fuzzer,address,undefined fuzz.ll sable_runtime.c from_bytes.c
 *
 * Anything AddressSanitizer or UndefinedBehaviorSanitizer catches stops the
 * run with a report; a record accepted that does not write back as it was
 * read aborts, so libFuzzer keeps the input either way.
 *
 * Built with -DSABLE_FUZZ_STANDALONE instead of libFuzzer (Apple's clang
 * has no libFuzzer runtime), it has its own main:
 *
 *     from_bytes seeds <dir>      write the seed corpus: valid records of both shapes
 *     from_bytes <file>...        run each file once, as libFuzzer would
 */
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void sable_fuzz_begin(void);
void sable_fuzz_push(uint8_t byte);
int64_t sable_fuzz_check(void);
int64_t sable_fuzz_seed_count(int64_t n);
uint8_t sable_fuzz_seed_byte(int64_t n, int64_t at);

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
    sable_fuzz_begin();
    for (size_t i = 0; i < size; i++) {
        sable_fuzz_push(data[i]);
    }
    if (sable_fuzz_check() < 0) {
        abort();
    }
    return 0;
}

#ifdef SABLE_FUZZ_STANDALONE
/* The harness's own 24 seeds, the same records its random driver starts from. */
static int write_seeds(const char *directory) {
    char path[4096];
    for (int64_t n = 0; n < 24; n++) {
        snprintf(path, sizeof path, "%s/seed-%02lld", directory, (long long)n);
        FILE *file = fopen(path, "wb");
        if (!file) {
            perror(path);
            return 1;
        }
        int64_t count = sable_fuzz_seed_count(n);
        for (int64_t at = 0; at < count; at++) {
            fputc(sable_fuzz_seed_byte(n, at), file);
        }
        fclose(file);
    }
    return 0;
}

static int run_file(const char *path) {
    FILE *file = fopen(path, "rb");
    if (!file) {
        perror(path);
        return 1;
    }
    size_t capacity = 4096, size = 0;
    uint8_t *data = malloc(capacity);
    for (int c; (c = fgetc(file)) != EOF;) {
        if (size == capacity) {
            capacity *= 2;
            data = realloc(data, capacity);
        }
        data[size++] = (uint8_t)c;
    }
    fclose(file);
    LLVMFuzzerTestOneInput(data, size);
    free(data);
    return 0;
}

int main(int argc, char **argv) {
    if (argc == 3 && strcmp(argv[1], "seeds") == 0) {
        return write_seeds(argv[2]);
    }
    for (int i = 1; i < argc; i++) {
        if (run_file(argv[i]) != 0) {
            return 1;
        }
    }
    printf("ran %d inputs\n", argc - 1);
    return 0;
}
#endif
