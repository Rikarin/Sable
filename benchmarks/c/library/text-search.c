// Port of benchmarks/versus-dotnet/text-search.sb, with memmem; the pieces
// of a split are pointers into the text.
#define _GNU_SOURCE
#include "checked.h"

int main(void) {
    static const char sentence[] = "lorem ipsum dolor sit amet, consectetur adipiscing elit, ";
    const size_t piece = sizeof sentence - 1, length = 20000 * piece + 6;
    char *buffer = xrealloc(NULL, length);
    for (size_t i = 0; i < 20000; i++) memcpy(buffer + i * piece, sentence, piece);
    memcpy(buffer + 20000 * piece, "needle", 6);

    int64_t found = 0, pieces = 0;
    for (int round = 0; round < 200; round++) {
        const char *text = opaque(buffer);
        const char *at = memmem(text, length, "needle", 6);
        if (at != NULL) found = add_i(found, at - text);
        if (memmem(text, length, "haystack", 8) == NULL) found = add_i(found, 1);

        Ints starts = {0};
        const char *start = text, *end = text + length;
        for (;;) {
            ints_append(&starts, start - text);
            const char *comma = memmem(start, (size_t)(end - start), ", ", 2);
            if (comma == NULL) break;
            start = comma + 2;
        }
        pieces = add_i(pieces, starts.count);
        free(starts.elements);
    }
    printf("%lld %lld\n", (long long)found, (long long)pieces);
    free(buffer);
    return 0;
}
