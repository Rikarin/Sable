// The Sable runtime: type metadata (docs/06 §6.2) — the value witnesses
// every trivial type and every single counted reference share, the ones
// the compiler's tables share, and what a program asks of a type's metadata.
// Part of sable_runtime.c, which includes every part in order (see there);
// the records themselves are objects.c's.

// ---- Value witnesses --------------------------------------------------------

// What the compiler's tables share: a value is moved by moving its bits, and
// assigning one destroys what is there first.
void sable_vw_take(void *destination, void *source, const SableTypeMetadata *type) {
    memmove(destination, source, (size_t)type->size);
}

void sable_vw_assign_with_take(void *destination, void *source, const SableTypeMetadata *type) {
    if (destination == source) {
        return;
    }

    type->witnesses->destroy(destination, type);
    memcpy(destination, source, (size_t)type->size);
}

// The copy is made before what it replaces is destroyed: the source may be
// reachable only through the destination.
void sable_vw_assign_with_copy(void *destination, void *source, const SableTypeMetadata *type) {
    unsigned char small[256];
    void *copy = type->size <= (int64_t)sizeof small ? small : malloc((size_t)type->size);
    if (copy == NULL) {
        sable_trap("out of memory");
    }

    type->witnesses->initialize_with_copy(copy, source, type);
    type->witnesses->destroy(destination, type);
    memcpy(destination, copy, (size_t)type->size);
    if (copy != small) {
        free(copy);
    }
}

// A trivial value: its bits.
static void bits_copy(void *destination, void *source, const SableTypeMetadata *type) {
    memmove(destination, source, (size_t)type->size);
}

static void bits_destroy(void *value, const SableTypeMetadata *type) {
    (void)value;
    (void)type;
}

const SableValueWitnesses sable_bits_witnesses = {
    bits_copy, bits_copy, bits_copy, bits_copy, bits_destroy, bits_destroy,
};

// One counted reference, or null: an object, a string, an array, a region,
// an optional reference.
static void reference_copy(void *destination, void *source, const SableTypeMetadata *type) {
    (void)type;
    void *object = *(void **)source;
    sable_retain(object);
    *(void **)destination = object;
}

static void reference_take(void *destination, void *source, const SableTypeMetadata *type) {
    (void)type;
    *(void **)destination = *(void **)source;
}

static void reference_assign_copy(void *destination, void *source, const SableTypeMetadata *type) {
    (void)type;
    void *object = *(void **)source;
    sable_retain(object);
    void *old = *(void **)destination;
    *(void **)destination = object;
    sable_release(old);
}

static void reference_assign_take(void *destination, void *source, const SableTypeMetadata *type) {
    (void)type;
    void *old = *(void **)destination;
    *(void **)destination = *(void **)source;
    if (old != *(void **)destination) {
        sable_release(old);
    }
}

static void reference_destroy(void *value, const SableTypeMetadata *type) {
    (void)type;
    sable_release(*(void **)value);
}

static void reference_visit(void *value, const SableTypeMetadata *type) {
    (void)type;
    sable_heap_visit(*(void **)value);
}

const SableValueWitnesses sable_reference_witnesses = {
    reference_copy, reference_take, reference_assign_copy, reference_assign_take, reference_destroy, reference_visit,
};

// ---- What a program asks of metadata ----------------------------------------

static const SableField *metadata_field(const SableTypeMetadata *type, int64_t index) {
    if (index < 0 || index >= type->field_count) {
        sable_trap("field index out of range");
    }

    return &type->fields[index];
}

static SableString *metadata_text(const char *text) {
    return text == NULL ? make_string("", 0) : make_string(text, (int64_t)strlen(text));
}

SableString *sable_metadata_name(const SableTypeMetadata *type) {
    return metadata_text(type->name);
}

SableString *sable_metadata_field_name(const SableTypeMetadata *type, int64_t index) {
    return metadata_text(metadata_field(type, index)->name);
}

int64_t sable_metadata_field_offset(const SableTypeMetadata *type, int64_t index) {
    return metadata_field(type, index)->offset;
}

