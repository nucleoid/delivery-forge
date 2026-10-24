---
name: delivery-forge
description: Plan and deliver a bounded repository change with Delivery Forge, preserving immutable authorization, actual worker evidence, independent review and quality gates. Use for delivery planning, implementation, PR, merge or recovery; not release or deployment.
---

Use the installed `delivery-forge` helper as the deterministic owner API boundary. Read `delivery-forge --help` and `--version` from the selected installation before use. Select an explicit mode: `plan`, `implement`, `pr`, `merge`, `resume`, or `capabilities`. Never treat replay output as an execution input.

For planning and trusted input, read [the command protocol](references/tool-protocol.md). For actual Codex, Claude Code or Pi dispatch, read [host bindings](references/host-bindings.md). For packaging, read [installation](docs/installation.md) or [uninstallation](docs/uninstallation.md).

Keep the original user's ceiling. A plan cannot authorize implementation; implementation cannot authorize a PR or merge. Resume can lower or preserve a stored ceiling and cannot raise it. A config file, stored receipt, model assertion, successful parser, or fixture cannot issue a private runtime or publication capability.

Use the selected host's actual exposed tools through the named typed adapter, or the explicitly selected installed native transport constructed by the same trusted standalone owner. Bind exact request, accepted session, completion and controls to the same actual owner. Preserve entered operations and unknown outcomes; do not retry a dispatch or publication with an unknown prior effect. Report `INCOMPLETE` when the current host lacks required actual capabilities. Do not invoke an arbitrary agent CLI, alternate model, paid provider or external service as a fallback. A configured native transport is a selected primary route only after genuine version-specific installed conformance; it preserves existing sign-in, model and permission defaults.

Before PR or merge, require the real protected quality evidence and independent review bound to the current revision. This skill supplies no coverage waiver and no release/deployment mode. The standalone owner requires an independently pinned root configuration and actual installed-host conformance before native dispatch. Missing records remain INCOMPLETE. No production publication issuer is fabricated: parent publication integration and its protected quality/review proofs remain required. Read [standalone ownership](docs/standalone-ownership.md) before preparing that root. Diagnostic profiles and source-only transports are not conformance.
