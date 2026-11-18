-- The Sable language server for Neovim 0.11 and later: with this directory
-- on 'runtimepath', `vim.lsp.enable("sable")` starts `sable lsp` for every
-- Sable buffer. Override any field with `vim.lsp.config("sable", { ... })`.
---@type vim.lsp.Config
return {
  cmd = { "sable", "lsp" },
  filetypes = { "sable" },
  -- The nearest package or workspace manifest; else the repository.
  root_markers = { "workspace.sb", "package.sb", "sable.toml", ".git" },
  -- What the server takes at start: both on by default. A change later is
  -- sent as the "sable" section of workspace/didChangeConfiguration.
  init_options = {
    inlayHints = { enable = true },
    lints = { enable = true },
  },
  settings = {
    sable = {
      inlayHints = { enable = true },
      lints = { enable = true },
    },
  },
}
