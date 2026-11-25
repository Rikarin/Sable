// The Sable runtime: watching files on macOS and Linux — what platform.h's
// "Watching" says each platform gives. Part of platform_posix.c, which
// includes it after platform_fs_posix.c.

// A path and a name in it, in memory the caller frees.
static char *watch_join(const char *directory, const char *name) {
    size_t length = strlen(directory);
    size_t more = strlen(name);
    char *path = malloc(length + more + 2);
    if (path == NULL) {
        sable_trap("out of memory");
    }

    memcpy(path, directory, length);
    if (length > 0 && more > 0 && directory[length - 1] != '/') {
        path[length++] = '/';
    }

    memcpy(path + length, name, more + 1);
    return path;
}

static char *watch_copy(const char *text) {
    char *copy = strdup(text);
    if (copy == NULL) {
        sable_trap("out of memory");
    }

    return copy;
}

static bool watch_is_directory(const char *path) {
    struct stat info;
    return lstat(path, &info) == 0 && S_ISDIR(info.st_mode);
}

#if defined(__APPLE__)

// ---- FSEvents ----------------------------------------------------------------
//
// FSEvents is CoreServices', loaded only by a program that watches: one that
// does not links and loads nothing more. It says which paths something
// happened to, its flags gathered over a while — a file made and written
// and one only written may look the same — so the paths that are there are
// remembered, and what happened is told from whether the path is there now
// and was before. A file is watched by the directory it is in, the rest of
// which is ignored: watched itself, what takes its place a second time goes
// unsaid.

#include <dispatch/dispatch.h>

typedef void *FsEventStream;
typedef void (*FsEventCallback)(FsEventStream stream, void *context, size_t count, void *paths, const uint32_t *flags, const uint64_t *ids);

typedef struct FsEventContext {
    long version;
    void *info;
    const void *retain, *release, *describe;
} FsEventContext;

enum {
    FSEVENT_CREATE_FLAGS = 0x2 /* NoDefer */ | 0x4 /* WatchRoot */ | 0x10 /* FileEvents */,
    FSEVENT_MUST_SCAN = 0x1 | 0x2 | 0x4, // and what was dropped, by the system or by this program
    FSEVENT_ROOT_CHANGED = 0x20,
    FSEVENT_CREATED = 0x100,
    FSEVENT_REMOVED = 0x200,
    FSEVENT_RENAMED = 0x800,
    FSEVENT_MODIFIED = 0x1000,
};

static struct {
    const void *(*string)(const void *allocator, const uint8_t *bytes, long count, uint32_t encoding, unsigned char external);
    const void *(*array)(const void *allocator, const void **values, long count, const void *callbacks);
    void (*release)(const void *object);
    const void *array_callbacks;
    FsEventStream (*create)(const void *allocator, FsEventCallback callback, FsEventContext *context, const void *paths, uint64_t since, double latency, uint32_t flags);
    void (*set_queue)(FsEventStream stream, dispatch_queue_t queue);
    unsigned char (*start)(FsEventStream stream);
    void (*stop)(FsEventStream stream);
    void (*invalidate)(FsEventStream stream);
    void (*let_go)(FsEventStream stream);
    bool loaded;
} fsevents;

static void fsevents_load(void *context) {
    (void)context;
    void *services = dlopen("/System/Library/Frameworks/CoreServices.framework/CoreServices", RTLD_LAZY | RTLD_LOCAL);
    if (services == NULL) {
        return;
    }

    fsevents.string = dlsym(services, "CFStringCreateWithBytes");
    fsevents.array = dlsym(services, "CFArrayCreate");
    fsevents.release = dlsym(services, "CFRelease");
    fsevents.array_callbacks = dlsym(services, "kCFTypeArrayCallBacks");
    fsevents.create = dlsym(services, "FSEventStreamCreate");
    fsevents.set_queue = dlsym(services, "FSEventStreamSetDispatchQueue");
    fsevents.start = dlsym(services, "FSEventStreamStart");
    fsevents.stop = dlsym(services, "FSEventStreamStop");
    fsevents.invalidate = dlsym(services, "FSEventStreamInvalidate");
    fsevents.let_go = dlsym(services, "FSEventStreamRelease");
    fsevents.loaded = fsevents.string && fsevents.array && fsevents.release && fsevents.array_callbacks && fsevents.create
                      && fsevents.set_queue && fsevents.start && fsevents.stop && fsevents.invalidate && fsevents.let_go;
}

