// The Sable runtime's platform layer: what the rest of the runtime asks of
// the operating system. platform_posix.c gives it for macOS and Linux,
// platform_win32.c for Windows; sable_runtime.c includes the one for the
// system it is compiled for, before the parts that use it.
//
// Every name and string crosses as UTF-8, NUL-terminated; a string the
// layer gives back is in memory the caller frees, and NULL where there is
// none. Each layer also defines:
//
//   SABLE_AT_EXIT(f)    the start of the definition of 'static void f(void)',
//                       which runs as the program exits

#ifndef SABLE_RUNTIME_PLATFORM_H
#define SABLE_RUNTIME_PLATFORM_H

// ---- Start-up --------------------------------------------------------------

// What the system needs set up before any Sable code runs: the main
// thread's stack watched, the standard streams' modes. Called once, by
// sable_runtime_start.
static void platform_start(void);

// ---- Traps ---------------------------------------------------------------

// The most frames a backtrace holds.
enum { BACKTRACE_DEPTH = 64 };

// The return addresses of the calls that led to the caller, its caller's
// first, at most 'capacity' of them; how many there are.
static int capture_frames(void **frames, int capacity);

// A frame as text: 'symbol + offset' where the symbol is known, the
// address otherwise.
static void name_frame(void *frame, char *text, size_t size);

// Ends the program as a trap does: as an abort is reported, status 134 in
// a shell, on every platform.
_Noreturn static void stop_trapped(void);

// ---- Memory --------------------------------------------------------------

// 'size' bytes of zeroed memory straight from the system, starting at a
// multiple of 'size' (a power of two, a multiple of the page); NULL when
// there is none. unmap_slab gives it back to the system.
static void *map_slab(size_t size);
static void unmap_slab(void *slab, size_t size);

// The most memory the process has held at once, in bytes, as the system
// counts it (its peak resident set); 0 where the system does not say.
static int64_t peak_memory(void);

// ---- Time ----------------------------------------------------------------

// Nanoseconds from some fixed point, never going back; since 1970; and a
// wait of at least so many.
int64_t sable_time_monotonic(void);
int64_t sable_time_wall_clock(void);
void sable_time_sleep(int64_t nanoseconds);

// ---- Arguments, files and the environment -------------------------------

// The program's arguments, from what main() was given; how many in 'count'.
static char **platform_arguments(int argc, char **argv, int *count);

static FILE *open_file(const char *name, const char *mode);
static bool file_exists(const char *name);
static bool is_directory(const char *name);
// A file, or an empty directory.
static bool remove_file(const char *name);
static bool make_directory(const char *name);
// The names in a directory, but '.' and '..', in no order; their number in
// 'count'. NULL when it cannot be read.
static char **list_directory(const char *name, size_t *count);
static char *environment_variable(const char *name);

// ---- Processes -----------------------------------------------------------

// Runs argv[0], found on PATH as the system finds a program, with the
// arguments after it, to its end: 'input' written to it, what it writes to
// standard output and error added to the buffers, and how it ended put in
// 'status'. False when there is no such program, or it cannot be started.
static bool run_program(char **argv, const char *input, int64_t input_length, Buffer *output, Buffer *errors, int64_t *status);

static char *current_directory(void);
static bool change_directory(const char *name);
static int64_t process_id(void);
// The path the program can be started by again, from its arguments or the system.
static char *executable_path(int argc, char **argv);

// ---- Randomness ----------------------------------------------------------

// 'length' bytes (at most 256) from the system's secure random source, for
// the hash seed; false when the system gives none.
static bool random_bytes(void *buffer, size_t length);

#endif
