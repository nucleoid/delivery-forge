# FORGE / The quiet cockpit

**Design revision UX-1 — frozen for the presentation prototype**  
Designer/reviewer: **openai/gpt-6-astra · xhigh**. Implementation must follow this design; do not delegate design to another model. This is a proposal, not a shipped Forge UI.

## 1. Product promise

**Know what is happening. Know what blocks it. Know what you can safely do next.**

Not a wall of agent chatter. Not a build lamp that turns green when one subprocess exits. A compact, persistent record of one delivery run—with the proof one deliberate drilldown away.

A useful cockpit answers five questions in reading order:

1. **Which run and source?** Run ID, plan revision, short exact head and current ceiling.
2. **Where are we?** One phase and observed lifecycle, with a compact phase trail.
3. **What matters now?** One current task or blocker, in plain English.
4. **What proves that?** Outcome, currentness and evidence reference; raw output accessible.
5. **What next?** One recommended action, or a clear reason no action is admissible.

The user can keep talking normally. Intake defaults to one sentence and, only if genuinely necessary, one conversational question. Avoid recurring option dialogs. Deep planning is opt-in. Permission prompts remain explicit and separate from ordinary product questions.

## 2. Information hierarchy and visual language

### Shared content structure

- **Identity rail:** `FORGE  /  demo-014  /  plan r3  /  head c82e4a1  /  ceiling IMPLEMENT`. Add a visible `DEMO • SYNTHETIC` mark in every prototype surface. Full IDs are available in detail, never replaced by shortened hashes in validation.
- **Phase trail:** `Launch → Plan → Execute → Evaluate → Repair → Review → PR`. This is navigation/history, **not seven equal progress percentages**. Repair is a branch/revisit, not an inevitable stage. Completed phase labels mean activity completed, not acceptance PASS.
- **Focus statement:** human sentence, e.g. **Mutation evidence is incomplete.** Follow with the smallest useful explanation: “The report covers 18 of 20 discovered mutants. Publication is blocked.” The counts are synthetic in the demo.
- **Evidence list:** gate name, outcome word, current/stale/unknown status, receipt ID. “Build PASS / Tests PASS / Mutation INCOMPLETE” remains three rows; never “2/3 green = almost done”.
- **Next action:** `Inspect missing evidence` first; `Request bounded repair` only if genuinely available. Show unavailable action and reason as readable text, not a mystery disabled button.
- **Secondary detail:** Plan · Evidence · Activity · Integrations. In terminal form these are command hints rather than fake clickable tabs.

### Palette and type

The identity is engineered, calm and warm—not cyberpunk. Deep ink background **#101820**, raised panel **#17232D**, hairline **#30414D**, bright text **#E8EEF2**, secondary text **#AFBEC9**. Forge amber **#F2BA69** marks attention/current action, sea-glass **#79D7C4** marks validated PASS, rose **#FF9CA7** marks FAIL/ERROR, pale violet **#B9AEF5** marks review/authority boundaries. Do not use color as the only signal. On light backgrounds use dark semantic equivalents and test contrast.

Terminal: existing host font, 1-column space grid, simple separators, no emoji-width dependence. Uppercase outcome labels. Unicode separators may have an ASCII fallback. Prefer 3–6 persistent lines and an expanded view over permanent full-screen takeover. No spinner unless real recent activity exists; no shimmer at all. No automatic animated timeline.

Companion **supplement**: system sans (no web fonts), monospace for IDs/evidence, 8px spacing grid, 14–16px body, 28–32px focus headline, 1px rules, restrained 10–12px panel radii. Main viewport at 1440: 224px left phase navigator + 24px gutter + flexible content + 320px evidence/context rail. Header 80px, outer margin 32px. At 900px collapse rail below content; at 600px use a single column and horizontal scrollable phase navigation. No gradients or fake host chrome. Title explicitly “Forge presentation laboratory — companion, not an embedded host UI”.

### Density

- **80 columns:** no 2-column table. Identity wraps into 2 lines, phase trail wraps, each evidence row is 1–2 lines. All content within 80 display cells. Stable full IDs accessible with evidence drilldown. Focus next-action above historical chatter.
- **120+ columns:** identity 1–2 lines, phase trail 1 line, focus/evidence can use aligned compact columns; max content width 120. Do not stretch sentences to the full screen width.
- **Screen reader/plain:** linear headings and labeled values, no decorative borders, no ANSI, no cursor rewriting. Repeating identity stamp allowed at each update. Outcome changes announced as a complete sentence, not a stream of individual tokens.

## 3. Native adaptations

### Codex CLI — a disciplined transcript

