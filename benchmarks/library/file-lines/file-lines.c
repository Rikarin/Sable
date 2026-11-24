// Port of file-lines.sb, with getline.
#include "../../support/c/checked.h"

int main(void) {
    char *text = NULL;
    size_t length = 0, capacity = 0;
    for (int i = 0; i < 300000; i++) {
        char line[80];
        size_t n = (size_t)snprintf(line, sizeof line, "line %d of the file, with some more text after it\n", i);
        if (length + n > capacity) {
            capacity = capacity < 64 ? 4096 : capacity * 2;
            text = xrealloc(text, capacity);
        }
        memcpy(text + length, line, n);
        length += n;
    }
    FILE *file = fopen("file-lines-c.txt", "w");
    if (file == NULL || fwrite(text, 1, length, file) != length || fclose(file) != 0) trap("cannot write");
    free(text);

    file = fopen("file-lines-c.txt", "r");
    if (file == NULL) trap("cannot read");
    char *line = NULL;
    size_t size = 0;
    ssize_t n;
    int64_t count = 0, characters = 0;
    while ((n = getline(&line, &size, file)) > 0) {
        count = add_i(count, 1);
        characters = add_i(characters, n - 1);
    }
    free(line);
    fclose(file);
    remove("file-lines-c.txt");
    printf("%lld %lld\n", (long long)count, (long long)characters);
    return 0;
}
