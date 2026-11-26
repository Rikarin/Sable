// The Sable runtime's pipes, named pipes, shared memory, serial ports, ping
// and network statistics for Windows: what platform.h says, under "Pipes,
// local streams, shared memory and serial ports" and "Ping, raw sockets,
// changes and statistics". Part of platform_win32.c, after
// platform_process_win32.c, whose pipes (WinPipe) these are.

// Only what windows.h (lean) and iphlpapi.h bring, and icmpapi.h: aclapi.h
// and sddl.h would bring rpcndr.h, whose macros (small, hyper) are words the
// runtime's other parts use, the runtime being one translation unit.
#include <icmpapi.h>
#undef small
#undef hyper

static int ipc_error_kind(DWORD code) {
    switch (code) {
        case ERROR_FILE_NOT_FOUND: case ERROR_PATH_NOT_FOUND: case ERROR_BAD_NETPATH: return NET_ERROR_NOT_FOUND;
        case ERROR_ACCESS_DENIED: case ERROR_SHARING_VIOLATION: case ERROR_PRIVILEGE_NOT_HELD: return NET_ERROR_PERMISSION;
        case ERROR_ALREADY_EXISTS: case ERROR_FILE_EXISTS: return NET_ERROR_EXISTS;
        case ERROR_INVALID_PARAMETER: case ERROR_INVALID_NAME: case ERROR_BAD_PATHNAME: case ERROR_FILENAME_EXCED_RANGE: return NET_ERROR_INVALID;
        case ERROR_BROKEN_PIPE: case ERROR_NO_DATA: case ERROR_PIPE_NOT_CONNECTED: return NET_ERROR_DISCONNECTED;
        case ERROR_NOT_SUPPORTED: case ERROR_CALL_NOT_IMPLEMENTED: return NET_ERROR_UNSUPPORTED;
        case ERROR_NOT_ENOUGH_MEMORY: case ERROR_OUTOFMEMORY: case ERROR_NO_SYSTEM_RESOURCES: return NET_ERROR_TRY_LATER;
        default: return NET_ERROR_OTHER;
    }
}

static int ipc_fail_with(DWORD code) {
    net_last_failure.code = (int64_t)code;
    net_last_failure.kind = ipc_error_kind(code);
    return IO_FAILED;
}

static int ipc_fail(void) {
    return ipc_fail_with(GetLastError());
}

// The prefix and the name, as Windows takes it: in memory the caller frees.
static wchar_t *ipc_wide_name(const wchar_t *prefix, const char *name) {
    wchar_t *wide = to_wide(name);
    size_t before = wcslen(prefix), length = wcslen(wide);
    wchar_t *whole = malloc((before + length + 1) * sizeof(wchar_t));
    if (whole == NULL) {
        sable_trap("out of memory");
    }

    memcpy(whole, prefix, before * sizeof(wchar_t));
    memcpy(whole + before, wide, (length + 1) * sizeof(wchar_t));
    free(wide);
    return whole;
}

// ---- Pipes -----------------------------------------------------------------

// A pipe is a named pipe with a name of its own, as a child's stream is
// (open_pipe): an end this program reads or writes is overlapped, and a
// child's is not. Two ends for children are an anonymous pipe.
static bool os_pipe(int children, intptr_t ends[2]) {
    if (children == 3) {
        HANDLE reader, writer;
        if (!CreatePipe(&reader, &writer, NULL, 0)) {
            ipc_fail();
            return false;
        }

        ends[0] = (intptr_t)reader;
        ends[1] = (intptr_t)writer;
        return true;
    }

    static LONG count;
    unsigned char random[8];
    if (!random_bytes(random, sizeof random)) {
        memset(random, 0, sizeof random);
    }

    unsigned long long part;
    memcpy(&part, random, sizeof part);
    wchar_t name[96];
    swprintf(name, sizeof name / sizeof *name, L"\\\\.\\pipe\\sable-os-%lu-%ld-%016llx", GetCurrentProcessId(), InterlockedIncrement(&count), part);
    bool own_reader = (children & 1) == 0, own_writer = (children & 2) == 0;
    HANDLE reader = CreateNamedPipeW(name, PIPE_ACCESS_INBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE | (own_reader ? FILE_FLAG_OVERLAPPED : 0),
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, WIN_PIPE_BUFFER, WIN_PIPE_BUFFER, 0, NULL);
    if (reader == INVALID_HANDLE_VALUE) {
        ipc_fail();
        return false;
    }

    HANDLE writer = CreateFileW(name, GENERIC_WRITE | FILE_READ_ATTRIBUTES, 0, NULL, OPEN_EXISTING, own_writer ? FILE_FLAG_OVERLAPPED : FILE_ATTRIBUTE_NORMAL, NULL);
    if (writer == INVALID_HANDLE_VALUE) {
        ipc_fail();
        CloseHandle(reader);
        return false;
    }

    ends[0] = own_reader ? (intptr_t)pipe_make(reader, true) : (intptr_t)reader;
    ends[1] = own_writer ? (intptr_t)pipe_make(writer, false) : (intptr_t)writer;
    return true;
}