// The paths there were, inside the one watched: an open-addressed table.
typedef struct PathSlot {
    char *path;
    bool directory;
} PathSlot;

typedef struct PathSet {
    PathSlot *slots;
    size_t capacity, used; // 'used' counts the removed, whose slots keep 'path_removed'
    size_t count;
} PathSet;

static char path_removed[1];

static size_t path_hash(const char *path) {
    uint64_t hash = 14695981039346656037ull;
    for (; *path != '\0'; path++) {
        hash = (hash ^ (unsigned char)*path) * 1099511628211ull;
    }

    return (size_t)(hash ^ (hash >> 32));
}

static PathSlot *path_find(const PathSet *set, const char *path) {
    if (set->capacity == 0) {
        return NULL;
    }

    for (size_t at = path_hash(path) & (set->capacity - 1);; at = (at + 1) & (set->capacity - 1)) {
        PathSlot *slot = &set->slots[at];
        if (slot->path == NULL) {
            return NULL;
        }

        if (slot->path != path_removed && strcmp(slot->path, path) == 0) {
            return slot;
        }
    }
}

static void path_place(PathSet *set, PathSlot slot) {
    size_t at = path_hash(slot.path) & (set->capacity - 1);
    while (set->slots[at].path != NULL) {
        at = (at + 1) & (set->capacity - 1);
    }

    set->slots[at] = slot;
    set->used++;
    set->count++;
}

// Whether the path was not there, and is now.
static bool path_add(PathSet *set, const char *path, bool directory) {
    PathSlot *known = path_find(set, path);
    if (known != NULL) {
        known->directory = directory;
        return false;
    }

    if ((set->used + 1) * 4 > set->capacity * 3) {
        PathSet grown = { .capacity = 64 };
        while (grown.capacity < (set->count + 1) * 2) {
            grown.capacity *= 2;
        }

        grown.slots = calloc(grown.capacity, sizeof(PathSlot));
        if (grown.slots == NULL) {
            sable_trap("out of memory");
        }

        for (size_t i = 0; i < set->capacity; i++) {
            if (set->slots[i].path != NULL && set->slots[i].path != path_removed) {
                path_place(&grown, set->slots[i]);
            }
        }

        free(set->slots);
        *set = grown;
    }

    path_place(set, (PathSlot){ watch_copy(path), directory });
    return true;
}

static void path_drop(PathSet *set, PathSlot *slot) {
    free(slot->path);
    slot->path = path_removed;
    set->count--;
}

// The path no longer there, nor anything that was inside it.
static void path_remove(PathSet *set, const char *path) {
    PathSlot *known = path_find(set, path);
    if (known == NULL) {
        return;
    }

    bool directory = known->directory;
    path_drop(set, known);
    size_t length = strlen(path);
    for (size_t i = 0; directory && i < set->capacity; i++) {
        char *held = set->slots[i].path;
        if (held != NULL && held != path_removed && strncmp(held, path, length) == 0 && held[length] == '/') {
            path_drop(set, &set->slots[i]);
        }
    }
}

static void path_clear(PathSet *set) {
    for (size_t i = 0; i < set->capacity; i++) {
        if (set->slots[i].path != NULL && set->slots[i].path != path_removed) {
            path_drop(set, &set->slots[i]);
        }
    }

    free(set->slots);
    *set = (PathSet){ 0 };
}

