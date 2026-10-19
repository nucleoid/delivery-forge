# Evidence index

## Final source (`e9eb721…`)

- [Implementation report](implementation-report.md) — final telemetry safety/durability source and 70/70 test receipt.
- [Independent final design review](final-design-review.md) — bounded synthetic-demo admission and remaining production limitations.
- [JSONL durability receipt](usage-jsonl-durability.txt) — fsynced append, fresh-process replay, duplicate stability, and damaged-tail uncertainty.
- [Preset byte-parity receipt](preset-byte-parity-37aab4c.txt) — five renderer views unchanged from the native-capture source.

## Installed-host captures (`37aab4c…`)

Fourteen PNG files are currently present in [`../assets/`](../assets/), but only twelve are accepted. `codex-native-0.153.4-execute.png` and `pi-native-0.80.7-usage.png` are rejected pending parent-owned replacement. The current ledger records file provenance only; hashes and dimensions are not pixel-acceptance evidence.

- Codex `0.153.4`: launch, failed gate, and usage accepted; execute rejected.
- Claude Code `2.1.303`: launch, execute, failed gate, missing-state fallback.
- Pi `0.80.7`: launch, execute, failed gate, evidence selection, and unknown-scenario rejection accepted; usage rejected.

The twelve accepted captures prove installed-host presentation of their deterministic synthetic presets at source `37aab4c…`. The two rejected files do not. None prove installed execution of final source `e9eb721…`, model-driven MCP/skill paths, production telemetry, or actual billing.

## Harness and companion captures

The remaining text, ANSI, and PNG files in this directory are deterministic **HARNESS RENDER** or **COMPANION DEMO** outputs. They are useful for implementation and regression review but are not installed-host screenshots.
