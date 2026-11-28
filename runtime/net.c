// The Sable runtime: sockets — what Sable.Net's types are written over.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Sockets -----------------------------------------------------------------

// A child's streams (process.c) are read, written and closed by the same
// calls: pipes, watched by the reactor as sockets are.
//
// Every socket is nonblocking and watched by the reactor (tasks.c's
// IoSource), named by the source's handle. A call that would block gives
// what says so — net_status 1 — and the library waits for the socket to
// be ready (Task.waitForIO, a suspension: the worker runs other tasks
// meanwhile) and calls again. A call that fails gives net_status 2, and
// net_error why: NET_ERROR_CLOSED for a handle that names nothing.
enum { NET_DONE = 0, NET_WOULD_BLOCK = 1, NET_FAILED = 2 };

static _Thread_local int64_t net_last_status;

// The address the last call that has one to give gave, and whether the
// datagram with it was cut short: taken before the task suspends.
static _Thread_local PlatformAddress net_last_address;
static _Thread_local bool net_last_truncated;

int64_t sable_net_status(void) {
    return net_last_status;
}

int64_t sable_net_error(void) {
    return net_last_failure.kind;
}

int64_t sable_net_error_code(void) {
    return net_last_failure.code;
}

// A part of the address: 0 its family, 1 and 2 its high and low halves, 3
// its scope, 4 its port; 5, whether the datagram was cut short.
int64_t sable_net_address(int64_t part) {
    switch (part) {
        case 0: return net_last_address.family;
        case 1: return (int64_t)net_last_address.high;
        case 2: return (int64_t)net_last_address.low;
        case 3: return net_last_address.scope;
        case 4: return net_last_address.port;
        default: return net_last_truncated;
    }
}

// A Unix domain socket's name; "" for one with none.
SableString *sable_net_path(void) {
    return make_text(net_last_address.path, (int64_t)net_last_address.path_length);
}

static int64_t net_result(int64_t result) {
    net_last_status = result >= 0 ? NET_DONE : result == IO_WOULD_BLOCK ? NET_WOULD_BLOCK : NET_FAILED;
    return result;
}

static void net_closed(void) {
    net_last_status = NET_FAILED;
    net_last_failure.kind = NET_ERROR_CLOSED;
    net_last_failure.code = 0;
}

// The address the parts make; false for a Unix domain socket's name too
// long for any system.
static bool net_make_address(PlatformAddress *address, int64_t family, int64_t high, int64_t low, int64_t scope, int64_t port, const SableString *path) {
    memset(address, 0, sizeof *address);
    address->family = (int)family;
    address->high = (uint64_t)high;
    address->low = (uint64_t)low;
    address->scope = (uint32_t)scope;
    address->port = (int)port;
    if (family == NET_UNIX) {
        if (path->length > (int64_t)sizeof address->path) {
            net_last_status = NET_FAILED;
            net_last_failure.kind = NET_ERROR_INVALID;
            net_last_failure.code = 0;
            return false;
        }

        memcpy(address->path, path->bytes, (size_t)path->length);
        address->path_length = (size_t)path->length;
    }

    return true;
}

// The socket watched from now on: its handle, or 0.
static int64_t net_open(PlatformSocket socket, uint8_t ready) {
    if (socket == IO_FAILED) {
        net_last_status = NET_FAILED;
        return 0;
    }

    int64_t handle = io_source_open(socket, SOURCE_SOCKET, ready);
    net_last_status = NET_DONE;
    if (handle == 0) {
        net_last_status = NET_FAILED;
        net_last_failure.kind = NET_ERROR_OTHER;
        net_last_failure.code = 0;
    }

    return handle;
}

// A listener on the address (port 0: any free one): its handle, or 0.
// Listening never waits.
int64_t sable_net_listen(int64_t family, int64_t high, int64_t low, int64_t scope, int64_t port, const SableString *path, int64_t backlog, int64_t flags) {
    PlatformAddress address;
    if (!net_make_address(&address, family, high, low, scope, port, path)) {
        return 0;
    }

    return net_open(socket_listen(&address, (int)backlog, (int)flags), REACTOR_READABLE | REACTOR_WRITABLE);
}

