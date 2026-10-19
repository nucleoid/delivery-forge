# UX-1.2 synthetic usage/API-equivalent presentation — implementation notes

This records the bounded prototype implementation scope for the usage/cost design. Read [`usage-cost-design.md`](usage-cost-design.md) for normative semantic choices. It is historical implementation guidance, not proof of production telemetry or native-host acceptance.

## Clarification to implement, not just document

The user means **metrics throughout Delivery Forge runs**, not current-chat/UI counters. Production instrumentation is required at the harness adapter/model-invocation boundary across planning, coordinator/context-model work, workers, repair, independent review, retries and observable failed/cancelled operations; durable history/export belongs to the harness. **For this prototype, implement a real bounded append-only synthetic JSONL ledger and derive live-view replay from it.** The earlier in-memory-only allowance is superseded. Do not edit current core/PR32; only inert prototype persistence is authorized here.

## Sequence and write scope

1. Preserve the R1–R4 repair/regression history, then apply this bounded fixture/presentation change.
2. Limit changes to the isolated prototype and generated synthetic evidence; do not alter Delivery Forge runtime work.
3. No host install/upgrade/global config, telemetry exporter activation, credentials, real provider/model/Forge/Engram/Continuum/Switchyard call, runtime pipeline or remote write. No background collector/watch timer. Existing local Node/Playwright harness only; no new heavy dependencies.
4. Final commit must include both completed repair and usage slice. Report exact commit/tree, tests, regenerated raw/PNG hashes and what remains unverified. Do not report native-installed/production usage streaming from a fixture.

## Implement this finite slice

### A. Shared pure demo accounting

Add small versioned pure usage normalization/pricing/reducer helpers plus a bounded demo-only JSONL persistence boundary and synthetic observation/rate fixtures. Expose one `usage` summary on the shared presentation projection; all adapters and companion consume the ledger-derived result. Run/phase/task/attempt/invocation/model/provider and parent-child correlation must be carried through the records; invocation/incurrence identity is distinct from observation/event delivery ID. Optional absence degrades to **unavailable**, not0. Keep gate/intent/authority behavior unchanged.

Model counts are normalized to exclusive ordinary/read/write buckets plus inclusive inputTotal and outputTotal; reasoning is an output subset. Support both raw OpenAI-inclusive and Anthropic/Pi-exclusive fixture shapes; preserve mapping provenance. CacheWrite1h is a subset of cacheWrite. Missing presence/unknownmodel/rate/tier must survive as unknown, never default0 merely for UI convenience.

Implement only the deterministic demo cases:
- Stable request/attempt identity with replaceable partial/final revisions.
- Identical duplicate and mirrored observation do not add; conflicting duplicate marks uncertainty.
- Distinct incurred retry adds its own measured usage.
- A cumulative checkpoint/resume replacement does not add the full historical total again; epoch/reset/gap requires explicit reconciliation.
- Inclusive parent/host aggregate cannot be added to the same covered child records; choose primary contribution and show corroboration/coverage.
- Immutable synthetic rate snapshot per priced record, decimal-safe arithmetic, known subtotal/complete-or-partial status.
- Per-model totals, whole-run totals and phase/actor/request drilldown. A live unpriced coordinator row is still counted in observed tokens and unpriced coverage; not cost0.
- Stale/revoked source presentation, out-of-order older revision ignored/quarantined with visible condition, no mutation of gate authority.

This is **not** a live provider/database integration, but it must be **real file-backed synthetic accounting**, not cosmetic screen fixture values. Closed allowlisted metadata, bounds and safe malformed-input fallback are required. Optional/unreported external work marks coverage partial; it cannot disappear from a claimed complete run total.

### A2. Actual demo persistence and replay (required)

