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
