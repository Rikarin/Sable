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

// The source's reference is handed over: what the destination held is
// released, even the same object, which the source held a count of its own on.
static void reference_assign_take(void *destination, void *source, const SableTypeMetadata *type) {
    (void)type;
    if (destination == source) {
        return;
    }

    void *old = *(void **)destination;
    *(void **)destination = *(void **)source;
    sable_release(old);
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

// ---- Unspecialised code ------------------------------------------------------

// What unspecialised generic code holds a value of a type parameter's type in
// (docs/06 §6.2, #297) is a buffer of the metadata's size: the value is its
// address, and it is copied, moved and destroyed by the value witnesses.

// A value's dynamic type, given the type parameter's metadata: for an
// object, its own class's, which may be a subclass of the one given.
const SableTypeMetadata *sable_opaque_type(const void *value, const SableTypeMetadata *type) {
    if (type->kind == SABLE_KIND_CLASS) {
        const void *object = *(const void *const *)value;
        if (object != NULL) {
            return (*(const SableMetadata *const *)object)->type;
        }
    }

    // What an existential holds, as its metadata says.
    if (type->kind == SABLE_KIND_EXISTENTIAL) {
        const SableTypeMetadata *held = *(const SableTypeMetadata *const *)((const char *)value + sizeof(void *));
        return held == NULL ? type : sable_box_value_metadata(*(const void *const *)value);
    }

    return type;
}

// What writes out a value of each type unspecialised code was given, as an
// interpolation would: registered by each image as it starts, with the
// type's metadata, and found by it (or, across images, by the type's name).
typedef SableString *(*SableDescriber)(void *value, const SableTypeMetadata *type);

static struct {
    const SableTypeMetadata *type;
    SableDescriber describe;
} *describers;
static int64_t describer_count, describer_capacity;

void sable_register_describer(const SableTypeMetadata *type, SableDescriber describe) {
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

SableString *sable_opaque_describe(void *value, const SableTypeMetadata *type) {
    for (int64_t i = 0; i < describer_count; i++) {
        if (describers[i].type == type) {
            return describers[i].describe(value, type);
        }
    }

    for (int64_t i = 0; i < describer_count; i++) {
        if (sable_metadata_equal(describers[i].type, type)) {
            return describers[i].describe(value, type);
        }
    }

    sable_trap("a value with no description");
}

// The same for '"\(x:?)"': what writes a value out as Debug describes it.
static struct {
    const SableTypeMetadata *type;
    SableDescriber describe;
} *debug_describers;
static int64_t debug_describer_count, debug_describer_capacity;

void sable_register_debug_describer(const SableTypeMetadata *type, SableDescriber describe) {
    if (debug_describer_count == debug_describer_capacity) {
        debug_describer_capacity = debug_describer_capacity == 0 ? 16 : debug_describer_capacity * 2;
        debug_describers = realloc(debug_describers, (size_t)debug_describer_capacity * sizeof *debug_describers);
        if (debug_describers == NULL) {
            sable_trap("out of memory");
        }
    }

    debug_describers[debug_describer_count].type = type;
    debug_describers[debug_describer_count].describe = describe;
    debug_describer_count++;
}

SableString *sable_opaque_debug_describe(void *value, const SableTypeMetadata *type) {
    for (int64_t i = 0; i < debug_describer_count; i++) {
        if (debug_describers[i].type == type) {
            return debug_describers[i].describe(value, type);
        }
    }

    for (int64_t i = 0; i < debug_describer_count; i++) {
        if (sable_metadata_equal(debug_describers[i].type, type)) {
            return debug_describers[i].describe(value, type);
        }
    }

    sable_trap("a value with no description");
}

// What boxes an error of each type unspecialised code may throw on as
// 'any Error' (#297): registered by each image as it starts, as describers
// are. The eraser copies the value into a box of its type; the value, which
// the call takes, is then destroyed.
typedef void *(*SableErrorEraser)(void *value, const SableTypeMetadata *type);

static struct {
    const SableTypeMetadata *type;
    SableErrorEraser erase;
} *erasers;
static int64_t eraser_count, eraser_capacity;

void sable_register_error_eraser(const SableTypeMetadata *type, SableErrorEraser erase) {
    if (eraser_count == eraser_capacity) {
        eraser_capacity = eraser_capacity == 0 ? 16 : eraser_capacity * 2;
        erasers = realloc(erasers, (size_t)eraser_capacity * sizeof *erasers);
        if (erasers == NULL) {
            sable_trap("out of memory");
        }
    }

    erasers[eraser_count].type = type;
    erasers[eraser_count].erase = erase;
    eraser_count++;
}

void *sable_opaque_erase_error(void *value, const SableTypeMetadata *type) {
    SableErrorEraser erase = NULL;
    for (int64_t i = 0; i < eraser_count && erase == NULL; i++) {
        if (erasers[i].type == type) {
            erase = erasers[i].erase;
        }
    }

    for (int64_t i = 0; i < eraser_count && erase == NULL; i++) {
        if (sable_metadata_equal(erasers[i].type, type)) {
            erase = erasers[i].erase;
        }
    }

    if (erase == NULL) {
        sable_trap("an error with no way to box it as 'any Error'");
    }

    void *boxed = erase(value, type);
    type->witnesses->destroy(value, type);
    return boxed;
}

// ---- Opening a Type (#297 stage 6) -------------------------------------------

// Never's table of its conformance to Error, and to what Error refines: it
// has no values to call anything on, and no record. Its metadata is '()'s.
static const struct SableNothingTable {
    const SableProtocol *protocol;
    int64_t associated_type_count;
    const SableTypeMetadata *const *associated_types;
    int64_t associated_conformance_count;
    const SableAssociatedConformance *associated_conformances;
    int64_t base_count;
    const SableWitnessTable *const *bases;
    int64_t requirement_count;
    void *const requirements[8];
} nothing_table;

static const SableWitnessTable *const nothing_bases[8] = {
    (const SableWitnessTable *)&nothing_table, (const SableWitnessTable *)&nothing_table, (const SableWitnessTable *)&nothing_table,
    (const SableWitnessTable *)&nothing_table, (const SableWitnessTable *)&nothing_table, (const SableWitnessTable *)&nothing_table,
    (const SableWitnessTable *)&nothing_table, (const SableWitnessTable *)&nothing_table,
};

static const struct SableNothingTable nothing_table = {NULL, 0, NULL, 0, NULL, 8, nothing_bases, 8, {NULL}};

static bool is_nothing(const SableTypeMetadata *type, const SableProtocol *protocol) {
    return type->size == 0 && type->name != NULL && strcmp(type->name, "()") == 0 && protocol->name != NULL
           && (strcmp(protocol->name, "Sable.Core.Error") == 0 || strcmp(protocol->name, "Sable.Core.Debug") == 0);
}

// A call that opens a Type runs generic code compiled once with the type's
// metadata, and asks the registry for the tables of its conformances: a
// type that does not conform is a trap, naming both.
const SableWitnessTable *sable_witness_table_required(const SableTypeMetadata *type, const SableProtocol *protocol) {
    const SableWitnessTable *table = sable_witness_table(type, protocol);
    if (table == NULL && is_nothing(type, protocol)) {
        return (const SableWitnessTable *)&nothing_table;
    }

    if (table == NULL) {
        char message[512];
        snprintf(message, sizeof message, "'%s' does not conform to '%s'", type->name == NULL ? "?" : type->name,
                 protocol->name == NULL ? "?" : protocol->name);
        trap_here(message);
    }

    return table;
}

// Where such a call's result of the opened type is written: a buffer the
// metadata sizes, all zeros, let go by sable_opaque_box or, when the call
// throws, sable_opaque_free.
void *sable_opaque_alloc(const SableTypeMetadata *type) {
    void *buffer = calloc(1, type->size > 0 ? (size_t)type->size : 1);
    if (buffer == NULL) {
        sable_trap("out of memory");
    }

    return buffer;
}

void sable_opaque_free(void *buffer) {
    free(buffer);
}

// What boxes a value of each type such a call may erase a result of as an
// existential: registered by each image as it starts, as describers are. The
// boxer copies the value into the box an existential keeps a value of its
// type in; the value, which the call takes, is then destroyed, and its
// buffer let go.
typedef void *(*SableBoxer)(void *value, const SableTypeMetadata *type);

static struct {
    const SableTypeMetadata *type;
    SableBoxer box;
} *boxers;
static int64_t boxer_count, boxer_capacity;

void sable_register_boxer(const SableTypeMetadata *type, SableBoxer box) {
    if (boxer_count == boxer_capacity) {
        boxer_capacity = boxer_capacity == 0 ? 16 : boxer_capacity * 2;
        boxers = realloc(boxers, (size_t)boxer_capacity * sizeof *boxers);
        if (boxers == NULL) {
            sable_trap("out of memory");
        }
    }

    boxers[boxer_count].type = type;
    boxers[boxer_count].box = box;
    boxer_count++;
}

static void *box_made(void *value, const SableTypeMetadata *type);

void *sable_opaque_box(void *buffer, const SableTypeMetadata *type) {
    // An existential's value is in a box already: that one, taken.
    if (type->kind == SABLE_KIND_EXISTENTIAL) {
        void *held = *(void **)buffer;
        free(buffer);
        return held;
    }

    SableBoxer box = NULL;
    for (int64_t i = 0; i < boxer_count && box == NULL; i++) {
        if (boxers[i].type == type) {
            box = boxers[i].box;
        }
    }

    for (int64_t i = 0; i < boxer_count && box == NULL; i++) {
        if (sable_metadata_equal(boxers[i].type, type)) {
            box = boxers[i].box;
        }
    }

    // No image knows an instance of a generic type made at run time: its box's class is made here too.
    void *boxed = box != NULL ? box(buffer, type) : box_made(buffer, type);
    type->witnesses->destroy(buffer, type);
    free(buffer);
    return boxed;
}

// 'e as? T' in code that knows T only by its metadata (#297): an existential
// is asked of the caster the image made where the type was known, registered
// as boxers are, or, with none, of the registry; any other type is the one
// the box holds, or for a class one it derives from. The value is copied to
// 'into' when the box holds one.
typedef bool (*SableCaster)(void *box, void *into);

static struct {
    const SableTypeMetadata *type;
    SableCaster cast;
} *casters;
static int64_t caster_count, caster_capacity;

void sable_register_caster(const SableTypeMetadata *type, SableCaster cast) {
    if (caster_count == caster_capacity) {
        caster_capacity = caster_capacity == 0 ? 16 : caster_capacity * 2;
        casters = realloc(casters, (size_t)caster_capacity * sizeof *casters);
        if (casters == NULL) {
            sable_trap("out of memory");
        }
    }

    casters[caster_count].type = type;
    casters[caster_count].cast = cast;
    caster_count++;
}

// The existential of a box's value, its protocols named by the type ('any
// M.Shape & M.Named'): the box, shared, the value's metadata and its tables.
static bool existential_made(void *box, const SableTypeMetadata *held, const SableTypeMetadata *to, void **into) {
    const char *parts = to->name + 4;
    if (strchr(parts, '<') != NULL || strchr(parts, '$') != NULL) {
        return false;
    }

    const void *tables[16];
    int count = 0;
    if (strcmp(parts, "Any") != 0) {
        for (;;) {
            const char *end = strstr(parts, " & ");
            size_t length = end == NULL ? strlen(parts) : (size_t)(end - parts);
            char name[256];
            if (length >= sizeof name || count == 16) {
                return false;
            }

            memcpy(name, parts, length);
            name[length] = 0;
            SableProtocol protocol = {name};
            tables[count] = sable_witness_table(held, &protocol);
            if (tables[count++] == NULL) {
                return false;
            }

            if (end == NULL) {
                break;
            }

            parts = end + 3;
        }
    }

    sable_retain(box);
    into[0] = box;
    memcpy((char *)into + sizeof(void *), &held, sizeof held);
    memcpy(into + 2, tables, (size_t)count * sizeof tables[0]);
    return true;
}

bool sable_existential_cast(void *box, const SableTypeMetadata *to, void *into) {
    const SableTypeMetadata *held = sable_box_value_metadata(box);
    if (to->kind == SABLE_KIND_EXISTENTIAL && to->name != NULL && strncmp(to->name, "any ", 4) == 0) {
        for (int64_t i = 0; i < caster_count; i++) {
            if (casters[i].type == to) {
                return casters[i].cast(box, into);
            }
        }

        for (int64_t i = 0; i < caster_count; i++) {
            if (sable_metadata_equal(casters[i].type, to)) {
                return casters[i].cast(box, into);
            }
        }

        return existential_made(box, held, to, into);
    }

    if (!sable_metadata_is_subtype(held, to)) {
        return false;
    }

    int64_t offset = to->alignment > (int64_t)sizeof(SableObject) ? to->alignment : (int64_t)sizeof(SableObject);
    to->witnesses->initialize_with_copy(into, (char *)box + offset, to);
    return true;
}

// The box at *slot made an existential's own before a witness changes the
// value it holds (#299: a mutating requirement, a setter): when another
// existential shares it, a new box of the same class, holding a copy of the
// value, takes its place. Returns where the value is: just past the
// header, aligned as its type is — what compiled code's 'box_project'
// computes inline.
void *sable_box_unique(void **slot, const SableTypeMetadata *type) {
    SableObject *box = *slot;
    int64_t offset = type->alignment > (int64_t)sizeof(SableObject) ? type->alignment : (int64_t)sizeof(SableObject);
    if (strong_count(box) == 1 && !(object_flags(box) & SABLE_IMMORTAL)) {
        return (char *)box + offset;
    }

    SableObject *fresh = sable_alloc(box->type, box->type->size);
    type->witnesses->initialize_with_copy((char *)fresh + offset, (char *)box + offset, type);
    *slot = fresh;
    sable_release(box);
    return (char *)fresh + offset;
}

// A call's cache of the registry's answer for the type it was first asked
// about (#297): the first table found is kept, once, and read without a
// lock; a call asked about another type asks the registry. Null is not kept,
// as a library loaded later may register the conformance.
typedef struct SableWitnessCacheEntry {
    const SableTypeMetadata *type;
    const SableWitnessTable *table;
} SableWitnessCacheEntry;

const SableWitnessTable *sable_witness_table_cached(SableWitnessCacheEntry **cache, const SableTypeMetadata *type, const SableProtocol *protocol) {
    SableWitnessCacheEntry *entry = __atomic_load_n(cache, __ATOMIC_ACQUIRE);
    if (entry != NULL && entry->type == type) {
        return entry->table;
    }

    const SableWitnessTable *table = sable_witness_table(type, protocol);
    if (table == NULL || entry != NULL) {
        return table;
    }

    SableWitnessCacheEntry *made = malloc(sizeof *made);
    if (made == NULL) {
        return table;
    }

    made->type = type;
    made->table = table;
    SableWitnessCacheEntry *expected = NULL;
    if (!__atomic_compare_exchange_n(cache, &expected, made, false, __ATOMIC_RELEASE, __ATOMIC_RELAXED)) {
        free(made);
    }

    return table;
}

// ---- Optionals, enums, structs and tuples of a type known only at run time --

// An optional, an enum or union, a struct or a tuple with a type parameter's
// value among its parts (#297), as unspecialised code holds one: its metadata
// is made at run time from its parts' — once per set of parts, kept for the
// program's life and at each call that asks — and lays the value out as the
// compiler lays out the same type where it knows its parts (TypeLayout), so
// that code that knows the type passes its own value where unspecialised code
// takes one:
//
// - an enum of two cases, one holding an object or a string and the other
//   nothing, is the reference, null for the empty case;
// - one holding a Bool or a Char, the rest nothing, is that scalar, each empty
//   case one of the values it never takes, in order: 2 on, or U+110000 on;
// - any other is a tag (a byte, or four past 256 cases) and room for the
//   largest payload after it, aligned for the most aligned one;
// - a struct or a tuple is its fields, each at the next offset its alignment
//   allows, as C lays a struct out.
//
// Its copy and destroy go to the active case's payload or each field.
// What a type argument must conform to for a conditional conformance to
// hold: 'extension Box: P where T: P' requires argument 0 conform to P.
typedef struct SableShapeRequirement {
    int64_t argument;
    const SableProtocol *protocol;
} SableShapeRequirement;

// A conformance a generic type's declaration has: one that always holds
// (no requirements), or one that holds where its arguments meet them.
typedef struct SableShapeConformance {
    const SableProtocol *protocol;
    int64_t requirement_count;
    const SableShapeRequirement *requirements;
} SableShapeConformance;

typedef struct SableAggregateShape {
    int64_t kind;                  // SABLE_KIND_OPTIONAL, _ENUM, _UNION or _STRUCT
    int64_t part_count;            // its cases or fields
    const char *const *part_names; // each case's or field's name
    const char *name;              // a generic type's, without its arguments: 'Sable.Core.Outcome'
    int64_t argument_count;        // its type arguments, whose metadata follows the parts'
    int64_t conformance_count;     // what its declaration conforms it to
    const SableShapeConformance *conformances;
    const char *const *part_labels; // each case's payload's labels, 'width, height', or null; null for a type with none
} SableAggregateShape;

// The conformance records of an instance of a generic type made at run time
// (#297): each its declaration has that holds for these arguments — a
// conditional one where each argument it constrains conforms — as the
// compiler gives an instance it knows the arguments of. The registry finds
// its declaration's template tables (sable_register_template).
static void instance_conformances(SableTypeMetadata *type, const SableAggregateShape *shape) {
    if (shape == NULL || shape->conformance_count == 0) {
        return;
    }

    SableConformance *records = calloc((size_t)shape->conformance_count, sizeof *records);
    if (records == NULL) {
        sable_trap("out of memory");
    }

    int64_t count = 0;
    for (int64_t i = 0; i < shape->conformance_count; i++) {
        const SableShapeConformance *conformance = &shape->conformances[i];
        bool holds = true;
        for (int64_t j = 0; j < conformance->requirement_count && holds; j++) {
            const SableShapeRequirement *requirement = &conformance->requirements[j];
            holds = requirement->argument < type->argument_count
                    && sable_type_conformance(type->arguments[requirement->argument], requirement->protocol) != NULL;
        }

        if (holds) {
            records[count].protocol = conformance->protocol;
            count++;
        }
    }

    type->conformance_count = count;
    type->conformances = records;
}

enum { LAYOUT_TAGGED = 0, LAYOUT_NULLABLE = 1, LAYOUT_BOOL = 2, LAYOUT_CHAR = 3, LAYOUT_FIELDS = 4 };

typedef struct SableAggregateInstance {
    SableTypeMetadata type;
    const SableAggregateShape *shape; // null for a tuple
    int64_t layout;                   // LAYOUT_*
    int64_t payload_offset;           // where an enum's payload is
    int64_t tag_bytes;                // a tagged enum's tag
    int64_t some;                     // the case with the payload, for a nullable or niche layout
    int64_t spare;                    // a niche's first value no payload takes
    int64_t part_count;               // its cases or fields
    int64_t key_count;                // and its type arguments after them
    const SableTypeMetadata **parts;  // each case's payload or null, each field, then each type argument
    SableField *fields;
    struct SableAggregateInstance *next;
} SableAggregateInstance;

static const SableAggregateInstance *aggregate_of(const SableTypeMetadata *type) {
    return (const SableAggregateInstance *)(const void *)type;
}

static int64_t round_up(int64_t offset, int64_t align) {
    return (offset + align - 1) / align * align;
}

int64_t sable_enum_tag(const void *value, const SableTypeMetadata *type) {
    const SableAggregateInstance *instance = aggregate_of(type);
    switch (instance->layout) {
    case LAYOUT_NULLABLE:
        return *(void *const *)value == NULL ? 1 - instance->some : instance->some;
    case LAYOUT_BOOL:
    case LAYOUT_CHAR: {
        int64_t scalar = instance->layout == LAYOUT_BOOL ? *(const uint8_t *)value : *(const uint32_t *)value;
        if (scalar < instance->spare) {
            return instance->some;
        }

        // The (scalar - spare)th case without a payload.
        int64_t empty = scalar - instance->spare;
        for (int64_t i = 0; i < instance->part_count; i++) {
            if (instance->parts[i] == NULL && empty-- == 0) {
                return i;
            }
        }

        sable_trap("an enum holds no case");
    }
    default:
        return instance->tag_bytes == 1 ? *(const uint8_t *)value : *(const uint32_t *)value;
    }
}

void *sable_enum_payload(void *value, const SableTypeMetadata *type) {
    return (char *)value + aggregate_of(type)->payload_offset;
}

// The case written, once its payload is in place: its tag, or a niche's or a
// null reference's value for an empty case.
void sable_enum_inject(void *value, int64_t tag, const SableTypeMetadata *type) {
    const SableAggregateInstance *instance = aggregate_of(type);
    switch (instance->layout) {
    case LAYOUT_NULLABLE:
        if (tag != instance->some) {
            *(void **)value = NULL;
        }

        return;
    case LAYOUT_BOOL:
    case LAYOUT_CHAR: {
        if (tag == instance->some) {
            return;
        }

        int64_t scalar = instance->spare;
        for (int64_t i = 0; i < tag; i++) {
            scalar += instance->parts[i] == NULL;
        }

        if (instance->layout == LAYOUT_BOOL) {
            *(uint8_t *)value = (uint8_t)scalar;
        } else {
            *(uint32_t *)value = (uint32_t)scalar;
        }

        return;
    }
    default:
        if (instance->tag_bytes == 1) {
            *(uint8_t *)value = (uint8_t)tag;
        } else {
            *(uint32_t *)value = (uint32_t)tag;
        }
    }
}

// Where a struct's or a tuple's field is.
void *sable_field_address(void *value, int64_t index, const SableTypeMetadata *type) {
    return (char *)value + type->fields[index].offset;
}

// The witnesses of a tagged enum, a struct or a tuple that holds references:
// the active case's payload's, or each field's, where it is.
static void aggregate_copy(void *destination, void *source, const SableTypeMetadata *type) {
    const SableAggregateInstance *instance = aggregate_of(type);
    memmove(destination, source, (size_t)type->size);
    if (instance->layout == LAYOUT_FIELDS) {
        for (int64_t i = 0; i < instance->part_count; i++) {
            const SableTypeMetadata *part = instance->parts[i];
            int64_t offset = type->fields[i].offset;
            part->witnesses->initialize_with_copy((char *)destination + offset, (char *)source + offset, part);
        }

        return;
    }

    const SableTypeMetadata *payload = instance->parts[sable_enum_tag(source, type)];
    if (payload != NULL) {
        int64_t offset = instance->payload_offset;
        payload->witnesses->initialize_with_copy((char *)destination + offset, (char *)source + offset, payload);
    }
}

static void aggregate_destroy(void *value, const SableTypeMetadata *type) {
    const SableAggregateInstance *instance = aggregate_of(type);
    if (instance->layout == LAYOUT_FIELDS) {
        for (int64_t i = 0; i < instance->part_count; i++) {
            const SableTypeMetadata *part = instance->parts[i];
            part->witnesses->destroy((char *)value + type->fields[i].offset, part);
        }

        return;
    }

    const SableTypeMetadata *payload = instance->parts[sable_enum_tag(value, type)];
    if (payload != NULL) {
        payload->witnesses->destroy((char *)value + instance->payload_offset, payload);
    }
}

static void aggregate_visit(void *value, const SableTypeMetadata *type) {
    const SableAggregateInstance *instance = aggregate_of(type);
    if (instance->layout == LAYOUT_FIELDS) {
        for (int64_t i = 0; i < instance->part_count; i++) {
            const SableTypeMetadata *part = instance->parts[i];
            part->witnesses->visit((char *)value + type->fields[i].offset, part);
        }

        return;
    }

    const SableTypeMetadata *payload = instance->parts[sable_enum_tag(value, type)];
    if (payload != NULL) {
        payload->witnesses->visit((char *)value + instance->payload_offset, payload);
    }
}

static const SableValueWitnesses aggregate_witnesses = {
    aggregate_copy, sable_vw_take, sable_vw_assign_with_copy, sable_vw_assign_with_take, aggregate_destroy, aggregate_visit,
};

static SableAggregateInstance *aggregate_instances;

static size_t name_length(const SableTypeMetadata *type) {
    return type == NULL || type->name == NULL ? 0 : strlen(type->name);
}

static const char *name_of(const SableTypeMetadata *type) {
    return type == NULL || type->name == NULL ? "" : type->name;
}

// What a case's payload is, its labels written: '(width: Int, height: M.Label)'
// for 'width, height' and what they label: a tuple of them, or the one.
static char *labelled_name(const char *labels, const SableTypeMetadata *payload) {
    int64_t count = 1;
    for (const char *at = labels; *at != 0; at++) {
        count += *at == ',';
    }

    if (count > 1 && payload->argument_count != count) {
        return NULL;
    }

    size_t length = strlen(labels) + 3;
    for (int64_t i = 0; i < count; i++) {
        length += 2 + name_length(count == 1 ? payload : payload->arguments[i]);
    }

    char *name = malloc(length);
    if (name == NULL) {
        sable_trap("out of memory");
    }

    char *to = name;
    *to++ = '(';
    const char *label = labels;
    for (int64_t i = 0; i < count; i++) {
        const char *end = strchr(label, ',');
        size_t label_length = end == NULL ? strlen(label) : (size_t)(end - label);
        const char *type = name_of(count == 1 ? payload : payload->arguments[i]);
        if (i > 0) {
            *to++ = ',';
            *to++ = ' ';
        }

        memcpy(to, label, label_length);
        to += label_length;
        *to++ = ':';
        *to++ = ' ';
        memcpy(to, type, strlen(type));
        to += strlen(type);
        label = end == NULL ? label : end + 2;
    }

    *to++ = ')';
    *to = 0;
    return name;
}

// The parts' names joined, between an opening and a closing text.
static char *joined_name(const char *before, int64_t count, const SableTypeMetadata *const *parts, const char *after) {
    size_t length = strlen(before) + strlen(after) + 1;
    for (int64_t i = 0; i < count; i++) {
        length += name_length(parts[i]) + 2;
    }

    char *name = malloc(length);
    if (name == NULL) {
        sable_trap("out of memory");
    }

    strcpy(name, before);
    for (int64_t i = 0; i < count; i++) {
        if (i > 0) {
            strcat(name, ", ");
        }

        strcat(name, name_of(parts[i]));
    }

    strcat(name, after);
    return name;
}

// The name it is written with: 'Int?', '(Int, String)', 'M.Box<Int>'.
static char *aggregate_name(const SableAggregateInstance *made) {
    const SableAggregateShape *shape = made->shape;
    if (shape == NULL) {
        return joined_name("(", made->part_count, made->parts, ")");
    }

    if (shape->kind == SABLE_KIND_OPTIONAL) {
        return joined_name("", 1, &made->parts[made->some], "?");
    }

    if (shape->argument_count == 0) {
        return joined_name(shape->name, 0, made->parts, "");
    }

    size_t base = strlen(shape->name);
    char *before = malloc(base + 2);
    if (before == NULL) {
        sable_trap("out of memory");
    }

    memcpy(before, shape->name, base);
    before[base] = '<';
    before[base + 1] = 0;
    char *name = joined_name(before, shape->argument_count, made->parts + made->part_count, ">");
    free(before);
    return name;
}

static void lay_out_enum(SableAggregateInstance *made) {
    const SableTypeMetadata *payload = NULL;
    int64_t payloads = 0, empties = 0;
    for (int64_t i = 0; i < made->part_count; i++) {
        if (made->parts[i] != NULL) {
            payload = made->parts[i];
            made->some = i;
            payloads++;
        } else {
            empties++;
        }
    }

    SableTypeMetadata *type = &made->type;
    if (made->part_count == 2 && payloads == 1 && (payload->flags & SABLE_TYPE_NULLABLE) != 0) {
        made->layout = LAYOUT_NULLABLE;
        type->size = type->alignment = (int64_t)sizeof(void *);
        type->witnesses = &sable_reference_witnesses;
        return;
    }

    if (payloads == 1 && (payload->flags & SABLE_TYPE_BOOL) != 0 && empties <= 254) {
        made->layout = LAYOUT_BOOL;
        made->spare = 2;
        type->size = type->alignment = 1;
        type->flags |= SABLE_TYPE_TRIVIAL;
        type->witnesses = &sable_bits_witnesses;
        return;
    }

    if (payloads == 1 && (payload->flags & SABLE_TYPE_CHAR) != 0 && empties <= 0x7FFFFFFF - 0x110000) {
        made->layout = LAYOUT_CHAR;
        made->spare = 0x110000;
        type->size = type->alignment = 4;
        type->flags |= SABLE_TYPE_TRIVIAL;
        type->witnesses = &sable_bits_witnesses;
        return;
    }

    made->layout = LAYOUT_TAGGED;
    made->tag_bytes = made->part_count <= 256 ? 1 : 4;
    int64_t size = 0, unit = 1;
    bool trivial = true;
    for (int64_t i = 0; i < made->part_count; i++) {
        if (made->parts[i] != NULL) {
            size = made->parts[i]->size > size ? made->parts[i]->size : size;
            unit = made->parts[i]->alignment > unit ? made->parts[i]->alignment : unit;
            trivial = trivial && (made->parts[i]->flags & SABLE_TYPE_TRIVIAL) != 0;
        }
    }

    if (size == 0) {
        unit = 1;
    }

    int64_t count = (size + unit - 1) / unit;
    made->payload_offset = round_up(made->tag_bytes, unit);
    int64_t align = unit > made->tag_bytes ? unit : made->tag_bytes;
    int64_t end = count == 0 ? made->tag_bytes : made->payload_offset + count * unit;
    type->size = round_up(end, align);
    type->alignment = align;
    type->flags |= trivial ? SABLE_TYPE_TRIVIAL : 0;
    type->witnesses = trivial ? &sable_bits_witnesses : &aggregate_witnesses;
}

static void lay_out_fields(SableAggregateInstance *made) {
    SableTypeMetadata *type = &made->type;
    int64_t end = 0, align = 1;
    bool trivial = true;
    for (int64_t i = 0; i < made->part_count; i++) {
        const SableTypeMetadata *part = made->parts[i];
        int64_t part_align = part->alignment < 1 ? 1 : part->alignment;
        int64_t at = round_up(end, part_align);
        made->fields[i].offset = at;
        end = at + part->size;
        align = part_align > align ? part_align : align;
        trivial = trivial && (part->flags & SABLE_TYPE_TRIVIAL) != 0;
    }

    made->layout = LAYOUT_FIELDS;
    type->size = round_up(end, align);
    type->alignment = align;
    type->flags |= trivial ? SABLE_TYPE_TRIVIAL : 0;
    type->witnesses = trivial ? &sable_bits_witnesses : &aggregate_witnesses;
}

static bool aggregate_matches(const SableAggregateInstance *at, const SableAggregateShape *shape, int64_t count, const SableTypeMetadata *const *parts) {
    if (at->shape != shape || at->key_count != count) {
        return false;
    }

    for (int64_t i = 0; i < count; i++) {
        if (at->parts[i] != parts[i]) {
            return false;
        }
    }

    return true;
}

static SableAggregateInstance *aggregate_make(const SableAggregateShape *shape, int64_t count, const SableTypeMetadata *const *parts) {
    SableAggregateInstance *made = calloc(1, sizeof *made);
    const SableTypeMetadata **kept = calloc((size_t)(count > 0 ? count : 1), sizeof *kept);
    int64_t part_count = shape == NULL ? count : shape->part_count;
    SableField *fields = calloc((size_t)(part_count > 0 ? part_count : 1), sizeof *fields);
    if (made == NULL || kept == NULL || fields == NULL) {
        sable_trap("out of memory");
    }

    memcpy(kept, parts, (size_t)count * sizeof *kept);
    bool optional = shape != NULL && shape->kind == SABLE_KIND_OPTIONAL;
    bool enumeration = shape != NULL && shape->kind != SABLE_KIND_STRUCT;
    made->shape = shape;
    made->part_count = part_count;
    made->key_count = count;
    made->parts = kept;
    made->fields = fields;
    made->type.kind = shape == NULL ? SABLE_KIND_TUPLE : shape->kind;
    made->type.field_count = part_count;
    made->type.fields = fields;
    made->type.flags = SABLE_TYPE_FIELDS | SABLE_TYPE_FIELD_TYPES;
    if (enumeration) {
        lay_out_enum(made);
    } else {
        lay_out_fields(made);
    }

    made->type.name = aggregate_name(made);
    made->type.argument_count = optional ? 1 : shape == NULL ? part_count : shape->argument_count;
    made->type.arguments = optional ? &kept[made->some] : shape == NULL ? kept : kept + part_count;
    made->type.stride = round_up(made->type.size, made->type.alignment);
    instance_conformances(&made->type, optional ? NULL : shape);
    for (int64_t i = 0; i < part_count; i++) {
        fields[i].type = parts[i];
        fields[i].type_name = name_of(parts[i]);
        fields[i].kind = parts[i] == NULL || (parts[i]->flags & SABLE_TYPE_TRIVIAL) != 0 ? SABLE_FIELD_DATA : SABLE_FIELD_AGGREGATE;
        if (enumeration) {
            fields[i].offset = made->payload_offset;
            if (shape != NULL && shape->part_labels != NULL && shape->part_labels[i] != NULL && parts[i] != NULL) {
                const char *labelled = labelled_name(shape->part_labels[i], parts[i]);
                fields[i].type_name = labelled == NULL ? fields[i].type_name : labelled;
            }
        }

        if (shape != NULL) {
            fields[i].name = shape->part_names[i];
            continue;
        }

        char *index = malloc(24);
        if (index == NULL) {
            sable_trap("out of memory");
        }

        snprintf(index, 24, "%lld", (long long)i);
        fields[i].name = index;
    }

    return made;
}

static const SableTypeMetadata *aggregate_instance(void **cache, const SableAggregateShape *shape, int64_t count, const SableTypeMetadata *const *parts) {
    SableAggregateInstance *known = __atomic_load_n((SableAggregateInstance **)cache, __ATOMIC_ACQUIRE);
    if (known != NULL && aggregate_matches(known, shape, count, parts)) {
        return &known->type;
    }

    SableAggregateInstance *found = NULL;
    for (SableAggregateInstance *at = __atomic_load_n(&aggregate_instances, __ATOMIC_ACQUIRE); at != NULL; at = at->next) {
        if (aggregate_matches(at, shape, count, parts)) {
            found = at;
            break;
        }
    }

    if (found == NULL) {
        found = aggregate_make(shape, count, parts);

        // Another thread may have made one meanwhile: either serves, and both are kept.
        found->next = __atomic_load_n(&aggregate_instances, __ATOMIC_ACQUIRE);
        while (!__atomic_compare_exchange_n(&aggregate_instances, &found->next, found, true, __ATOMIC_RELEASE, __ATOMIC_ACQUIRE)) {
        }
    }

    if (known == NULL) {
        __atomic_store_n((SableAggregateInstance **)cache, found, __ATOMIC_RELEASE);
    }

    return &found->type;
}

// An optional's, an enum's or a struct's metadata: its parts' (null for a
// case without a payload), then its type arguments'.
const SableTypeMetadata *sable_aggregate_type(void **cache, const SableAggregateShape *shape, const SableTypeMetadata *const *parts) {
    return aggregate_instance(cache, shape, shape->part_count + shape->argument_count, parts);
}

const SableTypeMetadata *sable_tuple_type(void **cache, int64_t count, const SableTypeMetadata *const *elements) {
    return aggregate_instance(cache, NULL, count, elements);
}

// ---- Generic classes of a type known only at run time ---------------------

// A generic class whose code is compiled once (#297): its metadata — the
// class's type metadata and its object metadata — made at run time from its
// fields' and its type arguments' metadata, laid out as the compiler lays out
// an object where it knows the types (TypeLayout.Object): the header, then
// each field at the next offset its alignment allows. Such a class is never
// inherited from, so its object metadata has no vtable, and its destroy runs
// its deinit, when it has one — compiled once for every instance, and
// registered by its generic class's name as an image starts — and then
// destroys its fields, through their value witnesses.
//
// An instance the program's own code made where it knew the types — a static
// 'Sable.Collections.Deque<Int>' — is registered as the image starts, and is
// what the runtime gives for those arguments: a class's metadata is its
// identity to 'is' and 'as?', so there is one per set of arguments.
typedef struct SableClassInstance {
    SableTypeMetadata type; // first: what the object metadata's 'type' points to
    SableMetadata object;
    const SableAggregateShape *shape;
    const SableTypeMetadata **parts; // each field's, then each type argument's
    SableField *fields;
    void (*deinit)(void *object); // null: it has none
    struct SableClassInstance *next;
} SableClassInstance;

static SableClassInstance *class_instances;
static SpinLock class_lock;

static struct {
    const char *name; // 'M.Cell'
    void (*deinit)(void *object);
} *generic_deinits;
static int64_t generic_deinit_count, generic_deinit_capacity;

void sable_register_generic_deinit(const char *name, void (*deinit)(void *object)) {
    if (generic_deinit_count == generic_deinit_capacity) {
        generic_deinit_capacity = generic_deinit_capacity == 0 ? 8 : generic_deinit_capacity * 2;
        generic_deinits = realloc(generic_deinits, (size_t)generic_deinit_capacity * sizeof *generic_deinits);
        if (generic_deinits == NULL) {
            sable_trap("out of memory");
        }
    }

    generic_deinits[generic_deinit_count].name = name;
    generic_deinits[generic_deinit_count].deinit = deinit;
    generic_deinit_count++;
}

static struct {
    const SableAggregateShape *shape;
    const SableTypeMetadata *type;
} *registered_classes;
static int64_t registered_class_count, registered_class_capacity;

void sable_register_generic_class(const SableAggregateShape *shape, const SableTypeMetadata *type) {
    spin_lock(&class_lock);
    if (registered_class_count == registered_class_capacity) {
        registered_class_capacity = registered_class_capacity == 0 ? 32 : registered_class_capacity * 2;
        registered_classes = realloc(registered_classes, (size_t)registered_class_capacity * sizeof *registered_classes);
        if (registered_classes == NULL) {
            sable_trap("out of memory");
        }
    }

    registered_classes[registered_class_count].shape = shape;
    registered_classes[registered_class_count].type = type;
    registered_class_count++;
    spin_unlock(&class_lock);
}

static bool same_shape(const SableAggregateShape *a, const SableAggregateShape *b) {
    return a == b || (a->name != NULL && b->name != NULL && strcmp(a->name, b->name) == 0 && a->argument_count == b->argument_count);
}

// Whether the class's metadata has these type arguments: the same records,
// or, for one made elsewhere, records of the same types.
static bool class_arguments_are(const SableTypeMetadata *type, int64_t count, const SableTypeMetadata *const *arguments, bool exactly) {
    if (type->argument_count != count) {
        return false;
    }

    for (int64_t i = 0; i < count; i++) {
        if (type->arguments[i] != arguments[i] && (exactly || !sable_metadata_equal(type->arguments[i], arguments[i]))) {
            return false;
        }
    }

    return true;
}

static const SableClassInstance *class_instance_of(const void *object) {
    return (const SableClassInstance *)(const void *)(*(const SableMetadata *const *)object)->type;
}

// Runs an object's deinit, unless its initialiser threw, and destroys its
// fields, last first, as a class's compiled destroy does.
static void class_destroy(void *object) {
    const SableClassInstance *instance = class_instance_of(object);
    if (instance->deinit != NULL && (__atomic_load_n(&((SableObject *)object)->flags, __ATOMIC_RELAXED) & SABLE_UNMADE) == 0) {
        instance->deinit(object);
    }

    for (int64_t i = instance->shape->part_count - 1; i >= 0; i--) {
        const SableTypeMetadata *part = instance->parts[i];
        part->witnesses->destroy((char *)object + instance->fields[i].offset, part);
    }
}

static void class_visit(void *object) {
    const SableClassInstance *instance = class_instance_of(object);
    for (int64_t i = 0; i < instance->shape->part_count; i++) {
        const SableTypeMetadata *part = instance->parts[i];
        part->witnesses->visit((char *)object + instance->fields[i].offset, part);
    }
}

static SableClassInstance *class_make(const SableAggregateShape *shape, const SableTypeMetadata *const *parts) {
    int64_t field_count = shape->part_count, count = field_count + shape->argument_count;
    SableClassInstance *made = calloc(1, sizeof *made);
    const SableTypeMetadata **kept = calloc((size_t)(count > 0 ? count : 1), sizeof *kept);
    SableField *fields = calloc((size_t)(field_count > 0 ? field_count : 1), sizeof *fields);
    if (made == NULL || kept == NULL || fields == NULL) {
        sable_trap("out of memory");
    }

    memcpy(kept, parts, (size_t)count * sizeof *kept);
    made->shape = shape;
    made->parts = kept;
    made->fields = fields;
    for (int64_t i = 0; i < generic_deinit_count && made->deinit == NULL; i++) {
        if (strcmp(generic_deinits[i].name, shape->name) == 0) {
            made->deinit = generic_deinits[i].deinit;
        }
    }

    // The header — its type, its strong count, its weak count and flags — then the fields.
    int64_t end = (int64_t)(sizeof(void *) + 2 * sizeof(uint32_t)), align = (int64_t)sizeof(void *);
    for (int64_t i = 0; i < field_count; i++) {
        const SableTypeMetadata *part = kept[i];
        int64_t part_align = part->alignment < 1 ? 1 : part->alignment;
        int64_t at = round_up(end, part_align);
        fields[i].name = shape->part_names[i];
        fields[i].type_name = name_of(part);
        fields[i].offset = at;
        fields[i].kind = (part->flags & SABLE_TYPE_TRIVIAL) != 0 ? SABLE_FIELD_DATA
                         : part->kind == SABLE_KIND_CLASS ? SABLE_FIELD_STRONG
                         : SABLE_FIELD_AGGREGATE;
        fields[i].type = part;
        end = at + part->size;
        align = part_align > align ? part_align : align;
    }

    SableTypeMetadata *type = &made->type;
    type->kind = SABLE_KIND_CLASS;
    type->field_count = field_count;
    type->fields = fields;
    type->size = type->alignment = type->stride = (int64_t)sizeof(void *);
    type->flags = SABLE_TYPE_NULLABLE | SABLE_TYPE_FIELDS | SABLE_TYPE_FIELD_TYPES;
    type->witnesses = &sable_reference_witnesses;
    type->argument_count = shape->argument_count;
    type->arguments = kept + field_count;
    type->object = &made->object;
    instance_conformances(type, shape);

    size_t base = strlen(shape->name);
    char *before = malloc(base + 2);
    if (before == NULL) {
        sable_trap("out of memory");
    }

    memcpy(before, shape->name, base);
    before[base] = '<';
    before[base + 1] = 0;
    type->name = shape->argument_count == 0 ? shape->name : joined_name(before, shape->argument_count, type->arguments, ">");
    free(before);

    SableMetadata *object = &made->object;
    object->destroy = class_destroy;
    object->name = type->name;
    object->size = round_up(end, align);
    object->alignment = align;
    object->visit = class_visit;
    object->type = type;
    return made;
}

// A generic class's metadata for these fields' and type arguments' metadata
// (the fields' first): the program's own, registered as it started, or one
// made the first time it is asked for and kept for the program's life — and
// at the call that asked, for the next time.
const SableTypeMetadata *sable_class_type(void **cache, const SableAggregateShape *shape, const SableTypeMetadata *const *parts) {
    const SableTypeMetadata *const *arguments = parts + shape->part_count;
    const SableTypeMetadata *known = __atomic_load_n((const SableTypeMetadata **)cache, __ATOMIC_ACQUIRE);
    if (known != NULL && class_arguments_are(known, shape->argument_count, arguments, true)) {
        return known;
    }

    // Found or made under a lock: two threads asking at once for the same
    // arguments must get the one record, the class's identity.
    const SableTypeMetadata *found = NULL;
    spin_lock(&class_lock);
    for (int64_t i = 0; i < registered_class_count && found == NULL; i++) {
        if (same_shape(registered_classes[i].shape, shape) && class_arguments_are(registered_classes[i].type, shape->argument_count, arguments, false)) {
            found = registered_classes[i].type;
        }
    }

    for (SableClassInstance *at = class_instances; at != NULL && found == NULL; at = at->next) {
        if (same_shape(at->shape, shape) && class_arguments_are(&at->type, shape->argument_count, arguments, false)) {
            found = &at->type;
        }
    }

    if (found == NULL) {
        SableClassInstance *made = class_make(shape, parts);
        made->next = class_instances;
        class_instances = made;
        found = &made->type;
    }

    spin_unlock(&class_lock);

    if (known == NULL) {
        __atomic_store_n((const SableTypeMetadata **)cache, found, __ATOMIC_RELEASE);
    }

    return found;
}

// A box for a value of a type no image boxes, an instance of a generic type
// made at run time (#297): an object of a class made here for the type, laid
// out as a compiled box is, its one field the value and its one type argument
// the type, so that what it holds is found as any box's is.
static void *box_made(void *value, const SableTypeMetadata *type) {
    static const char *const names[] = {"value"};
    static const SableAggregateShape shape = {SABLE_KIND_CLASS, 1, names, "any$box$of", 1, 0, NULL, NULL};
    void *cache = NULL;
    const SableTypeMetadata *parts[] = {type, type};
    const SableTypeMetadata *made = sable_class_type(&cache, &shape, parts);
    void *box = sable_alloc(made->object, made->object->size);
    type->witnesses->initialize_with_copy((char *)box + made->fields[0].offset, value, type);
    return box;
}
