# 8. Source generators

A source generator is a Sable function that the compiler runs while it
compiles a package. It reads the declarations that carry its attribute and
writes new Sable source. The new files are compiled with the package as if
someone had written them by hand. This is the model of Roslyn's source
generators: the generator adds code, and never changes the code that is
already there.

Generators are what Sable has instead of macros. §8.10 says why.

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
to `src/`, `tests/` and `benches/`, except `generators/tests/` (§8.8). They are
not part of the package's library or binaries, and a build never links them.
The compiler builds them as a separate program, the *generator program*, and
runs that program in the SIL interpreter.

A generator is a top-level function with `@generator("name")`. There are two
kinds, told apart by their first parameter:

```sable
// Run once for each declaration that carries @json.
@generator("json")
fn json(_ declaration: Declaration, into output: inout Output)

// Run once for each package, with every declaration that carries @route.
@generator("route")
fn routes(_ pkg: Package, into output: inout Output)
```

`name` is the attribute it answers to. `@generator("json")` runs for every
declaration that carries `@json` or `@json(...)`. One package may have any
number of generators, but no two may share a name.

A *declaration generator* sees one declaration at a time, so each run can be
cached on that declaration alone. A *package generator* sees all of them at
once, for output that needs the whole set: a table of routes, a registry of
services, a list of commands. It runs when at least one declaration carries
its attribute. With `@generator("name", always: true)`, it runs for every
package that can see it, even one with no such declaration. That is how a
generator adds code unconditionally, such as helper types its other output
uses, or types generated from input files (§8.3).

The compiler rejects an attribute it does not know (S3092), so a generator
also names the attributes it reads on members and parameters:
`@generator("json", members: ["rename", "skip"])`. A package that can see the
generator may then write `@json` where a generator is given declarations
(§8.2), and `@rename(...)` or `@skip` on fields, cases and parameters.
Written anywhere else, they are S3093.

## 8.2 Which declarations a generator sees

A generator runs over two kinds of package:

- the package that has it, and
- every package that depends on that package directly. The package's tests
  also count when it is a dev-dependency.

So writing `@json` in your package takes one thing: depending on the package
whose generator answers to `json`. A transitive dependency's generators do not
apply. If they did, adding a dependency deep in the graph could change what a
package compiles to.

A generator is given the declarations that carry its attribute:

- structs, classes, actors, unions and enums, at the top level of a file or
  nested in another type, and
- functions at the top level of a file, and methods in a type or an
  extension.

If two generators visible to one package share a name, that is an error
(S3230). It is also an error for a generator to take the name of a built-in
attribute (`derive`, `inline`, `test`, and the others in SYNTAX.md §22),
because the attribute would then mean two things.

## 8.3 What a generator is given

`Sable.Generators` describes each declaration as plain data. It is a value,
not the compiler's symbols:

