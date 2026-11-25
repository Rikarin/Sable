// The ciphers the processor has instructions for: AES and its modes, by
// AES-NI, VAES or ARMv8's, GHASH by carryless multiplication, and ChaCha20
// and Poly1305 in whatever vectors there are.
//
// As with the hashes (crypto.c), the portable code is Sable.Crypto's, each
// kernel does exactly what it does, and none branches or indexes on the key
// or the data: only on their lengths.

#if defined(SABLE_CRYPTO_ARM64) || defined(SABLE_CRYPTO_X64)

// What 'kind' names. Sable.Crypto's Cipher has the same.
enum {
    SABLE_CIPHER_AES_KEY = 1,       // the round keys of the key that is the input, made at 'key'
    SABLE_CIPHER_GCM_KEY = 2,       // GHASH's powers, made of the round keys
    SABLE_CIPHER_ECB_ENCRYPT = 3,   // whole blocks
    SABLE_CIPHER_ECB_DECRYPT = 4,
    SABLE_CIPHER_CBC_ENCRYPT = 5,   // whole blocks; 'work' is the block before, then the last one
    SABLE_CIPHER_CBC_DECRYPT = 6,
    SABLE_CIPHER_CTR = 7,           // 'work' is the first counter block, of 128 bits, then the next one
    SABLE_CIPHER_CFB_ENCRYPT = 8,   // 'work' is the block before
    SABLE_CIPHER_CFB_DECRYPT = 9,
    SABLE_CIPHER_CFB8_ENCRYPT = 10,
    SABLE_CIPHER_CFB8_DECRYPT = 11,
    SABLE_CIPHER_GCM_SEAL = 12,     // 'work' is J0; the tag is made 16 bytes on
    SABLE_CIPHER_GCM_OPEN = 13,
    SABLE_CIPHER_GHASH = 14,        // the same, the input only hashed and the tag not encrypted
    SABLE_CIPHER_CCM_SEAL = 15,     // 'work' is B0; the tag is made 16 bytes on
    SABLE_CIPHER_CCM_OPEN = 16,
    SABLE_CIPHER_POLY_SEAL = 17,    // 'work' is the nonce's 12 bytes; the tag is made 16 bytes on
    SABLE_CIPHER_POLY_OPEN = 18,
    SABLE_CIPHER_CHACHA = 19,       // 'work' is the first block's number, 4 bytes, and the nonce's 12
};

// What the memory at 'key' has. Sable.Crypto's AesKey leaves 1024 bytes for it.
typedef struct {
    int64_t rounds;
    int64_t unused;
    uint8_t encrypt[240];
    uint8_t decrypt[240];      // the inverse cipher's, in its order
    uint8_t powers[16 * 32];   // H^32 to H^1, for GCM
} AesKey;

void sable_secure_zero(void *bytes, int64_t count);

static inline uint32_t load_big32(const uint8_t *bytes) {
    uint32_t word;
    memcpy(&word, bytes, 4);
    return __builtin_bswap32(word);
}

static inline void store_big32(uint8_t *bytes, uint32_t word) {
    word = __builtin_bswap32(word);
    memcpy(bytes, &word, 4);
}

static inline void store_big64(uint8_t *bytes, uint64_t word) {
    word = __builtin_bswap64(word);
    memcpy(bytes, &word, 8);
}

static inline uint64_t load_big64(const uint8_t *bytes) {
    uint64_t word;
    memcpy(&word, bytes, 8);
    return __builtin_bswap64(word);
}

#define SABLE_INLINE static inline __attribute__((always_inline))

// ---- A block: what each processor has -------------------------------------------

#if defined(SABLE_CRYPTO_ARM64)

#if defined(__clang__)
#define SABLE_TARGET_AES __attribute__((target("aes")))
#else
#define SABLE_TARGET_AES __attribute__((target("+crypto")))
#endif

typedef uint8x16_t Block;

SABLE_TARGET_AES SABLE_INLINE Block block_load(const uint8_t *p) { return vld1q_u8(p); }
SABLE_TARGET_AES SABLE_INLINE void block_store(uint8_t *p, Block b) { vst1q_u8(p, b); }
SABLE_TARGET_AES SABLE_INLINE Block block_xor(Block a, Block b) { return veorq_u8(a, b); }
SABLE_TARGET_AES SABLE_INLINE Block block_zero(void) { return vdupq_n_u8(0); }

// AESE adds the key first, so a round has the key the one before does on x86.
#define SABLE_ROUND_BEFORE 1
SABLE_TARGET_AES SABLE_INLINE Block block_start(Block b, Block key) { (void)key; return b; }
SABLE_TARGET_AES SABLE_INLINE Block block_round(Block b, Block before) { return vaesmcq_u8(vaeseq_u8(b, before)); }
SABLE_TARGET_AES SABLE_INLINE Block block_last(Block b, Block before, Block last) { return veorq_u8(vaeseq_u8(b, before), last); }
SABLE_TARGET_AES SABLE_INLINE Block block_unround(Block b, Block before) { return vaesimcq_u8(vaesdq_u8(b, before)); }
SABLE_TARGET_AES SABLE_INLINE Block block_unlast(Block b, Block before, Block last) { return veorq_u8(vaesdq_u8(b, before), last); }
SABLE_TARGET_AES SABLE_INLINE Block block_unmix(Block key) { return vaesimcq_u8(key); }

// The S-box of each byte of a word.
SABLE_TARGET_AES SABLE_INLINE uint32_t block_substitute(uint32_t word) {
    // The rows shifted are of four columns that are the same.
    return vgetq_lane_u32(vreinterpretq_u32_u8(vaeseq_u8(vreinterpretq_u8_u32(vdupq_n_u32(word)), vdupq_n_u8(0))), 0);
}

SABLE_TARGET_AES SABLE_INLINE Block block_reverse(Block b) {
    Block halves = vrev64q_u8(b);
    return vextq_u8(halves, halves, 8);
}

SABLE_TARGET_AES SABLE_INLINE Block block_multiply(Block a, int upperOfA, Block b, int upperOfB) {
    poly64x2_t x = vreinterpretq_p64_u8(a), y = vreinterpretq_p64_u8(b);
    return vreinterpretq_u8_p128(vmull_p64(
        upperOfA ? vgetq_lane_p64(x, 1) : vgetq_lane_p64(x, 0),
        upperOfB ? vgetq_lane_p64(y, 1) : vgetq_lane_p64(y, 0)
    ));
}

SABLE_TARGET_AES SABLE_INLINE Block block_low(Block a, Block b) { return block_multiply(a, 0, b, 0); }
SABLE_TARGET_AES SABLE_INLINE Block block_high(Block a, Block b) {
    return vreinterpretq_u8_p128(vmull_high_p64(vreinterpretq_p64_u8(a), vreinterpretq_p64_u8(b)));
}
SABLE_TARGET_AES SABLE_INLINE Block block_middle(Block a, Block b) {
    Block swapped = vextq_u8(b, b, 8);
    return veorq_u8(block_low(a, swapped), block_high(a, swapped));
}

