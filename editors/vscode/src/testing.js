// @ts-check
// The Test Explorer: each package's @test functions, as `sable test --list`
// lists them, run by `sable test <names> --exact`, debugged one at a time
// in the harness `sable test -g --harness-out` builds, or run with
// `--coverage` to show what of the package they ran (coverage.js).
"use strict";

const vscode = require("vscode");
const fs = require("fs");
const os = require("os");
const path = require("path");
const sable = require("./sable");
const coverage = require("./coverage");

/** `Module.name: test (path:line)`, a line of `sable test --list`. */
const LISTED = /^(\S+): test \((.+):(\d+)\)$/;

/** `test Module.name ... ok (1.2 ms)`, a line of a run; the time in ns, µs, ms or s. */
const RESULT = /^test (\S+) \.\.\. (ok|FAILED|ignored)(?: \(([\d.]+) (ns|µs|us|ms|s)\))?/;

/** Milliseconds in each unit the harness prints a time in. */
const UNITS = { ns: 1e-6, "µs": 1e-3, us: 1e-3, ms: 1, s: 1000 };

/** `---- Module.name (path:line) ----`, the start of a failure's report. */
const FAILURE = /^---- (\S+) \((.+):(\d+)\) ----$/;

/** @param {vscode.ExtensionContext} context */
function register(context) {
  const controller = vscode.tests.createTestController("sable", "Sable");
  context.subscriptions.push(controller);

  /** Package root → its item: the tests are its children. */
  const packages = new Map();

  /**
   * Lists one package's tests.
   * @param {string} root
   */
  async function discover(root) {
    const name = sable.packageName(root) ?? path.basename(root);
    /** @type {vscode.TestItem} */
    let item = packages.get(root);
    if (!item) {
      item = controller.createTestItem(root, name, vscode.Uri.file(sable.manifestIn(root) ?? path.join(root, "package.sb")));
      packages.set(root, item);
      controller.items.add(item);
    }

    item.busy = true;
    try {
      const { code, stdout, stderr } = await sable.run(["test", "--list"], { cwd: root });
      if (code !== 0) {
        item.error = new vscode.MarkdownString().appendText("sable test --list failed:\n").appendCodeblock(stderr || stdout, "text");
        return;
      }

      item.error = undefined;
      /** @type {vscode.TestItem[]} */
      const tests = [];
      for (const line of stdout.split(/\r?\n/)) {
        const match = LISTED.exec(line.trim());
        if (!match) {
          continue;
        }

        const [, id, file, lineNumber] = match;
        const test = controller.createTestItem(`${root}::${id}`, id.split(".").pop() ?? id, vscode.Uri.file(path.resolve(root, file)));
        const at = Number(lineNumber) - 1;
        test.range = new vscode.Range(at, 0, at, 0);
        test.description = id.includes(".") ? id.slice(0, id.lastIndexOf(".")) : undefined;
        tests.push(test);
      }

      item.children.replace(tests);
    } catch (error) {
      item.error = error instanceof Error ? error.message : String(error);
    } finally {
      item.busy = false;
    }
  }

  async function discoverAll() {
    const manifests = await vscode.workspace.findFiles(sable.MANIFEST_GLOB, "**/{target,vendor,node_modules,.git}/**");
    const roots = manifests.map((m) => m.fsPath).filter(sable.isPackage).map((m) => path.dirname(m));
    for (const [root, item] of packages) {
      if (!roots.includes(root)) {
        controller.items.delete(item.id);
        packages.delete(root);
      }
    }

    await Promise.all(roots.map(discover));
  }

  controller.resolveHandler = async (item) => {
    if (!item) {
      await discoverAll();
    }
  };

  controller.refreshHandler = () => discoverAll();

  // Saving a source file may add, remove or move a test: list that package again.
  /** @type {Map<string, NodeJS.Timeout>} */
  const pending = new Map();
  context.subscriptions.push(
    vscode.workspace.onDidSaveTextDocument((document) => {
      const isManifest = ["package.sb", "workspace.sb", "sable.toml"].includes(path.basename(document.fileName));
      if (document.languageId !== "sable" && !isManifest) {
        return;
      }

      const root = sable.packageRoot(document.uri);
      if (!root || !packages.has(root)) {
        if (isManifest) {
          discoverAll();
        }

        return;
      }

      clearTimeout(pending.get(root));
      pending.set(root, setTimeout(() => {
        pending.delete(root);
        discover(root);
      }, 500));
    }),
  );

  controller.createRunProfile("Run", vscode.TestRunProfileKind.Run, (request, token) => runTests(controller, packages, request, token), true);
  controller.createRunProfile("Debug", vscode.TestRunProfileKind.Debug, (request, token) => debugTests(controller, packages, request, token), true);

  // Coverage needs VS Code 1.88's API; an older one has the other two profiles.
  if (coverage.isSupported()) {
    /** @type {any} */
    const profile = controller.createRunProfile("Coverage", vscode.TestRunProfileKind.Coverage, (request, token) => runTests(controller, packages, request, token, true), true);
    profile.loadDetailedCoverage = async (/** @type {unknown} */ _run, /** @type {unknown} */ file) => coverage.details(file);
  }
}

