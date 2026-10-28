// UX-2 cockpit renderer. One projection, one layout grammar, four terminal views:
// framed cockpit (expanded), status line (3-4 lines), evidence list/detail panel, usage panel.
// Colour is optional and never the only signal: every outcome also has a glyph and an uppercase word.
// ASCII mode replaces every non-ASCII glyph and box character. All lines stay within the requested width.
import { cleanText, displayWidth, short, wrap } from './safety.mjs';
import { project } from './projector.mjs';

const PHASES = ['Launch', 'Plan', 'Execute', 'Evaluate', 'Repair', 'Review', 'PR'];
const RGB = { amber: [242, 186, 105], teal: [121, 215, 196], rose: [255, 156, 167], violet: [185, 174, 245], muted: [175, 190, 201], text: [232, 238, 242] };
const CODES = { bold: '1', dim: '2', inverse: '7' };
const GLYPHS = {
  unicode: { PASS: '✓', FAIL: '✗', INCOMPLETE: '◐', ERROR: '!', NOT_APPLICABLE: '–', UNKNOWN: '?', current: '▶', dot: '·', bar: '▇', rest: '░', ellipsis: '…', h: '─', v: '│', tl: '┌', tr: '┐', bl: '└', br: '┘', ml: '├', mr: '┤' },
  ascii: { PASS: '+', FAIL: 'x', INCOMPLETE: '~', ERROR: '!', NOT_APPLICABLE: '-', UNKNOWN: '?', current: '>', dot: '*', bar: '#', rest: '.', ellipsis: '~', h: '-', v: '|', tl: '+', tr: '+', bl: '+', br: '+', ml: '+', mr: '+' }
};
const OUTCOME_COLOR = { PASS: 'teal', NOT_APPLICABLE: 'muted', INCOMPLETE: 'amber', FAIL: 'rose', ERROR: 'rose' };
// ASCII mode also transliterates fixture punctuation 1:1 so widths and padding are unaffected
const ASCII_MAP = { '—': '-', '–': '-', '•': '*', '·': '*', '…': '~', '✓': '+', '✗': 'x', '◐': '~', '▶': '>', '→': '>', '←': '<', '\u00a0': ' ' };
const asciiize = (o) => (line) => o.ascii ? line.replace(/[^\x00-\x7f]/g, (ch) => ASCII_MAP[ch] || '?') : line;
const fmt = (n) => Number.isFinite(Number(n)) ? Number(n).toLocaleString('en-US') : 'unknown';

export function resolveOptions(options = {}) {
  const width = Math.max(40, Math.min(120, Number(options.width) || 80));
  const noColor = process.env.NO_COLOR === '1' || process.env.NO_COLOR === 'true';
  const ascii = options.ascii === true || (options.ascii !== false && noColor);
  const color = options.color === true && !noColor;
  return { width, ascii, color, presentation: options.presentation === 'harness' ? 'harness' : options.presentation === 'native' ? 'native' : null, glyph: GLYPHS[ascii ? 'ascii' : 'unicode'] };
}

export function provenance(presentation, surface = 'cockpit', dot = '·') {
  if (presentation === 'native') return `NATIVE DEMO SURFACE ${dot} host receipt is separate`;
  if (presentation === 'harness') return `HARNESS RENDER ${dot} not an installed host`;
  return `DEMO ${dot} SYNTHETIC ${dot} ${surface}`;
}

