# Delivery Forge UX-1 native presentation prototypes

**DEMO ONLY · SYNTHETIC FIXTURES · ZERO LIVE ADAPTERS**

This directory implements the frozen UX-1 design as three deliberately different host packages plus a static companion. It cannot run Delivery Forge, call a model, issue authority, write source, publish, poll CI, contact Engram/Continuum/Switchyard/GitHub, or kill processes. Intent output is always `PREVIEW_ONLY`, `sideEffects: 0`, `authorityIssued: false`.

## Local no-model demos

From this directory:

```sh
npm install
node scripts/demo.mjs failed-gate 80
node scripts/demo.mjs failed-gate 80 --presentation native --compact
node scripts/demo.mjs failed-gate 80 --presentation native --view usage
node hosts/codex/protocol-harness.mjs failed-gate 80
printf '{"session_id":"fixture"}' | COLUMNS=80 NO_COLOR=1 node hosts/claude/statusline.mjs --state hosts/claude/statusline-state.json
node --test
```

Open `companion/index.html` directly as a local file. It needs no server. Keys: Left/Right replay fixtures, Up/Down select evidence, Enter detail, `P` preview, `R` reset. Replay controls are `demo.*` presentation actions, not workflow commands.

CLI grammar: `node scripts/demo.mjs [scenario] [width] [--presentation harness|native] [--view expanded|compact|usage]`. `--compact` remains an alias for `--view compact`. These options select deterministic fixture presentation only.

## UX-1.2 synthetic usage contract

Every surface consumes one `forge.usage/0.1` projection from `src/usage.mjs`. It uses **SYNTHETIC TOKENS + SYNTHETIC RATES** and labels money **USD API-EQUIVALENT ESTIMATE**. It is not an invoice, subscription bill, actual charge, credit balance, or production telemetry stream. Actual charge remains unknown.

Fixture progression: launch/plan unavailable; execute writer partial `USD0.021700`; later writer final replacement plus reviewer and distinct retry produce `USD0.044225` known subtotal; one unresolved coordinator request makes the run `PARTIAL` with 19,300 observed tokens and one unpriced request. Cache and reasoning values are subsets. Duplicate/mirror/parent observations and checkpoint replay do not add; a distinct incurred retry does. Serialized checkpoint roundtrip is tested, but no host transcript is read and no collector/database exists.

Production adapters, durable multi-host storage, real model resolution, and admitted provider price catalogs are deferred to the proposed **“Track per-model tokens and live USD API-equivalent estimates”** issue, whose GitHub number is pending publication. CPU, memory, elapsed time, and actual charge remain unknown.

## Packages and admission status

| Package | Path | Presentation | Status |
|---|---|---|---|
| Shared projection | `data/fixtures.json`, `src/` | Policy-bound workflow plus pure synthetic usage normalization, ledger, pricing and checkpoint replay | Local Node tested |
| Codex | `hosts/codex/` | Repository skill + newline-delimited stdio MCP text tools | Protocol harness tested; installed Codex rendering **unverified** |
| Claude Code | `hosts/claude/` | 4–5 line status script + expanded plugin skill | Contract harness tested; installed status line/plugin **unverified** |
| Pi | `hosts/pi/forge-demo-extension.js` | Widget, status and focusable keyboard detail panel | API test-double harness only; installed Pi **unverified** |
| Companion | `companion/` | Static presentation laboratory | Chromium visual/keyboard tested; **not embedded in any host** |

No Dynamic Workflow sample is included. Its phase/log-only parser and empty-phase display were not installed-verified, and it is optional in UX-1; fabricating runtime-owned workflow events would weaken the prototype.

## Version/reference basis

