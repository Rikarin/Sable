# Sable for Visual Studio Code

Support for the [Sable](https://github.com/sable-lang/sable) language, from the `sable` toolchain
itself: the editor features come from `sable lsp`, and building, running, testing and debugging go
through `sable` too.

## Features

- **Diagnostics** as you type, with the compiler's own messages and codes. Every diagnostic has a
  quick fix, **Explain S####**, that opens the code's page from `sable explain`.
- **Completion, hover, signature help, go to definition, find references, rename** and the
  **outline**, across the workspace.
- **Inlay hints** for inferred types, and for the roots the compiler infers.
- **Formatting** (`Format Document`, or format on save): `sable fmt`'s one style. A file with a
  syntax error is left alone.
- **Run and Debug** above a program's `main`, and in the editor title; `Cmd/Ctrl+F5` runs.
- **The Test Explorer**: each package's `@test` functions, listed by `sable test --list` and run
  from the gutter, with failures (and the values that differ) shown where the test is.
- **Tasks** for `build`, `check`, `run`, `test`, `bench`, `fmt` and `clean` in every package
  (Terminal → Run Task → sable). Their diagnostics reach the Problems panel through `$sable`.
- **Debugging** with lldb: Sable's formatters are loaded, so a `String` shows its text, a list its
  elements, and a union its case.
- **Snippets**, highlighting for Sable code blocks in Markdown, and, with
  [Even Better TOML](https://marketplace.visualstudio.com/items?itemName=tamasfe.even-better-toml),
  completion and validation in `sable.toml`.

## Requirements

- `sable` on the `PATH`, or the `sable.path` setting pointing at it. In the compiler's own
  repository the extension uses the driver built under `src/Sable.Driver/bin/` when there is no
  `sable` on the `PATH`.
- `clang`, which `sable` builds with.
- For debugging, an lldb debug adapter: LLVM's
  [LLDB DAP](https://marketplace.visualstudio.com/items?itemName=llvm-vs-code-extensions.lldb-dap)
  (preferred) or [CodeLLDB](https://marketplace.visualstudio.com/items?itemName=vadimcn.vscode-lldb).

## Debugging

Press F5 in a `.sab` file with no `launch.json`. The extension debugs the package the file is in, or
the file alone if it is in no package. To keep a configuration, add one of type `sable`:

```jsonc
{
  "type": "sable",
  "request": "launch",
  "name": "Debug the package",
  "args": [],
  "cwd": "${workspaceFolder}"
  // "program": "${file}"          a .sab file on its own, instead of the package
  // "bin": "tool"                 a package's binary, when it has several
  // "buildArgs": ["--features", "extra"]
}
```

The extension builds the program with debug information (`sable build`, whose dev profile carries
it, or `sable build <file> -g`). It writes Sable's lldb plugin with `sable debug --write-plugin`, and
starts the debug adapter with the plugin imported.

## Settings

| Setting | Default | |
| --- | --- | --- |
| `sable.path` | `sable` | The `sable` binary. `${workspaceFolder}` and `~` are expanded. |
| `sable.server.enable` | `true` | Run the language server. |
| `sable.codeLens.enable` | `true` | Run and Debug above `main`. |
| `sable.trace.server` | `off` | Log the language server's messages to the Sable output. |

## Development

```sh
cd editors/vscode
npm install
npm run check      # type-check the JavaScript (tsc, checkJs)
npm test           # the grammar, against samples and every .sab file in the repository
npm run package    # sable.vsix
```

To try the extension, open `editors/vscode` in VS Code and run **Run the extension** (F5). To
install the package, run `code --install-extension sable.vsix`.