static void handle_close(PlatformHandle handle) {
    if (handle != 0 && handle != -1) {
        CloseHandle((HANDLE)handle);
    }
}

// Windows has no FIFOs: its named pipes are LocalListener's.
static bool fifo_make(const char *path, int permissions) {
    (void)path;
    (void)permissions;
    socket_fail_as(NET_ERROR_UNSUPPORTED);
    return false;
}

static PlatformPipe fifo_open(const char *path, bool writes) {
    (void)path;
    (void)writes;
    return socket_fail_as(NET_ERROR_UNSUPPORTED);
}

// The user's own temporary directory (%LOCALAPPDATA%\Temp), for locks.
static char *local_directory(bool any) {
    (void)any;
    wchar_t path[MAX_PATH + 1];
    DWORD length = GetTempPathW(MAX_PATH + 1, path);
    if (length == 0 || length > MAX_PATH) {
        ipc_fail();
        return NULL;
    }

    // Without the separator it ends with.
    if (length > 1 && (path[length - 1] == L'\\' || path[length - 1] == L'/')) {
        path[length - 1] = L'\0';
    }

    return from_wide(path);
}

// ---- Named pipes ---------------------------------------------------------------

// This process's user, as a SID in memory the caller frees; NULL failing.
static PSID current_user(void) {
    HANDLE token;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        return NULL;
    }

    DWORD size = 0;
    GetTokenInformation(token, TokenUser, NULL, 0, &size);
    TOKEN_USER *user = size == 0 ? NULL : malloc(size);
    PSID sid = NULL;
    if (user != NULL && GetTokenInformation(token, TokenUser, user, size, &size)) {
        DWORD length = GetLengthSid(user->User.Sid);
        sid = malloc(length);
        if (sid != NULL && !CopySid(length, sid, user->User.Sid)) {
            free(sid);
            sid = NULL;
        }
    }

    free(user);
    CloseHandle(token);
    return sid;
}

// Who may open the pipe: this user alone (a DACL of one ACE, its SID's), or,
// NULL, as the system's default has it. The descriptor and its ACL are one
// block, which the caller frees.
static PSECURITY_DESCRIPTOR local_security(int flags) {
    if ((flags & LOCAL_CURRENT_USER) == 0) {
        return NULL;
    }

    PSID sid = current_user();
    if (sid == NULL) {
        return NULL;
    }

    DWORD acl_size = (DWORD)(sizeof(ACL) + sizeof(ACCESS_ALLOWED_ACE) - sizeof(DWORD) + GetLengthSid(sid));
    size_t at = (SECURITY_DESCRIPTOR_MIN_LENGTH + 7) & ~(size_t)7;
    char *block = malloc(at + acl_size);
    if (block == NULL) {
        sable_trap("out of memory");
    }

    PSECURITY_DESCRIPTOR descriptor = block;
    PACL acl = (PACL)(block + at);
    bool made = InitializeSecurityDescriptor(descriptor, SECURITY_DESCRIPTOR_REVISION) && InitializeAcl(acl, acl_size, ACL_REVISION)
                && AddAccessAllowedAce(acl, ACL_REVISION, GENERIC_ALL, sid) && SetSecurityDescriptorDacl(descriptor, TRUE, acl, FALSE);
    free(sid);
    if (!made) {
        free(block);
        return NULL;
    }

    return descriptor;
}

static PlatformPipe local_listen(const char *name, int flags, bool first) {
    wchar_t *path = ipc_wide_name(L"\\\\.\\pipe\\", name);
    PSECURITY_DESCRIPTOR descriptor = local_security(flags);
    if ((flags & LOCAL_CURRENT_USER) && descriptor == NULL) {
        free(path);
        return ipc_fail();
    }

    SECURITY_ATTRIBUTES security = { sizeof security, descriptor, FALSE };
    DWORD mode = flags & LOCAL_MESSAGES ? PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE : PIPE_TYPE_BYTE | PIPE_READMODE_BYTE;
    HANDLE pipe = CreateNamedPipeW(path, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | (first ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0),
        mode | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, PIPE_UNLIMITED_INSTANCES, WIN_PIPE_BUFFER, WIN_PIPE_BUFFER, 0,
        descriptor == NULL ? NULL : &security);
    DWORD code = GetLastError();
    free(path);
    free(descriptor);

    if (pipe == INVALID_HANDLE_VALUE) {
        // The first instance of a name another has already.
        ipc_fail_with(code);
        if (first && code == ERROR_ACCESS_DENIED) {
            net_last_failure.kind = NET_ERROR_IN_USE;
        }

        return IO_FAILED;
    }

    return (PlatformPipe)pipe_make(pipe, true);
}