// The halves of one swapped, and one's half moved to the other with zeros.
SABLE_TARGET_AES SABLE_INLINE Block block_swap(Block b) { return vextq_u8(b, b, 8); }
SABLE_TARGET_AES SABLE_INLINE Block block_up(Block b) { return vextq_u8(vdupq_n_u8(0), b, 8); }
SABLE_TARGET_AES SABLE_INLINE Block block_down(Block b) { return vextq_u8(b, vdupq_n_u8(0), 8); }
SABLE_TARGET_AES SABLE_INLINE Block block_of(uint64_t low, uint64_t high) {
    return vreinterpretq_u8_u64(vcombine_u64(vcreate_u64(low), vcreate_u64(high)));
}

// Counter blocks, their last word kept so that it can be added to.
SABLE_TARGET_AES SABLE_INLINE Block counter_load(const uint8_t *p) { return vrev32q_u8(vld1q_u8(p)); }
SABLE_TARGET_AES SABLE_INLINE Block counter_add(Block counter, uint32_t n) {
    uint32x4_t by = {0, 0, 0, n};
    return vreinterpretq_u8_u32(vaddq_u32(vreinterpretq_u32_u8(counter), by));
}
SABLE_TARGET_AES SABLE_INLINE Block counter_block(Block counter) { return vrev32q_u8(counter); }

#else

#define SABLE_TARGET_AES __attribute__((target("aes,pclmul,ssse3,sse4.1")))

typedef __m128i Block;

SABLE_TARGET_AES SABLE_INLINE Block block_load(const uint8_t *p) { return _mm_loadu_si128((const __m128i *)p); }
SABLE_TARGET_AES SABLE_INLINE void block_store(uint8_t *p, Block b) { _mm_storeu_si128((__m128i *)p, b); }
SABLE_TARGET_AES SABLE_INLINE Block block_xor(Block a, Block b) { return _mm_xor_si128(a, b); }
SABLE_TARGET_AES SABLE_INLINE Block block_zero(void) { return _mm_setzero_si128(); }

#define SABLE_ROUND_BEFORE 0
SABLE_TARGET_AES SABLE_INLINE Block block_start(Block b, Block key) { return _mm_xor_si128(b, key); }
SABLE_TARGET_AES SABLE_INLINE Block block_round(Block b, Block key) { return _mm_aesenc_si128(b, key); }
SABLE_TARGET_AES SABLE_INLINE Block block_last(Block b, Block before, Block last) { (void)before; return _mm_aesenclast_si128(b, last); }
SABLE_TARGET_AES SABLE_INLINE Block block_unround(Block b, Block key) { return _mm_aesdec_si128(b, key); }
SABLE_TARGET_AES SABLE_INLINE Block block_unlast(Block b, Block before, Block last) { (void)before; return _mm_aesdeclast_si128(b, last); }
SABLE_TARGET_AES SABLE_INLINE Block block_unmix(Block key) { return _mm_aesimc_si128(key); }

// The S-box of each byte of a word.
SABLE_TARGET_AES SABLE_INLINE uint32_t block_substitute(uint32_t word) {
    // The rows shifted are of four columns that are the same.
    return (uint32_t)_mm_cvtsi128_si32(_mm_aesenclast_si128(_mm_set1_epi32((int)word), _mm_setzero_si128()));
}

SABLE_TARGET_AES SABLE_INLINE Block block_reverse(Block b) {
    return _mm_shuffle_epi8(b, _mm_set_epi64x(0x0001020304050607ll, 0x08090a0b0c0d0e0fll));
}

SABLE_TARGET_AES SABLE_INLINE Block block_low(Block a, Block b) { return _mm_clmulepi64_si128(a, b, 0x00); }
SABLE_TARGET_AES SABLE_INLINE Block block_high(Block a, Block b) { return _mm_clmulepi64_si128(a, b, 0x11); }
SABLE_TARGET_AES SABLE_INLINE Block block_middle(Block a, Block b) {
    return _mm_xor_si128(_mm_clmulepi64_si128(a, b, 0x01), _mm_clmulepi64_si128(a, b, 0x10));
}

// The halves of one swapped, and one's half moved to the other with zeros.
SABLE_TARGET_AES SABLE_INLINE Block block_swap(Block b) { return _mm_shuffle_epi32(b, 0x4E); }
SABLE_TARGET_AES SABLE_INLINE Block block_up(Block b) { return _mm_slli_si128(b, 8); }
SABLE_TARGET_AES SABLE_INLINE Block block_down(Block b) { return _mm_srli_si128(b, 8); }
SABLE_TARGET_AES SABLE_INLINE Block block_of(uint64_t low, uint64_t high) { return _mm_set_epi64x((int64_t)high, (int64_t)low); }

// Counter blocks, the whole of them reversed, so that their last word can be added to.
SABLE_TARGET_AES SABLE_INLINE Block counter_load(const uint8_t *p) { return block_reverse(block_load(p)); }
SABLE_TARGET_AES SABLE_INLINE Block counter_add(Block counter, uint32_t n) { return _mm_add_epi32(counter, _mm_set_epi32(0, 0, 0, (int)n)); }
SABLE_TARGET_AES SABLE_INLINE Block counter_block(Block counter) { return block_reverse(counter); }

#endif

// ---- AES --------------------------------------------------------------------------

// FIPS 197's key expansion, and the inverse cipher's keys of it.
SABLE_TARGET_AES static void aes_key(AesKey *key, const uint8_t *bytes, int64_t count) {
    int words = (int)count / 4, rounds = words + 6;
    uint32_t w[60];
    memcpy(w, bytes, (size_t)count);
    uint32_t constant = 1;
    for (int i = words; i < 4 * (rounds + 1); i++) {
        uint32_t word = w[i - 1];
        if (i % words == 0) {
            word = block_substitute(word >> 8 | word << 24) ^ constant;
            constant = constant << 1 ^ (constant >> 7) * 0x11B;
        } else if (words > 6 && i % words == 4) {
            word = block_substitute(word);
        }

        w[i] = w[i - words] ^ word;
    }

    key->rounds = rounds;
    memcpy(key->encrypt, w, 16 * (size_t)(rounds + 1));
    for (int i = 0; i <= rounds; i++) {
        Block round = block_load(key->encrypt + 16 * (rounds - i));
        block_store(key->decrypt + 16 * i, i == 0 || i == rounds ? round : block_unmix(round));
    }

    sable_secure_zero(w, sizeof w);
}

// Blocks put through the cipher, or the inverse one, side by side.
SABLE_TARGET_AES SABLE_INLINE void aes_encrypt(const uint8_t *keys, int rounds, Block *b, int count) {
    SABLE_UNROLL
    for (int i = 0; i < count; i++) {
        b[i] = block_start(b[i], block_load(keys));
    }

    for (int round = 1; round < rounds; round++) {
        Block key = block_load(keys + 16 * (round - SABLE_ROUND_BEFORE));
        SABLE_UNROLL
        for (int i = 0; i < count; i++) {
            b[i] = block_round(b[i], key);
        }
    }

    Block before = block_load(keys + 16 * (rounds - 1)), last = block_load(keys + 16 * rounds);
    SABLE_UNROLL
    for (int i = 0; i < count; i++) {
        b[i] = block_last(b[i], before, last);
    }
}

