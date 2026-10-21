import { project } from './projector.mjs';
import { cleanText } from './safety.mjs';

export const COCKPIT_VIEWS = ['overview', 'evidence', 'usage', 'activity'];
export const glyph = (outcome, ascii = false) => ({ PASS: ascii ? '[OK]' : '✓', FAIL: ascii ? '[X]' : '×', ERROR: ascii ? '[X]' : '×', INCOMPLETE: ascii ? '[!]' : '!', NOT_APPLICABLE: ascii ? '[-]' : '–' }[outcome] || (ascii ? '[?]' : '?'));
const line = (label, value) => `${label}: ${cleanText(value ?? 'UNKNOWN')}`;

export function cockpitView(input, options = {}) {
  const s = project(input), selected = Math.max(0, Math.min(s.gates.length - 1, Number(options.selected) || 0));
  const source = `${s.source.realm.toUpperCase()} · snapshot ${s.source.sequence} · ${s.source.freshness.toUpperCase()} / ${s.source.continuity.toUpperCase()}`;
  const header = [`FORGE · ${s.run.id} · SYNTHETIC`, `${s.workflow.phase} · CEILING: ${s.run.authorizationCeiling.toUpperCase()}`, `${glyph(s.aggregate.outcome, options.ascii)} ${s.aggregate.outcome}${s.focus.blockerCode ? ` · ${s.focus.blockerCode}` : ''}`, `Next: ${s.focus.nextActionReason}`, source];
  const evidence = s.gates.map((g) => ({ id: g.id, label: g.label, outcome: g.outcome, currentness: g.currentness, receiptId: g.receiptId, search: `${g.id} ${g.label} ${g.outcome} ${g.currentness} ${g.receiptId}`.toLowerCase(), text: `${glyph(g.outcome, options.ascii)} ${g.outcome} · ${g.label} · ${g.currentness.toUpperCase()} · ${g.receiptId || 'MISSING RECEIPT'}` }));
  const gate = s.gates[selected];
  const detail = gate ? [
    `${glyph(gate.outcome, options.ascii)} ${gate.outcome} · ${gate.label}`,
    line('receipt', gate.receiptId || 'MISSING RECEIPT'), line('currentness', gate.currentness),
    line('observed head', gate.headCommit), line('expected head', s.run.headCommit), line('tree', gate.treeId), line('policy', gate.policyIdentity),
    line('artifacts', gate.artifactRefs.join(', ') || 'none'), line('limitations', gate.limitations.join('; ') || 'none recorded'),
    'Read-only. This receipt does not authorize retry or publication.'
  ] : ['No evidence selected.'];
  const overview = [
    line('plan', s.run.planIdentity), line('head', s.run.headCommit), line('tree', s.run.treeId), line('policy', s.run.policyIdentity),
    line('requirements', `${s.requirements.gateIds.join(', ')} · review ${s.requirements.reviewRequired ? 'required' : 'not required'} · ${s.requirements.policyIdentity}`),
    line('aggregate', s.aggregate.outcome), ...s.aggregate.reasons.map((reason) => line('reason', reason)), line('blocker', s.focus.blockerCode || 'none'), line('review', s.review ? `${s.review.outcome} / independence ${s.review.independence} / ${s.review.assessmentRef}` : 'NOT ISSUED'),
    line('publication', s.publication?.requestedAction ? `${s.publication.requestedAction} / ${s.publication.observedEffect}` : 'NOT REQUESTED'),
    line('cleanup', s.workflow.cleanup), ...s.attempts.map((attempt) => line('attempt', `${attempt.id} · ${attempt.status} · ${attempt.headCommit} · ${attempt.receiptId} · retained ${attempt.retained}`)),
    ...s.integrations.map((item) => line(item.name, `${item.observedCapability} · ${item.version}`)),
    'No execution controls · fixture inspection only'
  ];
  const u = s.usage;
  const valuation = u.provisionalRequests > 0 ? 'PROVISIONAL' : u.finalRequests > 0 ? 'FINAL' : 'UNAVAILABLE';
  const usage = [u.label, `~${u.knownCostUsd ?? 'UNKNOWN'} known subtotal · COVERAGE: ${u.status}`, `VALUATION: ${valuation} · synthetic rates · not a bill`];
  for (const m of u.models) usage.push(`${m.model} · input ${m.inputTotal} · output ${m.outputTotal} · cache read ${m.cacheRead ?? 'UNKNOWN'} / write ${m.cacheWrite ?? 'UNKNOWN'} · ${m.knownCostUsd ? `~USD ${m.knownCostUsd}` : 'UNPRICED'}`);
  usage.push(`Ledger identity unavailable · display-only fixture · run ${s.run.id}`, `Observed requests ${u.observedRequests} · priced ${u.pricedRequests} · unpriced ${u.unpricedRequests} · final ${u.finalRequests} · provisional ${u.provisionalRequests}`, `Reconciliation ${u.status === 'COMPLETE' ? 'complete' : 'pending'} · source ${u.freshness}`, u.legend);
  if (u.models.some((m) => m.rateSnapshots.length)) {
    usage.push('Pricing provenance');
    for (const m of u.models) for (const rate of m.rateSnapshots) usage.push(`${m.model} · ${rate.id} · as of ${rate.asOf} · ${rate.source}`);
  }
  if (u.requests.length) {
    usage.push('Request provenance');
    for (const request of u.requests) usage.push(`${request.incurrenceId} · attempt ${request.attemptId} · ${request.phase} · ${request.stage} r${request.revision} · ${request.effectiveModel || request.requestedModel} · ${request.rateSnapshotId || `UNPRICED: ${request.unpricedReason}`} · cache read ${request.cachePresence.cacheRead ? 'observed' : 'unknown'} / write ${request.cachePresence.cacheWrite ? 'observed' : 'unknown'} · ${request.mapping}`);
  }
  const history = Array.isArray(options.history) ? options.history : [];
  const activity = history.slice(-12).reverse().map((x, i) => { const p = project(x.snapshot ?? x); return `${i === 0 ? 'NEWEST · ' : ''}#${p.source.sequence} · ${p.source.observedAt} · fixture · ${p.workflow.phase} · ${p.aggregate.outcome}`; });
  if (!activity.length) activity.push(`#${s.source.sequence} · ${s.source.observedAt} · fixture · ${s.workflow.phase} · ${s.aggregate.outcome}`);
  return { snapshot: s, header, overview, evidence, detail, usage, activity, selected };
}

export function linesForView(input, view, options = {}) {
  const vm = cockpitView(input, options), name = COCKPIT_VIEWS.includes(view) ? view : 'overview';
  if (name === 'evidence') return options.detail ? vm.detail : vm.evidence.length ? vm.evidence.map((x, i) => `${i === vm.selected ? '>' : ' '} ${x.text}`) : ['No evidence receipts yet · aggregate remains INCOMPLETE'];
  return vm[name];
}
