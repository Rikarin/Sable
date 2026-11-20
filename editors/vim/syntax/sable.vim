" Vim syntax file
" Language: Sable
" Files:    *.sb
"
" The same classes of token as the VS Code grammar
" (editors/vscode/syntaxes/sable.tmLanguage.json): editors/test checks that
" every keyword there is a keyword here too.

if exists("b:current_syntax")
  finish
endif

let s:cpo_save = &cpo
set cpo&vim

syn case match

" Keywords, as the VS Code grammar groups them.
syn keyword sableConditional if else guard match switch case default fallthrough
syn keyword sableRepeat      while repeat for in break continue
syn keyword sableFlow        return throw yield try catch await defer do where
syn keyword sableStorage     fn let var const struct class actor union enum protocol extension alias associatedtype
syn keyword sableStorage     init deinit subscript operator region
syn keyword sableInclude     module import
syn keyword sableModifier    public private internal package static mutating nonmutating override open final sealed abstract
syn keyword sableModifier    required convenience lazy weak unowned async throws rethrows inout out ref consuming
syn keyword sableModifier    borrowing move unsafe unchecked comptime some any nonisolated prefix postfix using
syn keyword sableBoolean     true false
syn keyword sableNone        none
syn keyword sableSelf        self Self super
syn keyword sableOperatorWord is as
syn keyword sableReserved    macro trait impl become effect handler
syn keyword sableBuiltinType Int UInt I8 I16 I32 I64 I128 U8 U16 U32 U64 U128 F16 F32 F64 Bool Char String Never Void

" Declared names.
syn match sableFunction "\<fn\s\+\zs`\?\h\w*`\?"
syn match sableTypeDecl "\<\(struct\|class\|actor\|union\|enum\|protocol\|alias\|associatedtype\|extension\)\s\+\zs\h[A-Za-z0-9_.]*"
syn match sableType     "\<\u\w*\>"
syn match sableAccessor "\<\(get\|set\|willSet\|didSet\)\>\ze\s*\({\|(\|=>\)"
syn match sableShorthand "\$\d\+"

" Attributes and directives.
syn match sableAttribute "@\h\w*"
syn match sableDirective "^\s*#\(if\|elseif\|else\|endif\|error\)\>"
syn match sableShebang   "\%^#!.*$"

" Numbers: hex, binary, octal, decimal and floats, '_' between digits.
syn match sableNumber "\<0x\x[0-9A-Fa-f_]*\>"
syn match sableNumber "\<0b[01][01_]*\>"
syn match sableNumber "\<0o\o[0-7_]*\>"
syn match sableNumber "\<\d[0-9_]*\(\.\d[0-9_]*\)\=\([eE][+-]\=\d[0-9_]*\)\=\>"

" Strings: escapes and \( interpolation \) in plain ones; raw ones
" (#"…"#, #"""…"""#) take neither.
syn match  sableEscape        contained "\\\([nrt0\\\"']\|u{\x\+}\)"
syn region sableInterpolation contained matchgroup=sableInterpolationDelimiter start="\\(" end=")" contains=TOP,sableInterpolationParens
syn region sableInterpolationParens contained transparent start="(" end=")" contains=TOP,sableInterpolationParens
syn region sableString        start=+"""+ end=+"""+ contains=sableEscape,sableInterpolation,@Spell
syn region sableString        start=+"+ skip=+\\\\\|\\"+ end=+"+ end=+$+ oneline contains=sableEscape,sableInterpolation,@Spell
syn region sableRawString     start=+\z(#\+\)"""+ end=+"""\z1+
syn region sableRawString     start=+\z(#\+\)"+ end=+"\z1+
syn match  sableChar          "'\([^'\\]\|\\.\|\\u{\x\+}\)'"

" Comments: '///' is documentation; block comments nest.
syn keyword sableTodo contained TODO FIXME XXX NOTE
syn match   sableDocKeyword contained "\(///\s*-\s*\)\@<=\(Parameters\=\|Returns\|Throws\|Note\|Warning\|Precondition\|Complexity\)\>"
syn match   sableDocCode    contained "`[^`]*`"
syn match   sableDocComment "///.*$" contains=sableTodo,sableDocKeyword,sableDocCode,@Spell
syn match   sableComment    "//\(/\)\@!.*$" contains=sableTodo,@Spell
syn region  sableComment    start="/\*" end="\*/" contains=sableComment,sableTodo,@Spell

syn sync minlines=200

hi def link sableConditional           Conditional
hi def link sableRepeat                Repeat
hi def link sableFlow                  Statement
hi def link sableStorage               StorageClass
hi def link sableInclude               Include
hi def link sableModifier              StorageClass
hi def link sableBoolean               Boolean
hi def link sableNone                  Constant
hi def link sableSelf                  Identifier
hi def link sableShorthand             Identifier
hi def link sableOperatorWord          Operator
hi def link sableReserved              Error
hi def link sableBuiltinType           Type
hi def link sableType                  Type
hi def link sableTypeDecl              Typedef
hi def link sableFunction              Function
hi def link sableAccessor              StorageClass
hi def link sableAttribute             PreProc
hi def link sableDirective             PreCondit
hi def link sableShebang               Comment
hi def link sableNumber                Number
hi def link sableString                String
hi def link sableRawString             String
hi def link sableChar                  Character
hi def link sableEscape                SpecialChar
hi def link sableInterpolationDelimiter Delimiter
hi def link sableTodo                  Todo
hi def link sableComment               Comment
hi def link sableDocComment            SpecialComment
hi def link sableDocKeyword            SpecialComment
hi def link sableDocCode               SpecialComment

let b:current_syntax = "sable"

let &cpo = s:cpo_save
unlet s:cpo_save
