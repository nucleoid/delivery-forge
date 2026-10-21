import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = path.dirname(fileURLToPath(import.meta.url));
const oxlint = path.join(root, "node_modules", "oxlint", "bin", "oxlint");

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
  | "suppression-asi-statement-regex"
  | "violation-asi-statement-regex"
  | "suppression-for-await-of-regex"
  | "violation-for-await-of-regex"
  | "suppression-hashbang-backtick"
  | "violation-hashbang-backtick"
  | "bom-hashbang"
  | "suppression-eslint-accumulating-spread"
  | "violation-eslint-accumulating-spread"
  | "suppression-jsx-expression-container"
  | "violation-jsx-expression-container"
  | "suppression-less-than-regex"
  | "violation-less-than-regex"
  | "suppression-export-default-regex"
  | "violation-export-default-regex"
  | "suppression-extends-regex"
  | "violation-extends-regex"
  | "suppression-property-keyword-newline"
  | "violation-property-keyword-newline"
  | "suppression-prefix-update-regex"
  | "violation-prefix-update-regex"
  | "suppression-no-space-from"
  | "violation-no-space-from"
  | "suppression-multiline-jsx-backtick"
  | "violation-multiline-jsx-backtick"
  | "suppression-multiline-jsx-expression-backtick"
  | "violation-multiline-jsx-expression-backtick"
  | "suppression-same-line-jsx-expression-quote"
  | "violation-same-line-jsx-expression-quote"
  | "suppression-blanket-specific-enable"
  | "suppression-prefixed-alias"): {
  status: number | null;
  report: Report;
} {
  const result = spawnSync(
    process.execPath,
    [
      oxlint,
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
        fixture === "violation-jsx-expression-container" ||
        fixture === "suppression-multiline-jsx-backtick" ||
        fixture === "violation-multiline-jsx-backtick" ||
        fixture === "suppression-multiline-jsx-expression-backtick" ||
        fixture === "violation-multiline-jsx-expression-backtick" ||
        fixture === "suppression-same-line-jsx-expression-quote" ||
        fixture === "violation-same-line-jsx-expression-quote"
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
  ["suppression-asi-statement-regex", "violation-asi-statement-regex"],
  ["suppression-for-await-of-regex", "violation-for-await-of-regex"],
  ["suppression-hashbang-backtick", "violation-hashbang-backtick"],
  ["suppression-jsx-expression-container", "violation-jsx-expression-container"],
  ["suppression-less-than-regex", "violation-less-than-regex"],
  ["suppression-export-default-regex", "violation-export-default-regex"],
  ["suppression-extends-regex", "violation-extends-regex"],
  ["suppression-property-keyword-newline", "violation-property-keyword-newline"],
  ["suppression-prefix-update-regex", "violation-prefix-update-regex"],
  ["suppression-no-space-from", "violation-no-space-from"],
  ["suppression-multiline-jsx-backtick", "violation-multiline-jsx-backtick"],
  ["suppression-multiline-jsx-expression-backtick", "violation-multiline-jsx-expression-backtick"],
  ["suppression-same-line-jsx-expression-quote", "violation-same-line-jsx-expression-quote"],
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

const accumulatingSpreadViolation = run("violation-eslint-accumulating-spread");
assert.equal(accumulatingSpreadViolation.status, 1);
assert.ok(accumulatingSpreadViolation.report.diagnostics.some(
  (diagnostic) => diagnostic.code === "oxc(no-accumulating-spread)",
), "matched control must prove the configured oxc/no-accumulating-spread rule executes");

const accumulatingSpreadAlias = run("suppression-eslint-accumulating-spread");
assert.equal(accumulatingSpreadAlias.status, 0);
assert.ok(accumulatingSpreadAlias.report.diagnostics.every(
  (diagnostic) => diagnostic.code !== "oxc(no-accumulating-spread)",
), "pinned Oxlint must honor eslint/no-accumulating-spread as an alias for the configured oxc rule");

const bomHashbang = run("bom-hashbang");
assert.equal(bomHashbang.status, 1);
assert.equal(bomHashbang.report.number_of_files, 1);
assert.equal(bomHashbang.report.number_of_rules, 19);
assert.ok(bomHashbang.report.diagnostics.some(
  (diagnostic) => diagnostic.code === undefined,
), "pinned Oxlint 1.78.0 must expose BOM+hashbang as a parser diagnostic, which normalizes to INCOMPLETE");

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

console.log("anti-slop fixture integration: clean PASS, violation FAIL, malformed INCOMPLETE, suppression attacks plus matched regex/division ambiguity controls verified");
