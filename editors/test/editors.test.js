// @ts-check
// The editor setups other than VS Code's, under plain node: that they agree
// with the VS Code grammar, and — where the editor or tool is installed —
// that the editor reads them as meant. A check whose tool is missing is
// skipped, and says so.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const cp = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const editors = path.join(__dirname, "..");
const grammar = JSON.parse(fs.readFileSync(path.join(editors, "vscode", "syntaxes", "sable.tmLanguage.json"), "utf8"));

/** @param {string} tool */
function has(tool) {
  return cp.spawnSync(tool, [tool === "lua" ? "-v" : "--version"], { stdio: "ignore" }).status === 0;
}

/** Every word the VS Code grammar's keyword rules and built-in types name. */
function grammarWords() {
  const words = new Set();
  const rules = [...grammar.repository.keywords.patterns, ...grammar.repository.types.patterns.slice(0, 1)];
  for (const rule of rules) {
    const group = /\\b\(([\w|]+)\)\\b/.exec(rule.match) ?? /\\b(\w+)\\b/.exec(rule.match);
    assert.ok(group, `a word list in ${rule.match}`);
    group[1].split("|").forEach((w) => words.add(w));
  }

  assert.ok(words.size > 80, "found the grammar's keywords");
  return [...words];
}

test("Sublime's grammar is the VS Code grammar, regenerated", () => {
  const { generate, target } = require("../sublime/generate");
  assert.equal(fs.readFileSync(target, "utf8"), generate(), "run 'node editors/sublime/generate.js'");
});

test("Vim highlights every keyword the VS Code grammar does", () => {
  const syntax = fs.readFileSync(path.join(editors, "vim", "syntax", "sable.vim"), "utf8");
  const keywords = new Set(
    syntax.split("\n").filter((l) => l.startsWith("syn keyword ")).flatMap((l) => l.split(/\s+/).slice(3)),
  );
  const missing = grammarWords().filter((w) => !keywords.has(w));
  assert.deepEqual(missing, []);
});

test("Emacs highlights every keyword the VS Code grammar does", () => {
  const mode = fs.readFileSync(path.join(editors, "emacs", "sable-mode.el"), "utf8");
  const missing = grammarWords().filter((w) => !mode.includes(`"${w}"`));
  assert.deepEqual(missing, []);
});

const SAMPLE = `module Demo

/// Adds \`a\` and \`b\`.
@inline
fn add(_ a: Int, _ b: Int) -> Int {
// a comment
let total = a + b
if total > 0x10 {
return total
}
let text = "sum \\(total)"
print(text)
return 0
}
`;

const INDENTED = `module Demo

/// Adds \`a\` and \`b\`.
@inline
fn add(_ a: Int, _ b: Int) -> Int {
    // a comment
    let total = a + b
    if total > 0x10 {
        return total
    }
    let text = "sum \\(total)"
    print(text)
    return 0
}
`;

