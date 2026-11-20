;;; sable-mode.el --- Major mode for the Sable language -*- lexical-binding: t; -*-

;; Version: 0.1.0
;; Package-Requires: ((emacs "29.1"))
;; Keywords: languages
;; URL: https://github.com/Rikarin/Sable

;;; Commentary:

;; Highlighting, indentation and comments for Sable (`.sb' files), and the
;; language server, `sable lsp', through Eglot:
;;
;;   (add-to-list 'load-path "/path/to/sable/editors/emacs")
;;   (require 'sable-mode)
;;   (add-hook 'sable-mode-hook #'eglot-ensure)   ; optional
;;
;; The keywords are those of the VS Code grammar
;; (editors/vscode/syntaxes/sable.tmLanguage.json); editors/test checks
;; that every keyword there is one here too.

;;; Code:

(require 'rx)

(defgroup sable nil
  "The Sable programming language."
  :group 'languages
  :prefix "sable-")

(defcustom sable-indent-offset 4
  "Columns per level of indentation: four, as `sable fmt' writes."
  :type 'integer
  :safe #'integerp)

(defcustom sable-program "sable"
  "The `sable' binary, which serves the editor as `sable lsp'."
  :type 'string)

(defcustom sable-lsp-initialization-options
  '(:inlayHints (:enable t) :lints (:enable t))
  "What the language server is started with: inlay hints and lints, on or off.
Use `:json-false' for off."
  :type 'sexp)

(defconst sable--keywords
  '(;; Control flow.
    "if" "else" "guard" "match" "switch" "case" "default" "fallthrough"
    "while" "repeat" "for" "in" "break" "continue"
    "return" "throw" "yield" "try" "catch" "await" "defer" "do" "where"
    ;; Declarations.
    "fn" "let" "var" "const" "struct" "class" "actor" "union" "enum" "protocol"
    "extension" "alias" "associatedtype" "init" "deinit" "subscript" "operator"
    "region" "module" "import"
    ;; Modifiers.
    "public" "private" "internal" "package" "static" "mutating" "nonmutating"
    "override" "open" "final" "sealed" "abstract" "required" "convenience" "lazy" "weak"
    "unowned" "async" "throws" "rethrows" "inout" "out" "ref" "consuming"
    "borrowing" "move" "unsafe" "unchecked" "comptime" "some" "any"
    "nonisolated" "prefix" "postfix" "using"
    ;; Operators spelled as words.
    "is" "as")
  "Sable's keywords.")

(defconst sable--constants '("true" "false" "none" "self" "Self" "super")
  "Words that name a value.")

(defconst sable--reserved '("macro" "trait" "impl" "become" "effect" "handler")
  "Words reserved for later, which are errors today.")

(defconst sable--builtin-types
  '("Int" "UInt" "I8" "I16" "I32" "I64" "I128" "U8" "U16" "U32" "U64" "U128"
    "F16" "F32" "F64"
    "Bool" "Char" "String" "Never" "Void")
  "The built-in types.")

(defconst sable--font-lock-keywords
  `((,(rx bol (* space) "#" (or "if" "elseif" "else" "endif" "error") word-end) . font-lock-preprocessor-face)
    (,(rx "@" (any alpha "_") (* (any alnum "_"))) . font-lock-preprocessor-face)
    (,(regexp-opt sable--reserved 'symbols) . font-lock-warning-face)
    (,(regexp-opt sable--keywords 'symbols) . font-lock-keyword-face)
    (,(regexp-opt sable--constants 'symbols) . font-lock-constant-face)
    (,(regexp-opt sable--builtin-types 'symbols) . font-lock-type-face)
    ;; An accessor's name, where an accessor begins: `get {', `set(v)', `get =>'.
    (,(rx symbol-start (group (or "get" "set" "willSet" "didSet")) symbol-end (* space) (or "{" "(" "=>"))
     1 font-lock-keyword-face)
    ;; A closure's parameters by position: `$0', `$1'.
    (,(rx "$" (+ digit)) . font-lock-variable-name-face)
    (,(rx symbol-start "fn" (+ space) (group (? "`") (any alpha "_") (* (any alnum "_")) (? "`")))
     1 font-lock-function-name-face)
    (,(rx symbol-start (or "struct" "class" "actor" "union" "enum" "protocol" "alias" "associatedtype" "extension")
          (+ space) (group (any alpha "_") (* (any alnum "_" "."))))
     1 font-lock-type-face)
    (,(rx symbol-start (or "let" "var" "const") (+ space) (group (any lower "_") (* (any alnum "_"))))
     1 font-lock-variable-name-face)
    (,(rx symbol-start (or (seq "0x" (any hex) (* (any hex "_")))
                           (seq "0b" (any "01") (* (any "01_")))
                           (seq "0o" (any "0-7") (* (any "0-7_")))
                           (seq digit (* (any digit "_"))
                                (? "." digit (* (any digit "_")))
                                (? (any "eE") (? (any "+-")) digit (* (any digit "_")))))
          symbol-end)
     . font-lock-constant-face)
    (,(rx symbol-start upper (* (any alnum "_")) symbol-end) . font-lock-type-face))
  "What `sable-mode' highlights, beyond strings and comments.")

(defvar sable-mode-syntax-table
  (let ((table (make-syntax-table)))
    ;; `//' to the end of the line, and `/* */', which nests.
    (modify-syntax-entry ?/ ". 124b" table)
    (modify-syntax-entry ?* ". 23n" table)
    (modify-syntax-entry ?\n "> b" table)
    (modify-syntax-entry ?\" "\"" table)
    (modify-syntax-entry ?' "\"" table)
    (modify-syntax-entry ?\\ "\\" table)
    (modify-syntax-entry ?_ "_" table)
    (dolist (char '(?+ ?- ?= ?< ?> ?& ?| ?! ?% ?^ ?~ ??))
      (modify-syntax-entry char "." table))
    (modify-syntax-entry ?@ "'" table)
    (modify-syntax-entry ?# "'" table)
    table)
  "Syntax table for `sable-mode'.")

(defun sable--doc-comment-face (state)
  "The face of a comment or string at STATE: `///' is documentation."
  (if (and (nth 4 state)
           (save-excursion
             (goto-char (nth 8 state))
             (looking-at-p "///")))
      font-lock-doc-face
    (if (nth 3 state) font-lock-string-face font-lock-comment-face)))

(defun sable-indent-line ()
  "Indent the line one level per bracket open around it, as `sable fmt' does."
  (interactive)
  (let* ((depth (save-excursion
                  (beginning-of-line)
                  (car (syntax-ppss))))
         (closes (save-excursion
                   (back-to-indentation)
                   (looking-at-p "[]})]")))
         (column (* sable-indent-offset (max 0 (if closes (1- depth) depth))))
         (offset (- (current-column) (current-indentation))))
    (indent-line-to column)
    (when (> offset 0)
      (move-to-column (+ column offset)))))

;;;###autoload
(define-derived-mode sable-mode prog-mode "Sable"
  "Major mode for editing Sable source."
  :syntax-table sable-mode-syntax-table
  (setq-local comment-start "// ")
  (setq-local comment-end "")
  (setq-local comment-start-skip (rx (or (seq "/" (+ "/")) (seq "/" (+ "*"))) (* space)))
  (setq-local indent-tabs-mode nil)
  (setq-local tab-width sable-indent-offset)
  (setq-local indent-line-function #'sable-indent-line)
  (setq-local electric-indent-chars (append "{}()[]" electric-indent-chars))
  (setq-local font-lock-defaults
              (list sable--font-lock-keywords nil nil nil nil
                    (cons 'font-lock-syntactic-face-function #'sable--doc-comment-face))))

;;;###autoload
(add-to-list 'auto-mode-alist '("\\.sb\\'" . sable-mode))

;;;###autoload
(add-to-list 'interpreter-mode-alist '("sable" . sable-mode))

;; The language server, for Eglot (built into Emacs 29).
(with-eval-after-load 'eglot
  (defvar eglot-server-programs)
  (add-to-list 'eglot-server-programs
               `(sable-mode . (,sable-program "lsp" :initializationOptions ,sable-lsp-initialization-options))))

(provide 'sable-mode)

;;; sable-mode.el ends here
