// The Sable runtime: C and Sable calling each other — threads the runtime did
// not make, closures C holds a context of, libraries loaded as the program
// runs, the system's own handles (Sable.Interop).
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Threads the runtime did not make ----------------------------------------

// C may call a Sable function on a thread of its own. Every entry from C asks
// first (sable_foreign_enter): a thread seen for the first time is given what
// the runtime's own threads have — a task to be running, so that it is not
// taken for the main one; a frame slab; a SableThread, the allocator's. They
// are kept in a record, which goes to the next such thread once this one
// ends: the number of records is how many there were at once, and a task
// spawned by one that has gone still has a parent.
//
// Objects crossing to it are what docs/02 §2.7 asks: what C can call on any
// thread was shared as it was given to C, as were the globals' values
// (sable_threads_begin), before C could call it.
typedef struct ForeignThread {
    SableTask task;
    SableThread *state;
    struct ForeignThread *next;
} ForeignThread;

static ForeignThread *foreign_free;
static void library_failed(char *message);
static SpinLock foreign_lock;

// As the thread ends: the platform calls it with what platform_at_thread_exit
// was given. The thread may yet call Sable again, from another library's
// clean-up: it is then one seen for the first time.
static void foreign_thread_end(void *pointer) {
    ForeignThread *thread = pointer;
    free(accesses);
    accesses = NULL;
    access_capacity = 0;
    free(share_stack);
    share_stack = NULL;
    share_capacity = 0;
    library_failed(NULL);
    current_task = &main_task;
    current_frames = NULL;
    if (this_thread_get() == thread->state) {
        this_thread_set(NULL);
    }

    spin_lock(&foreign_lock);
    thread->next = foreign_free;
    foreign_free = thread;
    spin_unlock(&foreign_lock);
}

SABLE_SLOW static void foreign_thread_begin(void) {
    spin_lock(&foreign_lock);
    ForeignThread *thread = foreign_free;
    if (thread != NULL) {
        foreign_free = thread->next;
    }

    spin_unlock(&foreign_lock);
    if (thread == NULL) {
        thread = calloc(1, sizeof *thread);
        if (thread == NULL) {
            sable_trap("out of memory");
        }

        slab_init(&thread->task.frames, thread->task.first_frames, sizeof thread->task.first_frames);
        thread->task.sleeper = -1;
        thread->task.live = 1; // the thread, which never finishes as a task does
        thread->task.inject_band = -1;
        thread->task.base_priority = thread->task.priority = PRIORITY_MEDIUM;
    }

    // A thread of the runtime's own blocking pool has its state already.
    if (this_thread_get() == NULL && thread->state != NULL) {
        this_thread_set(thread->state);
    } else if (thread->state == NULL && this_thread_get() == NULL) {
        thread->state = current_thread();
    }

    switch_to(&thread->task);
    platform_at_thread_exit(thread);
}

// First in every function C calls by its address or its name.
void sable_foreign_enter(void) {
    if (__builtin_expect(current_task == &main_task && !on_main_thread, 0)) {
        foreign_thread_begin();
    }
}

// ---- Closures C holds ----------------------------------------------------------

// A closure given to C goes as a C function, which is the same for every
// closure of its type, and a context that says which. The context is not an
// address: it is a place in a table and how many closures were in that place
// before, so that C calling one let go of finds that out, and the program
// stops, rather than memory that is another's by now.
//
// A cell's 'state' is the generation (its upper half), whether it is let go
// of, and how many calls are in it: one is let go of by its owner, or by the
// last call in it to return, whichever is later.
enum { CALLBACK_CHUNK = 256, CALLBACK_CHUNKS = 4096, CONTEXT_INDEX_BITS = sizeof(void *) == 8 ? 32 : 20 };

#define CALLBACK_GONE UINT64_C(0x80000000)

typedef struct CallbackCell {
    uint64_t state;
    void *closure[2];  // a function value: its code, and what it captured, counted
    const void *thread; // the only one to call it on, or NULL
    uint32_t next_free;
} CallbackCell;

static CallbackCell *callback_chunks[CALLBACK_CHUNKS];
static uint32_t callback_count, callback_free; // under the lock; a place is its index and one, 0 none
static SpinLock callback_lock;
static _Thread_local char this_thread_mark; // its address is the thread's own

static uint64_t generation_mask(void) {
    return (UINT64_C(1) << (sizeof(void *) * 8 - CONTEXT_INDEX_BITS)) - 1;
}

static CallbackCell *callback_cell(uint32_t place) {
    uint32_t index = place - 1;
    CallbackCell *chunk = index / CALLBACK_CHUNK < CALLBACK_CHUNKS ? __atomic_load_n(&callback_chunks[index / CALLBACK_CHUNK], __ATOMIC_ACQUIRE) : NULL;
    return chunk == NULL ? NULL : &chunk[index % CALLBACK_CHUNK];
}