// A connection taken from the listener: its handle, and its peer's address;
// or 0, with net_status saying whether it would block or failed.
int64_t sable_net_accept(int64_t handle) {
    uint64_t ticks;
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, &ticks);
    if (source == NULL) {
        net_closed();
        return 0;
    }

    PlatformSocket socket = socket_accept(at, &net_last_address);
    if (socket == IO_WOULD_BLOCK) {
        io_would_block(source, 0, ticks);
    }

    io_end(source);
    return net_result(socket) < 0 ? 0 : net_open(socket, REACTOR_READABLE | REACTOR_WRITABLE);
}

// A connection begun: its handle, writable once it is made; or 0 when it
// could not begin.
int64_t sable_net_connect(int64_t family, int64_t high, int64_t low, int64_t scope, int64_t port, const SableString *path) {
    PlatformAddress address;
    if (!net_make_address(&address, family, high, low, scope, port, path)) {
        return 0;
    }

    // Not writable until the connection is made: the reactor says when.
    return net_open(socket_connect(&address), REACTOR_READABLE);
}

// Whether the connection begun was made, once it is writable; net_status
// says whether it still may be.
bool sable_net_connected(int64_t handle) {
    uint64_t ticks;
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 1, &ticks);
    if (source == NULL) {
        net_closed();
        return false;
    }

    int result = socket_connected(at);
    if (result == IO_WOULD_BLOCK) {
        io_would_block(source, 1, ticks);
    }

    io_end(source);
    return net_result(result) >= 0;
}

// A datagram socket bound to the address: its handle, or 0.
int64_t sable_net_datagram(int64_t family, int64_t high, int64_t low, int64_t scope, int64_t port, const SableString *path, int64_t flags) {
    PlatformAddress address;
    if (!net_make_address(&address, family, high, low, scope, port, path)) {
        return 0;
    }

    return net_open(socket_datagram(&address, (int)flags), REACTOR_READABLE | REACTOR_WRITABLE);
}

bool sable_net_connect_datagram(int64_t handle, int64_t family, int64_t high, int64_t low, int64_t scope, int64_t port, const SableString *path) {
    PlatformAddress address;
    if (!net_make_address(&address, family, high, low, scope, port, path)) {
        return false;
    }

    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, NULL);
    if (source == NULL) {
        net_closed();
        return false;
    }

    int result = socket_connect_datagram(at, &address);
    io_end(source);
    return net_result(result) >= 0;
}

// The bytes as an array; or NULL, for none.
static SableArray *net_bytes(const char *buffer, int64_t count, const SableMetadata *type) {
    if (count <= 0) {
        return NULL;
    }

    SableArray *bytes = sable_alloc(type, (int64_t)sizeof(SableArray));
    bytes->count = bytes->capacity = count;
    bytes->elements = buffer_alloc(count);
    memcpy(bytes->elements, buffer, (size_t)count);
    return bytes;
}

// Up to 'most' bytes read: none at the end of what the other end sends; or
// none, with net_status saying whether it would block or failed.
SableArray *sable_net_read(int64_t handle, int64_t most, const SableMetadata *type) {
    uint64_t ticks;
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, &ticks);
    if (source == NULL) {
        net_closed();
        return NULL;
    }

    if (most <= 0) {
        io_end(source);
        net_last_status = NET_DONE;
        return NULL;
    }

    char buffer[65536];
    if (most > (int64_t)sizeof buffer) {
        most = sizeof buffer;
    }

    int64_t got = source->kind == SOURCE_PIPE ? pipe_read(at, buffer, most) : socket_read(at, buffer, most);
    if (got == IO_WOULD_BLOCK) {
        io_would_block(source, 0, ticks);
    }

    io_end(source);
    return net_bytes(buffer, net_result(got), type);
}

