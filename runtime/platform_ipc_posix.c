// The Sable runtime's pipes, local streams, shared memory, serial ports,
// ping and network statistics for POSIX systems: what platform.h says,
// under "Pipes, local streams, shared memory and serial ports" and "Ping,
// raw sockets, changes and statistics". Part of platform_posix.c, after
// platform_process_posix.c, whose pipes these are.

#include <semaphore.h>
#include <sys/file.h>
#include <termios.h>
#if defined(__linux__)
#include <linux/errqueue.h>
#include <linux/netlink.h>
#include <linux/rtnetlink.h>
#else
#include <net/route.h>
#include <netinet/ip.h>
#include <netinet/tcp_var.h>
#include <netinet/udp.h>
#include <netinet/udp_var.h>
#endif

// Why the last call failed, as net_last_failure has it: a name another
// has already, and the rest as a socket's.
static int ipc_fail_with(int code) {
    net_last_failure.code = code;
    net_last_failure.kind = code == EEXIST ? NET_ERROR_EXISTS : code == ENXIO || code == ENODEV ? NET_ERROR_NOT_FOUND : socket_error_kind(code);
    return IO_FAILED;
}

static int ipc_fail(void) {
    return ipc_fail_with(errno);
}

// ---- Pipes -----------------------------------------------------------------

static bool os_pipe(int children, intptr_t ends[2]) {
    int fds[2];
    pthread_mutex_lock(&spawn_lock);
    bool made = make_pipe(fds);
    int code = errno;
    pthread_mutex_unlock(&spawn_lock);
    if (!made) {
        ipc_fail_with(code);
        return false;
    }

    // A child's end blocks, as a program's streams do; this program's does not.
    for (int i = 0; i < 2; i++) {
        if ((children & (1 << i)) == 0 && !make_nonblocking(fds[i])) {
            ipc_fail();
            close(fds[0]);
            close(fds[1]);
            return false;
        }

        ends[i] = fds[i];
    }

    return true;
}

static void handle_close(PlatformHandle handle) {
    if (handle >= 0) {
        close((int)handle);
    }
}

static bool fifo_make(const char *path, int permissions) {
    if (mkfifo(path, (mode_t)permissions) != 0) {
        ipc_fail();
        return false;
    }

    return true;
}

static PlatformPipe fifo_open(const char *path, bool writes) {
    int fd;
    do {
        fd = open(path, (writes ? O_WRONLY : O_RDONLY) | O_NONBLOCK | O_CLOEXEC);
    } while (fd < 0 && errno == EINTR);
    if (fd < 0) {
        return errno == ENXIO ? IO_WOULD_BLOCK : ipc_fail();
    }

    struct stat info;
    if (fstat(fd, &info) != 0 || !S_ISFIFO(info.st_mode) || !make_nonblocking(fd)) {
        close(fd);
        return ipc_fail_with(EINVAL);
    }

    return fd;
}

// ---- Local streams -------------------------------------------------------------

// A directory of this user's: there, a directory, its own, and closed to
// everyone else.
static bool is_own_directory(const char *path) {
    struct stat info;
    return lstat(path, &info) == 0 && S_ISDIR(info.st_mode) && info.st_uid == getuid() && (info.st_mode & 077) == 0;
}

static char *local_directory(bool any) {
    if (any) {
        return strdup("/tmp");
    }

    const char *runtime = getenv("XDG_RUNTIME_DIR");
    if (runtime != NULL && runtime[0] == '/' && is_own_directory(runtime)) {
        return strdup(runtime);
    }

    char path[64];
    snprintf(path, sizeof path, "/tmp/sable-%lu", (unsigned long)getuid());
    if (mkdir(path, 0700) != 0 && errno != EEXIST) {
        ipc_fail();
        return NULL;
    }

    // Made by another user first, or opened to others: not used.
    if (!is_own_directory(path)) {
        ipc_fail_with(EACCES);
        return NULL;
    }

    return strdup(path);
}

// Local streams are Unix domain sockets here (Sable.Net's): no named pipes.
static PlatformPipe local_listen(const char *name, int flags, bool first) {
    (void)name;
    (void)flags;
    (void)first;
    return socket_fail_as(NET_ERROR_UNSUPPORTED);
}

static int local_accept(PlatformPipe pipe) {
    (void)pipe;
    return socket_fail_as(NET_ERROR_UNSUPPORTED);
}

