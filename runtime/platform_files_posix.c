// The Sable runtime: file handles on POSIX and WASI — descriptors, with
// what platform.h's "File handles" says each platform gives. Part of
// platform_posix.c and platform_wasi.c, which include it.

#include <fcntl.h>

#if defined(__APPLE__)
#include <copyfile.h>
#endif

// What errno says went wrong, as IoError's kinds.
static int file_error_of(int code) {
    switch (code) {
        case ENOENT:
        case ENOTDIR:
            return FILE_ERROR_NOT_FOUND;
        case EACCES:
        case EPERM:
            return FILE_ERROR_PERMISSION;
        case EEXIST:
            return FILE_ERROR_EXISTS;
        case EISDIR:
            return FILE_ERROR_IS_DIRECTORY;
        case EINTR:
            return FILE_ERROR_INTERRUPTED;
        case EINVAL:
        case EBADF:
            return FILE_ERROR_INVALID;
        default:
            return FILE_ERROR_OTHER;
    }
}

static void file_failed(FileError *error) {
    error->kind = file_error_of(errno);
    error->code = errno;
}

static PlatformFile file_open(const char *name, int flags, int permissions, FileError *error) {
    bool writes = (flags & (FILE_WRITE | FILE_APPEND)) != 0;
    int mode = (flags & FILE_READ) && writes ? O_RDWR : writes ? O_WRONLY : O_RDONLY;
    if (flags & FILE_APPEND) {
        mode |= O_APPEND;
    }
    if (flags & FILE_CREATE_NEW) {
        mode |= O_CREAT | O_EXCL;
    } else if (flags & FILE_CREATE) {
        mode |= O_CREAT;
    }
    if (flags & FILE_TRUNCATE) {
        mode |= O_TRUNC;
    }
#ifdef O_CLOEXEC
    mode |= O_CLOEXEC;
#endif

    int fd;
    do {
        fd = open(name, mode, (mode_t)permissions);
    } while (fd < 0 && errno == EINTR);
    if (fd < 0) {
        file_failed(error);
        return -1;
    }

    // POSIX opens a directory for reading: it is not a file to read.
    struct stat info;
    if (fstat(fd, &info) == 0 && S_ISDIR(info.st_mode)) {
        close(fd);
        error->kind = FILE_ERROR_IS_DIRECTORY;
        error->code = EISDIR;
        return -1;
    }

    return fd;
}

static int64_t file_read(PlatformFile file, void *into, int64_t most, int64_t offset, FileError *error) {
    ssize_t got;
    do {
        got = offset < 0 ? read((int)file, into, (size_t)most) : pread((int)file, into, (size_t)most, (off_t)offset);
    } while (got < 0 && errno == EINTR);
    if (got < 0) {
        file_failed(error);
    }

    return got;
}

static int64_t file_write(PlatformFile file, const void *from, int64_t count, int64_t offset, FileError *error) {
    ssize_t put;
    do {
        put = offset < 0 ? write((int)file, from, (size_t)count) : pwrite((int)file, from, (size_t)count, (off_t)offset);
    } while (put < 0 && errno == EINTR);
    if (put < 0) {
        file_failed(error);
    }

    return put;
}

static int64_t file_seek(PlatformFile file, int64_t offset, int whence, FileError *error) {
    off_t at = lseek((int)file, (off_t)offset, whence == 0 ? SEEK_SET : whence == 1 ? SEEK_CUR : SEEK_END);
    if (at < 0) {
        file_failed(error);
    }

    return at;
}

static bool file_sync(PlatformFile file, FileError *error) {
    if (fsync((int)file) != 0) {
        file_failed(error);
        return false;
    }

    return true;
}

static bool file_set_length(PlatformFile file, int64_t length, FileError *error) {
    if (ftruncate((int)file, (off_t)length) != 0) {
        file_failed(error);
        return false;
    }

    return true;
}

static bool file_metadata(PlatformFile file, FileMetadata *metadata, FileError *error) {
    struct stat info;
    if (fstat((int)file, &info) != 0) {
        file_failed(error);
        return false;
    }

    metadata->size = info.st_size;
#if defined(__APPLE__)
    metadata->modified = (int64_t)info.st_mtimespec.tv_sec * 1000000000 + info.st_mtimespec.tv_nsec;
#else
    metadata->modified = (int64_t)info.st_mtim.tv_sec * 1000000000 + info.st_mtim.tv_nsec;
#endif
    metadata->kind = S_ISDIR(info.st_mode) ? 1 : S_ISREG(info.st_mode) ? 0 : 2;
    return true;
}

static bool file_close(PlatformFile file, FileError *error) {
    if (close((int)file) != 0 && errno != EINTR) {
        file_failed(error);
        return false;
    }

    return true;
}

static PlatformFile file_standard(int which) {
    return which;
}

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

