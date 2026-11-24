// The Sable runtime: the file system by path on POSIX and WASI — what
// platform.h's "The file system" says each platform gives. Part of
// platform_posix.c and platform_wasi.c, which include it after
// platform_files_posix.c.

#if defined(__linux__)
#include <sys/syscall.h>
#endif

#ifndef O_CLOEXEC
#define O_CLOEXEC 0
#endif

static int file_kind_of(mode_t mode) {
    return S_ISREG(mode)  ? FILE_KIND_FILE
         : S_ISDIR(mode)  ? FILE_KIND_DIRECTORY
         : S_ISLNK(mode)  ? FILE_KIND_LINK
         : S_ISFIFO(mode) ? FILE_KIND_FIFO
         : S_ISSOCK(mode) ? FILE_KIND_SOCKET
         : S_ISCHR(mode)  ? FILE_KIND_CHARACTER
         : S_ISBLK(mode)  ? FILE_KIND_BLOCK
                          : FILE_KIND_OTHER;
}

static void metadata_of(const struct stat *info, FileMetadata *metadata) {
    metadata->size = info->st_size;
    metadata->kind = file_kind_of(info->st_mode);
    metadata->permissions = info->st_mode & 07777;
    metadata->has_created = 0;
    metadata->created[0] = metadata->created[1] = 0;
#if defined(__APPLE__)
    metadata->modified[0] = info->st_mtimespec.tv_sec;
    metadata->modified[1] = info->st_mtimespec.tv_nsec;
    metadata->accessed[0] = info->st_atimespec.tv_sec;
    metadata->accessed[1] = info->st_atimespec.tv_nsec;
    metadata->created[0] = info->st_birthtimespec.tv_sec;
    metadata->created[1] = info->st_birthtimespec.tv_nsec;
    metadata->has_created = 1;
#else
    metadata->modified[0] = info->st_mtim.tv_sec;
    metadata->modified[1] = info->st_mtim.tv_nsec;
    metadata->accessed[0] = info->st_atim.tv_sec;
    metadata->accessed[1] = info->st_atim.tv_nsec;
#endif
}

// When it was made, which Linux's stat does not say and statx does, on a
// file system that keeps it.
static void created_of(int fd, const char *name, bool follow, FileMetadata *metadata) {
#if defined(__linux__) && defined(STATX_BTIME) && defined(SYS_statx)
    struct statx info;
    int flags = (name[0] == '\0' ? AT_EMPTY_PATH : 0) | (follow ? 0 : AT_SYMLINK_NOFOLLOW);
    if (syscall(SYS_statx, fd, name, flags, STATX_BTIME, &info) == 0 && (info.stx_mask & STATX_BTIME)) {
        metadata->created[0] = info.stx_btime.tv_sec;
        metadata->created[1] = info.stx_btime.tv_nsec;
        metadata->has_created = 1;
    }
#else
    (void)fd;
    (void)name;
    (void)follow;
    (void)metadata;
#endif
}

static bool file_metadata(PlatformFile file, FileMetadata *metadata, FileError *error) {
    struct stat info;
    if (fstat((int)file, &info) != 0) {
        file_failed(error);
        return false;
    }

    metadata_of(&info, metadata);
    created_of((int)file, "", true, metadata);
    return true;
}

static bool fs_metadata(const char *name, bool follow, FileMetadata *metadata, FileError *error) {
    struct stat info;
    if ((follow ? stat(name, &info) : lstat(name, &info)) != 0) {
        file_failed(error);
        return false;
    }

    metadata_of(&info, metadata);
    created_of(AT_FDCWD, name, follow, metadata);
    return true;
}

static bool fs_set_permissions(const char *name, int permissions, FileError *error) {
#if defined(__wasi__)
    (void)name;
    (void)permissions;
    error->kind = FILE_ERROR_UNSUPPORTED;
    error->code = ENOTSUP;
    return false;
#else
    if (chmod(name, (mode_t)(permissions & 07777)) != 0) {
        file_failed(error);
        return false;
    }

    return true;
#endif
}

static bool fs_set_time(const char *name, int which, int64_t seconds, int64_t nanoseconds, FileError *error) {
    struct timespec times[2] = { { 0, UTIME_OMIT }, { 0, UTIME_OMIT } };
    struct timespec *set = &times[which == FILE_TIME_ACCESSED ? 0 : 1];
    set->tv_sec = (time_t)seconds;
    set->tv_nsec = (long)nanoseconds;
    if (utimensat(AT_FDCWD, name, times, 0) != 0) {
        file_failed(error);
        return false;
    }

    return true;
}