/**
 * The tests a request names, by package root: a package stands for each of its tests.
 * @param {Map<string, vscode.TestItem>} packages
 * @param {vscode.TestRunRequest} request
 * @returns {Map<string, vscode.TestItem[]>}
 */
function selectedTests(packages, request) {
  const excluded = new Set(request.exclude?.map((t) => t.id) ?? []);
  /** @type {Map<string, vscode.TestItem[]>} */
  const plan = new Map();
  const roots = [...packages.keys()];
  for (const item of request.include ?? [...packages.values()]) {
    const root = roots.find((r) => item.id === r || item.id.startsWith(`${r}::`));
    if (!root || excluded.has(item.id)) {
      continue;
    }

    const tests = plan.get(root) ?? [];
    if (item.id === root) {
      item.children.forEach((child) => {
        if (!excluded.has(child.id)) {
          tests.push(child);
        }
      });
    } else {
      tests.push(item);
    }

    plan.set(root, tests);
  }

  return plan;
}

/**
 * Debugs the selected tests: each package's harness is built once with
 * debug information, and each test is started in it under the debugger,
 * one after the other (`<harness> --run-test <name>`, as `sable test` runs it).
 * @param {vscode.TestController} controller
 * @param {Map<string, vscode.TestItem>} packages
 * @param {vscode.TestRunRequest} request
 * @param {vscode.CancellationToken} token
 */
async function debugTests(controller, packages, request, token) {
  const run = controller.createTestRun(request);
  try {
    for (const [root, tests] of selectedTests(packages, request)) {
      if (token.isCancellationRequested || tests.length === 0) {
        break;
      }

      const harness = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "sable-debug-")), "harness");
      run.appendOutput(`Building the tests of ${root} with debug information...\r\n`);
      const built = await sable.run(["test", "-g", "--harness-out", harness], { cwd: root, token });
      if (built.code !== 0) {
        const message = new vscode.TestMessage((built.stderr || built.stdout).trim() || `sable test exited with ${built.code}`);
        tests.forEach((test) => run.errored(test, message));
        continue;
      }

      const folder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(root));
      for (const test of tests) {
        if (token.isCancellationRequested) {
          break;
        }

        const name = test.id.slice(root.length + 2);
        run.started(test);
        const ended = new Promise((resolve) => {
          const watch = vscode.debug.onDidTerminateDebugSession((session) => {
            if (session.configuration?.sableTest === test.id) {
              watch.dispose();
              resolve(undefined);
            }
          });
        });
        const started = await vscode.debug.startDebugging(folder, {
          type: "sable",
          request: "launch",
          name: `Debug ${name}`,
          program: harness,
          args: ["--run-test", name],
          cwd: root,
          sableTest: test.id,
        });
        if (!started) {
          run.errored(test, new vscode.TestMessage("the debugger did not start"));
          continue;
        }

        await ended;
        // Whether it passed is the debugger's to show; the run only says it ran.
        run.skipped(test);
      }
    }
  } finally {
    run.end();
  }
}

/**
 * Runs the selected tests, each package's in one `sable test`; measuring
 * coverage, it then reports the files of the package the tests ran.
 * @param {vscode.TestController} controller
 * @param {Map<string, vscode.TestItem>} packages
 * @param {vscode.TestRunRequest} request
 * @param {vscode.CancellationToken} token
 * @param {boolean} [measure] Run with `--coverage`, and report it.
 */
