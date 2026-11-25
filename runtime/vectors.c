// Sable.Math's kernels over arrays of floats (VectorKernels.sb) for a processor
// with AVX2. Each does exactly what the portable kernel does — the same lanes
// added in the same order, nothing fused — so that a result is the same number
// on every processor.

// What 'kind' names.
enum {
    VECTOR_SUM = 1,
    VECTOR_DOT = 2,
    VECTOR_COSINE = 3,
    VECTOR_ADD = 4,
    VECTOR_SUBTRACT = 5,
    VECTOR_MULTIPLY = 6,
    VECTOR_DIVIDE = 7,
};

bool sable_vector_available(void) {
    return processor_has_avx2();
}

#if defined(SABLE_X64)

#if defined(__clang__)
#define SABLE_VECTOR_UNROLL _Pragma("clang loop unroll(full)")
#else
#define SABLE_VECTOR_UNROLL _Pragma("GCC unroll 4")
#endif

typedef float sable_f32x8 __attribute__((vector_size(32)));
typedef double sable_f64x4 __attribute__((vector_size(32)));

// The registers' lanes added pairwise, as SIMD.reduceAdd adds them: lane i
// with lane i + n/2, down to one.
#define SABLE_PAIRWISE(T, V, lanes, registers) ({                            \
    T all[(registers) * (int)(sizeof(V) / sizeof(T))];                       \
    memcpy(all, lanes, sizeof all);                                          \
    for (int n = (int)(sizeof all / sizeof(T)) / 2; n > 0; n /= 2) {         \
        for (int k = 0; k < n; k++) {                                        \
            all[k] += all[k + n];                                            \
        }                                                                    \
    }                                                                        \
    all[0];                                                                  \
})

