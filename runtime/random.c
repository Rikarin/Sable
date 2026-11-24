// The system's random numbers, for Sable: what SystemRandomNumberGenerator
// and SecureRandom draw.

// 'count' bytes from the system's secure random source. A program is never
// given fewer, or other ones: when the system has none to give, it stops.
void sable_random_fill(void *bytes, int64_t count) {
    if (count > 0 && !random_bytes(bytes, (size_t)count)) {
        sable_trap("the system's random source failed");
    }
}

int64_t sable_random_bits(void) {
    int64_t bits = 0;
    sable_random_fill(&bits, sizeof bits);
    return bits;
}
