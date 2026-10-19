# [UX/Telemetry] Track per-model tokens and live USD API-equivalent estimates

> **Implementation-ready issue plan, pending GitHub issue creation and URL binding.** Synthetic prototype update and future live producer wiring are separate acceptance stages. No production telemetry/billing or native screenshot pass is implied.

## Outcome and ownership

**Required: harness-wide durable telemetry throughout Delivery Forge runs**, not current-chat/statusline counters. Instrument the agent-adapter/model-invocation boundary across planning, orchestrator/coordinator, model-based context preparation, workers, repairs, independent reviews, retries and observable failed/cancelled operations. Record run/phase/task/attempt/invocation/model/provider plus parent-child correlation. Persist incremental observations→final reconciliation across pause/resume/restart, retain completed-run history and provide bounded export. The UI projects the ledger live; it is never the sole keeper or accounting authority. Optional/unreported external usage makes coverage partial rather than silently disappearing.

One shared normalization/ledger/pricing boundary prevents each host from calculating a different bill. Non-model retrieval is not fabricated model usage. Actual production producer wiring is required by this issue but **not authorized by the current prototype/design task**; current PR32/core work remains untouched.

This is a proposed standalone telemetry issue with distinct producer/accounting ownership, dependent on the shared presentation contract and consumed by all three existing adapters. Its GitHub number is pending publication; existing GitHub issue #7 is unrelated. No further issue per model, rate or screenshot is needed. This does not restart existing runtime work.

## User-visible meaning

Always label **USD API-EQUIVALENT ESTIMATE**. This compares observed token work to an explicit API rate basis; it is not an invoice, actual charge, subscription marginal spend or credit consumption. Actual charge is a separate nullable authoritative-billing receipt, never inferred from a CLI's estimated cost.

Unknown model/rate/category/usage is **unknown**, not0. If some work is priced, show a **known subtotal + unpriced/missing coverage**; do not call the subtotal a complete run total. Explicit measuredzero is distinct from absent/defaultzero. Provider-partial snapshots and host estimates are marked provisional; provider-final reconciles rather than adds a second charge. CPU/memory remain unknown. Telemetry quality alone does not alter gate outcome, authority, worker dispatch or model choice.

## Grounded producer contracts / profile admission

| Source | Observed schema/semantics | Accounting constraint |
|---|---|---|
| Codex0.153.4 app-server, pinned `3d2ee51…` | `thread/tokenUsage/updated`: threadId/turnId; `tokenUsage.total` and `last`; input/cachedInput/cacheWriteInput/output/reasoningOutput/total counters | Total is cumulative; last is already included. Resume replays previous usage. Notification itself lacks effective model/provider request ID/tier. Join only trusted metadata; ambiguous attribution stays unknown. Context-fill fallback is not priced usage. |
| Claude2.1.302 statusline | Current-context/latest-response token fields; session estimated `cost.total_cost_usd` | Never sum repeated footer samples or treat context occupancy as cumulative consumption. |
| Claude stream-json/SDK documented result | Main-loop `usage` differs from cumulative inclusive `modelUsage`; model map includes input/output/cache/read/write, optional thinking/canonicalModel/provider/costBasis | Latest cumulative result replaces earlier; resume can include restored history; inclusive parent already includes subagents/compaction. Helper calls may be excluded; scope visible. Thinking inside output. Version-admit optional fields, not assumed SDK installation. |
| Claude documented OTel API events/metrics | Request/client IDs, model/query_source/input/output/cache, speed/effort; metrics have explicit delta/cumulative temporality | Ordinary input excludes caches; metric temporality/process epoch matters. Errors do not expose every intermediate retry. No exporter/settings activation in demo. |
| Pi0.80.7, pinned `818d674…` | AssistantMessage provider/api/model, optional responseModel/responseId; usage input/output/cacheRead/cacheWrite/cacheWrite1h?/reasoning? | Pi normalizes input to ordinary/exclusive; totalinput=input+read+write. reasoning⊂output; write1h⊂write. Final-message usage and session totals overlap; all-entry session history includes past/compacted branches. |

Declared source semantics do not prove actual installed telemetry wiring. Select one supported read-only route per accounting domain; no prompt-content scraping, guessed hidden host API or broad “unsupported” claim when a richer field is missing. Other providers require separate mapping profiles.

