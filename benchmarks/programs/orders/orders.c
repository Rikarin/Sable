// Manually-managed port of orders.sb (see ../README.md).
//
// The @inline_layout records are kept in exactly Sable's byte layout
// (docs/02 §2.5): the fixed-size fields first, packed, then each
// variable-size field in declaration order.
//
//   Order:     id U64 @0 | parent rel32? @8 | customer: u8 count, bytes (<= 32)
//              | lines: LEB128 count, then each OrderLine
//   OrderLine: qty U16 @0 | price U32 @2 | sku: u8 count, bytes (<= 16)
//
// Each record is built in a 96-byte buffer in the frame of the function
// that uses it (moved to the heap only if it outgrows that, which it never
// does here), with the same splices Sable's runtime does: a string assigned
// replaces the field's bytes, an appended line goes in at the array's end
// and the count is re-encoded. The roundtrip writes the order out as a
// [U8] (a header and a buffer: two mallocs), copies it into a heap block
// (one malloc), frees the bytes, validates the block as fromBytes does —
// every count, length and reference checked against the end — totals it,
// and frees it. Arithmetic, conversions and bounds are checked as Sable
// checks them. The layout code is written for these two record types
// rather than driven by layout descriptors.

#include "../../support/c/bench.h"
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static _Noreturn void trap(const char *message) {
    fflush(stdout);
    fprintf(stderr, "fatal error: %s\n", message);
    abort();
}

static inline int64_t add_i(int64_t a, int64_t b) {
    int64_t r;
    if (__builtin_add_overflow(a, b, &r)) trap("integer overflow");
    return r;
}

static inline uint64_t add_u64(uint64_t a, uint64_t b) {
    uint64_t r;
    if (__builtin_add_overflow(a, b, &r)) trap("integer overflow");
    return r;
}

static inline uint64_t mul_u64(uint64_t a, uint64_t b) {
    uint64_t r;
    if (__builtin_mul_overflow(a, b, &r)) trap("integer overflow");
    return r;
}

static inline uint64_t int_to_u64(int64_t v) {
    if (v < 0) trap("Int does not fit in U64");
    return (uint64_t)v;
}

static inline uint16_t int_to_u16(int64_t v) {
    if (v < 0 || v > UINT16_MAX) trap("Int does not fit in U16");
    return (uint16_t)v;
}

static inline uint32_t int_to_u32(int64_t v) {
    if (v < 0 || v > (int64_t)UINT32_MAX) trap("Int does not fit in U32");
    return (uint32_t)v;
}

static void *xmalloc(size_t size) {
    void *p = malloc(size);
    if (p == NULL) trap("out of memory");
    return p;
}

// ---- Blocks: a record's bytes ------------------------------------------------

typedef struct Block {
    int64_t length, capacity;
    bool on_heap;
    uint8_t bytes[];
} Block;

enum { FRAME_CAPACITY = 96 };

typedef union FrameBlock {
    Block block;
    uint8_t memory[sizeof(Block) + FRAME_CAPACITY];
} FrameBlock;

static Block *block_in_frame(FrameBlock *frame, int64_t empty_size) {
    memset(frame, 0, sizeof *frame);
    frame->block.length = empty_size;
    frame->block.capacity = FRAME_CAPACITY;
    return &frame->block;
}

static void block_release(Block *block) {
    if (block->on_heap) free(block);
}

// Room for 'room' more bytes: a block that outgrows its frame moves to the heap.
static Block *block_reserve(Block **slot, int64_t room) {
    Block *block = *slot;
    int64_t needed = block->length + room;
    if (needed <= block->capacity) return block;

    int64_t capacity = needed < 2 * block->capacity ? 2 * block->capacity : needed;
    Block *grown;
    if (block->on_heap) {
        grown = realloc(block, sizeof(Block) + (size_t)capacity);
        if (grown == NULL) trap("out of memory");
    } else {
        grown = xmalloc(sizeof(Block) + (size_t)capacity);
        grown->length = block->length;
        grown->on_heap = true;
        memcpy(grown->bytes, block->bytes, (size_t)block->length);
    }
    grown->capacity = capacity;
    *slot = grown;
    return grown;
}

// Replaces 'removed' bytes at 'at' with 'count' new ones, moving what follows.
static void splice(Block **slot, int64_t at, int64_t removed, const uint8_t *inserted, int64_t count) {
    Block *block = block_reserve(slot, count > removed ? count - removed : 0);
    memmove(block->bytes + at + count, block->bytes + at + removed, (size_t)(block->length - at - removed));
    memcpy(block->bytes + at, inserted, (size_t)count);
    block->length += count - removed;
}

static int64_t read_varint(const uint8_t *p, int *size) {
    int64_t value = 0;
    int i = 0;
    do {
        value |= (int64_t)(p[i] & 0x7F) << (7 * i);
    } while (p[i++] & 0x80);
    *size = i;
    return value;
}

static int write_varint(uint8_t *p, int64_t value) {
    int i = 0;
    do {
        uint8_t byte = value & 0x7F;
        value >>= 7;
        p[i++] = byte | (value != 0 ? 0x80 : 0);
    } while (value != 0);
    return i;
}

