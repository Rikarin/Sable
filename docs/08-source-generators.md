# 8. Source generators

A source generator is a Sable function that the compiler runs while it
compiles a package. It reads the declarations that carry its attribute and
writes new Sable source. The new files are compiled with the package as if
someone had written them by hand. This is the model of Roslyn's source
generators: the generator adds code, and never changes the code that is
already there.

Generators are what Sable has instead of macros. §8.9 says why.

```sable
// app/src/model.sb
module App

import Json

@json
public struct User {
    var name: String
    var age: Int
}
```

```sable
// json/generators/json.sb: the 'json' package's generator
module JsonGenerators

import Sable.Generators
import Sable.Text

@generator("json")
fn json(_ declaration: Declaration, into output: inout Output) {
    let body = TextBuilder()
    body.appendLine("extension \(declaration.name): JsonWritable {")
    body.appendLine("    public fn writeJson(into w: inout JsonWriter) {")
    body.appendLine("        w.beginObject()")
    for field in declaration.fields {
        body.appendLine("        w.field(\"\(field.name)\", self.\(field.name))")
    }
    body.appendLine("        w.endObject()")
    body.appendLine("    }")
    body.appendLine("}")
    output.add(file: "\(declaration.name).json.sb", body.build())
}
```

`App` gets an `extension User: JsonWritable` that nobody wrote. It is in the
build, the language server and the debugger like any other file.

---

## 8.1 Where generators live

A package's generators are the files under its `generators/` directory, next
to `src/`, `tests/` and `benches/`. They are not part of the package's library
or binaries, and a build never links them. The compiler builds them as a
separate program, the *generator program*, and runs that program in the SIL
interpreter.

A generator is a top-level function with `@generator("name")` and this
signature:

```sable
@generator("name")
fn anything(_ declaration: Declaration, into output: inout Output)
```

`name` is the attribute it answers to. `@generator("json")` runs for every
declaration that carries `@json` or `@json(...)`. One package may have any
number of generators, but no two may share a name.

## 8.2 Which declarations a generator sees

A generator runs over two kinds of package:

- the package that has it, and
- every package that depends on that package directly. The package's tests
  also count when it is a dev-dependency.

So writing `@json` in your package takes one thing: depending on the package
whose generator answers to `json`. A transitive dependency's generators do not
apply. If they did, adding a dependency deep in the graph could change what a
package compiles to.

A generator receives one *declaration* at a time: a struct, class, actor,
union or enum at the top level of a file, carrying the generator's attribute.
Nested declarations, functions and members are not passed to generators in
this version (§8.10).

If two generators visible to one package share a name, that is an error
(S3230). It is also an error for a generator to take the name of a built-in
attribute (`derive`, `inline`, `test`, and the others in SYNTAX.md §22),
because the attribute would then mean two things.

## 8.3 What a generator is given

`Sable.Generators` describes the declaration as plain data. It is a value, not
the compiler's symbols:

| Member | What it is |
|---|---|
| `keyword` | How it is declared: `"struct"`, `"class"`, `"actor"`, `"union"` or `"enum"` |
| `name` | The type's name, as declared (`typeName` adds its generic parameters) |
| `moduleName` | The module of the file it is in |
| `isPublic` | Whether it is `public` |
| `typeParameters` | Its generic parameters' names, in order |
| `conformances` | Its base list, each as written |
| `trigger` | The attribute that started this run, with its arguments |
| `attributes` | Every attribute on the declaration |
| `fields` | The stored fields: name, type as written, `var` or `let`, whether it has a default, its attributes |
| `cases` | A union's cases and their payloads, or an enum's cases and their raw values |
| `imports` | The imports of the file it is in |
| `file` | That file's name, for messages |

Types are given as they are written in the source (`String?`, `Int[>..]`,
`Pair<A, B>`), not as resolved types. That keeps a generator's input to what
can be read from one file. Nothing it is given changes when an unrelated file
changes, so its output can be cached on the declaration alone (§8.6). The
generated code is type-checked like everything else, so a type that does not
resolve is still reported, at the generated line that uses it.

An argument is given as its label and its source text, and a string literal is
also given as its value. With `@json(rename: "user_name")`,
`declaration.trigger.argument(named: "rename")` is `"\"user_name\""` and
`declaration.trigger.string(named: "rename")` is `"user_name"`. Fields and
cases have attributes too, read the same way: `field.attribute(named: "skip")`.

## 8.4 What a generator gives back

`Output` collects what a run produces:

```sable
output.add(file: "User.json.sb", text)       // a new source file
output.error("@json cannot be put on a union")  // the build fails, at the attribute
output.warning("field 'cache' is not written")  // a warning, at the attribute
```

The compiler places each file in the module of the declaration that produced
it. It starts the file with that module's `module` line and the imports of the
declaration's file, so a generator writes only declarations, and any imports
of its own that it needs. A file may not declare a `module` itself.

