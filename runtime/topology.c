// The Sable runtime: the machine's processors, and where threads run on them —
// Sable.Concurrency's ProcessorTopology, a thread's priority and affinity, and
// the pool's placement (tasks.c follows it, parallel.c sets it).
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Topology ------------------------------------------------------------------

// The system's numbers of the cores, packages and nodes found so far, each
// given the topology's own: its place in the list.
typedef struct TopologyKeys {
    int64_t *keys;
    int32_t count, capacity;
} TopologyKeys;

static TopologyKeys topology_cores, topology_packages, topology_nodes;

static int32_t topology_key(TopologyKeys *keys, int64_t key) {
    for (int32_t i = 0; i < keys->count; i++) {
        if (keys->keys[i] == key) {
            return i;
        }
    }

    if (keys->count == keys->capacity) {
        keys->capacity = keys->capacity > 0 ? keys->capacity * 2 : 16;
        int64_t *more = realloc(keys->keys, sizeof(int64_t) * (size_t)keys->capacity);
        if (more == NULL) {
            sable_trap("out of memory");
        }

        keys->keys = more;
    }

    keys->keys[keys->count] = key;
    return keys->count++;
}

static void topology_add(Topology *topology, int32_t id, int64_t core, int64_t package, int64_t node, int32_t efficiency) {
    for (int32_t i = 0; i < topology->count; i++) {
        if (topology->processors[i].id == id) {
            return;
        }
    }

    if (topology->count == topology->capacity) {
        topology->capacity = topology->capacity > 0 ? topology->capacity * 2 : 16;
        TopologyProcessor *more = realloc(topology->processors, sizeof(TopologyProcessor) * (size_t)topology->capacity);
        if (more == NULL) {
            sable_trap("out of memory");
        }

        topology->processors = more;
    }

    // A core is the system's number of it within its package.
    int32_t place = topology_key(&topology_packages, package);
    topology->processors[topology->count++] = (TopologyProcessor) {
        .id = id,
        .core = topology_key(&topology_cores, (int64_t)place << 32 | (core & 0xFFFFFFFF)),
        .package = place,
        .node = topology_key(&topology_nodes, node),
        .efficiency = efficiency,
    };
}

static int compare_ids(const void *a, const void *b) {
    int32_t x = *(const int32_t *)a, y = *(const int32_t *)b;
    return x < y ? -1 : x > y;
}

static void topology_add_cache(Topology *topology, int32_t level, int32_t kind, int64_t size, int64_t line, const int32_t *processors, int32_t count) {
    if (count <= 0 || processors == NULL) {
        return;
    }

    int32_t *sorted = malloc(sizeof(int32_t) * (size_t)count);
    if (sorted == NULL) {
        sable_trap("out of memory");
    }

    memcpy(sorted, processors, sizeof(int32_t) * (size_t)count);
    qsort(sorted, (size_t)count, sizeof(int32_t), compare_ids);
    for (int32_t i = 0; i < topology->cache_count; i++) {
        TopologyCache *cache = &topology->caches[i];
        if (cache->level == level && cache->kind == kind && cache->count == count && memcmp(cache->processors, sorted, sizeof(int32_t) * (size_t)count) == 0) {
            free(sorted);
            return;
        }
    }

    if (topology->cache_count == topology->cache_capacity) {
        topology->cache_capacity = topology->cache_capacity > 0 ? topology->cache_capacity * 2 : 16;
        TopologyCache *more = realloc(topology->caches, sizeof(TopologyCache) * (size_t)topology->cache_capacity);
        if (more == NULL) {
            sable_trap("out of memory");
        }

        topology->caches = more;
    }

    topology->caches[topology->cache_count++] = (TopologyCache) {
        .level = level, .kind = kind, .size = size, .line = line, .processors = sorted, .count = count,
    };
}

static int compare_processors(const void *a, const void *b) {
    return compare_ids(&((const TopologyProcessor *)a)->id, &((const TopologyProcessor *)b)->id);
}

static int compare_caches(const void *a, const void *b) {
    const TopologyCache *x = a, *y = b;
    if (x->level != y->level) {
        return x->level - y->level;
    }

    if (x->kind != y->kind) {
        return x->kind - y->kind;
    }

    return compare_ids(x->processors, y->processors);
}

