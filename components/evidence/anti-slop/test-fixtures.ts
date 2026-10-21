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
type Report = { diagnostics: Diagnostic[]; number_of_files: number; number_of_rules: number };

function run(fixture:
  | "clean"
  | "violation"
  | "malformed"
  | "suppression"
  | "suppression-line-division"
  | "suppression-reserved-method"
  | "suppression-adjacent-quote"
  | "suppression-line-comment-terminator"
  | "suppression-multiline-block"
  | "suppression-keyword-properties"
  | "suppression-jsx-guess"
  | "suppression-bare"
  | "suppression-unknown"
  | "suppression-jsx-slash"
  | "suppression-template-regex"
  | "suppression-template-regex-object"
  | "violation-template-regex-object"
  | "suppression-template-jsx-object"
  | "violation-template-jsx-object"
  | "suppression-statement-regex"
  | "violation-statement-regex"
  | "suppression-await-regex"
  | "violation-await-regex"
  | "suppression-for-of-regex"
  | "violation-for-of-regex"
  | "suppression-jsx-expression-container"
  | "violation-jsx-expression-container"
  | "suppression-blanket-specific-enable"
  | "suppression-prefixed-alias"): {
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
      "--no-ignore",
      "--disable-nested-config",
      path.join(root, "fixtures", `${fixture}.${
        fixture === "suppression-jsx-slash" ||
        fixture === "suppression-template-jsx-object" ||
        fixture === "violation-template-jsx-object" ||
        fixture === "suppression-jsx-expression-container" ||
        fixture === "violation-jsx-expression-container"
          ? "tsx"
          : "ts"
      }`),
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
assert.equal(clean.report.number_of_rules, 19);
assert.deepEqual(clean.report.diagnostics, []);

const violation = run("violation");
assert.equal(violation.status, 1);
assert.equal(violation.report.number_of_files, 1);
assert.equal(violation.report.number_of_rules, 19);
assert.ok(violation.report.diagnostics.length > 0);
assert.ok(violation.report.diagnostics.every((diagnostic) => diagnostic.code?.startsWith("anti-slop(")));

const malformed = run("malformed");
assert.equal(malformed.status, 1);
assert.equal(malformed.report.number_of_files, 1);
assert.equal(malformed.report.number_of_rules, 19);
assert.ok(malformed.report.diagnostics.length > 0);
assert.ok(malformed.report.diagnostics.some((diagnostic) => !diagnostic.code));

const suppression = run("suppression");
assert.equal(suppression.status, 0);
assert.equal(suppression.report.number_of_files, 1);
assert.equal(suppression.report.number_of_rules, 19);
assert.deepEqual(suppression.report.diagnostics, []);

const templateRegexViolation = run("violation-template-regex-object");
assert.equal(templateRegexViolation.status, 1);
assert.ok(templateRegexViolation.report.diagnostics.some(
  (diagnostic) => diagnostic.code === "anti-slop(no-object-parameters)",
));

const templateRegexExact = run("suppression-template-regex");
assert.equal(templateRegexExact.status, 1);
assert.ok(templateRegexExact.report.diagnostics.some(
  (diagnostic) => diagnostic.code === "anti-slop(require-readable-spacing)",
));
assert.ok(templateRegexExact.report.diagnostics.every(
  (diagnostic) => diagnostic.code !== "anti-slop(no-object-parameters)",
));

const templateRegexSuppressed = run("suppression-template-regex-object");
assert.equal(templateRegexSuppressed.status, 1);
assert.ok(templateRegexSuppressed.report.diagnostics.some(
  (diagnostic) => diagnostic.code === "anti-slop(require-readable-spacing)",
));
assert.ok(templateRegexSuppressed.report.diagnostics.every(
  (diagnostic) => diagnostic.code !== "anti-slop(no-object-parameters)",
));

const templateJsxViolation = run("violation-template-jsx-object");
assert.equal(templateJsxViolation.status, 1);
assert.ok(templateJsxViolation.report.diagnostics.some(
  (diagnostic) => diagnostic.code === "anti-slop(no-object-parameters)",
));

const templateJsxSuppressed = run("suppression-template-jsx-object");
assert.equal(templateJsxSuppressed.status, 0);
assert.deepEqual(templateJsxSuppressed.report.diagnostics, []);

for (const [suppressedName, violationName] of [
  ["suppression-statement-regex", "violation-statement-regex"],
  ["suppression-await-regex", "violation-await-regex"],
  ["suppression-for-of-regex", "violation-for-of-regex"],
  ["suppression-jsx-expression-container", "violation-jsx-expression-container"],
] as const) {
  const violationControl = run(violationName);
  assert.equal(violationControl.status, 1);
  assert.ok(violationControl.report.diagnostics.some(
    (diagnostic) => diagnostic.code === "anti-slop(no-chained-type-assertions)",
  ), `${violationName}: matched control must report the protected rule`);

  const suppressedAttack = run(suppressedName);
  assert.ok(suppressedAttack.report.diagnostics.every(
    (diagnostic) => diagnostic.code !== "anti-slop(no-chained-type-assertions)",
  ), `${suppressedName}: pinned Oxlint must honor the exact reviewer directive`);
}

for (const bypass of [
  "suppression-line-division",
  "suppression-reserved-method",
  "suppression-adjacent-quote",
  "suppression-line-comment-terminator",
  "suppression-multiline-block",
  "suppression-keyword-properties",
  "suppression-jsx-guess",
  "suppression-jsx-slash",
  "suppression-blanket-specific-enable",
  "suppression-prefixed-alias",
] as const) {
  const result = run(bypass);
  assert.equal(result.status, 0, `${bypass}: pinned Oxlint must demonstrate the suppression attack`);
  assert.equal(result.report.number_of_files, 1);
  assert.equal(result.report.number_of_rules, 19);
  assert.deepEqual(result.report.diagnostics, []);
}

for (const name of ["suppression-bare", "suppression-unknown"] as const) {
  const result = run(name);
  assert.equal(result.status, 0, `${name}: pinned Oxlint accepts this suppression name`);
  assert.deepEqual(result.report.diagnostics, []);
}

console.log("anti-slop fixture integration: clean PASS, violation FAIL, malformed INCOMPLETE, eleven clean suppression attacks plus matched template and exact statement-regex/await/for-of/JSX-container controls verified");
