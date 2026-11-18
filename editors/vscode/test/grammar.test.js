// @ts-check
// The grammar, run by the same TextMate engine VS Code runs: each line of
// a sample is tokenised, and what a token's scopes should include is checked.
// Then every .sab file in the repository is tokenised, to show that none
// throws the engine off (an unclosed string swallowing the rest of a file).
"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const textmate = require("vscode-textmate");
const oniguruma = require("vscode-oniguruma");

const root = path.join(__dirname, "..");

async function load() {
  const wasm = fs.readFileSync(require.resolve("vscode-oniguruma/release/onig.wasm")).buffer;
  await oniguruma.loadWASM(wasm);
  const registry = new textmate.Registry({
    onigLib: Promise.resolve({
      createOnigScanner: (sources) => new oniguruma.OnigScanner(sources),
      createOnigString: (text) => new oniguruma.OnigString(text),
    }),
    loadGrammar: async (scope) => {
      const file = { "source.sable": "sable.tmLanguage.json" }[scope];
      if (!file) {
        return null;
      }

      const full = path.join(root, "syntaxes", file);
      return textmate.parseRawGrammar(fs.readFileSync(full, "utf8"), full);
    },
  });

  const grammar = await registry.loadGrammar("source.sable");
  assert.ok(grammar, "the grammar loads");
  return grammar;
}

/**
 * The tokens of some lines, each as its text and scopes.
 * @param {import("vscode-textmate").IGrammar} grammar
 * @param {string} text
 */
function tokenise(grammar, text) {
  let stack = textmate.INITIAL;
  /** @type {{ text: string, scopes: string[], line: number }[]} */
  const tokens = [];
  text.split("\n").forEach((line, number) => {
    const result = grammar.tokenizeLine(line, stack);
    for (const token of result.tokens) {
      tokens.push({ text: line.slice(token.startIndex, token.endIndex), scopes: token.scopes, line: number });
    }

    stack = result.ruleStack;
  });

  return { tokens, stack };
}

/** [line of source, token text, a scope the token must have] */
const EXPECTATIONS = [
  ["module Examples.Service", "module", "keyword.other.import.sable"],
  ["module Examples.Service", "Examples.Service", "entity.name.namespace.sable"],
  ["import Sable.IO", "Sable.IO", "entity.name.namespace.sable"],
  ["/// The sum of `x` and `y`.", "`x`", "markup.inline.raw.sable"],
  ["/// The sum of `x` and `y`.", " The sum of ", "comment.line.documentation.sable"],
  ["// a comment", "// a comment", "comment.line.double-slash.sable"],
  ["/* outer /* inner */ still */ let", " still ", "comment.block.sable"],
  ["/* outer /* inner */ still */ let", "let", "storage.type.sable"],
  ["public fn area(_ s: Shape) -> F64 {", "public", "storage.modifier.sable"],
  ["public fn area(_ s: Shape) -> F64 {", "fn", "storage.type.function.sable"],
  ["public fn area(_ s: Shape) -> F64 {", "area", "entity.name.function.sable"],
  ["public fn area(_ s: Shape) -> F64 {", "Shape", "entity.name.type.sable"],
  ["public fn area(_ s: Shape) -> F64 {", "F64", "support.type.builtin.sable"],
  ["public fn area(_ s: Shape) -> F64 {", "->", "keyword.operator.arrow.sable"],
  ["public actor Cache<Key: Hashable> {", "Cache", "entity.name.type.sable"],
  ["public union ServiceError {", "ServiceError", "entity.name.type.sable"],
  ["extension ServiceError: From<HttpError> {", "ServiceError", "entity.name.type.sable"],
  ["    let total = work(n)", "total", "variable.other.declaration.sable"],
  ["    let total = work(n)", "work", "entity.name.function.call.sable"],
  ["    var x = 0x1F_FF", "0x1F_FF", "constant.numeric.hex.sable"],
  ["    let r = 2.5e-3", "2.5e-3", "constant.numeric.decimal.sable"],
  ["guard let entry = entries[key] else { return none }", "guard", "keyword.control.conditional.sable"],
  ["guard let entry = entries[key] else { return none }", "return", "keyword.control.flow.sable"],
  ["guard let entry = entries[key] else { return none }", "none", "constant.language.none.sable"],
  ["for i in 0..<count {", "..<", "keyword.operator.range.sable"],
  ["for i in 0..<count {", "for", "keyword.control.loop.sable"],
  ["let v = try? load()", "try?", "keyword.control.flow.sable"],
  ["if x is Circle && y != 0 {", "is", "keyword.operator.type.sable"],
  ["if x is Circle && y != 0 {", "&&", "keyword.operator.logical.sable"],
  ["if x is Circle && y != 0 {", "!=", "keyword.operator.comparison.sable"],
  ["@derive(Serializable, Equatable)", "derive", "entity.other.attribute-name.sable"],
  ["@test", "test", "entity.other.attribute-name.sable"],
  ["let s = \"a \\(name.count + 1) b\\n\"", "name", "meta.embedded.interpolation.sable"],
  ["let s = \"a \\(name.count + 1) b\\n\"", "\\n", "constant.character.escape.sable"],
  ["let s = \"a \\(name.count + 1) b\\n\"", " b", "string.quoted.double.sable"],
  ["let s = \"call \\(f(g(1))) done\"", " done", "string.quoted.double.sable"],
  ["let raw = #\"no \\(escape)\"# + x", "no \\(escape)", "string.quoted.double.raw.sable"],
  ["let raw = #\"no \\(escape)\"# + x", "+", "keyword.operator.arithmetic.sable"],
  ["let c = '\\n'", "\\n", "string.quoted.single.sable"],
  ["let c = '\\n'", "\\n", "constant.character.escape.sable"],
  ["#if feature(extra) && !DEBUG", "#if", "keyword.control.directive.conditional.sable"],
  ["#if feature(extra) && !DEBUG", "feature", "support.function.directive.sable"],
  ["#endif", "#endif", "keyword.control.directive.conditional.sable"],
  ["    get => value", "get", "storage.type.accessor.sable"],
  ["    public fn get(_ key: Key) -> Value? {", "get", "entity.name.function.sable"],
  ["let self = Self.make(super.x)", "Self", "variable.language.sable"],
  ["fn yield() {}", "yield", "entity.name.function.sable"],
  ["let trait = 1", "trait", "variable.other.declaration.sable"],
  ["impl Foo {}", "impl", "invalid.illegal.reserved.sable"],
];

