# UX-1.2 — model usage and live API-equivalent cost telemetry

**Astra/xhigh design addendum, 2026-10-19.** Explicit new user requirement: track token usage by model and display/track equivalent API usage costs live in the Forge UI. This supersedes “all token/cost measurements unavailable” for **clearly synthetic prototype telemetry** and defines the future read-only producer integration. CPU/memory and other unmeasured resources remain unknown. It does not authorize paid probes, model calls, telemetry export changes, credentials, production producer wiring or delivery effects.

**User clarification — harness-wide durable accounting:** tracking is required throughout Delivery Forge runs, not merely in the current chat/statusline. The harness owns instrumentation, durable observations, reconciliation, completed-run history and export; the UI is a read-only projection, never the sole keeper. The demo must persist/reopen/replay an actual bounded append-only **synthetic JSONL ledger**, not only store costs in screen fixtures. This clarification supersedes the earlier in-memory-only prototype allowance.

**Separate from R1–R4:** this is an additive presentation/fixture slice after those repairs, not a redesign of the existing projection contract or authority model.

## 1. Product contract

Show whole-run and per-model usage/cost, with phase/task/attempt/invocation and actor drilldown: planning, orchestrator/coordinator, model-based context preparation, each worker, repair, independent review, retries, compaction/auxiliary and failed/cancelled operations where usage is observable. Non-model context retrieval is not invented token usage. Optional external/unreported model work marks coverage partial rather than pretending the harness accounted for it. The person should understand **how much observed work each model did, what it would cost at an explicit API rate basis, how complete the observation is, and whether it is changing**.

The cost label is **USD API-EQUIVALENT ESTIMATE**. It is **not** a subscription bill, marginal subscription spend, invoice, credit consumption or actual charge. API-equivalent rates do not make subscription execution API-billed. Actual charge is a separate nullable field shown only with an authoritative provider billing receipt and explicit scope; a CLI's `costUSD`/`total_cost_usd` is not that receipt.

No data/rate/model match → **unknown**, not0. Known observed zero is permissible only when explicitly measured with complete relevant coverage. Before producers attach, “Usage unavailable” is correct. A known-cost subtotal with unpriced/missing usage is **PARTIAL: USD0.0442 known;1 unpriced request**, never an exact whole-run total. No hidden assumption that absent cache/reasoning fields mean zero.

Live means “updated when admitted observations arrive,” not fake spinning counters or an assertion every host emits token-by-token cost. Provider partial snapshots, host estimates and provider-final usage are visibly distinct. Demo playback demonstrates these transitions with **SYNTHETIC TOKENS + SYNTHETIC RATES**, never claims actual production streaming.

## 2. Targeted source verification (no model probes)

Exact source excerpts and hashes were retained during design research but are not included in this sanitized bundle. Rolling official pages were checked 2026-10-19 approximately 10:14–10:18 UTC. Preflight targets were Codex 0.153.4, Claude Code 2.1.302 and Pi 0.80.7; installed capture observed Claude Code 2.1.303. Source declarations and native fixture rendering do not prove production telemetry events.

### Codex CLI/app-server0.153.4 — pinned `3d2ee51ca2d5db578f328aa75e20aa22c0197c9a`

Public generated v2 schema:

```text
ThreadTokenUsageUpdatedNotification = {threadId,turnId,tokenUsage}
ThreadTokenUsage = {total,last,modelContextWindow}
TokenUsageBreakdown = {totalTokens,inputTokens,cachedInputTokens,
                      cacheWriteInputTokens,outputTokens,reasoningOutputTokens}
```

`TokenUsageInfo.append_last_usage` adds the last response into total and replaces last; **do not sum successive total snapshots or add total+last**. `token_usage_replay.rs` explicitly says restored usage is lifecycle replay, not a new model event. Resume must reconcile a prior high-water mark/epoch rather than bill it again. Core also has `fill_to_context_window`, which can synthesize total/context counts; **context-fill or inconsistent breakdown is not monetary usage proof**. Validate coverage and component consistency.

The public notification has **no effective model, provider response ID, rate tier or token-cache TTL field**. Join model/provider/actor to trusted turn/port metadata only where attribution is established; an ambiguous model switch inside a cumulative interval remains unknown/unallocated. Source `TokenUsageRecord` has per-response IDs and usage internally, but its existence is **not a claim this record is publicly exposed by v2**. Prefer admitted request-level producer observations when available; otherwise an explicitly scoped cumulative cohort.