test("Vim recognises, highlights and indents a Sable file", { skip: !has("vim") && "vim is not installed" }, () => {
  const work = fs.mkdtempSync(path.join(os.tmpdir(), "sable-vim-"));
  const file = path.join(work, "demo.sb");
  const script = path.join(work, "check.vim");
  const out = path.join(work, "out.txt");
  const shebang = path.join(work, "script");
  fs.writeFileSync(file, SAMPLE);
  fs.writeFileSync(shebang, "#!/usr/bin/env sable\nmodule Main\n");
  fs.writeFileSync(script, `
set nocompatible
let &runtimepath = ${JSON.stringify(path.join(editors, "vim"))} . ',' . &runtimepath
filetype plugin indent on
syntax on
function! s:Group(line, col) abort
  return synIDattr(synID(a:line, a:col, 1), 'name')
endfunction
edit ${file}
let s:r = ['filetype=' . &filetype, 'shiftwidth=' . &shiftwidth, 'expandtab=' . &expandtab, 'commentstring=' . &commentstring]
call add(s:r, 'fn=' . s:Group(5, 1))
call add(s:r, 'Int=' . s:Group(5, 13))
call add(s:r, 'attribute=' . s:Group(4, 2))
call add(s:r, 'doc=' . s:Group(3, 5))
call add(s:r, 'comment=' . s:Group(6, 4))
call add(s:r, 'if=' . s:Group(8, 1))
call add(s:r, 'hex=' . s:Group(8, 12))
call add(s:r, 'return=' . s:Group(9, 1))
call add(s:r, 'string=' . s:Group(11, 13))
call add(s:r, 'interpolated=' . s:Group(11, 20))
normal! gg=G
call add(s:r, 'indented=' . join(getline(1, '$'), '\\n'))
edit ${shebang}
call add(s:r, 'shebang=' . &filetype)
call writefile(s:r, ${JSON.stringify(out)})
qall!
`);
  const run = cp.spawnSync("vim", ["-Nu", "NONE", "-i", "NONE", "-n", "-es", "-S", script], { encoding: "utf8", timeout: 30000 });
  assert.ok(fs.existsSync(out), `vim wrote no results: ${run.stderr}`);
  const results = Object.fromEntries(fs.readFileSync(out, "utf8").trimEnd().split("\n").map((l) => [l.slice(0, l.indexOf("=")), l.slice(l.indexOf("=") + 1)]));

  assert.equal(results.filetype, "sable");
  assert.equal(results.shiftwidth, "4");
  assert.equal(results.expandtab, "1");
  assert.equal(results.commentstring, "// %s");
  assert.equal(results.fn, "sableStorage");
  assert.equal(results.Int, "sableBuiltinType");
  assert.equal(results.attribute, "sableAttribute");
  assert.equal(results.doc, "sableDocComment");
  assert.equal(results.comment, "sableComment");
  assert.equal(results.if, "sableConditional");
  assert.equal(results.hex, "sableNumber");
  assert.equal(results.return, "sableFlow");
  assert.equal(results.string, "sableString");
  assert.notEqual(results.interpolated, "sableString", "an interpolation is code, not text");
  assert.equal(results.indented.replace(/\\n/g, "\n") + "\n", INDENTED);
  assert.equal(results.shebang, "sable");
});

