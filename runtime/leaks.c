// The Sable runtime: leaks — which objects are alive, for SABLE_CHECK_LEAKS.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Leaks -----------------------------------------------------------------

// With SABLE_CHECK_LEAKS set, every live object is kept in a set, so that a
// program ending with objects alive can say which (docs/02 §2.2): how many
// of each type, and the reference cycles among them — under ARC the usual
// reason an object outlives the program. The walk follows each object's
// strong references through its metadata's visitor, which the compiler
// writes per class and per array element type; weak and unowned
// references keep nothing alive, and are not followed.

// Open addressing, by address; a removed object leaves a tombstone. Every
// thread makes and frees objects, so the set is taken under a lock.
static SpinLock tracked_lock;
static SableObject **tracked;
static size_t tracked_capacity, tracked_used;
#define TRACKED_GONE ((SableObject *)1)

static size_t tracked_slot(const SableObject *object) {
    uint64_t h = (uintptr_t)object;
    h ^= h >> 33;
    h *= 0xff51afd7ed558ccdULL;
    h ^= h >> 33;
    return (size_t)h & (tracked_capacity - 1);
}

static void track_insert(SableObject *object);

static void tracked_grow(void) {
    SableObject **old = tracked;
    size_t old_capacity = tracked_capacity;
    tracked_capacity = old_capacity < 1024 ? 1024 : old_capacity * 2;
    tracked = calloc(tracked_capacity, sizeof(SableObject *));
    if (tracked == NULL) {
        sable_trap("out of memory");
    }

    tracked_used = 0;
    for (size_t i = 0; i < old_capacity; i++) {
        if (old[i] != NULL && old[i] != TRACKED_GONE) {
            track_insert(old[i]);
        }
    }

    free(old);
}

static void track_insert(SableObject *object) {
    if ((tracked_used + 1) * 2 > tracked_capacity) {
        tracked_grow();
    }

    size_t i = tracked_slot(object);
    while (tracked[i] != NULL && tracked[i] != TRACKED_GONE) {
        i = (i + 1) & (tracked_capacity - 1);
    }

    if (tracked[i] == NULL) {
        tracked_used++;
    }

    tracked[i] = object;
}

static void track_remove(SableObject *object) {
    if (tracked_capacity == 0) {
        return;
    }

    for (size_t i = tracked_slot(object); tracked[i] != NULL; i = (i + 1) & (tracked_capacity - 1)) {
        if (tracked[i] == object) {
            tracked[i] = TRACKED_GONE;
            return;
        }
    }
}

static void leaks_start(void) {
    tracking = getenv("SABLE_CHECK_LEAKS") != NULL;
    checking_shared = getenv("SABLE_CHECK_SHARED") != NULL;
}

// Into the set, or out of it: apart, as hardly any program runs with it.
__attribute__((noinline)) static void track(SableObject *object, bool born) {
    spin_lock(&tracked_lock);
    if (born) {
        track_insert(object);
    } else {
        track_remove(object);
    }

    spin_unlock(&tracked_lock);
}

// An object made in a frame, which no allocation counts, is alive: counted, and kept in the set when leaks are checked.
static void object_born(SableObject *object) {
    thread_count(&current_thread()->gone_objects, -1);
    if (tracking) {
        track(object, true);
    }
}

// An object's memory is no longer the program's.
static inline void object_gone_on(SableThread *thread, SableObject *object) {
    thread_count(&thread->gone_objects, 1);
    if (tracking) {
        track(object, false);
    }
}

static void object_gone(SableObject *object) {
    object_gone_on(current_thread(), object);
}

// The walk: the leaked objects sorted by address, and the references found
// from the one being visited.
static SableObject **leaked;
static int64_t leaked_count, visiting;
static int64_t *edge_from, *edge_to;
static int64_t edge_count, edge_capacity;

static int compare_addresses(const void *a, const void *b) {
    uintptr_t x = (uintptr_t)*(SableObject *const *)a, y = (uintptr_t)*(SableObject *const *)b;
    return x < y ? -1 : x > y;
}

static int64_t leaked_index(const void *object) {
    int64_t low = 0, high = leaked_count - 1;
    while (low <= high) {
        int64_t middle = low + (high - low) / 2;
        if ((const void *)leaked[middle] == object) {
            return middle;
        }

        if ((uintptr_t)leaked[middle] < (uintptr_t)object) {
            low = middle + 1;
        } else {
            high = middle - 1;
        }
    }

    return -1;
}

// With SABLE_CHECK_SHARED set, whether what an object reachable from more than
// one thread holds is counted atomically too, looked at as the object goes: a
// store that should have shared it did not, and its counts may be wrong.
static void expect_shared(void *pointer) {
    if (pointer != NULL && !(object_flags(pointer) & (SABLE_IMMORTAL | SABLE_SHARED | SABLE_DEINITED))) {
        static _Thread_local char message[256];
        snprintf(message, sizeof message, "an object reachable from more than one thread held %s counted by one",
            ((SableObject *)pointer)->type->name);
        sable_trap(message);
    }
}