typedef struct WatchState {
    void *watch;
    char *root; // its real path, as FSEvents gives paths
    bool recursive, directory;
    bool gone; // the root, as was last said
    FsEventStream stream;
    dispatch_queue_t queue;
    PathSet there;
} WatchState;

// What the directory holds remembered as there, and, when 'report', what
// was not known to be reported as made.
static void watch_learn(WatchState *state, const char *relative, bool report) {
    char *path = watch_join(state->root, relative);
    DIR *entries = opendir(path);
    if (entries == NULL) {
        free(path);
        return;
    }

    struct dirent *entry;
    while ((entry = readdir(entries)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }

        char *inside = watch_join(relative, entry->d_name);
        bool directory = entry->d_type == DT_DIR;
        if (entry->d_type == DT_UNKNOWN) {
            char *whole = watch_join(state->root, inside);
            directory = watch_is_directory(whole);
            free(whole);
        }

        if (path_add(&state->there, inside, directory) && report) {
            watch_report(state->watch, WATCH_CREATED, inside, strlen(inside));
        }

        if (state->recursive && directory) {
            watch_learn(state, inside, report);
        }

        free(inside);
    }

    closedir(entries);
    free(path);
}

// The root's going and coming, told once each.
static void watch_root(WatchState *state) {
    struct stat info;
    bool gone = lstat(state->root, &info) != 0;
    if (gone != state->gone) {
        state->gone = gone;
        if (gone) {
            path_clear(&state->there);
        }
        watch_report(state->watch, gone ? WATCH_REMOVED : WATCH_CREATED, "", 0);
        if (!gone && state->directory) {
            watch_learn(state, "", true);
        }
    }
}

// Whether something there before and now was written, or had its place taken.
static bool watch_is_change(uint32_t flags) {
    return (flags & (FSEVENT_MODIFIED | FSEVENT_RENAMED)) != 0
           || (flags & (FSEVENT_CREATED | FSEVENT_REMOVED)) == (FSEVENT_CREATED | FSEVENT_REMOVED);
}

static void watch_event(WatchState *state, const char *path, uint32_t flags) {
    if (flags & FSEVENT_MUST_SCAN) {
        // What is there is known anew, for what is told of after.
        path_clear(&state->there);
        if (state->directory) {
            watch_learn(state, "", false);
        }
        watch_report(state->watch, WATCH_RESCAN, "", 0);
        return;
    }

    size_t length = strlen(state->root);
    bool at_root = strcmp(path, state->root) == 0;
    if ((flags & FSEVENT_ROOT_CHANGED) || at_root) {
        watch_root(state);
        if (at_root && !state->directory && !state->gone && watch_is_change(flags)) {
            watch_report(state->watch, WATCH_MODIFIED, "", 0);
        }
        return;
    }

    bool top = length == 1; // the root of the file system
    if (strncmp(path, state->root, length) != 0 || (!top && path[length] != '/')) {
        return;
    }

    const char *relative = path + length + (top ? 0 : 1);
    if (relative[0] == '\0' || (!state->recursive && strchr(relative, '/') != NULL)) {
        return;
    }

    struct stat info;
    bool exists = lstat(path, &info) == 0;
    bool known = path_find(&state->there, relative) != NULL;
    if (!exists) {
        if (known) {
            path_remove(&state->there, relative);
            watch_report(state->watch, WATCH_REMOVED, relative, strlen(relative));
        } else if (flags & FSEVENT_CREATED) {
            // Made and gone again before it was heard of.
            watch_report(state->watch, WATCH_CREATED, relative, strlen(relative));
            watch_report(state->watch, WATCH_REMOVED, relative, strlen(relative));
        }
    } else if (!known) {
        path_add(&state->there, relative, S_ISDIR(info.st_mode));
        watch_report(state->watch, WATCH_CREATED, relative, strlen(relative));
        if (S_ISDIR(info.st_mode) && state->recursive) {
            watch_learn(state, relative, true);
        }
    } else if (!S_ISDIR(info.st_mode) && watch_is_change(flags)) {
        watch_report(state->watch, WATCH_MODIFIED, relative, strlen(relative));
    }
}

