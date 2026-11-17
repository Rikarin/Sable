# Manually-managed C ports

The baseline for the Phase 3 exit criterion "ARC overhead on the suite < 5%
vs a manually-managed equivalent" (docs/04-implementation-plan.md §3.8). Each
`X.c` is a port of `../X.sab`: the same algorithm, sizes and iteration counts,
and byte-for-byte the same output.

    clang -O2 -Wall -Wextra X.c -o X -lm

The rules the ports follow, so that the comparison isolates memory management:

- **Memory by hand.** `malloc`/`free` exactly where the Sable object's life
  ends; no reference counts, no collector, nothing leaked at exit. Values
  shared in Sable are borrowed pointers here, owned by exactly one place.
- **The same allocations.** A class instance is one `malloc`; a string is
  one heap object (literals static, as Sable's are immortal); an array is a
  header plus an element buffer (two mallocs, the empty array `NULL`),
  growing 4, 8, 16, ...; a slice is a copy; an `any P` is a witness table
  plus one heap box; interpolation concatenates piece by piece, freeing each
  intermediate. Copy-on-write is mirrored where it decides whether a copy
  happens (words' `ranked` copies only on its first write).
- **The same checks.** Integer arithmetic on `Int`/`U64` traps on overflow
  (`__builtin_*_overflow`), narrowing conversions are range-checked, and
  array indexing is bounds-checked.
- **The same arithmetic.** Floating point is evaluated as written
  (`#pragma STDC FP_CONTRACT OFF`, as Sable emits no FMAs) and printed with
  Sable's shortest-round-trip `%.Ng` format.

Per benchmark:

- `binary-trees.c` — one `malloc` per node; each tree freed after `check`.
- `nbody.c` — no allocation in the loop; the control.
- `shapes.c` — function-pointer witness tables and boxed values for
  `any Shape`; a vtable pointer in each `Animal`.
- `sort.c` — the generic merge sort instantiated for `Int` and `String`, the
  ordering a function pointer.
- `words.c` — the dictionary is Sable's: an array of (key, value) entries
  searched linearly, owning its keys.
- `orders.c` — the `@inline_layout` records in Sable's exact byte layout,
  built in 96-byte frame buffers by the same splices, serialised to a `[U8]`
  and validated on the way back. Unlike Sable's runtime, its layout code is
  written for these two record types rather than driven by layout
  descriptors, and assigning a string does not allocate a temporary.
