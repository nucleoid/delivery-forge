import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = path.dirname(fileURLToPath(import.meta.url));
const oxlint = path.join(
  root,
  "node_modules",
  ".bin",
  process.platform === "win32" ? "oxlint.cmd" : "oxlint",
);

type Diagnostic = { code?: string; severity?: string };
type Report = { diagnostics: Diagnostic[]; number_of_files: number };

function run(fixture: "clean" | "violation" | "malformed"): {
  status: number | null;
  report: Report;
} {
  const result = spawnSync(
    oxlint,
    [
      "--config",
      path.join(root, "oxlint.config.ts"),
      "--format",
      "json",
      path.join(root, "fixtures", `${fixture}.ts`),
    ],
    {
      cwd: root,
      encoding: "utf8",
      timeout: 30_000,
      windowsHide: true,
    },
  );

  assert.ifError(result.error);
  assert.equal(result.signal, null, `${fixture}: Oxlint was terminated by ${result.signal}`);
  assert.notEqual(result.stdout.trim(), "", `${fixture}: Oxlint emitted no JSON report`);
  return { status: result.status, report: JSON.parse(result.stdout) as Report };
}

const clean = run("clean");
assert.equal(clean.status, 0);
assert.equal(clean.report.number_of_files, 1);
assert.deepEqual(clean.report.diagnostics, []);

const violation = run("violation");
assert.equal(violation.status, 1);
assert.equal(violation.report.number_of_files, 1);
assert.ok(violation.report.diagnostics.length > 0);
assert.ok(violation.report.diagnostics.every((diagnostic) => diagnostic.code?.startsWith("anti-slop(")));

const malformed = run("malformed");
assert.equal(malformed.status, 1);
assert.equal(malformed.report.number_of_files, 1);
assert.ok(malformed.report.diagnostics.length > 0);
assert.ok(malformed.report.diagnostics.some((diagnostic) => !diagnostic.code));

console.log("anti-slop fixture integration: clean PASS, violation FAIL, malformed INCOMPLETE evidence");