SABLE_TARGET_AES SABLE_INLINE void aes_decrypt(const uint8_t *keys, int rounds, Block *b, int count) {
    SABLE_UNROLL
    for (int i = 0; i < count; i++) {
        b[i] = block_start(b[i], block_load(keys));
    }

    for (int round = 1; round < rounds; round++) {
        Block key = block_load(keys + 16 * (round - SABLE_ROUND_BEFORE));
        SABLE_UNROLL
        for (int i = 0; i < count; i++) {
            b[i] = block_unround(b[i], key);
        }
    }

    Block before = block_load(keys + 16 * (rounds - 1)), last = block_load(keys + 16 * rounds);
    SABLE_UNROLL
    for (int i = 0; i < count; i++) {
        b[i] = block_unlast(b[i], before, last);
    }
}

SABLE_TARGET_AES static Block aes_one(const uint8_t *keys, int rounds, Block b) {
    aes_encrypt(keys, rounds, &b, 1);
    return b;
}

SABLE_TARGET_AES static void aes_ecb(const AesKey *key, bool decrypt, const uint8_t *in, uint8_t *out, int64_t count) {
    int rounds = (int)key->rounds;
    const uint8_t *keys = decrypt ? key->decrypt : key->encrypt;
    Block b[8];
    while (count >= 128) {
        SABLE_UNROLL
        for (int i = 0; i < 8; i++) {
            b[i] = block_load(in + 16 * i);
        }

        if (decrypt) {
            aes_decrypt(keys, rounds, b, 8);
        } else {
            aes_encrypt(keys, rounds, b, 8);
        }

        SABLE_UNROLL
        for (int i = 0; i < 8; i++) {
            block_store(out + 16 * i, b[i]);
        }

        in += 128, out += 128, count -= 128;
    }

    for (; count >= 16; in += 16, out += 16, count -= 16) {
        b[0] = block_load(in);
        if (decrypt) {
            aes_decrypt(keys, rounds, b, 1);
        } else {
            aes_encrypt(keys, rounds, b, 1);
        }

        block_store(out, b[0]);
    }
}

SABLE_TARGET_AES static void aes_cbc_encrypt(const AesKey *key, uint8_t *work, const uint8_t *in, uint8_t *out, int64_t count) {
    int rounds = (int)key->rounds;
    Block b = block_load(work);
    for (; count >= 16; in += 16, out += 16, count -= 16) {
        b = block_xor(b, block_load(in));
        aes_encrypt(key->encrypt, rounds, &b, 1);
        block_store(out, b);
    }

    block_store(work, b);
}

// CBC's and CFB's decryption: the blocks before are the ciphertext's, so that
// many are done at once.
SABLE_TARGET_AES SABLE_INLINE void aes_chained(const AesKey *key, bool feedback, uint8_t *work, const uint8_t *in, uint8_t *out, int64_t count) {
    int rounds = (int)key->rounds;
    Block b[8], text[8], before = block_load(work);
    for (; count >= 16; ) {
        int n = count >= 128 ? 8 : 1;
        SABLE_UNROLL
        for (int i = 0; i < 8; i++) {
            if (i < n) {
                text[i] = block_load(in + 16 * i);
            }
        }

        if (feedback) {
            b[0] = before;
            SABLE_UNROLL
            for (int i = 1; i < 8; i++) {
                if (i < n) {
                    b[i] = text[i - 1];
                }
            }

            if (n == 8) {
                aes_encrypt(key->encrypt, rounds, b, 8);
            } else {
                aes_encrypt(key->encrypt, rounds, b, 1);
            }

            SABLE_UNROLL
            for (int i = 0; i < 8; i++) {
                if (i < n) {
                    block_store(out + 16 * i, block_xor(b[i], text[i]));
                }
            }
        } else {
            SABLE_UNROLL
            for (int i = 0; i < 8; i++) {
                if (i < n) {
                    b[i] = text[i];
                }
            }

            if (n == 8) {
                aes_decrypt(key->decrypt, rounds, b, 8);
            } else {
                aes_decrypt(key->decrypt, rounds, b, 1);
            }

            block_store(out, block_xor(b[0], before));
            SABLE_UNROLL
            for (int i = 1; i < 8; i++) {
                if (i < n) {
                    block_store(out + 16 * i, block_xor(b[i], text[i - 1]));
                }
            }
        }

        before = text[n - 1];
        in += 16 * n, out += 16 * n, count -= 16 * n;
    }

    block_store(work, before);
}

SABLE_TARGET_AES static void aes_cbc_decrypt(const AesKey *key, uint8_t *work, const uint8_t *in, uint8_t *out, int64_t count) {
    aes_chained(key, false, work, in, out, count);
}

// 'count' bytes, less than a block, and as many of a block's, exclusive or.
SABLE_TARGET_AES static void xor_part(Block stream, const uint8_t *in, uint8_t *out, int64_t count) {
    uint8_t part[16];
    block_store(part, stream);
    for (int64_t i = 0; i < count; i++) {
        part[i] ^= in[i];
    }

    memcpy(out, part, (size_t)count);
    sable_secure_zero(part, sizeof part);
}

// The last segment may be less than a block.
SABLE_TARGET_AES static void aes_cfb_encrypt(const AesKey *key, uint8_t *work, const uint8_t *in, uint8_t *out, int64_t count) {
    int rounds = (int)key->rounds;
    Block b = block_load(work);
    for (; count >= 16; in += 16, out += 16, count -= 16) {
        aes_encrypt(key->encrypt, rounds, &b, 1);
        b = block_xor(b, block_load(in));
        block_store(out, b);
    }

    if (count > 0) {
        xor_part(aes_one(key->encrypt, rounds, b), in, out, count);
    }

    block_store(work, b);
}

SABLE_TARGET_AES static void aes_cfb_decrypt(const AesKey *key, uint8_t *work, const uint8_t *in, uint8_t *out, int64_t count) {
    int64_t whole = count & ~(int64_t)15;
    aes_chained(key, true, work, in, out, whole);
    if (count > whole) {
        xor_part(aes_one(key->encrypt, (int)key->rounds, block_load(work)), in + whole, out + whole, count - whole);
    }
}

SABLE_TARGET_AES static void aes_cfb8(const AesKey *key, bool decrypt, uint8_t *work, const uint8_t *in, uint8_t *out, int64_t count) {
    uint8_t window[32];
    for (int64_t i = 0; i < count; i++) {
        uint8_t made[16];
        block_store(made, aes_one(key->encrypt, (int)key->rounds, block_load(work)));
        uint8_t read = in[i], written = read ^ made[0];
        out[i] = written;
        memcpy(window, work, 16);
        window[16] = decrypt ? read : written;
        memcpy(work, window + 1, 16);
    }
}

// ---- GHASH --------------------------------------------------------------------------

