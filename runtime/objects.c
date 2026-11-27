// The Sable runtime: objects — the header every counted object starts with, and what is counted.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Objects -----------------------------------------------------------

// What a type is to the runtime (docs/06 §6.2): its kind, its layout, how
// its values are copied and destroyed, its fields, the protocols it
// conforms to, its type arguments and its name. The compiler emits one per
// type a program asks about, makes, or boxes in an existential — a class's
// is its object metadata's 'type' (SableMetadata below) — once per program:
// a unit's copy is linkonce_odr in a COMDAT, so its address is the type's
// identity. The format only grows, at the end.
enum {
    SABLE_KIND_CLASS = 1,
    SABLE_KIND_STRUCT = 2,
    SABLE_KIND_ENUM = 3,
    SABLE_KIND_UNION = 4,
    SABLE_KIND_OPTIONAL = 5,
    SABLE_KIND_TUPLE = 6,
    SABLE_KIND_FUNCTION = 7,
    SABLE_KIND_ARRAY = 8,
    SABLE_KIND_SLICE = 9,
    SABLE_KIND_EXISTENTIAL = 10,
    SABLE_KIND_METATYPE = 11,
    SABLE_KIND_SCALAR = 12,
    SABLE_KIND_STRING = 13,
    SABLE_KIND_REFERENCE = 14,
    SABLE_KIND_OPAQUE = 15,
    SABLE_KIND_INLINE_ARRAY = 16, // T[k] or T[..k]: elements held where the value is; its argument is the element
};

enum {
    SABLE_TYPE_TRIVIAL = 1,      // copied by copying its bits; destroying it does nothing
    SABLE_TYPE_NONCOPYABLE = 2,  // move-only: the copy witnesses are null
    SABLE_TYPE_HAS_DEINIT = 4,   // destroying one runs a deinit
    SABLE_TYPE_FIELDS = 8,       // the metadata carries its field descriptors
    SABLE_TYPE_FIELD_TYPES = 16, // and each names its field's type's metadata
    SABLE_TYPE_NULLABLE = 32,    // one counted reference, never null: an optional of it is null for none
    SABLE_TYPE_BOOL = 64,        // a Bool: an enum of it and empty cases takes its spare values, 2 on
    SABLE_TYPE_STRUCTURAL = 256, // a structural union, 'A | B': its arguments its cases' types, by name
    SABLE_TYPE_CHAR = 128,       // a Char: an enum of it and empty cases takes the values past U+10FFFF
};

// A stored field: where it is in an instance, and what it holds. The names
// are null in a program built with --deny-reflection. An enum's are its
// cases: each case's name, and its payload's type and offset.
enum {
    SABLE_FIELD_DATA = 0,      // nothing the runtime follows
    SABLE_FIELD_STRONG = 1,    // a strong reference to an object, or null
    SABLE_FIELD_AGGREGATE = 2, // a value holding references: a struct, a union, a closure, ...
    SABLE_FIELD_WEAK = 3,      // a weak or unowned reference: it keeps nothing alive
};

struct SableTypeMetadata;

// A pointer in a record to data (#300): where the object format relocates
// the difference of two addresses at link time — Mach-O and ELF, in 64 bits —
// the distance from the field itself to what it points at, so that a record
// the compiler writes needs no fixup as the program loads and can be
// read-only; elsewhere (COFF, which relocates such a difference only within
// a section, and WebAssembly, which has no fixups to save) the address. 0 is
// null either way, as no field points at itself. The runtime makes its own
// records' the same way (sable_set_ref), any distance apart. Read with
// sable_ref; a record holding one is never copied as it is, since a
// distance means something only where it is. Pointers to code stay
// absolute: a call through one is as quick as ever.
typedef struct SableRef {
    uintptr_t bits;
} SableRef;

#if UINTPTR_MAX > UINT32_MAX && !defined(_WIN32)
#define SABLE_RELATIVE_REFS 1
#else
#define SABLE_RELATIVE_REFS 0
#endif

