// The Sable runtime: inline layout — values laid out in one block.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Inline layout -------------------------------------------------------

// An @inline_layout value (docs/02 §2.5) is a block: one counted object
// holding the value's bytes, copied on write. The bytes are the layout the
// compiler describes for the runtime: the fixed-size fields first, packed,
// then each variable-size field in declaration order — a bounded array's
// count in the fewest bytes its bound needs, a grow-only one's as a LEB128
// varint, then the elements; a relative reference a signed offset from its
// own address, 0 for null. Nothing in it is an absolute pointer, so the
// bytes are the value wherever they are copied to.

enum {
    INLINE_SCALAR = 0, INLINE_BOOL = 1, INLINE_CHAR = 2, INLINE_REF = 3,
    INLINE_STRUCT = 4, INLINE_BOUNDED = 5, INLINE_GROW = 6,
};

typedef struct SableInlineLayout SableInlineLayout;

typedef struct SableInlineSlot {
    int32_t offset;        // in the prefix; -1 for a variable-size field
    int32_t kind;
    int32_t width;         // a scalar's or a reference's bytes; a bounded count's
    int32_t optional;      // a reference that may be null
    int32_t capacity;      // a bounded array's bound
    int32_t element_kind;  // an array's element: scalar, Bool, Char or struct
    int32_t element_width; // its bytes, when not a struct
    const SableInlineLayout *layout; // a struct's, an element's, a reference's target
} SableInlineSlot;

struct SableInlineLayout {
    const char *name;
    int32_t prefix;     // the fixed-size fields' bytes
    int32_t fixed_size; // every value's size, or -1 when it varies
    int32_t empty_size; // the empty value's size: zero counts, and so on down
    int32_t references; // whether a value of it can hold a relative reference
    int32_t count;
    SableInlineSlot slots[];
};

typedef struct SableBlock {
    SableObject header;
    int64_t length, capacity;
    uint8_t bytes[];
} SableBlock;

static const SableMetadata sable_block_metadata = { NULL, "inline block", 0, NULL, NULL, 0, _Alignof(SableObject), NULL, NULL };

static uint32_t read_unsigned(const uint8_t *p, int width) {
    uint32_t value = 0;
    for (int i = 0; i < width; i++) {
        value |= (uint32_t)p[i] << (8 * i);
    }

    return value;
}

static void write_unsigned(uint8_t *p, int width, uint32_t value) {
    for (int i = 0; i < width; i++) {
        p[i] = (uint8_t)(value >> (8 * i));
    }
}

// A varint's value and bytes, trusting it: the block was validated or built here.
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

static int varint_size(int64_t value) {
    uint8_t scratch[10];
    return write_varint(scratch, value);
}

static const uint8_t *skip_value(const SableInlineLayout *layout, const uint8_t *p);

// An array's count, and where its elements start.
static int64_t array_count(const SableInlineSlot *slot, const uint8_t *array, const uint8_t **elements) {
    if (slot->kind == INLINE_BOUNDED) {
        *elements = array + slot->width;
        return read_unsigned(array, slot->width);
    }

    int size;
    int64_t count = read_varint(array, &size);
    *elements = array + size;
    return count;
}

static const uint8_t *skip_element(const SableInlineSlot *slot, const uint8_t *p) {
    return slot->element_kind == INLINE_STRUCT ? skip_value(slot->layout, p) : p + slot->element_width;
}

static const uint8_t *skip_slot(const SableInlineSlot *slot, const uint8_t *p) {
    if (slot->kind == INLINE_STRUCT) {
        return skip_value(slot->layout, p);
    }

    const uint8_t *element;
    int64_t count = array_count(slot, p, &element);
    if (slot->element_kind != INLINE_STRUCT) {
        return element + count * slot->element_width;
    }

    if (slot->layout->fixed_size >= 0) {
        return element + count * slot->layout->fixed_size;
    }

    for (int64_t i = 0; i < count; i++) {
        element = skip_value(slot->layout, element);
    }

    return element;
}