// Up to 'most' bytes read to 'into', how many: 0 at the end of what the
// other end sends; or 0, with net_status saying whether it would block or
// failed. The caller has made the bytes its own to write.
int64_t sable_net_read_into(int64_t handle, char *into, int64_t most) {
    uint64_t ticks;
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, &ticks);
    if (source == NULL) {
        net_closed();
        return 0;
    }

    if (most <= 0) {
        io_end(source);
        net_last_status = NET_DONE;
        return 0;
    }

    int64_t got = source->kind == SOURCE_PIPE ? pipe_read(at, into, most) : socket_read(at, into, most);
    if (got == IO_WOULD_BLOCK) {
        io_would_block(source, 0, ticks);
    }

    io_end(source);
    int64_t result = net_result(got);
    return result > 0 ? result : 0;
}

// Some of the bytes written, how many; or 0, with net_status saying
// whether it would block or failed. A slice reaches the runtime as its
// buffer, where it starts, and its count.
int64_t sable_net_write(int64_t handle, const SableArray *buffer, int64_t start, int64_t count) {
    uint64_t ticks;
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 1, &ticks);
    if (source == NULL) {
        net_closed();
        return 0;
    }

    if (count == 0) {
        io_end(source);
        net_last_status = NET_DONE;
        return 0;
    }

    const void *bytes = slice_bytes(buffer, start);
    int64_t sent = source->kind == SOURCE_PIPE ? pipe_write(at, bytes, count) : socket_write(at, bytes, count);
    if (sent == IO_WOULD_BLOCK) {
        io_would_block(source, 1, ticks);
    }

    io_end(source);
    return net_result(sent) < 0 ? 0 : sent;
}

// A datagram sent: how many bytes; or 0, with net_status saying how it went.
int64_t sable_net_send(int64_t handle, const SableArray *buffer, int64_t start, int64_t count, int64_t family, int64_t high, int64_t low, int64_t scope, int64_t port, const SableString *path) {
    PlatformAddress address;
    if (!net_make_address(&address, family, high, low, scope, port, path)) {
        return 0;
    }

    uint64_t ticks;
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 1, &ticks);
    if (source == NULL) {
        net_closed();
        return 0;
    }

    int64_t sent = socket_send_to(at, count == 0 ? (const void *)"" : slice_bytes(buffer, start), count, &address);
    if (sent == IO_WOULD_BLOCK) {
        io_would_block(source, 1, ticks);
    }

    io_end(source);
    return net_result(sent) < 0 ? 0 : sent;
}

// A datagram received, no more of it than 'most', and whom it is from; or
// none, with net_status saying how it went.
SableArray *sable_net_receive(int64_t handle, int64_t most, const SableMetadata *type) {
    uint64_t ticks;
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, &ticks);
    if (source == NULL) {
        net_closed();
        return NULL;
    }

    char buffer[65536];
    most = most < 0 ? 0 : most > (int64_t)sizeof buffer ? (int64_t)sizeof buffer : most;
    int64_t got = socket_receive(at, buffer, most, &net_last_address, &net_last_truncated);
    if (got == IO_WOULD_BLOCK) {
        io_would_block(source, 0, ticks);
    }

    io_end(source);
    return net_bytes(buffer, net_result(got), type);
}

// Its own address, or its peer's, kept for net_address.
bool sable_net_name(int64_t handle, bool peer) {
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, NULL);
    if (source == NULL) {
        net_closed();
        return false;
    }

    bool known = socket_name(at, peer, &net_last_address);
    io_end(source);
    net_last_status = known ? NET_DONE : NET_FAILED;
    return known;
}

bool sable_net_set_option(int64_t handle, int64_t option, int64_t value) {
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, NULL);
    if (source == NULL) {
        net_closed();
        return false;
    }

    bool done = socket_set_option(at, (int)option, value);
    io_end(source);
    net_last_status = done ? NET_DONE : NET_FAILED;
    return done;
}

// The option's value, net_status saying whether it was known.
int64_t sable_net_option(int64_t handle, int64_t option) {
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, NULL);
    if (source == NULL) {
        net_closed();
        return 0;
    }

    int64_t value = 0;
    bool done = socket_get_option(at, (int)option, &value);
    io_end(source);
    net_last_status = done ? NET_DONE : NET_FAILED;
    return value;
}