static PlatformPipe local_writer(PlatformPipe pipe) {
    (void)pipe;
    return socket_fail_as(NET_ERROR_UNSUPPORTED);
}

static PlatformPipe local_connect(const char *name, int flags) {
    (void)name;
    (void)flags;
    return socket_fail_as(NET_ERROR_UNSUPPORTED);
}

static int64_t local_peer_process(PlatformPipe pipe, bool server) {
    (void)pipe;
    (void)server;
    return -1;
}

// ---- Shared memory and semaphores -----------------------------------------------

// The name as shm_open and sem_open take it: '/' and the name, whose length
// the library has checked (macOS takes 31 bytes at most).
static void ipc_object_name(const char *name, char path[64]) {
    snprintf(path, 64, "/%s", name);
}

static PlatformHandle shared_open(const char *name, int64_t size, bool create, int64_t *actual) {
    char path[64];
    ipc_object_name(name, path);
    int fd;
    do {
        fd = shm_open(path, O_RDWR | (create ? O_CREAT | O_EXCL : 0), 0600);
    } while (fd < 0 && errno == EINTR);
    if (fd < 0) {
        return ipc_fail();
    }

    fcntl(fd, F_SETFD, FD_CLOEXEC);
    struct stat info;
    if ((create && ftruncate(fd, (off_t)size) != 0) || fstat(fd, &info) != 0) {
        int code = errno;
        close(fd);
        if (create) {
            shm_unlink(path);
        }

        return ipc_fail_with(code);
    }

    // Made, it is the size asked for, though macOS gives it whole pages.
    *actual = create ? size : (int64_t)info.st_size;
    return fd;
}

static bool shared_remove(const char *name) {
    char path[64];
    ipc_object_name(name, path);
    if (shm_unlink(path) != 0) {
        ipc_fail();
        return false;
    }

    return true;
}

static PlatformHandle semaphore_open(const char *name, int64_t count, bool create) {
    if (count < 0 || count > SEM_VALUE_MAX) {
        return ipc_fail_with(EINVAL);
    }

    char path[64];
    ipc_object_name(name, path);
    sem_t *semaphore = create ? sem_open(path, O_CREAT | O_EXCL, 0600, (unsigned)count) : sem_open(path, 0);
    if (semaphore == SEM_FAILED) {
        return ipc_fail();
    }

    return (PlatformHandle)semaphore;
}

static int semaphore_try(PlatformHandle semaphore) {
    for (;;) {
        if (sem_trywait((sem_t *)semaphore) == 0) {
            return 1;
        }

        if (errno == EAGAIN) {
            return 0;
        }

        if (errno != EINTR) {
            ipc_fail();
            return -1;
        }
    }
}

static bool semaphore_release(PlatformHandle semaphore, int64_t count) {
    for (int64_t i = 0; i < count; i++) {
        if (sem_post((sem_t *)semaphore) != 0) {
            ipc_fail();
            return false;
        }
    }

    return true;
}

static void semaphore_close(PlatformHandle semaphore) {
    sem_close((sem_t *)semaphore);
}

static bool semaphore_remove(const char *name) {
    char path[64];
    ipc_object_name(name, path);
    if (sem_unlink(path) != 0) {
        ipc_fail();
        return false;
    }

    return true;
}

// flock's lock is the open file's: another opening of it, in this process
// or another, waits for it; and it goes with the process.
static PlatformHandle lock_try(const char *path) {
    int fd;
    do {
        fd = open(path, O_RDWR | O_CREAT | O_CLOEXEC, 0600);
    } while (fd < 0 && errno == EINTR);
    if (fd < 0) {
        return ipc_fail();
    }

    int result;
    do {
        result = flock(fd, LOCK_EX | LOCK_NB);
    } while (result != 0 && errno == EINTR);
    if (result != 0) {
        int code = errno;
        close(fd);
        return code == EWOULDBLOCK ? IO_WOULD_BLOCK : ipc_fail_with(code);
    }

    return fd;
}

// ---- Serial ports --------------------------------------------------------------

#if !defined(__linux__) && !defined(IOSSIOSPEED)
#define IOSSIOSPEED _IOW('T', 2, speed_t)
#endif

