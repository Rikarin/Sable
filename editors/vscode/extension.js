// Starts `sable lsp` for .sab files. Everything else is the server's.
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

let client;

function activate(context) {
  const command = vscode.workspace.getConfiguration("sable").get("path", "sable");
  const server = { command, args: ["lsp"], transport: TransportKind.stdio };

  client = new LanguageClient(
    "sable",
    "Sable",
    { run: server, debug: server },
    {
      documentSelector: [{ scheme: "file", language: "sable" }],
      synchronize: {
        fileEvents: vscode.workspace.createFileSystemWatcher("**/*.sab"),
      },
    },
  );

  context.subscriptions.push(client);
  return client.start();
}

function deactivate() {
  return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