bool sable_net_membership(int64_t handle, bool join, int64_t family, int64_t high, int64_t low, int64_t index) {
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, NULL);
    if (source == NULL) {
        net_closed();
        return false;
    }

    PlatformAddress group;
    memset(&group, 0, sizeof group);
    group.family = (int)family;
    group.high = (uint64_t)high;
    group.low = (uint64_t)low;
    bool done = socket_membership(at, join, &group, (uint32_t)index);
    io_end(source);
    net_last_status = done ? NET_DONE : NET_FAILED;
    return done;
}

// No more written: the other end reads to its end. Or no more read.
void sable_net_shutdown(int64_t handle, bool writing) {
    PlatformSocket at;
    IoSource *source = io_begin(handle, &at, 0, NULL);
    if (source != NULL) {
        if (source->kind == SOURCE_SOCKET) {
            socket_shutdown(at, writing);
        }

        io_end(source);
    }
}

void sable_net_close(int64_t handle) {
    io_source_close(handle);
}

// ---- Looking names up -----------------------------------------------------------

// The system's resolver waits, and cannot be told to stop: a lookup runs on
// a thread of its own, and one whose caller has gone — cancelled, or out of
// time — is left to finish there, what it finds dropped. No more than so
// many run at once, and the others wait their turn.
enum { LOOKUPS_MOST = 32 };

typedef struct Lookup {
    struct Lookup *next;
    int64_t references; // the caller's, and the thread's
    int64_t event;      // told once it is done
    int family;
    bool done;          // under lookups_lock
    bool dropped;       // its caller has gone
    PlatformAddress *addresses;
    int count;          // or -1, and why
    int failure;
    char host[];
} Lookup;

static SpinLock lookups_lock;
static Lookup *lookups_head, *lookups_tail;
static int lookups_running;

static void lookup_release(Lookup *lookup) {
    if (__atomic_sub_fetch(&lookup->references, 1, __ATOMIC_ACQ_REL) == 0) {
        free(lookup->addresses);
        free(lookup);
    }
}

static void lookup_main(void *argument) {
    (void)argument;
    for (;;) {
        spin_lock(&lookups_lock);
        Lookup *lookup = lookups_head;
        if (lookup == NULL) {
            lookups_running--;
            spin_unlock(&lookups_lock);
            return;
        }

        lookups_head = lookup->next;
        if (lookups_head == NULL) {
            lookups_tail = NULL;
        }

        bool dropped = lookup->dropped;
        spin_unlock(&lookups_lock);
        if (!dropped) {
            PlatformAddress *addresses = NULL;
            int failure = 0;
            int count = host_resolve(lookup->host, lookup->family, &addresses, &failure);
            spin_lock(&lookups_lock);
            lookup->addresses = addresses;
            lookup->count = count;
            lookup->failure = failure;
            lookup->done = true;
            spin_unlock(&lookups_lock);
            // Closed since, its event names nothing.
            io_event(lookup->event);
        }

        lookup_release(lookup);
    }
}

// No thread could be started: what waits is another's that runs, or, when
// none does, has failed.
static void lookups_without_thread(void) {
    spin_lock(&lookups_lock);
    Lookup *lookup = --lookups_running > 0 ? NULL : lookups_head;
    if (lookup != NULL) {
        lookups_head = lookups_tail = NULL;
    }

    for (Lookup *at = lookup; at != NULL; at = at->next) {
        at->count = -1;
        at->failure = NET_ERROR_TRY_LATER;
        at->done = true;
    }

    spin_unlock(&lookups_lock);
    while (lookup != NULL) {
        Lookup *next = lookup->next;
        io_event(lookup->event);
        lookup_release(lookup);
        lookup = next;
    }
}