static const uint8_t *skip_value(const SableInlineLayout *layout, const uint8_t *p) {
    if (layout->fixed_size >= 0) {
        return p + layout->fixed_size;
    }

    const uint8_t *q = p + layout->prefix;
    for (int32_t i = 0; i < layout->count; i++) {
        if (layout->slots[i].offset < 0) {
            q = skip_slot(&layout->slots[i], q);
        }
    }

    return q;
}

// The empty value's size: zero counts, and so on down — worked out by the compiler.
static int64_t empty_size(const SableInlineLayout *layout) {
    return layout->empty_size;
}

uint8_t *sable_inline_field(const SableInlineLayout *layout, uint8_t *p, int32_t slot) {
    if (layout->slots[slot].offset >= 0) {
        return p + layout->slots[slot].offset;
    }

    const uint8_t *q = p + layout->prefix;
    for (int32_t i = 0; i < slot; i++) {
        if (layout->slots[i].offset < 0) {
            q = skip_slot(&layout->slots[i], q);
        }
    }

    return (uint8_t *)q;
}

int64_t sable_inline_count(const SableInlineSlot *s, const uint8_t *array) {
    const uint8_t *elements;
    return array_count(s, array, &elements);
}

uint8_t *sable_inline_first(const SableInlineSlot *s, uint8_t *array) {
    const uint8_t *elements;
    array_count(s, array, &elements);
    return (uint8_t *)elements;
}

uint8_t *sable_inline_next(const SableInlineSlot *s, uint8_t *element) {
    return (uint8_t *)skip_element(s, element);
}

uint8_t *sable_inline_at(const SableInlineSlot *s, uint8_t *array, int64_t index) {
    const uint8_t *elements;
    int64_t count = array_count(s, array, &elements);
    if (index < 0 || index >= count) {
        trap_here("array index out of range");
    }

    int64_t size = s->element_kind == INLINE_STRUCT ? s->layout->fixed_size : s->element_width;
    return (uint8_t *)elements + index * size;
}

static SableBlock *new_block(int64_t length, int64_t capacity) {
    SableBlock *block = sable_alloc(&sable_block_metadata, (int64_t)sizeof(SableBlock) + capacity);
    block->length = length;
    block->capacity = capacity;
    return block;
}

SableBlock *sable_inline_new(const SableInlineLayout *layout) {
    int64_t size = empty_size(layout);
    return new_block(size, size);
}

// A block in the caller's frame, with room for 'capacity' bytes; one that
// outgrows it is copied to the heap, as a shared one is.
SableBlock *sable_inline_new_stack(void *memory, const SableInlineLayout *layout, int64_t capacity) {
    int64_t size = empty_size(layout);
    if (size > capacity) {
        return sable_inline_new(layout);
    }

    // The header and the empty value's bytes: the rest is written before it is read.
    SableBlock *block = memory;
    block->header.type = &sable_block_metadata;
    block->header.strong = 1;
    block->header.flags = SABLE_STACK;
    object_born(&block->header);
    block->length = size;
    block->capacity = capacity;
    memset(block->bytes, 0, (size_t)size);
    return block;
}

// The block at '*slot', made the only reference to its bytes, with room
// for 'room' more: a copy of a shared one, a larger one for a full one.
static SableBlock *unique_block(SableBlock **slot, int64_t room) {
    SableBlock *block = *slot;
    int64_t needed = block->length + room;
    if (block->header.strong == 1 && !(block->header.flags & SABLE_IMMORTAL) && needed <= block->capacity) {
        return block;
    }

    int64_t capacity = needed <= block->capacity ? block->capacity : needed < 2 * block->capacity ? 2 * block->capacity : needed;
    // Grown in place, by realloc, only when it came from malloc: one from a
    // size class is copied below, as a shared one is.
    if (block->header.strong == 1 && !(block->header.flags & (SABLE_IMMORTAL | SABLE_STACK | SABLE_CLASS_MASK))) {
        SableBlock *grown = realloc(block, sizeof(SableBlock) + (size_t)capacity);
        if (grown == NULL) {
            sable_trap("out of memory");
        }

        grown->capacity = capacity;
        *slot = grown;
        return grown;
    }

    SableBlock *copy = new_block(block->length, capacity);
    memcpy(copy->bytes, block->bytes, (size_t)block->length);
    copy->header.flags |= block->header.flags & SABLE_LINKED;
    sable_release(block);
    *slot = copy;
    return copy;
}