| Surface | Basis |
|---|---|
| Codex CLI | Target 0.153.4 at `3d2ee51ca2d5db578f328aa75e20aa22c0197c9a`; fixed `StatusLineItem` footer and local `!command` user-shell seam confirmed in pinned source. This package does not claim a custom footer. |
| Claude Code CLI | Target 2.1.302; official `statusLine` JSON stdin, `COLUMNS`, multiline output, and installed-help `--settings` / session-only `--plugin-dir` observations. |
| Pi | Target `@earendil-works/pi-coding-agent` 0.80.7 at `818d67457cdd6b60bce6b121d16b23141c252dd8`; its exact pinned declarations contain every API used here. |
| Forge | Presentation reads synthetic proposal contract only; source context pinned to main `d34280b…` and PR32 `6fa0d36…`. It is not a Forge v1 API change. |

Public capability references are summarized in the [capability matrix](../../docs/ux/native-presentations/capability-matrix.md).

## Future opt-in host loading (do not run for this prototype acceptance)

These are manual, explicit future steps. They do not modify global configuration unless an operator chooses to do so.

- **Codex 0.153.4:** the pinned native no-model seam is an interactive user-shell submission of `!node /ABSOLUTE/PATH/TO/examples/presentation-demo/scripts/demo.mjs failed-gate 80 --presentation native --compact`. Replace `--compact` with `--view usage` for the synthetic usage drilldown. `--presentation` changes provenance copy only. This is local command transcript text, not a footer, model call, or persistent host integration. For MCP testing, copy `hosts/codex/mcp-config.example.toml`, replace only `/ABSOLUTE/PATH/TO`, and use a separately admitted scoped profile. Exact installed behavior remains unverified here.
- **Claude Code 2.1.302:** replace the two absolute placeholders in `hosts/claude/settings.session.example.json`, then start a disposable trusted session with documented `--settings <that-file> --plugin-dir <absolute hosts/claude/plugin>`. This example does not overwrite user/project settings. Managed-hook policy or workspace trust can suppress the status line. No `/statusline` generation prompt is needed.
- **Pi 0.80.7:** from a disposable directory, the documented isolated extension form is `pi --no-extensions --no-context-files -e /ABSOLUTE/PATH/TO/examples/presentation-demo/hosts/pi/forge-demo-extension.js`. `/forge-demo scenario failed-gate` selects the full partial-known state and `/forge-demo usage` opens the bounded usage panel. Do not use RPC expecting custom TUI components; the extension guards `ctx.mode === "tui"`. Non-TUI output uses `triggerTurn:false`.

No installed Codex, Claude Code or Pi session was started to produce this repository or its evidence.

## Fixture states

Eight primary states: `launch`, `plan`, `execute`, `failed-gate`, `repair`, `review`, `pr-request`, `pr-receipt`.

Recovery/evidence variants: `pause-requested`, `paused-quiescent`, `cancel-unknown`, `stale-reconnected`, `head-drift`, `tool-fail`, `protocol-error`. Failed and superseded attempts remain inspectable; stale receipts cannot aggregate PASS. N/A requires policy allowance and rationale. Elapsed, CPU, memory and actual charge are unknown; synthetic API-equivalent usage is separate.

The synthetic accepted policy projects `requirements.gateIds`, `reviewRequired`, and `policyIdentity` independently of received rows. Missing gates, empty evidence, incomplete/invalid review, malformed snapshots, and discontinuous events are non-PASS and inhibit consequential intent previews where reconciliation is required.

## Evidence generation

```sh
node scripts/build-companion-data.mjs
node scripts/capture-evidence.mjs
node scripts/screenshots.mjs
```

`capture-evidence.mjs` executes only local Node harnesses with bounded output/time. `screenshots.mjs` rasterizes the saved raw terminal text and the static companion with the pinned Playwright development dependency. By default both scripts write to `../../docs/ux/native-presentations/evidence`; set `FORGE_EVIDENCE_DIR` to use another output directory. Images are labelled **HARNESS RENDER** or **COMPANION DEMO**; none is an installed-host screenshot.

The text formatters accept a presentation-only provenance value. Harnesses explicitly request `HARNESS RENDER`; installed host package paths request `NATIVE DEMO SURFACE`. This label never changes fixture facts, aggregation, capability, authority, or intent admission.
