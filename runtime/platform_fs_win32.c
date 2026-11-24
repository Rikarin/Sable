// The Sable runtime: the file system by path on Windows, and files watched
// as they change — what platform.h's "The file system" and "Watching" say
// each platform gives. Part of platform_win32.c, which includes it after
// its file handles.

#include <winioctl.h>

// The epoch of a FILETIME, 1601, from 1970's, in its hundreds of nanoseconds.
#define FILETIME_EPOCH 116444736000000000LL

static void time_of(FILETIME time, int64_t parts[2]) {
    int64_t ticks = (int64_t)(((uint64_t)time.dwHighDateTime << 32) | time.dwLowDateTime) - FILETIME_EPOCH;
    int64_t seconds = ticks / 10000000;
    int64_t left = ticks % 10000000;
    if (left < 0) {
        seconds--;
        left += 10000000;
    }

    parts[0] = seconds;
    parts[1] = left * 100;
}

// Whether the reparse point with the tag stands for something elsewhere: a
// symbolic link or a junction, and not a file the system keeps another way
// (a cloud's, a compressed one), which is the file it says it is.
static bool is_link_tag(DWORD tag) {
    return IsReparseTagNameSurrogate(tag);
}

static bool is_link(HANDLE handle, DWORD attributes) {
    FILE_ATTRIBUTE_TAG_INFO tag;
    return (attributes & FILE_ATTRIBUTE_REPARSE_POINT)
           && GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &tag, sizeof tag) && is_link_tag(tag.ReparseTag);
}

