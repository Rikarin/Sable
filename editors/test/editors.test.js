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
