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

// A slice: a view of a run of an array's elements (see "Slices" below).
typedef struct SableSlice {
    SableArray *buffer;
    int64_t start;
    int64_t count;
} SableSlice;

// ---- Arrays of a type known only at run time (#297) -------------------------

// An array of a type parameter's values, made by unspecialised generic code:
// its object metadata is made at run time from the element's type metadata,
// and its elements are copied, destroyed and visited by the element's value
// witnesses. The copy, destroy and visit entries name the functions below,
// which stand for that: the runtime's array code calls the witnesses instead
// (element_copy, element_destroy, element_visit), and nothing else calls them.
// With it, the array type's own metadata, [T] — an array's argument is its
// element — for generic code to pass on as a type argument. Each is made once
// per element type, kept for the program's life, and found again by it.
typedef struct SableArrayInstance {
    SableMetadata elements;            // the array object's metadata
    const SableTypeMetadata *element;  // the element's, which the witnesses take
    SableTypeMetadata type;            // the array type's, [T]
    const SableTypeMetadata *argument; // its one type argument: the element
    SableTypeMetadata slice;           // a slice's of it, [T:], which has the same argument
    struct SableArrayInstance *next;
} SableArrayInstance;

// A slice's value witnesses: its buffer is one counted reference, and its
// start and count are bits. Defined with the slice, below.
static const SableValueWitnesses slice_witnesses;

static void witnessed_element_copy(void *destination, const void *source) {
    (void)destination;
    (void)source;
    sable_trap("an element copied without its type");
}

static void witnessed_element_destroy(void *element) {
    (void)element;
    sable_trap("an element destroyed without its type");
}

static void witnessed_element_visit(void *element) {
    (void)element;
    sable_trap("an element visited without its type");
}

static const SableTypeMetadata *witnessed_element(const SableMetadata *type) {
    return ((const SableArrayInstance *)(const void *)type)->element;
}

static void element_copy(const SableMetadata *type, void *destination, const void *source) {
    if (type->copy_element == witnessed_element_copy) {
        const SableTypeMetadata *element = witnessed_element(type);
        element->witnesses->initialize_with_copy(destination, (void *)source, element);
    } else if (type->copy_element != NULL) {
        type->copy_element(destination, source);
    } else {
        memcpy(destination, source, (size_t)type->element_size);
    }
}

// A run of elements copied to where none are yet: at once where each is
// copied by its bits, whether the array's type says so or its element's does.
static void elements_copy(const SableMetadata *type, char *destination, const char *source, int64_t count) {
    int64_t size = type->element_size;
    bool bits = type->copy_element == NULL
                || (type->copy_element == witnessed_element_copy && (witnessed_element(type)->flags & SABLE_TYPE_TRIVIAL) != 0);
    if (bits) {
        if (count > 0 && size > 0) {
            memcpy(destination, source, (size_t)(count * size));
        }

        return;
    }

    for (int64_t i = 0; i < count; i++) {
        element_copy(type, destination + i * size, source + i * size);
    }
}

static void element_destroy(const SableMetadata *type, void *element) {
    if (type->destroy_element == witnessed_element_destroy) {
        const SableTypeMetadata *of = witnessed_element(type);
        of->witnesses->destroy(element, of);
    } else if (type->destroy_element != NULL) {
        type->destroy_element(element);
    }
}

static void element_visit(const SableMetadata *type, void *element) {
    if (type->visit == witnessed_element_visit) {
        const SableTypeMetadata *of = witnessed_element(type);
        of->witnesses->visit(element, of);
    } else if (type->visit != NULL) {
        type->visit(element);
    }
}

void sable_array_destroy(void *pointer);
extern const SableValueWitnesses sable_reference_witnesses;

static SableArrayInstance *array_instances;

