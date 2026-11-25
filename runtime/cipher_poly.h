// Poly1305 in 2 * L blocks at once, for ciphers.c, which includes this once
// for each L it has registers for, having defined
//
//   NAME(x)    the function's name at this L
//   TARGET     the instructions the function may have
//   L          how many blocks to a register: 2, 4 or 8
//   FACTOR     a limb to multiply by; FACTOR_OF(v) and FACTOR_ALL(word) make one
//   MULTIPLY(a, b), MULTIPLY_ADD(sum, a, b)   the products of the low halves
//   LOAD_BLOCKS(low, high, p)   the halves of the L blocks at 'p'
//   ORDER      which of the blocks each lane of those has
//
// Five limbs of 26 bits, in the low halves of 64-bit lanes: the products of
// two, ten of them added up, are still 64 bits. Each lane takes every Lth
// block of the message, in powers of r^L, and the last step puts the lanes
// together; two steps at a time, by r^2L and r^L.

#define LANES NAME(lanes)

typedef uint64_t LANES __attribute__((vector_size(8 * L)));

// The L blocks at 'p', with the bit after each, added to 'sum's limbs.
#define TAKE(a, sum, p) \
    do { \
        LANES low, high; \
        LOAD_BLOCKS(low, high, p); \
        a[0] = FACTOR_OF(sum[0] + (low & mask)); \
        a[1] = FACTOR_OF(sum[1] + (low >> 26 & mask)); \
        a[2] = FACTOR_OF(sum[2] + ((low >> 52 | high << 12) & mask)); \
        a[3] = FACTOR_OF(sum[3] + (high >> 14 & mask)); \
        a[4] = FACTOR_OF(sum[4] + (high >> 40 | one)); \
    } while (0)

// The limbs of the product of 'a' and 'r', 's' being five times r's: the
// first five products by FIRST, the rest added to them.
#define PRODUCT(d, FIRST, a, r, s) \
    do { \
        d[0] = FIRST(d[0], a[0], r[0]); \
        d[1] = FIRST(d[1], a[0], r[1]); \
        d[2] = FIRST(d[2], a[0], r[2]); \
        d[3] = FIRST(d[3], a[0], r[3]); \
        d[4] = FIRST(d[4], a[0], r[4]); \
        d[0] = MULTIPLY_ADD(d[0], a[1], s[4]); \
        d[1] = MULTIPLY_ADD(d[1], a[1], r[0]); \
        d[2] = MULTIPLY_ADD(d[2], a[1], r[1]); \
        d[3] = MULTIPLY_ADD(d[3], a[1], r[2]); \
        d[4] = MULTIPLY_ADD(d[4], a[1], r[3]); \
        d[0] = MULTIPLY_ADD(d[0], a[2], s[3]); \
        d[1] = MULTIPLY_ADD(d[1], a[2], s[4]); \
        d[2] = MULTIPLY_ADD(d[2], a[2], r[0]); \
        d[3] = MULTIPLY_ADD(d[3], a[2], r[1]); \
        d[4] = MULTIPLY_ADD(d[4], a[2], r[2]); \
        d[0] = MULTIPLY_ADD(d[0], a[3], s[2]); \
        d[1] = MULTIPLY_ADD(d[1], a[3], s[3]); \
        d[2] = MULTIPLY_ADD(d[2], a[3], s[4]); \
        d[3] = MULTIPLY_ADD(d[3], a[3], r[0]); \
        d[4] = MULTIPLY_ADD(d[4], a[3], r[1]); \
        d[0] = MULTIPLY_ADD(d[0], a[4], s[1]); \
        d[1] = MULTIPLY_ADD(d[1], a[4], s[2]); \
        d[2] = MULTIPLY_ADD(d[2], a[4], s[3]); \
        d[3] = MULTIPLY_ADD(d[3], a[4], s[4]); \
        d[4] = MULTIPLY_ADD(d[4], a[4], r[0]); \
    } while (0)