uint8_t *sable_inline_unique(SableBlock **slot) {
    return unique_block(slot, 0)->bytes;
}

SableBlock *sable_inline_copy(const SableInlineLayout *layout, const uint8_t *p) {
    int64_t size = skip_value(layout, p) - p;
    SableBlock *block = new_block(size, size);
    block->header.flags |= SABLE_LINKED;
    memcpy(block->bytes, p, (size_t)size);
    return block;
}

// Every relative reference in a value: where it is, and where it points.
typedef struct InlineLink {
    int64_t at, target;
    int32_t width;
} InlineLink;

typedef struct InlineLinks {
    InlineLink *links;
    int64_t count, capacity;
} InlineLinks;

static void collect_value(const SableInlineLayout *layout, const uint8_t *base, const uint8_t *p, InlineLinks *links);

static void collect_element(const SableInlineSlot *slot, const uint8_t *base, const uint8_t *p, InlineLinks *links) {
    if (slot->element_kind == INLINE_STRUCT) {
        collect_value(slot->layout, base, p, links);
    }
}

static void collect_value(const SableInlineLayout *layout, const uint8_t *base, const uint8_t *p, InlineLinks *links) {
    const uint8_t *q = p + layout->prefix;
    for (int32_t i = 0; i < layout->count; i++) {
        const SableInlineSlot *slot = &layout->slots[i];
        const uint8_t *field = slot->offset >= 0 ? p + slot->offset : q;
        if (slot->kind == INLINE_REF) {
            int32_t offset = slot->width == 2 ? (int16_t)read_unsigned(field, 2) : slot->width == 1 ? (int8_t)field[0] : (int32_t)read_unsigned(field, 4);
            if (offset != 0) {
                if (links->count == links->capacity) {
                    links->capacity = links->capacity < 8 ? 8 : links->capacity * 2;
                    links->links = realloc(links->links, (size_t)links->capacity * sizeof(InlineLink));
                }

                links->links[links->count++] = (InlineLink){ field - base, field - base + offset, slot->width };
            }
        } else if (slot->kind == INLINE_STRUCT) {
            collect_value(slot->layout, base, field, links);
        } else if (slot->kind == INLINE_BOUNDED || slot->kind == INLINE_GROW) {
            const uint8_t *element;
            int64_t count = array_count(slot, field, &element);
            for (int64_t j = 0; j < count; j++) {
                collect_element(slot, base, element, links);
                element = skip_element(slot, element);
            }
        }

        if (slot->offset < 0) {
            q = skip_slot(slot, q);
        }
    }
}

// Every relative reference in the block, if it may hold any: read while its
// bytes are a value, before a change that takes more than one splice.
static InlineLinks collect_links(SableBlock *block, const SableInlineLayout *layout) {
    InlineLinks links = { NULL, 0, 0 };
    if (block->header.flags & SABLE_LINKED) {
        collect_value(layout, block->bytes, block->bytes, &links);
    }

    return links;
}

