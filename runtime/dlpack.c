// The Sable runtime: DLPack — tensors handed to and taken from other
// libraries (PyTorch, NumPy, JAX) without a copy, by DLPack's C structs
// (dmlc/dlpack's include/dlpack/dlpack.h, 1.x; the legacy DLManagedTensor
// and the versioned one). Part of sable_runtime.c, which includes every
// part in order (see there).
//
// Taking one in is Sable.Math.Tensors' own: it reads the struct's fields,
// and the elements become a foreign array (arrays.c) whose last release is
// the struct's deleter. Handing one out is here: the struct, its shape and
// its strides in one block of C's memory, holding a counted reference to
// the elements' buffer, which its deleter lets go of — on whichever thread
// the consumer calls it from, so the buffer is shared (counted atomically)
// first.

typedef struct {
    uint32_t major;
    uint32_t minor;
} SableDLPackVersion;

typedef struct {
    int32_t device_type;
    int32_t device_id;
} SableDLDevice;

typedef struct {
    uint8_t code;
    uint8_t bits;
    uint16_t lanes;
} SableDLDataType;

typedef struct {
    void *data;
    SableDLDevice device;
    int32_t ndim;
    SableDLDataType dtype;
    int64_t *shape;
    int64_t *strides;
    uint64_t byte_offset;
} SableDLTensor;

typedef struct SableDLManagedTensor {
    SableDLTensor dl_tensor;
    void *manager_ctx;
    void (*deleter)(struct SableDLManagedTensor *self);
} SableDLManagedTensor;

typedef struct SableDLManagedTensorVersioned {
    SableDLPackVersion version;
    void *manager_ctx;
    void (*deleter)(struct SableDLManagedTensorVersioned *self);
    uint64_t flags;
    SableDLTensor dl_tensor;
} SableDLManagedTensorVersioned;

_Static_assert(sizeof(SableDLTensor) == 48, "DLTensor's layout");
_Static_assert(sizeof(SableDLManagedTensor) == 64, "DLManagedTensor's layout");
_Static_assert(sizeof(SableDLManagedTensorVersioned) == 80, "DLManagedTensorVersioned's layout");

enum { SABLE_DL_CPU = 1, SABLE_DL_READ_ONLY = 1 };

static void dlpack_delete(SableDLManagedTensor *self) {
    if (self != NULL) {
        sable_release(self->manager_ctx);
        free(self);
    }
}

static void dlpack_delete_versioned(SableDLManagedTensorVersioned *self) {
    if (self != NULL) {
        sable_release(self->manager_ctx);
        free(self);
    }
}

// A DLPack struct of 'ndim' dimensions over 'data', of elements of the
// type 'code' and 'bits' say, on the CPU: the shape and strides (in
// elements) copied after it. '*cell' is a slice of the elements' buffer,
// moved in: the struct keeps its reference. 'versioned' makes a
// DLManagedTensorVersioned, marked read-only (a Sable tensor is a value:
// what shares its buffer must not see it change); else a DLManagedTensor,
// which has no such mark. Null, the reference let go of, when C has no
// memory for it.
int64_t sable_dlpack_export(int64_t at, int64_t start, int64_t ndim, int64_t shape_at, int64_t strides_at, int64_t code, int64_t bits, bool versioned) {
    void **cell = (void **)(intptr_t)at;
    void *data = (void *)(intptr_t)start;
    const int64_t *shape = (const int64_t *)(intptr_t)shape_at;
    const int64_t *strides = (const int64_t *)(intptr_t)strides_at;
    void *buffer = cell[0];
    cell[0] = NULL;
    size_t head = versioned ? sizeof(SableDLManagedTensorVersioned) : sizeof(SableDLManagedTensor);
    char *block = malloc(head + 2 * (size_t)ndim * sizeof(int64_t) + 1);
    if (block == NULL) {
        sable_release(buffer);
        return 0;
    }

    if (buffer != NULL) {
        sable_share(buffer);
    }

    int64_t *lengths = (int64_t *)(block + head);
    int64_t *steps = lengths + ndim;
    for (int64_t i = 0; i < ndim; i++) {
        lengths[i] = shape[i];
        steps[i] = strides[i];
    }

    SableDLTensor tensor = {
        data, {SABLE_DL_CPU, 0}, (int32_t)ndim, {(uint8_t)code, (uint8_t)bits, 1}, lengths, steps, 0,
    };
    if (versioned) {
        SableDLManagedTensorVersioned *made = (SableDLManagedTensorVersioned *)block;
        made->version = (SableDLPackVersion){1, 0};
        made->manager_ctx = buffer;
        made->deleter = dlpack_delete_versioned;
        made->flags = SABLE_DL_READ_ONLY;
        made->dl_tensor = tensor;
    } else {
        SableDLManagedTensor *made = (SableDLManagedTensor *)block;
        made->dl_tensor = tensor;
        made->manager_ctx = buffer;
        made->deleter = dlpack_delete;
    }

    return (int64_t)(intptr_t)block;
}

// A DLPack struct's deleter called, as its consumer does when done with it:
// for a library's own tests of what it hands out, and for code that gives a
// struct back unused. 'versioned' says which kind it is.
void sable_dlpack_delete(int64_t at, bool versioned) {
    void *managed = (void *)(intptr_t)at;
    if (managed == NULL) {
        return;
    }

    if (versioned) {
        SableDLManagedTensorVersioned *self = managed;
        if (self->deleter != NULL) {
            self->deleter(self);
        }
    } else {
        SableDLManagedTensor *self = managed;
        if (self->deleter != NULL) {
            self->deleter(self);
        }
    }
}
