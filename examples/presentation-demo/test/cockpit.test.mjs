import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { getScenario, snapshotsFromCatalog } from '../src/fixtures.mjs';
import { displayWidth, stripAnsi } from '../src/safety.mjs';
import { renderCockpit, renderEvidenceDetail, renderEvidenceList, renderStatusLine, renderUsagePanel } from '../src/render-cockpit.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const scenario = (name) => getScenario(name).snapshot;
const lines = (text) => (Array.isArray(text) ? text : text.split('\n'));
const ASCII_ONLY = /^[\x20-\x7e]*$/;
// frame-free, single-line text for phrase assertions that may wrap across framed rows
const flat = (text) => stripAnsi(Array.isArray(text) ? text.join('\n') : text).split('\n').map((line) => line.replace(/^[│|]\s?|\s?[│|]$/g, '').trim()).join(' ').replace(/\s+/g, ' ');

test('cockpit frame stays exactly within 80 and 120 columns for every fixture state, with and without colour', () => {
  for (const width of [80, 120]) for (const color of [false, true]) for (const { name, snapshot } of snapshotsFromCatalog()) {
    const out = lines(renderCockpit(snapshot, { width, color, presentation: 'harness' }));
    for (const line of out) assert.ok(displayWidth(line) <= width, `${name}@${width}: ${stripAnsi(line)}`);
    // framed rows are exactly the requested width so the box edges align
    const framed = out.filter((line) => /^[│|]/.test(stripAnsi(line)));
    assert.ok(framed.length > 10, `${name}: framed rows`);
    for (const line of framed) assert.equal(displayWidth(line), width, `${name}@${width}: ${stripAnsi(line)}`);
  }
});

test('ascii mode emits only printable ASCII and NO_COLOR suppresses ANSI', () => {
  for (const { name, snapshot } of snapshotsFromCatalog()) {
    for (const line of lines(renderCockpit(snapshot, { width: 80, ascii: true }))) assert.match(line, ASCII_ONLY, `${name}: ${line}`);
    for (const line of lines(renderCockpit(snapshot, { width: 80, ascii: true, color: true }))) assert.match(stripAnsi(line), ASCII_ONLY, `${name}: ${line}`);
    for (const line of renderStatusLine(snapshot, { width: 80, ascii: true })) assert.match(line, ASCII_ONLY, `${name}: ${line}`);
  }
  const previous = process.env.NO_COLOR; process.env.NO_COLOR = '1';
  try { assert.doesNotMatch(renderCockpit(scenario('failed-gate'), { width: 80, color: true }), /\x1b/); } finally { if (previous === undefined) delete process.env.NO_COLOR; else process.env.NO_COLOR = previous; }
});

test('colour is never the only signal: every gate row carries a glyph and an uppercase outcome word', () => {
  const plain = stripAnsi(renderCockpit(scenario('failed-gate'), { width: 80, color: true }));
  assert.match(plain, /✓ PASS\s+Build/); assert.match(plain, /◐ INCOMPLETE\s+Mutate4CSharp/); assert.match(plain, /– N\/A\s+Migration/);
  assert.match(plain, /blocker MUTATION_ENUMERATION_INCOMPLETE/);
  assert.match(plain, /why INCOMPLETE/);
});

test('cockpit keeps honesty boundaries visible in every state', () => {
  for (const { name, snapshot } of snapshotsFromCatalog()) {
    const plain = flat(renderCockpit(snapshot, { width: 80, presentation: 'harness' }));
    assert.match(plain, /DEMO • SYNTHETIC/, name); assert.match(plain, /HARNESS RENDER/, name);
    assert.match(plain, /actual charge unknown/, name); assert.match(plain, /intent output is preview only/, name);
    assert.match(plain, new RegExp(`head ${snapshot.run.headCommit}`), `${name}: full head in identity footer`);
    assert.doesNotMatch(plain, /\b(deployed|delivered)\b/i, name);
  }
  const native = stripAnsi(renderCockpit(scenario('failed-gate'), { width: 80, presentation: 'native' }));
  assert.match(native, /NATIVE DEMO SURFACE/); assert.doesNotMatch(native, /HARNESS RENDER/);
});

test('stale, unknown-cleanup and head-drift states are marked in the frame, never green', () => {
  const drift = flat(renderCockpit(scenario('head-drift'), { width: 80 }));
  assert.match(drift, /PASS STALE\s+Build/); assert.match(drift, /REVIEW · INCOMPLETE/); assert.match(drift, /belongs to head c82e4a1, run head is d91f700/);
  const cancel = flat(renderCockpit(scenario('cancel-unknown'), { width: 80 }));
  assert.match(cancel, /cleanup unknown/); assert.match(cancel, /UNAVAILABLE/); assert.match(cancel, /no closure proof/);
  const stale = flat(renderCockpit(scenario('stale-reconnected'), { width: 80 }));
  assert.match(stale, /seq 12 · stale · unknown/); assert.match(stale, /Publish PR\s+UNAVAILABLE/);
  const receipt = flat(renderCockpit(scenario('pr-receipt'), { width: 80 }));
  assert.match(receipt, /merge NOT authorized/); assert.match(receipt, /CI INCOMPLETE/); assert.match(receipt, /ceiling PR/);
});

test('launch and plan states show the frozen plan instead of an empty evidence wall', () => {
  const plan = flat(renderCockpit(scenario('plan'), { width: 80 }));
  assert.match(plan, /scope\s+Add CSV representation/); assert.match(plan, /exclude\s+No schema migration/); assert.match(plan, /preview is not approval/);
  assert.match(plan, /no receipt yet: build, tests, crap4csharp, mutation, migration/);
});