#if defined(__linux__)
// A rate as termios names it; 0 for one it has no name for.
static speed_t serial_speed(int64_t baud) {
    static const struct {
        int64_t baud;
        speed_t speed;
    } speeds[] = {
        { 50, B50 }, { 75, B75 }, { 110, B110 }, { 134, B134 }, { 150, B150 }, { 200, B200 }, { 300, B300 }, { 600, B600 },
        { 1200, B1200 }, { 1800, B1800 }, { 2400, B2400 }, { 4800, B4800 }, { 9600, B9600 }, { 19200, B19200 },
        { 38400, B38400 }, { 57600, B57600 }, { 115200, B115200 }, { 230400, B230400 },
#ifdef B460800
        { 460800, B460800 }, { 500000, B500000 }, { 576000, B576000 }, { 921600, B921600 }, { 1000000, B1000000 },
        { 1152000, B1152000 }, { 1500000, B1500000 }, { 2000000, B2000000 }, { 2500000, B2500000 }, { 3000000, B3000000 },
        { 3500000, B3500000 }, { 4000000, B4000000 },
#endif
    };
    for (size_t i = 0; i < sizeof speeds / sizeof *speeds; i++) {
        if (speeds[i].baud == baud) {
            return speeds[i].speed;
        }
    }

    return 0;
}
#endif

static bool serial_configure(PlatformPipe port, const SerialSettings *settings) {
    int fd = (int)port;
    struct termios mode;
    if (tcgetattr(fd, &mode) != 0) {
        ipc_fail();
        return false;
    }

    cfmakeraw(&mode);
    mode.c_cflag |= CLOCAL | CREAD;
    mode.c_cflag &= ~(tcflag_t)(CSIZE | PARENB | PARODD | CSTOPB | CRTSCTS);
#ifdef CMSPAR
    mode.c_cflag &= ~(tcflag_t)CMSPAR;
#endif
    mode.c_iflag &= ~(tcflag_t)(IXON | IXOFF | IXANY | INPCK);
    switch (settings->data_bits) {
        case 5: mode.c_cflag |= CS5; break;
        case 6: mode.c_cflag |= CS6; break;
        case 7: mode.c_cflag |= CS7; break;
        case 8: mode.c_cflag |= CS8; break;
        default: ipc_fail_with(EINVAL); return false;
    }

    switch (settings->parity) {
        case 0: break;
        case 1: mode.c_cflag |= PARENB | PARODD; mode.c_iflag |= INPCK; break;
        case 2: mode.c_cflag |= PARENB; mode.c_iflag |= INPCK; break;
#ifdef CMSPAR
        case 3: mode.c_cflag |= PARENB | PARODD | CMSPAR; break;
        case 4: mode.c_cflag |= PARENB | CMSPAR; break;
#endif
        default: ipc_fail_with(EINVAL); return false;
    }

    if (settings->stop_bits == 2) {
        mode.c_cflag |= CSTOPB;
    } else if (settings->stop_bits != 1) {
        ipc_fail_with(EINVAL);
        return false;
    }

    if (settings->flow == 1) {
        mode.c_cflag |= CRTSCTS;
    } else if (settings->flow == 2) {
        mode.c_iflag |= IXON | IXOFF;
    }

    // A read gives what has come as soon as there is a byte.
    mode.c_cc[VMIN] = 1;
    mode.c_cc[VTIME] = 0;
#if defined(__linux__)
    speed_t speed = serial_speed(settings->baud);
    if (speed == 0) {
        ipc_fail_with(EINVAL);
        return false;
    }

    cfsetispeed(&mode, speed);
    cfsetospeed(&mode, speed);
    if (tcsetattr(fd, TCSANOW, &mode) != 0) {
        ipc_fail();
        return false;
    }
#else
    // macOS's speeds are the rates themselves; one its table lacks is set
    // by IOSSIOSPEED, after the rest.
    if (settings->baud <= 0 || settings->baud > UINT32_MAX) {
        ipc_fail_with(EINVAL);
        return false;
    }

    cfsetspeed(&mode, (speed_t)settings->baud);
    if (tcsetattr(fd, TCSANOW, &mode) != 0) {
        speed_t speed = (speed_t)settings->baud;
        cfsetspeed(&mode, 9600);
        if (tcsetattr(fd, TCSANOW, &mode) != 0 || ioctl(fd, IOSSIOSPEED, &speed) != 0) {
            ipc_fail();
            return false;
        }
    }
#endif
    return true;
}

