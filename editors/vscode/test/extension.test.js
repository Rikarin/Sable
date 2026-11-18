// @ts-nocheck
// The extension's own code, run under plain node against a stand-in for
// VS Code (test/vscode-mock.js): what activation registers and starts, the
// manifest it must agree with, multi-root folders, tasks, code lenses,
// quick fixes, debug configurations, and the Test Explorer reading a
// `sable` that prints what the real one does. (Type-checking is off here:
// the mock is not the `vscode` the types describe.)
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { load, context, Uri } = require("./vscode-mock");

const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "package.json"), "utf8"));
const posix = process.platform !== "win32";

/** A directory of files, made fresh: `{ "a/b.sb": "text" }`. */
function tree(files) {
  const root = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), "sable-vscode-")));
  for (const [file, text] of Object.entries(files)) {
    fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    fs.writeFileSync(path.join(root, file), text);
  }

  return root;
}

const PACKAGE = 'Package {\n    name: "demo"\n}\n';
const MAIN = "module Demo\n\nfn main() {\n    print(1)\n}\n";

/**
 * A `sable` that answers as the real one does to what the extension asks:
 * `test --list`, a test run with one pass and one failure, the rest a success.
 */
function fakeSable(directory) {
  const script = path.join(directory, "fake-sable");
  fs.writeFileSync(script, `#!/bin/sh
if [ "$1" = "test" ] && [ "$2" = "--list" ]; then
  echo "Demo.adds: test (src/main.sb:3)"
  echo "Demo.fails: test (src/main.sb:7)"
  exit 0
fi
if [ "$1" = "test" ]; then
  echo "running 2 tests"
  echo "test Demo.adds ... ok (1.5 ms)"
  echo "test Demo.fails ... FAILED (2 µs)"
  echo ""
  echo "---- Demo.fails (src/main.sb:7) ----"
  echo "  assertion failed: values differ"
  echo "    left: 3"
  echo "    right: 4"
  echo ""
  echo "failed:"
  echo "    Demo.fails"
  echo ""
  echo "test result: FAILED. 1 passed; 1 failed"
  exit 1
fi
exit 0
`);
  fs.chmodSync(script, 0o755);
  return script;
}

function document(file, text) {
  const lines = text.split("\n");
  return {
    uri: Uri.file(file),
    fileName: file,
    languageId: "sable",
    lineCount: lines.length,
    lineAt: (line) => ({ text: lines[line] }),
  };
}

test("activation registers every command the manifest declares, and nothing else", async () => {
  const { modules, recorded } = load({ folders: [tree({})] });
  await modules.extension.activate(context(os.tmpdir()));

  const declared = manifest.contributes.commands.map((c) => c.command).sort();
  assert.deepEqual([...recorded.commands.keys()].sort(), declared);
  assert.ok(recorded.taskProviders.has("sable"), "a task provider for the 'sable' task type");
  assert.equal(recorded.debugProviders.filter((p) => p.type === "sable").length, 2);
  assert.deepEqual(recorded.codeLensProviders.map((p) => p.selector), [{ language: "sable" }]);
  assert.deepEqual(recorded.codeActionProviders.map((p) => p.selector), [{ language: "sable" }]);
  assert.equal(recorded.controllers.length, 1);
  assert.ok(recorded.controllers[0].profiles.some((p) => p.kind === 1 && p.isDefault), "a default Run profile");
});

