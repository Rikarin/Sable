# Benchmarks

A folder for each benchmark, with the Sable program and its ports side by side:

    library/map-int/map-int.sb    Sable
    library/map-int/map-int.cs    .NET
    library/map-int/map-int.rs    Rust
    library/map-int/map-int.c     C, where there is one

- `library/` — the standard library, a benchmark for each type or function
  worth timing: `@bench` functions (docs/05 §5.1). A port exists where the
  language's own standard library has an equal; where none has, the Sable
  program is alone, so that a slowdown is seen.
- `programs/` — whole programs, for the language itself: each has a `main`,
  and a `@bench` function that measures what that does. `startup` is a program
  that does nothing, measured whole and not by the harness. The C ports follow
  [rules of their own](programs/README.md).
- `concurrency/` — tasks, as jobs of no worker, one and one a core
  (`tools/bench/concurrency.sh`); `sleepers`, whose point is what a process
  holds, is a program. `compare/` is the same in .NET and Swift.
- `support/` — the .NET ports' project and runner, and the Rust and C ports'
  shared sources.
- `history.tsv` — the programs' times and ARC counts by commit, of the whole
  process (`tools/bench/run.py --record`): the date, the commit, with `+` for
  uncommitted changes, the program, milliseconds, allocations, retains and
  releases. `sable bench --history` adds rows of the same columns, of an
  operation.

`tools/bench/compare.py` runs `library/` and `programs/` in every language and
prints the tables; CI's *Benchmarks* runs have them as their summary and their
artifact, `benchmarks.md`. Sable's side alone is `sable bench`:

```bash
tools/bench/compare.py
tools/bench/compare.py map-int programs/sort
sable bench benchmarks/library/map-int --job "master=sable=$MASTER/sable" --job this=
sable bench benchmarks/library regex --counters
```

The first of the last two compares this library and compiler with another's,
built elsewhere; `--compare master` would build the benchmarks as they are at
`master` with this compiler, whose library is its own.

## How it is measured

The same way in each language: Sable's by the harness, and the ports by
`support/dotnet/Program.cs`, `support/rust/bench.rs` and `support/c/bench.h`,
which do what the harness does and print their samples for `compare.py`.

- Each benchmark runs in a process of its own. Its input is made once, and is
  not measured.
- It is run until that takes an eighth of a sample's 20 ms, the iterations
  doubling, to learn how many make a sample; then for a second of warm-up, and
  on until it has stopped getting faster, for three at most. .NET's JIT needs
  the most of that; the others are given it too.
- Then 15 samples, or as many as a second allows once there are five.
- A sample's time is **processor time**, of every thread — .NET's collector's
  and JIT's among them — and of the processes it waited for. It is less at the
  mercy of what else the machine does; and what only waits costs nothing.
- The table has the median, of an operation: what the body does, once.
- *allocated* is what an operation allocates: for Sable, objects and buffers as
  the allocator rounds them; for .NET, `GC.GetTotalAllocatedBytes`. Rust's and
  C's are not counted.
- Every port computes something that depends on all its work, and it must be
  what the Sable benchmark's `check` is.
- *Whole processes* is another thing: the processor time of a process from its
  start to its end, cold, the median of seven. There a .NET program starts its
  runtime and is compiled as it runs, and a Sable one is what it is.

## The tables

`### Standard library` and `### Programs`, a row a benchmark, or a case of one
(`aes-gcm(64, 100000)`):

| column | |
|:--|:--|
| benchmark | the folder's name, and the case's arguments |
| Sable, .NET, Rust, C | processor time an operation, with its unit, three digits; the fastest in bold; `—` without a port; what went wrong, where it did |
| vs .NET, vs Rust, vs C | Sable's time over the other's |
| Sable allocated, .NET allocated | bytes an operation, with their unit |

The last row is the geometric mean of each ratio, and of how many. `### Whole
processes` has the same columns in milliseconds, without the last two; and
`### Failures`, when there are any, says what did not build, run or agree,
which the script exits with 1 for.

## Adding one

Make `library/<name>/<name>.sb`, a module with a `@bench` function that takes a
`Bencher`, makes its input, and gives `iterate` a body that takes some tens of
milliseconds and returns a line that depends on all its work, which `check` is
given. Sizes are `@bench(arguments:)`, not copies. A body that does the same to
the same input many times over puts a `blackBox` in its loop, or the optimiser
may do it once. Each folder is built as a program of its own: what the optimiser
inlines depends on what else there is. So that several can be built as one all
the same (`sable bench benchmarks/library`), a module has a name of its own.

Then its ports, named the same. They compute the same line, keep the checks
(integer overflow and bounds), and use nothing outside their language's
standard library, where .NET's includes `System.Numerics.Tensors`. What they
write, they write in the directory they are run in, and remove.

- .NET: a static class named after the folder (`LibraryMapInt`) with
  `[Benchmark("library/map-int")]`, or one such for each case with its
  arguments, and `public static string Run()` — or `Setup(...)`, which makes
  the input and returns the `Func<string>` to measure, and perhaps `Cleanup()`.
- Rust: `bench::run(|| ...)` in `main`, after what makes the input.
- C: `BENCH_MAIN(run)`, or `BENCH_MAIN_WITH(run, setup)`; the line is said
  with `bench_printf`.

A port run without arguments does its work once and prints the line.

### From the old format

A library benchmark used to be a program: `@main`, and a line printed. `compare.py`
leaves such a folder out and says so. To convert one:

1. `import Sable.Testing`; rename `main` to `fn run() -> String`, and return the
   line in place of printing it. (`async`, `throws` stay.)
2. Add, below it,

   ```sable
   @bench
   fn measure(_ b: Bencher) {
       b.check(b.iterate { run() })
   }
   ```

   or, for an async one, `async fn measure` and
   `b.check(await b.iterateAsync { await run() })`; `try!` for one that throws.
3. Where making the input takes a share worth speaking of, move that into
   `measure` and give it to `run`; in .NET, `Run()` becomes `Setup()`, which
   makes it and returns `() => Run(input)`.
4. The .NET class is named after the folder, `LibraryMapInt`. A `Run()` of the
   whole needs no other change.
5. Rust: the module of `support/rust/bench.rs`, and `bench::run(|| { ... })`
   whose last expression is `format!(...)`. C: `main` becomes `static void run(void)`,
   `printf` becomes `bench_printf`, and `BENCH_MAIN(run)` ends the file.