static inline const void *sable_ref(const SableRef *ref) {
#if SABLE_RELATIVE_REFS
    return ref->bits == 0 ? NULL : (const char *)ref + (intptr_t)ref->bits;
#else
    return (const void *)ref->bits;
#endif
}

static inline void sable_set_ref(SableRef *ref, const void *pointer) {
#if SABLE_RELATIVE_REFS
    ref->bits = pointer == NULL ? 0 : (uintptr_t)((const char *)pointer - (const char *)ref);
#else
    ref->bits = (uintptr_t)pointer;
#endif
}

// What a field points at, read: SABLE_REF(type->name).
#define SABLE_REF(field) sable_ref(&(field))

// An array of SableRefs to each of 'count' records, made where it stays.
static SableRef *sable_ref_array(const void *const *pointers, int64_t count) {
    SableRef *refs = calloc((size_t)(count > 0 ? count : 1), sizeof *refs);
    if (refs == NULL) {
        abort();
    }

    for (int64_t i = 0; i < count; i++) {
        sable_set_ref(&refs[i], pointers[i]);
    }

    return refs;
}

typedef struct SableField {
    SableRef name;      // const char *
    SableRef type_name; // const char *
    int64_t offset;     // from the start of the value — of an object, its header's bytes among them
    int64_t kind;       // SABLE_FIELD_*
    SableRef type;      // const SableTypeMetadata *: null unless SABLE_TYPE_FIELD_TYPES, and for a case with no payload
} SableField;

// A protocol, identified by its descriptor's address, or — across images,
// each of which has its own copy — by its name.
typedef struct SableProtocol {
    SableRef name; // const char *
} SableProtocol;

// What an associated type must conform to: its index, and the protocol.
typedef struct SableAssociatedConformance {
    int64_t associated_type;
    SableRef protocol; // const SableProtocol *
} SableAssociatedConformance;

// A conformance's witness table (#299): its associated types' metadata and
// what each must conform to (found in this registry from that metadata),
// the conformer's tables of the protocols this one refines directly, in the
// order it names them, and then, inline, the requirements the protocol
// itself declares, in declaration order, each in the one calling convention
// both unspecialised code and a call through an existential use: given self
// and the arguments where the caller holds them, where a result of Self
// goes, and Self's metadata. An existential holds a pointer to one per
// protocol, so a call through it is two loads: the table, then the entry.
typedef struct SableWitnessTable {
    SableRef protocol; // const SableProtocol *
    int64_t associated_type_count;
    SableRef associated_types; // SableRef[]: each const SableTypeMetadata *
    int64_t associated_conformance_count;
    SableRef associated_conformances; // const SableAssociatedConformance *
    int64_t base_count;
    SableRef bases; // SableRef[]: each const SableWitnessTable *, or null
    int64_t requirement_count;
    void *const requirements[];
} SableWitnessTable;

// That a type conforms to a protocol: the record a dynamic cast looks up,
// with the witness table an existential of the protocol is built from — or
// null, where the program builds none.
typedef struct SableConformance {
    SableRef protocol;  // const SableProtocol *
    SableRef witnesses; // const SableWitnessTable *, or null
} SableConformance;

// How a value of a type is copied, moved and destroyed in memory, given its
// metadata (which generic code knows a type by). The take witnesses move the
// bits: every Sable value may be moved by copying them. A move-only type's
// copy witnesses are null.
typedef struct SableValueWitnesses {
    void (*initialize_with_copy)(void *destination, void *source, const struct SableTypeMetadata *type);
    void (*initialize_with_take)(void *destination, void *source, const struct SableTypeMetadata *type);
    void (*assign_with_copy)(void *destination, void *source, const struct SableTypeMetadata *type);
    void (*assign_with_take)(void *destination, void *source, const struct SableTypeMetadata *type);
    void (*destroy)(void *value, const struct SableTypeMetadata *type);
    // Hands each strong reference the value holds to sable_heap_visit.
    void (*visit)(void *value, const struct SableTypeMetadata *type);
} SableValueWitnesses;

