// @ts-check
// Finding the sable binary, running it, and finding the package a file is in.
"use strict";

const vscode = require("vscode");
const cp = require("child_process");
const fs = require("fs");
const path = require("path");

/** @type {vscode.OutputChannel | undefined} */
let output;

/** The channel the extension's own messages and the commands it runs go to. */
function channel() {
  output ??= vscode.window.createOutputChannel("Sable");
  return output;
}

/**
 * The sable binary: the `sable.path` setting, with `${workspaceFolder}` and
 * `~` expanded. Left at its default, `sable` on the PATH — or, when there is
 * none and the workspace is the compiler's own repository, the driver built
 * there, so that the compiler's developers need not install it.
 * @returns {string}
 */
function binary() {
  const configured = vscode.workspace.getConfiguration("sable").get("path", "sable") || "sable";
  const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? "";
  let expanded = configured.replace(/\$\{workspaceFolder\}/g, folder);
  if (expanded.startsWith("~/")) {
    expanded = path.join(require("os").homedir(), expanded.slice(2));
  }

  if (expanded !== "sable" || onPath("sable")) {
    return expanded;
  }

  for (const workspace of vscode.workspace.workspaceFolders ?? []) {
    for (const configuration of ["Release", "Debug"]) {
      const built = path.join(workspace.uri.fsPath, "src", "Sable.Driver", "bin", configuration, "net11.0", process.platform === "win32" ? "sable.exe" : "sable");
      if (fs.existsSync(built)) {
        return built;
      }
    }
  }

  return expanded;
}

/** @param {string} name */
function onPath(name) {
  const extensions = process.platform === "win32" ? (process.env.PATHEXT ?? ".EXE").split(";") : [""];
  for (const directory of (process.env.PATH ?? "").split(path.delimiter)) {
    for (const extension of extensions) {
      if (directory && fs.existsSync(path.join(directory, name + extension))) {
        return true;
      }
    }
  }

  return false;
}

/**
 * Runs sable to completion.
 * @param {string[]} args
 * @param {{ cwd?: string, token?: vscode.CancellationToken, input?: string }} [options]
 * @returns {Promise<{ code: number | null, stdout: string, stderr: string }>}
 */
function run(args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = cp.spawn(binary(), args, { cwd: options.cwd, env: process.env });
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8").on("data", (chunk) => (stdout += chunk));
    child.stderr.setEncoding("utf8").on("data", (chunk) => (stderr += chunk));
    child.on("error", (error) => reject(notFound(error)));
    child.on("close", (code) => resolve({ code, stdout, stderr }));
    options.token?.onCancellationRequested(() => child.kill());
    if (options.input !== undefined) {
      child.stdin.end(options.input);
    } else {
      child.stdin.end();
    }
  });
}

/**
 * Runs sable, streaming what it writes to `onOutput` as it comes.
 * @param {string[]} args
 * @param {{ cwd?: string, token?: vscode.CancellationToken, onOutput: (text: string) => void }} options
 * @returns {Promise<number | null>}
 */
function stream(args, options) {
  return new Promise((resolve, reject) => {
    const child = cp.spawn(binary(), args, { cwd: options.cwd, env: process.env });
    child.stdout.setEncoding("utf8").on("data", options.onOutput);
    child.stderr.setEncoding("utf8").on("data", options.onOutput);
    child.on("error", (error) => reject(notFound(error)));
    child.on("close", resolve);
    options.token?.onCancellationRequested(() => child.kill());
  });
}

/** @param {Error & { code?: string }} error */
function notFound(error) {
  return error.code === "ENOENT"
    ? new Error(`cannot run '${binary()}': install sable, or set 'sable.path' to it`)
    : error;
}

/** The files a package's manifest may be in: Sable first (docs/05 §5.2), then the canonical TOML. */
const MANIFESTS = ["package.sb", "sable.toml"];

/** A glob for every package manifest in the workspace. */
const MANIFEST_GLOB = "**/{package.sb,sable.toml}";

/**
 * The package manifest in a directory, if it has one.
 * @param {string} directory
 * @returns {string | undefined}
 */
function manifestIn(directory) {
  for (const name of MANIFESTS) {
    const manifest = path.join(directory, name);
    if (fs.existsSync(manifest) && isPackage(manifest)) {
      return manifest;
    }
  }

  return undefined;
}

/**
 * The directory of the nearest package manifest, at or above the file,
 * inside the workspace; undefined for a loose file.
 * @param {vscode.Uri} uri
 * @returns {string | undefined}
 */
function packageRoot(uri) {
  const folder = vscode.workspace.getWorkspaceFolder(uri)?.uri.fsPath;
  let directory = path.dirname(uri.fsPath);
  for (;;) {
    if (manifestIn(directory)) {
      return directory;
    }

    const parent = path.dirname(directory);
    if (parent === directory || (folder !== undefined && !parent.startsWith(folder))) {
      return undefined;
    }

    directory = parent;
  }
}

/**
 * Whether a manifest describes a package: every package.sb does; a
 * sable.toml when it has a [package] table (a workspace root's may not).
 * @param {string} manifest
 */
function isPackage(manifest) {
  if (path.basename(manifest) === "package.sb") {
    return true;
  }

  try {
    return /^\s*\[package\]\s*$/m.test(fs.readFileSync(manifest, "utf8"));
  } catch {
    return false;
  }
}

/**
 * The package's name, from its manifest: what `sable build` calls its binary.
 * @param {string} root
 * @returns {string | undefined}
 */
function packageName(root) {
  const manifest = manifestIn(root);
  if (!manifest) {
    return undefined;
  }

  try {
    const text = fs.readFileSync(manifest, "utf8");
    if (path.basename(manifest) === "package.sb") {
      // `name: "..."` in the Package { } record.
      return text.match(/Package\s*\{[\s\S]*?\bname\s*:\s*"([^"]+)"/)?.[1]?.split("/").pop();
    }

    const table = text.split(/^\s*\[/m).find((section) => section.startsWith("package]"));
    return table?.match(/^\s*name\s*=\s*"([^"]+)"/m)?.[1]?.split("/").pop();
  } catch {
    return undefined;
  }
}

module.exports = { binary, channel, run, stream, packageRoot, packageName, isPackage, manifestIn, MANIFEST_GLOB };