static bool serial_open(const char *name, const SerialSettings *settings, PlatformPipe ends[2]) {
    int fd;
    do {
        fd = open(name, O_RDWR | O_NOCTTY | O_NONBLOCK | O_CLOEXEC);
    } while (fd < 0 && errno == EINTR);
    if (fd < 0) {
        ipc_fail();
        return false;
    }

    if (!isatty(fd)) {
        close(fd);
        ipc_fail_with(ENOTTY);
        net_last_failure.kind = NET_ERROR_INVALID;
        return false;
    }

    // No other program opens it while this one has it: a hint, which a
    // pseudo-terminal may not take.
    ioctl(fd, TIOCEXCL);
    if (!make_nonblocking(fd) || !serial_configure(fd, settings)) {
        NetFailure failure = net_last_failure;
        close(fd);
        net_last_failure = failure;
        return false;
    }

    ends[0] = fd;
    ends[1] = PIPE_NONE;
    return true;
}

static int serial_lines(PlatformPipe port) {
    int bits = 0;
    if (ioctl((int)port, TIOCMGET, &bits) != 0) {
        ipc_fail();
        return -1;
    }

    return (bits & TIOCM_DTR ? SERIAL_DTR : 0) | (bits & TIOCM_RTS ? SERIAL_RTS : 0) | (bits & TIOCM_CTS ? SERIAL_CTS : 0)
           | (bits & TIOCM_DSR ? SERIAL_DSR : 0) | (bits & TIOCM_RI ? SERIAL_RING : 0) | (bits & TIOCM_CD ? SERIAL_CARRIER : 0);
}

static bool serial_set_line(PlatformPipe port, int line, bool on) {
    int bits = line == SERIAL_DTR ? TIOCM_DTR : line == SERIAL_RTS ? TIOCM_RTS : 0;
    if (bits == 0 || ioctl((int)port, on ? TIOCMBIS : TIOCMBIC, &bits) != 0) {
        bits == 0 ? ipc_fail_with(EINVAL) : ipc_fail();
        return false;
    }

    return true;
}

static bool serial_break(PlatformPipe port, bool on) {
    if (ioctl((int)port, on ? TIOCSBRK : TIOCCBRK) != 0) {
        ipc_fail();
        return false;
    }

    return true;
}

static bool serial_discard(PlatformPipe port, bool input, bool output) {
    if ((input || output) && tcflush((int)port, input && output ? TCIOFLUSH : input ? TCIFLUSH : TCOFLUSH) != 0) {
        ipc_fail();
        return false;
    }

    return true;
}

static int64_t serial_queued(PlatformPipe port, bool output) {
    int count = 0;
    if (ioctl((int)port, output ? TIOCOUTQ : FIONREAD, &count) != 0) {
        ipc_fail();
        return -1;
    }

    return count;
}

static char *serial_names(void) {
    return NULL;
}

// ---- Ping and raw sockets ---------------------------------------------------------

static PlatformSocket socket_raw(int family, int protocol, bool raw) {
    int fd = socket(family == NET_IPV6 ? AF_INET6 : AF_INET, (raw ? SOCK_RAW : SOCK_DGRAM) | SOCKET_CLOEXEC, protocol);
    if (fd < 0) {
        return socket_fail();
    }

    if (!socket_prepare(fd)) {
        return socket_give_up(fd);
    }

#if defined(__linux__)
    // Errors that come back for what a ping socket sends (time exceeded,
    // unreachable) wait in its error queue, where socket_icmp_error looks.
    if (!raw) {
        int one = 1;
        setsockopt(fd, family == NET_IPV6 ? IPPROTO_IPV6 : IPPROTO_IP, family == NET_IPV6 ? IPV6_RECVERR : IP_RECVERR, &one, sizeof one);
    }
#endif
    return fd;
}

