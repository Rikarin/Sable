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

// Open addressing, by address; a removed object leaves a tombstone.
static SableObject **tracked;
static size_t tracked_capacity, tracked_used;
#define TRACKED_GONE ((SableObject *)1)

static size_t tracked_slot(const SableObject *object) {
    uintptr_t h = (uintptr_t)object;
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
}

// An object is alive: counted, and kept in the set when leaks are checked.
static void object_born(SableObject *object) {
    live_objects++;
    if (tracking) {
        track_insert(object);
    }
}

// An object's memory is no longer the program's.
static void object_gone(SableObject *object) {
    live_objects--;
    if (tracking) {
        track_remove(object);
    }
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

// A strong reference the object being visited holds: what its visitor,
// the compiler's, calls for each. Only references between leaked objects
// are kept; the rest of the heap is gone by now.
void sable_heap_visit(void *pointer) {
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

void sable_array_destroy(void *pointer);

// Each strong reference the object holds: a class's fields, an array's elements.
static void visit_object(SableObject *object) {
    const SableMetadata *type = object->type;
    if (type == NULL || type->visit == NULL || (object->flags & SABLE_DEINITED)) {
        return;
    }

    if (type->destroy == sable_array_destroy) {
        SableArray *array = (SableArray *)object;
        for (int64_t i = 0; i < array->count; i++) {
            type->visit(array->elements + i * type->element_size);
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

    fprintf(stderr, "  a reference cycle: %s", type_name(leaked[start]));
    for (int64_t i = length - 1, shown = 1; i >= 0; i--, shown++) {
        if (shown == CYCLE_LENGTH_SHOWN && i > 0) {
            fprintf(stderr, " -> … (%lld more)", (long long)i);
            break;
        }

        fprintf(stderr, " -> %s", type_name(leaked[path[i]]));
    }

    fprintf(stderr, " -> %s\n", type_name(leaked[start]));
    free(parent);
    free(queue);
    free(path);
}

// What leaked: each type and how many, then the cycles that keep them.
static void report_leaks(void) {
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