Use a repository-owned skill and a demo MCP tool/text renderer. Each user-requested status or meaningful transition returns a compact block with the identity stamp first. Host tool output may collapse; the skill requests an accurate compact summary, not invented Markdown cards. The built-in plan may mirror coarse steps, but it is only a navigation aid. Do not write arbitrary IDs into built-in host status slots or hijack task progress as proof.

No invented `/forge` command: package entry is a skill (`$forge-demo` where supported) and documented demo tools. Native tool text is the prototype boundary. A transcript can scroll out of view; we **do not claim a persistent custom cockpit in Codex CLI**. For continuous visibility use a separately named adjacent terminal/viewer, explicitly optional. Desktop MCP App panel is a later admitted surface, not silently represented by the companion.

### Claude Code CLI — the narrow persistent cockpit

A 3-line `statusLine` at 80 columns, at most 4 lines when identity needs wrapping:

`FORGE DEMO  demo-014 · r3 · c82e4a1 · ceiling IMPLEMENT`
`EVALUATE / INCOMPLETE — Mutation report omits discovered work`
`Next: inspect mutation receipt · /forge-demo:status for detail`

Output is based on the explicit demo-state file, not the Claude session's cost/elapsed fields. If that file disappears or becomes invalid/stale, show **INCOMPLETE · state unavailable/stale**, never the previous green. Escape untrusted control characters. Statusline may temporarily hide under host permission/help UI; the transcript stamp remains the fallback.

Plugin skill opens the expanded text readout. An optional documented Dynamic Workflow file may replay `phase()`/`log()` only, with no `agent()` calls, no timers, no claims of agent task progress. Do not use Workflow as a fake host for external Forge jobs. Its installed empty-phase display is U until observed.

### Pi TUI — the richest native cockpit

Register deterministic `/forge-demo` (no model turn). Default sets a small `setWidget` above the editor plus `setStatus` with run/phase. `/forge-demo detail` opens a focusable `ctx.ui.custom` panel; keyboard: arrows/j/k move evidence selection, Enter opens detail, Esc returns/closes, `?` help. `/forge-demo next`, `prev`, `reset`, and `scenario <name>` advance only fixture replay. Never intercept the host's global interrupt keys. Keep full model/editor interaction available outside the focused panel.

Persist only fixture cursor/scenario and UI preferences via `appendEntry`, reconstruct from the **active branch** on `session_start`. Validate persisted data; never hydrate authority from it. `session_shutdown` removes owned widgets/status and closes only owned UI resources. No watchers/processes/timers needed. Guard terminal rendering with `ctx.mode === "tui"`; RPC/JSON/print receive useful text, not an exception or fake custom UI.

## 4. Walkthrough — one story, all synthetic

Scenario: **“Add CSV export to the report endpoint.”** Repository `example/report-service`, run `demo-014`; fixture IDs and hashes visibly synthetic. No real source is written. Eight primary screens plus recovery variants must exist in shared data and render in all three native adapters.

| Step | Focus/copy | Evidence and authority | Next action |
|---|---|---|---|
| **01 Launch** | “A delivery run, not a leap of faith.” User intent is one sentence. Repository and capability inventory shown. | Phase LAUNCH; ceiling PLAN; Engram desired integration / demo not connected; Continuum memory and coordination separate | “Review scope” — no execution |
| **02 Plan** | “CSV export, without changing report semantics.” 3 scoped bullets, 3 acceptance checks, exclusions (no migration/deployment), known uncertainties | Draft/frozen plan r3, base/head, policy ID. An actual approval request would bind plan+base+capability. Demo says “Approval preview only” | “Inspect implementation request” — never auto-escalate |
| **03 Execute** | “One writer. One isolated change.” Worker assignment, current step “Add failing export-contract test” | Ceiling IMPLEMENT in fixture observation; writer/worktree identity; RED/GREEN receipts separate; resource limit unknown/observed only | “Request pause” (intent preview, not instantly paused) |
| **04 Failed gate** | **“Mutation evidence is incomplete.”** Two discovered mutants lack outcomes. | Build PASS, tests PASS, Crap4CSharp PASS, Mutate4CSharp INCOMPLETE. Aggregate **INCOMPLETE**, not green. Original failure retained with raw receipt | “Inspect missing evidence”; bounded repair request, publication blocked |
| **05 Repair** | “Repair the evidence—not the threshold.” Scope only missing mutation outcome capture; policy unchanged | New attempt ID; failed receipt retained; source changes to head c82e4a1 invalidate old-head passes. Missing exact-head reruns remain INCOMPLETE | “Re-evaluate exact head”; no “ignore gate” |
| **06 Review** | “Independent review, on this exact head.” | Exact head/tree/patch, writer identity, reviewer identity/family, trusted independence assessment and scope. Fixture shows an accepted independent review only as synthetic; no same-agent self-certification | “Inspect findings and review binding” |
| **07 PR request** | “Ready to request a pull request. Not authorized yet.” | Required exact-head gates PASS, review accepted; ceiling still IMPLEMENT. Requested ceiling PR. Approval packet identifies run/r3/head/policy/capability/expiry/idempotency. No merge | “Inspect PR authorization request” — prototype issues no approval |
| **08 PR receipt** | “PR opened. Delivery is not deployment.” | Fixture PR receipt + readback confirmed, ceiling PR; CI INCOMPLETE/pending; merge unauthorized; no fake real URL | “View receipt”; monitor checks is future integration, no actual polling |