static int64_t socket_icmp_error(PlatformSocket socket, int *type, int *code, PlatformAddress *from, void *bytes, int64_t size) {
#if defined(__linux__)
    char control[512];
    struct sockaddr_storage address;
    struct iovec part = { .iov_base = bytes, .iov_len = (size_t)size };
    struct msghdr message = { 0 };
    message.msg_name = &address;
    message.msg_namelen = sizeof address;
    message.msg_iov = &part;
    message.msg_iovlen = 1;
    message.msg_control = control;
    message.msg_controllen = sizeof control;
    ssize_t got;
    do {
        got = recvmsg((int)socket, &message, MSG_ERRQUEUE | MSG_DONTWAIT);
    } while (got < 0 && errno == EINTR);
    if (got < 0) {
        return socket_blocks() ? IO_WOULD_BLOCK : socket_fail();
    }

    for (struct cmsghdr *at = CMSG_FIRSTHDR(&message); at != NULL; at = CMSG_NXTHDR(&message, at)) {
        if ((at->cmsg_level == SOL_IP && at->cmsg_type == IP_RECVERR) || (at->cmsg_level == SOL_IPV6 && at->cmsg_type == IPV6_RECVERR)) {
            struct sock_extended_err *error = (struct sock_extended_err *)CMSG_DATA(at);
            if (error->ee_origin != SO_EE_ORIGIN_ICMP && error->ee_origin != SO_EE_ORIGIN_ICMP6) {
                continue;
            }

            *type = error->ee_type;
            *code = error->ee_code;
            struct sockaddr *offender = SO_EE_OFFENDER(error);
            struct sockaddr_storage copy;
            memset(&copy, 0, sizeof copy);
            memcpy(&copy, offender, offender->sa_family == AF_INET6 ? sizeof(struct sockaddr_in6) : sizeof(struct sockaddr_in));
            socket_address_from(&copy, sizeof copy, from);
            return got;
        }
    }

    return IO_WOULD_BLOCK;
#else
    // Errors come to the socket as messages of their own.
    (void)socket;
    (void)type;
    (void)code;
    (void)from;
    (void)bytes;
    (void)size;
    return IO_WOULD_BLOCK;
#endif
}

static bool ping_echo(const PlatformAddress *to, int timeout, int hops, bool dont_fragment, const void *bytes, int64_t size, int64_t out[2], PlatformAddress *from) {
    (void)to;
    (void)timeout;
    (void)hops;
    (void)dont_fragment;
    (void)bytes;
    (void)size;
    (void)out;
    (void)from;
    socket_fail_as(NET_ERROR_UNSUPPORTED);
    return false;
}

// ---- Changes to the interfaces ----------------------------------------------------

static PlatformSocket changes_open(void) {
#if defined(__linux__)
    int fd = socket(AF_NETLINK, SOCK_RAW | SOCK_CLOEXEC, NETLINK_ROUTE);
    if (fd < 0) {
        return socket_fail();
    }

    struct sockaddr_nl at;
    memset(&at, 0, sizeof at);
    at.nl_family = AF_NETLINK;
    at.nl_groups = RTMGRP_LINK | RTMGRP_IPV4_IFADDR | RTMGRP_IPV6_IFADDR;
    if (bind(fd, (struct sockaddr *)&at, sizeof at) != 0) {
        return socket_give_up(fd);
    }
#else
    int fd = socket(PF_ROUTE, SOCK_RAW, AF_UNSPEC);
    if (fd < 0) {
        return socket_fail();
    }
#endif
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0 || fcntl(fd, F_SETFL, flags | O_NONBLOCK) != 0 || fcntl(fd, F_SETFD, FD_CLOEXEC) != 0) {
        return socket_give_up(fd);
    }

    return fd;
}

// What came is no matter: the interfaces are listed again.
static void changes_drain(PlatformSocket socket) {
    char buffer[8192];
    for (;;) {
        ssize_t got = recv((int)socket, buffer, sizeof buffer, 0);
        if (got < 0 && errno == EINTR) {
            continue;
        }

        // Linux's queue overflowed: what was lost is listed again too.
        if (got <= 0 && !(got < 0 && errno == ENOBUFS)) {
            return;
        }
    }
}

static void *changes_watch(int64_t event) {
    (void)event;
    return NULL;
}

static void changes_unwatch(void *watch) {
    (void)watch;
}

// ---- Connections and counters ------------------------------------------------------

// Sable.Net's TcpState: 1 closed, 2 listening, 3 SYN sent, 4 SYN received,
// 5 established, 6 FIN wait 1, 7 FIN wait 2, 8 close wait, 9 closing, 10
// last ACK, 11 time wait.
static int connections_add(PlatformConnection **rows, int *count, int *capacity) {
    if (*count == *capacity) {
        *capacity = *capacity == 0 ? 64 : *capacity * 2;
        PlatformConnection *grown = realloc(*rows, (size_t)*capacity * sizeof **rows);
        if (grown == NULL) {
            sable_trap("out of memory");
        }

        *rows = grown;
    }

    memset(&(*rows)[*count], 0, sizeof **rows);
    return (*count)++;
}