static void watch_heard(FsEventStream stream, void *context, size_t count, void *paths, const uint32_t *flags, const uint64_t *ids) {
    (void)stream;
    (void)ids;
    WatchState *state = context;
    for (size_t i = 0; i < count; i++) {
        watch_event(state, ((char **)paths)[i], flags[i]);
    }
}

static void watch_nothing(void *context) {
    (void)context;
}

static void watch_state_free(WatchState *state) {
    path_clear(&state->there);
    free(state->root);
    free(state);
}

// On the queue, so that what is heard meanwhile waits for it.
static void watch_begin(void *context) {
    WatchState *state = context;
    if (state->directory) {
        watch_learn(state, "", false);
    }
}

static void *watch_open(void *watch, const char *name, bool recursive, FileError *error) {
    static dispatch_once_t once;
    dispatch_once_f(&once, NULL, fsevents_load);
    struct stat info;
    if (stat(name, &info) != 0) {
        file_failed(error);
        return NULL;
    }

    char *root = realpath(name, NULL);
    if (root == NULL) {
        file_failed(error);
        return NULL;
    }

    if (!fsevents.loaded) {
        free(root);
        error->kind = FILE_ERROR_UNSUPPORTED;
        error->code = ENOTSUP;
        return NULL;
    }

    WatchState *state = calloc(1, sizeof *state);
    if (state == NULL) {
        sable_trap("out of memory");
    }

    state->watch = watch;
    state->root = root;
    state->recursive = recursive;
    state->directory = S_ISDIR(info.st_mode);
    state->queue = dispatch_queue_create("sable.watch", DISPATCH_QUEUE_SERIAL);
    size_t length = strlen(root);
    if (!state->directory) {
        length = (size_t)(strrchr(root, '/') - root);
        length = length == 0 ? 1 : length;
    }

    const void *path = fsevents.string(NULL, (const uint8_t *)root, (long)length, 0x08000100 /* UTF-8 */, 0);
    const void *paths = path == NULL ? NULL : fsevents.array(NULL, &path, 1, fsevents.array_callbacks);
    FsEventContext context = { .info = state };
    if (paths != NULL) {
        state->stream = fsevents.create(NULL, watch_heard, &context, paths, UINT64_MAX /* since now */, 0.02, FSEVENT_CREATE_FLAGS);
        fsevents.release(paths);
    }

    if (path != NULL) {
        fsevents.release(path);
    }

    bool started = false;
    if (state->stream != NULL) {
        // Held back until what is there is known.
        dispatch_suspend(state->queue);
        fsevents.set_queue(state->stream, state->queue);
        started = fsevents.start(state->stream) != 0;
        if (started) {
            watch_begin(state);
        }
        dispatch_resume(state->queue);
    }

    if (!started) {
        if (state->stream != NULL) {
            fsevents.invalidate(state->stream);
            fsevents.let_go(state->stream);
        }
        dispatch_release(state->queue);
        watch_state_free(state);
        error->kind = FILE_ERROR_OTHER;
        error->code = EIO;
        return NULL;
    }

    return state;
}

static PlatformSocket watch_descriptor(void *state) {
    (void)state;
    return -1;
}

static bool watch_read(void *state) {
    (void)state;
    return false;
}

static void watch_close(void *handle) {
    WatchState *state = handle;
    fsevents.stop(state->stream);
    fsevents.invalidate(state->stream);
    // What was being told is told first: nothing is after.
    dispatch_sync_f(state->queue, NULL, watch_nothing);
    fsevents.let_go(state->stream);
    dispatch_release(state->queue);
    watch_state_free(state);
}

#else

// ---- inotify -----------------------------------------------------------------
//
// inotify watches one directory, not what is inside it: each directory is
// watched as it is found, and what one found later already holds is
// reported as made (inotify may say so too: twice is better than not). A
// file is watched by the directory it is in, the rest of which is ignored:
// a watch of its own would be of the file there is now, and not of the one
// that takes its place.