test("the manifest's types and settings are the ones the code uses", () => {
  assert.ok(manifest.contributes.taskDefinitions.some((t) => t.type === "sable"));
  assert.ok(manifest.contributes.debuggers.some((d) => d.type === "sable"));
  const settings = Object.keys(manifest.contributes.configuration.properties);
  const source = fs.readdirSync(path.join(__dirname, "..", "src")).map((f) => fs.readFileSync(path.join(__dirname, "..", "src", f), "utf8")).join("\n");
  const used = [...source.matchAll(/\.get\("([\w.]+)",/g)].map((m) => `sable.${m[1]}`);
  assert.ok(used.length >= 5, "found the settings the code reads");
  for (const setting of used) {
    assert.ok(settings.includes(setting), `${setting} is read but not declared in package.json`);
  }

  // The language client reads this one itself, by the client's id.
  assert.ok(settings.includes("sable.trace.server"));
});

test("the language server is started as 'sable lsp' with the editor's settings", async () => {
  const { modules, recorded } = load({ folders: [tree({})], settings: { "sable.path": "/opt/sable/bin/sable" } });
  await modules.extension.activate(context(os.tmpdir()));

  assert.equal(recorded.clients.length, 1);
  const client = recorded.clients[0];
  assert.equal(client.serverOptions.run.command, "/opt/sable/bin/sable");
  assert.deepEqual(client.serverOptions.run.args, ["lsp"]);
  assert.deepEqual(client.clientOptions.initializationOptions, { inlayHints: { enable: true }, lints: { enable: true }, diagnostics: { workspace: true } });
  assert.equal(client.clientOptions.synchronize.configurationSection, "sable");
  assert.ok(client.isRunning());
});

test("inlay hints and lints can be turned off, and the server turned off", async () => {
  const off = load({ folders: [tree({})], settings: { "sable.inlayHints.enable": false, "sable.lints.enable": false, "sable.diagnostics.workspace": false } });
  await off.modules.extension.activate(context(os.tmpdir()));
  assert.deepEqual(off.recorded.clients[0].clientOptions.initializationOptions, { inlayHints: { enable: false }, lints: { enable: false }, diagnostics: { workspace: false } });

  const none = load({ folders: [tree({})], settings: { "sable.server.enable": false } });
  await none.modules.extension.activate(context(os.tmpdir()));
  assert.equal(none.recorded.clients.length, 0);
});

test("changing the binary offers a restart; changing a server setting does not", async () => {
  const { modules, recorded } = load({ folders: [tree({})] });
  await modules.extension.activate(context(os.tmpdir()));
  await recorded.configurationListeners.fire({ affectsConfiguration: (s) => s === "sable.lints.enable" });
  assert.equal(recorded.messages.length, 0);
  await recorded.configurationListeners.fire({ affectsConfiguration: (s) => s === "sable.path" });
  assert.match(recorded.messages[0].text, /restart the language server/);
});

test("in a workspace of several folders, each folder's sable.path is its own", () => {
  const first = tree({ "src/a.sb": MAIN });
  const second = tree({ "src/b.sb": MAIN });
  const { modules, vscode } = load({
    folders: [first, second],
    settings: { "sable.path": "${workspaceFolder}/tools/sable" },
    folderSettings: { [second]: { "sable.path": "~/other/sable" } },
  });
  const { sable } = modules;

  assert.equal(sable.binary(Uri.file(path.join(first, "src", "a.sb"))), path.join(first, "tools", "sable"));
  assert.equal(sable.binary(path.join(second, "src")), path.join(os.homedir(), "other", "sable"));
  assert.equal(sable.folderOf(path.join(second, "src", "b.sb")).uri.fsPath, second);

  // With nothing to go by: the active editor's folder, else the first.
  assert.equal(sable.folderOf().uri.fsPath, first);
  vscode.window.activeTextEditor = { document: document(path.join(second, "src", "b.sb"), MAIN) };
  assert.equal(sable.folderOf().uri.fsPath, second);
});

test("a file's package is the nearest manifest inside its folder", () => {
  const root = tree({
    "package.sb": PACKAGE,
    "src/main.sb": MAIN,
    "nested/sable.toml": '[package]\nname = "inner/nested"\n',
    "nested/src/lib.sb": MAIN,
    "ws/sable.toml": '[workspace]\nmembers = ["a"]\n',
    "ws/loose.sb": MAIN,
  });
  const { modules } = load({ folders: [path.join(root, "nested"), path.join(root, "ws")] });
  const { sable } = modules;

  assert.equal(sable.packageRoot(Uri.file(path.join(root, "nested", "src", "lib.sb"))), path.join(root, "nested"));
  // A workspace root's sable.toml is no package, and the search stops at the folder.
  assert.equal(sable.packageRoot(Uri.file(path.join(root, "ws", "loose.sb"))), undefined);
  assert.equal(sable.packageName(root), "demo");
  assert.equal(sable.packageName(path.join(root, "nested")), "nested");
  assert.equal(sable.isPackage(path.join(root, "ws", "sable.toml")), false);
});

test("tasks are offered for every package of every folder", async () => {
  const first = tree({ "package.sb": PACKAGE, "src/main.sb": MAIN, "target/dev/package.sb": PACKAGE });
  const second = tree({ "tools/gen/package.sb": PACKAGE, "tools/gen/src/main.sb": MAIN });
  const { modules, recorded, vscode } = load({
    folders: [first, second],
    folderSettings: { [second]: { "sable.path": "/second/sable" } },
  });
  modules.tasks.register(context(os.tmpdir()));
  const provider = recorded.taskProviders.get("sable");
  const tasks = await provider.provideTasks();

  const names = tasks.map((t) => `${t.scope.name}:${t.name}`);
  for (const command of ["build", "check", "run", "test", "bench", "fmt", "clean"]) {
    assert.ok(names.includes(`${path.basename(first)}:${command}`), `${command} in the first folder`);
    assert.ok(names.includes(`${path.basename(second)}:${command} (${path.join("tools", "gen")})`), `${command} in the second`);
  }

  assert.equal(tasks.length, 14, "nothing from target/");
  const build = tasks.find((t) => t.scope.uri.fsPath === second && t.definition.command === "build");
  assert.equal(build.execution.process, "/second/sable");
  assert.deepEqual(build.execution.args, ["build"]);
  assert.equal(build.execution.options.cwd, path.join(second, "tools", "gen"));
  assert.deepEqual(build.problemMatchers, ["$sable"]);
  assert.deepEqual(tasks.find((t) => t.definition.command === "fmt").problemMatchers, []);

  // A tasks.json entry is resolved against its own folder.
  const resolved = provider.resolveTask({ definition: { type: "sable", command: "test", args: ["--exact", "x"], cwd: "${workspaceFolder}/tools/gen" }, scope: vscode.workspace.workspaceFolders[1] });
  assert.equal(resolved.execution.options.cwd, `${second}/tools/gen`);
  assert.deepEqual(resolved.execution.args, ["test", "--exact", "x"]);
  assert.equal(resolved.name, "test --exact x");
});

test("Run builds the package a file is in, or the file alone", async () => {
  const root = tree({ "package.sb": PACKAGE, "src/main.sb": MAIN, "scratch/one.sb": MAIN });
  const loose = tree({ "two.sb": MAIN });
  const { modules, recorded } = load({ folders: [root, loose] });
  await modules.extension.activate(context(os.tmpdir()));

  await recorded.commands.get("sable.run")(Uri.file(path.join(root, "src", "main.sb")));
  const inPackage = recorded.executedTasks.pop();
  assert.equal(inPackage.execution.options.cwd, root);
  assert.deepEqual(inPackage.execution.args, ["run"]);
  assert.equal(inPackage.scope.uri.fsPath, root);

  await recorded.commands.get("sable.check")(Uri.file(path.join(loose, "two.sb")));
  const alone = recorded.executedTasks.pop();
  assert.equal(alone.execution.options.cwd, loose);
  assert.deepEqual(alone.execution.args, ["check", "two.sb"]);
  assert.equal(alone.scope.uri.fsPath, loose);

  await recorded.commands.get("sable.debug")(Uri.file(path.join(root, "src", "main.sb")));
  assert.deepEqual(recorded.started.pop().configuration, { type: "sable", request: "launch", name: "Debug the package", package: root, cwd: root });

  // With no file at all: a warning, and nothing run.
  await recorded.commands.get("sable.build")();
  assert.equal(recorded.executedTasks.length, 0);
  assert.match(recorded.messages.pop().text, /open a \.sb file/);
});

test("Run and Debug lenses sit over main, or over an @main function", async () => {
  const { modules, recorded } = load({ folders: [tree({})] });
  await modules.extension.activate(context(os.tmpdir()));
  const lenses = recorded.codeLensProviders[0].provider;

  const plain = lenses.provideCodeLenses(document("/x/main.sb", MAIN));
  assert.deepEqual(plain.map((l) => [l.range.start.line, l.command.command]), [[2, "sable.run"], [2, "sable.debug"]]);

  const marked = lenses.provideCodeLenses(document("/x/app.sb", "module App\n\n@main\n/// Starts.\nfn start() {\n}\n"));
  assert.deepEqual(marked.map((l) => l.range.start.line), [4, 4]);

  assert.deepEqual(lenses.provideCodeLenses(document("/x/lib.sb", "module Lib\n\nfn helper() {\n}\n")), []);
});

test("the code lens follows its setting", async () => {
  const { modules, recorded } = load({ folders: [tree({})], settings: { "sable.codeLens.enable": false } });
  await modules.extension.activate(context(os.tmpdir()));
  assert.deepEqual(recorded.codeLensProviders[0].provider.provideCodeLenses(document("/x/main.sb", MAIN)), []);
});

test("each coded sable diagnostic gets an Explain quick fix, once", async () => {
  const { modules, recorded } = load({ folders: [tree({})] });
  await modules.extension.activate(context(os.tmpdir()));
  const provider = recorded.codeActionProviders[0].provider;
  const actions = provider.provideCodeActions(undefined, undefined, {
    diagnostics: [
      { source: "sable", code: "S3004" },
      { source: "sable", code: { value: "S3300", target: undefined } },
      { source: "sable", code: "S3004" },
      { source: "other", code: "S1234" },
      { source: "sable", code: "E42" },
    ],
  });
  assert.deepEqual(actions.map((a) => a.title), ["Explain S3004", "Explain S3300"]);
  assert.deepEqual(actions[0].command.arguments, ["S3004"]);
});

test("debug configurations: the first one, and F5 without a launch.json", async () => {
  const root = tree({ "package.sb": PACKAGE, "src/main.sb": MAIN });
  const loose = tree({ "one.sb": MAIN });
  const { modules, recorded, vscode } = load({ folders: [root, loose] });
  modules.debug.register(context(os.tmpdir()));
  const [resolver, initial] = recorded.debugProviders.map((p) => p.provider);

  assert.equal(initial.provideDebugConfigurations(vscode.workspace.workspaceFolders[0])[0].name, "Debug the package");
  assert.equal(initial.provideDebugConfigurations(vscode.workspace.workspaceFolders[1])[0].program, "${file}");

  // No editor on a Sable file: nothing to debug.
  assert.equal(resolver.resolveDebugConfiguration(undefined, {}), undefined);
  vscode.window.activeTextEditor = { document: document(path.join(loose, "one.sb"), MAIN) };
  assert.deepEqual(resolver.resolveDebugConfiguration(undefined, {}), { type: "sable", request: "launch", name: "Debug this file", program: path.join(loose, "one.sb") });
  vscode.window.activeTextEditor = { document: document(path.join(root, "src", "main.sb"), MAIN) };
  assert.equal(resolver.resolveDebugConfiguration(undefined, {}).package, root);

  // A configuration that says what it wants is left as it is.
  const given = { type: "sable", request: "launch", name: "mine", program: "/bin/true" };
  assert.equal(resolver.resolveDebugConfiguration(undefined, given), given);
});

test("debugging without an lldb adapter says which to install", async () => {
  const { modules, recorded } = load({ folders: [tree({})] });
  modules.debug.register(context(os.tmpdir()));
  const resolver = recorded.debugProviders[0].provider;
  const result = await resolver.resolveDebugConfigurationWithSubstitutedVariables(undefined, { type: "sable", request: "launch", name: "x", program: "/bin/true" });
  assert.equal(result, undefined);
  assert.match(recorded.messages[0].text, /lldb debug adapter/);
  assert.deepEqual(recorded.messages[0].choices, ["Find LLDB DAP", "Find CodeLLDB"]);
});

test("a built program becomes lldb-dap's configuration, with the formatters", { skip: !posix && "needs a shell script for sable" }, async () => {
  const storage = tree({});
  const sablePath = fakeSable(storage);
  const { modules, recorded } = load({ folders: [storage], settings: { "sable.path": sablePath }, extensions: ["llvm-vs-code-extensions.lldb-dap", "vadimcn.vscode-lldb"] });
  modules.debug.register(context(storage));
  const resolver = recorded.debugProviders[0].provider;
  const result = await resolver.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
    type: "sable", request: "launch", name: "x", program: "/bin/true", args: ["a"], initCommands: ["mine"],
  });
  assert.equal(result.type, "lldb-dap", "LLVM's adapter is preferred");
  assert.equal(result.program, "/bin/true");
  assert.deepEqual(result.args, ["a"]);
  assert.equal(result.initCommands[0], "settings set target.load-script-from-symbol-file false");
  assert.match(result.initCommands[1], /^command script import ".*sable_lldb\.py"$/);
  assert.equal(result.initCommands[2], "mine");
});

