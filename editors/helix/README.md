# Sable for Helix

`languages.toml` registers the language (`*.sb`, and scripts whose shebang
runs `sable`) and its language server, `sable lsp`. Copy it into
`~/.config/helix/languages.toml`, or a project's `.helix/languages.toml`, with
`sable` on the `PATH`.

Helix sends `[language-server.sable-lsp.config]` as the server's
initialization options: `inlayHints.enable` and `lints.enable` turn inlay hints
and `sable check`'s lints off. Inlay hints show with `lsp.display-inlay-hints =
true` in Helix's `config.toml`.

Helix highlights and indents through tree-sitter, and Sable has no tree-sitter
grammar yet, so files are served by the language server but not coloured.