#include <sys/inotify.h>

enum {
    INOTIFY_WANTED = IN_CREATE | IN_DELETE | IN_MODIFY | IN_MOVED_FROM | IN_MOVED_TO | IN_DELETE_SELF | IN_MOVE_SELF | IN_EXCL_UNLINK,
};

typedef struct WatchedDirectory {
    int descriptor;
    char *relative; // inside the root
} WatchedDirectory;

typedef struct WatchState {
    void *watch;
    char *root; // as given, or the directory of the file watched
    char *only; // the one name in it watched, or NULL
    bool recursive;
    int inotify;
    WatchedDirectory *directories;
    size_t count, capacity;
} WatchState;

static WatchedDirectory *watch_find(WatchState *state, int descriptor) {
    for (size_t i = 0; i < state->count; i++) {
        if (state->directories[i].descriptor == descriptor) {
            return &state->directories[i];
        }
    }

    return NULL;
}

static void watch_forget(WatchState *state, size_t i) {
    free(state->directories[i].relative);
    state->directories[i] = state->directories[--state->count];
}

// The directory watched, and, when recursive, those in it, 'report'ing what
// they hold. A link is not followed, but to the root itself.
static bool watch_add(WatchState *state, const char *relative, bool report) {
    char *path = watch_join(state->root, relative);
    bool root = relative[0] == '\0';
    int descriptor = inotify_add_watch(state->inotify, path, INOTIFY_WANTED | (root ? 0 : IN_ONLYDIR | IN_DONT_FOLLOW));
    if (descriptor < 0) {
        int code = errno;
        free(path);
        errno = code;
        // Gone already, it is no failure.
        return !root && (code == ENOENT || code == ENOTDIR || code == ELOOP);
    }

    WatchedDirectory *known = watch_find(state, descriptor);
    if (known != NULL) {
        // Moved, and watched before by its old name.
        free(known->relative);
        known->relative = watch_copy(relative);
    } else {
        if (state->count == state->capacity) {
            state->capacity = state->capacity == 0 ? 16 : state->capacity * 2;
            state->directories = realloc(state->directories, state->capacity * sizeof(WatchedDirectory));
            if (state->directories == NULL) {
                sable_trap("out of memory");
            }
        }

        state->directories[state->count++] = (WatchedDirectory){ descriptor, watch_copy(relative) };
    }

    bool added = true;
    DIR *entries = state->recursive ? opendir(path) : NULL;
    if (entries != NULL) {
        struct dirent *entry;
        while (added && (entry = readdir(entries)) != NULL) {
            if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
                continue;
            }

            char *inside = watch_join(relative, entry->d_name);
            if (report) {
                watch_report(state->watch, WATCH_CREATED, inside, strlen(inside));
            }

            if (entry->d_type == DT_DIR || entry->d_type == DT_UNKNOWN) {
                char *directory = watch_join(state->root, inside);
                if (watch_is_directory(directory)) {
                    added = watch_add(state, inside, report);
                }
                free(directory);
            }

            free(inside);
        }

        closedir(entries);
    }

    free(path);
    return added;
}

static void watch_close(void *handle) {
    WatchState *state = handle;
    close(state->inotify);
    while (state->count > 0) {
        watch_forget(state, state->count - 1);
    }

    free(state->directories);
    free(state->root);
    free(state->only);
    free(state);
}