// GHASH's field has its bits the other way round, which the bytes reversed
// and H doubled make up for (Gueron, as in RFC 8452's appendix A): a product
// is of three parts, and reduced by two multiplications more.
SABLE_TARGET_AES SABLE_INLINE Block ghash_reduce(Block low, Block middle, Block high) {
    const Block polynomial = block_of(1, 0xC200000000000000);
    low = block_xor(low, block_up(middle));
    high = block_xor(high, block_down(middle));
    Block swapped = block_swap(polynomial);
    low = block_xor(block_swap(low), block_low(low, swapped));
    low = block_xor(block_swap(low), block_low(low, swapped));
    return block_xor(low, high);
}

SABLE_TARGET_AES SABLE_INLINE Block ghash_multiply(Block a, Block b) {
    return ghash_reduce(block_low(a, b), block_middle(a, b), block_high(a, b));
}

// H^32 to H^1 from H, the zero block encrypted.
SABLE_TARGET_AES static void ghash_key(AesKey *key) {
    uint8_t bytes[16];
    block_store(bytes, aes_one(key->encrypt, (int)key->rounds, block_zero()));
    uint64_t high = load_big64(bytes), low = load_big64(bytes + 8);
    uint64_t carry = high >> 63;
    high = (high << 1 | low >> 63) ^ (carry * 0xC200000000000000);
    low = low << 1 ^ carry;
    Block h = block_of(low, high), power = h;
    for (int i = 31; i >= 0; i--) {
        block_store(key->powers + 16 * i, power);
        power = ghash_multiply(power, h);
    }

    sable_secure_zero(bytes, sizeof bytes);
}

// The bytes, with zeros to a whole block after them, worked into the sum.
SABLE_TARGET_AES static Block ghash(const uint8_t *powers, Block sum, const uint8_t *data, int64_t count) {
    while (count > 0) {
        uint8_t last[16];
        int64_t blocks = count >= 128 ? 8 : (count + 15) / 16;
        Block low = block_zero(), middle = low, high = low;
        for (int64_t i = 0; i < blocks; i++) {
            const uint8_t *at = data + 16 * i;
            if (count - 16 * i < 16) {
                memset(last, 0, sizeof last);
                memcpy(last, at, (size_t)(count - 16 * i));
                at = last;
            }

            Block x = block_reverse(block_load(at));
            if (i == 0) {
                x = block_xor(x, sum);
            }

            Block power = block_load(powers + 16 * (32 - blocks + i));
            low = block_xor(low, block_low(x, power));
            middle = block_xor(middle, block_middle(x, power));
            high = block_xor(high, block_high(x, power));
        }

        sum = ghash_reduce(low, middle, high);
        data += 16 * blocks, count -= 16 * blocks;
    }

    return sum;
}

// ---- CTR and GCM ----------------------------------------------------------------------

enum { CTR_ONLY, CTR_SEAL, CTR_OPEN };

#define NAME(x) x##_1
#define TARGET SABLE_TARGET_AES
#define V Block
#define W 1
#define V_LOAD block_load
#define V_STORE block_store
#define V_XOR block_xor
#define V_KEY block_load
#define V_START block_start
#define V_ROUND block_round
#define V_LAST block_last
#define V_COUNTER counter_load
#define V_COUNT counter_add
#define V_BLOCKS counter_block
#define V_REVERSE block_reverse
#define V_LOW block_low
#define V_MIDDLE block_middle
#define V_HIGH block_high
#define V_FOLD(v) (v)
#define V_WIDEN(b) (b)
#include "cipher_blocks.h"

#if defined(SABLE_CRYPTO_X64)

#define SABLE_TARGET_VAES __attribute__((target("aes,pclmul,ssse3,sse4.1,avx2,vaes,vpclmulqdq")))
#define SABLE_TARGET_VAES_WIDE __attribute__((target("aes,pclmul,ssse3,sse4.1,avx2,vaes,vpclmulqdq,avx512f,avx512bw,avx512vl")))

#define NAME(x) x##_2
#define TARGET SABLE_TARGET_VAES
#define V __m256i
#define W 2
#define V_LOAD(p) _mm256_loadu_si256((const __m256i *)(p))
#define V_STORE(p, v) _mm256_storeu_si256((__m256i *)(p), v)
#define V_XOR _mm256_xor_si256
#define V_KEY(p) _mm256_broadcastsi128_si256(_mm_loadu_si128((const __m128i *)(p)))
#define V_START _mm256_xor_si256
#define V_ROUND _mm256_aesenc_epi128
#define V_LAST(b, before, last) _mm256_aesenclast_epi128(b, last)
#define V_COUNTER(p) _mm256_add_epi32(_mm256_broadcastsi128_si256(counter_load(p)), _mm256_set_epi32(0, 0, 0, 1, 0, 0, 0, 0))
#define V_COUNT(v, n) _mm256_add_epi32(v, _mm256_set_epi32(0, 0, 0, n, 0, 0, 0, n))
#define V_REVERSE(v) _mm256_shuffle_epi8(v, _mm256_set_epi64x(0x0001020304050607ll, 0x08090a0b0c0d0e0fll, 0x0001020304050607ll, 0x08090a0b0c0d0e0fll))
#define V_BLOCKS V_REVERSE
#define V_LOW(a, b) _mm256_clmulepi64_epi128(a, b, 0x00)
#define V_HIGH(a, b) _mm256_clmulepi64_epi128(a, b, 0x11)
#define V_MIDDLE(a, b) _mm256_xor_si256(_mm256_clmulepi64_epi128(a, b, 0x01), _mm256_clmulepi64_epi128(a, b, 0x10))
#define V_FOLD(v) _mm_xor_si128(_mm256_castsi256_si128(v), _mm256_extracti128_si256(v, 1))
#define V_WIDEN(b) _mm256_inserti128_si256(_mm256_setzero_si256(), b, 0)
#include "cipher_blocks.h"

#define NAME(x) x##_4
#define TARGET SABLE_TARGET_VAES_WIDE
#define V __m512i
#define W 4
#define V_LOAD(p) _mm512_loadu_si512(p)
#define V_STORE(p, v) _mm512_storeu_si512(p, v)
#define V_XOR _mm512_xor_si512
#define V_KEY(p) _mm512_broadcast_i32x4(_mm_loadu_si128((const __m128i *)(p)))
#define V_START _mm512_xor_si512
#define V_ROUND _mm512_aesenc_epi128
#define V_LAST(b, before, last) _mm512_aesenclast_epi128(b, last)
#define V_COUNTER(p) _mm512_add_epi32(_mm512_broadcast_i32x4(counter_load(p)), _mm512_set_epi32(0, 0, 0, 3, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 0))
#define V_COUNT(v, n) _mm512_add_epi32(v, _mm512_set_epi32(0, 0, 0, n, 0, 0, 0, n, 0, 0, 0, n, 0, 0, 0, n))
#define V_REVERSE(v) _mm512_shuffle_epi8(v, _mm512_set_epi64(0x0001020304050607ll, 0x08090a0b0c0d0e0fll, 0x0001020304050607ll, 0x08090a0b0c0d0e0fll, 0x0001020304050607ll, 0x08090a0b0c0d0e0fll, 0x0001020304050607ll, 0x08090a0b0c0d0e0fll))
#define V_BLOCKS V_REVERSE
#define V_LOW(a, b) _mm512_clmulepi64_epi128(a, b, 0x00)
#define V_HIGH(a, b) _mm512_clmulepi64_epi128(a, b, 0x11)
#define V_MIDDLE(a, b) _mm512_xor_si512(_mm512_clmulepi64_epi128(a, b, 0x01), _mm512_clmulepi64_epi128(a, b, 0x10))
#define V_FOLD(v) \
    _mm_xor_si128( \
        _mm_xor_si128(_mm512_extracti32x4_epi32(v, 0), _mm512_extracti32x4_epi32(v, 1)), \
        _mm_xor_si128(_mm512_extracti32x4_epi32(v, 2), _mm512_extracti32x4_epi32(v, 3)) \
    )
