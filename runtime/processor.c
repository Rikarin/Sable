// What an x86-64 processor has beyond the baseline every one has (SSE2), found
// once and kept. A kernel for more is a function compiled for it, chosen where
// it is called; the portable code runs everywhere else and does the same.

enum {
    PROCESSOR_AVX2 = 1,     // AVX2, BMI1, BMI2 and POPCNT, and a system that keeps the registers
    PROCESSOR_AVX512 = 2,   // those, and AVX-512's F and VL, and its registers kept as well
    PROCESSOR_ASKED = 128,
};

#if defined(__x86_64__) && !defined(__wasi__)

#include <cpuid.h>
#include <immintrin.h>

#define SABLE_X64 1
#define SABLE_TARGET_AVX2 __attribute__((target("avx2,bmi,bmi2")))
#define SABLE_TARGET_AVX512 __attribute__((target("avx2,bmi,bmi2,avx512f,avx512vl")))

static int processor_features(void) {
    unsigned int a, b, c, d;
    // OSXSAVE, AVX and POPCNT; then that the system saves the upper halves.
    if (!__get_cpuid(1, &a, &b, &c, &d) || !(c >> 27 & 1) || !(c >> 28 & 1) || !(c >> 23 & 1)) {
        return 0;
    }

    unsigned int low, high;
    __asm__ __volatile__("xgetbv" : "=a"(low), "=d"(high) : "c"(0));
    if ((low & 6) != 6 || !__get_cpuid_count(7, 0, &a, &b, &c, &d)) {
        return 0;
    }

    if (!(b >> 5 & 1) || !(b >> 3 & 1) || !(b >> 8 & 1)) {
        return 0;
    }

    return PROCESSOR_AVX2 | ((low & 0xE6) == 0xE6 && (b >> 16 & 1) && (b >> 31 & 1) ? PROCESSOR_AVX512 : 0);
}

#endif

// What has been found, or 0: read by a program's '@vectorised' functions too
// (LlvmEmitter.Vectorised.cs), which ask sable_vector_available where it is 0.
int32_t sable_processor_known;

// Two threads that look at once find the same. SABLE_PORTABLE=1 says the
// processor has nothing more, so that the tests can run the portable code
// where the kernels would.
static inline int processor_known(void) {
#if defined(SABLE_X64)
    int features = __atomic_load_n(&sable_processor_known, __ATOMIC_RELAXED);
    if (features == 0) {
        const char *portable = getenv("SABLE_PORTABLE");
        features = PROCESSOR_ASKED | (portable != NULL && strcmp(portable, "1") == 0 ? 0 : processor_features());
        __atomic_store_n(&sable_processor_known, features, __ATOMIC_RELAXED);
    }

    return features;
#else
    return 0;
#endif
}

static inline bool processor_has_avx2(void) {
    return (processor_known() & PROCESSOR_AVX2) != 0;
}
