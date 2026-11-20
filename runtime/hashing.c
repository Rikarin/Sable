// Hashing: the process's seed, and a string's bytes hashed under a key.
//
// Sable.Core's Hasher is Sable; what it asks of the runtime is a seed no
// one outside the process can know, so that keys chosen to collide in one
// run collide in no other (hash flooding), and a string's bytes hashed in
// one call rather than a call per byte. Both are computed the same way by
// the interpreter (SilInterpreter.HashSeed and StringHash), so that with
// SABLE_DETERMINISTIC_HASHING=1 a program prints the same hashes natively
// and interpreted.

// The seed every Hasher starts from: random per process, read by compiled
// code as a global (LlvmEmitter's 'hash_seed') rather than through a call.
// SABLE_DETERMINISTIC_HASHING=1 fixes it, for runs that must repeat.
uint64_t sable_hash_seed;

// The seed fixed hashing uses: any constant would do; this is pi's digits.
#define SABLE_DETERMINISTIC_SEED 0x243F6A8885A308D3ull

static void hashing_start(void) {
    const char *deterministic = getenv("SABLE_DETERMINISTIC_HASHING");
    if (deterministic != NULL && strcmp(deterministic, "1") == 0) {
        sable_hash_seed = SABLE_DETERMINISTIC_SEED;
        return;
    }

    uint64_t seed = 0;
    if (!random_bytes(&seed, sizeof seed)) {
        // No entropy to be had: something no one else can predict well,
        // which is better than a constant.
        seed = (uint64_t)sable_time_monotonic() ^ ((uint64_t)process_id() << 32) ^ (uint64_t)(uintptr_t)&seed;
    }

    sable_hash_seed = seed;
}

// The 128-bit product's high half folded onto its low half: every bit of
// each operand reaches every bit of the result. Hasher.fold in Sable.
static inline uint64_t fold_multiply(uint64_t a, uint64_t b) {
    __uint128_t product = (__uint128_t)a * b;
    return (uint64_t)product ^ (uint64_t)(product >> 64);
}

// Little-endian loads, so that every machine hashes alike.
static inline uint64_t load_little64(const unsigned char *p) {
    uint64_t word;
    memcpy(&word, p, sizeof word);
#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ == __ORDER_BIG_ENDIAN__
    word = __builtin_bswap64(word);
#endif
    return word;
}

static inline uint64_t load_little32(const unsigned char *p) {
    uint32_t word;
    memcpy(&word, p, sizeof word);
#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ == __ORDER_BIG_ENDIAN__
    word = __builtin_bswap32(word);
#endif
    return word;
}

// A string's UTF-8 bytes hashed under a key, sixteen bytes to a folded
// multiply (foldhash's construction, as the Hasher's words are): each
// multiply takes two words of text, one of them mixed with what came before
// and the other with a second key derived from the first, so that no input
// can be chosen to collide without knowing the key. Short strings take one
// multiply over two loads that between them cover every byte; the length
// goes in first, so that two such strings of different lengths differ. The
// interpreter computes exactly this (SilInterpreter.StringHash).
static int64_t hash_bytes(const char *bytes, int64_t length, int64_t key) {
    const uint64_t k0 = (uint64_t)key;
    const uint64_t k1 = ((k0 << 32) | (k0 >> 32)) ^ 0x9E3779B97F4A7C15ull;
    const unsigned char *p = (const unsigned char *)bytes;
    const uint64_t n = (uint64_t)length;
    uint64_t acc = k0 ^ (n * 0xD6E8FEB86659FD93ull);

    if (n <= 16) {
        uint64_t a = 0;
        uint64_t b = 0;
        if (n >= 8) {
            a = load_little64(p);
            b = load_little64(p + n - 8);
        } else if (n >= 4) {
            a = load_little32(p);
            b = load_little32(p + n - 4);
        } else if (n > 0) {
            a = p[0];
            b = ((uint64_t)p[n / 2] << 8) | p[n - 1];
        }
        acc = fold_multiply(a ^ acc, b ^ k1);
    } else {
        uint64_t i = 0;
        for (; i + 16 < n; i += 16) {
            acc = fold_multiply(load_little64(p + i) ^ acc, load_little64(p + i + 8) ^ k1);
        }
        acc = fold_multiply(load_little64(p + n - 16) ^ acc, load_little64(p + n - 8) ^ k1);
    }

    return (int64_t)fold_multiply(acc, k1 ^ 0x2D358DCCAA6C78A5ull);
}

int64_t sable_string_hash(const SableString *s, int64_t key) {
    return hash_bytes(s->bytes, s->length, key);
}

// The bytes of a range of the string hashed as a string of just those bytes
// is: a Substring hashes as the String it would copy to.
int64_t sable_string_hash_range(const SableString *s, int64_t from, int64_t to, int64_t key) {
    return hash_bytes(s->bytes + from, to - from, key);
}