#define V_WIDEN(b) _mm512_inserti32x4(_mm512_setzero_si512(), b, 0)
#include "cipher_blocks.h"

#endif

// The bytes in CTR, the counter the block's last four bytes, big-endian.
SABLE_TARGET_AES static void aes_ctr32(const AesKey *key, uint8_t *counter, const uint8_t *in, uint8_t *out, int64_t count) {
    int rounds = (int)key->rounds;
    int64_t done = 0;
#if defined(SABLE_CRYPTO_X64)
    if (crypto_available() & CRYPTO_VAES_WIDE) {
        done += ctr_4(key->encrypt, rounds, counter, in, out, count);
    }
    if (crypto_available() & CRYPTO_VAES) {
        done += ctr_2(key->encrypt, rounds, counter, in + done, out + done, count - done);
    }
#endif
    done += ctr_1(key->encrypt, rounds, counter, in + done, out + done, count - done);
    in += done, out += done, count -= done;
    if (count == 0) {
        return;
    }

    // Less than eight blocks, the last of them maybe not whole.
    Block b[8], next = counter_load(counter);
    int blocks = (int)((count + 15) / 16);
    SABLE_UNROLL
    for (int i = 0; i < 8; i++) {
        b[i] = counter_block(counter_add(next, (uint32_t)i));
    }

    if (blocks == 1) {
        aes_encrypt(key->encrypt, rounds, b, 1);
    } else if (blocks <= 4) {
        aes_encrypt(key->encrypt, rounds, b, 4);
    } else {
        aes_encrypt(key->encrypt, rounds, b, 8);
    }

    int i = 0;
    for (; count >= 16; i++, in += 16, out += 16, count -= 16) {
        block_store(out, block_xor(b[i], block_load(in)));
    }

    if (count > 0) {
        xor_part(b[i], in, out, count);
    }

    store_big32(counter + 12, load_big32(counter + 12) + (uint32_t)blocks);
}

// The same with a counter of the block's 128 bits: 'counter' is then the one
// after the last whole block's.
static void aes_ctr(const AesKey *key, uint8_t *counter, const uint8_t *in, uint8_t *out, int64_t count) {
    while (count > 0) {
        uint32_t first = load_big32(counter + 12);
        int64_t left = (INT64_C(0x100000000) - first) * 16;
        int64_t done = count < left ? count : left;
        aes_ctr32(key, counter, in, out, done);
        if (load_big32(counter + 12) == 0) {
            for (int i = 11; i >= 0 && ++counter[i] == 0; i--) {
            }
        }

        in += done, out += done, count -= done;
    }
}

// AES-GCM, SP 800-38D: 'work' has the first counter block, J0, and after it
// room for the tag, whose first bytes are what the tag is when it is shorter.
// Opening gives the plaintext and the tag it should have had.
SABLE_TARGET_AES static void aes_gcm(const AesKey *key, int mode, uint8_t *work, const uint8_t *ad, int64_t ad_count, const uint8_t *in, uint8_t *out, int64_t count) {
    int rounds = (int)key->rounds;
    uint8_t counter[16];
    memcpy(counter, work, 16);
    store_big32(counter + 12, load_big32(counter + 12) + 1);
    Block sum = ghash(key->powers, block_zero(), ad, ad_count);
    int64_t done = 0;
#if defined(SABLE_CRYPTO_X64)
    if (crypto_available() & CRYPTO_VAES_WIDE) {
        done += (mode == CTR_SEAL ? seal_4 : open_4)(key->encrypt, rounds, key->powers, counter, &sum, in, out, count);
    }
    if (crypto_available() & CRYPTO_VAES) {
        done += (mode == CTR_SEAL ? seal_2 : open_2)(key->encrypt, rounds, key->powers, counter, &sum, in + done, out + done, count - done);
    }
#endif
    done += (mode == CTR_SEAL ? seal_1 : open_1)(key->encrypt, rounds, key->powers, counter, &sum, in + done, out + done, count - done);
    if (done < count) {
        if (mode == CTR_OPEN) {
            sum = ghash(key->powers, sum, in + done, count - done);
        }

        aes_ctr32(key, counter, in + done, out + done, count - done);
        if (mode == CTR_SEAL) {
            sum = ghash(key->powers, sum, out + done, count - done);
        }
    }

    sum = block_xor(sum, block_of((uint64_t)count * 8, (uint64_t)ad_count * 8));
    sum = ghash_multiply(sum, block_load(key->powers + 16 * 31));
    block_store(work + 16, block_xor(block_reverse(sum), aes_one(key->encrypt, rounds, block_load(work))));
}

// GHASH alone, of the two and their lengths.
SABLE_TARGET_AES static void aes_ghash(const AesKey *key, uint8_t *work, const uint8_t *ad, int64_t ad_count, const uint8_t *in, int64_t count) {
    Block sum = ghash(key->powers, block_zero(), ad, ad_count);
    sum = ghash(key->powers, sum, in, count);
    sum = block_xor(sum, block_of((uint64_t)count * 8, (uint64_t)ad_count * 8));
    block_store(work + 16, block_reverse(ghash_multiply(sum, block_load(key->powers + 16 * 31))));
}

// ---- CCM ------------------------------------------------------------------------------

// Bytes worked into a CBC-MAC, 'used' of whose block are in already.
SABLE_TARGET_AES static Block cbc_mac(const AesKey *key, Block mac, int used, const uint8_t *data, int64_t count) {
    while (count > 0) {
        uint8_t part[16] = {0};
        int64_t taken = count < 16 - used ? count : 16 - used;
        memcpy(part + used, data, (size_t)taken);
        mac = aes_one(key->encrypt, (int)key->rounds, block_xor(mac, block_load(part)));
        used = 0, data += taken, count -= taken;
    }

    return mac;
}

