# Delivery Forge native presentation prototype

This package preserves the frozen UX-1 design, final synthetic prototype source, deterministic harness evidence, and source-bound installed-host captures. It is an implementation-ready design handoff, **not** a live Delivery Forge adapter or production telemetry certification.

> **UX-2 revision (2026-10-20):** the terminal cockpit, status line, Pi panels and Codex text were redesigned, and Claude Desktop / Codex Desktop mockups were added. Start at [docs/ux/cockpit-ux2](../cockpit-ux2/README.md). The UX-1 material below is retained as the frozen baseline the native captures were taken from.

## Start here

- [Capability matrix](capability-matrix.md)
- [Project state at design freeze](project-state.md)
- [Frozen design](design.md)
- [Presentation interaction contract](interaction-contract.md)
- [Projection completeness addendum](projection-completeness-addendum.md)
- [Usage and API-equivalent cost design](usage-cost-design.md)
- [Usage implementation notes](usage-implementation-notes.md)
- [Run-wide usage implementation issue #42](https://github.com/nucleoid/delivery-forge/issues/42) · [detailed usage plan](usage-cost-issue-plan.md)
- [Portable final demo source](../../../examples/presentation-demo/README.md)
- [Publication manifest](publication-manifest.md)
- [Evidence index](evidence/README.md)
- [Final immutable source ZIP](assets/native-prototype-e9eb721.zip)

The seven implementation issues are published and cross-linked: start at [epic #36](https://github.com/nucleoid/delivery-forge/issues/36) or the [complete issue handoff](issues.md). Each host issue embeds multiple real native screenshots.

## Two source identities

- **Final prototype source:** `e9eb721c664440b61352547d694c2c83798e07ed`, tree `f51d3abcc6998f0be1f6e4d4f9b8a6c742300cf9`; independently **70 passed, 0 failed**. This head adds the four telemetry safety fixes and bounded synthetic JSONL append/fsync/fresh-process replay. It has Node/harness verification only.
- **Installed-host capture source:** `37aab4c6529dfcd361cf9f924f5dcca41423af7f`; independently **63 passed, 0 failed** in its source tree. Its distributed native-test ZIP contained no tests, so a laptop `node --test` discovered **0 tests**; that zero-test invocation is not a pass receipt.
- **Parity bridge:** five public renderer views are byte-identical between `37aab4c…` and `e9eb721…`. This establishes retained view content, not installed-host retesting of the final source.

## Native evidence

Fourteen native images are now individually accepted after parent pixel review, all bound to source `37aab4c…`: Codex 0.153.4 ×4, Claude Code 2.1.303 ×4, Pi 0.80.7 ×6. The parent freshly re-executed the Codex execute preset and verified a precise Pi usage crop. See [parent pixel review](evidence/parent-pixel-review.md) and the [current capture ledger](assets/native-capture-ledger-37aab4c.json).

Codex is a native local user-shell transcript, Claude a compact native statusline, and Pi an offline extension widget/detail panel. No model-driven MCP/skill execution or custom Codex footer is claimed. Claude startup refresh was exercised, not idle streaming. Native viewport resize/persistence and production collectors remain implementation acceptance work.

Two earlier correction attempts were rejected: one showed an incorrect/error state and two included local paths. Current replacements have been personally inspected. Superseded Git history remains retained; no credential values were observed. The earlier interrupted Codex MCP-startup configuration mistake remains recorded, so no all-network-silence claim is made.

## Safety boundary

Every fixture is synthetic. Money is labelled **USD API-EQUIVALENT ESTIMATE**; actual invoice charge remains unknown. The demo performs no model call, network request, live Forge run, authority grant, repository write, PR publication, integration call, process kill, or deployment. Intent output is preview-only.

## Reproduce the final source locally

```sh
cd examples/presentation-demo
npm install
npx playwright install chromium
npm test
npm run demo -- failed-gate 80
npm run demo -- failed-gate 80 --view usage
npm run capture:usage
```

Playwright `1.58.2` is pinned as a development dependency solely for companion browser tests and screenshot generation. Projection, terminal rendering, host demo packages, and the bounded synthetic JSONL demonstration do not require it.