static SableArrayInstance *array_instance(const SableTypeMetadata *element) {
    for (SableArrayInstance *at = __atomic_load_n(&array_instances, __ATOMIC_ACQUIRE); at != NULL; at = at->next) {
        if (at->element == element) {
            return at;
        }
    }

    SableArrayInstance *made = calloc(1, sizeof *made);
    size_t length = element->name == NULL ? 0 : strlen(element->name);
    char *name = malloc(length + 3);
    char *slice_name = malloc(length + 4);
    if (made == NULL || name == NULL || slice_name == NULL) {
        sable_trap("out of memory");
    }

    name[0] = '[';
    memcpy(name + 1, element->name == NULL ? "" : element->name, length);
    name[length + 1] = ']';
    name[length + 2] = 0;
    memcpy(slice_name, name, length + 1);
    memcpy(slice_name + length + 1, ":]", 3);

    bool trivial = (element->flags & SABLE_TYPE_TRIVIAL) != 0;
    made->element = element;
    made->elements.destroy = sable_array_destroy;
    made->elements.name = name;
    made->elements.element_size = element->stride;
    made->elements.copy_element = trivial ? NULL : witnessed_element_copy;
    made->elements.destroy_element = trivial ? NULL : witnessed_element_destroy;
    made->elements.size = (int64_t)sizeof(SableArray);
    made->elements.alignment = (int64_t)_Alignof(SableArray);
    made->elements.visit = trivial ? NULL : witnessed_element_visit;
    made->argument = element;
    made->type.kind = SABLE_KIND_ARRAY;
    made->type.name = name;
    made->type.size = (int64_t)sizeof(void *);
    made->type.alignment = (int64_t)_Alignof(void *);
    made->type.stride = (int64_t)sizeof(void *);
    made->type.witnesses = &sable_reference_witnesses;
    made->type.argument_count = 1;
    made->type.arguments = &made->argument;
    made->slice.kind = SABLE_KIND_SLICE;
    made->slice.name = slice_name;
    made->slice.size = (int64_t)sizeof(SableSlice);
    made->slice.alignment = (int64_t)_Alignof(SableSlice);
    made->slice.stride = (int64_t)sizeof(SableSlice);
    made->slice.witnesses = &slice_witnesses;
    made->slice.argument_count = 1;
    made->slice.arguments = &made->argument;

    // Another thread may have made one meanwhile: either serves, and both are kept.
    made->next = __atomic_load_n(&array_instances, __ATOMIC_ACQUIRE);
    while (!__atomic_compare_exchange_n(&array_instances, &made->next, made, true, __ATOMIC_RELEASE, __ATOMIC_ACQUIRE)) {
    }

    return made;
}

// A call's cache of the instance for the element it was first asked about.
static SableArrayInstance *array_instance_cached(SableArrayInstance **cache, const SableTypeMetadata *element) {
    SableArrayInstance *known = __atomic_load_n(cache, __ATOMIC_ACQUIRE);
    if (known != NULL && known->element == element) {
        return known;
    }

    SableArrayInstance *found = array_instance(element);
    if (known == NULL) {
        __atomic_store_n(cache, found, __ATOMIC_RELEASE);
    }

    return found;
}

// The object metadata of an array of the element type: what array code is given.
const SableMetadata *sable_array_metadata(SableArrayInstance **cache, const SableTypeMetadata *element) {
    return &array_instance_cached(cache, element)->elements;
}

// The type metadata of an array of the element type, [T].
const SableTypeMetadata *sable_array_type(SableArrayInstance **cache, const SableTypeMetadata *element) {
    return &array_instance_cached(cache, element)->type;
}

// The type metadata of a slice of the element type, [T:].
const SableTypeMetadata *sable_slice_type(SableArrayInstance **cache, const SableTypeMetadata *element) {
    return &array_instance_cached(cache, element)->slice;
}

// An array literal that does not escape its frame (docs/04 §3.7) is made
// there whole: the header, and its elements after it at this offset, which
// the compiler's code stores. Counted as any array is, its last release
// destroys the elements and frees neither. Grown past them, its elements
// move to the heap; the header stays where it is.
enum { SABLE_FRAME_ELEMENTS = 48 };
_Static_assert(sizeof(SableArray) <= SABLE_FRAME_ELEMENTS, "a frame's array header fits before its elements");

static bool elements_in_frame(const SableArray *array) {
    return (object_flags(&array->header) & SABLE_STACK) && array->elements == (const char *)array + SABLE_FRAME_ELEMENTS;
}

