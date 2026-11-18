// @ts-nocheck
// A stand-in for the `vscode` module and for `vscode-languageclient/node`,
// enough of each for the extension's modules to load and run under plain
// node: what they register, run and show is recorded, so a test can look.
// VS Code's own test host needs a display and a download of VS Code; this
// needs neither, and runs wherever `npm test` does.
"use strict";

const fs = require("fs");
const path = require("path");
const Module = require("module");

class Disposable {
  constructor(dispose = () => {}) {
    this.dispose = dispose;
  }
}

class EventEmitter {
  constructor() {
    this.listeners = [];
    this.event = (listener) => {
      this.listeners.push(listener);
      return new Disposable(() => (this.listeners = this.listeners.filter((l) => l !== listener)));
    };
  }

  fire(value) {
    return Promise.all(this.listeners.map((l) => l(value)));
  }
}

class Uri {
  constructor(scheme, fsPath) {
    this.scheme = scheme;
    this.fsPath = fsPath;
    this.path = fsPath;
  }

  static file(file) {
    return new Uri("file", path.resolve(file));
  }

  static joinPath(base, ...parts) {
    return new Uri(base.scheme, path.join(base.fsPath, ...parts));
  }

  toString() {
    return `${this.scheme}://${this.fsPath}`;
  }
}

class Position {
  constructor(line, character) {
    this.line = line;
    this.character = character;
  }
}

class Range {
  constructor(startLine, startCharacter, endLine, endCharacter) {
    this.start = new Position(startLine, startCharacter);
    this.end = new Position(endLine, endCharacter);
  }
}

class Location {
  constructor(uri, range) {
    this.uri = uri;
    this.range = range;
  }
}

class CodeLens {
  constructor(range, command) {
    this.range = range;
    this.command = command;
  }
}

class CodeActionKind {
  constructor(value) {
    this.value = value;
  }
}
CodeActionKind.QuickFix = new CodeActionKind("quickfix");

class CodeAction {
  constructor(title, kind) {
    this.title = title;
    this.kind = kind;
  }
}

class MarkdownString {
  constructor(value = "") {
    this.value = value;
  }

  appendText(text) {
    this.value += text;
    return this;
  }

  appendCodeblock(code, language) {
    this.value += `\n\`\`\`${language}\n${code}\n\`\`\`\n`;
    return this;
  }
}

class TestMessage {
  constructor(message) {
    this.message = message;
  }
}

class ProcessExecution {
  constructor(process, args, options) {
    this.process = process;
    this.args = args;
    this.options = options;
  }
}

class Task {
  constructor(definition, scope, name, source, execution, problemMatchers) {
    Object.assign(this, { definition, scope, name, source, execution, problemMatchers });
  }
}

const TaskGroup = { Build: { id: "build" }, Test: { id: "test" }, Clean: { id: "clean" } };
const TaskScope = { Global: 1, Workspace: 2 };
const TaskRevealKind = { Always: 1, Silent: 2, Never: 3 };
const StatusBarAlignment = { Left: 1, Right: 2 };
const ProgressLocation = { SourceControl: 1, Window: 10, Notification: 15 };
const DebugConfigurationProviderTriggerKind = { Initial: 1, Dynamic: 2 };
const TestRunProfileKind = { Run: 1, Debug: 2, Coverage: 3 };

/** A test controller that keeps its items and records each run. */
function createTestController(id, label) {
  const items = new Map();
  const collection = (owner) => {
    const children = new Map();
    return {
      add: (item) => children.set(item.id, item),
      delete: (id) => children.delete(id),
      get: (id) => children.get(id),
      replace: (list) => {
        children.clear();
        list.forEach((item) => children.set(item.id, item));
      },
      forEach: (f) => [...children.values()].forEach((item) => f(item, owner)),
      get size() {
        return children.size;
      },
    };
  };
  const controller = {
    id,
    label,
    items: collection(undefined),
    profiles: [],
    runs: [],
    createTestItem(id, label, uri) {
      const item = { id, label, uri, busy: false, error: undefined };
      item.children = collection(item);
      items.set(id, item);
      return item;
    },
    createRunProfile(label, kind, handler, isDefault) {
      const profile = { label, kind, handler, isDefault };
      controller.profiles.push(profile);
      return profile;
    },
    createTestRun(request) {
      const run = { request, events: [], output: "", ended: false };
      for (const event of ["started", "passed", "failed", "errored", "skipped"]) {
        run[event] = (item, ...rest) => run.events.push({ event, id: item.id, rest });
      }

      run.appendOutput = (text) => (run.output += text);
      run.end = () => (run.ended = true);
      controller.runs.push(run);
      return run;
    },
    dispose() {},
  };
  return controller;
}