- Implement append-only JSONL in an explicitly owned demo directory. Only bundled synthetic observations/rate snapshots are admitted. No reading real host/session histories or arbitrary credential/content files.
- Bounds:1000 records,16KiB per record,1MiB total; reject/mark capacity partial before exceeding any bound. One writer only; fail explicitly if ownership unavailable. Do not add a daemon or distributed store.
- Stable ledger sequence plus observation/delivery ID, invocation/incurrence ID, attempt/revision and correlation/coverage. Append a full line and flush/fsync before acknowledging durable success. Never rewrite older observations; corrections/finals are appended revisions whose reducer replaces prior contributions.
- Reopen in a **fresh Node process**, replay from disk and get the same model/run totals. Pause/resume/restart and UI teardown do not reset costs. UI cursor/preferences are separate from the ledger's accounting state.
- Duplicate delivery may appear in the append log but contributes once; distinct new actual-attempt fixture adds usage. Replayed cumulative baseline does not double count.
- Detect truncated/uncommitted tail, malformed middle, gaps and conflicting IDs. Preserve existing bytes; report incomplete coverage and stop unsafe append/reconciliation rather than silently skip/truncate/overwrite data. A recovery demonstration may use explicit new reconciliation records/segment after retained uncertain evidence; do not invent complete recovery.
- Provide completed synthetic-run history/readback and a bounded metadata-only export. The ledger remains the source; exported snapshots/checkpoints are projections with source identity, not substitutes that erase observations.
- Demo progression appends known inert observations, then rerenders from the ledger. Manual replay/control is sufficient to show live updates; no fake clock increments, polling model providers or actual invocations. A static browser companion may display exported ledger-derived snapshots without filesystem access; label its replay as view-only and do not claim a browser click durably appended anything. Native/CLI persistence/reopen tests establish the durable source of truth without adding a server. A tiny local Node child for restart testing is allowed; no coding-host/model CLI calls.
- Report the exact final CLI/API recipe for creating, appending, reopening and exporting an owned ledger so callers do not have to infer flags or patch source.

### B. Exact synthetic example to implement/test

Use names unmistakably fake, e.g. `demo.openai.writer`, `demo.anthropic.reviewer`, `demo.unresolved.coordinator`. Rates have `synthetic:true`, USD/MTok unit, explicit synthetic effective/as-of version and category applicability. Do not download prices in code or silently use current official prices.

1. Writer attemptA: raw inclusive input10000, read6000, write1000 → ordinary3000; final output2000 including reasoning500. Synthetic rates2/0.2/2.5/10. Cost **0.029700USD**.
2. Earlier partialA output1200 (same request/attempt; not another request) → **0.021700USD**. Final replaces partial, increasing total by0.008000; never sum both.
3. ReviewerB: ordinary1000, read4000, write500 including200 at1h (remaining3005m), output400 including reasoning100. Synthetic rates input3/read0.3/write5m3.75/write1h6/output15. Cost **0.012525USD**.
4. Distinct writer retryC: ordinary500, measured read0/write0, output100, reasoning breakdownunknown. Same writer rates. Cost **0.002000USD**. No need to know reasoning subset to price inclusive output if that rate basis is verified.
5. Known subtotal **0.044225USD**; writer total0.031700, reviewer0.012525. Known inclusive tokens18500: writer12600, reviewer5900.
6. Fourth unresolved coordinator record: input700/output100, rate/model mappingunknown. Observed tokens19300 across all4 records; **whole-run complete costunknown/PARTIAL**, known subtotal0.044225, unpriced requests1. If modelidentity is ambiguous, keep a clearly unresolved bucket rather than allocating it to an API SKU.
7. Duplicate/mirrorA and replayed checkpoint leave totals unchanged. A new synthetic catalog version never silently reprices the existing4 observations. Demonstrate explicit comparison separately only if small; otherwise test old snapshot immutability.

Enrich existing main screenshots/states rather than invent a separate product: launch usageunavailable; execute partialA; failed-gate/recovery known subtotal plus unresolved coverage; later usage detail shows multiple models/roles. Keep source/ceiling/gate story exactly as designed. If additional usage-specific fixture scenarios are needed, name them clearly and include them in catalog/tests; do not manufacture actual host session history.

### C. Presentation