struct SableMetadata;

typedef struct SableTypeMetadata {
    int64_t kind; // SABLE_KIND_*
    int64_t field_count;
    SableRef fields; // const SableField *: a class's (its superclass's first) always; another type's with SABLE_TYPE_FIELDS
    int64_t conformance_count;
    SableRef conformances; // const SableConformance *: a class's own: its superclass's are the superclass's
    SableRef name; // const char *: as Sable writes it: 'Zoo.Animal?', 'Sable.Collections.Deque<String>'
    int64_t size;
    int64_t alignment;
    int64_t stride;
    int64_t flags; // SABLE_TYPE_*
    const SableValueWitnesses *witnesses;
    // A generic type's arguments; an optional's wrapped type, an array's
    // element, a tuple's elements, a function's parameters and result.
    int64_t argument_count;
    SableRef arguments; // SableRef[]: each const SableTypeMetadata *
    SableRef object;    // const SableMetadata *: a class's object metadata: its vtable, its superclass's; else null
} SableTypeMetadata;

// What a counted object is (docs/06 §6.2): how to destroy what it holds,
// and, for an array, how its elements are copied and destroyed; how large
// and how aligned an instance is; what it keeps alive; and, for a class,
// its type's metadata. The compiler emits one per class and per array
// element type; a class's vtable follows.
typedef struct SableMetadata {
    void (*destroy)(void *object); // destroys the contents; not the memory
    const char *name;
    int64_t element_size;
    void (*copy_element)(void *destination, const void *source); // null: copy the bits; a class's superclass
    void (*destroy_element)(void *element);                       // null: nothing to do
    int64_t size;                                                 // an instance's bytes, the header's among them; 0 when it varies (a string)
    int64_t alignment;
    // Calls sable_heap_visit with each strong reference an object holds —
    // for an array, an element holds. Null: none.
    void (*visit)(void *object);
    const SableTypeMetadata *type; // a class's; null for an array and the runtime's own objects
} SableMetadata;

// The records' pointers, read (SableRef).
static inline const char *type_name_of(const SableTypeMetadata *type) {
    return SABLE_REF(type->name);
}

static inline const SableField *type_fields(const SableTypeMetadata *type) {
    return SABLE_REF(type->fields);
}

static inline const SableConformance *type_conformances(const SableTypeMetadata *type) {
    return SABLE_REF(type->conformances);
}

static inline const SableMetadata *type_object(const SableTypeMetadata *type) {
    return SABLE_REF(type->object);
}

// Type argument 'index': an element of an array of SableRefs, which the
// runtime's own arrays of pointers are too, each being absolute.
static inline const SableTypeMetadata *type_argument(const SableTypeMetadata *type, int64_t index) {
    return sable_ref((const SableRef *)SABLE_REF(type->arguments) + index);
}

static inline const char *field_name(const SableField *field) {
    return SABLE_REF(field->name);
}

static inline const char *field_type_name(const SableField *field) {
    return SABLE_REF(field->type_name);
}

static inline const SableTypeMetadata *field_type(const SableField *field) {
    return SABLE_REF(field->type);
}

static inline const char *protocol_name(const SableProtocol *protocol) {
    return SABLE_REF(protocol->name);
}

static inline const SableProtocol *conformance_protocol(const SableConformance *conformance) {
    return SABLE_REF(conformance->protocol);
}

static inline const SableWitnessTable *conformance_witnesses(const SableConformance *conformance) {
    return SABLE_REF(conformance->witnesses);
}

static inline const SableProtocol *table_protocol(const SableWitnessTable *table) {
    return SABLE_REF(table->protocol);
}

static inline const SableTypeMetadata *table_associated_type(const SableWitnessTable *table, int64_t index) {
    return sable_ref((const SableRef *)SABLE_REF(table->associated_types) + index);
}