/**
 * A fresh `vscode` with folders, settings and an active editor as given.
 * @param {{ folders?: string[], settings?: Record<string, unknown>, folderSettings?: Record<string, Record<string, unknown>>, extensions?: string[] }} [world]
 */
function create(world = {}) {
  /** @type {Set<(session: { configuration: unknown }) => void>} */
  const terminated = new Set();
  const recorded = {
    commands: new Map(),
    executed: [],
    taskProviders: new Map(),
    executedTasks: [],
    debugProviders: [],
    started: [],
    codeLensProviders: [],
    codeActionProviders: [],
    messages: [],
    controllers: [],
    clients: [],
    configurationListeners: new EventEmitter(),
    saveListeners: new EventEmitter(),
  };

  const folders = (world.folders ?? []).map((folder, index) => ({ uri: Uri.file(folder), name: path.basename(folder), index }));
  const settings = { ...(world.settings ?? {}) };
  const folderSettings = world.folderSettings ?? {};

  const workspace = {
    workspaceFolders: folders.length > 0 ? folders : undefined,
    getWorkspaceFolder(uri) {
      // The deepest folder holding it, as VS Code answers.
      return folders
        .filter((f) => uri.fsPath === f.uri.fsPath || uri.fsPath.startsWith(f.uri.fsPath + path.sep))
        .sort((a, b) => b.uri.fsPath.length - a.uri.fsPath.length)[0];
    },
    getConfiguration(section, scope) {
      // A scope is a Uri, a folder or a document: VS Code takes each.
      const uri = scope && (scope.fsPath !== undefined ? scope : scope.uri);
      const folder = uri ? workspace.getWorkspaceFolder(uri) : undefined;
      const own = folder ? folderSettings[folder.uri.fsPath] ?? {} : {};
      return {
        get(key, fallback) {
          const full = `${section}.${key}`;
          return full in own ? own[full] : full in settings ? settings[full] : fallback;
        },
      };
    },
    async findFiles(glob, exclude) {
      // Only the one glob the extension uses: every package manifest.
      if (glob !== "**/{package.sb,sable.toml}" || !exclude.includes("target")) {
        throw new Error(`the mock does not know the glob ${glob}`);
      }

      const found = [];
      const walk = (directory) => {
        for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
          const full = path.join(directory, entry.name);
          if (entry.isDirectory()) {
            if (!["target", "vendor", "node_modules", ".git"].includes(entry.name)) {
              walk(full);
            }
          } else if (entry.name === "package.sb" || entry.name === "sable.toml") {
            found.push(Uri.file(full));
          }
        }
      };
      folders.forEach((f) => walk(f.uri.fsPath));
      return found;
    },
    onDidChangeConfiguration: recorded.configurationListeners.event,
    onDidSaveTextDocument: recorded.saveListeners.event,
    onDidChangeWorkspaceFolders: new EventEmitter().event,
    createFileSystemWatcher: (glob) => ({ glob, dispose() {} }),
    saveAll: async () => true,
    openTextDocument: async (options) => ({ uri: new Uri("untitled", "/explain"), ...options }),
  };

  const message = (level) => async (text, ...choices) => {
    recorded.messages.push({ level, text, choices });
    return undefined;
  };

  const window = {
    activeTextEditor: undefined,
    createOutputChannel: (name) => ({ name, text: "", append(t) { this.text += t; }, appendLine(t) { this.text += `${t}\n`; }, show() {}, dispose() {} }),
    createStatusBarItem: (id) => ({ id, show() { this.visible = true; }, dispose() {} }),
    showInformationMessage: message("info"),
    showWarningMessage: message("warning"),
    showErrorMessage: message("error"),
    showInputBox: async () => undefined,
    showQuickPick: async () => undefined,
    showOpenDialog: async () => undefined,
    showTextDocument: async () => undefined,
    withProgress: (_options, task) => task(),
  };

  const vscode = {
    Disposable,
    EventEmitter,
    Uri,
    Position,
    Range,
    Location,
    CodeLens,
    CodeAction,
    CodeActionKind,
    MarkdownString,
    TestMessage,
    Task,
    ProcessExecution,
    TaskGroup,
    TaskScope,
    TaskRevealKind,
    StatusBarAlignment,
    ProgressLocation,
    DebugConfigurationProviderTriggerKind,
    TestRunProfileKind,
    workspace,
    window,
    commands: {
      registerCommand(id, handler) {
        if (recorded.commands.has(id)) {
          throw new Error(`command '${id}' already exists`);
        }

        recorded.commands.set(id, handler);
        return new Disposable(() => recorded.commands.delete(id));
      },
      executeCommand: async (id, ...args) => recorded.executed.push({ id, args }),
    },
    languages: {
      registerCodeLensProvider: (selector, provider) => (recorded.codeLensProviders.push({ selector, provider }), new Disposable()),
      registerCodeActionsProvider: (selector, provider, metadata) => (recorded.codeActionProviders.push({ selector, provider, metadata }), new Disposable()),
    },
    tasks: {
      registerTaskProvider: (type, provider) => (recorded.taskProviders.set(type, provider), new Disposable()),
      executeTask: async (task) => recorded.executedTasks.push(task),
    },
    tests: {
      createTestController: (id, label) => {
        const controller = createTestController(id, label);
        recorded.controllers.push(controller);
        return controller;
      },
    },
    debug: {
      registerDebugConfigurationProvider: (type, provider, trigger) => (recorded.debugProviders.push({ type, provider, trigger }), new Disposable()),
      // A session ends as soon as it starts: what was started is recorded.
      startDebugging: async (folder, configuration) => {
        recorded.started.push({ folder, configuration });
        setImmediate(() => terminated.forEach((listener) => listener({ configuration })));
        return true;
      },
      onDidTerminateDebugSession: (listener) => {
        terminated.add(listener);
        return new Disposable(() => terminated.delete(listener));
      },
    },
    extensions: {
      getExtension: (id) => ((world.extensions ?? []).includes(id) ? { id } : undefined),
    },
  };

  const languageclient = {
    TransportKind: { stdio: 0, ipc: 1, pipe: 2, socket: 3 },
    State: { Stopped: 1, Starting: 3, Running: 2 },
    LanguageClient: class {
      constructor(id, name, serverOptions, clientOptions) {
        Object.assign(this, { id, name, serverOptions, clientOptions, running: false, stateListeners: [] });
        recorded.clients.push(this);
      }

      onDidChangeState(listener) {
        this.stateListeners.push(listener);
        return new Disposable();
      }

      async start() {
        this.stateListeners.forEach((l) => l({ oldState: 1, newState: 3 }));
        this.running = true;
        this.stateListeners.forEach((l) => l({ oldState: 3, newState: 2 }));
      }

      isRunning() {
        return this.running;
      }

      async stop() {
        this.running = false;
        this.stateListeners.forEach((l) => l({ oldState: 2, newState: 1 }));
      }
    },
  };

  return { vscode, languageclient, recorded };
}

const source = path.join(__dirname, "..", "src");
const originalLoad = Module._load;

/**
 * Loads the extension's modules afresh against a new mock: `require` of
 * `vscode` and of the language client gets the mock's.
 * @param {Parameters<typeof create>[0]} [world]
 */
function load(world) {
  const mock = create(world);
  for (const key of Object.keys(require.cache)) {
    if (key.startsWith(source + path.sep)) {
      delete require.cache[key];
    }
  }

  Module._load = function (request, parent, isMain) {
    if (request === "vscode") {
      return mock.vscode;
    }

    if (request === "vscode-languageclient/node") {
      return mock.languageclient;
    }

    return originalLoad.call(this, request, parent, isMain);
  };

  const modules = {};
  for (const name of ["sable", "client", "commands", "tasks", "testing", "debug", "extension"]) {
    modules[name] = require(path.join(source, name));
  }

  return { ...mock, modules };
}

/** A context as VS Code passes `activate`. */
function context(storage) {
  return {
    subscriptions: [],
    globalStorageUri: Uri.file(path.join(storage, "global")),
    storageUri: Uri.file(path.join(storage, "workspace")),
    extensionUri: Uri.file(path.join(__dirname, "..")),
  };
}

module.exports = { load, context, Uri };