function kit(o) {
  const g = o.glyph;
  const W = o.width - 4; // inner width inside "│ " and " │"
  const paint = (text, ...names) => {
    if (!o.color || !names.length) return text;
    const codes = names.map((n) => CODES[n] ? CODES[n] : RGB[n] ? `38;2;${RGB[n].join(';')}` : null).filter(Boolean);
    return codes.length ? `\x1b[${codes.join(';')}m${text}\x1b[0m` : text;
  };
  const dot = ` ${g.dot} `;
  const cut = (text, max) => { if (displayWidth(text) <= max) return text; const t = cleanText(stripForWrap(text)); return [...t].slice(0, Math.max(1, max - 1)).join('') + g.ellipsis; };
  const pad = (text, width) => text + ' '.repeat(Math.max(0, width - displayWidth(text)));
  const clip = (text, max) => displayWidth(text) <= max ? text : cut(stripForWrap(text), max);
  const frame = (content = '') => `${g.v} ${pad(content, W)} ${g.v}`;
  const rule = (title) => {
    if (!title) return g.ml + g.h.repeat(o.width - 2) + g.mr;
    const t = cut(title, W - 4);
    return `${g.ml}${g.h} ${t} ${g.h.repeat(Math.max(1, o.width - displayWidth(t) - 5))}${g.mr}`;
  };
  const top = (title, tag) => {
    const t = cut(title, W - 2), tg = cut(tag, W - 2);
    const fill = o.width - displayWidth(t) - displayWidth(tg) - 8;
    if (fill >= 2) return [`${g.tl}${g.h} ${paint(t, 'bold')} ${g.h.repeat(fill)} ${paint(tg, 'amber', 'bold')} ${g.h}${g.tr}`];
    return [`${g.tl}${g.h} ${paint(t, 'bold')} ${g.h.repeat(Math.max(1, o.width - displayWidth(t) - 5))}${g.tr}`, frame(pad('', W - displayWidth(tg)) + paint(tg, 'amber', 'bold'))];
  };
  const bottom = () => g.bl + g.h.repeat(o.width - 2) + g.br;
  // left + right on one line when both fit, otherwise right goes on its own right-aligned line
  const fitRow = (left, right = '') => {
    const lw = displayWidth(left), rw = displayWidth(right);
    if (!rw) return wrapStyled(left).map(frame);
    if (lw + rw + 2 <= W) return [frame(left + ' '.repeat(W - lw - rw) + right)];
    return [...wrapStyled(left).map(frame), frame(' '.repeat(Math.max(0, W - rw)) + cut(right, W))];
  };
  // wrap plain text then apply one style to each line
  const wrapStyled = (text, ...names) => wrap(stripForWrap(text), W).map((line) => paint(line, ...names));
  const stripForWrap = (text) => String(text).replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '');
  // first column aligned table row with a note that moves to an indented continuation line when it does not fit
  const noteRow = (head, note, noteStyle = ['muted']) => {
    const hw = displayWidth(head);
    if (!note) return [frame(head)];
    if (hw + 2 + displayWidth(note) <= W) return [frame(`${head}  ${paint(note, ...noteStyle)}`)];
    return [frame(head), ...wrap(note, W - 4).map((line) => frame(`    ${paint(line, ...noteStyle)}`))];
  };
  return { g, W, paint, dot, cut, clip, pad, frame, rule, top, bottom, fitRow, wrapStyled, noteRow };
}

const outcomeGlyph = (g, outcome) => g[outcome] || g.UNKNOWN;
const outcomeWord = (outcome, currentness = 'current') => `${outcome === 'NOT_APPLICABLE' ? 'N/A' : outcome}${currentness === 'current' ? '' : ` ${currentness.toUpperCase()}`}`;
// drop trailing pieces until the line fits; the first piece always survives (cut if needed)
const fitPieces = (pieces, W, sep) => { let out = pieces.slice(); while (out.length > 1 && displayWidth(out.join(sep)) > W) out.pop(); return out.join(sep); };
// hard-wrap a value: words first, then any single token longer than the width is chunked so full IDs stay visible
const hardWrap = (text, width) => { const w = Math.max(8, width); const out = []; for (const line of wrap(text, w)) { if (displayWidth(line) <= w) out.push(line); else { const chars = [...line]; for (let i = 0; i < chars.length; i += w) out.push(chars.slice(i, i + w).join('')); } } return out; };
const freshOk = (s) => s.source.freshness === 'current' && s.source.continuity === 'complete';

function compressReasons(reasons) {
  const missing = [], rest = [];
  for (const reason of reasons) { const m = /^(.+): required policy gate is missing$/.exec(reason); m ? missing.push(m[1]) : rest.push(reason); }
  const out = [];
  if (missing.length) out.push(`no receipt yet: ${missing.join(', ')}`);
  return out.concat(rest);
}

function trailText(phase, g, paint) {
  const index = PHASES.findIndex((p) => p.toUpperCase() === phase);
  return PHASES.map((p, i) => i === index ? paint(`${g.current} ${p.toUpperCase()}`, 'amber', 'bold') : i < index ? paint(p, 'muted') : p).join('   ');
}

function usageSummaryText(usage, g) {
  if (!usage.available) return `${usage.label}  unavailable ${g.dot} no admitted observations yet ${g.dot} actual charge unknown`;
  const cost = usage.knownCostUsd === null ? 'no priced subtotal' : `${usage.knownCostUsd} known`;
  return `${usage.label}  ${cost} ${g.dot} ${usage.status} ${g.dot} ${fmt(usage.observedTokens)} tok ${g.dot} ${usage.unpricedRequests} unpriced`;
}