For OpenAI Responses semantics, total input includes ordinary+cache-read+cache-write; `cachedInputTokens` and `cacheWriteInputTokens` are subsets, not additional input. `reasoningOutputTokens` is a subset of output. Rate model/type applicability still matters. Missing/legacy-default cache-write0 is not automatically authoritative absence for every model/profile.

Native CLI footer/context counters are presentation, not a second additive accounting source. The fixture `!` command makes no model call and contributes **no real token telemetry**.

### Claude Code CLI 2.1.303 actual capture / 2.1.302 preflight / stream-json / Agent SDK

Official statusline docs distinguish:
- `cost.total_cost_usd`: client-side session **estimate**, list or managed pricing; `/clear` resets; not invoice.
- `context_window.total_input_tokens`, `total_output_tokens`, `current_usage`: **latest context/response**, not accumulated run usage. Combined input includes cache components. Polling the statusline repeatedly must not add them.

Official SDK result docs distinguish:
- `usage`: main loop only, per-turn in streaming-input mode, excludes subagents/auxiliary calls.
- `modelUsage`: per-model cumulative totals across query-pipeline calls, including subagents/compaction/Workflow agents; restored prior totals on resume; **latest result replaces earlier result, not summed**. Helper calls outside that pipeline can be excluded. `total_cost_usd` covers the same cumulative scope and is explicitly an estimate.
- `ModelUsage`: `inputTokens`, `outputTokens`, optional `thinkingTokens`, `cacheReadInputTokens`, `cacheCreationInputTokens`, `costUSD`, `canonicalModel?`, `provider?`, `costBasis?` (list/managed/unknown). Thinking is already included in output. Canonical/provider documented since2.1.218, costBasis2.1.246; optional thinking requires SDK0.3.257+ and can be partial after old-session resume. **Declare missing optional fields unknown; do not assume the SDK is installed/tested.**

Documented OTel `claude_code.api_request` offers request/client IDs, model, query_source, input/output/cache counters, speed/effort and estimated cost; input excludes cache-read/write. Metrics can be **delta or cumulative** (documented default delta); use actual exported temporality/start time, not metric name. `event.sequence` is process-local, so pair with process epoch. `api_error` does not enumerate every intermediate retry: unobserved incurred work is not known-zero. No telemetry settings/exporter are enabled by this demo. No raw request/response bodies or prompt logging are needed.

Anthropic `message_delta.usage` counts are **cumulative within the message**, not increments. `input_tokens` is ordinary input, with `cache_read_input_tokens` and `cache_creation_input_tokens` separate. Cache creation may split5-minute/1-hour buckets; if TTL/rate split is unavailable and costs differ, exact estimate is unknown/partial rather than assuming all5m. `output_tokens` includes thinking when a thinking breakdown exists. Match `canonicalModel`/effective request model rather than user alias alone. Agent correlation should use actual stable agent ID; tool-use parent references can change on resume and are not themselves request cost IDs.

### Pi0.80.7 — pinned `818d67457cdd6b60bce6b121d16b23141c252dd8`

`AssistantMessage` includes `provider`, `api`, requested `model`, optional `responseModel`, `responseId`, `usage`, stopReason/timestamp. `Usage` has `input`, `output`, `cacheRead`, `cacheWrite`, optional `cacheWrite1h`, optional `reasoning`, `totalTokens` and host-computed `cost` components. The type explicitly says reasoning is a subset of output; cacheWrite1h is a subset of cacheWrite.

Pinned OpenAI Responses mapping **subtracts cache reads/writes from raw inclusive input** into Pi `usage.input`; Anthropic mapping carries base input separately. Thus Pi's normalized total input is **input+cacheRead+cacheWrite**, not raw OpenAI input semantics applied a second time. Pi provider mappings can default omitted fields to0, so the future adapter needs producer completeness metadata/raw-field presence where available. Do not promote a host default0 into measured-zero certainty.

Consume final assistant `message_end`/RPC message events only through admitted metadata extraction, keyed by session-entry/response identity. Intermediate message snapshots replace the same request observation. `getSessionStats()` sums assistant usage across **all persisted session entries**, including compacted/other-branch history, rather than current context. It is not another additive source alongside those messages and not a per-model ledger by itself. Imported fork/resume history is a baseline, not newly incurred usage. Host `usage.cost` is an estimate with its own catalog provenance, not an invoice or necessarily our chosen rate snapshot.