// ---- The two layouts -----------------------------------------------------------

enum {
    LINE_PREFIX = 6, LINE_QTY = 0, LINE_PRICE = 2, LINE_SKU_BOUND = 16,
    LINE_EMPTY_SIZE = LINE_PREFIX + 1,
    ORDER_PREFIX = 12, ORDER_ID = 0, ORDER_PARENT = 8, ORDER_CUSTOMER_BOUND = 32,
    ORDER_EMPTY_SIZE = ORDER_PREFIX + 1 + 1,
};

static inline int64_t line_size(const uint8_t *line) {
    return LINE_PREFIX + 1 + line[LINE_PREFIX];
}

static inline int64_t order_customer(void) {
    return ORDER_PREFIX;
}

static inline int64_t order_lines(const uint8_t *order) {
    return ORDER_PREFIX + 1 + order[ORDER_PREFIX];
}

// Where the lines array that starts at 'array' ends.
static const uint8_t *lines_end(const uint8_t *array) {
    int size;
    int64_t count = read_varint(array, &size);
    const uint8_t *element = array + size;
    for (int64_t i = 0; i < count; i++) {
        element += line_size(element);
    }
    return element;
}

static inline int64_t order_size(const uint8_t *order) {
    return lines_end(order + order_lines(order)) - order;
}

// A bounded U8 array (a one-byte count) at 'at' given these bytes.
static void assign_bytes(Block **slot, int64_t at, int64_t bound, const char *text) {
    int64_t count = (int64_t)strlen(text);
    if (count > bound) trap("too many bytes for the inline array");
    uint8_t encoded[1 + 32];
    encoded[0] = (uint8_t)count;
    memcpy(encoded + 1, text, (size_t)count);
    int64_t old_size = 1 + (*slot)->bytes[at];
    splice(slot, at, old_size, encoded, 1 + count);
}

static void store_u16(Block *block, int64_t at, uint16_t v) { memcpy(block->bytes + at, &v, sizeof v); }
static void store_u32(Block *block, int64_t at, uint32_t v) { memcpy(block->bytes + at, &v, sizeof v); }
static void store_u64(Block *block, int64_t at, uint64_t v) { memcpy(block->bytes + at, &v, sizeof v); }

static void append_line(Block **order, const Block *line) {
    int64_t at = order_lines((*order)->bytes);
    int old_size;
    int64_t count = read_varint((*order)->bytes + at, &old_size);
    int64_t end = lines_end((*order)->bytes + at) - (*order)->bytes;
    splice(order, end, 0, line->bytes, line_size(line->bytes));

    uint8_t encoded[10];
    int new_size = write_varint(encoded, count + 1);
    if (new_size == old_size) {
        memcpy((*order)->bytes + at, encoded, (size_t)new_size);
    } else {
        splice(order, at, old_size, encoded, new_size);
    }
}

static uint64_t total(const uint8_t *order) {
    uint64_t t = 0;
    const uint8_t *array = order + order_lines(order);
    int size;
    int64_t count = read_varint(array, &size);
    const uint8_t *line = array + size;
    int64_t i = 0;
    while (i < count) {
        uint16_t qty;
        uint32_t price;
        memcpy(&qty, line + LINE_QTY, sizeof qty);
        memcpy(&price, line + LINE_PRICE, sizeof price);
        t = add_u64(t, mul_u64((uint64_t)qty, (uint64_t)price));
        line += line_size(line);
        i = add_i(i, 1);
    }
    return t;
}

// A new order in 'frame': id, customer, no lines, no parent.
static Block *order_new(FrameBlock *frame, uint64_t id, const char *customer) {
    Block *order = block_in_frame(frame, ORDER_EMPTY_SIZE);
    store_u64(order, ORDER_ID, id);
    assign_bytes(&order, order_customer(), ORDER_CUSTOMER_BOUND, customer);
    store_u32(order, ORDER_PARENT, 0);
    return order;
}

static Block *line_new(FrameBlock *frame, const char *sku, uint16_t qty, uint32_t price) {
    Block *line = block_in_frame(frame, LINE_EMPTY_SIZE);
    assign_bytes(&line, LINE_PREFIX, LINE_SKU_BOUND, sku);
    store_u16(line, LINE_QTY, qty);
    store_u32(line, LINE_PRICE, price);
    return line;
}

// ---- Bytes out, and back in through validation -----------------------------------

typedef struct ByteArray {
    int64_t count, capacity;
    uint8_t *elements;
} ByteArray;

static ByteArray *bytes_of(const uint8_t *order) {
    int64_t size = order_size(order);
    ByteArray *array = xmalloc(sizeof(ByteArray));
    array->count = array->capacity = size;
    array->elements = xmalloc((size_t)size);
    memcpy(array->elements, order, (size_t)size);
    return array;
}

static Block *block_from(const ByteArray *bytes) {
    int64_t size = bytes == NULL ? 0 : bytes->count;
    Block *block = xmalloc(sizeof(Block) + (size_t)size);
    block->length = block->capacity = size;
    block->on_heap = true;
    if (size > 0) memcpy(block->bytes, bytes->elements, (size_t)size);
    return block;
}