// one aligned row per model: name, known USD, share-of-known bar, tokens, then actors when the width allows
function modelRows(usage, g, paint, W, cut) {
  const priced = usage.models.filter((m) => m.knownCostNanoUsd > 0), total = priced.reduce((a, m) => a + m.knownCostNanoUsd, 0);
  const barMax = Math.max(6, Math.min(18, W - 58));
  return usage.models.map((m) => {
    const name = cut(m.model.replace(/^demo\.(openai|anthropic)\./, ''), 12).padEnd(12), tokens = `${fmt(m.inputTotal + m.outputTotal)} tok`.padStart(10);
    const actors = W >= 96 ? paint(`  ${cut(m.actors.join(', '), W - 60 - barMax)}`, 'muted') : '';
    if (m.knownCostNanoUsd > 0 && total) {
      const share = m.knownCostNanoUsd / total, len = Math.max(1, Math.round(share * barMax));
      return `${paint(name, 'bold')} ${m.knownCostUsd.padStart(9)}  ${paint(g.bar.repeat(len), 'violet')}${paint(g.rest.repeat(barMax - len), 'muted')} ${String(Math.round(share * 100)).padStart(3)}% ${paint(tokens, 'muted')}${actors}`;
    }
    return `${paint(name, 'bold')} ${paint('UNPRICED'.padStart(9), 'amber')}  ${paint(cut(`${g.UNKNOWN} ${m.unpricedRequests} unpriced request${m.unpricedRequests === 1 ? '' : 's'}`, barMax + 5).padEnd(barMax + 5), 'amber')} ${paint(tokens, 'muted')}${actors}`;
  });
}

