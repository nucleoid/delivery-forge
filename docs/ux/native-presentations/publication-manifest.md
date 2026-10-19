# Native presentation prototype publication manifest

Published as a draft documentation/prototype handoff. This manifest describes immutable source provenance and current limitations; it does not claim native-host acceptance.

## Immutable source

- Source commit: `f9b05e6bdf6c2d6b869323f6a6aa08297b862a63`
- Source tree: `6d09707aa1a5a83a67d49db6f9879286ccec55f1`
- Source branch at freeze: `ux/native-presentations-20261019`
- Source test receipt: `node --test` — **44 passed, 0 failed**
- Native-source archive: [`assets/native-prototype-f9b05e6.zip`](assets/native-prototype-f9b05e6.zip)
- Archive size: `24734` bytes
- Archive SHA-256: `2a21be3d1de4312e0a385532006ca1074ac6bb1184ebc5c1c039fcd44b4fadae`

The ZIP contains only `src/`, `data/`, `hosts/`, `scripts/demo.mjs`, and `package.json`. It has no dependencies, credentials, absolute paths, private notes, screenshots, research copies, or repository metadata.

## Public-copy portability changes

The checked-in demo under [`examples/presentation-demo`](../../../examples/presentation-demo/README.md) preserves the immutable operational adapter and projection bytes from the source commit. Two browser-only files replace a machine-specific Playwright import with the package import `playwright`, pinned to `1.58.2` in `devDependencies`. Evidence scripts default to the repository evidence directory and accept `FORGE_EVIDENCE_DIR` as an override. These publication-only changes do not alter fixture facts, projection, intent checks, terminal rendering, or host adapter behavior.

## Evidence status

[`evidence/`](evidence/) contains deterministic raw text/ANSI captures, static PNGs, capture provenance, and the implementation report. All screenshots are labelled **HARNESS RENDER** or **COMPANION DEMO**. None was captured from an installed Codex, Claude Code, or Pi host.

The original RED log was intentionally excluded because its stack traces contained a private filesystem path. Its sanitized result remains recorded in the implementation report: baseline `3a97fe0d7148459ee5b74e18fb57ba3604292026` had **33 passed, 8 failed**; repaired source `f9b05e6…` had **44 passed, 0 failed**.

## Pending

- independent Astra design/source review;
- installed no-provider rendering and keyboard/layout checks on the pinned host profiles;
- native capture receipts and screenshots;
- final issue URL binding and issue publication;
- any production projection/controller integration.

The pull request carrying this bundle remains draft until those gates are resolved or explicitly descoped.