Only OpenAI/Anthropic mapping semantics were source-checked here. Other Pi providers require separate normalization profiles; preserve raw typed counts and show unpriced/unknown rather than treating all providers alike.

## 3. Proposed accounting boundary (read-only)

Add a separate versioned **`forge.usage/0.1`** observation/ledger at the **harness agent-adapter/model-invocation boundary**, with a summarized `usage` section on the presentation snapshot. Instrument the whole run independently of whether any UI is attached. Persist incremental observations, terminal/final reconciliation and unresolved outcomes through pause/resume/restart; expose completed-run history and bounded export. This does not alter gate schema/authority, does not become a controller and does not make budget consumption permission to dispatch work.

Production work must cover run lifecycle/bootstrap ownership; adapter/invocation producer hooks; planning/coordinator/context-model/worker/repair/reviewer/auxiliary attribution; event identity and delivery correlation; durable append/checkpoint/replay; pricing; aggregation/export; and UI subscription/projection. Existing source classes such as CodexAppServerPort, ClaudeStreamPort, PiRpcPort and OwnerHostBridge are integration candidates **to inspect at the accepted implementation head**, not authorization to edit current PR32 or assume every provider exposes individual requests. A host turn may contain multiple actual model attempts; retain scoped aggregate/unknown attribution when per-invocation IDs are unavailable.

Observation metadata (bounded allowlist; no content):
- `observationId`, schema/source profile/version, `sourceEpoch`, sequence, observedAt/receivedAt; authenticity/provenance reference and current source authorization state.
- Forge run/phase/actor ID and role, parent actor, host/surface/version, adapter/source route, provider/backend, opaque account/counter scope. Actor labels are trusted orchestration metadata, not inferred from conversation text.
- Requested model string, reported effective/canonical model, resolution evidence/version, pricing SKU or unresolved reason. Alias mapping is scoped to provider/backend/time, not global string substitution.
- Provider request/response ID when present, task ID, logical operation ID, **attempt ID and invocation ID**, parent invocation/actor IDs, session/thread/turn/message-entry IDs as available; `incurrenceId` assigned by the trusted producer mapping. Keep **invocation/billing identity separate from observation/event delivery ID**: multiple observations may describe one incurred request, while a new actual retry attempt can incur additional usage.
- `counterKind`: request-snapshot / cumulative-cohort / delta; scope includes whether children/auxiliary calls are included; cumulative start/epoch/baseline and coverage identity. Never infer semantics from field names.
- `stage`: host-estimated / provider-partial / provider-final / unknown; revision/correction reference; counts as nullable exact integers and field-presence/meaning metadata.
- `inputTotal`, `inputOrdinary`, `inputCacheRead`, `inputCacheWrite` with retention/class breakdown; `outputTotal`, `outputReasoningSubset`; cached/reasoning quantities are subsets of these normalized totals. Keep original numeric categories and mapping version for reproducibility, but no prompt/text/thinking content.
- `pricingSnapshotId`, cost basis/assumptions, included/excluded dimensions; separately nullable authoritative actual-charge receipt. Freshness, completeness, gaps/conflicts and unpriced reasons propagate to summary.

Require nonnegative safe integer counts, subset consistency, known category semantics, bounded cardinality/size and no uncontrolled identifiers/paths. Use decimal/rational arithmetic for prices; round only for display, never each event. Avoid binary-float monetary drift. A valid partial observation can contribute a labelled known subtotal while the complete total remains unknown.

## 4. Idempotency, retries, hierarchy and restart

**One incurrence, one contribution.** Host, adapter, parent aggregation and optional Switchyard may all observe the same request. Choose one admitted accounting authority per disjoint scope. A second route is corroboration, not additive spend.