export function renderCockpit(input, options = {}) {
  const o = resolveOptions(options), s = project(input), k = kit(o), { g, W, paint, dot, frame, rule, fitRow, wrapStyled, noteRow, cut } = k;
  const agg = s.aggregate.outcome, aggColor = OUTCOME_COLOR[agg] || 'amber';
  const L = [];
  const wide = o.width >= 100;
  L.push(...k.top(`FORGE${dot}${s.run.id}${dot}${wide ? 'plan ' : ''}r${s.run.planRevision}${dot}${wide ? 'head ' : ''}${short(s.run.headCommit)}${dot}ceiling ${s.run.authorizationCeiling.toUpperCase()}`, o.ascii ? s.demoLabel.replace(/[^ -~]/g, '*') : s.demoLabel));
  const sourceText = `seq ${s.source.sequence}${dot}${s.source.freshness}${dot}${s.source.continuity}`;
  L.push(...fitRow(paint(provenance(o.presentation, 'cockpit', g.dot), 'muted'), paint(sourceText, freshOk(s) ? 'muted' : 'amber', 'bold')));
  L.push(frame(trailText(s.workflow.phase, g, paint)));
  L.push(rule());
  L.push(...fitRow(paint(`${outcomeGlyph(g, agg)} ${s.workflow.phase} ${g.dot} ${agg}`, aggColor, 'bold'), s.focus.blockerCode ? paint(`blocker ${s.focus.blockerCode}`, 'muted') : ''));
  L.push(...wrapStyled(s.focus.title, 'bold').map(frame));
  L.push(...wrapStyled(s.focus.explanation, 'muted').map(frame));
  if (!s.validation.valid) { L.push(rule('Validation')); for (const err of s.validation.errors.slice(0, 6)) L.push(...wrapStyled(`${g.ERROR} ${err}`, 'rose').map(frame)); }

  if (['LAUNCH', 'PLAN'].includes(s.workflow.phase) && s.plan && Array.isArray(s.plan.scope)) {
    L.push(rule(`Plan r${s.run.planRevision}${dot}${s.run.planIdentity}`));
    for (const [label, items] of [['scope', s.plan.scope], ['accept', s.plan.acceptance], ['exclude', s.plan.exclusions]]) if (Array.isArray(items) && items.length) {
      const text = items.map((x) => cleanText(x)).join(dot);
      L.push(...wrap(text, W - 9).map((line, i) => frame(`${paint((i ? '' : label).padEnd(7), 'violet')}  ${line}`)));
    }
    L.push(...wrapStyled(`approval preview binds plan, base ${short(s.run.baseCommit)} and capability ${g.dot} preview is not approval`, 'muted').map(frame));
  }

  L.push(rule(`Evidence${dot}policy ${s.run.policyIdentity}`));
  if (!s.gates.length) L.push(...wrapStyled(`No gate receipts in this fixture state. ${s.requirements.gateIds.length} required gates and independent review are still due; aggregate cannot infer green.`, 'muted').map(frame));
  const labelWidth = Math.min(14, Math.max(5, ...s.gates.map((x) => displayWidth(x.label))));
  for (const gate of s.gates) {
    const stale = gate.currentness !== 'current';
    const color = stale ? 'amber' : (OUTCOME_COLOR[gate.outcome] || 'amber');
    const head = `${paint(outcomeGlyph(g, gate.outcome), color, 'bold')} ${paint(cut(outcomeWord(gate.outcome, gate.currentness), 11).padEnd(11), color, 'bold')} ${paint(cut(gate.label, labelWidth).padEnd(labelWidth), 'text', 'bold')}  ${paint(cut(gate.receiptId || 'MISSING RECEIPT', Math.max(12, W - labelWidth - 17)), gate.receiptId ? 'muted' : 'rose')}`;
    const notes = [];
    if (stale) notes.push(`receipt binds head ${short(gate.headCommit) || 'unknown'}, run head is ${short(s.run.headCommit)} ${g.dot} cannot aggregate PASS`);
    if (gate.outcome === 'NOT_APPLICABLE') notes.push(`N/A allowed by policy ${g.dot} ${gate.notApplicableRationale || 'MISSING RATIONALE'}`);
    for (const lim of gate.limitations) notes.push(lim);
    L.push(...noteRow(head, notes.join(` ${g.dot} `), [stale ? 'amber' : gate.outcome === 'PASS' || gate.outcome === 'NOT_APPLICABLE' ? 'muted' : color]));
  }
  if (s.review) {
    const sameHead = s.review.headCommit === s.run.headCommit;
    const color = sameHead && s.review.independence === 'accepted' ? OUTCOME_COLOR[s.review.outcome] || 'amber' : 'amber';
    const head = `${paint(outcomeGlyph(g, s.review.outcome), color, 'bold')} ${paint(`${s.review.outcome}`.padEnd(11), color, 'bold')} ${paint('Review'.padEnd(labelWidth), 'violet', 'bold')}  ${paint(cut(`independence ${s.review.independence}${dot}${cleanText(s.review.reviewerFamily || 'reviewer')} reviews ${cleanText(s.review.writerFamily || 'writer')}`, Math.max(12, W - labelWidth - 17)), 'muted')}`;
    L.push(...noteRow(head, sameHead ? `bound to head ${short(s.review.headCommit)} ${g.dot} ${cleanText(s.review.assessmentRef)}` : `belongs to head ${short(s.review.headCommit)}, run head is ${short(s.run.headCommit)} ${g.dot} re-review required`, [sameHead ? 'muted' : 'amber']));
  } else if (s.requirements.reviewRequired && s.gates.length) {
    L.push(...noteRow(`${paint(g.INCOMPLETE, 'amber', 'bold')} ${paint('INCOMPLETE'.padEnd(11), 'amber', 'bold')} ${paint('Review'.padEnd(labelWidth), 'violet', 'bold')}`, 'required independent review has not been issued for this head', ['muted']));
  }
  if (s.attempts.length) L.push(...wrap(`attempts  ${s.attempts.map((a) => `${a.id} ${a.status} @ ${short(a.headCommit)}${a.retained ? '' : ' (not retained)'}`).join(dot)}`, W).map((x) => frame(paint(x, 'muted'))));
  if (agg !== 'PASS' && s.aggregate.reasons.length) L.push(...wrap(`why ${agg}  ${compressReasons(s.aggregate.reasons).slice(0, 6).join('; ')}`, W).map((x) => frame(paint(x, aggColor))));

  if (s.publication && s.publication.requestedAction) {
    const pub = s.publication;
    L.push(rule('Publication'));
    L.push(...wrap(`${cleanText(pub.requestedAction)} ${g.dot} remote effect ${cleanText(pub.observedEffect)} ${g.dot} readback ${pub.readbackRef ? 'confirmed' : 'missing'} ${g.dot} CI ${cleanText(pub.ciOutcome)} ${g.dot} merge ${pub.mergeAuthorized ? 'AUTHORIZED' : 'NOT authorized'} ${g.dot} ${pub.remoteUrl ? cleanText(pub.remoteUrl) : 'no remote URL in fixture'}`, W).map((x) => frame(paint(x, 'violet'))));
    if (pub.receiptRef) L.push(...wrapStyled(`receipt ${cleanText(pub.receiptRef)}${pub.readbackRef ? `${dot}readback ${cleanText(pub.readbackRef)}` : ''}`, 'muted').map(frame));
  }

  L.push(rule('Control'));
  const mismatch = s.workflow.desiredState !== s.workflow.observedState, cleanupUnknown = s.workflow.cleanup === 'unknown';
  const controlText = `desired ${s.workflow.desiredState}${dot}observed ${s.workflow.observedState}${dot}cleanup ${s.workflow.cleanup}${dot}owned ops ${s.workflow.activeOperationCount ?? 'UNKNOWN'}`;
  L.push(...wrapStyled(controlText, cleanupUnknown ? 'rose' : mismatch ? 'amber' : 'text').map(frame));
  if (s.workflow.quiescenceEvidenceRef) L.push(...wrapStyled(`${g.PASS} quiescence proof ${s.workflow.quiescenceEvidenceRef}`, 'teal').map(frame));
  else if (mismatch || cleanupUnknown) L.push(...wrapStyled(cleanupUnknown ? `${g.ERROR} no closure proof; resume, repair and publication stay blocked until reconciliation` : `${g.INCOMPLETE} desired and observed differ; host idle is not Forge quiescence`, cleanupUnknown ? 'rose' : 'amber').map(frame));

  L.push(rule('Next'));
  const primary = s.actions.find((a) => a.id === s.focus.nextActionId) || s.actions[0];
  if (!primary) L.push(...wrapStyled(`No action admissible ${g.dot} ${s.focus.nextActionReason}`, 'amber').map(frame));
  for (const action of s.actions) {
    const isPrimary = action === primary, intent = action.kind === 'intent';
    const marker = isPrimary ? paint(g.current, 'amber', 'bold') : ' ';
    const label = paint(cut(action.label, Math.max(24, Math.min(48, W - 28))), isPrimary ? 'bold' : 'muted');
    const avail = action.available ? paint('AVAILABLE', isPrimary ? 'teal' : 'muted') : paint('UNAVAILABLE', 'rose');
    const head = `${marker} ${label}  ${avail}${intent ? paint(`  intent ${g.dot} preview only`, 'violet') : ''}`;
    L.push(...noteRow(head, action.reason, [action.available ? 'muted' : 'rose']));
  }

  L.push(rule(`Usage${dot}synthetic tokens + synthetic rates`));
  const u = s.usage;
  if (!u.available) {
    L.push(...fitRow(paint(`${u.label}  unavailable`, 'bold'), paint('UNAVAILABLE', 'muted', 'bold')));
    L.push(...wrapStyled(`no admitted observations yet${dot}actual charge unknown`, 'muted').map(frame));
  } else {
    L.push(...fitRow(paint(`${u.label}  ${u.knownCostUsd === null ? 'no priced subtotal' : `USD ${u.knownCostUsd} known`}`, 'bold'), paint(u.status, u.status === 'COMPLETE' ? 'teal' : 'amber', 'bold')));
    L.push(...wrapStyled(`${fmt(u.observedTokens)} tokens${dot}${u.finalRequests} final${dot}${u.provisionalRequests} provisional${dot}${u.unpricedRequests} unpriced${dot}actual charge unknown`, 'muted').map(frame));
    L.push(...modelRows(u, g, paint, W, cut).map(frame));
    for (const condition of u.conditions.slice(0, 2)) L.push(...wrapStyled(`${g.dot} ${condition}`, 'muted').map(frame));
  }

  L.push(rule('Integrations'));
  const capGlyph = (i) => i.observedCapability === 'supported' ? paint(g.PASS, 'teal') : i.observedCapability === 'unavailable' ? paint(g.FAIL, 'muted') : paint(g.UNKNOWN, 'amber');
  const oneLine = s.integrations.map((i) => `${capGlyph(i)} ${i.name}`).join('   ');
  if (displayWidth(oneLine) <= W) {
    L.push(frame(oneLine));
    const counts = ['supported', 'unavailable', 'unverified'].map((c) => [c, s.integrations.filter((i) => i.observedCapability === c).length]).filter(([, n]) => n).map(([c, n]) => `${n} ${c}`).join(dot);
    L.push(...wrapStyled(`${counts || 'none declared'}${dot}no integration call is made by the demo`, 'muted').map(frame));
  } else for (const i of s.integrations) L.push(frame(`${capGlyph(i)} ${cut(i.name, 24).padEnd(24)} ${paint(cut(`${i.observedCapability} ${g.dot} ${i.version} ${g.dot} ${i.limitations.join('; ')}`, W - 27), 'muted')}`));
  const unmeasured = s.measurements.filter((m) => m.value === null).map((m) => m.label === 'Cost' ? 'actual charge' : m.label.toLowerCase());
  const measured = s.measurements.filter((m) => m.value !== null).map((m) => `${m.label} ${m.value} ${m.unit}`);
  L.push(...wrapStyled(`${measured.length ? `${measured.join(dot)}${dot}` : ''}not measured: ${unmeasured.join(', ') || 'none'}`, 'muted').map(frame));

  L.push(rule());
  L.push(...fitRow(paint(cleanText(options.hints || `detail: evidence ${g.dot} usage ${g.dot} plan${dot}replay: next ${g.dot} prev ${g.dot} reset`), 'muted'), paint(`zero live adapters ${g.dot} intent output is preview only`, 'muted')));
  for (const line of [`run ${s.run.id}${dot}plan ${s.run.planIdentity}${dot}policy ${s.run.policyIdentity}`, `capability ${s.source.capabilityIdentity}${dot}snapshot ${s.source.snapshotId}`, `head ${s.run.headCommit}`, `tree ${s.run.treeId}`]) L.push(...wrapStyled(line, 'dim').map(frame));
  L.push(k.bottom());
  return L.map(asciiize(o)).join('\n');
}