static int local_accept(PlatformPipe which) {
    WinPipe *pipe = (WinPipe *)which;
    int result = IO_FAILED;
    AcquireSRWLockExclusive(&pipe->lock);
    if (pipe->handle == NULL) {
        socket_fail_as(NET_ERROR_CLOSED);
    } else if (!pipe->pending) {
        pipe_operation(pipe);
        DWORD code = ConnectNamedPipe(pipe->handle, &pipe->operation) ? ERROR_PIPE_CONNECTED : GetLastError();
        if (code == ERROR_IO_PENDING) {
            pipe->pending = true;
        } else if (code == ERROR_PIPE_CONNECTED || code == ERROR_NO_DATA) {
            // Connected before it was asked; or connected and gone again,
            // which a read finds.
            result = 0;
        } else {
            ipc_fail_with(code);
        }
    }

    if (pipe->handle != NULL && pipe->pending) {
        DWORD ignored = 0;
        if (GetOverlappedResult(pipe->handle, &pipe->operation, &ignored, FALSE)) {
            pipe->pending = false;
            result = 0;
        } else if (GetLastError() == ERROR_IO_INCOMPLETE) {
            result = IO_WOULD_BLOCK;
        } else {
            pipe->pending = false;
            ipc_fail();
        }
    }

    ReleaseSRWLockExclusive(&pipe->lock);
    return result;
}

static PlatformPipe local_writer(PlatformPipe which) {
    WinPipe *pipe = (WinPipe *)which;
    HANDLE copy = NULL;
    AcquireSRWLockExclusive(&pipe->lock);
    bool made = pipe->handle != NULL && DuplicateHandle(GetCurrentProcess(), pipe->handle, GetCurrentProcess(), &copy, 0, FALSE, DUPLICATE_SAME_ACCESS);
    ReleaseSRWLockExclusive(&pipe->lock);
    if (!made) {
        return ipc_fail();
    }

    return (PlatformPipe)pipe_make(copy, false);
}

// Whether the pipe's owner is this user: a server another user made under
// the name is not connected to.
static bool owned_by_current_user(HANDLE pipe) {
    DWORD size = 0;
    GetKernelObjectSecurity(pipe, OWNER_SECURITY_INFORMATION, NULL, 0, &size);
    PSECURITY_DESCRIPTOR descriptor = size == 0 ? NULL : malloc(size);
    PSID owner = NULL;
    BOOL defaulted = FALSE;
    bool known = descriptor != NULL && GetKernelObjectSecurity(pipe, OWNER_SECURITY_INFORMATION, descriptor, size, &size)
                 && GetSecurityDescriptorOwner(descriptor, &owner, &defaulted);
    PSID user = known ? current_user() : NULL;
    bool same = user != NULL && owner != NULL && EqualSid(owner, user);
    free(user);
    free(descriptor);
    return same;
}

static PlatformPipe local_connect(const char *name, int flags) {
    wchar_t *path = ipc_wide_name(L"\\\\.\\pipe\\", name);
    HANDLE pipe = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, NULL);
    DWORD code = GetLastError();
    free(path);
    if (pipe == INVALID_HANDLE_VALUE) {
        return code == ERROR_PIPE_BUSY ? IO_WOULD_BLOCK : ipc_fail_with(code);
    }

    DWORD mode = PIPE_READMODE_MESSAGE;
    bool refused = (flags & LOCAL_CURRENT_USER) && !owned_by_current_user(pipe);
    if (refused || ((flags & LOCAL_MESSAGES) && !SetNamedPipeHandleState(pipe, &mode, NULL, NULL))) {
        code = refused ? ERROR_ACCESS_DENIED : GetLastError();
        CloseHandle(pipe);
        return ipc_fail_with(code);
    }

    return (PlatformPipe)pipe_make(pipe, true);
}

static int64_t local_peer_process(PlatformPipe which, bool server) {
    WinPipe *pipe = (WinPipe *)which;
    ULONG process = 0;
    AcquireSRWLockExclusive(&pipe->lock);
    BOOL known = pipe->handle != NULL
                 && (server ? GetNamedPipeClientProcessId(pipe->handle, &process) : GetNamedPipeServerProcessId(pipe->handle, &process));
    ReleaseSRWLockExclusive(&pipe->lock);
    return known ? (int64_t)process : -1;
}