// A strong reference the object being visited holds: what its visitor,
// the compiler's, calls for each. Only references between leaked objects
// are kept; the rest of the heap is gone by now.
static _Thread_local void (*visit_hook)(void *pointer);

static void share_unowned(void *pointer);

void sable_heap_visit(void *pointer) {
    if (visit_hook != NULL) {
        visit_hook(pointer);
        return;
    }

    int64_t to = pointer == NULL ? -1 : leaked_index(pointer);
    if (to < 0) {
        return;
    }

    if (edge_count == edge_capacity) {
        edge_capacity = edge_capacity < 64 ? 64 : edge_capacity * 2;
        edge_from = realloc(edge_from, (size_t)edge_capacity * sizeof(int64_t));
        edge_to = realloc(edge_to, (size_t)edge_capacity * sizeof(int64_t));
        if (edge_from == NULL || edge_to == NULL) {
            return;
        }
    }

    edge_from[edge_count] = visiting;
    edge_to[edge_count] = to;
    edge_count++;
}

// An unowned reference the object being visited holds, which keeps nothing
// alive, but through which another thread may retain the object it names: for
// a walk that shares what it reaches (tasks.c), or makes it every thread's.
void sable_heap_visit_unowned(void *pointer) {
    if (visit_hook == expect_shared) {
        expect_shared(pointer);
    } else if (visit_hook != NULL) {
        share_unowned(pointer);
    }
}

void sable_array_destroy(void *pointer);

static void visit_object(SableObject *object);

static void check_shared(SableObject *object) {
    void (*saved)(void *) = visit_hook;
    visit_hook = expect_shared;
    visit_object(object);
    visit_hook = saved;
}

// Each strong reference the object holds: a class's fields, an array's elements.
static void visit_object(SableObject *object) {
    const SableMetadata *type = object->type;
    if (type == NULL || type->visit == NULL || (object_flags(object) & SABLE_DEINITED)) {
        return;
    }

    if (type->destroy == sable_array_destroy) {
        SableArray *array = (SableArray *)object;
        for (int64_t i = 0; i < array->count; i++) {
            // An array of a type known only at run time visits by the element's witnesses.
            element_visit(type, array->elements + i * type->element_size);
        }
    } else {
        type->visit(object);
    }
}

// How many of each type: the name, and the count.
typedef struct LeakedType {
    const char *name;
    int64_t count;
} LeakedType;

static int compare_leaked_types(const void *a, const void *b) {
    const LeakedType *x = a, *y = b;
    if (x->count != y->count) {
        return x->count > y->count ? -1 : 1;
    }

    return strcmp(x->name, y->name);
}

static const char *type_name(const SableObject *object) {
    return object->type != NULL && object->type->name != NULL ? object->type->name : "?";
}

// The strongly connected components of the references, by Tarjan's
// algorithm without recursion: a leak can be a long list.
static int64_t *component_of;

static void find_components(const int64_t *first_edge, const int64_t *targets) {
    int64_t n = leaked_count;
    int64_t *index = malloc((size_t)n * sizeof(int64_t));
    int64_t *low = malloc((size_t)n * sizeof(int64_t));
    bool *on_stack = calloc((size_t)n, sizeof(bool));
    int64_t *stack = malloc((size_t)n * sizeof(int64_t));
    int64_t *calls = malloc((size_t)n * sizeof(int64_t)); // the node, on the call stack
    int64_t *next_edge = malloc((size_t)n * sizeof(int64_t));
    component_of = malloc((size_t)n * sizeof(int64_t));
    if (index == NULL || low == NULL || on_stack == NULL || stack == NULL || calls == NULL || next_edge == NULL || component_of == NULL) {
        return;
    }

    for (int64_t i = 0; i < n; i++) {
        index[i] = -1;
    }

    int64_t counter = 0, depth = 0, top = 0, components = 0;
    for (int64_t root = 0; root < n; root++) {
        if (index[root] >= 0) {
            continue;
        }

        calls[depth++] = root;
        index[root] = low[root] = counter++;
        next_edge[root] = first_edge[root];
        stack[top++] = root;
        on_stack[root] = true;
        while (depth > 0) {
            int64_t v = calls[depth - 1];
            if (next_edge[v] < first_edge[v + 1]) {
                int64_t w = targets[next_edge[v]++];
                if (index[w] < 0) {
                    index[w] = low[w] = counter++;
                    next_edge[w] = first_edge[w];
                    stack[top++] = w;
                    on_stack[w] = true;
                    calls[depth++] = w;
                } else if (on_stack[w] && index[w] < low[v]) {
                    low[v] = index[w];
                }

                continue;
            }

            depth--;
            if (depth > 0 && low[v] < low[calls[depth - 1]]) {
                low[calls[depth - 1]] = low[v];
            }

            if (low[v] == index[v]) {
                int64_t w;
                do {
                    w = stack[--top];
                    on_stack[w] = false;
                    component_of[w] = components;
                } while (w != v);
                components++;
            }
        }
    }

    free(index);
    free(low);
    free(on_stack);
    free(stack);
    free(calls);
    free(next_edge);
}

