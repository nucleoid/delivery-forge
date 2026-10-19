# Public-copy verification receipt

Date: 2026-10-19 UTC

- Immutable prototype source: `f9b05e6bdf6c2d6b869323f6a6aa08297b862a63`
- Immutable prototype tree: `6d09707aa1a5a83a67d49db6f9879286ccec55f1`
- Operational `src/`, `data/`, `hosts/`, companion, demo script, and non-browser test bytes: exact match to the immutable source commit
- Publication-only portability changes: package-level Playwright `1.58.2` import/pin, repository-relative evidence output, and browser test import
- Syntax checks: projector, Pi extension, Claude status line, Codex MCP endpoint, capture script, and screenshot script passed
- Test command: `npm test`
- Result: **44 passed, 0 failed**
- Demo smoke test: `npm run demo -- failed-gate 80` exited successfully and emitted `HARNESS RENDER` plus `DEMO • SYNTHETIC`
- External/network/model/installed-host tests: not run

This receipt verifies the portable public copy only. It does not admit installed native rendering or production integration.
