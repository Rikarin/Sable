// The Sable runtime: frames — the slab each task's async frames come from.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Frames ----------------------------------------------------------------

// An async function's frame (AsyncLowering in the compiler) holds what the
// function keeps while its task is suspended. A call to one makes the
// callee's frame and gives it back once the callee has finished, so a task's
// frames come and go as a stack does: each task has a slab of its own, a
// bump allocator, and a call costs a bump, never a malloc. The slab's first
// chunk is inside the task itself; a task whose calls go deeper takes more
// chunks from the heap, each kept until the frames in it are gone, and one
// emptied chunk is kept as a spare for the next time it goes as deep.
//
// A frame is never counted: its caller gives it back (sable_frame_free),
// and what it held is the function's to have destroyed. Frames are given
// back in the reverse of the order they were made, which the slab checks.

enum {
    // What a task holds inside itself, enough for the frames of most tasks:
    // the root, a call or two beneath it.
    FRAME_FIRST_CHUNK = 576,
    // Each chunk after the first, at least.
    FRAME_CHUNK = 16 * 1024,
    FRAME_ALIGN = 16,
};

// A chunk taken from the heap: the frames in it follow this header.
typedef struct FrameChunk {
    struct FrameChunk *previous; // the chunk the slab was in before, or NULL for the first
    char *previous_top;          // where the slab had reached in it
    char *previous_limit;
    size_t size;                 // the bytes after the header
} FrameChunk;

typedef struct FrameSlab {
    char *top;          // where the next frame goes
    char *limit;        // the end of the chunk it is in
    FrameChunk *chunk;  // the chunk taken from the heap it is in; NULL in the first
    FrameChunk *spare;  // an emptied chunk, kept for the next time the slab outgrows the one before
} FrameSlab;

// The running task's slab (tasks.c sets it as it switches tasks).
static FrameSlab *current_frames;

// What the slabs have done, for SABLE_STATS.
static int64_t frames_made, frame_chunks_taken;

static inline size_t frame_round(size_t size) {
    return (size + FRAME_ALIGN - 1) & ~(size_t)(FRAME_ALIGN - 1);
}

static inline char *chunk_frames(FrameChunk *chunk) {
    return (char *)chunk + frame_round(sizeof(FrameChunk));
}

// A slab whose first chunk is 'size' bytes at 'first'.
static void slab_init(FrameSlab *slab, char *first, size_t size) {
    slab->top = first;
    slab->limit = first + size;
    slab->chunk = NULL;
    slab->spare = NULL;
}

static void *slab_push(FrameSlab *slab, size_t size) {
    size = frame_round(size);
    if ((size_t)(slab->limit - slab->top) < size) {
        FrameChunk *chunk = slab->spare;
        if (chunk != NULL && chunk->size >= size) {
            slab->spare = NULL;
        } else {
            size_t room = size > FRAME_CHUNK ? size : FRAME_CHUNK;
            chunk = malloc(frame_round(sizeof(FrameChunk)) + room);
            if (chunk == NULL) {
                sable_trap("out of memory");
            }

            chunk->size = room;
            frame_chunks_taken++;
        }

        chunk->previous = slab->chunk;
        chunk->previous_top = slab->top;
        chunk->previous_limit = slab->limit;
        slab->chunk = chunk;
        slab->top = chunk_frames(chunk);
        slab->limit = slab->top + chunk->size;
    }

    // Empty, as every slot starts: storage holding nothing is all zeros.
    void *frame = slab->top;
    memset(frame, 0, size);
    slab->top += size;
    frames_made++;
    return frame;
}

// The most recent frame given back: the slab goes back to where it was
// before it, into the chunk before when this one is left empty.
static void slab_pop(FrameSlab *slab, void *frame) {
    char *at = frame;
    if (at > slab->top) {
        sable_trap("an async frame freed out of order");
    }

    slab->top = at;
    FrameChunk *chunk = slab->chunk;
    if (chunk != NULL && at == chunk_frames(chunk)) {
        slab->chunk = chunk->previous;
        slab->top = chunk->previous_top;
        slab->limit = chunk->previous_limit;
        if (slab->spare == NULL) {
            slab->spare = chunk;
        } else if (slab->spare->size < chunk->size) {
            free(slab->spare);
            slab->spare = chunk;
        } else {
            free(chunk);
        }
    }
}

// A slab no longer used: its chunks given back. Every frame in it is gone.
static void slab_destroy(FrameSlab *slab) {
    while (slab->chunk != NULL) {
        FrameChunk *chunk = slab->chunk;
        slab->chunk = chunk->previous;
        free(chunk);
    }

    free(slab->spare);
    slab->spare = NULL;
}

// A frame for the running task: 'size' bytes, an object header's among them.
void *sable_frame_alloc(int64_t size) {
    return slab_push(current_frames, (size_t)size);
}

void sable_frame_free(void *frame) {
    slab_pop(current_frames, frame);
}

static void report_frames(void) {
    fprintf(stderr, "sable: frames %lld, frame chunks %lld\n", (long long)frames_made, (long long)frame_chunks_taken);
}
