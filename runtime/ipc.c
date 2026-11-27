// The Sable runtime: pipes, local streams, shared memory, named semaphores
// and locks, serial ports — what Sable.IO's OsPipe, Sable.IO.Ipc and
// Sable.IO.Ports call (@extern) — and ping, raw sockets, changes to the
// interfaces and the system's connections, Sable.Net's.
// Part of sable_runtime.c, which includes every part in order (see there).
//
// A name crosses as its UTF-8 bytes and their count. A call that fails says
// why as a socket's call does: Runtime.socketError, and its code.

// The name as a C string, in memory the caller frees; NULL, failing as
// invalid, for one with a NUL in it.
static char *ipc_c_name(const uint8_t *bytes, int64_t length) {
    if (length < 0 || (length > 0 && memchr(bytes, '\0', (size_t)length) != NULL)) {
        socket_fail_as(NET_ERROR_INVALID);
        return NULL;
    }

    char *name = malloc((size_t)length + 1);
    if (name == NULL) {
        sable_trap("out of memory");
    }

    if (length > 0) {
        memcpy(name, bytes, (size_t)length);
    }

    name[length] = '\0';
    return name;
}

// This program's end of a pipe watched as a source: its handle, or 0.
static int64_t ipc_source(PlatformPipe pipe) {
    int64_t handle = io_source_open(pipe, SOURCE_PIPE, REACTOR_READABLE | REACTOR_WRITABLE);
    if (handle == 0) {
        socket_fail_as(NET_ERROR_UNSUPPORTED);
    }

    return handle;
}

// ---- Pipes -----------------------------------------------------------------------

// A pipe's two ends in 'ends', the reader's first: a source's handle for
// each of this program's, and the handle itself for one (by its bit in
// 'children') a child is to be given. False when it could not be made.
bool sable_ipc_pipe(int64_t children, int64_t *ends) {
    intptr_t made[2];
    if (!os_pipe((int)children, made)) {
        return false;
    }

    for (int i = 0; i < 2; i++) {
        if (children & (1 << i)) {
            ends[i] = made[i];
        } else {
            ends[i] = ipc_source((PlatformPipe)made[i]);
            if (ends[i] == 0) {
                // The source closed its pipe; the other end goes too.
                if (i == 0) {
                    (children & 2) ? handle_close(made[1]) : pipe_close((PlatformPipe)made[1]);
                } else {
                    (children & 1) ? handle_close(made[0]) : io_source_close(ends[0]);
                }

                return false;
            }
        }
    }

    return true;
}

void sable_ipc_handle_close(int64_t handle) {
    handle_close((PlatformHandle)handle);
}

bool sable_ipc_fifo_make(const uint8_t *path, int64_t length, int64_t permissions) {
    char *name = ipc_c_name(path, length);
    bool made = name != NULL && fifo_make(name, (int)permissions);
    free(name);
    return made;
}

// An end of the FIFO: a source's handle; -1 while it would wait for a
// reader; 0 failing.
int64_t sable_ipc_fifo_open(const uint8_t *path, int64_t length, bool writes) {
    char *name = ipc_c_name(path, length);
    if (name == NULL) {
        return 0;
    }

    PlatformPipe pipe = fifo_open(name, writes);
    free(name);
    return pipe == IO_WOULD_BLOCK ? -1 : pipe == IO_FAILED ? 0 : ipc_source(pipe);
}

// ---- Local streams ------------------------------------------------------------------

// The directory, written into 'into' if it has room: its length, or -1.
int64_t sable_ipc_directory(bool any, uint8_t *into, int64_t room) {
    char *path = local_directory(any);
    if (path == NULL) {
        return -1;
    }

    int64_t length = (int64_t)strlen(path);
    if (length <= room) {
        memcpy(into, path, (size_t)length);
    }

    free(path);
    return length;
}

// An instance of a named pipe to accept by: the source's handle, or 0.
int64_t sable_ipc_local_listen(const uint8_t *bytes, int64_t length, int64_t flags, const uint8_t *group, int64_t group_length, bool first) {
    char *name = ipc_c_name(bytes, length);
    char *owners = name == NULL ? NULL : ipc_c_name(group, group_length);
    if (owners == NULL) {
        free(name);
        return 0;
    }

    PlatformPipe pipe = local_listen(name, (int)flags, owners, first);
    free(name);
    free(owners);
    return pipe == IO_FAILED ? 0 : ipc_source(pipe);
}

