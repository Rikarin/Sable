// The Sable runtime: Windows' wide text and the runtime's bytes, which are
// WTF-8 — UTF-8, and a surrogate without its other half as the three bytes
// UTF-8 would have for it — so that a name Windows has that is not well-formed
// UTF-16 comes back to it as it was (Sable.IO's OsString, which converts the
// same way). platform_win32.c uses it; nothing in it is Windows' own.

#ifndef SABLE_RUNTIME_WIDE_H
#define SABLE_RUNTIME_WIDE_H

#include <stddef.h>
#include <stdint.h>

// The scalar or lone surrogate at the start of the bytes, a NUL after them,
// and how many bytes it is; U+FFFD, and one, where there is none.
static uint32_t wtf8_next(const unsigned char *bytes, int *width) {
    uint32_t first = bytes[0];
    *width = 1;
    if (first < 0x80) {
        return first;
    }

    if (first < 0xC2 || first > 0xF4) {
        return 0xFFFD;
    }

    int length = first >= 0xF0 ? 4 : first >= 0xE0 ? 3 : 2;
    uint32_t scalar = first & (0x7Fu >> length);
    for (int i = 1; i < length; i++) {
        if ((bytes[i] & 0xC0) != 0x80) {
            return 0xFFFD; // a NUL is none, so nothing past the end is read
        }

        scalar = scalar << 6 | (bytes[i] & 0x3F);
    }

    if (scalar < (length == 2 ? 0x80u : length == 3 ? 0x800u : 0x10000u) || scalar > 0x10FFFF) {
        return 0xFFFD;
    }

    *width = length;
    return scalar;
}

// The bytes' wide text with its 0, if there is somewhere to put it, and how
// many units it is, the 0 among them.
static size_t wtf8_to_wide(const char *text, uint16_t *wide) {
    const unsigned char *bytes = (const unsigned char *)text;
    size_t units = 0;
    while (*bytes != 0) {
        int width;
        uint32_t scalar = wtf8_next(bytes, &width);
        bytes += width;
        if (scalar >= 0x10000) {
            if (wide != NULL) {
                wide[units] = (uint16_t)(0xD800 + ((scalar - 0x10000) >> 10));
                wide[units + 1] = (uint16_t)(0xDC00 + ((scalar - 0x10000) & 0x3FF));
            }

            units += 2;
        } else {
            if (wide != NULL) {
                wide[units] = (uint16_t)scalar;
            }

            units++;
        }
    }

    if (wide != NULL) {
        wide[units] = 0;
    }

    return units + 1;
}

// And back: how many bytes, the NUL among them.
static size_t wide_to_wtf8(const uint16_t *wide, char *text) {
    size_t length = 0;
    while (*wide != 0) {
        uint32_t scalar = *wide++;
        if (scalar >= 0xD800 && scalar < 0xDC00 && *wide >= 0xDC00 && *wide < 0xE000) {
            scalar = 0x10000 + ((scalar - 0xD800) << 10) + (*wide++ - 0xDC00u);
        }

        int width = scalar < 0x80 ? 1 : scalar < 0x800 ? 2 : scalar < 0x10000 ? 3 : 4;
        if (text != NULL) {
            static const unsigned char leads[] = { 0, 0, 0xC0, 0xE0, 0xF0 };
            for (int i = width - 1; i > 0; i--) {
                text[length + (size_t)i] = (char)(0x80 | (scalar & 0x3F));
                scalar >>= 6;
            }

            text[length] = (char)(leads[width] | scalar);
        }

        length += (size_t)width;
    }

    if (text != NULL) {
        text[length] = '\0';
    }

    return length + 1;
}

#endif