static inline const SableWitnessTable *table_base(const SableWitnessTable *table, int64_t index) {
    return sable_ref((const SableRef *)SABLE_REF(table->bases) + index);
}

static inline const SableAssociatedConformance *table_associated_conformances(const SableWitnessTable *table) {
    return SABLE_REF(table->associated_conformances);
}

// ---- Conformances -----------------------------------------------------------

static bool same_protocol(const SableProtocol *a, const SableProtocol *b) {
    return a == b || (a != NULL && b != NULL && protocol_name(a) != NULL && protocol_name(b) != NULL && strcmp(protocol_name(a), protocol_name(b)) == 0);
}

// The superclass's metadata, for a class's; null for any other type.
static const SableTypeMetadata *superclass_of(const SableTypeMetadata *type) {
    if (type->kind != SABLE_KIND_CLASS || type_object(type) == NULL) {
        return NULL;
    }

    // A class keeps its superclass where an array keeps its element copier.
    const SableMetadata *super = (const SableMetadata *)(const void *)type_object(type)->copy_element;
    return super == NULL ? NULL : super->type;
}

// The witness tables a program built in units registers as it starts: a
// unit's copy of a type's conformance records leaves their tables null, since
// which tables a unit makes depends on its own code and every unit's copy of
// a record must be the same. A program compiled whole registers none.
static struct {
    const SableTypeMetadata *type;
    const SableWitnessTable *table;
} *registered_tables;
static int64_t registered_count, registered_capacity;

void sable_register_witness_table(const SableTypeMetadata *type, const SableWitnessTable *table) {
    if (registered_count == registered_capacity) {
        registered_capacity = registered_capacity == 0 ? 64 : registered_capacity * 2;
        registered_tables = realloc(registered_tables, (size_t)registered_capacity * sizeof *registered_tables);
        if (registered_tables == NULL) {
            sable_trap("out of memory");
        }
    }

    registered_tables[registered_count].type = type;
    registered_tables[registered_count].table = table;
    registered_count++;
}

static const SableWitnessTable *registered_table(const SableTypeMetadata *type, const SableProtocol *protocol) {
    for (int64_t i = 0; i < registered_count; i++) {
        if (registered_tables[i].type == type && same_protocol(table_protocol(registered_tables[i].table), protocol)) {
            return registered_tables[i].table;
        }
    }

    return NULL;
}

bool sable_metadata_equal(const SableTypeMetadata *a, const SableTypeMetadata *b);

// The table registered for a type the program names, for the same type made as it runs.
static const SableWitnessTable *registered_for_equal(const SableTypeMetadata *type, const SableProtocol *protocol) {
    for (int64_t i = 0; i < registered_count; i++) {
        if (same_protocol(table_protocol(registered_tables[i].table), protocol) && sable_metadata_equal(registered_tables[i].type, type)) {
            return registered_tables[i].table;
        }
    }

    return NULL;
}

// The template tables of generic types' conformances (#297, #299): one for
// every instance whose type arguments are known only at run time, by the
// generic type's name, registered by each image as it starts. Its entries
// read the type arguments from Self's metadata. One with associated types
// gives the functions that make their metadata from Self's: an instance then
// has a table of its own, made once, with those metadata in it.
typedef const SableTypeMetadata *(*SableAssociatedAccessor)(const SableTypeMetadata *self);

static struct {
    const char *name; // 'M.Box', of 'M.Box<M.Label>'
    size_t length;
    const SableWitnessTable *table;
    const SableAssociatedAccessor *associated; // null: the table is every instance's
} *templates;
static int64_t template_count, template_capacity;

void sable_register_template(const char *name, const SableWitnessTable *table, const SableAssociatedAccessor *associated) {
    if (template_count == template_capacity) {
        template_capacity = template_capacity == 0 ? 16 : template_capacity * 2;
        templates = realloc(templates, (size_t)template_capacity * sizeof *templates);
        if (templates == NULL) {
            sable_trap("out of memory");
        }
    }

    templates[template_count].name = name;
    templates[template_count].length = strlen(name);
    templates[template_count].table = table;
    templates[template_count].associated = associated;
    template_count++;
}

