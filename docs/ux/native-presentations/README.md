# Delivery Forge native presentation prototype

This package preserves the frozen UX-1 design, final synthetic prototype source, deterministic harness evidence, and source-bound installed-host captures. It is an implementation-ready design handoff, **not** a live Delivery Forge adapter or production telemetry certification.

## Start here

- [Capability matrix](capability-matrix.md)
- [Project state at design freeze](project-state.md)
- [Frozen design](design.md)
- [Presentation interaction contract](interaction-contract.md)
- [Projection completeness addendum](projection-completeness-addendum.md)
- [Usage and API-equivalent cost design](usage-cost-design.md)
- [Usage implementation notes](usage-implementation-notes.md)
- [Usage issue plan — GitHub number pending](usage-cost-issue-plan.md)
- [Portable final demo source](../../../examples/presentation-demo/README.md)
- [Publication manifest](publication-manifest.md)
- [Evidence index](evidence/README.md)
- [Final immutable source ZIP](assets/native-prototype-e9eb721.zip)

The implementation issue set is prepared but not yet published. Links remain plain pending text until the seven real GitHub issues are created and their actual URLs are bound.

## Two source identities

- **Final prototype source:** `e9eb721c664440b61352547d694c2c83798e07ed`, tree `f51d3abcc6998f0be1f6e4d4f9b8a6c742300cf9`; independently **70 passed, 0 failed**. This head adds the four telemetry safety fixes and bounded synthetic JSONL append/fsync/fresh-process replay. It has Node/harness verification only.
- **Installed-host capture source:** `37aab4c6529dfcd361cf9f924f5dcca41423af7f`; independently **63 passed, 0 failed** in its source tree. Its distributed native-test ZIP contained no tests, so a laptop `node --test` discovered **0 tests**; that zero-test invocation is not a pass receipt.
- **Parity bridge:** five public renderer views are byte-identical between `37aab4c…` and `e9eb721…`. This establishes retained view content, not installed-host retesting of the final source.

## Native evidence

Twelve sanitized frames are accepted and bind to source `37aab4c…`. Two published files are **rejected pending replacement**: `codex-native-0.153.4-execute.png` shows a Windows Terminal file-not-found result rather than the intended execute state, and `pi-native-0.80.7-usage.png` exposes local-path warnings/footer text.

- Codex CLI `0.153.4`: three accepted literal local user-shell transcript frames; the execute frame is rejected pending replacement. No model prompt, MCP tool invocation, skill execution, or custom-footer claim.
- Claude Code `2.1.303`: four compact status-line frames. Version `2.1.302` was the historical preflight target; no expanded native usage panel or model-driven skill invocation was captured.
- Pi `0.80.7`: five accepted offline widget/panel frames; the usage frame is rejected pending replacement.

The preliminary Codex isolation attempt used an empty MCP table that merged with existing configuration and began startup; it was interrupted and is **not** counted as a clean run. Final captures explicitly disabled all configured MCP servers and verified that disabled state. No private server names, profile paths, or account metadata are published.

Only the twelve accepted native frames prove installed-host pixels and bounded keyboard behavior for their known synthetic presets. File hashes and ledger provenance do not constitute pixel acceptance. The two rejected files prove nothing about their intended states and must not be used as evidence. None of the frames prove production collectors, model-driven MCP/skill paths, real prices, invoices, actual charges, workflow authority, or final-source native acceptance.

## Safety boundary

Every fixture is synthetic. Money is labelled **USD API-EQUIVALENT ESTIMATE**; actual invoice charge remains unknown. The demo performs no model call, network request, live Forge run, authority grant, repository write, PR publication, integration call, process kill, or deployment. Intent output is preview-only.

## Reproduce the final source locally

```sh
cd examples/presentation-demo
npm install
npm test
npm run demo -- failed-gate 80
npm run demo -- failed-gate 80 --view usage
npm run capture:usage
```

Playwright `1.58.2` is pinned as a development dependency solely for companion browser tests and screenshot generation. Projection, terminal rendering, host demo packages, and the bounded synthetic JSONL demonstration do not require it.
