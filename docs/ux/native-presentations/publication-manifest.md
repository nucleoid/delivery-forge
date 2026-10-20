# Native presentation prototype publication manifest

Published in draft PR #33. The repository is public. This manifest separates final source, installed-host evidence, and publication receipts; none may be promoted into production-runtime proof.

## Final immutable source

- Source commit: `e9eb721c664440b61352547d694c2c83798e07ed`
- Source tree: `f51d3abcc6998f0be1f6e4d4f9b8a6c742300cf9`
- Source branch at freeze: `ux/native-presentations-20261019`
- Independent source test receipt: `node --test` — **70 passed, 0 failed**
- Archive: [`assets/native-prototype-e9eb721.zip`](assets/native-prototype-e9eb721.zip)
- Archive size: `42150` bytes
- Archive SHA-256: `b37350f01d46dbb78005a4c13d7be5344e7eff89cd9c0de80a5195a938189c1d`

The final ZIP contains committed `README.md`, `package.json`, `data/`, `src/`, `hosts/`, `scripts/demo.mjs`, `scripts/usage-ledger.mjs`, and `scripts/capture-usage-durability.mjs`. It excludes repository metadata, tests, companion assets, evidence, credentials, absolute private paths, and publication worktrees.

The earlier [`native-prototype-37aab4c.zip`](assets/native-prototype-37aab4c.zip) is retained because it is the exact archive used for installed-host captures. `native-prototype-f9b05e6.zip` is historical and superseded.

## Final-source behavior and limits

Final source adds provider/backend/tier/effective-SKU rate binding, deep checkpoint validation, all-unpriced null rendering, and a bounded explicit-path synthetic JSONL demonstration. The ledger admits only exact bundled fixture records, caps files at 256 KiB / 256 records, fsyncs appends, replays across fresh Node processes, and surfaces malformed/truncated tails as uncertainty without mutating the file.

It has **no live collectors**, generic importer, database, daemon, host transcript reader, current provider rate catalog, invoice integration, or actual-charge field telemetry. Its tested story remains `USD0.044225`, 19,300 observed synthetic tokens, one unpriced request, `PARTIAL`, actual charge unknown.

## Installed-host evidence

Fourteen native images are now individually accepted after parent pixel review, all bound to source `37aab4c…`: Codex 0.153.4 ×4, Claude Code 2.1.303 ×4, Pi 0.80.7 ×6. The parent freshly re-executed the Codex execute preset and verified a precise Pi usage crop. See [parent pixel review](evidence/parent-pixel-review.md) and the [current capture ledger](assets/native-capture-ledger-37aab4c.json).

The native-test ZIP omitted tests, so its laptop `node --test` discovered **0 tests**. The independent source-tree receipt for `37aab4c…` was 63/63. The preliminary Codex MCP isolation mistake is retained as a caveat; only the later explicitly-disabled configuration is admitted. No model-driven MCP or Claude expanded-skill path is claimed.

Five renderer outputs—launch, execute, failed-gate expanded, failed-gate usage, and failed-gate compact—match byte-for-byte between source `37aab4c…` and final source `e9eb721…`. See [the parity receipt](evidence/preset-byte-parity-37aab4c.txt). This is not a native rerun of final source.

## Public-copy portability changes

The checked-in demo under [`examples/presentation-demo`](../../../examples/presentation-demo/README.md) preserves immutable operational source bytes from `e9eb721…`. Browser-only files use package import `playwright`, pinned to `1.58.2`; evidence scripts use repository-relative output with optional `FORGE_EVIDENCE_DIR`; public docs use repository-relative links. These publication-only changes do not alter fixture facts, projection, usage accounting, JSONL behavior, intent checks, terminal rendering, or host adapter behavior.

## Evidence and issue status

[Evidence](evidence/README.md) includes the source implementation report, independent design review, durability/parity receipts, deterministic harness/companion captures, and source-bound native PNGs. Harness/companion images remain explicitly distinct from installed-host captures.

Seven detailed issues are published and cross-linked: [#36–#42](issues.md), including [run-wide telemetry #42](https://github.com/nucleoid/delivery-forge/issues/42). Exact issue bodies were read back from GitHub; all14 immutable images were downloaded and hash-verified, and issue38 image rendering was checked in the laptop browser. PR #33 remains draft for owner review; no core runtime PR was changed or merged.