// The socket's file opened to the group (POSIX).
bool sable_ipc_local_share(const uint8_t *path, int64_t length, const uint8_t *group, int64_t group_length) {
    char *name = ipc_c_name(path, length);
    char *owners = name == NULL ? NULL : ipc_c_name(group, group_length);
    bool shared = owners != NULL && local_share(name, owners);
    free(name);
    free(owners);
    return shared;
}

// 0 once a client is connected, 1 while none is (waited for as a read), 2
// failing.
int64_t sable_ipc_local_accept(int64_t handle) {
    uint64_t ticks;
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, &ticks);
    if (source == NULL) {
        net_closed();
        return 2;
    }

    int result = local_accept((PlatformPipe)at);
    if (result == IO_WOULD_BLOCK) {
        io_would_block(source, 0, ticks);
    }

    io_end(source);
    return result == 0 ? 0 : result == IO_WOULD_BLOCK ? 1 : 2;
}

// The connected pipe's direction to write by: a source's handle, or 0.
int64_t sable_ipc_local_writer(int64_t handle) {
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, NULL);
    if (source == NULL) {
        net_closed();
        return 0;
    }

    PlatformPipe writer = local_writer((PlatformPipe)at);
    io_end(source);
    return writer == IO_FAILED ? 0 : ipc_source(writer);
}

// A client's end, to read by: a source's handle; -1 while every instance
// is busy; 0 failing.
int64_t sable_ipc_local_connect(const uint8_t *bytes, int64_t length, int64_t flags) {
    char *name = ipc_c_name(bytes, length);
    if (name == NULL) {
        return 0;
    }

    PlatformPipe pipe = local_connect(name, (int)flags);
    free(name);
    return pipe == IO_WOULD_BLOCK ? -1 : pipe == IO_FAILED ? 0 : ipc_source(pipe);
}

int64_t sable_ipc_local_peer(int64_t handle, bool server) {
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, NULL);
    if (source == NULL) {
        return -1;
    }

    int64_t process = local_peer_process((PlatformPipe)at, server);
    io_end(source);
    return process;
}

// ---- Shared memory, semaphores and locks ------------------------------------------------

// Shared memory's handle, mapped by Runtime.fileMap's mode 3, its size in
// 'actual'; or -1.
int64_t sable_ipc_shared_open(const uint8_t *bytes, int64_t length, int64_t size, bool create, int64_t *actual) {
    char *name = ipc_c_name(bytes, length);
    if (name == NULL) {
        return -1;
    }

    PlatformHandle handle = shared_open(name, size, create, actual);
    free(name);
    return handle < 0 ? -1 : (int64_t)handle;
}

bool sable_ipc_shared_remove(const uint8_t *bytes, int64_t length) {
    char *name = ipc_c_name(bytes, length);
    bool removed = name != NULL && shared_remove(name);
    free(name);
    return removed;
}

int64_t sable_ipc_semaphore_open(const uint8_t *bytes, int64_t length, int64_t count, bool create) {
    char *name = ipc_c_name(bytes, length);
    if (name == NULL) {
        return -1;
    }

    PlatformHandle handle = semaphore_open(name, count, create);
    free(name);
    return handle < 0 ? -1 : (int64_t)handle;
}

int64_t sable_ipc_semaphore_try(int64_t semaphore) {
    return semaphore_try((PlatformHandle)semaphore);
}

bool sable_ipc_semaphore_release(int64_t semaphore, int64_t count) {
    return semaphore_release((PlatformHandle)semaphore, count);
}

void sable_ipc_semaphore_close(int64_t semaphore) {
    semaphore_close((PlatformHandle)semaphore);
}

bool sable_ipc_semaphore_remove(const uint8_t *bytes, int64_t length) {
    char *name = ipc_c_name(bytes, length);
    bool removed = name != NULL && semaphore_remove(name);
    free(name);
    return removed;
}

// The lock's handle, closed to let it go; -1 while another holds it; -2
// failing.
int64_t sable_ipc_lock(const uint8_t *bytes, int64_t length) {
    char *path = ipc_c_name(bytes, length);
    if (path == NULL) {
        return -2;
    }

    PlatformHandle handle = lock_try(path);
    free(path);
    return handle == IO_WOULD_BLOCK ? -1 : handle < 0 ? -2 : (int64_t)handle;
}

// ---- Serial ports --------------------------------------------------------------------

static SerialSettings serial_settings(int64_t baud, int64_t data_bits, int64_t parity, int64_t stop_bits, int64_t flow) {
    SerialSettings settings = { baud, (int)data_bits, (int)parity, (int)stop_bits, (int)flow };
    return settings;
}