// ---- Shared memory, semaphores and locks ---------------------------------------------

// A mapping of the paging file, named in the session's space. It lives while
// a handle or a view of it does: removing it is letting it go.
static PlatformHandle shared_open(const char *name, int64_t size, bool create, int64_t *actual) {
    wchar_t *path = ipc_wide_name(L"Local\\sable.shm.", name);
    HANDLE mapping;
    if (create) {
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, (DWORD)((uint64_t)size >> 32), (DWORD)((uint64_t)size & 0xFFFFFFFF), path);
        if (mapping != NULL && GetLastError() == ERROR_ALREADY_EXISTS) {
            CloseHandle(mapping);
            free(path);
            return ipc_fail_with(ERROR_ALREADY_EXISTS);
        }

        *actual = size;
    } else {
        mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, path);
        // Its size is its view's, whole pages.
        void *view = mapping == NULL ? NULL : MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
        MEMORY_BASIC_INFORMATION region;
        *actual = view != NULL && VirtualQuery(view, &region, sizeof region) != 0 ? (int64_t)region.RegionSize : 0;
        if (view != NULL) {
            UnmapViewOfFile(view);
        }
    }

    DWORD code = GetLastError();
    free(path);
    if (mapping == NULL) {
        return ipc_fail_with(code);
    }

    return (PlatformHandle)mapping;
}

static bool shared_remove(const char *name) {
    (void)name;
    return true;
}

static PlatformHandle semaphore_open(const char *name, int64_t count, bool create) {
    if (count < 0 || count > MAXLONG) {
        return ipc_fail_with(ERROR_INVALID_PARAMETER);
    }

    wchar_t *path = ipc_wide_name(L"Local\\sable.sem.", name);
    HANDLE semaphore = create ? CreateSemaphoreW(NULL, (LONG)count, MAXLONG, path) : OpenSemaphoreW(SYNCHRONIZE | SEMAPHORE_MODIFY_STATE, FALSE, path);
    DWORD code = GetLastError();
    free(path);
    if (semaphore != NULL && create && code == ERROR_ALREADY_EXISTS) {
        CloseHandle(semaphore);
        return ipc_fail_with(code);
    }

    return semaphore == NULL ? ipc_fail_with(code) : (PlatformHandle)semaphore;
}

static int semaphore_try(PlatformHandle semaphore) {
    DWORD result = WaitForSingleObject((HANDLE)semaphore, 0);
    if (result == WAIT_OBJECT_0) {
        return 1;
    }

    if (result == WAIT_TIMEOUT) {
        return 0;
    }

    ipc_fail();
    return -1;
}

static bool semaphore_release(PlatformHandle semaphore, int64_t count) {
    if (count < 0 || count > MAXLONG || !ReleaseSemaphore((HANDLE)semaphore, (LONG)count, NULL)) {
        count < 0 || count > MAXLONG ? ipc_fail_with(ERROR_INVALID_PARAMETER) : ipc_fail();
        return false;
    }

    return true;
}

static void semaphore_close(PlatformHandle semaphore) {
    CloseHandle((HANDLE)semaphore);
}

static bool semaphore_remove(const char *name) {
    (void)name;
    return true;
}

