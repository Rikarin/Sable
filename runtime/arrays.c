// The Sable runtime: arrays — copy-on-write arrays, and strings joined from one.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Arrays ------------------------------------------------------------

// A counted buffer of elements, copied before it is changed while shared.
// The empty array is no buffer: a null pointer.
typedef struct SableArray {
    SableObject header;
    int64_t count;
    int64_t capacity;
    char *elements;
} SableArray;

void sable_array_destroy(void *pointer) {
    SableArray *array = pointer;
    const SableMetadata *type = array->header.type;
    if (type->destroy_element != NULL) {
        for (int64_t i = 0; i < array->count; i++) {
            type->destroy_element(array->elements + i * type->element_size);
        }
    }

    buffer_free(array->elements, array->capacity * type->element_size);
}

int64_t sable_array_count(const SableArray *array) {
    return array == NULL ? 0 : array->count;
}

void *sable_array_element(const SableArray *array, int64_t index) {
    if (array == NULL || index < 0 || index >= array->count) {
        trap_here("array index out of range");
    }

    return array->elements + index * array->header.type->element_size;
}

// The array at '*slot', made the only reference to its buffer, so that it
// can be changed: a fresh one for the empty array, a copy of a shared one.
static SableArray *unique(SableArray **slot, const SableMetadata *type, int64_t room) {
    SableArray *array = *slot;
    if (array != NULL && array->header.strong == 1) {
        return array;
    }

    SableArray *copy = sable_alloc(type, (int64_t)sizeof(SableArray));
    if (array != NULL) {
        int64_t size = type->element_size;
        copy->capacity = array->count + room;
        copy->elements = buffer_alloc(copy->capacity * size);
        for (int64_t i = 0; i < array->count; i++) {
            if (type->copy_element != NULL) {
                type->copy_element(copy->elements + i * size, array->elements + i * size);
            } else {
                memcpy(copy->elements + i * size, array->elements + i * size, (size_t)size);
            }
        }

        copy->count = array->count;
        sable_release(array);
    }

    *slot = copy;
    return copy;
}

// Made the only reference to its buffer, before a loop that writes it in
// place: the empty array stays empty, and the loop's index checks trap.
void sable_array_make_unique(SableArray **slot, const SableMetadata *type) {
    if (*slot != NULL && (*slot)->header.strong != 1) {
        unique(slot, type, 0);
    }
}

// Room for one more element at the end; the caller stores it.
void *sable_array_append_slot(SableArray **slot, const SableMetadata *type) {
    SableArray *array = unique(slot, type, 1);
    if (array->count == array->capacity) {
        int64_t old = array->capacity * type->element_size;
        array->capacity = array->capacity < 4 ? 4 : array->capacity * 2;
        array->elements = buffer_resize(array->elements, old, array->capacity * type->element_size);
    }

    return array->elements + array->count++ * type->element_size;
}

void *sable_array_mutable_element(SableArray **slot, const SableMetadata *type, int64_t index) {
    if (*slot == NULL || index < 0 || index >= (*slot)->count) {
        trap_here("array index out of range");
    }

    SableArray *array = unique(slot, type, 0);
    return array->elements + index * type->element_size;
}

int64_t sable_array_capacity(const SableArray *array) {
    return array == NULL ? 0 : array->capacity;
}

// Room for at least 'minimum' elements in all, so that appends up to it do
// not grow the buffer; the array made the only reference to its buffer.
// Asking for less than it has changes nothing.
void sable_array_reserve(SableArray **slot, const SableMetadata *type, int64_t minimum) {
    if (minimum <= 0) {
        return;
    }

    SableArray *array = unique(slot, type, 0);
    if (array->capacity < minimum) {
        int64_t old = array->capacity * type->element_size;
        array->capacity = minimum;
        array->elements = buffer_resize(array->elements, old, minimum * type->element_size);
    }
}

// The 'length' elements at 'base' rotated left by 'by' (0 < by < length):
// the shorter side kept aside, the longer moved over in one go, and the
// shorter put back after it. A permutation of the bytes, so no element is
// retained or released.
static void rotate_elements(char *base, int64_t length, int64_t by, int64_t size) {
    int64_t left = by * size, right = (length - by) * size;
    int64_t aside = left < right ? left : right;
    char *scratch = buffer_alloc(aside);
    if (left <= right) {
        memcpy(scratch, base, (size_t)left);
        memmove(base, base + left, (size_t)right);
        memcpy(base + right, scratch, (size_t)left);
    } else {
        memcpy(scratch, base + left, (size_t)right);
        memmove(base + right, base, (size_t)left);
        memcpy(base, scratch, (size_t)right);
    }
    buffer_free(scratch, aside);
}

