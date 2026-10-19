import { boundLines, cleanText, short, wrap } from './safety.mjs';
import { project } from './projector.mjs';

const trail = ['Launch','Plan','Execute','Evaluate','Repair','Review','PR'];
const status = (gate) => `${gate.outcome}${gate.currentness === 'current' ? '' : ` / ${gate.currentness.toUpperCase()}`}`;
const provenance=(presentation='harness',detail='native text contract')=>presentation==='native'?'NATIVE DEMO SURFACE — installed-host rendering requires a separate receipt':`HARNESS RENDER — ${detail} (not an installed host screenshot)`;

export function renderCompact(input, options = {}) {
  const s = project(input), width = Math.max(40, Math.min(120, Number(options.width) || 80));
  const ascii = options.ascii || process.env.NO_COLOR === '1';
  const sep = ascii ? ' | ' : ' · ';
  const lines = [];
  if(options.presentation) lines.push(options.presentation==='native'?'NATIVE DEMO SURFACE | DEMO SYNTHETIC':'HARNESS RENDER | DEMO SYNTHETIC');
  lines.push(`FORGE DEMO${sep}${cleanText(s.run.id)}${sep}r${s.run.planRevision}${sep}${short(s.run.headCommit)}${sep}ceiling ${s.run.authorizationCeiling.toUpperCase()}`);
  lines.push(`${s.workflow.phase} / ${s.aggregate.outcome} — ${cleanText(s.focus.title)}`);
  lines.push(s.usage.available?`${s.usage.label} ~${Number(s.usage.knownCostUsd).toFixed(4)} | ${s.usage.models.length>1?'multiple models':s.usage.models[0]?.model||'unresolved'} | ${s.usage.status}`:`${s.usage.label} unavailable | SYNTHETIC TOKENS/RATES`);
  lines.push(`Next: ${cleanText(s.focus.nextActionReason)}`);
  return boundLines(lines, width);
}

export function renderExpanded(input, options = {}) {
  const s = project(input), width = Math.max(40, Math.min(120, Number(options.width) || 80));
  const ascii = options.ascii || process.env.NO_COLOR === '1', dot = ascii ? ' | ' : ' · ';
  const lines = [
    provenance(options.presentation,'native text contract'),
    'DEMO • SYNTHETIC — zero live adapters',
    `FORGE / ${s.run.id} / plan r${s.run.planRevision} / head ${short(s.run.headCommit)} / ceiling ${s.run.authorizationCeiling.toUpperCase()}`,
    `Source ${s.source.snapshotId}${dot}seq ${s.source.sequence}${dot}${s.source.freshness}/${s.source.continuity}${dot}${s.source.capabilityIdentity}`,
    `Phase: ${trail.map((p)=>p.toUpperCase()===s.workflow.phase?`[${p}]`:p).join(' -> ')}`,
    '',
    `${s.workflow.phase} / ${s.aggregate.outcome}`,
    ...wrap(s.focus.title, width),
    ...wrap(s.focus.explanation, width),
    ''
  ];
  lines.push('Evidence');
  if (!s.gates.length) lines.push('  No gate receipts in this fixture state. Aggregate cannot infer green.');
  for (const gate of s.gates) {
    lines.push(...wrap(`  ${gate.label.padEnd(16)} ${status(gate).padEnd(20)} ${gate.receiptId || 'MISSING RECEIPT'}`, width));
    if (gate.outcome === 'NOT_APPLICABLE') lines.push(...wrap(`    N/A rationale: ${gate.notApplicableRationale || 'MISSING'}`, width));
  }
  if (s.review) lines.push(...wrap(`  Review ${s.review.outcome} / independence ${s.review.independence} / ${s.review.assessmentRef}`, width));
  if ((s.attempts || []).length) {
    lines.push('  Attempt history (retained)');
    for (const attempt of s.attempts) lines.push(...wrap(`    ${attempt.id}: ${attempt.status}; head ${short(attempt.headCommit)}; ${attempt.receiptId}`, width));
  }
  lines.push('', 'Control');
  lines.push(...wrap(`  Desired ${s.workflow.desiredState}; observed ${s.workflow.observedState}; cleanup ${s.workflow.cleanup}; active owned operations ${s.workflow.activeOperationCount ?? 'UNKNOWN'}.`, width));
  if (s.workflow.quiescenceEvidenceRef) lines.push(...wrap(`  Quiescence proof: ${s.workflow.quiescenceEvidenceRef}`, width));
  lines.push('', 'Next');
  const primary = s.actions.find((a)=>a.id===s.focus.nextActionId) || s.actions[0];
  lines.push(...wrap(`  ${primary?.label || 'No action'} — ${primary ? (primary.available ? 'AVAILABLE' : 'UNAVAILABLE') : 'UNAVAILABLE'}. ${primary?.reason || s.focus.nextActionReason}`, width));
  lines.push('', 'Integrations');
  for (const item of s.integrations) lines.push(...wrap(`  ${item.name}: ${item.observedCapability}; ${item.version}. ${item.limitations.join('; ')}`, width));
  lines.push('', 'Measurements');
  for (const m of s.measurements) lines.push(`  ${m.label==='Cost'?'Actual charge':m.label}: ${m.value === null ? 'unknown' : `${m.value} ${m.unit}`}${m.value === null && m.source === 'not measured' ? '' : ` (${m.source})`}`);
  lines.push('', ...renderUsageLines(s.usage,width,false));
  lines.push('', `Full identity: run=${s.run.id} plan=${s.run.planIdentity} head=${s.run.headCommit}`);
  lines.push(`tree=${s.run.treeId} policy=${s.run.policyIdentity} capability=${s.source.capabilityIdentity}`);
  return boundLines(lines, width).join('\n');
}

