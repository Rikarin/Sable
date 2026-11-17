// The Sable runtime: the small amount of native code a compiled program
// links against (docs/06 §6.1).
//
// This is the first slice of it: writing a line, and stopping a program the
// way the language says it stops. Allocation, reference counting, metadata
// and the error ABI arrive with the lowerings that need them.

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// A string is UTF-8 bytes and their length, with no terminator.
void sable_print_string(const char *bytes, int64_t length) {
    fwrite(bytes, 1, (size_t)length, stdout);
    fputc('\n', stdout);
}

void sable_print_int(int64_t value) {
    printf("%lld\n", (long long)value);
}

// %.17g round-trips every double; the interpreter formats the same way.
void sable_print_f64(double value) {
    printf("%.17g\n", value);
}

void sable_print_bool(bool value) {
    fputs(value ? "true\n" : "false\n", stdout);
}

// Overflow, division by zero, an unreachable point reached: not catchable,
// not unwound. Say why, then stop (docs/06 §6.6).
_Noreturn void sable_trap(const char *message) {
    fflush(stdout);
    fprintf(stderr, "fatal error: %s\n", message);
    abort();
}

// Byte-for-byte equality: strings compare by content, not identity.
bool sable_string_equal(const char *a, int64_t a_length, const char *b, int64_t b_length) {
    return a_length == b_length && (a_length == 0 || memcmp(a, b, (size_t)a_length) == 0);
}