#define FIRST_PRODUCT(unused, a, b) MULTIPLY(a, b)

// What a limb has past its 26 bits carried to the next, the fifth's to the
// first, five times: two chains at once.
#define CARRY(d) \
    do { \
        LANES c, e; \
        c = d[0] >> 26, e = d[3] >> 26, d[0] &= mask, d[3] &= mask, d[1] += c, d[4] += e; \
        c = d[1] >> 26, e = d[4] >> 26, d[1] &= mask, d[4] &= mask, d[2] += c, d[0] += e + (e << 2); \
        c = d[2] >> 26, e = d[0] >> 26, d[2] &= mask, d[0] &= mask, d[3] += c, d[1] += e; \
        e = d[3] >> 26, d[3] &= mask, d[4] += e; \
    } while (0)

// As many whole strides of 2 * L blocks as there are worked into the sum:
// how many bytes that is.
TARGET static int64_t NAME(poly1305)(Poly1305 *poly, const uint8_t *data, int64_t count) {
    static const int order[L] = ORDER;
    int64_t whole = count - count % (32 * L);
    if (whole == 0) {
        return 0;
    }

    poly1305_powers(poly, 2 * L);
    const LANES mask = 0x3FFFFFF - (LANES){0};
    const LANES one = 0x1000000 - (LANES){0};
    const LANES none[5] = {{0}};
    FACTOR a[5], first[5], first5[5], second[5], second5[5];
    LANES sum[5] = {{0}}, d[5];
    for (int i = 0; i < 5; i++) {
        first[i] = FACTOR_ALL(poly->power[L - 1][i]);
        first5[i] = FACTOR_ALL(5 * poly->power[L - 1][i]);
        second[i] = FACTOR_ALL(poly->power[2 * L - 1][i]);
        second5[i] = FACTOR_ALL(5 * poly->power[2 * L - 1][i]);
    }

    // What has been summed already goes on with the first block.
    uint32_t limbs[5];
    poly1305_limbs(limbs, poly->h);
    for (int i = 0; i < 5; i++) {
        sum[i][0] = limbs[i];
    }

    int64_t left = whole;
    for (; left > 32 * L; left -= 32 * L, data += 32 * L) {
        TAKE(a, sum, data);
        PRODUCT(d, FIRST_PRODUCT, a, second, second5);
        TAKE(a, none, data + 16 * L);
        PRODUCT(d, MULTIPLY_ADD, a, first, first5);
        CARRY(d);
        SABLE_UNROLL
        for (int i = 0; i < 5; i++) {
            sum[i] = d[i];
        }
    }

    // The last stride, its second half by the power each lane is short of.
    TAKE(a, sum, data);
    PRODUCT(d, FIRST_PRODUCT, a, first, first5);
    CARRY(d);
    TAKE(a, d, data + 16 * L);
    for (int i = 0; i < 5; i++) {
        LANES power = {0};
        for (int lane = 0; lane < L; lane++) {
            power[lane] = poly->power[L - 1 - order[lane]][i];
        }

        first[i] = FACTOR_OF(power);
        first5[i] = FACTOR_OF(power + (power << 2));
    }

    PRODUCT(d, FIRST_PRODUCT, a, first, first5);
    CARRY(d);
    uint64_t total[5];
    for (int i = 0; i < 5; i++) {
        total[i] = 0;
        for (int lane = 0; lane < L; lane++) {
            total[i] += d[i][lane];
        }
    }

    poly1305_set(poly, total);
    return whole;
}

#undef LANES
#undef TAKE
#undef PRODUCT
#undef FIRST_PRODUCT
#undef CARRY
#undef NAME
#undef TARGET
#undef L
#undef FACTOR
#undef FACTOR_OF
#undef FACTOR_ALL
#undef MULTIPLY
#undef MULTIPLY_ADD
#undef LOAD_BLOCKS
#undef ORDER