export function renderStatusLine(input, options = {}) {
  const o = resolveOptions(options), s = project(input), { g, paint, dot, cut, pad } = kit({ ...o, width: o.width + 4 });
  const W = o.width, agg = s.aggregate.outcome, color = OUTCOME_COLOR[agg] || 'amber';
  const left = `FORGE DEMO${dot}${s.run.id}${dot}r${s.run.planRevision}${dot}${short(s.run.headCommit)}${dot}ceiling ${s.run.authorizationCeiling.toUpperCase()}`;
  const tag = o.presentation === 'harness' ? 'HARNESS RENDER' : o.presentation === 'native' ? 'NATIVE DEMO SURFACE' : 'DEMO';
  const right = tag;
  const lines = [];
  if (displayWidth(left) + displayWidth(right) + 2 <= W) lines.push(paint(left, 'bold') + ' '.repeat(W - displayWidth(left) - displayWidth(right)) + paint(right, 'amber'));
  else lines.push(paint(cut(left, W), 'bold'), paint(right, 'amber'));
  const freshness = freshOk(s) ? '' : `${dot}${s.source.freshness}/${s.source.continuity}`;
  lines.push(paint(cut(`${outcomeGlyph(g, agg)} ${s.workflow.phase} / ${agg}${freshness}${dot}${s.focus.title}`, W), color, 'bold'));
  const u = s.usage;
  const pricedCount = u.available ? u.models.filter((m) => m.knownCostNanoUsd > 0).length : 0;
  const models = u.available ? `${pricedCount} model${pricedCount === 1 ? '' : 's'}${u.unpricedRequests ? ` + ${u.unpricedRequests} unpriced` : ''}` : '';
  lines.push(paint(cut(u.available ? `${u.label} ~${u.knownCostUsd === null ? 'unknown' : Number(u.knownCostUsd).toFixed(4)}${dot}${u.status}${dot}${models}` : `${u.label} unavailable${dot}synthetic tokens/rates`, W), 'muted'));
  const primary = s.actions.find((a) => a.id === s.focus.nextActionId) || s.actions[0];
  const next = primary ? `${primary.label}${primary.available ? '' : ` (UNAVAILABLE: ${primary.reason})`}` : s.focus.nextActionReason;
  lines.push(`${paint(g.current, 'amber', 'bold')} ${cut(`Next: ${next}${dot}detail /forge-demo:status`, W - 2)}`);
  return lines.map(asciiize(o));
}

