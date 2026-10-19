# Evidence index

## Final source (`e9eb721…`)

- [Implementation report](implementation-report.md) — final telemetry safety/durability source and 70/70 test receipt.
- [Independent final design review](final-design-review.md) — bounded synthetic-demo admission and remaining production limitations.
- [JSONL durability receipt](usage-jsonl-durability.txt) — fsynced append, fresh-process replay, duplicate stability, and damaged-tail uncertainty.
- [Preset byte-parity receipt](preset-byte-parity-37aab4c.txt) — five renderer views unchanged from the native-capture source.

## Installed-host captures (`37aab4c…`)

The 14 public PNGs are in [`../assets/`](../assets/) with per-file hashes, dimensions, actions, versions, and provenance in [`native-capture-ledger-37aab4c.json`](../assets/native-capture-ledger-37aab4c.json).

- Codex `0.153.4`: launch, execute, failed gate, usage.
- Claude Code `2.1.303`: launch, execute, failed gate, missing-state fallback.
- Pi `0.80.7`: launch, execute, failed gate, usage, evidence selection, unknown-scenario rejection.

These captures prove installed-host presentation of deterministic synthetic presets at source `37aab4c…`. They do not prove installed execution of final source `e9eb721…`, model-driven MCP/skill paths, production telemetry, or actual billing.

## Harness and companion captures

The remaining text, ANSI, and PNG files in this directory are deterministic **HARNESS RENDER** or **COMPANION DEMO** outputs. They are useful for implementation and regression review but are not installed-host screenshots.