// A file opened to be shared with no one: held until it is closed, or the
// process ends.
static PlatformHandle lock_try(const char *path) {
    wchar_t *wide = to_wide_path(path);
    HANDLE file = CreateFileW(wide, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    DWORD code = GetLastError();
    free(wide);
    if (file == INVALID_HANDLE_VALUE) {
        return code == ERROR_SHARING_VIOLATION ? IO_WOULD_BLOCK : ipc_fail_with(code);
    }

    return (PlatformHandle)file;
}

// ---- Serial ports ----------------------------------------------------------------

static bool serial_configure(PlatformPipe port, const SerialSettings *settings) {
    HANDLE handle = ((WinPipe *)port)->handle;
    DCB control;
    memset(&control, 0, sizeof control);
    control.DCBlength = sizeof control;
    if (settings->baud <= 0 || settings->baud > UINT32_MAX || settings->data_bits < 5 || settings->data_bits > 8 || settings->parity < 0
        || settings->parity > 4 || settings->stop_bits < 1 || settings->stop_bits > 3) {
        ipc_fail_with(ERROR_INVALID_PARAMETER);
        return false;
    }

    if (handle == NULL || !GetCommState(handle, &control)) {
        handle == NULL ? socket_fail_as(NET_ERROR_CLOSED) : ipc_fail();
        return false;
    }

    control.BaudRate = (DWORD)settings->baud;
    control.ByteSize = (BYTE)settings->data_bits;
    // NOPARITY, ODDPARITY, EVENPARITY, MARKPARITY, SPACEPARITY: the library's order.
    control.Parity = (BYTE)settings->parity;
    control.StopBits = settings->stop_bits == 2 ? TWOSTOPBITS : settings->stop_bits == 3 ? ONE5STOPBITS : ONESTOPBIT;
    control.fBinary = TRUE;
    control.fParity = settings->parity != 0;
    control.fOutxCtsFlow = settings->flow == 1;
    control.fOutxDsrFlow = FALSE;
    control.fDtrControl = DTR_CONTROL_ENABLE;
    control.fDsrSensitivity = FALSE;
    control.fRtsControl = settings->flow == 1 ? RTS_CONTROL_HANDSHAKE : RTS_CONTROL_ENABLE;
    control.fOutX = control.fInX = settings->flow == 2;
    control.XonChar = 0x11;
    control.XoffChar = 0x13;
    control.XonLim = 2048;
    control.XoffLim = 512;
    control.fAbortOnError = FALSE;
    control.fNull = FALSE;
    control.fErrorChar = FALSE;

    // A read returns as soon as a byte has come, and waits as long as it
    // must for one: whatever is there at once, or the first to come.
    COMMTIMEOUTS timeouts = { MAXDWORD, MAXDWORD, MAXDWORD - 1, 0, 0 };
    if (!SetCommState(handle, &control) || !SetCommTimeouts(handle, &timeouts)) {
        ipc_fail();
        return false;
    }

    return true;
}

static bool serial_open(const char *name, const SerialSettings *settings, PlatformPipe ends[2]) {
    wchar_t *path = strncmp(name, "\\\\", 2) == 0 ? to_wide(name) : ipc_wide_name(L"\\\\.\\", name);
    HANDLE port = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
    DWORD code = GetLastError();
    free(path);
    if (port == INVALID_HANDLE_VALUE) {
        ipc_fail_with(code);
        return false;
    }

    WinPipe *reader = pipe_make(port, true);
    HANDLE copy = NULL;
    if (!serial_configure((PlatformPipe)reader, settings)
        || !DuplicateHandle(GetCurrentProcess(), port, GetCurrentProcess(), &copy, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
        NetFailure failure = net_last_failure;
        if (copy == NULL && failure.kind == NET_ERROR_OTHER && failure.code == 0) {
            ipc_fail();
            failure = net_last_failure;
        }

        pipe_close((PlatformPipe)reader);
        net_last_failure = failure;
        return false;
    }

    ends[0] = (PlatformPipe)reader;
    ends[1] = (PlatformPipe)pipe_make(copy, false);
    return true;
}

static HANDLE serial_handle(PlatformPipe port) {
    HANDLE handle = ((WinPipe *)port)->handle;
    if (handle == NULL) {
        socket_fail_as(NET_ERROR_CLOSED);
    }

    return handle;
}

// DTR and RTS are what this program sets, and Windows does not say them.
static int serial_lines(PlatformPipe port) {
    HANDLE handle = serial_handle(port);
    DWORD status = 0;
    if (handle == NULL || !GetCommModemStatus(handle, &status)) {
        handle == NULL ? 0 : ipc_fail();
        return -1;
    }

    return (status & MS_CTS_ON ? SERIAL_CTS : 0) | (status & MS_DSR_ON ? SERIAL_DSR : 0) | (status & MS_RING_ON ? SERIAL_RING : 0)
           | (status & MS_RLSD_ON ? SERIAL_CARRIER : 0);
}

static bool serial_set_line(PlatformPipe port, int line, bool on) {
    HANDLE handle = serial_handle(port);
    DWORD function = line == SERIAL_DTR ? (on ? SETDTR : CLRDTR) : line == SERIAL_RTS ? (on ? SETRTS : CLRRTS) : 0;
    if (handle == NULL || function == 0 || !EscapeCommFunction(handle, function)) {
        handle == NULL ? 0 : function == 0 ? ipc_fail_with(ERROR_INVALID_PARAMETER) : ipc_fail();
        return false;
    }

    return true;
}

static bool serial_break(PlatformPipe port, bool on) {
    HANDLE handle = serial_handle(port);
    if (handle == NULL || !(on ? SetCommBreak(handle) : ClearCommBreak(handle))) {
        handle == NULL ? 0 : ipc_fail();
        return false;
    }

    return true;
}

static bool serial_discard(PlatformPipe port, bool input, bool output) {
    HANDLE handle = serial_handle(port);
    DWORD flags = (input ? PURGE_RXCLEAR : 0) | (output ? PURGE_TXCLEAR : 0);
    if (handle == NULL || (flags != 0 && !PurgeComm(handle, flags))) {
        handle == NULL ? 0 : ipc_fail();
        return false;
    }

    if (input) {
        WinPipe *pipe = (WinPipe *)port;
        AcquireSRWLockExclusive(&pipe->lock);
        if (!pipe->pending) {
            pipe->used = pipe->length;
        }

        ReleaseSRWLockExclusive(&pipe->lock);
    }

    return true;
}

// What waits to be read is the driver's and what the pipe has read already.
static int64_t serial_queued(PlatformPipe port, bool output) {
    HANDLE handle = serial_handle(port);
    DWORD errors = 0;
    COMSTAT status;
    if (handle == NULL || !ClearCommError(handle, &errors, &status)) {
        handle == NULL ? 0 : ipc_fail();
        return -1;
    }

    if (output) {
        return status.cbOutQue;
    }

    WinPipe *pipe = (WinPipe *)port;
    AcquireSRWLockExclusive(&pipe->lock);
    int64_t buffered = pipe->pending ? 0 : (int64_t)(pipe->length - pipe->used);
    ReleaseSRWLockExclusive(&pipe->lock);
    return status.cbInQue + buffered;
}

// The ports' names, as the system's registry has them: COM1, COM3.
static char *serial_names(void) {
    Buffer names = { 0 };
    HKEY key;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"HARDWARE\\DEVICEMAP\\SERIALCOMM", 0, KEY_READ, &key) == ERROR_SUCCESS) {
        for (DWORD index = 0;; index++) {
            wchar_t value[256], data[256];
            DWORD value_length = sizeof value / sizeof *value, data_size = sizeof data - sizeof *data, type = 0;
            LONG result = RegEnumValueW(key, index, value, &value_length, NULL, &type, (BYTE *)data, &data_size);
            if (result == ERROR_NO_MORE_ITEMS) {
                break;
            }

            if (result != ERROR_SUCCESS || type != REG_SZ) {
                continue;
            }

            data[data_size / sizeof *data] = L'\0';
            char *name = from_wide(data);
            buffer_add(&names, name, strlen(name));
            buffer_add(&names, "\n", 1);
            free(name);
        }

        RegCloseKey(key);
    }

    buffer_add(&names, "", 1);
    return names.bytes;
}