async function runTests(controller, packages, request, token, measure = false) {
  const run = controller.createTestRun(request);
  const excluded = new Set(request.exclude?.map((t) => t.id) ?? []);

  // Package root → the tests to run in it, or "all" to run every one.
  /** @type {Map<string, vscode.TestItem[] | "all">} */
  const plan = new Map();
  const roots = [...packages.keys()];
  const requested = request.include ?? [...packages.values()];
  for (const item of requested) {
    const root = roots.find((r) => item.id === r || item.id.startsWith(`${r}::`));
    if (!root) {
      continue;
    }

    if (item.id === root) {
      /** @type {vscode.TestItem[]} */
      const children = [];
      item.children.forEach((child) => {
        if (!excluded.has(child.id)) {
          children.push(child);
        }
      });
      plan.set(root, excluded.size === 0 ? "all" : children);
    } else if (plan.get(root) !== "all" && !excluded.has(item.id)) {
      plan.set(root, [.../** @type {vscode.TestItem[]} */ (plan.get(root) ?? []), item]);
    }
  }

  try {
    for (const [root, selection] of plan) {
      if (token.isCancellationRequested) {
        break;
      }

      const item = /** @type {vscode.TestItem} */ (packages.get(root));
      /** @type {Map<string, vscode.TestItem>} */
      const byName = new Map();
      const all = selection === "all";
      const tests = [];
      if (all) {
        item.children.forEach((child) => tests.push(child));
      } else {
        tests.push(...selection);
      }

      for (const test of tests) {
        byName.set(test.id.slice(root.length + 2), test);
        run.started(test);
      }

      if (tests.length === 0) {
        continue;
      }

      const args = all ? ["test"] : ["test", ...byName.keys(), "--exact"];
      const output = await runPackage(run, root, measure ? [...args, "--coverage"] : args, byName, token);
      if (measure && !token.isCancellationRequested) {
        coverage.report(run, root, output);
      }
    }
  } finally {
    run.end();
  }
}

/**
 * Runs one package's tests and reports each result as it is printed.
 * @param {vscode.TestRun} run
 * @param {string} root
 * @param {string[]} args
 * @param {Map<string, vscode.TestItem>} byName
 * @param {vscode.CancellationToken} token
 * @returns {Promise<string>} What it printed.
 */
async function runPackage(run, root, args, byName, token) {
  const reported = new Set();
  /** @type {Map<string, { item: vscode.TestItem, duration?: number }>} */
  const failed = new Map();
  let buffer = "";
  let transcript = "";

  /** @type {{ name: string, lines: string[] } | undefined} */
  let failure;
  /** @type {Map<string, string[]>} */
  const reports = new Map();

  /** @param {string} line */
  function consume(line) {
    const result = RESULT.exec(line);
    if (result) {
      const [, name, outcome, duration, unit] = result;
      const test = byName.get(name);
      if (!test) {
        return;
      }

      reported.add(name);
      const ms = duration === undefined ? undefined : Number(duration) * UNITS[/** @type {keyof typeof UNITS} */ (unit)];
      if (outcome === "ok") {
        run.passed(test, ms);
      } else if (outcome === "ignored") {
        run.skipped(test);
      } else {
        failed.set(name, { item: test, duration: ms });
      }

      return;
    }

    const start = FAILURE.exec(line);
    if (start) {
      failure = { name: start[1], lines: [] };
      reports.set(start[1], failure.lines);
      return;
    }

    if (failure && (line === "failed:" || line.startsWith("test result:"))) {
      failure = undefined;
    } else if (failure) {
      failure.lines.push(line);
    }
  }

  let code;
  try {
    code = await sable.stream(args, {
      cwd: root,
      token,
      onOutput: (text) => {
        transcript += text;
        run.appendOutput(text.replace(/\r?\n/g, "\r\n"));
        buffer += text;
        const lines = buffer.split(/\r?\n/);
        buffer = lines.pop() ?? "";
        lines.forEach(consume);
      },
    });
  } catch (error) {
    const message = new vscode.TestMessage(error instanceof Error ? error.message : String(error));
    byName.forEach((test) => run.errored(test, message));
    return transcript;
  }

  if (buffer) {
    consume(buffer);
  }

  for (const [name, { item, duration }] of failed) {
    const text = (reports.get(name) ?? []).join("\n").replace(/\s+$/, "") || "the test failed";
    const message = new vscode.TestMessage(text);
    const expected = /^\s*right: (.*)$/m.exec(text)?.[1];
    const actual = /^\s*left: (.*)$/m.exec(text)?.[1];
    if (expected !== undefined && actual !== undefined) {
      message.expectedOutput = expected;
      message.actualOutput = actual;
    }

    if (item.uri && item.range) {
      message.location = new vscode.Location(item.uri, item.range);
    }

    run.failed(item, message, duration);
  }

  // A build that failed reports no test at all: its diagnostics say why.
  for (const [name, test] of byName) {
    if (reported.has(name)) {
      continue;
    }

    if (token.isCancellationRequested) {
      run.skipped(test);
    } else {
      const why = code === 0 ? "the test did not run" : transcript.trim() || `sable test exited with ${code}`;
      run.errored(test, new vscode.TestMessage(why));
    }
  }

  return transcript;
}

module.exports = { register };