export function renderEvidenceList(input, selected = 0, options = {}) {
  const o = resolveOptions(options), s = project(input), { g, paint, dot, cut } = kit({ ...o, width: o.width + 4 });
  const W = o.width, agg = s.aggregate.outcome;
  const lines = [paint(provenance(o.presentation, 'evidence panel', g.dot), 'muted'), paint(fitPieces([`Evidence ${g.dot} ${agg}`, s.run.id, `head ${short(s.run.headCommit)}`, `seq ${s.source.sequence}`], W, dot), OUTCOME_COLOR[agg] || 'amber', 'bold')];
  if (!s.gates.length) lines.push(cut(`  No receipts. ${s.requirements.gateIds.length} required gates are INCOMPLETE.`, W));
  const labelWidth = Math.min(14, Math.max(5, ...s.gates.map((x) => displayWidth(x.label))));
  s.gates.forEach((gate, index) => {
    const stale = gate.currentness !== 'current', color = stale ? 'amber' : OUTCOME_COLOR[gate.outcome] || 'amber';
    const marker = index === selected ? '>' : ' ';
    const plain = cut(`${marker} ${outcomeGlyph(g, gate.outcome)} ${cut(outcomeWord(gate.outcome, gate.currentness), 11).padEnd(11)} ${cut(gate.label, labelWidth).padEnd(labelWidth)}  ${gate.receiptId || 'MISSING'}`, W);
    lines.push(index === selected ? paint(plain + ' '.repeat(Math.max(0, W - displayWidth(plain))), 'inverse', 'bold') : paint(plain, color));
  });
  if (s.review) { const same = s.review.headCommit === s.run.headCommit; lines.push(paint(cut(`  ${outcomeGlyph(g, s.review.outcome)} ${same ? `REVIEW ${s.review.outcome}` : `REVIEW ${s.review.outcome} STALE`}${dot}head ${short(s.review.headCommit)}${same ? '' : ` not ${short(s.run.headCommit)}`}${dot}independence ${s.review.independence}`, W), same ? 'violet' : 'amber')); }
  return lines.map(asciiize(o));
}

