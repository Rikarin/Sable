// @ts-check
// Commands, the Run | Debug code lens over `main`, and the "Explain" quick
// fix on every diagnostic that has a code.
"use strict";

const vscode = require("vscode");
const path = require("path");
const sable = require("./sable");
const client = require("./client");
const tasks = require("./tasks");

/** @param {vscode.ExtensionContext} context */
function register(context) {
  const command = (/** @type {string} */ id, /** @type {(...args: any[]) => any} */ handler) =>
    context.subscriptions.push(vscode.commands.registerCommand(id, handler));

  command("sable.restartServer", () => client.restart(context));
  command("sable.showOutput", () => sable.channel().show());
  command("sable.run", (/** @type {vscode.Uri | undefined} */ uri) => runTask("run", uri));
  command("sable.build", (/** @type {vscode.Uri | undefined} */ uri) => runTask("build", uri));
  command("sable.check", (/** @type {vscode.Uri | undefined} */ uri) => runTask("check", uri));
  command("sable.test", (/** @type {vscode.Uri | undefined} */ uri) => runTask("test", uri));
  command("sable.bench", (/** @type {vscode.Uri | undefined} */ uri) => runTask("bench", uri));
  command("sable.debug", (/** @type {vscode.Uri | undefined} */ uri) => debug(uri));
  command("sable.explain", (/** @type {string | undefined} */ code) => explain(code));
  command("sable.newPackage", () => newPackage());

  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider({ language: "sable" }, new MainLens()),
    vscode.languages.registerCodeActionsProvider({ language: "sable" }, new ExplainAction(), {
      providedCodeActionKinds: [vscode.CodeActionKind.QuickFix],
    }),
  );
}

/**
 * The file a command is about: the one it was invoked on, else the active editor's.
 * @param {vscode.Uri | undefined} uri
 */
function target(uri) {
  const file = uri instanceof vscode.Uri ? uri : vscode.window.activeTextEditor?.document.uri;
  if (!file || file.scheme !== "file") {
    vscode.window.showWarningMessage("Sable: open a .sb file first.");
    return undefined;
  }

  return file;
}

/**
 * Runs a sable command as a task in the terminal: over the package the file
 * is in, or over the file alone when it is in none.
 * @param {string} name
 * @param {vscode.Uri | undefined} uri
 */
async function runTask(name, uri) {
  const file = target(uri);
  if (!file) {
    return;
  }

  // An unsaved file would be built as it is on disk.
  await vscode.workspace.saveAll(false);
  const root = sable.packageRoot(file);
  const scope = vscode.workspace.getWorkspaceFolder(file) ?? vscode.TaskScope.Workspace;
  const loose = [path.basename(file.fsPath)];
  const created = root
    ? tasks.task(name, [], root, scope)
    : tasks.task(name, loose, path.dirname(file.fsPath), scope);
  if (name === "run" || name === "test" || name === "bench") {
    created.presentationOptions = { reveal: vscode.TaskRevealKind.Always, focus: false, clear: true };
  } else {
    created.presentationOptions = { reveal: vscode.TaskRevealKind.Silent, clear: true };
  }

  await vscode.tasks.executeTask(created);
}

/** @param {vscode.Uri | undefined} uri */
async function debug(uri) {
  const file = target(uri);
  if (!file) {
    return;
  }

  await vscode.workspace.saveAll(false);
  const root = sable.packageRoot(file);
  const folder = vscode.workspace.getWorkspaceFolder(file);
  await vscode.debug.startDebugging(folder, root
    ? { type: "sable", request: "launch", name: "Debug the package", package: root, cwd: root }
    : { type: "sable", request: "launch", name: `Debug ${path.basename(file.fsPath)}`, program: file.fsPath, cwd: path.dirname(file.fsPath) });
}

/**
 * Shows `sable explain <code>`'s page as rendered Markdown.
 * @param {string | undefined} code
 */
async function explain(code) {
  code ??= await vscode.window.showInputBox({
    title: "Explain a diagnostic",
    prompt: "A diagnostic code, like S3004",
    validateInput: (value) => (/^\s*[sS]?\d{4}\s*$/.test(value) ? undefined : "codes look like S3004"),
  });
  if (!code) {
    return;
  }

  try {
    const { code: exit, stdout, stderr } = await sable.run(["explain", code.trim()]);
    if (exit !== 0) {
      vscode.window.showWarningMessage(`Sable: ${(stderr || stdout).trim() || `no page for ${code}`}`);
      return;
    }

    const document = await vscode.workspace.openTextDocument({ language: "markdown", content: stdout });
    await vscode.commands.executeCommand("markdown.showPreview", document.uri);
  } catch (error) {
    vscode.window.showErrorMessage(`Sable: ${error instanceof Error ? error.message : String(error)}`);
  }
}

