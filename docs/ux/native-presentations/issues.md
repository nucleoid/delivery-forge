# Implementation issue handoff

All seven issues have been created, cross-linked, and read back from GitHub. They include scope, non-goals, dependencies, authoritative host references, reproduction recipes, acceptance matrices and immutable prototype evidence. This publishes implementation requirements; it does not start or complete production runtime work.

- [[UX] One evidence-led Forge experience across Codex, Claude Code and Pi](https://github.com/nucleoid/delivery-forge/issues/36)
- [[UX] Project trusted Forge state into one read-only presentation contract](https://github.com/nucleoid/delivery-forge/issues/37)
- [[UX/Codex] Deliver compact native transcript presentation without inventing a footer API](https://github.com/nucleoid/delivery-forge/issues/38)
- [[UX/Claude] Add an opt-in persistent statusline with evidence drilldown](https://github.com/nucleoid/delivery-forge/issues/39)
- [[UX/Pi] Build a keyboard-first native cockpit on the admitted extension API](https://github.com/nucleoid/delivery-forge/issues/40)
- [[UX] Verify installed-host presentation, recovery honesty and source-bound evidence](https://github.com/nucleoid/delivery-forge/issues/41)
- [[UX/Telemetry] Track per-model tokens and live USD API-equivalent estimates](https://github.com/nucleoid/delivery-forge/issues/42)

Recommended sequence: common projection + run-wide telemetry contracts, then host adapters, then cross-host acceptance; the epic tracks the whole experience.

## Evidence delivered

-14 accepted native screenshots: Codex0.153.4×4, Claude2.1.303×4, Pi0.80.7×6, all at prototype37aab4c.
- Final prototypee9eb721:70/70 independent tests, finite synthetic JSONL append/fsync/reopen and replay-safe totals; five preset outputs are byte-identical to the captured version.
- Real run-wide producer instrumentation, durable multi-host accounting and real price catalogs remain required work in the telemetry issue.
- GitHub issue38 was opened in the laptop browser and its embedded native execute, failed-gate and usage images visually checked after publication. All14 immutable PNG downloads were separately hash-verified.
- [Capture errata and parent pixel review](evidence/parent-pixel-review.md) record rejected older revisions and accepted replacements without rewriting history.

[Evidence index](evidence/README.md) · [Prototype source](../../../examples/presentation-demo/README.md) · [Draft asset/source PR](https://github.com/nucleoid/delivery-forge/pull/33).
