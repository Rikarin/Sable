// The Sable runtime: exclusivity — the dynamic checks of exclusive access.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Exclusivity -----------------------------------------------------------

// The accesses in progress to storage more than one part of the program can
// reach — a global, a field of an object (docs/02 §2.3): an inout or ref
// argument's, for the length of its call. Another access to storage that
// overlaps one in progress, either of them a change, is a trap: one that
// begins (a second inout argument), or a plain read or write as it happens
// (a function given '&counter' that reads 'counter'). Storage is a range of
// bytes, so a struct and a field inside it overlap. Kept in checked builds
// only; the compiler proves what it can without them.
//
// The compiled code checks sable_access_count itself before a plain read
// or write, and calls in only when an access is in progress: most of the
// time none is. Accesses nest with calls, so the list stays short.
typedef struct SableAccess {
    char *storage;
    int64_t size;
    int32_t modify;
} SableAccess;

static SableAccess *accesses;
int64_t sable_access_count;
static int64_t access_capacity;

// A storage of no bytes still has a place: one byte of it.
static bool overlaps(const SableAccess *access, const char *storage, int64_t size) {
    const char *end = storage + (size > 0 ? size : 1);
    const char *access_end = access->storage + (access->size > 0 ? access->size : 1);
    return storage < access_end && access->storage < end;
}

// A plain read ('modify' 0) or write of the storage, now.
void sable_access_check(void *storage, int64_t size, int32_t modify) {
    for (int64_t i = 0; i < sable_access_count; i++) {
        if ((modify || accesses[i].modify) && overlaps(&accesses[i], storage, size)) {
            trap_here("overlapping accesses to the same storage");
        }
    }
}

void sable_access_begin(void *storage, int64_t size, int32_t modify) {
    sable_access_check(storage, size, modify);
    if (sable_access_count == access_capacity) {
        access_capacity = access_capacity < 16 ? 16 : access_capacity * 2;
        accesses = realloc(accesses, (size_t)access_capacity * sizeof(SableAccess));
        if (accesses == NULL) {
            sable_trap("out of memory");
        }
    }

    accesses[sable_access_count++] = (SableAccess){ storage, size, modify };
}

void sable_access_end(void *storage) {
    for (int64_t i = sable_access_count - 1; i >= 0; i--) {
        if (accesses[i].storage == storage) {
            accesses[i] = accesses[--sable_access_count];
            return;
        }
    }
}
