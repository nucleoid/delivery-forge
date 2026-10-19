# FORGE cockpit — UX-2 prototypes for the terminal, Claude Code Desktop and Codex Desktop

**Revision UX-2, 2026-10-20. Prototype only: HARNESS RENDER and DESKTOP MOCKUP images, synthetic fixtures, zero live adapters.** This revision answers the UX half of [epic #36](https://github.com/nucleoid/delivery-forge/issues/36): one evidence model, presented well on every surface a developer actually sits in front of. It supersedes the presentation layer of PR #33 while keeping its projection, fixtures, usage ledger, intent preview and honesty rules untouched. The UX-1 material under [`../native-presentations`](../native-presentations/README.md) remains the frozen baseline that the fourteen installed-host captures were taken from.

## What changed and why

UX-1 proved the model but rendered it as a flat wall of same-weight text: eleven lines of "unknown" under Measurements and Integrations, no alignment, no glyphs, no colour, the blocker code never shown, the aggregate reason never shown, and desktop surfaces not prototyped at all. UX-2 keeps the five questions in the same reading order and gives each one a shape.

| Question | UX-1 | UX-2 |
|---|---|---|
| Which run and source? | Two wrapped text lines | One framed identity rail with run, plan revision, short head and ceiling; `DEMO • SYNTHETIC` tag on the frame; full head, tree, policy and capability in a dim footer |
| Where are we? | `Launch -> Plan -> [Evaluate]` | Phase trail with the current phase marked `▶` in amber, earlier phases dimmed, Repair italic as a branch |
| What matters now? | Title and explanation | Outcome glyph + uppercase word + blocker code on one line, then the focus sentence in bold |
| What proves that? | `Build PASS receipt-build-b45e` lines | Aligned evidence rows: glyph, outcome word (with `STALE` when currentness is not current), gate, receipt, and a muted note with the limitation or N/A rationale; a Review row even when the review is missing; attempts; and a `why INCOMPLETE` line that prints the aggregate reasons |
| What next? | One sentence | Primary action marked `▶` with AVAILABLE / UNAVAILABLE and its reason; consequential actions tagged `intent · preview only` |
| Usage | Five lines of prose | Known subtotal with status chip, token counts, and one aligned row per model with a share-of-known bar; unpriced models stay `UNPRICED`, never zero |
| Integrations and measurements | Eight lines | One line of glyphs plus a count line; `not measured: elapsed, actual charge, cpu, memory` in one line |

Colour is never the only signal. Every outcome has a glyph and an uppercase word, and `--ascii` or `NO_COLOR=1` swaps every glyph and box character 1:1 so widths and alignment do not move.

## One renderer, four terminal views

`examples/presentation-demo/src/render-cockpit.mjs` exports `renderCockpit`, `renderStatusLine`, `renderEvidenceList`, `renderEvidenceDetail` and `renderUsagePanel`. All of them read `project(snapshot)` exactly as UX-1 did; no new facts are invented and the UX-1 `render-text.mjs` remains available as the `legacy-*` and `--view expanded` views.

### Framed cockpit, 80 columns

![Cockpit at 80 columns, failed gate](assets/tui-cockpit-80-failed-gate.png)

### Framed cockpit, 120 columns

![Cockpit at 120 columns, review on the exact head](assets/tui-cockpit-120-review.png)

### Recovery states

![Recovery states: cleanup unknown and head drift](assets/tui-cockpit-80-recovery.png)

### PR receipt

![PR receipt with publication block](assets/tui-cockpit-80-pr-receipt.png)

### Status line (Claude Code `statusLine`, Pi widget above the editor)

Four lines at 80 columns: identity with the surface tag right-aligned, outcome glyph + word + focus sentence, synthetic API-equivalent estimate, next action. Stale and unknown-cleanup states change the second line and never stay green.

![Status lines for launch, execute, failed gate, stale and cancel-unknown](assets/tui-status-lines.png)

### Evidence panel (Pi `/forge-demo detail`)

The selected row is inverse video and prefixed with `>`. Detail hard-wraps the full head and tree instead of truncating them, and shows the receipt head next to the run head so STALE is explained rather than just labelled.

![Evidence list and detail at 60 columns](assets/tui-evidence-panel.png)

### Usage panel

![Usage panel at 80 columns](assets/tui-usage-panel-80.png)

### ASCII fallback

![ASCII and NO_COLOR fallback](assets/tui-ascii-fallback-80.png)

### Walkthrough contact sheet

![Eight walkthrough states at 72 columns](assets/tui-walkthrough-contact-sheet.png)

## Host wiring

| Host | Surface | What UX-2 changes | Boundary |
|---|---|---|---|
| Codex CLI 0.153.4 | `!node scripts/demo.mjs <state> 80` user-shell transcript; `forge_demo_status` MCP text | Default MCP view is the framed cockpit; `view: "status"` and `view: "usage"` added; `ascii: true` for transcripts that cannot draw box characters; UX-1 views kept as `legacy-*` | Still a disciplined transcript. No footer item, no persistent widget. Not re-captured on the installed host for UX-2 |
| Claude Code CLI 2.1.303 | `statusLine` command | `hosts/claude/statusline.mjs` emits the four-line UX-2 status with ANSI colour unless `NO_COLOR=1`; all fail-closed paths unchanged | Reads the explicit fixture state file only. Not re-captured on the installed host for UX-2 |
| Pi 0.80.7 | widget above editor, `/forge-demo detail`, `/forge-demo usage` | Widget renders the status line; detail and usage panels use the new list, detail and usage renderers; colour only when Pi hands the component a real theme | Same deterministic commands, same `ctx.mode === "tui"` guard, same branch restore and shutdown |

## Desktop surfaces

The user asked for the two desktop experiences to be as good as or better than the terminal. Both mockups embed the **same panel component** (`desktop/forge-cockpit.js`, `desktop/forge-cockpit.css`) fed by the same generated projection (`companion/companion-data.js`), inside host chrome drawn only for layout. They are static files that open from disk, work with the keyboard (arrows select evidence, Enter opens detail, 1 to 5 switch tabs, P previews the bound intent), and expose `?scenario=&tab=&detail&preview` for deterministic captures.

### Claude Code Desktop

Documented Code-tab seams used: the chat shows the skill's tool call collapsed, an inline Forge card (the same facts the chat would otherwise get as text), Claude's own task list and next-step box; the Forge pane is the **Browser pane showing the local companion** (title bar `Changes · Forge · Terminal`). No custom pane API or CLI statusline parity is claimed for Desktop.

![Claude Code Desktop mockup, failed gate](assets/desktop-claude-failed-gate.png)

![Claude Code Desktop mockup, usage tab](assets/desktop-claude-usage.png)

![Claude Code Desktop mockup, head drift on the evidence tab](assets/desktop-claude-head-drift.png)

![Claude Code Desktop mockup, plan tab at PLAN](assets/desktop-claude-plan.png)

### Codex Desktop (ChatGPT desktop app in Codex mode)

MCP App surfaces follow the public `openai/mcp-extensions` spec: an **inline** card in the thread (display mode `inline`) and a **thread content tab** holding the full panel (display mode `fullscreen`). Whether the installed Codex desktop build embeds MCP Apps is unverified, exactly as the UX-1 capability matrix recorded; the mockup does not claim otherwise.

![Codex Desktop mockup, failed gate with intent preview open](assets/desktop-codex-failed-gate.png)

![Codex Desktop mockup, PR receipt](assets/desktop-codex-pr-receipt.png)

![Codex Desktop mockup, stop requested with cleanup unknown](assets/desktop-codex-cancel-unknown.png)

### The shared panel on its own

![Shared panel, failed gate with detail and intent preview](assets/panel-failed-gate-overview.png)

![Shared panel, review on exact head](assets/panel-review-overview.png)

![Shared panel, cleanup unknown](assets/panel-cancel-unknown.png)

![Shared panel, usage tab](assets/panel-usage.png)

## Honesty rules carried over unchanged

- Every surface shows `DEMO • SYNTHETIC`, the provenance (`HARNESS RENDER`, `NATIVE DEMO SURFACE` or `DESKTOP MOCKUP`), `actual charge unknown`, and that intent output is preview only with `sideEffects 0` and `authorityIssued false`.
- Stale, gap, unknown-cleanup and head-drift states disable every consequential action and say why; historical PASS receipts stay visible and are marked STALE.
- The aggregate never infers green from an empty or partial gate set; LAUNCH and PLAN show the frozen plan and the count of required receipts still due.
- Share-of-known bars are arithmetic on the known subtotal only; unpriced requests are shown as UNPRICED with their reason.
- No ETA, no percent complete, no spinner, no invented dollars.

## Reproduce

```sh
cd examples/presentation-demo
npm install
npx playwright install chromium
npm test                          # includes test/cockpit.test.mjs and test/desktop.test.mjs
npm run screenshots:ux2           # writes assets/*.png and assets/manifest.json with SHA-256 per image
```

`assets/manifest.json` records the generator, Node version and SHA-256 of each image. Terminal images are real renderer output: ANSI converted to HTML spans and photographed in headless Chromium inside a drawn terminal frame. Desktop images are screenshots of the static mockup pages at 1440×960 and of the standalone panel at 700 px.

## Not done in this revision

- No installed-host recapture of Codex, Claude Code or Pi with the UX-2 renderer. The UX-1 native captures remain the only installed-host evidence; UX-2 native acceptance is the same work item as before (#38, #39, #40, #41) with a better renderer to point at.
- No embedding test in Claude Code Desktop or the Codex desktop app. The mockups show where the panel would live according to public documentation; admission of each desktop surface still needs its own capability and version check.
- The companion web app and the mobile layout from UX-1 were intentionally left as they were; the user asked for the TUI and the two desktop experiences.
- Two pre-existing tests fail on Windows only (`mcp.test.mjs` Windows path comparison and `usage.test.mjs` fresh-process replay build a `C:\C:\…` path). They are unrelated to UX-2 and were not changed here.
