// The Sable runtime: the file system by path, as Sable.IO's File, Directory and Temp see it.
// Part of sable_runtime.c, which includes every part in order (see there).

// Over the platform's calls (platform.h, "The file system"); a call that
// fails says why as a file handle's does ('sable_file_error').

// The bytes as a new array; none for none.
static SableArray *bytes_array(const char *bytes, size_t length, const SableMetadata *type) {
    if (length == 0) {
        return NULL;
    }

    SableArray *array = sable_alloc(type, (int64_t)sizeof(SableArray));
    array->elements = buffer_alloc((int64_t)length);
    memcpy(array->elements, bytes, length);
    array->capacity = (int64_t)length;
    array->count = (int64_t)length;
    return array;
}

// What is at the path, as FileMetadata's ten integers; none on failure.
SableArray *sable_fs_metadata(const SableString *path, bool follow, const SableMetadata *type) {
    char *name = c_string(path);
    FileMetadata metadata;
    bool found = fs_metadata(name, follow, &metadata, file_error_cleared());
    free(name);
    return found ? metadata_array(&metadata, type) : NULL;
}

bool sable_fs_set_permissions(const SableString *path, int64_t permissions) {
    char *name = c_string(path);
    bool set = fs_set_permissions(name, (int)permissions, file_error_cleared());
    free(name);
    return set;
}

bool sable_fs_set_time(const SableString *path, int64_t which, int64_t seconds, int64_t nanoseconds) {
    char *name = c_string(path);
    bool set = fs_set_time(name, (int)which, seconds, nanoseconds, file_error_cleared());
    free(name);
    return set;
}

bool sable_fs_make_directory(const SableString *path, int64_t permissions) {
    char *name = c_string(path);
    bool made = fs_make_directory(name, (int)permissions, file_error_cleared());
    free(name);
    return made;
}

bool sable_fs_remove(const SableString *path, bool directory) {
    char *name = c_string(path);
    bool removed = fs_remove(name, directory, file_error_cleared());
    free(name);
    return removed;
}

bool sable_fs_remove_tree(const SableString *path) {
    FileError *error = file_error_cleared();
    // A path cut at a NUL would be another's.
    if (memchr(path->bytes, '\0', (size_t)path->length) != NULL) {
        error->kind = FILE_ERROR_INVALID;
        return false;
    }

    char *name = c_string(path);
    bool removed = fs_remove_tree(name, error);
    free(name);
    return removed;
}

bool sable_fs_rename(const SableString *from, const SableString *to, bool replace) {
    char *source = c_string(from);
    char *target = c_string(to);
    bool renamed = fs_rename(source, target, replace, file_error_cleared());
    free(source);
    free(target);
    return renamed;
}

bool sable_fs_link(const SableString *target, const SableString *path, int64_t kind) {
    char *to = c_string(target);
    char *name = c_string(path);
    bool linked = fs_link(to, name, (int)kind, file_error_cleared());
    free(to);
    free(name);
    return linked;
}

// The bytes of where a symbolic link leads; none on failure.
SableArray *sable_fs_read_link(const SableString *path, const SableMetadata *type) {
    char *name = c_string(path);
    char *target = fs_read_link(name, file_error_cleared());
    free(name);
    if (target == NULL) {
        return NULL;
    }

    SableArray *bytes = bytes_array(target, strlen(target), type);
    free(target);
    return bytes;
}

// A directory open to read its entries: a handle, or 0.
int64_t sable_fs_open_directory(const SableString *path) {
    char *name = c_string(path);
    void *directory = directory_open(name, file_error_cleared());
    free(name);
    return (int64_t)(intptr_t)directory;
}

// The next entry: a byte for its kind (255 where the directory does not
// say), then its name's; none at the end, or on failure.
SableArray *sable_fs_next_entry(int64_t handle, const SableMetadata *type) {
    int kind = -1;
    char *name = directory_next((void *)(intptr_t)handle, &kind, file_error_cleared());
    if (name == NULL) {
        return NULL;
    }

    size_t length = strlen(name);
    char *entry = malloc(length + 1);
    if (entry == NULL) {
        sable_trap("out of memory");
    }

    entry[0] = (char)kind;
    memcpy(entry + 1, name, length);
    SableArray *bytes = bytes_array(entry, length + 1, type);
    free(entry);
    free(name);
    return bytes;
}

void sable_fs_close_directory(int64_t handle) {
    directory_close((void *)(intptr_t)handle);
}