static bool handle_metadata(HANDLE handle, FileMetadata *metadata, FileError *error) {
    BY_HANDLE_FILE_INFORMATION info;
    if (!GetFileInformationByHandle(handle, &info)) {
        file_failed(error);
        return false;
    }

    bool directory = (info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    metadata->kind = is_link(handle, info.dwFileAttributes) ? FILE_KIND_LINK : directory ? FILE_KIND_DIRECTORY : FILE_KIND_FILE;
    metadata->size = directory ? 0 : (int64_t)(((uint64_t)info.nFileSizeHigh << 32) | info.nFileSizeLow);
    metadata->permissions = (info.dwFileAttributes & FILE_ATTRIBUTE_READONLY ? 0444 : 0666) | (directory ? 0111 : 0);
    time_of(info.ftLastWriteTime, metadata->modified);
    time_of(info.ftLastAccessTime, metadata->accessed);
    time_of(info.ftCreationTime, metadata->created);
    metadata->has_created = 1;
    return true;
}

static bool file_metadata(PlatformFile file, FileMetadata *metadata, FileError *error) {
    return handle_metadata(handle_of(file), metadata, error);
}

// Opened for what is asked, a directory too (backup semantics), and, when
// not followed, the link itself.
static HANDLE fs_open(const wchar_t *name, DWORD access, DWORD share, bool follow) {
    return CreateFileW(name, access, share, NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | (follow ? 0 : FILE_FLAG_OPEN_REPARSE_POINT), NULL);
}

enum { SHARE_ALL = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE };

static bool fs_metadata(const char *name, bool follow, FileMetadata *metadata, FileError *error) {
    wchar_t *wide = to_wide(name);
    HANDLE handle = fs_open(wide, FILE_READ_ATTRIBUTES, SHARE_ALL, follow);
    free(wide);
    if (handle == INVALID_HANDLE_VALUE) {
        file_failed(error);
        return false;
    }

    bool found = handle_metadata(handle, metadata, error);
    CloseHandle(handle);
    return found;
}

static bool fs_set_permissions(const char *name, int permissions, FileError *error) {
    wchar_t *wide = to_wide(name);
    HANDLE handle = fs_open(wide, FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, SHARE_ALL, true);
    free(wide);
    if (handle == INVALID_HANDLE_VALUE) {
        file_failed(error);
        return false;
    }

    FILE_BASIC_INFO info;
    bool set = GetFileInformationByHandleEx(handle, FileBasicInfo, &info, sizeof info) != FALSE;
    if (set) {
        DWORD attributes = info.FileAttributes & ~(DWORD)FILE_ATTRIBUTE_READONLY;
        // A directory's says nothing of what may be written in it.
        if (!(permissions & 0222) && !(attributes & FILE_ATTRIBUTE_DIRECTORY)) {
            attributes |= FILE_ATTRIBUTE_READONLY;
        }

        // Times of zero are left as they are.
        FILE_BASIC_INFO changed = { 0 };
        changed.FileAttributes = attributes == 0 ? FILE_ATTRIBUTE_NORMAL : attributes;
        set = SetFileInformationByHandle(handle, FileBasicInfo, &changed, sizeof changed) != FALSE;
    }

    if (!set) {
        file_failed(error);
    }

    CloseHandle(handle);
    return set;
}

static bool fs_set_time(const char *name, int which, int64_t seconds, int64_t nanoseconds, FileError *error) {
    if (seconds < -11644473600LL || seconds > 900000000000LL) {
        error->kind = FILE_ERROR_INVALID;
        error->code = ERROR_INVALID_PARAMETER;
        return false;
    }

    wchar_t *wide = to_wide(name);
    HANDLE handle = fs_open(wide, FILE_WRITE_ATTRIBUTES, SHARE_ALL, true);
    free(wide);
    if (handle == INVALID_HANDLE_VALUE) {
        file_failed(error);
        return false;
    }

    uint64_t ticks = (uint64_t)(seconds * 10000000 + nanoseconds / 100 + FILETIME_EPOCH);
    FILETIME time = { (DWORD)(ticks & 0xFFFFFFFF), (DWORD)(ticks >> 32) };
    bool set = SetFileTime(handle, NULL, which == FILE_TIME_ACCESSED ? &time : NULL, which == FILE_TIME_MODIFIED ? &time : NULL) != FALSE;
    if (!set) {
        file_failed(error);
    }

    CloseHandle(handle);
    return set;
}

static bool fs_make_directory(const char *name, int permissions, FileError *error) {
    (void)permissions;
    wchar_t *wide = to_wide(name);
    bool made = CreateDirectoryW(wide, NULL) != FALSE;
    if (!made) {
        file_failed(error);
    }

    free(wide);
    return made;
}

// ---- Removing ----------------------------------------------------------------
//
// What is removed is opened, looked at and deleted by its handle, so that
// what is deleted is what was looked at: a link is deleted itself, and only
// a directory that is no reparse point is gone into. A directory is held
// open while what is in it is removed, and not shared to be deleted, so
// that neither it nor any directory above it can be renamed or swapped for
// a link meanwhile, and the paths made of its name lead into it.

// FileDispositionInfoEx and its flags, which older headers have not.
enum { DISPOSITION_EX = 21, DISPOSITION_FLAGS = 0x1 /* delete */ | 0x2 /* as POSIX does */ | 0x10 /* read-only too */ };

// The name gone at once where the system can, else as the last handle closes.
static bool delete_by_handle(HANDLE handle) {
    DWORD flags = DISPOSITION_FLAGS;
    if (SetFileInformationByHandle(handle, (FILE_INFO_BY_HANDLE_CLASS)DISPOSITION_EX, &flags, sizeof flags)) {
        return true;
    }

    DWORD code = GetLastError();
    if (code != ERROR_INVALID_PARAMETER && code != ERROR_NOT_SUPPORTED && code != ERROR_INVALID_FUNCTION) {
        SetLastError(code);
        return false;
    }

    FILE_DISPOSITION_INFO info = { TRUE };
    return SetFileInformationByHandle(handle, FileDispositionInfo, &info, sizeof info) != FALSE;
}

static bool is_missing(DWORD code) {
    // ERROR_DELETE_PENDING: deleted already, by a handle still to be closed.
    return code == ERROR_FILE_NOT_FOUND || code == ERROR_PATH_NOT_FOUND || code == ERROR_DELETE_PENDING;
}

static bool fs_remove(const char *name, bool directory, FileError *error) {
    wchar_t *wide = to_wide(name);
    HANDLE handle = fs_open(wide, DELETE | FILE_READ_ATTRIBUTES, SHARE_ALL, false);
    free(wide);
    FILE_ATTRIBUTE_TAG_INFO info;
    if (handle == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &info, sizeof info)) {
        file_failed(error);
        if (handle != INVALID_HANDLE_VALUE) {
            CloseHandle(handle);
        }
        return false;
    }

    bool is_directory = (info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                        && !((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) && is_link_tag(info.ReparseTag));
    bool removed = false;
    if (is_directory != directory) {
        error->kind = directory ? FILE_ERROR_NOT_DIRECTORY : FILE_ERROR_IS_DIRECTORY;
        error->code = ERROR_DIRECTORY;
    } else {
        removed = delete_by_handle(handle);
        if (!removed) {
            file_failed(error);
        }
    }

    CloseHandle(handle);
    return removed;
}

static wchar_t *path_inside(const wchar_t *directory, const wchar_t *name, size_t units) {
    size_t length = wcslen(directory);
    wchar_t *path = malloc((length + units + 2) * sizeof(wchar_t));
    if (path == NULL) {
        sable_trap("out of memory");
    }

    memcpy(path, directory, length * sizeof(wchar_t));
    if (length > 0 && directory[length - 1] != L'\\') {
        path[length++] = L'\\';
    }

    memcpy(path + length, name, units * sizeof(wchar_t));
    path[length + units] = L'\0';
    return path;
}

// What the path names removed, and, a directory, all in it. The path is a
// verbatim one (\\?\), taken as it is written.
static bool tree_remove(const wchar_t *path, bool first, int *retries, FileError *error) {
    HANDLE handle = fs_open(path, DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE, false);
    if (handle == INVALID_HANDLE_VALUE) {
        if (!first && is_missing(GetLastError())) {
            return true;
        }

        // A file others have open and share to be deleted, or that may be
        // deleted but not read.
        DWORD code = GetLastError();
        handle = fs_open(path, DELETE | FILE_READ_ATTRIBUTES, SHARE_ALL, false);
        FILE_ATTRIBUTE_TAG_INFO found;
        if (handle == INVALID_HANDLE_VALUE || !GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &found, sizeof found)
            || ((found.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) && !(found.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))) {
            bool missing = !first && handle == INVALID_HANDLE_VALUE && is_missing(GetLastError());
            if (handle != INVALID_HANDLE_VALUE) {
                CloseHandle(handle);
            }

            if (!missing) {
                SetLastError(code);
                file_failed(error);
            }
            return missing;
        }
    }

    FILE_ATTRIBUTE_TAG_INFO info;
    if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &info, sizeof info)) {
        file_failed(error);
        CloseHandle(handle);
        return false;
    }

    // Any reparse point is deleted itself, and never gone into.
    bool directory = (info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) && !(info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT);
    if (first && !directory) {
        CloseHandle(handle);
        error->kind = FILE_ERROR_NOT_DIRECTORY;
        error->code = ERROR_DIRECTORY;
        return false;
    }

    enum { ENTRIES = 65536 };
    char *entries = directory ? malloc(ENTRIES) : NULL;
    if (directory && entries == NULL) {
        sable_trap("out of memory");
    }

    bool removed = true;
    for (;;) {
        bool restart = directory;
        while (directory && removed) {
            if (!GetFileInformationByHandleEx(handle, restart ? FileFullDirectoryRestartInfo : FileFullDirectoryInfo, entries, ENTRIES)) {
                DWORD code = GetLastError();
                if (code != ERROR_NO_MORE_FILES && code != ERROR_FILE_NOT_FOUND) {
                    file_failed(error);
                    removed = false;
                }
                break;
            }

            restart = false;
            for (char *at = entries; removed;) {
                FILE_FULL_DIR_INFO *entry = (FILE_FULL_DIR_INFO *)at;
                size_t units = entry->FileNameLength / sizeof(wchar_t);
                bool dots = (units == 1 && entry->FileName[0] == L'.') || (units == 2 && entry->FileName[0] == L'.' && entry->FileName[1] == L'.');
                if (!dots) {
                    wchar_t *inside = path_inside(path, entry->FileName, units);
                    removed = tree_remove(inside, false, retries, error);
                    free(inside);
                }

                if (entry->NextEntryOffset == 0) {
                    break;
                }
                at += entry->NextEntryOffset;
            }
        }

        if (!removed || delete_by_handle(handle)) {
            break;
        }

        // Written to as it was emptied: emptied again, though not for ever.
        if (!directory || GetLastError() != ERROR_DIR_NOT_EMPTY || (*retries)-- <= 0) {
            file_failed(error);
            removed = false;
            break;
        }
    }

    free(entries);
    CloseHandle(handle);
    return removed;
}

