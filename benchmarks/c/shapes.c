// Manually-managed port of benchmarks/shapes.sb (see README.md).
//
// An `any Shape` is a witness table (a struct of function pointers) and a
// heap box holding the struct value: one malloc per existential, as Sable
// boxes one. An Animal is one heap object whose first field is its class's
// vtable. name() returns a static string, as Sable's literals are immortal.
// Arrays are a header and an element buffer, indexed with bounds checks;
// Int arithmetic is checked.

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#pragma STDC FP_CONTRACT OFF

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

static void *xmalloc(size_t size) {
    void *p = malloc(size);
    if (p == NULL) trap("out of memory");
    return p;
}

// ---- Strings: only literals here ----------------------------------------

typedef struct String {
    int64_t length;
    int64_t count; // Unicode scalars
    const char *bytes;
} String;

static const String circle_name = { 6, 6, "circle" };
static const String rect_name = { 4, 4, "rect" };

// ---- The protocol and its conformances -----------------------------------

typedef struct ShapeWitness {
    double (*area)(const void *self);
    const String *(*name)(const void *self);
    void (*destroy)(void *self);
} ShapeWitness;

typedef struct AnyShape {
    const ShapeWitness *witness;
    void *box;
} AnyShape;

typedef struct Circle { double r; } Circle;
typedef struct Rect { double w, h; } Rect;

static double circle_area(const void *self) {
    const Circle *c = self;
    return 3.14159 * c->r * c->r;
}
static const String *circle_name_fn(const void *self) { (void)self; return &circle_name; }

static double rect_area(const void *self) {
    const Rect *r = self;
    return r->w * r->h;
}
static const String *rect_name_fn(const void *self) { (void)self; return &rect_name; }

static void box_free(void *self) { free(self); }

static const ShapeWitness circle_witness = { circle_area, circle_name_fn, box_free };
static const ShapeWitness rect_witness = { rect_area, rect_name_fn, box_free };

static AnyShape make_circle(Circle value) {
    Circle *box = xmalloc(sizeof(Circle));
    *box = value;
    return (AnyShape){ &circle_witness, box };
}

static AnyShape make_rect(Rect value) {
    Rect *box = xmalloc(sizeof(Rect));
    *box = value;
    return (AnyShape){ &rect_witness, box };
}

// ---- The class hierarchy -------------------------------------------------

typedef struct Animal Animal;

typedef struct AnimalVTable {
    int64_t (*weight)(const Animal *self);
} AnimalVTable;

struct Animal {
    const AnimalVTable *vtable;
    int64_t legs;
};

static int64_t bird_weight(const Animal *self) { (void)self; return 1; }
static int64_t dog_weight(const Animal *self) { (void)self; return 20; }

static const AnimalVTable bird_vtable = { bird_weight };
static const AnimalVTable dog_vtable = { dog_weight };

static Animal *animal_new(const AnimalVTable *vtable, int64_t legs) {
    Animal *animal = xmalloc(sizeof(Animal));
    animal->vtable = vtable;
    animal->legs = legs;
    return animal;
}

// ---- Arrays ----------------------------------------------------------------

#define DEFINE_ARRAY(Name, T)                                                        \
    typedef struct Name {                                                            \
        int64_t count, capacity;                                                     \
        T *elements;                                                                 \
    } Name;                                                                          \
    static void Name##_append(Name **slot, T value) {                                \
        Name *array = *slot;                                                         \
        if (array == NULL) {                                                         \
            array = xmalloc(sizeof(Name));                                           \
            array->count = array->capacity = 0;                                      \
            array->elements = NULL;                                                  \
            *slot = array;                                                           \
        }                                                                            \
        if (array->count == array->capacity) {                                       \
            array->capacity = array->capacity < 4 ? 4 : array->capacity * 2;         \
            array->elements = realloc(array->elements, (size_t)array->capacity * sizeof(T)); \
            if (array->elements == NULL) trap("out of memory");                      \
        }                                                                            \
        array->elements[array->count++] = value;                                     \
    }                                                                                \
    static inline int64_t Name##_count(const Name *array) {                          \
        return array == NULL ? 0 : array->count;                                     \
    }                                                                                \
    static inline T *Name##_at(const Name *array, int64_t index) {                   \
        if (array == NULL || index < 0 || index >= array->count) trap("array index out of range"); \
        return &array->elements[index];                                              \
    }

DEFINE_ARRAY(ShapeArray, AnyShape)
DEFINE_ARRAY(AnimalArray, Animal *)

static void format_f64(double value, char *text, size_t size) {
    if (isnan(value)) {
        snprintf(text, size, "nan");
        return;
    }
    for (int precision = 1; precision < 17; precision++) {
        snprintf(text, size, "%.*g", precision, value);
        if (strtod(text, NULL) == value) return;
    }
    snprintf(text, size, "%.17g", value);
}

int main(void) {
    ShapeArray *shapes = NULL;
    AnimalArray *animals = NULL;
    int64_t i = 0;
    while (i < 1000) {
        if (i % 2 == 0) {
            ShapeArray_append(&shapes, make_circle((Circle){ (double)(i % 10) }));
            AnimalArray_append(&animals, animal_new(&bird_vtable, 2));
        } else {
            ShapeArray_append(&shapes, make_rect((Rect){ (double)(i % 7), 2.0 }));
            AnimalArray_append(&animals, animal_new(&dog_vtable, 4));
        }
        i = add_i(i, 1);
    }

    double area = 0.0;
    int64_t letters = 0;
    int64_t weight = 0;
    int64_t round = 0;
    while (round < 300) {
        for (int64_t k = 0; k < ShapeArray_count(shapes); k++) {
            const AnyShape *shape = ShapeArray_at(shapes, k);
            area = area + shape->witness->area(shape->box);
            letters = add_i(letters, shape->witness->name(shape->box)->count);
        }
        for (int64_t k = 0; k < AnimalArray_count(animals); k++) {
            const Animal *animal = *AnimalArray_at(animals, k);
            weight = add_i(add_i(weight, animal->vtable->weight(animal)), animal->legs);
        }
        round = add_i(round, 1);
    }

    char text[40];
    format_f64(area, text, sizeof text);
    puts(text);
    printf("%lld\n", (long long)letters);
    printf("%lld\n", (long long)weight);

    for (int64_t k = 0; k < shapes->count; k++) {
        shapes->elements[k].witness->destroy(shapes->elements[k].box);
    }
    free(shapes->elements);
    free(shapes);
    for (int64_t k = 0; k < animals->count; k++) {
        free(animals->elements[k]);
    }
    free(animals->elements);
    free(animals);
    return 0;
}
