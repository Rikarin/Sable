/**
 * @file A tree-sitter grammar for Sable, written by hand from spec/SYNTAX.md
 * and spec/grammar.ebnf.
 *
 * It is for highlighting, folding and selection in editors that read
 * tree-sitter (Helix, Zed, Neovim), not for compiling: the compiler's own
 * parser is the reference. Where the language needs more than a grammar to
 * decide (generic `<` against a comparison, a trailing closure against a
 * block), this one takes the reading an editor most often needs, and
 * error recovery covers the rest.
 *
 * Statements end at a newline or a `;`. tree-sitter has no layout rule, so
 * statements are simply sequenced: the newline is whitespace, as in
 * tree-sitter-swift's simpler grammars.
 */

/* eslint-disable no-undef */

const PREC = {
  assign: 1,
  ternary: 2,
  coalesce: 3,
  or: 4,
  and: 5,
  compare: 6,
  range: 7,
  bitor: 8,
  bitxor: 9,
  bitand: 10,
  shift: 11,
  add: 12,
  multiply: 13,
  cast: 14,
  prefix: 15,
  postfix: 16,
  call: 17,
  member: 18,
};

const commaSep = (rule) => optional(seq(rule, repeat(seq(",", rule)), optional(",")));
const commaSep1 = (rule) => seq(rule, repeat(seq(",", rule)), optional(","));