// Replaces 'removed' bytes at 'at' with 'inserted' ones, moving what
// follows, and keeps every relative reference in 'links' pointing where it
// did: one whose field or target moved is measured again; one whose target
// went is a trap. The links are moved with the bytes, so a change made of
// several splices — an element, then the count that takes it in — reads
// them once, from the block as it was, and never from bytes between two
// splices, which are no value: a count not yet taking in an element walks
// into whatever follows.
static SableBlock *splice_links(SableBlock **slot, int64_t at, int64_t removed, const uint8_t *inserted, int64_t count, InlineLinks *links) {
    SableBlock *block = unique_block(slot, count > removed ? count - removed : 0);
    int64_t delta = count - removed;
    if (block->length - at - removed > 0 && delta != 0) {
        memmove(block->bytes + at + count, block->bytes + at + removed, (size_t)(block->length - at - removed));
    }

    memcpy(block->bytes + at, inserted, (size_t)count);
    block->length += delta;

    int64_t kept = 0;
    for (int64_t i = 0; i < links->count; i++) {
        InlineLink link = links->links[i];
        if (link.at >= at && link.at < at + removed) {
            continue; // the reference itself was replaced
        }

        if (link.target >= at && link.target < at + removed && removed > 0) {
            trap_here("a relative reference's target was replaced");
        }

        int64_t from = link.at >= at + removed ? link.at + delta : link.at;
        int64_t to = link.target >= at + removed ? link.target + delta : link.target;
        int64_t offset = to - from;
        if ((link.width == 2 && (offset < INT16_MIN || offset > INT16_MAX)) || (link.width == 1 && (offset < INT8_MIN || offset > INT8_MAX))) {
            trap_here("a relative reference no longer fits its width");
        }

        write_unsigned(block->bytes + from, link.width, (uint32_t)offset);
        links->links[kept++] = (InlineLink){ from, to, link.width };
    }

    links->count = kept;
    return block;
}

// One splice, its links read from the block as it is.
static SableBlock *splice(SableBlock **slot, const SableInlineLayout *layout, int64_t at, int64_t removed, const uint8_t *inserted, int64_t count) {
    InlineLinks links = collect_links(unique_block(slot, count > removed ? count - removed : 0), layout);
    SableBlock *block = splice_links(slot, at, removed, inserted, count, &links);
    free(links.links);
    return block;
}

// True when a value of the layout can hold a relative reference — worked out by the compiler.
static bool layout_has_references(const SableInlineLayout *layout, int depth) {
    (void)depth;
    return layout->references != 0;
}

void sable_inline_append_at(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const uint8_t *end, const uint8_t *element, int64_t size);

void sable_inline_append(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const uint8_t *element, int64_t size) {
    sable_inline_append_at(slot, root, s, array, skip_slot(s, array), element, size);
}

// As sable_inline_append, with where the array ends already found: the
// compiler's own walker, specialised to the layout, finds it.
void sable_inline_append_at(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const uint8_t *array_end, const uint8_t *element, int64_t size) {
    int64_t at = array - (*slot)->bytes;
    const uint8_t *elements;
    int64_t count = array_count(s, array, &elements);
    if (s->kind == INLINE_BOUNDED && count >= s->capacity) {
        trap_here("inline array is full");
    }

    int64_t end = array_end - (*slot)->bytes;

    // A struct's size is read from it.
    if (size < 0) {
        size = skip_value(s->layout, element) - element;
    }

    // An element copied in from somewhere else in this block goes through a
    // copy of its own, since the splice may move or grow the block under it.
    SableBlock *block = *slot;
    uint8_t *copy = NULL;
    if (element >= block->bytes && element < block->bytes + block->length) {
        copy = malloc((size_t)(size > 0 ? size : 1));
        memcpy(copy, element, (size_t)size);
        element = copy;
    }

    // An element that may hold references of its own makes the block one that may.
    if (s->element_kind == INLINE_STRUCT && layout_has_references(s->layout, 0)) {
        block->header.flags |= SABLE_LINKED;
    }

    // The element first, then the count, which may take another byte: two
    // splices, with the links read once, before either.
    InlineLinks links = collect_links(unique_block(slot, size), root);
    block = splice_links(slot, end, 0, element, size, &links);
    free(copy);

    uint8_t encoded[10];
    int old_size = (int)(elements - array);
    int new_size;
    if (s->kind == INLINE_BOUNDED) {
        write_unsigned(encoded, s->width, (uint32_t)(count + 1));
        new_size = s->width;
    } else {
        new_size = write_varint(encoded, count + 1);
    }

    if (new_size == old_size) {
        memcpy(block->bytes + at, encoded, (size_t)new_size);
    } else {
        splice_links(slot, at, old_size, encoded, new_size, &links);
    }

    free(links.links);
}