// The port opened: its sources' handles in 'ends', to read and to write
// by, the same one twice where one does both. False failing.
bool sable_ipc_serial_open(const uint8_t *bytes, int64_t length, int64_t baud, int64_t data_bits, int64_t parity, int64_t stop_bits, int64_t flow,
    int64_t *ends) {
    char *name = ipc_c_name(bytes, length);
    if (name == NULL) {
        return false;
    }

    SerialSettings settings = serial_settings(baud, data_bits, parity, stop_bits, flow);
    PlatformPipe made[2];
    bool opened = serial_open(name, &settings, made);
    free(name);
    if (!opened) {
        return false;
    }

    ends[0] = ipc_source(made[0]);
    if (ends[0] == 0) {
        if (made[1] != PIPE_NONE) {
            pipe_close(made[1]);
        }

        return false;
    }

    ends[1] = made[1] == PIPE_NONE ? ends[0] : ipc_source(made[1]);
    if (ends[1] == 0) {
        io_source_close(ends[0]);
        return false;
    }

    return true;
}

// What a call on the port's source does, with its pipe: -1, failing as
// closed, when there is none.
#define SERIAL_CALL(handle, call)                                                                                                                    \
    PlatformSocket at;                                                                                                                               \
    IoSource *source = io_begin(handle, &at, 0, NULL);                                                                                               \
    if (source == NULL) {                                                                                                                            \
        net_closed();                                                                                                                                \
        return -1;                                                                                                                                   \
    }                                                                                                                                                \
    int64_t result = (call);                                                                                                                         \
    io_end(source);                                                                                                                                  \
    return result

int64_t sable_ipc_serial_configure(int64_t handle, int64_t baud, int64_t data_bits, int64_t parity, int64_t stop_bits, int64_t flow) {
    SerialSettings settings = serial_settings(baud, data_bits, parity, stop_bits, flow);
    SERIAL_CALL(handle, serial_configure((PlatformPipe)at, &settings) ? 0 : -1);
}

int64_t sable_ipc_serial_lines(int64_t handle) {
    SERIAL_CALL(handle, serial_lines((PlatformPipe)at));
}

int64_t sable_ipc_serial_set_line(int64_t handle, int64_t line, bool on) {
    SERIAL_CALL(handle, serial_set_line((PlatformPipe)at, (int)line, on) ? 0 : -1);
}

int64_t sable_ipc_serial_break(int64_t handle, bool on) {
    SERIAL_CALL(handle, serial_break((PlatformPipe)at, on) ? 0 : -1);
}

int64_t sable_ipc_serial_discard(int64_t handle, bool input, bool output) {
    SERIAL_CALL(handle, serial_discard((PlatformPipe)at, input, output) ? 0 : -1);
}

int64_t sable_ipc_serial_queued(int64_t handle, bool output) {
    SERIAL_CALL(handle, serial_queued((PlatformPipe)at, output));
}

#undef SERIAL_CALL

// The system's serial ports, '\n' after each, written into 'into' if it has
// room: their length; 0 where the library lists them itself.
int64_t sable_ipc_serial_names(uint8_t *into, int64_t room) {
    char *names = serial_names();
    if (names == NULL) {
        return 0;
    }

    int64_t length = (int64_t)strlen(names);
    if (length <= room) {
        memcpy(into, names, (size_t)length);
    }

    free(names);
    return length;
}

// ---- Ping and raw sockets ----------------------------------------------------------------

// A socket of ICMP datagrams (raw false), which needs no privilege where
// the system allows it, or a raw one of the protocol: its handle, or 0.
int64_t sable_net_raw(int64_t family, int64_t protocol, bool raw) {
    return net_open(socket_raw((int)family, (int)protocol, raw), REACTOR_READABLE | REACTOR_WRITABLE);
}

// An ICMP error waiting for the socket, taken: the bytes of the echo it is
// about, how many, into 'bytes'; its type and code in 'out', who sent it
// kept for net_address. -1 when there is none, -2 failing.
int64_t sable_net_icmp_error(int64_t handle, int64_t *out, uint8_t *bytes, int64_t size) {
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, NULL);
    if (source == NULL) {
        net_closed();
        return -2;
    }

    int type = 0, code = 0;
    int64_t got = socket_icmp_error(at, &type, &code, &net_last_address, bytes, size);
    io_end(source);
    out[0] = type;
    out[1] = code;
    return got >= 0 ? got : got == IO_WOULD_BLOCK ? -1 : -2;
}