static bool fs_remove_tree(const char *name, FileError *error) {
    wchar_t *wide = to_wide(name);
    size_t length = wcslen(wide);
    bool verbatim = length >= 4 && wcsncmp(wide, L"\\\\?\\", 4) == 0;
    while (length > 0 && (wide[length - 1] == L'\\' || (wide[length - 1] == L'/' && !verbatim))) {
        wide[--length] = L'\0';
    }

    // Nothing, a drive, '.' and '..' are refused, and what the system would
    // read as one of them: it drops the dots and spaces a name ends with.
    wchar_t last = length == 0 ? L'\0' : wide[length - 1];
    if (length == 0 || last == L':' || (!verbatim && (last == L'.' || last == L' ')) || (verbatim && last == L'.')) {
        free(wide);
        error->kind = FILE_ERROR_INVALID;
        error->code = ERROR_INVALID_PARAMETER;
        return false;
    }

    // The path as the system has it: every link above it resolved, and
    // taken as written from now on.
    HANDLE handle = fs_open(wide, FILE_READ_ATTRIBUTES, SHARE_ALL, false);
    free(wide);
    if (handle == INVALID_HANDLE_VALUE) {
        file_failed(error);
        return false;
    }

    DWORD units = GetFinalPathNameByHandleW(handle, NULL, 0, FILE_NAME_OPENED);
    wchar_t *path = units == 0 ? NULL : malloc(((size_t)units + 1) * sizeof(wchar_t));
    if (path == NULL || GetFinalPathNameByHandleW(handle, path, units + 1, FILE_NAME_OPENED) == 0) {
        file_failed(error);
        free(path);
        CloseHandle(handle);
        return false;
    }

    CloseHandle(handle);
    length = wcslen(path);
    bool removed = false;
    if (length < 8 || path[length - 1] == L'\\') {
        // A volume's root: the only path that ends so.
        error->kind = FILE_ERROR_INVALID;
        error->code = ERROR_INVALID_PARAMETER;
    } else {
        int retries = 256;
        removed = tree_remove(path, true, &retries, error);
    }

    free(path);
    return removed;
}

