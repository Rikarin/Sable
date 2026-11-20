-- Tree-sitter highlighting for Sable in Neovim, from the grammar in
-- editors/tree-sitter-sable, through nvim-treesitter:
--
--   require("sable.treesitter").setup({ path = "/path/to/sable/editors/tree-sitter-sable" })
--
-- then `:TSInstall sable`. The grammar has no generated parser checked in,
-- so nvim-treesitter generates it (it needs the tree-sitter CLI on the PATH).
-- The highlight queries are queries/sable/highlights.scm in this directory,
-- a link to the grammar's own. Without this, the Vim syntax file highlights.
local M = {}

---@param opts { path: string }
function M.setup(opts)
  local path = assert(opts and opts.path, "sable.treesitter.setup: give the path of editors/tree-sitter-sable")
  local ok, parsers = pcall(require, "nvim-treesitter.parsers")
  if not ok then
    vim.notify("sable.treesitter: nvim-treesitter is not installed", vim.log.levels.WARN)
    return
  end

  if type(parsers.get_parser_configs) == "function" then
    -- nvim-treesitter's master branch.
    parsers.get_parser_configs().sable = {
      install_info = { url = path, files = { "src/parser.c" }, requires_generate_from_grammar = true },
      filetype = "sable",
    }
  else
    -- Its main branch reads the table again on each :TSUpdate.
    vim.api.nvim_create_autocmd("User", {
      pattern = "TSUpdate",
      callback = function()
        require("nvim-treesitter.parsers").sable = {
          install_info = { path = path, generate = true },
        }
      end,
    })
  end

  vim.treesitter.language.register("sable", "sable")
  vim.api.nvim_create_autocmd("FileType", {
    pattern = "sable",
    callback = function(args)
      -- Falls back to the syntax file while the parser is not installed.
      pcall(vim.treesitter.start, args.buf, "sable")
    end,
  })
end

return M