typedef struct SableTemplateInstance {
    const SableTypeMetadata *type;
    const SableWitnessTable *template_table;
    const SableWitnessTable *table;
    struct SableTemplateInstance *next;
} SableTemplateInstance;

static SableTemplateInstance *template_instances;

const SableWitnessTable *sable_witness_table(const SableTypeMetadata *type, const SableProtocol *protocol);

// The table an instance has of its own: the template's, its associated types' metadata and its bases' tables the instance's.
static const SableWitnessTable *template_instance(const SableTypeMetadata *type, const SableWitnessTable *of, const SableAssociatedAccessor *associated) {
    for (SableTemplateInstance *at = __atomic_load_n(&template_instances, __ATOMIC_ACQUIRE); at != NULL; at = at->next) {
        if (at->type == type && at->template_table == of) {
            return at->table;
        }
    }

    size_t size = sizeof(SableWitnessTable) + (size_t)of->requirement_count * sizeof of->requirements[0];
    SableWitnessTable *table = malloc(size);
    SableRef *types = calloc((size_t)of->associated_type_count + 1, sizeof *types);
    SableRef *bases = calloc((size_t)of->base_count + 1, sizeof *bases);
    SableTemplateInstance *made = malloc(sizeof *made);
    if (table == NULL || types == NULL || bases == NULL || made == NULL) {
        sable_trap("out of memory");
    }

    memcpy(table, of, size);
    // Its pointers to data made absolute: a relative one means something only where it is.
    sable_set_ref(&table->protocol, table_protocol(of));
    sable_set_ref(&table->associated_conformances, table_associated_conformances(of));
    for (int64_t i = 0; i < of->associated_type_count; i++) {
        sable_set_ref(&types[i], associated[i](type));
    }

    for (int64_t i = 0; i < of->base_count; i++) {
        sable_set_ref(&bases[i], table_base(of, i) == NULL ? NULL : sable_witness_table(type, table_protocol(table_base(of, i))));
    }

    sable_set_ref(&table->associated_types, types);
    sable_set_ref(&table->bases, bases);
    made->type = type;
    made->template_table = of;
    made->table = table;
    made->next = __atomic_load_n(&template_instances, __ATOMIC_ACQUIRE);
    while (!__atomic_compare_exchange_n(&template_instances, &made->next, made, true, __ATOMIC_RELEASE, __ATOMIC_ACQUIRE)) {
    }

    return table;
}

// Whether a template's name is the type's: an array's is "[]", a slice's "[:]",
// an optional's "?", a structural union's "|".
static bool is_instance_of(const SableTypeMetadata *type, const char *name, size_t length) {
    switch (type->kind) {
    case SABLE_KIND_ARRAY:
        return strcmp(name, "[]") == 0;
    case SABLE_KIND_SLICE:
        return strcmp(name, "[:]") == 0;
    case SABLE_KIND_OPTIONAL:
        return strcmp(name, "?") == 0;
    default:
        if ((type->flags & SABLE_TYPE_STRUCTURAL) != 0) {
            return strcmp(name, "|") == 0;
        }

        return strncmp(type_name_of(type), name, length) == 0 && type_name_of(type)[length] == '<';
    }
}

static const SableWitnessTable *template_table(const SableTypeMetadata *type, const SableProtocol *protocol) {
    for (int64_t i = 0; i < template_count; i++) {
        if (same_protocol(table_protocol(templates[i].table), protocol) && is_instance_of(type, templates[i].name, templates[i].length)) {
            return templates[i].associated == NULL ? templates[i].table : template_instance(type, templates[i].table, templates[i].associated);
        }
    }

    return NULL;
}

