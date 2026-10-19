# Native presentation prototype publication manifest

Published as a draft documentation/prototype handoff. This manifest describes immutable source provenance and current limitations; it does not claim native-host acceptance.

## Immutable source

- Source commit: `37aab4c6529dfcd361cf9f924f5dcca41423af7f`
- Source tree: `095b2ea4a9140cca3a841be58d8e9c10b28ae030`
- Source branch at freeze: `ux/native-presentations-20261019`
- Source test receipt: `node --test` — **63 passed, 0 failed**
- Native-source archive: [`assets/native-prototype-37aab4c.zip`](assets/native-prototype-37aab4c.zip)
- Archive size: `40519` bytes
- Archive SHA-256: `2469a22fdd291567823d86856edab5e6d8308a2e1cb43ad94ec3d7bacd9fc426`

The ZIP contains only the prototype `README.md`, `src/`, `data/`, `hosts/`, `scripts/demo.mjs`, and `package.json`. It has no credentials, absolute paths, private notes, screenshots, research copies, tests, companion assets, or repository metadata.

The earlier `native-prototype-f9b05e6.zip` remains available only as immutable historical evidence. It is superseded by `37aab4c…` and is not the native-test candidate.

## Public-copy portability changes

The checked-in demo under [`examples/presentation-demo`](../../../examples/presentation-demo/README.md) preserves the immutable operational adapter and projection bytes from the source commit. Two browser-only files replace a machine-specific Playwright import with the package import `playwright`, pinned to `1.58.2` in `devDependencies`. Evidence scripts default to the repository evidence directory and accept `FORGE_EVIDENCE_DIR` as an override. Public documentation uses repository-relative paths and names the pending telemetry issue by title rather than assigning a GitHub number. These publication-only changes do not alter fixture facts, projection, usage accounting, intent checks, terminal rendering, or host adapter behavior.

## Evidence status

[`evidence/`](evidence/) contains deterministic raw text/ANSI captures, static PNGs, capture provenance, and the implementation report. It includes Codex and Pi usage-detail harness captures. All screenshots are labelled **HARNESS RENDER** or **COMPANION DEMO**. None was captured from an installed Codex, Claude Code, or Pi host.

Raw RED logs and machine-specific receipts are intentionally excluded. The sanitized history remains in the implementation report: the original repair checkpoint reached **44 passed, 0 failed**; final source `37aab4c…` adds the usage/cost slice and records **63 passed, 0 failed**.

## Pending

- final immutable-source admission review after the R1–R4 and usage/cost changes;
- installed no-provider rendering and keyboard/layout checks on the pinned host profiles;
- native capture receipts and screenshots;
- final telemetry issue number/URL binding and issue publication;
- any production projection/controller integration.

The pull request carrying this bundle remains draft until those gates are resolved or explicitly descoped.
