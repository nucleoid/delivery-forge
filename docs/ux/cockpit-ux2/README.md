# FORGE cockpit — UX-2 prototypes for the terminal, Claude Code Desktop and Codex Desktop

**Revision UX-2, 2026-10-20. Prototype only: HARNESS RENDER and DESKTOP MOCKUP images plus NATIVE installed-host captures (see [Native captures](#native-captures)), synthetic fixtures, zero live adapters.** This revision answers the UX half of [epic #36](https://github.com/nucleoid/delivery-forge/issues/36): one evidence model, presented well on every surface a developer actually sits in front of. It supersedes the presentation layer of PR #33 while keeping its projection, fixtures, usage ledger, intent preview and honesty rules untouched. The UX-1 material under [`../native-presentations`](../native-presentations/README.md) remains the frozen baseline that the fourteen installed-host captures were taken from.

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
| Codex CLI 0.162.1 (UX-1 pin 0.153.4) | `!node scripts/demo.mjs <state> 80` user-shell transcript; `forge_demo_status` MCP text | Default MCP view is the framed cockpit; `view: "status"` and `view: "usage"` added; `ascii: true` for transcripts that cannot draw box characters; UX-1 views kept as `legacy-*` | Still a disciplined transcript. No footer item, no persistent widget. Captured on the installed host, see [Native captures](#native-captures) |
| Claude Code CLI 2.1.303 | `statusLine` command | `hosts/claude/statusline.mjs` emits the four-line UX-2 status with ANSI colour unless `NO_COLOR=1`; all fail-closed paths unchanged | Reads the explicit fixture state file only. Captured on the installed host, see [Native captures](#native-captures) |
| Pi 1.3.0 (UX-1 pin 0.80.7) | widget above editor, `/forge-demo detail`, `/forge-demo usage` | Widget renders the status line; detail and usage panels use the new list, detail and usage renderers; colour only when Pi hands the component a real theme | Same deterministic commands, same `ctx.mode === "tui"` guard, same branch restore and shutdown. Captured on the installed host, see [Native captures](#native-captures) |

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

## Native captures

**Installed-host evidence for the UX-2 renderer, captured 2026-10-20 on a Windows 11 laptop.** Every image in this section is labelled **NATIVE**: a cropped screen capture of the real host rendering the UX-2 output, driven only by local input (`!` user-shell lines in Codex, a session-scoped `statusLine` in Claude Code, `/forge-demo` commands and panel keys in Pi). No prompt was sent to any model for a published frame. Host version, surface, source commit, fixture, command, capture time, dimensions and SHA-256 for each file are in the [UX-2 capture ledger](assets/native-capture-ledger-dcfafae.json), together with the host launch lines, the MCP-disabled receipt, one defect the captures exposed, two incidents during capture and the caveats. Parent pixel review has not yet accepted these images.

**Versions.** By owner decision this revision captures the latest installed version of each TUI rather than the UX-1 pinned profiles, which supersedes the "no host upgrade" line in [epic #36](https://github.com/nucleoid/delivery-forge/issues/36) for UX-2; the owner may still override on the PR. The capability matrix still lists the UX-1 pins and is not updated here.

| Host | UX-1 pinned | Captured | Frames |
|---|---|---|---|
| Claude Code CLI | 2.1.303 | **2.1.303** (current; exact match) | 4 |
| Codex CLI | 0.153.4 | **0.162.1** (installed, equals the latest npm publish) | 3 |
| Pi | 0.80.7 | **1.3.0** (installed by the owner during capture) | 6 |

### Claude Code 2.1.303, native status line

Session-scoped `--settings` pointing `statusLine` at `hosts/claude/statusline.mjs` with a temp copy of the fixture state file, `--strict-mcp-config` with no servers, no prompt, one fresh start per state. The `auto mode on` row and the token counter are the host's own.

![NATIVE Claude Code 2.1.303: launch](assets/claude-native-2.1.303-ux2-launch.png)

![NATIVE Claude Code 2.1.303: execute](assets/claude-native-2.1.303-ux2-execute.png)

![NATIVE Claude Code 2.1.303: failed gate](assets/claude-native-2.1.303-ux2-failed-gate.png)

![NATIVE Claude Code 2.1.303: missing state fallback](assets/claude-native-2.1.303-ux2-missing-state.png)

At 100 columns Claude Code elides the end of the first status row with an ellipsis; the frames above are at 128 columns.

### Codex CLI 0.162.1, native user-shell transcript

Every configured MCP server disabled by `-c` override (receipt in the ledger), a `-c projects` trust override instead of a config write, and an empty task directory holding one junction to `examples/presentation-demo`. Codex prints `!` output monochrome, so these show the UX-2 shapes without colour.

![NATIVE Codex 0.162.1: UX-2 cockpit, failed gate](assets/codex-native-0.162.1-ux2-cockpit.png)

![NATIVE Codex 0.162.1: UX-2 status view below the cockpit](assets/codex-native-0.162.1-ux2-status.png)

![NATIVE Codex 0.162.1: UX-2 usage panel](assets/codex-native-0.162.1-ux2-usage-panel.png)

### Pi 1.3.0, native widget and custom panels

`pi -e hosts/pi/forge-demo-extension.js --no-extensions --no-session --no-context-files`. The installed 1.3.0 extension types were checked against what the extension assumes (`registerCommand`, `setWidget` with placement, `setStatus`, `custom`, `appendEntry`, `sendMessage`, `sessionManager.getBranch`); no signature moved.

The first native widget frame exposed a UX-2 defect: with a real host theme the widget scrubbed the ESC byte out of its own colour sequences and printed `[1mFORGE DEMO ... [0m` as text. That is fixed on the base branch (`dcfafae`, `clean()` keeps ESC, with a real-theme widget test), and the frames below come from the fixed source.

![NATIVE Pi 1.3.0: widget at launch](assets/pi-native-1.3.0-ux2-widget-launch.png)

![NATIVE Pi 1.3.0: widget at failed gate](assets/pi-native-1.3.0-ux2-widget-failed-gate.png)

![NATIVE Pi 1.3.0: /forge-demo detail, evidence list](assets/pi-native-1.3.0-ux2-detail-list.png)

![NATIVE Pi 1.3.0: selection moved with j](assets/pi-native-1.3.0-ux2-detail-selection.png)

![NATIVE Pi 1.3.0: Enter opens evidence detail](assets/pi-native-1.3.0-ux2-detail-evidence.png)

![NATIVE Pi 1.3.0: /forge-demo usage](assets/pi-native-1.3.0-ux2-usage.png)

### What this changes about the claims above

- The three "not re-captured on the installed host for UX-2" notes in the host-wiring table are superseded by this section.
- Admission of 0.162.1 and 1.3.0 in the capability matrix, and any UX-1 ledger cross-reference, remain follow-up documentation work.

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
npm test                          # 87 tests, includes test/cockpit.test.mjs and test/desktop.test.mjs
npm run screenshots:ux2           # writes assets/*.png and assets/manifest.json with SHA-256 per image
```

`assets/manifest.json` records the generator, Node version and SHA-256 of each image. Terminal images are real renderer output: ANSI converted to HTML spans and photographed in headless Chromium inside a drawn terminal frame. Desktop images are screenshots of the static mockup pages at 1440×960 and of the standalone panel at 700 px.

## Not done in this revision

- Installed-host captures of Codex, Claude Code and Pi with the UX-2 renderer now exist (see [Native captures](#native-captures)), but they have not been through parent pixel review, and the capability matrix still lists the UX-1 pins; acceptance of the native surfaces remains the work items #38, #39, #40 and #41.
- No embedding test in Claude Code Desktop or the Codex desktop app. The mockups show where the panel would live according to public documentation; admission of each desktop surface still needs its own capability and version check.
- The companion web app and the mobile layout from UX-1 were intentionally left as they were; the user asked for the TUI and the two desktop experiences.