// An echo sent by the system's own ICMP service (Windows), waited for on
// the caller's thread: false when it could not be sent. 'out' has the
// status and the round trip in milliseconds, and who answered: family,
// halves and scope.
bool sable_net_ping(int64_t family, int64_t high, int64_t low, int64_t scope, int64_t timeout, int64_t hops, bool dont_fragment,
    const uint8_t *bytes, int64_t size, int64_t *out) {
    PlatformAddress to, from;
    memset(&to, 0, sizeof to);
    memset(&from, 0, sizeof from);
    to.family = (int)family;
    to.high = (uint64_t)high;
    to.low = (uint64_t)low;
    to.scope = (uint32_t)scope;
    int64_t result[2] = { 0, 0 };
    if (!ping_echo(&to, (int)timeout, (int)hops, dont_fragment, bytes, size, result, &from)) {
        return false;
    }

    out[0] = result[0];
    out[1] = result[1];
    out[2] = from.family;
    out[3] = (int64_t)from.high;
    out[4] = (int64_t)from.low;
    out[5] = from.scope;
    return true;
}

// ---- Changes to the interfaces ---------------------------------------------------------

// A watch: what tells of a change (a socket's source, or an event's), and
// what the system has for it.
typedef struct ChangeWatch {
    int64_t source;
    PlatformSocket socket; // -1 where the system calls back instead
    void *watch;
} ChangeWatch;

// Changes watched from now on: the watch, or 0.
int64_t sable_net_changes_open(void) {
    ChangeWatch *watch = calloc(1, sizeof *watch);
    if (watch == NULL) {
        sable_trap("out of memory");
    }

    watch->socket = -1;
#ifdef _WIN32
    watch->source = io_event_open(true);
    watch->watch = watch->source == 0 ? NULL : changes_watch(watch->source);
    if (watch->watch == NULL) {
        io_source_close(watch->source);
        free(watch);
        return 0;
    }
#else
    PlatformSocket socket = changes_open();
    // Waiting for a change keeps no program going once its main has returned.
    watch->source = socket < 0 ? 0 : io_source_open(socket, SOURCE_WATCH, REACTOR_READABLE);
    if (watch->source == 0) {
        if (socket >= 0) {
            socket_close(socket);
            socket_fail_as(NET_ERROR_UNSUPPORTED);
        }

        free(watch);
        return 0;
    }

    watch->socket = socket;
#endif
    return (int64_t)(intptr_t)watch;
}

// What to wait for, for a change.
int64_t sable_net_changes_event(int64_t handle) {
    return ((ChangeWatch *)(intptr_t)handle)->source;
}

// What has come said to be seen: the next wait is for another.
void sable_net_changes_drain(int64_t handle) {
    ChangeWatch *watch = (ChangeWatch *)(intptr_t)handle;
#ifdef _WIN32
    io_event_reset(watch->source);
#else
    uint64_t ticks;
    PlatformSocket at;
    IoSource *source = io_begin(watch->source, &at, 0, &ticks);
    if (source != NULL) {
        changes_drain(at);
        io_would_block(source, 0, ticks);
        io_end(source);
    }
#endif
}

void sable_net_changes_close(int64_t handle) {
    ChangeWatch *watch = (ChangeWatch *)(intptr_t)handle;
    if (watch->watch != NULL) {
        changes_unwatch(watch->watch);
    }

    io_source_close(watch->source);
    if (watch->socket >= 0) {
        socket_close(watch->socket);
    }

    free(watch);
}

// ---- Connections and counters ----------------------------------------------------------

// The connections as they were when last asked for, by this thread.
static _Thread_local PlatformConnection *net_connections;

// How many there are of the protocol (6, 17), or -1; with 0, those listed
// are let go of.
int64_t sable_net_connections(int64_t protocol) {
    free(net_connections);
    net_connections = NULL;
    return protocol == 0 ? 0 : connections_list((int)protocol, &net_connections);
}

// A part of the row: 0 its state, 1 its process; 2 and 3 its local and
// remote addresses, kept for net_address, their family given.
int64_t sable_net_connection(int64_t row, int64_t part) {
    const PlatformConnection *at = &net_connections[row];
    switch (part) {
        case 0: return at->state;
        case 1: return at->process;
        case 2: net_last_address = at->local; return at->local.family;
        default: net_last_address = at->remote; return at->remote.family;
    }
}

bool sable_net_counters(int64_t protocol, int64_t *counters) {
    return protocol_counters((int)protocol, counters);
}