// ---- Ping and raw sockets ---------------------------------------------------------

// Windows has no socket of ICMP datagrams: Ping uses IcmpSendEcho2.
static PlatformSocket socket_raw(int family, int protocol, bool raw) {
    if (!raw) {
        return socket_fail_as(NET_ERROR_UNSUPPORTED);
    }

    if (!sockets_start()) {
        return socket_fail();
    }

    SOCKET handle = WSASocketW(family == NET_IPV6 ? AF_INET6 : AF_INET, SOCK_RAW, protocol, NULL, 0, WSA_FLAG_OVERLAPPED | WSA_FLAG_NO_HANDLE_INHERIT);
    if (handle == INVALID_SOCKET) {
        return socket_fail();
    }

    if (!socket_prepare(handle)) {
        return socket_give_up(handle);
    }

    return (PlatformSocket)handle;
}

static int64_t socket_icmp_error(PlatformSocket socket, int *type, int *code, PlatformAddress *from, void *bytes, int64_t size) {
    (void)socket;
    (void)type;
    (void)code;
    (void)from;
    (void)bytes;
    (void)size;
    return IO_WOULD_BLOCK;
}

static bool ping_echo(const PlatformAddress *to, int timeout, int hops, bool dont_fragment, const void *bytes, int64_t size, int64_t out[2], PlatformAddress *from) {
    if (size < 0 || size > 65500) {
        socket_fail_as(NET_ERROR_INVALID);
        return false;
    }

    bool six = to->family == NET_IPV6;
    HANDLE service = six ? Icmp6CreateFile() : IcmpCreateFile();
    if (service == INVALID_HANDLE_VALUE) {
        ipc_fail();
        return false;
    }

    IP_OPTION_INFORMATION options;
    memset(&options, 0, sizeof options);
    options.Ttl = (UCHAR)(hops <= 0 ? 128 : hops > 255 ? 255 : hops);
    options.Flags = dont_fragment ? IP_FLAG_DF : 0;
    DWORD room = (DWORD)(sizeof(ICMPV6_ECHO_REPLY) + sizeof(ICMP_ECHO_REPLY) + size + 8 + 64);
    void *reply = calloc(1, room);
    if (reply == NULL) {
        sable_trap("out of memory");
    }

    DWORD count;
    if (six) {
        struct sockaddr_in6 source, target;
        memset(&source, 0, sizeof source);
        source.sin6_family = AF_INET6;
        struct sockaddr_storage storage;
        socket_address(to, &storage);
        memcpy(&target, &storage, sizeof target);
        count = Icmp6SendEcho2(service, NULL, NULL, NULL, &source, &target, (LPVOID)bytes, (WORD)size, &options, reply, room, (DWORD)timeout);
    } else {
        IPAddr target = htonl((u_long)to->low);
        count = IcmpSendEcho2(service, NULL, NULL, NULL, target, (LPVOID)bytes, (WORD)size, &options, reply, room, (DWORD)timeout);
    }

    memset(from, 0, sizeof *from);
    if (count == 0) {
        // No reply: why is the error, IP_REQ_TIMED_OUT and the like.
        out[0] = (int64_t)GetLastError();
        out[1] = 0;
    } else if (six) {
        Icmp6ParseReplies(reply, room);
        const ICMPV6_ECHO_REPLY *answer = reply;
        out[0] = answer->Status;
        out[1] = answer->RoundTripTime;
        from->family = NET_IPV6;
        from->scope = answer->Address.sin6_scope_id;
        for (int i = 0; i < 4; i++) {
            uint16_t high = ntohs(answer->Address.sin6_addr[i]), low = ntohs(answer->Address.sin6_addr[4 + i]);
            from->high = from->high << 16 | high;
            from->low = from->low << 16 | low;
        }
    } else {
        const ICMP_ECHO_REPLY *answer = reply;
        out[0] = answer->Status;
        out[1] = answer->RoundTripTime;
        from->family = NET_IPV4;
        from->low = ntohl(answer->Address);
    }

    free(reply);
    IcmpCloseHandle(service);
    return true;
}