#define SABLE_VECTOR_KERNELS(T, V, N, SQRT, reduce, map)                     \
    SABLE_TARGET_AVX2 static T reduce(int64_t kind, const T *x, const T *y, int64_t count) { \
        int64_t i = 0;                                                       \
        V a, b;                                                              \
        if (kind == VECTOR_SUM || kind == VECTOR_DOT) {                      \
            V lanes[4] = { { 0 } };                                          \
            if (kind == VECTOR_SUM) {                                        \
                for (; i + 4 * N <= count; i += 4 * N) {                     \
                    SABLE_VECTOR_UNROLL                                             \
                    for (int k = 0; k < 4; k++) {                            \
                        memcpy(&a, x + i + k * N, sizeof a);                 \
                        lanes[k] += a;                                       \
                    }                                                        \
                }                                                            \
            } else {                                                         \
                for (; i + 4 * N <= count; i += 4 * N) {                     \
                    SABLE_VECTOR_UNROLL                                             \
                    for (int k = 0; k < 4; k++) {                            \
                        memcpy(&a, x + i + k * N, sizeof a);                 \
                        memcpy(&b, y + i + k * N, sizeof b);                 \
                        lanes[k] += a * b;                                   \
                    }                                                        \
                }                                                            \
            }                                                                \
            T total = SABLE_PAIRWISE(T, V, lanes, 4);                        \
            for (; i < count; i++) {                                         \
                total += kind == VECTOR_SUM ? x[i] : x[i] * y[i];            \
            }                                                                \
            return total;                                                    \
        }                                                                    \
        V products[2] = { { 0 } }, xx[2] = { { 0 } }, yy[2] = { { 0 } };     \
        for (; i + 2 * N <= count; i += 2 * N) {                             \
            SABLE_VECTOR_UNROLL                                                     \
            for (int k = 0; k < 2; k++) {                                    \
                memcpy(&a, x + i + k * N, sizeof a);                         \
                memcpy(&b, y + i + k * N, sizeof b);                         \
                products[k] += a * b;                                        \
                xx[k] += a * a;                                              \
                yy[k] += b * b;                                              \
            }                                                                \
        }                                                                    \
        T dot = SABLE_PAIRWISE(T, V, products, 2);                           \
        T xSquared = SABLE_PAIRWISE(T, V, xx, 2);                            \
        T ySquared = SABLE_PAIRWISE(T, V, yy, 2);                            \
        for (; i < count; i++) {                                             \
            dot += x[i] * y[i];                                              \
            xSquared += x[i] * x[i];                                         \
            ySquared += y[i] * y[i];                                         \
        }                                                                    \
        return dot / (SQRT(xSquared) * SQRT(ySquared));                      \
    }                                                                        \
    SABLE_VECTOR_MAP(T, V, N, map##_add, +)                                  \
    SABLE_VECTOR_MAP(T, V, N, map##_subtract, -)                             \
    SABLE_VECTOR_MAP(T, V, N, map##_multiply, *)                             \
    SABLE_VECTOR_MAP(T, V, N, map##_divide, /)                               \
    static void map(int64_t kind, T *out, const T *x, const T *y, int64_t count) { \
        switch (kind) {                                                      \
            case VECTOR_ADD: map##_add(out, x, y, count); break;             \
            case VECTOR_SUBTRACT: map##_subtract(out, x, y, count); break;   \
            case VECTOR_MULTIPLY: map##_multiply(out, x, y, count); break;   \
            default: map##_divide(out, x, y, count); break;                  \
        }                                                                    \
    }

// 'out' may be x or y, or be apart from them.
#define SABLE_VECTOR_MAP(T, V, N, name, op)                                  \
    SABLE_TARGET_AVX2 static void name(T *out, const T *x, const T *y, int64_t count) { \
        int64_t i = 0;                                                       \
        for (; i + 2 * N <= count; i += 2 * N) {                             \
            V a, b, c, d;                                                    \
            memcpy(&a, x + i, sizeof a);                                     \
            memcpy(&b, y + i, sizeof b);                                     \
            memcpy(&c, x + i + N, sizeof c);                                 \
            memcpy(&d, y + i + N, sizeof d);                                 \
            a = a op b;                                                      \
            c = c op d;                                                      \
            memcpy(out + i, &a, sizeof a);                                   \
            memcpy(out + i + N, &c, sizeof c);                               \
        }                                                                    \
        for (; i < count; i++) {                                             \
            out[i] = x[i] op y[i];                                           \
        }                                                                    \
    }

SABLE_VECTOR_KERNELS(float, sable_f32x8, 8, sqrtf, vector_reduce_f32, vector_map_f32)
SABLE_VECTOR_KERNELS(double, sable_f64x4, 4, sqrt, vector_reduce_f64, vector_map_f64)

#endif

// A whole reduction of x (and y, as long), each from where it starts in its
// buffer; and an elementwise operation, to as many elements at 'out'. Called
// only where sable_vector_available.
#if defined(SABLE_X64)
#define SABLE_VECTOR_ELEMENTS(T, array, start) ((array) == NULL ? NULL : (const T *)(array)->elements + (start))
#define SABLE_VECTOR_ENTRIES(T, name, reduce, map)                                                                                                \
    T sable_vector_reduce_##name(int64_t kind, const SableArray *x, int64_t xStart, int64_t count, const SableArray *y, int64_t yStart) {         \
        return reduce(kind, SABLE_VECTOR_ELEMENTS(T, x, xStart), SABLE_VECTOR_ELEMENTS(T, y, yStart), count);                                     \
    }                                                                                                                                             \
    void sable_vector_map_##name(T *out, int64_t kind, const SableArray *x, int64_t xStart, int64_t count, const SableArray *y, int64_t yStart) { \
        map(kind, out, SABLE_VECTOR_ELEMENTS(T, x, xStart), SABLE_VECTOR_ELEMENTS(T, y, yStart), count);                                          \
    }
#else
#define SABLE_VECTOR_ENTRIES(T, name, reduce, map)                                                                                                \
    T sable_vector_reduce_##name(int64_t kind, const SableArray *x, int64_t xStart, int64_t count, const SableArray *y, int64_t yStart) {         \
        (void)kind, (void)x, (void)xStart, (void)count, (void)y, (void)yStart;                                                                    \
        abort();                                                                                                                                  \
    }                                                                                                                                             \
    void sable_vector_map_##name(T *out, int64_t kind, const SableArray *x, int64_t xStart, int64_t count, const SableArray *y, int64_t yStart) { \
        (void)out, (void)kind, (void)x, (void)xStart, (void)count, (void)y, (void)yStart;                                                         \
        abort();                                                                                                                                  \
    }
#endif

SABLE_VECTOR_ENTRIES(float, F32, vector_reduce_f32, vector_map_f32)
SABLE_VECTOR_ENTRIES(double, F64, vector_reduce_f64, vector_map_f64)
