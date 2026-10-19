# Delivery Forge native presentation prototype

This bundle preserves the frozen UX-1 proposal, a deterministic synthetic demo, and its harness evidence. It is intended for design review and installed-host testing; it is **not** a live Delivery Forge adapter.

## Contents

- [Capability matrix](capability-matrix.md)
- [Project state at design freeze](project-state.md)
- [Frozen design](design.md)
- [Presentation interaction contract](interaction-contract.md)
- [Projection completeness addendum](projection-completeness-addendum.md)
- [Portable demo source](../../../examples/presentation-demo/README.md)
- [Publication manifest](publication-manifest.md)
- [Synthetic evidence](evidence/)
- [Immutable native-source ZIP](assets/native-prototype-f9b05e6.zip)

## Status

- Prototype source commit: `f9b05e6bdf6c2d6b869323f6a6aa08297b862a63`
- Prototype source tree: `6d09707aa1a5a83a67d49db6f9879286ccec55f1`
- Deterministic test result at that source: **44 passed, 0 failed**
- Captures in this bundle: **HARNESS RENDER** and **COMPANION DEMO** only
- Installed Codex, Claude Code, and Pi rendering: **pending / unverified**
- Independent Astra design/source review: **pending**

Every fixture is synthetic. The demo performs no model call, network request, Forge run, authority grant, repository write, PR publication, integration call, process kill, or deployment. Intent output is preview-only.

## Reproduce locally

```sh
cd examples/presentation-demo
npm install
npm test
npm run demo -- failed-gate 80
```

Playwright is pinned as a development dependency solely for the companion browser tests and screenshot generator. The projection, terminal renderers, and host demo packages do not require it.

## Native testing boundary

The host packages are ready for separately authorized, no-provider installed-profile testing. A native screenshot must record the exact host/version/surface/action and must retain `DEMO • SYNTHETIC` plus `NATIVE DEMO SURFACE`. Rendering fixture pixels in an installed host does not prove production state trust, authority, side effects, or live Forge admission.
