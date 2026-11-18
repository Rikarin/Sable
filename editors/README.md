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
| Helix | [`helix`](helix) | none yet (Helix needs a tree-sitter grammar) | `languages.toml` |
| Emacs | [`emacs`](emacs) | `sable-mode` | Eglot (or lsp-mode) |
| Sublime Text | [`sublime`](sublime) | the TextMate grammar, generated from VS Code's | the LSP package |

Not yet: a tree-sitter grammar (for Helix's and Zed's highlighting, and Neovim's
tree-sitter), and a Zed extension, which needs that grammar.

`node --test editors/test/editors.test.js` checks the non-VS Code files; `npm
test` in `vscode` checks the extension.
