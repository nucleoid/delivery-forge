import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const root = path.dirname(fileURLToPath(import.meta.url));
const runner = path.join(root, "run-oxlint-batches.mjs");
const config = path.join(root, "oxlint.config.ts");

function run(oxlint, inputs) {
  return spawnSync(process.execPath, [
    runner, "--oxlint", oxlint, "--config", config, "--inputs", inputs,
  ], {
    cwd: root,
    encoding: "utf8",
    timeout: 30_000,
    windowsHide: true,
  });
}

const clean = run(
  path.join(root, "node_modules", "oxlint", "bin", "oxlint"),
  path.join(root, "fixtures", "inputs.json"),
);
assert.ifError(clean.error);
assert.equal(clean.status, 0);
assert.deepEqual(JSON.parse(clean.stdout).diagnostics, []);

const temp = mkdtempSync(path.join(tmpdir(), "delivery-forge-batch-shape-"));
try {
  const fake = path.join(temp, "fake-oxlint.mjs");
  const manifest = path.join(temp, "inputs.json");
  writeFileSync(manifest, JSON.stringify(["fixture.ts"]));
  for (const report of [
    null,
    [],
    { diagnostics: [], number_of_files: "1", number_of_rules: 19 },
    { diagnostics: [null], number_of_files: 1, number_of_rules: 19 },
    { diagnostics: [7], number_of_files: 1, number_of_rules: 19 },
  ]) {
    writeFileSync(fake, `process.stdout.write(${JSON.stringify(JSON.stringify(report))}); process.exit(1);\n`);
    const result = run(fake, manifest);
    assert.ifError(result.error);
    assert.equal(result.status, 2, `malformed batch shape escaped: ${result.stdout}`);
    const normalized = JSON.parse(result.stdout);
    assert.equal(normalized.number_of_files, 0);
    assert.equal(normalized.number_of_rules, 0);
    assert.equal(normalized.diagnostics[0]?.code, "delivery-forge/tool-error");
  }
} finally {
  rmSync(temp, { recursive: true, force: true });
}

console.log("anti-slop batch integration: real pinned clean report and malformed count/diagnostic shapes verified");