// AES-CCM, SP 800-38C: 'work' has B0, whose flags, nonce and length say all
// there is to know, and after it room for the tag.
SABLE_TARGET_AES static void aes_ccm(const AesKey *key, bool open, uint8_t *work, const uint8_t *ad, int64_t ad_count, const uint8_t *in, uint8_t *out, int64_t count) {
    int rounds = (int)key->rounds;
    Block mac = aes_one(key->encrypt, rounds, block_load(work));
    if (ad_count > 0) {
        // How long it is, in two, six or ten bytes, goes before it.
        uint8_t first[16] = {0};
        int used = 2;
        if (ad_count < 0xFF00) {
            first[0] = (uint8_t)(ad_count >> 8), first[1] = (uint8_t)ad_count;
        } else if (ad_count <= 0xFFFFFFFF) {
            first[0] = 0xFF, first[1] = 0xFE, used = 6;
            store_big32(first + 2, (uint32_t)ad_count);
        } else {
            first[0] = 0xFF, first[1] = 0xFF, used = 10;
            store_big64(first + 2, (uint64_t)ad_count);
        }

        int64_t taken = ad_count < 16 - used ? ad_count : 16 - used;
        memcpy(first + used, ad, (size_t)taken);
        mac = cbc_mac(key, mac, 0, first, 16);
        mac = cbc_mac(key, mac, 0, ad + taken, ad_count - taken);
    }

    // The counter blocks have the length's width, of the flags, and no more;
    // a message is too short for the counter to reach the nonce.
    uint8_t counter[16];
    memcpy(counter, work, 16);
    int width = (counter[0] & 7) + 1;
    counter[0] &= 7;
    memset(counter + 16 - width, 0, (size_t)width);
    uint64_t prefix = load_big64(counter + 8), number = 0;
    Block start = aes_one(key->encrypt, rounds, block_load(counter));

    // Each block of the stream is made beside the MAC of the block before it.
    Block pair[2];
    store_big64(counter + 8, prefix + ++number);
    pair[0] = mac;
    pair[1] = aes_one(key->encrypt, rounds, block_load(counter));
    for (; count > 0; ) {
        uint8_t part[16] = {0};
        int64_t taken = count < 16 ? count : 16;
        Block text;
        if (taken == 16) {
            Block read = block_load(in);
            Block written = block_xor(read, pair[1]);
            block_store(out, written);
            text = open ? written : read;
        } else {
            uint8_t read[16] = {0};
            memcpy(read, in, (size_t)taken);
            xor_part(pair[1], in, out, taken);
            memcpy(part, open ? out : read, (size_t)taken);
            text = block_load(part);
        }

        pair[0] = block_xor(pair[0], text);
        store_big64(counter + 8, prefix + ++number);
        pair[1] = block_load(counter);
        aes_encrypt(key->encrypt, rounds, pair, 2);
        in += taken, out += taken, count -= taken;
    }

    block_store(work + 16, block_xor(pair[0], start));
}

// ---- ChaCha20 ---------------------------------------------------------------------------

#define NAME(x) x##_x4
#define TARGET
#define N 4
#include "cipher_chacha.h"

#if defined(SABLE_CRYPTO_ARM64)

// Two registers to a word: the rounds of a block wait for each other more
// than for the registers.
#define NAME(x) x##_x8
#define TARGET
#define N 8
#include "cipher_chacha.h"

#else

#define NAME(x) x##_x8
#define TARGET SABLE_TARGET_AVX2
#define N 8
#include "cipher_chacha.h"

#define NAME(x) x##_x16
#define TARGET SABLE_TARGET_AVX512
#define N 16
#include "cipher_chacha.h"

#endif

// The bytes and the stream from the block 'state' is of on, exclusive or;
// the state is then the next block's.
static void chacha(uint32_t *state, const uint8_t *in, uint8_t *out, int64_t count) {
    int64_t done = 0;
#if defined(SABLE_CRYPTO_X64)
    if (crypto_available() & CRYPTO_WIDE) {
        done += chacha_x16(state, in, out, count);
    }
    if (crypto_available() & CRYPTO_AVX2) {
        done += chacha_x8(state, in + done, out + done, count - done);
    }
#else
    done += chacha_x8(state, in, out, count);
#endif
    done += chacha_x4(state, in + done, out + done, count - done);
    if (done < count) {
        uint8_t stream[256] = {0};
        uint32_t next = state[12];
        chacha_x4(state, stream, stream, 256);
        for (int64_t i = 0; done < count; done++, i++) {
            out[done] = in[done] ^ stream[i];
            state[12] = next + 1 + (uint32_t)(i / 64);
        }

        sable_secure_zero(stream, sizeof stream);
    }
}

static void chacha_state(uint32_t *state, const uint8_t *key, uint32_t number, const uint8_t *nonce) {
    static const uint32_t constants[4] = {0x61707865, 0x3320646e, 0x79622d32, 0x6b206574};
    memcpy(state, constants, 16);
    memcpy(state + 4, key, 32);
    state[12] = number;
    memcpy(state + 13, nonce, 12);
}

// ---- Poly1305 ---------------------------------------------------------------------------

#define SABLE_POLY_MASK 0xFFFFFFFFFFF

// The sum and r in limbs of 44, 44 and 42 bits; r's powers, as far as they
// have been wanted, in limbs of 26.
typedef struct {
    uint64_t h[3];
    uint64_t r[3];
    uint64_t pad[2];
    uint64_t last[3];       // the last power made
    uint32_t power[16][5];
    int powers;
} Poly1305;

static void poly1305_limbs(uint32_t *limbs, const uint64_t *h) {
    const uint64_t mask = 0x3FFFFFF;
    limbs[0] = (uint32_t)(h[0] & mask);
    limbs[1] = (uint32_t)((h[0] >> 26 | h[1] << 18) & mask);
    limbs[2] = (uint32_t)(h[1] >> 8 & mask);
    limbs[3] = (uint32_t)((h[1] >> 34 | h[2] << 10) & mask);
    limbs[4] = (uint32_t)(h[2] >> 16);
}

static void poly1305_start(Poly1305 *poly, const uint8_t *key) {
    uint64_t low, high;
    memcpy(&low, key, 8);
    memcpy(&high, key + 8, 8);
    memcpy(poly->pad, key + 16, 16);
    low &= 0x0FFFFFFC0FFFFFFF, high &= 0x0FFFFFFC0FFFFFFC;
    poly->r[0] = low & SABLE_POLY_MASK;
    poly->r[1] = (low >> 44 | high << 20) & SABLE_POLY_MASK;
    poly->r[2] = high >> 24;
    memcpy(poly->last, poly->r, sizeof poly->r);
    memset(poly->h, 0, sizeof poly->h);
    poly1305_limbs(poly->power[0], poly->r);
    poly->powers = 1;
}

// h * r, its limbs carried: the first two are then less than 2^44, and the
// third at most one more than 42 bits are.
SABLE_INLINE void poly1305_multiply(uint64_t *h, const uint64_t *r) {
    typedef unsigned __int128 Wide;
    uint64_t s1 = r[1] * 20, s2 = r[2] * 20;
    Wide d0 = (Wide)h[0] * r[0] + (Wide)h[1] * s2 + (Wide)h[2] * s1;
    Wide d1 = (Wide)h[0] * r[1] + (Wide)h[1] * r[0] + (Wide)h[2] * s2;
    Wide d2 = (Wide)h[0] * r[2] + (Wide)h[1] * r[1] + (Wide)h[2] * r[0];
    d1 += (uint64_t)(d0 >> 44);
    d2 += (uint64_t)(d1 >> 44);
    uint64_t carry = (uint64_t)(d2 >> 42);
    h[2] = (uint64_t)d2 & 0x3FFFFFFFFFF;
    uint64_t low = ((uint64_t)d0 & SABLE_POLY_MASK) + carry * 5;
    uint64_t middle = ((uint64_t)d1 & SABLE_POLY_MASK) + (low >> 44);
    h[0] = low & SABLE_POLY_MASK;
    h[1] = middle & SABLE_POLY_MASK;
    h[2] += middle >> 44;
}

