// @ts-check
// Writes Sable.tmLanguage, the VS Code grammar as the property list Sublime
// Text reads (it does not read the JSON form): one grammar, two editors.
// Run it after changing editors/vscode/syntaxes/sable.tmLanguage.json;
// editors/test fails while the two differ.
"use strict";

const fs = require("fs");
const path = require("path");

const source = path.join(__dirname, "..", "vscode", "syntaxes", "sable.tmLanguage.json");
const target = path.join(__dirname, "Sable.tmLanguage");

/** @param {string} text */
function escape(text) {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

/**
 * @param {unknown} value
 * @param {string} indent
 * @returns {string}
 */
function plist(value, indent) {
  const inner = indent + "\t";
  if (typeof value === "string") {
    return `${indent}<string>${escape(value)}</string>\n`;
  }

  if (typeof value === "boolean") {
    return `${indent}<${value}/>\n`;
  }

  if (typeof value === "number") {
    return `${indent}<integer>${value}</integer>\n`;
  }

  if (Array.isArray(value)) {
    return `${indent}<array>\n${value.map((v) => plist(v, inner)).join("")}${indent}</array>\n`;
  }

  if (value && typeof value === "object") {
    const entries = Object.entries(value).map(([key, v]) => `${inner}<key>${escape(key)}</key>\n${plist(v, inner)}`);
    return `${indent}<dict>\n${entries.join("")}${indent}</dict>\n`;
  }

  throw new Error(`cannot write ${JSON.stringify(value)} as a property list`);
}

/** The grammar as Sublime reads it: the JSON's, with the file types it is for and no schema. */
function generate() {
  const { $schema: _schema, ...grammar } = JSON.parse(fs.readFileSync(source, "utf8"));
  const withTypes = { name: grammar.name, scopeName: grammar.scopeName, fileTypes: ["sb"], firstLineMatch: "^#!.*\\bsable\\b", ...grammar };
  return [
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">',
    "<!-- Generated from editors/vscode/syntaxes/sable.tmLanguage.json by editors/sublime/generate.js; do not edit. -->",
    '<plist version="1.0">',
    plist(withTypes, "").trimEnd(),
    "</plist>",
    "",
  ].join("\n");
}

if (require.main === module) {
  fs.writeFileSync(target, generate());
  console.log(`wrote ${path.relative(process.cwd(), target)}`);
}

module.exports = { generate, target };
