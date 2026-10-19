# Delivery Forge UX-1.2 implementation report — synthetic usage and API-equivalent cost

Date: 2026-10-19 UTC
Writer: native GPT-5.6 SOL, sole prototype writer
Design authority: frozen UX-1, [projection addendum](../projection-completeness-addendum.md), [usage/cost design](../usage-cost-design.md), and [implementation notes](../usage-implementation-notes.md)
Prior immutable checkpoint: `995ca755ea6a21817957aa1f3d67106c4b069144`
Final immutable commit: **`37aab4c6529dfcd361cf9f924f5dcca41423af7f`**
Final tree: `095b2ea4a9140cca3a841be58d8e9c10b28ae030`
Branch: `ux/native-presentations-20261019`
Remote: none

## Result

UX-1.2 adds one shared, pure, demo-only `forge.usage/0.1` accounting projection consumed by Codex, Claude Code, Pi and the static companion. It makes no provider, model, billing, host-transcript, telemetry-exporter, network, Forge, Engram, Continuum, Switchyard or GitHub call.

The fixtures and all UI surfaces state **SYNTHETIC TOKENS + SYNTHETIC RATES** and **USD API-EQUIVALENT ESTIMATE**. They do not claim an invoice, subscription charge, actual spend, credit consumption or production stream. Actual charge is null/unknown. Unavailable and entirely unpriced cost values remain null, never numeric zero.

Exact finite story:

- Launch and Plan: usage unavailable.
- Execute: writer partial snapshot, `USD0.021700`; provisional revision only.
- Writer final replaces partial: `USD0.029700`, not their sum.
- Reviewer final: `USD0.012525`.
- Distinct writer retry: `USD0.002000`.
- Known subtotal: **`USD0.044225`**.
- Fourth unresolved coordinator request: 800 observed tokens, no price/model resolution.
- Whole run: **19,300 observed tokens**, one unpriced request, **PARTIAL**, actual charge unknown.

Writer inclusive input normalizes to ordinary/read/write without double counting. Reviewer and Pi-shaped fixtures use exclusive ordinary input. Reasoning remains an output subset. Reviewer 1-hour cache writes remain within total cache write. Explicit measured zero survives; absent or ambiguous fields remain unknown.

## Accounting behavior

`src/usage.mjs` provides bounded pure normalization, exact nano-USD integer arithmetic, immutable synthetic rate snapshots, request revision replacement, dedupe/corroboration handling, and checkpoint serialization.

- Same incurrence partial → final replaces the provisional contribution.
- Identical duplicate, mirror route and inclusive parent aggregate do not add.
- A distinct provider attempt/retry adds.
- Conflicting duplicate, older revision, sequence gap, source-epoch change, malformed input and revoked source remain visible conditions and make coverage partial.
- Cumulative resume data is retained as a baseline and is not charged again.
- Serialized checkpoint roundtrip preserves dedupe/contribution state; no transcript is read.
- Unknown provider/model/SKU/tier never borrows another rate.
- Usage freshness/continuity does not mutate workflow source continuity, gates, authorization ceiling, action availability or preview authority.

Production Codex app-server, Claude result/OTel and Pi final-message producers; a durable multi-host ledger; trusted correlation; and real price catalog admission remain deferred to the proposed **“Track per-model tokens and live USD API-equivalent estimates”** issue, whose GitHub number is pending publication. This prototype is not production telemetry acceptance.

## Host presentation

- **Codex 0.153.4 target:** MCP `forge_demo_status` accepts `view: expanded|compact|usage`. Standalone native text uses the reviewed user-shell seam; no footer is invented.
- **Claude Code 2.1.302 target:** the status line reads only the explicit synthetic fixture pointer and adds the compact usage row. It never accumulates statusline context counters. Expanded usage is shared text/skill output, not an invented native panel.
- **Pi 0.80.7 target:** `/forge-demo usage` opens a width-aware, 18-row bounded, scrollable TUI component. Escape closes and focus returns through the host custom-UI contract. Non-TUI output uses `triggerTurn:false`.
- **Companion:** generated from the same projected snapshots; it contains no rate or ledger arithmetic.