// The whole file copied to a new one, or one there replaced, at the
// system's fastest: a clone where the file system can (APFS), the kernel's
// copy_file_range on Linux, and bytes read and written otherwise.
static bool file_copy(const char *from, const char *to, FileError *error) {
#if defined(__APPLE__)
    // A clone, where APFS can make one, of everything; a file at 'to'
    // removed first, as a clone is only made where nothing is.
    if (copyfile(from, to, NULL, COPYFILE_ALL | COPYFILE_CLONE | COPYFILE_UNLINK) != 0) {
        file_failed(error);
        return false;
    }

    return true;
#else
    int source = (int)file_open(from, FILE_READ, 0, error);
    if (source < 0) {
        return false;
    }

    struct stat info;
    mode_t mode = fstat(source, &info) == 0 ? info.st_mode & 0777 : 0644;
    int target = (int)file_open(to, FILE_WRITE | FILE_CREATE | FILE_TRUNCATE, (int)mode, error);
    if (target < 0) {
        close(source);
        return false;
    }

    bool copied = false;
#if defined(__linux__)
    for (;;) {
        ssize_t moved = copy_file_range(source, NULL, target, NULL, (size_t)1 << 30, 0);
        if (moved == 0) {
            copied = true;
            break;
        }
        if (moved < 0) {
            if (errno == EINTR) {
                continue;
            }
            // Across file systems, or a kernel without it: the bytes read
            // and written from where it got to.
            if (errno == EXDEV || errno == ENOSYS || errno == EOPNOTSUPP || errno == EINVAL) {
                copied = copy_by_reading(source, target, error);
            } else {
                file_failed(error);
            }
            break;
        }
    }
#else
    copied = copy_by_reading(source, target, error);
#endif

    close(source);
    if (close(target) != 0 && copied) {
        file_failed(error);
        copied = false;
    }

    return copied;
#endif
}

static char *file_canonical(const char *name, FileError *error) {
#if defined(__wasi__)
    // WASI has no links to resolve past what the host hands it: the path
    // as given, if something is there.
    struct stat status;
    if (stat(name, &status) != 0) {
        file_failed(error);
        return NULL;
    }
    size_t length = strlen(name);
    char *copy = malloc(length + 1);
    if (copy == NULL) {
        error->kind = FILE_ERROR_OTHER;
        error->code = ENOMEM;
        return NULL;
    }
    memcpy(copy, name, length + 1);
    return copy;
#else
    char *resolved = realpath(name, NULL);
    if (resolved == NULL) {
        file_failed(error);
    }
    return resolved;
#endif
}

// ---- Mapped files ----------------------------------------------------------

#if defined(__wasi__)

// WASI has no mmap: mapping fails, and MappedFile says so.
static void *file_map(PlatformFile file, int64_t length, int mode, FileError *error) {
    (void)file;
    (void)length;
    (void)mode;
    error->kind = FILE_ERROR_OTHER;
    error->code = ENOTSUP;
    return NULL;
}

static void file_unmap(void *base, int64_t length) {
    (void)base;
    (void)length;
}

static bool file_map_flush(void *base, int64_t at, int64_t length, FileError *error) {
    (void)base;
    (void)at;
    (void)length;
    (void)error;
    return true;
}

static void file_map_advise(void *base, int64_t length, int advice) {
    (void)base;
    (void)length;
    (void)advice;
}

#else

static void *file_map(PlatformFile file, int64_t length, int mode, FileError *error) {
    int protection = mode == FILE_MAP_READ_ONLY ? PROT_READ : PROT_READ | PROT_WRITE;
    int sharing = mode == FILE_MAP_READ_WRITE ? MAP_SHARED : MAP_PRIVATE;
    void *mapped = mmap(NULL, (size_t)length, protection, sharing, (int)file, 0);
    if (mapped == MAP_FAILED) {
        file_failed(error);
        return NULL;
    }

    return mapped;
}

static void file_unmap(void *base, int64_t length) {
    munmap(base, (size_t)length);
}

static bool file_map_flush(void *base, int64_t at, int64_t length, FileError *error) {
    // msync starts at a page: the one 'at' is in.
    uintptr_t page = (uintptr_t)sysconf(_SC_PAGESIZE);
    uintptr_t from = ((uintptr_t)base + (uintptr_t)at) & ~(page - 1);
    uintptr_t to = (uintptr_t)base + (uintptr_t)(at + length);
    if (to > from && msync((void *)from, to - from, MS_SYNC) != 0) {
        file_failed(error);
        return false;
    }

    return true;
}

static void file_map_advise(void *base, int64_t length, int advice) {
    int how = advice == FILE_ADVISE_SEQUENTIAL ? MADV_SEQUENTIAL
        : advice == FILE_ADVISE_RANDOM         ? MADV_RANDOM
        : advice == FILE_ADVISE_WILL_NEED      ? MADV_WILLNEED
        : advice == FILE_ADVISE_DONT_NEED      ? MADV_DONTNEED
                                               : MADV_NORMAL;
    // Advice only: a system that will not take it changes nothing.
    (void)madvise(base, (size_t)length, how);
}

#endif