## Proposed read-only contract and ledger

`forge.usage/0.1` carries bounded metadata only: observation/delivery-event/revision/sourceprofile/epoch/sequence; run/phase/task/actor/role/host/surface; provider/backend/opaque accounting scope; requested/effective/canonical model and resolution proof; request/response/attempt/invocation/incurrence identity and parent-child correlation; counter kind and inclusive coverage; nullable integer counts with presence/meaning; provisional/final state; immutable pricingSnapshotId; completeness/freshness/provenance and unpriced reason. **Invocation/billing identity is separate from event delivery identity**: many incremental/mirrored events can describe one incurrence, and a new actual retry attempt can incur additional cost. No prompt/response/thinking text, secrets or arbitrary local paths.

Normalized categories: **inputTotal = ordinary + cacheRead + cacheWrite**, outputTotal already includes reasoning. Preserve write-retention subclasses and raw numeric mapping version. Reject negative/subset-inconsistent/oversized data; do not clamp a bad receipt into plausibility. Presentation gets a summary/drilldown, not a writable authority object.

Ledger rules:
1. One actual incurrence contributes once. Provider/host/adapter/Switchyard mirrors correlate through trusted request/coverage identity; equal tokens/time is not dedupe proof. Without correlation select a primary source or mark overlap unknown.
2. Inclusive parent aggregates and child observations cover the same work; use a contribution map, not addition. When allocation is unavailable, display unallocated scope rather than invented worker splits.
3. Partial snapshot→final replaces that request contribution; explicit corrections retain before/after audit. Distinct incurred retry adds measured usage even on failure; replayed observation does not. Unknown failed-attempt usage remains unknown.
4. Cumulative snapshots replace/checkpoint per model/scope/epoch; never sum every total or total+last. Resume restores high-water marks/dedupe keys. Decrease/reset/gap/model switch requires reconciliation, not negative spend or silentzero.
5. Older out-of-order revisions cannot overwrite final; conflicting duplicates quarantine. Revoked/disconnected source preserves accepted history but makes freshness/coverage incomplete and cannot keep feeding trusted live events.
6. Durable production ledger/checkpoint retains incurrence/coverage/rate identity across pause/resume/restart and fork; completed-run history/export remains readable without an attached UI. Imported historical messages are baseline, not new current-run spending. **Prototype must exercise actual append/flush/reopen/replay of a bounded synthetic JSONL ledger**, not only serialize an in-memory object or screen values. Detect malformed/truncated records, preserve original bytes and mark incomplete coverage instead of silently skipping/overwriting evidence.

## Production implementation work packages (required, separately authorized)

1. **Core run lifecycle/invocation boundary:** establish trusted run/phase/task/actor/attempt/invocation and parent-child metadata before dispatch; include planning/context-model calls and all producer roles, not just implementation workers. Do not assume one host turn equals one provider invocation.
2. **Host adapters/producers:** map supported Codex/Claude/Pi observations to incurrence identity, optional effective model and partial/final usage; instrument success/failure/cancel paths. Existing CodexAppServerPort/ClaudeStreamPort/PiRpcPort/OwnerHostBridge are candidates to inspect at the then-accepted source head, not permission to modify today's PR32.
3. **Events/persistence/recovery:** append durable observations and explicit corrections, dedupe/reconcile cumulative and per-request sources, checkpoint/reopen safely across pause/restart, preserve uncertain cancelled/retried work. UI absence cannot stop accounting.
4. **Aggregation/history/export:** per-model and run totals with phase/task/actor/attempt drilldown, inclusive coverage/unallocated buckets, completed-run history and bounded content-free export.
5. **Pricing:** immutable versioned catalogs, observed/effective applicability, exact arithmetic, unknown rates and separate actual-charge receipts.
6. **UI projection:** subscribe/read durable summaries with freshness/completeness, never maintain a competing authoritative ledger; test all three native renderers.

These tasks belong to the proposed **“Track per-model tokens and live USD API-equivalent estimates”** issue and integrate with the shared contract/adapter issues. They are **end-to-end instrumentation requirements**, not optional future polish. This plan starts no live/core implementation.

## Pricing and cost formula