void *sable_stack_array(void *memory, const SableMetadata *type, int64_t count) {
    SableArray *array = memory;
    memset(array, 0, sizeof *array);
    array->header.type = type;
    array->header.strong = 1;
    array->header.flags = SABLE_STACK;
    array->count = count;
    array->capacity = count;
    array->elements = (char *)memory + SABLE_FRAME_ELEMENTS;
    object_born(&array->header);
    return array;
}

// The elements given room for 'size' bytes, keeping the first 'old': out of
// the frame, the first time an array made there grows.
static char *resize_elements(SableArray *array, int64_t old, int64_t size) {
    if (!elements_in_frame(array)) {
        return buffer_resize(array->elements, old, size);
    }

    char *grown = buffer_alloc(size);
    memcpy(grown, array->elements, (size_t)(old < size ? old : size));
    return grown;
}

// ---- Mapped arrays --------------------------------------------------------

// An array of a file's bytes mapped into memory (MappedFile): its elements
// are the mapping, and its capacity the mapping's length. Its last release
// unmaps them. It is never the only reference to its buffer, whatever its
// count says — the mapping is not the array's to grow or free — so a
// change to it is made to a copy, as a change to a shared array is: its
// bytes are written through the runtime's calls alone.
static void mapped_array_destroy(void *pointer) {
    SableArray *array = pointer;
    file_unmap(array->elements, array->capacity);
}

static const SableMetadata mapped_metadata = {
    mapped_array_destroy, "[U8]", 1, NULL, NULL, sizeof(SableArray), _Alignof(SableArray), NULL, NULL,
};
// One mapped to be written: its bytes may be changed in place by
// sable_file_map_store.
static const SableMetadata mapped_writable_metadata = {
    mapped_array_destroy, "[U8]", 1, NULL, NULL, sizeof(SableArray), _Alignof(SableArray), NULL, NULL,
};

static bool is_mapped(const SableArray *array) {
    return array->header.type == &mapped_metadata || array->header.type == &mapped_writable_metadata;
}

// Whether the array is the only reference to a buffer it may change.
static bool uniquely_held(const SableArray *array) {
    return strong_count(&array->header) == 1 && !is_mapped(array);
}

void sable_array_destroy(void *pointer) {
    SableArray *array = pointer;
    const SableMetadata *type = array->header.type;
    if (type->destroy_element != NULL) {
        for (int64_t i = 0; i < array->count; i++) {
            element_destroy(type, array->elements + i * type->element_size);
        }
    }

    if (!elements_in_frame(array)) {
        buffer_free(array->elements, array->capacity * type->element_size);
    }
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

// A buffer reachable from more than one thread holds only what is counted
// atomically (SABLE_SHARED): what is put into one is shared first. Compiled
// code shares the element it stores (LlvmEmitter's ShareIntoArray); these
// are the elements copied in here.
static void share_run(const SableMetadata *type, char *elements, int64_t count);

static inline void share_stored(const SableArray *array, const SableMetadata *type, char *elements, int64_t count) {
    if ((object_flags(&array->header) & SABLE_SHARED) != 0 && type->visit != NULL) {
        share_run(type, elements, count);
    }
}

// A copy of a buffer that takes its place: shared if it was, as what it is
// held by may be. What it holds is shared already.
static void share_copy(SableArray *copy, const SableArray *array) {
    if ((object_flags(&array->header) & SABLE_SHARED) != 0) {
        flags_set(&copy->header, SABLE_SHARED);
    }
}

// The array at '*slot', made the only reference to its buffer, so that it
// can be changed: a fresh one for the empty array, a copy of a shared one.
static SableArray *unique(SableArray **slot, const SableMetadata *type, int64_t room) {
    SableArray *array = *slot;
    if (array != NULL && uniquely_held(array)) {
        return array;
    }

    SableArray *copy = sable_alloc(type, (int64_t)sizeof(SableArray));
    if (array != NULL) {
        int64_t size = type->element_size;
        copy->capacity = array->count + room;
        copy->elements = buffer_alloc(copy->capacity * size);
        elements_copy(type, copy->elements, array->elements, array->count);

        copy->count = array->count;
        share_copy(copy, array);
        sable_release(array);
    }

    *slot = copy;
    return copy;
}

// Made the only reference to its buffer, before a loop that writes it in
// place: the empty array stays empty, and the loop's index checks trap.
SABLE_SLOW void sable_array_make_unique(SableArray **slot, const SableMetadata *type) {
    if (*slot != NULL && !uniquely_held(*slot)) {
        unique(slot, type, 0);
    }
}

// Room for one more element at the end; the caller stores it.
SABLE_SLOW void *sable_array_append_slot(SableArray **slot, const SableMetadata *type) {
    SableArray *array = unique(slot, type, 1);
    if (array->count == array->capacity) {
        int64_t old = array->capacity * type->element_size;
        array->capacity = array->capacity < 4 ? 4 : array->capacity * 2;
        array->elements = resize_elements(array, old, array->capacity * type->element_size);
    }

    return array->elements + array->count++ * type->element_size;
}

SABLE_SLOW void *sable_array_mutable_element(SableArray **slot, const SableMetadata *type, int64_t index) {
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
        array->elements = resize_elements(array, old, minimum * type->element_size);
    }
}

