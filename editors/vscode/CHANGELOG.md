# Changelog

## 0.3.0

- A Coverage profile in the Test Explorer (VS Code 1.88 or later): the selected tests run with
  `sable test --coverage`, and each file's lines and branches are shown from its `lcov.info`.
- Every folder of a multi-root workspace: `sable.path` and `${workspaceFolder}` are the folder's.
- `sable.inlayHints.enable` and `sable.lints.enable`, to turn off the server's inlay hints and lints.
- Tests of the extension's own code, run under node against a stand-in for VS Code.
- A Debug profile in the Test Explorer: a test runs under the debugger, in a harness built with `-g`.
- `sable.showReferences`, which the language server's references lenses run to open the references view.

## 0.2.0

- Formatting, through the language server's `textDocument/formatting` (`sable fmt`'s one style).
- The Test Explorer: each package's `@test` functions, run from the gutter or the Testing view.
- Tasks for `build`, `check`, `run`, `test`, `bench`, `fmt` and `clean`, with the `$sable` problem matcher.
- Debugging: a `sable` launch configuration that builds with debug information and hands the
  program to lldb-dap or CodeLLDB, with Sable's lldb formatters loaded.
- Run and Debug above `main`; Run in the editor title; `Sable: Explain` as a quick fix on every diagnostic.
- Snippets, a fuller grammar (raw and multi-line strings, directives, doc comments), Sable code
  blocks in Markdown, and `sable.toml` validation with Even Better TOML.

## 0.1.0

- The language server (`sable lsp`) and syntax highlighting.