#if defined(__linux__)
// An address as /proc/net writes it: the words of it in hexadecimal, each
// as the machine holds it, and the port.
static bool proc_address(const char *text, bool six, PlatformAddress *address) {
    unsigned int words[4] = { 0 }, port = 0;
    unsigned char bytes[16];
    int n = six ? sscanf(text, "%8x%8x%8x%8x:%x", &words[0], &words[1], &words[2], &words[3], &port) : sscanf(text, "%8x:%x", &words[0], &port);
    if (n != (six ? 5 : 2)) {
        return false;
    }

    memcpy(bytes, words, six ? 16 : 4);
    address->family = six ? NET_IPV6 : NET_IPV4;
    address->port = (int)port;
    for (int i = 0; i < (six ? 8 : 4); i++) {
        address->low = address->low << 8 | bytes[(six ? 8 : 0) + i];
        if (six) {
            address->high = address->high << 8 | bytes[i];
        }
    }

    return true;
}

static int connections_list(int protocol, PlatformConnection **rows) {
    static const int states[] = { 0, 5, 3, 4, 6, 7, 11, 1, 8, 10, 2, 9 };
    *rows = NULL;
    int count = 0, capacity = 0;
    for (int six = 0; six < 2; six++) {
        char path[32];
        snprintf(path, sizeof path, "/proc/net/%s%s", protocol == 6 ? "tcp" : "udp", six ? "6" : "");
        FILE *file = fopen(path, "r");
        if (file == NULL) {
            continue;
        }

        char line[512];
        bool first = true;
        while (fgets(line, sizeof line, file) != NULL) {
            char local[64], remote[64];
            unsigned int state = 0;
            if (first || sscanf(line, " %*d: %63s %63s %x", local, remote, &state) != 3) {
                first = false;
                continue;
            }

            int at = connections_add(rows, &count, &capacity);
            PlatformConnection *row = &(*rows)[at];
            row->process = -1;
            row->state = protocol == 6 && state < sizeof states / sizeof *states ? states[state] : 0;
            if (!proc_address(local, six, &row->local) || !proc_address(remote, six, &row->remote)) {
                count--;
            }
        }

        fclose(file);
    }

    return count;
}

// The value under the name in /proc/net/snmp's lines of the protocol: a
// line of names, then one of values.
static bool protocol_counters(int protocol, int64_t counters[8]) {
    static const char *const tcp[] = { "ActiveOpens", "PassiveOpens", "AttemptFails", "CurrEstab", "InSegs", "OutSegs", "RetransSegs", "OutRsts" };
    static const char *const udp[] = { "InDatagrams", "OutDatagrams", "NoPorts", "InErrors", NULL, NULL, NULL, NULL };
    const char *const *wanted = protocol == 6 ? tcp : udp;
    const char *prefix = protocol == 6 ? "Tcp:" : "Udp:";
    FILE *file = fopen("/proc/net/snmp", "r");
    if (file == NULL) {
        ipc_fail();
        return false;
    }

    char names[2048], values[2048];
    bool found = false;
    while (!found && fgets(names, sizeof names, file) != NULL) {
        if (strncmp(names, prefix, 4) == 0 && fgets(values, sizeof values, file) != NULL) {
            found = true;
        }
    }

    fclose(file);
    for (int i = 0; i < 8; i++) {
        counters[i] = -1;
    }

    if (!found) {
        ipc_fail_with(ENOENT);
        return false;
    }

    char *name_state = NULL, *value_state = NULL;
    char *name = strtok_r(names + 4, " \n", &name_state);
    char *value = strtok_r(values + 4, " \n", &value_state);
    while (name != NULL && value != NULL) {
        for (int i = 0; i < 8; i++) {
            if (wanted[i] != NULL && strcmp(wanted[i], name) == 0) {
                counters[i] = strtoll(value, NULL, 10);
            }
        }

        name = strtok_r(NULL, " \n", &name_state);
        value = strtok_r(NULL, " \n", &value_state);
    }

    return true;
}
#else
// The system's own list (netstat's): records of kinds, each padded to 8
// bytes, an internet PCB's before its socket's and its TCP's. Their layout
// is the kernel's (xinpcb_n, xtcpcb_n in XNU's in_pcb.h and tcp_var.h),
// which the SDK does not publish. Without an entitlement it holds this
// process's sockets alone.
enum { XSO_INPCB = 0x10, XSO_TCPCB = 0x20 };