export function renderEvidenceDetail(input, selected = 0, options = {}) {
  const o = resolveOptions(options), s = project(input), gate = s.gates[selected], { g, paint, cut, clip } = kit({ ...o, width: o.width + 4 });
  const W = o.width;
  if (!gate) return [paint(provenance(o.presentation, 'evidence detail', g.dot), 'muted'), 'No gate selected.'].map(asciiize(o));
  const stale = gate.currentness !== 'current', color = stale ? 'amber' : OUTCOME_COLOR[gate.outcome] || 'amber';
  const kv = (key, value, ...style) => hardWrap(cleanText(value), W - 14).map((line, i) => `${(i ? '' : key).padEnd(12)}  ${paint(line, ...style)}`);
  const lines = [paint(provenance(o.presentation, 'evidence detail', g.dot), 'muted'), paint(cut(`${outcomeGlyph(g, gate.outcome)} ${gate.label} ${g.dot} ${gate.outcome} ${g.dot} ${gate.currentness}`, W), color, 'bold')];
  lines.push(...kv('receipt', gate.receiptId || 'MISSING RECEIPT', gate.receiptId ? 'text' : 'rose'));
  lines.push(...kv('head', gate.headCommit || 'unknown', gate.headCommit === s.run.headCommit ? 'text' : 'amber'));
  if (gate.headCommit !== s.run.headCommit) lines.push(...kv('run head', s.run.headCommit, 'muted'));
  lines.push(...kv('tree', gate.treeId || 'unknown', 'muted'));
  lines.push(...kv('policy', gate.policyIdentity || 'unknown', 'muted'));
  lines.push(...kv('artifacts', gate.artifactRefs.join(', ') || 'none', 'muted'));
  lines.push(...kv('limitations', gate.limitations.join('; ') || 'none recorded', gate.limitations.length ? color : 'muted'));
  if (gate.outcome === 'NOT_APPLICABLE') lines.push(...kv('N/A reason', gate.notApplicableRationale || 'MISSING', gate.notApplicableRationale ? 'text' : 'rose'));
  return lines.map((line) => clip(line, W)).map(asciiize(o));
}

