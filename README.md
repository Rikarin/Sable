# Sable

> A protocol-oriented systems language with C#-shaped syntax, Swift-shaped memory
> management, Rust-shaped safety, and a Goose-shaped data layout model.
> Compiled to native code via LLVM.

**`Sable` is a placeholder name.** It is short, unclaimed in the major package
registries, and the file extension `.sab` is free. Rename at will — the only
places the name is load-bearing are the toolchain binary (`sable`), the manifest
(`sable.toml`) and the module namespace root.

---

## The one-paragraph pitch

Sable is what you get when you take C#'s syntax and tooling discipline, Swift's
value semantics and ARC, Rust's move checking, and Goose's insight that *most
object graphs do not need to be graphs at all*, and then refuse to pick only one.
It compiles ahead-of-time to native binaries through LLVM. It has protocols with
associated types, closed unions with exhaustive matching, generics that are
monomorphised by default and boxed on demand, optional runtime reflection that
you pay for only where you ask for it, and a single tool — `sable` — that builds,
tests, formats, documents and resolves packages.

## Documents in this repository

| File | What it is |
|---|---|
| [docs/01-overview.md](docs/01-overview.md) | Design pillars, influences, and the things deliberately left out |
| [docs/02-memory-model.md](docs/02-memory-model.md) | The tiered memory model: inline values, regions, ARC, and root inference |
| [docs/03-type-system.md](docs/03-type-system.md) | Protocols, unions, generics, variance, effects |
| [docs/04-implementation-plan.md](docs/04-implementation-plan.md) | The actual build plan: 7 phases, ~30 months, Roslyn architecture |
| [docs/05-toolchain.md](docs/05-toolchain.md) | `sable` CLI, manifest format, resolver, build graph |
| [docs/06-runtime-and-stdlib.md](docs/06-runtime-and-stdlib.md) | Runtime ABI, reflection, metadata, framework layout |
| [docs/07-references.md](docs/07-references.md) | Annotated reading list — papers, compilers, and prior art worth stealing from |
| [spec/SYNTAX.md](spec/SYNTAX.md) | **The full syntax reference**, with examples for every construct |
| [spec/grammar.ebnf](spec/grammar.ebnf) | The complete formal grammar |
| [spec/lexical.md](spec/lexical.md) | Tokens, literals, trivia, and the newline rules |
| [examples/](examples/) | Working-shaped sample programs |

## Read in this order

1. `docs/01-overview.md` — why the language exists
2. `spec/SYNTAX.md` — what it looks like
3. `docs/02-memory-model.md` — the part that is actually novel
4. `docs/04-implementation-plan.md` — how to build it

---

## Repository layout

```
docs/      design documents
spec/      the language specification
examples/  sample programs, also used as the lexer test corpus
src/       the compiler, written in C# (see docs/04 for why)
tests/     the test suite
```

## Building

Requires the .NET 11 SDK.

```bash
dotnet build Sable.slnx
```

```bash
dotnet test Sable.slnx
```

## Status

**Phase 0, in progress** — see [docs/04-implementation-plan.md](docs/04-implementation-plan.md).

| Component | State |
|---|---|
| `Sable.Syntax` — source text, spans, diagnostics | done |
| `Sable.Syntax` — tokens, trivia, `SyntaxFacts` | done |
| `Sable.Syntax` — lexer | done, 207 tests green |
| `Sable.Syntax` — green/red syntax tree | not started |
| `Sable.Syntax` — parser | not started |
| `Sable.Syntax` — incremental reparse | not started |
| Everything downstream (symbols, binder, SIL, codegen) | not started |

The lexer covers all of [spec/lexical.md](spec/lexical.md): full-fidelity
trivia, nested block comments, every literal form including interpolated and
raw strings, and the statement-terminator rule. The full-fidelity round-trip
invariant — concatenating every token's full text reproduces the source byte
for byte — is enforced by the test suite over the `examples/` corpus.

Not yet handled by the lexer: conditional-compilation directives are lexed as
`#` plus an identifier rather than being turned into `DisabledText` trivia.
That is a separate pass, due with the parser.
