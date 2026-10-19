// Forge cockpit panel and inline card for desktop host mockups.
// Reads the shared projected states (companion-data.js, generated from data/fixtures.json by the same projector the
// terminal renderer uses). No network, no model, no authority: the only button previews a bound intent as JSON.
(() => {
  'use strict';
  const PHASES = ['Launch', 'Plan', 'Execute', 'Evaluate', 'Repair', 'Review', 'PR'];
  const GLYPH = { PASS: '✓', FAIL: '✗', INCOMPLETE: '◐', ERROR: '!', NOT_APPLICABLE: '–', UNKNOWN: '?' };
  const fmt = (n) => Number.isFinite(Number(n)) ? Number(n).toLocaleString('en-US') : 'unknown';
  const short = (v) => String(v || '').slice(0, 7);
  const el = (tag, attrs = {}, ...children) => {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs)) {
      if (value === null || value === undefined || value === false) continue;
      if (key === 'class') node.className = value; else if (key === 'text') node.textContent = value; else if (key.startsWith('on')) node.addEventListener(key.slice(2), value); else node.setAttribute(key, value === true ? '' : value);
    }
    for (const child of children.flat()) if (child !== null && child !== undefined && child !== false) node.append(child.nodeType ? child : document.createTextNode(String(child)));
    return node;
  };
  const glyphFor = (outcome) => GLYPH[outcome] || GLYPH.UNKNOWN;
  const word = (gate) => `${gate.outcome === 'NOT_APPLICABLE' ? 'N/A' : gate.outcome}${gate.currentness === 'current' ? '' : ` ${gate.currentness.toUpperCase()}`}`;
  const outcomeClass = (gate) => gate.currentness !== 'current' ? 'o-STALE' : `o-${gate.outcome}`;
  const freshOk = (s) => s.source.freshness === 'current' && s.source.continuity === 'complete';
  const primaryAction = (s) => s.actions.find((a) => a.id === s.focus.nextActionId) || s.actions[0] || null;
  const compressReasons = (reasons) => {
    const missing = [], rest = [];
    for (const r of reasons) { const m = /^(.+): required policy gate is missing$/.exec(r); m ? missing.push(m[1]) : rest.push(r); }
    return (missing.length ? [`no receipt yet: ${missing.join(', ')}`] : []).concat(rest);
  };
  const previewIntent = (s, action) => ({ status: 'PREVIEW_ONLY', action: action.id, runId: s.run.id, expectedSnapshotId: s.source.snapshotId, expectedSequence: s.source.sequence, expectedPlanIdentity: s.run.planIdentity, expectedPlanRevision: s.run.planRevision, expectedHeadCommit: s.run.headCommit, expectedTreeId: s.run.treeId, expectedPolicyIdentity: s.run.policyIdentity, expectedCapabilityIdentity: s.source.capabilityIdentity, requestedCeiling: action.id === 'request.publish-pr' ? 'pr' : s.run.authorizationCeiling, approvalRequestRef: null, sideEffects: 0, authorityIssued: false, note: 'No controller adapter exists in this prototype.' });

  function rail(s, hostLabel) {
    return el('div', { class: 'forge-rail' },
      el('span', { class: 'wordmark', text: 'FORGE' }),
      el('span', { class: 'identity' }, el('b', { text: s.run.id }), ` · plan r${s.run.planRevision} · head ${short(s.run.headCommit)} · ceiling `, el('b', { text: s.run.authorizationCeiling.toUpperCase() }), hostLabel ? ` · ${hostLabel}` : ''),
      el('span', { class: 'forge-tag', text: s.demoLabel || 'DEMO • SYNTHETIC' }));
  }
  function trail(s) {
    const index = PHASES.findIndex((p) => p.toUpperCase() === s.workflow.phase);
    return el('div', { class: 'forge-trail', 'aria-label': 'Phase trail (history, not progress percentage)' },
      PHASES.map((p, i) => el('span', { class: [i === index ? 'current' : i < index ? 'done' : '', p === 'Repair' ? 'branch' : ''].join(' ').trim(), text: i === index ? `● ${p}` : p })));
  }
  function focus(s) {
    const agg = s.aggregate.outcome;
    return el('div', { class: 'forge-focus' },
      el('div', { class: 'state' }, el('span', { class: `o-${agg}`, text: `${glyphFor(agg)} ${s.workflow.phase} · ${agg}` }), el('span', { class: `src ${freshOk(s) ? '' : 'warn'}`, text: `seq ${s.source.sequence} · ${s.source.freshness} · ${s.source.continuity}` })),
      el('h1', { text: s.focus.title }),
      el('p', { text: s.focus.explanation }),
      s.focus.blockerCode ? el('div', { class: 'blocker', text: `blocker ${s.focus.blockerCode}` }) : null);
  }
  function section(title, meta, cls, ...body) { return el('section', { class: `forge-section ${cls || ''}` }, el('header', {}, el('span', { text: title }), meta ? el('span', { class: 'meta', text: meta }) : null), ...body); }

  function evidence(s, ui) {
    const rows = s.gates.map((gate, i) => {
      const stale = gate.currentness !== 'current';
      const notes = [];
      if (stale) notes.push({ text: `receipt binds head ${short(gate.headCommit)}, run head is ${short(s.run.headCommit)} · cannot aggregate PASS`, warn: true });
      if (gate.outcome === 'NOT_APPLICABLE') notes.push({ text: `N/A allowed by policy · ${gate.notApplicableRationale || 'MISSING RATIONALE'}` });
      for (const lim of gate.limitations) notes.push({ text: lim, warn: gate.outcome !== 'PASS' });
      return el('tr', { tabindex: 0, 'aria-selected': String(ui.selected === i), onclick: () => ui.select(i), onkeydown: (e) => { if (e.key === 'Enter') { ui.select(i, true); e.preventDefault(); } } },
        el('td', { class: `glyph ${outcomeClass(gate)}`, text: glyphFor(gate.outcome) }),
        el('td', { class: `outcome ${outcomeClass(gate)}`, text: word(gate) }),
        el('td', { class: 'label', text: gate.label }),
        el('td', { class: 'receipt' }, gate.receiptId || 'MISSING RECEIPT', notes.map((n) => el('span', { class: `note ${n.warn ? 'warn' : ''}`, text: n.text }))));
    });
    if (s.review) {
      const same = s.review.headCommit === s.run.headCommit;
      rows.push(el('tr', { class: 'review' },
        el('td', { class: `glyph ${same ? `o-${s.review.outcome}` : 'o-STALE'}`, text: glyphFor(s.review.outcome) }),
        el('td', { class: `outcome ${same ? `o-${s.review.outcome}` : 'o-STALE'}`, text: `${s.review.outcome}${same ? '' : ' STALE'}` }),
        el('td', { class: 'label', text: 'Review' }),
        el('td', { class: 'receipt' }, `independence ${s.review.independence} · ${s.review.reviewerFamily} reviews ${s.review.writerFamily}`, el('span', { class: `note ${same ? '' : 'warn'}`, text: same ? `bound to head ${short(s.review.headCommit)} · ${s.review.assessmentRef}` : `belongs to head ${short(s.review.headCommit)}, run head is ${short(s.run.headCommit)} · re-review required` }))));
    } else if (s.requirements.reviewRequired && s.gates.length) {
      rows.push(el('tr', { class: 'review' }, el('td', { class: 'glyph o-INCOMPLETE', text: GLYPH.INCOMPLETE }), el('td', { class: 'outcome o-INCOMPLETE', text: 'INCOMPLETE' }), el('td', { class: 'label', text: 'Review' }), el('td', { class: 'receipt' }, el('span', { class: 'note', text: 'required independent review has not been issued for this head' }))));
    }
    const gate = s.gates[ui.selected];
    const detail = gate && ui.detailOpen ? el('dl', { class: 'gate-detail' },
      el('dt', { text: 'receipt' }), el('dd', { class: gate.receiptId ? '' : 'bad', text: gate.receiptId || 'MISSING RECEIPT' }),
      el('dt', { text: 'head' }), el('dd', { class: gate.headCommit === s.run.headCommit ? '' : 'warn', text: gate.headCommit }),
      gate.headCommit === s.run.headCommit ? null : el('dt', { text: 'run head' }), gate.headCommit === s.run.headCommit ? null : el('dd', { text: s.run.headCommit }),
      el('dt', { text: 'tree' }), el('dd', { text: gate.treeId }),
      el('dt', { text: 'policy' }), el('dd', { text: gate.policyIdentity }),
      el('dt', { text: 'artifacts' }), el('dd', { text: gate.artifactRefs.join(', ') || 'none' }),
      el('dt', { text: 'limitations' }), el('dd', { class: gate.limitations.length ? 'warn' : '', text: gate.limitations.join('; ') || 'none recorded' }),
      gate.outcome === 'NOT_APPLICABLE' ? el('dt', { text: 'N/A reason' }) : null, gate.outcome === 'NOT_APPLICABLE' ? el('dd', { text: gate.notApplicableRationale || 'MISSING' }) : null) : null;
    const agg = s.aggregate.outcome;
    return section('Evidence', `policy ${s.run.policyIdentity} · ${s.gates.length}/${s.requirements.gateIds.length} receipts`, '',
      s.gates.length ? el('table', { class: 'gate-table', role: 'grid' }, el('tbody', {}, rows)) : el('div', { class: 'attempts', text: `No gate receipts in this fixture state. ${s.requirements.gateIds.length} required gates and independent review are still due; aggregate cannot infer green.` }),
      detail,
      s.attempts.length ? el('div', { class: 'attempts', text: `attempts  ${s.attempts.map((a) => `${a.id} ${a.status} @ ${short(a.headCommit)}`).join(' · ')}` }) : null,
      agg !== 'PASS' && s.aggregate.reasons.length ? el('div', { class: 'why', text: `why ${agg}: ${compressReasons(s.aggregate.reasons).slice(0, 6).join('; ')}` }) : null);
  }

  function control(s) {
    const w = s.workflow, mismatch = w.desiredState !== w.observedState, unknown = w.cleanup === 'unknown';
    const item = (k, v, cls) => el('span', { class: cls || '' }, `${k} `, el('b', { text: v }));
    return section('Control', null, '', el('div', { class: 'control' },
      item('desired', w.desiredState, mismatch ? 'warn' : ''), item('observed', w.observedState, mismatch ? 'warn' : ''), item('cleanup', w.cleanup, unknown ? 'bad' : ''), item('owned ops', w.activeOperationCount ?? 'UNKNOWN', w.activeOperationCount === null ? 'bad' : ''),
      w.quiescenceEvidenceRef ? el('span', { class: 'proof', text: `${GLYPH.PASS} quiescence proof ${w.quiescenceEvidenceRef}` }) : unknown ? el('span', { class: 'proof bad', text: '! no closure proof; resume, repair and publication stay blocked until reconciliation' }) : mismatch ? el('span', { class: 'proof warn', text: '◐ desired and observed differ; host idle is not Forge quiescence' }) : null));
  }

  function next(s, ui) {
    const primary = primaryAction(s);
    const rows = s.actions.map((a) => el('div', { class: `action ${a === primary ? '' : 'secondary'}` },
      el('span', { class: 'marker', text: a === primary ? '▶' : '' }), el('span', { class: 'label', text: a.label }), el('span', { class: `avail ${a.available ? '' : 'no'}`, text: a.available ? 'AVAILABLE' : 'UNAVAILABLE' }),
      a.kind === 'intent' ? el('span', { class: 'kind', text: 'intent · preview only · controller revalidates everything at time of effect' }) : null,
      el('span', { class: `reason ${a.available ? '' : 'no'}`, text: a.reason })));
    const intentAction = s.actions.find((a) => a.kind === 'intent' && a.available);
    const pre = el('pre', { class: `intent ${ui.intentOpen ? 'open' : ''}`, tabindex: 0, 'aria-label': 'Intent preview', text: ui.intentOpen && intentAction ? JSON.stringify(previewIntent(s, intentAction), null, 2) : '' });
    return section('Next', null, '', el('div', { class: 'actions' }, rows.length ? rows : el('div', { class: 'action' }, el('span'), el('span', { class: 'label', text: `No action admissible · ${s.focus.nextActionReason}` }))),
      el('div', { class: 'action-footer' }, el('button', { type: 'button', disabled: !intentAction, onclick: () => ui.toggleIntent() }, intentAction ? `Preview bound intent: ${intentAction.label}` : 'No consequential intent is admissible here'), el('span', { text: 'sideEffects 0 · authorityIssued false' })), pre);
  }

  function usage(s, full) {
    const u = s.usage;
    const head = el('div', { class: 'usage-head' },
      el('div', {}, el('div', { class: 'label', text: u.label }), el('div', { class: 'amount' }, u.available ? (u.knownCostUsd === null ? 'no priced subtotal' : `USD ${u.knownCostUsd}`) : 'unavailable', el('small', { text: u.available ? 'known subtotal' : 'no admitted observations yet' }))),
      el('span', { class: `chip ${u.status}`, text: u.status }));
    const meta = el('div', { class: 'usage-meta', text: u.available ? `${fmt(u.observedTokens)} tokens · ${u.finalRequests} final · ${u.provisionalRequests} provisional · ${u.unpricedRequests} unpriced · freshness ${u.freshness} · actual charge unknown` : 'actual charge unknown · synthetic tokens + synthetic rates' });
    if (!u.available) return section('Usage', 'synthetic tokens + synthetic rates', 'violet', head, meta);
    const priced = u.models.filter((m) => m.knownCostNanoUsd > 0), total = priced.reduce((a, m) => a + m.knownCostNanoUsd, 0);
    const table = el('table', { class: 'model-table' }, el('thead', {}, el('tr', {}, el('th', { text: 'Model' }), el('th', { text: 'Known USD', class: 'num' }), el('th', { text: 'Tokens', class: 'num' }), el('th', { text: 'Share of known' }))),
      el('tbody', {}, u.models.map((m) => {
        const share = m.knownCostNanoUsd > 0 && total ? m.knownCostNanoUsd / total : null;
        return el('tr', {},
          el('td', { class: 'name' }, m.model, el('small', { text: `${m.provider} · ${m.actors.join(', ')}` })),
          el('td', { class: `num ${share === null ? 'o-INCOMPLETE' : ''}`, text: m.knownCostUsd || 'UNPRICED' }),
          el('td', { class: 'num', text: fmt(m.inputTotal + m.outputTotal) }),
          el('td', {}, share === null ? el('div', { class: 'bar unpriced', text: `? ${m.unpricedRequests} unpriced request${m.unpricedRequests === 1 ? '' : 's'}` }) : el('div', { class: 'bar' }, el('i', { style: `width:${Math.max(4, Math.round(share * 120))}px` }), el('b', { text: `${Math.round(share * 100)}%` }))));
      })));
    const breakdown = full ? u.models.map((m) => el('div', { class: 'usage-legend', text: `${m.model}: input ${fmt(m.inputTotal)} = ${fmt(m.inputOrdinary)} ordinary + ${fmt(m.cacheRead)} cache read + ${fmt(m.cacheWrite)} cache write (1h ${fmt(m.cacheWrite1h)}) · output ${fmt(m.outputTotal)} (reasoning ${m.reasoningUnknown ? `${fmt(m.reasoningKnown)} known + unknown` : fmt(m.reasoningKnown)}) · rate ${m.rateSnapshots.map((r) => `${r.id} as-of ${r.asOf.slice(0, 10)}`).join(', ') || 'UNRESOLVED'}` })) : [];
    const requests = full && u.requests.length ? el('table', { class: 'req-table' }, el('tbody', {}, u.requests.map((r) => el('tr', {}, el('td', { text: r.phase }), el('td', { text: `${r.actorRole}:${r.actorId}` }), el('td', { text: `${r.attemptId} r${r.revision}` }), el('td', { text: r.stage }), el('td', { class: 'num' }, r.knownCostUsd === null ? el('span', { class: 'warn', text: `UNPRICED · ${r.unpricedReason}` }) : `USD ${r.knownCostUsd}`))))) : null;
    return section('Usage', 'synthetic tokens + synthetic rates', 'violet', head, meta, table, ...breakdown, requests, el('div', { class: 'usage-legend', text: u.legend }), ...u.conditions.map((c) => el('div', { class: 'usage-legend', text: `· ${c}` })));
  }

  function plan(s) {
    const p = s.plan || {};
    const list = (items) => el('ul', {}, (items || []).map((x) => el('li', { text: x })));
    return section('Plan', `r${s.run.planRevision} · ${s.run.planIdentity}`, 'violet',
      el('dl', { class: 'plan-grid' }, el('dt', { text: 'scope' }), el('dd', {}, list(p.scope)), el('dt', { text: 'accept' }), el('dd', {}, list(p.acceptance)), el('dt', { text: 'exclude' }), el('dd', {}, list(p.exclusions))),
      el('div', { class: 'plan-note', text: `approval preview binds plan r${s.run.planRevision}, base ${short(s.run.baseCommit)} and capability ${s.source.capabilityIdentity} · preview is not approval` }));
  }

  function publication(s) {
    const p = s.publication;
    if (!p || !p.requestedAction) return null;
    return section('Publication', null, 'violet', el('div', { class: 'control' },
      el('span', {}, 'requested ', el('b', { text: p.requestedAction })), el('span', {}, 'remote effect ', el('b', { text: p.observedEffect })), el('span', {}, 'readback ', el('b', { text: p.readbackRef ? 'confirmed' : 'missing' })), el('span', { class: 'warn' }, 'CI ', el('b', { text: p.ciOutcome })), el('span', { class: p.mergeAuthorized ? '' : 'warn' }, 'merge ', el('b', { text: p.mergeAuthorized ? 'AUTHORIZED' : 'NOT authorized' })),
      el('span', { class: 'proof warn', text: `receipt ${p.receiptRef || 'none'} · readback ${p.readbackRef || 'none'} · ${p.remoteUrl || 'no remote URL in fixture'} · PR is not deployment` })));
  }

  function integrations(s) {
    const cls = (c) => c === 'supported' ? 'o-PASS' : c === 'unavailable' ? 'o-NOT_APPLICABLE' : 'o-INCOMPLETE';
    const glyph = (c) => c === 'supported' ? GLYPH.PASS : c === 'unavailable' ? GLYPH.FAIL : GLYPH.UNKNOWN;
    const unmeasured = s.measurements.filter((m) => m.value === null).map((m) => m.label === 'Cost' ? 'actual charge' : m.label.toLowerCase());
    return section('Integrations', 'no integration call is made by the demo', '',
      el('div', { class: 'integrations' }, s.integrations.map((i) => el('div', { class: 'integration' }, el('b', {}, i.name, el('span', { class: cls(i.observedCapability), text: `${glyph(i.observedCapability)} ${i.observedCapability.toUpperCase()}` })), el('p', { text: `${i.role} · ${i.version}` }), el('p', { text: i.limitations.join('; ') })))),
      el('div', { class: 'measure', text: `not measured: ${unmeasured.join(', ') || 'none'}` }));
  }

  function footer(s, hints) {
    return el('div', { class: 'forge-foot' },
      el('div', { class: 'keys' }, el('span', { text: hints || '↑/↓ evidence · Enter detail · 1-5 tabs · P preview' }), el('span', { text: 'zero live adapters · intent output is preview only' })),
      el('div', { class: 'ids', text: `run ${s.run.id} · plan ${s.run.planIdentity} · policy ${s.run.policyIdentity} · capability ${s.source.capabilityIdentity} · snapshot ${s.source.snapshotId}` }),
      el('div', { class: 'ids', text: `head ${s.run.headCommit} · tree ${s.run.treeId}` }));
  }

  const TABS = [['overview', 'Overview'], ['evidence', 'Evidence'], ['usage', 'Usage'], ['plan', 'Plan'], ['integrations', 'Integrations']];

  function mount(container, options = {}) {
    const states = options.states || globalThis.FORGE_DEMO_STATES || [];
    const ui = { scenario: options.scenario || 'failed-gate', tab: options.tab || 'overview', selected: 0, detailOpen: options.detail === true, intentOpen: options.preview === true,
      select(i, toggle) { ui.selected = i; if (toggle) ui.detailOpen = !ui.detailOpen; else ui.detailOpen = true; render(); },
      toggleIntent() { ui.intentOpen = !ui.intentOpen; render(); },
      setTab(tab) { ui.tab = tab; render(); },
      setScenario(name) { ui.scenario = name; ui.selected = 0; ui.detailOpen = false; ui.intentOpen = false; render(); } };
    const state = () => states.find((x) => x.name === ui.scenario) || states[0];
    function render() {
      const s = state(); if (!s) { container.replaceChildren(el('div', { class: 'forge', text: 'No fixture states loaded.' })); return; }
      const tabs = el('div', { class: 'forge-tabs', role: 'tablist' }, TABS.map(([id, label]) => el('button', { type: 'button', role: 'tab', 'aria-selected': String(ui.tab === id), onclick: () => ui.setTab(id) }, label, id === 'evidence' ? el('span', { class: 'count', text: String(s.gates.length) }) : null)));
      const body = el('div', { class: 'forge-body', role: 'tabpanel' });
      if (ui.tab === 'overview') body.append(evidence(s, ui), publication(s) || '', control(s), next(s, ui), usage(s, false));
      else if (ui.tab === 'evidence') body.append(evidence(s, { ...ui, detailOpen: true }), control(s));
      else if (ui.tab === 'usage') body.append(usage(s, true));
      else if (ui.tab === 'plan') body.append(plan(s), next(s, ui));
      else body.append(integrations(s));
      const root = el('div', { class: 'forge', tabindex: 0, 'aria-label': 'Forge cockpit', onkeydown: (e) => {
        if (['INPUT', 'TEXTAREA'].includes(document.activeElement?.tagName)) return;
        if (e.key === 'ArrowDown') { ui.select(Math.min(s.gates.length - 1, ui.selected + 1)); e.preventDefault(); }
        else if (e.key === 'ArrowUp') { ui.select(Math.max(0, ui.selected - 1)); e.preventDefault(); }
        else if (e.key === 'Enter') { ui.select(ui.selected, true); }
        else if (/^[1-5]$/.test(e.key)) ui.setTab(TABS[Number(e.key) - 1][0]);
        else if (e.key.toLowerCase() === 'p') ui.toggleIntent();
      } }, rail(s, options.hostLabel), trail(s), focus(s), tabs, body, footer(s, options.hints));
      // keep keyboard focus on the panel across re-renders so arrow/Enter/P sequences keep working
      const hadFocus = container.contains(document.activeElement);
      container.replaceChildren(root);
      if (hadFocus) root.focus({ preventScroll: true });
      if (options.onRender) options.onRender(s, ui);
    }
    render();
    return ui;
  }

  function card(container, options = {}) {
    const states = options.states || globalThis.FORGE_DEMO_STATES || [];
    const s = states.find((x) => x.name === (options.scenario || 'failed-gate')) || states[0];
    const agg = s.aggregate.outcome, primary = primaryAction(s), u = s.usage;
    const chips = s.gates.map((g) => el('span', { class: `gate ${outcomeClass(g)} ${g.currentness === 'current' ? '' : 'STALE'}`, text: `${glyphFor(g.outcome)} ${g.label} ${word(g)}` }));
    if (s.review) chips.push(el('span', { class: `gate ${s.review.headCommit === s.run.headCommit ? `o-${s.review.outcome}` : 'o-STALE STALE'}`, text: `${glyphFor(s.review.outcome)} Review ${s.review.outcome}${s.review.headCommit === s.run.headCommit ? '' : ' STALE'}` }));
    const node = el('div', { class: 'forge-card', 'aria-label': 'Forge status card' },
      el('div', { class: 'row' }, el('span', { class: 'wordmark', text: 'FORGE' }), el('span', { class: 'identity' }, el('b', { text: s.run.id }), ` · r${s.run.planRevision} · ${short(s.run.headCommit)} · ceiling `, el('b', { text: s.run.authorizationCeiling.toUpperCase() }), ` · seq ${s.source.sequence}${freshOk(s) ? '' : ` · ${s.source.freshness.toUpperCase()}`}`), el('span', { class: 'forge-tag', text: s.demoLabel || 'DEMO • SYNTHETIC' })),
      el('div', { class: 'row' }, el('span', { class: `state o-${agg}`, text: `${glyphFor(agg)} ${s.workflow.phase} · ${agg}` }), el('span', { class: 'title', text: s.focus.title })),
      el('div', { class: 'row' }, el('div', { class: 'chips' }, chips.length ? chips : el('span', { class: 'gate o-INCOMPLETE', text: `◐ ${s.requirements.gateIds.length} required receipts due · no green inferred` }))),
      el('div', { class: 'row' }, el('span', { class: 'usage', text: u.available ? `${u.label} ~${Number(u.knownCostUsd || 0).toFixed(4)} · ${u.status} · ${fmt(u.observedTokens)} tok${u.unpricedRequests ? ` · ${u.unpricedRequests} unpriced` : ''}` : `${u.label} unavailable · synthetic tokens/rates` })),
      el('div', { class: 'row' }, el('div', { class: 'next' }, el('b', { text: primary ? `▶ ${primary.label}` : 'No action admissible' }), el('span', { text: primary ? `${primary.available ? 'AVAILABLE' : 'UNAVAILABLE'} · ${primary.reason}` : s.focus.nextActionReason })), el('button', { type: 'button', class: 'open', onclick: options.onOpen || null }, options.openLabel || 'Open Forge panel')),
      el('div', { class: 'row foot', text: options.footer || 'one projection · zero live adapters · preview only' }));
    container.replaceChildren(node);
    return s;
  }

  globalThis.ForgeCockpit = { mount, card, PHASES };
})();