static bool fs_make_directory(const char *name, int permissions, FileError *error) {
    if (mkdir(name, (mode_t)permissions) != 0) {
        file_failed(error);
        return false;
    }

    return true;
}

static bool fs_remove(const char *name, bool directory, FileError *error) {
    if ((directory ? rmdir(name) : unlink(name)) == 0) {
        return true;
    }

    file_failed(error);
    struct stat info;
    if (lstat(name, &info) == 0) {
        if (directory && error->code == ENOTDIR && !S_ISDIR(info.st_mode)) {
            error->kind = FILE_ERROR_NOT_DIRECTORY;
        } else if (!directory && S_ISDIR(info.st_mode)) {
            // POSIX has unlink say EPERM of a directory.
            error->kind = FILE_ERROR_IS_DIRECTORY;
        }
    }

    return false;
}

// ---- Removing a tree ---------------------------------------------------------
//
// What is removed is found by descriptors, never by a path looked up again:
// a directory is opened by its name in its parent's descriptor and only if
// it is a directory (O_NOFOLLOW), and its entries are unlinked in its own
// descriptor. Another program that swaps a directory for a link meanwhile
// has the link unlinked, and nothing beyond it touched. Few descriptors are
// held whatever the depth: the way back up is "..", opened and checked to
// be the directory come from, which a tree moved meanwhile does not pass.

#if defined(__wasi__)

// A WASI descriptor has no "..".
static bool fs_remove_tree(const char *name, FileError *error) {
    (void)name;
    error->kind = FILE_ERROR_UNSUPPORTED;
    error->code = ENOTSUP;
    return false;
}

#else

typedef struct TreeLevel {
    dev_t device;
    ino_t node;
    char *name; // in the level above; the first's is the tree's own
} TreeLevel;

typedef struct Tree {
    TreeLevel *levels;
    size_t depth, capacity;
    int retries; // left, for a directory written to as it is emptied
} Tree;

enum { TREE_DIRECTORY = O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK };

static void tree_push(Tree *tree, const struct stat *info, const char *name) {
    if (tree->depth == tree->capacity) {
        tree->capacity = tree->capacity == 0 ? 16 : tree->capacity * 2;
        tree->levels = realloc(tree->levels, tree->capacity * sizeof(TreeLevel));
        if (tree->levels == NULL) {
            sable_trap("out of memory");
        }
    }

    char *copy = strdup(name);
    if (copy == NULL) {
        sable_trap("out of memory");
    }

    tree->levels[tree->depth++] = (TreeLevel){ info->st_dev, info->st_ino, copy };
}

static void tree_pop(Tree *tree) {
    free(tree->levels[--tree->depth].name);
}

// Unlinks what the directory holds, up to the first directory in it, whose
// name is given in 'into' (the caller frees it); NULL when there is none.
static bool tree_scan(int fd, char **into, FileError *error) {
    *into = NULL;
    int stream = openat(fd, ".", TREE_DIRECTORY);
    DIR *entries = stream < 0 ? NULL : fdopendir(stream);
    if (entries == NULL) {
        file_failed(error);
        if (stream >= 0) {
            close(stream);
        }
        return false;
    }

    bool done = true;
    for (;;) {
        errno = 0;
        struct dirent *entry = readdir(entries);
        if (entry == NULL) {
            if (errno != 0) {
                file_failed(error);
                done = false;
            }
            break;
        }

        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }

        bool directory = entry->d_type == DT_DIR;
        struct stat info;
        if (entry->d_type == DT_UNKNOWN) {
            if (fstatat(fd, entry->d_name, &info, AT_SYMLINK_NOFOLLOW) != 0) {
                if (errno == ENOENT) {
                    continue;
                }
                file_failed(error);
                done = false;
                break;
            }
            directory = S_ISDIR(info.st_mode);
        }

        if (!directory) {
            if (unlinkat(fd, entry->d_name, 0) == 0 || errno == ENOENT) {
                continue;
            }

            // Another program may have put a directory in its place.
            int code = errno;
            if (fstatat(fd, entry->d_name, &info, AT_SYMLINK_NOFOLLOW) != 0 || !S_ISDIR(info.st_mode)) {
                errno = code;
                file_failed(error);
                done = false;
                break;
            }
        }

        *into = strdup(entry->d_name);
        if (*into == NULL) {
            sable_trap("out of memory");
        }
        break;
    }

    closedir(entries);
    return done;
}