export function renderUsagePanel(input, options = {}) {
  const o = resolveOptions(options), s = project(input), u = s.usage, { g, paint, dot, cut, clip } = kit({ ...o, width: o.width + 4 });
  const W = o.width;
  const lines = [paint(provenance(o.presentation, 'usage panel', g.dot), 'muted'), paint(cut(`FORGE ${g.dot} ${s.run.id} ${g.dot} ${s.workflow.phase} ${g.dot} workflow ${s.aggregate.outcome}`, W), 'bold'), paint(cut(`DEMO ${g.dot} SYNTHETIC TOKENS + SYNTHETIC RATES`, W), 'amber'), ''];
  lines.push(...wrap(usageSummaryText(u, g), W).map((x) => paint(x, 'bold')));
  lines.push(...wrap(u.legend, W).map((x) => paint(x, 'muted')));
  lines.push(...wrap(`final ${u.finalRequests}${dot}provisional ${u.provisionalRequests}${dot}freshness ${u.freshness}${dot}actual charge unknown`, W).map((x) => paint(x, 'muted')));
  if (!u.available) return lines.map(asciiize(o));
  const wide = W >= 100, narrow = W < 72;
  const priced = u.models.filter((m) => m.knownCostNanoUsd > 0), total = priced.reduce((a, m) => a + m.knownCostNanoUsd, 0);
  lines.push('', paint(narrow ? 'Share of known subtotal by model' : cut(`${'Model'.padEnd(24)} ${'Provider'.padEnd(16)} ${'Known USD'.padStart(10)} ${'Tokens'.padStart(8)}  Share of known`, W), 'muted'));
  for (const m of u.models) {
    const tokens = m.inputTotal + m.outputTotal, share = m.knownCostNanoUsd > 0 && total ? m.knownCostNanoUsd / total : null;
    const barMax = Math.max(6, Math.min(wide ? 24 : 12, narrow ? W - 30 : W - 66));
    const bar = share === null ? paint(`${g.UNKNOWN} ${m.unpricedRequests} unpriced`, 'amber') : `${paint(g.bar.repeat(Math.max(1, Math.round(share * barMax))), 'violet')}${paint(g.rest.repeat(barMax - Math.max(1, Math.round(share * barMax))), 'muted')} ${Math.round(share * 100)}%`;
    if (narrow) { lines.push(clip(`${paint(cut(m.model, W - 22), 'bold')}  ${m.knownCostUsd ? m.knownCostUsd : paint('UNPRICED', 'amber')}  ${fmt(tokens)} tok`, W), clip(`  ${bar}`, W)); }
    else lines.push(clip(`${paint(cut(m.model, 24).padEnd(24), 'bold')} ${cut(m.provider, 16).padEnd(16)} ${(m.knownCostUsd ? m.knownCostUsd : 'UNPRICED').padStart(10)} ${fmt(tokens).padStart(8)}  ${bar}`, W));
    lines.push(...wrap(`input ${fmt(m.inputTotal)} = ${fmt(m.inputOrdinary)} ordinary + ${fmt(m.cacheRead)} cache read + ${fmt(m.cacheWrite)} cache write (1h ${fmt(m.cacheWrite1h)})${dot}output ${fmt(m.outputTotal)} (reasoning ${m.reasoningUnknown ? `${fmt(m.reasoningKnown)} known + unknown` : fmt(m.reasoningKnown)})`, W - 2).map((x) => paint(`  ${x}`, 'muted')));
    lines.push(...wrap(`actors ${m.actors.join(', ')}${dot}requested ${m.requestedModels.join(', ')}${m.rateSnapshots.length ? `${dot}rate ${m.rateSnapshots.map((r) => `${r.id} ${r.tier} as-of ${r.asOf.slice(0, 10)}`).join(', ')}` : `${dot}rate UNRESOLVED`}`, W - 2).map((x) => paint(`  ${x}`, 'muted')));
  }
  if (u.requests.length) {
    lines.push('', ...wrap(`Requests / attempts  (${u.requests.length}; retries are new incurrences, revisions replace)`, W).map((x) => paint(x, 'muted')));
    for (const r of u.requests) {
      const cost = r.knownCostUsd === null ? paint('UNPRICED', 'amber') : `USD ${r.knownCostUsd}`;
      if (narrow) lines.push(clip(`  ${r.phase} ${r.actorRole}:${r.actorId} ${r.attemptId} r${r.revision}`, W), clip(`    ${r.stage}${dot}${cost}`, W));
      else lines.push(clip(`  ${r.phase.padEnd(8)} ${cut(`${r.actorRole}:${r.actorId}`, 26).padEnd(26)} ${cut(r.attemptId, 9).padEnd(9)} r${String(r.revision).padEnd(2)} ${r.stage.padEnd(15)} ${cost}`, W));
      if (r.knownCostUsd === null) lines.push(...wrap(`${g.UNKNOWN} ${r.unpricedReason}`, W - 6).map((x) => paint(`      ${x}`, 'amber')));
    }
  }
  for (const condition of u.conditions) lines.push(...wrap(`${g.dot} ${condition}`, W).map((x) => paint(x, 'muted')));
  return lines.map(asciiize(o));
}

export const COCKPIT_PHASES = PHASES;