// How far left a rotation of 'length' elements by 'by' moves them, 'by'
// taken round the length (a negative one rotates right); the range checked
// against 'count' first.
static int64_t rotation(int64_t from, int64_t to, int64_t by, int64_t count) {
    if (from < 0 || to > count || from > to) {
        trap_here("array slice out of range");
    }

    int64_t length = to - from;
    return length == 0 ? 0 : ((by % length) + length) % length;
}

// Elements [from, to) of the array at '*slot' rotated left by 'by', in place,
// the array made the only reference to its buffer first: 'insert(at:)' and
// 'remove(at:)' move the elements after the place with one memmove.
void sable_array_rotate(SableArray **slot, const SableMetadata *type, int64_t from, int64_t to, int64_t by) {
    int64_t shift = rotation(from, to, by, sable_array_count(*slot));
    if (shift == 0) {
        return;
    }

    SableArray *array = unique(slot, type, 0);
    rotate_elements(array->elements + from * type->element_size, to - from, shift, type->element_size);
}

// The last element, moved out: the caller loads it, and owns it.
void *sable_array_remove_last(SableArray **slot, const SableMetadata *type) {
    if (*slot == NULL || (*slot)->count == 0) {
        trap_here("removeLast of an empty array");
    }

    SableArray *array = unique(slot, type, 0);
    return array->elements + --array->count * type->element_size;
}

// ---- Slices ------------------------------------------------------------

// A slice, 'T[:]': a run of an array's elements, seen where they are — the
// buffer, which it holds a strong reference to, where the run starts and
// how many elements it has. The empty slice is no buffer. Taking one, and
// reading through one, is code the compiler writes in place; only a write,
// which may need a copy, calls here.
typedef struct SableSlice {
    SableArray *buffer;
    int64_t start;
    int64_t count;
} SableSlice;

// The slice at '*slice', made the only reference to its buffer, so that an
// element can be changed: a shared buffer gives way to a copy of the run
// alone, as an array's copy is of its own elements.
void sable_slice_make_unique(SableSlice *slice, const SableMetadata *type) {
    SableArray *buffer = slice->buffer;
    if (buffer == NULL || buffer->header.strong == 1) {
        return;
    }

    int64_t size = type->element_size;
    SableArray *copy = sable_alloc(type, (int64_t)sizeof(SableArray));
    copy->capacity = slice->count;
    copy->count = slice->count;
    copy->elements = buffer_alloc(copy->capacity * size);
    for (int64_t i = 0; i < copy->count; i++) {
        const char *source = buffer->elements + (slice->start + i) * size;
        if (type->copy_element != NULL) {
            type->copy_element(copy->elements + i * size, source);
        } else {
            memcpy(copy->elements + i * size, source, (size_t)size);
        }
    }

    sable_release(buffer);
    slice->buffer = copy;
    slice->start = 0;
}

// A slice as an array again: the buffer itself, retained, when the slice is
// the whole of it — what a mutating member of 'Element[:]' hands back to the
// array it was called on, which costs nothing — else a copy of its run.
SableArray *sable_array_of_slice(SableArray *buffer, int64_t start, int64_t count, const SableMetadata *type) {
    if (buffer == NULL || count == 0) {
        return NULL;
    }

    if (start == 0 && count == buffer->count) {
        sable_retain(buffer);
        return buffer;
    }

    int64_t size = type->element_size;
    SableArray *copy = sable_alloc(type, (int64_t)sizeof(SableArray));
    copy->capacity = count;
    copy->count = count;
    copy->elements = buffer_alloc(count * size);
    for (int64_t i = 0; i < count; i++) {
        const char *source = buffer->elements + (start + i) * size;
        if (type->copy_element != NULL) {
            type->copy_element(copy->elements + i * size, source);
        } else {
            memcpy(copy->elements + i * size, source, (size_t)size);
        }
    }

    return copy;
}

void *sable_slice_mutable_element(SableSlice *slice, const SableMetadata *type, int64_t index) {
    if (index < 0 || index >= slice->count) {
        trap_here("array index out of range");
    }

    sable_slice_make_unique(slice, type);
    return slice->buffer->elements + (slice->start + index) * type->element_size;
}