// The tree's directory, open as 'fd' (which it closes), emptied.
static bool tree_empty(Tree *tree, int fd, FileError *error) {
    for (;;) {
        char *into;
        if (!tree_scan(fd, &into, error)) {
            break;
        }

        struct stat info;
        if (into != NULL) {
            int child = openat(fd, into, TREE_DIRECTORY);
            if (child < 0) {
                // Gone, or no longer a directory: looked at again.
                bool changed = (errno == ENOENT || errno == ENOTDIR || errno == ELOOP) && tree->retries-- > 0;
                if (!changed) {
                    file_failed(error);
                }
                free(into);
                if (changed) {
                    continue;
                }
                break;
            }

            if (fstat(child, &info) != 0) {
                file_failed(error);
                free(into);
                close(child);
                break;
            }

            tree_push(tree, &info, into);
            free(into);
            close(fd);
            fd = child;
            continue;
        }

        if (tree->depth == 1) {
            close(fd);
            return true;
        }

        // Empty: back up, if up is still where it was, and it is removed there.
        int parent = openat(fd, "..", TREE_DIRECTORY);
        if (parent < 0 || fstat(parent, &info) != 0) {
            file_failed(error);
            if (parent >= 0) {
                close(parent);
            }
            break;
        }

        close(fd);
        fd = parent;
        const TreeLevel *above = &tree->levels[tree->depth - 2];
        if (info.st_dev != above->device || info.st_ino != above->node) {
            // Moved to somewhere else while it was emptied.
            error->kind = FILE_ERROR_OTHER;
            error->code = ESTALE;
            break;
        }

        if (unlinkat(fd, tree->levels[tree->depth - 1].name, AT_REMOVEDIR) != 0 && errno != ENOENT) {
            // Written to as it was emptied, or something else put in its
            // place: looked at again, though not for ever.
            bool changed = (errno == ENOTEMPTY || errno == EEXIST || errno == ENOTDIR) && tree->retries-- > 0;
            if (!changed) {
                file_failed(error);
                break;
            }
        }

        tree_pop(tree);
    }

    close(fd);
    return false;
}