CLI grammar:

```text
node scripts/demo.mjs [scenario] [width]
  [--presentation harness|native]
  [--view expanded|compact|usage]
```

`--compact` remains an alias for `--view compact`. Provenance selection changes presentation copy only.

## Verification

Final commands exited 0:

```text
git diff --check
node --check src/usage.mjs
node --check hosts/pi/forge-demo-extension.js
node --check hosts/codex/mcp-server.mjs
node --check hosts/claude/statusline.mjs
node scripts/build-companion-data.mjs
node --test
node scripts/capture-evidence.mjs
node scripts/screenshots.mjs
```

Final test result: **63 passed, 0 failed**. Coverage includes all prior 50 tests plus exact arithmetic; inclusive/exclusive mapping; cache retention and reasoning subsets; unknown versus measured zero; malformed/bounded values; revision replacement; duplicate/mirror/parent/retry behavior; cumulative baseline/checkpoint replay; gap/conflict/order/epoch/revocation; unresolved aliases; frozen rate snapshots; gate/authority isolation; 80/120-column output; no false zero; Codex usage selection; Claude compact usage; Pi usage keyboard/bounds; and companion shared-state behavior.

Historical receipts remain unchanged:

- `red-baseline-3a97fe0.txt`: 41 discovered, 33 passed, 8 failed.
- `f9b05e6`: 44/44 passed.
- `red-baseline-f9b05e6.txt`: 45 discovered, 40 passed, 5 failed.
- `995ca75`: 50/50 passed after R1–R4.

## Evidence

Regenerated deterministic local evidence includes:

- Codex usage: `codex-usage-detail-80.txt` / `.png`.
- Claude usage-bearing statusline: `claude-statusline-80.txt` / `.png`.
- Pi usage: `pi-usage-detail-80.txt` / `.png`.
- Shared expanded 80/120 captures: `terminal-80`, `terminal-120` raw and PNG.
- Eight-state usage-progress walkthrough: `walkthrough-contact-sheet.png`.
- Companion: `companion-desktop.png`, `companion-mobile-390.png`.
- Existing failure/repair/review/evidence-selection captures remain.
- `capture-provenance.json` records generator, runtime, operation/argv and hashes.

Every regenerated image was visually inspected. No clipping or horizontal overflow was observed. Host contract images remain explicitly **HARNESS RENDER**; companion images remain **COMPANION DEMO**. None is an installed-host screenshot.

Native source bundle:

- File: `delivery-forge-native-prototypes-37aab4c.zip`
- Exact source: commit `37aab4c6529dfcd361cf9f924f5dcca41423af7f`
- SHA-256: **`2469a22fdd291567823d86856edab5e6d8308a2e1cb43ad94ec3d7bacd9fc426`**
- Contents: committed `README.md`, `package.json`, `data/`, `src/`, `hosts/`, and `scripts/demo.mjs` only.
- Excludes `.git`, tests, companion, evidence and publication worktrees.
- `unzip -t`: no errors.

## Native and production limitations

Installed Codex 0.153.4, Claude Code 2.1.302 and Pi 0.80.7 rendering remains unverified. Native captures still require separate exact-version/action receipts. `NATIVE DEMO SURFACE` is a package presentation label, not certification.

No real producer was attached. No provider identity, current real rate, bill, historical effective date, retry spend or production coverage was inferred. No automatic budget stop, model switching, dispatch, authority escalation or controller behavior was added. CPU, memory, elapsed time and actual charge remain unknown.

## Scope and side effects

Source changes were confined to the isolated prototype repository and generated evidence directory. No Delivery Forge core checkout, host/global config, credential, Android tool, GitHub object, model/provider, live Forge run or external system was touched. The source repository was clean, had no remote, and no push was attempted. No owned helper remained.
