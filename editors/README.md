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
| Vim, Neovim | [`vim`](vim) | Vim syntax file | Neovim 0.11's `vim.lsp.enable("sable")`; vim-lsp in Vim |
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
`tests/corpus` with it. Not yet: a Zed extension, which would package that
grammar.

`node --test editors/test/editors.test.js` checks the non-VS Code files; `npm
test` in `vscode` checks the extension.
