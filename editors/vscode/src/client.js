// @ts-check
// The language client: `sable lsp` over stdio, and a status bar item that
// says how it is doing.
"use strict";

const vscode = require("vscode");
const { LanguageClient, TransportKind, State } = require("vscode-languageclient/node");
const sable = require("./sable");
const freshness = require("./freshness");

/** @type {LanguageClient | undefined} */
let client;

/** The check, after the server started, that its sable is not older than the workspace's compiler sources. */
let checked = Promise.resolve();

/** @type {vscode.StatusBarItem | undefined} */
let status;

/** @param {vscode.ExtensionContext} context */
async function start(context) {
  status ??= createStatus(context);
  if (!vscode.workspace.getConfiguration("sable").get("server.enable", true)) {
    showStatus("$(circle-slash) Sable", "The language server is off ('sable.server.enable')", undefined);
    return;
  }

  const command = sable.binary();
  const server = { command, args: ["lsp"], transport: TransportKind.stdio };
  client = new LanguageClient(
    "sable",
    "Sable Language Server",
    { run: server, debug: server },
    {
      documentSelector: [
        { scheme: "file", language: "sable" },
        { scheme: "untitled", language: "sable" },
      ],
      // What the server starts with; a change is sent as the "sable"
      // section of workspace/didChangeConfiguration, which it reads the same.
      initializationOptions: serverSettings(),
      synchronize: {
        // Sources, and what decides how they are compiled: a manifest
        // (package.sb, or sable.toml) or the lock file changing makes the
        // server read the packages again.
        fileEvents: vscode.workspace.createFileSystemWatcher("**/{*.sb,sable.toml,sable.lock}"),
        // The server's settings ('sable.diagnostics.*', hints and lints),
        // sent again whenever they change.
        configurationSection: "sable",
      },
      outputChannel: sable.channel(),
      markdown: { isTrusted: false, supportHtml: false },
    },
  );

  client.onDidChangeState(({ newState }) => {
    switch (newState) {
      case State.Starting:
        showStatus("$(sync~spin) Sable", "The language server is starting", "sable.showOutput");
        break;
      case State.Running:
        showStatus("$(check) Sable", `The language server is running: ${command} lsp`, "sable.showOutput");
        break;
      case State.Stopped:
        showStatus("$(error) Sable", "The language server stopped. Click to restart it.", "sable.restartServer");
        break;
    }
  });

  try {
    await client.start();
    // Not waited for: the server is up, and the check runs `sable --version`.
    checked = freshness.warnIfStale(command).catch((error) => sable.channel().appendLine(`checking the server's build failed: ${error}`));
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    showStatus("$(error) Sable", `The language server did not start: ${message}`, "sable.restartServer");
    const choice = await vscode.window.showErrorMessage(
      `Sable: could not start '${command} lsp'. Install sable, or set 'sable.path' to it.`,
      "Open Settings",
      "Show Output",
    );
    if (choice === "Open Settings") {
      vscode.commands.executeCommand("workbench.action.openSettings", "sable.path");
    } else if (choice === "Show Output") {
      sable.channel().show();
    }
  }
}

/** The settings the server takes: inlay hints and lints, on or off. */
function serverSettings() {
  const configuration = vscode.workspace.getConfiguration("sable");
  return {
    inlayHints: { enable: configuration.get("inlayHints.enable", true) },
    lints: { enable: configuration.get("lints.enable", true) },
    diagnostics: { workspace: configuration.get("diagnostics.workspace", true) },
  };
}

async function stop() {
  const stopping = client;
  client = undefined;
  if (stopping?.isRunning()) {
    await stopping.stop();
  }
}

/** @param {vscode.ExtensionContext} context */
async function restart(context) {
  await stop();
  await start(context);
}

/** @param {vscode.ExtensionContext} context */
function createStatus(context) {
  const item = vscode.window.createStatusBarItem("sable.status", vscode.StatusBarAlignment.Left, 0);
  item.name = "Sable";
  context.subscriptions.push(item);
  return item;
}

/**
 * @param {string} text
 * @param {string} tooltip
 * @param {string | undefined} command
 */
function showStatus(text, tooltip, command) {
  if (!status) {
    return;
  }

  status.text = text;
  status.tooltip = tooltip;
  status.command = command;
  status.show();
}

/** The latest check of the server's build, for what waits for it. */
function whenChecked() {
  return checked;
}

module.exports = { start, stop, restart, serverSettings, whenChecked };