N/A example belongs in evidence detail: migration gate NOT_APPLICABLE only with policy allowance and “No schema or data migration in approved scope.” Never treat absent tests as N/A. FAIL variant must show a genuine negative test outcome distinct from incomplete enumeration. ERROR variant covers malformed/wrong-head evidence, not just tool exit 1.

### Recovery branches to implement

- **Pause requested:** desired PAUSED; observed EXECUTING/SETTLING. “No new dispatch requested; one owned operation still settling.” Pause button cannot claim quiescence.
- **Paused/quiescent:** controller observation has explicit cleanup/owned-operation proof. “Paused. No owned operations remain.” Restore requires reconciliation, not automatic dispatch.
- **Cancel uncertain:** desired STOPPED; observed UNKNOWN; cleanup UNKNOWN. “Stop requested. Cleanup is unconfirmed.” Block resume, repair and publication; expose reconciliation detail. Do not signal arbitrary processes from UI.
- **Stale/reconnected:** last sequence/time visible. “View is stale. Reconcile before acting.” Inhibit consequential actions until a fresh authoritative snapshot resolves run/plan/head/capability and prior pending intents.
- **Head drift:** retain historical green receipt, mark STALE, aggregate INCOMPLETE. “Review belongs to an earlier head.” Re-review exact new head, never silently reuse.
- **Tool FAIL / protocol ERROR:** distinct words, distinct next steps. Preserve raw evidence paths/hashes and attempt history.

## 5. Event and authority behavior

See `04-interaction-contract.md`. All screens use one projection function, no bespoke per-host status logic. UI preferences never modify evidence. Desired state and observed state have different labels. Host idle is never Forge quiescence. An error on a status refresh freezes a clearly stale view, not a false activity indicator.

Only measured values may appear as metrics. In this demo: numeric fixture results are marked **synthetic**; elapsed/cost/CPU/memory display “not measured” unless explicitly fixture-labelled. No ETA, percentage completion, invented dollars, or unbounded spinner. Real future host token estimates must retain their source and definition; Claude list-price estimate is not Forge billed cost.

## 6. A11y, keyboard and motion

Contrast target WCAG AA for companion; test text/background combinations. Focus visible 2px amber, no color-only outcomes, buttons have verb labels, form fields accessible names, semantic headings/lists. Native custom panel supports Escape and arrows, with plain text fallback; don't capture Ctrl+C/Ctrl+D globally. All actions work without a mouse. Companion controls tab in reading order; focus stays stable after event changes; `aria-live="polite"` announces phase/outcome once. `prefers-reduced-motion` and `NO_COLOR` honored; this design has no animation to disable. Zoom/390px mobile must not hide run identity or next action.

## 7. Implementation freeze and acceptance

Keep prototype source and generated evidence isolated from the runtime implementation. No global installs/config; no .NET; no Android; no network/provider/GitHub writes; no real Forge/Engram/Continuum/Switchyard/tool invocations. No subprocess/model CLI is launched by prototype code.

Runtime implementation: small Node ESM/JS with no heavy framework/runtime SDK; shared JSON fixture events; pure projection/intent validation; adapters for MCP text, Claude stdin/stdout and Pi extension. Browser companion static and optional. Reuse existing Playwright only for local visual tests. No dependency is permitted to provide hidden model calls. Untrusted input sanitized for terminal/HTML.

Deliver executable scripts, package layouts, exact manual local launch instructions, dependency versions, tests, saved terminal captures, actual screenshots and contact sheets. Every image must say **HARNESS RENDER** or **COMPANION DEMO** if not captured from installed host. Native installed rendering remains U unless actually observed without forbidden calls/settings changes. Never fake a host window title as proof.

Minimum tests: all steps/hosts; 80/120-column no overflow; invalid schema; duplicate/out-of-order/gapped events; wrong run/head/plan action; stale view; head drift; missing receipt; unknown cleanup; N/A rationale; no authority escalation; no external side effects; terminal/HTML escaping; Pi branch restore/mode guard/shutdown; MCP protocol errors; Claude bad-input/stale-state fallback; keyboard drilldown; reduced motion and viewport layout. Keep data-only preview separate from operational acceptance.

Independent design/source review and installed-host captures remain required before native admission. Any visual correction should return to one source owner to avoid overlapping writers.