// What the system says, with processors and caches in order and efficiency
// classes ranked from 0, the slowest; or, where it says nothing, a core for
// each processor the program may use.
static SpinLock topology_lock;

static void topology_made(Topology *topology) {
    spin_lock(&topology_lock);
    topology_cores.count = topology_packages.count = topology_nodes.count = 0;
    if (!topology_read(topology) || topology->count == 0) {
        topology->count = 0;
        topology_cores.count = topology_packages.count = topology_nodes.count = 0;
        int count = processor_count();
        for (int i = 0; i < count; i++) {
            topology_add(topology, i, i, 0, 0, 0);
        }
    }

    spin_unlock(&topology_lock);

    qsort(topology->processors, (size_t)topology->count, sizeof(TopologyProcessor), compare_processors);
    qsort(topology->caches, (size_t)topology->cache_count, sizeof(TopologyCache), compare_caches);
    // The classes, as ranks: one kind of core is all 0.
    int32_t ranks[64];
    int32_t rank_count = 0;
    for (int32_t i = 0; i < topology->count; i++) {
        int32_t class = topology->processors[i].efficiency;
        bool seen = false;
        for (int32_t r = 0; r < rank_count; r++) {
            seen |= ranks[r] == class;
        }

        if (!seen && rank_count < 64) {
            ranks[rank_count++] = class;
        }
    }

    qsort(ranks, (size_t)rank_count, sizeof(int32_t), compare_ids);
    for (int32_t i = 0; i < topology->count; i++) {
        int32_t rank = 0;
        while (rank < rank_count - 1 && ranks[rank] != topology->processors[i].efficiency) {
            rank++;
        }

        topology->processors[i].efficiency = rank;
    }
}

static void topology_free(Topology *topology) {
    for (int32_t i = 0; i < topology->cache_count; i++) {
        free(topology->caches[i].processors);
    }

    free(topology->caches);
    free(topology->processors);
}

// A growing array of Int, made with the type given.
typedef struct Numbers {
    int64_t *values;
    int64_t count, capacity;
} Numbers;

static void numbers_add(Numbers *numbers, int64_t value) {
    if (numbers->count == numbers->capacity) {
        numbers->capacity = numbers->capacity > 0 ? numbers->capacity * 2 : 64;
        int64_t *more = realloc(numbers->values, sizeof(int64_t) * (size_t)numbers->capacity);
        if (more == NULL) {
            sable_trap("out of memory");
        }

        numbers->values = more;
    }

    numbers->values[numbers->count++] = value;
}

static SableArray *numbers_array(Numbers *numbers, const SableMetadata *type) {
    SableArray *array = NULL;
    if (numbers->count > 0) {
        array = sable_alloc(type, (int64_t)sizeof(SableArray));
        array->elements = buffer_alloc(numbers->count * (int64_t)sizeof(int64_t));
        memcpy(array->elements, numbers->values, (size_t)numbers->count * sizeof(int64_t));
        array->capacity = numbers->count;
        array->count = numbers->count;
    }

    free(numbers->values);
    return array;
}

// The topology, for ProcessorTopology.current: how many processors and
// caches; each processor's id, core, package, node and class; then each
// cache's level, kind, size, line and count, and the processors sharing it.
SableArray *sable_thread_topology(const SableMetadata *type) {
    Topology topology = { 0 };
    topology_made(&topology);
    Numbers numbers = { 0 };
    numbers_add(&numbers, topology.count);
    numbers_add(&numbers, topology.cache_count);
    for (int32_t i = 0; i < topology.count; i++) {
        TopologyProcessor *processor = &topology.processors[i];
        numbers_add(&numbers, processor->id);
        numbers_add(&numbers, processor->core);
        numbers_add(&numbers, processor->package);
        numbers_add(&numbers, processor->node);
        numbers_add(&numbers, processor->efficiency);
    }

    for (int32_t i = 0; i < topology.cache_count; i++) {
        TopologyCache *cache = &topology.caches[i];
        numbers_add(&numbers, cache->level);
        numbers_add(&numbers, cache->kind);
        numbers_add(&numbers, cache->size);
        numbers_add(&numbers, cache->line);
        numbers_add(&numbers, cache->count);
        for (int32_t p = 0; p < cache->count; p++) {
            numbers_add(&numbers, cache->processors[p]);
        }
    }

    topology_free(&topology);
    return numbers_array(&numbers, type);
}

// ---- A thread's priority and affinity ------------------------------------------