- Canonical request key uses provider/backend/account scope + trusted provider request/response identity where available, with explicit host mapping. Request IDs are not assumed globally unique. Without shared IDs, do not dedupe by equal tokens/model/time; identical legitimate requests exist. Keep scopes separate/unknown until correlated, or select one source and mark other coverage diagnostic.
- Parent inclusive totals and child request rows are alternatives for the same covered work. Never sum both. Maintain a coverage/contribution map. If only an inclusive aggregate is available, show its known total and **unallocated** drilldown rather than inventing worker splits; unexplained overlap stays partial.
- A retry with a new actual provider attempt is additional incurred usage even if failed/aborted. A duplicate event/replayed result is not. Unknown failed-attempt usage remains unknown, not0; no model probe to fill it.
- Request streaming snapshots **replace the same request's prior provisional contribution**. Provider-final reconciles to the final counters; append a correction/revision with auditable before/after, not a second charge. Counts may revise downward with explicit correction; do not hide it.
- Cumulative cohort: store last accepted vector per model/counter scope and source epoch. New cumulative value is a replacement/checkpoint or validated delta against that high-water mark, not a new whole contribution. Never add `last` to a cumulative `total`. Reset/decrease, model change or epoch discontinuity requires reconciliation/baseline; no negative automatic charges or silent zero reset.
- Repeated observation IDs identical→idempotent; conflicting duplicates→quarantine/uncertain. Out-of-order older revisions do not overwrite final/current state. Gaps mark incomplete until complete scoped snapshot/request reconciliation proves coverage.
- Durable per-run ledger/checkpoint retains dedupe keys, source epochs, contribution coverage and immutable price snapshots across restart. Fork/resume/import history belongs to its original incurrence/baseline; only newly observed work assigned to the current run adds cost. Presentation branch navigation cannot erase incurred work.
- Revoked/disconnected source cannot keep supplying trusted “live” updates. Preserve accepted historical observations, mark freshness/coverage revoked/stale, reject new untrusted events, and reconcile before declaring a complete total. Do not silently delete costs because a source disappeared.

Production instrumentation/durable multihost collection remains required future issue work, but **the prototype must now implement actual synthetic persistence**: an explicitly owned demo-only append-only JSONL ledger, reopened by a fresh process/reducer, incremental→final reconciliation, pause/restart replay, completed synthetic-run history and bounded metadata-only export. The screen is derived from that ledger; losing/restarting the UI must not lose usage. No real model calls, host transcript reads or production collector are authorized.

Demo storage bounds: at most1000 records,16KiB per record and1MiB total (stop with explicit capacity/partial status before exceeding); bundled synthetic observations/rates only, explicit owned demo directory, no credential/raw-content paths. Each committed line has a stable observation identity and ledger sequence; append and flush/fsync before acknowledging durability. Preserve prior records, dedupe at projection, and record corrections as new records. A truncated/uncommitted tail or malformed middle record is detected and reported; do not silently skip/overwrite/truncate it or claim complete recovery. Reopen retains the last valid committed facts with coverage uncertain until explicit reconciliation. Checkpoint/export is not a replacement for the source ledger. Concurrent writers are outside this bounded prototype: one writer, fail explicitly if ownership is unavailable.

## 5. Pricing, identities and arithmetic

Each immutable price snapshot records: provider/backend + exact SKU, currency USD, per-million-token unit, category rates, tier/speed, context threshold and inclusivity, cache retention/class, batch applicability, reasoning-mode applicability if any, region/data-residency, source URL + retrievedAt + content hash, catalog version, **provider-declared effective interval when known**, and adopted/as-of time. If the public page does not state a historical effective date, record that as unknown; retrievedAt is not fabricated provider-effectiveFrom. Do not apply current rates to older events as historically exact.

Public official catalogs were checked—not used to bill anything:
- OpenAI current pricing **does list `gpt-6-astra`** and GPT5.6/6 variants; this is not permission to map a casual “Astra” alias to that SKU. Exact effective-model/provider evidence is still required. Current tables distinguish cache writes, short/long context (≤272K/>272K input), Standard/Batch/Flex/Fast/Ultrafast and regional uplifts. Never assume one global multiplier or a subscription label implies Fast/Batch.
- Anthropic current catalog distinguishes5m/1h cache writes, model-specific cache-hit rates and tier/region/cloud platform differences. Cache hits are not uniformly0.1× across every current model. Claude CLI/SDK `costBasis:managed` means a different configured table may apply; preserve it as a separate host estimate unless the table is admitted into our price catalog.
- Unknown SKU/provider/tier/context/cache split/rate→unknown or explicit partial valuation. Do not substitute a familiar model or a rate from another backend. A clearly selected **comparison basis** such as “first-party Standard equivalent” may be displayed even for subscription execution, but it is an explicit assumption in the label/provenance, not a claim of actual served tier or charge.

Canonical formula when categories are exclusive and known:

`USD estimate = (ordinaryInput*rInput + cacheRead*rRead + Σ(cacheWriteClass*rWriteClass) + totalOutput*rOutput) / 1,000,000`