const SableTypeMetadata *sable_metadata_field_type(const SableTypeMetadata *type, int64_t index) {
    const SableTypeMetadata *field = metadata_field(type, index)->type;
    if (field == NULL) {
        sable_trap("the metadata has no field types");
    }

    return field;
}

const SableTypeMetadata *sable_metadata_argument(const SableTypeMetadata *type, int64_t index) {
    if (index < 0 || index >= type->argument_count) {
        sable_trap("type argument index out of range");
    }

    return type->arguments[index];
}

// What an existential's box holds (docs/06 §6.2): its type's metadata — of
// the object's own class, for an object, which may be a subclass of the
// one the box was made for. A box's class has the held type as its one
// argument, and the value as its one field.
const SableTypeMetadata *sable_box_value_metadata(const void *box) {
    const SableTypeMetadata *box_type = (*(const SableMetadata *const *)box)->type;
    if (box_type->argument_count == 0) {
        return box_type;
    }

    const SableTypeMetadata *held = box_type->arguments[0];
    if (held->kind == SABLE_KIND_CLASS) {
        const void *object = *(const void *const *)((const char *)box + box_type->fields[0].offset);
        return (*(const SableMetadata *const *)object)->type;
    }

    return held;
}

// ---- Types as values --------------------------------------------------------

// Whether two records describe one type (docs/06 §6.2): within an image the
// linker keeps one record per type, so its address is the type; a Sable
// shared library and the program that loads it each have their own, so
// across them a type is its kind, its size and its name.
bool sable_metadata_equal(const SableTypeMetadata *a, const SableTypeMetadata *b) {
    if (a == b) {
        return true;
    }

    return a != NULL && b != NULL && a->kind == b->kind && a->size == b->size && a->name != NULL && b->name != NULL
           && strcmp(a->name, b->name) == 0;
}

// A hash of the type that agrees with sable_metadata_equal: FNV-1a over its
// name, as the interpreter computes it.
uint64_t sable_metadata_hash(const SableTypeMetadata *type) {
    uint64_t hash = 14695981039346656037ULL;
    for (const unsigned char *at = (const unsigned char *)(type->name == NULL ? "" : type->name); *at != 0; at++) {
        hash = (hash ^ *at) * 1099511628211ULL;
    }

    return hash;
}

// Whether the type, or a superclass, conforms to the protocol named by the
// text's first length bytes.
static bool conforms_by_name(const SableTypeMetadata *type, const char *name, size_t length) {
    char buffer[256];
    if (length >= sizeof buffer) {
        return false;
    }

    memcpy(buffer, name, length);
    buffer[length] = 0;
    SableProtocol protocol = {buffer};
    return sable_type_conformance(type, &protocol) != NULL;
}

// Whether a value of the first type is one of the second — 't.isInstance(x)'
// with t known only at run time: the same type; a subclass of a class, by its
// superclass chain; a type that conforms to each protocol an existential
// names ('any M.Shape & M.Named', read from its name, as protocols are known
// across images; every type is an 'any Any'). An instance of a generic
// protocol is not known to the registry by name, so only its own type is one.
bool sable_metadata_is_subtype(const SableTypeMetadata *type, const SableTypeMetadata *of) {
    if (sable_metadata_equal(type, of)) {
        return true;
    }

    if (of->kind == SABLE_KIND_EXISTENTIAL && of->name != NULL && strncmp(of->name, "any ", 4) == 0) {
        const char *parts = of->name + 4;
        if (strcmp(parts, "Any") == 0) {
            return true;
        }

        if (strchr(parts, '<') != NULL || strchr(parts, '$') != NULL) {
            return false;
        }

        for (;;) {
            const char *end = strstr(parts, " & ");
            size_t length = end == NULL ? strlen(parts) : (size_t)(end - parts);
            if (!conforms_by_name(type, parts, length)) {
                return false;
            }

            if (end == NULL) {
                return true;
            }

            parts = end + 3;
        }
    }

    for (const SableTypeMetadata *at = superclass_of(type); at != NULL; at = superclass_of(at)) {
        if (sable_metadata_equal(at, of)) {
            return true;
        }
    }

    return false;
}