bool sable_thread_priority_set(int64_t priority) {
    return priority >= -2 && priority <= 2 && thread_set_priority((int)priority);
}

int64_t sable_thread_priority_get(void) {
    return thread_priority();
}

// The ids of an array of Int, as int32_t: false where one is out of range.
static bool processor_ids(const SableArray *array, int32_t **ids, int32_t *count) {
    *count = array != NULL ? (int32_t)array->count : 0;
    *ids = malloc(sizeof(int32_t) * (size_t)(*count > 0 ? *count : 1));
    if (*ids == NULL) {
        sable_trap("out of memory");
    }

    for (int32_t i = 0; i < *count; i++) {
        int64_t id = ((const int64_t *)array->elements)[i];
        if (id < 0 || id >= 1 << 20) {
            free(*ids);
            *ids = NULL;
            return false;
        }

        (*ids)[i] = (int32_t)id;
    }

    return true;
}

bool sable_thread_affinity_set(const SableArray *processors) {
    int32_t *ids, count;
    if (!processor_ids(processors, &ids, &count)) {
        return false;
    }

    bool bound = count > 0 && thread_set_affinity(ids, count);
    free(ids);
    return bound;
}

SableArray *sable_thread_affinity_get(const SableMetadata *type) {
    int32_t *ids;
    int32_t count = thread_affinity(&ids);
    Numbers numbers = { 0 };
    for (int32_t i = 0; i < count; i++) {
        numbers_add(&numbers, ids[i]);
    }

    free(ids);
    return numbers_array(&numbers, type);
}

// ---- The pool's placement ------------------------------------------------------

// What WorkerPool.configure asked of the pool's threads: how many cores to
// leave to the rest of the system as it starts, and where each worker is to
// run, which it takes up as it next looks for work after a change
// (placement_follow) — on macOS a thread can set only its own class. On
// lines of its own, which every worker reads and nothing else writes.
static struct {
    _Alignas(128) SpinLock lock;
    int32_t generation;    // changed with each configure; 0, none yet
    int32_t reserved;      // as the pool starts
    int32_t priority;
    int32_t *processors;   // NULL: any
    int32_t count;
    bool sized;            // the pool has taken 'reserved'
    _Alignas(128) char after[1];
} placement;

// The pool's size as it starts, SABLE_WORKERS not set: one a processor it may
// use, less those reserved, and at least one.
static int placement_workers(int processors) {
    spin_lock(&placement.lock);
    placement.sized = true;
    int count = placement.count > 0 && placement.count < processors ? placement.count : processors;
    count -= placement.reserved;
    spin_unlock(&placement.lock);
    return count > 0 ? count : 1;
}

// The processors the pool's threads could run on before any was bound, as
// the first to be placed found: what 'any' gives back.
static int32_t *pool_affinity, pool_affinity_count = -1;

__attribute__((noinline)) static int32_t placement_apply(void) {
    int32_t *before = NULL;
    int32_t before_count = __atomic_load_n(&pool_affinity_count, __ATOMIC_ACQUIRE) < 0 ? thread_affinity(&before) : 0;
    spin_lock(&placement.lock);
    if (pool_affinity_count < 0) {
        pool_affinity = before;
        __atomic_store_n(&pool_affinity_count, before_count, __ATOMIC_RELEASE);
        before = NULL;
    }

    int32_t generation = placement.generation;
    int32_t priority = placement.priority;
    int32_t count = placement.count > 0 ? placement.count : pool_affinity_count;
    const int32_t *from = placement.count > 0 ? placement.processors : pool_affinity;
    int32_t *ids = count > 0 ? malloc(sizeof(int32_t) * (size_t)count) : NULL;
    if (ids != NULL) {
        memcpy(ids, from, sizeof(int32_t) * (size_t)count);
    }

    spin_unlock(&placement.lock);
    free(before);
    thread_set_priority(priority);
    if (ids != NULL) {
        thread_set_affinity(ids, count);
        free(ids);
    }

    return generation;
}

// Called by a worker as it looks for work, with the placement it last took:
// the one it has now. A load it shares with no writer but configure; what
// it then reads is under the lock.
static inline int32_t placement_follow(int32_t placed) {
    return __builtin_expect(__atomic_load_n(&placement.generation, __ATOMIC_RELAXED) == placed, 1) ? placed : placement_apply();
}