async function newPackage() {
  const kind = await vscode.window.showQuickPick(
    [
      { label: "Binary", description: "a program, with src/main.sb", flag: "--bin" },
      { label: "Library", description: "a library, with src/lib.sb", flag: "--lib" },
    ],
    { title: "New Sable package" },
  );
  if (!kind) {
    return;
  }

  const name = await vscode.window.showInputBox({
    title: "New Sable package",
    prompt: "The package's name",
    validateInput: (value) => (/^[a-z][a-z0-9_-]*$/.test(value) ? undefined : "lower-case letters, digits, '-' and '_', starting with a letter"),
  });
  if (!name) {
    return;
  }

  const parent = await vscode.window.showOpenDialog({
    canSelectFiles: false,
    canSelectFolders: true,
    openLabel: "Create Here",
    defaultUri: sable.folderOf()?.uri,
  });
  if (!parent?.[0]) {
    return;
  }

  try {
    const result = await sable.run(["new", name, kind.flag], { cwd: parent[0].fsPath });
    if (result.code !== 0) {
      vscode.window.showErrorMessage(`Sable: ${(result.stderr || result.stdout).trim()}`);
      return;
    }

    const created = vscode.Uri.joinPath(parent[0], name);
    const inWorkspace = vscode.workspace.getWorkspaceFolder(created);
    if (inWorkspace) {
      const main = vscode.Uri.joinPath(created, "src", kind.flag === "--lib" ? "lib.sb" : "main.sb");
      await vscode.window.showTextDocument(main);
    } else {
      await vscode.commands.executeCommand("vscode.openFolder", created, { forceNewWindow: false });
    }
  } catch (error) {
    vscode.window.showErrorMessage(`Sable: ${error instanceof Error ? error.message : String(error)}`);
  }
}

/** "Run | Debug" over a program's entry point: `fn main` or an `@main` function. */
/** @implements {vscode.CodeLensProvider} */
class MainLens {
  /** @param {vscode.TextDocument} document */
  provideCodeLenses(document) {
    if (!vscode.workspace.getConfiguration("sable", document).get("codeLens.enable", true)) {
      return [];
    }

    const lenses = [];
    let marked = false;
    for (let line = 0; line < document.lineCount; line++) {
      const text = document.lineAt(line).text;
      if (/^\s*@main\b/.test(text)) {
        marked = true;
        continue;
      }

      if (/^\s*(?:public\s+)?(?:async\s+)?fn\s+main\s*\(/.test(text) || (marked && /^\s*(?:public\s+)?(?:async\s+)?fn\s+\w+\s*\(/.test(text))) {
        const range = new vscode.Range(line, 0, line, 0);
        lenses.push(
          new vscode.CodeLens(range, { title: "▶ Run", command: "sable.run", arguments: [document.uri] }),
          new vscode.CodeLens(range, { title: "Debug", command: "sable.debug", arguments: [document.uri] }),
        );
        break;
      }

      if (text.trim() && !text.trim().startsWith("//") && !text.trim().startsWith("@")) {
        marked = false;
      }
    }

    return lenses;
  }
}

/** A quick fix on each coded diagnostic: open its page from `sable explain`. */
/** @implements {vscode.CodeActionProvider} */
class ExplainAction {
  /**
   * @param {vscode.TextDocument} _document
   * @param {vscode.Range} _range
   * @param {vscode.CodeActionContext} context
   */
  provideCodeActions(_document, _range, context) {
    const codes = new Set();
    for (const diagnostic of context.diagnostics) {
      const code = typeof diagnostic.code === "object" ? diagnostic.code.value : diagnostic.code;
      if (diagnostic.source === "sable" && typeof code === "string" && /^S\d{4}$/.test(code)) {
        codes.add(code);
      }
    }

    return [...codes].map((code) => {
      const action = new vscode.CodeAction(`Explain ${code}`, vscode.CodeActionKind.QuickFix);
      action.command = { title: `Explain ${code}`, command: "sable.explain", arguments: [code] };
      action.isPreferred = false;
      return action;
    });
  }
}

module.exports = { register };