// Bytes as a bounded U8 array's elements: a string, as UTF-8.
void sable_inline_assign_bytes(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const uint8_t *bytes, int64_t count) {
    if (s->kind == INLINE_BOUNDED && count > s->capacity) {
        trap_here("too many bytes for the inline array");
    }

    int64_t at = array - (*slot)->bytes;
    int64_t old_size = skip_slot(s, array) - array;
    int width = s->kind == INLINE_BOUNDED ? s->width : varint_size(count);

    // Short text, the usual case, is encoded on the stack.
    uint8_t small[256];
    uint8_t *encoded = width + count <= (int64_t)sizeof small ? small : malloc((size_t)(width + count));
    if (s->kind == INLINE_BOUNDED) {
        write_unsigned(encoded, width, (uint32_t)count);
    } else {
        write_varint(encoded, count);
    }

    memcpy(encoded + width, bytes, (size_t)count);
    splice(slot, root, at, old_size, encoded, width + count);
    if (encoded != small) {
        free(encoded);
    }
}

void sable_inline_assign_string(SableBlock **slot, const SableInlineLayout *root, const SableInlineSlot *s,
    uint8_t *array, const SableString *text) {
    sable_inline_assign_bytes(slot, root, s, array, (const uint8_t *)text->bytes, text->length);
}

// A nested struct replaced by another value of it.
void sable_inline_assign(SableBlock **slot, const SableInlineLayout *root, const SableInlineLayout *layout, uint8_t *field, const uint8_t *value) {
    int64_t at = field - (*slot)->bytes;
    int64_t old_size = skip_value(layout, field) - field;
    int64_t size = skip_value(layout, value) - value;
    SableBlock *block = *slot;
    uint8_t *copy = NULL;
    if (value >= block->bytes && value < block->bytes + block->length) {
        copy = malloc((size_t)(size > 0 ? size : 1));
        memcpy(copy, value, (size_t)size);
        value = copy;
    }

    // A value from another block may hold references of its own.
    block->header.flags |= SABLE_LINKED;
    splice(slot, root, at, old_size, value, size);
    free(copy);
}

uint8_t *sable_inline_ref_get(const uint8_t *field, int32_t width) {
    int32_t offset = width == 2 ? (int16_t)read_unsigned(field, 2) : width == 1 ? (int8_t)field[0] : (int32_t)read_unsigned(field, 4);
    return offset == 0 ? NULL : (uint8_t *)field + offset;
}

void sable_inline_ref_set(SableBlock **slot, uint8_t *field, int32_t width, const uint8_t *target) {
    SableBlock *block = *slot;
    if (target < block->bytes || target >= block->bytes + block->length) {
        trap_here("a relative reference must point into its own block");
    }

    int64_t offset = target - field;
    if (offset == 0) {
        trap_here("a relative reference cannot point at itself");
    }

    if ((width == 2 && (offset < INT16_MIN || offset > INT16_MAX)) || (width == 1 && (offset < INT8_MIN || offset > INT8_MAX))) {
        trap_here("a relative reference does not fit its width");
    }

    write_unsigned(field, width, (uint32_t)offset);
    block->header.flags |= SABLE_LINKED;
}

// Bytes as text: UTF-8, each byte of an ill-formed sequence read as U+FFFD.
SableString *sable_inline_to_string(const SableInlineSlot *s, const uint8_t *array) {
    const uint8_t *bytes;
    int64_t count = array_count(s, array, &bytes);
    return count == 0 ? make_string("", 0) : make_text((const char *)bytes, count);
}

SableArray *sable_inline_bytes(const SableInlineLayout *layout, const uint8_t *p, const SableMetadata *type) {
    int64_t size = skip_value(layout, p) - p;
    if (size == 0) {
        return NULL;
    }

    SableArray *array = sable_alloc(type, (int64_t)sizeof(SableArray));
    array->count = array->capacity = size;
    array->elements = buffer_alloc(size);
    memcpy(array->elements, p, (size_t)size);
    return array;
}

SableBlock *sable_inline_from(const SableArray *bytes) {
    int64_t size = sable_array_count(bytes);
    SableBlock *block = new_block(size, size);
    block->header.flags |= SABLE_LINKED;
    if (size > 0) {
        memcpy(block->bytes, bytes->elements, (size_t)size);
    }

    return block;
}