// ---- Renaming, links and copying -----------------------------------------------

static bool fs_rename(const char *from, const char *to, bool replace, FileError *error) {
    wchar_t *source = to_wide(from);
    wchar_t *target = to_wide(to);
    bool renamed = MoveFileExW(source, target, replace ? MOVEFILE_REPLACE_EXISTING : 0) != FALSE;
    if (!renamed) {
        file_failed(error);
    }

    free(source);
    free(target);
    return renamed;
}

static bool fs_link(const char *target, const char *name, int kind, FileError *error) {
    wchar_t *to = to_wide(target);
    wchar_t *link = to_wide(name);
    bool linked;
    if (kind == FILE_LINK_HARD) {
        linked = CreateHardLinkW(link, to, NULL) != FALSE;
    } else {
        // '/' is no separator in what a link says.
        for (wchar_t *at = to; *at != L'\0'; at++) {
            if (*at == L'/') {
                *at = L'\\';
            }
        }

        DWORD flags = (kind == FILE_LINK_DIRECTORY ? SYMBOLIC_LINK_FLAG_DIRECTORY : 0) | 0x2 /* unprivileged, in developer mode */;
        linked = CreateSymbolicLinkW(link, to, flags) != FALSE;
    }

    if (!linked) {
        file_failed(error);
    }

    free(to);
    free(link);
    return linked;
}

// What FSCTL_GET_REPARSE_POINT gives of a symbolic link or a junction.
typedef struct LinkData {
    ULONG tag;
    USHORT length, reserved;
    USHORT substitute_offset, substitute_length, print_offset, print_length;
    union {
        struct {
            ULONG flags;
            wchar_t names[1];
        } symbolic;
        wchar_t junction[1];
    } of;
} LinkData;