Each observation freezes a versioned price snapshot: exact provider/backend/SKU, currency/unit, category rates, context boundary/tier/speed/batch/region/cacheTTL applicability, reasoningmode if relevant, sourceURL/retrievedAt/contenthash, provider-declared effective interval when known and adopted/as-of time. If historical effective date is absent, say unknown; retrieval date is not fabricated provider-effectiveFrom.

Use exact decimal/rational arithmetic; round only for display:

`USD = (ordinaryInput*rInput + cacheRead*rRead + Σ(writeClass*rWriteClass) + outputTotal*rOutput)/1e6`.

- OpenAI raw inclusiveinput must subtract read/write once to find ordinary. Anthropic/Pi ordinaryinput already excludes them. Reasoning is not billed a second time.
- Cachewrite costs are not uniform:5m/1h and model/provider differ. Missing necessary split yields partial/unpriced, not an assumed5m/default multiplier.
- Current official OpenAI page checked2026-10-19 **lists gpt-6-astra**; nevertheless a user alias “Astra” is not sufficient SKU/effective-model evidence. No invented pricing for unknown aliases/models. Exact model names, rate tiers and known context applicability must match, or an explicitly labelled comparison-basis assumption is required.
- Current catalogs include multiple service modes and model-specific cache discounts. Never infer Batch/Fast from subscription plan, hardcode all cachehit rates0.1×, or borrow first-party rates as a claim of Bedrock/Vertex charges.
- CLI costUSD/list/managed estimates are retained with provenance, not relabelled authoritativecharges. Tool/search/storage/multimodal/fixed fees/tax/discounts are excluded unless separately admitted; label scope **token API-equivalent**.
- Later catalog revisions never silently reprice history. Explicit comparison/repricing is a separate series/auditable correction. USD remains canonical; optional FX is separately sourced/timestamped conversion.

Official sources are evidence for the future price catalog, **not live rates automatically downloaded by prototype code**.

## Prototype slice and display

Normative detailed design: [`usage-cost-design.md`](usage-cost-design.md); bounded prototype guidance: [`usage-implementation-notes.md`](usage-implementation-notes.md). Immutable source and evidence are bound in the [publication manifest](publication-manifest.md).

Synthetic fixture models/rates demonstrate:
- writer inclusiveinput10000/read6000/write1000/output2000(reasoning500), demo rates2/0.2/2.5/10 →USD0.029700;
- reviewer ordinary1000/read4000/write500(3005m+2001h)/output400(reasoning100), rates3/0.3/3.75/6/15 →USD0.012525;
- distinct writerretry500input/100output →USD0.002000;
- **known subtotal0.044225**, known tokens18500; add unresolved coordinator700input/100output →19300observedtokens and **PARTIAL whole-run cost**,1unpriced record.

Writer partialoutput1200 costs0.021700 then final replaces it; duplicates/resume/mirrors addnothing. **Persist these inert records in a real append-only JSONL demo ledger**, flush/fsync committed lines, close/reopen in a fresh process, replay/reconcile and expose completed synthetic-run history/export. Bound1000records/16KiB perrecord/1MiB total; one explicit owned demo directory/writer, no real host transcripts or prompt content. Truncated tail/malformed middle must preserve bytes and report partial/blocked reconciliation. Screens derive from reopened ledger state, not hardcoded cost labels. All values/rates marked SYNTHETIC, no real provider stream. Gate outcomes and authority unchanged.

Compact80-column row: `USD API-EQUIVALENT ESTIMATE ~0.0442 | demo.writer | PARTIAL`. Preserve identity/blocker/nextaction;4–5lines, max6when wrapping. Parallel models saymultiple; idle showslast/none, not fake activity.120-column detailtable;80-column stackedmodelcards include ordinary/cache/total/output/reasoning, rateversion, cost, coverage/freshness and phase/actor/request drilldown. Plain text/a11y, no animation dependency.

Codex uses supported native text, Claude actual statusline, Pi widget plus deterministic usage-detailcommand. No invented native panels, paid skillinvocation or provider probe. Three+ nativeframes eachhost must include updated usage state; supplemental pending slots:
- `codex-native-0.153.4-usage.png`
- `claude-native-2.1.302-usage.png` (statusline)
- `pi-native-0.80.7-usage-detail.png`

