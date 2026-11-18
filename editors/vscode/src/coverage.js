// @ts-check
// Coverage in the Test Explorer: `sable test --coverage` writes what the
// tests ran as LCOV (target/coverage/lcov.info, named on its summary line),
// which becomes VS Code's coverage of each file — its lines, and each
// outcome of each decision on them.
"use strict";

const fs = require("fs");
const path = require("path");

/**
 * VS Code's coverage API (1.88), which the types the extension builds
 * against (1.85) predate: reached through this, and used only when there.
 * @type {any}
 */
const vscode = require("vscode");

/** `coverage: 80% of lines (8 of 10), …; target/coverage/lcov.info`, the summary `sable test --coverage` prints. */
const SUMMARY = /^coverage: .*; (.+?)\s*$/m;

/**
 * One decision's outcome, as a `BRDA:line,block,branch,taken` record says:
 * the decision numbered among those of its line, the outcome among its
 * own, and how often it happened — undefined for a decision never reached.
 * @typedef {{ line: number, block: number, branch: number, taken: number | undefined }} LcovBranch
 */

/**
 * One file of an LCOV report: how often each line ran, and its branches.
 * @typedef {{ file: string, lines: Map<number, number>, branches: LcovBranch[] }} LcovRecord
 */

/**
 * The records of an LCOV report: `SF` starts one, `DA` and `BRDA` fill it,
 * `end_of_record` ends it; the totals (`LF`, `LH`, `BRF`, `BRH`) are
 * counted again from those, and anything else is passed over.
 * @param {string} text
 * @returns {LcovRecord[]}
 */
function parseLcov(text) {
  /** @type {LcovRecord[]} */
  const records = [];
  /** @type {LcovRecord | undefined} */
  let record;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    const colon = line.indexOf(":");
    const tag = colon < 0 ? line : line.slice(0, colon);
    const fields = colon < 0 ? [] : line.slice(colon + 1).split(",");
    switch (tag) {
      case "SF":
        record = { file: line.slice(colon + 1), lines: new Map(), branches: [] };
        records.push(record);
        break;
      case "DA": {
        const [number, count] = fields.map(Number);
        if (record && number > 0 && Number.isFinite(count)) {
          record.lines.set(number, (record.lines.get(number) ?? 0) + count);
        }

        break;
      }
      case "BRDA": {
        const [number, block, branch] = fields.slice(0, 3).map(Number);
        const taken = fields[3] === "-" ? undefined : Number(fields[3]);
        if (record && number > 0 && Number.isInteger(block) && Number.isInteger(branch)) {
          record.branches.push({ line: number, block, branch, taken });
        }

        break;
      }
      case "end_of_record":
        record = undefined;
        break;
    }
  }

  return records;
}

/**
 * The LCOV file a coverage run of the package at `root` wrote, as its
 * summary line names it (relative to where it ran), if it printed one: a
 * run whose build failed wrote none, and an older one may still be there.
 * @param {string} root
 * @param {string} output
 * @returns {string | undefined}
 */
function lcovPath(root, output) {
  const match = SUMMARY.exec(output);
  return match ? path.resolve(root, match[1]) : undefined;
}

/**
 * What VS Code shows of an outcome: which, of how many on the line.
 * @param {LcovBranch} branch
 * @param {LcovBranch[]} onLine
 */
function label(branch, onLine) {
  const decisions = new Set(onLine.map((b) => b.block)).size;
  const outcome = `outcome ${branch.branch + 1}`;
  return decisions > 1 ? `decision ${branch.block + 1}, ${outcome}` : outcome;
}

/**
 * A file's coverage as VS Code takes it: the counts it shows in the
 * Testing view, and the lines and branches it shows in the editor — a
 * statement per line that has code, each with the outcomes of the
 * decisions on it.
 * @param {LcovRecord} record
 * @param {string} root The directory the run was in, which a relative path is from.
 * @returns {{ coverage: any, details: any[] }}
 */
function toFileCoverage(record, root) {
  const uri = vscode.Uri.file(path.resolve(root, record.file));

  /** @type {Map<number, LcovBranch[]>} */
  const byLine = new Map();
  for (const branch of record.branches) {
    byLine.set(branch.line, [...(byLine.get(branch.line) ?? []), branch]);
  }

  const numbers = [...new Set([...record.lines.keys(), ...byLine.keys()])].sort((a, b) => a - b);
  const details = numbers.map((number) => {
    const onLine = byLine.get(number) ?? [];
    const at = new vscode.Position(number - 1, 0);
    const branches = onLine.map((b) => new vscode.BranchCoverage(b.taken ?? 0, at, label(b, onLine)));
    // A line with branches and no DA record ran when any of its outcomes happened.
    const count = record.lines.get(number) ?? (onLine.some((b) => (b.taken ?? 0) > 0) ? 1 : 0);
    return new vscode.StatementCoverage(count, at, branches);
  });

  const lines = [...record.lines.values()];
  const statements = new vscode.TestCoverageCount(lines.filter((c) => c > 0).length, lines.length);
  const branches = record.branches.length === 0
    ? undefined
    : new vscode.TestCoverageCount(record.branches.filter((b) => (b.taken ?? 0) > 0).length, record.branches.length);
  return { coverage: new vscode.FileCoverage(uri, statements, branches), details };
}

/** Each file's details, kept for VS Code to ask for when the file is opened. */
const detailed = new WeakMap();

/**
 * Whether this VS Code has the coverage API (1.88 and later).
 * @returns {boolean}
 */
function isSupported() {
  return typeof vscode.FileCoverage === "function" && vscode.TestRunProfileKind?.Coverage !== undefined;
}

/**
 * Reports to the run the coverage that the run of the package at `root`
 * wrote, as its output says where; nothing when it wrote none.
 * @param {any} run The test run.
 * @param {string} root
 * @param {string} output What `sable test --coverage` printed.
 * @returns {number} How many files were reported.
 */
function report(run, root, output) {
  const file = lcovPath(root, output);
  if (!file || !fs.existsSync(file)) {
    return 0;
  }

  const records = parseLcov(fs.readFileSync(file, "utf8"));
  for (const record of records) {
    const { coverage, details } = toFileCoverage(record, root);
    detailed.set(coverage, details);
    run.addCoverage(coverage);
  }

  return records.length;
}

/**
 * The lines and branches of a file reported, for `loadDetailedCoverage`.
 * @param {any} coverage
 * @returns {any[]}
 */
function details(coverage) {
  return detailed.get(coverage) ?? [];
}

module.exports = { parseLcov, lcovPath, toFileCoverage, report, details, isSupported };
