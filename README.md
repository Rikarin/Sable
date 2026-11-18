# Sable

**A systems language with C#-shaped syntax, Swift-style value semantics and
reference counting, Rust-style move checking, and compile-time checks on how
memory is used — compiled ahead of time to native code through LLVM.**

```sable
module Tour

/// A closed union: a `match` over it must cover every case.
union Shape {
    Circle(radius: F64)
    Rect(width: F64, height: F64)
}

protocol Describable {
    fn describe() -> String
}

extension Shape: Describable {
    var area: F64 => match self {
        case .Circle(let r)      => 3.14159 * r * r
        case .Rect(let w, let h) => w * h
    }

    fn describe() -> String => "a shape of area \(area)"
}

union ShapeError { Negative(side: F64) }

/// Errors are typed: callers know exactly what can go wrong.
fn square(side: F64) throws(ShapeError) -> Shape {
    guard side >= 0.0 else {
        throw .Negative(side: side)
    }

    return .Rect(width: side, height: side)
}

fn main() {
    let shapes: Shape[] = [.Circle(radius: 1.0), .Rect(width: 2.0, height: 3.0)]
    var total = 0.0
    for shape in shapes {
        print(shape.describe())
        total += shape.area
    }

    print("total area: \(total)")

    do {
        print(try square(side: 4.0).describe())
        print(try square(side: -1.0).describe())
    } catch .Negative(let side) {
        print("a side cannot be \(side)")
    }
}
```

```
$ sable run tour.sb
a shape of area 3.14159
a shape of area 6.0
total area: 9.14159
a shape of area 16.0
a side cannot be -1.0
```

> **Sable is pre-1.0 and changes often.** The language, the standard library
> and the toolchain all work end to end, but nothing is stable yet: expect
> source changes between versions, and don't ship production code on it.

## Why Sable

- **Familiar syntax.** If you read C# or Swift, you can read Sable: braces,
  `fn`, `struct`, `class`, `protocol`, `extension`, generics with `where`
  clauses, string interpolation.
- **Memory safety without a garbage collector.** Values are copied, classes are
  reference-counted, and move-only types (`@noncopy`) are checked at compile
  time. Exclusive access to mutable state is enforced, statically where it can
  be and at run time where it can't.
- **Most data doesn't need to be a graph.** Sable lets you say where memory
  lives — inline, in a region that is freed all at once, or counted — and
  checks that references don't outlive what they point into. See
  [the memory model](docs/02-memory-model.md).
- **Protocols, unions and generics.** Protocols with associated types and
  default implementations; closed unions whose `match` must be exhaustive;
  structural unions like `Int | String`; generics that are specialised for
  speed by default and boxed (`any P`) when you ask.
- **Typed errors.** `throws(MyError)` says exactly what a function can throw,
  and a `catch` that doesn't cover every case is a compile error.
- **async/await and actors**, with `Sendable` checking at actor boundaries.
- **Reflection you only pay for when you use it**, and source generators
  instead of macros: ordinary Sable functions that write code while a package
  compiles.
- **C interop.** `@extern` and `@export` follow the platform's C calling
  convention, and `sable bindgen` writes declarations from a C header.
- **One tool.** `sable` builds, runs, tests, benchmarks, formats, lints,
  documents, debugs and manages packages, and serves the language server.

## Getting started

### Install

Sable has no binary releases yet; you build the toolchain from source. You
need:

- the [.NET 11 SDK](https://dotnet.microsoft.com/download)
- `clang` (or set `$CC` to a clang that can link for your machine)
- `lldb`, only for `sable debug`

```bash
git clone https://github.com/Rikarin/Sable.git
```

```bash
cd Sable && dotnet publish src/Sable.Driver -c Release -o ~/.sable/bin
```

Then add `~/.sable/bin` to your `PATH`, and check it works:

```bash
sable --version
```

Supported platforms are macOS (Apple silicon and Intel) and Linux (x86-64 and
arm64). Windows support is in progress.

### Your first program

A single file runs directly:

```bash
sable run hello.sb
```

For anything bigger, make a package:

```bash
sable new hello
```

```bash
cd hello && sable run
```

That gives you this layout:

```
hello/
├── package.sb     the manifest
└── src/
    └── main.sb    the entry point
```

### The manifest

A package's manifest, `package.sb`, is itself written in Sable, so it is
type-checked and your editor completes it:

```sable
import Sable.Build

let manifest = Package {
    name: "hello",
    version: "0.1.0",
    edition: 2027,
    dependencies: [
        .registry("sable-json", "^1.2"),
        .path("shared", "../shared"),
        .git("http", "https://github.com/example/http.git", tag: "v0.4.0"),
    ],
}
```

It runs at build time in a sandbox: no I/O, no clock, nothing that would make
one machine's build differ from another's. `sable add` and `sable remove` edit
it for you, keeping your comments and layout. Workspaces of several packages,
features, build profiles and build scripts are all described in
[docs/05](docs/05-toolchain.md).

## The `sable` command

| Command | What it does |
|---|---|
| `sable new`, `sable init` | start a package |
| `sable run`, `sable build` | compile (and run) a package or a single file |
| `sable check` | type-check without generating code — fast |
| `sable test` | run `@test` functions; `--doc` also runs doc examples |
| `sable bench` | run `@bench` functions and compare with a baseline |
| `sable fmt` | format source; there are no options, by design |
| `sable lint`, `sable fix` | run the lints, at the levels your manifest sets; apply their fixes |
| `sable doc` | generate documentation |
| `sable debug` | build with debug info and run under lldb, with Sable-aware formatters |
| `sable repl` | an interactive session |
| `sable add`, `remove`, `update`, `tree`, `vendor` | manage dependencies |
| `sable publish`, `yank`, `search`, `install` | work with a registry |
| `sable explain S3004` | the full explanation of a compiler diagnostic |
| `sable bindgen header.h` | Sable declarations for a C header |
| `sable lsp` | the language server, for editors |

`sable help <command>` shows each command's options. `--target <triple>`
cross-compiles, and `--message-format json` gives machine-readable diagnostics.

## Editor support

The VS Code extension in [editors/vscode](editors/vscode) gives you
highlighting, diagnostics as you type, completion, go to definition, find
references, rename, hover, inlay hints, formatting, code actions, tasks and
debugging. [editors/](editors/) also has setups for Vim and Neovim, Helix, Emacs
and Sublime Text, and any other editor with LSP support can run `sable lsp`.

## Learning the language

| | |
|---|---|
| [spec/SYNTAX.md](spec/SYNTAX.md) | **The language reference**, with an example for every construct |
| [examples/](examples/) | Sample programs, from hello world to an async service and a static site generator |
| [docs/01-overview.md](docs/01-overview.md) | The design: what Sable is for, and what it deliberately leaves out |
| [docs/02-memory-model.md](docs/02-memory-model.md) | Inline values, regions, reference counting, and how references are checked |
| [docs/03-type-system.md](docs/03-type-system.md) | Protocols, unions, generics, variance and effects |
| [docs/05-toolchain.md](docs/05-toolchain.md) | The `sable` command, manifests, packages and builds |
| [docs/06-runtime-and-stdlib.md](docs/06-runtime-and-stdlib.md) | The runtime, reflection and the standard library |
| [docs/08-source-generators.md](docs/08-source-generators.md) | Source generators |
| [docs/diagnostics/](docs/diagnostics/) | What every compiler diagnostic means — the pages `sable explain` prints |

## Status

The compiler, runtime, standard library, package manager and language server
are all built and tested. What's still missing is tracked in
[GitHub issues](https://github.com/Rikarin/Sable/issues). Issue #224 is the
compiler's overview, and issue #1 tracks the standard library.

## Contributing

Bug reports and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md)
explains how the repository is laid out, how to build and test the compiler,
and the tools that help when working on it.