typedef struct Check {
    const uint8_t *base, *end;
    const char *why;
    bool has_parent;
    int64_t parent_target;
} Check;

static bool fail(Check *check, const char *why) {
    if (check->why == NULL) check->why = why;
    return false;
}

static bool check_bytes(Check *check, int64_t bound, const uint8_t *p, const uint8_t **out) {
    if (check->end - p < 1) return fail(check, "truncated");
    int64_t count = p[0];
    if (count > bound) return fail(check, "an inline array longer than its bound");
    const uint8_t *element = p + 1;
    if (count > check->end - element) return fail(check, "a count longer than the bytes");
    for (int64_t i = 0; i < count; i++) {
        if (check->end - element < 1) return fail(check, "truncated");
        element += 1;
    }
    *out = element;
    return true;
}

static bool check_line(Check *check, const uint8_t *p, const uint8_t **out) {
    if (check->end - p < LINE_PREFIX) return fail(check, "truncated");
    if (check->end - (p + LINE_QTY) < 2) return fail(check, "truncated");
    if (check->end - (p + LINE_PRICE) < 4) return fail(check, "truncated");
    return check_bytes(check, LINE_SKU_BOUND, p + LINE_PREFIX, out);
}

static bool check_order(Check *check, const uint8_t *p, const uint8_t **out) {
    if (check->end - p < ORDER_PREFIX) return fail(check, "truncated");
    if (check->end - (p + ORDER_ID) < 8) return fail(check, "truncated");

    const uint8_t *field = p + ORDER_PARENT;
    if (check->end - field < 4) return fail(check, "truncated");
    uint32_t raw;
    memcpy(&raw, field, sizeof raw);
    int32_t offset = (int32_t)raw;
    if (offset != 0) {
        check->has_parent = true;
        check->parent_target = (field - check->base) + offset;
    }

    const uint8_t *q;
    if (!check_bytes(check, ORDER_CUSTOMER_BOUND, p + ORDER_PREFIX, &q)) return false;

    int64_t count = 0;
    int i = 0;
    for (;; i++) {
        if (i == 5) return fail(check, "a count that is not a varint");
        if (q + i >= check->end) return fail(check, "truncated");
        count |= (int64_t)(q[i] & 0x7F) << (7 * i);
        if (!(q[i] & 0x80)) break;
    }
    if (count > INT32_MAX) return fail(check, "a count that is not a varint");
    const uint8_t *element = q + i + 1;
    if (count > (check->end - element) / LINE_EMPTY_SIZE) return fail(check, "a count longer than the bytes");
    for (int64_t k = 0; k < count; k++) {
        if (!check_line(check, element, &element)) return false;
    }

    *out = element;
    return true;
}

// Null when the block holds a valid Order; why not, otherwise.
static const char *order_invalid(const Block *block) {
    Check check = { block->bytes, block->bytes + block->length, NULL, false, 0 };
    const uint8_t *end;
    if (check_order(&check, block->bytes, &end) && end != check.end) {
        fail(&check, "bytes after the value");
    }
    // The only Order in an Order is the one at its start.
    if (check.why == NULL && check.has_parent && check.parent_target != 0) {
        fail(&check, "a relative reference that does not point at a value of its type");
    }
    return check.why;
}

// ---- The program ---------------------------------------------------------------------

static uint64_t priced(int64_t i) {
    FrameBlock order_frame, a_frame, b_frame, c_frame;
    Block *order = order_new(&order_frame, int_to_u64(i), "Ann");

    Block *a = line_new(&a_frame, "A-1", int_to_u16(add_i(i % 5, 1)), 1999);
    append_line(&order, a);
    Block *b = line_new(&b_frame, "B-22", 1, int_to_u32(i % 1000));
    append_line(&order, b);
    Block *c = line_new(&c_frame, "C-333", 3, 250);
    append_line(&order, c);

    uint64_t result = total(order->bytes);
    block_release(c);
    block_release(b);
    block_release(a);
    block_release(order);
    return result;
}

static uint64_t roundTrip(int64_t i) {
    FrameBlock order_frame, line_frame;
    Block *order = order_new(&order_frame, int_to_u64(i), "Bob");
    Block *line = line_new(&line_frame, "Z-9", 2, int_to_u32(i % 100));
    append_line(&order, line);

    ByteArray *bytes = bytes_of(order->bytes);
    Block *copy = block_from(bytes);
    free(bytes->elements);
    free(bytes);

    uint64_t result = order_invalid(copy) == NULL ? total(copy->bytes) : 0;
    free(copy);
    block_release(line);
    block_release(order);
    return result;
}

static void program(void) {
    uint64_t sum = 0;
    for (int64_t i = 0; i < 1000000; i = add_i(i, 1)) {
        sum = add_u64(sum, priced(i));
        if (i % 1000 == 0) {
            sum = add_u64(sum, roundTrip(i));
        }
    }
    bench_printf("%llu\n", (unsigned long long)sum);
}

BENCH_MAIN(program)