static void poly1305_powers(Poly1305 *poly, int count) {
    for (; poly->powers < count; poly->powers++) {
        poly1305_multiply(poly->last, poly->r);
        poly1305_limbs(poly->power[poly->powers], poly->last);
    }
}

// The sum, from limbs of 26 bits with some more in each.
static void poly1305_set(Poly1305 *poly, uint64_t *limb) {
    const uint64_t mask = 0x3FFFFFF;
    for (int pass = 0; pass < 2; pass++) {
        for (int i = 0; i < 4; i++) {
            limb[i + 1] += limb[i] >> 26, limb[i] &= mask;
        }

        limb[0] += (limb[4] >> 26) * 5, limb[4] &= mask;
    }

    // The first may have one bit more again; the sum is of values, not bits.
    uint64_t first = limb[0] + (limb[1] << 26);
    uint64_t second = (first >> 44) + (limb[2] << 8) + (limb[3] << 34 & SABLE_POLY_MASK);
    poly->h[0] = first & SABLE_POLY_MASK;
    poly->h[1] = second & SABLE_POLY_MASK;
    poly->h[2] = (second >> 44) + (limb[3] >> 10) + (limb[4] << 16);
}

static void poly1305_blocks(Poly1305 *poly, const uint8_t *data, int64_t count) {
    uint64_t h[3] = {poly->h[0], poly->h[1], poly->h[2]};
    for (; count >= 16; data += 16, count -= 16) {
        uint64_t low, high;
        memcpy(&low, data, 8);
        memcpy(&high, data + 8, 8);
        h[0] += low & SABLE_POLY_MASK;
        h[1] += (low >> 44 | high << 20) & SABLE_POLY_MASK;
        h[2] += high >> 24 | (uint64_t)1 << 40;
        poly1305_multiply(h, poly->r);
    }

    memcpy(poly->h, h, sizeof h);
}

#if defined(SABLE_CRYPTO_ARM64)

#define NAME(x) x##_x2
#define TARGET
#define L 2
#define FACTOR uint32x2_t
#define FACTOR_OF(v) vmovn_u64((uint64x2_t)(v))
#define FACTOR_ALL(word) vdup_n_u32(word)
#define MULTIPLY(a, b) ((LANES)vmull_u32(a, b))
#define MULTIPLY_ADD(sum, a, b) ((LANES)vmlal_u32((uint64x2_t)(sum), a, b))
#define LOAD_BLOCKS(low, high, p) \
    do { \
        uint8x16_t one = vld1q_u8(p), other = vld1q_u8((p) + 16); \
        low = (LANES)vzip1q_u64(vreinterpretq_u64_u8(one), vreinterpretq_u64_u8(other)); \
        high = (LANES)vzip2q_u64(vreinterpretq_u64_u8(one), vreinterpretq_u64_u8(other)); \
    } while (0)
#define ORDER {0, 1}
#include "cipher_poly.h"

#else

#define NAME(x) x##_x4
#define TARGET SABLE_TARGET_AVX2
#define L 4
#define FACTOR LANES
#define FACTOR_OF(v) (v)
#define FACTOR_ALL(word) ((LANES)_mm256_set1_epi64x(word))
#define MULTIPLY(a, b) ((LANES)_mm256_mul_epu32((__m256i)(a), (__m256i)(b)))
#define MULTIPLY_ADD(sum, a, b) ((sum) + MULTIPLY(a, b))
#define LOAD_BLOCKS(low, high, p) \
    do { \
        __m256i one = _mm256_loadu_si256((const __m256i *)(p)), other = _mm256_loadu_si256((const __m256i *)((p) + 32)); \
        low = (LANES)_mm256_unpacklo_epi64(one, other); \
        high = (LANES)_mm256_unpackhi_epi64(one, other); \
    } while (0)
#define ORDER {0, 2, 1, 3}
#include "cipher_poly.h"

#define NAME(x) x##_x8
#define TARGET SABLE_TARGET_AVX512
#define L 8
#define FACTOR LANES
#define FACTOR_OF(v) (v)
#define FACTOR_ALL(word) ((LANES)_mm512_set1_epi64(word))
#define MULTIPLY(a, b) ((LANES)_mm512_mul_epu32((__m512i)(a), (__m512i)(b)))
#define MULTIPLY_ADD(sum, a, b) ((sum) + MULTIPLY(a, b))
#define LOAD_BLOCKS(low, high, p) \
    do { \
        __m512i one = _mm512_loadu_si512(p), other = _mm512_loadu_si512((p) + 64); \
        low = (LANES)_mm512_unpacklo_epi64(one, other); \
        high = (LANES)_mm512_unpackhi_epi64(one, other); \
    } while (0)
#define ORDER {0, 4, 1, 5, 2, 6, 3, 7}
#include "cipher_poly.h"

#endif

// The bytes, with zeros to a whole block after them, worked into the sum.
static void poly1305(Poly1305 *poly, const uint8_t *data, int64_t count) {
    // Not worth r's powers, for a message that is short.
    if (count >= 512) {
        int64_t done = 0;
#if defined(SABLE_CRYPTO_X64)
        if (crypto_available() & CRYPTO_WIDE) {
            done += poly1305_x8(poly, data, count);
        }
        if (crypto_available() & CRYPTO_AVX2) {
            done += poly1305_x4(poly, data + done, count - done);
        }
#else
        done += poly1305_x2(poly, data, count);
#endif
        data += done, count -= done;
    }

    poly1305_blocks(poly, data, count);
    if (count % 16 != 0) {
        uint8_t last[16] = {0};
        memcpy(last, data + count - count % 16, (size_t)(count % 16));
        poly1305_blocks(poly, last, 16);
    }
}

