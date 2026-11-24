# Benchmarks

A folder for each benchmark, with the Sable program and its ports side by side:

    library/map-int/map-int.sb    Sable
    library/map-int/map-int.cs    .NET
    library/map-int/map-int.rs    Rust
    library/map-int/map-int.c     C, where there is one

- `library/` — the standard library, a benchmark for each type or function
  worth timing. A port exists where the language's own standard library has an
  equal.
- `programs/` — whole programs, for the language itself. The C ports follow
  [rules of their own](programs/README.md).
- `concurrency/` — tasks, timed by the programs themselves
  (`tools/bench/concurrency.sh`); `compare/` is the same in .NET and Swift.
- `support/` — the .NET ports' project and runner, and the C ports' shared header.
- `history.tsv` — the programs' times and ARC counts by commit (`tools/bench/run.py --record`).

`tools/bench/compare.py` runs `library/` and `programs/` in every language and
prints the tables; CI's *Benchmarks* runs have them as their summary.

```bash
tools/bench/compare.py
tools/bench/compare.py map-int programs/sort
```

## Adding one

Make `library/<name>/<name>.sb`, a program that prints a line that depends on
all its work, and takes some tens of milliseconds; then its ports, named the
same. The .NET one is a static class with `[Benchmark("library/<name>")]` and
`public static string Run()`. A port prints what the Sable program prints, keeps
its checks (integer overflow and bounds), and uses nothing outside its
language's standard library, where .NET's includes `System.Numerics.Tensors`.
Programs run in a scratch directory, where they may write files.