test("a package without a manifest cannot be debugged, and says so", async () => {
  const empty = tree({});
  const { modules, recorded } = load({ folders: [empty], extensions: ["vadimcn.vscode-lldb"] });
  modules.debug.register(context(empty));
  const result = await recorded.debugProviders[0].provider.resolveDebugConfigurationWithSubstitutedVariables(
    { uri: Uri.file(empty), name: "e", index: 0 }, { type: "sable", request: "launch", name: "x" });
  assert.equal(result, undefined);
  assert.match(recorded.messages[0].text, /no package to debug/);
});

test("the Test Explorer lists a package's tests and reports a run", { skip: !posix && "needs a shell script for sable" }, async () => {
  const root = tree({ "package.sb": PACKAGE, "src/main.sb": MAIN });
  const sablePath = fakeSable(root);
  const { modules, recorded } = load({ folders: [root], settings: { "sable.path": sablePath } });
  modules.testing.register(context(root));
  const controller = recorded.controllers[0];

  await controller.resolveHandler(undefined);
  const packages = [];
  controller.items.forEach((item) => packages.push(item));
  assert.deepEqual(packages.map((p) => p.label), ["demo"]);
  const tests = [];
  packages[0].children.forEach((item) => tests.push(item));
  assert.deepEqual(tests.map((t) => [t.label, t.description, t.range.start.line, t.uri.fsPath]), [
    ["adds", "Demo", 2, path.join(root, "src", "main.sb")],
    ["fails", "Demo", 6, path.join(root, "src", "main.sb")],
  ]);

  const profile = controller.profiles.find((p) => p.isDefault);
  await profile.handler({ include: undefined, exclude: [] }, { isCancellationRequested: false, onCancellationRequested: () => {} });
  const run = controller.runs[0];
  assert.ok(run.ended);
  const outcome = (id) => run.events.filter((e) => e.id === `${root}::${id}` && e.event !== "started").map((e) => e.event);
  assert.deepEqual(outcome("Demo.adds"), ["passed"]);
  assert.deepEqual(outcome("Demo.fails"), ["failed"]);

  const passed = run.events.find((e) => e.event === "passed");
  assert.equal(passed.rest[0], 1.5);
  const failed = run.events.find((e) => e.event === "failed");
  const [message, duration] = failed.rest;
  assert.equal(duration, 0.002);
  assert.equal(message.expectedOutput, "4");
  assert.equal(message.actualOutput, "3");
  assert.match(message.message, /assertion failed/);
  assert.equal(message.location.range.start.line, 6);
  assert.match(run.output, /test result: FAILED/);
});

test("every snippet and grammar the manifest names exists", () => {
  for (const file of [
    ...manifest.contributes.grammars.map((g) => g.path),
    ...manifest.contributes.snippets.map((s) => s.path),
    ...manifest.contributes.languages.map((l) => l.configuration),
    ...manifest.contributes.tomlValidation.map((t) => t.url),
    manifest.main,
  ]) {
    assert.ok(fs.existsSync(path.join(__dirname, "..", file)), `${file} exists`);
  }
});