static void *watch_open(void *watch, const char *name, bool recursive, FileError *error) {
    struct stat info;
    char *file = NULL;
    if (stat(name, &info) != 0 || (!S_ISDIR(info.st_mode) && (file = realpath(name, NULL)) == NULL)) {
        file_failed(error);
        return NULL;
    }

    int descriptor = inotify_init1(IN_NONBLOCK | IN_CLOEXEC);
    if (descriptor < 0) {
        file_failed(error);
        free(file);
        return NULL;
    }

    WatchState *state = calloc(1, sizeof *state);
    if (state == NULL) {
        sable_trap("out of memory");
    }

    state->watch = watch;
    if (file != NULL) {
        // Absolute, so a separator is before its name.
        char *last = strrchr(file, '/');
        state->only = watch_copy(last + 1);
        last[last == file ? 1 : 0] = '\0';
        state->root = file;
    } else {
        state->root = watch_copy(name);
        state->recursive = recursive;
    }

    state->inotify = descriptor;
    errno = 0;
    if (!watch_add(state, "", false)) {
        file_failed(error);
        watch_close(state);
        return NULL;
    }

    return state;
}

static PlatformSocket watch_descriptor(void *state) {
    return ((WatchState *)state)->inotify;
}

static void watch_event(WatchState *state, const struct inotify_event *event) {
    if (event->mask & IN_Q_OVERFLOW) {
        // A directory made meanwhile may be watched by no one.
        if (state->recursive) {
            (void)!watch_add(state, "", false);
        }
        watch_report(state->watch, WATCH_RESCAN, "", 0);
        return;
    }

    WatchedDirectory *found = watch_find(state, event->wd);
    if (found == NULL) {
        return;
    }

    if (event->mask & IN_IGNORED) {
        watch_forget(state, (size_t)(found - state->directories));
        return;
    }

    bool root = found->relative[0] == '\0';
    if (event->mask & (IN_DELETE_SELF | IN_MOVE_SELF)) {
        // Any other's is said by the directory it is in.
        if (root) {
            watch_report(state->watch, WATCH_REMOVED, "", 0);
            if (event->mask & IN_MOVE_SELF) {
                // What happens in it now is at no path of its old name.
                while (state->count > 0) {
                    inotify_rm_watch(state->inotify, state->directories[state->count - 1].descriptor);
                    watch_forget(state, state->count - 1);
                }
            }
        }
        return;
    }

    if (event->len == 0 || (state->only != NULL && strcmp(event->name, state->only) != 0)) {
        return;
    }

    char *inside = state->only != NULL ? watch_copy("") : watch_join(found->relative, event->name);
    size_t length = strlen(inside);
    if (event->mask & (IN_CREATE | IN_MOVED_TO)) {
        watch_report(state->watch, WATCH_CREATED, inside, length);
        if ((event->mask & IN_ISDIR) && state->recursive && !watch_add(state, inside, true)) {
            // No more can be watched: what is in it must be looked for.
            watch_report(state->watch, WATCH_RESCAN, inside, length);
        }
    } else if (event->mask & (IN_DELETE | IN_MOVED_FROM)) {
        watch_report(state->watch, WATCH_REMOVED, inside, length);
        if ((event->mask & (IN_ISDIR | IN_MOVED_FROM)) == (IN_ISDIR | IN_MOVED_FROM)) {
            // Moved out, or to where it is found again, by its new name.
            for (size_t i = state->count; i-- > 0;) {
                const char *relative = state->directories[i].relative;
                if (strncmp(relative, inside, length) == 0 && (relative[length] == '\0' || relative[length] == '/')) {
                    inotify_rm_watch(state->inotify, state->directories[i].descriptor);
                    watch_forget(state, i);
                }
            }
        }
    } else if ((event->mask & IN_MODIFY) && !(event->mask & IN_ISDIR)) {
        watch_report(state->watch, WATCH_MODIFIED, inside, length);
    }

    free(inside);
}

static bool watch_read(void *handle) {
    WatchState *state = handle;
    _Alignas(struct inotify_event) char buffer[16384];
    ssize_t got;
    do {
        got = read(state->inotify, buffer, sizeof buffer);
    } while (got < 0 && errno == EINTR);
    if (got <= 0) {
        return false;
    }

    for (char *at = buffer; at < buffer + got;) {
        const struct inotify_event *event = (const struct inotify_event *)at;
        watch_event(state, event);
        at += sizeof *event + event->len;
    }

    return true;
}

#endif