Parent binds actual immutable assets after source/visual/native review. Native fixture rendering is not production telemetry acceptance.

## Acceptance matrix

- [ ] OpenAI inclusive and Anthropic/Pi exclusive mappings, optional reasoning/cache presence and TTLsubsets verified from versioned numericfixtures.
- [ ] Exact synthetic calculations above; no double reasoning/cachewrite or rounding drift; unknown distinct from measured0.
- [ ] Partial→final, duplicate/mirror, parent-child coverage, distinct retry and importedhistory each counted correctly.
- [ ] Cumulative epoch/resume/reset/gap/out-of-order/conflict/revokedsource cases reconcile without silent newcharges/losthistory.
- [ ] Actual synthetic JSONL append→flush→close→fresh-process reopen→replay→final reconciliation produces identical once-counted totals; truncated/malformed/over-limit records fail visibly with prior bytes retained. Completed synthetic-run history and bounded metadata-only export work after UI restart.
- [ ] Effective-model ambiguity, mixed-model interval and unknownSKU/tier/rates stay unresolved; rateversions freeze; repricing/FX remains explicit.
- [ ] Per-model/phase/actor totals reconcile with declared whole-run coverage, including unallocated/unpriced buckets.
- [ ]80/120/plain/native layouts remain readable and do not hide failure/authority; syntheticlabels and separate actualchargeunknown visible.
- [ ] Final prototype tests/source/harness review and3+nativeframes eachhost, includingusageupdate; no forbidden effects.
- [ ] **Required production stage:** end-to-end instrumentation actually covers planning/orchestration/context-model calls/workers/repair/independent review/retries and observable failed/cancelled operations through admitted core/host-adapter producers. Persisted accounting survives pause/resume/restart without UI; history/export/correlation/coverage/privacy proven from authorized no-content observations. External/unreported usage is explicitly partial. Synthetic tests alone cannot check this box.
- [ ] API-equivalent estimate and authoritativeactualcharge are never conflated. No automatic budget enforcement/modelrouting added.

## Dependencies / rollout / risks

Common contract supplies trusted run/actor/source projection; this issue owns one usage boundary; three adapters render it; acceptance issue independently verifies. Existing #8/#11/#12 and paused#17/PR32 remain unchanged. Switchyard#30 is optional and, if used, a correlated observer—not another additive bill. No new live dependency required for demo.

Rollout: synthetic pureledger+UI → separately approved read-only producerprofile → durable multihost reconciliation → optional authoritativebilling adapter only with its own access/receipt. Main risks are double counting, context-versus-consumption confusion, falsezero, alias mispricing, rate drift and privatecontent leakage. Mitigate with explicit semantics/provenance and unknownstates, not more dialogs or hidden fallback pricing.

## Primary sources

- Codex schema: https://github.com/openai/codex/blob/3d2ee51ca2d5db578f328aa75e20aa22c0197c9a/codex-rs/app-server-protocol/schema/typescript/v2/TokenUsageBreakdown.ts ; same directory ThreadTokenUsage/ThreadTokenUsageUpdatedNotification.
- Codex replay: https://github.com/openai/codex/blob/3d2ee51ca2d5db578f328aa75e20aa22c0197c9a/codex-rs/app-server/src/request_processors/token_usage_replay.rs
- Claude: https://code.claude.com/docs/en/statusline.md ; https://code.claude.com/docs/en/agent-sdk/typescript ; https://code.claude.com/docs/en/monitoring-usage.md ; https://platform.claude.com/docs/en/build-with-claude/streaming
- Pi types: https://github.com/earendil-works/pi/blob/818d67457cdd6b60bce6b121d16b23141c252dd8/packages/ai/src/types.ts ; mappings: same ref `packages/ai/src/api/openai-responses-shared.ts`, `anthropic-messages.ts`; session aggregation: same ref `packages/coding-agent/src/core/agent-session.ts`.
- Official pricing/semantics checked2026-10-19: https://developers.openai.com/api/docs/pricing ; https://developers.openai.com/api/docs/guides/prompt-caching ; https://developers.openai.com/api/docs/guides/reasoning ; https://platform.claude.com/docs/en/about-claude/pricing .