test("Neovim's language server configuration is a table that runs 'sable lsp'", { skip: !has("lua") && "lua is not installed" }, () => {
  const file = path.join(editors, "vim", "lsp", "sable.lua");
  const run = cp.spawnSync("lua", ["-e", `
    local config = dofile(${JSON.stringify(file)})
    assert(config.cmd[1] == "sable" and config.cmd[2] == "lsp", "cmd")
    assert(config.filetypes[1] == "sable", "filetypes")
    assert(config.init_options.lints.enable == true, "lints")
    assert(config.init_options.inlayHints.enable == true, "inlay hints")
  `], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
});

test("Neovim's tree-sitter setup registers the grammar with nvim-treesitter, either branch", { skip: !has("lua") && "lua is not installed" }, () => {
  const file = path.join(editors, "vim", "lua", "sable", "treesitter.lua");
  const run = cp.spawnSync("lua", ["-e", `
    local function neovim()
      local autocmds, registered = {}, {}
      vim = {
        log = { levels = { WARN = 2 } },
        notify = function(message) error(message) end,
        api = { nvim_create_autocmd = function(event, opts) autocmds[#autocmds + 1] = { event = event, opts = opts } end },
        treesitter = { language = { register = function(lang, ft) registered[ft] = lang end }, start = function() end },
      }
      return autocmds, registered
    end

    -- The master branch: a table of parser configurations.
    local autocmds, registered = neovim()
    local configs = {}
    package.loaded["nvim-treesitter.parsers"] = { get_parser_configs = function() return configs end }
    dofile(${JSON.stringify(file)}).setup({ path = "/g" })
    assert(configs.sable.install_info.url == "/g", "url")
    assert(configs.sable.install_info.requires_generate_from_grammar, "generated from grammar.js")
    assert(registered.sable == "sable", "filetype")
    assert(autocmds[1].event == "FileType" and autocmds[1].opts.pattern == "sable", "started on a Sable buffer")

    -- The main branch: the parser is declared on each :TSUpdate.
    autocmds = neovim()
    local parsers = {}
    package.loaded["nvim-treesitter.parsers"] = parsers
    dofile(${JSON.stringify(file)}).setup({ path = "/g" })
    assert(autocmds[1].event == "User" and autocmds[1].opts.pattern == "TSUpdate", "TSUpdate")
    autocmds[1].opts.callback()
    assert(parsers.sable.install_info.path == "/g" and parsers.sable.install_info.generate, "main branch")
  `], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  const query = path.join(editors, "vim", "queries", "sable", "highlights.scm");
  assert.equal(fs.readFileSync(query, "utf8"), fs.readFileSync(path.join(editors, "tree-sitter-sable", "queries", "highlights.scm"), "utf8"));
});

test("Helix's languages.toml declares the language and its server", { skip: !has("python3") && "python3 is not installed" }, () => {
  const file = path.join(editors, "helix", "languages.toml");
  const run = cp.spawnSync("python3", ["-I", "-c", `
import json, sys, tomllib
with open(sys.argv[1], "rb") as f:
    print(json.dumps(tomllib.load(f)))
`, file], { encoding: "utf8" });
  if (run.status !== 0 && /No module named 'tomllib'/.test(run.stderr)) {
    return; // Python before 3.11 has no TOML reader.
  }

  assert.equal(run.status, 0, run.stderr);
  const toml = JSON.parse(run.stdout);
  const [language] = toml.language;
  assert.equal(language.name, "sable");
  assert.equal(language.scope, grammar.scopeName);
  assert.deepEqual(language["file-types"], ["sb"]);
  for (const server of language["language-servers"]) {
    assert.deepEqual([toml["language-server"][server].command, ...toml["language-server"][server].args], ["sable", "lsp"]);
  }

  assert.deepEqual(toml["language-server"]["sable-lsp"].config, { inlayHints: { enable: true }, lints: { enable: true } });
});

test("Emacs byte-compiles the mode without a warning", { skip: !has("emacs") && "emacs is not installed" }, () => {
  const work = fs.mkdtempSync(path.join(os.tmpdir(), "sable-emacs-"));
  const copy = path.join(work, "sable-mode.el");
  fs.copyFileSync(path.join(editors, "emacs", "sable-mode.el"), copy);
  const run = cp.spawnSync("emacs", ["-Q", "--batch", "--eval", "(setq byte-compile-error-on-warn t)", "-f", "batch-byte-compile", copy], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
});

// ---- The tree-sitter grammar ----------------------------------------------

/**
 * grammar.js, run against a stand-in for tree-sitter's DSL that records what
 * each rule is made of: the symbols it names, its fields, the strings it
 * matches and the names it aliases. No parser is generated: that needs the
 * tree-sitter CLI, which the last test uses where it is installed.
 */
function readTreeSitterGrammar() {
  const wrap = (rule) =>
    typeof rule === "string" ? { type: "STRING", value: rule, members: [] } : rule instanceof RegExp ? { type: "PATTERN", members: [] } : rule;
  const node = (type, members, extra = {}) => ({ type, members: members.map(wrap), ...extra });
  const prec = (a, b) => node("PREC", [b ?? a]);
  prec.left = prec.right = prec.dynamic = prec;
  const token = (rule) => node("TOKEN", [rule]);
  token.immediate = token;
  const dsl = {
    grammar: (definition) => definition,
    seq: (...members) => node("SEQ", members),
    choice: (...members) => node("CHOICE", members),
    repeat: (rule) => node("REPEAT", [rule]),
    repeat1: (rule) => node("REPEAT", [rule]),
    optional: (rule) => node("CHOICE", [rule]),
    blank: () => node("BLANK", []),
    field: (name, rule) => node("FIELD", [rule], { name }),
    alias: (rule, value) => node("ALIAS", [rule], { value: typeof value === "string" ? value : value.name, named: typeof value !== "string" }),
    token,
    prec,
  };
  const source = fs.readFileSync(path.join(editors, "tree-sitter-sable", "grammar.js"), "utf8");
  const module = { exports: {} };
  new Function("module", ...Object.keys(dsl), source)(module, ...Object.values(dsl));
  const definition = module.exports;

  const referenced = new Set();
  const $ = new Proxy({}, { get: (_, name) => (referenced.add(String(name)), { type: "SYMBOL", name: String(name), members: [] }) });
  const rules = new Map(Object.entries(definition.rules).map(([name, rule]) => [name, wrap(rule($))]));
  for (const key of ["word", "extras", "supertypes", "conflicts"]) {
    definition[key]?.($);
  }

  const strings = new Set();
  const fields = new Set();
  const aliases = new Set();
  const walk = (rule) => {
    if (rule.type === "STRING") strings.add(rule.value);
    if (rule.type === "FIELD") fields.add(rule.name);
    if (rule.type === "ALIAS") (rule.named ? aliases : strings).add(rule.value);
    rule.members.forEach(walk);
  };
  rules.forEach(walk);
  const named = new Set([...[...rules.keys()].filter((r) => !r.startsWith("_")), ...aliases]);
  return { definition, rules, referenced, strings, fields, named };
}

test("the tree-sitter grammar names only rules it defines", () => {
  const { rules, referenced, definition } = readTreeSitterGrammar();
  assert.equal(definition.name, "sable");
  const { named } = readTreeSitterGrammar();
  assert.deepEqual([...referenced].filter((name) => !rules.has(name) && !named.has(name)), []);
  assert.equal([...rules.keys()][0], "source_file", "source_file is the first rule");
});

test("the tree-sitter grammar has the syntax added since it was written", () => {
  const { rules, strings } = readTreeSitterGrammar();
  // The words a rule spells, its own and its fields', not those of the rules it names.
  const words = (name) => {
    const found = new Set();
    const walk = (rule) => (rule.type === "STRING" && found.add(rule.value), rule.members.forEach(walk));
    walk(rules.get(name));
    return found;
  };
  for (const rule of ["yield_statement", "async_let_statement", "lambda_expression", "closure_signature", "shorthand_parameter", "unsafe_expression"]) {
    assert.ok(rules.has(rule), `${rule} is a rule`);
  }
  assert.ok(words("for_statement").has("try"), "'for try' goes through a sequence that can fail");
  assert.ok(words("throws_clause").has("rethrows"), "'rethrows(E)'");
  assert.ok(words("cast_expression").has("unchecked"), "'x unchecked as T'");
  assert.ok(words("try_expression").has("catch"), "'try f() catch fallback'");
  assert.ok(words("modifier").has("unsafe"), "'unsafe fn'");
  assert.ok(words("trailing_closure").size === 1 && words("trailing_closure").has(":"), "labelled trailing closures");
  assert.ok(strings.has("=>"));
});

test("the tree-sitter grammar has every keyword of the VS Code grammar", () => {
  const { strings } = readTreeSitterGrammar();
  const reserved = new Set(["macro", "trait", "impl", "become", "effect", "handler"]);
  const builtinTypes = new Set(["Int", "UInt", "I8", "I16", "I32", "I64", "I128", "U8", "U16", "U32", "U64", "U128", "F16", "F32", "F64", "Bool", "Char", "String", "Never", "Void"]);
  const missing = grammarWords().filter((w) => !reserved.has(w) && !builtinTypes.has(w) && !strings.has(w));
  assert.deepEqual(missing, []);
});

test("the highlight queries name only the grammar's nodes, fields and tokens", () => {
  const { strings, fields, named } = readTreeSitterGrammar();
  const query = fs
    .readFileSync(path.join(editors, "tree-sitter-sable", "queries", "highlights.scm"), "utf8")
    .split("\n")
    .filter((l) => !l.trimStart().startsWith(";"))
    .join("\n")
    .replace(/\(#[a-z?!-]+(?:\s+(?:@[\w.]+|"(?:[^"\\]|\\.)*"))*\s*\)/g, "");
  const tokens = [...query.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => m[1].replace(/\\(.)/g, "$1"));
  const bare = query.replace(/"((?:[^"\\]|\\.)*)"/g, "");
  const nodes = [...bare.matchAll(/\(([a-z_]+)/g)].map((m) => m[1]);
  const usedFields = [...bare.matchAll(/([a-z_]+):/g)].map((m) => m[1]);
  assert.deepEqual(nodes.filter((n) => !named.has(n)), [], "node types");
  assert.deepEqual(usedFields.filter((f) => !fields.has(f)), [], "fields");
  assert.deepEqual(tokens.filter((t) => !strings.has(t)), [], "tokens");
});

test(
  "tree-sitter generates the parser and parses the corpus",
  { skip: !has("tree-sitter") && "the tree-sitter CLI is not installed, so the grammar is checked as data only" },
  () => {
    const work = fs.mkdtempSync(path.join(os.tmpdir(), "sable-tree-sitter-"));
    fs.cpSync(path.join(editors, "tree-sitter-sable"), work, { recursive: true });
    const generate = cp.spawnSync("tree-sitter", ["generate"], { cwd: work, encoding: "utf8" });
    assert.equal(generate.status, 0, generate.stderr);
    const corpus = path.join(editors, "..", "tests", "corpus");
    for (const file of fs.readdirSync(corpus).filter((f) => f.endsWith(".sb"))) {
      const parse = cp.spawnSync("tree-sitter", ["parse", "--quiet", path.join(corpus, file)], { cwd: work, encoding: "utf8" });
      assert.equal(parse.status, 0, `${file}: ${parse.stdout}${parse.stderr}`);
    }
  },
);