static bool fs_remove_tree(const char *name, FileError *error) {
    size_t length = strlen(name);
    while (length > 1 && name[length - 1] == '/') {
        length--;
    }

    size_t start = length;
    while (start > 0 && name[start - 1] != '/') {
        start--;
    }

    // Nothing, the root, '.' and '..' are refused: a name is wanted.
    const char *last = name + start;
    size_t last_length = length - start;
    if (last_length == 0 || (last_length == 1 && last[0] == '.') || (last_length == 2 && last[0] == '.' && last[1] == '.')) {
        error->kind = FILE_ERROR_INVALID;
        error->code = EINVAL;
        return false;
    }

    char *parent = start == 0 ? strdup(".") : strndup(name, start);
    char *base = strndup(last, last_length);
    if (parent == NULL || base == NULL) {
        sable_trap("out of memory");
    }

#if defined(O_PATH)
    int above = open(parent, O_PATH | O_DIRECTORY | O_CLOEXEC);
#elif defined(O_SEARCH)
    int above = open(parent, O_SEARCH | O_DIRECTORY | O_CLOEXEC);
#else
    int above = open(parent, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
#endif
    free(parent);
    if (above < 0) {
        file_failed(error);
        free(base);
        return false;
    }

    bool removed = false;
    struct stat info, root, now;
    int fd = openat(above, base, TREE_DIRECTORY);
    if (fd < 0 || fstat(fd, &info) != 0) {
        file_failed(error);
        if (fd < 0 && fstatat(above, base, &now, AT_SYMLINK_NOFOLLOW) == 0 && !S_ISDIR(now.st_mode)) {
            error->kind = FILE_ERROR_NOT_DIRECTORY;
        }
        if (fd >= 0) {
            close(fd);
        }
    } else if (stat("/", &root) == 0 && root.st_dev == info.st_dev && root.st_ino == info.st_ino) {
        // The root under another name.
        close(fd);
        error->kind = FILE_ERROR_INVALID;
        error->code = EINVAL;
    } else {
        Tree tree = { .retries = 256 };
        tree_push(&tree, &info, base);
        for (;;) {
            if (!tree_empty(&tree, fd, error)) {
                break;
            }

            if (unlinkat(above, base, AT_REMOVEDIR) == 0 || errno == ENOENT) {
                removed = true;
                break;
            }

            // Written to since it was emptied: emptied again, if it is
            // still the same directory.
            bool again = (errno == ENOTEMPTY || errno == EEXIST) && tree.retries-- > 0;
            if (!again) {
                file_failed(error);
                break;
            }

            fd = openat(above, base, TREE_DIRECTORY);
            if (fd < 0 || fstat(fd, &now) != 0 || now.st_dev != info.st_dev || now.st_ino != info.st_ino) {
                errno = ESTALE;
                file_failed(error);
                if (fd >= 0) {
                    close(fd);
                }
                break;
            }
        }

        while (tree.depth > 0) {
            tree_pop(&tree);
        }
        free(tree.levels);
    }

    close(above);
    free(base);
    return removed;
}

#endif

// ---- Renaming and links ------------------------------------------------------

static bool fs_rename(const char *from, const char *to, bool replace, FileError *error) {
    int result;
    if (replace) {
        result = rename(from, to);
    } else {
#if defined(__APPLE__)
        result = renamex_np(from, to, RENAME_EXCL);
#elif defined(__linux__) && defined(SYS_renameat2)
        result = (int)syscall(SYS_renameat2, AT_FDCWD, from, AT_FDCWD, to, 1u /* RENAME_NOREPLACE */);
#else
        result = -1;
        errno = ENOSYS;
#endif
        if (result != 0 && (errno == ENOSYS || errno == EINVAL || errno == ENOTSUP)) {
            // A system or file system that has no such call: a link, which
            // replaces nothing, and the old name then taken away. A
            // directory, which has no second name, is not renamed so.
            result = link(from, to);
            if (result == 0 && unlink(from) != 0) {
                int code = errno;
                unlink(to);
                errno = code;
                result = -1;
            }
        }
    }

    if (result != 0) {
        file_failed(error);
        return false;
    }

    return true;
}

static bool fs_link(const char *target, const char *name, int kind, FileError *error) {
    // A hard link to a symbolic one is to the link itself, on every system.
    int result = kind == FILE_LINK_HARD ? linkat(AT_FDCWD, target, AT_FDCWD, name, 0) : symlink(target, name);
    if (result != 0) {
        file_failed(error);
        return false;
    }

    return true;
}

static char *fs_read_link(const char *name, FileError *error) {
    for (size_t capacity = 256;; capacity *= 2) {
        char *text = malloc(capacity);
        if (text == NULL) {
            sable_trap("out of memory");
        }

        ssize_t length = readlink(name, text, capacity);
        if (length < 0) {
            file_failed(error);
            free(text);
            return NULL;
        }

        if ((size_t)length < capacity) {
            text[length] = '\0';
            return text;
        }

        free(text);
    }
}

// ---- Copying -----------------------------------------------------------------

#if !defined(__APPLE__)

// What is left of a copy the system's own call did not do: read and
// written a megabyte at a time.
static bool copy_by_reading(int from, int to, FileError *error) {
    enum { CHUNK = 1 << 20 };
    char *buffer = malloc(CHUNK);
    if (buffer == NULL) {
        error->kind = FILE_ERROR_OTHER;
        error->code = ENOMEM;
        return false;
    }

    bool copied = true;
    for (;;) {
        int64_t got = file_read(from, buffer, CHUNK, -1, error);
        if (got <= 0) {
            copied = got == 0;
            break;
        }

        for (int64_t done = 0; done < got;) {
            int64_t put = file_write(to, buffer + done, got - done, -1, error);
            if (put <= 0) {
                copied = false;
                break;
            }

            done += put;
        }

        if (!copied) {
            break;
        }
    }

    free(buffer);
    return copied;
}

#endif

// The bytes of one open file written to another, at the system's fastest.
static bool copy_bytes(int source, int target, FileError *error) {
#if defined(__APPLE__)
    if (fcopyfile(source, target, NULL, COPYFILE_DATA) == 0) {
        return true;
    }

    file_failed(error);
    return false;
#elif defined(__linux__)
    for (bool any = false;;) {
        ssize_t moved = copy_file_range(source, NULL, target, NULL, (size_t)1 << 30, 0);
        if (moved > 0) {
            any = true;
            continue;
        }

        // A file of /proc says it has no length, and gives nothing here
        // that reading does not.
        if (moved == 0) {
            return any || copy_by_reading(source, target, error);
        }

        if (errno == EINTR) {
            continue;
        }

        // Across file systems, or a kernel without it: the bytes read and
        // written from where it got to.
        if (errno == EXDEV || errno == ENOSYS || errno == EOPNOTSUPP || errno == EINVAL || errno == EPERM) {
            return copy_by_reading(source, target, error);
        }

        file_failed(error);
        return false;
    }
#else
    return copy_by_reading(source, target, error);
#endif
}

// A clone where the file system makes one (APFS here; copy_file_range does
// on Linux), else the bytes. A file there is written over where it is, and
// gets the permissions: whatever else has it open, or links to it, sees it.
static bool file_copy(const char *from, const char *to, bool replace, FileError *error) {
    // Not waiting for a FIFO's writer: it is no file to copy.
    int source = open(from, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
    struct stat info, there;
    if (source < 0 || fstat(source, &info) != 0) {
        file_failed(error);
        if (source >= 0) {
            close(source);
        }
        return false;
    }

    if (!S_ISREG(info.st_mode)) {
        close(source);
        error->kind = S_ISDIR(info.st_mode) ? FILE_ERROR_IS_DIRECTORY : FILE_ERROR_INVALID;
        error->code = S_ISDIR(info.st_mode) ? EISDIR : EINVAL;
        return false;
    }

    // Emptied to be written, it would be lost: looked at before it is opened,
    // which a read-only file could not be, and again after.
    if (replace && stat(to, &there) == 0 && there.st_dev == info.st_dev && there.st_ino == info.st_ino) {
        close(source);
        error->kind = FILE_ERROR_INVALID;
        error->code = EINVAL;
        return false;
    }

#if defined(__APPLE__)
    if (fclonefileat(source, AT_FDCWD, to, 0) == 0) {
        close(source);
        return true;
    }
#endif

    mode_t mode = info.st_mode & 0777;
    // Writable while it is written, whatever it is to be.
    int target = open(to, O_WRONLY | O_CREAT | O_CLOEXEC | (replace ? 0 : O_EXCL), mode | 0200);
    if (target < 0 || fstat(target, &there) != 0) {
        file_failed(error);
        if (target >= 0) {
            close(target);
        }
        close(source);
        return false;
    }

    bool copied = false;
    if (there.st_dev == info.st_dev && there.st_ino == info.st_ino) {
        error->kind = FILE_ERROR_INVALID;
        error->code = EINVAL;
    } else if (S_ISREG(there.st_mode) && ftruncate(target, 0) != 0) {
        file_failed(error);
    } else {
        copied = copy_bytes(source, target, error);
#if !defined(__wasi__)
        if (copied && S_ISREG(there.st_mode) && fchmod(target, mode) != 0) {
            file_failed(error);
            copied = false;
        }
#endif
    }

    close(source);
    if (close(target) != 0 && copied) {
        file_failed(error);
        copied = false;
    }

    // Nothing was there before: no part of a copy is left.
    if (!copied && !replace) {
        unlink(to);
    }

    return copied;
}

// ---- Directories, an entry at a time --------------------------------------------

static void *directory_open(const char *name, FileError *error) {
    DIR *entries = opendir(name);
    if (entries == NULL) {
        file_failed(error);
        struct stat info;
        if (error->code == ENOTDIR && stat(name, &info) == 0) {
            error->kind = FILE_ERROR_NOT_DIRECTORY;
        }
    }

    return entries;
}

static char *directory_next(void *directory, int *kind, FileError *error) {
    for (;;) {
        errno = 0;
        struct dirent *entry = readdir(directory);
        if (entry == NULL) {
            if (errno != 0) {
                file_failed(error);
            }
            return NULL;
        }

        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }

        switch (entry->d_type) {
            case DT_REG: *kind = FILE_KIND_FILE; break;
            case DT_DIR: *kind = FILE_KIND_DIRECTORY; break;
            case DT_LNK: *kind = FILE_KIND_LINK; break;
            case DT_CHR: *kind = FILE_KIND_CHARACTER; break;
            case DT_BLK: *kind = FILE_KIND_BLOCK; break;
#if !defined(__wasi__)
            case DT_FIFO: *kind = FILE_KIND_FIFO; break;
            case DT_SOCK: *kind = FILE_KIND_SOCKET; break;
#endif
            default: *kind = -1; break;
        }

        char *name = strdup(entry->d_name);
        if (name == NULL) {
            sable_trap("out of memory");
        }
        return name;
    }
}

static void directory_close(void *directory) {
    closedir(directory);
}
