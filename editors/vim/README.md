# Sable for Vim and Neovim

Filetype detection (`*.sb`, and scripts whose first line runs `sable`), syntax
highlighting, indentation and comment settings, and — for Neovim — the language
server (`sable lsp`): diagnostics and lints, completion, hover, go to definition,
references, rename, signature help, inlay hints and formatting.

`sable` must be on the `PATH` (or give its path in `cmd` below).

## Install

Put this directory on `runtimepath`. With a plugin manager, point it at the
repository's `editors/vim`; by hand:

```vim
" ~/.vimrc, or init.vim
set runtimepath+=/path/to/sable/editors/vim
```

```lua
-- init.lua
vim.opt.runtimepath:append("/path/to/sable/editors/vim")
```

## Tree-sitter in Neovim

With [nvim-treesitter](https://github.com/nvim-treesitter/nvim-treesitter)
(either its `master` or its `main` branch), Neovim can highlight with the
grammar in `editors/tree-sitter-sable` instead of the syntax file:

```lua
require("sable.treesitter").setup({ path = "/path/to/sable/editors/tree-sitter-sable" })
```

then `:TSInstall sable`. No parser is checked in, so nvim-treesitter generates
it from `grammar.js`, which needs the `tree-sitter` CLI. The highlight queries
are `queries/sable/highlights.scm` here, a link to the grammar's own. Until the
parser is installed, the syntax file highlights.

## The language server in Neovim

Neovim 0.11 and later read `lsp/sable.lua` from the runtime path:

```lua
vim.lsp.enable("sable")

-- Optional: inlay hints, and settings of the server's own.
vim.lsp.inlay_hint.enable(true)
vim.lsp.config("sable", {
  settings = { sable = { lints = { enable = false } } },
})
```

On older versions, start it from an autocommand:

```lua
vim.api.nvim_create_autocmd("FileType", {
  pattern = "sable",
  callback = function(args)
    vim.lsp.start({
      name = "sable",
      cmd = { "sable", "lsp" },
      root_dir = vim.fs.root(args.buf, { "workspace.sb", "package.sb", "sable.toml", ".git" }),
      init_options = { inlayHints = { enable = true }, lints = { enable = true } },
    })
  end,
})
```

`gq` formats with Vim's own rules; to format with `sable fmt` (through the
server), map `vim.lsp.buf.format()`.

## The language server in Vim

Vim has no client of its own; with [vim-lsp](https://github.com/prabirshrestha/vim-lsp):

```vim
autocmd User lsp_setup call lsp#register_server({
    \ 'name': 'sable',
    \ 'cmd': ['sable', 'lsp'],
    \ 'allowlist': ['sable'],
    \ 'initialization_options': {'inlayHints': {'enable': v:true}, 'lints': {'enable': v:true}},
    \ })
```

## Settings the server takes

Both in `init_options` (at start) and under `sable` in `settings` (later):

| Setting | Default | |
| --- | --- | --- |
| `inlayHints.enable` | `true` | Inferred types, and the roots a returned reference may point into. |
| `lints.enable` | `true` | `sable check`'s lints as you type, at the package's `[lints]` levels. |

## Tests

`node --test editors/test/editors.test.js` checks that every keyword of the VS Code grammar is
highlighted here, and, when `vim` is on the `PATH`, opens a Sable file in it
headless and checks the filetype, the highlighting of a few tokens, and the
indentation of a block.