test('status line is at most four lines, carries identity, outcome, usage and next action, and fits 80 and 120', () => {
  for (const width of [80, 120]) for (const { name, snapshot } of snapshotsFromCatalog()) {
    const out = renderStatusLine(snapshot, { width, presentation: 'native', color: true });
    assert.ok(out.length <= 4, `${name}: ${out.length} lines`);
    for (const line of out) assert.ok(displayWidth(line) <= width, `${name}@${width}: ${stripAnsi(line)}`);
    const plain = out.map(stripAnsi).join('\n');
    assert.match(plain, /FORGE DEMO/); assert.match(plain, /NATIVE DEMO SURFACE/); assert.match(plain, /USD API-EQUIVALENT ESTIMATE/); assert.match(plain, /Next:/);
    assert.match(plain, new RegExp(`${snapshot.workflow.phase} / (PASS|FAIL|INCOMPLETE|ERROR)`));
  }
  assert.match(renderStatusLine(scenario('cancel-unknown'), { width: 80 }).join('\n'), /Stop requested/);
  assert.match(renderStatusLine(scenario('launch'), { width: 80 }).join('\n'), /unavailable/);
});

test('evidence list marks the selected row with > and keeps stale visible even at 52 columns', () => {
  const list = renderEvidenceList(scenario('head-drift'), 1, { width: 52, color: true });
  for (const line of list) assert.ok(displayWidth(line) <= 52, stripAnsi(line));
  const plain = list.map(stripAnsi);
  assert.equal(plain.filter((x) => x.startsWith('>')).length, 1);
  assert.match(plain.find((x) => x.startsWith('>')), /PASS STALE\s+Tests/);
  assert.match(plain.join('\n'), /REVIEW PASS STALE/);
  assert.match(list[3], /\x1b\[7/, 'selected row is also inverse video when colour is on');
});

test('evidence detail shows the complete head and tree even when they must hard-wrap', () => {
  const gate = scenario('failed-gate').gates[3];
  for (const width of [48, 80]) {
    const detail = renderEvidenceDetail(scenario('failed-gate'), 3, { width });
    for (const line of detail) assert.ok(displayWidth(line) <= width, line);
    const joined = detail.map((x) => x.replace(/^\s{12,}/, '')).join('').replace(/\s+/g, '');
    assert.ok(joined.includes(gate.headCommit), `${width}: full head`); assert.ok(joined.includes(gate.treeId), `${width}: full tree`);
    assert.match(detail.join('\n'), /2 of 20 discovered mutants/);
  }
  assert.match(renderEvidenceDetail(scenario('failed-gate'), 4, { width: 80 }).join('\n'), /N\/A reason\s+No schema or data migration/);
});

test('usage panel shows per-model share of the known subtotal, unpriced rows and request attempts within width', () => {
  for (const width of [52, 80, 120]) {
    const panel = renderUsagePanel(scenario('failed-gate'), { width, color: width === 80 });
    for (const line of panel) assert.ok(displayWidth(line) <= width, `${width}: ${stripAnsi(line)}`);
    const plain = flat(panel);
    assert.match(plain, /0\.044225 known · PARTIAL · 19,300 tok · 1 unpriced/); assert.match(plain, /SYNTHETIC TOKENS \+ SYNTHETIC RATES/);
    assert.match(plain, /demo\.openai\.writer/); assert.match(plain, /72%/); assert.match(plain, /28%/); assert.match(plain, /UNPRICED/);
    assert.match(plain, /effective model and pricing SKU unresolved/); assert.match(plain, /retryC/);
    assert.match(plain, /actual charge unknown/);
  }
  assert.match(renderUsagePanel(scenario('launch'), { width: 80 }).join('\n'), /unavailable/);
});

test('malformed snapshots render a validation block and never a PASS banner', () => {
  for (const malformed of [null, {}, { schemaVersion: 'forge.presentation/0.1', mode: 'demo' }]) {
    const rendered = stripAnsi(renderCockpit(malformed, { width: 80 }));
    assert.match(rendered, /ERROR/); assert.match(rendered, /Validation/); assert.doesNotMatch(rendered, /· PASS\n/);
    for (const line of rendered.split('\n')) assert.ok(displayWidth(line) <= 80, line);
  }
  const malicious = structuredClone(scenario('launch')); malicious.focus.title = '<b>\u001b[31mBAD</b>\u0007';
  const rendered = renderCockpit(malicious, { width: 80, color: false });
  assert.doesNotMatch(rendered, /\x1b|\u0007/);
});

test('demo CLI exposes the UX-2 views and keeps legacy views reachable', () => {
  const script = path.join(root, 'scripts/demo.mjs');
  const run = (...args) => spawnSync(process.execPath, [script, ...args], { encoding: 'utf8', timeout: 5000, env: { ...process.env, NO_COLOR: '' } });
  const cockpit = run('failed-gate', '80'); assert.equal(cockpit.status, 0); assert.match(cockpit.stdout, /┌─ FORGE/); assert.match(cockpit.stdout, /HARNESS RENDER/);
  const colored = run('failed-gate', '80', '--color'); assert.match(colored.stdout, /\x1b\[/);
  const ascii = run('failed-gate', '80', '--ascii'); assert.doesNotMatch(ascii.stdout, /[^\x20-\x7e\n]/);
  assert.ok(run('failed-gate', '80', '--view', 'status').stdout.trimEnd().split('\n').length <= 4);
  assert.match(run('failed-gate', '80', '--view', 'usage-panel').stdout, /Share of known/);
  assert.match(run('head-drift', '60', '--view', 'evidence', '--select', '2').stdout, />.*Crap4CSharp/);
  assert.match(run('failed-gate', '80', '--view', 'expanded').stdout, /^HARNESS RENDER — native text contract/m);
  assert.notEqual(run('failed-gate', '80', '--view', 'bogus').status, 0);
});
