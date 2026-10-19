# Delivery Forge UX-1.1 repair implementation report

Date: 2026-10-19 UTC  
Implementation writer: native GPT-5.6 SOL, sole prototype writer  
Design authority: frozen UX-1 plus `04a-projection-completeness-addendum.md`  
Baseline commit: `3a97fe0d7148459ee5b74e18fb57ba3604292026`  
Repaired immutable commit: **`f9b05e6bdf6c2d6b869323f6a6aa08297b862a63`**  
Branch: `ux/native-presentations-20261019`  
Remote: none

## Result

All nine Astra native-readiness findings were reproduced against the baseline and repaired without adding a live adapter, controller, authority, model call, network effect, or host installation.

1. Pi detail output is bounded to 18 rows and scrollable.
2. Pi evidence selection has a stable visible `>` marker before Enter.
3. Pi persistent widget is a documented component factory rendering at the host-provided width.
4. Pi `invalidate()` is cache-only and cannot request-render recursively.
5. Empty/unestablished evidence is INCOMPLETE at Launch, Plan, and Execute.
6. Required independent review accepts only exact-source PASS with accepted independence; INCOMPLETE is non-PASS and invalid values are ERROR.
7. `requirements.gateIds`, `reviewRequired`, and `policyIdentity` are projected independently of received rows. Missing/duplicate gates, duplicate receipt identities, and policy mismatch are non-PASS.
8. Event schema, run/event identity, sequence, snapshot consistency, gaps, conflicts, and out-of-order delivery flow into projected continuity and block consequential intent preview.
9. Malformed public projection/render/intent inputs return bounded typed ERROR/INCOMPLETE output instead of throwing or retaining green state.

Presentation provenance is now a controlled display-only option. Standalone captures say `HARNESS RENDER`; installed package paths say `NATIVE DEMO SURFACE`. Both retain `DEMO • SYNTHETIC`, zero authority, and identical facts/admission status.

## RED/GREEN receipts

- RED baseline: `red-baseline-3a97fe0.txt` recorded **41 tests: 33 passed, 8 failed** against baseline `3a97fe0`. Failures cover the nine grouped findings. The raw log is not published because its stack traces contain a private filesystem path; this result is the sanitized receipt.
- GREEN immutable verification: `node --test` at `f9b05e6` records **44 tests: 44 passed, 0 failed** in 1.409 s.
- Syntax checks passed for projector, Pi extension, Claude status line, Codex MCP server, capture script, and screenshot script.
- `git diff --check`: exit 0.
- `git status --short --branch`: clean, `## ux/native-presentations-20261019`.
- `git remote -v`: empty.
- Operational host source scan found no HTTP/TLS/network imports, `fetch`, or model CLI execution. The labelled Codex protocol harness spawns only its local Node MCP endpoint.
- Helper-process audit found no owned MCP, capture, screenshot, Playwright, or Chromium process remaining.

Coverage includes all 15 fixture states at widths 80 and 120; policy completeness; review outcomes; N/A rationale; stale/head drift/unknown cleanup; duplicate receipts; malformed snapshots; event gaps/conflicts/order/identity; event→projection→intent rejection; zero authority; escaping; MCP lifecycle/errors/EOF; Claude invalid/missing/stale/width/provenance; Pi restore/mode/fallback/cleanup/width/selection/scroll/invalidation; and companion keyboard/mobile/reduced-motion/no-empty-green behavior.

## Evidence

Raw captures and screenshots were regenerated from the repaired source.

- Codex: `codex-mcp-protocol.txt` / `.png` — protocol harness, explicitly not installed Codex.
- Claude: `claude-statusline-80.txt` / `.png` — status-line contract harness.
- Pi: `pi-detail-list-52`, `pi-detail-selected-52`, and `pi-evidence-detail-52` `.txt` / `.png` — bounded component harness showing selection movement and detail.
- Shared terminal: `terminal-80`, `terminal-120`, `failure`, `repair`, and `review` raw/screenshot pairs.
- Walkthrough: `walkthrough-contact-sheet.png` — eight primary states; empty early evidence is INCOMPLETE.
- Companion: `companion-desktop.png` and `companion-mobile-390.png`.
- `capture-provenance.json` records generator, runtime, argv/operation, exit status, and raw-capture hashes.
- `SHA256SUMS` covers final evidence files.

Every image was visually inspected after regeneration. No clipping, horizontal overflow, fake host chrome, false native-host claim, or missing provenance was observed. The Pi list images visibly move selection from Build to Tests before the detail image. All images remain harness/companion evidence, not installed-host certification.

## Native readiness and remaining verification

- **Codex 0.153.4:** package and bounded MCP/text protocol are ready. Installed user-shell/MCP/skill rendering remains unverified here.
- **Claude Code 2.1.302:** status-line and plugin package are ready. Installed trust/policy/layout behavior remains unverified here.
- **Pi 0.80.7 (`818d674…`):** APIs used are present in the pinned declarations. Widget/detail implementation is ready; installed TUI keyboard/layout behavior remains unverified here.
- **Node 22.19.0 laptop target:** no laptop command was run by this worker.

No claim of native installed rendering is made. `NATIVE DEMO SURFACE` is emitted only by package paths intended for an actual host and still requires a separate host/version/action receipt.

## Scope and side effects

Source changes were confined to the isolated prototype repository and sibling evidence directory. No Delivery Forge core checkout, host/global config, credential, Android tool, GitHub object, model/provider, Engram, Continuum, Switchyard, .NET build, live Forge run, or external system was touched. No installed host was launched. The prototype repository had no remote and no push was attempted.

The previous 33-pass evidence remains historical only; this report and regenerated evidence describe the repaired immutable commit above.