// Elements [from, to) of the slice at '*slice' rotated left by 'by', in place,
// the slice made the only reference to its run first.
void sable_slice_rotate(SableSlice *slice, const SableMetadata *type, int64_t from, int64_t to, int64_t by) {
    int64_t shift = rotation(from, to, by, slice->count);
    if (shift == 0) {
        return;
    }

    sable_slice_make_unique(slice, type);
    char *base = slice->buffer->elements + (slice->start + from) * type->element_size;
    rotate_elements(base, to - from, shift, type->element_size);
}

static void copy_string_element(void *destination, const void *source) {
    memcpy(destination, source, sizeof(void *));
    sable_retain(*(void *const *)source);
}

static void destroy_string_element(void *element) {
    sable_release(*(void **)element);
}

// ---- Bytes as text (first_invalid_utf8 and make_string are in strings.c)

// A slice of bytes reaches the runtime as its buffer (null when empty),
// where the slice starts in it, and how many bytes it has.
static const uint8_t *slice_bytes(const SableArray *buffer, int64_t start) {
    return buffer == NULL ? NULL : (const uint8_t *)buffer->elements + start;
}

int64_t sable_utf8_validate(const SableArray *buffer, int64_t start, int64_t count) {
    return count == 0 ? -1 : first_invalid_utf8(slice_bytes(buffer, start), count);
}

// Bytes already known to be well-formed UTF-8, as a string: what
// String.fromUtf8 makes once it has validated them.
SableString *sable_string_from_utf8_unchecked(const SableArray *buffer, int64_t start, int64_t count) {
    return count == 0 ? make_counted("", 0, 0) : make_string((const char *)slice_bytes(buffer, start), count);
}

static const SableMetadata string_array_metadata = {
    sable_array_destroy, "[String]", sizeof(void *), copy_string_element, destroy_string_element,
    sizeof(SableArray), _Alignof(SableArray), NULL, NULL,
};

SableArray *sable_string_split(const SableString *s, int32_t separator) {
    // An ASCII separator is a byte that never occurs inside another scalar's
    // encoding: the parts are found with memchr, counted first so that the
    // array is made once at its size, and an ASCII string's parts need no
    // scalars counted.
    if (separator < 0x80) {
        int64_t n = 1;
        for (const char *at = s->bytes, *end = s->bytes + s->length;
             (at = memchr(at, separator, (size_t)(end - at))) != NULL; at++) {
            n++;
        }

        SableArray *parts = sable_alloc(&string_array_metadata, (int64_t)sizeof(SableArray));
        parts->elements = buffer_alloc(n * (int64_t)sizeof(SableString *));
        parts->capacity = n;
        bool ascii = s->count == s->length;
        const char *start = s->bytes, *end = s->bytes + s->length;
        for (int64_t i = 0; i < n; i++) {
            const char *stop = i + 1 < n ? memchr(start, separator, (size_t)(end - start)) : end;
            int64_t length = stop - start;
            ((SableString **)parts->elements)[i] = ascii ? make_counted(start, length, length) : make_string(start, length);
            parts->count = i + 1;
            start = stop + 1;
        }

        return parts;
    }

    SableArray *parts = NULL;
    int64_t start = 0;
    for (int64_t i = 0; i <= s->length;) {
        int64_t width = 1;
        if (i == s->length || decode(s->bytes + i, &width) == separator) {
            SableString *part = make_string(s->bytes + start, i - start);
            *(SableString **)sable_array_append_slot(&parts, &string_array_metadata) = part;
            start = i + width;
        }

        i += width;
    }

    return parts;
}

// ---- Joining (Sable.Text's floor) ------------------------------------

// The parts with the separator between them, in one string made at its size:
// what Sable.Text's joined and TextBuilder build on, linear in the result.
SableString *sable_string_join(const SableArray *parts, const SableString *separator) {
    int64_t n = parts == NULL ? 0 : parts->count;
    SableString *const *pieces = n == 0 ? NULL : (SableString *const *)parts->elements;
    int64_t length = 0, count = 0;
    for (int64_t i = 0; i < n; i++) {
        length += pieces[i]->length;
        count += pieces[i]->count;
    }

    if (n > 1) {
        length += (n - 1) * separator->length;
        count += (n - 1) * separator->count;
    }

    SableString *s = new_string(length, count);
    char *end = s->bytes;
    for (int64_t i = 0; i < n; i++) {
        if (i > 0) {
            memcpy(end, separator->bytes, (size_t)separator->length);
            end += separator->length;
        }

        memcpy(end, pieces[i]->bytes, (size_t)pieces[i]->length);
        end += pieces[i]->length;
    }

    return s;
}
