// The Sable runtime: file handles on POSIX and WASI — descriptors, with
// what platform.h's "File handles" says each platform gives. Part of
// platform_posix.c and platform_wasi.c, which include it.

#include <fcntl.h>

#if defined(__APPLE__)
#include <copyfile.h>
#include <sys/clonefile.h>
#include <sys/sysctl.h>
#endif
#if !defined(__wasi__)
#include <pwd.h>
#include <sys/utsname.h>
#endif

extern char **environ;

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
        case ENAMETOOLONG:
            return FILE_ERROR_INVALID;
        case ENOTEMPTY:
            return FILE_ERROR_NOT_EMPTY;
        case EXDEV:
            return FILE_ERROR_CROSSES;
        case ENOSYS:
        case ENOTSUP:
            return FILE_ERROR_UNSUPPORTED;
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

static bool file_close(PlatformFile file, FileError *error) {
    if (close((int)file) != 0 && errno != EINTR) {
        file_failed(error);
        return false;
    }

    return true;
}

static bool descriptor_close(int descriptor, FileError *error) {
    return file_close(descriptor, error);
}

// Not for a program run later.
static int descriptor_duplicate(int descriptor, FileError *error) {
#ifdef __wasi__
    (void)descriptor;
    errno = ENOTSUP;
    int made = -1;
#else
    int made = fcntl(descriptor, F_DUPFD_CLOEXEC, 0);
#endif
    if (made < 0) {
        file_failed(error);
    }

    return made;
}

static PlatformFile file_standard(int which) {
    return which;
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

// ---- The environment, written ------------------------------------------------

static bool set_environment_variable(const char *name, const char *value) {
    return (value == NULL ? unsetenv(name) : setenv(name, value, 1)) == 0;
}

static char **environment_entries(size_t *count) {
    size_t n = 0;
    while (environ != NULL && environ[n] != NULL) {
        n++;
    }

    char **entries = malloc((n > 0 ? n : 1) * sizeof(char *));
    if (entries == NULL) {
        sable_trap("out of memory");
    }

    for (size_t i = 0; i < n; i++) {
        entries[i] = strdup(environ[i]);
    }

    *count = n;
    return entries;
}

// ---- The system ----------------------------------------------------------

static char *host_name(void) {
#if defined(__wasi__)
    return NULL;
#else
    char name[256];
    if (gethostname(name, sizeof name) != 0) {
        return NULL;
    }

    name[sizeof name - 1] = '\0';
    return strdup(name);
#endif
}

static char *user_name(void) {
#if !defined(__wasi__)
    struct passwd *entry = getpwuid(geteuid());
    if (entry != NULL && entry->pw_name != NULL) {
        return strdup(entry->pw_name);
    }
#endif
    return environment_variable("USER");
}

static char *home_directory(void) {
    char *home = environment_variable("HOME");
#if !defined(__wasi__)
    if (home == NULL || home[0] == '\0') {
        free(home);
        struct passwd *entry = getpwuid(geteuid());
        home = entry != NULL && entry->pw_dir != NULL ? strdup(entry->pw_dir) : NULL;
    }
#endif
    return home;
}

static char *temporary_directory(void) {
    char *directory = environment_variable("TMPDIR");
    if (directory == NULL || directory[0] == '\0') {
        free(directory);
        return strdup("/tmp");
    }

    // macOS's ends in '/': the directory, without it.
    size_t length = strlen(directory);
    if (length > 1 && directory[length - 1] == '/') {
        directory[length - 1] = '\0';
    }

    return directory;
}

static char *os_version(void) {
#if defined(__APPLE__)
    char version[64];
    size_t size = sizeof version;
    if (sysctlbyname("kern.osproductversion", version, &size, NULL, 0) == 0) {
        return strdup(version);
    }
    return NULL;
#elif defined(__wasi__)
    return NULL;
#else
    struct utsname name;
    return uname(&name) == 0 ? strdup(name.release) : NULL;
#endif
}

// CoreFoundation is found at run time, so that a program that never asks is
// not linked with it.
static char *user_locale(void) {
    char *found = NULL;
#if defined(__APPLE__)
    void *cf = dlopen("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", RTLD_LAZY | RTLD_LOCAL);
    if (cf == NULL) {
        return NULL;
    }
    const void *(*copy_current)(void) = (const void *(*)(void))dlsym(cf, "CFLocaleCopyCurrent");
    const void *(*identifier)(const void *) = (const void *(*)(const void *))dlsym(cf, "CFLocaleGetIdentifier");
    unsigned char (*get_text)(const void *, char *, long, uint32_t) =
        (unsigned char (*)(const void *, char *, long, uint32_t))dlsym(cf, "CFStringGetCString");
    void (*release)(const void *) = (void (*)(const void *))dlsym(cf, "CFRelease");
    if (copy_current != NULL && identifier != NULL && get_text != NULL && release != NULL) {
        const void *locale = copy_current();
        if (locale != NULL) {
            char text[256];
            if (get_text(identifier(locale), text, sizeof text, 0x08000100)) { // UTF-8
                found = strdup(text);
            }
            release(locale);
        }
    }
    dlclose(cf);
#endif
    return found;
}

static int64_t page_size(void) {
#if defined(__wasi__)
    return 65536;
#else
    long size = sysconf(_SC_PAGESIZE);
    return size > 0 ? size : 4096;
#endif
}

static int64_t physical_memory(void) {
#if defined(__APPLE__)
    uint64_t bytes = 0;
    size_t size = sizeof bytes;
    return sysctlbyname("hw.memsize", &bytes, &size, NULL, 0) == 0 ? (int64_t)bytes : 0;
#elif defined(__wasi__)
    return 0;
#else
    long pages = sysconf(_SC_PHYS_PAGES);
    long size = sysconf(_SC_PAGESIZE);
    return pages > 0 && size > 0 ? (int64_t)pages * size : 0;
#endif
}