// What every array, slice, optional and structural union conforms to, by its
// kind: where each type it is of meets what is asked. Their metadata, the same in every unit
// and made as the program runs, carries no records; each image that asks the
// registry gives it the list of what it sees declared.
typedef struct SableStructuralConformance {
    SableConformance record; // its witnesses null: a table registered, or a template's
    int64_t kind;
    int64_t requirement_count; // of each type argument
    const SableProtocol *const *requirements;
} SableStructuralConformance;

static struct {
    const SableStructuralConformance *list;
    int64_t count;
} *structural;
static int64_t structural_count;

void sable_register_structural(const SableStructuralConformance *list, int64_t count) {
    structural = realloc(structural, (size_t)(structural_count + 1) * sizeof *structural);
    if (structural == NULL) {
        sable_trap("out of memory");
    }

    structural[structural_count].list = list;
    structural[structural_count].count = count;
    structural_count++;
}

const SableConformance *sable_type_conformance(const SableTypeMetadata *type, const SableProtocol *protocol);

static const SableConformance *structural_conformance(const SableTypeMetadata *type, const SableProtocol *protocol) {
    bool is_union = (type->flags & SABLE_TYPE_STRUCTURAL) != 0;
    if ((type->kind != SABLE_KIND_ARRAY && type->kind != SABLE_KIND_SLICE && type->kind != SABLE_KIND_OPTIONAL && !is_union) || type->argument_count < 1) {
        return NULL;
    }

    for (int64_t i = 0; i < structural_count; i++) {
        for (int64_t j = 0; j < structural[i].count; j++) {
            const SableStructuralConformance *each = &structural[i].list[j];
            if (each->kind != type->kind || !same_protocol(conformance_protocol(&each->record), protocol)) {
                continue;
            }

            bool holds = true;
            for (int64_t k = 0; k < each->requirement_count && holds; k++) {
                for (int64_t m = 0; m < (is_union ? type->argument_count : 1) && holds; m++) {
                    holds = sable_type_conformance(type_argument(type, m), each->requirements[k]) != NULL;
                }
            }

            if (holds) {
                return &each->record;
            }
        }
    }

    return NULL;
}

// The record of the type's conformance to the protocol — its own, or for a
// class one it inherits from a superclass — or null when it does not
// conform. This is the conformance registry: each type's metadata carries
// its records, so a lookup walks the type and its superclasses, and nothing
// is built as the program starts (but for the tables a program built in
// units registers, above).
const SableConformance *sable_type_conformance(const SableTypeMetadata *type, const SableProtocol *protocol) {
    for (; type != NULL; type = superclass_of(type)) {
        for (int64_t i = 0; i < type->conformance_count; i++) {
            if (same_protocol(conformance_protocol(&type_conformances(type)[i]), protocol)) {
                return &type_conformances(type)[i];
            }
        }

        const SableConformance *shared = structural_conformance(type, protocol);
        if (shared != NULL) {
            return shared;
        }
    }

    return NULL;
}

// The same, for a class by its object metadata.
const SableConformance *sable_conformance(const SableMetadata *type, const SableProtocol *protocol) {
    return type == NULL ? NULL : sable_type_conformance(type->type, protocol);
}

// The witness table of the type's conformance to the protocol — its own,
// or for a class a superclass's — or null: what 'e as? any Q' builds the
// existential from.
const SableWitnessTable *sable_witness_table(const SableTypeMetadata *type, const SableProtocol *protocol) {
    for (; type != NULL; type = superclass_of(type)) {
        bool conforms = false;
        for (int64_t i = 0; i < type->conformance_count; i++) {
            if (same_protocol(conformance_protocol(&type_conformances(type)[i]), protocol)) {
                if (conformance_witnesses(&type_conformances(type)[i]) != NULL) {
                    return conformance_witnesses(&type_conformances(type)[i]);
                }

                conforms = true;
            }
        }

        conforms = conforms || structural_conformance(type, protocol) != NULL;
        const SableWitnessTable *table = registered_table(type, protocol);
        if (table == NULL && conforms && type->argument_count > 0 && type_name_of(type) != NULL) {
            table = template_table(type, protocol);
            if (table == NULL) {
                table = registered_for_equal(type, protocol);
            }
        }

        if (table != NULL) {
            return table;
        }
    }

    return NULL;
}

