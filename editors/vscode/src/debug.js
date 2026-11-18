// @ts-check
// Debugging: a `sable` launch configuration builds the program with debug
// information, then hands it to an lldb debug adapter — LLVM's lldb-dap
// extension or CodeLLDB, whichever is installed — with Sable's formatters
// loaded, so that a String shows its text and a union its case.
"use strict";

const vscode = require("vscode");
const fs = require("fs");
const path = require("path");
const sable = require("./sable");

/** The debug adapters we can hand a program to, in order of preference. */
const ADAPTERS = [
  { extension: "llvm-vs-code-extensions.lldb-dap", type: "lldb-dap" },
  { extension: "vadimcn.vscode-lldb", type: "lldb" },
];

/** @param {vscode.ExtensionContext} context */
function register(context) {
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider("sable", new Provider(context)),
    vscode.debug.registerDebugConfigurationProvider("sable", {
      provideDebugConfigurations: (folder) => initialConfigurations(folder),
    }, vscode.DebugConfigurationProviderTriggerKind.Initial),
  );
}

/** @param {vscode.WorkspaceFolder | undefined} folder */
function initialConfigurations(folder) {
  const hasPackage = folder ? fs.existsSync(path.join(folder.uri.fsPath, "sable.toml")) : false;
  return [
    hasPackage
      ? { type: "sable", request: "launch", name: "Debug the package", args: [], cwd: "${workspaceFolder}" }
      : { type: "sable", request: "launch", name: "Debug this file", program: "${file}", args: [], cwd: "${fileDirname}" },
  ];
}

/** @implements {vscode.DebugConfigurationProvider} */
class Provider {
  /** @param {vscode.ExtensionContext} context */
  constructor(context) {
    this.context = context;
  }

  /**
   * F5 with no launch.json: debug the package the active file is in, or the file.
   * @param {vscode.WorkspaceFolder | undefined} _folder
   * @param {vscode.DebugConfiguration} configuration
   */
  resolveDebugConfiguration(_folder, configuration) {
    if (!configuration.type && !configuration.request && !configuration.name) {
      const editor = vscode.window.activeTextEditor;
      if (editor?.document.languageId !== "sable") {
        return undefined;
      }

      const root = sable.packageRoot(editor.document.uri);
      return root
        ? { type: "sable", request: "launch", name: "Debug the package", cwd: root, package: root }
        : { type: "sable", request: "launch", name: "Debug this file", program: editor.document.uri.fsPath };
    }

    return configuration;
  }

  /**
   * Builds, then becomes the adapter's own configuration.
   * @param {vscode.WorkspaceFolder | undefined} folder
   * @param {vscode.DebugConfiguration} configuration
   * @param {vscode.CancellationToken} [token]
   */
  async resolveDebugConfigurationWithSubstitutedVariables(folder, configuration, token) {
    const adapter = ADAPTERS.find((a) => vscode.extensions.getExtension(a.extension));
    if (!adapter) {
      const choice = await vscode.window.showErrorMessage(
        "Sable: debugging needs an lldb debug adapter: install LLVM's 'LLDB DAP' extension or CodeLLDB.",
        "Find LLDB DAP",
        "Find CodeLLDB",
      );
      if (choice) {
        vscode.commands.executeCommand("workbench.extensions.search", choice === "Find LLDB DAP" ? ADAPTERS[0].extension : ADAPTERS[1].extension);
      }

      return undefined;
    }

    let executable;
    try {
      executable = await build(this.context, folder, configuration, token);
    } catch (error) {
      vscode.window.showErrorMessage(`Sable: ${error instanceof Error ? error.message : String(error)}`);
      return undefined;
    }

    if (!executable) {
      return undefined;
    }

    const plugin = path.join(this.context.globalStorageUri.fsPath, "sable_lldb.py");
    const written = await sable.run(["debug", "--write-plugin", plugin]);
    const initCommands = [
      // A macOS .dSYM carries the same plugin; it is loaded here instead, and once.
      "settings set target.load-script-from-symbol-file false",
      ...(written.code === 0 ? [`command script import "${plugin}"`] : []),
      ...(Array.isArray(configuration.initCommands) ? configuration.initCommands : []),
    ];

    const { package: _package, ...rest } = configuration;
    return {
      ...rest,
      type: adapter.type,
      request: "launch",
      program: executable,
      args: Array.isArray(configuration.args) ? configuration.args : [],
      cwd: configuration.cwd ?? (configuration.package || path.dirname(executable)),
      initCommands,
      ...(adapter.type === "lldb" ? { sourceLanguages: ["sable"] } : {}),
    };
  }
}

/**
 * Builds what the configuration names with debug information: a .sb file
 * on its own, or a package (dev builds carry debug information).
 * @param {vscode.ExtensionContext} context
 * @param {vscode.WorkspaceFolder | undefined} folder
 * @param {vscode.DebugConfiguration} configuration
 * @param {vscode.CancellationToken} [token]
 * @returns {Promise<string | undefined>} the executable, or undefined when the build failed
 */
async function build(context, folder, configuration, token) {
  const program = typeof configuration.program === "string" ? configuration.program : undefined;
  if (program && !program.endsWith(".sb")) {
    return program; // Already built, by a preLaunchTask or by hand.
  }

  /** @type {string[]} */
  let args;
  let cwd;
  let executable;
  if (program) {
    const storage = context.storageUri?.fsPath ?? context.globalStorageUri.fsPath;
    const directory = path.join(storage, "debug");
    fs.mkdirSync(directory, { recursive: true });
    executable = path.join(directory, path.basename(program, ".sb") + (process.platform === "win32" ? ".exe" : ""));
    args = ["build", program, "-g", "-o", executable];
    cwd = path.dirname(program);
  } else {
    const root = typeof configuration.package === "string" ? configuration.package
      : typeof configuration.cwd === "string" && fs.existsSync(path.join(configuration.cwd, "sable.toml")) ? configuration.cwd
        : folder?.uri.fsPath;
    if (!root || !fs.existsSync(path.join(root, "sable.toml"))) {
      throw new Error("no package to debug: set 'program' to a .sb file, or 'package' to a directory with a sable.toml");
    }

    const bin = typeof configuration.bin === "string" ? configuration.bin : sable.packageName(root);
    args = ["build", ...(typeof configuration.bin === "string" ? ["--bin", configuration.bin] : []), ...(Array.isArray(configuration.buildArgs) ? configuration.buildArgs : [])];
    cwd = root;
    executable = path.join(root, "target", "dev", (bin ?? "main") + (process.platform === "win32" ? ".exe" : ""));
  }

  const output = sable.channel();
  output.appendLine(`> sable ${args.join(" ")}`);
  const result = await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Window, title: "Sable: building for the debugger" },
    () => sable.run(args, { cwd, token }),
  );
  output.append(result.stdout);
  output.append(result.stderr);
  if (result.code !== 0) {
    output.show(true);
    vscode.window.showErrorMessage("Sable: the build failed; the output says why.");
    return undefined;
  }

  return executable;
}

module.exports = { register };