OpenAI inclusive input: ordinary = inputTotal−cacheRead−cacheWrite (validate nonnegative; no silent clamp). Anthropic/Pi ordinary input is already exclusive; inputTotal is ordinary+read+write. Reasoning is included in output and **never billed again**. CacheWrite1h is part of write total:5m=writeTotal−write1h only if that split is authoritative. No-cache-write SKU may have an explicit not-applicable category; absence is not generally equivalent.

Provider tools/search/storage/image/audio/fixed charges, taxes and discounts are outside **token API-equivalent** scope unless separately admitted and priced. Make exclusions visible; don't call a token subtotal a complete API invoice. Default USD. Optional FX is a separate display conversion with rate/source/as-of and original USD retained, never silently rewrite ledger currency.

Freeze the chosen rate snapshot on each priced observation/revision. A later catalog update applies to future eligible observations. An explicit “reprice comparison at catalogX” produces a separate comparison series; original estimates remain reproducible. Final tokens reconcile using the original rate basis unless a documented price-selection error is corrected with an audit record.

## 6. Native UX

Keep run identity, blocker and safe next action ahead of spend. Compact row at80 columns:

`USD API-EQUIVALENT ESTIMATE ~0.0442 | demo.writer | PARTIAL`

A persistent DEMO/SYNTHETIC marker identifies the fixture. Target4–5 short persistent lines, at most6 when identity wraps; this deliberately relaxes UX-1's3–4-line target to fit the explicit user requirement. Never let usage obscure INCOMPLETE/cleanup UNKNOWN. Current model means current attributed actor/model; parallel work says **multiple models**, not a misleading single active model.

Expanded usage view:
- Whole run: known cost subtotal/full-or-partial, observed tokens, final versus provisional, fresh/stale, unpriced records and coverage description.
- By model: provider/effective model; input total with ordinary/read/write breakdown; output with reasoning subset; selected rate/catalog/as-of; estimate; freshness/provenance/completeness.
- Drilldown: phase→actor/role→request/attempt revisions, requested/effective alias resolution, observer dedupe, inherited baseline and retry distinctions. Do not expose prompt contents.
- At120 columns use a concise table; at80 use stacked model cards. Full model/rate IDs accessible rather than clipped. Plain text and screen-reader labels explain subsets/partial; no color-only status or required animation.
- Codex: same read-only renderer in native local-command transcript, explicit usage detail selection through final documented CLI; no fake footer/model turn.
- Claude: add usage row to actual statusline from the explicit demo projection/state pointer. Statusline host JSON is not polled into a cumulative ledger. Skill invocation remains out of the no-model test.
- Pi: widget adds usage row; deterministic `/forge-demo usage` opens a bounded keyboard usage detail panel (proposed command to implement/document). Preserve evidence detail/back and editor focus; no global key takeover.

Unknown/stale usage is **telemetry quality**, with its own freshness/continuity fields; it must not overwrite the workflow's authoritative `source` continuity or gate state. It is not automatically a failed quality gate. No automatic budget stop, model switching, authority escalation or dispatch policy is added. If future spending controls are desired they need separately authorized controller policy, not a UI number.

## 7. Concrete synthetic fixture arithmetic and story

Use clearly fake model/rate identities, e.g. `demo.openai.writer`, `demo.anthropic.reviewer`, `demo.unresolved.coordinator`; **do not copy live rates into the demo as if current invoicing were measured**. All examples below are synthetic USD/MTok rates:

| Fixture | Ordinary input | Cache read | Cache write | Output (reasoning subset) | Rates input/read/write/output | Final estimate |
|---|---:|---:|---:|---:|---|---:|
| Writer attemptA |3000|6000|1000|2000 (500)|2 /0.2 /2.5 /10|USD0.029700|
| Reviewer attemptB |1000|4000|500 total:3005m+2001h|400 (100)|3 /0.3 /3.75(5m),6(1h) /15|USD0.012525|
| Distinct writer retryC |500|0 measured|0 measured|100 (unknown breakdown)|2 /0.2 /2.5 /10|USD0.002000|

Known final subtotal **USD0.044225**. Writer inclusive input is10000, not20000; reviewer normalized total input is5500. Writer reasoning500 is inside output2000, not another charge. Add a fourth unresolved-model record with700 input/100 output and rateunknown: whole-run complete cost becomes **unknown/PARTIAL**, known subtotal remains0.044225.