- Prominent exact wording **USD API-EQUIVALENT ESTIMATE**. Every demo surface says **synthetic tokens/rates** (can be a shared DEMO marker plus explicit detail legend); not actual invoice/subscription spend.
- Compact80-column row example: `USD API-EQUIVALENT ESTIMATE ~0.0442 | demo.writer | PARTIAL`.
- Label current actor/model only when active; parallel→multiplemodels, idle→last model/no active model. Do not generate activity indicators from replay time.
- Shared compact target4–5 lines, max6 when identity wraps. Never hide identity/ceiling/blocker/nextaction to fit spend.
- Detail120-column concise table and80-column stacked cards: provider/effective model, input total and ordinary/cacheR/cacheW5m/W1h, output and reasoning subset, rate snapshot/version/as-of, cost/provenance/freshness. Include requested→effective resolution and unpriced reason on drilldown.
- Codex: extend the reviewed renderer's documented view selection and R4 provenance mechanism; do not invent unsupported flags. Detail is native user-shell text, not a footer or MCP/model call.
- Claude: actual statusline reads the selected synthetic projection/statefile. **Do not sum statusline current-context fields.** Usage detail is shared text/harness or future skill output; no invented native panel. Three native statusline states can carry usage progress.
- Pi: deterministic `/forge-demo usage` opens a bounded usage detail component; maintain selection/scroll/back/close/focus behavior and TUI guard. Do not replace existing evidence detail or host shortcut handling.
- Companion uses the **same normalized usage projection**, not separate ad-hoc cost math. Clearly remains companion.
- Existing generic `Cost:not measured` must not contradict the new estimate: label **Actual charge:unknown** separately if shown; CPU/Memory/Elapsed remain not measured unless existing synthetic source explicitly says otherwise. Do not invent real resource metrics.

### D. Required lightweight tests

1. Exact arithmetic above (no per-event display rounding accumulation).
2. OpenAI input includes cache; Anthropic/Pi ordinary excludes it; reasoningsubset counted once;1h writes counted within totalwrite, not on top.
3. Unknown versus explicit measured0; missing writeTTL with nonuniform rates stays unpriced/partial; malformed negative/subset-inconsistent/oversized values safe.
4. Partial→final replacement, final duplicate, duplicate observer, inclusive parent+child coverage and distinct retry.
5. Cumulative snapshot/resume/checkpoint/reset; gap/out-of-order/conflict/revoked-source states and same reconciliation snapshot no increment. **File-backed tests must append, flush, close, reopen in a fresh process and verify identical totals; then reconcile a final observation without recharging earlier partials.** Test truncated-tail/malformed-middle/size bounds and completed-run history/export with prior bytes retained.
6. Requested/effective mismatch or ambiguous alias is unresolved unless an explicit fixture mapping proves it. Unknown provider/SKU/tier does not borrow another rate.
7. Rate snapshot immutability and newer catalog affects only newly eligible observations; separate actual-chargeunknown.
8. Phase/actor/model totals sum once to whole-run known subtotal/coverage; telemetry failures do not change gate outcomes, ceilings, previewauthority or dispatch.
9.80/120-width/plain output, full labels and no false0; Pi usage keyboard/rowbounds; Claude long fallback stays safe afterR2; Codex native/harness provenance afterR4.
10. All existing semantic/native-contract/browser tests remain green. Update only tests that deliberately assumed **all** cost/token fields unavailable; keep CPU/memory and realcharge assertions unknown.

### E. Evidence to regenerate / report

Regenerate affected raw/harness/companion captures after the final immutable commit. Add clearly labelled supplemental usage harness/detail images and list exact names for later asset binding. No screenshot is native merely because the renderer's presentation flag says native.

Parent will obtain3+ actual native frames perhost with usage updates, plus reserved supplemental slots:
- `codex-native-0.153.4-usage.png`
- `claude-native-2.1.302-usage.png` (real statusline usage state)
- `pi-native-0.80.7-usage-detail.png`

Native data remains fixtures; production collector/model pricing integration is future issue work. Report source changed, exact test results, full revised CLI grammar, entrypoints, screenshot provenance and known remaining gaps. Do not run or publish an actual delivery.

## Producer integration explicitly deferred

The proposed **“Track per-model tokens and live USD API-equivalent estimates”** issue (GitHub number pending publication) requires future harness-wide producer instrumentation at agent-adapter/model-invocation boundaries: planning/orchestrator/context-preparation model calls, workers/repair/independent review, retries and observable failed/cancelled operations; Codex app-server/Pi final-message/Claude result-or-OTel adapters; run/phase/task/attempt/invocation/provider/model correlation; durable multihost per-run ledger, pause/resume/restart, completed history/export and a real price catalog. This is required production work, not optional UI polish. The synthetic prototype demonstrates the presentation and accounting semantics without authorizing those core edits. It does **not** attach to existing sessions, read credentials/raw conversations, enable telemetry, inspect billing accounts or invoke models.