export function renderEvidenceDetail(input, selected = 0, width = 80, options = {}) {
  const s = project(input), gate = s.gates[selected];
  if (!gate) return renderExpanded(input, {width,presentation:options.presentation});
  const lines = [provenance(options.presentation,'evidence detail'), 'DEMO • SYNTHETIC', `${gate.label}: ${gate.outcome} / ${gate.currentness}`, `receipt=${gate.receiptId}`, `head=${gate.headCommit}`, `tree=${gate.treeId}`, `policy=${gate.policyIdentity}`, `artifacts=${(gate.artifactRefs||[]).join(', ')}`, `limitations=${(gate.limitations||[]).join('; ') || 'none'}`];
  if (gate.outcome === 'NOT_APPLICABLE') lines.push(`N/A rationale=${gate.notApplicableRationale}`);
  return boundLines(lines, width).join('\n');
}

export function renderEvidencePanel(input, selected = 0, width = 80, options = {}) {
  const s=project(input),lines=[provenance(options.presentation,'evidence selector'),'DEMO • SYNTHETIC',`Evidence / ${s.aggregate.outcome}`];
  if(!s.gates.length) lines.push('  No receipts. Required policy evidence is INCOMPLETE.');
  s.gates.forEach((gate,index)=>lines.push(`${index===selected?'>':' '} ${gate.label||gate.id}: ${status(gate)} | ${gate.receiptId||'MISSING'}`));
  if(s.review) lines.push(`  Review: ${s.review.outcome} | independence ${s.review.independence}`);
  return boundLines(lines,width).join('\n');
}

function renderUsageLines(usage,width,detail=true){
  const lines=['Usage',`  ${usage.label}: ${usage.available?`USD ${usage.knownCostUsd} known / ${usage.status}`:'unavailable'}`,`  ${usage.legend}`,`  Observed tokens: ${usage.available?usage.observedTokens:'unknown'}; final ${usage.finalRequests}; provisional ${usage.provisionalRequests}; unpriced requests ${usage.unpricedRequests}; freshness ${usage.freshness}.`,`  Actual charge: unknown.`];
  if(detail)for(const model of usage.models){
    lines.push('',`  ${model.model} | ${model.provider} | ${model.knownCostNanoUsd?`USD ${model.knownCostUsd} known`:model.unpricedRequests?'no priced subtotal':'known measured zero'}${model.unpricedRequests?` | ${model.unpricedRequests} unpriced`:''}`);
    lines.push(`    input ${model.inputTotal} = ordinary ${model.inputOrdinary} + cacheR ${model.cacheRead} + cacheW ${model.cacheWrite} (1h ${model.cacheWrite1h})`);
    lines.push(`    output ${model.outputTotal}; reasoning subset ${model.reasoningUnknown?`${model.reasoningKnown} known + unknown`:model.reasoningKnown}`);
    lines.push(`    actors ${model.actors.join(', ')}; requested ${model.requestedModels.join(', ')}`);
    for(const rate of model.rateSnapshots)lines.push(`    rate ${rate.id}; ${rate.tier}; as-of ${rate.asOf}; ${rate.source}`);
  }
  if(detail&&usage.requests.length){lines.push('','  Requests / attempts');for(const row of usage.requests)lines.push(`    ${row.phase} ${row.actorRole}:${row.actorId} ${row.attemptId} r${row.revision} ${row.stage} | ${row.knownCostUsd===null?`UNPRICED: ${row.unpricedReason}`:`USD ${row.knownCostUsd}`}`);}
  for(const condition of usage.conditions)lines.push(`  condition: ${condition}`);
  return lines.flatMap(line=>wrap(line,width));
}

export function renderUsageDetail(input, options={}){
  const s=project(input),width=Math.max(40,Math.min(120,Number(options.width)||80));
  return boundLines([provenance(options.presentation,'usage detail'),'DEMO • SYNTHETIC TOKENS + SYNTHETIC RATES',`FORGE / ${s.run.id} / ${s.workflow.phase} / workflow ${s.aggregate.outcome}`,'',...renderUsageLines(s.usage,width,true)],width).join('\n');
}
