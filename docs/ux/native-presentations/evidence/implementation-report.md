# Delivery Forge UX-1.2 implementation report — synthetic usage, safety and bounded durability

Date: 2026-10-19 UTC
Writer: native GPT-5.6 SOL, sole prototype writer
Design authority: frozen UX-1, projection addendum, `10-usage-cost-design.md`, `10a-usage-implementation-handoff.md`
Prior native-capture checkpoint: `37aab4c6529dfcd361cf9f924f5dcca41423af7f`
Final telemetry safety/durability commit: **`e9eb721c664440b61352547d694c2c83798e07ed`**
Final tree: `f51d3abcc6998f0be1f6e4d4f9b8a6c742300cf9`
Branch: `ux/native-presentations-20261019`
Remote: none

## Result

UX-1.2 adds one shared, pure, demo-only `forge.usage/0.1` accounting projection consumed by Codex, Claude Code, Pi and the static companion. It makes no provider, model, billing, host-transcript, telemetry-exporter, network, Forge, Engram, Continuum, Switchyard or GitHub call.

The final targeted repair binds every priced observation to matching provider, backend, tier, effective SKU and immutable synthetic rate identity; deeply validates checkpoint contributions and recomputes admitted prices; preserves all-unpriced cost as null; and adds a bounded explicit-path owned synthetic JSONL demonstration. The JSONL path admits only exact named bundled fixture bytes, caps files at 256 KiB / 256 records, fsyncs append operations, and never silently repairs malformed/truncated input.

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

`src/usage.mjs` provides bounded pure normalization, exact nano-USD-per-token integer arithmetic, immutable synthetic rate snapshots, request revision replacement, dedupe/corroboration handling, and deeply validated checkpoint serialization.

- Same incurrence partial → final replaces the provisional contribution.
- Identical duplicate, mirror route and inclusive parent aggregate do not add.
- A distinct provider attempt/retry adds.
- Conflicting duplicate, older revision, sequence gap, source-epoch change, malformed input and revoked source remain visible conditions and make coverage partial.
- Cumulative resume data is retained as a baseline and is not charged again.
- Serialized checkpoint roundtrip preserves dedupe/contribution state; no transcript is read.
- Unknown or mismatched provider/backend/model/SKU/tier never borrows another rate.
- Usage freshness/continuity does not mutate workflow source continuity, gates, authorization ceiling, action availability or preview authority.

`src/usage-jsonl.mjs` and `scripts/usage-ledger.mjs` provide only a synthetic fixture durability demonstration. There is no generic record import, production collector, database, daemon, transcript reader, or auto-repair. Fresh-process replay preserves the exact `0.044225 / 19,300 / PARTIAL` story; duplicate append does not increase it; partial-to-final replacement and distinct retries survive restart; malformed and truncated tails create explicit uncertainty conditions without mutating the file.

Production Codex app-server, Claude result/OTel and Pi final-message producers; a durable multi-host ledger; trusted correlation; and real price catalog admission remain deferred to the usage issue (seventh in this UX set, not necessarily GitHub issue `#7`). This prototype is not production telemetry acceptance.

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
node --check src/usage-jsonl.mjs
node --check scripts/usage-ledger.mjs
node --check scripts/capture-usage-durability.mjs
node --check hosts/pi/forge-demo-extension.js
node --check hosts/codex/mcp-server.mjs
node --check hosts/claude/statusline.mjs
node scripts/build-companion-data.mjs
node --test
node scripts/capture-usage-durability.mjs
```

Final test result: **70 passed, 0 failed**. The prior 63-test checkpoint remains the regression baseline. New coverage includes provider/backend/tier/SKU binding; malformed and forged deep checkpoints; all-unpriced null rendering; fsynced append/reopen; fresh-process replay; duplicate append; partial-to-final replacement plus distinct retry across restart; malformed/truncated-tail uncertainty with byte non-mutation; and rejection of tampered bundled-event envelopes.

Exact stdout parity against immutable `37aab4c` passed for launch, execute, failed-gate expanded, failed-gate usage, and failed-gate compact native demo views. Hashes are in `preset-byte-parity-37aab4c.txt`; source-version metadata was excluded. Therefore the valid preset UI appearance and command grammar did not change.

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
- `usage-jsonl-durability.txt` records the bounded durability story and explicit damaged-tail uncertainty.
- `preset-byte-parity-37aab4c.txt` records exact before/after public-view hashes.
- `capture-provenance.json` records generator, runtime, operation/argv and hashes.

Every regenerated image was visually inspected. No clipping or horizontal overflow was observed. Host contract images remain explicitly **HARNESS RENDER**; companion images remain **COMPANION DEMO**. None is an installed-host screenshot.

Native source bundle:

- File: `delivery-forge-native-prototypes-e9eb721.zip`
- Exact source: commit `e9eb721c664440b61352547d694c2c83798e07ed`
- SHA-256: **`b37350f01d46dbb78005a4c13d7be5344e7eff89cd9c0de80a5195a938189c1d`**
- Contents: committed `README.md`, `package.json`, `data/`, `src/`, `hosts/`, `scripts/demo.mjs`, `scripts/usage-ledger.mjs`, and `scripts/capture-usage-durability.mjs` only.
- Excludes `.git`, tests, companion, evidence, and publication worktrees.
- `unzip -t`: no errors.

## Native and production limitations

Installed Codex 0.153.4, Claude Code 2.1.303 and Pi 0.80.7 rendering was later captured using immutable source `37aab4c`; those receipts remain bound to that tested source SHA. The added telemetry safety/durability behavior at `e9eb721` is Node/harness verified only and was not rerun in installed hosts. `NATIVE DEMO SURFACE` is a package presentation label, not certification. Claude Code 2.1.302 remains the historical preflight target.

No real producer was attached. No provider identity, current real rate, bill, historical effective date, retry spend or production coverage was inferred. No automatic budget stop, model switching, dispatch, authority escalation or controller behavior was added. CPU, memory, elapsed time and actual charge remain unknown.

## Scope and side effects

Source changes were confined to an isolated prototype repository and generated evidence directory. No Delivery Forge runtime, host/global configuration, credential, model/provider, live Forge run, or external production system was touched. The prototype repository was clean, had no remote, and no source push was attempted.