// Whether the object's dynamic type conforms to the protocol: 'x is P' on a class reference.
bool sable_conforms(const void *object, const SableProtocol *protocol) {
    return object != NULL && sable_conformance(*(const SableMetadata *const *)object, protocol) != NULL;
}

// The field of the object's type that holds 'target' at 'object', or null:
// what names the edges of a reported reference cycle. Its superclasses'
// fields are the class's own, at the same offsets.
static const SableField *field_holding(const SableMetadata *type, const void *object, const void *target) {
    if (type == NULL || type->type == NULL) {
        return NULL;
    }

    for (int64_t i = 0; i < type->type->field_count; i++) {
        const SableField *field = &type_fields(type->type)[i];
        if (field->kind == SABLE_FIELD_STRONG && *(const void *const *)((const char *)object + field->offset) == target) {
            return field;
        }
    }

    return NULL;
}

// The header of docs/06 §6.2: metadata, the strong count, and a word of
// flags in its low byte with the unowned count above them.
typedef struct SableObject {
    const SableMetadata *type;
    uint32_t strong;
    uint32_t flags;
} SableObject;

enum {
    // Never counted, never freed: string literals live in read-only memory.
    SABLE_IMMORTAL = 1u,

    // The last strong reference has gone and the deinit has run; unowned
    // references still hold the memory.
    SABLE_DEINITED = 2u,

    // Weak references to it share an entry in the side table.
    SABLE_HAS_SIDE = 4u,

    // Made in a frame it does not outlive (docs/04 §3.7): counted as any
    // object is, its contents destroyed at the last release, never freed.
    SABLE_STACK = 8u,

    // An inline block that may hold a relative reference: one has been set
    // in it, or it came from bytes or another block. A block without one
    // changes size without walking itself for references to measure again.
    SABLE_LINKED = 16u,

    // Bits 5 to 8: the size class the object's memory came from (see
    // Memory, below); zero for memory from malloc.
    SABLE_CLASS_SHIFT = 5,
    SABLE_CLASS_MASK = 15u << SABLE_CLASS_SHIFT,

    // More than one thread may reach it: its counts are atomic (docs/02
    // §2.7). Set as a value crosses from one task to another — into a
    // task's closure, through an actor's call, as a task's result — on the
    // object and on everything it reaches (sable_share). An object only one
    // task can reach is counted with plain loads and stores; one task runs
    // on one thread at a time, and a task moving to another thread is
    // handed over with a release and an acquire.
    SABLE_SHARED = 1u << 9,

    // Its initialiser threw: it was never made, so no deinit of its runs
    // when it goes, though the fields set so far are destroyed as ever.
    // Compiled code's class destroy functions test this bit (1024).
    SABLE_UNMADE = 1u << 10,

    // The unowned count, above the flags.
    SABLE_UNOWNED_SHIFT = 11,
    SABLE_UNOWNED_ONE = 1u << SABLE_UNOWNED_SHIFT,
};

// The flags, read while another thread may change the unowned count.
static inline uint32_t object_flags(const SableObject *object) {
    return __atomic_load_n(&object->flags, __ATOMIC_RELAXED);
}

// The flags, changed: atomically, as another thread may change them too.
static inline void flags_set(SableObject *object, uint32_t bits) {
    __atomic_fetch_or(&object->flags, bits, __ATOMIC_RELAXED);
}

static inline void flags_clear(SableObject *object, uint32_t bits) {
    __atomic_fetch_and(&object->flags, ~bits, __ATOMIC_RELAXED);
}

// The strong count, as a check of uniqueness reads it: the releases other
// threads made before it are seen.
static inline uint32_t strong_count(const SableObject *object) {
    return __atomic_load_n(&object->strong, __ATOMIC_ACQUIRE);
}


