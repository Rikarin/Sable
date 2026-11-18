// @ts-check
// Tasks: `sable build`, `run`, `test`, `check`, `bench`, `fmt` and `clean`
// for every package in the workspace, with the compiler's diagnostics
// matched into the Problems panel by `$sable`.
"use strict";

const vscode = require("vscode");
const path = require("path");
const sable = require("./sable");

const TYPE = "sable";

/** @type {{ command: string, group?: vscode.TaskGroup, matcher: boolean, detail: string }[]} */
const COMMANDS = [
  { command: "build", group: vscode.TaskGroup.Build, matcher: true, detail: "compile the package" },
  { command: "check", group: vscode.TaskGroup.Build, matcher: true, detail: "type-check and lint, without generating code" },
  { command: "run", matcher: true, detail: "compile and run" },
  { command: "test", group: vscode.TaskGroup.Test, matcher: true, detail: "run the @test functions" },
  { command: "bench", matcher: true, detail: "run the @bench functions" },
  { command: "fmt", matcher: false, detail: "format every source file" },
  { command: "clean", group: vscode.TaskGroup.Clean, matcher: false, detail: "remove target/" },
];

/**
 * @param {string} command
 * @param {string[]} args
 * @param {string} cwd
 * @param {vscode.WorkspaceFolder | vscode.TaskScope} scope
 * @param {Record<string, unknown>} [definition]
 */
function task(command, args, cwd, scope, definition) {
  const known = COMMANDS.find((c) => c.command === command);
  const name = args.length > 0 ? `${command} ${args.join(" ")}` : command;
  const created = new vscode.Task(
    /** @type {vscode.TaskDefinition} */ (definition ?? { type: TYPE, command, args, cwd }),
    scope,
    name,
    "sable",
    new vscode.ProcessExecution(sable.binary(), [command, ...args], { cwd }),
    known?.matcher === false ? [] : ["$sable"],
  );
  if (known) {
    created.detail = known.detail;
    if (known.group) {
      created.group = known.group;
    }
  }

  return created;
}

/** @type {vscode.TaskProvider} */
const provider = {
  async provideTasks() {
    const manifests = await vscode.workspace.findFiles("**/sable.toml", "**/{target,vendor,node_modules,.git}/**");
    const tasks = [];
    for (const manifest of manifests) {
      if (!sable.isPackage(manifest.fsPath)) {
        continue;
      }

      const folder = vscode.workspace.getWorkspaceFolder(manifest);
      if (!folder) {
        continue;
      }

      const root = path.dirname(manifest.fsPath);
      const relative = path.relative(folder.uri.fsPath, root);
      for (const { command } of COMMANDS) {
        const created = task(command, [], root, folder, { type: TYPE, command, cwd: relative ? `\${workspaceFolder}/${relative}` : "${workspaceFolder}" });
        if (relative) {
          created.name = `${command} (${relative})`;
        }

        tasks.push(created);
      }
    }

    return tasks;
  },

  resolveTask(resolved) {
    const definition = resolved.definition;
    if (typeof definition.command !== "string") {
      return undefined;
    }

    const scope = resolved.scope ?? vscode.TaskScope.Workspace;
    const folder = typeof scope === "object" ? scope.uri.fsPath : vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();
    const cwd = typeof definition.cwd === "string" ? definition.cwd.replace(/\$\{workspaceFolder\}/g, folder) : folder;
    const args = Array.isArray(definition.args) ? definition.args.map(String) : [];
    return task(definition.command, args, cwd, scope, definition);
  },
};

/** @param {vscode.ExtensionContext} context */
function register(context) {
  context.subscriptions.push(vscode.tasks.registerTaskProvider(TYPE, provider));
}

module.exports = { register, task };