// ---- Changes to the interfaces ----------------------------------------------------

static PlatformSocket changes_open(void) {
    return socket_fail_as(NET_ERROR_UNSUPPORTED);
}

static void changes_drain(PlatformSocket socket) {
    (void)socket;
}

typedef struct ChangeNotices {
    int64_t event;
    HANDLE interfaces, addresses;
} ChangeNotices;

static VOID WINAPI interface_changed(PVOID context, PMIB_IPINTERFACE_ROW row, MIB_NOTIFICATION_TYPE kind) {
    (void)row;
    (void)kind;
    io_event(((ChangeNotices *)context)->event);
}

static VOID WINAPI address_changed(PVOID context, PMIB_UNICASTIPADDRESS_ROW row, MIB_NOTIFICATION_TYPE kind) {
    (void)row;
    (void)kind;
    io_event(((ChangeNotices *)context)->event);
}

static void *changes_watch(int64_t event) {
    ChangeNotices *notices = calloc(1, sizeof *notices);
    if (notices == NULL) {
        sable_trap("out of memory");
    }

    notices->event = event;
    DWORD code = NotifyIpInterfaceChange(AF_UNSPEC, interface_changed, notices, FALSE, &notices->interfaces);
    if (code == NO_ERROR) {
        code = NotifyUnicastIpAddressChange(AF_UNSPEC, address_changed, notices, FALSE, &notices->addresses);
        if (code != NO_ERROR) {
            CancelMibChangeNotify2(notices->interfaces);
        }
    }

    if (code != NO_ERROR) {
        free(notices);
        ipc_fail_with(code);
        return NULL;
    }

    return notices;
}

// Returns once no callback runs: the event is not told after.
static void changes_unwatch(void *watch) {
    ChangeNotices *notices = watch;
    CancelMibChangeNotify2(notices->addresses);
    CancelMibChangeNotify2(notices->interfaces);
    free(notices);
}

// ---- Connections and counters ------------------------------------------------------

static void ipc_address_from(int family, const void *address, DWORD scope, DWORD port, PlatformAddress *to) {
    memset(to, 0, sizeof *to);
    const unsigned char *bytes = address;
    to->family = family;
    to->port = ntohs((u_short)port);
    to->scope = family == NET_IPV6 ? scope : 0;
    for (int i = 0; i < (family == NET_IPV6 ? 8 : 4); i++) {
        if (family == NET_IPV6) {
            to->high = to->high << 8 | bytes[i];
            to->low = to->low << 8 | bytes[8 + i];
        } else {
            to->low = to->low << 8 | bytes[i];
        }
    }
}

