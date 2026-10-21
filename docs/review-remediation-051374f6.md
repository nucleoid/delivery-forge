# Final-review additive remediation (`051374f6`)

Date: 2026-10-21

Protected base: `d34280b7f578acca04299009ca6e2bd53e6f0427`

Reviewed parent: `051374f673cca6f8a1056c71435d2448df6ecb4a`

## Finding dispositions

1. The suppression scanner recognizes ASI-separated `return`, `throw`, `yield`, and `await` regex prefixes, preserves `for` state through `for await`, and consumes a byte-zero hashbang as its own lexical line. Exact scanner regressions and matched pinned Oxlint 1.78.0 suppressed/unsuppressed fixtures cover all three reviewed bypass families.
2. Every completed anti-slop v1 persistence now hashes deterministic `anti-slop-binding.json`. It binds the repository base/head/tree, plugin identity/version/status/raw outcome, configuration and runtime closure identities, exact input-set identity and inputs, tool/version/format/executable identity, scope, final normalized outcome, and truthful authority classification. The v1 command envelope uses `<host-owned-frozen-inputs>` rather than the random private materialization path. Canonical v1 schema/serialization and legacy-adapter command bytes remain unchanged; no v1 field is added.
3. Non-number Oxlint counts and non-object diagnostics normalize to `INCOMPLETE`. The production batch host rejects the same malformed shapes before aggregation.
4. Protected-base comparison proved exact built-in tool-version matching was new to legacy-v1 authority. Legacy v1 again relies on the already detector-verified protected capability range while retaining exact format checks; exact registry tool/version/runtime pins remain intact for future-v2 validation.
5. Hosted run `39538708653` was stale at `d877cad6`. Its hard-coded `/usr/bin/node` failures are corrected by resolving the setup-provided absolute Node path and pinning CI to reviewed Node `24.21.0`. Unsupported non-Linux production closures remain non-authoritative. The unrelated Windows lifecycle, claim, Git-object deletion, and real-.NET fixture failures are outside this plugin remediation.

## Immutable pins

- anti-slop upstream `13909c9e07590f5a85d93c62712259fe14a883f7`, MIT
- plugin `0.1.2`
- `oxlint` and `@oxlint/plugins` `1.78.0`
- Node `24.21.0` for the reviewed Linux x64 GNU closure
- base closure `sha256:74eb6bfe76e45e488e4ca336bc15818b093db2672050c2fccb5f036a259e76a0`
- Effect closure `sha256:6cc88c912de3f61f066b807eda906546aa5d4786af1c7e3c6a8a20a9e7269bc4`
- retained immutable .NET SDK `10.0.401`

## Validation discipline

Every SDK, Node, npm, Oxlint, fixture, build, test, and format invocation uses outer `/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock`, a finite `/usr/bin/timeout`, and user `systemd-run --scope --collect --quiet` with `MemoryMax=1024M`, `MemorySwapMax=128M`, and `CPUQuota=100%`. SDK commands use no restore and disable build servers, node reuse, shared compilation, and parallel build. No SDK/package installation or mutation campaign runs.

No push, PR mutation, merge, deployment, rebase, amend, queue restart, or external publication is part of this remediation.

## Verified results

- Focused scanner/plugin GREEN: 130 passed, 0 failed.
- Pinned Oxlint 1.78.0: clean PASS, policy violations FAIL, malformed syntax/report shapes INCOMPLETE; all ASI statement-regex, `for await`, and byte-zero hashbang suppression/violation pairs passed.
- Unfiltered Release solution: 1,125 passed, 0 failed.
- Release solution and four external fixture helpers: 0 warnings, 0 errors.
- `dotnet format DeliveryForge.slnx --no-restore --verify-no-changes --verbosity minimal`: clean.
