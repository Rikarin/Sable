; Highlighting for Sable, in the capture names Helix, Zed and Neovim share.

(line_comment) @comment
(block_comment) @comment
(doc_comment) @comment.documentation
(shebang) @keyword.directive
(directive_keyword) @keyword.directive

; ---- Literals -------------------------------------------------------------

(integer_literal) @number
(float_literal) @number.float
(boolean_literal) @boolean
(none) @constant.builtin
(string_literal) @string
(multiline_string_literal) @string
(raw_string_literal) @string
(character_literal) @character
(escape_sequence) @string.escape
(interpolation
  "\\(" @punctuation.special
  ")" @punctuation.special)

; ---- Declarations ---------------------------------------------------------

(module_declaration name: (qualified_name) @module)
(import_declaration name: (qualified_name) @module)

(function_declaration name: (identifier) @function)
(function_declaration name: (operator_name) @operator)
(operator_declaration name: (operator_name) @operator)
(parameter label: (identifier) @label)
(parameter name: (identifier) @variable.parameter)
(type_parameter name: (identifier) @type)

(struct_declaration name: (identifier) @type)
(class_declaration name: (identifier) @type)
(actor_declaration name: (identifier) @type)
(protocol_declaration name: (identifier) @type)
(union_declaration name: (identifier) @type)
(enum_declaration name: (identifier) @type)
(alias_declaration name: (identifier) @type)
(associatedtype_declaration name: (identifier) @type)
(union_case name: (identifier) @constant)
(enum_case name: (identifier) @constant)
(const_declaration name: (identifier) @constant)
(case_field label: (identifier) @variable.member)

(attribute "@" @attribute name: (qualified_name) @attribute)

; ---- Types ----------------------------------------------------------------

(type_identifier) @type
(self_type) @type.builtin

((type_identifier) @type.builtin
  (#match? @type.builtin "^(Int|UInt|I8|I16|I32|I64|I128|U8|U16|U32|U64|U128|F16|F32|F64|Bool|Char|String|Never|Void)$"))

; ---- Expressions ----------------------------------------------------------

(call_expression function: (identifier) @function.call)
(call_expression function: (member_expression member: (identifier) @function.method.call))
(argument label: (identifier) @label)
(member_expression member: (identifier) @variable.member)
(implicit_member_expression name: (identifier) @constant)
(case_pattern case: (identifier) @constant)
(field_initializer name: (identifier) @variable.member)
(tuple_element label: (identifier) @label)
(closure_parameter name: (identifier) @variable.parameter)
(closure_signature name: (identifier) @variable.parameter)
(trailing_closure label: (identifier) @label)
(shorthand_parameter) @variable.parameter
(self) @variable.builtin
(super) @variable.builtin

; A capitalised name is a type, or a type's case or static.
((identifier) @type
  (#match? @type "^[A-Z]"))

; ---- Keywords -------------------------------------------------------------

[
  "module" "import" "fn" "let" "var" "const" "struct" "class" "actor" "union" "enum" "protocol"
  "extension" "alias" "associatedtype" "init" "deinit" "subscript" "operator" "region"
] @keyword

[
  "if" "else" "guard" "match" "switch" "case" "default" "fallthrough"
] @keyword.conditional

[
  "while" "repeat" "for" "in" "break" "continue"
] @keyword.repeat

[
  "return" "throw" "yield" "try" "try?" "try!" "catch" "await" "defer" "do" "where"
] @keyword.control

[
  "async" "throws" "rethrows" "inout" "out" "consuming" "move" "unsafe" "comptime"
  "some" "any" "using" "get" "set" "willSet" "didSet"
] @keyword.modifier

(modifier) @keyword.modifier
(parameter_modifier) @keyword.modifier

[
  "as" "as?" "as!" "is" "unchecked"
] @keyword.operator

; ---- Punctuation and operators --------------------------------------------

[ "(" ")" "[" "]" "{" "}" ] @punctuation.bracket
[ "," ":" "." ";" "?." ] @punctuation.delimiter
[ "->" "=>" ] @punctuation.special

[
  "=" "+=" "-=" "*=" "/=" "%=" "&=" "|=" "^=" "<<=" ">>=" "??="
  "+" "-" "*" "/" "%" "&+" "&-" "&*" "==" "!=" "===" "!==" "<" "<=" ">" ">="
  "&&" "||" "!" "~" "&" "|" "^" "<<" ">>" "??" "..<" "..."
] @operator
