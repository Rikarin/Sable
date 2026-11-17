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
             Sable.Syntax   lexer, parser, trees, formatter
             Sable.Symbols  symbols, declaration table, type binding
tools/     Sable.Syntax.Generator  generates the node classes
             Sable.Visualiser        the syntax-tree visualiser
tests/     the test suite
```

Node classes are generated from [src/Sable.Syntax/Syntax.xml](src/Sable.Syntax/Syntax.xml).
After changing the schema:

```bash
dotnet run --project tools/Sable.Syntax.Generator
```

The generated files are committed, and a test fails if they drift from the schema.

### Seeing what the compiler made of a file

`sable-visualise` is the syntax-tree visualiser — Roslyn's Syntax Visualiser,
for Sable. It shows every node, token and trivia, including the missing
tokens and skipped text error recovery produces, and what the semantic model
says about each node: its type and the symbol it binds to.

```bash
dotnet run --project tools/Sable.Visualiser -- examples/01-hello.sab
```

```bash
dotnet run --project tools/Sable.Visualiser -- examples/01-hello.sab --html hello.html
```

The first prints the tree as indented text (`--trivia` adds whitespace and
comments). The second writes one self-contained page: the source beside a
collapsible tree and a property pane, where selecting a node highlights its
span and clicking the source selects the deepest node under the caret.
`--syntax-only` skips binding.

## Building

Requires the .NET 11 SDK.

```bash
dotnet build Sable.slnx
```

```bash
dotnet test Sable.slnx
```

## Status

**Phase 0 complete; Phase 1 nearly so** — see
[docs/04-implementation-plan.md](docs/04-implementation-plan.md).

| Component | State |
|---|---|
| `Sable.Syntax` — source text, spans, diagnostics | done |
| `Sable.Syntax` — tokens, trivia, `SyntaxFacts` | done |
| `Sable.Syntax` — lexer | done |
| `Sable.Syntax` — green/red tree, lists, visitor | done |
| `Sable.Syntax` — node generator | done |
| `Sable.Syntax` — parser: types and expressions | done |
| `Sable.Syntax` — parser: statements, patterns, closures | done |
| `Sable.Syntax` — parser: declarations and compilation units | done |
| `Sable.Syntax` — conditional compilation | done |
| `Sable.Syntax` — structured string interpolation | done |
| `Sable.Syntax` — incremental reparse | done |
| `Sable.Syntax` — formatter | done |
| `Sable.Symbols` — symbol model, declaration table | done |
| `Sable.Symbols` — type binding and signatures | done |
| `Sable.Symbols` — bound tree, expression and statement binding | done |
| `Sable.Symbols` — SemanticModel | done |
| `Sable.Symbols` — patterns, match, exhaustiveness, flow | done |
| `Sable.Symbols` — generic substitution and inference | done |
| `Sable.Symbols` — protocol conformance | done |
| `Sable.Symbols` — `Self` and associated types | done |
| `Sable.Symbols` — accessibility, mutability, inheritance control | done |
| `Sable.Symbols` — coherence and declaration-site variance | done |
| `Sable.Symbols` — flow-sensitive narrowing | done |
| `Sable.Symbols` — closures and function values | done |
| `Sable.Symbols` — extensions and memberwise initialisers | done |
| `Sable.Symbols` — local types, destructuring, generic aliases | done |
| `Sable.Symbols` — effects: `throws`, `rethrows` and `async` | done |
| `Sable.Symbols` — definite assignment and the flow-analysis API | done |
| `Sable.Symbols` — negative-diagnostic corpus (631 cases) | done |
| `Sable.Symbols` — throughput: 100,000 lines in under 5 s | done |
| `tools/Sable.Visualiser` — syntax-tree visualiser | done |
| Everything downstream (SIL, codegen, runtime) | not started |

Phase 1's exit criteria, as docs/04 §1.4 states them:

| Criterion | State |
|---|---|
| The full type system of `docs/03` is checked | done |
| ≥ 600 negative programs, each asserting code, span and message | 631, over 99 diagnostics |
| `AnalyzeDataFlow` correct on a 300-case corpus | 59 cases so far |
| Type-checking 100,000 lines in under 5 s | 0.98 s |
| Go/no-go: 10,000 lines of real Sable | ~2,900 in `tests/corpus/` |

**1,247 tests green**, and every file in [examples/](examples/) parses as a
complete compilation unit with no diagnostics and an exact round-trip.
[tests/corpus/](tests/corpus/) holds programs that must type-check with
nothing to say, and [tests/negative/](tests/negative/) holds programs that
must fail, each claiming the exact diagnostic its line produces.

The lexer covers all of [spec/lexical.md](spec/lexical.md): full-fidelity
trivia, nested block comments, every literal form including interpolated and
raw strings, and the statement-terminator rule.

The tree is Roslyn's red/green design: immutable, position-independent green
nodes shared between tree versions, with thin red facades created on demand.
The parser is hand-written recursive descent with error recovery that never
drops text — unusable tokens become skipped-token trivia and absent ones are
inserted as zero-width missing tokens.

The full-fidelity round-trip invariant — concatenating every token's full text
reproduces the source byte for byte — is enforced over the `examples/` corpus,
a table of hand-written cases, deliberately malformed input, and 400 rounds of
random token soup.

`SyntaxTree.Parse` reads a whole file; `ParseExpression`, `ParseType` and
`ParseStatement` read a single construct, which is what most of the tests use.
`SyntaxTree.WithChange` applies an edit, reusing everything it did not touch:
a one-character edit in a 10,000-line file reparses in **under 2 ms**, against
about 30 ms for a full parse. The contract is that the result is exactly the
tree a full parse would have produced, and every incremental test asserts it.

`SyntaxFormatter.Format` renders a tree as canonical source. It has no options,
by design. Two properties are tested over the corpus rather than asserted on
sample output: formatting is idempotent, and formatting never changes the tree.