async function main() {
  const grammar = await load();
  let failures = 0;
  for (const [line, text, scope] of EXPECTATIONS) {
    const { tokens } = tokenise(grammar, line);
    const token = tokens.find((t) => t.text === text);
    if (!token || !token.scopes.includes(scope)) {
      failures++;
      console.error(`FAIL  ${JSON.stringify(line)}\n      ${JSON.stringify(text)} should be ${scope}; it is ${token ? token.scopes.join(" ") : "not a token"}`);
      console.error(`      tokens: ${tokens.map((t) => JSON.stringify(t.text)).join(" ")}`);
    }
  }

  // A multi-line string closes, and what follows is code again.
  {
    const { tokens } = tokenise(grammar, 'let page = """\n    <p>\\(title)</p>\n    """\nlet after = 1');
    const after = tokens.find((t) => t.text === "after");
    if (!after?.scopes.includes("variable.other.declaration.sable")) {
      failures++;
      console.error("FAIL  a multi-line string swallows what follows it");
    }
  }

  // Every source file in the repository ends where it began: at the top level.
  const repository = path.join(root, "..", "..");
  let files = 0;
  for (const file of walk(repository)) {
    const { stack } = tokenise(grammar, fs.readFileSync(file, "utf8"));
    files++;
    if (stack.depth > 1 && !/negative|corpus|fuzz/.test(file)) {
      failures++;
      console.error(`FAIL  ${path.relative(repository, file)} ends inside a construct (depth ${stack.depth})`);
    }
  }

  console.log(`${EXPECTATIONS.length + 1} expectations, ${files} files tokenised, ${failures} failures`);
  process.exitCode = failures === 0 ? 0 : 1;
}

/**
 * @param {string} directory
 * @returns {Generator<string>}
 */
function* walk(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name.startsWith(".") || ["node_modules", "bin", "obj", "target"].includes(entry.name)) {
      continue;
    }

    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      yield* walk(full);
    } else if (entry.name.endsWith(".sab")) {
      yield full;
    }
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
