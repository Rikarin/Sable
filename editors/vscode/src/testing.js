// @ts-check
// The Test Explorer: each package's tests, under their suites — a module, or
// a @suite type — as `sable test --list --json -` describes them, run by
// `sable test <names> --exact --json -`, whose events say how each went,
// debugged one at a time in the harness `sable test -g --harness-out` builds,
// or run with `--coverage` to show what of the package they ran (coverage.js).
"use strict";

const vscode = require("vscode");
const fs = require("fs");
const os = require("os");
const path = require("path");
const sable = require("./sable");
const coverage = require("./coverage");

/**
 * A test, as a line of `sable test --list --json -` describes it. What it
 * holds besides is passed over.
 * @typedef {{ name: string, file: string, line: number, suite: string, tags: string[], skip?: string }} Described
 */

/**
 * Something that happened in a run, a line of `sable test --json -`.
 * @typedef {{ event: string, name: string, outcome?: string, duration?: number, message?: string, output?: string, reason?: string, attempts?: number, attachments?: string[] }} Happened
 */

/**
 * The JSON object a line is; undefined for a line of anything else.
 * @param {string} line
 */
function objectIn(line) {
  if (!line.startsWith("{")) {
    return undefined;
  }

  try {
    return JSON.parse(line);
  } catch {
    return undefined;
  }
}

/**
 * A test's name without a case's arguments, whose dots are not the name's.
 * @param {string} id
 */
function functionOf(id) {
  const open = id.indexOf("(");
  return open < 0 ? id : id.slice(0, open);
}

/**
 * The tests there are to run in an item: itself, or, of a
 * `@test(arguments:)` test or a package, each of those under it.
 * @param {vscode.TestItem} item
 * @param {Set<string>} excluded The items left out, by id.
 * @returns {vscode.TestItem[]}
 */
function leaves(item, excluded) {
  if (excluded.has(item.id)) {
    return [];
  }

  // A package with no tests is none itself.
  if (item.children.size === 0) {
    return item.id.includes("::") && !item.id.includes("::suite ") ? [item] : [];
  }

  /** @type {vscode.TestItem[]} */
  const all = [];
  item.children.forEach((child) => {
    all.push(...leaves(child, excluded));
  });
  return all;
}

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
      const { code, stdout, stderr } = await sable.run(["test", "--list", "--json", "-"], { cwd: root });
      if (code !== 0) {
        item.error = new vscode.MarkdownString().appendText("sable test --list failed:\n").appendCodeblock(stderr || stdout, "text");
        return;
      }

      item.error = undefined;
      /** Suite → its item. @type {Map<string, vscode.TestItem>} */
      const suites = new Map();
      /** A function with arguments → its item: its cases are its children. */
      const functions = new Map();
      for (const line of stdout.split(/\r?\n/)) {
        /** @type {Described | undefined} */
        const described = objectIn(line);
        if (!described?.name) {
          continue;
        }

        const { name: id, file, line: lineNumber, skip } = described;
        const name = functionOf(id);
        const suiteName = described.suite || name.slice(0, Math.max(0, name.lastIndexOf(".")));
        const at = Number(lineNumber) - 1;
        /** @param {string} id @param {string} label */
        const create = (id, label) => {
          const test = controller.createTestItem(`${root}::${id}`, label, vscode.Uri.file(path.resolve(root, file)));
          test.range = new vscode.Range(at, 0, at, 0);
          test.tags = (described.tags ?? []).map((tag) => new vscode.TestTag(tag));
          if (skip !== undefined) {
            test.description = `skipped: ${skip}`;
          }

          return test;
        };

        let suite = suites.get(suiteName);
        if (!suite) {
          suite = controller.createTestItem(`${root}::suite ${suiteName}`, suiteName, vscode.Uri.file(path.resolve(root, file)));
          suites.set(suiteName, suite);
        }

        let test = functions.get(name);
        if (!test) {
          test = create(name, name.startsWith(`${suiteName}.`) ? name.slice(suiteName.length + 1) : name);
          suite.children.add(test);
          functions.set(name, test);
        }

        if (id !== name) {
          test.children.add(create(id, id.slice(name.length)));
        }
      }

      item.children.replace([...suites.values()]);
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

    plan.set(root, [...(plan.get(root) ?? []), ...leaves(item, excluded)]);
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
      plan.set(root, excluded.size === 0 ? "all" : leaves(item, excluded));
    } else if (plan.get(root) !== "all" && !excluded.has(item.id)) {
      plan.set(root, [.../** @type {vscode.TestItem[]} */ (plan.get(root) ?? []), ...leaves(item, excluded)]);
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
        tests.push(...leaves(item, excluded));
      } else {
        tests.push(...selection);
      }

      for (const test of tests) {
        byName.set(test.id.slice(root.length + 2), test);
        run.enqueued(test);
      }

      if (tests.length === 0) {
        continue;
      }

      const args = all ? ["test", "--json", "-"] : ["test", ...byName.keys(), "--exact", "--json", "-"];
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
 * Says how a test went.
 * @param {vscode.TestRun} run
 * @param {vscode.TestItem} test
 * @param {Happened} happened
 */
