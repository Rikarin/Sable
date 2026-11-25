// AES in many blocks at once, for ciphers.c, which includes this once for
// each width of register it has: CTR, and CTR with GHASH over the ciphertext.
// Before each, it defines
//
//   NAME(x)         the function's name at this width
//   TARGET          the instructions the function may have
//   V, W            a register, and how many blocks it holds
//   V_LOAD, V_STORE, V_XOR, V_KEY(p), V_START, V_ROUND, V_LAST
//   V_COUNTER(p)    the counter block at 'p' and the ones after it, to add to
//   V_COUNT(v, n)   the same 'n' blocks on
//   V_BLOCKS(v)     those as the cipher's input
//   V_REVERSE       the bytes of each block in the other order
//   V_LOW, V_MIDDLE, V_HIGH   the parts of each block's carryless product
//   V_FOLD          the blocks of a register added up, as one block
//
// A stride is eight registers. The counter is the block's last four bytes,
// big-endian, and wraps in them; 'sum' is GHASH's, its bytes in the other
// order, as 'powers' are, where H^n is the (32 - n)th.

TARGET static inline __attribute__((always_inline)) int64_t NAME(strides)(int mode, const uint8_t *keys, int rounds, const uint8_t *powers, uint8_t *counter, Block *sum, const uint8_t *in, uint8_t *out, int64_t count) {
    if (count < 8 * 16 * W) {
        return 0;
    }

    int64_t done = 0;
    uint32_t number = load_big32(counter + 12);
    V next = V_COUNTER(counter);
    Block total = mode == CTR_ONLY ? block_zero() : *sum;
    const uint8_t *h = powers + 16 * (32 - 8 * W);
    for (; count - done >= 8 * 16 * W; done += 8 * 16 * W, in += 8 * 16 * W, out += 8 * 16 * W) {
        V b[8], text[8];
        SABLE_UNROLL
        for (int i = 0; i < 8; i++) {
            b[i] = V_START(V_BLOCKS(V_COUNT(next, i * W)), V_KEY(keys));
        }

        next = V_COUNT(next, 8 * W);
        for (int round = 1; round < rounds; round++) {
            V key = V_KEY(keys + 16 * (round - SABLE_ROUND_BEFORE));
            SABLE_UNROLL
            for (int i = 0; i < 8; i++) {
                b[i] = V_ROUND(b[i], key);
            }
        }

        V last = V_KEY(keys + 16 * rounds);
        V before = V_KEY(keys + 16 * (rounds - 1));
        (void)before;
        SABLE_UNROLL
        for (int i = 0; i < 8; i++) {
            V read = V_LOAD(in + 16 * W * i);
            V written = V_XOR(V_LAST(b[i], before, last), read);
            V_STORE(out + 16 * W * i, written);
            text[i] = mode == CTR_OPEN ? read : written;
        }

        if (mode != CTR_ONLY) {
            V x = V_XOR(V_REVERSE(text[0]), V_WIDEN(total));
            V power = V_LOAD(h);
            V low = V_LOW(x, power), middle = V_MIDDLE(x, power), high = V_HIGH(x, power);
            SABLE_UNROLL
            for (int i = 1; i < 8; i++) {
                x = V_REVERSE(text[i]);
                power = V_LOAD(h + 16 * W * i);
                low = V_XOR(low, V_LOW(x, power));
                middle = V_XOR(middle, V_MIDDLE(x, power));
                high = V_XOR(high, V_HIGH(x, power));
            }

            total = ghash_reduce(V_FOLD(low), V_FOLD(middle), V_FOLD(high));
        }
    }

    store_big32(counter + 12, number + (uint32_t)(done / 16));
    if (mode != CTR_ONLY) {
        *sum = total;
    }

    return done;
}

TARGET static int64_t NAME(ctr)(const uint8_t *keys, int rounds, uint8_t *counter, const uint8_t *in, uint8_t *out, int64_t count) {
    return NAME(strides)(CTR_ONLY, keys, rounds, keys, counter, NULL, in, out, count);
}

TARGET static int64_t NAME(seal)(const uint8_t *keys, int rounds, const uint8_t *powers, uint8_t *counter, Block *sum, const uint8_t *in, uint8_t *out, int64_t count) {
    return NAME(strides)(CTR_SEAL, keys, rounds, powers, counter, sum, in, out, count);
}

TARGET static int64_t NAME(open)(const uint8_t *keys, int rounds, const uint8_t *powers, uint8_t *counter, Block *sum, const uint8_t *in, uint8_t *out, int64_t count) {
    return NAME(strides)(CTR_OPEN, keys, rounds, powers, counter, sum, in, out, count);
}

#undef NAME
#undef TARGET
#undef V
#undef W
#undef V_LOAD
#undef V_STORE
#undef V_XOR
#undef V_KEY
#undef V_START
#undef V_ROUND
#undef V_LAST
#undef V_COUNTER
#undef V_COUNT
#undef V_BLOCKS
#undef V_REVERSE
#undef V_LOW
#undef V_MIDDLE
#undef V_HIGH
#undef V_FOLD
#undef V_WIDEN
