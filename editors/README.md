# Editor support

Every editor gets the language server, `sable lsp`: diagnostics with
`sable check`'s lints, completion, hover, go to definition, references, rename,
signature help, inlay hints and formatting. The server takes two settings, as
initialization options or under `sable` in `workspace/didChangeConfiguration`:
`inlayHints.enable` and `lints.enable`, both on by default. It serves every
folder of a multi-root workspace.

| Editor | Directory | Highlighting | Language server |
| --- | --- | --- | --- |
| VS Code | [`vscode`](vscode) | TextMate grammar | built in, with tasks, the Test Explorer and debugging |
| Vim, Neovim | [`vim`](vim) | Vim syntax file; in Neovim, the tree-sitter grammar through nvim-treesitter | Neovim 0.11's `vim.lsp.enable("sable")`; vim-lsp in Vim |
| Helix | [`helix`](helix) | the tree-sitter grammar, built by Helix | `languages.toml` |
| Emacs | [`emacs`](emacs) | `sable-mode` | Eglot (or lsp-mode) |
| Sublime Text | [`sublime`](sublime) | the TextMate grammar, generated from VS Code's | the LSP package |

[`tree-sitter-sable`](tree-sitter-sable) is a tree-sitter grammar written by hand
from the spec (`grammar.js`, `tree-sitter.json`, `queries/highlights.scm`), for
Helix's, Zed's and Neovim's highlighting. No tree-sitter CLI was at hand to
generate its parser, so it has not been run over real files yet: the tests
check it as data — every rule it names exists, it has every keyword the VS
Code grammar highlights, the highlight queries name only its nodes, fields and
tokens — and, where `tree-sitter` is installed, generate the parser and parse
`tests/corpus` with it. It follows the syntax as it now is: generators
(`yield`), `for try`, `unsafe fn`, `throws(E)` and `rethrows(E)` on functions and function types, `async let`,
arrow closures (`(x: Int) => x * 2`, `x => x.name`) and `$0`, labelled trailing
closures, `x unchecked as T`, `try f() catch fallback` and `T[..N]`.

Not yet: a Zed extension. Zed takes a language server only from an extension
compiled to WebAssembly (Rust, not a configuration file), and builds a grammar
from a repository with its parser generated, which this one does not have yet.

`node --test editors/test/editors.test.js` checks the non-VS Code files; `npm
test` in `vscode` checks the extension. The extension's tests run its code in
Node against a stand-in for the `vscode` module (`vscode/test/vscode-mock.js`):
activation, the commands, the server's start and settings, multi-root
folders, tasks, lenses, quick fixes, debugging (CodeLLDB and lldb-dap), the
Test Explorer and coverage. Running them inside VS Code's own test host needs
`@vscode/test-electron` and a downloaded VS Code, which the repository does
not fetch; the grammar test needs `vscode-textmate` (`npm install` in
`vscode`).