static char *fs_read_link(const char *name, FileError *error) {
    wchar_t *wide = to_wide(name);
    HANDLE handle = fs_open(wide, FILE_READ_ATTRIBUTES, SHARE_ALL, false);
    free(wide);
    if (handle == INVALID_HANDLE_VALUE) {
        file_failed(error);
        return NULL;
    }

    union {
        LinkData link;
        char space[MAXIMUM_REPARSE_DATA_BUFFER_SIZE];
    } data;
    DWORD got = 0;
    bool read = DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT, NULL, 0, &data, sizeof data, &got, NULL) != FALSE;
    if (!read) {
        file_failed(error);
        // Not a link, as POSIX says EINVAL.
        if (error->code == ERROR_NOT_A_REPARSE_POINT) {
            error->kind = FILE_ERROR_INVALID;
        }
    }

    CloseHandle(handle);
    if (!read) {
        return NULL;
    }

    const wchar_t *names;
    if (data.link.tag == IO_REPARSE_TAG_SYMLINK) {
        names = data.link.of.symbolic.names;
    } else if (data.link.tag == IO_REPARSE_TAG_MOUNT_POINT) {
        names = data.link.of.junction;
    } else {
        error->kind = FILE_ERROR_INVALID;
        error->code = ERROR_NOT_A_REPARSE_POINT;
        return NULL;
    }

    // As it is shown, or, with no such name, as the system keeps it.
    USHORT offset = data.link.print_length > 0 ? data.link.print_offset : data.link.substitute_offset;
    USHORT length = data.link.print_length > 0 ? data.link.print_length : data.link.substitute_length;
    size_t units = length / sizeof(wchar_t);
    wchar_t *target = malloc((units + 1) * sizeof(wchar_t));
    if (target == NULL) {
        sable_trap("out of memory");
    }

    memcpy(target, (const char *)names + offset, units * sizeof(wchar_t));
    target[units] = L'\0';
    char *result = from_wide(target);
    free(target);
    return result;
}

// The whole file copied by the system, its attributes with it.
static bool file_copy(const char *from, const char *to, bool replace, FileError *error) {
    wchar_t *source = to_wide(from);
    wchar_t *target = to_wide(to);
    DWORD attributes = GetFileAttributesW(source);
    bool copied = false;
    if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        error->kind = FILE_ERROR_IS_DIRECTORY;
        error->code = ERROR_DIRECTORY;
    } else {
        copied = CopyFileExW(source, target, NULL, NULL, NULL, replace ? 0 : COPY_FILE_FAIL_IF_EXISTS) != FALSE;
        if (!copied) {
            file_failed(error);
            // A file copied to itself is one it has open.
            HANDLE a = fs_open(source, FILE_READ_ATTRIBUTES, SHARE_ALL, true);
            HANDLE b = fs_open(target, FILE_READ_ATTRIBUTES, SHARE_ALL, true);
            BY_HANDLE_FILE_INFORMATION one, other;
            if (replace && a != INVALID_HANDLE_VALUE && b != INVALID_HANDLE_VALUE && GetFileInformationByHandle(a, &one)
                && GetFileInformationByHandle(b, &other) && one.dwVolumeSerialNumber == other.dwVolumeSerialNumber
                && one.nFileIndexHigh == other.nFileIndexHigh && one.nFileIndexLow == other.nFileIndexLow) {
                error->kind = FILE_ERROR_INVALID;
            }
            if (a != INVALID_HANDLE_VALUE) {
                CloseHandle(a);
            }
            if (b != INVALID_HANDLE_VALUE) {
                CloseHandle(b);
            }
        }
    }

    free(source);
    free(target);
    return copied;
}

// ---- Directories, an entry at a time --------------------------------------------

typedef struct DirectoryReader {
    HANDLE find;
    WIN32_FIND_DATAW entry;
    bool first; // 'entry' is still to be given
} DirectoryReader;