// The first 'count' elements kept and the rest destroyed, the buffer and its
// room kept, so the array can be filled again without growing: what a
// pooled buffer is emptied with. The array made the only reference to it.
void sable_array_truncate(SableArray **slot, const SableMetadata *type, int64_t count) {
    if (*slot == NULL) {
        if (count != 0) {
            trap_here("array index out of range");
        }
        return;
    }
    if (count < 0 || count > (*slot)->count) {
        trap_here("array index out of range");
    }

    SableArray *array = unique(slot, type, 0);
    if (type->destroy_element != NULL) {
        for (int64_t i = count; i < array->count; i++) {
            element_destroy(type, array->elements + i * type->element_size);
        }
    }
    array->count = count;
}

// The string's UTF-8 bytes [from, to) added at the end of a byte array,
// copied at once: the array made the only reference to its buffer, grown
// by half again at least, as appending one at a time grows it.
void sable_array_append_utf8(SableArray **slot, const SableMetadata *type, const SableString *text, int64_t from, int64_t to) {
    if (from < 0 || to < from || to > text->length) {
        trap_here("UTF-8 offsets out of range");
    }

    int64_t length = to - from;
    if (length == 0) {
        return;
    }

    SableArray *array = unique(slot, type, length);
    if (array->count + length > array->capacity) {
        int64_t old = array->capacity;
        int64_t wanted = array->capacity + array->capacity / 2;
        wanted = wanted < array->count + length ? array->count + length : wanted;
        wanted = wanted < 16 ? 16 : wanted;
        array->capacity = wanted;
        array->elements = resize_elements(array, old, wanted);
    }

    memcpy(array->elements + array->count, text->bytes + from, (size_t)length);
    array->count += length;
}

// The array at '*slot', made the only reference to its buffer, with room for
// 'count' more elements at its end: grown by half as much again at least,
// so that adding a few at a time copies each a bounded number of times.
static SableArray *with_room(SableArray **slot, const SableMetadata *type, int64_t count) {
    int64_t size = type->element_size;
    SableArray *array = unique(slot, type, count);
    if (array->count + count > array->capacity) {
        int64_t old = array->capacity;
        int64_t wanted = array->capacity + array->capacity / 2;
        wanted = wanted < array->count + count ? array->count + count : wanted;
        wanted = wanted < 4 ? 4 : wanted;
        array->capacity = wanted;
        array->elements = resize_elements(array, old * size, wanted * size);
    }

    return array;
}

// The elements of a run of another array added at the end, each copied: the
// buffer it is in is held by the slice that views it, so an array's own run
// is added to a copy of the array.
void sable_array_append_all(SableArray **slot, const SableMetadata *type, const SableArray *buffer, int64_t start, int64_t count) {
    if (count == 0) {
        return;
    }

    const char *source = buffer->elements + start * type->element_size;
    SableArray *array = with_room(slot, type, count);
    char *added = array->elements + array->count * type->element_size;
    elements_copy(type, added, source, count);
    share_stored(array, type, added, count);
    array->count += count;
}