Playback: launch unavailable → execute writer partial output1200 (same request;USD0.021700) → writer final2000 replaces partial (USD0.029700, not sum) → reviewer and distinct retry bring known subtotal0.044225 → duplicated/mirrored event leaves total unchanged → unresolved model gives partial cost → reconnect gap/stale then same complete snapshot reconciles without charging again. Phase/actor drilldown follows each record. CPU/memory stay unmeasured. Real gate outcomes and authority remain exactly as before.

## 8. Acceptance and handoff

The proposed **“Track per-model tokens and live USD API-equivalent estimates”** issue (GitHub number pending publication) owns normalization, ledger, pricing and producer admission. The shared-contract issue exposes its read-only summary, three adapters render it, and cross-host acceptance tests it. Avoid duplicate usage ledgers per host; no extra per-price or per-model tickets are needed.

Required tests: inclusive/exclusive mapping; cache-write TTL; reasoning subset; unknown versus measured0; partial→final replacement; duplicate/mirror/parent-child inclusive scope; retries; cumulative snapshots/resume/reset/gap/out-of-order/revoked source; model switch/unresolved aliases; immutable price/as-of repricing; exactdecimal arithmetic; **actual JSONL append/flush/reopen/replay in a fresh process, pause/restart reconciliation, truncated-tail/malformed-record/size-limit handling, completed-run history and bounded export**;80/120/plain layouts; privacy bounds; telemetry failure does not change authorization/gates. Production acceptance additionally proves whole-run producer coverage across planning/context/coordinator/workers/repair/review and failed/cancelled attempts, with explicit partial coverage for unreported external usage.

Maintain3+ meaningful native frames **each host** and include an updated usage row/state (execute partial and later final/partial-known). Add supplemental usage drilldown frames:
Accepted native usage assets are `codex-native-0.153.4-usage.png` and compact usage within `claude-native-2.1.303-execute.png` and `claude-native-2.1.303-failed-gate.png`. The current `pi-native-0.80.7-usage.png` is accepted after parent review; additional model rows require native scrolling. Claude has no separate expanded usage capture because that path would require a model-driven invocation.
All are pending, synthetic, exact-source-bound. No production stream/billing claim from screenshots.

The bounded prototype implementation guidance is published as [`usage-implementation-notes.md`](usage-implementation-notes.md). Runtime integration and native-host acceptance remain separate work.

## Primary source URLs

**C1** https://github.com/openai/codex/blob/3d2ee51ca2d5db578f328aa75e20aa22c0197c9a/codex-rs/app-server-protocol/schema/typescript/v2/TokenUsageBreakdown.ts ; same directory `ThreadTokenUsage.ts`, `ThreadTokenUsageUpdatedNotification.ts`.

**C2** https://github.com/openai/codex/blob/3d2ee51ca2d5db578f328aa75e20aa22c0197c9a/codex-rs/protocol/src/protocol.rs ; https://github.com/openai/codex/blob/3d2ee51ca2d5db578f328aa75e20aa22c0197c9a/codex-rs/app-server/src/request_processors/token_usage_replay.rs .

**A1** https://code.claude.com/docs/en/statusline.md — context versus session estimate.

**A2** https://code.claude.com/docs/en/agent-sdk/typescript — SDKResultMessage/ModelUsage; cumulative/restored scopes, thinking subset, canonical model/provider/cost basis.

**A3** https://code.claude.com/docs/en/monitoring-usage.md — api_request/correlation, cache-exclusive input, metric temporality, retry and privacy limitations.

**A4** https://platform.claude.com/docs/en/build-with-claude/streaming — message_delta usage cumulative.

**P1** https://github.com/earendil-works/pi/blob/818d67457cdd6b60bce6b121d16b23141c252dd8/packages/ai/src/types.ts .

**P2** https://github.com/earendil-works/pi/blob/818d67457cdd6b60bce6b121d16b23141c252dd8/packages/ai/src/api/openai-responses-shared.ts ; same directory `anthropic-messages.ts`.

**P3** https://github.com/earendil-works/pi/blob/818d67457cdd6b60bce6b121d16b23141c252dd8/packages/coding-agent/src/core/agent-session.ts (`getSessionStats`, message_end); same pinned package `docs/rpc.md`.

**R1** https://developers.openai.com/api/docs/pricing ; https://developers.openai.com/api/docs/guides/prompt-caching ; https://developers.openai.com/api/docs/guides/reasoning .

**R2** https://platform.claude.com/docs/en/about-claude/pricing .

No paid API call/agent invocation or telemetry exporter activation was used to obtain these sources. Rolling docs/rates can change; retained hashes bind this design's evidence, not future prices.
