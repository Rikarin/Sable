// The Sable runtime's platform layer: what the rest of the runtime asks of
// the operating system. platform_posix.c gives it for macOS and Linux,
// platform_win32.c for Windows; sable_runtime.c includes the one for the
// system it is compiled for, before the parts that use it.
//
// Every name and string crosses as UTF-8, NUL-terminated; a string the
// layer gives back is in memory the caller frees, and NULL where there is
// none. Each layer also defines:
//
//   struct TaskCondition  what a task or a thread waits on, under the one
//                         lock the running task holds (tasks.c)
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

// With SABLE_BACKTRACE set, the frames of the calls that led to a trap, on
// standard error; nothing without it. Called by sable_trap_at, whose frame
// and its own it leaves out.
static void print_backtrace(void);

// Ends the program as a trap does: as an abort is reported, status 134 in
// a shell, on every platform.
_Noreturn static void stop_trapped(void);

// Has a stack overflow on the calling thread reported as a trap, not as a
// crash. Every thread that runs tasks calls it as it starts; the main
// thread's is watched from the start.
static void *watch_stack(void);

// ---- Tasks' threads ------------------------------------------------------

typedef struct TaskCondition TaskCondition;

// The lock the running task holds; taken once by each thread as it starts
// running tasks, and given up only while it waits on a condition.
static void lock_tasks(void);
static void condition_init(TaskCondition *condition);
static void condition_destroy(TaskCondition *condition);
static void condition_signal(TaskCondition *condition);
static void condition_wait(TaskCondition *condition);

// Starts a thread, detached, with a stack of 8 MiB, running body(argument).
static bool start_thread(void (*body)(void *argument), void *argument);

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

#endif