// 'count' copies of the element at 'value' added at the end at once: the
// array made the only reference to its buffer, grown as appending them one
// at a time grows it. A trivial element is copied as bytes — by memset when
// its bytes are all one, as a byte's always are, else by doubling memcpy —
// and any other by its copy witness, once for each.
void sable_array_append_repeating(SableArray **slot, const SableMetadata *type, const void *value, int64_t count) {
    if (count < 0) {
        trap_here("cannot append a negative number of elements");
    }

    if (count == 0) {
        return;
    }

    // No buffer is this long: checked before the sizes below can overflow.
    int64_t size = type->element_size;
    int64_t held = *slot == NULL ? 0 : (*slot)->count;
    if (count > INT64_MAX / 4 / (size > 0 ? size : 1) - held) {
        trap_here("cannot append so many elements");
    }

    SableArray *array = with_room(slot, type, count);

    char *start = array->elements + array->count * size;
    if (type->copy_element != NULL) {
        for (int64_t i = 0; i < count; i++) {
            element_copy(type, start + i * size, value);
        }

        share_stored(array, type, start, 1);
    } else if (size > 0) {
        const unsigned char *bytes = value;
        bool same = true;
        for (int64_t i = 1; i < size && same; i++) {
            same = bytes[i] == bytes[0];
        }

        if (same) {
            memset(start, bytes[0], (size_t)(count * size));
        } else {
            memcpy(start, value, (size_t)size);
            int64_t done = 1;
            while (done < count) {
                int64_t run = done < count - done ? done : count - done;
                memcpy(start + done * size, start, (size_t)(run * size));
                done += run;
            }
        }
    }

    array->count += count;
}

// The 'length' elements at 'base' rotated left by 'by' (0 < by < length):
// the shorter side kept aside, the longer moved over in one go, and the
// shorter put back after it. A permutation of the bytes, so no element is
// retained or released.
static void rotate_elements(char *base, int64_t length, int64_t by, int64_t size) {
    int64_t left = by * size, right = (length - by) * size;
    int64_t aside = left < right ? left : right;
    // One element moved, as an insertion moves it, is set aside on the stack.
    _Alignas(16) char small[64];
    char *scratch = aside <= (int64_t)sizeof small ? small : buffer_alloc(aside);
    if (left <= right) {
        memcpy(scratch, base, (size_t)left);
        memmove(base, base + left, (size_t)right);
        memcpy(base + right, scratch, (size_t)left);
    } else {
        memcpy(scratch, base + left, (size_t)right);
        memmove(base + right, base, (size_t)left);
        memcpy(base, scratch, (size_t)right);
    }

    if (scratch != small) {
        buffer_free(scratch, aside);
    }
}