static void poly1305_finish(Poly1305 *poly, uint8_t *tag) {
    // The sum less the prime, if that is not negative, chosen without a branch.
    uint64_t h0 = poly->h[0], h1 = poly->h[1], h2 = poly->h[2];
    uint64_t carry = h2 >> 42;
    h2 &= 0x3FFFFFFFFFF;
    h0 += carry * 5, carry = h0 >> 44, h0 &= SABLE_POLY_MASK;
    h1 += carry, carry = h1 >> 44, h1 &= SABLE_POLY_MASK;
    h2 += carry, carry = h2 >> 42, h2 &= 0x3FFFFFFFFFF;
    h0 += carry * 5, carry = h0 >> 44, h0 &= SABLE_POLY_MASK;
    h1 += carry;
    uint64_t g0 = h0 + 5;
    carry = g0 >> 44, g0 &= SABLE_POLY_MASK;
    uint64_t g1 = h1 + carry;
    carry = g1 >> 44, g1 &= SABLE_POLY_MASK;
    uint64_t g2 = h2 + carry - ((uint64_t)1 << 42);
    uint64_t smaller = (g2 >> 63) - 1;   // all ones when the prime came off
    h0 = (h0 & ~smaller) | (g0 & smaller);
    h1 = (h1 & ~smaller) | (g1 & smaller);
    h2 = (h2 & ~smaller) | (g2 & smaller);
    uint64_t low = h0 | h1 << 44, high = h1 >> 20 | h2 << 24;
    unsigned __int128 sum = (unsigned __int128)low + poly->pad[0];
    low = (uint64_t)sum;
    high += poly->pad[1] + (uint64_t)(sum >> 64);
    memcpy(tag, &low, 8);
    memcpy(tag + 8, &high, 8);
}

// ChaCha20-Poly1305, RFC 8439: 'work' has the nonce, and 16 bytes on room for the tag.
static void chacha_poly(const uint8_t *key, bool open, uint8_t *work, const uint8_t *ad, int64_t ad_count, const uint8_t *in, uint8_t *out, int64_t count) {
    struct {
        uint32_t state[16];
        uint8_t stream[256];
        Poly1305 poly;
    } secret;
    chacha_state(secret.state, key, 0, work);

    // Poly1305's key is the stream's first block, and what comes after it is
    // the message's: made at once, for that a message is often short.
    memset(secret.stream, 0, sizeof secret.stream);
    chacha_x4(secret.state, secret.stream, secret.stream, sizeof secret.stream);
    poly1305_start(&secret.poly, secret.stream);
    poly1305(&secret.poly, ad, ad_count);
    int64_t first = count < 192 ? count : 192;
    if (open) {
        // The tag's part first, as the ciphertext may be where the plaintext will.
        for (int64_t done = 0; done < count; ) {
            int64_t taken = count - done < 16384 ? count - done : 16384;
            poly1305(&secret.poly, in + done, taken);
            if (done == 0) {
                for (int64_t i = 0; i < first; i++) {
                    out[i] = in[i] ^ secret.stream[64 + i];
                }

                chacha(secret.state, in + first, out + first, taken - first);
            } else {
                chacha(secret.state, in + done, out + done, taken);
            }

            done += taken;
        }
    } else {
        for (int64_t done = 0; done < count; ) {
            int64_t taken = count - done < 16384 ? count - done : 16384;
            if (done == 0) {
                for (int64_t i = 0; i < first; i++) {
                    out[i] = in[i] ^ secret.stream[64 + i];
                }

                chacha(secret.state, in + first, out + first, taken - first);
            } else {
                chacha(secret.state, in + done, out + done, taken);
            }

            poly1305(&secret.poly, out + done, taken);
            done += taken;
        }
    }

    uint64_t lengths[2] = {(uint64_t)ad_count, (uint64_t)count};
    poly1305_blocks(&secret.poly, (const uint8_t *)lengths, 16);
    poly1305_finish(&secret.poly, work + 16);
    sable_secure_zero(&secret, sizeof secret);
}

#endif

// ---- Choosing ---------------------------------------------------------------------------

// What 'kind' says done to the 'count' bytes at 'in', to those at 'out', which
// are the same ones or none of them; false, with nothing done, when there is
// no kernel for it here.
bool sable_cipher(int64_t kind, void *key, uint8_t *work, const uint8_t *ad, int64_t ad_count, const uint8_t *in, uint8_t *out, int64_t count) {
#if defined(SABLE_CRYPTO_ARM64) || defined(SABLE_CRYPTO_X64)
    if (!(crypto_available() & (kind >= SABLE_CIPHER_POLY_SEAL ? CRYPTO_VECTORS : CRYPTO_AES))) {
        return false;
    }

    switch (kind) {
        case SABLE_CIPHER_AES_KEY: aes_key(key, in, count); return true;
        case SABLE_CIPHER_GCM_KEY: ghash_key(key); return true;
        case SABLE_CIPHER_ECB_ENCRYPT: aes_ecb(key, false, in, out, count); return true;
        case SABLE_CIPHER_ECB_DECRYPT: aes_ecb(key, true, in, out, count); return true;
        case SABLE_CIPHER_CBC_ENCRYPT: aes_cbc_encrypt(key, work, in, out, count); return true;
        case SABLE_CIPHER_CBC_DECRYPT: aes_cbc_decrypt(key, work, in, out, count); return true;
        case SABLE_CIPHER_CTR: aes_ctr(key, work, in, out, count); return true;
        case SABLE_CIPHER_CFB_ENCRYPT: aes_cfb_encrypt(key, work, in, out, count); return true;
        case SABLE_CIPHER_CFB_DECRYPT: aes_cfb_decrypt(key, work, in, out, count); return true;
        case SABLE_CIPHER_CFB8_ENCRYPT: aes_cfb8(key, false, work, in, out, count); return true;
        case SABLE_CIPHER_CFB8_DECRYPT: aes_cfb8(key, true, work, in, out, count); return true;
        case SABLE_CIPHER_GCM_SEAL: aes_gcm(key, CTR_SEAL, work, ad, ad_count, in, out, count); return true;
        case SABLE_CIPHER_GCM_OPEN: aes_gcm(key, CTR_OPEN, work, ad, ad_count, in, out, count); return true;
        case SABLE_CIPHER_GHASH: aes_ghash(key, work, ad, ad_count, in, count); return true;
        case SABLE_CIPHER_CCM_SEAL: aes_ccm(key, false, work, ad, ad_count, in, out, count); return true;
        case SABLE_CIPHER_CCM_OPEN: aes_ccm(key, true, work, ad, ad_count, in, out, count); return true;
        case SABLE_CIPHER_POLY_SEAL: chacha_poly(key, false, work, ad, ad_count, in, out, count); return true;
        case SABLE_CIPHER_POLY_OPEN: chacha_poly(key, true, work, ad, ad_count, in, out, count); return true;
        case SABLE_CIPHER_CHACHA: {
            uint32_t state[16], number;
            memcpy(&number, work, 4);
            chacha_state(state, key, number, work + 4);
            chacha(state, in, out, count);
            sable_secure_zero(state, sizeof state);
            return true;
        }
    }
#else
    (void)kind, (void)key, (void)work, (void)ad, (void)ad_count, (void)in, (void)out, (void)count;
#endif
    return false;
}

// The same of slices of bytes (each its buffer, where it starts in it, and how
// many), the input where the output is when it starts at -1.
bool sable_crypto_cipher(int64_t kind, void *key, uint8_t *work, const SableArray *ad, int64_t ad_start, int64_t ad_count, const SableArray *in, int64_t in_start, int64_t count, uint8_t *out) {
    return sable_cipher(kind, key, work, slice_bytes(ad, ad_start), ad_count, in_start < 0 ? out : slice_bytes(in, in_start), out, count);
}
