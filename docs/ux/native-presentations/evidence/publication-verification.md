# Public-copy verification receipt

Date: 2026-10-19 UTC

- Immutable prototype source: `37aab4c6529dfcd361cf9f924f5dcca41423af7f`
- Immutable prototype tree: `095b2ea4a9140cca3a841be58d8e9c10b28ae030`
- Immutable source ZIP: `native-prototype-37aab4c.zip`, SHA-256 `2469a22fdd291567823d86856edab5e6d8308a2e1cb43ad94ec3d7bacd9fc426`, 40,519 bytes
- Operational `src/`, `data/`, `hosts/`, companion, demo script, and non-browser test bytes: exact match to the immutable source commit
- Publication-only portability changes: package-level Playwright `1.58.2` import/pin, repository-relative evidence output, and browser test import
- Syntax checks: projector, Pi extension, Claude status line, Codex MCP endpoint, capture script, and screenshot script passed
- Test command: `npm test`
- Result: **63 passed, 0 failed**
- Demo smoke test: `npm run demo -- failed-gate 80` exited successfully and emitted `HARNESS RENDER` plus `DEMO • SYNTHETIC`
- Operational parity: **28 immutable source files byte-identical**; only documented browser-test/import, evidence-output, package metadata and public-documentation portability changes differ
- External/network/model/installed-host tests: not run

This receipt verifies the portable public copy only. It does not admit installed native rendering or production integration.