A generated file is named after its package, generator and file name:
`target/generated/<package>/<generator>/<file>`. Diagnostics in it point
there, and the build writes the file to that path so that an editor or a
debugger can open it. It is written again on every build, so editing it
changes nothing, and a file the generators no longer write is removed.

Within one generator, two declarations may not produce the same file name
(S3234). File names may not contain `/` or `..`, and must end in `.sb`.

## 8.5 The rules

A generator must give the same output for the same input, on every machine.
The rules that keep `package.sb` data (docs/05 §5.2.4) apply here too, and are
checked the same way:

- **No effects.** Nothing that reads, writes or waits: no `Runtime`, `print`,
  `Task` or `Region`. (`Sable.Text`'s `joined` does what
  `Runtime.joinStrings` does.) A generator may import only `Sable.Generators`,
  `Sable.Text`, `Sable.Collections` and `Sable.Math`.
- **No conditional compilation.** No `#if` in a generator file. Code that
  depends on the platform belongs in the generated output, behind its own
  `#if`.
- **Bounded.** Each run is limited in steps. A generator that loops forever
  stops with S3231 at the attribute that started it.
- **Total.** A trap, such as an index out of range or a failed `!`, is S3231,
  at the attribute, with the trap's message. The other declarations still
  run.
- **Additive.** Generated files are compiled, but their attributes do not
  trigger generators. The output of a generator is never another
  generator's input, so the order generators run in cannot matter.

Breaking a rule is S3230, reported at the generator's line.

Build scripts (docs/05 §5.6) are the other way of generating code. They can
read files and run tools, inside a sandbox the manifest declares. A generator
can see the package's declarations, but can do nothing else. Code that comes
from the program goes in a generator. Code that comes from outside it, such
as a schema file, a C header or a shader, goes in a build script.

## 8.6 Running them

A build parses the package's files and then runs the generators before it
type-checks anything. The steps are:

1. Find the generators visible to each package (§8.2).
2. Compile each generator program, once per program text. The compiled SIL
   stays in memory for the life of the process.
3. Collect the declarations each one answers to, and give each to its
   generator as data.
4. Parse what comes back with the package's features. Add the files after
   the package's own and compile the whole program.

A run is keyed by the generator program's text, the toolchain and the
declaration's input, and its result is kept. Editing a declaration re-runs the
generators for that declaration only. Editing anything else re-runs nothing.
The language server uses the same cache, so typing in a function body never
runs a generator.

Generators run in the SIL interpreter. They are slower than compiled code
would be, but this affects only the build. What the generator writes is
compiled and optimised like any other source, so the program is as fast as if
the code had been written by hand. Interpreting is also what makes §8.5
possible: the interpreter gives a generator only what the rules allow, so a
dependency's generator cannot touch the machine it runs on.

A loose file (`sable run file.sb`) has no package, so it has no generators.

## 8.7 In the editor

The language server runs a package's generators as `sable check` does, and
compiles their output with the package. Go-to-definition on a generated member
opens the file under `target/generated/`. A generator's error is reported on
the attribute that started it, in the file being edited.

## 8.8 Diagnostics

| Code | Meaning |
|---|---|
| S3230 | A generator breaks a rule: an import or call it may not make, a wrong signature, or a name that is taken |
| S3231 | A generator trapped, or ran out of steps |
| S3232 | A generator reported an error with `output.error` |
| S3233 | A generator reported a warning with `output.warning` |
| S3234 | A generator's output is not a file it may write: a bad name, a name used twice, or a `module` declaration |

## 8.9 Why not macros

Sable has no macros and is not planning any. Generators cover what macros
would be used for, and avoid their costs:

- **The grammar stays fixed.** A macro system needs syntax for quoting and
  splicing, and every tool that reads Sable then has to expand macros first.
  A generator writes plain Sable, so the formatter, the language server and a
  person reading `target/generated/` all see ordinary code.
- **No hygiene problem.** Macros need hygiene rules so that a name a macro
  introduces does not capture a name at the call site. Generated code is a
  separate file, compiled under the same name rules as any other file.
- **What you read is what runs.** A macro's expansion is visible only through
  a tool. A generated file is on disk, can be stepped through, and its
  diagnostics point at its own lines.
- **Incremental by design.** A generator's input is data about one
  declaration, so it can be cached. A macro sees arbitrary tokens, so it
  cannot be.

What macros can do and generators cannot is change existing code: rewrite a
function body, or add syntax. That is the point. `comptime` and `const fn`
(SYNTAX.md §21) cover computing values at compile time, `@derive` covers the
built-in conformances, and generators cover the rest.

## 8.10 Not in this version

- **Functions and members as input.** A generator sees only top-level types.
  Methods, functions and nested types would follow the same model, with a
  `Declaration` for each.
- **Resolved types.** Types are given as written. A generator that needs a
  field's conformances, for example to know whether `T` is `Equatable`, needs a
  read-only view of the semantic model. That view would also have to be part
  of the cache key.
- **Generated files from a separate step.** `sable generate`, which writes
  `target/generated/` without building, is a small addition once there is a
  use for it.