static void pcb_address(const unsigned char *at, bool six, int port, PlatformAddress *address) {
    address->family = six ? NET_IPV6 : NET_IPV4;
    address->port = port;
    for (int i = 0; i < 8; i++) {
        if (six) {
            address->high = address->high << 8 | at[i];
            address->low = address->low << 8 | at[8 + i];
        } else if (i < 4) {
            address->low = address->low << 8 | at[12 + i];
        }
    }
}

static int connections_list(int protocol, PlatformConnection **rows) {
    static const int states[] = { 1, 2, 3, 4, 5, 8, 6, 9, 10, 7, 11 };
    *rows = NULL;
    const char *name = protocol == 6 ? "net.inet.tcp.pcblist_n" : "net.inet.udp.pcblist_n";
    size_t length = 0;
    if (sysctlbyname(name, NULL, &length, NULL, 0) != 0) {
        return ipc_fail();
    }

    length += length / 4 + 4096;
    unsigned char *list = malloc(length);
    if (list == NULL) {
        sable_trap("out of memory");
    }

    if (sysctlbyname(name, list, &length, NULL, 0) != 0) {
        free(list);
        return ipc_fail();
    }

    int count = 0, capacity = 0, pending = -1;
    size_t at = length >= 4 ? ((*(uint32_t *)list + 7) & ~(size_t)7) : length;
    while (at + 24 <= length) {
        uint32_t record, kind;
        memcpy(&record, list + at, 4);
        memcpy(&kind, list + at + 4, 4);
        if (record <= 24 || at + record > length) {
            break;
        }

        // xinpcb_n: its 64-bit fields aligned to 4 bytes, the ports at 16,
        // inp_vflag at 44, the foreign address at 48 and the local at 64.
        if (kind == XSO_INPCB && record >= 80) {
            uint16_t ports[2];
            memcpy(ports, list + at + 16, 4);
            bool six = (list[at + 44] & 0x2) != 0; // INP_IPV6
            pending = connections_add(rows, &count, &capacity);
            PlatformConnection *row = &(*rows)[pending];
            row->process = -1;
            pcb_address(list + at + 64, six, ntohs(ports[1]), &row->local);
            pcb_address(list + at + 48, six, ntohs(ports[0]), &row->remote);
        // xtcpcb_n: t_state at 36.
        } else if (kind == XSO_TCPCB && pending >= 0 && record >= 40) {
            int32_t state;
            memcpy(&state, list + at + 36, 4);
            (*rows)[pending].state = state >= 0 && state < 11 ? states[state] : 0;
        }

        at += (record + 7) & ~(uint32_t)7;
    }

    free(list);
    return count;
}

// The system's counters: TCP's may read as nothing at all, which macOS
// keeps from programs without an entitlement.
static bool protocol_counters(int protocol, int64_t counters[8]) {
    for (int i = 0; i < 8; i++) {
        counters[i] = -1;
    }

    if (protocol == 6) {
        struct tcpstat stats;
        size_t length = sizeof stats;
        if (sysctlbyname("net.inet.tcp.stats", &stats, &length, NULL, 0) != 0) {
            ipc_fail();
            return false;
        }

        counters[0] = stats.tcps_connattempt;
        counters[1] = stats.tcps_accepts;
        counters[2] = stats.tcps_conndrops;
        counters[4] = stats.tcps_rcvtotal;
        counters[5] = stats.tcps_sndtotal;
        counters[6] = stats.tcps_sndrexmitpack;
        counters[7] = stats.tcps_sndctrl;
        return true;
    }

    struct udpstat stats;
    size_t length = sizeof stats;
    if (sysctlbyname("net.inet.udp.stats", &stats, &length, NULL, 0) != 0) {
        ipc_fail();
        return false;
    }

    counters[0] = stats.udps_ipackets;
    counters[1] = stats.udps_opackets;
    counters[2] = stats.udps_noport;
    counters[3] = stats.udps_hdrops + stats.udps_badsum + stats.udps_badlen;
    return true;
}
#endif