enum { CYCLES_SHOWN = 5, CYCLE_LENGTH_SHOWN = 8 };

// One step of a cycle: the object's type, and the field by which it holds
// the next, when its metadata names it.
static void print_step(const SableObject *object, const SableObject *next) {
    const SableField *field = field_holding(object->type, object, next);
    if (field != NULL && field_name(field) != NULL) {
        fprintf(stderr, "%s.%s", type_name(object), field_name(field));
    } else {
        fprintf(stderr, "%s", type_name(object));
    }
}

// One cycle through 'start', inside its component: the shortest way back,
// found breadth first, written as the types along it.
static void print_cycle(int64_t start, const int64_t *first_edge, const int64_t *targets) {
    int64_t n = leaked_count;
    int64_t *parent = malloc((size_t)n * sizeof(int64_t));
    int64_t *queue = malloc((size_t)n * sizeof(int64_t));
    int64_t *path = malloc((size_t)(n + 1) * sizeof(int64_t));
    if (parent == NULL || queue == NULL || path == NULL) {
        free(parent);
        free(queue);
        free(path);
        return;
    }

    for (int64_t i = 0; i < n; i++) {
        parent[i] = -1;
    }

    int64_t head = 0, tail = 0, back = -1;
    queue[tail++] = start;
    while (head < tail && back < 0) {
        int64_t v = queue[head++];
        for (int64_t e = first_edge[v]; e < first_edge[v + 1]; e++) {
            int64_t w = targets[e];
            if (component_of[w] != component_of[start]) {
                continue;
            }

            if (w == start) {
                back = v;
                break;
            }

            if (parent[w] < 0) {
                parent[w] = v;
                queue[tail++] = w;
            }
        }
    }

    // The way back, from its end: start ← … ← back.
    int64_t length = 0;
    for (int64_t v = back; v >= 0 && v != start; v = parent[v]) {
        path[length++] = v;
    }

    // Each step names the field that holds the next object, where the
    // metadata's field descriptors say which: Test.Pet.toy -> Test.Toy.
    fprintf(stderr, "  a reference cycle: ");
    int64_t from = start;
    for (int64_t i = length - 1, shown = 1; i >= -1; i--, shown++) {
        int64_t to = i >= 0 ? path[i] : start;
        if (shown > 1) {
            fprintf(stderr, " -> ");
        }

        print_step(leaked[from], leaked[to]);
        if (shown == CYCLE_LENGTH_SHOWN && i > 0) {
            fprintf(stderr, " -> … (%lld more)", (long long)i);
            break;
        }

        from = to;
    }

    fprintf(stderr, " -> %s\n", type_name(leaked[start]));
    free(parent);
    free(queue);
    free(path);
}

