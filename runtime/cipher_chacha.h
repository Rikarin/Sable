// ChaCha20 in N blocks at once, for ciphers.c, which includes this once for
// each N it has registers for, having defined
//
//   NAME(x)   the function's name at this N
//   TARGET    the instructions the function may have
//   N         4, 8 or 16
//
// 'state' is the sixteen words of the first block; its counter is the 13th.

#define WORDS NAME(words)
#define ROTATED(x, n) ((x) << (n) | (x) >> (32 - (n)))
#define QUARTER(a, b, c, d) \
    do { \
        a += b, d = ROTATED(d ^ a, 16); \
        c += d, b = ROTATED(b ^ c, 12); \
        a += b, d = ROTATED(d ^ a, 8); \
        c += d, b = ROTATED(b ^ c, 7); \
    } while (0)

typedef uint32_t WORDS __attribute__((vector_size(4 * N)));

// As many whole strides of 64 * N bytes as there are: how many bytes that is.
TARGET static int64_t NAME(chacha)(uint32_t *state, const uint8_t *in, uint8_t *out, int64_t count) {
    int64_t done = 0;
    WORDS steps;
    for (int i = 0; i < N; i++) {
        steps[i] = (uint32_t)i;
    }

    for (; count - done >= 64 * N; done += 64 * N, in += 64 * N, out += 64 * N) {
        WORDS first[16], x[16];
        SABLE_UNROLL
        for (int i = 0; i < 16; i++) {
            first[i] = state[i] - (WORDS){0};
        }

        first[12] += steps;
        SABLE_UNROLL
        for (int i = 0; i < 16; i++) {
            x[i] = first[i];
        }

        for (int round = 0; round < 10; round++) {
            SABLE_UNROLL
            for (int i = 0; i < 4; i++) {
                QUARTER(x[i], x[4 + i], x[8 + i], x[12 + i]);
            }

            SABLE_UNROLL
            for (int i = 0; i < 4; i++) {
                QUARTER(x[i], x[4 + ((i + 1) & 3)], x[8 + ((i + 2) & 3)], x[12 + ((i + 3) & 3)]);
            }
        }

        // Each four words of each block together: in the ith of a quarter of
        // 'x' those of the blocks i, i + 4, i + 8 and so on.
        SABLE_UNROLL
        for (int i = 0; i < 16; i += 4) {
            WORDS a = x[i] + first[i], b = x[i + 1] + first[i + 1], c = x[i + 2] + first[i + 2], d = x[i + 3] + first[i + 3];
#if N == 4
            WORDS ab = __builtin_shufflevector(a, b, 0, 4, 1, 5), ba = __builtin_shufflevector(a, b, 2, 6, 3, 7);
            WORDS cd = __builtin_shufflevector(c, d, 0, 4, 1, 5), dc = __builtin_shufflevector(c, d, 2, 6, 3, 7);
            x[i] = __builtin_shufflevector(ab, cd, 0, 1, 4, 5);
            x[i + 1] = __builtin_shufflevector(ab, cd, 2, 3, 6, 7);
            x[i + 2] = __builtin_shufflevector(ba, dc, 0, 1, 4, 5);
            x[i + 3] = __builtin_shufflevector(ba, dc, 2, 3, 6, 7);
#elif N == 8
            WORDS ab = __builtin_shufflevector(a, b, 0, 8, 1, 9, 4, 12, 5, 13), ba = __builtin_shufflevector(a, b, 2, 10, 3, 11, 6, 14, 7, 15);
            WORDS cd = __builtin_shufflevector(c, d, 0, 8, 1, 9, 4, 12, 5, 13), dc = __builtin_shufflevector(c, d, 2, 10, 3, 11, 6, 14, 7, 15);
            x[i] = __builtin_shufflevector(ab, cd, 0, 1, 8, 9, 4, 5, 12, 13);
            x[i + 1] = __builtin_shufflevector(ab, cd, 2, 3, 10, 11, 6, 7, 14, 15);
            x[i + 2] = __builtin_shufflevector(ba, dc, 0, 1, 8, 9, 4, 5, 12, 13);
            x[i + 3] = __builtin_shufflevector(ba, dc, 2, 3, 10, 11, 6, 7, 14, 15);
#else
            WORDS ab = __builtin_shufflevector(a, b, 0, 16, 1, 17, 4, 20, 5, 21, 8, 24, 9, 25, 12, 28, 13, 29);
            WORDS ba = __builtin_shufflevector(a, b, 2, 18, 3, 19, 6, 22, 7, 23, 10, 26, 11, 27, 14, 30, 15, 31);
            WORDS cd = __builtin_shufflevector(c, d, 0, 16, 1, 17, 4, 20, 5, 21, 8, 24, 9, 25, 12, 28, 13, 29);
            WORDS dc = __builtin_shufflevector(c, d, 2, 18, 3, 19, 6, 22, 7, 23, 10, 26, 11, 27, 14, 30, 15, 31);
            x[i] = __builtin_shufflevector(ab, cd, 0, 1, 16, 17, 4, 5, 20, 21, 8, 9, 24, 25, 12, 13, 28, 29);
            x[i + 1] = __builtin_shufflevector(ab, cd, 2, 3, 18, 19, 6, 7, 22, 23, 10, 11, 26, 27, 14, 15, 30, 31);
            x[i + 2] = __builtin_shufflevector(ba, dc, 0, 1, 16, 17, 4, 5, 20, 21, 8, 9, 24, 25, 12, 13, 28, 29);
            x[i + 3] = __builtin_shufflevector(ba, dc, 2, 3, 18, 19, 6, 7, 22, 23, 10, 11, 26, 27, 14, 15, 30, 31);
#endif
        }

        SABLE_UNROLL
        for (int i = 0; i < 4; i++) {
            WORDS a = x[i], b = x[i + 4], c = x[i + 8], d = x[i + 12], stream[4];
#if N == 4
            stream[0] = a, stream[1] = b, stream[2] = c, stream[3] = d;
#elif N == 8
            stream[0] = __builtin_shufflevector(a, b, 0, 1, 2, 3, 8, 9, 10, 11);
            stream[1] = __builtin_shufflevector(c, d, 0, 1, 2, 3, 8, 9, 10, 11);
            stream[2] = __builtin_shufflevector(a, b, 4, 5, 6, 7, 12, 13, 14, 15);
            stream[3] = __builtin_shufflevector(c, d, 4, 5, 6, 7, 12, 13, 14, 15);
#else
            WORDS ab = __builtin_shufflevector(a, b, 0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20, 21, 22, 23);
            WORDS ba = __builtin_shufflevector(a, b, 8, 9, 10, 11, 12, 13, 14, 15, 24, 25, 26, 27, 28, 29, 30, 31);
            WORDS cd = __builtin_shufflevector(c, d, 0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20, 21, 22, 23);
            WORDS dc = __builtin_shufflevector(c, d, 8, 9, 10, 11, 12, 13, 14, 15, 24, 25, 26, 27, 28, 29, 30, 31);
            stream[0] = __builtin_shufflevector(ab, cd, 0, 1, 2, 3, 8, 9, 10, 11, 16, 17, 18, 19, 24, 25, 26, 27);
            stream[1] = __builtin_shufflevector(ab, cd, 4, 5, 6, 7, 12, 13, 14, 15, 20, 21, 22, 23, 28, 29, 30, 31);
            stream[2] = __builtin_shufflevector(ba, dc, 0, 1, 2, 3, 8, 9, 10, 11, 16, 17, 18, 19, 24, 25, 26, 27);
            stream[3] = __builtin_shufflevector(ba, dc, 4, 5, 6, 7, 12, 13, 14, 15, 20, 21, 22, 23, 28, 29, 30, 31);
#endif

            // Where a register's worth of the stream goes: at 4 a quarter of
            // block i, at 8 a half of it or of block i + 4, at 16 the whole
            // of block i, i + 4, i + 8 or i + 12.
            SABLE_UNROLL
            for (int part = 0; part < 4; part++) {
                int at = 64 * i + (N == 4 ? 16 * part : N == 8 ? 32 * (part & 1) + 256 * (part >> 1) : 256 * part);
                WORDS text;
                memcpy(&text, in + at, sizeof text);
                text ^= stream[part];
                memcpy(out + at, &text, sizeof text);
            }
        }

        state[12] += N;
    }

    return done;
}

#undef WORDS
#undef ROTATED
#undef QUARTER
#undef NAME
#undef TARGET
#undef N
