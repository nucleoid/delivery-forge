import { readFileSync, statSync } from "node:fs";
import { spawnSync } from "node:child_process";

const fail = (reason) => {
  process.stdout.write(JSON.stringify({
    diagnostics: [{ code: "delivery-forge/tool-error", filename: "<batch-runner>", category: "fatal", message: reason }],
    number_of_files: 0,
    number_of_rules: 0
  }));
  process.exit(2);
};

const args = process.argv.slice(2);
if (args.length !== 6 || args[0] !== "--oxlint" || args[2] !== "--config" || args[4] !== "--inputs")
  fail("invalid canonical batch-runner arguments");

let inputs;
try {
  if (statSync(args[5]).size > 8 * 1024 * 1024)
    fail("input manifest exceeds the 8 MiB ceiling");
  inputs = JSON.parse(readFileSync(args[5], "utf8"));
} catch (error) {
  fail(`input manifest unavailable: ${error.message}`);
}
if (!Array.isArray(inputs) || inputs.length > 100_000 ||
    inputs.some((value) => typeof value !== "string" || Buffer.byteLength(value) > 4096))
  fail("input manifest must be a JSON string array");

const prefix = ["--config", args[3], "--format", "json", "--no-ignore", "--disable-nested-config"];
const batches = [];
let batch = [];
let bytes = prefix.reduce((total, value) => total + Buffer.byteLength(value) + 1, 0);
for (const input of inputs) {
  const size = Buffer.byteLength(input) + 1;
  if (batch.length > 0 && bytes + size > 24_000) {
    batches.push(batch);
    batch = [];
    bytes = prefix.reduce((total, value) => total + Buffer.byteLength(value) + 1, 0);
  }
  batch.push(input);
  bytes += size;
}
if (batch.length > 0) batches.push(batch);
if (batches.length === 0) fail("zero inputs cannot be executed");
if (batches.length > 4096) fail("input manifest requires too many bounded Oxlint batches");

const diagnostics = [];
let numberOfFiles = 0;
let numberOfRules;
let exitCode = 0;
let diagnosticBytes = 0;
const deadline = Date.now() + 15 * 60_000;
for (const paths of batches) {
  const remaining = deadline - Date.now();
  if (remaining <= 0) fail("shared Oxlint batch deadline expired");
  const result = spawnSync(process.execPath, [args[1], ...prefix, ...paths], {
    cwd: process.cwd(),
    env: process.env,
    encoding: "utf8",
    maxBuffer: 1024 * 1024,
    timeout: remaining,
    killSignal: "SIGKILL",
    windowsHide: true
  });
  if (result.error) fail(`Oxlint batch failed to start or settle before the shared deadline: ${result.error.message}`);
  if (result.signal) fail(`Oxlint batch was terminated by ${result.signal}`);
  let report;
  try {
    report = JSON.parse(result.stdout);
  } catch {
    fail("Oxlint batch returned malformed JSON");
  }
  if (!Array.isArray(report.diagnostics) || !Number.isInteger(report.number_of_files) ||
      !Number.isInteger(report.number_of_rules))
    fail("Oxlint batch returned an unsupported report");
  if (numberOfRules !== undefined && numberOfRules !== report.number_of_rules)
    fail("Oxlint rule count changed between batches");
  const batchDiagnosticBytes = Buffer.byteLength(JSON.stringify(report.diagnostics));
  diagnosticBytes += batchDiagnosticBytes;
  if (report.diagnostics.length > 10_000 || diagnostics.length + report.diagnostics.length > 10_000 ||
      diagnosticBytes > 768 * 1024)
    fail("Oxlint diagnostics exceed the deterministic aggregate ceiling");
  numberOfRules = report.number_of_rules;
  diagnostics.push(...report.diagnostics);
  numberOfFiles += report.number_of_files;
  exitCode = Math.max(exitCode, result.status ?? 2);
}

process.stdout.write(JSON.stringify({
  diagnostics,
  number_of_files: numberOfFiles,
  number_of_rules: numberOfRules ?? 0
}));
process.exit(exitCode === 0 ? 0 : diagnostics.length > 0 && exitCode === 1 ? 1 : 2);