// The table the system gives, of the family: in memory the caller frees.
static void *connection_table(int protocol, ULONG family) {
    DWORD size = 0;
    void *table = NULL;
    DWORD result = ERROR_INSUFFICIENT_BUFFER;
    for (int tries = 0; tries < 4 && result == ERROR_INSUFFICIENT_BUFFER; tries++) {
        free(table);
        table = size == 0 ? NULL : malloc(size);
        result = protocol == 6 ? GetExtendedTcpTable(table, &size, FALSE, family, TCP_TABLE_OWNER_PID_ALL, 0)
                               : GetExtendedUdpTable(table, &size, FALSE, family, UDP_TABLE_OWNER_PID, 0);
    }

    if (result != NO_ERROR) {
        free(table);
        return NULL;
    }

    return table;
}

static int connections_list(int protocol, PlatformConnection **rows) {
    *rows = NULL;
    int count = 0, capacity = 0;
    for (int six = 0; six < 2; six++) {
        void *table = connection_table(protocol, six ? AF_INET6 : AF_INET);
        if (table == NULL) {
            continue;
        }

        DWORD entries = *(DWORD *)table;
        for (DWORD i = 0; i < entries; i++) {
            if (count == capacity) {
                capacity = capacity == 0 ? 64 : capacity * 2;
                PlatformConnection *grown = realloc(*rows, (size_t)capacity * sizeof **rows);
                if (grown == NULL) {
                    sable_trap("out of memory");
                }

                *rows = grown;
            }

            PlatformConnection *row = &(*rows)[count++];
            memset(row, 0, sizeof *row);
            if (protocol == 6 && six) {
                const MIB_TCP6ROW_OWNER_PID *at = &((const MIB_TCP6TABLE_OWNER_PID *)table)->table[i];
                ipc_address_from(NET_IPV6, at->ucLocalAddr, at->dwLocalScopeId, at->dwLocalPort, &row->local);
                ipc_address_from(NET_IPV6, at->ucRemoteAddr, at->dwRemoteScopeId, at->dwRemotePort, &row->remote);
                row->state = (int)at->dwState;
                row->process = at->dwOwningPid;
            } else if (protocol == 6) {
                const MIB_TCPROW_OWNER_PID *at = &((const MIB_TCPTABLE_OWNER_PID *)table)->table[i];
                ipc_address_from(NET_IPV4, &at->dwLocalAddr, 0, at->dwLocalPort, &row->local);
                ipc_address_from(NET_IPV4, &at->dwRemoteAddr, 0, at->dwRemotePort, &row->remote);
                row->state = (int)at->dwState;
                row->process = at->dwOwningPid;
            } else if (six) {
                const MIB_UDP6ROW_OWNER_PID *at = &((const MIB_UDP6TABLE_OWNER_PID *)table)->table[i];
                ipc_address_from(NET_IPV6, at->ucLocalAddr, at->dwLocalScopeId, at->dwLocalPort, &row->local);
                row->remote.family = NET_IPV6;
                row->process = at->dwOwningPid;
            } else {
                const MIB_UDPROW_OWNER_PID *at = &((const MIB_UDPTABLE_OWNER_PID *)table)->table[i];
                ipc_address_from(NET_IPV4, &at->dwLocalAddr, 0, at->dwLocalPort, &row->local);
                row->remote.family = NET_IPV4;
                row->process = at->dwOwningPid;
            }
        }

        free(table);
    }

    return count;
}

// IPv4's and IPv6's, added.
static bool protocol_counters(int protocol, int64_t counters[8]) {
    for (int i = 0; i < 8; i++) {
        counters[i] = protocol == 6 || i < 4 ? 0 : -1;
    }

    bool any = false;
    for (int six = 0; six < 2; six++) {
        ULONG family = six ? AF_INET6 : AF_INET;
        if (protocol == 6) {
            MIB_TCPSTATS stats;
            if (GetTcpStatisticsEx(&stats, family) != NO_ERROR) {
                continue;
            }

            counters[0] += stats.dwActiveOpens;
            counters[1] += stats.dwPassiveOpens;
            counters[2] += stats.dwAttemptFails;
            counters[3] += stats.dwCurrEstab;
            counters[4] += stats.dwInSegs;
            counters[5] += stats.dwOutSegs;
            counters[6] += stats.dwRetransSegs;
            counters[7] += stats.dwOutRsts;
        } else {
            MIB_UDPSTATS stats;
            if (GetUdpStatisticsEx(&stats, family) != NO_ERROR) {
                continue;
            }

            counters[0] += stats.dwInDatagrams;
            counters[1] += stats.dwOutDatagrams;
            counters[2] += stats.dwNoPorts;
            counters[3] += stats.dwInErrors;
        }

        any = true;
    }

    if (!any) {
        ipc_fail();
    }

    return any;
}
