# Forge Presentation Contract — proposal v0.1

**Status: PROPOSED / DEMO ONLY.** This does not change existing Forge contract schema 1.0.0, worker envelopes, controller transitions or CLI grammar. It is a versioned view-layer boundary in front of trusted Forge state, deliberately not a second orchestrator.

## Pipeline

`trusted source records + host observations → validation/currentness projection → PresentationSnapshot → host renderer → ActionIntent → trusted controller revalidation → receipt/event → refreshed projection`

Presentation cannot mint leases, override policy, manufacture independence, convert raw tool exit into acceptance, or grant an authorization ceiling. A valid JSON receipt alone is not proof of trusted issuance. Remote side effects stay controller-owned.

## Snapshot shape (normative design, implementation can use equivalent JS objects)

- `schemaVersion: "forge.presentation/0.1"`
- `mode: "demo" | "live"` — prototype **accepts demo only**, rejects live.
- `run`: `id`, `repository`, `planIdentity`, `planRevision`, `policyIdentity`, `baseCommit`, `headCommit`, `treeId`, `patchSha256`, `authorizationCeiling` (`plan|implement|pr|merge`). Store full values; shorten only labels.
- `source`: `snapshotId`, `sequence` (monotonic per run), `observedAt`, `receivedAt`, `continuity` (`complete|gap|unknown`), `freshness` (`current|stale|unknown`), `capabilityIdentity`, `realm` (`fixture|native-unverified|accepted-live`). Demo realm is fixture.
- `workflow`: `phase` (presentation grouping), `coreState` (original Forge state), `desiredState`, `observedState`, `cleanup` (`not-requested|pending|complete|unknown`), `activeOperationCount` (nullable), `quiescenceEvidenceRef` (nullable). `activeOperationCount=0` without trusted closure proof is not enough.
- `focus`: `title`, `explanation`, `blockerCode` (nullable), `nextActionId` (nullable), `nextActionReason`.
- `gates[]`: `id`, `label`, `required`, `outcome` using actual wire enum `PASS|FAIL|INCOMPLETE|ERROR|NOT_APPLICABLE`, `currentness` (`current|stale|unknown`), `receiptId`, `headCommit`, `treeId`, `policyIdentity`, `artifactRefs[]`, `limitations[]`, `notApplicableRationale`, `notApplicableAllowed`.
- `review`: `outcome`, `headCommit`, `treeId`, `patchSha256`, `reviewerIdentity`, `reviewerFamily`, `writerIdentity`, `writerFamily`, `independence` (`accepted|rejected|unverified`), `assessmentRef`, `scope`, `findings[]`. Names/family strings alone cannot create `accepted`; trust comes from source assessment. Demo marks all as synthetic.
- `publication`: `requestedAction`, `observedEffect` (`none|pending|confirmed|unknown`), `receiptRef`, `readbackRef`, `remoteUrl` (nullable), `ciOutcome`, `mergeAuthorized`.
- `integrations[]`: `name`, `role`, `desired`, `observedCapability` (`unavailable|unverified|supported`), `version`, `evidenceRef`, `limitations`. Separate Continuum memory and coordination entries.
- `measurements[]`: `label`, `value` (nullable), `unit`, `source`, `observedAt`, `isEstimate`, `scope`. Missing means **not measured**, never zero.
- `actions[]`: `id`, `label`, `kind` (`read|intent`), `available`, `reason`, `requiredCapability`. Disabled reasons remain readable.

Data is bounded: demo file ≤256KiB; strings ≤4096 chars (display fields further bounded/wrapped); arrays ≤100; control chars stripped/escaped before terminal emission; HTML escaped; artifact refs resolve only inside the demo fixture directory, not arbitrary local file paths/URLs. Evidence availability failure is explicit.

## Events and restore

A proposal event uses `schemaVersion`, `runId`, `sequence`, `eventId`, `observedAt`, `kind`, and a bounded data payload. Demo fixtures may use complete snapshots per event to keep prototype small. Persist only demo cursor/scenario and presentation preferences, never accepted authorization. Do not use host transcript position as authority.

- Exact duplicate event IDs with identical payload are idempotent; conflicting duplicates are ERROR.
- Lower sequence never rewinds live state; mark/reject out-of-order. A gap makes continuity incomplete and consequential actions unavailable until an authoritative complete snapshot is obtained.
- Switching runs resets pending action state and requires explicit matching snapshot identity. Delayed completions from another run/head are historical evidence, not current success.
- Reconnect first shows last-known state with stale marker. Reconcile source, pending intent IDs and uncertain effects before re-enabling actions. A fresh timestamp without current source/capability proof is insufficient.
- A source change invalidates every source-bound gate/review not reissued for the new identity. Historical receipts remain inspectable.
- Offline “next event” controls belong only to fixture replay; never masquerade as workflow execution.

## Outcomes and honest aggregation

Always display original per-gate outcomes. Aggregate uses required current receipts and policy:

1. Malformed/invalid binding evidence → ERROR, with original payload retained in the safe evidence set.
2. Valid, current FAIL → FAIL.
3. Missing, stale, unverified, incomplete, unsupported, gap/unknown continuity or required unknown cleanup → INCOMPLETE.
4. Required NOT_APPLICABLE is admissible only with policy allowance **and** recorded rationale; otherwise ERROR/INCOMPLETE according to source validation.
5. PASS only when every required gate is accepted/current PASS or properly allowed N/A, required review is accepted/current, and no required unresolved uncertainty remains.

Show failures/incompleteness alongside one another even when one takes banner precedence. A PASS gate set means **evaluation accepted**, not publication authorized or project delivered. Phase completion ≠ gate PASS. Review PASS with unverified independence is not acceptance. Demo aggregation is an illustrative pure function, not production trust validation.

## Typed intent, not a command string

A consequential intent contains:

- `schemaVersion: "forge.action/0.1"`, `intentId`, `action` from a closed enum;
- `runId`, `expectedSnapshotId`, `expectedSequence`, `expectedPlanIdentity`, `expectedPlanRevision`, `expectedHeadCommit`, `expectedTreeId`, `expectedPolicyIdentity`, `expectedCapabilityIdentity`;
- `requestedCeiling` where relevant, `approvalRequestRef` (not an approval token supplied by the model), `idempotencyKey`, `issuedAt`, `expiresAt`;
- narrowly typed action payload; **no shell text**, arbitrary executable, alternate repo/root, token/secret, or renderer-selected principal.

Closed actions: `request.pause`, `request.stop`, `request.resume`, `request.repair`, `request.publish-pr`. Read-only actions: `view.plan`, `view.evidence`, `view.activity`, `view.integrations`, `view.approval-request`. Demo replay actions have a separate `demo.*` namespace and cannot be routed to live adapters.

UI may preflight to explain a disabled action. The controller must independently check user authority, authentic approval issuer, audience/action/resource, run/plan/head/tree/policy/capability, expiry/replay, current permission/ownership/gates/review and immutable ceiling immediately before side effect. A stale button or natural-language “yes” without a bound approval cannot authorize anything. Receipt links are not bearer credentials.

Prototype `previewIntent()` performs deterministic identity/currentness checks then returns **PREVIEW_ONLY, sideEffects: 0, authorityIssued: false**. It never changes fixture ceiling or advances the run. Invalid intent returns REJECTED with exact reason. User selects `Replay next event` to view a later synthetic observation, visibly labelled as replay—not approval.

## Control state semantics

| User action / observation | UI must say | Never say |
|---|---|---|
| Pause intent sent | “Pause requested; awaiting controller acknowledgement” | “Paused” |
| Controller stops dispatch but live descendants remain | “Pausing; owned work settling” | “Safe to close/restart” |
| All relevant ownership/streams/operations settled with proof | “Paused / quiescent” | “Published” |
| Stop/timeout/disconnect, cleanup uncertain | “Stop requested; cleanup UNKNOWN” | “Cancelled successfully” |
| Resume requested from stale snapshot | “Reconciliation required” | “Resuming now” |
| Publication call times out | “Remote effect UNKNOWN; readback required” | “Failed, retry” |
| PR receipt and readback accepted | “PR published; CI pending; merge not authorized” | “Delivered” or “Deployed” |

Host Esc/interrupt is observed as host control, not assumed to revoke/close every Forge worker. Stop controls scope to one run; cleanup reports exact owned resources. UI has no process-kill implementation.

## Integration task order after this prototype

1. Owner reviews UX-1 and chooses the first admitted host/version profile; preserve current runtime pause outside separately authorized work.
2. Add a **read-only** production projection from trusted Forge manifest/checkpoint/gate/review/control sources; schema/version validation and provenance acceptance tests first. No renderer authority.
3. Establish event sequencing, stale/reconnect and unknown cleanup behavior against genuine controller state; bounded IO; preserve raw evidence. No live worker launch required to prove projection.
4. Codex skill/tool text + Claude CLI status line: use explicit profile paths; don't overwrite user statusline/config. Pi extension: validate against pinned actual installed SDK and keyboard renderer; independent host acceptance without paid calls where possible.
5. Implement typed request routing to controller with binding, revocation, expiration, replay and zero-forbidden-write tests. UI previews remain non-authoritative until real approvals are independently admitted.
6. Connect **Engram** repository/snapshot-bound code intelligence and **Continuum memory** context. Admit **Continuum coordination** separately from actual lease proof, not from memory connectivity.
7. Import real dotnet/Crap4CSharp/Mutate4CSharp currentness and policy evidence when the active implementation is accepted. Do not promote prototype numbers or old CI counts.
8. Prove exact-head independent review and PR readback under owner-approved disposable integration. Merge/release/deploy remain separate.
9. Optional ChatGPT desktop MCP App panel, separate IDE clients, Switchyard #30, optional OpenClaw presentation—each needs its own version/capability/security/native-render acceptance. Companion is never proof of those surfaces.