// How far left a rotation of 'length' elements by 'by' moves them, 'by'
// taken round the length (a negative one rotates right); the range checked
// against 'count' first.
static int64_t rotation(int64_t from, int64_t to, int64_t by, int64_t count) {
    if (from < 0 || to > count || from > to) {
        trap_here("array slice out of range");
    }

    int64_t length = to - from;
    if (by >= -length && by <= length) {
        return by < 0 ? by + length : by == length ? 0 : by;
    }

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
SABLE_SLOW void *sable_array_remove_last(SableArray **slot, const SableMetadata *type) {
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

void sable_heap_visit(void *pointer);

// A slice moved, copied, assigned, destroyed and visited as a value of a type
// known only by its metadata (#297): its buffer counted, its run's bits copied.
static void slice_copy(void *destination, void *source, const SableTypeMetadata *type) {
    (void)type;
    SableSlice *from = source;
    sable_retain(from->buffer);
    memmove(destination, source, sizeof(SableSlice));
}

static void slice_take(void *destination, void *source, const SableTypeMetadata *type) {
    (void)type;
    memmove(destination, source, sizeof(SableSlice));
}

static void slice_assign_copy(void *destination, void *source, const SableTypeMetadata *type) {
    (void)type;
    SableArray *old = ((SableSlice *)destination)->buffer;
    sable_retain(((SableSlice *)source)->buffer);
    memmove(destination, source, sizeof(SableSlice));
    sable_release(old);
}

static void slice_assign_take(void *destination, void *source, const SableTypeMetadata *type) {
    (void)type;
    if (destination == source) {
        return;
    }

    SableArray *old = ((SableSlice *)destination)->buffer;
    memmove(destination, source, sizeof(SableSlice));
    sable_release(old);
}

static void slice_destroy(void *value, const SableTypeMetadata *type) {
    (void)type;
    sable_release(((SableSlice *)value)->buffer);
}

static void slice_visit(void *value, const SableTypeMetadata *type) {
    (void)type;
    sable_heap_visit(((SableSlice *)value)->buffer);
}

static const SableValueWitnesses slice_witnesses = {
    slice_copy, slice_take, slice_assign_copy, slice_assign_take, slice_destroy, slice_visit,
};

// The slice at '*slice', made the only reference to its buffer, so that an
// element can be changed: a shared buffer gives way to a copy of the run
// alone, as an array's copy is of its own elements.
void sable_slice_make_unique(SableSlice *slice, const SableMetadata *type) {
    SableArray *buffer = slice->buffer;
    if (buffer == NULL || uniquely_held(buffer)) {
        return;
    }

    int64_t size = type->element_size;
    SableArray *copy = sable_alloc(type, (int64_t)sizeof(SableArray));
    copy->capacity = slice->count;
    copy->count = slice->count;
    copy->elements = buffer_alloc(copy->capacity * size);
    elements_copy(type, copy->elements, buffer->elements + slice->start * size, copy->count);

    share_copy(copy, buffer);
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
    elements_copy(type, copy->elements, buffer->elements + start * size, count);

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

// The 'count' elements of the run at 'buffer', from 'start', stored over
// those at 'destination', each replaced: what 'f(&xs[1..<3])' writes back,
// and 'xs[1..<3] = s'. The caller has made the destination's buffer the
// only reference to it, so the two runs are not one buffer's.
static void store_run(char *destination, const SableMetadata *type, const SableArray *buffer, int64_t start, int64_t count) {
    int64_t size = type->element_size;
    if (type->copy_element == NULL && type->destroy_element == NULL) {
        if (count > 0 && size > 0) {
            memmove(destination, buffer->elements + start * size, (size_t)(count * size));
        }

        return;
    }

    for (int64_t i = 0; i < count; i++) {
        char *element = destination + i * size;
        element_destroy(type, element);
        element_copy(type, element, buffer->elements + (start + i) * size);
    }
}

// The range [from, to) of a run of 'count' elements checked, and that the
// slice stored over it is as long.
static void check_store(int64_t from, int64_t to, int64_t count, int64_t stored) {
    if (from < 0 || to > count || from > to) {
        trap_here("array slice out of range");
    }

    if (stored != to - from) {
        char message[96];
        snprintf(message, sizeof message, "a slice of count %lld stored over a range of count %lld", (long long)stored, (long long)(to - from));
        trap_here(message);
    }
}

// The slice 'buffer'/'start'/'count' stored over elements [from, to) of the
// array at '*slot', in place: nothing when it is still the view of that
// range, whose elements are the range's already; else the array made the
// only reference to its buffer, and each element replaced.
void sable_array_store_slice(SableArray **slot, const SableMetadata *type, int64_t from, int64_t to, SableArray *buffer, int64_t start, int64_t count) {
    check_store(from, to, sable_array_count(*slot), count);
    if (count == 0 || (buffer == *slot && start == from)) {
        return;
    }

    SableArray *array = unique(slot, type, 0);
    char *stored = array->elements + from * type->element_size;
    store_run(stored, type, buffer, start, count);
    share_stored(array, type, stored, count);
}

// The same, over elements [from, to) of the slice at '*slice'.
void sable_slice_store_slice(SableSlice *slice, const SableMetadata *type, int64_t from, int64_t to, SableArray *buffer, int64_t start, int64_t count) {
    check_store(from, to, slice->count, count);
    if (count == 0 || (buffer == slice->buffer && start == slice->start + from)) {
        return;
    }

    sable_slice_make_unique(slice, type);
    char *stored = slice->buffer->elements + (slice->start + from) * type->element_size;
    store_run(stored, type, buffer, start, count);
    share_stored(slice->buffer, type, stored, count);
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

// ---- Comparing elements by their bits

// Where the two runs of bytes first differ, or their length: sixty-four and
// then sixteen at a time, the last sixteen over again in place of a scalar
// tail, so that only a run of fewer is walked by eight and by one.
static int64_t first_difference(const uint8_t *a, const uint8_t *b, int64_t length) {
    int64_t i = 0;
    while (i + 64 <= length) {
        const char *x = (const char *)a + i, *y = (const char *)b + i;
        if (any16((sable_mask16)((load16(x) ^ load16(y)) | (load16(x + 16) ^ load16(y + 16)) | (load16(x + 32) ^ load16(y + 32)) | (load16(x + 48) ^ load16(y + 48))))) {
            break;
        }

        i += 64;
    }

    while (i + 16 <= length) {
        sable_mask16 differs = (sable_mask16)(load16((const char *)a + i) != load16((const char *)b + i));
        if (any16(differs)) {
            return i + (int64_t)(__builtin_ctzll(mask16_bits(differs)) / MASK16_LANE_BITS);
        }

        i += 16;
    }

    if (i < length && length >= 16) {
        i = length - 16;
        sable_mask16 differs = (sable_mask16)(load16((const char *)a + i) != load16((const char *)b + i));
        return any16(differs) ? i + (int64_t)(__builtin_ctzll(mask16_bits(differs)) / MASK16_LANE_BITS) : length;
    }

    while (i + 8 <= length) {
        uint64_t x, y;
        memcpy(&x, a + i, 8);
        memcpy(&y, b + i, 8);
        if (x != y) {
            return i + __builtin_ctzll(x ^ y) / 8;
        }

        i += 8;
    }

    while (i < length && a[i] == b[i]) {
        i++;
    }

    return i;
}

// Where two slices of elements of 'size' bytes, compared as bytes, first
// differ, or the shorter's count.
int64_t sable_array_mismatch(const SableArray *a, int64_t aStart, int64_t aCount, const SableArray *b, int64_t bStart, int64_t bCount, int64_t size) {
    int64_t shorter = aCount < bCount ? aCount : bCount;
    if (shorter == 0) {
        return 0;
    }

    return first_difference((const uint8_t *)a->elements + aStart * size, (const uint8_t *)b->elements + bStart * size, shorter * size) / size;
}

// The same of a type known only by its metadata: -1 unless it is one whose
// '==' compares its bits, which of the scalars all but the floats ('F16' …
// 'F64', 'BF16') are.
int64_t sable_opaque_mismatch(const SableArray *a, int64_t aStart, int64_t aCount, const SableArray *b, int64_t bStart, int64_t bCount, const SableTypeMetadata *type) {
    if (type->kind != SABLE_KIND_SCALAR || type->name == NULL || type->name[0] == 'F' || (type->name[0] == 'B' && type->name[1] == 'F')) {
        return -1;
    }

    return sable_array_mismatch(a, aStart, aCount, b, bStart, bCount, type->size);
}

// ---- Searching bytes (U8[:]'s own contains, firstIndex and count)

// Where the byte first is in the slice, or -1: memchr, which the C library
// vectorises.
int64_t sable_bytes_find(const SableArray *buffer, int64_t start, int64_t count, uint8_t byte) {
    if (count == 0) {
        return -1;
    }

    const uint8_t *bytes = slice_bytes(buffer, start);
    const uint8_t *found = memchr(bytes, byte, (size_t)count);
    return found == NULL ? -1 : found - bytes;
}

// Where the first byte of the set is in the slice, or -1 (.NET's
// IndexOfAny): memchr for a set of one, a table of the set's bytes
// otherwise.
int64_t sable_bytes_find_any(const SableArray *buffer, int64_t start, int64_t count, const SableArray *setBuffer, int64_t setStart, int64_t setCount) {
    if (count == 0 || setCount == 0) {
        return -1;
    }

    const uint8_t *set = slice_bytes(setBuffer, setStart);
    if (setCount == 1) {
        return sable_bytes_find(buffer, start, count, set[0]);
    }

    bool member[256] = { false };
    for (int64_t i = 0; i < setCount; i++) {
        member[set[i]] = true;
    }

    const uint8_t *bytes = slice_bytes(buffer, start);
    for (int64_t i = 0; i < count; i++) {
        if (member[bytes[i]]) {
            return i;
        }
    }

    return -1;
}

// How many times the byte is in the bytes: four vectors compared a step, the
// matches added up in byte lanes and gathered before they can overflow.
#define SABLE_COUNT_BYTES(attributes, name, V)                                              \
    attributes static int64_t name(const uint8_t *bytes, int64_t count, uint8_t byte) {     \
        const V wanted = (V){ 0 } + byte;                                                   \
        const int64_t width = (int64_t)sizeof(V);                                           \
        int64_t total = 0, i = 0;                                                           \
        while (i + 4 * width <= count) {                                                    \
            V a = { 0 }, b = { 0 }, c = { 0 }, d = { 0 };                                   \
            /* Each lane counts up to 63 in each of the four, and 252 in all. */            \
            int64_t end = i + 4 * width * 63 < count ? i + 4 * width * 63 : count;          \
            for (; i + 4 * width <= end; i += 4 * width) {                                  \
                V p, q, r, s;                                                               \
                memcpy(&p, bytes + i, sizeof p);                                            \
                memcpy(&q, bytes + i + width, sizeof q);                                    \
                memcpy(&r, bytes + i + 2 * width, sizeof r);                                \
                memcpy(&s, bytes + i + 3 * width, sizeof s);                                \
                a -= (V)(p == wanted);                                                      \
                b -= (V)(q == wanted);                                                      \
                c -= (V)(r == wanted);                                                      \
                d -= (V)(s == wanted);                                                      \
            }                                                                               \
            a += b + c + d;                                                                 \
            for (int k = 0; k < (int)sizeof(V); k++) {                                      \
                total += a[k];                                                              \
            }                                                                               \
        }                                                                                   \
        V lanes = { 0 };                                                                    \
        for (; i + width <= count; i += width) {                                            \
            V p;                                                                            \
            memcpy(&p, bytes + i, sizeof p);                                                \
            lanes -= (V)(p == wanted);                                                      \
        }                                                                                   \
        for (int k = 0; k < (int)sizeof(V); k++) {                                          \
            total += lanes[k];                                                              \
        }                                                                                   \
        for (; i < count; i++) {                                                            \
            total += bytes[i] == byte;                                                      \
        }                                                                                   \
        return total;                                                                       \
    }

SABLE_COUNT_BYTES(, count_bytes, sable_bytes16)

#if defined(SABLE_X64)
typedef uint8_t sable_bytes32 __attribute__((vector_size(32)));
SABLE_COUNT_BYTES(SABLE_TARGET_AVX2, count_bytes_avx2, sable_bytes32)
#endif

int64_t sable_bytes_count(const SableArray *buffer, int64_t start, int64_t count, uint8_t byte) {
    const uint8_t *bytes = slice_bytes(buffer, start);
#if defined(SABLE_X64)
    if (count >= 256 && processor_has_avx2()) {
        return count_bytes_avx2(bytes, count, byte);
    }
#endif
    return count_bytes(bytes, count, byte);
}

// Bytes already known to be well-formed UTF-8, as a string: what
// String.fromUtf8 makes once it has validated them.
SableString *sable_string_from_utf8_unchecked(const SableArray *buffer, int64_t start, int64_t count) {
    return count == 0 ? make_counted("", 0, 0) : make_string((const char *)slice_bytes(buffer, start), count);
}

// The first 'count' bytes of the words, ASCII and at most forty, as a string.
SableString *sable_string_from_ascii_words(uint64_t a, uint64_t b, uint64_t c, uint64_t d, uint64_t e, int64_t count) {
    uint64_t words[5] = { a, b, c, d, e };
#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ == __ORDER_BIG_ENDIAN__
    for (int i = 0; i < 5; i++) {
        words[i] = __builtin_bswap64(words[i]);
    }
#endif
    return make_counted((const char *)words, count, count);
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