_Noreturn void sable_trap(const char *message);
_Noreturn static void trap_here(const char *message);
static void sable_weak_object_died(SableObject *object);

static void track(SableObject *object, bool born);
static void object_born(SableObject *object);
static void object_gone(SableObject *object);
static inline void object_gone_on(SableThread *thread, SableObject *object);
static void report_leaks(void);
static void report_frames(void);
static void report_tasks(void);
static void forget_immortal_reach(void);
static void trap_hook_forget(void);
static bool tracking, checking_shared;
// Whether an allocation is looked at: tracked, or its bytes counted.
static bool observing;
static void check_shared(SableObject *object);

// What ARC did: the counts the benchmarks track, and what the ARC passes are
// measured by. Retains and releases count only those that touch a count —
// not null, not immortal — and only under SABLE_STATS (threads.c). The two
// are the program's to count too: compiled code retains and releases
// inline, calling here only for an object's last release
// (sable_release_last), and keeps these counts as the runtime does.
// Allocations and the objects alive are counted by each thread (threads.c).
static int64_t thread_sum(size_t offset) {
    int64_t sum = 0;
    spin_lock(&threads_lock);
    for (SableThread *thread = all_threads; thread != NULL; thread = thread->next) {
        sum += __atomic_load_n((int64_t *)((char *)thread + offset), __ATOMIC_RELAXED);
    }

    spin_unlock(&threads_lock);
    return sum;
}

// The program ended by Process.exit: what it left alive is no leak.
static bool process_exiting;

static int64_t live_object_count(void) {
    return thread_sum(offsetof(SableThread, allocations)) - thread_sum(offsetof(SableThread, gone_objects));
}

// What a library does as the process exits — destroy its globals — given by
// its start-up (LlvmEmitter's LibraryLifetime). Run here, before the report,
// rather than as destructors of their own: the order of one image's
// destructors is the linker's, and the report must come after them.
static void (*exit_hooks[8])(void);
static int exit_hook_count;

void sable_runtime_at_exit(void (*hook)(void)) {
    if (exit_hook_count < (int)(sizeof exit_hooks / sizeof exit_hooks[0])) {
        exit_hooks[exit_hook_count++] = hook;
    }
}

// The small blocks' slabs (memory.c): how many are held now and were at
// most, and how many were given back to the system.
static int64_t slabs_held, slabs_most, slabs_returned;

// With SABLE_CHECK_LEAKS set, a program that ends with objects still alive
// says how many: tests run every program this way. With SABLE_STATS set, it
// says what ARC did. Run as the program exits (platform.h).
SABLE_AT_EXIT(sable_report) {
    while (exit_hook_count > 0) {
        exit_hooks[--exit_hook_count]();
    }

    trap_hook_forget();
    forget_immortal_reach();

    int64_t live_objects = live_object_count();
    if (live_objects != 0 && !process_exiting && getenv("SABLE_CHECK_LEAKS") != NULL) {
        fflush(stdout);
        fprintf(stderr, "sable: %lld object(s) leaked\n", (long long)live_objects);
        if (tracking) {
            report_leaks();
        }
    }

    if (getenv("SABLE_STATS") != NULL) {
        fflush(stdout);
        fprintf(stderr, "sable: allocations %lld, retains %lld, releases %lld\n",
            (long long)thread_sum(offsetof(SableThread, allocations)), (long long)sable_retains, (long long)sable_releases);
        fprintf(stderr, "sable: slabs %lld held, %lld at most, %lld given back\n",
            (long long)__atomic_load_n(&slabs_held, __ATOMIC_RELAXED), (long long)__atomic_load_n(&slabs_most, __ATOMIC_RELAXED),
            (long long)__atomic_load_n(&slabs_returned, __ATOMIC_RELAXED));
        report_tasks();
        report_frames();
        fprintf(stderr, "sable: peak memory %lld KiB\n", (long long)(peak_memory() / 1024));
    }
}
