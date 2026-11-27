// The Sable runtime: symbols — a symbol of the program read back as the SIL
// name it was made from, so that a backtrace names a frame as the program
// names the function.
// Part of sable_runtime.c, which includes every part in order (see there).
//
// The compiler shortens a symbol by substitutions, as Swift's mangling does
// (src/Sable.CodeGen/SymbolNames.cs, docs/05 §5.4 "Symbols"): a chain of
// dotted names is written part by part, where a run of parts may be '~' and
// a letter for a module of the standard library or a word names are full of
// (the tables below, which a test holds to the compiler's), or '~', a number
// and '_' for a prefix of a chain the name has had before. Each chain's
// prefixes of four characters or more that are not in the tables are
// numbered as the chain ends. A '~' of the name itself is '~~'.

// The substitutions by the character after '~': lower case the modules, upper
// case the words.
static const char *const symbol_modules[26] = {
    "Sable.Core", "Sable.Collections", "Sable.Concurrency", "Sable.Text", "Sable.IO",
    "Sable.Time", "Sable.Math", "Sable.Net", "Sable.Process", "Sable.Serialize",
    "Sable.Testing", "Sable.Crypto", "Sable.Globalization", "Sable.Diagnostics", "Sable.Hosting",
    "Sable.Interop", "Sable.Validation", "Sable.Build", "Sable", "Sable.DependencyInjection",
    "Sable.Generators", NULL, NULL, NULL, NULL, NULL,
};

static const char *const symbol_words[26] = {
    "witnesses", "generic", "witness", "opaque", "template", "conformances",
    "associated", "associated_conformances", "cache", "shape", "Element", "String",
    "Int", "debugDescription", "Bool", "thick", "throws", "Error",
    "destroy", "visit", "reabstract", "convert", "describe", "closure",
    "Self", "accessors",
};

static bool symbol_start(char c) {
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
}

static bool symbol_part(char c) {
    return symbol_start(c) || (c >= '0' && c <= '9');
}

// The prefixes numbered so far: where each starts in the text read back, and
// how long it is (each is a prefix of a chain written out, so it stays
// there). As many as a backtrace's line can hold.
enum { SYMBOL_SUBSTITUTIONS = 256 };

typedef struct {
    int count;
    uint16_t start[SYMBOL_SUBSTITUTIONS];
    uint16_t length[SYMBOL_SUBSTITUTIONS];
} SymbolTable;

// What a chain's part at 'at' stands for, and where it ends: its text (in
// 'out', from 'start' for 'length') or NULL where none starts there.
static const char *symbol_component(const char *symbol, size_t at, const SymbolTable *table, const char *out, size_t *length, size_t *end) {
    char c = symbol[at];
    if (symbol_start(c)) {
        size_t i = at;
        while (symbol_part(symbol[i])) {
            i++;
        }

        *length = i - at;
        *end = i;
        return symbol + at;
    }

    if (c != '~') {
        return NULL;
    }

    char code = symbol[at + 1];
    const char *fixed = code >= 'a' && code <= 'z' ? symbol_modules[code - 'a'] : code >= 'A' && code <= 'Z' ? symbol_words[code - 'A'] : NULL;
    if (fixed != NULL) {
        *length = strlen(fixed);
        *end = at + 2;
        return fixed;
    }

    size_t i = at + 1;
    long index = 0;
    while (symbol[i] >= '0' && symbol[i] <= '9' && index < SYMBOL_SUBSTITUTIONS) {
        index = index * 10 + (symbol[i] - '0');
        i++;
    }

    if (i == at + 1 || symbol[i] != '_' || index >= table->count) {
        return NULL;
    }

    *length = table->length[index];
    *end = i + 1;
    return out + table->start[index];
}

// Numbers the prefixes of the chain at text[start, end), as the compiler
// does once a chain ends.
static void symbol_remember(SymbolTable *table, const char *text, size_t start, size_t end) {
    for (size_t i = start; i <= end; i++) {
        if (i != end && text[i] != '.') {
            continue;
        }

        size_t length = i - start;
        if (length < 4 || length > UINT16_MAX || start > UINT16_MAX || table->count >= SYMBOL_SUBSTITUTIONS) {
            continue;
        }

        bool fixed = false, seen = false;
        for (int k = 0; k < 26 && !fixed; k++) {
            fixed = (symbol_modules[k] != NULL && strlen(symbol_modules[k]) == length && memcmp(symbol_modules[k], text + start, length) == 0)
                || (strlen(symbol_words[k]) == length && memcmp(symbol_words[k], text + start, length) == 0);
        }

        for (int k = 0; k < table->count && !seen; k++) {
            seen = table->length[k] == length && memcmp(text + table->start[k], text + start, length) == 0;
        }

        if (!fixed && !seen) {
            table->start[table->count] = (uint16_t)start;
            table->length[table->count] = (uint16_t)length;
            table->count++;
        }
    }
}

// The SIL name of 'symbol' (one starting 'sable.'), in 'out' (of 'size'
// bytes), cut short where it does not fit; 'symbol' as it is where it has
// nothing substituted.
static const char *symbol_demangle(const char *symbol, char *out, size_t size) {
    if (symbol == NULL || strncmp(symbol, "sable.", 6) != 0 || strchr(symbol, '~') == NULL || size == 0) {
        return symbol;
    }

    SymbolTable local = { 0 };
    size_t written = 0, at = 0;
#define SYMBOL_PUT(c) do { if (written + 1 < size) { out[written++] = (c); } } while (0)
    // The prefix is no part of the name: nothing is numbered for it.
    if (strncmp(symbol, "sable.", 6) == 0) {
        for (; at < 6; at++) {
            SYMBOL_PUT(symbol[at]);
        }
    }

    while (symbol[at] != '\0') {
        if (symbol[at] == '~' && symbol[at + 1] == '~') {
            SYMBOL_PUT('~');
            at += 2;
            continue;
        }

        size_t length, end;
        const char *text = symbol_component(symbol, at, &local, out, &length, &end);
        if (text == NULL) {
            SYMBOL_PUT(symbol[at]);
            at++;
            continue;
        }

        size_t chain = written;
        for (;;) {
            // A back-reference's text is earlier in 'out' itself: copied a byte at a time.
            for (size_t i = 0; i < length; i++) {
                SYMBOL_PUT(text[i]);
            }

            at = end;
            if (symbol[at] != '.' || (text = symbol_component(symbol, at + 1, &local, out, &length, &end)) == NULL) {
                break;
            }

            SYMBOL_PUT('.');
        }

        symbol_remember(&local, out, chain, written);
    }
#undef SYMBOL_PUT

    out[written] = '\0';
    return out;
}