module.exports = grammar({
  name: "sable",

  word: ($) => $.identifier,

  extras: ($) => [/\s/, $.line_comment, $.block_comment, $.doc_comment],

  supertypes: ($) => [$._declaration, $._statement, $._expression, $._type, $._pattern],

  conflicts: ($) => [
    [$._expression, $._type],
    [$.generic_type, $._expression],
    [$.record_literal, $._expression],
    [$.tuple_expression, $.parenthesized_expression],
    [$._pattern, $._expression],
    [$.closure_signature, $.tuple_expression],
    [$.closure_signature, $.parenthesized_expression],
    [$.closure_signature, $._expression],
  ],

  rules: {
    source_file: ($) => seq(optional($.shebang), optional($.module_declaration), repeat($.import_declaration), repeat($._top_level)),

    shebang: (_) => token(seq("#!", /.*/)),

    _top_level: ($) => choice($._declaration, $._statement, $.directive),

    // ---- Comments and directives ------------------------------------------

    line_comment: (_) => token(seq("//", /[^/!\n]?/, /.*/)),
    doc_comment: (_) => token(choice(seq("///", /.*/), seq("//!", /.*/))),
    block_comment: (_) => token(seq("/*", /[^*]*\*+([^/*][^*]*\*+)*/, "/")),

    directive: ($) => seq(field("keyword", alias(/#(if|elseif|else|endif|error|warning)/, $.directive_keyword)), optional(/[^\n]+/)),

    // ---- Modules -----------------------------------------------------------

    module_declaration: ($) => seq("module", field("name", $.qualified_name)),

    import_declaration: ($) =>
      seq(
        optional("public"),
        "import",
        field("name", $.qualified_name),
        optional(choice(seq("as", field("alias", $.identifier)), seq(".", "{", commaSep(choice($.identifier, "*")), "}"))),
      ),

    qualified_name: ($) => prec.right(seq($.identifier, repeat(seq(".", $.identifier)))),

    // ---- Declarations ------------------------------------------------------

    _declaration: ($) =>
      choice(
        $.function_declaration,
        $.struct_declaration,
        $.class_declaration,
        $.actor_declaration,
        $.protocol_declaration,
        $.union_declaration,
        $.enum_declaration,
        $.extension_declaration,
        $.alias_declaration,
        $.property_declaration,
        $.const_declaration,
        $.init_declaration,
        $.deinit_declaration,
        $.subscript_declaration,
        $.operator_declaration,
        $.associatedtype_declaration,
      ),

    attribute: ($) => prec.right(seq("@", field("name", $.qualified_name), optional($.argument_list))),

    modifier: (_) =>
      choice(
        "public", "private", "internal", "package", "static", "mutating", "nonmutating", "override", "open", "final",
        "sealed", "abstract", "required", "convenience", "lazy", "weak", "unowned", "nonisolated", "prefix", "postfix",
        "unsafe", "unchecked", "comptime", "async",
      ),

    _prefix: ($) => repeat1(choice($.attribute, $.modifier)),

    function_declaration: ($) =>
      prec.right(
        seq(
          optional($._prefix),
          "fn",
          optional(seq(field("protocol", $._type), ".")),
          field("name", choice($.identifier, $.operator_name)),
          optional($.type_parameters),
          field("parameters", $.parameter_list),
          optional("async"),
          optional($.throws_clause),
          optional(seq("->", field("result", $._type))),
          optional($.where_clause),
          optional(choice(field("body", $.block), seq("=>", field("body", $._expression)))),
        ),
      ),

    operator_name: (_) => /[+\-*/%<>=!&|^~?]+/,

    throws_clause: ($) => prec.right(seq(choice("throws", "rethrows"), optional(seq("(", $._type, ")")))),

    parameter_list: ($) => seq("(", commaSep($.parameter), ")"),

    parameter: ($) =>
      seq(
        repeat($.attribute),
        optional($.parameter_modifier),
        optional(field("label", choice($.identifier, "_", $._keyword_label))),
        field("name", $.identifier),
        ":",
        optional($.parameter_modifier),
        field("type", $._type),
        optional("..."),
        optional(seq("=", field("default", $._expression))),
      ),

    // A label may be a keyword: 'fn first(where p: ...)'.
    _keyword_label: (_) => choice("where", "in", "for", "to", "from", "as", "is", "default", "case", "of", "with", "by", "at"),

    parameter_modifier: (_) => choice("inout", "out", "ref", "in", "consuming", "borrowing"),

    type_parameters: ($) => seq("<", commaSep1($.type_parameter), ">"),
    type_parameter: ($) => seq(optional("comptime"), field("name", $.identifier), optional(seq(":", $._type)), optional(seq("=", $._type))),

    where_clause: ($) => seq("where", commaSep1($.where_requirement)),
    where_requirement: ($) => choice(seq($._type, choice(":", "=="), $._type), $._expression),

    inheritance: ($) => seq(":", commaSep1($._type)),

    _type_body: ($) => seq("{", repeat(choice($._declaration, $.directive, ";")), "}"),

    struct_declaration: ($) =>
      seq(optional($._prefix), "struct", field("name", $.identifier), optional($.type_parameters), optional($.inheritance), optional($.where_clause), field("body", alias($._type_body, $.type_body))),

    class_declaration: ($) =>
      seq(optional($._prefix), "class", field("name", $.identifier), optional($.type_parameters), optional($.inheritance), optional($.where_clause), field("body", alias($._type_body, $.type_body))),

    actor_declaration: ($) =>
      seq(optional($._prefix), "actor", field("name", $.identifier), optional($.type_parameters), optional($.inheritance), field("body", alias($._type_body, $.type_body))),

    protocol_declaration: ($) =>
      seq(optional($._prefix), "protocol", field("name", $.identifier), optional($.type_parameters), optional($.inheritance), field("body", alias($._type_body, $.type_body))),

    extension_declaration: ($) =>
      seq(optional($._prefix), "extension", optional($.type_parameters), field("type", $._type), optional($.inheritance), optional($.where_clause), field("body", alias($._type_body, $.type_body))),

    union_declaration: ($) =>
      seq(
        optional($._prefix),
        "union",
        field("name", $.identifier),
        optional($.type_parameters),
        optional($.inheritance),
        optional($.where_clause),
        "{",
        commaSep(choice($.union_case, $._declaration)),
        repeat($._declaration),
        "}",
      ),

    union_case: ($) => seq(repeat($.attribute), field("name", $.identifier), optional(seq("(", commaSep($.case_field), ")"))),
    case_field: ($) => seq(optional(seq(field("label", $.identifier), ":")), field("type", $._type)),

    enum_declaration: ($) =>
      seq(
        optional($._prefix),
        "enum",
        field("name", $.identifier),
        optional($.inheritance),
        "{",
        repeat(seq(choice($.enum_case, $._declaration), optional(","))),
        "}",
      ),

    enum_case: ($) => prec.right(seq(repeat($.attribute), field("name", $.identifier), optional(seq("=", field("value", $._expression))))),

    alias_declaration: ($) => seq(optional($._prefix), "alias", field("name", $.identifier), optional($.type_parameters), "=", field("type", $._type)),

    associatedtype_declaration: ($) => seq("associatedtype", field("name", $.identifier), optional($.inheritance), optional(seq("=", $._type))),

    property_declaration: ($) =>
      prec.right(
        seq(
          optional($._prefix),
          choice("let", "var"),
          field("pattern", $._pattern),
          optional(seq(":", field("type", $._type))),
          optional(choice(seq("=", field("value", $._expression)), seq("=>", field("getter", $._expression)), $.accessor_list)),
        ),
      ),

    accessor_list: ($) => seq("{", repeat($.accessor), "}"),
    accessor: ($) =>
      seq(
        repeat(choice($.attribute, $.modifier)),
        choice("get", "set", "willSet", "didSet"),
        optional(seq("(", $.identifier, ")")),
        optional(choice($.block, seq("=>", $._expression))),
      ),

    const_declaration: ($) => seq(optional($._prefix), "const", field("name", $.identifier), optional(seq(":", field("type", $._type))), "=", field("value", $._expression)),

    init_declaration: ($) =>
      seq(optional($._prefix), "init", optional("?"), optional($.type_parameters), field("parameters", $.parameter_list), optional($.throws_clause), optional($.block)),

    deinit_declaration: ($) => seq(optional($._prefix), "deinit", $.block),

    subscript_declaration: ($) =>
      seq(optional($._prefix), "subscript", optional($.type_parameters), field("parameters", $.parameter_list), "->", field("result", $._type), choice($.accessor_list, $.block, seq("=>", $._expression))),

    operator_declaration: ($) =>
      seq(optional($._prefix), "operator", field("name", $.operator_name), field("parameters", $.parameter_list), optional(seq("->", field("result", $._type))), optional(choice($.block, seq("=>", $._expression)))),

    // ---- Statements --------------------------------------------------------

    block: ($) => seq("{", repeat(choice($._statement, $._declaration, $.directive)), "}"),

    _statement: ($) =>
      choice(
        $.expression_statement,
        $.return_statement,
        $.throw_statement,
        $.yield_statement,
        $.break_statement,
        $.continue_statement,
        $.fallthrough_statement,
        $.if_statement,
        $.guard_statement,
        $.while_statement,
        $.repeat_statement,
        $.for_statement,
        $.defer_statement,
        $.do_statement,
        $.region_statement,
        $.using_statement,
        $.comptime_block,
        $.async_let_statement,
        $.labeled_statement,
        ";",
      ),

    expression_statement: ($) => prec.right(seq($._expression, optional(";"))),
    return_statement: ($) => prec.right(seq("return", optional($._expression))),
    throw_statement: ($) => seq("throw", $._expression),
    yield_statement: ($) => seq("yield", $._expression),
    break_statement: ($) => prec.right(seq("break", optional($.identifier))),
    continue_statement: ($) => prec.right(seq("continue", optional($.identifier))),
    fallthrough_statement: (_) => "fallthrough",
    labeled_statement: ($) => seq(field("label", $.identifier), ":", choice($.while_statement, $.for_statement, $.repeat_statement)),

    _condition: ($) => commaSep1(choice($._expression, $.optional_binding, $.case_condition)),
    optional_binding: ($) => seq(choice("let", "var"), $._pattern, optional(seq("=", $._expression))),
    case_condition: ($) => seq("case", $._pattern, "=", $._expression),

    if_statement: ($) =>
      prec.right(seq("if", field("condition", $._condition), field("consequence", $.block), optional(seq("else", field("alternative", choice($.block, $.if_statement)))))),

    guard_statement: ($) => seq("guard", field("condition", $._condition), "else", $.block),
    while_statement: ($) => seq("while", field("condition", $._condition), field("body", $.block)),
    repeat_statement: ($) => seq("repeat", field("body", $.block), "while", field("condition", $._expression)),
    for_statement: ($) =>
      seq("for", optional("try"), optional("await"), field("pattern", $._pattern), "in", field("sequence", $._expression), optional(seq("where", $._expression)), field("body", $.block)),

    defer_statement: ($) => seq("defer", $.block),
    do_statement: ($) => prec.right(seq("do", $.block, repeat($.catch_clause))),
    catch_clause: ($) => seq("catch", optional($._pattern), optional(seq("where", $._expression)), $.block),
    region_statement: ($) => seq("region", field("name", $.identifier), $.block),
    using_statement: ($) => seq("using", choice("let", "var"), $._pattern, "=", $._expression),
    comptime_block: ($) => seq("comptime", $.block),
    async_let_statement: ($) => seq("async", "let", $._pattern, optional(seq(":", $._type)), "=", $._expression),

    // ---- Expressions -------------------------------------------------------

    _expression: ($) =>
      choice(
        $.identifier,
        $.self,
        $.super,
        $._literal,
        $.array_literal,
        $.dictionary_literal,
        $.record_literal,
        $.tuple_expression,
        $.parenthesized_expression,
        $.implicit_member_expression,
        $.member_expression,
        $.call_expression,
        $.index_expression,
        $.generic_expression,
        $.prefix_expression,
        $.postfix_expression,
        $.binary_expression,
        $.range_expression,
        $.assignment_expression,
        $.ternary_expression,
        $.cast_expression,
        $.try_expression,
        $.await_expression,
        $.move_expression,
        $.closure_expression,
        $.lambda_expression,
        $.shorthand_parameter,
        $.match_expression,
        $.if_expression,
        $.comptime_expression,
        $.unsafe_expression,
      ),

    self: (_) => "self",
    super: (_) => "super",

    parenthesized_expression: ($) => seq("(", $._expression, ")"),
    tuple_expression: ($) => seq("(", optional(seq($.tuple_element, ",", commaSep($.tuple_element))), ")"),
    tuple_element: ($) => seq(optional(seq(field("label", $.identifier), ":")), $._expression),

    array_literal: ($) => seq("[", commaSep($._expression), "]"),
    dictionary_literal: ($) => seq("[", choice(":", commaSep1($.dictionary_entry)), "]"),
    dictionary_entry: ($) => seq(field("key", $._expression), ":", field("value", $._expression)),

    record_literal: ($) => prec(PREC.call, seq(field("type", choice($.identifier, $.generic_type)), "{", commaSep($.field_initializer), "}")),
    field_initializer: ($) => seq(field("name", $.identifier), optional(seq(":", field("value", $._expression)))),

    implicit_member_expression: ($) => prec.right(PREC.member, seq(".", field("name", $.identifier))),

    member_expression: ($) => prec(PREC.member, seq(field("object", $._expression), choice(".", "?."), field("member", choice($.identifier, $.integer_literal)))),

    call_expression: ($) =>
      prec.right(PREC.call, seq(field("function", $._expression), optional("?"), choice(seq(field("arguments", $.argument_list), optional($.trailing_closure)), $.trailing_closure))),

    argument_list: ($) => seq("(", commaSep($.argument), ")"),
    argument: ($) => seq(optional(seq(field("label", choice($.identifier, $._keyword_label)), ":")), optional(choice("&", "out")), field("value", $._expression)),
    // After the first, more trailing closures, each with its label: '} onFail: {'.
    trailing_closure: ($) =>
      prec.dynamic(-1, prec.right(seq($.closure_expression, repeat(seq(field("label", $.identifier), ":", $.closure_expression))))),

    index_expression: ($) => prec(PREC.call, seq(field("object", $._expression), optional("?"), "[", commaSep1($._expression), "]")),

    generic_expression: ($) => prec(PREC.call, seq($.identifier, $.type_arguments)),

    prefix_expression: ($) => prec(PREC.prefix, seq(field("operator", choice("-", "!", "~", "&", "*", "+")), field("operand", $._expression))),
    postfix_expression: ($) => prec(PREC.postfix, seq(field("operand", $._expression), field("operator", choice("!", "?")))),

    binary_expression: ($) => {
      const table = [
        [PREC.coalesce, "??", prec.right],
        [PREC.or, "||", prec.left],
        [PREC.and, "&&", prec.left],
        [PREC.compare, choice("==", "!=", "<", "<=", ">", ">=", "===", "!=="), prec.left],
        [PREC.bitor, "|", prec.left],
        [PREC.bitxor, "^", prec.left],
        [PREC.bitand, "&", prec.left],
        [PREC.shift, choice("<<", ">>"), prec.left],
        [PREC.add, choice("+", "-", "&+", "&-"), prec.left],
        [PREC.multiply, choice("*", "/", "%", "&*"), prec.left],
      ];
      return choice(
        ...table.map(([precedence, operator, associate]) =>
          associate(precedence, seq(field("left", $._expression), field("operator", operator), field("right", $._expression))),
        ),
      );
    },

    range_expression: ($) =>
      choice(
        prec.left(PREC.range, seq(field("start", $._expression), field("operator", choice("..<", "...")), field("end", $._expression))),
        prec(PREC.range, seq(field("operator", choice("..<", "...")), field("end", $._expression))),
        prec(PREC.range - 1, seq(field("start", $._expression), field("operator", "..."))),
      ),

    assignment_expression: ($) =>
      prec.right(PREC.assign, seq(field("target", $._expression), field("operator", choice("=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "??=")), field("value", $._expression))),

    ternary_expression: ($) => prec.right(PREC.ternary, seq(field("condition", $._expression), "?", field("consequence", $._expression), ":", field("alternative", $._expression))),

    cast_expression: ($) => prec.left(PREC.cast, seq(field("value", $._expression), field("operator", choice("as", "as?", "as!", seq("unchecked", "as"), "is")), field("type", $._type))),

    // 'try f() catch fallback': the catch gives the value when f throws.
    try_expression: ($) =>
      prec.right(PREC.prefix, seq(choice("try", "try?", "try!"), $._expression, optional(seq("catch", choice($.block, $._expression))))),
    await_expression: ($) => prec.right(PREC.prefix, seq("await", $._expression)),
    move_expression: ($) => prec.right(PREC.prefix, seq("move", $._expression)),
    comptime_expression: ($) => prec.right(PREC.prefix, seq("comptime", $._expression)),
    unsafe_expression: ($) => seq("unsafe", $.block),

    closure_expression: ($) =>
      seq(
        "{",
        optional(seq(optional($.capture_list), optional(choice($.closure_parameters, commaSep1($.identifier))), optional($.throws_clause), optional(seq("->", $._type)), "in")),
        repeat(choice($._statement, $._declaration)),
        "}",
      ),
    capture_list: ($) =>
      seq("[", commaSep1(seq(optional(choice("weak", "unowned", "move")), choice($.identifier, $.self), optional(seq("=", $._expression)))), "]"),
    closure_parameters: ($) => seq("(", commaSep($.closure_parameter), ")"),
    closure_parameter: ($) => seq(field("name", choice($.identifier, "_")), optional(seq(":", optional("inout"), field("type", $._type)))),

    // '(x: Int) => x * 2', 'x => x.name', '[weak self] () => { ... }'.
    lambda_expression: ($) =>
      prec.right(
        PREC.assign,
        seq(optional($.capture_list), field("signature", $.closure_signature), "=>", field("body", choice($.block, $._expression))),
      ),
    closure_signature: ($) =>
      choice(seq("(", commaSep($.closure_parameter), ")", optional(seq("->", field("result", $._type)))), field("name", $.identifier)),

    // '$0', '$1': a brace closure's parameters by position.
    shorthand_parameter: (_) => /\$[0-9]+/,

    match_expression: ($) => seq(choice("match", "switch"), field("subject", $._expression), "{", repeat($.match_arm), "}"),
    match_arm: ($) =>
      prec.right(
        seq(
          choice(seq("case", commaSep1($._pattern), optional(seq("where", field("guard", $._expression)))), "default"),
          choice("=>", ":"),
          field("body", choice($.block, $._expression, repeat1($._statement))),
        ),
      ),

    if_expression: ($) => prec.right(seq("if", $._condition, $.block, "else", choice($.block, $.if_expression))),

    // ---- Literals ----------------------------------------------------------

    _literal: ($) =>
      choice($.integer_literal, $.float_literal, $.string_literal, $.raw_string_literal, $.multiline_string_literal, $.character_literal, $.boolean_literal, $.none),

    integer_literal: (_) => token(choice(/0x[0-9A-Fa-f_]+/, /0o[0-7_]+/, /0b[01_]+/, /[0-9][0-9_]*/)),
    float_literal: (_) => token(choice(/[0-9][0-9_]*\.[0-9][0-9_]*([eE][+-]?[0-9]+)?/, /[0-9][0-9_]*[eE][+-]?[0-9]+/, /0x[0-9A-Fa-f_]+(\.[0-9A-Fa-f_]+)?[pP][+-]?[0-9]+/)),
    boolean_literal: (_) => choice("true", "false"),
    none: (_) => "none",

    string_literal: ($) => seq('"', repeat(choice($.string_content, $.escape_sequence, $.interpolation)), '"'),
    multiline_string_literal: ($) => seq('"""', repeat(choice($.multiline_string_content, $.escape_sequence, $.interpolation)), '"""'),
    raw_string_literal: (_) => token(seq(/#+"/, /[^"]*("[^#][^"]*)*/, /"#+/)),
    string_content: (_) => token.immediate(prec(1, /[^"\\]+/)),
    multiline_string_content: (_) => token.immediate(prec(1, choice(/[^"\\]+/, /"[^"\\]/, /""[^"\\]/))),
    escape_sequence: (_) => token.immediate(seq("\\", choice(/[nrt0\\'"]/, /u\{[0-9A-Fa-f]+\}/))),
    interpolation: ($) => seq(token.immediate("\\("), $._expression, ")"),
    character_literal: (_) => token(seq("'", choice(/[^'\\]/, /\\[nrt0\\'"]/, /\\u\{[0-9A-Fa-f]+\}/), "'")),

    // ---- Types -------------------------------------------------------------

    _type: ($) =>
      choice(
        $._type_name,
        $.generic_type,
        $.qualified_type,
        $.optional_type,
        $.reference_type,
        $.array_type,
        $.dictionary_type,
        $.tuple_type,
        $.function_type,
        $.union_type,
        $.existential_type,
        $.noncopyable_type,
        $.self_type,
      ),

    _type_name: ($) => alias($.identifier, $.type_identifier),
    self_type: (_) => "Self",
    generic_type: ($) => prec(1, seq(field("name", $._type_name), $.type_arguments)),
    type_arguments: ($) => seq("<", commaSep1(choice($._type, $._literal)), ">"),
    qualified_type: ($) => prec.left(seq($._type, ".", $._type_name)),
    optional_type: ($) => prec(2, seq($._type, choice("?", "!"))),
    reference_type: ($) => prec.left(2, seq($._type, "&", optional(seq("<", $.identifier, optional(seq("in", $.identifier)), ">")))),
    array_type: ($) => prec(2, seq($._type, "[", optional(choice($.integer_literal, $.identifier, "..", seq("..", choice($.integer_literal, $.identifier)), ">..", ">..<", ":")), "]")),
    dictionary_type: ($) => seq("[", $._type, ":", $._type, "]"),
    tuple_type: ($) => seq("(", commaSep(seq(optional(seq($.identifier, ":")), $._type)), ")"),
    function_type: ($) => prec.right(seq(optional("async"), $.tuple_type, optional($.throws_clause), "->", $._type)),
    union_type: ($) => prec.left(seq($._type, "|", $._type)),
    existential_type: ($) => prec.right(seq(choice("any", "some"), $._type)),
    noncopyable_type: ($) => seq("~", $._type_name),

    // ---- Patterns ----------------------------------------------------------

    _pattern: ($) =>
      choice(
        $.identifier,
        $.wildcard_pattern,
        $.binding_pattern,
        $.tuple_pattern,
        $.record_pattern,
        $.case_pattern,
        $.type_pattern,
        $.optional_pattern,
        $._literal,
        $.range_pattern,
      ),

    wildcard_pattern: (_) => "_",
    binding_pattern: ($) => prec.right(seq(choice("let", "var"), $._pattern)),
    tuple_pattern: ($) => seq("(", commaSep1(seq(optional(seq($.identifier, ":")), $._pattern)), ")"),
    record_pattern: ($) => seq(optional($._type_name), "{", commaSep(seq($.identifier, optional(seq(":", $._pattern)))), "}"),
    case_pattern: ($) => prec.right(seq(optional($._type_name), ".", field("case", $.identifier), optional($.tuple_pattern))),
    type_pattern: ($) => prec.right(choice(seq("is", $._type), seq($._pattern, "as", $._type))),
    optional_pattern: ($) => prec(1, seq($._pattern, "?")),
    range_pattern: ($) => prec.left(seq($._literal, choice("...", "..<"), $._literal)),

    identifier: (_) => /[A-Za-z_][A-Za-z0-9_]*/,
  },
});