// The host's addresses looked for, of the family (0: any): the lookup, to
// be ended once with net_resolve_end; or 0.
int64_t sable_net_resolve(const SableString *host, int64_t family) {
    Lookup *lookup = calloc(1, sizeof *lookup + (size_t)host->length + 1);
    if (lookup == NULL) {
        sable_trap("out of memory");
    }

    memcpy(lookup->host, host->bytes, (size_t)host->length);
    lookup->family = (int)family;
    lookup->references = 2;
    lookup->event = io_event_open(false);
    if (lookup->event == 0) {
        free(lookup);
        net_last_status = NET_FAILED;
        net_last_failure.kind = NET_ERROR_UNSUPPORTED;
        net_last_failure.code = 0;
        return 0;
    }

    spin_lock(&lookups_lock);
    if (lookups_tail != NULL) {
        lookups_tail->next = lookup;
    } else {
        lookups_head = lookup;
    }

    lookups_tail = lookup;
    bool more = lookups_running < LOOKUPS_MOST;
    lookups_running += more ? 1 : 0;
    spin_unlock(&lookups_lock);
    if (more && !thread_start(lookup_main, NULL)) {
        lookups_without_thread();
    }

    net_last_status = NET_DONE;
    return (int64_t)(intptr_t)lookup;
}

// What to wait for, for it to be done.
int64_t sable_net_resolve_event(int64_t handle) {
    return ((Lookup *)(intptr_t)handle)->event;
}

// How many addresses it found, with net_status as a socket's calls have it.
int64_t sable_net_resolved(int64_t handle) {
    Lookup *lookup = (Lookup *)(intptr_t)handle;
    spin_lock(&lookups_lock);
    bool done = lookup->done;
    spin_unlock(&lookups_lock);
    net_last_status = !done ? NET_WOULD_BLOCK : lookup->count < 0 ? NET_FAILED : NET_DONE;
    if (done && lookup->count < 0) {
        net_last_failure.kind = lookup->failure;
        net_last_failure.code = 0;
    }

    return done && lookup->count > 0 ? lookup->count : 0;
}

// One of the addresses found, kept for net_address.
void sable_net_resolved_take(int64_t handle, int64_t index) {
    net_last_address = ((Lookup *)(intptr_t)handle)->addresses[index];
}

void sable_net_resolve_end(int64_t handle) {
    Lookup *lookup = (Lookup *)(intptr_t)handle;
    spin_lock(&lookups_lock);
    lookup->dropped = true;
    spin_unlock(&lookups_lock);
    io_source_close(lookup->event);
    lookup_release(lookup);
}

// ---- Interfaces -----------------------------------------------------------------

// The interfaces as they were when last asked for, by this thread.
static _Thread_local PlatformInterface *net_interfaces;
static _Thread_local int net_interface_count;

// How many rows there are, an interface's for each of its addresses; or -1.
// Until it is asked for again with false, which frees them.
int64_t sable_net_interfaces(bool list) {
    free(net_interfaces);
    net_interfaces = NULL;
    net_interface_count = 0;
    if (list) {
        net_interface_count = interfaces_list(&net_interfaces);
        net_last_status = net_interface_count < 0 ? NET_FAILED : NET_DONE;
    }

    return net_interface_count < 0 ? -1 : net_interface_count;
}

SableString *sable_net_interface_name(int64_t row) {
    return make_string(net_interfaces[row].name, (int64_t)strlen(net_interfaces[row].name));
}

// A part of the row: 0 the interface's index, 1 its flags, 2 its MTU, 3 its
// hardware address, 4 its address's prefix length; 5, its address is kept
// for net_address, and its family given.
int64_t sable_net_interface(int64_t row, int64_t part) {
    const PlatformInterface *at = &net_interfaces[row];
    switch (part) {
        case 0: return at->index;
        case 1: return at->flags;
        case 2: return at->mtu;
        case 3: return at->hardware;
        case 4: return at->prefix;
        default: net_last_address = at->address; return at->address.family;
    }
}

int64_t sable_net_interface_index(const SableString *name) {
    char text[256];
    if (name->length == 0 || name->length >= (int64_t)sizeof text || memchr(name->bytes, '\0', (size_t)name->length) != NULL) {
        return 0;
    }

    memcpy(text, name->bytes, (size_t)name->length);
    text[name->length] = '\0';
    return interface_index(text);
}
