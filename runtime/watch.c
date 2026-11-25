// The Sable runtime: files watched as they change, as Sable.IO's FileWatcher sees them.
// Part of sable_runtime.c, which includes every part in order (see there).

// A watch: what the platform (platform.h, "Watching") has reported and no
// task has taken, and what a task waits for more with: an event of the
// reactor's, or, where the system gives changes to a descriptor (inotify),
// that descriptor, read only as events are taken.
typedef struct WatchEvent {
    char *bytes; // what happened, a byte, and then the path's
    size_t length;
    struct WatchEvent *next;
} WatchEvent;

typedef struct FileWatch {
    SpinLock lock; // of the queue
    WatchEvent *first, *last;
    int64_t count;
    int64_t source; // the event, or the descriptor's
    bool polled;    // whether it is a descriptor
    void *state;    // the platform's
    PlatformMutex reading;
} FileWatch;

// So many not taken are kept, as many as inotify does unless told; then all
// are dropped for one that says to look again.
enum { WATCH_MOST = 16384 };

static SpinLock watches_lock;
static bool watches_stopped;

// The program ends: no task is told of a change from now on.
static void watches_stop(void) {
    spin_lock(&watches_lock);
    watches_stopped = true;
    spin_unlock(&watches_lock);
}

static void watch_events_free(WatchEvent *event) {
    while (event != NULL) {
        WatchEvent *next = event->next;
        free(event->bytes);
        free(event);
        event = next;
    }
}

static void watch_report(void *watched, int kind, const char *path, size_t length) {
    FileWatch *watch = watched;
    WatchEvent *event = malloc(sizeof *event);
    char *bytes = malloc(length + 1);
    if (event == NULL || bytes == NULL) {
        sable_trap("out of memory");
    }

    bytes[0] = (char)kind;
    memcpy(bytes + 1, path, length);
    *event = (WatchEvent){ bytes, length + 1, NULL };

    WatchEvent *dropped = NULL;
    spin_lock(&watch->lock);
    if (watch->count >= WATCH_MOST) {
        dropped = watch->first;
        watch->first = watch->last = NULL;
        watch->count = 0;
        event->bytes[0] = WATCH_RESCAN;
        event->length = 1;
    }

    WatchEvent *last = watch->last;
    if (last != NULL && last->length == event->length && memcmp(last->bytes, event->bytes, event->length) == 0) {
        // A file written in many pieces: said once.
        dropped = event;
    } else {
        if (last == NULL) {
            watch->first = event;
        } else {
            last->next = event;
        }

        watch->last = event;
        watch->count++;
    }

    spin_unlock(&watch->lock);
    watch_events_free(dropped);
    if (!watch->polled) {
        spin_lock(&watches_lock);
        if (!watches_stopped) {
            io_event(watch->source);
        }
        spin_unlock(&watches_lock);
    }
}

static bool watch_empty(FileWatch *watch) {
    spin_lock(&watch->lock);
    bool empty = watch->first == NULL;
    spin_unlock(&watch->lock);
    return empty;
}

// Watching the path, and, when asked, all inside it: a handle, or 0.
int64_t sable_fs_watch(const SableString *path, bool recursive) {
    FileError *error = file_error_cleared();
    FileWatch *watch = calloc(1, sizeof *watch);
    if (watch == NULL) {
        sable_trap("out of memory");
    }

    mutex_init(&watch->reading);
    watch->source = io_event_open(true);
    if (watch->source == 0) {
        // No reactor: WASI.
        error->kind = FILE_ERROR_UNSUPPORTED;
        free(watch);
        return 0;
    }

    char *name = c_string(path);
    watch->state = watch_open(watch, name, recursive, error);
    free(name);
    if (watch->state == NULL) {
        io_source_close(watch->source);
        watch_events_free(watch->first);
        free(watch);
        return 0;
    }

    PlatformSocket descriptor = watch_descriptor(watch->state);
    if (descriptor != -1) {
        io_source_close(watch->source);
        watch->polled = true;
        watch->source = io_source_open(descriptor, SOURCE_WATCH, REACTOR_READABLE);
        if (watch->source == 0) {
            error->kind = FILE_ERROR_OTHER;
            watch_close(watch->state);
            watch_events_free(watch->first);
            free(watch);
            return 0;
        }
    }

    return (int64_t)(intptr_t)watch;
}

// What to wait for, for more to be taken.
int64_t sable_fs_watch_event(int64_t handle) {
    return ((FileWatch *)(intptr_t)handle)->source;
}

// What happened next: a byte for what (platform.h's WATCH_), then the
// bytes of where, inside the path watched. None for now: to be waited for.
SableArray *sable_fs_watch_take(int64_t handle, const SableMetadata *type) {
    FileWatch *watch = (FileWatch *)(intptr_t)handle;
    mutex_lock(&watch->reading);
    if (watch->polled && watch_empty(watch)) {
        uint64_t ticks;
        PlatformSocket descriptor;
        IoSource *source = io_begin(watch->source, &descriptor, 0, &ticks);
        bool more = true;
        while (more && watch_empty(watch)) {
            more = watch_read(watch->state);
        }

        if (source != NULL) {
            if (!more && watch_empty(watch)) {
                io_would_block(source, 0, ticks);
            }

            io_end(source);
        }
    }

    spin_lock(&watch->lock);
    WatchEvent *event = watch->first;
    if (event != NULL) {
        watch->first = event->next;
        watch->count--;
        if (watch->first == NULL) {
            watch->last = NULL;
        }

        event->next = NULL;
    } else if (!watch->polled) {
        // Reported again after this, whatever is added after it.
        io_event_reset(watch->source);
    }

    spin_unlock(&watch->lock);
    mutex_unlock(&watch->reading);
    if (event == NULL) {
        return NULL;
    }

    SableArray *bytes = bytes_array(event->bytes, event->length, type);
    watch_events_free(event);
    return bytes;
}

// No longer watched: a task waiting wakes.
void sable_fs_unwatch(int64_t handle) {
    FileWatch *watch = (FileWatch *)(intptr_t)handle;
    io_source_close(watch->source);
    watch_close(watch->state);
    watch_events_free(watch->first);
    free(watch);
}
