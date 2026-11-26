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
// The compiled code checks the count itself before a plain read or write,
// and calls in only when an access is in progress: most of the time none
// is. Accesses nest with calls, so the list stays short.
typedef struct SableAccess {
    char *storage;
    int64_t size;
    int32_t modify;
} SableAccess;

// A task's accesses, while it does not run; and a thread's own, while it
// runs a task. Empty for most.
typedef struct SableAccesses {
    SableAccess *list;
    int64_t count, capacity;
} SableAccesses;

// Those of what the thread runs now: a task's are its own, and go with it as
// it waits with one in progress — an async function's inout argument — and
// another thread goes on with it (tasks.c's step). One thread-local, whose
// address the compiled code asks for once a call and passes to each of
// these: on macOS a thread-local is found by a call (_tlv_get_addr), which,
// made for every access and every check, cost more than the checks (#346).
static _Thread_local SableAccesses sable_access_state;

// Where they are, for the compiled code: asked once a call.
SableAccesses *sable_access_state_here(void) {
    return &sable_access_state;
}

// The thread's accesses swapped for others.
static inline void accesses_swap(SableAccesses *other) {
    SableAccesses here = sable_access_state;
    sable_access_state = *other;
    *other = here;
}

// A storage of no bytes still has a place: one byte of it.
static bool overlaps(const SableAccess *access, const char *storage, int64_t size) {
    const char *end = storage + (size > 0 ? size : 1);
    const char *access_end = access->storage + (access->size > 0 ? access->size : 1);
    return storage < access_end && access->storage < end;
}

_Noreturn void sable_trap_at(const char *message, const char *site); // traps.c

// 'site' is where in the source the access is, for the trap.
static inline void access_check(const SableAccesses *state, void *storage, int64_t size, int32_t modify, const char *site) {
    for (int64_t i = 0; i < state->count; i++) {
        if ((modify || state->list[i].modify) && overlaps(&state->list[i], storage, size)) {
            sable_trap_at("overlapping accesses to the same storage", site);
        }
    }
}

// A plain read ('modify' 0) or write of the storage, now. 'state' is the
// running thread's sable_access_state, as each of these is given it.
void sable_access_check(SableAccesses *state, void *storage, int64_t size, int32_t modify, const char *site) {
    access_check(state, storage, size, modify, site);
}

void sable_access_begin(SableAccesses *state, void *storage, int64_t size, int32_t modify, const char *site) {
    access_check(state, storage, size, modify, site);
    if (state->count == state->capacity) {
        state->capacity = state->capacity < 16 ? 16 : state->capacity * 2;
        state->list = realloc(state->list, (size_t)state->capacity * sizeof(SableAccess));
        if (state->list == NULL) {
            sable_trap("out of memory");
        }
    }

    state->list[state->count++] = (SableAccess){ storage, size, modify };
}

void sable_access_end(SableAccesses *state, void *storage) {
    for (int64_t i = state->count - 1; i >= 0; i--) {
        if (state->list[i].storage == storage) {
            state->list[i] = state->list[--state->count];
            return;
        }
    }
}