// ---- Generic boxes ----------------------------------------------------------

// A value of a type unspecialised code knows only by its metadata (docs/06
// §6.2, #297): a counted object holding the metadata and then the value, at
// its type's alignment, in a buffer sized by the metadata at run time.
// Copying the value shares the box; it is changed in place only once it is
// the box's only reference (sable_box_make_unique), and destroyed by its
// type's value witness when the last reference goes.
typedef struct SableBox {
    SableObject header;
    const SableTypeMetadata *type;
} SableBox;

static int64_t box_value_offset(const SableTypeMetadata *type) {
    int64_t alignment = type->alignment < 1 ? 1 : type->alignment;
    return ((int64_t)sizeof(SableBox) + alignment - 1) / alignment * alignment;
}

void *sable_box_project(void *box) {
    return (char *)box + box_value_offset(((SableBox *)box)->type);
}

// The type of what the box holds: for an object, of the object's own class.
const SableTypeMetadata *sable_box_type(const void *box) {
    const SableTypeMetadata *type = ((const SableBox *)box)->type;
    if (type->kind == SABLE_KIND_CLASS) {
        const void *object = *(const void *const *)((const char *)box + box_value_offset(type));
        if (object != NULL) {
            return (*(const SableMetadata *const *)object)->type;
        }
    }

    return type;
}

static void box_destroy(void *box) {
    const SableTypeMetadata *type = ((SableBox *)box)->type;
    type->witnesses->destroy(sable_box_project(box), type);
}

static void box_visit(void *box) {
    const SableTypeMetadata *type = ((SableBox *)box)->type;
    type->witnesses->visit(sable_box_project(box), type);
}

static const SableMetadata sable_box_metadata = {
    box_destroy, "Box", 0, NULL, NULL, 0, 16, box_visit, NULL,
};

// A new box for a value of the type, the value not yet in it: all zeros.
void *sable_box_alloc(const SableTypeMetadata *type) {
    SableBox *box = sable_alloc(&sable_box_metadata, box_value_offset(type) + (type->size < 1 ? 1 : type->size));
    box->type = type;
    return box;
}

// What writes out a value of each type unspecialised code was given a box of,
// as an interpolation would: registered by each image as it starts, with the
// type's metadata, and found by it (or, across images, by the type's name).
static struct {
    const SableTypeMetadata *type;
    SableString *(*describe)(void *box);
} *describers;
static int64_t describer_count, describer_capacity;

void sable_register_describer(const SableTypeMetadata *type, SableString *(*describe)(void *box)) {
    if (describer_count == describer_capacity) {
        describer_capacity = describer_capacity == 0 ? 32 : describer_capacity * 2;
        describers = realloc(describers, (size_t)describer_capacity * sizeof *describers);
        if (describers == NULL) {
            sable_trap("out of memory");
        }
    }

    describers[describer_count].type = type;
    describers[describer_count].describe = describe;
    describer_count++;
}

SableString *sable_box_describe(void *box) {
    const SableTypeMetadata *type = ((SableBox *)box)->type;
    for (int64_t i = 0; i < describer_count; i++) {
        if (describers[i].type == type) {
            return describers[i].describe(box);
        }
    }

    for (int64_t i = 0; i < describer_count; i++) {
        if (sable_metadata_equal(describers[i].type, type)) {
            return describers[i].describe(box);
        }
    }

    sable_trap("a value with no description");
}

// The box in the slot made its only reference before its value is changed:
// one another value shares is replaced by a copy of its own.
void sable_box_make_unique(void **slot) {
    SableBox *box = *slot;
    if (!(object_flags(&box->header) & SABLE_IMMORTAL) && __atomic_load_n(&box->header.strong, __ATOMIC_ACQUIRE) == 1) {
        return;
    }

    const SableTypeMetadata *type = box->type;
    if (type->witnesses->initialize_with_copy == NULL) {
        sable_trap("a move-only value is shared");
    }

    void *copy = sable_box_alloc(type);
    type->witnesses->initialize_with_copy(sable_box_project(copy), sable_box_project(box), type);
    *slot = copy;
    sable_release(box);
}