| Member | What it is |
|---|---|
| `keyword` | How it is declared: `"struct"`, `"class"`, `"actor"`, `"union"`, `"enum"` or `"fn"` |
| `name` | Its name, as declared (`typeName` adds a type's generic parameters) |
| `container` | For a nested type or a method, the type it is in, as `Outer.Inner`; otherwise `""` |
| `moduleName` | The module of the file it is in |
| `isPublic`, `isStatic` | Its modifiers |
| `typeParameters` | Its generic parameters' names, in order |
| `conformances` | Its base list, each as written |
| `allConformances` | Every protocol it conforms to, by name, its base list's and those declared elsewhere |
| `superclass` | A class's superclass, by name, or none |
| `trigger` | The attribute that started this run, with its arguments |
| `attributes` | Every attribute on the declaration |
| `fields` | A type's stored fields: name, type, `var` or `let`, whether it has a default, its attributes |
| `cases` | A union's cases and their payloads, or an enum's cases and their raw values |
| `parameters` | A function's parameters: label, name, type, whether it has a default, its attributes |
| `returnType`, `returned` | A function's return type, as written and resolved |
| `isAsync`, `throws` | A function's effects |
| `imports` | The imports of the file it is in |
| `file` | That file's name, for messages |
| `location` | Where it is, for messages (§8.4) |

Each type is given twice. `type` is the type as it is written in the source
(`String?`, `Int[>..]`, `Pair<A, B>`). `resolved` is a `TypeRef`, what the
compiler resolved it to:

| `TypeRef` member | What it is |
|---|---|
| `kind` | `"struct"`, `"class"`, `"actor"`, `"union"`, `"enum"`, `"protocol"`, `"optional"`, `"array"`, `"dictionary"`, `"tuple"`, `"function"`, `"parameter"` for a generic parameter, or `"unresolved"` |
| `name` | The type's name, without its module or arguments |
| `moduleName` | The module that declares it, or `""` |
| `text` | The whole type, as the compiler writes it |
| `arguments` | Its generic arguments; an optional's or an array's element; a dictionary's key and value |
| `conformances` | Every protocol it conforms to, by name |
| `attributes` | The attributes on its declaration, by name |

With these, a JSON generator can tell that a field's type is itself `@json`
(`field.resolved.has(attribute: "json")`), or that it is an array of
`Equatable` values (`field.resolved.kind == "array" &&
field.resolved.arguments[0].conforms(to: "Equatable")`).

What resolves is what the package declares, what its dependencies declare,
and what their generators wrote. The package's own generators' output is not
there, because a generator's output is never another generator's input
(§8.5). A type that does not resolve is `"unresolved"`. The generated code is
type-checked like everything else, so such a type is still reported where
generated code uses it.

An argument is given as its label and its source text, and a string literal is
also given as its value. With `@json(rename: "user_name")`,
`declaration.trigger.argument(named: "rename")` is `"\"user_name\""` and
`declaration.trigger.string(named: "rename")` is `"user_name"`. Fields, cases
and parameters have attributes too, read the same way:
`field.attribute(named: "skip")`.

A package generator is given a `Package`: the package's `name`, its
`declarations` in source order, and its `inputs`. Inputs are files that the
package's manifest lists for its generators. Each is a directory, meaning
every file under it, or a single file:

```sable
let manifest = Package {
    name: "api",
    version: "1.0.0",
    generatorInputs: ["schemas/"],
}
```

Each input is given as its path relative to the package and its text. This
gives a generator data from outside the program without breaking §8.5,
because the files' contents are part of its input and its cache key. A listed
file that does not exist is a manifest error.

## 8.4 What a generator gives back

`Output` collects what a run produces:

```sable
output.add(file: "User.json.sb", text)              // a new source file
output.add(file: "Routes.sb", text, inModule: "Api") // in a module of the package's choosing
output.error("@json cannot be put on a union")       // the build fails, at the attribute
output.warning("'cache' is not written", at: field.location)  // a warning, at the field
```

A message is reported at the attribute that started the run, unless it says
`at:` a declaration's, field's, case's or parameter's `location`. A package
generator's message without `at:` is reported at the first declaration's
attribute.

The compiler places each file in a module. By default this is the module of
the declaration that produced it. For a package generator it is the first
declaration's module, or, when there is none, the module of the package's
first library file (or of its first binary). `inModule:` names another module,
which must be one the package declares. The compiler starts the file with the
module's `module` line and the imports of the declaration's file, so a
generator writes only declarations and any imports of its own. A file may not
declare a `module` itself.

A generated file is named after its package, generator and file name:
`target/generated/<package>/<generator>/<file>`. Diagnostics in it point
there, and the build writes the file to that path so that an editor or a
debugger can open it. It is written again on every build, so editing it
changes nothing, and a file the generators no longer write is removed.

Within one generator, two runs may not produce the same file name (S3234).
File names may not contain `/` or `..`, and must end in `.sb`.

## 8.5 The rules

A generator must give the same output for the same input, on every machine.
The rules that keep `package.sb` data (docs/05 §5.2.4) apply here too, and are
checked the same way:

- **No effects.** Nothing that reads, writes or waits: no `Runtime`, `print`,
  `Task` or `Region`. (`Sable.Text`'s `joined` does what
  `Runtime.joinStrings` does.) A generator may import only `Sable.Generators`,
  `Sable.Text`, `Sable.Collections` and `Sable.Math`. The only files it sees
  are the inputs its package lists (§8.3).
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
read any file and run tools, inside a sandbox the manifest declares. A
generator can see the package's declarations and its listed inputs, and can
do nothing else. Code that comes from the program, or from data files the
package carries, goes in a generator. Code that needs a tool, such as a C
header's bindings or a compiled shader, goes in a build script.

## 8.6 Running them

A build parses the package's files and then runs the generators before it
type-checks anything. The steps are:

1. Find the generators visible to each package (§8.2).
2. Compile each generator program, once per program text. The compiled SIL
   stays in memory for the life of the process.
3. Bind the package's declarations, without the bodies, to resolve the types
   given to the generators (§8.3).
4. Collect the declarations each generator answers to, and give them to it as
   data: one run per declaration for a declaration generator, one per package
   for a package generator.
5. Parse what comes back with the package's features. Add the files after
   the package's own and compile the whole program.

A run is keyed by the generator program's text, the toolchain and the run's
input, and its result is kept. A declaration generator's input is one
declaration with the types it uses, resolved. A package generator's input is
every declaration with its attribute, and the inputs. Editing a declaration
re-runs the declaration generators for that declaration, and the package
generators whose input it is in. Editing a function body re-runs nothing. The
language server uses the same cache, so typing in a function body never runs
a generator.

Generators run in the SIL interpreter. They are slower than compiled code
would be, but this affects only the build. What the generator writes is
compiled and optimised like any other source, so the program is as fast as if
the code had been written by hand. Interpreting is also what makes §8.5
possible: the interpreter gives a generator only what the rules allow, so a
dependency's generator cannot touch the machine it runs on.

A loose file (`sable run file.sb`) has no package, so it has no generators.

## 8.7 In the editor

The language server runs a package's generators as `sable check` does, and
compiles their output with the package. It runs its dependencies' generators
too, on their files as they are in the editor, so a change to a dependency
shows in the packages that use it without a reload. Go-to-definition on a
generated member opens the file under `target/generated/`. A generator's
message is reported where it points, in the file being edited.

A package's `generators/` directory is a project of its own. Its files are
checked against §8.5 as they are edited. Each case under
`generators/tests/` is a project of its own too, compiled with the package's
generators.

## 8.8 Testing a generator

A generator is tested with snapshots. Each directory under
`generators/tests/` is a case:

```
json/generators/tests/nested-types/
    model.sb                       the source the generators run over
    inputs/schema.json             inputs, for package generators (optional)
    expected/json/User.json.sb     each file the generators write
    expected/diagnostics.txt       each message they report (optional)
```

`sable test` runs the package's generators over each case, as for a package
whose files are the case's `.sb` files, and compares what they write and
report with `expected/`. A difference fails the test and shows both
versions. `sable test --bless` writes `expected/` from what the generators
produce, to start a case or to accept a change after reading the diff. Then
`sable test` runs the package's `@test` functions as usual.

The case's files are compiled too, with what the generators wrote. Without
that, a generator could write code that does not compile and its snapshot
would still pass. A case that should not compile, because it tests a
generator's error, says so with an `expected/diagnostics.txt` that has an
error in it.

## 8.9 Diagnostics

| Code | Meaning |
|---|---|
| S3230 | A generator breaks a rule: an import or call it may not make, a wrong signature, or a name that is taken |
| S3231 | A generator trapped, or ran out of steps |
| S3232 | A generator reported an error with `output.error` |
| S3233 | A generator reported a warning with `output.warning` |
| S3234 | A generator's output is not a file it may write: a bad name, a name used twice, a `module` declaration, or a module the package does not declare |

## 8.10 Why not macros

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

## 8.11 Next

- **`sable generate`**, which writes `target/generated/` without building, is a
  small addition once there is a use for it.
- **Stored fields from a generator.** An extension cannot add stored fields,
  so a generator cannot either. Roslyn's generators add them through
  `partial` types. Sable has no `partial`, and adding it would be a language
  decision, not a generator one.
