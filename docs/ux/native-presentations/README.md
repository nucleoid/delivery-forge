# Delivery Forge native presentation prototype

This bundle preserves the frozen UX-1 proposal, a deterministic synthetic demo, and its harness evidence. It is intended for design review and installed-host testing; it is **not** a live Delivery Forge adapter.

## Contents

- [Capability matrix](capability-matrix.md)
- [Project state at design freeze](project-state.md)
- [Frozen design](design.md)
- [Presentation interaction contract](interaction-contract.md)
- [Projection completeness addendum](projection-completeness-addendum.md)
- [Usage and API-equivalent cost design](usage-cost-design.md)
- [Usage implementation notes](usage-implementation-notes.md)
- [Usage issue plan — GitHub number pending](usage-cost-issue-plan.md)
- [Portable demo source](../../../examples/presentation-demo/README.md)
- [Publication manifest](publication-manifest.md)
- [Synthetic evidence](evidence/)
- [Immutable final source ZIP](assets/native-prototype-37aab4c.zip)

## Status

- Prototype source commit: `37aab4c6529dfcd361cf9f924f5dcca41423af7f`
- Prototype source tree: `095b2ea4a9140cca3a841be58d8e9c10b28ae030`
- Deterministic test result at that source: **63 passed, 0 failed**
- Usage display: synthetic per-model tokens and **USD API-EQUIVALENT ESTIMATE** only; actual invoice charge remains unknown
- Captures in this bundle: **HARNESS RENDER** and **COMPANION DEMO** only
- Installed Codex, Claude Code, and Pi rendering: **pending / unverified**
- Final immutable-source admission review: **pending**

Every fixture is synthetic. The demo performs no model call, network request, Forge run, authority grant, repository write, PR publication, integration call, process kill, or deployment. Intent output is preview-only.

## Reproduce locally

```sh
cd examples/presentation-demo
npm install
npm test
npm run demo -- failed-gate 80
npm run demo -- failed-gate 80 --view usage
```

Playwright is pinned as a development dependency solely for the companion browser tests and screenshot generator. The projection, terminal renderers, and host demo packages do not require it.

## Native testing boundary

The host packages are ready for separately authorized, no-provider installed-profile testing. A native screenshot must record the exact host/version/surface/action and must retain `DEMO • SYNTHETIC` plus `NATIVE DEMO SURFACE`. Rendering fixture pixels in an installed host does not prove production state trust, authority, side effects, or live Forge admission.