// What leaked: each type and how many, then the cycles that keep them.
static void report_leaks(void) {
    int64_t live_objects = live_object_count();
    leaked = malloc((size_t)live_objects * sizeof(SableObject *));
    if (leaked == NULL) {
        return;
    }

    for (size_t i = 0; i < tracked_capacity; i++) {
        if (tracked[i] != NULL && tracked[i] != TRACKED_GONE && leaked_count < live_objects) {
            leaked[leaked_count++] = tracked[i];
        }
    }

    qsort(leaked, (size_t)leaked_count, sizeof(SableObject *), compare_addresses);

    // By type, the most first.
    LeakedType *types = calloc((size_t)leaked_count + 1, sizeof(LeakedType));
    int64_t type_count = 0;
    for (int64_t i = 0; i < leaked_count && types != NULL; i++) {
        const char *name = type_name(leaked[i]);
        int64_t t = 0;
        while (t < type_count && strcmp(types[t].name, name) != 0) {
            t++;
        }

        if (t == type_count) {
            types[type_count++] = (LeakedType){ name, 0 };
        }

        types[t].count++;
    }

    if (types != NULL) {
        qsort(types, (size_t)type_count, sizeof(LeakedType), compare_leaked_types);
        for (int64_t t = 0; t < type_count; t++) {
            fprintf(stderr, "  %lld %s\n", (long long)types[t].count, types[t].name);
        }

        free(types);
    }

    // The references among them, grouped by where they start.
    for (visiting = 0; visiting < leaked_count; visiting++) {
        visit_object(leaked[visiting]);
    }

    int64_t *first_edge = calloc((size_t)leaked_count + 1, sizeof(int64_t));
    int64_t *targets = malloc((size_t)(edge_count > 0 ? edge_count : 1) * sizeof(int64_t));
    if (first_edge == NULL || targets == NULL) {
        return;
    }

    for (int64_t e = 0; e < edge_count; e++) {
        first_edge[edge_from[e] + 1]++;
    }

    for (int64_t i = 0; i < leaked_count; i++) {
        first_edge[i + 1] += first_edge[i];
    }

    int64_t *filled = calloc((size_t)leaked_count + 1, sizeof(int64_t));
    if (filled == NULL) {
        return;
    }

    for (int64_t e = 0; e < edge_count; e++) {
        targets[first_edge[edge_from[e]] + filled[edge_from[e]]++] = edge_to[e];
    }

    free(filled);
    find_components(first_edge, targets);
    if (component_of == NULL) {
        return;
    }

    // A component of more than one object is a cycle, and so is one object
    // that refers to itself. Each is shown once, from its first object.
    bool *shown = calloc((size_t)leaked_count + 1, sizeof(bool));
    int64_t cycles = 0;
    for (int64_t i = 0; i < leaked_count && shown != NULL; i++) {
        int64_t component = component_of[i];
        if (shown[component]) {
            continue;
        }

        bool cyclic = false;
        for (int64_t e = first_edge[i]; e < first_edge[i + 1] && !cyclic; e++) {
            cyclic = component_of[targets[e]] == component;
        }

        if (!cyclic) {
            continue;
        }

        shown[component] = true;
        if (cycles++ < CYCLES_SHOWN) {
            print_cycle(i, first_edge, targets);
        }
    }

    if (cycles > CYCLES_SHOWN) {
        fprintf(stderr, "  and %lld more reference cycle(s)\n", (long long)(cycles - CYCLES_SHOWN));
    }

    free(shown);
}

// ---- Immortal objects ------------------------------------------------------

// '@immortal static let' (docs/02 §2.2): each object the global's value holds
// is made immortal once the global is set. Its count is never touched again
// (sable_retain and sable_release, and compiled code's inline forms, skip an
// object with the flag) and it is never released, so it is no longer one of
// the live objects a leak is counted from. What it keeps alive is no leak
// either: those objects are found at exit, by walking from the immortal
// ones, before anything is reported.
static SableObject **immortals;
static int64_t immortal_count, immortal_capacity;

void sable_make_immortal(void *pointer) {
    SableObject *object = pointer;
    // A string literal is immortal already, and in read-only memory.
    if (object == NULL || (object->flags & SABLE_IMMORTAL)) {
        return;
    }

    flags_set(object, SABLE_IMMORTAL);
    object_gone(object);
    spin_lock(&tracked_lock);
    if (immortal_count == immortal_capacity) {
        immortal_capacity = immortal_capacity < 16 ? 16 : immortal_capacity * 2;
        immortals = realloc(immortals, (size_t)immortal_capacity * sizeof(SableObject *));
        if (immortals == NULL) {
            sable_trap("out of memory");
        }
    }

    immortals[immortal_count++] = object;
    spin_unlock(&tracked_lock);
}

static bool track_contains(const SableObject *object) {
    if (tracked_capacity == 0) {
        return false;
    }

    for (size_t i = tracked_slot(object); tracked[i] != NULL; i = (i + 1) & (tracked_capacity - 1)) {
        if (tracked[i] == object) {
            return true;
        }
    }

    return false;
}

// The walk from the immortal objects: each live object found is taken out
// of the count, and its own references followed in turn.
static SableObject **reached;
static int64_t reached_count, reached_capacity;

static void reach_from_immortal(void *pointer) {
    SableObject *object = pointer;
    if (object == NULL || (object->flags & SABLE_IMMORTAL) || !track_contains(object)) {
        return;
    }

    track_remove(object);
    thread_count(&current_thread()->gone_objects, 1);
    if (reached_count == reached_capacity) {
        reached_capacity = reached_capacity < 64 ? 64 : reached_capacity * 2;
        reached = realloc(reached, (size_t)reached_capacity * sizeof(SableObject *));
        if (reached == NULL) {
            return;
        }
    }

    reached[reached_count++] = object;
}

static void forget_immortal_reach(void) {
    if (!tracking || immortal_count == 0) {
        return;
    }

    visit_hook = reach_from_immortal;
    for (int64_t i = 0; i < immortal_count; i++) {
        visit_object(immortals[i]);
    }

    for (int64_t i = 0; i < reached_count; i++) {
        visit_object(reached[i]);
    }

    visit_hook = NULL;
}
