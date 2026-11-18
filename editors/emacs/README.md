# Sable for Emacs

`sable-mode.el` is a major mode for `.sb` files: highlighting (keywords,
types, attributes, directives, numbers, strings, `//`, `///` and nesting `/* */`
comments), indentation by bracket depth, four spaces as `sable fmt` writes, and
comment commands. It registers `sable lsp` with Eglot (Emacs 29 and later).

```elisp
(add-to-list 'load-path "/path/to/sable/editors/emacs")
(require 'sable-mode)
(add-hook 'sable-mode-hook #'eglot-ensure)
```

Settings (`M-x customize-group RET sable`):

| Variable | Default | |
| --- | --- | --- |
| `sable-program` | `"sable"` | The binary that serves the editor as `sable lsp`. |
| `sable-lsp-initialization-options` | inlay hints and lints on | `(:inlayHints (:enable :json-false))` turns hints off; `:lints` likewise. Set it before Eglot starts. |
| `sable-indent-offset` | `4` | Columns per level. |

Eglot shows inlay hints with `eglot-inlay-hints-mode`, and formats with
`eglot-format-buffer` (`sable fmt`'s formatter, through the server).

With lsp-mode instead of Eglot:

```elisp
(with-eval-after-load 'lsp-mode
  (add-to-list 'lsp-language-id-configuration '(sable-mode . "sable"))
  (lsp-register-client
   (make-lsp-client :new-connection (lsp-stdio-connection '("sable" "lsp"))
                    :activation-fn (lsp-activate-on "sable")
                    :server-id 'sable-lsp)))
```

Not yet tested in Emacs itself: `editors/test` checks only that its keywords
are the VS Code grammar's, and byte-compiles it when `emacs` is on the `PATH`.
