// @ts-check
// The Sable extension: the language server, commands, tasks, the Test
// Explorer and debugging. Each lives in a module of its own.
"use strict";

const vscode = require("vscode");
const client = require("./client");
const commands = require("./commands");
const tasks = require("./tasks");
const testing = require("./testing");
const debug = require("./debug");

/** @param {vscode.ExtensionContext} context */
async function activate(context) {
  commands.register(context);
  tasks.register(context);
  testing.register(context);
  debug.register(context);

  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration(async (event) => {
      if (event.affectsConfiguration("sable.path") || event.affectsConfiguration("sable.server.enable")) {
        const choice = await vscode.window.showInformationMessage("Sable: restart the language server to use the new setting?", "Restart");
        if (choice === "Restart") {
          await client.restart(context);
        }
      }
    }),
  );

  await client.start(context);
}

function deactivate() {
  return client.stop();
}

module.exports = { activate, deactivate };
