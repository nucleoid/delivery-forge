# Final-review remediation disposition (`09ad186`)

Date: 2026-10-20

Protected base: `d34280b7f578acca04299009ca6e2bd53e6f0427`

Reviewed predecessor: `09ad186342c34b418c9c51e1740449d9dfa67355` (`9dfb33191897620ffd749235d17e102a673abd8d`)

The complete `.final-review-09ad-output.txt` was read before remediation. Every grounded finding was traced against the contracts, runner, Git reader, adapters, trusted middle-driver composition, and tests.

## Finding dispositions

1. **Suppression bypass — remediated.** Authoritative scope no longer depends on the heuristic JavaScript lexer. A linear conservative scan examines every changed non-ignored blob for ESLint/Oxlint directive spellings, checks cancellation/deadline every 16 KiB, and fails closed at 4 MiB. Blanket directives, anti-slop and `oxc/no-accumulating-spread` directives, malformed boundaries, and ambiguous literal/prose occurrences cannot authorize PASS. Narrow unrelated directives remain legitimate. The exact line-leading division, reserved-word method, and identifier-adjacent quote constructions have C# regressions and real pinned Oxlint fixtures proving Oxlint honors the attacks.
2. **Frozen-byte TOCTOU — remediated.** Production scope materializes selected object IDs into a fresh mode-0700 host root outside the repository, writes repository-relative paths, re-hashes every file with `git hash-object --no-filters`, makes files read-only, lints only that root, records the object/input-set identity and a deterministic logical root label, then deletes the materialization after receipt persistence. The exact repository remains checked before and after execution.
3. **Bounded/cancellable scan and shared deadline — remediated.** The quadratic backwards scan is no longer on the authority path. Suppression work is linear, size-capped, and cancellation/deadline aware. Initial identity, automatic scope, plugin validation/snapshot, execution, normalization, and final identity all consume the one gate deadline; deadline exhaustion persists `INCOMPLETE`.
4. **Cancellation claim orphan/retry — remediated.** Claims are live delete-on-close leases retained for the full run. Initial identity and scope cancellation persist `INCOMPLETE` with cancellation-independent receipt writes. A completed attempt can retry under the same gate id in a fresh immutable `.retry-*` directory; live claims still reject concurrent runs and empty pre-existing directories still fail closed. Tests execute two deadline retries and verify both receipts plus claim release.
5. **Legitimate directives and ignores — remediated.** Protected ignored paths are skipped. Narrow unrelated directives such as `eslint-disable-next-line no-console` are accepted. Both base and head are bounded/scanned; dangerous head coverage is never grandfathered, while removing dangerous inherited coverage is allowed. Ambiguous dangerous spellings fail closed.
6. **v1→v2 production path — remediated.** v1 schema/fixture/serializer behavior remains unchanged and readable. V2 emission now refuses construction without `HostOwnedReceiptStore`, validates canonical v2 bytes, appends through that store, and returns its opaque provenance capability on `GateRunResult`. Trusted `MiddleDriverComposition` explicitly wires v2 plus the host store. The rollout/runbook documents reader upgrade, store provisioning, flag flip, in-flight rerun, and mixed-version rollback constraints.
7. **Executed `NOT_APPLICABLE` — remediated.** Executed N/A now serializes plugin status as `NOT_APPLICABLE` (including the schema-required underscore), requires a rationale, policy allowance, required protected binding, non-fixture capability, and production-capable evidence, and validates as v2 authority. Custom production and existing Crap4CSharp/adapter outcome tests cover the authority path and compatibility behavior.

## Pins and licensing

- Anti-slop source: `13909c9e07590f5a85d93c62712259fe14a883f7`, plugin `0.1.2+13909c9e07590f5a85d93c62712259fe14a883f7`, MIT.
- `oxlint` and `@oxlint/plugins`: exactly `1.78.0`; package-lock integrity and native bindings remain in the configuration closure.
- Effect rules remain explicit opt-in. Zero applicable files never produce PASS.
- Canonical gate receipt v1 schema and valid fixture were not changed.

## Mandatory validation lane

Every SDK and Node/Oxlint invocation used one outer lane:

```text
/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock
/usr/bin/timeout <finite>s
/usr/bin/systemd-run --user --scope --collect --quiet
  -p MemoryMax=1024M -p MemorySwapMax=128M -p CPUQuota=100%
/usr/bin/env DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1
```

The SDK was always `/home/fuego/.openclaw/workspace/artifacts/delivery-forge-fire-off-20261007/issue-3/toolchain/dotnet/dotnet`; no restore/install/global install or heavy mutation campaign ran.

## Validation results

- Focused evaluation/security/Crap4CSharp suite: **100 passed, 0 failed**.
- Contracts: **140 passed, 0 failed**.
- Publication: **120 passed, 0 failed**.
- Real pinned Oxlint fixtures: clean PASS, violation FAIL, malformed INCOMPLETE, and four suppression attacks reproduced; exit 0.
- Full solution build: succeeded, **0 warnings, 0 errors**.
- `dotnet format --verify-no-changes`: exit 0.
- Broad solution excluding only `QualityInterruptionScenarioTests` first passed **1,032/1,032** before the final operational-interruption taxonomy tightening. The exact committed implementation rerun passed all eight unaffected projects plus 203/205 evidence tests; the two failures were stale assertions expecting `ERROR` instead of the required `INCOMPLETE`. After correcting only those assertions, the complete exact-final-tree evidence project passed **205/205**. Thus every one of the same 1,032 tests is green on final code, although the seven-minute aggregate command was not repeated a third time after the assertion-only amendment.
- Isolated `QualityInterruptionScenarioTests`: **0 passed, 5 failed**. The first shared five-phase setup timed out after 2m21s at `QualityInterruptionOrchestrator.cs:173` under the mandatory 1-CPU cap; the other four inherited that same lazy setup timeout immediately. This exactly reproduces the pre-existing failure family and does not touch changed anti-slop/receipt code.
- `git diff --check`: clean.

One attempted broad run was discarded before diagnosis because the required root-built helper environment was omitted; it produced immediate `PlatformNotSupportedException`s and was stopped. The corrected run used only the already-built Debug helpers and the readonly SDK, then passed 1,032/1,032 outside the isolated timeout family.

## Remaining limitations

- The conservative directive scan intentionally trades precision for soundness. Directive-like blanket/protected text in a changed literal or ambiguous construct is `INCOMPLETE` until a pinned supported oxc comment-token API is available.
- Git object transport still uses bounded captured UTF-8 text, but every private materialized file must hash back to the exact Git object ID or scope derivation fails closed.
- V1 receipts remain historical/non-authoritative. Operators must retain v2-capable readers and host provenance during mixed-version rollback; a v1-only rollback is unsafe while v2 runs remain live.
- The unrelated five-test quality-interruption fixture remains unable to finish its shared setup inside its own budget under the required 1-CPU lane.