static void *directory_open(const char *name, FileError *error) {
    wchar_t *wide = to_wide(name);
    DWORD attributes = GetFileAttributesW(wide);
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        file_failed(error);
        if (attributes != INVALID_FILE_ATTRIBUTES) {
            error->kind = FILE_ERROR_NOT_DIRECTORY;
            error->code = ERROR_DIRECTORY;
        }
        free(wide);
        return NULL;
    }

    wchar_t *pattern = path_inside(wide, L"*", 1);
    free(wide);
    DirectoryReader *reader = malloc(sizeof *reader);
    if (reader == NULL) {
        sable_trap("out of memory");
    }

    reader->first = true;
    reader->find = FindFirstFileExW(pattern, FindExInfoBasic, &reader->entry, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    free(pattern);
    if (reader->find == INVALID_HANDLE_VALUE) {
        // An empty root has not even '.' and '..'.
        if (GetLastError() != ERROR_FILE_NOT_FOUND) {
            file_failed(error);
            free(reader);
            return NULL;
        }
        reader->first = false;
    }

    return reader;
}

static char *directory_next(void *directory, int *kind, FileError *error) {
    DirectoryReader *reader = directory;
    for (;;) {
        if (reader->find == INVALID_HANDLE_VALUE) {
            return NULL;
        }

        if (!reader->first && !FindNextFileW(reader->find, &reader->entry)) {
            if (GetLastError() != ERROR_NO_MORE_FILES) {
                file_failed(error);
            }
            return NULL;
        }

        reader->first = false;
        const WIN32_FIND_DATAW *entry = &reader->entry;
        if (wcscmp(entry->cFileName, L".") == 0 || wcscmp(entry->cFileName, L"..") == 0) {
            continue;
        }

        bool link = (entry->dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) && is_link_tag(entry->dwReserved0);
        *kind = link ? FILE_KIND_LINK : entry->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY ? FILE_KIND_DIRECTORY : FILE_KIND_FILE;
        return from_wide(entry->cFileName);
    }
}

static void directory_close(void *directory) {
    DirectoryReader *reader = directory;
    if (reader->find != INVALID_HANDLE_VALUE) {
        FindClose(reader->find);
    }

    free(reader);
}

// ---- Watching ----------------------------------------------------------------
//
// ReadDirectoryChangesW, overlapped, a directory's handle: the thread pool
// hears each time it completes, reports what it says, and asks again. A
// file is watched by the directory it is in, the rest of which is ignored.

typedef struct WatchState {
    void *watch;
    HANDLE directory;
    TP_IO *io;
    OVERLAPPED overlapped;
    wchar_t *path;  // the directory's
    wchar_t *only;  // the one name in it watched, or NULL
    bool recursive;
    volatile LONG closing;
    HANDLE finished; // set once nothing is asked, nor will be
    _Alignas(DWORD) char changes[65536]; // the most a network's takes
} WatchState;

static bool watch_ask(WatchState *state) {
    memset(&state->overlapped, 0, sizeof state->overlapped);
    StartThreadpoolIo(state->io);
    if (ReadDirectoryChangesW(state->directory, state->changes, sizeof state->changes, state->recursive,
                              FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE,
                              NULL, &state->overlapped, NULL)) {
        return true;
    }

    DWORD code = GetLastError();
    CancelThreadpoolIo(state->io);
    SetLastError(code);
    return false;
}

static void watch_change(WatchState *state, const FILE_NOTIFY_INFORMATION *change) {
    size_t units = change->FileNameLength / sizeof(wchar_t);
    if (state->only != NULL && (wcslen(state->only) != units || _wcsnicmp(state->only, change->FileName, units) != 0)) {
        return;
    }

    int kind = change->Action == FILE_ACTION_ADDED || change->Action == FILE_ACTION_RENAMED_NEW_NAME     ? WATCH_CREATED
             : change->Action == FILE_ACTION_REMOVED || change->Action == FILE_ACTION_RENAMED_OLD_NAME ? WATCH_REMOVED
                                                                                                       : WATCH_MODIFIED;
    if (kind == WATCH_MODIFIED) {
        // A directory is written as what is in it changes: its own say so.
        wchar_t *whole = path_inside(state->path, change->FileName, units);
        DWORD attributes = GetFileAttributesW(whole);
        free(whole);
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY)) {
            return;
        }
    }

    if (state->only != NULL) {
        watch_report(state->watch, kind, "", 0);
        return;
    }

    wchar_t *wide = malloc((units + 1) * sizeof(wchar_t));
    if (wide == NULL) {
        sable_trap("out of memory");
    }

    memcpy(wide, change->FileName, units * sizeof(wchar_t));
    wide[units] = L'\0';
    char *name = from_wide(wide);
    free(wide);
    watch_report(state->watch, kind, name, strlen(name));
    free(name);
}