// The function value at the address moved into the table: its context. One
// 'confined' is called on this thread alone; any other may be on any, so what
// it captured is shared already, and what the globals hold is now.
int64_t sable_callback_begin(int64_t closure, bool confined) {
    if (!confined) {
        sable_threads_begin();
    }

    spin_lock(&callback_lock);
    uint32_t place = callback_free;
    if (place != 0) {
        callback_free = callback_cell(place)->next_free;
    } else {
        if (callback_count % CALLBACK_CHUNK == 0) {
            CallbackCell *chunk = callback_count / CALLBACK_CHUNK < CALLBACK_CHUNKS ? calloc(CALLBACK_CHUNK, sizeof *chunk) : NULL;
            if (chunk == NULL) {
                sable_trap("too many closures given to C at once");
            }

            __atomic_store_n(&callback_chunks[callback_count / CALLBACK_CHUNK], chunk, __ATOMIC_RELEASE);
        }

        place = ++callback_count;
    }

    spin_unlock(&callback_lock);
    CallbackCell *cell = callback_cell(place);
    memcpy(cell->closure, (const void *)(intptr_t)closure, sizeof cell->closure);
    cell->thread = confined ? &this_thread_mark : NULL;
    uint64_t generation = (__atomic_load_n(&cell->state, __ATOMIC_RELAXED) >> 32) + 1 & generation_mask();
    generation = generation == 0 ? 1 : generation;
    __atomic_store_n(&cell->state, generation << 32, __ATOMIC_RELEASE);
    return (int64_t)(generation << CONTEXT_INDEX_BITS | place);
}

static CallbackCell *callback_of(int64_t context, uint64_t *generation) {
    uint64_t bits = (uint64_t)context & UINTPTR_MAX;
    *generation = bits >> CONTEXT_INDEX_BITS;
    uint64_t place = bits & ((UINT64_C(1) << CONTEXT_INDEX_BITS) - 1);
    return place == 0 ? NULL : callback_cell((uint32_t)place);
}

static void callback_done(CallbackCell *cell, uint32_t place) {
    void *captured = cell->closure[1];
    cell->closure[0] = cell->closure[1] = NULL;
    spin_lock(&callback_lock);
    cell->next_free = callback_free;
    callback_free = place;
    spin_unlock(&callback_lock);
    sable_release(captured);
}

// C calls the closure: where its function value is, to be copied and called.
int64_t sable_callback_enter(int64_t context) {
    sable_foreign_enter();
    uint64_t generation;
    CallbackCell *cell = callback_of(context, &generation);
    if (cell == NULL) {
        sable_trap("C called a closure with a context that is none");
    }

    uint64_t state = __atomic_load_n(&cell->state, __ATOMIC_ACQUIRE);
    do {
        if (state >> 32 != generation || (state & CALLBACK_GONE) != 0) {
            sable_trap("C called a closure that was let go of");
        }
    } while (!__atomic_compare_exchange_n(&cell->state, &state, state + 1, true, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE));

    if (cell->thread != NULL && cell->thread != &this_thread_mark) {
        sable_trap("C called a closure on another thread than the one that lent it: one called on any thread is a CCallback");
    }

    return (int64_t)(intptr_t)cell->closure;
}

void sable_callback_leave(int64_t context) {
    uint64_t generation;
    CallbackCell *cell = callback_of(context, &generation);
    if (__atomic_sub_fetch(&cell->state, 1, __ATOMIC_ACQ_REL) == (generation << 32 | CALLBACK_GONE)) {
        callback_done(cell, (uint32_t)((uint64_t)context & ((UINT64_C(1) << CONTEXT_INDEX_BITS) - 1)));
    }
}

// Let go of by its owner: C may not call it from now on.
void sable_callback_end(int64_t context) {
    uint64_t generation;
    CallbackCell *cell = callback_of(context, &generation);
    if (__atomic_or_fetch(&cell->state, CALLBACK_GONE, __ATOMIC_ACQ_REL) == (generation << 32 | CALLBACK_GONE)) {
        callback_done(cell, (uint32_t)((uint64_t)context & ((UINT64_C(1) << CONTEXT_INDEX_BITS) - 1)));
    }
}

// ---- Addresses -----------------------------------------------------------------

// Where an array's elements are; NULL for the empty one.
void *sable_buffer_elements(const SableArray *array) {
    return array == NULL ? NULL : array->elements;
}

// ---- Libraries -----------------------------------------------------------------

// Over the platform's (platform.h). What a call failed with is the thread's,
// for as long as it makes no other.
static _Thread_local char *library_error;

static void library_failed(char *message) {
    free(library_error);
    library_error = message;
}

const char *sable_library_error(void) {
    return library_error != NULL ? library_error : "";
}

void *sable_library_open(const char *path, int32_t flags) {
    char *message = NULL;
    void *library = library_open(path, flags, &message);
    library_failed(message);
    return library;
}

// Whether the library has the symbol; where it is, which may be NULL.
bool sable_library_symbol(void *library, const char *name, void **address) {
    char *message = NULL;
    bool found = library_symbol(library, name, address, &message);
    library_failed(message);
    return found;
}

void sable_library_close(void *library) {
    library_close(library);
}

// ---- Descriptors and handles ---------------------------------------------------

bool sable_fd_close(int32_t fd) {
    return descriptor_close(fd, file_error_cleared());
}

// Another descriptor of the same file, which no program run is given; -1 on
// failure, which sable_file_error tells of.
int32_t sable_fd_duplicate(int32_t fd) {
    return descriptor_duplicate(fd, file_error_cleared());
}

#ifdef _WIN32
bool sable_handle_close(void *handle) {
    return CloseHandle(handle) != 0;
}

void *sable_handle_duplicate(void *handle) {
    HANDLE made = NULL;
    FileError *error = file_error_cleared();
    if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), &made, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
        file_failed(error);
        return NULL;
    }

    return made;
}
#endif
