// @ts-check
// Whether the sable the language server runs is older than the compiler's
// sources in the workspace, and rebuilding it. Inside the Sable repository
// the server compiles the standard library from the workspace's lib/; a
// sable built before lib/ last changed may not compile it at all (#284).
"use strict";

const vscode = require("vscode");
const cp = require("child_process");
const fs = require("fs");
const path = require("path");
const sable = require("./sable");

/** The command that builds the driver, in the repository: what the extension prefers to start. */
const BUILD = ["build", "src/Sable.Driver", "-c", "Release", "-m:2"];

/**
 * Whether a folder is the compiler's own repository.
 * @param {string} root
 */
function isCompilerRepository(root) {
  return fs.existsSync(path.join(root, "src", "Sable.Driver", "Sable.Driver.csproj")) && fs.existsSync(path.join(root, "lib", "Sable.Core"));
}

/**
 * The file a command runs: itself, when it is a path; a bare name, as
 * found on the PATH. Undefined when there is none.
 * @param {string} command
 * @returns {string | undefined}
 */
function resolve(command) {
  if (command.includes("/") || command.includes("\\")) {
    return fs.existsSync(command) ? fs.realpathSync(command) : undefined;
  }

  const extensions = process.platform === "win32" ? (process.env.PATHEXT ?? ".EXE").split(";") : [""];
  for (const directory of (process.env.PATH ?? "").split(path.delimiter)) {
    for (const extension of extensions) {
      const candidate = directory && path.join(directory, command + extension);
      if (candidate && fs.existsSync(candidate)) {
        return fs.realpathSync(candidate);
      }
    }
  }

  return undefined;
}

/**
 * When a sable was built: the newer of the executable and its `sable.dll`,
 * which holds the compiler and the embedded library (an incremental build
 * may leave the executable's launcher as it was).
 * @param {string} file
 * @returns {number | undefined} milliseconds since the epoch
 */
function builtAt(file) {
  let newest;
  for (const candidate of [file, path.join(path.dirname(file), "sable.dll")]) {
    try {
      const time = fs.statSync(candidate).mtimeMs;
      newest = newest === undefined || time > newest ? time : newest;
    } catch {
      // Not there.
    }
  }

  return newest;
}

/**
 * The most recently changed source of the compiler — under lib/ and src/,
 * leaving out builds' output and hidden folders.
 * @param {string} root
 * @returns {{ file: string, time: number } | undefined}
 */
function newestSource(root) {
  /** @type {{ file: string, time: number } | undefined} */
  let newest;
  /** @param {string} directory */
  const walk = (directory) => {
    let entries;
    try {
      entries = fs.readdirSync(directory, { withFileTypes: true });
    } catch {
      return;
    }

    for (const entry of entries) {
      if (entry.name.startsWith(".") || (entry.isDirectory() && (entry.name === "bin" || entry.name === "obj"))) {
        continue;
      }

      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(full);
      } else if (/\.(sb|cs|csproj|xml|md)$/.test(entry.name)) {
        const time = fs.statSync(full).mtimeMs;
        if (!newest || time > newest.time) {
          newest = { file: full, time };
        }
      }
    }
  };
  walk(path.join(root, "lib"));
  walk(path.join(root, "src"));
  return newest;
}

/**
 * What `sable --version` says: its version and the commit it was built from.
 * @param {string} file
 * @returns {Promise<string | undefined>}
 */
function versionOf(file) {
  return new Promise((done) => {
    cp.execFile(file, ["--version"], { timeout: 10000 }, (error, stdout) => done(error ? undefined : stdout.trim() || undefined));
  });
}

/**
 * Whether the sable the server runs was built before the workspace's
 * compiler sources last changed: what the check found, or undefined when
 * the workspace is not the compiler's repository or the binary is not found.
 * @param {string} command
 * @param {string} root
 * @returns {{ binary: string, built: number, newest: { file: string, time: number }, stale: boolean } | undefined}
 */
function compare(command, root) {
  const binary = resolve(command);
  const built = binary ? builtAt(binary) : undefined;
  const newest = newestSource(root);
  if (!binary || built === undefined || !newest) {
    return undefined;
  }

  return { binary, built, newest, stale: newest.time > built };
}

/**
 * Warns, once per start of the server, when its sable is older than the
 * compiler's sources in the workspace, and offers to rebuild it when it is
 * the repository's own build.
 * @param {string} command
 * @returns {Promise<void>}
 */
async function warnIfStale(command) {
  for (const folder of vscode.workspace.workspaceFolders ?? []) {
    const root = folder.uri.fsPath;
    if (!isCompilerRepository(root)) {
      continue;
    }

    const found = compare(command, root);
    if (!found?.stale) {
      continue;
    }

    const version = (await versionOf(found.binary)) ?? found.binary;
    const ownBuild = found.binary.startsWith(fs.realpathSync(root) + path.sep);
    const changed = path.relative(root, found.newest.file);
    const message =
      `Sable: the language server's ${version} was built ${new Date(found.built).toLocaleString()}, ` +
      `before ${changed} changed (${new Date(found.newest.time).toLocaleString()}). ` +
      "It may not compile this workspace's library.";
    sable.channel().appendLine(message);
    const choices = ownBuild ? ["Rebuild", "Not Now"] : ["Open Settings", "Not Now"];
    const choice = await vscode.window.showWarningMessage(message, ...choices);
    if (choice === "Rebuild") {
      await vscode.commands.executeCommand("sable.rebuildServer");
    } else if (choice === "Open Settings") {
      await vscode.commands.executeCommand("workbench.action.openSettings", "sable.path");
    }

    return;
  }
}

/**
 * Builds the driver in the compiler's repository — with the server
 * stopped, since the build replaces the files it runs from — then starts
 * the server again on the new build.
 * @param {{ stop: () => Promise<void>, start: () => Promise<void> }} server
 * @returns {Promise<boolean>} whether the build succeeded
 */
async function rebuild(server) {
  const folder = (vscode.workspace.workspaceFolders ?? []).find((f) => isCompilerRepository(f.uri.fsPath));
  if (!folder) {
    vscode.window.showWarningMessage("Sable: rebuilding the language server needs the Sable repository open; install a newer sable instead.");
    return false;
  }

  const output = sable.channel();
  output.appendLine(`> dotnet ${BUILD.join(" ")}`);
  await server.stop();
  const code = await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Sable: rebuilding the language server" },
    () =>
      new Promise((done) => {
        const child = cp.spawn("dotnet", BUILD, { cwd: folder.uri.fsPath, env: process.env });
        child.stdout.setEncoding("utf8").on("data", (text) => output.append(text));
        child.stderr.setEncoding("utf8").on("data", (text) => output.append(text));
        child.on("error", (error) => {
          output.appendLine(`cannot run dotnet: ${error.message}`);
          done(-1);
        });
        child.on("close", done);
      }),
  );

  if (code !== 0) {
    output.show(true);
    vscode.window.showErrorMessage("Sable: the build failed; the output says why. The server runs the old sable.");
  }

  await server.start();
  return code === 0;
}

module.exports = { isCompilerRepository, resolve, builtAt, newestSource, compare, warnIfStale, rebuild, BUILD };