static void CALLBACK watch_changed(PTP_CALLBACK_INSTANCE instance, PVOID context, PVOID overlapped, ULONG result, ULONG_PTR bytes, PTP_IO io) {
    (void)instance;
    (void)overlapped;
    (void)io;
    WatchState *state = context;
    if (InterlockedCompareExchange(&state->closing, 0, 0) != 0) {
        SetEvent(state->finished);
        return;
    }

    if (result != NO_ERROR && result != ERROR_NOTIFY_ENUM_DIR) {
        // The directory is gone.
        watch_report(state->watch, WATCH_REMOVED, "", 0);
        SetEvent(state->finished);
        return;
    }

    if (result == ERROR_NOTIFY_ENUM_DIR || bytes == 0) {
        // More changed than it could hold.
        watch_report(state->watch, WATCH_RESCAN, "", 0);
    } else {
        for (char *at = state->changes;;) {
            const FILE_NOTIFY_INFORMATION *change = (const FILE_NOTIFY_INFORMATION *)at;
            watch_change(state, change);
            if (change->NextEntryOffset == 0) {
                break;
            }
            at += change->NextEntryOffset;
        }
    }

    if (!watch_ask(state)) {
        watch_report(state->watch, WATCH_REMOVED, "", 0);
        SetEvent(state->finished);
    }
}

static void watch_state_free(WatchState *state) {
    if (state->io != NULL) {
        CloseThreadpoolIo(state->io);
    }

    if (state->finished != NULL) {
        CloseHandle(state->finished);
    }

    CloseHandle(state->directory);
    free(state->path);
    free(state->only);
    free(state);
}

static void *watch_open(void *watch, const char *name, bool recursive, FileError *error) {
    wchar_t *wide = to_wide(name);
    DWORD length = GetFullPathNameW(wide, 0, NULL, NULL);
    wchar_t *path = length == 0 ? NULL : malloc(((size_t)length + 1) * sizeof(wchar_t));
    if (path == NULL || GetFullPathNameW(wide, length + 1, path, NULL) == 0) {
        file_failed(error);
        free(path);
        free(wide);
        return NULL;
    }

    free(wide);
    DWORD attributes = GetFileAttributesW(path);
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        file_failed(error);
        free(path);
        return NULL;
    }

    WatchState *state = calloc(1, sizeof *state);
    if (state == NULL) {
        sable_trap("out of memory");
    }

    state->watch = watch;
    state->path = path;
    state->recursive = recursive;
    if (!(attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        wchar_t *last = wcsrchr(path, L'\\');
        if (last != NULL) {
            state->only = _wcsdup(last + 1);
            // A drive's root keeps its separator: path_inside adds no second.
            last[1] = L'\0';
        }
        state->recursive = false;
    }

    state->directory = CreateFileW(path, FILE_LIST_DIRECTORY, SHARE_ALL, NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OVERLAPPED, NULL);
    if (state->directory == INVALID_HANDLE_VALUE) {
        file_failed(error);
        free(state->path);
        free(state->only);
        free(state);
        return NULL;
    }

    state->io = CreateThreadpoolIo(state->directory, watch_changed, state, NULL);
    state->finished = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (state->io == NULL || state->finished == NULL || !watch_ask(state)) {
        file_failed(error);
        watch_state_free(state);
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
    InterlockedExchange(&state->closing, 1);
    // One being told of as this began may ask again after the first cancel.
    do {
        CancelIoEx(state->directory, NULL);
    } while (WaitForSingleObject(state->finished, 10) == WAIT_TIMEOUT);
    WaitForThreadpoolIoCallbacks(state->io, FALSE);
    watch_state_free(state);
}
