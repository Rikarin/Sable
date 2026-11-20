// The Sable runtime: sockets — what Sable.IO's TcpListener and TcpStream are written over.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Sockets -----------------------------------------------------------------

// Every socket is nonblocking and watched by the reactor (tasks.c's
// IoSource), named by the source's handle. A call that would block gives
// what says so — net_status 1 — and the library waits for the socket to
// be ready (Task.waitForIO, a suspension: the worker runs other tasks
// meanwhile) and calls again. A call that fails gives net_status 2: the
// socket was closed, the other end went, or the system said no.
enum { NET_DONE = 0, NET_WOULD_BLOCK = 1, NET_FAILED = 2 };

static _Thread_local int64_t net_last_status;

int64_t sable_net_status(void) {
    return net_last_status;
}

static int64_t net_result(int64_t result) {
    net_last_status = result >= 0 ? NET_DONE : result == IO_WOULD_BLOCK ? NET_WOULD_BLOCK : NET_FAILED;
    return result;
}

// The host as a C string, in memory the caller frees.
static char *net_host(const SableString *host) {
    char *text = malloc((size_t)host->length + 1);
    if (text == NULL) {
        sable_trap("out of memory");
    }

    memcpy(text, host->bytes, (size_t)host->length);
    text[host->length] = '\0';
    return text;
}

// A listener on the host's address and port (0: any free one): its handle,
// or 0. Listening never waits.
int64_t sable_net_listen(const SableString *host, int64_t port) {
    char *name = net_host(host);
    int bound = 0;
    PlatformSocket socket = socket_listen(name, (int)port, &bound);
    free(name);
    if (socket == IO_FAILED) {
        net_last_status = NET_FAILED;
        return 0;
    }

    int64_t handle = io_source_open(socket, REACTOR_READABLE | REACTOR_WRITABLE);
    net_last_status = handle == 0 ? NET_FAILED : NET_DONE;
    if (handle != 0) {
        IoSource *source = source_of(handle);
        spin_lock(&source->lock);
        source->port = bound;
        spin_unlock(&source->lock);
    }

    return handle;
}

// The port a listener is bound to.
int64_t sable_net_port(int64_t handle) {
    IoSource *source = source_of(handle);
    return source == NULL ? 0 : source->port;
}

// A connection taken from the listener: its handle; or 0, with net_status
// saying whether it would block or failed.
int64_t sable_net_accept(int64_t handle) {
    IoSource *source = source_of(handle);
    if (source == NULL) {
        net_last_status = NET_FAILED;
        return 0;
    }

    uint64_t ticks = io_ticks(source, 0);
    PlatformSocket socket = socket_accept(__atomic_load_n(&source->socket, __ATOMIC_ACQUIRE));
    if (socket == IO_WOULD_BLOCK) {
        io_would_block(source, 0, ticks);
    }

    if (net_result(socket) < 0) {
        return 0;
    }

    int64_t accepted = io_source_open(socket, REACTOR_READABLE | REACTOR_WRITABLE);
    net_last_status = accepted == 0 ? NET_FAILED : NET_DONE;
    return accepted;
}

// A connection begun: its handle, writable once it is made; or 0 when it
// could not begin.
int64_t sable_net_connect(const SableString *host, int64_t port) {
    char *name = net_host(host);
    PlatformSocket socket = socket_connect(name, (int)port);
    free(name);
    if (socket == IO_FAILED) {
        net_last_status = NET_FAILED;
        return 0;
    }

    // Not writable until the connection is made: the reactor says when.
    int64_t handle = io_source_open(socket, REACTOR_READABLE);
    net_last_status = handle == 0 ? NET_FAILED : NET_DONE;
    return handle;
}

// Whether the connection begun was made, once it is writable.
bool sable_net_connected(int64_t handle) {
    IoSource *source = source_of(handle);
    return source != NULL && socket_connected(__atomic_load_n(&source->socket, __ATOMIC_ACQUIRE)) == 0;
}

// Up to 'most' bytes read: none at the end of what the other end sends; or
// none, with net_status saying whether it would block or failed.
SableArray *sable_net_read(int64_t handle, int64_t most, const SableMetadata *type) {
    IoSource *source = source_of(handle);
    if (source == NULL || most <= 0) {
        net_last_status = source == NULL ? NET_FAILED : NET_DONE;
        return NULL;
    }

    char buffer[65536];
    if (most > (int64_t)sizeof buffer) {
        most = sizeof buffer;
    }

    uint64_t ticks = io_ticks(source, 0);
    int64_t got = socket_read(__atomic_load_n(&source->socket, __ATOMIC_ACQUIRE), buffer, most);
    if (got == IO_WOULD_BLOCK) {
        io_would_block(source, 0, ticks);
    }

    if (net_result(got) <= 0) {
        return NULL;
    }

    SableArray *bytes = sable_alloc(type, (int64_t)sizeof(SableArray));
    bytes->count = bytes->capacity = got;
    bytes->elements = buffer_alloc(got);
    memcpy(bytes->elements, buffer, (size_t)got);
    return bytes;
}

// Some of the bytes written, how many; or 0, with net_status saying
// whether it would block or failed. A slice reaches the runtime as its
// buffer, where it starts, and its count.
int64_t sable_net_write(int64_t handle, const SableArray *buffer, int64_t start, int64_t count) {
    IoSource *source = source_of(handle);
    if (source == NULL) {
        net_last_status = NET_FAILED;
        return 0;
    }

    if (count == 0) {
        net_last_status = NET_DONE;
        return 0;
    }

    uint64_t ticks = io_ticks(source, 1);
    int64_t sent = socket_write(__atomic_load_n(&source->socket, __ATOMIC_ACQUIRE), slice_bytes(buffer, start), count);
    if (sent == IO_WOULD_BLOCK) {
        io_would_block(source, 1, ticks);
    }

    return net_result(sent) < 0 ? 0 : sent;
}

// No more written: the other end reads to its end.
void sable_net_shutdown(int64_t handle) {
    IoSource *source = source_of(handle);
    if (source != NULL) {
        socket_shutdown_write(__atomic_load_n(&source->socket, __ATOMIC_ACQUIRE));
    }
}

void sable_net_close(int64_t handle) {
    io_source_close(handle);
}