function report(run, test, happened) {
  const duration = happened.duration === undefined ? undefined : happened.duration * 1000;
  const location = test.uri && test.range ? new vscode.Location(test.uri, test.range) : undefined;
  /** @param {string} text */
  const write = (text) => run.appendOutput(text.replace(/\r?\n/g, "\r\n"), location, test);
  const tries = (happened.attempts ?? 1) > 1 ? `, on try ${happened.attempts}` : "";
  write(`test ${happened.name} ... ${happened.outcome}${tries}${happened.reason ? `: ${happened.reason}` : ""}\n`);
  if (happened.output) {
    write(happened.output.endsWith("\n") ? happened.output : `${happened.output}\n`);
  }

  for (const file of happened.attachments ?? []) {
    write(`attached ${file}\n`);
  }

  if (happened.outcome === "passed") {
    run.passed(test, duration);
    return;
  }

  if (happened.outcome === "skipped") {
    run.skipped(test);
    return;
  }

  const text = (happened.message ?? "").replace(/\s+$/, "") || "the test failed";
  write(`${text}\n`);
  const message = new vscode.TestMessage(text);
  const expected = /^\s*right: (.*)$/m.exec(text)?.[1];
  const actual = /^\s*left: (.*)$/m.exec(text)?.[1];
  if (expected !== undefined && actual !== undefined) {
    message.expectedOutput = expected;
    message.actualOutput = actual;
  }

  // Where an expectation failed, when that is in the test's file.
  const said = /\((\S+\.sb):(\d+)\)$/m.exec(text);
  if (said && test.uri && path.basename(test.uri.fsPath) === path.basename(said[1])) {
    const line = Number(said[2]) - 1;
    message.location = new vscode.Location(test.uri, new vscode.Range(line, 0, line, 0));
  } else {
    message.location = location;
  }

  run.failed(test, message, duration);
}

/**
 * Runs one package's tests and reports each result as it is printed.
 * @param {vscode.TestRun} run
 * @param {string} root
 * @param {string[]} args
 * @param {Map<string, vscode.TestItem>} byName
 * @param {vscode.CancellationToken} token
 * @returns {Promise<string>} What it printed besides its events.
 */
async function runPackage(run, root, args, byName, token) {
  const reported = new Set();
  let buffer = "";
  let transcript = "";

  /** @param {string} line */
  function consume(line) {
    /** @type {Happened | undefined} */
    const happened = objectIn(line);
    if (!happened?.event) {
      // A diagnostic of the build, or the coverage's summary.
      transcript += `${line}\n`;
      run.appendOutput(`${line}\r\n`);
      return;
    }

    const test = byName.get(happened.name);
    if (happened.event === "ended") {
      const { passed, failed, skipped } = /** @type {any} */ (happened);
      run.appendOutput(`test result: ${failed > 0 ? "FAILED" : "ok"}. ${passed} passed; ${failed} failed; ${skipped} skipped\r\n`);
    }

    if (!test) {
      return;
    }

    if (happened.event === "started") {
      run.started(test);
    } else if (happened.event === "finished") {
      reported.add(happened.name);
      report(run, test, happened);
    }
  }

  let code;
  try {
    code = await sable.stream(args, {
      cwd: root,
      token,
      onOutput: (text) => {
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
